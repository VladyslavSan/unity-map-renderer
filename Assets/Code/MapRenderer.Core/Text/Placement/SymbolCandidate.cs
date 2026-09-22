// Engine-free: no UnityEngine dependency. Pure data carrier (no Unity.Mathematics types) — no
// namespace-collision guard needed.

using System;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One collision candidate spanning a CONTIGUOUS range of <see cref="SymbolBox"/>es —
    /// all-or-nothing. A POINT symbol is a single-box candidate (its whole-symbol AABB); a CURVED along-line
    /// symbol is an N-box candidate (one AABB per glyph). <c>CollisionJob</c> places a candidate iff EVERY
    /// box in its range is free, and — if placed — reserves ALL of them; so a curved symbol drops entirely
    /// when any one glyph collides, and blocks others across its whole run.
    ///
    /// <para>Only the CANDIDATES are sorted into placement order; the flat <c>boxes[]</c> array stays put
    /// (the grid stores absolute box indices), so <see cref="BoxStart"/> keeps addressing the same boxes
    /// after the sort. Blittable-friendly (all value fields), mirroring <see cref="SymbolBox"/>.</para>
    /// </summary>
    public struct SymbolCandidate
    {
        /// <summary>Index of this candidate's FIRST box in the caller's flat <c>boxes[]</c> array.</summary>
        public int BoxStart;

        /// <summary>Number of boxes in this candidate's range (1 for a point symbol, N glyphs for a curved one).</summary>
        public int BoxCount;

        /// <summary>Index of this candidate's FIRST <see cref="CandidateEmit"/> in the staged emit pool —
        /// mirrors <see cref="BoxStart"/>. Emits are NOT keyed by <see cref="SymbolIndex"/>, which carries
        /// only its survivor-identity role.</summary>
        public int EmitStart;

        /// <summary>Number of emits in this candidate's range — 1 for an ordinary symbol, 2 for a centred
        /// icon+text pair (its icon and text each keep their own <c>(Slot, AtlasKind)</c>
        /// <see cref="CandidateEmit"/>). An ordinary candidate satisfies <c>EmitCount == 1 &amp;&amp;
        /// EmitStart == SymbolIndex</c>, asserted by a test rather than enforced by code.
        /// <see cref="TryFindRangeTilingViolation"/> covers BOXES only.</summary>
        public int EmitCount;

        /// <summary>`icon-optional` / `text-optional` — INPUT: bit <c>b</c> set ⇒ box
        /// <c>BoxStart + b</c> (and, 1:1, emit <c>EmitStart + b</c>) may be DROPPED rather than dropping the
        /// whole candidate when it overlaps a placed blocker. The box↔emit correspondence holds because
        /// <c>SymbolStagingMath.AppendPointHalf</c> appends a box and its emit together, one per pair half —
        /// which is also why only a POINT PAIR (<c>BoxCount &lt;= 2</c>) may carry a non-zero mask; a curved
        /// symbol's N glyph boxes share ONE emit, so bits would not address them.
        ///
        /// <para>0 on every lone point symbol, every icon-only/text-only symbol and every curved symbol, so
        /// the collision loop keeps the all-or-nothing rule for all of them.</para></summary>
        public byte OptionalBoxMask;

        /// <summary>VERDICT: bit <c>b</c> set ⇒ box <c>BoxStart + b</c> overlapped a placed blocker
        /// and was dropped, so it was neither reserved in the grid nor drawn; always a subset of
        /// <see cref="OptionalBoxMask"/>, and always 0 on a candidate that did not place at all.
        /// Two writers, one frame apart, aliased on purpose:
        /// <c>SymbolStagingMath.StagePointPair</c> SEEDS it with last frame's verdict (carried by
        /// <c>SymbolPlacementSystem</c>'s dropped-halves map, keyed by <see cref="FadeId"/>) so the emit loop
        /// can skip the dropped half's quads, and the collision pass OVERWRITES it with this frame's verdict.
        /// The aliasing is safe because the emit loop reads the candidate BEFORE the collision job is
        /// scheduled (R3's one-frame verdict carry — the same latency <see cref="WasPlacedLastFrame"/> rides).</summary>
        public byte DroppedBoxMask;

        /// <summary>`symbol-sort-key` — greedy placement order. LOWER is placed FIRST (MapLibre priority).</summary>
        public float SortKey;

        /// <summary>Feature index within its tile — the first stable tiebreak when <see cref="SortKey"/>s are equal.</summary>
        public int FeatureIndex;

        /// <summary>Owning tile id (opaque key) — the second stable tiebreak, guaranteeing a total order.</summary>
        public long TileKey;

        /// <summary>`text-allow-overlap` — skip the collision test and always place this candidate.</summary>
        public bool AllowOverlap;

        /// <summary>`text-ignore-placement` — place this candidate but do NOT let its boxes block later ones.</summary>
        public bool IgnorePlacement;

        /// <summary>Opaque caller ordinal identifying the survivor after the candidate array is sorted in place
        /// (<see cref="SymbolPlacementSystem"/> keys its per-candidate emit data by this).</summary>
        public int SymbolIndex;

        /// <summary>This candidate's cross-frame FADE identity (stable across frames + tile swaps) —
        /// <see cref="SymbolPlacementSystem"/> keys its persistent opacity record by this so a symbol eases in/out
        /// instead of popping. It must be UNIQUE per live candidate (one opacity read-modify-write per id per frame),
        /// or two candidates sharing it fight over one opacity and stick at a partial value. Point symbols: a
        /// fixed-grid quantized-anchor hash folding in the layer (zoom-STABLE, unlike the display-zoom dedup
        /// key); line symbols: a (tile, LAYER, feature, anchor) hash — the layer term is load-bearing because
        /// <c>FeatureIndex</c> restarts per layer (see <see cref="SymbolStagingMath.LineFadeId"/>).</summary>
        public long FadeId;

        /// <summary>Display-time ZOOM GATE: this candidate is EXCLUDED from placement this frame — the greedy never
        /// places it and it never blocks others (as if absent from collision) — because its owning symbol layer is
        /// outside the LIVE camera zoom's <c>minzoom</c>/<c>maxzoom</c> (MapLibre layer visibility). Set on the main
        /// thread just before the collision pass (<c>SymbolPlacementSystem.ApplySuppression</c>) against the live zoom
        /// — NOT the tile build zoom — so overzoomed tiles reveal/hide layers as the camera crosses a boundary. A
        /// suppressed candidate is still STAGED (its quads ease to 0, fading out); only its collision role is removed
        /// — so it fades without blocking the genuine winner. Default false ⇒ every in-zoom candidate
        /// participates.</summary>
        public bool Suppressed;

        /// <summary>This candidate was a SURVIVOR last frame (looked up by <see cref="FadeId"/> against the
        /// placement system's kept-set). It biases <see cref="SymbolCollision.ComparePlacementOrder(in SymbolCandidate,in SymbolCandidate)"/>
        /// as a sticky-placement (hysteresis) tiebreak — at EQUAL <see cref="SortKey"/>, an incumbent places before
        /// a newcomer, so the arbitrary <see cref="FeatureIndex"/>/<see cref="TileKey"/> tiebreak can no longer
        /// flip a near-tied pair frame-to-frame (tile churn / reprojection) → no z-fighting-style flicker. It sits
        /// BELOW SortKey, so any strictly-higher-priority (lower-SortKey) newcomer still wins — incumbency never
        /// blocks a genuinely higher-priority symbol. The placement layer sets this each frame (feedback of history
        /// into collision — the one deliberately-relaxed spot of the "downstream of collision, never fed back" rule).</summary>
        public bool WasPlacedLastFrame;

        /// <summary>
        /// DEBUG-INVARIANT CHECK — the collision consumer's contract on a frame's staged candidate stream. The
        /// staging loop (<see cref="SymbolStagingMath"/>'s StagePoint / StageCurvedAnchor) appends each candidate's
        /// boxes CONTIGUOUSLY, one candidate at a time — never sharing a box, never skipping one — so in staging
        /// order the candidate ranges must TILE <c>[0, boxCount)</c> exactly: <c>candidates[0].BoxStart == 0</c>,
        /// each following <c>BoxStart == previous BoxStart + BoxCount</c>, all counts <c>&gt;= 0</c>, and the last
        /// range ending at <c>boxCount</c>. <see cref="CollisionGridSizing.NodeUpperBoundByCandidates"/> and
        /// the collision job's per-reference insert assume exactly this; a violation means a candidate range is
        /// out-of-range or overlaps another — the never-reproduced dense-scene node-pool overflow. Returns
        /// <c>true</c> with the offending candidate index (or <paramref name="candidates"/><c>.Length</c> when the
        /// ranges under-cover) and the <see cref="BoxStart"/> it should have had; <c>false</c> when the invariant
        /// holds. Pure and allocation-free so a <c>[Conditional("UNITY_ASSERTIONS")]</c> caller can run it every
        /// frame; O(<paramref name="candidates"/>.Length). Pass the candidates already sliced to the live count.
        /// </summary>
        public static bool TryFindRangeTilingViolation(ReadOnlySpan<SymbolCandidate> candidates, int boxCount,
            out int violatingIndex, out int expectedBoxStart)
        {
            int offset = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                SymbolCandidate c = candidates[i];
                if (c.BoxCount < 0 || c.BoxStart != offset || offset + c.BoxCount > boxCount)
                {
                    violatingIndex = i;
                    expectedBoxStart = offset;
                    return true;
                }
                offset += c.BoxCount;
            }
            if (offset != boxCount) // ranges are well-formed but leave a gap: they under-cover [0, boxCount)
            {
                violatingIndex = candidates.Length;
                expectedBoxStart = offset;
                return true;
            }
            violatingIndex = -1;
            expectedBoxStart = 0;
            return false;
        }
    }
}
