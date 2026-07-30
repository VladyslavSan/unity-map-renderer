// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// D3 (road-shields epic, docs/road-shields-design.md §3) — resolves the Style Spec's <c>auto</c>
    /// <see cref="AlignmentMode"/> default against the owning symbol layer's <c>symbol-placement</c>:
    /// <c>auto</c> resolves to <see cref="AlignmentMode.Map"/> under <see cref="SymbolPlacement.Line"/> /
    /// <see cref="SymbolPlacement.LineCenter"/>, and to <see cref="AlignmentMode.Viewport"/> under
    /// <see cref="SymbolPlacement.Point"/>. <see cref="AlignmentMode.Map"/>/<see cref="AlignmentMode.Viewport"/>
    /// pass through unchanged regardless of placement (an explicit style choice is never overridden).
    ///
    /// <para>Not cosmetic: <c>waterway_line_label</c>, <c>water_name_line_label</c> and
    /// <c>road_one_way_arrow*</c> all leave <c>text-rotation-alignment</c>/<c>icon-rotation-alignment</c>
    /// unset (Auto). If Auto resolved to Viewport under line placement, those layers would flip from their
    /// intended curved/along-line look to upright/screen-aligned — this resolver is what keeps them curved.</para>
    /// </summary>
    public static class AlignmentResolution
    {
        /// <summary>Resolves <paramref name="mode"/> against <paramref name="placement"/> per the Style
        /// Spec's <c>auto</c> default. Non-auto modes pass through untouched.</summary>
        public static AlignmentMode Resolve(AlignmentMode mode, SymbolPlacement placement)
        {
            if (mode != AlignmentMode.Auto) return mode;
            return placement == SymbolPlacement.Point ? AlignmentMode.Viewport : AlignmentMode.Map;
        }
    }
}
