// Engine-free (pure Core types + NUnit) — shared verbatim between the Unity EditMode runner and the fast
// dotnet core-tests project. Covers LabelPairing, the resolver road-shields §10 (docs/road-shields-design.md
// D10) introduces so a proposed Owner/Rider stamping (from SymbolFeatureExtractor) can dissolve into two
// ordinary labels rather than binding an owner to a stranger.

using NUnit.Framework;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class LabelPairingTests
    {
        private static SymbolLabel Symbol(LabelPairRole role, int pairId) =>
            new SymbolLabel { PairRole = role, PairId = pairId };

        private static LabelInstance Instance(LabelPairRole role, int pairId, long tileKey = 0, int materialIndex = 0) =>
            new LabelInstance { PairRole = role, PairId = pairId, TileKey = tileKey, MaterialIndex = materialIndex };

        // --- SymbolLabel overload ---

        [Test]
        public void SymbolLabel_IntactPair_Paired()
        {
            var labels = new[] { Symbol(LabelPairRole.Owner, 7), Symbol(LabelPairRole.Rider, 7) };
            Assert.IsTrue(LabelPairing.TryGetRider(labels, 0, out int riderIndex));
            Assert.AreEqual(1, riderIndex);
        }

        [Test]
        public void SymbolLabel_RiderMissing_ListTruncated_NotPaired()
        {
            var labels = new[] { Symbol(LabelPairRole.Owner, 7) };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        [Test]
        public void SymbolLabel_NextSlotNull_NotPaired()
        {
            var labels = new[] { Symbol(LabelPairRole.Owner, 7), null };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        [Test]
        public void SymbolLabel_MismatchedPairId_NotPaired()
        {
            // [Owner(A), Rider(B)] — the tooth a naive adjacency-only resolver fails.
            var labels = new[] { Symbol(LabelPairRole.Owner, 1), Symbol(LabelPairRole.Rider, 2) };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        [Test]
        public void SymbolLabel_NotAnOwner_NotPaired()
        {
            var labels = new[] { Symbol(LabelPairRole.None, 0), Symbol(LabelPairRole.Rider, 0) };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        // --- LabelInstance overload ---

        [Test]
        public void LabelInstance_IntactPair_Paired()
        {
            var labels = new[] { Instance(LabelPairRole.Owner, 3, tileKey: 9, materialIndex: 2),
                                  Instance(LabelPairRole.Rider, 3, tileKey: 9, materialIndex: 2) };
            Assert.IsTrue(LabelPairing.TryGetRider(labels, 0, out int riderIndex));
            Assert.AreEqual(1, riderIndex);
        }

        [Test]
        public void LabelInstance_RiderMissing_ListTruncated_NotPaired()
        {
            var labels = new[] { Instance(LabelPairRole.Owner, 3) };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        [Test]
        public void LabelInstance_NextSlotNull_NotPaired()
        {
            var labels = new[] { Instance(LabelPairRole.Owner, 3), null };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        [Test]
        public void LabelInstance_MismatchedPairId_NotPaired()
        {
            var labels = new[] { Instance(LabelPairRole.Owner, 1), Instance(LabelPairRole.Rider, 2) };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        [Test]
        public void LabelInstance_MismatchedTileKey_NotPaired()
        {
            var labels = new[] { Instance(LabelPairRole.Owner, 3, tileKey: 1),
                                  Instance(LabelPairRole.Rider, 3, tileKey: 2) };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        [Test]
        public void LabelInstance_MismatchedMaterialIndex_NotPaired()
        {
            var labels = new[] { Instance(LabelPairRole.Owner, 3, materialIndex: 1),
                                  Instance(LabelPairRole.Rider, 3, materialIndex: 2) };
            Assert.IsFalse(LabelPairing.TryGetRider(labels, 0, out _));
        }

        // --- IsRider (the mirror at i-1) ---

        [Test]
        public void IsRider_MatchingOwnerBefore_True()
        {
            var labels = new[] { Instance(LabelPairRole.Owner, 3), Instance(LabelPairRole.Rider, 3) };
            Assert.IsTrue(LabelPairing.IsRider(labels, 1));
        }

        [Test]
        public void IsRider_OrphanRider_NoOwnerBefore_False()
        {
            var labels = new[] { Instance(LabelPairRole.Rider, 3) };
            Assert.IsFalse(LabelPairing.IsRider(labels, 0));
        }

        [Test]
        public void IsRider_PrecedingLabelIsNotAnOwner_False()
        {
            var labels = new[] { Instance(LabelPairRole.None, 0), Instance(LabelPairRole.Rider, 3) };
            Assert.IsFalse(LabelPairing.IsRider(labels, 1));
        }

        [Test]
        public void IsRider_IndexZero_False()
        {
            var labels = new[] { Instance(LabelPairRole.Rider, 3) };
            Assert.IsFalse(LabelPairing.IsRider(labels, 0));
        }
    }
}
