using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Source;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// Selects the tile render backend. All three register one draw item per tile-layer mesh behind
    /// <see cref="ITileRenderBackend"/> and are driven by a per-frame floating-origin <c>Rebuild</c>; only
    /// the submission differs (Entities Graphics, raw BRG, or stock GameObjects).
    /// </summary>
    public enum RenderBackend
    {
        /// <summary>Default (S53c): each tile-layer draw item is an <see cref="Backend.Entities.TileRenderer"/>
        /// entity rendered via Entities Graphics (BatchRendererGroup under the hood), grouped per tile and
        /// inspectable/disable-able in the Entities Hierarchy. Value 0 so scenes serialized with the old
        /// default deserialize to Entities.</summary>
        Entities = 0,
        /// <summary>S49 BRG path: draw tile meshes via a hand-packed <see cref="Backend.BRG.TileRenderer"/>
        /// BatchRendererGroup. The zero-allocation production path.</summary>
        Brg      = 1,
        /// <summary>The original per-tile-layer GameObject path (<see cref="Backend.GameObjects.TileRenderer"/>):
        /// one MeshFilter+MeshRenderer child per layer under a <c>"Tile z/x/y"</c> container, drawn by the
        /// SRP Batcher. The simplest, most Inspector-debuggable backend — retired in S53c (the Entities
        /// Hierarchy covered the debug need) and restored as an explicit opt-in.</summary>
        GameObject = 2,
    }

    /// <summary>
    /// The live multi-tile render loop — per-layer styled fill/line rendering. A <b>plain C# class</b>
    /// (the MonoBehaviour host is <see cref="MapViewComponent"/>): correct by construction — it is built with
    /// its <see cref="MapViewConfig"/> and a non-null <see cref="MapCamera"/>, and owns its
    /// <see cref="RenderLayerSet"/> and <see cref="TileManager"/> from construction, so there are no
    /// "is it wired yet" null guards and no lifecycle flag: before <see cref="SetStyle(string,CancellationToken)"/>
    /// runs it is simply an empty map whose <see cref="Tick"/> selects a cover but has no sources to fetch from,
    /// so it renders nothing — a safe no-op, not an invalid state.
    ///
    /// <para>Architecture: one render bundle per fill/line style layer (declared/painter's order), each with a
    /// per-layer Material + ZoomStyleApplier, held by the <see cref="RenderLayerSet"/>. The tile lifecycle
    /// (cover→fetch→tessellate→consume→evict, disposal/leak guards) lives in <see cref="TileManager"/>, ticked
    /// once per frame. Per-frame: push zoom uniforms, snap the render origin to the look-at, rebuild, tick.</para>
    ///
    /// <para>Steady-state no-GC: the ApplyZoom loop is a plain <c>for</c> over a <c>List</c> (struct
    /// enumerator); TileManager early-outs before any allocation when the cover is clean and nothing pends.</para>
    ///
    /// <para>Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.</para>
    /// </summary>
    public sealed class MapView
    {
        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        //   ApplyZoom        — push zoom uniforms into every layer material (scales with layer count).
        //   InstancedRebuild — drive the render backend per frame (on Entities this ticks the EG system groups).
        //   ManagerTick      — cover select + request/release + build pump (CoverSelect/FetchPoll nest under it).
        private static readonly ProfilerMarker PmApplyZoom        = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.ApplyZoom");
        private static readonly ProfilerMarker PmInstancedRebuild = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.InstancedRebuild");
        private static readonly ProfilerMarker PmManagerTick      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.ManagerTick");

        // ── Injected collaborators (correct by construction — never null) ────────────────────────
        private readonly MapViewConfig _config;

        /// <summary>The map camera, owned by this view — the single source of camera state. Non-null, set
        /// once at construction: there is no re-injection (a new camera means a new MapView), so no setter.</summary>
        public MapCamera Camera { get; }

        private StyleDocument _style;

        // Per-style-layer render bundles (fills + lines), built at SetStyle. Owns the materials.
        private readonly Style.RenderLayerSet _layers = new Style.RenderLayerSet();

        // The tile lifecycle — owned by MapView, ticked once per frame. Built in the ctor (needs only _layers).
        internal readonly Tile.TileManager TileManager;

        // Camera-relative rendering: the scene's float render origin tracks the camera look-at every frame
        // (recomputed in Tick), so the look-at always sits at the render origin. Exposed for tests.
        private double2 _sceneOrigin;

        /// <summary>
        /// Builds the view over its <paramref name="config"/> (the Inspector knobs, shared by reference with
        /// <see cref="MapViewComponent"/>) and a non-null <paramref name="camera"/>. The
        /// <see cref="TileManager"/> is created here; a data source is wired later via
        /// <see cref="SetStyle(string,CancellationToken)"/>.
        /// </summary>
        public MapView(MapViewConfig config, MapCamera camera)
        {
            _config     = config ?? throw new ArgumentNullException(nameof(config));
            Camera      = camera ?? throw new ArgumentNullException(nameof(camera));
            TileManager = new Tile.TileManager(_layers);
        }

        // ── SetStyle — the style is the single source of truth ─────────────────────────────────
        // There is no separate "Initialise": the map is fully valid at construction (an empty map whose
        // Tick safely no-ops until data is wired). Loading/changing data is SetStyle — an async operation
        // (fetch style + TileJSON) that fits the MonoBehaviour host's async Start naturally.

        private string _styleId;

        /// <summary>S83b: the id of the active style (forward contract for S82's prepared-tile cache).
        /// For <see cref="SetStyle(string,CancellationToken)"/> it is the style URI; for the
        /// <see cref="StyleDocument"/> overload it is the caller-supplied id.</summary>
        internal string StyleId => _styleId;

        // S83b loader seams — production defaults; tests inject counting/offline fakes via InternalsVisibleTo.
        internal System.Func<string, CancellationToken, UniTask<string>> DocumentLoaderOverride;
        internal System.Func<string, IDataSource>                        TileSourceFactoryOverride;

        /// <summary>
        /// S83b: load a style from <paramref name="styleUri"/> (file:// or http(s)://), resolve each of its
        /// sources (inline <c>tiles[]</c>, else S83a TileJSON), wire one data pipeline per source-id, and
        /// build the render layers — each fetching from ITS OWN source. <c>styleId == styleUri</c>.
        /// </summary>
        public async UniTask SetStyle(string styleUri, CancellationToken ct = default)
        {
            var loader = DocumentLoaderOverride ?? StyleDocumentLoader.LoadTextAsync;
            string json = await loader(styleUri, ct);
            StyleDocument style = StyleParser.Parse(json);
            await SetStyle(style, styleUri, ct);
        }

        /// <summary>
        /// S83b: apply an already-parsed <paramref name="style"/> with a caller-supplied
        /// <paramref name="styleId"/>. A second call RESTYLES: <see cref="RenderLayerSet"/> rebuilds and the
        /// source registry diffs (unchanged sources keep their warm pipeline; removed are torn down).
        /// </summary>
        public async UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
        {
            _style   = style;
            _styleId = styleId;

            _layers.Build(_style, Camera.CurrentProperties.Zoom, _config.MaterialSet);

            var specs = await BuildSourceSpecs(style, ct);
            TileManager.SetSources(specs, _config.Backend);
        }

        /// <summary>
        /// S83b: resolves each rendered source-id of <paramref name="style"/> into a
        /// <see cref="Tile.TileManager.SourceSpec"/>. Inline <c>tiles[]</c> short-circuits (no TileJSON
        /// fetch); a <c>url</c>-only source fetches its TileJSON ONCE and resolves via S83a. Failure
        /// isolation: an offline/404/malformed TileJSON logs a warning and skips THAT source.
        /// </summary>
        private async UniTask<List<Tile.TileManager.SourceSpec>> BuildSourceSpecs(
            StyleDocument style, CancellationToken ct)
        {
            var loader  = DocumentLoaderOverride    ?? StyleDocumentLoader.LoadTextAsync;
            var factory = TileSourceFactoryOverride ?? TileDataSourceFactory.Create;

            // Distinct rendered (fill/line) source-ids in declared order.
            var seen    = new HashSet<string>();
            var ordered = new List<string>();
            foreach (var sl in style.Layers)
            {
                bool rendered = sl is MapRenderer.Core.Style.Fill.StyleLayer
                             || sl is MapRenderer.Core.Style.Line.StyleLayer;
                if (!rendered) continue;
                string sid = sl.Source ?? string.Empty;
                if (seen.Add(sid)) ordered.Add(sid);
            }

            var specs = new List<Tile.TileManager.SourceSpec>(ordered.Count);
            foreach (string sid in ordered)
            {
                SourceDefinition def = style.GetSource(sid);
                if (def == null)
                {
                    Debug.LogWarning($"[MapView.SetStyle] layer references undefined source '{sid}' — skipped.");
                    continue;
                }

                // Fetch + resolve the TileJSON ONCE when the source is url-only (inline tiles[] short-circuits).
                if (SourceResolver.NeedsTileJson(def))
                {
                    try
                    {
                        string tjText = await loader(def.Url, ct);
                        SourceResolver.Resolve(def, TileJsonParser.Parse(tjText));
                    }
                    catch (System.OperationCanceledException) { throw; }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[MapView.SetStyle] TileJSON load failed for source '{sid}' " +
                                         $"({def.Url}): {ex.Message}. Source skipped (no tiles).");
                        continue; // failure isolation — other sources still wire
                    }
                }

                if (def.Tiles == null || def.Tiles.Length == 0)
                {
                    Debug.LogWarning($"[MapView.SetStyle] source '{sid}' resolved to no tiles — skipped.");
                    continue;
                }

                string template = def.Tiles[0]; // first template (no multi-host round-robin yet)
                var key = Tile.TileManager.SourceKey.From(def);
                specs.Add(new Tile.TileManager.SourceSpec(
                    sid, key, def.MinZoom, def.MaxZoom, () => factory(template)));
            }
            return specs;
        }

        /// <summary>The scene root's current Mercator origin — this view's own per-frame state. Read by tests.</summary>
        internal double2 SceneOrigin => _sceneOrigin;

        /// <summary>The per-style-layer render bundles owned by this view. <c>internal</c>: tests read counts
        /// via <c>MapViewTestExtensions</c> (InternalsVisibleTo).</summary>
        internal Style.RenderLayerSet Layers => _layers;

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Deterministic frame update — the MonoBehaviour host feeds <c>Time.deltaTime</c>; tests feed an
        /// explicit dt. The camera drives its own transform on <see cref="MapCamera.Apply"/> (no per-frame
        /// animation), so a frame is just the tile loop; dt is retained for a future CameraController.
        /// </summary>
        public void UpdateFrame(double dt) => Tick();

        /// <summary>
        /// One frame of the live loop. Pushes zoom uniforms (always, even on a clean-cover frame), refreshes
        /// the scene origin, then ticks the <see cref="Tile.TileManager"/>. Allocation-free in steady state.
        /// </summary>
        public void Tick()
        {
            CameraProperties cam = Camera.CurrentProperties;

            // ApplyZoom first — so a fractional-zoom-only change always pushes uniforms (fill/line zoom paint,
            // live _MetersPerPixel for pixel line width, zoom-step dasharrays).
            using (PmApplyZoom.Auto())
                _layers.ApplyZoom(cam.Zoom);

            // Camera-relative rendering: snap the render origin to the look-at every frame, then place all
            // loaded tiles relative to it — best float precision, no threshold/rebase machinery.
            _sceneOrigin = cam.CenterMercator();

            using (PmInstancedRebuild.Auto())
                TileManager.InstancedRebuild(_sceneOrigin);

            EnsureSelector();
            using (PmManagerTick.Auto())
                TileManager.Tick(cam, BuildTileSelectionConfig());
        }

        // ── S71: visible-tile selector, rebuilt only when its algorithm config changes ──────────
        private int _selPadTiles = int.MinValue, _selMinZoom, _selMaxZoom, _selOnScreenTilePx;

        private void EnsureSelector()
        {
            if (TileManager.Selector == null ||
                _selPadTiles != _config.PadTiles || _selMinZoom != _config.MinZoom || _selMaxZoom != _config.MaxZoom ||
                _selOnScreenTilePx != _config.OnScreenTilePx)
            {
                TileManager.Selector = new ViewportCornerTileSelector(
                    _config.PadTiles, _config.MinZoom, _config.MaxZoom, _config.OnScreenTilePx);
                _selPadTiles = _config.PadTiles; _selMinZoom = _config.MinZoom; _selMaxZoom = _config.MaxZoom;
                _selOnScreenTilePx = _config.OnScreenTilePx;
            }
        }

        /// <summary>
        /// The per-frame view inputs the selector consumes. The camera IS the viewport: the framing viewport
        /// is the wrapped Unity camera's live pixel size (<see cref="MapCamera.ViewportPx"/>), normalised to
        /// LOGICAL pixels by <see cref="MapViewConfig.DevicePixelRatio"/> (S86 DPI slice) so an on-screen tile
        /// is the same physical size across panel densities; the projection is the pixel↔ground service the
        /// camera owns (Web-Mercator today).
        /// </summary>
        private Tile.TileManager.TileSelectionConfig BuildTileSelectionConfig()
            => new Tile.TileManager.TileSelectionConfig
            {
                FramingViewportPx       = Camera.ViewportPx / _config.DevicePixelRatio,
                Projection              = Camera.Projection,
                MaxBuildsPerTick        = _config.MaxBuildsPerTick,
                MaxTessellationsPerTick = _config.MaxTessellationsPerTick,
                MaxVerticesPerTick      = _config.MaxVerticesPerTick,
            };

        /// <summary>
        /// Releases all tile resources (via the <see cref="Tile.TileManager"/>), then disposes the
        /// RenderLayerSet's materials. Order matters: tiles first — their renderers reference layer
        /// materials. Idempotent (both disposes are). The MonoBehaviour host calls this from OnDestroy.
        /// </summary>
        public void Teardown()
        {
            TileManager.Dispose();  // tiles first — their renderers reference _layers' materials
            _layers.Dispose();
        }
    }
}
