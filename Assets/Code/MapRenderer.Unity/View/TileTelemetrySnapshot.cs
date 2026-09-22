// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int/double/bool fields only.

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// A pull-based snapshot of runtime tile/render telemetry — <see cref="Tile.TileManager.CaptureTelemetry"/>
    /// computes one of these on demand (never published per-tick). Every field is an instantaneous
    /// <b>level</b> (a count or a zoom number at the instant of capture) — never a duration or a per-tick
    /// rate (those are <c>*LastTick</c> territory). A plain data carrier: <c>init</c>-only, engine-free,
    /// so it compiles in the fast <c>dotnet</c> core-tests project and the Unity runner alike.
    /// </summary>
    public readonly struct TileTelemetrySnapshot
    {
        /// <summary>THE headline number — size of the selected cover (the frustum cover, <b>no pad ring</b>).
        /// NOT "tiles with on-screen geometry" and NOT the loaded-record count (see <see cref="LoadedTileCount"/>).</summary>
        public int VisibleTileCount { get; init; }

        /// <summary>Near-field grid width — distinct tile X among the cover's tiles at <see cref="CoverMaxZoom"/>
        /// (see <see cref="TileCoverStats"/>). Collapses to the whole-cover column count for a single-zoom cover.</summary>
        public int CoverColumns { get; init; }

        /// <summary>Near-field grid height — distinct tile Y among the cover's tiles at <see cref="CoverMaxZoom"/>.</summary>
        public int CoverRows { get; init; }

        /// <summary>The finest / near-field target zoom the traversal caps at (== <see cref="CoverMaxZoom"/>).
        /// Kept as the human-facing "headline zoom" alongside the span pair below.</summary>
        public int SelectionZoom { get; init; }

        /// <summary><c>CoverMinZoom != CoverMaxZoom</c> — routinely <c>true</c> under the default
        /// <c>ScreenSpaceLodStrategy</c> mixed-zoom cover; always <c>false</c> under <c>FlatLodStrategy</c>.</summary>
        public bool IsMixedZoom { get; init; }

        /// <summary>The cover's coarsest (far) zoom level.</summary>
        public int CoverMinZoom { get; init; }

        /// <summary>The cover's finest (near) zoom level.</summary>
        public int CoverMaxZoom { get; init; }

        /// <summary>The camera's fractional (MapLibre) zoom.</summary>
        public double FractionalZoom { get; init; }

        /// <summary>Number of currently loaded-or-loading <c>(tile, source)</c> records. Equals
        /// <see cref="VisibleTileCount"/> post-Tick for a single source with a wide zoom range; diverges
        /// under multiple rendered sources (one record per source pipeline per admitted tile).</summary>
        public int LoadedTileCount { get; init; }

        /// <summary>Loaded records not yet <c>Built</c> — the load-progress lag
        /// (built count == <see cref="LoadedTileCount"/> − <see cref="PendingTileCount"/>).</summary>
        public int PendingTileCount { get; init; }

        /// <summary>Loaded records whose mesh build is COMPLETE but consume is budget-deferred (a subset
        /// of <see cref="PendingTileCount"/>, which also lumps fetch-/mesh build-in-flight records) — the
        /// un-drained per-frame build backlog depth.</summary>
        public int ConsumeBacklog { get; init; }

        /// <summary>Loaded records currently in the SOURCE tile's managed PROLOGUE step
        /// (job-scheduling-design.md) — a subset of <see cref="PendingTileCount"/>, disjoint from
        /// <see cref="GraphMeasureInFlight"/>/<see cref="GraphWriteInFlight"/>. A background tile never
        /// appears here — it starts directly at Measure.</summary>
        public int PrologueInFlight { get; init; }

        /// <summary>Loaded records currently in the graph arm's MEASURE step — a subset of
        /// <see cref="PendingTileCount"/>. Every tile passes through here (job-scheduling-design.md);
        /// a background tile arrives directly, a source tile arrives from
        /// <see cref="PrologueInFlight"/>.</summary>
        public int GraphMeasureInFlight { get; init; }

        /// <summary>Loaded records currently in the graph arm's WRITE step — a subset of
        /// <see cref="PendingTileCount"/>, disjoint from <see cref="GraphMeasureInFlight"/>.</summary>
        public int GraphWriteInFlight { get; init; }

        /// <summary>In-flight network fetches, summed across every source pipeline.</summary>
        public int InFlightFetches { get; init; }

        /// <summary>Lifetime count of tiles released while their mesh build was still in-flight.</summary>
        public int ReleasedMidFlightCount { get; init; }

        /// <summary>Lifetime count of tiles released while their fetch was still in-flight.</summary>
        public int ReleasedMidFetchCount { get; init; }

        /// <summary>Lifetime count of genuine (non-cancellation) fetch errors.</summary>
        public int FetchErrorCount { get; init; }

        // ── PreparedTileCache utilization ─────────────────────────────────────────────────────

        /// <summary>Whether the <c>PreparedTileCache</c> is active (see
        /// <see cref="Map.PreparedTileCacheConfig.Enabled"/>). <see langword="false"/> means every revisit
        /// re-fetches/re-builds/re-uploads — <see cref="PreparedCacheHits"/> is always 0 in that state.</summary>
        public bool PreparedCacheEnabled { get; init; }

        /// <summary>Cumulative count of full-tile cache hits (a revisit/style-toggle that skipped
        /// decode/build/upload).</summary>
        public int PreparedCacheHits { get; init; }

        /// <summary>Cumulative count of cache misses (a cover-entry that genuinely re-prepared).</summary>
        public int PreparedCacheMisses { get; init; }

        /// <summary>Current number of prepared tile-layer entries held by the cache (produced meshes AND
        /// empty-layer completeness markers).</summary>
        public int PreparedCacheEntryCount { get; init; }

        /// <summary>The effective entry-count cap the cache evicts against (the LIVE applied value — a
        /// non-positive configured cap clamps up to <see cref="int.MaxValue"/>, i.e. unbounded).</summary>
        public int PreparedCacheMaxCount { get; init; }

        /// <summary>Current estimated VRAM bytes held by the cache (sum of every live entry's estimated
        /// mesh size).</summary>
        public long PreparedCacheBytesHeld { get; init; }

        /// <summary>The effective byte budget the cache evicts against (the LIVE applied value — a
        /// non-positive configured budget clamps up to <see cref="long.MaxValue"/>, i.e. unbounded).</summary>
        public long PreparedCacheByteBudget { get; init; }

        /// <summary>Cumulative count of LRU evictions (an entry destroyed because the byte budget or count
        /// cap was exceeded — distinct from a <see cref="PreparedCacheHits"/> take-out, which removes an
        /// entry without destroying it).</summary>
        public int PreparedCacheEvictions { get; init; }
    }
}
