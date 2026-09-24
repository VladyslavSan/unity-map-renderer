// Engine-free: no UnityEngine dependency.

using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The single owner of the SIGN conventions for symbol orientation: the map-bearing sign (the billboard
    /// rotation for <c>text-rotation-alignment:map</c> and the offset rotation for
    /// <c>text-translate-anchor:map</c>) and the <c>icon-rotate</c> sense conversion. Everything that turns a
    /// symbol routes through here, so each "which way does this turn" question has ONE place.
    ///
    /// <para>Non-local invariant: both signs are MEASURED, each by a rendered tooth at an angle where its two
    /// candidate values are distinguishable. A sign argued from the surrounding code's stated conventions can
    /// have a plausible derivation, pass every other test, and still be inverted by 180°. See each member's
    /// own doc for the tooth that pins it.</para>
    /// </summary>
    public static class SymbolBearing
    {
        /// <summary>The map-bearing sign for map-aligned orientation (<c>text-rotation-alignment:map</c> and
        /// <c>text-translate-anchor:map</c>): +1 = a heading of θ (CW from north) turns map-aligned symbols by +θ
        /// in the y-up screen frame. Non-obvious why: the sign is measured, and only
        /// <c>SymbolIconRenderSnapshotTests.MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing</c> (ink
        /// centroid at heading 45°) observes it; the rest of the suite stays green if it flips, so re-measure.
        /// </summary>
        public const float MapAlignedSign = 1f;

        /// <summary>
        /// The screen-space angle (radians) to rotate a symbol's billboard by, given its
        /// <c>text-rotation-alignment</c> and the current map <paramref name="bearingRadians"/>.
        /// <see cref="AlignmentMode.Map"/> → the bearing (× <see cref="MapAlignedSign"/>);
        /// <see cref="AlignmentMode.Viewport"/> and <see cref="AlignmentMode.Auto"/> (which resolves to
        /// viewport for point placement, the only path that calls this) → no rotation.
        /// </summary>
        public static float BillboardRotationRadians(AlignmentMode rotationAlignment, float bearingRadians)
            => rotationAlignment == AlignmentMode.Map ? MapAlignedSign * bearingRadians : 0f;

        /// <summary>
        /// <c>icon-rotate</c> (MapLibre: positive = clockwise on screen) in the staging frame's sense (positive =
        /// counter-clockwise on screen): the single negation between them. Both icon paths call it, so every
        /// carrier keeps MapLibre's sense and it flips once. Non-obvious why: the staging sense is measured,
        /// because <c>BillboardMath.BuildWorldQuad</c> rotates in a y-up frame and then negates Y
        /// (<c>SymbolIconRenderSnapshotTests.AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen</c>).
        /// It is <c>0f - x</c>, not <c>-x</c>, so unrotated text stays <c>+0f</c> and byte-identical.
        /// </summary>
        public static float IconRotationRadians(float iconRotateRadians) => 0f - iconRotateRadians;
    }
}
