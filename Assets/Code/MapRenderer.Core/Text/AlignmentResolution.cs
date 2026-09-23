// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Resolves the Style Spec's <c>auto</c> <see cref="AlignmentMode"/> for the rotation-alignment keys
    /// (<see cref="Resolve"/>) and the pitch-alignment keys (<see cref="ResolvePitch"/>) against the layer's
    /// <c>symbol-placement</c>: <c>auto</c> is <see cref="AlignmentMode.Map"/> under line placements and
    /// <see cref="AlignmentMode.Viewport"/> under point placement. An explicit value always passes through.
    /// <para>Not cosmetic: <c>waterway_line_label</c>, <c>water_name_line_label</c> and
    /// <c>road_one_way_arrow*</c> leave rotation-alignment unset, and this resolver keeps them curved along the
    /// line instead of upright.</para>
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

        /// <summary>Resolves a <c>*-pitch-alignment</c> value; the result is never <see cref="AlignmentMode.Auto"/>.
        /// The spec's <c>auto</c> "matches <c>*-rotation-alignment</c>" is circular when rotation is also
        /// <c>auto</c>, so it reads as the RESOLVED rotation alignment (<see cref="AlignmentMode"/>). An explicit
        /// <paramref name="pitchMode"/> wins; <c>auto</c> defers to <see cref="Resolve"/>, so the placement rule
        /// lives in one place.
        /// <para><c>SymbolFeatureExtractor.Extract</c> stamps the result on each <c>SymbolFeature</c>; through
        /// <c>CurvedStageInput.PitchAlignment</c>, <see cref="AlignmentMode.Map"/> selects the world-metre arc walk
        /// in <c>SymbolStagingMath.StageCurved</c>. Only that curved arm reads it: <c>PointStageInput</c> has no
        /// pitch-alignment field, so a map-pitched POINT symbol still billboards.</para></summary>
        public static AlignmentMode ResolvePitch(AlignmentMode pitchMode, AlignmentMode rotationMode, SymbolPlacement placement)
        {
            if (pitchMode != AlignmentMode.Auto) return pitchMode;
            return Resolve(rotationMode, placement);
        }
    }
}
