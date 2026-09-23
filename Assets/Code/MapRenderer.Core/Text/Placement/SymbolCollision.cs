// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The two collision primitives <see cref="MapRenderer.Jobs.Symbols.CollisionJob"/> sorts and tests by:
    /// the greedy placement order and the AABB overlap test. The greedy survivor selection itself
    /// (sort, then test-all-then-insert over a spatial grid) is <see cref="MapRenderer.Jobs.Symbols.CollisionJob"/> —
    /// the Burst-compiled per-frame path; this class holds only the shared math both a job and a test can
    /// call directly.
    /// </summary>
    public static class SymbolCollision
    {
        /// <summary>Half-open AABB overlap test (touching edges do NOT count as overlapping).</summary>
        public static bool Overlaps(in SymbolBox a, in SymbolBox b)
            => a.Min.x < b.Max.x && a.Max.x > b.Min.x &&
               a.Min.y < b.Max.y && a.Max.y > b.Min.y;

        /// <summary>
        /// The total placement order for <see cref="SymbolCandidate"/>s: LOWER <see cref="SymbolCandidate.SortKey"/>
        /// first (MapLibre priority), then the incumbency bias, then LOWER
        /// <see cref="SymbolCandidate.FeatureIndex"/>, then LOWER <see cref="SymbolCandidate.TileKey"/>, then
        /// LOWER <see cref="SymbolCandidate.FadeId"/>.
        /// </summary>
        public static int ComparePlacementOrder(in SymbolCandidate a, in SymbolCandidate b)
        {
            if (a.SortKey < b.SortKey) return -1;
            if (a.SortKey > b.SortKey) return 1;
            // Hysteresis: at EQUAL sort key, an incumbent (placed last frame) sorts first, so the greedy pass keeps
            // it over a newcomer that would otherwise win only on the arbitrary feature/tile tiebreak below (the
            // tile-churn / reprojection flip that reads as flicker). Strictly BELOW SortKey: a lower-SortKey
            // newcomer still sorts first and wins, so incumbency never blocks a higher-priority symbol. Since
            // incumbency only ever RAISES priority, last frame's survivor set is a one-step fixed point (no
            // oscillation).
            if (a.WasPlacedLastFrame != b.WasPlacedLastFrame) return a.WasPlacedLastFrame ? -1 : 1;
            if (a.FeatureIndex != b.FeatureIndex) return a.FeatureIndex < b.FeatureIndex ? -1 : 1;
            if (a.TileKey != b.TileKey) return a.TileKey < b.TileKey ? -1 : 1;
            // STRICT total order: a curved feature's repeated anchors all share (SortKey, FeatureIndex, TileKey), so
            // WITHOUT this final key they compare EQUAL. The heapsort's tie-resolution is then unstable, and the
            // incumbency feedback (WasPlacedLastFrame reflects last frame's survivors) can drive a limit CYCLE: the
            // placed-anchor subset oscillates frame-to-frame even on a STILL camera, flipping which neighbours are
            // blocked, so their collision losers never finish fading. FadeId is the anchor's stable per-frame
            // identity (LineFadeId(tile,feature,anchorIndex) / the point fade id), unique per candidate, so it
            // makes the order TOTAL and keeps the one-step fixed point. Distinct-FeatureIndex symbols never reach here.
            if (a.FadeId != b.FadeId) return a.FadeId < b.FadeId ? -1 : 1;
            return 0;
        }
    }
}
