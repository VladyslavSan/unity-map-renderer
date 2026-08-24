// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// I3 — distinguishes a <see cref="Style.Symbol.SymbolFeature"/>'s payload: a shaped text run
    /// (<see cref="Style.Symbol.SymbolFeature.Text"/>) or a sprite icon (<see cref="Style.Symbol.SymbolFeature.IconQuad"/>).
    /// <see cref="Text"/> is the zero value so every existing text symbol (which never sets
    /// <see cref="Style.Symbol.SymbolFeature.Kind"/>) stays <see cref="Text"/> by default — byte-identical to
    /// pre-I3 behaviour.
    /// </summary>
    public enum SymbolKind
    {
        Text = 0,
        Icon,
    }
}
