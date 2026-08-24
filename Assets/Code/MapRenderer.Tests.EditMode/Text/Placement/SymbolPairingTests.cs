// Engine-free (pure Core types + NUnit) — shared verbatim between the Unity EditMode runner and the fast
// dotnet core-tests project. Covers SymbolPairing, the resolver road-shields §10 (docs/road-shields-design.md
// D10) introduces so a proposed Owner/Rider stamping (from SymbolFeatureExtractor) can dissolve into two
// ordinary symbols rather than binding an owner to a stranger.

using NUnit.Framework;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolPairingTests
    {
        private static SymbolFeature Symbol(SymbolPairRole role, int pairId) =>
            new SymbolFeature { PairRole = role, PairId = pairId };

        private static ShapedSymbol ShapedSymbol(SymbolPairRole role, int pairId, long tileKey = 0, int materialIndex = 0) =>
            new ShapedSymbol { PairRole = role, PairId = pairId, TileKey = tileKey, MaterialIndex = materialIndex };

        // --- SymbolFeature overload ---

        [Test]
        public void SymbolFeature_IntactPair_Paired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 7), Symbol(SymbolPairRole.Rider, 7) };
            Assert.IsTrue(SymbolPairing.TryGetRider(symbols, 0, out int riderIndex));
            Assert.AreEqual(1, riderIndex);
        }

        [Test]
        public void SymbolFeature_RiderMissing_ListTruncated_NotPaired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 7) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void SymbolFeature_NextSlotNull_NotPaired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 7), null };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void SymbolFeature_MismatchedPairId_NotPaired()
        {
            // [Owner(A), Rider(B)] — the tooth a naive adjacency-only resolver fails.
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 1), Symbol(SymbolPairRole.Rider, 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void SymbolFeature_NotAnOwner_NotPaired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.None, 0), Symbol(SymbolPairRole.Rider, 0) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        // --- ShapedSymbol overload (the live production carrier that Bake resolves; the overload over the
        //     pre-migration per-symbol managed carrier was retired along with that carrier) ---

        [Test]
        public void ShapedSymbol_IntactPair_Paired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3, tileKey: 9, materialIndex: 2),
                                  ShapedSymbol(SymbolPairRole.Rider, 3, tileKey: 9, materialIndex: 2) };
            Assert.IsTrue(SymbolPairing.TryGetRider(symbols, 0, out int riderIndex));
            Assert.AreEqual(1, riderIndex);
        }

        [Test]
        public void ShapedSymbol_RiderMissing_ListTruncated_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_NextSlotNotRider_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3), ShapedSymbol(SymbolPairRole.None, 3) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_MismatchedPairId_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 1), ShapedSymbol(SymbolPairRole.Rider, 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_MismatchedTileKey_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3, tileKey: 1),
                                  ShapedSymbol(SymbolPairRole.Rider, 3, tileKey: 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_MismatchedMaterialIndex_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3, materialIndex: 1),
                                  ShapedSymbol(SymbolPairRole.Rider, 3, materialIndex: 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        // --- IsRider (the mirror at i-1) ---

        [Test]
        public void IsRider_MatchingOwnerBefore_True()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3), ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsTrue(SymbolPairing.IsRider(symbols, 1));
        }

        [Test]
        public void IsRider_OrphanRider_NoOwnerBefore_False()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsFalse(SymbolPairing.IsRider(symbols, 0));
        }

        [Test]
        public void IsRider_PrecedingSymbolIsNotAnOwner_False()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.None, 0), ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsFalse(SymbolPairing.IsRider(symbols, 1));
        }

        [Test]
        public void IsRider_IndexZero_False()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsFalse(SymbolPairing.IsRider(symbols, 0));
        }
    }
}
