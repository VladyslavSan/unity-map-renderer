using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
// MapRenderer.Jobs not used directly in MapView — StyledFillTileBuilder owns the Burst job.

namespace MapRenderer.Unity
{
    /// <summary>
    /// S40 live multi-tile render loop — per-layer styled fill rendering.
    ///
    /// Replaces the S06 single-shared-material path with one styled draw per <c>fill</c> style layer,
    /// ordered by painter's algorithm (LayerDrawOrder), filtered by S10 FeatureSelector, painted by
    /// S13 FillPaint (per-feature data-driven color baked into vertex stream via StyledFillTileBuilder).
    ///
    /// Architecture:
    ///   • <see cref="StyleDocument"/> is provided at <see cref="Initialise"/> time. MapView iterates
    ///     its fill layers once on initialise to build a <see cref="FillLayerRecord"/> list — one record
    ///     per fill style layer in declared (painter's) order. Each record holds a per-layer Material
    ///     instance (SRP-batcher-safe: never MaterialPropertyBlock) and a ZoomStyleApplier.
    ///   • Materials are SHARED across tiles for the same style layer: every tile's child renderer for
    ///     layer <em>i</em> uses <c>_layerRecords[i].Material</c>. Data-driven per-feature color lives
    ///     in the vertex stream (baked by StyledFillTileBuilder), so the material itself is tile-neutral.
    ///   • Per-frame: <see cref="ApplyZoom"/> pushes zoom uniforms into each layer's material before
    ///     anything else in Tick — so a fractional-zoom-only change (bearing/pitch) still updates
    ///     uniforms even when the cover is clean and the early-out fires immediately after.
    ///   • On tile build: <see cref="BuildTile"/> iterates fill layers in order; each gets a child
    ///     GameObject with one MeshFilter + MeshRenderer parented under the tile container.
    ///   • Eviction: the tile container GameObject (and all child renderers) are destroyed. Layer
    ///     Material instances live on <c>_layerRecords</c> and are disposed only in OnDestroy.
    ///
    /// Steady-state no-GC contract (preserved from S06):
    ///   The ApplyZoom loop over _layerRecords is a plain <c>for</c> over a <c>List</c> (struct
    ///   enumerator, no allocation). The early-out fires before any Request / collection mutation when
    ///   cover is clean AND nothing is pending. Reused buffers, no LINQ, no closures.
    ///
    /// Lines deferred to S14: line style layers are silently skipped here; they will be added in S14.
    /// A map rendered by this stage shows fills only — roads, coastlines, borders are not drawn.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public sealed class MapView : MonoBehaviour
    {
        // ── Configuration ──────────────────────────────────────────────────────────────────────
        [Tooltip("Cover over-select: viewport aspect (w/h) and pad factor (absorbs viewport size + pitch).")]
        public float ViewportAspect = 1.5f;
        public float PadFactor      = 1.5f;

        [Tooltip("Zoom clamp for tile selection.")]
        public int MinZoom = 0;
        public int MaxZoom = 14;

        [Tooltip("Rebase the scene origin to the camera when it drifts more than this many metres.")]
        public double RebaseThresholdMeters = 2000.0;

        [Tooltip("Max tile pipeline builds per Tick (load smoothing).")]
        public int MaxBuildsPerTick = 4;

        // ── Per-style-layer record (built once at Initialise, shared across all tiles) ──────────

        /// <summary>One record per fill style layer in declared order.</summary>
        private struct FillLayerRecord
        {
            /// <summary>The parsed fill paint for this layer.</summary>
            public FillPaint Paint;

            /// <summary>The style layer (needed by FeatureSelector for source-layer + filter).</summary>
            public StyleLayer StyleLayer;

            /// <summary>Shared Material instance. renderQueue = TransparentQueue + layerIndex.</summary>
            public Material Material;

            /// <summary>Applies zoom-dependent paint uniforms to Material each frame.</summary>
            public ZoomStyleApplier Applier;
        }

        // ── Live state ─────────────────────────────────────────────────────────────────────────
        private TileScheduler _scheduler;
        private IDataSource   _source;
        private bool          _ownsSource;

        // ── S50: Core camera system + Unity sync layer (owned by MapView, D4) ─────────────────
        // _cameraSystem is the canonical, single camera-state holder. Tick reads
        // _cameraSystem.Current directly. _mapCamera is the Unity transform syncer. Both are owned by
        // this MapView (constructed in Initialise; injectable via SetCamera for the test rig).
        private CameraSystem _cameraSystem;
        private MapCamera    _mapCamera;

        private StyleDocument _style;

        // Per-fill-layer records (built once at Initialise).
        private readonly List<FillLayerRecord> _layerRecords = new List<FillLayerRecord>(16);

        // Reused buffers — never reallocated in steady state.
        private readonly List<TileId>                   _cover     = new List<TileId>(64);
        private readonly HashSet<TileId>                _coverSet  = new HashSet<TileId>();
        private readonly Dictionary<TileId, LoadedTile> _loaded    = new Dictionary<TileId, LoadedTile>();
        private readonly List<TileId>                   _toRelease = new List<TileId>(32);

        private double2 _sceneOrigin;
        private bool    _sceneOriginInitialised;
        private bool    _coverDirty = true;

        // ── S50: tile-selection key (scalar fields, no boxing) ─────────────────────────────────
        // Tracks the camera state that affects WHICH tiles load (center + integer zoom). Heading/tilt
        // are deliberately EXCLUDED so an orientation-only Apply stays alloc-free (no cover dirty).
        // Seeded on the first dirty pass; compared each frame to decide whether to re-run the cover.
        private double _coverKeyLon;
        private double _coverKeyLat;
        private int    _coverKeyIntegerZoom;
        private bool   _coverKeyInitialised;

        /// <summary>Per-tile live record: the in-flight request and the built tile container GameObject.</summary>
        private struct LoadedTile
        {
            public System.Threading.Tasks.Task<TileResponse> Request;
            public bool       Built;         // mesh produced (or definitively absent/failed)
            public GameObject Go;            // tile container; child GameObjects are per-layer renderers
            public double2    TileOriginMerc;
        }

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Injects the data source, style document, and initial camera (call before the first
        /// <see cref="Tick"/>). If <paramref name="ownsSource"/> is true, <see cref="OnDestroy"/>
        /// disposes the source. The scheduler is always owned by this MapView.
        ///
        /// <para><b>S50 (D4/DD1):</b> MapView owns a <see cref="CameraSystem"/>. If one has not already
        /// been wired via <see cref="SetCamera"/>, this constructs a default-framed one from
        /// <paramref name="initialView"/> so <see cref="Tick"/> never reads a null system. The runtime
        /// path (<see cref="MapRoot.Wire"/>) replaces it with a fully-configured system + sync layer.</para>
        ///
        /// When <paramref name="style"/> is null, MapView renders nothing (no fill layers).
        /// Used by both runtime wiring and headless tests.
        /// </summary>
        public void Initialise(IDataSource source, CameraProperties initialView,
            bool ownsSource = false, StyleDocument style = null)
        {
            _source     = source;
            _ownsSource = ownsSource;
            _style      = style;
            _scheduler  = new TileScheduler(source, new TileCache(capacity: 256));

            // Ensure a camera system exists (DD1: Tick must never read a null system). A camera wired
            // earlier via SetCamera is kept; otherwise build a default-framed system from initialView.
            if (_cameraSystem == null)
                _cameraSystem = new CameraSystem(initialView);

            _sceneOriginInitialised = false;
            _coverDirty             = true;
            _coverKeyInitialised    = false;

            // Build per-fill-layer records from the style document (reads _cameraSystem.Current.Zoom).
            BuildLayerRecords();
        }

        /// <summary>
        /// True once <see cref="Initialise"/> has been called successfully.
        /// Exposed for the wiring test (tooth 1 of S41 acceptance).
        /// </summary>
        public bool IsInitialised => _scheduler != null;

        /// <summary>
        /// Current camera state (read-only). S50: this is <see cref="CameraSystem.Current"/> directly —
        /// the single camera-state type. Mutate via <c>Camera.Apply(...)</c>.
        /// </summary>
        public CameraProperties View => _cameraSystem != null ? _cameraSystem.Current : CameraProperties.Default;

        /// <summary>Number of currently loaded (or loading) tiles. Exposed for tests.</summary>
        public int LoadedTileCount => _loaded.Count;

        /// <summary>The scheduler's in-flight fetch count. Exposed for tests.</summary>
        public int InFlightCount => _scheduler != null ? _scheduler.InFlightCount : 0;

        /// <summary>The scene root's current Mercator origin. Exposed for tests.</summary>
        public double2 SceneOrigin => _sceneOrigin;

        /// <summary>Number of fill style layers in the loaded style. Exposed for tests.</summary>
        public int FillLayerCount => _layerRecords.Count;

        // ── S45/S50: Camera system accessors ───────────────────────────────────────────────────

        /// <summary>
        /// The Core camera system, owned by this MapView. The single source of camera state (S50).
        /// Set by <see cref="Initialise"/> or <see cref="SetCamera"/>.
        /// </summary>
        public CameraSystem Camera => _cameraSystem;

        /// <summary>
        /// Injects a fully-configured <see cref="CameraSystem"/> + <see cref="MapCamera"/> sync layer.
        /// Called by <see cref="MapRoot.Wire"/> (with the controller's framing) or by the test rig
        /// (which skips <see cref="Initialise"/> and drives the camera path only).
        /// </summary>
        public void SetCamera(MapCamera mapCamera, CameraSystem cameraSystem)
        {
            _mapCamera     = mapCamera;
            _cameraSystem  = cameraSystem;
            _coverKeyInitialised = false; // re-seed the cover key against the new camera
        }

        /// <summary>
        /// Test-only: returns true and the built tile's container GameObject when the tile is loaded
        /// AND its mesh has been produced. Lets headless tests inspect the live-loop output.
        /// </summary>
        public bool TryGetBuiltTile(TileId id, out GameObject go)
        {
            go = null;
            if (_loaded.TryGetValue(id, out var lt) && lt.Built && lt.Go != null)
            {
                go = lt.Go;
                return true;
            }
            return false;
        }

        /// <summary>Test-only: true once every loaded tile has finished building (or is definitively absent).</summary>
        public bool AllTilesSettled()
        {
            foreach (var kv in _loaded)
                if (!kv.Value.Built) return false;
            return true;
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// S45 D5 / S50 D4 — Deterministic frame update. Called from <see cref="Update"/>
        /// (MonoBehaviour) with <c>Time.deltaTime</c>. Also callable from tests with explicit dt.
        ///
        /// <para><b>STEP 1: Advance camera</b> — calls <c>_cameraSystem.Advance(dt)</c>, then syncs the
        /// Unity camera transform. This ensures all tile-selection and pose consumers in the same frame
        /// see the POST-update camera (the D5 determinism fix).</para>
        ///
        /// <para><b>STEP 2: Tile loop</b> — calls <see cref="Tick"/>, which reads
        /// <c>_cameraSystem.Current</c> directly (no derived-adapter bridge).</para>
        /// </summary>
        public void UpdateFrame(double dt)
        {
            // STEP 1: Advance camera (D5 — must run before tile selection reads the camera).
            if (_cameraSystem != null)
            {
                _cameraSystem.Advance(dt);
                _mapCamera?.Sync(_cameraSystem);
            }

            // STEP 2: Tile loop (reads the now-advanced _cameraSystem.Current).
            Tick();
        }

        /// <summary>
        /// One frame of the live loop. Allocation-free in steady state.
        ///
        /// <para>ApplyZoom runs FIRST (before the early-out) so zoom-dependent uniforms are always
        /// up-to-date, even on frames where the cover is unchanged (e.g. fractional-zoom / camera tilt).</para>
        ///
        /// <para><b>S50:</b> reads <see cref="CameraSystem.Current"/> directly. Cover-dirty is detected
        /// here by comparing the tile-selection key (center lon/lat + integer zoom) against the last
        /// cover pass — heading/tilt are excluded, so an orientation-only change never dirties the cover
        /// and stays allocation-free.</para>
        /// </summary>
        public void Tick()
        {
            if (_scheduler == null) return;

            // Read the canonical camera state by value (readonly struct → no heap allocation).
            CameraProperties cam = _cameraSystem.Current;

            // Dirty the cover only when the tile-selection-relevant state changed (center + integer
            // zoom). Scalar field compare — no boxing, no tuple allocation.
            int integerZoom = cam.IntegerZoom;
            if (!_coverKeyInitialised ||
                cam.LookAt.Lon != _coverKeyLon ||
                cam.LookAt.Lat != _coverKeyLat ||
                integerZoom    != _coverKeyIntegerZoom)
            {
                _coverDirty = true;
            }

            // ApplyZoom first — before any early-out — so fractional-zoom changes always push uniforms.
            // Plain for-loop over List (struct enumerator, no allocation).
            for (int i = 0; i < _layerRecords.Count; i++)
                _layerRecords[i].Applier.ApplyZoom(cam.Zoom);

            // Rebase the scene origin toward the camera if it has drifted too far.
            UpdateSceneOrigin(cam);

            // Pump any in-flight tile builds that have completed.
            int pending = PumpPendingBuilds(cam);

            // Steady-state early-out: cover is clean and nothing is loading → no work, no allocation.
            if (!_coverDirty && pending == 0)
                return;

            // Recompute the cover (reuses _cover; no allocation once warm).
            TileCover.Cover(cam, ViewportAspect, PadFactor, MinZoom, MaxZoom, _cover);

            _coverSet.Clear();
            for (int i = 0; i < _cover.Count; i++)
                _coverSet.Add(_cover[i]);

            // Request tiles newly entering the cover.
            for (int i = 0; i < _cover.Count; i++)
            {
                TileId id = _cover[i];
                if (!_loaded.ContainsKey(id))
                {
                    var req = _scheduler.Request(id);
                    _loaded[id] = new LoadedTile
                    {
                        Request        = req,
                        Built          = false,
                        TileOriginMerc = FloatingOrigin.TileLocalOriginMercator(id),
                    };
                }
            }

            // Release tiles leaving the cover.
            _toRelease.Clear();
            foreach (var kv in _loaded)
                if (!_coverSet.Contains(kv.Key))
                    _toRelease.Add(kv.Key);
            for (int i = 0; i < _toRelease.Count; i++)
                ReleaseTile(_toRelease[i]);

            // Record the tile-selection key this cover was computed for (scalar — no allocation).
            _coverKeyLon         = cam.LookAt.Lon;
            _coverKeyLat         = cam.LookAt.Lat;
            _coverKeyIntegerZoom = integerZoom;
            _coverKeyInitialised = true;

            _coverDirty = false;
        }

        /// <summary>
        /// Polls in-flight requests; for each completed, tessellates + builds the tile meshes
        /// (capped at MaxBuildsPerTick). Returns the number of tiles still pending a build.
        /// </summary>
        private int PumpPendingBuilds(CameraProperties cam)
        {
            int builds  = 0;
            int pending = 0;

            _toRelease.Clear();
            foreach (var kv in _loaded)
                if (!kv.Value.Built)
                    _toRelease.Add(kv.Key);

            for (int i = 0; i < _toRelease.Count; i++)
            {
                TileId id = _toRelease[i];
                LoadedTile lt = _loaded[id];
                var req = lt.Request;

                if (req == null || !req.IsCompleted)
                {
                    pending++;
                    continue;
                }

                if (builds >= MaxBuildsPerTick)
                {
                    pending++;
                    continue;
                }

                lt.Built = true;

                if (req.Status == System.Threading.Tasks.TaskStatus.RanToCompletion &&
                    req.Result.HasData && req.Result.Bytes != null)
                {
                    BuildTile(ref lt, id, req.Result.Bytes, cam);
                    builds++;
                }

                _loaded[id] = lt;
            }

            return pending;
        }

        /// <summary>
        /// Decodes the tile, iterates fill style layers in declared order, builds one mesh per layer
        /// (via StyledFillTileBuilder), and parents child renderers under the tile container GameObject.
        ///
        /// Each child renderer uses the SHARED per-layer Material instance (_layerRecords[i].Material)
        /// so data-driven color lives in the vertex stream while material uniforms are zoom-driven.
        /// </summary>
        private void BuildTile(ref LoadedTile lt, TileId id, byte[] mvtBytes, CameraProperties cam)
        {
            if (_layerRecords.Count == 0) return;

            MvtTile mvtTile = MvtDecoder.Decode(mvtBytes);

            // Create the tile container. Child GameObjects are per fill layer.
            var container = new GameObject($"Tile_{id}");
            container.transform.SetParent(transform, worldPositionStays: false);
            container.transform.localPosition =
                (Vector3)(float3ToVector(FloatingOrigin.TileLocalToScene(lt.TileOriginMerc, _sceneOrigin)));

            bool anyGeometry = false;

            for (int li = 0; li < _layerRecords.Count; li++)
            {
                FillLayerRecord rec = _layerRecords[li];

                // Select features via FeatureSelector (source-layer + filter).
                IReadOnlyList<MvtFeature> features =
                    FeatureSelector.SelectFeatures(rec.StyleLayer, mvtTile, cam.Zoom);
                if (features.Count == 0) continue;

                // Resolve the MVT layer to get the tile extent.
                MvtLayer mvtLayer = SourceLayerResolver.ResolveMvtLayer(rec.StyleLayer, mvtTile);
                if (mvtLayer == null) continue;

                double extent = mvtLayer.Extent;

                Mesh mesh = StyledFillTileBuilder.BuildMesh(
                    features, rec.Paint, cam.Zoom, extent, id, lt.TileOriginMerc);
                if (mesh == null) continue;

                // One child per fill layer.
                var layerGo = new GameObject($"Layer_{li}_{rec.StyleLayer.Id}");
                layerGo.transform.SetParent(container.transform, worldPositionStays: false);
                layerGo.transform.localPosition = Vector3.zero;

                var mf = layerGo.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;

                var mr = layerGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = rec.Material;
                mr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows     = false;

                anyGeometry = true;
            }

            if (!anyGeometry)
            {
                // No fill geometry for this tile — destroy the empty container.
                if (Application.isPlaying) Destroy(container);
                else                       DestroyImmediate(container);
                return;
            }

            lt.Go = container;
        }

        /// <summary>Releases a tile: scheduler release + destroy its container GameObject.</summary>
        private void ReleaseTile(TileId id)
        {
            if (_loaded.TryGetValue(id, out var lt))
            {
                if (lt.Go != null)
                {
                    if (Application.isPlaying) Destroy(lt.Go);
                    else                       DestroyImmediate(lt.Go);
                }
                _loaded.Remove(id);
            }
            _scheduler.Release(id);
        }

        /// <summary>
        /// Initialises or rebases the scene origin to the camera's Mercator position.
        /// On rebase, shifts every loaded tile's local position by the rebase delta.
        /// </summary>
        private void UpdateSceneOrigin(CameraProperties cam)
        {
            double2 cameraMerc = cam.CenterMercator();

            if (!_sceneOriginInitialised)
            {
                _sceneOrigin = cameraMerc;
                _sceneOriginInitialised = true;
                return;
            }

            if (!FloatingOrigin.ShouldRebase(_sceneOrigin, cameraMerc, RebaseThresholdMeters))
                return;

            double2 oldOrigin = _sceneOrigin;
            _sceneOrigin = cameraMerc;
            double2 delta = FloatingOrigin.RebaseDelta(oldOrigin, _sceneOrigin);

            foreach (var kv in _loaded)
            {
                var lt = kv.Value;
                if (lt.Go != null)
                    lt.Go.transform.localPosition = float3ToVector(
                        FloatingOrigin.TileLocalToScene(lt.TileOriginMerc, _sceneOrigin));
            }
            _ = delta;
        }

        private static Vector3 float3ToVector(float3 v) => new Vector3(v.x, v.y, v.z);

        /// <summary>
        /// Builds per-fill-layer records from the style document. Called once at Initialise.
        /// Each fill layer gets its own Material instance and ZoomStyleApplier.
        /// renderQueue = TransparentQueue + globalFillIndex (painter's algorithm).
        /// </summary>
        private void BuildLayerRecords()
        {
            // Dispose any existing records (in case Initialise is called again).
            DisposeLayerRecords();

            if (_style == null) return;

            // Collect fill layers in declared order.
            var fillLayers = new List<StyleLayer>(8);
            foreach (var layer in _style.Layers)
                if (layer.LayerType == StyleLayerType.Fill)
                    fillLayers.Add(layer);

            if (fillLayers.Count == 0) return;

            int[] queues = LayerDrawOrder.ComputeQueues(fillLayers.Count);

            for (int i = 0; i < fillLayers.Count; i++)
            {
                StyleLayer sl = fillLayers[i];
                FillPaint paint = new FillPaint(sl);

                // Create per-layer Material. _MapColor=white (identity; color lives in vertex stream).
                Material mat = CreateFillMaterial();
                // renderQueue assigned at runtime — avoids URP ValidateMaterial clobber on import.
                mat.renderQueue = queues[i];

                var applier = new ZoomStyleApplier(mat);
                BindFillPaintToApplier(paint, applier, mat);
                applier.ApplyZoom(_cameraSystem != null ? _cameraSystem.Current.Zoom : 0.0); // initial push

                _layerRecords.Add(new FillLayerRecord
                {
                    Paint      = paint,
                    StyleLayer = sl,
                    Material   = mat,
                    Applier    = applier,
                });
            }
        }

        /// <summary>
        /// Creates a base fill Material for a style layer. _MapColor=white (identity multiply);
        /// per-feature color lives in the vertex stream. ZWrite=0 for painter's-algorithm ordering.
        /// </summary>
        private static Material CreateFillMaterial()
        {
            var shader = Shader.Find("MapRenderer/Fill");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "MapView_Fill" };
                mat.SetColor("_MapColor",   Color.white); // identity — vertex color drives the fill
                mat.SetColor("_BaseColor",  Color.white);
                mat.SetFloat("_Opacity",    1f);
                mat.SetFloat("_Metallic",   0f);
                mat.SetFloat("_Smoothness", 0f);
                mat.SetFloat("_ZWrite",     0f); // painter's algorithm — no depth write
                return mat;
            }
            // Transient fallback (missing shader on first import).
            Debug.LogWarning("[MapView] MapRenderer/Fill shader not found — using Sprites/Default fallback.");
            return new Material(Shader.Find("Sprites/Default")) { name = "MapView_Fallback" };
        }

        /// <summary>
        /// Binds constant/zoom paint properties from <paramref name="paint"/> to the material
        /// via <paramref name="applier"/>. Constant properties are pushed immediately (at bind time).
        /// Zoom-dependent properties are queued for re-evaluation each frame via ApplyZoom.
        ///
        /// Color is NOT bound here — it lives in the vertex stream (data-driven path via
        /// StyledFillTileBuilder; _MapColor=white is identity). If the color kind is Constant/Zoom,
        /// _MapColor is also kept white so the vertex color (baked constant) is the sole driver.
        /// </summary>
        private static void BindFillPaintToApplier(FillPaint paint, ZoomStyleApplier applier, Material mat)
        {
            // fill-opacity → _Opacity
            if (paint.Opacity != null)
                applier.BindFloat(paint.Opacity, "_Opacity");

            // fill-outline-color → _FillOutlineColor (only when explicitly set; fallback = fill-color,
            // which we don't push to _MapColor, so the outline also stays neutral).
            if (paint.OutlineColor != null && !paint.OutlineColorIsFallback)
                applier.BindColor(paint.OutlineColor, "_FillOutlineColor");

            // fill-antialias → _FillAntialias
            if (paint.Antialias != null)
                applier.BindFloat(paint.Antialias, "_FillAntialias");

            // fill-translate: extract components and set the vector directly (always constant post-parse).
            float tx = (float)paint.TranslateX.EvaluateNumber(0.0);
            float ty = (float)paint.TranslateY.EvaluateNumber(0.0);
            mat.SetVector("_FillTranslate", new Vector4(tx, ty, 0f, 0f));

            // fill-translate-anchor → _FillTranslateAnchor
            if (paint.TranslateAnchor != null)
                applier.BindFloat(paint.TranslateAnchor, "_FillTranslateAnchor");
        }

        /// <summary>Dispose all layer Material instances.</summary>
        private void DisposeLayerRecords()
        {
            for (int i = 0; i < _layerRecords.Count; i++)
            {
                var mat = _layerRecords[i].Material;
                if (mat != null)
                {
                    if (Application.isPlaying) Destroy(mat);
                    else                       DestroyImmediate(mat);
                }
            }
            _layerRecords.Clear();
        }

        private void Update()
        {
            // S45 D5: UpdateFrame is the unified entry — advances the camera (step 1) then Tick.
            // Before Initialise/SetCamera (no camera system yet) it skips the advance and Tick early-outs.
            UpdateFrame(Time.deltaTime);
        }

        private void OnDestroy()
        {
            // Destroy tile container GameObjects.
            foreach (var kv in _loaded)
            {
                if (kv.Value.Go != null)
                {
                    if (Application.isPlaying) Destroy(kv.Value.Go);
                    else                       DestroyImmediate(kv.Value.Go);
                }
            }
            _loaded.Clear();

            // Dispose layer materials.
            DisposeLayerRecords();

            _scheduler?.Dispose();
            if (_ownsSource) _source?.Dispose();
        }
    }
}
