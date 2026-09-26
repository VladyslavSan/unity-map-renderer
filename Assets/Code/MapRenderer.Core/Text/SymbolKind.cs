// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Distinguishes a <see cref="Placement.SymbolFeature"/>'s payload: a shaped text run
    /// (<see cref="Placement.SymbolFeature.Text"/>) or a sprite icon (<see cref="Placement.SymbolFeature.IconQuad"/>).
    /// <see cref="Text"/> is the zero value so every existing text symbol (which never sets
    /// <see cref="Placement.SymbolFeature.Kind"/>) stays <see cref="Text"/> by default.
    /// </summary>
    public enum SymbolKind
    {
        Text = 0,
        Icon,
    }
}
