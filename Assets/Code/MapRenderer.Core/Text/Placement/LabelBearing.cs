// Engine-free: no UnityEngine dependency.

using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The single owner of the map-bearing sign for label orientation — both the billboard rotation
    /// (<c>text-rotation-alignment:map</c>) and the offset rotation (<c>text-translate-anchor:map</c>) route
    /// through it, so the whole "which way does a map-aligned label turn under bearing" question is ONE
    /// flippable constant, not sign math scattered across sites.
    ///
    /// <para><b>Visual-verify handoff:</b> a non-zero map bearing is not headlessly testable — every headless
    /// test runs at bearing 0, where map- and viewport-alignment coincide. So <see cref="MapAlignedSign"/> is
    /// <em>chosen</em>, not derived; if map-aligned labels/offsets turn the wrong way in the Editor under an
    /// active bearing, flip this one constant. The rotation <em>math</em> (rotate a vector/quad by a given
    /// angle) is fully headless-tested; only this sign is deferred to an eyeball pass.</para>
    /// </summary>
    public static class LabelBearing
    {
        /// <summary>The map-bearing sign for map-aligned label orientation. +1 = a map heading of θ (CW from
        /// north) turns map-aligned labels by +θ in the screen's (y-up) frame. Flip to -1 if that is backwards.</summary>
        public const float MapAlignedSign = 1f;

        /// <summary>
        /// The screen-space angle (radians) to rotate a label's billboard by, given its
        /// <c>text-rotation-alignment</c> and the current map <paramref name="bearingRadians"/>.
        /// <see cref="AlignmentMode.Map"/> → the bearing (× <see cref="MapAlignedSign"/>);
        /// <see cref="AlignmentMode.Viewport"/> and <see cref="AlignmentMode.Auto"/> (which resolves to
        /// viewport for the point placement this renderer emits today) → no rotation.
        /// </summary>
        public static float BillboardRotationRadians(AlignmentMode rotationAlignment, float bearingRadians)
            => rotationAlignment == AlignmentMode.Map ? MapAlignedSign * bearingRadians : 0f;
    }
}
