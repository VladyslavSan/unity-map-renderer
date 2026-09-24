// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int fields only, like TileTelemetrySnapshot.

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// A snapshot of the symbol PLACEMENT pass — what the last Tick projected, culled, collided and drew.
    /// Produced and published by <c>SymbolPlacementSystem</c>, which owns every number in it. Its sibling
    /// <see cref="SymbolStoreTelemetrySnapshot"/> has a separate owner and publish instant, so it is a separate
    /// snapshot (<c>docs/telemetry-design.md</c>). Every field is a LEVEL at capture, never a rate; the carrier
    /// is engine-free, so it compiles in the core-tests project too.
    /// </summary>
    public readonly struct SymbolPlacementTelemetrySnapshot
    {
        /// <summary>Symbols fed into the last placement <c>Tick</c> (before any projection cull) — the sum of
        /// every active tile's symbols.</summary>
        public int InputSymbolCount { get; init; }

        /// <summary>Symbols skipped by the pre-projection horizon/distance cull on the last Tick — never
        /// projected or collided (the trimmed tilted-view horizon pile-up). Tune the cull by watching this.</summary>
        public int DistanceCulledSymbols { get; init; }

        /// <summary>Symbols skipped on the last Tick because their anchor is hidden behind the globe's own
        /// bulk (<c>HorizonCull</c>). Always 0 under a planar projection.</summary>
        public int HorizonCulledSymbols { get; init; }

        /// <summary>Symbols skipped on the last Tick because their style layer is out of the LIVE camera zoom's
        /// <c>[minzoom, maxzoom)</c>. The gate runs per frame ahead of projection, so an out-of-zoom symbol is never
        /// projected, staged or collided; a record still fading out stays staged and is suppressed post-stage.
        /// Compare it with <see cref="InputSymbolCount"/> to see the gate's reach on an overzoomed view.</summary>
        public int ZoomCulledSymbols { get; init; }

        /// <summary>Companion to <see cref="SymbolStoreTelemetrySnapshot.CoverageDroppedSymbols"/>: symbols
        /// whose tile just crossed below the coverage threshold and finished easing out this Tick (they FADED
        /// rather than popped) — the transient tail of the store's coverage drop, observed from the placement
        /// side because the fade is the placement pass's doing.</summary>
        public int CoverageFadingSymbols { get; init; }

        /// <summary>Collision CANDIDATES on the last Tick — symbols that survived projection and entered the
        /// greedy pass (a point symbol counts 1; a curved / repeated line symbol counts 1 per along-line anchor).</summary>
        public int CollisionCandidateCount { get; init; }

        /// <summary>Collision SURVIVORS — candidates actually placed (the rest lost a collision). Collision is
        /// deferred, so this is the verdict over the PREVIOUS Tick's candidates: one Tick behind
        /// <see cref="CollisionCandidateCount"/>, which is always the current Tick's.</summary>
        public int CollisionSurvivorCount { get; init; }

        /// <summary>Glyph quads submitted to the GPU on the last Tick (4 vertices each) — the drawn symbol load.</summary>
        public int PlacedQuadCount { get; init; }

        /// <summary>Fade records held: the size of the map the per-frame decay sweep walks, so a COST. A faded-out
        /// identity is dropped, so this tracks the drawn count; if it tracks <see cref="CollisionCandidateCount"/>,
        /// invisible identities are retained. Keep it the RAW map size: a fading-in symbol holds a sub-epsilon
        /// value, so a thresholded count hides the regression.</summary>
        public int LiveFadeSymbolCount { get; init; }

        /// <summary>CUMULATIVE heavy rebuilds of the native symbol mirror since startup — bumped once per real
        /// gather, never on a memo hit. A LEVEL, per this type's contract; the panel derives the per-second rate,
        /// which is the number that matters: it says how often the winner set actually changes, and therefore
        /// whether the gather memo can help at all. Approaching the frame rate ⇒ the set churns every frame and
        /// memoization is structurally dead (see `docs/symbol-label-perf-design.md`).</summary>
        public int MirrorRebuildCount { get; init; }
    }
}
