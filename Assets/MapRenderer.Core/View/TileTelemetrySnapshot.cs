// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int/double/bool fields only (S85 decision 1).

namespace MapRenderer.Core.View
{
    /// <summary>
    /// S85: a pull-based snapshot of runtime tile/render telemetry — <see cref="Tile.TileManager.CaptureTelemetry"/>
    /// computes one of these on demand (never published per-tick). Every field is an instantaneous
    /// <b>level</b> (a count or a zoom number at the instant of capture) — never a duration or a per-tick
    /// rate (those are S46/<c>*LastTick</c> territory). A plain data carrier: <c>init</c>-only, engine-free,
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

        /// <summary>Loaded records whose tessellation is COMPLETE but consume is budget-deferred (a subset
        /// of <see cref="PendingTileCount"/>, which also lumps fetch-/tessellation-in-flight records) — the
        /// un-drained per-frame build backlog depth. The <c>s95-residual-tile-load-frame-stall</c>
        /// "measure first" signal.</summary>
        public int ConsumeBacklog { get; init; }

        /// <summary>In-flight network fetches, summed across every source pipeline.</summary>
        public int InFlightFetches { get; init; }

        /// <summary>Lifetime count of tiles released while their tessellation was still in-flight.</summary>
        public int ReleasedMidFlightCount { get; init; }

        /// <summary>Lifetime count of tiles released while their fetch was still in-flight.</summary>
        public int ReleasedMidFetchCount { get; init; }

        /// <summary>Lifetime count of genuine (non-cancellation) fetch errors.</summary>
        public int FetchErrorCount { get; init; }
    }
}
