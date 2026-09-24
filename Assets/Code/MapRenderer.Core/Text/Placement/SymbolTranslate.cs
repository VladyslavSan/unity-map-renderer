// TOP-LEVEL `using Unity.Mathematics;`: inside this namespace an inline `Unity.Mathematics.float2` binds
// to a nonexistent nested namespace (CS0234; see SymbolScreenProjection).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// <c>text-translate</c>: shifts a symbol's projected screen anchor by a paint-time pixel offset
    /// (moving its collision box AND its quads together). Pure and engine-free so the sign convention is
    /// unit-testable in isolation (the same factoring as <see cref="SymbolScreenProjection"/>).
    /// </summary>
    public static class SymbolTranslate
    {
        /// <summary>
        /// Apply the pixel offset to a projected screen anchor. <c>text-translate</c> is y-down and
        /// <paramref name="screenPx"/> is y-up, so y is negated, as for <c>text-offset</c>.
        /// <see cref="TextTranslateAnchor.Viewport"/> applies the delta in screen space;
        /// <see cref="TextTranslateAnchor.Map"/> rotates it by the map bearing, with the sign from
        /// <see cref="SymbolBearing.MapAlignedSign"/>.
        /// </summary>
        /// <param name="screenPx">The projected screen anchor (logical px, y-up).</param>
        /// <param name="translatePx">The <c>text-translate</c> offset (logical px, y-down as authored).</param>
        /// <param name="anchor">The <c>text-translate-anchor</c> frame of reference.</param>
        /// <param name="bearingRadians">The current map bearing (heading), radians.</param>
        public static float2 ApplyTranslate(in float2 screenPx, in float2 translatePx, TextTranslateAnchor anchor, float bearingRadians)
        {
            // MapLibre y-down -> screen y-up.
            float2 delta = new float2(translatePx.x, -translatePx.y);

            if (anchor == TextTranslateAnchor.Map && bearingRadians != 0f)
            {
                // Map-anchored: the offset rotates with the map. CCW in the y-up frame; sign via SymbolBearing.
                math.sincos(SymbolBearing.MapAlignedSign * bearingRadians, out float sin, out float cos);
                delta = new float2(cos * delta.x - sin * delta.y, sin * delta.x + cos * delta.y);
            }

            return screenPx + delta;
        }
    }
}
