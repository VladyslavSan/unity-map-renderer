using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.View;
using MapRenderer.Jobs;

namespace MapRenderer.Unity
{
    /// <summary>
    /// The S06 LIVE multi-tile render loop — the first runtime caller of the S04 jobified pipeline
    /// (<see cref="TileTessellationPipeline"/>) and the S03 <see cref="TileScheduler"/>. Drives tile
    /// selection from a <see cref="ViewState"/> (<see cref="TileCover"/>), fetches via the scheduler,
    /// tessellates new tiles through the Burst pipeline, builds meshes (<see cref="TileMeshFactory"/>),
    /// and places them under a floating-origin scene root (<see cref="FloatingOrigin"/>) so a panned /
    /// zoomed / tilted world-scale scene does not jitter.
    ///
    /// <para><b>Steady-state no-GC contract.</b> <see cref="Tick"/> first diffs the new cover against the
    /// previous one; when the cover is unchanged AND nothing is pending it returns BEFORE calling the
    /// scheduler (whose cache-hit path allocates a Task) or touching any collection. Reused buffers, no
    /// LINQ, no closures, cached component refs. This is what the no-per-frame-GC acceptance test pins.</para>
    ///
    /// <para><b>Threading.</b> <see cref="TileTessellationPipeline.Schedule"/> allocates NativeArrays and
    /// <c>.Complete()</c>s synchronously, so it runs on the main thread. We never <c>await</c> in the
    /// MonoBehaviour; we poll <c>Task.IsCompleted</c> on the scheduler's request task.</para>
    ///
    /// <para><b>Scope (S06).</b> Fills only (polygon/earcut). Bearing/pitch are applied to the camera
    /// transform by <see cref="MapCameraController"/>; tile/scene-root transforms are translation-only so
    /// +Y fill normals are preserved. Frustum/tilt-precise culling is deferred — the cover is a generous
    /// padded rectangle.</para>
    /// </summary>
    public sealed class MapView : MonoBehaviour
    {
        // ── Configuration ──────────────────────────────────────────────────────────────────────
        [Tooltip("Material used for every tile fill (MapRenderer/Fill URP Lit). " +
                 "If null, MapView resolves the MapRenderer/Fill shader at runtime.")]
        public Material FillMaterial;

        [Tooltip("MVT layer to render. Default: countries.")]
        public string LayerName = "countries";

        [Tooltip("Cover over-select: viewport aspect (w/h) and pad factor (absorbs viewport size + pitch).")]
        public float ViewportAspect = 1.5f;
        public float PadFactor      = 1.5f;

        [Tooltip("Zoom clamp for tile selection.")]
        public int MinZoom = 0;
        public int MaxZoom = 14;

        [Tooltip("Rebase the scene origin to the camera when it drifts more than this many metres.")]
        public double RebaseThresholdMeters = 2000.0;

        [Tooltip("Max tile pipeline builds per Tick (load smoothing; acceptance is no-GC, not no-hitch).")]
        public int MaxBuildsPerTick = 4;

        // ── Live state ─────────────────────────────────────────────────────────────────────────
        private TileScheduler _scheduler;
        private IDataSource   _source;
        private bool          _ownsSource;     // we created the source → we dispose it
        private Material      _fillMaterial;

        private ViewState _view;

        // Reused buffers — never reallocated in steady state.
        private readonly List<TileId>                  _cover        = new List<TileId>(64);
        private readonly HashSet<TileId>               _coverSet     = new HashSet<TileId>();
        private readonly Dictionary<TileId, LoadedTile> _loaded      = new Dictionary<TileId, LoadedTile>();
        private readonly List<TileId>                  _toRelease    = new List<TileId>(32);

        private double2 _sceneOrigin;
        private bool    _sceneOriginInitialised;
        private bool    _coverDirty = true;     // force a full pass on the first Tick

