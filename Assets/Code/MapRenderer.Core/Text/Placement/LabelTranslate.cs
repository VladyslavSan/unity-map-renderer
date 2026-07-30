// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 —
// this file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to a
// (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See the sibling
// LabelScreenProjection header comment for the full explanation of the namespace-collision trap.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Slice C — <c>text-translate</c>: shifts a label's projected screen anchor by a paint-time pixel offset
    /// (moving its collision box AND its quads together). Pure and engine-free so the sign convention is
    /// unit-testable in isolation (the same factoring as <see cref="LabelScreenProjection"/>).
    /// </summary>
    public static class LabelTranslate
    {
        /// <summary>
        /// Apply the pixel offset to a projected screen anchor.
        ///
        /// <para><b>Sign:</b> MapLibre <c>text-translate</c> is y-DOWN (positive y = down); the
        /// <paramref name="screenPx"/> from <see cref="LabelScreenProjection.TryProjectAnchor"/> is y-UP
        /// (origin bottom-left). So down-on-screen is <c>-ty</c> — the same y-down→y-up reconcile as
        /// <c>text-offset</c>. x is unchanged (right = +x in both).</para>
        ///
        /// <para><b>text-translate-anchor:</b> <see cref="TextTranslateAnchor.Viewport"/> applies the delta
        /// in screen space directly; <see cref="TextTranslateAnchor.Map"/> rotates it by the map bearing so
        /// the offset tracks the map (identical at bearing 0). The bearing sign routes through the single
        /// <see cref="LabelBearing.MapAlignedSign"/>, which is pinned by a rendered tooth on the billboard
        /// half of that shared constant — see its own doc.</para>
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
                // Map-anchored: the offset rotates with the map. CCW in the y-up frame; sign via LabelBearing.
                math.sincos(LabelBearing.MapAlignedSign * bearingRadians, out float sin, out float cos);
                delta = new float2(cos * delta.x - sin * delta.y, sin * delta.x + cos * delta.y);
            }

            return screenPx + delta;
        }
    }
}
