// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text —
// it uses Unity.Mathematics types (CurrentBatch's viewProj/viewport), but MapRenderer.Unity.Text does not
// collide with any bare UnityEngine type, so TOP-LEVEL `using Unity.Mathematics;` + unqualified types is safe.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Common;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S105 — the DECOUPLED production symbol-symbol subsystem: it owns the shared production
    /// <see cref="GlyphManager"/> + fixed-size <see cref="GlyphAtlasTexture"/> +
    /// <see cref="StyledSymbolTileBuilder"/>. A-1 split of concerns: symbol DATA arrives via the
    /// <see cref="Tile.TileManager"/>'s per-tile KICK (A5b: this class implements
    /// <see cref="ISymbolTileWorkerFactory"/> — TileManager's kick drives
    /// <see cref="TryBeginBuild"/> alongside the mesh pass, sharing the A4 shared-decode entry, no separate
    /// push/queue), while the tile LIFECYCLE is PULLED — each frame
    /// <see cref="ReconcileLoadedTiles"/> takes TileManager's current loaded set and reconciles which symbols are
    /// active/kept-warm. Symbols are the placed-every-frame
    /// class, so this feeds <see cref="SymbolPlacementSystem"/> via <see cref="CollectInto"/>, never the static
    /// tile-render backend (S20 T5).
    ///
    /// <para><b>Fixed atlas.</b> The glyph atlas is allocated big and FIXED (<see cref="AtlasDimension"/>,
    /// clamped to the GPU max) so its <c>Size</c> never changes as tiles append glyphs — a growing atlas
    /// would invalidate earlier tiles' baked UVs (glyph-atlas-uv-growth-staleness lesson). Overflow (a
    /// glyph set larger than the fixed atlas) degrades gracefully and is logged, never silent.</para>
    ///
    /// <para><b>Worker phase / tail split.</b> The worker phase (<see cref="TryBeginBuild"/>'s returned
    /// <c>SymbolTileWorkerPass</c>)
    /// stops after the pool-side extract and hands a <see cref="ReadySymbolTail"/> to <see cref="PumpBuilds"/>'
    /// budgeted tail-start loop (<see cref="RunTailAsync"/>), which does the glyph-fetching per-layer
    /// shape + store commit.</para>
    ///
    /// <para><b>A5b: budget split.</b> Worker-phase starts now ride TileManager's kick cadence
    /// (<c>MaxMeshBuildsPerTick</c>) — <see cref="MaxBuildsPerFrame"/> gates ONLY tail starts, its real
    /// stall-#1 role, since shaping (not extraction) is the main-thread burst. Under burst, tail starts
    /// trickle at that budget — an accepted, bounded symbol-appearance-latency tradeoff, never a
    /// symbol-content change.</para>
    /// </summary>
    internal sealed class SymbolSubsystem : VerifiedDisposable, ISymbolTileWorkerFactory
    {
        /// <summary>Target atlas edge in px, clamped to the GPU's max texture size. R8, so 4096² ≈ 16 MB.</summary>
        private const int AtlasDimension = 4096;

        private readonly MapCamera _camera;

        private GlyphManager _glyphManager;
        private GlyphAtlasTexture _atlasTexture;
        private StyledSymbolTileBuilder _builder;

        // I5b: the sprite sheet backing every icon symbol's UVs — a single pre-baked image per style (unlike
        // the glyph atlas's grow-and-append), fetched once in SetStyle and disposed on the next restyle/
        // teardown. Null until the fetch resolves (or forever, on a style with no `sprite` URL / no symbol
        // layers) — every icon draw path downstream (SymbolPlacementSystem.Tick's spriteTexture param) is
        // guarded on IconTexture being non-null, so a still-loading or absent sheet is inert, not a fault.
        private SpriteSheet _spriteSheet;
        private SpriteAtlasView _spriteAtlas;
        // D6 (road-shields, docs/road-shields-design.md §3 D6): the sprite fetch this style kicked off
        // (SetStyle), `.Preserve()`d so its Status can be polled across frames — mirrors this class's own
        // `_reconcileTask.Status == UniTaskStatus.Pending` polling. Readiness is "reached a TERMINAL state
        // OR gave up waiting" (see SpriteFetchDeadlineSeconds below), NOT "the atlas is non-null": a style
        // with no `sprite` URL, a 404/204, a fault, or a cancellation are all terminal too —
        // `default(UniTask).Status` is `Succeeded`, so a subsystem with no style set (or a style whose fetch
        // never awaits — see FetchSpriteSheetAsync's no-source fast path) never parks.
        private UniTask _spriteFetchTask;
        // D6 review follow-up (REQUIRED): `UnityWebRequestSpriteSource` sets no HTTP timeout, so a genuinely
        // hung endpoint (TCP connects, never responds — NOT the 404/204 path, which already reaches a
        // terminal Status) leaves `_spriteFetchTask` Pending forever. Without a bound, parking becomes
        // PERMANENT: zero symbol symbols of any kind ever commit for the style (strictly worse than the
        // pre-D6 degradation, which at least showed bare text), and `_pendingSpriteQueue` grows without
        // bound as the user pans — every parked build now holds a REFERENCE to a decoded tile, so an
        // unbounded queue pins unbounded Allocator.Persistent memory, not just managed state.
        // `SpriteFetchDeadlineSeconds` bounds
        // this: once elapsed, SpritesSettled goes true regardless of the fetch's own status, so PumpBuilds'
        // existing pending-drain (which already tolerates a null `_spriteAtlas` — the T11 absent-sheet path)
        // dispatches every parked build with whatever atlas state exists. This restores the PRE-D6 floor
        // (possibly icon-starved, never self-healing) as a bounded FALLBACK, not a permanent stall — never
        // relaxes "settled ≠ non-null" for the normal terminal paths, which resolve long before this bound.
        internal const double SpriteFetchDeadlineSeconds = 8.0;
        // Wall-clock timestamp (NowSeconds) the in-flight sprite fetch started at — read only by
        // SpritesSettled's deadline check.
        private double _spriteFetchStartedAtSeconds;
        // Test seam (mirrors GlyphSourceFactoryOverride/SpriteSourceFactoryOverride, below): the wall-clock
        // source SpritesSettled's deadline reads. Null (production) → UnityEngine.Time.realtimeSinceStartup —
        // deliberately UNSCALED real time, not MapView's simulated `Time.timeAsDouble` (used for
        // ReconcileLoadedTiles/CurrentBatch's `now`): an HTTP fetch keeps running in real seconds regardless
        // of Unity's timeScale, so a paused/slow-motion scene must not make the network deadline take longer
        // (or shorter) than it actually does. A test overrides this to a controllable clock so the deadline
        // can be crossed deterministically, with no real wait and no dependency on `[UnityTest]` frame-pump
        // cadence.
        internal Func<double> NowSecondsOverride { get; set; }
        private double NowSeconds => (NowSecondsOverride ?? DefaultNowSeconds)();
        private static double DefaultNowSeconds() => Time.realtimeSinceStartup;

        private bool SpritesSettled =>
            _spriteFetchTask.Status != UniTaskStatus.Pending ||
            NowSeconds - _spriteFetchStartedAtSeconds >= SpriteFetchDeadlineSeconds;

        // Flat symbol-layer list (index == ShapedSymbol.MaterialIndex) and a source id → its layers'
        // GLOBAL indices map (only sources with symbol layers are observed). D11/E2: per-layer MATERIALS
        // moved to SymbolRenderLayer (RenderLayerSet.Build owns cloning + halo bind) — this class only
        // needs the layer COUNT (CurrentBatch's slot count) and the layers' Source/id for build routing.
        private readonly List<SymbolStyle.StyleLayer> _allSymbolLayers = new();
        private Dictionary<string, List<int>> _layersBySource;

        // The material-slot clamp count shared by the per-frame Build (CurrentBatch) and the build-time bake
        // (RunTailAsync) — ONE normalization so the oracle and the bake pass ClampSlot the same bound and can't
        // diverge (Phase 1 Stage 1 drift-guard). Zero symbol layers → 1 (a lone default slot); a build is only
        // ever kicked when a source HAS layers, so 0 is unreachable here today, but the two paths must still agree.
        private int SlotCount => _allSymbolLayers.Count > 0 ? _allSymbolLayers.Count : 1;

        // Per-tile build markers (Profiler window → "MapRenderer.Symbol"). Only the SYNCHRONOUS main-thread
        // stages are marked — the shaping BuildAsync is awaited (its wall-clock includes glyph-fetch
        // suspension, not CPU), so it is deliberately left unmarked to avoid polluting the timeline.
        // A4: brackets the RunSymbolWorkerPass call, which now reads the SHARED decode (get-or-decode) —
        // ~0 cost on the common "mesh decoded first, symbol reuses" order; the true symbol-cadence cost is
        // whatever's left after that (feature extract), not the decode itself.
        /// <summary>Profiler marker name constants (SSOT) for this subsystem's per-tile/per-frame markers —
        /// referenced by the <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c>
        /// (internal, reached via <c>InternalsVisibleTo</c>). Names are hierarchical so the Profiler window's
        /// flat marker search reads as a tree; keep them that way.</summary>
        internal static class ProfilerMarkerNames
        {
            internal const string SymbolExtract = "MapRenderer.Symbol.Extract";
            internal const string AtlasUpload  = "MapRenderer.Symbol.AtlasUpload";
            internal const string BatchCollect = "MapRenderer.Symbol.BatchBuild.Collect";
            internal const string BatchSoA     = "MapRenderer.Symbol.BatchBuild.SoA";
            // Split of Collect into its two independently-scaling halves. DEDUP: the heavy cross-tile dedup runs
            // OFF-MAIN (SymbolReconciler, memoized on collect-generation — camera-independent, scales with
            // tile-set churn), so this marker now brackets only the main-thread pickup/schedule bookkeeping.
            // CLASSIFY: the coverage cull (ClassifyActive) — camera-dependent, runs every frame. No ms figures
            // here: a profiling snapshot in source goes stale and misleads; measurements live in the devloop.
            internal const string BatchCollectDedup    = "MapRenderer.Symbol.BatchBuild.Collect.Dedup";
            internal const string BatchCollectClassify = "MapRenderer.Symbol.BatchBuild.Collect.Classify";
        }

        // Renamed from TileDecode: under the eager decode this marker brackets the EXTRACT only — the
        // decode already ran inside the source's GetTile. A marker whose name lies is worse than no marker.
        private static readonly ProfilerMarker PmSymbolExtract =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolExtract);
        private static readonly ProfilerMarker PmAtlasUpload =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.AtlasUpload);

        // The two halves of CurrentBatch (nest under MapView's MapRenderer.Symbol.BatchBuild): the A-3 cross-tile
        // dedup gather vs the symbol→SoA build (+ tile-corner projection). Split so the profiler shows which
        // half of the per-frame batch rebuild dominates at high zoom.
        private static readonly ProfilerMarker PmBatchCollect =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchCollect);
        private static readonly ProfilerMarker PmBatchSoA =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchSoA);
        private static readonly ProfilerMarker PmCollectDedup =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchCollectDedup);
        private static readonly ProfilerMarker PmCollectClassify =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchCollectClassify);

        // Per-(source, tile) built symbols, with the active/cached lifecycle that mirrors the tile MESH cache
        // (Model B) so symbols survive a leave-cover → cache-hit → re-enter-cover round trip. Sized to the
        // prepared mesh cache's count cap so a cached tile's symbols always outlive its meshes.
        internal readonly SymbolTileStore _store;
        // A-1: whether the prepared mesh cache is enabled — drives keep-warm-on-release. Enabled ⇒ a released
        // tile can return via a cache HIT (no re-fetch), so keep its symbols warm to restore them; disabled ⇒
        // a revisit always re-fetches (→ rebuild), so keeping warm is pointless → drop on release.
        private readonly bool _cacheEnabled;
        // A-1: reused buffer for the per-frame reconcile — LoadedTileKey (source, tile) mapped to store keys,
        // filtered to sources that actually have symbol layers. Never reallocated in steady state.
        private readonly List<SymbolTileStore.Key> _reconcileKeys = new();
        private int _lastUploadedGlyphCount;
        private bool _loggedOverflow;
        private bool _loggedSkip;

        // ── Stall #1 fix (Stage A) / A5b feed swap: coalesced atlas upload + budgeted tail pump ────────
        /// <summary>A5a: a worker-phase-complete symbol build awaiting its budgeted main-thread tail
        /// (<see cref="RunTailAsync"/>) — the per-layer shape + commit. Readonly fields + ctor: MapRenderer.Unity
        /// has no IsExternalInit polyfill, and this mirrors the local carrier idiom
        /// (<c>TileManager.LoadedKey</c>/<c>SourceKey</c>).</summary>
        // internal, not private: _readyTails/_handoffQueue are internal for the test-assembly seams (see
        // SymbolSubsystemTestExtensions), and a field cannot be more accessible than its own type.
        internal readonly struct ReadySymbolTail
        {
            public readonly SymbolTileStore.Key   Key;
            public readonly int                        Generation;   // BeginBuild's gen — commit guard
            public readonly TileSymbolLayerProcessor[] Processors;   // extraction held inside each
            public readonly SymbolTileBuffer         Buffer;      // the build's shared, POOLED buffer buffer
            public readonly CancellationToken          Ct;           // the build's style-scoped token
            public readonly string                     SourceId;     // for the failure log line
            public readonly TileId                     Tile;
            /// <summary>Symbol-symbol perf Phase 1 / Stage 1 (design §4, §5 B): this tile's render-space origin
            /// (<c>TileLayerProcessContext.TileOriginRender</c>, captured at kick in <see cref="TryBeginBuild"/>)
            /// — threaded through to <see cref="RunTailAsync"/> so the main-thread bake needs no separate
            /// per-tile projection lookup.</summary>
            public readonly double3                    TileOriginRender;
            public ReadySymbolTail(SymbolTileStore.Key key, int generation, TileSymbolLayerProcessor[] processors,
                SymbolTileBuffer buffer, CancellationToken ct, string sourceId, TileId tile, double3 tileOriginRender)
            {
                Key = key; Generation = generation; Processors = processors; Buffer = buffer;
                Ct = ct; SourceId = sourceId; Tile = tile; TileOriginRender = tileOriginRender;
            }
        }

        // A5b: the worker→main thread-safe handoff — TileManager's kick task (POOL thread) enqueues here
        // (SymbolTileWorkerPass.RunWorkerAndHandoff, below); PumpBuilds (MAIN thread) drains it into
        // _readyTails as its first step, every frame. A ConcurrentQueue is the carrier + ordering +
        // safe-publication barrier in one — no volatile flag, no manual pending-list scan (design §Q2).
        internal readonly ConcurrentQueue<ReadySymbolTail> _handoffQueue = new();

        /// <summary>D6: a build kicked while <see cref="SpritesSettled"/> was false — parked on the pool
        /// thread (its worker step never ran) instead of committing icon-starved. Carries everything the
        /// worker step needs to run LATER, once the sprite fetch resolves: the raw <c>layerIndices</c> (NOT
        /// yet turned into <see cref="TileSymbolLayerProcessor"/>[] — the atlas is a ctor arg) and the held
        /// tile decode. `MapRenderer.Unity` has no <c>IsExternalInit</c> polyfill, so ctor + readonly fields
        /// (mirrors <see cref="ReadySymbolTail"/>).</summary>
        internal readonly struct PendingSymbolBuild
        {
            public readonly SymbolTileStore.Key      Key;
            public readonly int                           Generation;
            public readonly List<int>                     LayerIndices;
            public readonly SymbolTileBuffer            Buffer;
            public readonly TileLayerProcessContext       Context;
            /// <summary>This entry's OWN reference, taken at park time (inside <see cref="TryParkBuild"/>'s
            /// gate) while the kick's reference was still live. R2: no separate release token — the
            /// reference and its release both live on this SAME <see cref="SharedDisposable{T}"/>. Every path
            /// that removes an entry from the queue must call <see cref="SharedDisposable{T}.Release"/> on it
            /// exactly once — the drain's <c>finally</c>, the drain's ct-drop, or
            /// <see cref="DrainAndDiscardParkedBuilds"/>.</summary>
            public readonly SharedDisposable<IDecodedTile> Decode;
            public readonly CancellationToken             Ct;
            public readonly string                        SourceId;
            public readonly TileId                        Tile;
            public PendingSymbolBuild(SymbolTileStore.Key key, int generation, List<int> layerIndices,
                SymbolTileBuffer buffer, TileLayerProcessContext context, SharedDisposable<IDecodedTile> decode,
                CancellationToken ct, string sourceId, TileId tile)
            {
                Key = key; Generation = generation; LayerIndices = layerIndices; Buffer = buffer;
                Context = context; Decode = decode; Ct = ct; SourceId = sourceId; Tile = tile;
            }
        }

        /// <summary>D6: parked builds awaiting the sprite fetch to settle (pool thread enqueues from
        /// <see cref="SymbolTileWorkerPass.RunWorkerAndHandoff"/> in park mode; <see cref="PumpBuilds"/>
        /// drains it once <see cref="SpritesSettled"/>). Same safe-publication carrier as
        /// <see cref="_handoffQueue"/>.</summary>
        internal readonly ConcurrentQueue<PendingSymbolBuild> _pendingSpriteQueue = new();

        // R1: the atomic park's gate — makes TryParkBuild's ct-check + Acquire() + Enqueue exclusive with
        // DrainAndDiscardParkedBuilds' dequeue + dispose, so a park can never land in the window between a
        // canceller's cancel and its drain (see TryParkBuild's doc). Never recreated (unlike _buildCts) — the
        // two parties are always different threads, so plain non-reentrant lock is correct and simplest.
        private readonly object _parkGate = new();

        // Worker-phase-complete builds awaiting their budgeted tail (main-thread only). PumpBuilds' tail-start
        // loop drains this FIFO at most MaxBuildsPerFrame per frame (§D4/§D6).
        internal readonly List<ReadySymbolTail> _readyTails = new();

        // 4.4c (3b): a SymbolTileBuffer free-list — main-thread only (every Rent/Return call site,
        // TryBeginBuild and RunTailAsync's finally, runs on the main thread). Builds interleave (a tail's
        // ShapeAsync awaits glyph fetches), so each in-flight build RENTS its own instance rather than sharing
        // one field across builds — see SymbolTileBuffer's own lifetime doc. A build whose buffer is never
        // returned (dropped on a cancel/restyle path before RunTailAsync's finally runs — the handoff-queue
        // drain, a parked build's discard, SetStyle's _readyTails.Clear()) simply degrades to a fresh
        // allocation next time; deliberately NOT chased down every drop path (a still-referenced buffer must
        // never be recycled out from under it — see RunTailAsync's own comment).
        private readonly Stack<SymbolTileBuffer> _bufferPool = new();

        private SymbolTileBuffer RentBuffer() => _bufferPool.Count > 0 ? _bufferPool.Pop() : new SymbolTileBuffer();

        private void ReturnBuffer(SymbolTileBuffer buffer)
        {
            if (buffer == null) return;
            buffer.Clear();
            _bufferPool.Push(buffer);
        }

        // Cancels in-flight builds on restyle/teardown so a resumed build never touches disposed glyph/atlas
        // state (closes the missing-token + restyle-vs-in-flight-build risks from the review). Recreated per
        // SetStyle so each style has its own cancellation scope.
        private CancellationTokenSource _buildCts = new();

        /// <summary>Max symbol-tile TAILS started per frame in <see cref="PumpBuilds"/> — the responsiveness
        /// knob for stall #1 (shaping, the main-thread burst). Default 1 (locked design default); MapView may
        /// serialize it later. Worker-phase starts ride TileManager's kick cadence
        /// (<c>MaxMeshBuildsPerTick</c>), not this knob.</summary>
        public int MaxBuildsPerFrame { get; set; } = 1;

        /// <summary>Test seam (dependency-inversion, mirroring <c>IDataSource</c>): the glyph-source factory
        /// <see cref="SetStyle"/> uses, overridable so an EditMode test can inject a fixture-backed or
        /// gated source instead of the production web source. Null ⇒ the production
        /// <see cref="GlyphSourceFactory.Create"/>.</summary>
        internal Func<StyleDocument, IGlyphSource> GlyphSourceFactoryOverride { get; set; }

        /// <summary>I5b: the sprite-source factory <see cref="SetStyle"/> uses, overridable so an EditMode
        /// test can inject a fixture source instead of the production web source — mirrors
        /// <see cref="GlyphSourceFactoryOverride"/>. Null ⇒ the production <see cref="SpriteSourceFactory.Create"/>.</summary>
        internal Func<StyleDocument, ISpriteSource> SpriteSourceFactoryOverride { get; set; }

        // Per-frame observability (mirrors TileManager's *LastTick counters) — read by tests, never the live path.
        internal int TailsStartedLastPump  { get; private set; }
        internal int AtlasUploadsLastPump  { get; private set; }
        internal int CancelledBuildCount   { get; private set; }
        // Forwarded from the shared builder (null-safe — no glyph pipeline ⇒ nothing to skip).
        internal int SkippedSymbolCount => _builder?.SkippedSymbolCount ?? 0;
        // Stage 4b: how many off-main reconciles CurrentBatch SCHEDULED — bumped once per dirty/cold frame that
        // captures a snapshot + kicks a worker, held flat across clean frames. Test telemetry (mirrors the
        // TailsStartedLastPump idiom); proves no schedule on a clean frame and a schedule on a real tile event.
        internal int CollectRecomputeCount { get; private set; }

        /// <summary>Set true when a pickup's <c>GetResult()</c> rethrew a worker fault (i.e. the exception was
        /// OBSERVED, not swallowed) — the fault-observation half of SPEC B. This class WRITES it, so it is
        /// state the subsystem produces rather than a query over it.</summary>
        internal bool ReconcileFaultObserved { get; private set; }

        /// <param name="preparedCacheMaxCount">The <c>PreparedTileCache</c>'s entry cap — bounds how many
        /// out-of-cover tiles' symbols are kept warm (clamped to a finite hard cap inside the store even when
        /// this is 0/unbounded).</param>
        /// <param name="cacheEnabled">The prepared mesh cache's master toggle — see <see cref="_cacheEnabled"/>.</param>
        public SymbolSubsystem(MapCamera camera, int preparedCacheMaxCount = 0, bool cacheEnabled = true)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _cacheEnabled = cacheEnabled;
            _store = new SymbolTileStore(preparedCacheMaxCount);
        }

        /// <summary>True once <see cref="SetStyle"/> found at least one symbol layer — MapView places symbols
        /// only when true (a style with no symbol layers has nothing to place).</summary>
        public bool HasSymbolLayers => _layersBySource != null && _layersBySource.Count > 0;

        /// <summary>The shared SDF atlas texture backing every collected symbol's UVs (null before the first
        /// glyphs upload).</summary>
        public GlyphAtlasTexture Atlas => _atlasTexture;

        /// <summary>I5b: the sprite sheet texture backing every ICON symbol's UVs (null until the style's
        /// sprite fetch resolves, or forever on a style with no <c>sprite</c> URL / no symbol layers) — fed
        /// to <see cref="Text.Placement.SymbolPlacementSystem.Tick"/>'s <c>spriteTexture</c> param by MapView.</summary>
        public Texture2D IconTexture => _spriteSheet?.Texture;

        /// <summary>I5b: the parsed sprite index + sheet dimensions <see cref="TileSymbolLayerProcessor"/>
        /// forwards to <c>SymbolFeatureExtractor.Extract</c> to resolve <c>icon-image</c> names. Null until the
        /// sprite fetch resolves. D6: this is no longer a race a caller needs to worry about — a build kicked
        /// before the fetch settles PARKS (<see cref="SpritesSettled"/>) instead of committing icon-starved, so
        /// no tile ever needs a rebuild/self-heal once this becomes non-null.</summary>
        public SpriteAtlasView SpriteAtlas => _spriteAtlas;

        /// <summary>Active (in-cover) symbol-tile count — telemetry.</summary>
        public int ActiveTileCount => _store.ActiveTileCount;

        /// <summary>Cached (out-of-cover, kept-warm) symbol-tile count — telemetry (the symbols held so a
        /// prepared-cache hit re-shows them without a re-fetch).</summary>
        public int CachedTileCount => _store.CachedTileCount;

        /// <summary>
        /// This provider's own levels, owned as a field and handed out BY REFERENCE (no copy, no boxing) —
        /// refreshed by the type that owns them at the end of <see cref="CurrentBatch"/>, which is where the last
        /// of them (<see cref="LastTileCoverageCulledCount"/>) is decided. <c>docs/telemetry-design.md</c> §3.
        ///
        /// <para>A frame with no symbol layers never reaches <see cref="CurrentBatch"/>, so it does not refresh and
        /// the struct keeps its last real values. Deliberate: the provider does not zero itself, because "the symbol
        /// pass did not run" is not the same claim as "it ran and found zero", and neither consumer clears on a
        /// missing update either (the counters hold their last value too).</para>
        /// </summary>
        internal ref readonly SymbolStoreTelemetrySnapshot Telemetry => ref _telemetry;

        private SymbolStoreTelemetrySnapshot _telemetry;

        private void RefreshTelemetry() =>
            _telemetry = new SymbolStoreTelemetrySnapshot
            {
                ActiveSymbolTiles      = ActiveTileCount,
                CachedSymbolTiles      = CachedTileCount,
                CoverageDroppedSymbols = LastTileCoverageCulledCount,
            };

        /// <summary>Departing (left cover, still fading out within the grace window) symbol-tile count — telemetry.</summary>
        public int DepartingTileCount => _store.DepartingTileCount;

        // Retain-as-departing: how long a tile's symbols stay collected (fading out) after it leaves cover. Derived
        // from the fade duration + a small margin so the grace ALWAYS exceeds the fade — the store purges a departing
        // tile only after this window, by which point its symbols have fully faded (a purge mid-fade would pop).
        internal const double DepartingGraceSeconds = SymbolPlacementSystem.FadeDurationSeconds + 0.2;

        // Stage 3: the dedup GATE passed to the store's CollectInto. The store now keys on the fixed
        // CrossTileSymbolKey.CanonicalGridMeters, NOT this value — the magnitude only gates dedup ON (> 0). Passing
        // the canonical const (rather than a bare 1.0) keeps ONE grid number in the codebase even though the store
        // ignores the magnitude. Decouples the dedup winner set from display zoom (the Stage-4 prerequisite):
        // CollectInto no longer reads CameraPoseMath.MetersPerPixel(zoom).
        internal const double DedupEnabled = CrossTileSymbolKey.CanonicalGridMeters;

        /// <summary>
        /// Rebuild for a new style: group its symbol layers by source and (re)create the shared glyph
        /// pipeline from the style's <c>glyphs</c> URL. Idempotent — safe to call on every restyle.
        ///
        /// <para><paramref name="symbolLayers"/> is the caller-derived list of symbol layers, already
        /// built by <see cref="MapRenderer.Unity.Rendering.Style.RenderLayerSet"/> in declared order (the
        /// single registry — <see cref="MapRenderer.Unity.Rendering.Style.RenderLayerFactory"/>). <paramref
        /// name="style"/> stays a parameter for <c>style.Glyphs</c> (the glyph source factory).</para>
        /// </summary>
        public void SetStyle(StyleDocument style, IReadOnlyList<SymbolStyle.StyleLayer> symbolLayers)
        {
            // Stage 4b (SPEC A): a restyle inline-drains the reconcile pipeline BEFORE the store is cleared, so a
            // stale worker can never write the back buffer afterward (single-writer holds without a cross-frame
            // "leave inFlight true" dance). (1) cancel in-flight builds + the reconcile token; (2) block the
            // in-flight worker to terminal (finite pure-CPU ⇒ a bounded main-thread block on the rare restyle is
            // fine); (3) release the front + back snapshot pins (any deferred-but-now-unpinned block frees here);
            // (4) Clear the store (remaining blocks are now unpinned ⇒ disposed immediately); (5) reset the buffers.
            _buildCts.Cancel();
            DrainInFlightReconcile();
            _store.ReleasePins(_frontSnapshot);
            _store.ReleasePins(_backSnapshot);
            _store.Clear();
            _frontSnapshot.Clear(); _backSnapshot.Clear();
            _frontResult.Clear();   _backResult.Clear();
            // R1: defense in depth, not the crash-prevention (that is 3.3's plan.WinnerCount == _mirrorCount predicate
            // term) — every front-content change bumps, so a reader never has to re-derive which sites do.
            _frontSetVersion++;
            _reconcileScheduledGen = -1;

            _lastUploadedGlyphCount = 0;
            _loggedOverflow = false;
            _loggedSkip = false;
            // Open a fresh cancellation scope, then drop everything mid-flight (their layer-index lists belong to
            // the old _layersBySource rebuilt below): A5b's pool→main handoff queue (a kick task still running on
            // the pool enqueues into the FRESH queue below only if it re-reads _handoffQueue after this point — the
            // drain-side ct-drop in PumpBuilds is what actually closes that race, §Q2) and A5a's ready-but-untailed
            // builds — their reserved store slot died with the _store.Clear() above.
            _buildCts.Dispose();
            _buildCts = new CancellationTokenSource();
            while (_handoffQueue.TryDequeue(out _)) { } // BCL ConcurrentQueue<T> has no Clear()
            DrainAndDiscardParkedBuilds(); // D6: parked builds die with the old style scope
            _readyTails.Clear();
            DisposePipeline();

            // I5b: drop the previous style's sheet (if any) before fetching the new one — mirrors
            // DisposePipeline's glyph-atlas teardown, unconditional (every restyle, even to a style with no
            // symbol layers, must release the GPU texture).
            _spriteSheet?.Dispose();
            _spriteSheet = null;
            _spriteAtlas = null;

            _allSymbolLayers.Clear();
            _layersBySource = new Dictionary<string, List<int>>();
            if (symbolLayers != null)
            {
                for (int i = 0; i < symbolLayers.Count; i++)
                {
                    SymbolStyle.StyleLayer symbol = symbolLayers[i];
                    if (symbol?.Source == null) continue; // defensive — RenderLayerFactory's guard makes this unreachable
                    int index = _allSymbolLayers.Count; // == this layer's MaterialIndex
                    _allSymbolLayers.Add(symbol);
                    if (!_layersBySource.TryGetValue(symbol.Source, out List<int> indices))
                        _layersBySource[symbol.Source] = indices = new List<int>();
                    indices.Add(index);
                }
            }
            // I5b: kick off the sprite-sheet fetch (fire-and-forget, cancelled via THIS style's _buildCts
            // scope like every other in-flight build). Independent of the `glyphs` URL check below — an
            // icon-only style has no `glyphs` but still needs its sprite sheet, so this must not be gated on it.
            //
            // P2: this now runs BEFORE the no-symbol-layers early return, not after. The sheet is no longer
            // an icons-only resource — fill-pattern layers resolve against the same sheet — so a style with
            // pattern fills and NO symbol layers must still fetch it. Left below the return, those layers
            // would never resolve and would clip forever. The fetch never depended on _layersBySource, so
            // hoisting it changes nothing else; _buildCts is already this style's fresh scope by here, so the
            // cancellation contract is untouched.
            //
            // D6: `.Preserve()`d (not `.Forget()`) so SpritesSettled can poll its terminal status — a build
            // kicked before this resolves PARKS instead of committing icon-starved (see TryBeginBuild).
            // Stamp the deadline clock's start HERE, at the same moment the fetch itself starts, so
            // SpritesSettled's bound (SpriteFetchDeadlineSeconds) measures from the real fetch start on
            // every restyle, not just the first one.
            _spriteFetchStartedAtSeconds = NowSeconds;
            _spriteFetchTask = FetchSpriteSheetAsync(style, _buildCts.Token).Preserve();

            if (_layersBySource.Count == 0) return; // no symbol layers — stay idle (demo seam still works)

            // D11/E2: per-layer materials (SymbolText clone + text-halo-* bind) are no longer built here —
            // they live on each SymbolRenderLayer, built by RenderLayerSet.Build from this SAME symbolLayers
            // list (in the same declared order, so the ordinal mapping stays 1:1). This class only needs the
            // glyph pipeline below.

            // The glyph pipeline needs the style's glyphs URL. Without it there are no glyphs to shape, so
            // leave _builder null (TryBeginBuild no-ops, returning null — no symbol participation) rather
            // than throw — the symbols just don't render.
            if (string.IsNullOrEmpty(style.Glyphs))
            {
                Debug.LogWarning("[SymbolSubsystem] style has no 'glyphs' URL — symbol labels will not render.");
                return;
            }
            int dim = Math.Min(SystemInfo.maxTextureSize, AtlasDimension);
            IGlyphSource glyphSource = (GlyphSourceFactoryOverride ?? GlyphSourceFactory.Create)(style);
            _glyphManager = new GlyphManager(glyphSource, new GlyphAtlas(dim, dim));
            _atlasTexture = new GlyphAtlasTexture();
            _builder = new StyledSymbolTileBuilder(_glyphManager);
        }

        /// <summary>Epic A / A5b <see cref="Processing.ISymbolTileWorkerFactory"/> entry — MAIN THREAD, called
        /// from <see cref="Tile.TileManager"/>'s per-tile kick (<c>PumpPending</c>): begin a symbol build for
        /// this <paramref name="sourceId"/>/<paramref name="tile"/> if this source has symbol layers and the
        /// glyph pipeline is live. Returns null for no participation (a mesh-only source, or no glyph
        /// pipeline) — TileManager treats null as "the kick runs the mesh pass alone".
        ///
        /// <para>The main-thread prologue: BeginBuild + camera zoom/projection capture + one processor per
        /// layer. Camera properties are fixed for the whole frame, so capturing zoom/projection here at the
        /// kick call is behaviour-preserving — the value is identical for any build kicked and pumped in the
        /// same frame; only the SAMPLE FRAME shifts, an accepted timing delta.</para></summary>
        public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
        {
            if (_builder == null) return null;
            if (!_layersBySource.TryGetValue(sourceId, out List<int> layerIndices)) return null;

            var key = new SymbolTileStore.Key(sourceId, tile);
            int gen = _store.BeginBuild(key); // reserve the active slot (collected as empty until committed)

            // Capture the main-thread inputs BEFORE the pool-side worker step (Unity APIs are main-thread
            // only): this build's own builder, the camera zoom + projection, and one processor per style
            // layer, all sharing the same output list.
            StyledSymbolTileBuilder builder    = _builder;
            double                  zoom       = _camera.CurrentProperties.Zoom;
            var                     projection = _camera.Projection;

            // I5b/D6: read _spriteAtlas HERE, at kick time (main thread) — the same "capture Unity-adjacent
            // inputs before the pool-side worker step" rule as zoom/projection above. D6: readiness is read
            // alongside it — "settled" means the fetch reached a TERMINAL state (resolved with a sheet,
            // resolved absent, faulted, or cancelled) OR SpriteFetchDeadlineSeconds elapsed waiting on a
            // fetch that never terminates (a hung endpoint) — NOT "the atlas is non-null" (see SpritesSettled's doc).
            SpriteAtlasView spriteAtlas     = _spriteAtlas;
            bool            spritesSettled  = SpritesSettled;

            // 4.4c (3b): rented from the pool rather than `new`, one instance per in-flight build (builds
            // interleave — see the pool's own doc). Returned to the pool in RunTailAsync's finally, once
            // Bake has consumed it.
            SymbolTileBuffer buffer = RentBuffer();
            var context = new TileLayerProcessContext
            {
                Tile             = tile,
                Zoom             = zoom,
                TileOriginRender = TileRenderOrigin.Project(tile, projection),
                Projection       = projection,
            };

            if (spritesSettled)
            {
                var processors = new TileSymbolLayerProcessor[layerIndices.Count];
                for (int k = 0; k < layerIndices.Count; k++)
                {
                    int globalIndex = layerIndices[k];
                    processors[k] = new TileSymbolLayerProcessor(builder, _allSymbolLayers[globalIndex], globalIndex, buffer, spriteAtlas);
                }
                return new SymbolTileWorkerPass(key, gen, processors, buffer, context, _buildCts.Token, sourceId, tile, this);
            }

            // D6: NOT settled — park. The atlas is a TileSymbolLayerProcessor ctor arg, so processors cannot
            // be built yet; carry the raw layerIndices instead and construct them once PumpBuilds' pending
            // drain sees SpritesSettled. Everything else (BeginBuild's reserved slot, the zoom/projection
            // capture) is unchanged — only the worker step is deferred. R1: the park ctor carries `this`
            // (not the queue directly) — TryParkBuild is the gated site, so the queue and _parkGate stay
            // private to the subsystem.
            return new SymbolTileWorkerPass(key, gen, layerIndices, buffer, context, _buildCts.Token, sourceId, tile, this);
        }

        /// <summary>Epic A / A5b <see cref="Processing.ISymbolTileWorkerPass"/> implementor — the captured
        /// build state carried from <see cref="TryBeginBuild"/> (main) to <see cref="RunWorkerAndHandoff"/>
        /// (pool, inside TileManager's kick task). Infallible from the caller's view: owns its own
        /// try/catch, never rethrows (TileManager's kick lambda wraps the call too — belt-and-braces, §Q5).</summary>
        private sealed class SymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            private readonly SymbolTileStore.Key         _key;
            private readonly int                              _generation;
            private readonly TileSymbolLayerProcessor[]       _processors;  // null in park mode
            private readonly SymbolTileBuffer               _buffer;
            private readonly TileLayerProcessContext           _context;
            private readonly CancellationToken                _ct;
            private readonly string                           _sourceId;
            private readonly TileId                            _tile;
            // non-park mode only — the structure-test invariant (TileProcessingStructureTests) pins
            // TileLayerProcessorRunner.RunSymbolWorkerPass to exactly ONE call site in this whole class, so
            // the actual run+enqueue lives on the OWNER (SymbolSubsystem.RunSymbolWorkerAndHandoff),
            // shared with PumpBuilds' D6 pending-drain — this class just delegates to it.
            private readonly SymbolSubsystem             _owner;
            // D6: park mode — the sprite fetch had not settled at kick time (TryBeginBuild), so the atlas-
            // dependent TileSymbolLayerProcessor[] cannot be built yet. RunWorkerAndHandoff enqueues the raw
            // inputs instead of running the extract; PumpBuilds' pending drain finishes the job once settled.
            private readonly bool       _parked;
            private readonly List<int>  _layerIndices; // park mode only

            public SymbolTileWorkerPass(SymbolTileStore.Key key, int generation, TileSymbolLayerProcessor[] processors,
                SymbolTileBuffer buffer, TileLayerProcessContext context, CancellationToken ct, string sourceId, TileId tile,
                SymbolSubsystem owner)
            {
                _key = key; _generation = generation; _processors = processors; _buffer = buffer;
                _context = context; _ct = ct; _sourceId = sourceId; _tile = tile; _owner = owner;
                _parked = false;
            }

            /// <summary>D6 park-mode ctor — see the field docs above. R1: carries <c>owner</c> rather than the
            /// queue directly — <see cref="SymbolSubsystem.TryParkBuild"/> is the one gated site, so the
            /// queue and its gate stay private to the subsystem.</summary>
            public SymbolTileWorkerPass(SymbolTileStore.Key key, int generation, List<int> layerIndices,
                SymbolTileBuffer buffer, TileLayerProcessContext context, CancellationToken ct, string sourceId, TileId tile,
                SymbolSubsystem owner)
            {
                _key = key; _generation = generation; _buffer = buffer;
                _context = context; _ct = ct; _sourceId = sourceId; _tile = tile; _owner = owner;
                _layerIndices = layerIndices; _parked = true;
            }

            /// <summary>POOL THREAD (inside TileManager's mesh kick task, after the mesh pass): run this
            /// build's symbol worker pass over the SAME shared decode, then enqueue the completed worker
            /// phase for <see cref="PumpBuilds"/>' main-thread drain. A cancelled token (restyle/teardown
            /// raced ahead of this pool task) is a cheap early-out — never enqueued, so a stale build never
            /// reaches the tail (mirrors the drain-side ct-drop, §Q2 belt-and-braces). D6: in park mode, the
            /// extract does NOT run here — the HANDLE is retained and queued instead, via
            /// <see cref="SymbolSubsystem.TryParkBuild"/>, for <see cref="PumpBuilds"/>' drain to finish
            /// once the sprite fetch settles.
            ///
            /// <para><b>The park ACQUIRES a reference, and that reference is the whole point.</b> The tile is
            /// decoded and owns Allocator.Persistent buffers, so a parked entry that did not acquire would
            /// leak them. The acquire
            /// happens (inside <see cref="SymbolSubsystem.TryParkBuild"/>'s gate) while the KICK's
            /// reference is still live (this method runs inside the kick lambda, before its <c>finally</c>),
            /// so the count can never reach zero between the two and the tile survives the whole
            /// <c>SetStyle</c>→<c>SpritesSettled</c> window. That is what retires the parked re-decode: the
            /// drain reads the SAME decoded tile the kick read.</para></summary>
            public void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)
            {
                try
                {
                    if (_ct.IsCancellationRequested) return;
                    if (_parked)
                    {
                        // R1: TryParkBuild is the ONE gated site — ct-check, Acquire() and Enqueue all run
                        // under the SAME lock the abandon-drain takes, so there is no window left between
                        // them for a canceller to land in (see TryParkBuild's doc). A refusal (false) takes
                        // no reference and enqueues nothing, so there is nothing left to release here.
                        _owner.TryParkBuild(_key, _generation, _layerIndices, _buffer, _context, decode, _ct,
                            _sourceId, _tile);
                        return;
                    }
                    _owner.RunSymbolWorkerAndHandoff(decode, in _context, _processors, _key, _generation, _buffer,
                        _ct, _sourceId, _tile);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SymbolSubsystem] label build failed for tile {_tile} (source '{_sourceId}'): {ex.Message}");
                }
            }
        }

        /// <summary>The SOLE call site of <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> in this
        /// class (a structure-test-pinned invariant, TileProcessingStructureTests) — POOL THREAD: run one
        /// build's symbol worker pass over the shared decode, then enqueue the completed phase for
        /// <see cref="PumpBuilds"/>' main-thread tail-start drain. Shared by <see cref="SymbolTileWorkerPass"/>
        /// (the un-parked kick path) and <see cref="PumpBuilds"/>' D6 pending-drain (the parked path, once the
        /// sprite fetch settles) — the two call SAME code, not two copies of it.</summary>
        private void RunSymbolWorkerAndHandoff(
            SharedDisposable<IDecodedTile> decode, in TileLayerProcessContext context, TileSymbolLayerProcessor[] processors,
            SymbolTileStore.Key key, int generation, SymbolTileBuffer buffer, CancellationToken ct,
            string sourceId, TileId tile)
        {
            try
            {
                using (PmSymbolExtract.Auto())
                    TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, processors);
                _handoffQueue.Enqueue(new ReadySymbolTail(key, generation, processors, buffer, ct, sourceId, tile,
                    context.TileOriginRender));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolSubsystem] label build failed for tile {tile} (source '{sourceId}'): {ex.Message}");
            }
        }

        /// <summary>
        /// R1: the ONE gated site for parking a build — makes the park ATOMIC via exclusion (not
        /// idempotency), keeping a racing acquire and drain safe. Called from the pool, inside
        /// TileManager's kick lambda, via <see cref="SymbolTileWorkerPass.RunWorkerAndHandoff"/>.
        ///
        /// <para><b>The invariant.</b> Under <c>lock (_parkGate)</c>: if <paramref name="ct"/> is already
        /// cancelled, take NO reference and enqueue nothing — return <see langword="false"/>. Otherwise
        /// <see cref="SharedDisposable{T}.Acquire"/> and enqueue, both inside the SAME lock
        /// <see cref="DrainAndDiscardParkedBuilds"/> takes around its dequeue. A canceller
        /// (<see cref="SetStyle"/>, <see cref="DoDispose"/>)
        /// always cancels <see cref="_buildCts"/> strictly BEFORE calling <see cref="DrainAndDiscardParkedBuilds"/>,
        /// so a park that reads the gate AFTER that cancel sees it inside the SAME lock the drain would have
        /// taken and refuses before it ever holds a reference — there is no window left in which it could
        /// acquire one the drain has already swept past.</para>
        /// </summary>
        /// <param name="decode">The kick's decode handle — BORROWED; a successful park takes its own
        /// reference via <see cref="SharedDisposable{T}.Acquire"/>, never releasing the caller's.</param>
        /// <param name="ct">This build's style-scoped cancellation token, re-checked here (not trusted from
        /// the caller's earlier read) because the two reads can straddle a cancel.</param>
        /// <returns><see langword="true"/> if the build was enqueued (the caller now owns nothing further to
        /// do); <see langword="false"/> if it was refused (nothing acquired, nothing enqueued).</returns>
        internal bool TryParkBuild(SymbolTileStore.Key key, int generation, List<int> layerIndices,
            SymbolTileBuffer buffer, TileLayerProcessContext context, SharedDisposable<IDecodedTile> decode,
            CancellationToken ct, string sourceId, TileId tile)
        {
            lock (_parkGate)
            {
                if (ct.IsCancellationRequested) return false;

                decode.Acquire();
                try
                {
                    _pendingSpriteQueue.Enqueue(new PendingSymbolBuild(
                        key, generation, layerIndices, buffer, context, decode, ct, sourceId, tile));
                }
                catch
                {
                    // The queue never accepted the entry, so nothing else can ever own this reference —
                    // release it here (still inside the gate — Release() never re-enters _parkGate).
                    decode.Release();
                    throw;
                }
                return true;
            }
        }

        /// <summary>
        /// D6: the SINGLE purge funnel for the parked (pre-atlas) pending-build queue — dequeues every
        /// entry and throws it away. Both abandonment sites call it: <see cref="SetStyle"/> (parked builds
        /// die with the old style scope) and <see cref="DoDispose"/> (they die with teardown too).
        ///
        /// <para>It exists as a method rather than two bare <c>while (TryDequeue(out _)) { }</c> loops
        /// because two loops are two places to remember a future per-entry obligation, and one is one. The
        /// live drain in <see cref="PumpBuilds"/> is deliberately NOT routed through here — it CONSUMES
        /// entries rather than discarding them, which is the opposite job.</para>
        /// </summary>
        private void DrainAndDiscardParkedBuilds()
        {
            // FUNNEL 4: each discarded entry holds a REFERENCE to a decoded tile (taken at park time), so
            // throwing the entry away is not enough — dropping it un-disposed leaks the tile's
            // Allocator.Persistent buffers. This is exactly the future per-entry obligation the funnel was
            // extracted to have one home for.
            //
            // R1: gated by the SAME _parkGate TryParkBuild takes, so this dequeue-and-dispose loop can never
            // interleave with a park's own acquire+enqueue — see TryParkBuild's doc for what that buys.
            lock (_parkGate)
            {
                while (_pendingSpriteQueue.TryDequeue(out PendingSymbolBuild dropped)) // no Clear() on ConcurrentQueue<T>
                    dropped.Decode.Release();
            }
        }

        /// <summary>
        /// MAIN THREAD, once per frame (called by the owning map view): first drain
        /// <see cref="_handoffQueue"/> (worker phases TileManager's kick completed on the pool since the
        /// last pump) into <see cref="_readyTails"/> — dropping any entry whose <c>ct</c> is already
        /// cancelled (a restyle/teardown raced ahead of the enqueue, §Q2; counted, never started) — then
        /// (A5a) start at most <see cref="MaxBuildsPerFrame"/> ready TAILS, then perform AT MOST ONE atlas
        /// GPU upload if the shared atlas grew since the last upload (coalescing every commit + glyph-range
        /// arrival that landed since last frame into one ≤16 MB blit).
        ///
        /// <para>Worker-phase starts happen via TileManager's per-tile kick
        /// (<see cref="TryBeginBuild"/>), paced by its own kick cadence, not here. <see cref="MaxBuildsPerFrame"/>
        /// gates ONLY tail starts — the knob's real stall-#1 role (§D4: shaping, not extraction, is the
        /// main-thread burst). Under a burst of K tiles becoming tail-ready around the same frame, the Kth
        /// tile's shaping starts ~K / <see cref="MaxBuildsPerFrame"/> pumps later — an ACCEPTED, bounded
        /// symbol-appearance-latency tradeoff (§D6/N1), never a symbol-content change.</para>
        /// </summary>
        public void PumpBuilds()
        {
            TailsStartedLastPump  = 0;
            AtlasUploadsLastPump  = 0;
            if (_builder == null) return; // no glyph pipeline (style has no 'glyphs' URL) — nothing to do

            // A5b: drain the pool→main handoff FIRST — every worker phase TileManager's kick completed on
            // the pool since the last pump lands in _readyTails here (or is dropped, if its build was
            // cancelled mid-flight — a restyle/teardown that raced ahead of the pool-side enqueue, §Q2).
            while (_handoffQueue.TryDequeue(out ReadySymbolTail ready))
            {
                if (ready.Ct.IsCancellationRequested) { CancelledBuildCount++; continue; } // never starts (F-4)
                _readyTails.Add(ready);
            }

            // D6: drain the parked (pre-atlas) pending queue once the sprite fetch has settled — construct
            // each build's TileSymbolLayerProcessor[] NOW with the live _spriteAtlas, then dispatch the
            // worker phase (decode + extract) to the pool exactly as the kick would have, landing in
            // _handoffQueue on completion. The extract stays off-main; no new budget knob — the tails still
            // trickle at MaxBuildsPerFrame below. Not settled ⇒ leave the queue alone (checked every pump).
            //
            // R1: deliberately UNGATED (no lock (_parkGate) here) — this loop only ever runs on the MAIN
            // thread, same as TryParkBuild's canceller-side counterpart (DrainAndDiscardParkedBuilds), so
            // the two can never run concurrently with EACH OTHER; ConcurrentQueue's own safe-publication is
            // what makes it safe against the park's pool-side TryDequeue race. Only the abandon-drain races
            // the park's acquire window — this live drain never cancels, so it has no such window to close.
            if (SpritesSettled)
            {
                while (_pendingSpriteQueue.TryDequeue(out PendingSymbolBuild pending))
                {
                    if (pending.Ct.IsCancellationRequested)
                    {
                        // Funnel 4's third mouth: this entry leaves the queue here and reaches no dispatch,
                        // so its reference is released HERE or nowhere.
                        pending.Decode.Release();
                        CancelledBuildCount++;
                        continue; // mirrors the _handoffQueue drain above (F-4)
                    }
                    PendingSymbolBuild captured = pending;
                    // OWNERSHIP GUARD over the dequeue→worker-start window. TryDequeue already took the
                    // queue's reference away, and for a DISPATCHED entry the only release lives inside a
                    // delegate that has not started yet — so every statement between the dequeue and
                    // RunOnThreadPool accepting the delegate is a window in which a throw (a stale layer
                    // index, an allocation failure, the dispatch itself) strands the last reference to a
                    // decoded tile with no owner anywhere. Ownership is handed over only once the dispatch
                    // has returned — `handedToWorker` is what makes the two mouths below MUTUALLY EXCLUSIVE
                    // rather than relying on idempotency: R2's SharedDisposable has no per-acquire token, so a
                    // release from both mouths for the same entry would be a genuine double-release (an
                    // unbalanced Release(), caught only by a DEBUG assertion — see SharedDisposable's doc).
                    bool handedToWorker = false;
                    try
                    {
                        // Reads _builder/_allSymbolLayers LIVE rather than capturing them in
                        // PendingSymbolBuild (unlike the kick path's `builder` local) — safe only because
                        // the ct check above already dropped any entry from a stale style scope, and
                        // SetStyle/DoDispose drain this queue BEFORE DisposePipeline nulls _builder /
                        // _allSymbolLayers is rebuilt, so a surviving entry's style is still the live one by
                        // construction.
                        var processors = new TileSymbolLayerProcessor[captured.LayerIndices.Count];
                        for (int k = 0; k < captured.LayerIndices.Count; k++)
                        {
                            int globalIndex = captured.LayerIndices[k];
                            processors[k] = new TileSymbolLayerProcessor(_builder, _allSymbolLayers[globalIndex], globalIndex,
                                captured.Buffer, _spriteAtlas);
                        }
                        // Dispatches through the SAME RunSymbolWorkerAndHandoff the un-parked kick path uses
                        // — the structure-test invariant (TileProcessingStructureTests) pins
                        // TileLayerProcessorRunner.RunSymbolWorkerPass to exactly one call site in this class.
                        // NO `cancellationToken:` argument here, deliberately. A cancelled token would make
                        // UniTask skip the delegate entirely — and the delegate is the only thing that releases
                        // this entry's reference, so a cancellation between the check above and the dispatch
                        // would leak the tile with nothing left to observe it. The in-lambda ct check below is
                        // the same guard, and it sits INSIDE the try so the `finally` always runs.
                        UniTask.RunOnThreadPool(
                            () =>
                            {
                                try
                                {
                                    if (captured.Ct.IsCancellationRequested) return;
                                    // This build was parked at kick time, but its reference has held the kick's
                                    // decoded tile alive ever since — so this reads the SAME IDecodedTile the
                                    // mesh pass read, with no second decode. That is the cost the refcount buys
                                    // back; there is no ordering constraint between this dispatch and the kick
                                    // lambda either way.
                                    RunSymbolWorkerAndHandoff(captured.Decode, in captured.Context, processors,
                                        captured.Key, captured.Generation, captured.Buffer, captured.Ct, captured.SourceId, captured.Tile);
                                }
                                finally
                                {
                                    captured.Decode.Release(); // funnel 4's consuming mouth
                                }
                            },
                            configureAwait: false).Forget();
                        handedToWorker = true;
                    }
                    finally
                    {
                        // funnel 4's pre-handoff mouth: reached only when the entry never got a worker.
                        if (!handedToWorker) captured.Decode.Release();
                    }
                }
            }

            // A5a: start ≤ MaxBuildsPerFrame tails whose worker phase has already landed (FIFO — index 0;
            // the backlog is single-digit deep in practice, no deque needed). Deliberately NO stale/loaded
            // re-check here — a tail runs unconditionally once its worker phase lands; the store's
            // generation/superseded guard is the sole commit arbiter, so a released-to-cache tile's symbols
            // must still commit to the warm side (RunTailAsync's commit comment). Adding a drop here would
            // CHANGE that behaviour, not preserve it.
            while (_readyTails.Count > 0 && TailsStartedLastPump < MaxBuildsPerFrame)
            {
                ReadySymbolTail tail = _readyTails[0];
                _readyTails.RemoveAt(0);
                RunTailAsync(tail).Forget();
                TailsStartedLastPump++;
            }

            // ONE coalesced atlas upload per frame — any glyphs appended by builds that committed since the
            // last upload (this frame's inline-completing builds and any async ones that just resumed).
            int glyphCount = _glyphManager.Atlas.Count;
            if (glyphCount > _lastUploadedGlyphCount)
            {
                _lastUploadedGlyphCount = glyphCount;
                using (PmAtlasUpload.Auto())
                    _atlasTexture.Upload(_glyphManager.Atlas);
                AtlasUploadsLastPump = 1;
            }
            WarnOnAtlasOverflow();
            WarnOnSkippedSymbols();
        }

        /// <summary>
        /// A-1 PULL reconcile (MAIN THREAD, once per frame): given the tile pipeline's current loaded
        /// <c>(source, tile)</c> membership (from <see cref="TileManager.CollectLoadedTileKeys"/>), reconcile the
        /// symbol store — release tiles that left cover (kept warm iff the mesh cache is enabled), restore
        /// kept-warm symbols for tiles that re-entered via a cache hit. Self-healing (a membership change is
        /// corrected next frame) and reentrancy-free
        /// (nothing mutates mid-callback). Only keys for sources that actually have symbol layers are forwarded
        /// — a non-symbol source's tiles can never match a symbol entry, so they are filtered out here.
        /// </summary>
        public void ReconcileLoadedTiles(IReadOnlyList<LoadedTileKey> loaded, double nowSeconds = 0.0)
        {
            if (_layersBySource == null) return; // no style set yet
            _reconcileKeys.Clear();
            for (int i = 0; i < loaded.Count; i++)
            {
                LoadedTileKey k = loaded[i];
                if (_layersBySource.ContainsKey(k.SourceId))
                    _reconcileKeys.Add(new SymbolTileStore.Key(k.SourceId, k.Tile));
            }
            // Pass the wall-clock (from MapView) + the departing grace window so a tile that leaves cover keeps its
            // symbols COLLECTED (as departing) for the fade-out instead of popping. Grace applies only when the mesh
            // cache is enabled (symbols are dropped, not kept warm, on release otherwise → nothing to fade).
            double grace = _cacheEnabled ? DepartingGraceSeconds : 0.0;
            _store.ReconcileActiveSet(_reconcileKeys, _cacheEnabled, nowSeconds, grace);
        }

        /// <summary>A5a: the budgeted main-thread TAIL, started by <see cref="PumpBuilds"/>' tail-start loop
        /// once <see cref="TryBeginBuild"/>'s returned <c>SymbolTileWorkerPass</c> has landed a
        /// <see cref="ReadySymbolTail"/> (via the A5b pool→main handoff, drained at the top of
        /// <see cref="PumpBuilds"/>). Every layer's <c>CompleteOnMainAsync</c> runs sequentially behind this
        /// ONE hop-in, and the commit is gated behind the WHOLE loop clearing its cancellation check FIRST —
        /// partial symbols never reach <see cref="SymbolTileStore.CompleteBuild"/> (the commit guard, §C).</summary>
        private async UniTaskVoid RunTailAsync(ReadySymbolTail tail)
        {
            try
            {
                // 4.4c (3b): the buffer is returned to the pool HERE, once every layer's ShapeAsync tail has
                // been awaited (nothing async touches it after this inner try) and Bake has fully consumed it
                // into the native block — never on a path where a still-running ShapeAsync might hold it (see
                // the pool's own doc; SymbolTileBuffer's lifetime doc states the same rule from the other side).
                try
                {
                    for (int p = 0; p < tail.Processors.Length; p++)
                        await tail.Processors[p].CompleteOnMainAsync(tail.Ct);
                    tail.Ct.ThrowIfCancellationRequested();

                    // Symbol-symbol perf Phase 1 / Stage 1 (design §4, §5 B): bake this tile's native SoA block HERE,
                    // on the main thread — glyph quads / curved glyphs are only materialized by the layer shape
                    // above, so there is nothing left to bake off-main. Stage 1 is purely additive: nothing reads
                    // this block yet (CompleteBuild just stores it for a future consumer).
                    var block = SymbolTileBlockBaker.Bake(tail.Buffer, SlotCount, tail.TileOriginRender, _store.StringTable);

                    // Commit — unless superseded by a newer build OR dropped mid-build (released-to-cache is NOT
                    // stale: the store writes the block to the cached side so a later hit restores it). The
                    // atlas GPU upload is coalesced into the next PumpBuilds — no atlas touch here. CompleteBuild
                    // disposes `block` itself on a superseded/dropped commit — this call never leaks it either way.
                    // Resident-graph shed (4.4b): tail.Buffer is fed to Bake above and NOT threaded through here —
                    // the store no longer holds a managed symbol list at all.
                    _store.CompleteBuild(tail.Key, tail.Generation, block);
                }
                finally
                {
                    ReturnBuffer(tail.Buffer);
                }
            }
            catch (OperationCanceledException)
            {
                // Restyle/teardown mid-tail — silent, never touches disposed state (closes the review's two
                // latent lifetime risks). The reserved slot is dropped when SetStyle/Dispose Clears the store.
                CancelledBuildCount++;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolSubsystem] label build failed for tile {tail.Tile} (source '{tail.SourceId}'): {ex.Message}");
            }
        }

        /// <summary>I5b: fetch + decode this style's sprite sheet (index JSON + PNG), fire-and-forget from
        /// <see cref="SetStyle"/> — a single keyless pair per style (unlike glyphs, no per-tile/per-fontstack
        /// requests). A null source (no <c>sprite</c> URL — <see cref="SpriteSourceFactory.Create"/> warns
        /// once and returns null) or an absent response (<see cref="SpriteResponse.HasData"/> false, e.g.
        /// 404/204) leaves <see cref="_spriteSheet"/>/<see cref="_spriteAtlas"/> null — inert, never a fault;
        /// every icon draw/extract path downstream is already guarded on them being non-null. Cancelled via
        /// <paramref name="ct"/> (this style's <c>_buildCts</c> scope) exactly like every other in-flight
        /// build — a restyle/teardown racing ahead of the fetch never touches the (possibly disposed) next
        /// style's state.</summary>
        private async UniTask FetchSpriteSheetAsync(StyleDocument style, CancellationToken ct)
        {
            ISpriteSource source = null;
            try
            {
                source = (SpriteSourceFactoryOverride ?? SpriteSourceFactory.Create)(style);
                if (source == null) return; // no 'sprite' URL — inert, style still loads

                SpriteResponse resp = await source.FetchAsync(ct);
                if (!resp.HasData) return; // explicitly absent (404/204) — inert
                ct.ThrowIfCancellationRequested();

                // Texture2D construction (inside the SpriteSheet ctor) is a main-thread-only Unity API —
                // guard even though FetchAsync's own continuation typically already resumes on main (belt-
                // and-braces, mirrors every other GPU-resource boundary in this codebase).
                await UniTask.SwitchToMainThread();
                ct.ThrowIfCancellationRequested();

                var sheet = new SpriteSheet(resp.Png, SpriteIndex.Parse(resp.Json));
                _spriteSheet = sheet;
                _spriteAtlas = sheet.View;
            }
            catch (OperationCanceledException)
            {
                // Restyle/teardown mid-fetch — silent, mirrors RunTailAsync's cancellation branch.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolSubsystem] sprite sheet fetch failed: {ex.Message}");
            }
            finally
            {
                source?.Dispose();
            }
        }

        // Stage-2 (symbol-symbol native gather): the per-frame WINNER PLAN — the placement source of truth, rebuilt
        // EVERY frame from the current collected set. Replaces the old per-frame SoA Build: CurrentBatch now only
        // COLLECTS + DEDUPS + coverage-filters + symbols each winner's (blockId, localIndex) into the plan; the
        // camera-independent SoA is already baked per tile (Stage 1), so the placement gather compacts winning
        // blocks straight into its native job lists. Reused every frame — CollectInto/ClassifyActive/plan.Build all
        // reuse their buffers, so the rebuild is allocation-free (CPU only).
        private readonly SymbolGatherPlan      _gatherPlan   = new SymbolGatherPlan();

        // ── Stage 4b: the off-main reconcile state machine (design §2, plan SPEC A/B) ──────────────────────────
        // ONE worker in flight; a completed reconcile is applied even a few frames stale (apply-stale), and a
        // reschedule fires if the store generation moved during the run. The heavy cross-tile dedup (~11 ms on a
        // tile-event frame) runs on SymbolReconciler.Run on the thread pool, off the render thread.
        internal readonly SymbolReconciler _reconciler = new SymbolReconciler();
        // Double-buffer: the FRONT result is consumed every frame (coverage-classify → gather); the worker fills
        // the BACK result. A successful pickup swaps them. The paired snapshots PIN the blocks each result's
        // (blockId → OrderedBlocks) references, so the store cannot free a block the displayed OR in-flight set
        // still points at (design §3.2). Non-readonly — the pickup swaps them by ref.
        private SymbolReconcileResult _frontResult   = new SymbolReconcileResult();
        private SymbolReconcileResult _backResult    = new SymbolReconcileResult();
        // R1: monotonic FRONT-SET version — bumped on every change to _frontResult's CONTENT, i.e. exactly the
        // events that change the winner set the gather mirror is built from: a successful reconcile swap, and the
        // SetStyle/Dispose clears. Stamped onto SymbolGatherPlan at Build time; SymbolPlacementSystem.GatherIntoMirror
        // holds its heavy pools across frames while it is unchanged. Deliberately NOT _store.CollectGeneration: that
        // moves at the tile EVENT, while the front moves 1-4 frames later at the swap (apply-stale,
        // symbols-async-reconcile-design.md §2), so a CollectGeneration key would keep hitting straight through a
        // swap and serve a stale mirror.
        private int _frontSetVersion;
        private SymbolSnapshot        _frontSnapshot = new SymbolSnapshot();
        private SymbolSnapshot        _backSnapshot  = new SymbolSnapshot();
        internal bool          _reconcileInFlight;
        // The store CollectGeneration the in-flight/last-scheduled reconcile captured at (replaces Stage 4a's
        // _collectedAtGeneration). -1 (≠ the store's initial gen 0) is a cold-start sentinel → schedule frame 1.
        private int            _reconcileScheduledGen = -1;
        private UniTask        _reconcileTask;   // .Preserve()'d so its Status is polled across frames (not awaited)
        private CancellationToken _reconcileToken;
        private bool           _loggedReconcileFault; // SPEC B: log a worker fault ONCE (no per-frame spam)
        // Backstop for the teardown drain's blocking park: the worker is finite pure-CPU, so exceeding this means
        // the completion signal was LOST (a bug), not slow work — log loudly rather than hang teardown. Mirrors
        // the mesh-build teardown's WaitOffPlayerLoop(10000) in TileManager.AwaitInFlightMeshBuilds.
        private const int ReconcileDrainTimeoutMs = 10_000;
        // ClassifyActive's per-symbol Keep/Fade/Drop decision — per-frame buffer over the FRONT result (reused,
        // alloc-free). D1: a masking classify, never a compaction, so it moves nothing in the front result.
        private readonly List<byte> _planDecision = new List<byte>();
        // Per-block inputs/outputs for the per-block coverage classify (reused, alloc-free once warm):
        //   _blockTileKeys — one tile key per OrderedBlocks entry, resolved from the native block just before the
        //     call so ClassifyActive classifies O(blocks) tiles, not O(symbols) scattered symbol refs.
        //   _blockDecision — the per-block Keep/Fade/Drop the symbol fan-out reads back through BlockId.
        private readonly List<long> _blockTileKeys = new List<long>();
        private readonly List<byte> _blockDecision = new List<byte>();

        // REVISION 2 — fade-preserving coverage cull: cross-frame state SymbolTileCoverageFilter.ClassifyActive
        // (D1; was FilterActive) reads/writes each call, all reused (alloc-free once warm). A coverage-fading tile
        // is STILL ACTIVE (in cover, just below the on-screen coverage threshold) — a SEPARATE lifecycle from
        // SymbolDeparting's "tile unloaded→cached" (see SymbolBatch's doc).
        //   _coverageAbovePrev/_coverageAboveThisFrame — PING-PONG (ref-swapped after each call, not mutated in
        //     place): self-bounding, unlike a single set that would grow unboundedly for a tile that goes above
        //     once then vanishes.
        private HashSet<long> _coverageAbovePrev = new();
        private HashSet<long> _coverageAboveThisFrame = new();
        // TileKey → fade-out deadline (now + DepartingGraceSeconds), stamped once on the crossing frame, purged
        // once expired (PurgeExpiredCoverageDeadlines, below) — mirrors SymbolTileStore's departing stamps.
        private readonly Dictionary<long, double> _coverageDepartingUntil = new();
        // This call's coverage-fading tile keys — a ClassifyTile side effect (per-TILE), now vestigial for
        // SymbolGatherPlan.Build (D1 reads the per-RECORD _planDecision instead) but still populated for any
        // other consumer / telemetry.
        private readonly HashSet<long> _coverageFadingTiles = new();
        // Reused per-TileKey Keep/Fade/Drop decision cache for SymbolTileCoverageFilter.ClassifyActive — cleared +
        // repopulated every call so a tile shared by many symbols is classified ONCE per rebuild, not once per symbol.
        private readonly Dictionary<long, byte> _tileDecisionScratch = new();
        // Reused buffer for the expired-deadline sweep (PurgeExpiredCoverageDeadlines) — never reallocated in
        // steady state.
        private readonly List<long> _coverageDepartingPurgeScratch = new();

        /// <summary>Tile-coverage pre-cull: symbols dropped from the LAST <see cref="CurrentBatch"/> because their
        /// tile covered less than <paramref name="minCoverage"/>'s worth of the screen (never entered the SoA build,
        /// gather, projection, or collision). Telemetry — mirrors <see cref="SymbolPlacementSystem.LastDistanceCulledCount"/>.</summary>
        internal int LastTileCoverageCulledCount { get; private set; }

        /// <summary>The per-frame WINNER PLAN (<see cref="SymbolGatherPlan"/>) for this frame, built from the
        /// FRONT reconcile result: the coalescing state machine first PICKS UP a completed off-main reconcile
        /// (SPEC B — swap front/back only on success) and SCHEDULES a new one if the store generation moved
        /// (SPEC A/B — one worker in flight, apply-stale), then the per-frame camera passes run over the front:
        /// the tile-coverage classification (<see cref="SymbolTileCoverageFilter.ClassifyActive"/> — a MASKING
        /// classify, never moving elements), then <see cref="SymbolGatherPlan.Build"/> which snapshots ALL front
        /// winners (Drops included — resident, masked) + their per-frame departing/coverage-fading/dropped
        /// overrides. The heavy cross-tile dedup itself no longer runs here — it is the off-main reconcile
        /// (design §2). The camera-independent SoA is baked per tile at build time (Stage 1).
        ///
        /// <para><b>Apply-stale (Stage 4b).</b> Between a tile event and its reconcile pickup (1–4 frames), this
        /// serves the slightly-stale FRONT — a new tile's symbols appear a frame or two late, a removed tile's
        /// linger and fade. Cold start (empty front) yields a zero-winner plan until the first reconcile lands.
        /// The only observable change from Stage 4a's synchronous memo is that appearance latency.</para>
        ///
        /// <para>REVISION 2: the coverage cull is a Keep/Fade/Drop classification, not a plain two-way cull — a
        /// tile crossing below threshold FADES OUT (<see cref="SymbolGatherPlan.CoverageFading"/>, applied at
        /// plan-fill time) instead of popping; only a tile that was never on screen (or whose fade grace expired)
        /// actually Drops. D1: Drop stamps <see cref="SymbolGatherPlan.Dropped"/>, masked downstream by
        /// <see cref="SymbolPlacementSystem.GatherSymbolPoints"/>. See <see cref="SymbolTileCoverageFilter"/>'s type
        /// doc for the full state machine.</para></summary>
        /// <param name="frame">This frame's scene frame (camera-relative rebase) — the SAME snapshot the tiles and
        /// <see cref="SymbolPlacementSystem.Tick"/> use, so the coverage cull's projection matches exactly.</param>
        /// <param name="minCoverage">The coverage threshold (<c>MapViewConfig.SymbolTileCoverageCull</c>) —
        /// non-positive disables the cull entirely (mirrors <see cref="SymbolTileCoverage.IsCulled"/>).</param>
        /// <param name="now">This frame's wall-clock (seconds), for the coverage-fade grace deadline — mirrors
        /// <see cref="ReconcileLoadedTiles"/>'s <c>nowSeconds</c>. Default 0.0 keeps existing no-clock call sites
        /// compiling (a no-op when <paramref name="minCoverage"/> is non-positive, since the filter's cross-frame
        /// state is then never touched).</param>
        public SymbolGatherPlan CurrentBatch(in SceneFrame frame, double minCoverage, double now = 0.0)
        {
            using (PmBatchCollect.Auto())
            {
                using (PmCollectDedup.Auto()) // now ≈0 ms every frame — pickup/schedule bookkeeping only; the dedup is off-main
                {
                    PickupCompletedReconcile(); // SPEC B
                    ScheduleReconcileIfDirty();  // SPEC A/B
                }

                // Classify-after-collect, before-plan-fill over the FRONT result: a Dropped tile's decision rides
                // the symbol (masked downstream, D1) — a masking classify moves nothing. A Fading tile's symbols are
                // flagged so the placement gather eases them out. Departing symbols are untouched (ClassifyActive
                // never classifies them). Runs EVERY frame (camera-dependent) over the stable front set.
                float4x4 viewProj = SymbolPlacementSystem.ViewProj(_camera.Camera);
                double2 viewportLogicalPx = _camera.ViewportLogicalPx;
                int culled;
                using (PmCollectClassify.Auto()) // the coverage-cull half — camera-dependent, classified per BLOCK
                {
                    // Resolve one tile key per ordered block (the native block owns it) so the coverage classify
                    // runs per block, not per scattered per-symbol ref — the SAME OrderedBlocks
                    // SymbolGatherPlan.Build snapshots one call later.
                    var orderedBlocks = _frontResult.OrderedBlocks;
                    _blockTileKeys.Clear();
                    if (_blockTileKeys.Capacity < orderedBlocks.Count) _blockTileKeys.Capacity = orderedBlocks.Count;
                    for (int b = 0; b < orderedBlocks.Count; b++)
                        _blockTileKeys.Add(orderedBlocks[b].TileKey);

                    SymbolTileCoverageFilter.ClassifyActive(_blockTileKeys, _frontResult.BlockId, _frontResult.IsDeparting,
                        _camera.Projection, frame.SceneOriginRender, viewProj, viewportLogicalPx, frame.Rebase, minCoverage,
                        _coverageAbovePrev, _coverageAboveThisFrame, _coverageDepartingUntil, _coverageFadingTiles,
                        now, DepartingGraceSeconds, _tileDecisionScratch, _blockDecision, _planDecision, out culled);
                }
                LastTileCoverageCulledCount = culled;

                // Ping-pong the above-threshold sets (ref-swap, no realloc) and purge coverage-fade deadlines that
                // elapsed as of THIS frame's clock — mirrors SymbolTileStore.PurgeExpiredDeparting, just for
                // the coverage path. Order matters: the swap/purge happen AFTER ClassifyActive reads them.
                (_coverageAbovePrev, _coverageAboveThisFrame) = (_coverageAboveThisFrame, _coverageAbovePrev);
                PurgeExpiredCoverageDeadlines(now);
            }
            using (PmBatchSoA.Auto())
                _gatherPlan.Build(_frontResult.BlockId, _frontResult.LocalIndex,
                    _frontResult.IsDeparting, _planDecision, _frontResult.OrderedBlocks, _frontSetVersion);

            RefreshTelemetry();   // the store's levels are final for this frame
            return _gatherPlan;
        }

        // SPEC B: pick up a reconcile whose worker reached a TERMINAL status (mirrors TileManager.ConsumeMeshBuild).
        // Swap front/back ONLY on success — a faulted/partial back never reaches SymbolGatherPlan.Build (which
        // assumes aligned lists). Either way the OUTGOING snapshot (the demoted old front on success, or the failed
        // back on fault) is the one leaving service, so ReleasePins(_backSnapshot) after the swap is uniformly
        // correct. Cold start's first pickup releases an empty snapshot (no-op).
        private void PickupCompletedReconcile()
        {
            if (!_reconcileInFlight || _reconcileTask.Status == UniTaskStatus.Pending) return;
            bool ok = false;
            
            try { _reconcileTask.GetAwaiter().GetResult(); ok = _reconcileTask.Status == UniTaskStatus.Succeeded; }
            catch (Exception ex) // observe → no unobserved-exception; log once (SPEC B)
            {
                ReconcileFaultObserved = true; // reached ONLY because GetResult rethrew — proves the fault is observed
                if (!_loggedReconcileFault)
                {
                    _loggedReconcileFault = true;
                    Debug.LogWarning($"[SymbolSubsystem] label reconcile faulted (retried on the next tile event): {ex.Message}");
                }
            }
            if (ok)
            {
                (_frontResult, _backResult) = (_backResult, _frontResult);
                (_frontSnapshot, _backSnapshot) = (_backSnapshot, _frontSnapshot);
                _frontSetVersion++; // R1: the front's CONTENT changed — invalidate the gather memo
                _store.ReleasePins(_backSnapshot); // the demoted old front leaves service
            }
            else
            {
                _store.ReleasePins(_backSnapshot); // the failed snapshot leaves service (NO swap)
                _backSnapshot.Clear();
            }
            _reconcileInFlight = false;
        }

        // SPEC A/B: schedule ONE off-main reconcile when the store generation moved since the last schedule and no
        // worker is in flight. Capture (pins the back snapshot's blocks) on the main thread, then run the pure
        // dedup on the thread pool; poll .Status across frames (TileManager.KickMeshBuild idiom, not an awaited
        // continuation).
        //
        // The worker closure reads the _backSnapshot / _backResult FIELDS (captures only `this`, no method locals) —
        // deliberately, so this hot per-frame method allocates NOTHING on the early-return (clean, no-schedule) path:
        // a lambda that captured locals would force Roslyn to allocate its closure DisplayClass at method ENTRY,
        // i.e. every frame. Reading the fields at execution time is race-free: _backSnapshot/_backResult are only
        // reassigned by the pickup swap (which needs this task TERMINAL) or by SetStyle/Dispose (which cancel + DRAIN
        // this task to terminal first), so neither can retarget the worker while it runs.
        private void ScheduleReconcileIfDirty()
        {
            if (_reconcileInFlight || _store.CollectGeneration == _reconcileScheduledGen) return;
            _store.CaptureSnapshot(_backSnapshot); // main thread; pins the captured blocks
            _reconcileScheduledGen = _store.CollectGeneration;
            _reconcileToken = _buildCts.Token;
            CollectRecomputeCount++;
            _reconcileInFlight = true;
            _reconcileTask = UniTask.RunOnThreadPool(() => _reconciler.Run(_backSnapshot, _backResult),
                configureAwait: false, cancellationToken: _reconcileToken).Preserve();
        }

        // SPEC A step 2: BLOCK the main thread until the in-flight worker reaches terminal (restyle/teardown has no
        // future frame to poll .Status on, unlike PickupCompletedReconcile). Parks on the task's off-PlayerLoop
        // completion via WaitOffPlayerLoop — NOT _reconcileTask.GetAwaiter().GetResult(), which THROWS on a pending
        // task instead of waiting (UniTask is not a blocking wait), so a GetResult-based drain returned WITHOUT
        // joining the worker and the caller then freed the snapshot's native columns under a still-running Run
        // (use-after-free). Same park the mesh-build teardown uses (TileManager.AwaitInFlightMeshBuilds). The finite
        // pure-CPU worker makes the bounded block acceptable; a timeout means the completion signal was lost — log,
        // never hang. Once completed the task is terminal, so the guarded GetResult observes/swallows any fault.
        // Discards the result (no swap); the caller then ReleasePins both snapshots + Clears the store.
        private void DrainInFlightReconcile()
        {
            if (!_reconcileInFlight) return;
            if (!_reconcileTask.WaitOffPlayerLoop(ReconcileDrainTimeoutMs))
                Debug.LogError("[SymbolSubsystem] reconcile drain timed out — the in-flight worker never completed; " +
                               "releasing pins anyway (the leak/UAF backstop was defeated).");
            if (_reconcileTask.Status != UniTaskStatus.Pending)
                try { _reconcileTask.GetAwaiter().GetResult(); } catch { /* faulted/canceled — tearing down */ }
            _reconcileInFlight = false;
        }

        // Test seam: run SetStyle's SPEC A teardown core in ISOLATION (cancel → drain → release front+back pins →
        // Clear), OFF the main-thread glyph rebuild — so a test can drive it on a background thread and observe,
        // from the test thread, whether a still-parked reconcile worker's block is freed under it. Mirrors the
        // SetStyle ordering above; the native drops it triggers are off-main-safe. Internal test-only
        // (InternalsVisibleTo), like <see cref="SymbolReconciler.GateForTest"/> / <c>FaultNextRun</c>.
        internal void TeardownReconcileForTest()
        {
            _buildCts.Cancel();
            DrainInFlightReconcile();
            _store.ReleasePins(_frontSnapshot);
            _store.ReleasePins(_backSnapshot);
            _store.Clear();
            _frontSnapshot.Clear(); _backSnapshot.Clear(); // so the test's later Dispose can't double-release
        }

        // Drop coverage-fade deadlines whose grace window elapsed (now >= expiry) — the reused per-call buffer
        // avoids a per-purge allocation. A purged tile's symbols are no longer forced-fading; if it is STILL below
        // threshold on a later frame with no live deadline and not in AbovePrev, ClassifyActive drops it outright
        // (grace exceeds the fade duration, so by expiry it has already faded to invisible — never a pop).
        private void PurgeExpiredCoverageDeadlines(double now)
        {
            if (_coverageDepartingUntil.Count == 0) return;
            _coverageDepartingPurgeScratch.Clear();
            foreach (KeyValuePair<long, double> kv in _coverageDepartingUntil)
                if (now >= kv.Value) _coverageDepartingPurgeScratch.Add(kv.Key);
            for (int i = 0; i < _coverageDepartingPurgeScratch.Count; i++)
                _coverageDepartingUntil.Remove(_coverageDepartingPurgeScratch[i]);
        }

        private void WarnOnAtlasOverflow()
        {
            if (_loggedOverflow || _glyphManager.Atlas.OverflowCount == 0) return;
            _loggedOverflow = true;
            Debug.LogWarning($"[SymbolSubsystem] glyph atlas full ({_glyphManager.Atlas.OverflowCount} glyph(s) " +
                             $"dropped) — increase AtlasDimension beyond {Math.Min(SystemInfo.maxTextureSize, AtlasDimension)}px.");
        }

        private void WarnOnSkippedSymbols()
        {
            if (_loggedSkip || _builder == null || _builder.SkippedSymbolCount == 0) return;
            _loggedSkip = true;
            Debug.LogWarning($"[SymbolSubsystem] skipped {_builder.SkippedSymbolCount} label(s) whose build " +
                             $"failed (first: {_builder.LastSkipReason}). Further skips suppressed; running total in " +
                             $"SkippedSymbolCount telemetry.");
        }

        protected override void DoDispose()
        {
            // Stage 4b (SPEC A): teardown runs the SAME inline-drain protocol as a restyle — cancel, drain the
            // in-flight worker to terminal, release both snapshots' pins, THEN Clear (so no stale worker writes the
            // back buffer after teardown). A test tearing down while the reconciler's GateForTest is held MUST open
            // the gate first, or DrainInFlightReconcile hangs (SPEC A test note).
            _buildCts.Cancel();   // stop any in-flight build + the reconcile token before glyph/atlas state is disposed
            DrainInFlightReconcile();
            _store.ReleasePins(_frontSnapshot);
            _store.ReleasePins(_backSnapshot);
            _buildCts.Dispose();
            while (_handoffQueue.TryDequeue(out _)) { } // BCL ConcurrentQueue<T> has no Clear()
            DrainAndDiscardParkedBuilds(); // D6: parked builds die with teardown too
            _readyTails.Clear(); // A5a: ready-but-untailed builds die with the store slot cleared below
            _store.Clear();
            _frontSnapshot.Clear(); _backSnapshot.Clear();
            _frontResult.Clear();   _backResult.Clear();
            _frontSetVersion++; // R1: decorative here (Build never runs again post-Dispose) — kept for uniformity
            _reconcileScheduledGen = -1;
            DisposePipeline();

            // I5b: the sprite sheet, disposed as a unit (mirrors _atlasTexture above).
            _spriteSheet?.Dispose();
            _spriteSheet = null;
            _spriteAtlas = null;

            _gatherPlan.Dispose(); // Stage-2: free the reused winner-plan's native lists (its blocks are borrowed)
        }

        private void DisposePipeline()
        {
            _atlasTexture?.Dispose();
            _atlasTexture = null;
            _glyphManager?.Dispose();
            _glyphManager = null;
            _builder = null;
        }
    }
}
