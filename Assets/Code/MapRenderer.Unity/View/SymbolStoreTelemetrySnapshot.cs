// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int fields only (mirrors TileTelemetrySnapshot's S85 decision 1).

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// A snapshot of the symbol STORE — what the symbol subsystem is holding and feeding forward. Produced and
    /// published by <c>SymbolSubsystem</c>, which owns every number in it (each provider publishes its own
    /// telemetry; nobody assembles a snapshot on another type's behalf — see <c>docs/telemetry-design.md</c>).
    /// Its sibling is <see cref="SymbolPlacementTelemetrySnapshot"/>, the RESULTS of the placement pass those
    /// symbols then go through — a different owner, hence a different snapshot.
    ///
    /// <para>Every field is an instantaneous LEVEL at the instant of capture (a count), never a rate. A plain
    /// <c>init</c>-only, engine-free carrier so it compiles in the fast core-tests project and the Unity runner
    /// alike.</para>
    /// </summary>
    public readonly struct SymbolStoreTelemetrySnapshot
    {
        /// <summary>Active (in-cover) symbol-tile count — tiles whose symbols feed this frame's placement pass.</summary>
        public int ActiveSymbolTiles { get; init; }

        /// <summary>Cached (out-of-cover) symbol-tile count — symbols kept warm so a prepared-cache hit re-shows
        /// the tile without a re-fetch (the zoom-out-then-in fix). These do NOT render.</summary>
        public int CachedSymbolTiles { get; init; }

        /// <summary>§1.5 tile-coverage pre-cull: symbols classified DROP because their tile is steadily below the
        /// on-screen coverage threshold (never-visible, or the fade-out grace has expired). D1: kept resident in
        /// the mirror but masked out of placement (never staged) — the per-frame savings. Watch this against
        /// <c>MapViewConfig.SymbolTileCoverageCull</c> to tune it.</summary>
        public int CoverageDroppedSymbols { get; init; }
    }
}
