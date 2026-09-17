using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;
// Aliased rather than imported wholesale: this file names the GeoJSON parse entry at exactly one site (the
// geojson branch of BuildSourceSpecs) and nothing else here should reach for the rest of that namespace.
using GeoJson = MapRenderer.Core.GeoJson;

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
        Brg = 1,

        /// <summary>The original per-tile-layer GameObject path (<see cref="Backend.GameObjects.TileRenderer"/>):
        /// one MeshFilter+MeshRenderer child per layer under a <c>"Tile z/x/y"</c> container, drawn by the
        /// SRP Batcher. The simplest, most Inspector-debuggable backend, an explicit opt-in.</summary>
        GameObject = 2,
    }

    /// <summary>
    /// The live multi-tile render loop — per-layer styled fill/line rendering. A <b>plain C# class</b>
    /// (the MonoBehaviour host is <see cref="MapViewComponent"/>): correct by construction — it is built with
    /// its <see cref="MapViewConfig"/> and a non-null <see cref="MapCamera"/>, and owns its
    /// <see cref="RenderLayerSet"/> and <see cref="TileManager"/> from construction, so there are no
    /// "is it wired yet" null guards and no lifecycle flag: before <see cref="SetStyle(string,CancellationToken)"/>
    /// runs it is simply an empty map whose <see cref="LateUpdate"/> selects a cover but has no sources to fetch from,
    /// so it renders nothing — a safe no-op, not an invalid state.
    ///
    /// <para>Architecture: one render bundle per fill/line style layer (declared/painter's order), each with a
    /// per-layer Material + ZoomStyleApplier, held by the <see cref="RenderLayerSet"/>. The tile lifecycle
    /// (cover→fetch→build→consume→evict, disposal/leak guards) lives in <see cref="TileManager"/>, ticked
    /// once per frame. Per-frame: push zoom uniforms, snap the render origin to the look-at, rebuild, tick.</para>
    ///
    /// <para>Steady-state no-GC: the ApplyZoom loop is a plain <c>for</c> over a <c>List</c> (struct
    /// enumerator); TileManager early-outs before any allocation when the cover is clean and nothing pends.</para>
    ///
    /// <para>Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.</para>
    /// </summary>
    public sealed class MapView
    {
        /// <summary>Profiler marker name constants (SSOT) for the per-frame view path — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // UMBRELLA over the whole per-frame pipeline. Its self-time (total − the children below) is the
            // residual unmarked cost: if it is ~0 every per-frame span is mapped.
            internal const string LateUpdate       = "MapRenderer.View.LateUpdate";
            internal const string CameraAdvance    = "MapRenderer.Camera.Advance";
            internal const string ApplyZoom        = "MapRenderer.View.ApplyZoom";
            internal const string InstancedRebuild = "MapRenderer.View.InstancedRebuild";
            internal const string ManagerTick      = "MapRenderer.Tile.ManagerTick";
            internal const string SceneFrame       = "MapRenderer.View.SceneFrame";
            internal const string SymbolCollect    = "MapRenderer.Symbol.Collect";
            internal const string SymbolBatch      = "MapRenderer.Symbol.BatchBuild";
        }

        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        private static readonly ProfilerMarker PmLateUpdate =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.LateUpdate);

        // Commit this frame's camera pose (SyncToCamera: pose math + transform/clip push).
        private static readonly ProfilerMarker PmCameraAdvance =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.CameraAdvance);

        // Push zoom uniforms into every layer material (scales with layer count).
        private static readonly ProfilerMarker PmApplyZoom =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoom);

        // Drive the render backend per frame (on Entities this ticks the EG system groups).
        private static readonly ProfilerMarker PmInstancedRebuild =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.InstancedRebuild);

        // Cover select + request/release + build pump (CoverSelect/FetchPoll nest under it).
        private static readonly ProfilerMarker PmManagerTick =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ManagerTick);

        // Build the per-frame floating-origin scene frame (projection Project + tangent basis).
        private static readonly ProfilerMarker PmSceneFrame =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SceneFrame);

        // The symbol reconcile that runs before the symbol aggregation (A-1 pull/reconcile + PumpBuilds).
        private static readonly ProfilerMarker PmSymbolCollect =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolCollect);

        // The symbol AGGREGATION (A-3 cross-tile dedup CollectInto + record → SoA batch build). Its own
        // marker so the dedup/build cost is not misattributed to the unmarked LateUpdate self-time — it runs
        // between Symbol.Collect and SymbolPlacementSystem.Tick and is a managed main-thread hot spot in its own right
        // (it grows with the on-screen symbol count at high zoom).
        private static readonly ProfilerMarker PmSymbolBatch =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolBatch);

        // ── Injected collaborators (correct by construction — never null) ────────────────────────
        private readonly MapViewConfig _config;

        /// <summary>The map camera, owned by this view — the single source of camera state. Non-null, set
        /// once at construction: there is no re-injection (a new camera means a new MapView), so no setter.</summary>
        public MapCamera Camera { get; }

        private StyleDocument _style;

        /// <summary>UMR-151: the document the live layers were last SUCCESSFULLY built or restyled from —
        /// the in-place gate's <c>previous</c>. Distinct from <see cref="_style"/>, which commits early and
        /// stays advanced after an aborted rebuild; this field is null from the gate until either arm's end,
        /// so an abort in between forces the next call down the full-rebuild arm.</summary>
        private StyleDocument _committedStyle;

        // Style-transitions epic, Stage 2: the FillAntialiasing this view's TileManager.CurrentStyle token
        // was folded from. _config.FillAntialiasing is live-mutable; a toggle between two SetStyle calls
        // must not be absorbed by an in-place restyle that leaves the token stale (the token bakes it in,
        // §S82/UMR-95, since it changes vertices, not a uniform).
        private bool _committedFillAntialiasing;

        // Style-transitions epic, Stage 2, 4th gate conjunct: the base material REFERENCES the last real
        // Layers.Build ran against. MapMaterialSet is a mutable ScriptableObject — PreparedCacheTests'
        // FillExtrusionMaterialAssignedInPlace_.../FillExtrusionMaterialNulledInPlace_... mutate a field IN
        // PLACE (same MapMaterialSet reference) between two SetStyle calls with byte-identical style
        // content, specifically so a layer gains or drops a material and its dense id shifts. The in-place
        // restyle path re-binds EXISTING appliers against EXISTING materials; it cannot discover a layer
        // that would now build (or now be skipped) without re-running RenderLayerFactory, so it must refuse
        // whenever any base material reference has changed since the last Build — Material equality
        // (Unity's fake-null-aware Equals), not just a null/non-null check, because Restyle also never
        // re-clones a swapped-but-still-non-null base.
        private (Material fill, Material line, Material fillExtrusion, Material symbolText, Material symbolIcon)
            _committedMaterials;

        private static (Material, Material, Material, Material, Material) MaterialSnapshot(Materials.MapMaterialSet set)
            => (set.FillMaterial, set.LineMaterial, set.FillExtrusionMaterial, set.SymbolTextWorld, set.SymbolIconWorld);

        /// <summary>Test seam for the style-transition clock. Production → <c>Time.unscaledTimeAsDouble</c>
        /// (UNSCALED: a theme change is a UI-class animation that must ease under <c>timeScale == 0</c>).
        /// <see cref="Text.SymbolPlacementSystem.EaseFade"/> runs on SCALED time, so the two clocks disagree
        /// under <c>timeScale != 1</c> — pre-existing, and not this epic's to fix.</summary>
        internal Func<double> NowSecondsOverride { get; set; }
        // Not `(NowSecondsOverride ?? DefaultNowSeconds)()`: that reads as a method-group-to-delegate
        // conversion on every call, which this project's C# version does not cache — an allocation inside
        // the scope MapView_SteadyStateTick_DoesNotAllocateGCMemory measures. The ternary never converts.
        private double NowSeconds => NowSecondsOverride != null ? NowSecondsOverride() : Time.unscaledTimeAsDouble;

        /// <summary>How long a restyled uniform binding eases from its old value to its new one. The ONLY
        /// source of the duration/delay: no style key is read (epic decision 0a).</summary>
        public Style.StyleTransition StyleTransition { get; set; } = Style.StyleTransition.Default;

        // Per-style-layer render bundles (fills + lines), built at SetStyle. Owns the materials.
        /// <summary>The per-style-layer render bundles owned by this view. <c>internal</c>: tests read counts
        /// via <c>MapViewTestExtensions</c> (InternalsVisibleTo).</summary>
        internal Style.RenderLayerSet Layers { get; } = new Style.RenderLayerSet();

        // The tile lifecycle — owned by MapView, ticked once per frame. Built in the ctor (needs only Layers).
        internal readonly Tile.TileManager TileManager;

        // ── S20 Slice 1: the per-frame symbol placement path (F1) — a SEPARATE path from the tile lifecycle
        // above, never a static per-(tile,layer) mesh (T5). Needs no ctor dependency (unlike TileManager).
        /// <summary>The dedicated per-frame symbol renderer. <c>internal</c>: test surface (job-parity /
        /// alloc / structural teeth read it via <c>MapViewTestExtensions</c>-style InternalsVisibleTo).</summary>
        internal SymbolPlacementSystem SymbolPlacementSystem { get; }

        /// <summary>
        /// Builds the view over its <paramref name="config"/> (the Inspector knobs, shared by reference with
        /// <see cref="MapViewComponent"/>) and a non-null <paramref name="camera"/>. The
        /// <see cref="TileManager"/> is created here; a data source is wired later via
        /// <see cref="SetStyle(string,CancellationToken)"/>.
        /// </summary>
        public MapView(MapViewConfig config, MapCamera camera)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Camera  = camera ?? throw new ArgumentNullException(nameof(camera));
            // S82: the PreparedTileCache's Enabled toggle + byte/count budget — maintainer-tunable Inspector
            // fields (placeholder budget defaults pending in-editor VRAM profiling, stage Risk 3).
            TileManager = new Tile.TileManager(Layers, _config.PreparedCache);
            // S20: one symbol system per view, owning this view's camera (constructed here, after Camera is
            // set — a field initializer would see a null Camera). Epic A / A1 (design §11 A1 D7): the
            // world-anchored point/icon draw path's base materials — GUID assets, no Shader.Find (S58
            // architecture); the icon base rides alongside the text one, both optional (null → that draw
            // path stays inert, see MapMaterialSet.SymbolIconWorld's doc).
            SymbolPlacementSystem = new SymbolPlacementSystem(Camera,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolTextWorld : null,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolIconWorld : null);
            // S105: the decoupled symbol subsystem produces the real map symbols SymbolPlacementSystem.Tick renders.
            // D11/E2: per-layer materials (SymbolTextWorld clone + text-halo-* bind) now live on each
            // SymbolRenderLayer (Layers.Build), not here. A5b: DATA arrives via TileManager's per-tile KICK
            // (SymbolSubsystem implements ISymbolTileWorkerFactory); the tile LIFECYCLE is PULLED — each frame we
            // hand it TileManager's loaded set and it reconciles (no release/restore callbacks). cacheEnabled
            // drives keep-warm-on-release so it matches the prepared mesh cache.
            SymbolSubsystem = new SymbolSubsystem(Camera,
                _config.PreparedCache.MaxCount, _config.PreparedCache.Enabled);
            TileManager.SymbolWorkerFactory = SymbolSubsystem;
        }

        // S105: production symbols (real map data), fed to SymbolPlacementSystem.Tick each frame.
        internal readonly SymbolSubsystem SymbolSubsystem;

        // D10: reused scratch for SetStyle's symbol-layer derivation (below) — a restyle never allocates a
        // fresh list; the single registry (RenderLayerFactory) is walked once via Layers.Layers.
        private readonly List<Symbol.StyleLayer> _symbolStyleLayers = new List<Symbol.StyleLayer>();

        // D11/E2: the SymbolRenderLayer objects themselves (same walk as _symbolStyleLayers, same order) —
        // handed to SymbolPlacementSystem.Tick each frame so each layer's survivors draw with its own material/presenter.
        private readonly List<Style.SymbolRenderLayer> _symbolRenderLayers = new List<Style.SymbolRenderLayer>();

        // A-1: reused scratch for the per-frame loaded-tile pull handed to the subsystem's reconcile (no alloc).
        private readonly List<Tile.LoadedTileKey> _loadedTileKeys = new List<Tile.LoadedTileKey>();

        // ── SetStyle — the style is the single source of truth ─────────────────────────────────
        // There is no separate "Initialise": the map is fully valid at construction (an empty map whose
        // Tick safely no-ops until data is wired). Loading/changing data is SetStyle — an async operation
        // (fetch style + TileJSON) that fits the MonoBehaviour host's async Start naturally.


        /// <summary>The id of the active style.
        /// For <see cref="SetStyle(string,CancellationToken)"/> it is the style URI; for the
        /// <see cref="StyleDocument"/> overload it is the caller-supplied id.</summary>
        internal string StyleId { get; private set; }

        // S83b loader seams — production defaults; tests inject counting/offline fakes via InternalsVisibleTo.
        internal System.Func<string, CancellationToken, UniTask<string>> DocumentLoaderOverride;
        internal System.Func<string, IDataSource>                        TileSourceFactoryOverride;

        /// <summary>UMR-151: the six mutation sites of <see cref="SetStyle(StyleDocument,string,CancellationToken)"/>'s
        /// full-rebuild arm after which an exception would leave persistent state a later call or frame
        /// reads (the predicate `docs/tile-pipeline-design.md` §1.10 enumerates). Named for the site,
        /// in commit order.</summary>
        internal enum CommitPhase
        {
            /// <summary>After <see cref="_style"/>/<see cref="StyleId"/> commit — old style must stay fully live.</summary>
            IdentityCommitted,
            /// <summary>After the <see cref="_committedFillAntialiasing"/>/<see cref="_committedMaterials"/> memo
            /// write — the in-place gate's own memo must not outrun the build.</summary>
            MaterialMemoWritten,
            /// <summary>After <see cref="Style.RenderLayerSet.Build"/> — leak baseline from here on.</summary>
            LayersBuilt,
            /// <summary>After the <see cref="Tile.TileManager.CurrentStyle"/> token write.</summary>
            StyleTokenWritten,
            /// <summary>After <see cref="Text.SymbolSubsystem.SetStyle"/>.</summary>
            SymbolStyleApplied,
            /// <summary>Inside <see cref="Tile.TileManager.SetSources"/>'s teardown loop, once per record —
            /// the one phase whose OWN interior can throw mid-teardown.</summary>
            SourcesTeardownRecord,
        }

        /// <summary>UMR-151 test seam: null in production (a per-call delegate check, not a per-frame one —
        /// this method is not a hot path). Set by a test to throw at a chosen <see cref="CommitPhase"/> and
        /// observe what the full-rebuild arm leaves behind. Reached via the existing
        /// <c>InternalsVisibleTo("MapRenderer.Tests.EditMode")</c> (<c>MapRenderer.Unity/AssemblyInfo.cs</c>).</summary>
        internal Action<CommitPhase> CommitProbe;

        /// <summary>
        /// S83b: load a style from <paramref name="styleUri"/> (file:// or http(s)://), resolve each of its
        /// sources (inline <c>tiles[]</c>, else S83a TileJSON), wire one data pipeline per source-id, and
        /// build the render layers — each fetching from ITS OWN source. <c>styleId == styleUri</c>.
        /// </summary>
        public async UniTask SetStyle(string styleUri, CancellationToken ct = default)
        {
            var           loader = DocumentLoaderOverride ?? StyleDocumentLoader.LoadTextAsync;
            string        json   = await loader(styleUri, ct);
            StyleDocument style;
            try
            {
                // UMR-108: paint/layout expressions parse eagerly here, so a malformed-but-valid-JSON
                // expression (bad interpolate/step stops, wrong arity, unknown operator) throws NOW rather
                // than lazily on first access. Caught here, before SetStyle(StyleDocument,...) — the overload
                // commits _style/StyleId/TileManager.CurrentStyle as its very first step, so returning without
                // calling it leaves the previous style fully live (no half-applied restyle).
                style = StyleParser.Parse(json, _config.FillAntialiasing);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MapView.SetStyle] failed to parse style '{styleUri}' — keeping the previous style live. {ex}");
                return;
            }
            await SetStyle(style, styleUri, ct);
        }

        /// <summary>
        /// S83b: apply an already-parsed <paramref name="style"/> with a caller-supplied
        /// <paramref name="styleId"/>. A second call RESTYLES: <see cref="RenderLayerSet"/> rebuilds and the
        /// source registry diffs (unchanged sources keep their warm pipeline; removed are torn down).
        /// </summary>
        public async UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
        {
            // Epic A / A2 (design §E step 3, MED 4 / HIGH b — transactional restyle, option (i) bounded):
            // the ONE await runs FIRST, before any layer/identity mutation, so the OLD style's layers/
            // materials/backend/identity stay fully live through the resolution window — a delayed or
            // cancelled restyle leaves the previous map rendering (no blank background, no destroyed-
            // material window, no new-cache-token probe under old visuals). BuildSourceSpecs now derives its
            // source-ids from style.Layers directly (RenderLayerFactory.TryGetFetchSource), NOT from the
            // not-yet-built Layers.Layers.
            var specs = await BuildSourceSpecs(style, ct);
            ct.ThrowIfCancellationRequested(); // last safe abort — nothing mutated yet (old style stays intact)

            // Round-4 TOCTOU fix: MapMaterialSet fields (and _config.MaterialSet itself) are live-mutable, so
            // capture ONCE here and validate that SAME captured reference — a concurrent mutation between
            // this check and its use (Layers.Build below) cannot slip a null base past validation (no await
            // between validate and use). Fail-loud: an unconfigured base is a developer configuration error.
            // Live-mutable here is a WITHIN-one-call guard only — a field mutation (or an outright swap to a
            // different MapMaterialSet) that shifts the layer numbering ACROSS style loads is caught by the
            // cache token's layer-numbering fold (below), not by this guard.
            var materialSet = _config.MaterialSet;
            materialSet.Validate();

            // UMR-95: fail loud HERE (before any commit) — a null Root at Digest's site (after Build) would
            // leave the new style/id/layers installed under the OLD token. Digest's own check must never fire.
            if (style.Root == null)
                throw new InvalidOperationException("StyleDocument.Root is null — the prepared cache's " +
                    "cache-key digest needs it (a hand-built StyleDocument must set Root).");

            // Style-transitions epic, Stage 2 + Stage 3 (UMR-151) — docs/tile-pipeline-design.md §1.10.
            // UMR-151: the last SUCCESSFULLY committed document, nulled for this call's duration — an abort
            // below leaves it null, so the next call takes the full rebuild arm (correct, merely not fast).
            StyleDocument   previous   = _committedStyle; // null on the first load — inPlace is false, as it must be
            _committedStyle            = null;
            Style.StyleTransition transition = StyleTransition;
            double          now        = NowSeconds;
            bool inPlace = previous != null
                        && _config.FillAntialiasing == _committedFillAntialiasing
                        && MaterialSnapshot(materialSet).Equals(_committedMaterials)
                        && TileManager.SourcesUnchanged(specs)
                        && Layers.TryRestyleInPlace(previous, style, transition, now);

            // Identity commits HERE (not before the await, HIGH b) — a delayed restyle must not run the OLD
            // layers/pipelines under the NEW cache token, and a cancel above must not report the new identity.
            _style  = style;
            StyleId = styleId;
            CommitProbe?.Invoke(CommitPhase.IdentityCommitted);

            if (inPlace)
            {
                // UMR-151, unconditional here (including a pure reorder) — docs/tile-pipeline-design.md §1.10.
                TileManager.RestyleSourcesInPlace(specs, _config.Backend);

                // Layers.Build / TileManager.CurrentStyle / SymbolSubsystem.SetStyle / LogSkippedLayers /
                // _symbolStyleLayers/_symbolRenderLayers deliberately skipped — docs/tile-pipeline-design.md §1.10.
                Layers.ApplyZoom(new Style.StyleFrameInputs(
                    Camera.CurrentProperties.Zoom, _config.DevicePixelRatio, now, transition));
                TileManager.PushLayerDrawGates(); // the restyle may have moved a layer's zoom range
                _committedStyle = style; // UMR-151: the in-place patch completed — the gate may trust it again
                return;
            }

            _committedFillAntialiasing = _config.FillAntialiasing;
            _committedMaterials        = MaterialSnapshot(materialSet);
            CommitProbe?.Invoke(CommitPhase.MaterialMemoWritten);

            Layers.Build(_style, Camera.CurrentProperties.Zoom, materialSet);
            CommitProbe?.Invoke(CommitPhase.LayersBuilt);
            // S82/UMR-95: token = styleId + Root + built numbering + FillAntialiasing (bakes into vertices,
            // not a uniform — a toggle changes neither Root's bytes nor the numbering) — set AFTER Build.
            TileManager.CurrentStyle = new Tile.StyleToken(JsonCanonical.Digest(
                StyleId, _style.Root, LayerNumbering(Layers) + "|aa=" + _config.FillAntialiasing));
            CommitProbe?.Invoke(CommitPhase.StyleTokenWritten);
            LogSkippedLayers(Layers.SkippedLayers); // UMR-116: once per style load, never per tile/frame
            // S107: Build seeds every layer's px uniforms at dpr 1. SetStyle is async — its continuation can
            // resume AFTER this frame's LateUpdate has already run — and on a RESTYLE the previous tiles are
            // still loaded, so the newly-built layers would draw them once at the seeded ratio (roads and
            // halos ~36 % too thin at a ratio of 1.56). One line closes that window for every layer kind,
            // including the symbol halo, which has no seed of its own at all.
            Layers.ApplyZoom(new Style.StyleFrameInputs(Camera.CurrentProperties.Zoom, _config.DevicePixelRatio, now, transition));
            TileManager.PushLayerDrawGates(); // fade advanced above; the gate must not lag it by a frame
            // D10: derive the symbol layers from the just-built set (RenderLayerFactory is the sole
            // registry) instead of re-walking style.Layers with an is-check (kills §1.6). One walk, two
            // lists (D11/E2): the typed StyleLayer for the subsystem, the owning SymbolRenderLayer (its
            // material + presenter) for SymbolPlacementSystem.Tick — same order, so the ordinal mapping stays 1:1.
            // A2: the Mercator-only background gate is gone — background is now a per-covered-tile TileMesh
            // layer projected through the same IProjection fill/line use, so the globe renders it correctly.
            _symbolStyleLayers.Clear();
            _symbolRenderLayers.Clear();
            foreach (var layer in Layers.Layers)
                if (layer is Style.SymbolRenderLayer s)
                {
                    _symbolStyleLayers.Add(s.SymbolLayer);
                    _symbolRenderLayers.Add(s);
                }

            SymbolSubsystem.SetStyle(_style,
                _symbolStyleLayers); // S105: group symbol layers + (re)build the shared glyph pipeline
            CommitProbe?.Invoke(CommitPhase.SymbolStyleApplied);

            TileManager.SetSources(specs, _config.Backend, RecordProbeOrNull());
            _committedStyle = style; // UMR-151: the rebuild completed — the gate may trust it again
        }

        /// <summary>UMR-151: the <see cref="CommitPhase.SourcesTeardownRecord"/> half of <see cref="CommitProbe"/>
        /// — its ONE phase whose site lives inside <see cref="Tile.TileManager.SetSources"/>, not here, so it
        /// has to cross the call as a delegate rather than an inline invoke. Null when no probe is installed
        /// (production; also a per-call, not per-frame, allocation when one is).</summary>
        private Action RecordProbeOrNull()
            => CommitProbe != null ? () => CommitProbe(CommitPhase.SourcesTeardownRecord) : null;

        /// <summary>UMR-116: warns once, naming every layer <see cref="Style.RenderLayerSet.Build"/> skipped
        /// for an actual compatibility reason (unsupported kind / unconfigured material). A by-design skip
        /// stays silent — <see cref="Style.LayerSkipReason.GenuinelyUnpainted"/> (a source-less symbol
        /// layer), <see cref="Style.LayerSkipReason.Hidden"/> (<c>visibility: none</c>) and
        /// <see cref="Style.LayerSkipReason.FullyTransparent"/>, each documented on its own enum member.
        /// Internal (not private):
        /// a test-assembly caller exercises the GenuinelyUnpainted suppression directly, via
        /// InternalsVisibleTo (no production caller besides <c>SetStyle</c>).</summary>
        internal static void LogSkippedLayers(IReadOnlyList<Style.SkippedLayer> skipped)
        {
            var problems = new List<string>();
            for (int i = 0; i < skipped.Count; i++)
            {
                Style.SkippedLayer s = skipped[i];
                if (s.Reason is Style.LayerSkipReason.GenuinelyUnpainted
                             or Style.LayerSkipReason.Hidden
                             or Style.LayerSkipReason.FullyTransparent) continue;
                problems.Add($"'{s.Id}' ({s.RawType}): {s.Reason}");
            }
            if (problems.Count > 0)
                Debug.LogWarning(
                    $"[MapView.SetStyle] {problems.Count} style layer(s) not rendered: {string.Join(", ", problems)}");
        }

        /// <summary>UMR-95: a plain-text encoding of the dense (index, id) pairs the layer set just built —
        /// folded into the cache token so a numbering shift (a skipped/added layer, from EITHER the style or a
        /// slot-dropping <c>MapMaterialSet</c> field) changes the token even under unchanged style content.
        /// <c>RenderLayerCompatibilitySummaryTests</c> pins that it folds the (index, id) PAIRS, not just
        /// <see cref="Style.RenderLayerSet.Count"/> — why per-index not count is in <c>docs/tile-pipeline-design.md</c> §1.9.</summary>
        internal static string LayerNumbering(Style.RenderLayerSet layers)
        {
            var sb = new StringBuilder();
            for (int li = 0; li < layers.Count; li++)
                sb.Append(li).Append(':').Append(layers[li].StyleLayer?.Id).Append('|');
            return sb.ToString();
        }

        /// <summary>
        /// S83b: resolves each rendered source-id of <paramref name="style"/> into a
        /// <see cref="Tile.TileManager.SourceSpec"/>. Inline <c>tiles[]</c> short-circuits (no TileJSON
        /// fetch); a <c>url</c>-only source fetches its TileJSON ONCE and resolves via S83a. Failure
        /// isolation: an offline/404/malformed TileJSON logs a warning and skips THAT source.
        ///
        /// Runs BEFORE <see cref="Style.RenderLayerSet.Build"/>, so it cannot walk the built
        /// <see cref="Layers"/> set. Walks <paramref name="style"/>'s raw layers through
        /// <see cref="Style.RenderLayerFactory.TryGetFetchSource"/> instead — the ONE registry of which style
        /// layers fetch MVT tiles (fill/line/symbol with a non-empty source; background is source-less by
        /// design; raster/circle/hillshade/unknown are excluded so no non-MVT bytes reach the MVT decode).
        /// </summary>
        // internal (not private): several EditMode/PlayMode teeth (e.g. A7TileFeatureSourceTests,
        // MapViewSourceSpecTests) drive this method directly, reached via InternalsVisibleTo — test-only
        // visibility widening, no behaviour change (ARCHITECTURE.md "test code must not bloat the
        // production codebase").
        internal async UniTask<List<Tile.TileManager.SourceSpec>> BuildSourceSpecs(
            StyleDocument style, CancellationToken ct)
        {
            var loader    = DocumentLoaderOverride    ?? StyleDocumentLoader.LoadTextAsync;
            var factory   = TileSourceFactoryOverride ?? TileDataSourceFactory.Create;
            IWorkScheduler scheduler = WorkSchedulerFactory.ForCurrentPlatform();

            // Distinct rendered source-ids in declared order.
            var seen    = new HashSet<string>();
            var ordered = new List<string>();
            foreach (var sl in style.Layers)
            {
                if (!Style.RenderLayerFactory.TryGetFetchSource(sl, out string sid)) continue;
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

                // A geojson source is sliced locally: no TileJSON, no tiles[], no byte fetcher at all. Branch
                // BEFORE the TileJSON fetch below (which it has no url for) and therefore before the
                // no-tiles skip (which it would always hit). Same failure-isolation shape as the two warns
                // around it — a bad source is skipped, never a thrown SetStyle.
                if (def.Type == SourceType.GeoJson)
                {
                    if (def.Data == null || !def.Data.IsObject)
                    {
                        Debug.LogWarning($"[MapView.SetStyle] geojson source '{sid}' needs an INLINE object " +
                                         "`data` (a URL-valued `data` is not supported yet) — skipped.");
                        continue;
                    }

                    GeoJson.GeoJsonDataset parsed;
                    try
                    {
                        parsed = GeoJson.GeoJsonParser.Parse(def.Data);
                    }
                    catch (System.OperationCanceledException)
                    {
                        throw;
                    }
                    catch (System.Exception ex)
                    {
                        // System.Exception, not just GeoJsonFormatException, so this really is the shape the
                        // comment above claims. Every current GeoJsonParser throw IS a format exception, so
                        // the widening changes nothing today — it is here because a parser edit that threw
                        // anything else would otherwise fault SetStyle for the WHOLE style over one bad
                        // source, which is the failure isolation this branch exists to provide. Cancellation
                        // is rethrown: swallowing it is its own defect, and the TileJSON path beside this
                        // makes the same exception.
                        Debug.LogWarning($"[MapView.SetStyle] geojson source '{sid}' failed to parse: " +
                                         $"{ex.Message}. Source skipped.");
                        continue;
                    }

                    // Slice options are per-source by design; v1 has no style key for them, so the default is
                    // supplied HERE, at the wiring site, and the source keeps taking them as a parameter.
                    var geoJsonOptions = GeoJson.GeoJsonSliceOptions.Default;
                    specs.Add(new Tile.TileManager.SourceSpec(
                        sid, Tile.TileManager.SourceKey.From(def), def.MinZoom, def.MaxZoom,
                        () => new Tile.Processing.GeoJsonTileFeatureSource(parsed, geoJsonOptions, scheduler)));
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
                    catch (System.OperationCanceledException)
                    {
                        throw;
                    }
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
                var    key      = Tile.TileManager.SourceKey.From(def);
                // Epic A / A7: the ONE production site that wraps the byte fetcher into the raised
                // ITileFeatureSource seam — TileManager never names the byte-level type (F-1).
                specs.Add(new Tile.TileManager.SourceSpec(
                    sid, key, def.MinZoom, def.MaxZoom,
                    () => new Tile.Processing.MvtTileFeatureSource(factory(template), scheduler)));
            }

            return specs;
        }


        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the live loop, mirroring (and driven from) <c>MapViewComponent.LateUpdate</c>. Runs in
        /// LateUpdate on purpose: the input <c>Controller</c> mutates the camera props in its <c>Update</c>, and
        /// Unity runs every LateUpdate after every Update, so this pipeline is GUARANTEED to see this frame's
        /// input — no execution-order attributes needed. The whole per-frame pipeline lives HERE, in one ordered
        /// pass off a SINGLE camera snapshot, so tiles and symbols are frame-coherent by construction:
        /// <list type="number">
        ///   <item>commit the camera (DPI refresh + <see cref="MapCamera.SyncToCamera"/>) — the merged input
        ///         state from every controller this frame;</item>
        ///   <item>move the tiles — push zoom uniforms, then rebase every loaded tile onto the look-at's
        ///         floating origin;</item>
        ///   <item>place the symbols — project their anchors against the SAME snapshot + just-committed camera.</item>
        /// </list>
        /// Telemetry is not a step here: each provider publishes at the end of its OWN pass (see the facade
        /// below), so its levels are that pass's rather than a shared end-of-frame instant's.
        /// Allocation-free in steady state.
        /// </summary>
        public void LateUpdate()
        {
            using var _lateUpdate = PmLateUpdate.Auto(); // umbrella: self-time = residual unmarked per-frame cost

            // 1. Update the camera FIRST — commit this frame's merged input to the Unity camera, so the tile
            //    rebase and the symbol projection below both read the just-committed pose. DPI is refreshed from
            //    the live config before the commit, and it now feeds TWO consumers, not one: the altitude
            //    framing here, and the selector's framing viewport built at BuildTileSelectionConfig() below
            //    (both are Camera.ViewportLogicalPx since S108). This ordering is also what keeps
            //    Camera.CameraRelativePosition fresh for BuildSceneFrame one line below.
            Camera.DevicePixelRatio = _config.DevicePixelRatio;
            using (PmCameraAdvance.Auto())
                Camera.SyncToCamera();

            // ONE snapshot for the rest of the frame — tiles and symbols share it, so they can't diverge.
            CameraProperties   cameraProperties = Camera.CurrentProperties;
            Backend.SceneFrame sceneFrame;
            using (PmSceneFrame.Auto())
                sceneFrame = BuildSceneFrame(cameraProperties);

            // 2. Move the tiles. ApplyZoom first — so a fractional-zoom-only change always pushes uniforms
            //    (fill/line zoom paint, zoom-step dasharrays for pixel line width). The applier also carries
            //    the px→device basis (S107): the style's logical px meet the device-pixel ratio here and
            //    nowhere else. Read from _config, which OWNS the ratio — Camera.DevicePixelRatio is the same
            //    value (copied one step above) but going through the camera would imply the camera owns the
            //    paint basis, which is the confusion MapCamera's own doc-comment records.
            using (PmApplyZoom.Auto())
            {
                // Re-read StyleTransition every frame so a live change to the property takes effect
                // without a restyle — passed straight into the inputs struct, not staged through a
                // settable property first.
                Layers.ApplyZoom(new Style.StyleFrameInputs(
                    cameraProperties.Zoom, _config.DevicePixelRatio, NowSeconds, StyleTransition));
                TileManager.PushLayerDrawGates();
            }

            // Camera-relative rendering: snap the render origin to the look-at every frame, then place all
            // loaded tiles relative to it — best float precision, no threshold/rebase machinery.
            // S91-C Slice 2: place tiles via the projection-agnostic scene frame built from the launch-time
            // projection + the look-at. Mercator: rebase = identity and SceneOriginRender = (mercX, 0, mercZ)
            // (== SceneFrame.Mercator(cam.CenterMercator())), so placement is bit-for-bit the pre-S91 translation.
            // Globe: rebase rotates every tile into the look-at's local ENU frame (up = +Y), so the same
            // CameraPoseMath.ComputeRelativePose orbit frames it.
            using (PmInstancedRebuild.Auto())
                TileManager.InstancedRebuild(sceneFrame);

            EnsureSelector();
            using (PmManagerTick.Auto())
                TileManager.Tick(cameraProperties, BuildTileSelectionConfig());

            // Hand the style's sprite sheet to the layers that paint from it (fill-pattern today). Pulled per
            // frame rather than pushed from the fetch because the sheet is owned and fetched by the symbol
            // subsystem — the same per-frame pull SymbolPlacementSystem.Tick already does for SymbolSubsystem.IconTexture below.
            // RenderLayerSet.SetSprites early-outs on an unchanged pair, so the steady state is one reference
            // compare; the interesting frames are the one where the sheet lands and the one after a restyle.
            Layers.SetSprites(SymbolSubsystem.SpriteAtlas, SymbolSubsystem.IconTexture);

            // 3. Place the symbols against the SAME snapshot the tiles used (never a second BuildSceneFrame).
            //    A style with no symbol layers simply has nothing to place.
            if (SymbolSubsystem.HasSymbolLayers)
            {
                // ONE wall-clock read shared by ReconcileLoadedTiles' departing-tile grace window and CurrentBatch's
                // coverage-fade grace window (REVISION 2) — same frame, same clock, no double Time.timeAsDouble read.
                double now = Time.timeAsDouble;

                // A-1: PULL the current loaded-tile set (post-Tick, so cache-hit adds and releases are already
                // reflected) and reconcile the symbol store before collecting — restores kept-warm symbols for
                // cache-hit re-entries, releases tiles that left cover. Then aggregate the active symbols.
                using (PmSymbolCollect.Auto())
                {
                    TileManager.CollectLoadedTileKeys(_loadedTileKeys);
                    SymbolSubsystem.ReconcileLoadedTiles(_loadedTileKeys, now);
                    // Stall #1: start ≤MaxBuildsPerFrame queued symbol builds and coalesce the atlas upload.
                    // AFTER reconcile so its loaded-set snapshot drops builds for tiles that just left cover.
                    SymbolSubsystem.PumpBuilds();
                }

                // Stage-2 (symbol-label native gather): the per-frame WINNER PLAN — collect (+ cross-tile dedup) +
                // the pre-build tile-coverage cull, recording each winner's (blockId, localIndex) against the
                // per-tile baked block, rebuilt every frame (allocation-free). Hoisted out of the SymbolPlacementSystem.Tick
                // argument so its managed dedup/collect cost is MARKED (Symbol.BatchBuild), not folded into the
                // umbrella self-time. Pass this frame's SAME sceneFrame snapshot the tiles used, so the coverage
                // cull's projection matches the placement below exactly.
                SymbolGatherPlan plan;
                using (PmSymbolBatch.Auto())
                    plan = SymbolSubsystem.CurrentBatch(sceneFrame, _config.SymbolTileCoverageCull, now);
                // Then gather the winning blocks' baked slices → project/collide/build the placement, presenting
                // each slot through its own SymbolRenderLayer (D11/E2 — material + persistent presenter). Push the
                // per-symbol far-distance cull fraction live first (same read-every-Tick contract as the coverage
                // cull above), so an Inspector tweak takes effect the same frame.
                SymbolPlacementSystem.SymbolMaxDistanceFraction = _config.SymbolMaxDistanceFraction;
                SymbolPlacementSystem.Tick(sceneFrame, plan, SymbolSubsystem.Atlas, Time.deltaTime,
                    _symbolRenderLayers, SymbolSubsystem.IconTexture);
            }
        }

        /// <summary>
        /// Builds the per-frame <see cref="Backend.SceneFrame"/> (Level-2 of the two-level RTC) from the
        /// launch-time projection and the camera look-at: the render-space scene origin
        /// (<c>projection.Project(lookAt)</c>) and the render→look-at-local-ENU rebase
        /// (<c>transpose(projection.TangentBasisAt(lookAt))</c>). The look-at latitude is clamped to the
        /// projection's valid range (<see cref="IProjection.ClampValidLatitude"/>) — ±85.05° for Web-Mercator
        /// (matching <c>CenterMercator</c>), ±90° for the globe. For Web-Mercator the basis is the identity, so
        /// the frame equals <c>SceneFrame.Mercator(cam.CenterMercator())</c> bit-for-bit.
        ///
        /// <para>Folds in <see cref="MapCamera.CameraRelativePosition"/> — fresh because <c>LateUpdate</c> runs
        /// <see cref="MapCamera.SyncToCamera"/> before this call, never a <c>transform.position</c> round-trip.</para>
        /// </summary>
        /// <remarks><c>internal</c> (not <c>private</c>) solely so the single-owner test can call it directly
        /// after <see cref="MapCamera.SyncToCamera"/> without re-driving the whole <see cref="LateUpdate"/>
        /// pipeline — no other production caller.</remarks>
        internal Backend.SceneFrame BuildSceneFrame(in CameraProperties cam)
        {
            IProjection proj = Camera.Projection;
            var lookAt = new GeoCoordinate
            {
                Latitude  = proj.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };
            return new Backend.SceneFrame
            {
                SceneOriginRender      = proj.Project(lookAt),
                Rebase                 = math.transpose(proj.TangentBasisAt(lookAt)),
                CameraRelativePosition = Camera.CameraRelativePosition,
            };
        }

        /// <summary>The selector's rebuild-detection inputs, hand-rolled rather than a tuple — DO NOT
        /// "tidy" this back into one. UMR-125 measured a <c>System.ValueTuple</c> past 7 elements allocating
        /// on Unity's Mono every tick: the 8th+ field wraps in a nested <c>ValueTuple</c> (the compiler's
        /// <c>TRest</c>), and STORING or comparing that shape allocated. Internal (not private) only so
        /// <c>ProjectedAreaLodWiringTests.SelectorInputsEquals_DistinguishesEveryField</c> can reach it.</summary>
        internal readonly struct SelectorInputs : IEquatable<SelectorInputs>
        {
            public readonly bool        Globe;
            public readonly TileLodMode Lod;
            public readonly int         MinZoom;
            public readonly int         MaxZoom;
            public readonly int         OnScreenPx;
            public readonly double      MercFarCap;
            public readonly double      GlobeFarCap;
            public readonly double      AreaAggressiveness;

            public SelectorInputs(bool globe, TileLodMode lod, int minZoom, int maxZoom, int onScreenPx,
                                  double mercFarCap, double globeFarCap, double areaAggressiveness)
            {
                Globe = globe; Lod = lod; MinZoom = minZoom; MaxZoom = maxZoom; OnScreenPx = onScreenPx;
                MercFarCap = mercFarCap; GlobeFarCap = globeFarCap; AreaAggressiveness = areaAggressiveness;
            }

            /// <summary>Field-by-field only — no <see cref="EqualityComparer{T}"/>, no boxing, no
            /// <c>System.ValueTuple</c> machinery, so this stays allocation-free on the per-tick path.</summary>
            public bool Equals(SelectorInputs other)
                => Globe == other.Globe && Lod == other.Lod && MinZoom == other.MinZoom
                && MaxZoom == other.MaxZoom && OnScreenPx == other.OnScreenPx && MercFarCap == other.MercFarCap
                && GlobeFarCap == other.GlobeFarCap && AreaAggressiveness == other.AreaAggressiveness;

            public override bool Equals(object obj) => obj is SelectorInputs other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(Globe, Lod, MinZoom, MaxZoom, OnScreenPx, MercFarCap, GlobeFarCap,
                                    AreaAggressiveness);
        }

        // ── S71: visible-tile selector, rebuilt only when a selection input (or the projection) changes ──
        private bool           _hasSelectorInputs;
        private SelectorInputs _selectorInputs;

        private void EnsureSelector()
        {
            var  tileSelection = _config.TileSelection;
            bool globe         = Camera.Projection is SphericalProjection;
            var key = new SelectorInputs(
                globe: globe, lod: tileSelection.LodMode, minZoom: tileSelection.MinZoom,
                maxZoom: tileSelection.MaxZoom, onScreenPx: tileSelection.OnScreenTilePx,
                mercFarCap: tileSelection.MercatorFarPlaneCap, globeFarCap: tileSelection.GlobeFarPlaneCap,
                areaAggressiveness: tileSelection.ProjectedAreaAggressiveness);
            if (TileManager.Selector != null && _hasSelectorInputs && key.Equals(_selectorInputs)) return;
            _selectorInputs    = key;
            _hasSelectorInputs = true;

            // One universal FrustumTileSelector for every projection (occlusion via IProjection). LOD strategy
            // from config; far-plane policy per projection — ray-sphere for a self-occluding globe
            // (curvature-correct: tight near, limb far), geometry-aware for the flat atlas. The camera gets the
            // SAME far so the rendered frustum is byte-for-byte the selected one.
            ITileLodStrategy lod = tileSelection.LodMode switch
            {
                TileLodMode.ScreenSpaceLod => new ScreenSpaceLodStrategy(),
                TileLodMode.ProjectedArea  => new ProjectedAreaLodStrategy(tileSelection.ProjectedAreaAggressiveness),
                _                          => new FlatLodStrategy(),
            };
            IFarPlanePolicy far = Camera.Projection.TryGetHorizonOccluder(out _, out double occRadius)
                ? new RaySphereFarPlane(occRadius, tileSelection.GlobeFarPlaneCap)
                : new GeometryAwareFarPlane(tileSelection.MercatorFarPlaneCap);

            Camera.FarPlanePolicy = far;
            TileManager.Selector = new FrustumTileSelector(
                tileSelection.MinZoom, tileSelection.MaxZoom, tileSelection.OnScreenTilePx, lod, far);
        }

        /// <summary>
        /// The per-frame view inputs the selector consumes. The camera IS the viewport, and the framing
        /// viewport is literally <see cref="MapCamera.ViewportLogicalPx"/> — the SAME definition the camera
        /// altitude frames from, not a second derivation of it. That is what makes "render far == selection
        /// far" (S86/S92) structural: an on-screen tile stays the same physical size across panel densities
        /// because one quantity, not two, decides it. The projection is the pixel↔ground service the camera
        /// owns (Web-Mercator today).
        /// </summary>
        internal Tile.TileManager.TileSelectionConfig BuildTileSelectionConfig()
            => new Tile.TileManager.TileSelectionConfig
            {
                FramingViewportPx    = Camera.ViewportLogicalPx,
                Projection           = Camera.Projection,
                MaxConsumesPerTick   = _config.MaxConsumesPerTick,
                MaxMeshBuildsPerTick = _config.MaxMeshBuildsPerTick,
                MaxVerticesPerTick   = _config.MaxVerticesPerTick,
                MaxReleasesPerTick   = _config.MaxReleasesPerTick,
                MaxConcurrentTileLoads = _config.MaxConcurrentTileLoads,
                PriorityStrategy       = _config.PriorityStrategy,
                // Negative means "do not run the clip stage at all", which is not the same as a zero margin
                // ("cut at the tile boundary"). The decode lives in the value type so the parity oracles can
                // reach it — their reference arm must build under the SAME window as the arm it is compared
                // against, or it stops being a comparison.
                BufferClip           = TileBufferClip.FromInspectorUnits(_config.FillTileBufferClip),
            };

        /// <summary>
        /// Releases all tile resources (via the <see cref="Tile.TileManager"/>), then disposes the
        /// RenderLayerSet's materials, then the symbol placement system's mesh/material/native buffers.
        /// Order matters TWICE: tiles first — their renderers reference layer materials — and (E2)
        /// <see cref="Layers"/> before <see cref="SymbolPlacementSystem"/> — a <see cref="Style.SymbolRenderLayer"/>'s
        /// presenter (destroyed by <c>Layers.Dispose()</c>) references a slot <see cref="Mesh"/> owned by
        /// <see cref="SymbolPlacementSystem"/>; a MeshRenderer must not outlive the mesh it points at. The glyph atlas
        /// texture is owned by <see cref="SymbolSubsystem"/> and disposed there, never here.
        /// Idempotent (every dispose here is). The MonoBehaviour host calls this from OnDestroy.
        /// </summary>
        public void Teardown()
        {
            // Defense-in-depth: dispose each subsystem independently so a fault in ONE cannot strand the
            // others. Learned the hard way — on Play-mode Stop Unity tears down the Entities World before this
            // runs, and an unguarded entity touch inside TileManager.Dispose() threw straight out of here,
            // leaving Layers/SymbolPlacementSystem/SymbolSubsystem (and everything TileManager disposes after the throw) undisposed:
            // the whole-graph "finalized without Dispose()" flood. The exception is LOGGED, never swallowed
            // silently, so a genuine teardown bug is still loud. Order is preserved (see the summary): tiles
            // before Layers (renderers reference layer materials), Layers before SymbolPlacementSystem (E2 presenter/slot-mesh).
            DisposeStep(TileManager, nameof(TileManager)); // tiles first — their renderers reference Layers' materials
            DisposeStep(Layers,      nameof(Layers));
            DisposeStep(SymbolPlacementSystem,      nameof(SymbolPlacementSystem));
            DisposeStep(SymbolSubsystem,     nameof(SymbolSubsystem));     // S105: destroy the shared glyph atlas texture + manager

            static void DisposeStep(System.IDisposable subsystem, string name)
            {
                try { subsystem?.Dispose(); }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError(
                        $"[MapView.Teardown] {name}.Dispose() threw — continuing so the remaining subsystems " +
                        $"still release (a partial teardown beats a stranded graph). {ex}");
                }
            }
        }
    }
}
