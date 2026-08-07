// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// D3 (road-shields epic, docs/road-shields-design.md §3) — resolves the Style Spec's <c>auto</c>
    /// <see cref="AlignmentMode"/> default for BOTH the rotation-alignment keys
    /// (<see cref="Resolve"/>) and the pitch-alignment keys (<see cref="ResolvePitch"/>) against the owning
    /// symbol layer's <c>symbol-placement</c>. <c>Resolve</c>: <c>auto</c> resolves to
    /// <see cref="AlignmentMode.Map"/> under <see cref="SymbolPlacement.Line"/> /
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

        /// <summary>Resolves a <c>*-pitch-alignment</c> value. The spec's <c>auto</c> wording for
        /// pitch-alignment is "matches <c>*-rotation-alignment</c>" — read against a rotation value that is
        /// itself <c>auto</c>, that is circular, so the only non-circular reading (and this repo's stated
        /// position, <see cref="AlignmentMode"/>) is the RESOLVED rotation alignment: an explicit
        /// <paramref name="pitchMode"/> always wins; an auto <paramref name="pitchMode"/> defers to
        /// <see cref="Resolve"/> on <paramref name="rotationMode"/>/<paramref name="placement"/> — never the
        /// raw, possibly-still-auto <paramref name="rotationMode"/>. The placement rule therefore lives in
        /// exactly one place (<see cref="Resolve"/>); this method never re-tests <paramref name="placement"/>
        /// itself. The return value is never <see cref="AlignmentMode.Auto"/>.
        ///
        /// <para><b>WIRED as of W1</b> (pitch-alignment epic; landed with no production caller in P1, which
        /// this paragraph used to describe). <c>SymbolFeatureExtractor.Extract</c> calls this once per symbol
        /// layer for each of the text and icon key pairs, and stamps the RESOLVED value onto the emitted
        /// <c>SymbolLabel</c>; it travels to <c>CurvedStageInput.PitchAlignment</c>, where
        /// <see cref="AlignmentMode.Map"/> selects the world-metre arc walk in
        /// <c>LabelStagingMath.StageCurved</c>.</para>
        ///
        /// <para><b>The CURVED (along-line) arm only.</b> The point arm does not consume this yet — there is
        /// no pitch-alignment field on <c>PointStageInput</c> — so a map-pitched POINT label still billboards.
        /// That is a deliberate scope fence, not an oversight; it is the next stage of the same epic.</para></summary>
        public static AlignmentMode ResolvePitch(AlignmentMode pitchMode, AlignmentMode rotationMode, SymbolPlacement placement)
        {
            if (pitchMode != AlignmentMode.Auto) return pitchMode;
            return Resolve(rotationMode, placement);
        }
    }
}
