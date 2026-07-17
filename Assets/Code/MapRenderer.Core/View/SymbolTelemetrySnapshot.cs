// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int fields only (mirrors TileTelemetrySnapshot's S85 decision 1).

namespace MapRenderer.Core.View
{
    /// <summary>
    /// A pull-based snapshot of the SYMBOL-label subsystem's runtime state — the sibling of
    /// <see cref="TileTelemetrySnapshot"/> for text labels, produced on demand by
    /// <c>MapView.CaptureSymbolTelemetry</c> and displayed by <c>MapTelemetryPanel</c>. Every field is an
    /// instantaneous LEVEL at the instant of capture (a count), never a rate. A plain <c>init</c>-only,
    /// engine-free carrier so it compiles in the fast core-tests project and the Unity runner alike.
    /// </summary>
    public readonly struct SymbolTelemetrySnapshot
    {
        /// <summary>Active (in-cover) label-tile count — tiles whose labels feed this frame's placement pass.</summary>
        public int ActiveLabelTiles { get; init; }

        /// <summary>Cached (out-of-cover) label-tile count — labels kept warm so a prepared-cache hit re-shows
        /// the tile without a re-fetch (the zoom-out-then-in fix). These do NOT render.</summary>
        public int CachedLabelTiles { get; init; }

        /// <summary>Labels fed into the last placement <c>Tick</c> (before any projection cull) — the sum of
        /// every active tile's labels.</summary>
        public int InputLabelCount { get; init; }

        /// <summary>B-3: labels skipped by the pre-projection horizon/distance cull on the last Tick — never
        /// projected or collided (the trimmed tilted-view horizon pile-up). Tune the cull by watching this.</summary>
        public int DistanceCulledLabels { get; init; }

        /// <summary>S3: labels skipped on the last Tick because their anchor is hidden behind the globe's own
        /// bulk (<c>HorizonCull</c>). Always 0 under a planar projection.</summary>
        public int HorizonCulledLabels { get; init; }

        /// <summary>Collision CANDIDATES on the last Tick — labels that survived projection and entered the
        /// greedy pass (a point label counts 1; a curved / repeated line label counts 1 per along-line anchor).</summary>
        public int CollisionCandidateCount { get; init; }

        /// <summary>Collision SURVIVORS on the last Tick — candidates actually placed (the rest lost a collision).</summary>
        public int CollisionSurvivorCount { get; init; }

        /// <summary>Glyph quads submitted to the GPU on the last Tick (4 vertices each) — the drawn label load.</summary>
        public int PlacedQuadCount { get; init; }
    }
}
