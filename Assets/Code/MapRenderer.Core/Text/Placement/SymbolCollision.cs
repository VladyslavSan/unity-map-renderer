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
            // Hysteresis below SortKey: at equal sort key an incumbent sorts first, so the arbitrary tiebreak
            // below cannot flicker; incumbency only raises priority, so the survivor set is a fixed point.
            if (a.WasPlacedLastFrame != b.WasPlacedLastFrame) return a.WasPlacedLastFrame ? -1 : 1;
            if (a.FeatureIndex != b.FeatureIndex) return a.FeatureIndex < b.FeatureIndex ? -1 : 1;
            if (a.TileKey != b.TileKey) return a.TileKey < b.TileKey ? -1 : 1;
            // FadeId, unique per candidate, makes the order total: a curved feature's anchors share every key
            // above, and with the unstable heapsort that drives a limit cycle through incumbency on a still camera.
            if (a.FadeId != b.FadeId) return a.FadeId < b.FadeId ? -1 : 1;
            return 0;
        }
    }
}
