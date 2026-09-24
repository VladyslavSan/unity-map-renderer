// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;
using MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The ONE resolver of proposed <see cref="SymbolPairRole"/>s, so the baker and the reconciler agree on
    /// which pairs hold. <c>symbols[i]</c> owns a pair iff it is <see cref="SymbolPairRole.Owner"/>,
    /// <c>symbols[i + 1]</c> exists, is <see cref="SymbolPairRole.Rider"/>, and has the same <c>PairId</c>; the
    /// <see cref="ShapedSymbol"/> overload also matches <c>TileKey</c> and <c>MaterialIndex</c>, because PairId
    /// is unique only per layer and tile. A half-built pair dissolves into two symbols. No allocation, O(1).
    /// </summary>
    public static class SymbolPairing
    {
        /// <summary>True iff <c>symbols[i]</c> is a resolved pair owner, with <paramref name="riderIndex"/> set
        /// to <c>i + 1</c>.</summary>
        public static bool TryGetRider(IReadOnlyList<SymbolFeature> symbols, int i, out int riderIndex)
        {
            riderIndex = -1;
            if (symbols == null || i < 0 || i >= symbols.Count)
                return false;

            SymbolFeature owner = symbols[i];
            if (owner == null || owner.PairRole != SymbolPairRole.Owner)
                return false;

            int j = i + 1;
            if (j >= symbols.Count)
                return false;

            SymbolFeature rider = symbols[j];
            if (rider == null || rider.PairRole != SymbolPairRole.Rider)
                return false;

            if (rider.PairId != owner.PairId)
                return false;

            riderIndex = j;
            return true;
        }

        /// <summary>The <see cref="ShapedSymbol"/> form of the owner→rider resolver — same rule as the
        /// <see cref="SymbolFeature"/> overload plus <c>TileKey</c>/<c>MaterialIndex</c> matching, and no null
        /// check (a scratch record is a value, never null, and the list is dense).
        /// <c>SymbolTileBlockBaker.Fill</c> (Unity assembly) is this overload's production caller.</summary>
        public static bool TryGetRider(IReadOnlyList<ShapedSymbol> symbols, int i, out int riderIndex)
        {
            riderIndex = -1;
            if (symbols == null || i < 0 || i >= symbols.Count)
                return false;

            ShapedSymbol owner = symbols[i];
            if (owner.PairRole != SymbolPairRole.Owner)
                return false;

            int j = i + 1;
            if (j >= symbols.Count)
                return false;

            ShapedSymbol rider = symbols[j];
            if (rider.PairRole != SymbolPairRole.Rider)
                return false;

            if (rider.PairId != owner.PairId)
                return false;
            if (rider.TileKey != owner.TileKey)
                return false;
            if (rider.MaterialIndex != owner.MaterialIndex)
                return false;

            riderIndex = j;
            return true;
        }

        /// <summary>The <see cref="ShapedSymbol"/> form of <c>IsRider</c> — the mirror of
        /// <see cref="TryGetRider(IReadOnlyList{ShapedSymbol},int,out int)"/> at <c>i - 1</c>.</summary>
        public static bool IsRider(IReadOnlyList<ShapedSymbol> symbols, int i)
        {
            if (symbols == null || i <= 0 || i >= symbols.Count)
                return false;

            return TryGetRider(symbols, i - 1, out int riderIndex) && riderIndex == i;
        }
    }
}