        /// <summary>Per-tile live record: the in-flight request, the built GameObject, and its buffers.</summary>
        private struct LoadedTile
        {
            public System.Threading.Tasks.Task<TileResponse> Request;
            public bool            Built;        // mesh produced (or definitively absent/failed)
            public GameObject      Go;
            public TileMeshBuffers Buffers;
            public double2         TileOriginMerc;
        }

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Injects the data source + view (call before the first <see cref="Tick"/>). If
        /// <paramref name="ownsSource"/> is true, <see cref="OnDestroy"/> disposes the source. The
        /// scheduler is always owned by this MapView. Used by both <see cref="Awake"/>-style runtime
        /// wiring and headless tests.
        /// </summary>
        public void Initialise(IDataSource source, ViewState initialView, bool ownsSource = false)
        {
            _source     = source;
            _ownsSource = ownsSource;
            _view       = initialView;
            _scheduler  = new TileScheduler(source, new TileCache(capacity: 256));
            _fillMaterial = ResolveMaterial();
            _sceneOriginInitialised = false;
            _coverDirty = true;
        }

        /// <summary>Current view state (read-only externally; mutate via <see cref="SetView"/>).</summary>
        public ViewState View => _view;

        /// <summary>Number of currently loaded (or loading) tiles. Exposed for tests.</summary>
        public int LoadedTileCount => _loaded.Count;

        /// <summary>The scheduler's in-flight fetch count. Exposed for tests.</summary>
        public int InFlightCount => _scheduler != null ? _scheduler.InFlightCount : 0;

        /// <summary>The scene root's current Mercator origin. Exposed for tests.</summary>
        public double2 SceneOrigin => _sceneOrigin;

        /// <summary>
        /// Test-only: returns true and the built tile's GameObject when the tile is loaded AND its mesh
        /// has been produced. Lets headless tests inspect the live-loop output (geometry, transform).
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

