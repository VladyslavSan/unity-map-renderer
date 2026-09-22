// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int fields only, like TileTelemetrySnapshot.

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// A snapshot of the symbol PLACEMENT pass — what the last Tick projected, culled, collided and drew.
    /// Produced and published by <c>SymbolPlacementSystem</c>, which owns every number in it. Its sibling is
    /// <see cref="SymbolStoreTelemetrySnapshot"/> (what the store is holding); they are separate snapshots
    /// because they have separate owners, and each publishes when its own pass finishes rather than at one
    /// shared frame instant (<c>docs/telemetry-design.md</c>).
    ///
    /// <para>Every field is an instantaneous LEVEL at the instant of capture (a count), never a rate. A plain
    /// <c>init</c>-only, engine-free carrier so it compiles in the fast core-tests project and the Unity runner
    /// alike.</para>
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
        /// <c>[minzoom, maxzoom)</c> — the display-time layer gate, evaluated per-frame and moved AHEAD of
        /// projection so an out-of-zoom symbol (e.g. a z14 tile's <c>poi_r*</c> points before the camera reaches
        /// their minzoom) is never projected/staged/collided. A record still fading out is exempt — it stays
        /// staged and is suppressed post-stage. Watch this against <see cref="InputSymbolCount"/> to see the
        /// gate's reach on an overzoomed view.</summary>
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

        /// <summary>Fade records held — the size of the map the per-frame decay sweep walks, so a COST rather
        /// than just a memory figure. An identity that has finished fading OUT is dropped rather than parked at 0,
        /// so this should track the drawn symbol count and settle when the camera does; if it instead tracks
        /// <see cref="CollisionCandidateCount"/>, invisible identities are being retained and the sweep is paying
        /// for symbols nobody can see. Keep it the RAW map size — a filtered or epsilon-thresholded count hides
        /// the regression it exists to expose, because a fading-IN symbol holds a sub-epsilon value.</summary>
        public int LiveFadeSymbolCount { get; init; }

        /// <summary>CUMULATIVE heavy rebuilds of the native symbol mirror since startup — bumped once per real
        /// gather, never on a memo hit. A LEVEL, per this type's contract; the panel derives the per-second rate,
        /// which is the number that matters: it says how often the winner set actually changes, and therefore
        /// whether the gather memo can help at all. Approaching the frame rate ⇒ the set churns every frame and
        /// memoization is structurally dead (see `docs/symbol-label-perf-design.md`).</summary>
        public int MirrorRebuildCount { get; init; }
    }
}
