// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Road-shields stage 3 (§10 D8/D9, <c>docs/road-shields-design.md</c>) — a centred icon+text symbol is
    /// modelled as ONE placement instance: the icon is the <see cref="Owner"/> (it holds the pair's collision
    /// candidate, dedup key and fade id), the text is a <see cref="Rider"/> with no identity of its own.
    /// <see cref="None"/> is the zero value so every existing object initializer that omits this member stays
    /// unpaired — byte-identical to pre-pairing behaviour. The roles are a PROPOSAL stamped by the extractor;
    /// <see cref="Placement.LabelPairing"/> resolves whether a proposed pair actually holds (a half-built pair
    /// — e.g. a rider dropped by per-label shaping isolation — dissolves back into two ordinary labels).
    /// </summary>
    public enum LabelPairRole
    {
        None = 0,
        Owner,
        Rider,
    }
}
