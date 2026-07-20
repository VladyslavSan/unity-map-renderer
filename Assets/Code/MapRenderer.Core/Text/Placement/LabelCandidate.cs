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

        /// <summary>A-4: this candidate's cross-frame FADE identity (stable across frames + tile swaps) —
        /// <see cref="LabelPlacementSystem"/> keys its persistent opacity record by this so a label eases in/out
        /// instead of popping. It must be UNIQUE per live candidate (one opacity read-modify-write per id per frame),
        /// or two candidates sharing it fight over one opacity and stick at a partial value. Point labels: a
        /// fixed-grid quantized-anchor hash folding in the layer (zoom-STABLE, unlike the A-3 display-zoom dedup
        /// key); line labels: a (tile, LAYER, feature, anchor) hash — the layer term is load-bearing because
        /// <c>FeatureIndex</c> restarts per layer (see <see cref="LabelStagingMath.LineFadeId"/>).</summary>
        public long FadeId;

        /// <summary>Display-time ZOOM GATE: this candidate is EXCLUDED from placement this frame — the greedy never
        /// places it and it never blocks others (as if absent from collision) — because its owning symbol layer is
        /// outside the LIVE camera zoom's <c>minzoom</c>/<c>maxzoom</c> (MapLibre layer visibility). Set on the main
        /// thread just before the collision pass (<c>LabelPlacementSystem.ApplySuppression</c>) against the live zoom
        /// — NOT the tile build zoom — so overzoomed tiles reveal/hide layers as the camera crosses a boundary. A
        /// suppressed candidate is still STAGED (its quads ease to 0, fading out); only its collision role is removed
        /// — so it fades without blocking the genuine winner. Default false ⇒ every in-zoom candidate participates
        /// exactly as before.</summary>
        public bool Suppressed;

        /// <summary>A-5: this candidate was a SURVIVOR last frame (looked up by <see cref="FadeId"/> against the
        /// placement system's kept-set). It biases <see cref="LabelCollision.ComparePlacementOrder(in LabelCandidate,in LabelCandidate)"/>
        /// as a sticky-placement (hysteresis) tiebreak — at EQUAL <see cref="SortKey"/>, an incumbent places before
        /// a newcomer, so the arbitrary <see cref="FeatureIndex"/>/<see cref="TileKey"/> tiebreak can no longer
        /// flip a near-tied pair frame-to-frame (tile churn / reprojection) → no z-fighting-style flicker. It sits
        /// BELOW SortKey, so any strictly-higher-priority (lower-SortKey) newcomer still wins — incumbency never
        /// blocks a genuinely higher-priority label. The placement layer sets this each frame (feedback of history
        /// into collision — the one deliberately-relaxed spot of the "downstream of SelectSurvivors" rule).</summary>
        public bool WasPlacedLastFrame;
    }
}
