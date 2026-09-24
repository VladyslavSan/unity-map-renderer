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
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;
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
        /// <summary>Default: each tile-layer draw item is an <see cref="Backend.Entities.TileRenderer"/>
        /// entity rendered via Entities Graphics (BatchRendererGroup under the hood), grouped per tile and
        /// inspectable/disable-able in the Entities Hierarchy. Value 0 so a scene that serialized the
        /// field as 0 deserializes to Entities.</summary>
        Entities = 0,

        /// <summary>BRG path: draw tile meshes via a hand-packed <see cref="Backend.BRG.TileRenderer"/>
        /// BatchRendererGroup. The zero-allocation production path.</summary>
        Brg = 1,

        /// <summary>The original per-tile-layer GameObject path (<see cref="Backend.GameObjects.TileRenderer"/>):
        /// one MeshFilter+MeshRenderer child per layer under a <c>"Tile z/x/y"</c> container, drawn by the
        /// SRP Batcher. The simplest, most Inspector-debuggable backend, an explicit opt-in.</summary>
        GameObject = 2,
    }

    /// <summary>
    /// The live multi-tile render loop, a plain C# class hosted by <see cref="MapViewComponent"/>. It is built
    /// with its <see cref="MapViewConfig"/> and a non-null <see cref="MapCamera"/> and owns its
    /// <see cref="RenderLayerSet"/> and <see cref="TileManager"/> from construction, so it has no "wired yet"
    /// guards. Before <see cref="SetStyle(string,CancellationToken)"/> it is an empty map with no sources, so
    /// <see cref="LateUpdate"/> renders nothing. Steady-state frames do not allocate.
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

        // The symbol reconcile that runs before the symbol aggregation (pull/reconcile + PumpBuilds).
        private static readonly ProfilerMarker PmSymbolCollect =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolCollect);

        // The symbol aggregation (cross-tile dedup + batch build), a managed main-thread cost that grows with the
        // on-screen symbol count. Its own marker keeps it out of the unmarked LateUpdate self-time.
        private static readonly ProfilerMarker PmSymbolBatch =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolBatch);

        // ── Injected collaborators (set by the constructor — never null) ─────────────────────────
        private readonly MapViewConfig _config;

        /// <summary>The map camera, owned by this view — the single source of camera state. Non-null, set
        /// once at construction: there is no re-injection (a new camera means a new MapView), so no setter.</summary>
        public MapCamera Camera { get; }

        private StyleDocument _style;

        /// <summary>The document the live layers were last SUCCESSFULLY built or restyled from —
        /// the in-place gate's <c>previous</c>. Distinct from <see cref="_style"/>, which commits early and
        /// stays advanced after an aborted rebuild; this field is null from the gate until either arm's end,
        /// so an abort in between forces the next call down the full-rebuild arm.</summary>
        private StyleDocument _committedStyle;

        // The FillAntialiasing the TileManager.CurrentStyle token was folded from. It changes vertices, so a live
        // toggle between two SetStyle calls must fail the in-place gate rather than leave the token stale.
        private bool _committedFillAntialiasing;

        // The base material references the last full Layers.Build ran against. Non-obvious why: an in-place
        // restyle only re-binds existing appliers, so it cannot find a layer that a mutated MapMaterialSet
        // field now builds or skips; the gate refuses on any reference change (Unity's Equals), and
        // PreparedCacheTests' FillExtrusionMaterial*InPlace_ tests pin it.
        private (Material fill, Material line, Material fillExtrusion, Material symbolText, Material symbolIcon)
            _committedMaterials;

        private static (Material, Material, Material, Material, Material) MaterialSnapshot(Materials.MapMaterialSet set)
            => (set.FillMaterial, set.LineMaterial, set.FillExtrusionMaterial, set.SymbolTextWorld, set.SymbolIconWorld);

        /// <summary>Test seam for the style-transition clock. Production → <c>Time.unscaledTimeAsDouble</c>
        /// (UNSCALED: a theme change is a UI-class animation that must ease under <c>timeScale == 0</c>).
        /// <see cref="Text.SymbolPlacementSystem.EaseFade"/> runs on SCALED time, so the two clocks disagree
        /// under <c>timeScale != 1</c> — a known limitation.</summary>
        internal Func<double> NowSecondsOverride { get; set; }
        // A ternary, not `(NowSecondsOverride ?? DefaultNowSeconds)()`: that converts a method group to a delegate
        // per call, which allocates inside MapView_SteadyStateTick_DoesNotAllocateGCMemory's scope.
        private double NowSeconds => NowSecondsOverride != null ? NowSecondsOverride() : Time.unscaledTimeAsDouble;

        /// <summary>How long a restyled uniform binding eases from its old value to its new one. The ONLY
        /// source of the duration/delay: no style key is read.</summary>
        public Style.StyleTransition StyleTransition { get; set; } = Style.StyleTransition.Default;

        // Per-style-layer render bundles (fills + lines), built at SetStyle. Owns the materials.
        /// <summary>The per-style-layer render bundles owned by this view. <c>internal</c>: tests read counts
        /// via <c>MapViewTestExtensions</c> (InternalsVisibleTo).</summary>
        internal Style.RenderLayerSet Layers { get; } = new Style.RenderLayerSet();

        // The tile lifecycle — owned by MapView, ticked once per frame. Built in the ctor (needs only Layers).
        internal readonly Tile.TileManager TileManager;

        // ── The per-frame symbol placement path — a SEPARATE path from the tile lifecycle above, never a
        // static per-(tile,layer) mesh. Needs no ctor dependency (unlike TileManager).
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
            // The PreparedTileCache's Enabled toggle + byte/count budget — maintainer-tunable Inspector
            // fields (placeholder budget defaults pending in-editor VRAM profiling).
            TileManager = new Tile.TileManager(Layers, _config.PreparedCache);
            // Built here, after Camera is set: a field initializer would see a null Camera. Both base materials
            // are optional; a null one leaves that draw path inert (see MapMaterialSet.SymbolIconWorld).
            SymbolPlacementSystem = new SymbolPlacementSystem(Camera,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolTextWorld : null,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolIconWorld : null);
            // Non-local invariant: symbol data arrives through TileManager's per-tile worker factory, but the
            // tile lifecycle is pulled — LateUpdate hands over the loaded set and the subsystem reconciles.
            // The cache flag drives keep-warm-on-release, so symbols match the prepared mesh cache.
            SymbolSubsystem = new SymbolSubsystem(Camera,
                _config.PreparedCache.MaxCount, _config.PreparedCache.Enabled);
            TileManager.SymbolWorkerFactory = SymbolSubsystem;
        }

        // Production symbols (real map data), fed to SymbolPlacementSystem.Tick each frame.
        internal readonly SymbolSubsystem SymbolSubsystem;

        // Reused scratch for SetStyle's symbol-layer derivation (below) — a restyle never allocates a
        // fresh list; the single registry (RenderLayerFactory) is walked once via Layers.Layers.
        private readonly List<Symbol.StyleLayer> _symbolStyleLayers = new List<Symbol.StyleLayer>();

        // The SymbolRenderLayer objects themselves (same walk as _symbolStyleLayers, same order) —
        // handed to SymbolPlacementSystem.Tick each frame so each layer's survivors draw with its own material/presenter.
        private readonly List<Style.SymbolRenderLayer> _symbolRenderLayers = new List<Style.SymbolRenderLayer>();

        // Reused scratch for the per-frame loaded-tile pull handed to the subsystem's reconcile (no alloc).
        private readonly List<Tile.LoadedTileKey> _loadedTileKeys = new List<Tile.LoadedTileKey>();

        // ── SetStyle — the style is the single source of truth ─────────────────────────────────
        // No separate "Initialise": the map is valid at construction, and SetStyle loads or changes the data.


        /// <summary>The id of the active style.
        /// For <see cref="SetStyle(string,CancellationToken)"/> it is the style URI; for the
        /// <see cref="StyleDocument"/> overload it is the caller-supplied id.</summary>
        internal string StyleId { get; private set; }

        // Loader seams — production defaults; tests inject counting/offline fakes via InternalsVisibleTo.
        internal System.Func<string, CancellationToken, UniTask<string>> DocumentLoaderOverride;
        internal System.Func<string, IDataSource>                        TileSourceFactoryOverride;

        /// <summary>The six mutation sites of <see cref="SetStyle(StyleDocument,string,CancellationToken)"/>'s
        /// full-rebuild arm after which an exception would leave persistent state a later call or frame
        /// reads (the predicate `docs/tile-pipeline-design.md` enumerates). Named for the site,
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

        /// <summary>Test seam: null in production (a per-call delegate check, not a per-frame one —
        /// this method is not a hot path). Set by a test to throw at a chosen <see cref="CommitPhase"/> and
        /// observe what the full-rebuild arm leaves behind. Reached via the existing
        /// <c>InternalsVisibleTo("MapRenderer.Tests.EditMode")</c> (<c>MapRenderer.Unity/AssemblyInfo.cs</c>).</summary>
        internal Action<CommitPhase> CommitProbe;

        /// <summary>
        /// Load a style from <paramref name="styleUri"/> (file:// or http(s)://), resolve each of its
        /// sources (inline <c>tiles[]</c>, else TileJSON), wire one data pipeline per source-id, and
        /// build the render layers — each fetching from ITS OWN source. <c>styleId == styleUri</c>.
        /// </summary>
        public async UniTask SetStyle(string styleUri, CancellationToken ct = default)
        {
            var           loader = DocumentLoaderOverride ?? StyleDocumentLoader.LoadTextAsync;
            string        json   = await loader(styleUri, ct);
            StyleDocument style;
            try
            {
                // Expressions parse eagerly, so a malformed one throws here, before the other overload commits
                // anything. Returning without calling it leaves the previous style fully live.
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
        /// Applies an already-parsed <paramref name="style"/> with a caller-supplied
        /// <paramref name="styleId"/>. A second call RESTYLES: <see cref="RenderLayerSet"/> rebuilds and the
        /// source registry diffs (unchanged sources keep their warm pipeline; removed are torn down).
        /// </summary>
        public async UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
        {
            // Transactional restyle: the one await runs before any mutation, so a delayed or cancelled restyle
            // leaves the old style's layers, materials, backend and identity live and rendering.
            var specs = await BuildSourceSpecs(style, ct);
            ct.ThrowIfCancellationRequested(); // last safe abort — nothing mutated yet (old style stays intact)

            // TOCTOU: _config.MaterialSet is live-mutable, so capture it once and validate that same reference;
            // no await separates validation from Layers.Build. Across style loads the token's numbering fold guards.
            var materialSet = _config.MaterialSet;
            materialSet.Validate();

            // Fail loud HERE (before any commit) — a null Root at Digest's site (after Build) would
            // leave the new style/id/layers installed under the OLD token. Digest's own check must never fire.
            if (style.Root == null)
                throw new InvalidOperationException("StyleDocument.Root is null — the prepared cache's " +
                    "cache-key digest needs it (a hand-built StyleDocument must set Root).");

            // Nulled for this call: an abort below leaves it null, so the next call takes the full rebuild arm.
            // See docs/tile-pipeline-design.md § "Partial-survival restyle".
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
                // Unconditional here, including a pure reorder — docs/tile-pipeline-design.md,
                // "Partial-survival restyle".
                TileManager.RestyleSourcesInPlace(specs, _config.Backend);

                // The in-place arm skips Layers.Build, the style token, SymbolSubsystem.SetStyle and the symbol
                // lists; see the same section.
                Layers.ApplyZoom(new Style.StyleFrameInputs(
                    Camera.CurrentProperties.Zoom, _config.DevicePixelRatio, now, transition));
                TileManager.PushLayerDrawGates(); // the restyle may have moved a layer's zoom range
                _committedStyle = style; // the in-place patch completed — the gate may trust it again
                return;
            }

            _committedFillAntialiasing = _config.FillAntialiasing;
            _committedMaterials        = MaterialSnapshot(materialSet);
            CommitProbe?.Invoke(CommitPhase.MaterialMemoWritten);

            Layers.Build(_style, Camera.CurrentProperties.Zoom, materialSet);
            CommitProbe?.Invoke(CommitPhase.LayersBuilt);
            // Token = styleId + Root + built numbering + FillAntialiasing (bakes into vertices,
            // not a uniform — a toggle changes neither Root's bytes nor the numbering) — set AFTER Build.
            TileManager.CurrentStyle = new Tile.StyleToken(JsonCanonical.CacheKey(
                StyleId, _style.Root, LayerNumbering(Layers) + "|aa=" + _config.FillAntialiasing));
            CommitProbe?.Invoke(CommitPhase.StyleTokenWritten);
            LogSkippedLayers(Layers.SkippedLayers); // once per style load, never per tile/frame
            // Non-obvious why: Build seeds px uniforms at ratio 1, and this async continuation can resume after this
            // frame's LateUpdate, so restyled layers would draw loaded tiles once at the wrong device-pixel ratio.
            Layers.ApplyZoom(new Style.StyleFrameInputs(Camera.CurrentProperties.Zoom, _config.DevicePixelRatio, now, transition));
            TileManager.PushLayerDrawGates(); // fade advanced above; the gate must not lag it by a frame
            // Derive the symbol layers from the just-built set in one walk, filling two lists in the same order
            // so the subsystem's layer ordinal maps 1:1 to the SymbolRenderLayer that draws it.
            _symbolStyleLayers.Clear();
            _symbolRenderLayers.Clear();
            foreach (var layer in Layers.Layers)
                if (layer is Style.SymbolRenderLayer s)
                {
                    _symbolStyleLayers.Add(s.SymbolLayer);
                    _symbolRenderLayers.Add(s);
                }

            SymbolSubsystem.SetStyle(_style,
                _symbolStyleLayers); // group symbol layers + (re)build the shared glyph pipeline
            CommitProbe?.Invoke(CommitPhase.SymbolStyleApplied);

            TileManager.SetSources(specs, _config.Backend, RecordProbeOrNull());
            _committedStyle = style; // the rebuild completed — the gate may trust it again
        }

        /// <summary>The <see cref="CommitPhase.SourcesTeardownRecord"/> half of <see cref="CommitProbe"/>
        /// — its ONE phase whose site lives inside <see cref="Tile.TileManager.SetSources"/>, not here, so it
        /// has to cross the call as a delegate rather than an inline invoke. Null when no probe is installed
        /// (production; also a per-call, not per-frame, allocation when one is).</summary>
        private Action RecordProbeOrNull()
            => CommitProbe != null ? () => CommitProbe(CommitPhase.SourcesTeardownRecord) : null;

        /// <summary>Warns once, naming every layer <see cref="Style.RenderLayerSet.Build"/> skipped
        /// for a compatibility reason (unsupported kind / unconfigured material). By-design skips stay silent:
        /// <see cref="Style.LayerSkipReason.GenuinelyUnpainted"/>, <see cref="Style.LayerSkipReason.Hidden"/>
        /// and <see cref="Style.LayerSkipReason.FullyTransparent"/>. Internal so a test can exercise the
        /// suppression directly.</summary>
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

        /// <summary>A plain-text encoding of the dense (index, id) pairs the layer set just built —
        /// folded into the cache token so a numbering shift (a skipped/added layer, from EITHER the style or a
        /// slot-dropping <c>MapMaterialSet</c> field) changes the token even under unchanged style content.
        /// <c>RenderLayerCompatibilitySummaryTests</c> pins that it folds the (index, id) PAIRS, not just
        /// <see cref="Style.RenderLayerSet.Count"/> — why per-index not count is in <c>docs/tile-pipeline-design.md</c>.</summary>
        internal static string LayerNumbering(Style.RenderLayerSet layers)
        {
            var sb = new StringBuilder();
            for (int li = 0; li < layers.Count; li++)
                sb.Append(li).Append(':').Append(layers[li].StyleLayer?.Id).Append('|');
            return sb.ToString();
        }

        /// <summary>
        /// Resolves each rendered source-id of <paramref name="style"/> into a
        /// <see cref="Tile.TileManager.SourceSpec"/>. Inline <c>tiles[]</c> short-circuits (no TileJSON
        /// fetch); a <c>url</c>-only source fetches its TileJSON once. A failed TileJSON skips that source only.
        /// It runs before <see cref="Style.RenderLayerSet.Build"/>, so it walks the raw style layers through
        /// <see cref="Style.RenderLayerFactory.TryGetFetchSource"/>, the one registry of fetching layers.
        /// </summary>
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

                // A geojson source is sliced locally, with no TileJSON, tiles[] or byte fetcher, so it branches
                // before the TileJSON fetch and the no-tiles skip. A bad source is skipped, never a thrown SetStyle.
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
                        // System.Exception, not only GeoJsonFormatException: any other parser throw would fault
                        // SetStyle for the whole style over one bad source. Cancellation is rethrown above.
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
                // The ONE production site that wraps the byte fetcher into the raised ITileFeatureSource
                // seam — TileManager never names the byte-level type.
                specs.Add(new Tile.TileManager.SourceSpec(
                    sid, key, def.MinZoom, def.MaxZoom,
                    () => new Tile.Processing.MvtTileFeatureSource(factory(template), scheduler)));
            }

            return specs;
        }


        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the live loop, driven from <c>MapViewComponent.LateUpdate</c>, which Unity runs after every
        /// <c>Update</c>, so it sees this frame's input. One ordered pass off one camera snapshot keeps tiles and
        /// symbols frame-coherent: commit the camera, move the tiles, place the symbols. Each telemetry provider
        /// publishes at the end of its own pass. Allocation-free in steady state.
        /// </summary>
        public void LateUpdate()
        {
            using var _lateUpdate = PmLateUpdate.Auto(); // umbrella: self-time = residual unmarked per-frame cost

            // 1. Commit the camera first, with the live DPI: the altitude framing, the selector's framing viewport,
            //    the tile rebase, symbol projection and BuildSceneFrame all read the committed pose.
            Camera.DevicePixelRatio = _config.DevicePixelRatio;
            using (PmCameraAdvance.Auto())
                Camera.SyncToCamera();

            // ONE snapshot for the rest of the frame — tiles and symbols share it, so they can't diverge.
            CameraProperties   cameraProperties = Camera.CurrentProperties;
            Backend.SceneFrame sceneFrame;
            using (PmSceneFrame.Auto())
                sceneFrame = BuildSceneFrame(cameraProperties);

            // 2. Move the tiles. ApplyZoom first, so a fractional-zoom-only change still pushes uniforms. The style's
            //    logical px meet the device-pixel ratio only here, read from _config, which owns the ratio.
            using (PmApplyZoom.Auto())
            {
                // StyleTransition is re-read every frame, so a live change takes effect without a restyle.
                Layers.ApplyZoom(new Style.StyleFrameInputs(
                    cameraProperties.Zoom, _config.DevicePixelRatio, NowSeconds, StyleTransition));
                TileManager.PushLayerDrawGates();
            }

            // Camera-relative rendering: every frame, place all loaded tiles relative to the look-at origin.
            // Mercator's rebase is the identity; the globe's rotates each tile into the look-at's local ENU frame.
            using (PmInstancedRebuild.Auto())
                TileManager.InstancedRebuild(sceneFrame);

            EnsureSelector();
            using (PmManagerTick.Auto())
                TileManager.Tick(cameraProperties, BuildTileSelectionConfig());

            // Pull the sprite sheet, which the symbol subsystem owns and fetches, into the fill-pattern layers each
            // frame. SetSprites early-outs on an unchanged pair.
            Layers.SetSprites(SymbolSubsystem.SpriteAtlas, SymbolSubsystem.IconTexture);

            // 3. Place the symbols against the SAME snapshot the tiles used (never a second BuildSceneFrame).
            //    A style with no symbol layers simply has nothing to place.
            if (SymbolSubsystem.HasSymbolLayers)
            {
                // One clock read shared by ReconcileLoadedTiles' and CurrentBatch's grace windows.
                double now = Time.timeAsDouble;

                // Pull the post-Tick loaded-tile set and reconcile the symbol store: it restores kept-warm symbols
                // for cache-hit re-entries and releases tiles that left cover.
                using (PmSymbolCollect.Auto())
                {
                    TileManager.CollectLoadedTileKeys(_loadedTileKeys);
                    SymbolSubsystem.ReconcileLoadedTiles(_loadedTileKeys, now);
                    // Start ≤MaxBuildsPerFrame queued symbol builds and coalesce the atlas upload.
                    // AFTER reconcile so its loaded-set snapshot drops builds for tiles that just left cover.
                    SymbolSubsystem.PumpBuilds();
                }

                // The per-frame winner plan: collect, cross-tile dedup and coverage cull, recording each winner's
                // baked-block slot. It takes the tiles' sceneFrame, so the cull projects as the placement does.
                SymbolGatherPlan plan;
                using (PmSymbolBatch.Auto())
                    plan = SymbolSubsystem.CurrentBatch(sceneFrame, _config.SymbolTileCoverageCull, now);
                // Push the far-distance cull fraction live, then gather, project, collide and present each slot
                // through its own SymbolRenderLayer.
                SymbolPlacementSystem.SymbolMaxDistanceFraction = _config.SymbolMaxDistanceFraction;
                SymbolPlacementSystem.Tick(sceneFrame, plan, SymbolSubsystem.Atlas, Time.deltaTime,
                    _symbolRenderLayers, SymbolSubsystem.IconTexture);
            }
        }

        /// <summary>
        /// Builds the per-frame <see cref="Backend.SceneFrame"/> (Level-2 of the two-level RTC) from the
        /// projection and the clamped camera look-at. For Web-Mercator the rebase is the identity, so the frame
        /// equals <c>SceneFrame.Mercator(cam.CenterMercator())</c> bit-for-bit.
        /// Non-local invariant: <see cref="MapCamera.CameraRelativePosition"/> is fresh only because
        /// <c>LateUpdate</c> runs <see cref="MapCamera.SyncToCamera"/> before this call.
        /// </summary>
        /// <remarks><c>internal</c> so a test can call it after <see cref="MapCamera.SyncToCamera"/> without
        /// driving the whole <see cref="LateUpdate"/>.</remarks>
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
        /// "tidy" this back into one. A <c>System.ValueTuple</c> past 7 elements was measured allocating
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

        // ── Visible-tile selector, rebuilt only when a selection input (or the projection) changes ──────
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

            // One FrustumTileSelector for every projection; the far-plane policy is ray-sphere for the globe and
            // geometry-aware for the flat map. The camera gets the same far, so it renders the selected frustum.
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
        /// The per-frame view inputs the selector consumes. The framing viewport is
        /// <see cref="MapCamera.ViewportLogicalPx"/>, the same quantity the camera altitude frames from, so an
        /// on-screen tile keeps its physical size across panel densities.
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
                // Negative skips the clip stage; zero cuts at the tile boundary. The decode lives in the value
                // type so the parity oracles build their reference arm under the same window.
                BufferClip           = TileBufferClip.FromInspectorUnits(_config.FillTileBufferClip),
            };

        /// <summary>
        /// Disposes the tiles, then the layer materials, then the symbol placement system, then the symbol
        /// subsystem; idempotent. Non-local invariant: tiles go first because their renderers reference layer
        /// materials, and <see cref="Layers"/> goes before <see cref="SymbolPlacementSystem"/> because a symbol
        /// layer's presenter must not outlive the slot <see cref="Mesh"/> that system owns.
        /// </summary>
        public void Teardown()
        {
            // Each step is isolated and logs its exception, so one fault cannot strand the rest. Play-mode Stop
            // can destroy the Entities World before this runs, so an entity touch in TileManager.Dispose() may throw.
            DisposeStep(TileManager, nameof(TileManager)); // tiles first — their renderers reference Layers' materials
            DisposeStep(Layers,      nameof(Layers));
            DisposeStep(SymbolPlacementSystem,      nameof(SymbolPlacementSystem));
            DisposeStep(SymbolSubsystem,     nameof(SymbolSubsystem));     // destroy the shared glyph atlas texture + manager

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
