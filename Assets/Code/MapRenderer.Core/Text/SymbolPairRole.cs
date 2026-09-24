// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// An icon+text symbol (<c>docs/road-shields-design.md</c>) is ONE placement instance: the icon is the
    /// <see cref="Owner"/> (collision candidate, dedup key, fade id) and the text a <see cref="Rider"/> with no
    /// identity of its own. <see cref="None"/> is the zero value, so an initializer that omits it is unpaired.
    /// The extractor only proposes roles; <see cref="Placement.SymbolPairing"/> dissolves a half-built pair
    /// back into two ordinary symbols.
    /// </summary>
    public enum SymbolPairRole
    {
        None = 0,
        Owner,
        Rider,
    }
}
