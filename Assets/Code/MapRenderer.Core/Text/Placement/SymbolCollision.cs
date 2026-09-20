// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 (the
// namespace-collision trap — see SymbolBox.cs's header comment).

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
        /// first (MapLibre priority), then the A-5 incumbency bias, then LOWER
        /// <see cref="SymbolCandidate.FeatureIndex"/>, then LOWER <see cref="SymbolCandidate.TileKey"/>, then
        /// LOWER <see cref="SymbolCandidate.FadeId"/>.
        /// </summary>
        public static int ComparePlacementOrder(in SymbolCandidate a, in SymbolCandidate b)
        {
            if (a.SortKey < b.SortKey) return -1;
            if (a.SortKey > b.SortKey) return 1;
            // A-5 hysteresis: at EQUAL sort key, an incumbent (placed last frame) sorts first, so the greedy pass
            // keeps it over a newcomer that would otherwise win only on the arbitrary feature/tile tiebreak below
            // (the tile-churn / reprojection flip that reads as flicker). Strictly BELOW SortKey: a lower-SortKey
            // newcomer still sorts first and wins, so incumbency never blocks a genuinely higher-priority symbol.
            // Since incumbency only ever RAISES priority, last frame's survivor set is a one-step fixed point (no
            // oscillation) — and on a static frame the survivors are unchanged, so B-1's byte-identical skip holds.
            if (a.WasPlacedLastFrame != b.WasPlacedLastFrame) return a.WasPlacedLastFrame ? -1 : 1;
            if (a.FeatureIndex != b.FeatureIndex) return a.FeatureIndex < b.FeatureIndex ? -1 : 1;
            if (a.TileKey != b.TileKey) return a.TileKey < b.TileKey ? -1 : 1;
            // STRICT total order: a curved feature's repeated anchors all share (SortKey, FeatureIndex, TileKey), so
            // WITHOUT this final key they compare EQUAL — the heapsort's tie-resolution is then unstable and the A-5
            // incumbency feedback (WasPlacedLastFrame reflects last frame's survivors) can drive a limit CYCLE: the
            // placed-anchor subset oscillates frame-to-frame even on a STILL camera, flipping which neighbours are
            // blocked so their collision losers never finish fading (the "overlapping line symbols, one won't fade"
            // bug). FadeId is the anchor's stable per-frame identity (LineFadeId(tile,feature,anchorIndex) / the point
            // fade id), unique per candidate, so it makes the order TOTAL — restoring the one-step fixed point the
            // A-5 comment above relies on. Distinct-FeatureIndex symbols never reach here, so existing behaviour and
            // the permutation-invariance/hysteresis teeth are unchanged.
            if (a.FadeId != b.FadeId) return a.FadeId < b.FadeId ? -1 : 1;
            return 0;
        }
    }
}
