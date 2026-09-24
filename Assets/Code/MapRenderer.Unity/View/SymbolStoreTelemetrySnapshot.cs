// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int fields only, like TileTelemetrySnapshot.

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// A snapshot of the symbol STORE — what the symbol subsystem is holding and feeding forward. Produced
    /// and published by <c>SymbolSubsystem</c>, which owns every number in it (<c>docs/telemetry-design.md</c>).
    /// Its sibling <see cref="SymbolPlacementTelemetrySnapshot"/> holds the placement results, with a different
    /// owner. Every field is a LEVEL at capture, never a rate; the carrier is engine-free, so it compiles in the
    /// core-tests project too.
    /// </summary>
    public readonly struct SymbolStoreTelemetrySnapshot
    {
        /// <summary>Active (in-cover) symbol-tile count — tiles whose symbols feed this frame's placement pass.</summary>
        public int ActiveSymbolTiles { get; init; }

        /// <summary>Cached (out-of-cover) symbol-tile count — symbols kept warm so a prepared-cache hit re-shows
        /// the tile without a re-fetch (the zoom-out-then-in fix). These do NOT render.</summary>
        public int CachedSymbolTiles { get; init; }

        /// <summary>Tile-coverage pre-cull: symbols classified DROP because their tile is steadily below the
        /// on-screen coverage threshold (never-visible, or the fade-out grace has expired). They stay resident
        /// in the mirror but are masked out of placement, so they are never staged. Watch this against
        /// <c>MapViewConfig.SymbolTileCoverageCull</c> to tune it.</summary>
        public int CoverageDroppedSymbols { get; init; }
    }
}
