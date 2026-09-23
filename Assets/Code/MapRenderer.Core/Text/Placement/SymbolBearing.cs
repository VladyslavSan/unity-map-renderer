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
        /// <summary>The map-bearing sign for map-aligned symbol orientation (the <c>text-rotation-alignment:map</c>
        /// billboard and the <c>text-translate-anchor:map</c> offset). +1 = a map heading of θ (CW from north)
        /// turns map-aligned symbols by +θ in the screen's (y-up) frame.
        ///
        /// <para><b>MEASURED, not chosen.</b> A headless camera takes a non-zero heading like any other, so the
        /// sign is headlessly testable. Pinned end-to-end by the rendered tooth
        /// <c>SymbolIconRenderSnapshotTests.MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing</c>, which
        /// renders a map-aligned point icon at heading 45° — not 0 (map and viewport alignment coincide), not
        /// 180° (its own inverse), not axis-aligned (cannot separate +θ from −θ) — and measures ink CENTROID, a
        /// bounding box being direction-blind. It asserts the symbol turns +45° counter-clockwise on screen AND
        /// that this matches, to within a couple of degrees, the turn of the map itself, measured independently
        /// as where the camera projects an off-centre anchor placed due map-east. Flipping this constant to -1
        /// puts that tooth 91.5° out.</para>
        ///
        /// <para>Nothing else catches it: under an inverted sign the whole rest of the placement suite stays
        /// green, including <c>SymbolBearingTests</c> (which multiplies by this very constant) and
        /// <c>SymbolPlacementStructureTests</c>'s bearing case (which asserts the billboard rotates, not which
        /// way). So a change here must be re-measured, never reasoned.</para>
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
        /// <c>icon-rotate</c> (MapLibre: positive turns the icon <b>clockwise on screen</b>) expressed in the
        /// staging frame's rotation sense, where positive is <b>counter-clockwise on screen</b> — so the two
        /// senses are opposite and this is the single negation that reconciles them. Both icon paths call it:
        /// the point path folds the result into <c>PlacedQuad.RotationRadians</c>
        /// (<c>SymbolStagingMath.AppendPointHalf</c>), the along-line path rides it out on
        /// <see cref="CandidateEmit.ExtraRotationRadians"/> (<c>StageCurvedAnchor</c>). Placed here, below
        /// every producer of the value, so <c>icon-rotate</c> keeps MapLibre's own sense on every carrier
        /// (<c>SymbolFeature</c> → <c>ShapedSymbol</c> → the stage inputs) and flips exactly once, at the
        /// boundary where it becomes a staging rotation.
        ///
        /// <para><b>MEASURED, not chosen.</b> The staging frame's sense is what
        /// <c>BillboardMath.BuildWorldQuad</c> produces: it rotates the quad's corners in a y-up LOCAL frame and
        /// then negates Y, which lands <c>Offset</c> in a y-DOWN screen frame. A rotation read through a mirrored
        /// axis reverses, so a positive <c>rotationRadians</c> appears counter-clockwise on screen. A derivation
        /// that reads that negation as supplying the clockwise sense while treating <c>Offset</c> as y-up is
        /// self-contradictory: the negation is what makes <c>Offset</c> y-down. Pinned end-to-end by the
        /// rendered tooth
        /// <c>SymbolIconRenderSnapshotTests.AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen</c>,
        /// which measures ink CENTROID (a bounding box is direction-blind) on a 45° road through the real GPU
        /// path, at <c>icon-rotate: 90</c> — 180°, liberty's only live value, is its own inverse and can never
        /// show a sign error.</para>
        ///
        /// <para>Written <c>0f - x</c> rather than <c>-x</c> so an UNROTATED symbol stays at exactly
        /// <c>+0f</c>: <c>-0f</c> is a different bit pattern, it reaches <c>BuildWorldQuad</c> on every curved
        /// TEXT symbol (which leaves <c>icon-rotate</c> at 0), and <c>sincos(-0f)</c> yields <c>sin = -0f</c>,
        /// which flips a <c>-0f</c> corner offset to <c>+0f</c>. Text must stay byte-identical, not nearly
        /// so.</para>
        /// </summary>
        public static float IconRotationRadians(float iconRotateRadians) => 0f - iconRotateRadians;
    }
}
