// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre <c>symbol-placement</c>: how a symbol layer's symbols are placed relative to the feature
    /// geometry. <see cref="Point"/> (the style-spec default, and the zero value) = one symbol per point;
    /// <see cref="Line"/> = repeated along a line at <c>symbol-spacing</c> intervals; <see cref="LineCenter"/>
    /// = one symbol at the center of a line.
    /// </summary>
    public enum SymbolPlacement
    {
        Point = 0,
        Line,
        LineCenter,
    }
}
