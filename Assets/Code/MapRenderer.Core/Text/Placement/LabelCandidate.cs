// Engine-free: no UnityEngine dependency. Pure data carrier (no Unity.Mathematics types) — no
// namespace-collision guard needed.

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// #5 (B3): one collision candidate spanning a CONTIGUOUS range of <see cref="LabelBox"/>es —
    /// all-or-nothing. A POINT label is a single-box candidate (its whole-label AABB); a CURVED along-line
    /// label is an N-box candidate (one AABB per glyph). <see cref="LabelCollision.SelectSurvivors(LabelCandidate[],int,LabelBox[],int,bool[],LabelCollisionGrid)"/>
    /// places a candidate iff EVERY box in its range is free, and — if placed — reserves ALL of them; so a
    /// curved label drops entirely when any one glyph collides, and blocks others across its whole run.
    ///
    /// <para>Only the CANDIDATES are sorted into placement order; the flat <c>boxes[]</c> array stays put
    /// (the grid stores absolute box indices), so <see cref="BoxStart"/> keeps addressing the same boxes
    /// after the sort. Blittable-friendly (all value fields) for a future Burst pass, mirroring
    /// <see cref="LabelBox"/>.</para>
    /// </summary>
    public struct LabelCandidate
    {
        /// <summary>Index of this candidate's FIRST box in the caller's flat <c>boxes[]</c> array.</summary>
        public int BoxStart;

        /// <summary>Number of boxes in this candidate's range (1 for a point label, N glyphs for a curved one).</summary>
        public int BoxCount;

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
        /// (<see cref="LabelPlacementSystem"/> keys its per-candidate emit data by this).</summary>
        public int LabelIndex;
    }
}
