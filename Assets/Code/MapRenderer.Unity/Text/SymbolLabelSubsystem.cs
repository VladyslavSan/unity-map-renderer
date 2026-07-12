// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.
// It uses NO Unity.Mathematics types, so there is no float2/double3 trap to avoid here.

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Common;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S105 — the DECOUPLED production symbol-label subsystem: it owns the shared production
    /// <see cref="GlyphManager"/> + fixed-size <see cref="GlyphAtlasTexture"/> +
    /// <see cref="StyledSymbolTileBuilder"/>. A-1 split of concerns: label DATA arrives via the
    /// <see cref="Tile.TileManager"/> <c>SymbolTileBytesReady</c> push (already-fetched MVT bytes — no double
    /// download, never touching the mesh/disposal pipeline), while the tile LIFECYCLE is PULLED — each frame
    /// <see cref="ReconcileLoadedTiles"/> takes TileManager's current loaded set and reconciles which labels are
    /// active/kept-warm (retiring the fragile release/restore push-callbacks). Labels are the placed-every-frame
    /// class, so this feeds <see cref="LabelPlacementSystem"/> via <see cref="CollectInto"/>, never the static
    /// tile-render backend (S20 T5).
    ///
    /// <para><b>Fixed atlas.</b> The glyph atlas is allocated big and FIXED (<see cref="AtlasDimension"/>,
    /// clamped to the GPU max) so its <c>Size</c> never changes as tiles append glyphs — a growing atlas
    /// would invalidate earlier tiles' baked UVs (glyph-atlas-uv-growth-staleness lesson). Overflow (a
    /// glyph set larger than the fixed atlas) degrades gracefully and is logged, never silent.</para>
    /// </summary>
    internal sealed class SymbolLabelSubsystem : IDisposable
    {
        /// <summary>Target atlas edge in px, clamped to the GPU's max texture size. R8, so 4096² ≈ 16 MB.</summary>
        private const int AtlasDimension = 4096;

        private readonly MapCamera _camera;

        private GlyphManager _glyphManager;
        private GlyphAtlasTexture _atlasTexture;
        private StyledSymbolTileBuilder _builder;

        // Flat symbol-layer list (index == LabelInstance.MaterialIndex) and a source id → its layers'
        // GLOBAL indices map (only sources with symbol layers are observed). D11/E2: per-layer MATERIALS
        // moved to SymbolRenderLayer (RenderLayerSet.Build owns cloning + halo bind) — this class only
        // needs the layer COUNT (CurrentBatch's slot count) and the layers' Source/id for build routing.
        private readonly List<SymbolStyle.StyleLayer> _allSymbolLayers = new();
        private Dictionary<string, List<int>> _layersBySource;

        // Per-tile build markers (Profiler window → "MapRenderer.Symbol"). Only the SYNCHRONOUS main-thread
        // stages are marked — the shaping BuildAsync is awaited (its wall-clock includes glyph-fetch
        // suspension, not CPU), so it is deliberately left unmarked to avoid polluting the timeline.
        private static readonly ProfilerMarker PmTileDecode =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.TileDecode");
        private static readonly ProfilerMarker PmAtlasUpload =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.AtlasUpload");

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

        // ── Stall #1 fix (Stage A): bounded symbol-build queue + coalesced atlas upload ──────────────
        /// <summary>A queued deferred symbol build: the source's already-fetched MVT bytes and its layer
        /// indices (the live <see cref="_layersBySource"/> list, NOT copied — <see cref="SetStyle"/> clears
        /// the queue before rebuilding that map, so a queued entry never references a stale list). Readonly
        /// fields + ctor (not <c>init</c>): MapRenderer.Unity has no IsExternalInit polyfill, and this mirrors
        /// the local carrier idiom (<c>TileManager.LoadedKey</c>/<c>SourceKey</c>).</summary>
        private readonly struct PendingSymbolBuild
        {
            public readonly string    SourceId;
            public readonly TileId    Tile;
            public readonly byte[]    Bytes;
            public readonly List<int> LayerIndices;
            public PendingSymbolBuild(string sourceId, TileId tile, byte[] bytes, List<int> layerIndices)
            {
                SourceId = sourceId; Tile = tile; Bytes = bytes; LayerIndices = layerIndices;
            }
        }

        // Bytes-ready pushes land here (OnTileBytesReady ENQUEUES, never builds inline); PumpBuilds starts at
        // most MaxBuildsPerFrame of them per frame. byte[] retention is bounded by the cover size.
        private readonly Queue<PendingSymbolBuild> _buildQueue = new();
        // The current loaded (source, tile) set, refreshed each frame by ReconcileLoadedTiles — PumpBuilds
        // drops a queued build whose tile has since left the loaded set (a later re-entry is a fresh push).
        private readonly HashSet<SymbolTileLabelStore.Key> _loadedNow = new();
        // Cancels in-flight builds on restyle/teardown so a resumed build never touches disposed glyph/atlas
        // state (closes the missing-token + restyle-vs-in-flight-build risks from the review). Recreated per
        // SetStyle so each style has its own cancellation scope.
        private CancellationTokenSource _buildCts = new();

        /// <summary>Max symbol-tile builds STARTED per frame in <see cref="PumpBuilds"/> — the responsiveness
        /// knob for stall #1. Default 1 (locked design default); MapView may serialize it later.</summary>
        public int MaxBuildsPerFrame { get; set; } = 1;

        /// <summary>Test seam (dependency-inversion, mirroring <c>IDataSource</c>): the glyph-source factory
        /// <see cref="SetStyle"/> uses, overridable so an EditMode test can inject a fixture/gated
        /// <c>TestGlyphSource</c> instead of the production web source. Null ⇒ the production
        /// <see cref="GlyphSourceFactory.Create"/>.</summary>
        internal Func<StyleDocument, IGlyphSource> GlyphSourceFactoryOverride { get; set; }

        // Per-frame observability (mirrors TileManager's *LastTick counters) — read by tests, never the live path.
        internal int BuildsStartedLastPump { get; private set; }
        internal int AtlasUploadsLastPump  { get; private set; }
        internal int CancelledBuildCount   { get; private set; }
        internal int QueuedBuildCount => _buildQueue.Count;

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
            // Cancel any in-flight builds from the previous style and open a fresh cancellation scope, then
            // drop queued builds (their layer-index lists belong to the old _layersBySource rebuilt below).
            _buildCts.Cancel();
            _buildCts.Dispose();
            _buildCts = new CancellationTokenSource();
            _buildQueue.Clear();
            DisposePipeline();

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

            // D11/E2: per-layer materials (SymbolText clone + text-halo-* bind) are no longer built here —
            // they live on each SymbolRenderLayer, built by RenderLayerSet.Build from this SAME symbolLayers
            // list (in the same declared order, so the ordinal mapping stays 1:1). This class only needs the
            // glyph pipeline below.

            // The glyph pipeline needs the style's glyphs URL. Without it there are no glyphs to shape, so
            // leave _builder null (OnTileBytesReady no-ops) rather than throw — the labels just don't render.
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

        /// <summary>TileManager hook (MAIN THREAD): a tile's MVT bytes are ready — ENQUEUE a deferred build for
        /// its source's symbol layers, if any. Never throws (TileManager also isolates, belt and braces).
        ///
        /// <para>Stall #1: this used to run the WHOLE build (decode + shape + atlas upload) synchronously on the
        /// main thread for EVERY fetch completing this frame — an unbounded burst. It now only enqueues; the
        /// per-frame <see cref="PumpBuilds"/> starts at most <see cref="MaxBuildsPerFrame"/> of them.</para></summary>
        public void OnTileBytesReady(string sourceId, TileId tile, byte[] bytes)
        {
            if (_builder == null || bytes == null) return;
            if (!_layersBySource.TryGetValue(sourceId, out List<int> layerIndices)) return;
            _buildQueue.Enqueue(new PendingSymbolBuild(sourceId, tile, bytes, layerIndices));
        }

        /// <summary>
        /// MAIN THREAD, once per frame from <see cref="Map.MapView"/> AFTER <see cref="ReconcileLoadedTiles"/>
        /// (so <see cref="_loadedNow"/> reflects this frame's loaded set): start at most
        /// <see cref="MaxBuildsPerFrame"/> queued builds — dropping any whose tile has since left the loaded
        /// set — then perform AT MOST ONE atlas GPU upload if the shared atlas grew since the last upload
        /// (coalescing every commit + glyph-range arrival that landed since last frame into one ≤16 MB blit).
        ///
        /// <para>Fixes stall #1: the old path built every fetch-completing tile inline and re-uploaded the
        /// 16 MB atlas per glyph-adding tile; both are now bounded per frame. The dequeue loop runs at most
        /// <c>_buildQueue.Count</c> iterations (a dropped stale entry is still a dequeue).</para>
        /// </summary>
        public void PumpBuilds()
        {
            BuildsStartedLastPump = 0;
            AtlasUploadsLastPump  = 0;
            if (_builder == null) return; // no glyph pipeline (style has no 'glyphs' URL) — nothing to do

            // Start ≤ MaxBuildsPerFrame builds; drop queued entries whose tile left the loaded set.
            while (_buildQueue.Count > 0 && BuildsStartedLastPump < MaxBuildsPerFrame)
            {
                PendingSymbolBuild item = _buildQueue.Dequeue();
                var key = new SymbolTileLabelStore.Key(item.SourceId, item.Tile);
                if (!_loadedNow.Contains(key)) continue; // left cover before we reached it — drop the build
                BuildTileAsync(item.SourceId, item.Tile, item.Bytes, item.LayerIndices, _buildCts.Token).Forget();
                BuildsStartedLastPump++;
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
            _loadedNow.Clear(); // rebuilt here each frame; PumpBuilds reads it to drop builds for departed tiles
            for (int i = 0; i < loaded.Count; i++)
            {
                LoadedTileKey k = loaded[i];
                if (_layersBySource.ContainsKey(k.SourceId))
                {
                    var storeKey = new SymbolTileLabelStore.Key(k.SourceId, k.Tile);
                    _reconcileKeys.Add(storeKey);
                    _loadedNow.Add(storeKey);
                }
            }
            // Pass the wall-clock (from MapView) + the departing grace window so a tile that leaves cover keeps its
            // labels COLLECTED (as departing) for the fade-out instead of popping. Grace applies only when the mesh
            // cache is enabled (labels are dropped, not kept warm, on release otherwise → nothing to fade).
            double grace = _cacheEnabled ? DepartingGraceSeconds : 0.0;
            _store.ReconcileActiveSet(_reconcileKeys, _cacheEnabled, nowSeconds, grace);
        }

        private async UniTaskVoid BuildTileAsync(string sourceId, TileId tile, byte[] bytes,
            List<int> layerIndices, CancellationToken ct)
        {
            var key = new SymbolTileLabelStore.Key(sourceId, tile);
            int gen = _store.BeginBuild(key); // reserve the active slot (collected as empty until committed)

            // Capture the main-thread inputs BEFORE hopping to the pool (Unity APIs are main-thread only):
            // this build's own builder, the camera zoom + projection, and its layer list.
            StyledSymbolTileBuilder builder     = _builder;
            double                  zoom        = _camera.CurrentProperties.Zoom;
            var                     projection  = _camera.Projection;
            var layers = new List<SymbolStyle.StyleLayer>(layerIndices.Count);
            for (int k = 0; k < layerIndices.Count; k++) layers.Add(_allSymbolLayers[layerIndices[k]]);

            try
            {
                // Stage B: decode + feature-extract on the THREAD POOL — engine-free, touches no glyph cache /
                // atlas / UnityEngine object, so it is worker-safe (this was the ~600ms main-thread burst).
                await UniTask.SwitchToThreadPool();
                List<StyledSymbolTileBuilder.ExtractedLayer> extracted;
                using (PmTileDecode.Auto())
                {
                    MvtTile mvt = MvtDecoder.Decode(bytes);
                    extracted = builder.ExtractLayers(mvt, tile, layers, zoom, projection, layerIndices);
                }

                // Back to MAIN for glyph ensure + shape (they read the shared atlas). A restyle/teardown that
                // cancelled while we were on the pool throws here — before touching the store/glyphs/atlas.
                await UniTask.SwitchToMainThread(ct);

                var labels = new List<LabelInstance>();
                await builder.ShapeAsync(extracted, labels, ct);
                ct.ThrowIfCancellationRequested();

                // Commit — unless superseded by a newer build OR dropped mid-build (released-to-cache is NOT
                // stale: the store writes the labels to the cached side so a later hit restores them). The
                // atlas GPU upload is coalesced into the next PumpBuilds — no atlas touch here.
                _store.CompleteBuild(key, gen, labels);
            }
            catch (OperationCanceledException)
            {
                // Restyle/teardown mid-build — silent, never touches disposed state (closes the review's two
                // latent lifetime risks). The reserved slot is dropped when SetStyle/Dispose Clears the store.
                CancelledBuildCount++;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolLabelSubsystem] label build failed for tile {tile} (source '{sourceId}'): {ex.Message}");
            }
        }

        /// <summary>Aggregate every loaded tile's labels into <paramref name="output"/> for this frame's
        /// <see cref="LabelPlacementSystem.Tick"/> (which then projects/collides/billboards them). A-3: point
        /// labels are deduped across tiles at a grid of one logical pixel at the CURRENT display zoom
        /// (<see cref="WebMercator.GroundResolution"/>) — so the same symbol from a parent + child tile during a
        /// zoom transition collapses to one, and the grid tracks zoom (a fixed grid cannot serve all zooms).</summary>
        public void CollectInto(List<LabelInstance> output)
            => _store.CollectInto(output, WebMercator.GroundResolution(_camera.CurrentProperties.Zoom));

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
            double quantize  = WebMercator.GroundResolution(_camera.CurrentProperties.Zoom);
            int    slotCount = _allSymbolLayers.Count > 0 ? _allSymbolLayers.Count : 1;
            // CollectInto appends DEPARTING labels (tiles leaving cover, kept warm for a fade-out) after the active
            // ones and reports the split; the builder flags the departing records so the placement gather fades them
            // out instead of popping. Pass the projection so the batch stores each tile's render-space corners for
            // the per-frame tile-coverage pre-cull (camera-independent → built here).
            _store.CollectInto(_batchCollect, quantize, out int activeCount);
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

        public void Dispose()
        {
            _buildCts.Cancel();   // stop any in-flight build before its glyph/atlas state is disposed below
            _buildCts.Dispose();
            _buildQueue.Clear();
            _store.Clear();
            DisposePipeline();
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
