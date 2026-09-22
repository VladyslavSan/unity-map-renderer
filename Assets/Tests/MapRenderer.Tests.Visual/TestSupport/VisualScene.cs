// Unity EditMode only — the declarative visual-test authoring kit.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// The composer: assembles VisualSource/VisualLayer into a REAL style JSON string, drives it through the
// production MapView.SetStyle path (no StyleDocument shortcut), and renders the MapView's OWN MapCamera —
// the ONE camera, both the tile-selection viewport and the snapshotted camera. MapView construction and the
// pump mirror GeoJsonSourceTests; the lit-ambient recipe mirrors TiltedGroundScene.Create.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapCamera = MapRenderer.Unity.Rendering.Map.MapCamera;
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using RenderBackend = MapRenderer.Unity.Rendering.Map.RenderBackend;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Fluent driver for a declarative visual-test scene: named inline-GeoJSON sources, fill layers bound to
    /// them BY ID, and a camera pose — assembled into a real style JSON string, parsed by the REAL
    /// <see cref="StyleParser"/>, and rendered through a live <see cref="MapViewComponent"/>.
    ///
    /// <para><b>The kit owns exactly one camera.</b> The
    /// <see cref="MapCamera"/> built from <see cref="Camera(GeoCoordinate3D,double,double,double)"/> is BOTH
    /// what selects the tile cover (wired via <c>MapViewComponent.SetCamera</c>) AND what
    /// <see cref="SnapshotRenderer"/> renders — so the rendered frame is provably what the selector saw.</para>
    ///
    /// <para>Usage: <c>using var scene = VisualScene.New().Source(...).Layer(...).Camera(...);
    /// VisualFrame frame = scene.Render(px);</c> — <see cref="Render"/> returns the <see cref="VisualFrame"/>
    /// value, not the disposable; keep the <see cref="VisualScene"/> itself in a <c>using</c> so its
    /// MapView/camera/RT/ambient are torn down.</para>
    /// </summary>
    internal sealed class VisualScene : IDisposable
    {
        /// <summary>The mandated non-black background: a black frame can only mean "no GPU
        /// context", never "background only" — this is what makes the empty-dataset negative control
        /// (<c>IsBlank</c>) meaningful instead of vacuous.</summary>
        public static readonly Color BackgroundColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 BackgroundByte = new Color32(26, 28, 38, 255);

        /// <summary>Extra <c>LateUpdate</c> ticks pumped AFTER the tiles settle, before the snapshot: Entities
        /// Graphics needs at least one more Rebuild/EG-system tick before its BRG batch is cullable, so
        /// rendering on the settle frame itself yields a blank frame (probe-confirmed: +0 frames blank, +1
        /// renders). 10 is a robust margin over the observed 1; harmless on the GameObject backend, which
        /// is already renderable.</summary>
        private const int WarmupFrames = 10;

        /// <summary>Bounded ceiling for <see cref="SpinUntilSymbolsReady"/>: the symbol kick crosses a thread
        /// pool, so a fixed pump count is a flake generator — this bounds the spin instead of guessing a
        /// frame count. 300 is <c>SymbolProcessorParityTests.DriveProductionBuild</c>'s own ceiling of 200
        /// plus margin for the extra MapView-level plumbing this composer drives through.</summary>
        private const int SymbolReadinessCeiling = 300;

        private readonly List<VisualSource> _sources = new List<VisualSource>();
        private readonly List<VisualLayer>  _layers  = new List<VisualLayer>();

        private CameraProperties _cameraProperties;
        private bool _cameraSet;
        private RenderBackend _backend = RenderBackend.Entities;   // the product default; the suite validates the shipping path
        private MapRenderer.Unity.Rendering.Materials.RenderMode _renderMode =
            MapRenderer.Unity.Rendering.Materials.RenderMode.Lit;   // the product default

        /// <summary>Set via <see cref="Glyphs"/>; null ⇒ no override, the production
        /// <c>GlyphSourceFactory.Create</c> path runs unchanged (fill-only scenes never touch this).</summary>
        private Func<TestGlyphSource> _glyphSourceFactory;

        /// <summary>Set via <see cref="ExpectSymbolQuads"/> — the target <see cref="SpinUntilSymbolsReady"/>
        /// pumps toward. Required whenever the scene declares a <see cref="SymbolTextVisualLayer"/>.</summary>
        private int? _expectedSymbolQuads;

        private GameObject _mapGo;
        private MapViewComponent _mapView;
        private GameObject _cameraGo;
        private UnityEngine.Camera _unityCamera;
        private RenderTexture _rt;
        private MapCamera _mapCam;
        private GameObject _lightGo;
        private bool _ambientSaved;
        private (UnityEngine.Rendering.AmbientMode mode, Color light) _savedAmbient;

        /// <summary>Private — a scene is started via <see cref="New"/> and built fluently.</summary>
        private VisualScene() { }

        /// <summary>Starts a new, empty scene.</summary>
        public static VisualScene New() => new VisualScene();

        /// <summary>Adds a named source. <paramref name="source"/>'s <see cref="VisualSource.Id"/> is assigned
        /// here — a source built via <see cref="GeoJson"/> does not know its id until bound.</summary>
        public VisualScene Source(string id, VisualSource source)
        {
            source.Id = id;
            _sources.Add(source);
            return this;
        }

        /// <summary>Adds a layer. Its <see cref="VisualLayer.SourceId"/> (set via <c>.Source(id)</c> on the
        /// layer itself) is stored VERBATIM — including one naming no declared source, the shape T-Binding
        /// exercises.</summary>
        public VisualScene Layer(VisualLayer layer)
        {
            _layers.Add(layer);
            return this;
        }

        /// <summary>Sets the camera pose this scene both selects tiles with and renders. <paramref name="tilt"/>
        /// and <paramref name="heading"/> are degrees, matching <see cref="CameraProperties"/>'s own
        /// constructor (not an <c>Angle</c> — the production camera-state type takes bare degrees here).</summary>
        public VisualScene Camera(GeoCoordinate3D lookAt, double zoom, double tilt = 0.0, double heading = 0.0)
        {
            _cameraProperties = new CameraProperties(lookAt, zoom, heading, tilt);
            _cameraSet = true;
            return this;
        }

        /// <summary>Overrides the tile render backend (default <see cref="RenderBackend.Entities"/>, the product
        /// default — the suite validates the shipping path). Exposed so a future test can render a specific
        /// backend on purpose; every backend needs the same post-settle warm-up (<see cref="WarmupFrames"/>).</summary>
        /// <param name="backend">The backend to render this scene on.</param>
        public VisualScene Backend(RenderBackend backend)
        {
            _backend = backend;
            return this;
        }

        /// <summary>Selects which committed <c>MapMaterialSet</c> this scene renders through, and therefore
        /// whether the frame is lit or unlit (render mode is a property of the SET — see
        /// <c>RenderModeMaterialSelectionTests</c>; there is no separate config flag). Default
        /// <c>RenderMode.Lit</c>, the product default. Unlit is the arm that separates "the geometry is
        /// wrong" from "two materials shade the same colour differently": under it a colour is exactly its
        /// own albedo, so two overlapping draws of one colour are byte-identical by construction.</summary>
        /// <param name="mode">The render mode to load the committed material set for.</param>
        public VisualScene RenderMode(MapRenderer.Unity.Rendering.Materials.RenderMode mode)
        {
            _renderMode = mode;
            return this;
        }

        /// <summary>Injects a fixture glyph source keyed <c>(fontStack, rangeStart)</c> — wired onto the
        /// MapView-owned <c>SymbolSubsystem</c> in <see cref="Render"/>, between <c>SetCamera</c> and
        /// <c>SetStyle</c>. A scene that never calls this leaves the glyph factory null, so a fill-only scene
        /// takes the unchanged production path.</summary>
        /// <param name="ranges">Fixture glyph-range bytes, keyed the same way
        /// <c>TestGlyphSource.FromRanges</c> serves them.</param>
        public VisualScene Glyphs(IReadOnlyDictionary<(string fontName, int rangeStart), byte[]> ranges)
        {
            _glyphSourceFactory = () => TestGlyphSource.FromRanges(ranges);
            return this;
        }

        /// <summary>Convenience single-font, single-range overload of <see cref="Glyphs(IReadOnlyDictionary{ValueTuple{string,int},byte[]})"/> —
        /// covers the common case (one fixture font, the 0-255 ASCII range) without the caller assembling a
        /// dictionary.</summary>
        /// <param name="fontName">The <c>text-font</c> stack entry this range answers for.</param>
        /// <param name="range0Bytes">The codepoint-range-0 glyph PBF bytes.</param>
        public VisualScene Glyphs(string fontName, byte[] range0Bytes)
            => Glyphs(new Dictionary<(string, int), byte[]> { [(fontName, 0)] = range0Bytes });

        /// <summary>The placed-quad count <see cref="SpinUntilSymbolsReady"/> pumps <c>LateUpdate</c> toward
        /// before <see cref="Render"/> snapshots — the number of authored symbols times the
        /// glyph count of their text (a single-character symbol is 1 quad). Required whenever the scene
        /// declares a <see cref="SymbolTextVisualLayer"/>; a symbol scene that never calls this has no
        /// readiness target and <see cref="Render"/> throws rather than guessing a frame count
        /// (always-bound-loops).</summary>
        public VisualScene ExpectSymbolQuads(int quads)
        {
            _expectedSymbolQuads = quads;
            return this;
        }

        /// <summary>The style JSON this scene would hand to <c>SetStyle</c> — the compile-checkpoint-A seam
        /// (no render, no MapView). Also the string <see cref="Render"/> actually parses.</summary>
        public string BuildStyleJson()
        {
            var sourceEntries = new List<string>(_sources.Count);
            foreach (VisualSource s in _sources)
                sourceEntries.Add($"\"{s.Id}\":{s.ToSourceJson()}");

            var layerEntries = new List<string>(_layers.Count);
            foreach (VisualLayer l in _layers)
                layerEntries.Add(l.ToLayerJson());

            // Root "glyphs" URL template (Style Spec): SymbolSubsystem.SetStyle bails out (no _builder,
            // no symbols — a WARNING, not a throw) whenever style.Glyphs is null/empty, BEFORE it ever
            // consults GlyphSourceFactoryOverride — so a symbol scene needs a non-empty value here even
            // though GlyphSourceFactoryOverride means the URL itself is never actually fetched. Emitted only
            // when a glyph source was supplied (Glyphs(...)), so a fill-only scene's JSON is unchanged.
            string glyphsMember = _glyphSourceFactory != null
                ? "\"glyphs\":\"https://fixture.invalid/glyphs/{fontstack}/{range}.pbf\","
                : "";

            return "{\"version\":8," + glyphsMember +
                   $"\"sources\":{{{string.Join(",", sourceEntries)}}}," +
                   $"\"layers\":[{string.Join(",", layerEntries)}]}}";
        }

        /// <summary>
        /// Drives the full scene: lit-ambient recipe → MapView (test materials, tile-selection clamped to
        /// <c>floor(zoom)</c>) → this scene's own camera + off-screen RT + <see cref="MapCamera"/>, wired via
        /// <c>SetCamera</c> → the real style JSON parsed by <see cref="StyleParser.Parse(string)"/> →
        /// <c>SetStyle</c> → pump to settled → re-sync the camera → render.
        /// </summary>
        /// <param name="px">Square render-target size, device pixels.</param>
        /// <returns>The rendered frame, plus the seams (<see cref="StyleDocument"/>, MapView, camera) later
        /// assertions read.</returns>
        public VisualFrame Render(int px = 512)
        {
            if (!_cameraSet)
                throw new InvalidOperationException(
                    "VisualScene.Camera(...) must be called before Render() — the composer has no default pose.");

            // ── Lit-ambient recipe — REQUIRED: fill is URP Lit and renders near-black at ambient-only.
            // Ambient/light only (matches MapViewSnapshotTests / FillPaintSnapshotTests.cs:102-105) —
            // deliberately NOT QualitySettings.SetQualityLevel: that call is TiltedGroundScene.Create's, whose
            // scenes render hand-built MeshRenderer geometry, never the tile backend; forcing quality level 0
            // can swap in a different (or absent) URP Render Pipeline Asset for the batched/Entities tile
            // path while leaving plain-MeshRenderer fixtures unaffected. ───────────────────────────────────
            _savedAmbient = (RenderSettings.ambientMode, RenderSettings.ambientLight);
            _ambientSaved = true;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

            _lightGo = new GameObject("VisualScene_DirLight");
            _lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = _lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;

            // ── MapView (WithTestMaterials, tile-selection clamped to this scene's zoom). ──────────────────
            _mapGo   = new GameObject("VisualScene_MapView");
            _mapView = _mapGo.AddComponent<MapViewComponent>().WithTestMaterials(_renderMode);
            int tileZoom = _cameraProperties.IntegerZoom;
            _mapView.Config.TileSelection.MinZoom = tileZoom;
            _mapView.Config.TileSelection.MaxZoom = tileZoom;
            _mapView.Config.MaxConsumesPerTick   = 64;
            _mapView.Config.MaxMeshBuildsPerTick = 64;
            // The tile render backend — default Entities (the PRODUCT DEFAULT), so the suite validates the
            // shipping path; overridable via Backend(). Rendering correctly on Entities requires the warm-up
            // pump after settle (WarmupFrames, below): the earlier blank-on-Entities symptom was NOT a product
            // bug but EG/BRG warm-up latency. Config.Backend is an already-public field, so this is a
            // test-assembly configuration choice, not a production change.
            _mapView.Config.Backend = _backend;

            // ── This scene's OWN camera + off-screen RT + MapCamera — the one camera. Seeded with the
            // requested pose at construction (MapCamera's ctor calls SyncToCamera itself), THEN wired, so the
            // camera is correct before SetStyle builds render layers at the current zoom. ──────────────────
            _cameraGo    = new GameObject("VisualScene_Camera");
            _unityCamera = _cameraGo.AddComponent<UnityEngine.Camera>();
            _rt          = new RenderTexture(px, px, 24, RenderTextureFormat.ARGB32);
            _unityCamera.targetTexture   = _rt;
            _unityCamera.clearFlags      = CameraClearFlags.SolidColor;
            _unityCamera.backgroundColor = BackgroundColor;
            _unityCamera.enabled         = false;

            _mapCam = new MapCamera(_unityCamera, _cameraProperties);
            _mapView.SetCamera(_mapCam);

            // Glyph seam: AFTER SetCamera builds View, BEFORE SetStyle below consumes it. Only wired when a
            // glyph source was supplied, so a fill-only scene leaves this null and takes the production
            // GlyphSourceFactory.Create path.
            if (_glyphSourceFactory != null)
                _mapView.View.SymbolSubsystem.GlyphSourceFactoryOverride = _ => _glyphSourceFactory();

            // ── The real style path: assemble → parse → SetStyle → pump to settled. ─────────────────────────
            string json = BuildStyleJson();
            StyleDocument doc = StyleParser.Parse(json);
            SpinToCompleted(_mapView.SetStyle(doc, "visual-scene"));
            PumpUntilSettled(_mapView);

            // Warm-up (WarmupFrames): pump extra LateUpdate/Rebuild ticks so the Entities-Graphics BRG batch is
            // cullable before the snapshot — rendering on the settle frame itself is blank (see WarmupFrames).
            for (int f = 0; f < WarmupFrames; f++) _mapView.LateUpdate();

            // Bounded symbol-readiness settle, LAST before the snapshot: a style with a symbol layer needs
            // its symbols STAGED, not merely tile-settled — pump until LastQuadCount/LastSurvivorCount reach
            // the authored count, snapshotting the very next frame. An inserted frame after that is a flake
            // source. Only a scene declaring a symbol layer spins here.
            if (_layers.Exists(l => l is SymbolTextVisualLayer))
                SpinUntilSymbolsReady();

            // ── Render. Re-sync FIRST — SyncToCamera's last act pushes the process-global
            // _MapFrameMetersPerDevicePixel ruler (see TiltedGroundScene.Render), so a scene that renders
            // always pushes its own ruler immediately before rendering. ────────────────────────────────────
            _mapCam.SyncToCamera();
            using var snapshot = new SnapshotRenderer(px, px);
            snapshot.Render(_unityCamera);

            return new VisualFrame(
                snapshot.Pixels, px, px, BackgroundByte, doc, _mapView, _unityCamera);
        }

        /// <summary>Blocks the calling thread until <paramref name="task"/> completes — mirrors
        /// <c>GeoJsonSourceTests.SpinToCompleted</c> (not reused directly: that helper is private to its own
        /// fixture).</summary>
        private static void SpinToCompleted(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }

        /// <summary>Pumps <c>LateUpdate</c> until every loaded tile has settled — mirrors
        /// <c>GeoJsonSourceTests.PumpUntilSettled</c>. A multi-tile cover still settles even though the
        /// geojson probe returns definitively-absent null handles for disjoint tiles, and
        /// <c>AllTilesSettled</c> treats those as settled too.
        ///
        /// <para>The real <c>LateUpdate</c> Tick does the consume + symbol harvest every iteration, unlike
        /// <c>TileManager.DrainMeshBuilds</c>, which is "symbol-silent" — its two call sites pass no
        /// <c>symbolPass</c>, so the per-consumed-layer symbol harvest never fires. Between ticks,
        /// <see cref="TileManager.AwaitInFlightMeshBuilds"/> only supplies the ThreadPool wall-clock; it
        /// consumes and harvests nothing, so it cannot reintroduce the symbol-silent regression. Every
        /// <see cref="VisualScene"/> consumer shares this helper, symbol and fill-only alike, so it stays on
        /// the real per-frame Tick path.</para></summary>
        private static void PumpUntilSettled(MapViewComponent view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                view.AwaitInFlightMeshBuilds();
            }
        }

        /// <summary>Pumps <c>LateUpdate</c> until the MapView-owned <c>SymbolPlacementSystem</c> has both
        /// STAGED and SURVIVED <see cref="_expectedSymbolQuads"/> quads, or fails loud at
        /// <see cref="SymbolReadinessCeiling"/> (always-bound-loops) — never silently renders a symbol-less
        /// frame. Checks <c>LastSurvivorCount</c> alongside <c>LastQuadCount</c>: the collision verdict is
        /// harvested one tick late (see <c>OffLookAtSymbolScene</c>'s duplicate-Tick note), so a
        /// spin that stopped at quad-count-reached alone could snapshot a frame whose survivor count is still
        /// stale.</summary>
        private void SpinUntilSymbolsReady()
        {
            if (!_expectedSymbolQuads.HasValue)
                throw new InvalidOperationException(
                    "VisualScene declares a symbol layer but ExpectSymbolQuads(...) was never called — the " +
                    "bounded readiness spin has no target frame count to pump toward.");

            int expected = _expectedSymbolQuads.Value;
            var placement = _mapView.View.SymbolPlacementSystem;
            for (int f = 0; f < SymbolReadinessCeiling; f++)
            {
                _mapView.LateUpdate();
                if (placement.LastQuadCount >= expected && placement.LastSurvivorCount >= expected) return;
            }

            throw new InvalidOperationException(
                $"VisualScene symbol readiness spin exhausted its {SymbolReadinessCeiling}-frame ceiling " +
                $"without reaching {expected} placed+surviving quads — observed " +
                $"LastQuadCount={placement.LastQuadCount}, LastSurvivorCount={placement.LastSurvivorCount}, " +
                $"LastInputSymbolCount={placement.LastInputSymbolCount}. This is a genuine pipeline finding " +
                "(geojson→symbol produced fewer labels than authored), not a flake to retry around.");
        }

        /// <summary>Tears down in order: view → camera GO → RT → light/ambient → MapView GO.
        /// <c>DestroyImmediate(_mapGo)</c> also fires <c>MapViewComponent.OnDestroy</c> → a second
        /// <c>Teardown()</c>, which is harmless: <c>MapView.Teardown</c> is safe to call twice.</summary>
        public void Dispose()
        {
            _mapView?.Teardown();

            if (_cameraGo != null) { UnityEngine.Object.DestroyImmediate(_cameraGo); _cameraGo = null; }
            if (_rt != null)
            {
                _rt.Release();
                UnityEngine.Object.DestroyImmediate(_rt);
                _rt = null;
            }
            if (_lightGo != null) { UnityEngine.Object.DestroyImmediate(_lightGo); _lightGo = null; }
            if (_ambientSaved)
            {
                RenderSettings.ambientMode  = _savedAmbient.mode;
                RenderSettings.ambientLight = _savedAmbient.light;
                _ambientSaved = false;
            }
            if (_mapGo != null) { UnityEngine.Object.DestroyImmediate(_mapGo); _mapGo = null; }
        }
    }
}
#endif // UNITY_EDITOR
