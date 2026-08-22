// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — plain int fields only (mirrors TileTelemetrySnapshot's S85 decision 1).

namespace MapRenderer.Core.View
{
    /// <summary>
    /// A snapshot of the label PLACEMENT pass — what the last Tick projected, culled, collided and drew.
    /// Produced and published by <c>LabelPlacementSystem</c>, which owns every number in it. Its sibling is
    /// <see cref="SymbolStoreTelemetrySnapshot"/> (what the store is holding); they are separate snapshots
    /// because they have separate owners, and each publishes when its own pass finishes rather than at one
    /// shared frame instant (<c>docs/telemetry-design.md</c> §3).
    ///
    /// <para>Every field is an instantaneous LEVEL at the instant of capture (a count), never a rate. A plain
    /// <c>init</c>-only, engine-free carrier so it compiles in the fast core-tests project and the Unity runner
    /// alike.</para>
    /// </summary>
    public readonly struct LabelPlacementTelemetrySnapshot
    {
        /// <summary>Labels fed into the last placement <c>Tick</c> (before any projection cull) — the sum of
        /// every active tile's labels.</summary>
        public int InputLabelCount { get; init; }

        /// <summary>B-3: labels skipped by the pre-projection horizon/distance cull on the last Tick — never
        /// projected or collided (the trimmed tilted-view horizon pile-up). Tune the cull by watching this.</summary>
        public int DistanceCulledLabels { get; init; }

        /// <summary>S3: labels skipped on the last Tick because their anchor is hidden behind the globe's own
        /// bulk (<c>HorizonCull</c>). Always 0 under a planar projection.</summary>
        public int HorizonCulledLabels { get; init; }

        /// <summary>Labels skipped on the last Tick because their style layer is out of the LIVE camera zoom's
        /// <c>[minzoom, maxzoom)</c> — the display-time layer gate, evaluated per-frame and moved AHEAD of
        /// projection so an out-of-zoom label (e.g. a z14 tile's <c>poi_r*</c> points before the camera reaches
        /// their minzoom) is never projected/staged/collided. A record still fading out is exempt (it stays
        /// staged, suppressed post-stage), so this counts only the fade-dead out-of-zoom records the pre-gate
        /// used to project then discard. Watch it against <see cref="InputLabelCount"/> to see the gate's reach
        /// on an overzoomed view.</summary>
        public int ZoomCulledLabels { get; init; }

        /// <summary>§1.5 companion to <see cref="SymbolStoreTelemetrySnapshot.CoverageDroppedLabels"/>: labels
        /// whose tile just crossed below the coverage threshold and finished easing out this Tick (they FADED
        /// rather than popped) — the transient tail of the store's coverage drop, observed from the placement
        /// side because the fade is the placement pass's doing.</summary>
        public int CoverageFadingLabels { get; init; }

        /// <summary>Collision CANDIDATES on the last Tick — labels that survived projection and entered the
        /// greedy pass (a point label counts 1; a curved / repeated line label counts 1 per along-line anchor).</summary>
        public int CollisionCandidateCount { get; init; }

        /// <summary>Collision SURVIVORS — candidates actually placed (the rest lost a collision). R3 (deferred
        /// collision): this is the verdict of the collision run over the PREVIOUS Tick's candidates, one Tick
        /// behind <see cref="CollisionCandidateCount"/>, which is always the current Tick's.</summary>
        public int CollisionSurvivorCount { get; init; }

        /// <summary>Glyph quads submitted to the GPU on the last Tick (4 vertices each) — the drawn label load.</summary>
        public int PlacedQuadCount { get; init; }

        /// <summary>A-4 fade records held — the size of the map the per-frame decay sweep walks, so a COST rather
        /// than just a memory figure. An identity that has finished fading OUT is dropped rather than parked at 0,
        /// so this should track the drawn label count and settle when the camera does; if it instead tracks
        /// <see cref="CollisionCandidateCount"/>, invisible identities are being retained and the sweep is paying
        /// for labels nobody can see. This is deliberately the RAW map size — never redefine it as a filtered or
        /// epsilon-thresholded count, which would hide exactly the regression it exists to expose (a fading-IN
        /// label legitimately holds a sub-epsilon value).</summary>
        public int LiveFadeRecordCount { get; init; }

        /// <summary>R1: CUMULATIVE heavy rebuilds of the native label mirror since startup — bumped once per real
        /// gather, never on a memo hit. A LEVEL, per this type's contract; the panel derives the per-second rate,
        /// which is the number that matters: it says how often the winner set actually changes, and therefore
        /// whether the gather memo can help at all. Approaching the frame rate ⇒ the set churns every frame and
        /// memoization is structurally dead (see `docs/symbol-label-perf-design.md` §10.4).</summary>
        public int MirrorRebuildCount { get; init; }
    }
}
