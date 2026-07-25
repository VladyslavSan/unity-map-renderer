using UnityEngine;
using MapRenderer.Core.View;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S85: a dev/debug readout surface for <see cref="MapView.CaptureTelemetry"/> — mirrors
    /// <see cref="CameraControlPanel"/>'s shape (serialized read-only display fields, overwritten every
    /// frame). Reports only; never a control knob (decision 8) — no field here is ever read back into the
    /// map.
    ///
    /// <para><b>Play-mode only:</b> <see cref="MapView"/> is constructed only on the runtime
    /// <c>Bootstrapper.Wire</c>/<c>Start</c> path, so in edit mode <see cref="MapViewComponent.Camera"/> is
    /// null. <see cref="Update"/> null-guards <see cref="Map"/>/<see cref="MapViewComponent.Camera"/> and
    /// no-ops cleanly when unwired (the same guard <see cref="CameraControlPanel"/> uses).</para>
    /// </summary>
    public sealed class MapTelemetryPanel : MonoBehaviour
    {
        [Tooltip("The MapView whose live telemetry this panel reads (set in the Inspector).")]
        public MapViewComponent Map;

        [Header("Telemetry (live — overwritten each frame)")]
        [Tooltip("Size of the selected cover (the frustum cover, no pad ring). THE headline number.")]
        public int VisibleTileCount;

        [Tooltip("Near-field grid width — distinct tile X among the cover's tiles at CoverMaxZoom.")]
        public int CoverColumns;

        [Tooltip("Near-field grid height — distinct tile Y among the cover's tiles at CoverMaxZoom.")]
        public int CoverRows;

        [Tooltip("The finest / near-field target zoom the traversal caps at.")]
        public int SelectionZoom;

        [Tooltip("True when the cover spans more than one zoom level (routine under ScreenSpaceLod).")]
        public bool IsMixedZoom;

        [Tooltip("The cover's coarsest (far) zoom level.")]
        public int CoverMinZoom;

        [Tooltip("The cover's finest (near) zoom level.")]
        public int CoverMaxZoom;

        [Tooltip("The camera's fractional (MapLibre) zoom.")]
        public double FractionalZoom;

        [Tooltip("Number of currently loaded-or-loading (tile, source) records.")]
        public int LoadedTileCount;

        [Tooltip("Loaded records not yet Built — the load-progress lag.")]
        public int PendingTileCount;

        [Tooltip("Loaded records whose mesh build is complete but consume is budget-deferred.")]
        public int ConsumeBacklog;

        [Tooltip("In-flight network fetches, summed across every source pipeline.")]
        public int InFlightFetches;

        [Tooltip("Lifetime count of tiles released while their mesh build was still in-flight.")]
        public int ReleasedMidFlightCount;

        [Tooltip("Lifetime count of tiles released while their fetch was still in-flight.")]
        public int ReleasedMidFetchCount;

        [Tooltip("Lifetime count of genuine (non-cancellation) fetch errors.")]
        public int FetchErrorCount;

        [Header("S82: Prepared-tile cache utilization (live — overwritten each frame)")]
        [Tooltip("Master toggle for the PreparedTileCache (PreparedTileCacheConfig.Enabled). False means " +
                 "every revisit re-fetches/re-builds/re-uploads — hits are always 0 in that state.")]
        public bool PreparedCacheEnabled;

        [Tooltip("Cumulative full-tile cache hits (a revisit/style-toggle that skipped decode/build/upload).")]
        public int PreparedCacheHits;

        [Tooltip("Cumulative cache misses (a cover-entry that genuinely re-prepared).")]
        public int PreparedCacheMisses;

        [Tooltip("Hits / (Hits + Misses) * 100 — 0 while nothing has been probed yet (divide-by-zero guarded).")]
        public double PreparedCacheHitRatePercent;

        [Tooltip("Current number of prepared tile-layer entries held by the cache.")]
        public int PreparedCacheEntryCount;

        [Tooltip("The effective entry-count cap the cache evicts against (int.MaxValue = unbounded).")]
        public int PreparedCacheMaxCount;

        [Tooltip("Current estimated VRAM bytes held by the cache.")]
        public long PreparedCacheBytesHeld;

        [Tooltip("The effective byte budget the cache evicts against (long.MaxValue = unbounded).")]
        public long PreparedCacheByteBudget;

        [Tooltip("BytesHeld / ByteBudget * 100 — how full the cache is against its budget.")]
        public double PreparedCacheFillPercent;

        [Tooltip("Cumulative LRU evictions (an entry destroyed because the byte budget or count cap was exceeded).")]
        public int PreparedCacheEvictions;

        [Header("S105: Symbol labels (live — overwritten each frame)")]
        [Tooltip("Active (in-cover) label-tile count — tiles whose labels feed this frame's placement pass.")]
        public int SymbolActiveLabelTiles;

        [Tooltip("Cached (out-of-cover) label-tile count — labels kept warm so a prepared-cache hit re-shows " +
                 "the tile without a re-fetch (the zoom-out-then-in fix). These do NOT render.")]
        public int SymbolCachedLabelTiles;

        [Tooltip("Labels fed into the last placement Tick (before projection cull) — sum over active tiles.")]
        public int SymbolInputLabelCount;

        [Tooltip("B-3: labels skipped by the pre-projection horizon/distance cull last Tick (never projected/" +
                 "collided — the trimmed tilted-view horizon pile-up). Watch this to tune the cull radius.")]
        public int SymbolDistanceCulledLabels;

        [Tooltip("§1.5 tile-coverage pre-cull: labels classified Drop (tile steadily below the on-screen " +
                 "coverage threshold; D1 keeps them resident but masked out of placement). Tune LabelTileCoverageCull.")]
        public int SymbolCoverageDroppedLabels;

        [Tooltip("§1.5 companion: labels whose tile just crossed below coverage and finished fading out this " +
                 "Tick (they faded, not popped) — the transient tail of the coverage drop.")]
        public int SymbolCoverageFadingLabels;

        [Tooltip("Collision candidates on the last Tick (labels that survived projection; a point label is 1, " +
                 "a curved/repeated line label is 1 per along-line anchor).")]
        public int SymbolCollisionCandidates;

        [Tooltip("Collision survivors on the last Tick (candidates actually placed; the rest lost a collision).")]
        public int SymbolCollisionSurvivors;

        [Tooltip("Glyph quads submitted to the GPU on the last Tick (4 vertices each) — the drawn label load.")]
        public int SymbolPlacedQuads;

        [Tooltip("A-4 fade records held — the size of the map the per-frame decay sweep walks, so a cost, not " +
                 "just memory. A faded-out identity is dropped rather than parked at 0, so this should track " +
                 "SymbolPlacedQuads and settle when the camera does; tracking SymbolCollisionCandidates instead " +
                 "means invisible identities are being retained again.")]
        public int SymbolLiveFadeRecords;

        [Tooltip("R1: cumulative heavy rebuilds of the native label mirror (never bumped on a memo hit).")]
        public int SymbolMirrorRebuilds;

        [Tooltip("Heavy mirror rebuilds per second, averaged over the last sampling window — how often the winner " +
                 "set actually changes. Near the frame rate means the set churns every frame and the gather memo " +
                 "cannot help; near zero means it is hitting. See docs/symbol-label-perf-design.md §10.4.")]
        public double SymbolMirrorRebuildsPerSecond;

        // Rate sampling: a per-frame delta would read 0 or ~60 with nothing in between, so accumulate over a
        // window and publish once per window.
        private const double RebuildRateWindowSeconds = 0.5;
        private double _rebuildWindowStartTime = double.NegativeInfinity;
        private int    _rebuildWindowStartCount;

        private void Update() => Tick();

        /// <summary>
        /// One readout refresh. <c>internal</c> so an EditMode test can drive it deterministically (the
        /// MonoBehaviour game loop does not run under the EditMode test runner). Production calls it from
        /// <see cref="Update"/>; it is not part of the public surface.
        /// </summary>
        internal void Tick()
        {
            // Play-mode only: null-guard until the bootstrapper wires the camera (edit mode has no MapCamera).
            if (Map == null || Map.Camera == null) return;

            TileTelemetrySnapshot snap = Map.View.CaptureTelemetry();

            VisibleTileCount       = snap.VisibleTileCount;
            CoverColumns           = snap.CoverColumns;
            CoverRows              = snap.CoverRows;
            SelectionZoom          = snap.SelectionZoom;
            IsMixedZoom            = snap.IsMixedZoom;
            CoverMinZoom           = snap.CoverMinZoom;
            CoverMaxZoom           = snap.CoverMaxZoom;
            FractionalZoom         = snap.FractionalZoom;
            LoadedTileCount        = snap.LoadedTileCount;
            PendingTileCount       = snap.PendingTileCount;
            ConsumeBacklog         = snap.ConsumeBacklog;
            InFlightFetches        = snap.InFlightFetches;
            ReleasedMidFlightCount = snap.ReleasedMidFlightCount;
            ReleasedMidFetchCount  = snap.ReleasedMidFetchCount;
            FetchErrorCount        = snap.FetchErrorCount;

            PreparedCacheEnabled    = snap.PreparedCacheEnabled;
            PreparedCacheHits       = snap.PreparedCacheHits;
            PreparedCacheMisses     = snap.PreparedCacheMisses;
            PreparedCacheEntryCount = snap.PreparedCacheEntryCount;
            PreparedCacheMaxCount   = snap.PreparedCacheMaxCount;
            PreparedCacheBytesHeld  = snap.PreparedCacheBytesHeld;
            PreparedCacheByteBudget = snap.PreparedCacheByteBudget;
            PreparedCacheEvictions  = snap.PreparedCacheEvictions;

            int probes = snap.PreparedCacheHits + snap.PreparedCacheMisses;
            PreparedCacheHitRatePercent = probes > 0 ? (double)snap.PreparedCacheHits / probes * 100.0 : 0.0;
            PreparedCacheFillPercent = snap.PreparedCacheByteBudget > 0
                ? (double)snap.PreparedCacheBytesHeld / snap.PreparedCacheByteBudget * 100.0
                : 0.0;

            SymbolTelemetrySnapshot sym = Map.View.CaptureSymbolTelemetry();
            SymbolActiveLabelTiles    = sym.ActiveLabelTiles;
            SymbolCachedLabelTiles    = sym.CachedLabelTiles;
            SymbolInputLabelCount     = sym.InputLabelCount;
            SymbolDistanceCulledLabels = sym.DistanceCulledLabels;
            SymbolCoverageDroppedLabels = sym.CoverageDroppedLabels;
            SymbolCoverageFadingLabels = sym.CoverageFadingLabels;
            SymbolCollisionCandidates = sym.CollisionCandidateCount;
            SymbolCollisionSurvivors  = sym.CollisionSurvivorCount;
            SymbolPlacedQuads         = sym.PlacedQuadCount;
            SymbolLiveFadeRecords     = sym.LiveFadeRecordCount;
            SymbolMirrorRebuilds      = sym.MirrorRebuildCount;
            SampleMirrorRebuildRate(sym.MirrorRebuildCount);
        }

        // Publish the rebuild rate once per RebuildRateWindowSeconds. The first call only opens the window (no
        // rate yet — there is no earlier sample to difference against).
        private void SampleMirrorRebuildRate(int rebuildCount)
        {
            double now = Time.unscaledTimeAsDouble;
            if (double.IsNegativeInfinity(_rebuildWindowStartTime))
            {
                _rebuildWindowStartTime = now;
                _rebuildWindowStartCount = rebuildCount;
                return;
            }

            double elapsed = now - _rebuildWindowStartTime;
            if (elapsed < RebuildRateWindowSeconds) return;

            SymbolMirrorRebuildsPerSecond = (rebuildCount - _rebuildWindowStartCount) / elapsed;
            _rebuildWindowStartTime = now;
            _rebuildWindowStartCount = rebuildCount;
        }
    }
}
