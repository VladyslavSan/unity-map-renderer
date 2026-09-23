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
using MapRenderer.Unity.View;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
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
    /// <summary>The decoupled production symbol subsystem: owns the shared <see cref="GlyphManager"/>, the
    /// fixed-size <see cref="GlyphAtlasTexture"/>, and the <see cref="StyledSymbolTileBuilder"/>. Symbol DATA
    /// arrives via <see cref="Tile.TileManager"/>'s per-tile kick (this implements
    /// <see cref="ISymbolTileWorkerFactory"/>); the tile LIFECYCLE is pulled — <c>ReconcileLoadedTiles</c> takes
    /// the loaded set each frame. Symbols are placed every frame, feeding <see cref="SymbolPlacementSystem"/>.
    ///
    /// <para>The glyph atlas is allocated big and FIXED (<c>AtlasDimension</c>) so its <c>Size</c> never changes
    /// as tiles append glyphs — a growing atlas would invalidate earlier tiles' baked UVs
    /// against the old size.</para></summary>
    internal sealed class SymbolSubsystem : VerifiedDisposable, ISymbolTileWorkerFactory
    {

        // ── Constants ─────────────────────────────────────────────────
        /// <summary>Target atlas edge in px, clamped to the GPU's max texture size. R8, so 4096² ≈ 16 MB.</summary>
        private const int AtlasDimension = 4096;

        // Without this, a hung endpoint (never responds, no upstream HTTP timeout) would leave the fetch Pending
        // forever, parking every build permanently. Once elapsed, SpritesSettled goes true so parks dispatch.
        internal const double SpriteFetchDeadlineSeconds = 8.0;

        // Teardown-drain backstop: the worker is finite pure-CPU, so exceeding this means the completion signal
        // was LOST (a bug), not slow work — log loudly rather than hang teardown.
        private const int ReconcileDrainTimeoutMs = 10_000;

        // How long a tile's symbols stay collected (fading out) after it leaves cover. Grace ALWAYS exceeds the
        // fade duration, so the store purges a departing tile only after it has fully faded (a mid-fade purge pops).
        internal const double DepartingGraceSeconds = SymbolPlacementSystem.FadeDurationSeconds + 0.2;

        // The dedup gate for the store's CollectInto (only its > 0 gates dedup on; the store keys on the fixed
        // CanonicalGridMeters). Passing the canonical const keeps ONE grid number in the codebase.
        internal const double DedupEnabled = CrossTileSymbolKey.CanonicalGridMeters;

        // ── Nested types ──────────────────────────────────────────────
        /// <summary>Profiler marker name constants (SSOT), referenced by the <see cref="ProfilerMarker"/> fields
        /// below and by <c>ProfilerMarkerTests</c>. Hierarchical so the Profiler's flat search reads as a tree.
        /// Only synchronous main-thread stages are marked (the awaited glyph-range ensure step's wall-clock is
        /// fetch-suspension, not CPU; shaping itself is synchronous CPU but unmarked here).</summary>
        internal static class ProfilerMarkerNames
        {
            internal const string SymbolExtract = "MapRenderer.Symbol.Extract";
            internal const string AtlasUpload  = "MapRenderer.Symbol.AtlasUpload";
            internal const string BatchCollect = "MapRenderer.Symbol.BatchBuild.Collect";
            internal const string BatchSoA     = "MapRenderer.Symbol.BatchBuild.SoA";
            // Collect's two halves: Dedup = main-thread pickup/schedule bookkeeping, Classify = the coverage cull.
            internal const string BatchCollectDedup    = "MapRenderer.Symbol.BatchBuild.Collect.Dedup";
            internal const string BatchCollectClassify = "MapRenderer.Symbol.BatchBuild.Collect.Classify";
        }

        /// <summary>A worker-phase-complete symbol build awaiting its budgeted main-thread tail
        /// (<c>RunTailAsync</c>) — the per-layer shape + commit. Internal for the test-assembly seams.</summary>
        internal readonly struct ReadySymbolTail
        {
            public SymbolTileStore.Key        Key        { get; init; }
            public string                     SourceId   => Key.SourceId;
            public TileId                     Tile       => Key.Tile;
            public int                        Generation { get; init; }   // BeginBuild's gen — commit guard
            public TileSymbolLayerProcessor[] Processors { get; init; }
            public SymbolTileBuffer           Buffer     { get; init; } // the build's shared, POOLED buffer
            public CancellationToken          Ct         { get; init; } // the build's style-scoped token
            /// <summary>This tile's render-space origin, captured at kick and threaded to <c>RunTailAsync</c> so
            /// the main-thread bake needs no separate per-tile projection lookup.</summary>
            public double3 TileOriginRender { get; init; }
        }

        /// <summary>A build kicked while <c>SpritesSettled</c> was false — parked instead of committing
        /// icon-starved. Carries what the worker step needs once the sprite fetch resolves: the raw
        /// <c>layerIndices</c> (not yet processors — the atlas is a ctor arg) and the held tile decode.</summary>
        internal readonly struct PendingSymbolBuild
        {
            public SymbolTileStore.Key Key { get; init; }
            public int Generation { get; init; }
            public List<int> LayerIndices { get; init; }
            public SymbolTileBuffer Buffer { get; init; }
            public TileLayerProcessContext Context { get; init; }
            /// <summary>This entry's OWN reference, taken at park time. Reference and release share one
            /// <see cref="SharedDisposable{T}"/>, so every path that removes an entry must <c>Release</c> it
            /// exactly once.</summary>
            public SharedDisposable<IDecodedTile> Decode { get; init; }
            public CancellationToken Ct { get; init; }
            public string SourceId { get; init; }
            public TileId Tile { get; init; }
        }

        // ── Fields ────────────────────────────────────────────────────
        private static readonly ProfilerMarker PmSymbolExtract =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolExtract);
        private static readonly ProfilerMarker PmAtlasUpload =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.AtlasUpload);

        private static readonly ProfilerMarker PmBatchCollect =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchCollect);
        private static readonly ProfilerMarker PmBatchSoA =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchSoA);
        private static readonly ProfilerMarker PmCollectDedup =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchCollectDedup);
        private static readonly ProfilerMarker PmCollectClassify =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BatchCollectClassify);

        private readonly MapCamera _camera;

        private GlyphManager _glyphManager;
        private GlyphAtlasTexture _atlasTexture;
        internal StyledSymbolTileBuilder _builder;

        // The sprite sheet backing icon UVs, fetched in SetStyle. Null on a style with no `sprite` URL / no
        // symbol layers; every icon path is null-guarded, so an absent or still-loading sheet is inert.
        private SpriteSheet _spriteSheet;
        private SpriteAtlasView _spriteAtlas;
        // The sprite fetch (SetStyle), .Preserve()'d so SpritesSettled can poll its Status across frames.
        private UniTask _spriteFetchTask;
        // Wall-clock timestamp the in-flight sprite fetch started at — read only by SpritesSettled's deadline.
        private double _spriteFetchStartedAtSeconds;

        // Flat symbol-layer list (index == ShapedSymbol.MaterialIndex) + a source id → its layers' global indices.
        // This class only needs the layer COUNT and the layers' Source/id for build routing.
        private readonly List<SymbolStyle.StyleLayer> _allSymbolLayers = new();
        private Dictionary<string, List<int>> _layersBySource;

        // Per-(source, tile) built symbols, active/cached lifecycle mirroring the tile MESH cache so symbols
        // survive a leave-cover → cache-hit → re-enter round trip.
        internal readonly SymbolTileStore _store;
        // Whether the prepared mesh cache is enabled — drives keep-warm-on-release: enabled ⇒ a released tile
        // can return via a cache HIT, so keep its symbols warm; disabled ⇒ a revisit re-fetches, so drop them.
        private readonly bool _cacheEnabled;
        // Reused scratch for the per-frame reconcile — loaded keys filtered to sources with symbol layers. Never
        // reallocated in steady state.
        private readonly List<SymbolTileStore.Key> _reconcileKeys = new();
        private int _lastUploadedGlyphCount;
        private bool _loggedOverflow;
        private bool _loggedSkip;

        // The worker→main handoff: the kick task (pool) enqueues, PumpBuilds (main) drains into _readyTails. A
        // ConcurrentQueue is carrier, ordering, and safe-publication barrier in one.
        internal readonly ConcurrentQueue<ReadySymbolTail> _handoffQueue = new();

        /// <summary>Parked builds awaiting the sprite fetch to settle; <c>PumpBuilds</c> drains it once
        /// <c>SpritesSettled</c>. Same safe-publication carrier as <c>_handoffQueue</c>.</summary>
        internal readonly ConcurrentQueue<PendingSymbolBuild> _pendingSpriteQueue = new();

        // The park gate — makes TryParkBuild's ct-check + Acquire + Enqueue exclusive with
        // DrainAndDiscardParkedBuilds, so a park can't land in the window between a cancel and its drain.
        private readonly object _parkGate = new();

        // Worker-phase-complete builds awaiting their budgeted tail (main-thread only). PumpBuilds' tail-start
        // loop drains this FIFO at most MaxBuildsPerFrame per frame.
        internal readonly List<ReadySymbolTail> _readyTails = new();

        // A SymbolTileBuffer free-list — main-thread only. Builds interleave, so each in-flight build RENTS its
        // own instance. A never-returned buffer (dropped on cancel/restyle) just degrades to a fresh alloc next
        // time — not chased down every drop path, since a still-referenced buffer must never be recycled.
        private readonly Stack<SymbolTileBuffer> _bufferPool = new();

        // A per-build glyph-range-request list free-list — main-thread only, same idiom as _bufferPool. A
        // build's range list must survive its own await (RunTailAsync's EnsureGlyphRangesAsync suspension), so
        // it can't be a shared field like _rangeSeen below — but a per-tile `new List` would be a data-plane
        // allocation, so it is pooled instead.
        private readonly Stack<List<(string FontName, int RangeStart)>> _rangeListPool = new();

        // The build-wide glyph-range dedup set for RunTailAsync's collect step. SAFE to share as a single field
        // (unlike _rangeListPool's per-build lists): the whole collect runs synchronously on main with no
        // suspension inside it, so two builds' collects can never interleave — one build's Clear()+fill+read
        // always completes before the next tail's collect starts.
        private readonly HashSet<(string FontName, int RangeStart)> _rangeSeen = new();

        // Cancels in-flight builds on restyle/teardown so a resumed build never touches disposed glyph/atlas
        // state. Recreated per SetStyle so each style has its own cancellation scope.
        private CancellationTokenSource _buildCts = new();

        /// <summary>The execution policy both off-main dispatch sites in this class go through — the parked-
        /// build drain (<c>PumpBuilds</c>) and the cross-tile reconcile (<c>ScheduleReconcileIfDirty</c>):
        /// ThreadPool on desktop/editor, Inline on a WebGL player, where no worker ever picks a dispatch up
        /// (docs/web-target.md). Settable so a test can force Inline and exercise the web-correct path
        /// on desktop. Mirrors <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.WorkScheduler"/>.
        /// <para>Rejects a <see cref="IWorkScheduler.RunsInline"/> policy while
        /// <see cref="SymbolReconciler.GateForTest"/> is armed — under Inline,
        /// <c>ScheduleReconcileIfDirty</c>'s dispatch would park on that gate on the CALLING thread (the body
        /// runs inline), and only the test's OWN later statement could release it — a statement that cannot
        /// run until this call returns. The symmetric order (arming the gate while already Inline) is not
        /// guarded here — see <see cref="SymbolReconciler.GateForTest"/>'s own doc.</para></summary>
        internal IWorkScheduler WorkScheduler
        {
            get => _workScheduler;
            set
            {
                if (value != null && value.RunsInline && _reconciler.GateForTest != null)
                    throw new InvalidOperationException(
                        "WorkScheduler: cannot select a RunsInline policy while SymbolReconciler.GateForTest " +
                        "is armed — ScheduleReconcileIfDirty's dispatch would then park on that gate on the " +
                        "CALLING (main) thread, with no release available from inside the same call — a " +
                        "guaranteed deadlock.");
                _workScheduler = value;
            }
        }

        private IWorkScheduler _workScheduler = WorkSchedulerFactory.ForCurrentPlatform();

        // The per-frame winner plan — the placement source of truth, rebuilt (alloc-free) every frame.
        private readonly SymbolGatherPlan      _gatherPlan   = new SymbolGatherPlan();

        // The reconcile state machine: ONE worker in flight, its cross-tile dedup dispatched through
        // WorkScheduler — off the render thread on desktop/editor, inline-on-main on WebGL. Front/back
        // double-buffer — the FRONT is consumed every frame, the worker fills the BACK, a successful pickup
        // swaps by ref. The paired snapshots PIN each result's blocks so the store can't free a live one.
        internal readonly SymbolReconciler _reconciler = new SymbolReconciler();
        private SymbolReconcileResult _frontResult   = new SymbolReconcileResult();
        private SymbolReconcileResult _backResult    = new SymbolReconcileResult();
        // Monotonic front-set version, bumped on every change to _frontResult's CONTENT and stamped onto the
        // gather plan so it holds its pools across unchanged frames. NOT _store.CollectGeneration, which moves
        // 1-4 frames before the front swaps (apply-stale).
        private int _frontSetVersion;
        private SymbolSnapshot        _frontSnapshot = new SymbolSnapshot();
        private SymbolSnapshot        _backSnapshot  = new SymbolSnapshot();
        internal bool          _reconcileInFlight;
        // Store CollectGeneration the in-flight/last-scheduled reconcile captured at. -1 (≠ initial gen 0) is a
        // cold-start sentinel → schedule frame 1.
        private int            _reconcileScheduledGen = -1;
        private WorkHandle<bool> _reconcileHandle; // pollable across frames — no .Preserve() needed (see WorkHandle<T>)
        private CancellationToken _reconcileToken;
        private bool           _loggedReconcileFault; // log a worker fault ONCE (no per-frame spam)

        // Per-frame coverage-classify state over the FRONT result, all reused (alloc-free once warm). Classify is
        // per-block, fanned back to symbols through BlockId; it masks rather than compacts, moving nothing.
        private readonly List<byte> _planDecision = new List<byte>();   // per-symbol Keep/Fade/Drop
        private readonly List<long> _blockTileKeys = new List<long>();
        private readonly List<byte> _blockDecision = new List<byte>();
        // A coverage-fading tile is STILL ACTIVE (in cover, below the coverage threshold) — distinct from a
        // departing (unloaded) tile. The above-threshold sets PING-PONG (ref-swapped each call) so they self-bound.
        private HashSet<long> _coverageAbovePrev = new();
        private HashSet<long> _coverageAboveThisFrame = new();
        private readonly Dictionary<long, double> _coverageDepartingUntil = new();  // TileKey → fade-out deadline
        private readonly HashSet<long> _coverageFadingTiles = new();                // vestigial for Build; telemetry
        private readonly Dictionary<long, byte> _tileDecisions = new();       // classify each tile once/rebuild
        private readonly List<long> _coverageDepartingPurgeKeys = new();

        private SymbolStoreTelemetrySnapshot _telemetry;

        // ── Properties ────────────────────────────────────────────────
        /// <summary>Test seam for the clock <c>SpritesSettled</c>'s deadline reads. Production →
        /// Time.realtimeSinceStartup (UNSCALED — an HTTP fetch runs in real seconds regardless of timeScale).</summary>
        internal Func<double> NowSecondsOverride { get; set; }
        private double NowSeconds => (NowSecondsOverride ?? DefaultNowSeconds)();

        private bool SpritesSettled =>
            _spriteFetchTask.Status != UniTaskStatus.Pending ||
            NowSeconds - _spriteFetchStartedAtSeconds >= SpriteFetchDeadlineSeconds;

        // The material-slot clamp count shared by CurrentBatch and RunTailAsync's bake — ONE normalization so the
        // two paths ClampSlot the same bound. Zero layers → 1 (unreachable in practice, but the paths must agree).
        private int SlotCount => _allSymbolLayers.Count > 0 ? _allSymbolLayers.Count : 1;

        /// <summary>Max symbol-tile TAILS started per frame in <c>PumpBuilds</c> — the responsiveness
        /// knob for shaping, the main-thread burst. Worker-phase starts ride TileManager's kick cadence, not
        /// this knob.</summary>
        public int MaxBuildsPerFrame { get; set; } = 1;

        /// <summary>Test seam: the glyph-source factory <c>SetStyle</c> uses, overridable to inject a fixture
        /// source. Null ⇒ the production <see cref="GlyphSourceFactory.Create"/>.</summary>
        internal Func<StyleDocument, IGlyphSource> GlyphSourceFactoryOverride { get; set; }

        /// <summary>Test seam: the sprite-source factory <c>SetStyle</c> uses, overridable to inject a fixture
        /// source. Null ⇒ the production <see cref="SpriteSourceFactory.Create"/>.</summary>
        internal Func<StyleDocument, ISpriteSource> SpriteSourceFactoryOverride { get; set; }

        // Per-frame observability (mirrors TileManager's *LastTick counters) — read by tests, never the live path.
        internal int TailsStartedLastPump  { get; private set; }
        internal int AtlasUploadsLastPump  { get; private set; }
        internal int CancelledBuildCount   { get; private set; }
        internal int SkippedSymbolCount => _builder?.SkippedSymbolCount ?? 0;
        /// <summary>How many off-main reconciles <c>CurrentBatch</c> SCHEDULED — bumped once per dirty/cold
        /// frame, flat across clean frames. Test telemetry.</summary>
        internal int CollectRecomputeCount { get; private set; }

        /// <summary>Set true when a pickup's <c>GetResult()</c> rethrew a worker fault (i.e. the exception was
        /// OBSERVED, not swallowed). This class WRITES it, so it is state the subsystem produces.</summary>
        internal bool ReconcileFaultObserved { get; private set; }

        /// <summary>True once <c>SetStyle</c> found at least one symbol layer — MapView places symbols
        /// only when true (a style with no symbol layers has nothing to place).</summary>
        public bool HasSymbolLayers => _layersBySource != null && _layersBySource.Count > 0;

        /// <summary>The shared SDF atlas texture backing every collected symbol's UVs (null before the first
        /// glyphs upload).</summary>
        public GlyphAtlasTexture Atlas => _atlasTexture;

        /// <summary>The sprite sheet texture backing every ICON symbol's UVs (null until the sprite fetch
        /// resolves, or forever on a style with no <c>sprite</c> URL / no symbol layers).</summary>
        public Texture2D IconTexture => _spriteSheet?.Texture;

        /// <summary>The parsed sprite index + sheet dimensions used to resolve <c>icon-image</c> names. Null until
        /// the sprite fetch resolves; a build kicked before then PARKS rather than committing icon-starved.</summary>
        public SpriteAtlasView SpriteAtlas => _spriteAtlas;

        /// <summary>Active (in-cover) symbol-tile count — telemetry.</summary>
        public int ActiveTileCount => _store.ActiveTileCount;

        /// <summary>Cached (out-of-cover, kept-warm) symbol-tile count — telemetry (the symbols held so a
        /// prepared-cache hit re-shows them without a re-fetch).</summary>
        public int CachedTileCount => _store.CachedTileCount;

        /// <summary>This provider's per-frame levels, by reference, refreshed at the end of <c>CurrentBatch</c>.
        /// A frame with no symbol layers keeps the last values ("did not run" is not "ran and found zero").</summary>
        internal ref readonly SymbolStoreTelemetrySnapshot Telemetry => ref _telemetry;

        /// <summary>Departing (left cover, still fading out within the grace window) symbol-tile count — telemetry.</summary>
        public int DepartingTileCount => _store.DepartingTileCount;

        // ── Constructor ───────────────────────────────────────────────
        /// <param name="preparedCacheMaxCount">The <c>PreparedTileCache</c>'s entry cap — bounds how many
        /// out-of-cover tiles' symbols are kept warm (clamped to a finite hard cap inside the store even when
        /// this is 0/unbounded).</param>
        /// <param name="cacheEnabled">The prepared mesh cache's master toggle — see <c>_cacheEnabled</c>.</param>
        public SymbolSubsystem(MapCamera camera, int preparedCacheMaxCount = 0, bool cacheEnabled = true)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _cacheEnabled = cacheEnabled;
            _store = new SymbolTileStore(preparedCacheMaxCount);
        }

        // ── Methods ───────────────────────────────────────────────────
        /// <summary>Production clock <c>SpritesSettled</c>'s deadline reads (see <c>NowSecondsOverride</c>).</summary>
        private static double DefaultNowSeconds() => Time.realtimeSinceStartup;

        /// <summary>Rents a build's scratch buffer from <c>_bufferPool</c> (main-thread only), or makes a fresh one.</summary>
        private SymbolTileBuffer RentBuffer() => _bufferPool.Count > 0 ? _bufferPool.Pop() : new SymbolTileBuffer();

        /// <summary>Clears and returns a scratch buffer to <c>_bufferPool</c>; null is ignored.</summary>
        /// <param name="buffer">The buffer to recycle — must not be referenced by any in-flight build.</param>
        private void ReturnBuffer(SymbolTileBuffer buffer)
        {
            if (buffer == null) return;
            buffer.Clear();
            _bufferPool.Push(buffer);
        }

        /// <summary>Rents a build's glyph-range-request scratch list from <c>_rangeListPool</c> (main-thread
        /// only), or makes a fresh one.</summary>
        private List<(string FontName, int RangeStart)> RentRangeList() =>
            _rangeListPool.Count > 0 ? _rangeListPool.Pop() : new List<(string FontName, int RangeStart)>();

        /// <summary>Clears and returns a glyph-range-request scratch list to <c>_rangeListPool</c>.</summary>
        private void ReturnRangeList(List<(string FontName, int RangeStart)> list)
        {
            list.Clear();
            _rangeListPool.Push(list);
        }

        /// <summary>Snapshots this frame's store levels into <c>_telemetry</c> at the end of <c>CurrentBatch</c>.</summary>
        private void RefreshTelemetry() =>
            _telemetry = new SymbolStoreTelemetrySnapshot
            {
                ActiveSymbolTiles      = ActiveTileCount,
                CachedSymbolTiles      = CachedTileCount,
                CoverageDroppedSymbols = LastTileCoverageCulledCount,
            };

        /// <summary>Rebuild for a new style: group its symbol layers by source and (re)create the shared glyph
        /// pipeline from the style's <c>glyphs</c> URL. Idempotent — safe to call on every restyle.
        /// <paramref name="symbolLayers"/> arrives in declared order (a 1:1 ordinal mapping downstream).</summary>
        public void SetStyle(StyleDocument style, IReadOnlyList<SymbolStyle.StyleLayer> symbolLayers)
        {
            // A restyle inline-drains the reconcile pipeline BEFORE clearing the store, so a stale worker can
            // never write the back buffer afterward: cancel, block the in-flight worker to terminal, release both
            // snapshot pins, Clear, reset buffers. Ordering between the ReleasePins pair and store.Clear() is no
            // longer load-bearing (SharedDisposable): either order reaches the same single 0-transition.
            _buildCts.Cancel();
            DrainInFlightReconcile();
            _store.ReleasePins(_frontSnapshot);
            _store.ReleasePins(_backSnapshot);
            _store.Clear();
            _frontResult.Clear();   _backResult.Clear();
            _frontSetVersion++; // every front-content change bumps, so the gather memo is invalidated
            _reconcileScheduledGen = -1;

            _lastUploadedGlyphCount = 0;
            _loggedOverflow = false;
            _loggedSkip = false;
            // Open a fresh cancellation scope, then drop everything mid-flight: the pool→main handoff queue and
            // the ready-but-untailed builds, whose reserved store slot died with the _store.Clear() above.
            _buildCts.Dispose();
            _buildCts = new CancellationTokenSource();
            while (_handoffQueue.TryDequeue(out _)) { } // BCL ConcurrentQueue<T> has no Clear()
            DrainAndDiscardParkedBuilds(); // parked builds die with the old style scope
            _readyTails.Clear();
            DisposePipeline();

            // Drop the previous style's sheet before fetching the new one — unconditional (every restyle, even
            // to a style with no symbol layers, must release the GPU texture).
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
            // Kick off the sprite-sheet fetch (fire-and-forget, this style's _buildCts scope). BEFORE the
            // no-symbol-layers early return: fill-pattern layers resolve against the same sheet. .Preserve()'d so
            // SpritesSettled can poll its status. Stamp the deadline clock HERE, when the fetch actually starts.
            _spriteFetchStartedAtSeconds = NowSeconds;
            _spriteFetchTask = FetchSpriteSheetAsync(style, _buildCts.Token).Preserve();

            if (_layersBySource.Count == 0) return; // no symbol layers — stay idle (demo seam still works)

            // The glyph pipeline needs the style's glyphs URL — without it, leave _builder null (TryBeginBuild
            // then no-ops) rather than throw; the symbols just don't render.
            if (string.IsNullOrEmpty(style.Glyphs))
            {
                Debug.LogWarning("[SymbolSubsystem] style has no 'glyphs' URL — symbol labels will not render.");
                return;
            }
            int dim = Math.Min(SystemInfo.maxTextureSize, AtlasDimension);
            IGlyphSource glyphSource = (GlyphSourceFactoryOverride ?? GlyphSourceFactory.Create)(style);
            _glyphManager = new GlyphManager(glyphSource, new GlyphAtlas(dim, dim));
            _atlasTexture = new GlyphAtlasTexture();
            _builder = new StyledSymbolTileBuilder(_glyphManager, _store.StringTable); // shared table so cross-tile ids match
        }

        /// <summary><see cref="Processing.ISymbolTileWorkerFactory"/> entry — MAIN THREAD, from TileManager's
        /// prepared-cache probe. Mirrors <see cref="TryBeginBuild"/>'s two participation guards, so the pair
        /// can never disagree about whether this source produces labels at all.</summary>
        public bool SymbolsCachedFor(string sourceId, TileId tile)
        {
            if (_builder == null) return true;                              // no glyph pipeline — no labels to lose
            if (!_layersBySource.TryGetValue(sourceId, out _)) return true;  // mesh-only source
            return _store.HasCommittedBlock(new SymbolTileStore.Key(sourceId, tile));
        }

        /// <summary><see cref="Processing.ISymbolTileWorkerFactory"/> entry — MAIN THREAD, from TileManager's
        /// per-tile kick: begin a symbol build for this <paramref name="sourceId"/>/<paramref name="tile"/> if it
        /// has symbol layers and the glyph pipeline is live; null otherwise. Captures the camera zoom/projection
        /// at the kick.</summary>
        public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
        {
            if (_builder == null) return null;
            if (!_layersBySource.TryGetValue(sourceId, out List<int> layerIndices)) return null;

            var key = new SymbolTileStore.Key(sourceId, tile);
            int gen = _store.BeginBuild(key); // reserve the active slot (collected as empty until committed)

            // Capture the main-thread inputs BEFORE the pool-side worker step (Unity APIs are main-thread only):
            // this build's builder, the camera zoom + projection, and one processor per style layer.
            StyledSymbolTileBuilder builder    = _builder;
            double                  zoom       = _camera.CurrentProperties.Zoom;
            var                     projection = _camera.Projection;

            // Read _spriteAtlas + readiness HERE, at kick time — same capture-before-worker rule as zoom/projection.
            SpriteAtlasView spriteAtlas     = _spriteAtlas;
            bool            spritesSettled  = SpritesSettled;

            // Rented from the pool rather than `new`, one instance per in-flight build (builds interleave).
            // Returned to the pool in RunTailAsync's finally, once Bake has consumed it.
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
                return new SymbolTileWorkerPass(key, gen, processors, buffer, context, _buildCts.Token, this);
            }

            // NOT settled — park: the atlas is a processor ctor arg, so carry the raw layerIndices and build them
            // once PumpBuilds' pending drain sees SpritesSettled.
            return new SymbolTileWorkerPass(key, gen, layerIndices, buffer, context, _buildCts.Token, this);
        }

        /// <summary><see cref="Processing.ISymbolTileWorkerPass"/> implementor — the captured build state
        /// carried from <c>TryBeginBuild</c> (main) to <c>RunWorkerAndHandoff</c> (pool, inside
        /// TileManager's kick task). Infallible from the caller's view: owns its own try/catch, never
        /// rethrows.</summary>
        private sealed class SymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            private readonly SymbolTileStore.Key         _key;
            private readonly int                              _generation;
            private readonly TileSymbolLayerProcessor[]       _processors;  // null in park mode
            private readonly SymbolTileBuffer               _buffer;
            private readonly TileLayerProcessContext           _context;
            private readonly CancellationToken                _ct;
            // The run+enqueue lives on the OWNER (RunSymbolWorkerAndHandoff), shared with PumpBuilds' pending
            // drain — this class just delegates. A structure test pins that call to one site.
            private readonly SymbolSubsystem             _owner;
            // Park mode: the atlas-dependent processors couldn't be built at kick, so PumpBuilds' pending drain
            // finishes the job once settled.
            private readonly bool       _parked;
            private readonly List<int>  _layerIndices; // park mode only

            /// <summary>Normal-mode ctor: processors already built at kick.</summary>
            public SymbolTileWorkerPass(SymbolTileStore.Key key, int generation, TileSymbolLayerProcessor[] processors,
                SymbolTileBuffer buffer, TileLayerProcessContext context, CancellationToken ct,
                SymbolSubsystem owner)
            {
                _key = key; _generation = generation; _processors = processors; _buffer = buffer;
                _context = context; _ct = ct; _owner = owner; _parked = false;
            }

            /// <summary>Park-mode ctor — see the field docs above. Carries <c>owner</c> rather than the queue
            /// directly, since <c>TryParkBuild</c> is the one gated site.</summary>
            public SymbolTileWorkerPass(SymbolTileStore.Key key, int generation, List<int> layerIndices,
                SymbolTileBuffer buffer, TileLayerProcessContext context, CancellationToken ct,
                SymbolSubsystem owner)
            {
                _key = key; _generation = generation; _buffer = buffer;
                _context = context; _ct = ct; _owner = owner;
                _layerIndices = layerIndices; _parked = true;
            }

            /// <summary>POOL THREAD (inside TileManager's kick task): runs this build's worker phase, or in park
            /// mode defers it via <c>TryParkBuild</c> (which takes its own decode reference so the tile survives
            /// the park window). Infallible: own try/catch, never rethrows.</summary>
            public void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)
            {
                try
                {
                    if (_ct.IsCancellationRequested) return;
                    if (_parked)
                    {
                        // TryParkBuild is the one gated site (ct-check + Acquire + Enqueue under the drain's lock).
                        // A refusal takes no reference, so there is nothing to release here.
                        _owner.TryParkBuild(_key, _generation, _layerIndices, _buffer, _context, decode, _ct,
                            _key.SourceId, _key.Tile);
                        return;
                    }
                    _owner.RunSymbolWorkerAndHandoff(decode, in _context, _processors, _key, _generation, _buffer,
                        _ct, _key.SourceId, _key.Tile);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SymbolSubsystem] label build failed for tile {_key.Tile} (source '{_key.SourceId}'): {ex.Message}");
                }
            }
        }

        /// <summary>Runs one build's worker phase and hands the result to <c>PumpBuilds</c> — off-main on
        /// desktop/editor, inline-on-main on WebGL under either caller's scheduler (<c>TileManager.WorkScheduler</c>
        /// for the un-parked kick path below, <see cref="WorkScheduler"/> for the parked pending-drain in
        /// <c>PumpBuilds</c>). The SOLE call site of <c>TileLayerProcessorRunner.RunSymbolWorkerPass</c>
        /// (structure-test-pinned), shared by both callers so they run the SAME code.</summary>
        private void RunSymbolWorkerAndHandoff(
            SharedDisposable<IDecodedTile> decode, in TileLayerProcessContext context, TileSymbolLayerProcessor[] processors,
            SymbolTileStore.Key key, int generation, SymbolTileBuffer buffer, CancellationToken ct,
            string sourceId, TileId tile)
        {
            try
            {
                using (PmSymbolExtract.Auto())
                    TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, processors);
                _handoffQueue.Enqueue(new ReadySymbolTail
                {
                    Key = key, Generation = generation, Processors = processors, Buffer = buffer,
                    Ct = ct, TileOriginRender = context.TileOriginRender,
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolSubsystem] label build failed for tile {tile} (source '{sourceId}'): {ex.Message}");
            }
        }

        /// <summary>The ONE gated site for parking a build, made ATOMIC under <c>lock (_parkGate)</c>: refuse
        /// (acquire nothing) if <paramref name="ct"/> is already cancelled, else <c>Acquire</c> + enqueue. A
        /// canceller cancels BEFORE draining under the same lock, so a park racing a cancel refuses before it
        /// holds a reference the drain swept past.</summary>
        /// <param name="decode">BORROWED; a successful park takes its OWN reference, never releasing the caller's.</param>
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
                    _pendingSpriteQueue.Enqueue(new PendingSymbolBuild
                    {
                        Key = key, Generation = generation, LayerIndices = layerIndices, Buffer = buffer,
                        Context = context, Decode = decode, Ct = ct, SourceId = sourceId, Tile = tile,
                    });
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

        /// <summary>The SINGLE purge funnel for the parked pending-build queue — dequeues every entry and releases
        /// its reference. Called from <c>SetStyle</c>/<c>DoDispose</c>; <c>PumpBuilds</c>' live drain CONSUMES
        /// entries instead.</summary>
        private void DrainAndDiscardParkedBuilds()
        {
            // Each entry holds a decode reference (taken at park time) — dropping it un-disposed leaks Persistent
            // buffers. Same _parkGate as TryParkBuild, so acquire+enqueue can't interleave here.
            lock (_parkGate)
            {
                while (_pendingSpriteQueue.TryDequeue(out PendingSymbolBuild dropped)) // no Clear() on ConcurrentQueue<T>
                    dropped.Decode.Release();
            }
        }

        /// <summary>MAIN THREAD, once per frame: advances the symbol build pipeline and coalesces glyph-atlas
        /// uploads into a single blit. <c>MaxBuildsPerFrame</c> caps how many build tails start per frame, so a
        /// burst of ready tiles trickles out over several pumps (an appearance-latency tradeoff, not a content
        /// change).</summary>
        public void PumpBuilds()
        {
            TailsStartedLastPump  = 0;
            AtlasUploadsLastPump  = 0;
            if (_builder == null) return; // no glyph pipeline (style has no 'glyphs' URL) — nothing to do

            // Drain the pool→main handoff FIRST — every completed worker phase lands in _readyTails here (or is
            // dropped, if its build was cancelled mid-flight).
            while (_handoffQueue.TryDequeue(out ReadySymbolTail ready))
            {
                if (ready.Ct.IsCancellationRequested) { CancelledBuildCount++; continue; }
                _readyTails.Add(ready);
            }

            // Drain the parked queue once the sprite fetch settled — build each entry's processors with the live
            // _spriteAtlas and dispatch through WorkScheduler as the kick would. Ungated: main-thread-only and
            // never cancels, so it has no acquire window to guard.
            if (SpritesSettled)
            {
                while (_pendingSpriteQueue.TryDequeue(out PendingSymbolBuild pending))
                {
                    if (pending.Ct.IsCancellationRequested)
                    {
                        // Leaves the queue here and reaches no dispatch, so release its reference HERE or nowhere.
                        pending.Decode.Release();
                        CancelledBuildCount++;
                        continue;
                    }
                    PendingSymbolBuild captured = pending;
                    // OWNERSHIP GUARD over the dequeue→worker-start window: a throw before Schedule accepts the
                    // body would strand the last reference. `handedToWorker` makes the two release mouths below
                    // mutually exclusive (SharedDisposable has no per-acquire token, so releasing both is a
                    // double-release).
                    bool handedToWorker = false;
                    try
                    {
                        // Reads _builder/_allSymbolLayers LIVE — safe because the ct check dropped stale-scope
                        // entries and SetStyle/DoDispose drain this queue BEFORE nulling _builder.
                        var processors = new TileSymbolLayerProcessor[captured.LayerIndices.Count];
                        for (int k = 0; k < captured.LayerIndices.Count; k++)
                        {
                            int globalIndex = captured.LayerIndices[k];
                            processors[k] = new TileSymbolLayerProcessor(_builder, _allSymbolLayers[globalIndex], globalIndex,
                                captured.Buffer, _spriteAtlas);
                        }
                        // The in-lambda ct check below is the guard, not the `captured.Ct` argument passed to
                        // Schedule: unlike UniTask.RunOnThreadPool's cancellationToken:, IWorkScheduler.Schedule
                        // never skips the body based on its token (poll, not push — IWorkScheduler.cs) — a
                        // skipped body is exactly what would leak this reference, since the body is the only
                        // release. The check stays inside the try so `finally` always runs either way.
                        WorkScheduler.Schedule(_ =>
                            {
                                try
                                {
                                    if (captured.Ct.IsCancellationRequested) return true;
                                    // This reference held the decode alive since park time, so this reads the SAME
                                    // IDecodedTile the mesh pass read — no second decode.
                                    // in-param needs an addressable local: a { get; init; } getter returns a value,
                                    // so captured.Context can't be passed by ref directly (unlike a field).
                                    var capturedContext = captured.Context;
                                    RunSymbolWorkerAndHandoff(captured.Decode, in capturedContext, processors,
                                        captured.Key, captured.Generation, captured.Buffer, captured.Ct, captured.SourceId, captured.Tile);
                                    return true;
                                }
                                finally
                                {
                                    captured.Decode.Release(); // the consuming mouth
                                }
                            },
                            captured.Ct);
                        handedToWorker = true;
                    }
                    finally
                    {
                        // pre-handoff mouth: reached only when the entry never got a worker.
                        if (!handedToWorker) captured.Decode.Release();
                    }
                }
            }

            // Start ≤ MaxBuildsPerFrame tails whose worker phase landed (FIFO). No stale/loaded re-check — the
            // store's superseded guard is the sole commit arbiter, so a released-to-cache tile still commits warm.
            while (_readyTails.Count > 0 && TailsStartedLastPump < MaxBuildsPerFrame)
            {
                ReadySymbolTail tail = _readyTails[0];
                _readyTails.RemoveAt(0);
                RunTailAsync(tail).Forget();
                TailsStartedLastPump++;
            }

            // ONE coalesced atlas upload per frame — any glyphs appended by builds that committed since the
            // last upload.
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

        /// <summary>PULL reconcile (MAIN THREAD, once per frame): reconcile the symbol store against the pipeline's
        /// loaded <c>(source, tile)</c> membership — release tiles that left cover (kept warm iff the mesh cache is
        /// enabled), restore kept-warm symbols for tiles re-entered via a cache hit. Only sources with symbol
        /// layers are forwarded.</summary>
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
            // Pass the departing grace so a tile leaving cover keeps its symbols collected (fading out) instead of
            // popping. Only when the mesh cache is enabled — otherwise symbols are dropped, nothing to fade.
            double grace = _cacheEnabled ? DepartingGraceSeconds : 0.0;
            _store.ReconcileActiveSet(_reconcileKeys, _cacheEnabled, nowSeconds, grace);
        }

        /// <summary>The budgeted main-thread TAIL for one build: collects every processor's required glyph
        /// ranges, ensures them (the build's ONE suspension point), shapes each layer, then commits the tile.
        /// The commit is gated behind the whole shape loop clearing its cancellation check first, so a
        /// cancelled or partial build never reaches <c>SymbolTileStore.CompleteBuild</c>.</summary>
        private async UniTaskVoid RunTailAsync(ReadySymbolTail tail)
        {
            List<(string FontName, int RangeStart)> ranges = RentRangeList();
            try
            {
                // The buffer returns to the pool in the finally — after the shape loop below has run and Bake
                // consumed it, never while a still-in-flight step might hold it.
                try
                {
                    // ONE dedup scope per BUILD — safe to share _rangeSeen across builds; see its field comment.
                    _rangeSeen.Clear();
                    for (int p = 0; p < tail.Processors.Length; p++)
                        tail.Processors[p].CollectRequiredRanges(ranges, _rangeSeen);
                    await _builder.EnsureGlyphRangesAsync(ranges, tail.Ct); // the ONE suspension point
                    tail.Ct.ThrowIfCancellationRequested(); // new guard for the new suspension point

                    for (int p = 0; p < tail.Processors.Length; p++)
                        tail.Processors[p].CompleteOnMain(tail.Ct);
                    tail.Ct.ThrowIfCancellationRequested(); // the existing partial-commit guard

                    // Bake this tile's native SoA block HERE, on the main thread — glyph quads / curved glyphs
                    // are only materialized by the layer shape above, so nothing is left to bake off-main.
                    var block = SymbolTileBlockBaker.Bake(tail.Buffer, SlotCount, tail.TileOriginRender);

                    // Commit — unless superseded or dropped mid-build (released-to-cache still commits, to the
                    // cached side). CompleteBuild disposes `block` on a superseded/dropped commit, so no leak.
                    _store.CompleteBuild(tail.Key, tail.Generation, block);
                }
                finally
                {
                    ReturnBuffer(tail.Buffer);
                }
            }
            catch (OperationCanceledException)
            {
                // Restyle/teardown mid-tail — silent, never touches disposed state. The reserved slot is
                // dropped when SetStyle/Dispose Clears the store.
                CancelledBuildCount++;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolSubsystem] label build failed for tile {tail.Tile} (source '{tail.SourceId}'): {ex.Message}");
            }
            finally
            {
                ReturnRangeList(ranges);
            }
        }

        /// <summary>Fetch + decode this style's sprite sheet (index JSON + PNG), fire-and-forget from
        /// <c>SetStyle</c>. A null source or absent response (404/204) leaves <c>_spriteSheet</c>/<c>_spriteAtlas</c>
        /// null — inert (every icon path is null-guarded). Cancelled via <paramref name="ct"/> (this style's
        /// <c>_buildCts</c> scope).</summary>
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

                // Texture2D construction (inside the SpriteSheet ctor) is a main-thread-only Unity API — guard
                // even though FetchAsync's continuation typically already resumes on main.
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

        /// <summary>Tile-coverage pre-cull: symbols dropped from the LAST <c>CurrentBatch</c> because their
        /// tile covered less than <paramref name="minCoverage"/>'s worth of the screen (never entered the SoA build,
        /// gather, projection, or collision). Telemetry — mirrors <see cref="SymbolPlacementSystem.LastDistanceCulledCount"/>.</summary>
        internal int LastTileCoverageCulledCount { get; private set; }

        /// <summary>Builds this frame's winner plan (<see cref="SymbolGatherPlan"/>) for the placement system.
        /// Apply-stale: the plan can trail a tile event by 1–4 frames (its reconcile runs off-main), so a new
        /// tile's symbols appear a little late and a removed tile's linger. A tile whose on-screen coverage
        /// drops below <paramref name="minCoverage"/> fades out rather than popping.</summary>
        /// <param name="frame">The current scene frame — origin, rebase, for camera-relative culling.</param>
        /// <param name="minCoverage">Coverage threshold (<c>MapViewConfig.SymbolTileCoverageCull</c>);
        /// non-positive disables the cull.</param>
        /// <param name="now">Wall-clock time, for coverage-fade deadline bookkeeping.</param>
        public SymbolGatherPlan CurrentBatch(in SceneFrame frame, double minCoverage, double now = 0.0)
        {
            using (PmBatchCollect.Auto())
            {
                using (PmCollectDedup.Auto()) // pickup/schedule bookkeeping only; the dedup is off-main
                {
                    PickupCompletedReconcile();
                    ScheduleReconcileIfDirty();
                }

                // Classify over the FRONT result (a masking classify moves nothing): a Dropped tile's decision
                // rides the symbol, a Fading tile's are flagged for the gather to ease out. Runs EVERY frame.
                float4x4 viewProj = SymbolPlacementSystem.ViewProj(_camera.Camera);
                double2 viewportLogicalPx = _camera.ViewportLogicalPx;
                int culled;
                using (PmCollectClassify.Auto()) // the coverage-cull half — camera-dependent, per BLOCK
                {
                    // One tile key per ordered block so the classify runs per block, not per scattered symbol.
                    var orderedBlocks = _frontResult.OrderedBlocks;
                    _blockTileKeys.Clear();
                    if (_blockTileKeys.Capacity < orderedBlocks.Count) _blockTileKeys.Capacity = orderedBlocks.Count;
                    for (int b = 0; b < orderedBlocks.Count; b++)
                        _blockTileKeys.Add(orderedBlocks[b].TileKey);

                    SymbolTileCoverageFilter.ClassifyActive(_blockTileKeys, _frontResult.BlockId, _frontResult.IsDeparting,
                        _camera.Projection, frame.SceneOriginRender, viewProj, viewportLogicalPx, frame.Rebase, minCoverage,
                        _coverageAbovePrev, _coverageAboveThisFrame, _coverageDepartingUntil, _coverageFadingTiles,
                        now, DepartingGraceSeconds, _tileDecisions, _blockDecision, _planDecision, out culled);
                }
                LastTileCoverageCulledCount = culled;

                // Ping-pong the above-threshold sets (ref-swap, no realloc) and purge elapsed coverage-fade
                // deadlines. Order matters: the swap/purge happen AFTER ClassifyActive reads them.
                (_coverageAbovePrev, _coverageAboveThisFrame) = (_coverageAboveThisFrame, _coverageAbovePrev);
                PurgeExpiredCoverageDeadlines(now);
            }
            using (PmBatchSoA.Auto())
                _gatherPlan.Build(_frontResult.BlockId, _frontResult.LocalIndex,
                    _frontResult.IsDeparting, _planDecision, _frontResult.OrderedBlocks, _frontSetVersion);

            RefreshTelemetry();   // the store's levels are final for this frame
            return _gatherPlan;
        }

        /// <summary>Picks up a reconcile whose worker reached a TERMINAL status, swapping front/back only on
        /// success — a faulted/partial back never reaches the gather build (which assumes aligned lists). Either
        /// way the outgoing snapshot leaves service, so releasing the back snapshot's pins is uniformly
        /// correct.</summary>
        private void PickupCompletedReconcile()
        {
            if (!_reconcileInFlight || !_reconcileHandle.IsCompleted) return;
            bool ok = false;

            try { _reconcileHandle.GetResult(); ok = _reconcileHandle.IsSucceeded; }
            catch (Exception ex) // observe → no unobserved-exception; log once
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
                _frontSetVersion++; // the front's CONTENT changed — invalidate the gather memo
                _store.ReleasePins(_backSnapshot); // the demoted old front leaves service
            }
            else
            {
                _store.ReleasePins(_backSnapshot); // the failed snapshot leaves service (NO swap) — Clear()s itself
            }
            _reconcileInFlight = false;
        }

        /// <summary>Schedules ONE reconcile when the store moved and no worker is in flight — captures (pins
        /// the back snapshot) on the main thread, then dispatches the dedup through <see cref="WorkScheduler"/>
        /// (the pool on desktop/editor, inline-on-main on WebGL), polled across frames.
        /// <para><c>SymbolReconciler.Run</c> takes no <see cref="CancellationToken"/> and cannot skip early —
        /// unlike the <c>UniTask.RunOnThreadPool(cancellationToken:)</c> this replaces, <see cref="IWorkScheduler.Schedule{T}"/>
        /// never skips the body based on its token (poll, not push — <see cref="IWorkScheduler"/>'s own
        /// contract), so a reconcile whose token is already cancelled by dispatch time still runs to
        /// completion instead of going Canceled. Benign: the result only ever feeds a swap through
        /// <see cref="PickupCompletedReconcile"/>, and a restyle/teardown that cancelled this token also
        /// drains and discards via <see cref="DrainInFlightReconcile"/> before that pickup could run.</para></summary>
        private void ScheduleReconcileIfDirty()
        {
            if (_reconcileInFlight || _store.CollectGeneration == _reconcileScheduledGen) return;
            _store.CaptureSnapshot(_backSnapshot); // main thread; pins the captured blocks
            _reconcileScheduledGen = _store.CollectGeneration;
            _reconcileToken = _buildCts.Token;
            CollectRecomputeCount++;
            _reconcileInFlight = true;
            // The closure reads the _back* FIELDS (captures only `this`, no locals) so the clean early-return
            // path above allocates nothing — capturing a local would alloc the closure at method entry, every frame.
            _reconcileHandle = WorkScheduler.Schedule(_ => { _reconciler.Run(_backSnapshot, _backResult); return true; },
                _reconcileToken);
        }

        /// <summary>Blocks the main thread until the in-flight reconcile worker is terminal — teardown/restyle
        /// has no future frame to poll <c>.Status</c> on, unlike <c>PickupCompletedReconcile</c>. The finite
        /// pure-CPU worker makes the bounded block acceptable.</summary>
        private void DrainInFlightReconcile()
        {
            if (!_reconcileInFlight) return;
            // WaitOffPlayerLoop, NOT GetResult() — the latter THROWS on a pending WorkHandle instead of waiting,
            // so the caller would free the snapshot's native columns under a still-running Run (UAF). Bridged
            // via ToUniTask(): under Inline the handle is already terminal, so this is a no-op short-circuit;
            // under ThreadPool it parks exactly as it did on the UniTask this replaces. Timeout ⇒ signal lost;
            // log, never hang. The guarded GetResult below then observes any fault.
            UniTask<bool> reconcileTask = _reconcileHandle.ToUniTask();
            if (!reconcileTask.WaitOffPlayerLoop(ReconcileDrainTimeoutMs))
                Debug.LogError("[SymbolSubsystem] reconcile drain timed out — the in-flight worker never completed; " +
                               "releasing pins anyway (the leak/UAF backstop was defeated).");
            if (_reconcileHandle.IsCompleted)
                try { _reconcileHandle.GetResult(); } catch { /* faulted/canceled — tearing down */ }
            _reconcileInFlight = false;
        }

        /// <summary>Test seam: runs <c>SetStyle</c>'s teardown core in isolation, off the main-thread glyph
        /// rebuild, so a test can drive it on a background thread and observe whether a still-parked reconcile
        /// worker's block is freed under it. Internal test-only, like <see cref="SymbolReconciler.GateForTest"/>.</summary>
        internal void TeardownReconcileForTest()
        {
            _buildCts.Cancel();
            DrainInFlightReconcile();
            _store.ReleasePins(_frontSnapshot);
            _store.ReleasePins(_backSnapshot);
            _store.Clear();
        }

        /// <summary>Drops coverage-fade deadlines whose grace window has elapsed. A purged tile stops being
        /// forced-fading; if it is still below threshold with no live deadline, <c>ClassifyActive</c> drops it
        /// outright — grace exceeds the fade, so by expiry it has already faded to invisible (never a pop).</summary>
        private void PurgeExpiredCoverageDeadlines(double now)
        {
            if (_coverageDepartingUntil.Count == 0) return;
            _coverageDepartingPurgeKeys.Clear();
            foreach (KeyValuePair<long, double> kv in _coverageDepartingUntil)
                if (now >= kv.Value) _coverageDepartingPurgeKeys.Add(kv.Key);
            for (int i = 0; i < _coverageDepartingPurgeKeys.Count; i++)
                _coverageDepartingUntil.Remove(_coverageDepartingPurgeKeys[i]);
        }

        /// <summary>Logs once if the glyph atlas overflowed (glyphs dropped) — suggesting a larger atlas.</summary>
        private void WarnOnAtlasOverflow()
        {
            if (_loggedOverflow || _glyphManager.Atlas.OverflowCount == 0) return;
            _loggedOverflow = true;
            Debug.LogWarning($"[SymbolSubsystem] glyph atlas full ({_glyphManager.Atlas.OverflowCount} glyph(s) " +
                             $"dropped) — increase AtlasDimension beyond {Math.Min(SystemInfo.maxTextureSize, AtlasDimension)}px.");
        }

        /// <summary>Logs once if any symbol builds were skipped after a shaping failure; the running total stays
        /// in <c>SkippedSymbolCount</c>.</summary>
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
            // Teardown runs the SAME inline-drain protocol as a restyle — cancel, drain to terminal, release both
            // snapshots' pins, THEN Clear (ordering between the two is not load-bearing — SharedDisposable
            // makes both idempotent). A test tearing down while GateForTest is held MUST open the gate first, or
            // DrainInFlightReconcile hangs.
            _buildCts.Cancel();   // stop any in-flight build + the reconcile token before glyph/atlas state is disposed
            DrainInFlightReconcile();
            _store.ReleasePins(_frontSnapshot);
            _store.ReleasePins(_backSnapshot);
            _buildCts.Dispose();
            while (_handoffQueue.TryDequeue(out _)) { } // BCL ConcurrentQueue<T> has no Clear()
            DrainAndDiscardParkedBuilds(); // parked builds die with teardown too
            _readyTails.Clear(); // ready-but-untailed builds die with the store slot cleared below
            _store.Clear();
            _frontResult.Clear();   _backResult.Clear();
            _frontSetVersion++; // decorative here (Build never runs again post-Dispose) — kept for uniformity
            _reconcileScheduledGen = -1;
            DisposePipeline();

            // The sprite sheet, disposed as a unit (mirrors _atlasTexture above).
            _spriteSheet?.Dispose();
            _spriteSheet = null;
            _spriteAtlas = null;

            _gatherPlan.Dispose(); // free the reused winner-plan's native lists (its blocks are borrowed)
        }

        /// <summary>Disposes the glyph pipeline (atlas texture, glyph manager, builder) and nulls it, leaving
        /// the subsystem cleanly idle when a style has no <c>glyphs</c> URL.</summary>
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
