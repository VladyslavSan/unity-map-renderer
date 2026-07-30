// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;
using MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Road-shields §10 D10 — the ONE resolver that turns a proposed <see cref="LabelPairRole"/> stamping into
    /// a resolved pair. The extractor's roles are a PROPOSAL, not a fact (a rider can go missing to per-label
    /// shaping isolation, list truncation, or a downstream filter); this class decides the truth from a list,
    /// so the baker and the reconciler cannot disagree on which pairs actually hold.
    ///
    /// Rule (identical for both carriers): <c>labels[i]</c> is a paired owner iff it is non-null,
    /// <see cref="LabelPairRole.Owner"/>, <c>i + 1 &lt; Count</c>, <c>labels[i + 1]</c> is non-null,
    /// <see cref="LabelPairRole.Rider"/>, and the two <c>PairId</c>s are equal — for <see cref="LabelInstance"/>,
    /// <c>TileKey</c> and <c>MaterialIndex</c> must also match, since <c>FeatureIndex</c>/<c>PairId</c> is only
    /// unique within one <c>Extract</c> call (per layer, per tile). A half-built pair dissolves into two
    /// ordinary labels — never an owner bound to a stranger. Null-tolerant throughout (the baker's null-slot
    /// invariant, <c>SymbolTileLabelBlockBaker.cs:182</c>). No allocation, O(1).
    /// </summary>
    public static class LabelPairing
    {
        /// <summary>True iff <c>labels[i]</c> is a resolved pair owner, with <paramref name="riderIndex"/> set
        /// to <c>i + 1</c>.</summary>
        public static bool TryGetRider(IReadOnlyList<SymbolLabel> labels, int i, out int riderIndex)
        {
            riderIndex = -1;
            if (labels == null || i < 0 || i >= labels.Count)
                return false;

            SymbolLabel owner = labels[i];
            if (owner == null || owner.PairRole != LabelPairRole.Owner)
                return false;

            int j = i + 1;
            if (j >= labels.Count)
                return false;

            SymbolLabel rider = labels[j];
            if (rider == null || rider.PairRole != LabelPairRole.Rider)
                return false;

            if (rider.PairId != owner.PairId)
                return false;

            riderIndex = j;
            return true;
        }

        /// <summary>True iff <c>labels[i]</c> is a resolved pair owner, with <paramref name="riderIndex"/> set
        /// to <c>i + 1</c>. Also requires <c>TileKey</c>/<c>MaterialIndex</c> to match — <c>PairId</c> alone
        /// (the owner's <c>FeatureIndex</c>) is only unique per layer/tile.</summary>
        public static bool TryGetRider(IReadOnlyList<LabelInstance> labels, int i, out int riderIndex)
        {
            riderIndex = -1;
            if (labels == null || i < 0 || i >= labels.Count)
                return false;

            LabelInstance owner = labels[i];
            if (owner == null || owner.PairRole != LabelPairRole.Owner)
                return false;

            int j = i + 1;
            if (j >= labels.Count)
                return false;

            LabelInstance rider = labels[j];
            if (rider == null || rider.PairRole != LabelPairRole.Rider)
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

        /// <summary>True iff <c>labels[i]</c> is a resolved pair RIDER — the mirror of <see cref="TryGetRider(IReadOnlyList{LabelInstance},int,out int)"/>
        /// at <c>i - 1</c>. False for an orphan rider (no matching owner immediately before it).</summary>
        public static bool IsRider(IReadOnlyList<LabelInstance> labels, int i)
        {
            if (labels == null || i <= 0 || i >= labels.Count)
                return false;

            return TryGetRider(labels, i - 1, out int riderIndex) && riderIndex == i;
        }
    }
}
