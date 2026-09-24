// Engine-free: no UnityEngine dependency. Pure data carrier (no Unity.Mathematics types) — no
// namespace-collision guard needed.

using System;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One all-or-nothing collision candidate over a CONTIGUOUS range of <see cref="SymbolBox"/>es: one box for a
    /// point symbol, one per glyph for a curved symbol. <c>CollisionJob</c> places it only if every box is free,
    /// then reserves them all. Only candidates are sorted; the flat <c>boxes[]</c> stays put, so
    /// <see cref="BoxStart"/> stays valid after the sort. All value fields, like <see cref="SymbolBox"/>.
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

        /// <summary>`icon-optional` / `text-optional` INPUT: bit <c>b</c> set ⇒ box <c>BoxStart + b</c> (and emit
        /// <c>EmitStart + b</c>) may drop alone when it overlaps a blocker. Non-local invariant: only a point PAIR
        /// may set it, because <c>AppendPointHalf</c> appends one box with one emit per half, while a curved
        /// symbol's glyph boxes share one emit. 0 elsewhere keeps the all-or-nothing rule.</summary>
        public byte OptionalBoxMask;

        /// <summary>VERDICT: bit <c>b</c> set ⇒ box <c>BoxStart + b</c> overlapped a placed blocker
        /// and was dropped (not reserved, not drawn); a subset of <see cref="OptionalBoxMask"/>, 0 if the candidate
        /// did not place. Non-local invariant: <c>StagePointPair</c> seeds it with last frame's verdict for the
        /// emit loop, and collision then overwrites it; safe because emit reads it before collision is
        /// scheduled.</summary>
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

        /// <summary>This candidate's cross-frame FADE identity, which keys <see cref="SymbolPlacementSystem"/>'s
        /// opacity record. It must be unique per live candidate, or two candidates fight over one opacity. Point:
        /// a quantized-anchor hash with the layer; line: <see cref="SymbolStagingMath.LineFadeId"/>.</summary>
        public long FadeId;

        /// <summary>Display-time ZOOM GATE: this candidate is EXCLUDED from placement this frame — the greedy never
        /// places it and it never blocks others, because its layer is outside the LIVE zoom's
        /// <c>minzoom</c>/<c>maxzoom</c>. <c>SymbolPlacementSystem.ApplySuppression</c> sets it before collision,
        /// against the live zoom rather than the build zoom. A suppressed candidate still stages and fades out
        /// without blocking the winner.</summary>
        public bool Suppressed;

        /// <summary>This candidate was a SURVIVOR last frame (looked up by <see cref="FadeId"/> against the
        /// placement system's kept-set). At equal <see cref="SortKey"/> an incumbent places first in
        /// <see cref="SymbolCollision.ComparePlacementOrder(in SymbolCandidate,in SymbolCandidate)"/>, so near-tied
        /// pairs do not flicker; a lower-SortKey newcomer still wins. It is the one place history feeds back into
        /// collision.</summary>
        public bool WasPlacedLastFrame;

        /// <summary>
        /// Debug check of the collision contract: in staging order the candidate box ranges tile
        /// <c>[0, boxCount)</c> exactly, as <see cref="CollisionGridSizing.NodeUpperBoundByCandidates"/> assumes; a
        /// violation can overflow the grid's node pool. Returns <c>true</c> with the offending index (or
        /// <c>candidates.Length</c> on under-cover) and the expected <see cref="BoxStart"/>. Allocation-free for a
        /// per-frame <c>[Conditional("UNITY_ASSERTIONS")]</c> caller; pass candidates sliced to the live count.
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