        /// <summary>
        /// Updates the view. Marks the cover dirty only if the integer-zoom tile selection could change;
        /// pure bearing/pitch changes (camera-only) do NOT dirty the cover, so tilting is allocation-free.
        /// </summary>
        public void SetView(ViewState v)
        {
            // A change in center or zoom can change the cover; bearing/pitch cannot.
            bool selectionChanged =
                v.CenterLon != _view.CenterLon ||
                v.CenterLat != _view.CenterLat ||
                v.IntegerZoom != _view.IntegerZoom;
            _view = v;
            if (selectionChanged) _coverDirty = true;
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the live loop. Allocation-free in steady state (cover unchanged AND every loaded
        /// tile already built): the early-out below returns before any Request / collection mutation.
        /// </summary>
        public void Tick()
        {
            if (_scheduler == null) return;

            // Rebase the scene origin toward the camera if it has drifted too far (floating origin).
            UpdateSceneOrigin();

            // Pump any in-flight tile builds that have completed (this is a load, not steady state).
            int pending = PumpPendingBuilds();

            // Steady-state early-out: cover is clean and nothing is loading → no work, no allocation.
            if (!_coverDirty && pending == 0)
                return;

            // Recompute the cover (reuses _cover; no allocation once warm).
            TileCover.Cover(_view, ViewportAspect, PadFactor, MinZoom, MaxZoom, _cover);

            // Build the new cover set (reused HashSet).
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

            // Release tiles leaving the cover (reused list to avoid mutating the dict while iterating).
            _toRelease.Clear();
            foreach (var kv in _loaded)
                if (!_coverSet.Contains(kv.Key))
                    _toRelease.Add(kv.Key);
            for (int i = 0; i < _toRelease.Count; i++)
                ReleaseTile(_toRelease[i]);

            _coverDirty = false;
        }

        /// <summary>
        /// Polls in-flight requests; for each that completed since last tick, tessellate + build the mesh
        /// (capped at <see cref="MaxBuildsPerTick"/>). Returns the number of tiles still pending a build.
        /// </summary>
        private int PumpPendingBuilds()
        {
            int builds  = 0;
            int pending = 0;

            // Iterate a snapshot of keys via _toRelease scratch to avoid allocating an enumerator-mutation.
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

                // Mark built regardless of outcome (success, absent, or fault) so we don't re-poll it.
                lt.Built = true;

                if (req.Status == System.Threading.Tasks.TaskStatus.RanToCompletion &&
                    req.Result.HasData && req.Result.Bytes != null)
                {
                    BuildTile(ref lt, id, req.Result.Bytes);
                    builds++;
                }
                // else: absent / faulted → no geometry; the record stays so we don't re-request it while
                // it remains in cover (the scheduler's negative cache handles re-fetch timing on re-entry).

                _loaded[id] = lt;
            }

            return pending;
        }

        /// <summary>
        /// Decodes the tile bytes, runs the jobified tessellation pipeline (main thread), builds the mesh,
        /// and parents the tile GameObject under the scene root at its floating-origin local position.
        /// </summary>
        private void BuildTile(ref LoadedTile lt, TileId id, byte[] mvtBytes)
        {
            MvtTile tile  = MvtDecoder.Decode(mvtBytes);
            MvtLayer layer = tile.GetLayer(LayerName);
            if (layer == null) return;

            double extent = layer.Extent;

            var polyGeoms = new List<uint[]>();
            foreach (var f in layer.Features)
                if (f.GeometryType == MvtGeometryType.Polygon && f.Geometry != null)
                    polyGeoms.Add(f.Geometry);
            if (polyGeoms.Count == 0) return;

            double2 tileOrigin = lt.TileOriginMerc;

            var input = new TileTessellationPipeline.LayerInput
            {
                FeatureGeometries = polyGeoms,
                Extent      = extent,
                TileZ       = id.Z, TileX = id.X, TileY = id.Y,
                OriginMercX = tileOrigin.x,
                OriginMercY = tileOrigin.y,
            };

            TileMeshBuffers buffers = TileTessellationPipeline.Schedule(input);
            if (!buffers.IsCreated)
                return;

            Mesh mesh = TileMeshFactory.CreateMesh(buffers, extent, $"MapTile_{id}");
            if (mesh == null)
            {
                buffers.Dispose();
                return;
            }

            var go = new GameObject($"Tile_{id}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = (Vector3)(float3ToVector(FloatingOrigin.TileLocalToScene(tileOrigin, _sceneOrigin)));

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _fillMaterial;

            lt.Go      = go;
            lt.Buffers = buffers;
        }

        /// <summary>Releases a tile: scheduler release, dispose its NativeArrays, destroy its GameObject.</summary>
        private void ReleaseTile(TileId id)
        {
            if (_loaded.TryGetValue(id, out var lt))
            {
                if (lt.Buffers.IsCreated) lt.Buffers.Dispose();
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
        /// Initialises or rebases the scene origin to the camera's Mercator position. On rebase, shifts
        /// every loaded tile's local position by the rebase delta so absolute world positions are
        /// preserved (no visible jump).
        /// </summary>
        private void UpdateSceneOrigin()
        {
            double2 cameraMerc = _view.CenterMercator();

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

            // Re-place every loaded tile relative to the new scene origin.
            foreach (var kv in _loaded)
            {
                var lt = kv.Value;
                if (lt.Go != null)
                    lt.Go.transform.localPosition = float3ToVector(
                        FloatingOrigin.TileLocalToScene(lt.TileOriginMerc, _sceneOrigin));
            }
            // (delta is implicit in the recompute above; exposed via RebaseDelta for tests.)
            _ = delta;
        }

        private static Vector3 float3ToVector(float3 v) => new Vector3(v.x, v.y, v.z);

        private Material ResolveMaterial()
        {
            if (FillMaterial != null) return FillMaterial;
            var shader = Shader.Find("MapRenderer/Fill");
            if (shader != null) return new Material(shader) { name = "MapView_Fill" };
            // Transient fallback (first import / missing shader) so geometry is at least visible.
            return new Material(Shader.Find("Sprites/Default")) { name = "MapView_Fallback" };
        }

        private void Update()
        {
            Tick();
        }

        private void OnDestroy()
        {
            // Dispose every loaded tile's NativeArrays + GameObject.
            foreach (var kv in _loaded)
            {
                if (kv.Value.Buffers.IsCreated) kv.Value.Buffers.Dispose();
                if (kv.Value.Go != null)
                {
                    if (Application.isPlaying) Destroy(kv.Value.Go);
                    else                       DestroyImmediate(kv.Value.Go);
                }
            }
            _loaded.Clear();

            _scheduler?.Dispose();   // non-owning: cancels CTSs, does NOT dispose the source
            if (_ownsSource) _source?.Dispose();
        }
    }
}
