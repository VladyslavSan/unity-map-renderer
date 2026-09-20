// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Top-level `using Unity.Mathematics;` + unqualified float2 (namespace-collision trap
// — see SymbolBox.cs's header comment).

using NUnit.Framework;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// The one direct tooth for <see cref="SymbolCollision.ComparePlacementOrder(in SymbolCandidate,in SymbolCandidate)"/>:
    /// the placement order key is (SortKey, WasPlacedLastFrame, FeatureIndex, TileKey, FadeId), each term
    /// breaking a tie left by the one before it. Every other collision property lives on
    /// <c>CollisionJobPlacementTests</c> and <c>CollisionGridContractTests</c> — both need the job runtime
    /// (<c>NativeArray</c>/<c>IJob</c>) and so stay Unity-only, while this comparator is plain arithmetic and
    /// stays engine-free.
    /// </summary>
    [TestFixture]
    public class SymbolCollisionTests
    {
        private static SymbolCandidate Candidate(float sortKey, bool wasPlacedLastFrame, int featureIndex,
            long tileKey, long fadeId)
            => new SymbolCandidate
            {
                SortKey = sortKey, WasPlacedLastFrame = wasPlacedLastFrame, FeatureIndex = featureIndex,
                TileKey = tileKey, FadeId = fadeId,
            };

        [Test]
        public void ComparePlacementOrder_RanksSortKeyIncumbencyFeatureTileFade()
        {
            // Sort key dominates every tiebreak field.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 1f, wasPlacedLastFrame: false, featureIndex: 9, tileKey: 9, fadeId: 9),
                Candidate(sortKey: 2f, wasPlacedLastFrame: true, featureIndex: 0, tileKey: 0, fadeId: 0)), 0,
                "a lower sort key must order first regardless of every tiebreak field");

            // Equal sort key -> incumbency (A-5 hysteresis) breaks the tie.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: true, featureIndex: 9, tileKey: 9, fadeId: 9),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 0, tileKey: 0, fadeId: 0)), 0,
                "equal sort keys order the incumbent first regardless of feature/tile/fade");

            // Equal sort key and incumbency -> feature index breaks the tie.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 0, tileKey: 9, fadeId: 9),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 1, tileKey: 0, fadeId: 0)), 0,
                "equal sort key and incumbency order by feature index (a bare compare would return 0 here)");

            // Equal sort key, incumbency, and feature index -> tile key breaks the tie.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 9),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 200, fadeId: 0)), 0,
                "equal sort key, incumbency and feature index order by tile key");

            // Equal sort key, incumbency, feature index and tile key -> FadeId breaks the tie — the term that
            // makes the order STRICTLY total for a curved feature's repeated anchors (which share all four).
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 100),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 200)), 0,
                "equal sort key, incumbency, feature index and tile key order by FadeId");

            // Fully equal -> 0.
            Assert.AreEqual(0, SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 100),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 100)));
        }
    }
}
