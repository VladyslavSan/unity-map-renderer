// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.
// It uses NO Unity.Mathematics types, so there is no float2/double3 trap to avoid here.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Common;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S105 — the DECOUPLED production symbol-label subsystem: it owns the shared production
    /// <see cref="GlyphManager"/> + fixed-size <see cref="GlyphAtlasTexture"/> +
    /// <see cref="StyledSymbolTileBuilder"/>. A-1 split of concerns: label DATA arrives via the
    /// <see cref="Tile.TileManager"/>'s per-tile KICK (A5b: this class implements
    /// <see cref="ISymbolTileWorkerFactory"/> — TileManager's kick drives
    /// <see cref="TryBeginBuild"/> alongside the mesh pass, sharing the A4 shared-decode entry, no separate
    /// push/queue), while the tile LIFECYCLE is PULLED — each frame
    /// <see cref="ReconcileLoadedTiles"/> takes TileManager's current loaded set and reconciles which labels are
    /// active/kept-warm (retiring the fragile release/restore push-callbacks). Labels are the placed-every-frame
    /// class, so this feeds <see cref="LabelPlacementSystem"/> via <see cref="CollectInto"/>, never the static
    /// tile-render backend (S20 T5).
    ///
    /// <para><b>Fixed atlas.</b> The glyph atlas is allocated big and FIXED (<see cref="AtlasDimension"/>,
    /// clamped to the GPU max) so its <c>Size</c> never changes as tiles append glyphs — a growing atlas
    /// would invalidate earlier tiles' baked UVs (glyph-atlas-uv-growth-staleness lesson). Overflow (a
    /// glyph set larger than the fixed atlas) degrades gracefully and is logged, never silent.</para>
    ///
    /// <para><b>A5a: worker phase / tail split.</b> A build no longer runs start-to-commit as one
    /// coroutine — the worker phase (<see cref="TryBeginBuild"/>'s returned <c>SymbolTileWorkerPass</c>)
    /// stops after the pool-side extract and hands a <see cref="ReadySymbolTail"/> to <see cref="PumpBuilds"/>'
    /// budgeted tail-start loop (<see cref="RunTailAsync"/>), which does the glyph-fetching per-layer
    /// shape + store commit.</para>
    ///
    /// <para><b>A5b: budget split.</b> Worker-phase starts now ride TileManager's kick cadence
    /// (<c>MaxMeshBuildsPerTick</c>) — <see cref="MaxBuildsPerFrame"/> gates ONLY tail starts, its real
    /// stall-#1 role, since shaping (not extraction) is the main-thread burst. Under burst, tail starts
    /// trickle at that budget — an accepted, bounded label-appearance-latency tradeoff, never a
    /// label-content change.</para>
    /// </summary>
    internal sealed class SymbolLabelSubsystem : ISymbolTileWorkerFactory, IDisposable
    {
        /// <summary>Target atlas edge in px, clamped to the GPU's max texture size. R8, so 4096² ≈ 16 MB.</summary>
        private const int AtlasDimension = 4096;

        private readonly MapCamera _camera;

        private GlyphManager _glyphManager;
        private GlyphAtlasTexture _atlasTexture;
        private StyledSymbolTileBuilder _builder;

        // I5b: the sprite sheet backing every icon label's UVs — a single pre-baked image per style (unlike
        // the glyph atlas's grow-and-append), fetched once in SetStyle and disposed on the next restyle/
        // teardown. Null until the fetch resolves (or forever, on a style with no `sprite` URL / no symbol
        // layers) — every icon draw path downstream (LabelPlacementSystem.Tick's spriteTexture param) is
        // guarded on IconTexture being non-null, so a still-loading or absent sheet is inert, not a fault.
        private SpriteSheet _spriteSheet;
        private SpriteAtlasView _spriteAtlas;

        // Flat symbol-layer list (index == LabelInstance.MaterialIndex) and a source id → its layers'
        // GLOBAL indices map (only sources with symbol layers are observed). D11/E2: per-layer MATERIALS
        // moved to SymbolRenderLayer (RenderLayerSet.Build owns cloning + halo bind) — this class only
        // needs the layer COUNT (CurrentBatch's slot count) and the layers' Source/id for build routing.
        private readonly List<SymbolStyle.StyleLayer> _allSymbolLayers = new();
        private Dictionary<string, List<int>> _layersBySource;

        // Per-tile build markers (Profiler window → "MapRenderer.Symbol"). Only the SYNCHRONOUS main-thread
        // stages are marked — the shaping BuildAsync is awaited (its wall-clock includes glyph-fetch
        // suspension, not CPU), so it is deliberately left unmarked to avoid polluting the timeline.
        // A4: brackets the RunSymbolWorkerPass call, which now reads the SHARED decode (get-or-decode) —
        // ~0 cost on the common "mesh decoded first, symbol reuses" order; the true symbol-cadence cost is
        // whatever's left after that (feature extract), not the decode itself.
        private static readonly ProfilerMarker PmTileDecode =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.TileDecode");
        private static readonly ProfilerMarker PmAtlasUpload =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.AtlasUpload");

        // The two halves of CurrentBatch (nest under MapView's MapRenderer.Symbol.BatchBuild): the A-3 cross-tile
        // dedup gather vs the LabelInstance→SoA build (+ tile-corner projection). Split so the profiler shows which
        // half of the per-frame batch rebuild dominates at high zoom (the ~19.5ms z14-15 main-thread hot spot).
        private static readonly ProfilerMarker PmBatchCollect =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.BatchBuild.Collect");
        private static readonly ProfilerMarker PmBatchSoA =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.BatchBuild.SoA");

        // Per-(source, tile) built labels, with the active/cached lifecycle that mirrors the tile MESH cache
        // (Model B) so labels survive a leave-cover → cache-hit → re-enter-cover round trip. Sized to the
        // prepared mesh cache's count cap so a cached tile's labels always outlive its meshes.
        private readonly SymbolTileLabelStore _store;
        // A-1: whether the prepared mesh cache is enabled — drives keep-warm-on-release. Enabled ⇒ a released
        // tile can return via a cache HIT (no re-fetch), so keep its labels warm to restore them; disabled ⇒
        // a revisit always re-fetches (→ rebuild), so keeping warm is pointless → drop on release.
        private readonly bool _cacheEnabled;
        // A-1: reused scratch for the per-frame reconcile — LoadedTileKey (source, tile) mapped to store keys,
        // filtered to sources that actually have symbol layers. Never reallocated in steady state.
        private readonly List<SymbolTileLabelStore.Key> _reconcileKeys = new();
        private int _lastUploadedGlyphCount;
        private bool _loggedOverflow;
        private bool _loggedSkip;

        // ── Stall #1 fix (Stage A) / A5b feed swap: coalesced atlas upload + budgeted tail pump ────────
        /// <summary>A5a: a worker-phase-complete symbol build awaiting its budgeted main-thread tail
        /// (<see cref="RunTailAsync"/>) — the per-layer shape + commit. Readonly fields + ctor: MapRenderer.Unity
        /// has no IsExternalInit polyfill, and this mirrors the local carrier idiom
        /// (<c>TileManager.LoadedKey</c>/<c>SourceKey</c>).</summary>
        private readonly struct ReadySymbolTail
        {
            public readonly SymbolTileLabelStore.Key   Key;
            public readonly int                        Generation;   // BeginBuild's gen — commit guard
            public readonly TileSymbolLayerProcessor[] Processors;   // extraction held inside each
            public readonly List<LabelInstance>        Labels;       // the build's shared output list
            public readonly CancellationToken          Ct;           // the build's style-scoped token
            public readonly string                     SourceId;     // for the failure log line
            public readonly TileId                     Tile;
            public ReadySymbolTail(SymbolTileLabelStore.Key key, int generation, TileSymbolLayerProcessor[] processors,
                List<LabelInstance> labels, CancellationToken ct, string sourceId, TileId tile)
            {
                Key = key; Generation = generation; Processors = processors; Labels = labels;
                Ct = ct; SourceId = sourceId; Tile = tile;
            }
        }

        // A5b: the worker→main thread-safe handoff — TileManager's kick task (POOL thread) enqueues here
        // (SymbolTileWorkerPass.RunWorkerAndHandoff, below); PumpBuilds (MAIN thread) drains it into
        // _readyTails as its first step, every frame. A ConcurrentQueue is the carrier + ordering +
        // safe-publication barrier in one — no volatile flag, no manual pending-list scan (design §Q2).
        private readonly ConcurrentQueue<ReadySymbolTail> _handoffQueue = new();

        // Worker-phase-complete builds awaiting their budgeted tail (main-thread only). PumpBuilds' tail-start
        // loop drains this FIFO at most MaxBuildsPerFrame per frame (§D4/§D6).
        private readonly List<ReadySymbolTail> _readyTails = new();
        // Cancels in-flight builds on restyle/teardown so a resumed build never touches disposed glyph/atlas
        // state (closes the missing-token + restyle-vs-in-flight-build risks from the review). Recreated per
        // SetStyle so each style has its own cancellation scope.
        private CancellationTokenSource _buildCts = new();

        /// <summary>Max symbol-tile TAILS started per frame in <see cref="PumpBuilds"/> — the responsiveness
        /// knob for stall #1 (shaping, the main-thread burst). Default 1 (locked design default); MapView may
        /// serialize it later. A5b: worker-phase starts no longer ride this knob — they ride TileManager's
        /// kick cadence (<c>MaxMeshBuildsPerTick</c>) instead (§Q4).</summary>
        public int MaxBuildsPerFrame { get; set; } = 1;

        /// <summary>Test seam (dependency-inversion, mirroring <c>IDataSource</c>): the glyph-source factory
        /// <see cref="SetStyle"/> uses, overridable so an EditMode test can inject a fixture/gated
        /// <c>TestGlyphSource</c> instead of the production web source. Null ⇒ the production
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
        internal int SkippedLabelCount => _builder?.SkippedLabelCount ?? 0;
        // A5b: total not-yet-tailed builds — queued in the pool→main handoff (not yet drained) PLUS drained
        // but not-yet-started tails. The migrated F-7 budget tooth asserts against this total (§Q2/E-2).
        internal int ReadyTailCount => _readyTails.Count + _handoffQueue.Count;

        /// <param name="preparedCacheMaxCount">The <c>PreparedTileCache</c>'s entry cap — bounds how many
        /// out-of-cover tiles' labels are kept warm (clamped to a finite hard cap inside the store even when
        /// this is 0/unbounded).</param>
        /// <param name="cacheEnabled">The prepared mesh cache's master toggle — see <see cref="_cacheEnabled"/>.</param>
        public SymbolLabelSubsystem(MapCamera camera, int preparedCacheMaxCount = 0, bool cacheEnabled = true)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _cacheEnabled = cacheEnabled;
            _store = new SymbolTileLabelStore(preparedCacheMaxCount);
        }

        /// <summary>True once <see cref="SetStyle"/> found at least one symbol layer — MapView prefers this
        /// subsystem over the demo <c>LabelInstances</c> seam only when true.</summary>
        public bool HasSymbolLayers => _layersBySource != null && _layersBySource.Count > 0;

        /// <summary>The shared SDF atlas texture backing every collected label's UVs (null before the first
        /// glyphs upload).</summary>
        public GlyphAtlasTexture Atlas => _atlasTexture;

        /// <summary>I5b: the sprite sheet texture backing every ICON label's UVs (null until the style's
        /// sprite fetch resolves, or forever on a style with no <c>sprite</c> URL / no symbol layers) — fed
        /// to <see cref="Text.Placement.LabelPlacementSystem.Tick"/>'s <c>spriteTexture</c> param by MapView.</summary>
        public Texture2D IconTexture => _spriteSheet?.Texture;

        /// <summary>I5b: the parsed sprite index + sheet dimensions <see cref="TileSymbolLayerProcessor"/>
        /// forwards to <c>SymbolFeatureExtractor.Extract</c> to resolve <c>icon-image</c> names. Null until the
        /// sprite fetch resolves — a tile kicked before then extracts no icon labels and self-heals on its
        /// next rebuild once this is set (the same null→real flip <c>TileSymbolLayerProcessor</c> documents).</summary>
        public SpriteAtlasView SpriteAtlas => _spriteAtlas;

        /// <summary>Active (in-cover) label-tile count — telemetry.</summary>
        public int ActiveTileCount => _store.ActiveTileCount;

        /// <summary>Cached (out-of-cover, kept-warm) label-tile count — telemetry (the labels held so a
        /// prepared-cache hit re-shows them without a re-fetch).</summary>
        public int CachedTileCount => _store.CachedTileCount;

        /// <summary>Departing (left cover, still fading out within the grace window) label-tile count — telemetry.</summary>
        public int DepartingTileCount => _store.DepartingTileCount;

        // Retain-as-departing: how long a tile's labels stay collected (fading out) after it leaves cover. Derived
        // from the fade duration + a small margin so the grace ALWAYS exceeds the fade — the store purges a departing
        // tile only after this window, by which point its labels have fully faded (a purge mid-fade would pop).
        internal const double DepartingGraceSeconds = LabelPlacementSystem.FadeDurationSeconds + 0.2;

        /// <summary>
        /// Rebuild for a new style: group its symbol layers by source and (re)create the shared glyph
        /// pipeline from the style's <c>glyphs</c> URL. Idempotent — safe to call on every restyle.
        ///
        /// <para>D10: <paramref name="symbolLayers"/> is the caller-derived list of symbol layers, already
        /// built by <see cref="MapRenderer.Unity.Rendering.Style.RenderLayerSet"/> in declared order (the
        /// single registry — <see cref="MapRenderer.Unity.Rendering.Style.RenderLayerFactory"/>) — this
        /// method no longer re-walks <paramref name="style"/>'s layers with its own type-check. <paramref
        /// name="style"/> stays a parameter for <c>style.Glyphs</c> (the glyph source factory).</para>
        /// </summary>
        public void SetStyle(StyleDocument style, IReadOnlyList<SymbolStyle.StyleLayer> symbolLayers)
        {
            _store.Clear();
            _lastUploadedGlyphCount = 0;
            _loggedOverflow = false;
            _loggedSkip = false;
            // Cancel any in-flight builds from the previous style and open a fresh cancellation scope, then
            // drop everything mid-flight (their layer-index lists belong to the old _layersBySource rebuilt
            // below): A5b's pool→main handoff queue (a kick task still running on the pool enqueues into the
            // FRESH queue below only if it re-reads _handoffQueue after this point — the drain-side ct-drop
            // in PumpBuilds is what actually closes that race, §Q2) and A5a's ready-but-untailed builds —
            // their reserved store slot dies with the _store.Clear() below anyway (the same fate as an
            // in-flight build cancelled mid-build).
            _buildCts.Cancel();
            _buildCts.Dispose();
            _buildCts = new CancellationTokenSource();
            while (_handoffQueue.TryDequeue(out _)) { } // BCL ConcurrentQueue<T> has no Clear()
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
            if (_layersBySource.Count == 0) return; // no symbol layers — stay idle (demo seam still works)

            // I5b: kick off the sprite-sheet fetch (fire-and-forget, cancelled via THIS style's _buildCts
            // scope like every other in-flight build). Independent of the `glyphs` URL check below — an
            // icon-only style has no `glyphs` but still needs its sprite sheet, so this must not be gated on it.
            FetchSpriteSheetAsync(style, _buildCts.Token).Forget();

            // D11/E2: per-layer materials (SymbolText clone + text-halo-* bind) are no longer built here —
            // they live on each SymbolRenderLayer, built by RenderLayerSet.Build from this SAME symbolLayers
            // list (in the same declared order, so the ordinal mapping stays 1:1). This class only needs the
            // glyph pipeline below.

            // The glyph pipeline needs the style's glyphs URL. Without it there are no glyphs to shape, so
            // leave _builder null (TryBeginBuild no-ops, returning null — no symbol participation) rather
            // than throw — the labels just don't render.
            if (string.IsNullOrEmpty(style.Glyphs))
            {
                Debug.LogWarning("[SymbolLabelSubsystem] style has no 'glyphs' URL — symbol labels will not render.");
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
        /// <para>This is the moved main-thread prologue that pre-A5b's <c>BuildTileAsync</c> ran at build
        /// start (BeginBuild + camera zoom/projection capture + one processor per layer): the zoom-capture
        /// MOMENT moves from the tail-pump call to the kick call (§Q3 — behaviour-preserving: camera
        /// properties are fixed for the whole frame, so the value is identical for any build kicked and
        /// pumped in the same frame; only the SAMPLE FRAME shifts, an accepted timing delta).</para></summary>
        public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
        {
            if (_builder == null) return null;
            if (!_layersBySource.TryGetValue(sourceId, out List<int> layerIndices)) return null;

            var key = new SymbolTileLabelStore.Key(sourceId, tile);
            int gen = _store.BeginBuild(key); // reserve the active slot (collected as empty until committed)

            // Capture the main-thread inputs BEFORE the pool-side worker step (Unity APIs are main-thread
            // only): this build's own builder, the camera zoom + projection, and one processor per style
            // layer, all sharing the same output list.
            StyledSymbolTileBuilder builder    = _builder;
            double                  zoom       = _camera.CurrentProperties.Zoom;
            var                     projection = _camera.Projection;

            // I5b: read _spriteAtlas HERE, at kick time (main thread) — the same "capture Unity-adjacent
            // inputs before the pool-side worker step" rule as zoom/projection above. Null if the sprite
            // fetch hasn't resolved yet; the processor forwards it as-is (see ExtractLayers's spriteAtlas
            // param) — a tile kicked before the fetch resolves extracts no icon labels this round and
            // self-heals on its next rebuild once this is set (TileSymbolLayerProcessor's doc).
            SpriteAtlasView spriteAtlas = _spriteAtlas;

            var labels = new List<LabelInstance>();
            var processors = new TileSymbolLayerProcessor[layerIndices.Count];
            for (int k = 0; k < layerIndices.Count; k++)
            {
                int globalIndex = layerIndices[k];
                processors[k] = new TileSymbolLayerProcessor(builder, _allSymbolLayers[globalIndex], globalIndex, labels, spriteAtlas);
            }

            var context = new TileLayerProcessContext
            {
                Tile             = tile,
                Zoom             = zoom,
                TileOriginRender = TileRenderOrigin.Project(tile, projection),
                Projection       = projection,
            };

            return new SymbolTileWorkerPass(key, gen, processors, labels, context, _buildCts.Token, sourceId, tile, _handoffQueue);
        }

        /// <summary>Epic A / A5b <see cref="Processing.ISymbolTileWorkerPass"/> implementor — the captured
        /// build state carried from <see cref="TryBeginBuild"/> (main) to <see cref="RunWorkerAndHandoff"/>
        /// (pool, inside TileManager's kick task). Infallible from the caller's view: owns its own
        /// try/catch, never rethrows (TileManager's kick lambda wraps the call too — belt-and-braces, §Q5).</summary>
        private sealed class SymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            private readonly SymbolTileLabelStore.Key         _key;
            private readonly int                              _generation;
            private readonly TileSymbolLayerProcessor[]       _processors;
            private readonly List<LabelInstance>              _labels;
            private readonly TileLayerProcessContext           _context;
            private readonly CancellationToken                _ct;
            private readonly string                           _sourceId;
            private readonly TileId                            _tile;
            private readonly ConcurrentQueue<ReadySymbolTail>  _handoffQueue;

            public SymbolTileWorkerPass(SymbolTileLabelStore.Key key, int generation, TileSymbolLayerProcessor[] processors,
                List<LabelInstance> labels, TileLayerProcessContext context, CancellationToken ct, string sourceId, TileId tile,
                ConcurrentQueue<ReadySymbolTail> handoffQueue)
            {
                _key = key; _generation = generation; _processors = processors; _labels = labels;
                _context = context; _ct = ct; _sourceId = sourceId; _tile = tile; _handoffQueue = handoffQueue;
            }

            /// <summary>POOL THREAD (inside TileManager's mesh kick task, after the mesh pass): run this
            /// build's symbol worker pass over the SAME shared decode, then enqueue the completed worker
            /// phase for <see cref="PumpBuilds"/>' main-thread drain. A cancelled token (restyle/teardown
            /// raced ahead of this pool task) is a cheap early-out — never enqueued, so a stale build never
            /// reaches the tail (mirrors the drain-side ct-drop, §Q2 belt-and-braces).</summary>
            public void RunWorkerAndHandoff(IDecodedTileHandle decode)
            {
                try
                {
                    if (_ct.IsCancellationRequested) return;
                    using (PmTileDecode.Auto())
                        TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in _context, _processors);
                    _handoffQueue.Enqueue(new ReadySymbolTail(_key, _generation, _processors, _labels, _ct, _sourceId, _tile));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SymbolLabelSubsystem] label build failed for tile {_tile} (source '{_sourceId}'): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// MAIN THREAD, once per frame from <see cref="Map.MapView"/>: first drain
        /// <see cref="_handoffQueue"/> (worker phases TileManager's kick completed on the pool since the
        /// last pump) into <see cref="_readyTails"/> — dropping any entry whose <c>ct</c> is already
        /// cancelled (a restyle/teardown raced ahead of the enqueue, §Q2; counted, never started) — then
        /// (A5a) start at most <see cref="MaxBuildsPerFrame"/> ready TAILS, then perform AT MOST ONE atlas
        /// GPU upload if the shared atlas grew since the last upload (coalescing every commit + glyph-range
        /// arrival that landed since last frame into one ≤16 MB blit).
        ///
        /// <para>A5b: worker-phase starts no longer happen here — they ride TileManager's per-tile kick
        /// (<see cref="TryBeginBuild"/>), paced by its own kick cadence. <see cref="MaxBuildsPerFrame"/> now
        /// gates ONLY tail starts — the knob's real stall-#1 role (§D4: shaping, not extraction, is the
        /// main-thread burst). Under a burst of K tiles becoming tail-ready around the same frame, the Kth
        /// tile's shaping starts ~K / <see cref="MaxBuildsPerFrame"/> pumps later — an ACCEPTED, bounded
        /// label-appearance-latency tradeoff (§D6/N1), never a label-content change.</para>
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

            // A5a: start ≤ MaxBuildsPerFrame tails whose worker phase has already landed (FIFO — index 0;
            // the backlog is single-digit deep in practice, no deque needed). Deliberately NO stale/loaded
            // re-check here — a tail runs unconditionally once its worker phase lands; the store's
            // generation/superseded guard is the sole commit arbiter, so a released-to-cache tile's labels
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
            WarnOnSkippedLabels();
        }

        /// <summary>
        /// A-1 PULL reconcile (MAIN THREAD, once per frame): given the tile pipeline's current loaded
        /// <c>(source, tile)</c> membership (from <see cref="TileManager.CollectLoadedTileKeys"/>), reconcile the
        /// label store — release tiles that left cover (kept warm iff the mesh cache is enabled), restore
        /// kept-warm labels for tiles that re-entered via a cache hit. Replaces the retired release/restore
        /// push-callbacks: self-healing (a membership change is corrected next frame) and reentrancy-free
        /// (nothing mutates mid-callback). Only keys for sources that actually have symbol layers are forwarded
        /// — a non-symbol source's tiles can never match a label entry, so they are filtered out here.
        /// </summary>
        public void ReconcileLoadedTiles(IReadOnlyList<LoadedTileKey> loaded, double nowSeconds = 0.0)
        {
            if (_layersBySource == null) return; // no style set yet
            _reconcileKeys.Clear();
            for (int i = 0; i < loaded.Count; i++)
            {
                LoadedTileKey k = loaded[i];
                if (_layersBySource.ContainsKey(k.SourceId))
                    _reconcileKeys.Add(new SymbolTileLabelStore.Key(k.SourceId, k.Tile));
            }
            // Pass the wall-clock (from MapView) + the departing grace window so a tile that leaves cover keeps its
            // labels COLLECTED (as departing) for the fade-out instead of popping. Grace applies only when the mesh
            // cache is enabled (labels are dropped, not kept warm, on release otherwise → nothing to fade).
            double grace = _cacheEnabled ? DepartingGraceSeconds : 0.0;
            _store.ReconcileActiveSet(_reconcileKeys, _cacheEnabled, nowSeconds, grace);
        }

        /// <summary>A5a: the budgeted main-thread TAIL, started by <see cref="PumpBuilds"/>' tail-start loop
        /// once <see cref="TryBeginBuild"/>'s returned <c>SymbolTileWorkerPass</c> has landed a
        /// <see cref="ReadySymbolTail"/> (via the A5b pool→main handoff, drained at the top of
        /// <see cref="PumpBuilds"/>). Every layer's <c>CompleteOnMainAsync</c> runs sequentially behind this
        /// ONE hop-in, and the commit is gated behind the WHOLE loop clearing its cancellation check FIRST —
        /// partial labels never reach <see cref="SymbolTileLabelStore.CompleteBuild"/> (the commit guard, §C).</summary>
        private async UniTaskVoid RunTailAsync(ReadySymbolTail tail)
        {
            try
            {
                for (int p = 0; p < tail.Processors.Length; p++)
                    await tail.Processors[p].CompleteOnMainAsync(tail.Ct);
                tail.Ct.ThrowIfCancellationRequested();

                // Commit — unless superseded by a newer build OR dropped mid-build (released-to-cache is NOT
                // stale: the store writes the labels to the cached side so a later hit restores them). The
                // atlas GPU upload is coalesced into the next PumpBuilds — no atlas touch here.
                _store.CompleteBuild(tail.Key, tail.Generation, tail.Labels);
            }
            catch (OperationCanceledException)
            {
                // Restyle/teardown mid-tail — silent, never touches disposed state (closes the review's two
                // latent lifetime risks). The reserved slot is dropped when SetStyle/Dispose Clears the store.
                CancelledBuildCount++;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolLabelSubsystem] label build failed for tile {tail.Tile} (source '{tail.SourceId}'): {ex.Message}");
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
        private async UniTaskVoid FetchSpriteSheetAsync(StyleDocument style, CancellationToken ct)
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
                Debug.LogWarning($"[SymbolLabelSubsystem] sprite sheet fetch failed: {ex.Message}");
            }
            finally
            {
                source?.Dispose();
            }
        }

        /// <summary>Aggregate every loaded tile's labels into <paramref name="output"/> for this frame's
        /// <see cref="LabelPlacementSystem.Tick"/> (which then projects/collides/billboards them). A-3: point
        /// labels are deduped across tiles at a grid of one logical pixel at the CURRENT display zoom
        /// (<see cref="CameraPoseMath.MetersPerPixel"/>) — so the same symbol from a parent + child tile during a
        /// zoom transition collapses to one, and the grid tracks zoom (a fixed grid cannot serve all zooms).</summary>
        public void CollectInto(List<LabelInstance> output)
            => _store.CollectInto(output, CameraPoseMath.MetersPerPixel(_camera.CurrentProperties.Zoom));

        // Lever C step 2: the blittable label batch — the per-frame placement source of truth. Rebuilt EVERY frame
        // from the current collected set. The version cache that skipped rebuilds on a stable collected set was
        // removed: once the B-1 static-frame skip was retired it had this single caller, and its store-version bump
        // discipline (a bump on every set-changing store mutation) was not worth the complexity. The rebuild is
        // allocation-free — both CollectInto and Build reuse their buffers — so the cost is CPU only.
        private readonly SymbolLabelBatch      _batch        = new SymbolLabelBatch();
        private readonly List<LabelInstance>   _batchCollect = new List<LabelInstance>();

        /// <summary>The blittable <see cref="SymbolLabelBatch"/> for this frame, rebuilt every frame from the current
        /// collected set: the A-3 cross-tile dedup (<see cref="SymbolTileLabelStore.CollectInto"/>) + the
        /// LabelInstance→SoA conversion (<see cref="SymbolLabelBatchBuilder.Build"/>). Both reuse their buffers, so
        /// the rebuild is allocation-free (CPU only). Tile-corner projection for the coverage pre-cull is
        /// camera-independent, so it is done here too (not per Tick).</summary>
        public SymbolLabelBatch CurrentBatch()
        {
            double quantize  = CameraPoseMath.MetersPerPixel(_camera.CurrentProperties.Zoom);
            int    slotCount = _allSymbolLayers.Count > 0 ? _allSymbolLayers.Count : 1;
            // CollectInto appends DEPARTING labels (tiles leaving cover, kept warm for a fade-out) after the active
            // ones and reports the split; the builder flags the departing records so the placement gather fades them
            // out instead of popping. Pass the projection so the batch stores each tile's render-space corners for
            // the per-frame tile-coverage pre-cull (camera-independent → built here).
            int activeCount;
            using (PmBatchCollect.Auto())
                _store.CollectInto(_batchCollect, quantize, out activeCount);
            using (PmBatchSoA.Auto())
                SymbolLabelBatchBuilder.Build(_batch, _batchCollect, slotCount, _camera.Projection, activeCount);
            return _batch;
        }

        private void WarnOnAtlasOverflow()
        {
            if (_loggedOverflow || _glyphManager.Atlas.OverflowCount == 0) return;
            _loggedOverflow = true;
            Debug.LogWarning($"[SymbolLabelSubsystem] glyph atlas full ({_glyphManager.Atlas.OverflowCount} glyph(s) " +
                             $"dropped) — increase AtlasDimension beyond {Math.Min(SystemInfo.maxTextureSize, AtlasDimension)}px.");
        }

        private void WarnOnSkippedLabels()
        {
            if (_loggedSkip || _builder == null || _builder.SkippedLabelCount == 0) return;
            _loggedSkip = true;
            Debug.LogWarning($"[SymbolLabelSubsystem] skipped {_builder.SkippedLabelCount} label(s) whose build " +
                             $"failed (first: {_builder.LastSkipReason}). Further skips suppressed; running total in " +
                             $"SkippedLabelCount telemetry.");
        }

        public void Dispose()
        {
            _buildCts.Cancel();   // stop any in-flight build before its glyph/atlas state is disposed below
            _buildCts.Dispose();
            while (_handoffQueue.TryDequeue(out _)) { } // BCL ConcurrentQueue<T> has no Clear()
            _readyTails.Clear(); // A5a: ready-but-untailed builds die with the store slot cleared below
            _store.Clear();
            DisposePipeline();

            // I5b: the sprite sheet, disposed as a unit (mirrors _atlasTexture above).
            _spriteSheet?.Dispose();
            _spriteSheet = null;
            _spriteAtlas = null;
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
