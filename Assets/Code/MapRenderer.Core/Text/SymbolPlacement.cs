// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre <c>symbol-placement</c>: how a symbol layer's labels are placed relative to the feature
    /// geometry. <see cref="Point"/> (the style-spec default, and the zero value) = one label per point;
    /// <see cref="Line"/> = repeated along a line at <c>symbol-spacing</c> intervals; <see cref="LineCenter"/>
    /// = one label at the center of a line. Lives in <c>Core.Text</c> (not <c>Style.Symbol</c>) so the
    /// rendering-side carriers (<c>LabelInstance</c>, in <c>Core.Text.Placement</c>) can reference it without
    /// a dependency back onto the style layer.
    /// </summary>
    public enum SymbolPlacement
    {
        Point = 0,
        Line,
        LineCenter,
    }
}
