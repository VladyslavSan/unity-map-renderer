// Engine-free: no UnityEngine dependency.

using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The single owner of the SIGN conventions for symbol orientation: the map-bearing sign (the billboard
    /// rotation for <c>text-rotation-alignment:map</c> and the offset rotation for
    /// <c>text-translate-anchor:map</c>) and the <c>icon-rotate</c> sense conversion. Everything that turns a
    /// symbol routes through here, so each "which way does this turn" question is ONE place, not sign math
    /// scattered across sites.
    ///
    /// <para><b>Both signs are MEASURED</b> — each by a rendered tooth at an angle where its two candidate
    /// values are distinguishable, not by argument from the surrounding code's stated conventions. That
    /// distinction is not academic: <see cref="IconRotationRadians"/> was <em>chosen</em>, was documented
    /// with a plausible derivation, was covered by tests, and was still inverted by exactly 180° until it
    /// was rendered. See each constant's own doc for what pins it.</para>
    /// </summary>
    public static class SymbolBearing
    {
        /// <summary>The map-bearing sign for map-aligned symbol orientation (the <c>text-rotation-alignment:map</c>
        /// billboard and the <c>text-translate-anchor:map</c> offset). +1 = a map heading of θ (CW from north)
        /// turns map-aligned symbols by +θ in the screen's (y-up) frame.
        ///
        /// <para><b>MEASURED, not chosen.</b> This constant carried the opposite claim — that a non-zero
        /// bearing is "not headlessly testable" because every headless test runs at bearing 0, so the sign
        /// was <em>chosen</em> and deferred to an Editor eyeball. That was simply untrue: a headless camera
        /// takes a heading like any other. Pinned end-to-end by the rendered tooth
        /// <c>SymbolIconRenderSnapshotTests.MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing</c>,
        /// which renders a map-aligned point icon at heading 45° — not 0 (map and viewport alignment
        /// coincide), not 180° (its own inverse), not axis-aligned (cannot separate +θ from −θ) — and
        /// measures ink CENTROID, a bounding box being direction-blind. It asserts the symbol turns +45°
        /// counter-clockwise on screen AND that this matches, to within a couple of degrees, the turn of the
        /// map itself, measured independently as where the camera projects an off-centre anchor placed due
        /// map-east. Flipping this constant to -1 puts that tooth 91.5° out.</para>
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
        /// viewport for the point placement this renderer emits today) → no rotation.
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
        /// <para><b>MEASURED, not chosen</b> — the first of the two to be, and the reason
        /// <see cref="MapAlignedSign"/> was measured after it. The staging frame's sense
        /// is what <c>BillboardMath.BuildWorldQuad</c> produces: it rotates the quad's corners in a y-up LOCAL
        /// frame and then negates Y, which lands <c>Offset</c> in a y-DOWN screen frame, and a rotation read
        /// through a mirrored axis reverses — a positive <c>rotationRadians</c> appears counter-clockwise on
        /// screen. The earlier reading of that negation (that it supplied the clockwise sense, leaving
        /// <c>Offset</c> y-up) was self-contradictory: the negation is precisely what makes <c>Offset</c>
        /// y-down. Pinned end-to-end by the rendered tooth
        /// <c>SymbolIconRenderSnapshotTests.AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen</c>,
        /// which measures ink CENTROID (a bounding box is direction-blind) on a 45° road through the real GPU
        /// path, at <c>icon-rotate: 90</c> — 180°, liberty's only live value, is its own inverse and can never
        /// show a sign error.</para>
        ///
        /// <para>Written <c>0f - x</c> rather than <c>-x</c> so an UNROTATED symbol stays at exactly
        /// <c>+0f</c>: <c>-0f</c> is a different bit pattern, it reaches <c>BuildWorldQuad</c> on every curved
        /// TEXT symbol (which leaves <c>icon-rotate</c> at 0), and <c>sincos(-0f)</c> yields <c>sin = -0f</c>,
        /// which flips a <c>-0f</c> corner offset to <c>+0f</c>. Byte-identity for text is P-B's invariant;
        /// this keeps it exact instead of nearly so.</para>
        /// </summary>
        public static float IconRotationRadians(float iconRotateRadians) => 0f - iconRotateRadians;
    }
}
