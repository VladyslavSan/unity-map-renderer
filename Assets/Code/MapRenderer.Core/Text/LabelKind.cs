// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// I3 — distinguishes a <see cref="Style.Symbol.SymbolLabel"/>'s payload: a shaped text run
    /// (<see cref="Style.Symbol.SymbolLabel.Text"/>) or a sprite icon (<see cref="Style.Symbol.SymbolLabel.IconQuad"/>).
    /// <see cref="Text"/> is the zero value so every existing text label (which never sets
    /// <see cref="Style.Symbol.SymbolLabel.Kind"/>) stays <see cref="Text"/> by default — byte-identical to
    /// pre-I3 behaviour.
    /// </summary>
    public enum LabelKind
    {
        Text = 0,
        Icon,
    }
}
