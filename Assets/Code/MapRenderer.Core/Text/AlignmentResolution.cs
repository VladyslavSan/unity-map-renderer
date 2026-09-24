// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Resolves the Style Spec's <c>auto</c> <see cref="AlignmentMode"/> for the rotation-alignment keys
    /// (<see cref="Resolve"/>) and the pitch-alignment keys (<see cref="ResolvePitch"/>) against the layer's
    /// <c>symbol-placement</c>: <c>auto</c> is <see cref="AlignmentMode.Map"/> under line placements and
    /// <see cref="AlignmentMode.Viewport"/> under point placement. An explicit value passes through; layers such
    /// as <c>road_one_way_arrow*</c> leave rotation-alignment unset and stay curved along the line through this.
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
        /// <c>auto</c>, so <c>auto</c> defers to the RESOLVED rotation via <see cref="Resolve"/>. Only the
        /// curved arm reads the result; <c>PointStageInput</c> has no such field, so a map-pitched point
        /// billboards.</summary>
        public static AlignmentMode ResolvePitch(AlignmentMode pitchMode, AlignmentMode rotationMode, SymbolPlacement placement)
        {
            if (pitchMode != AlignmentMode.Auto) return pitchMode;
            return Resolve(rotationMode, placement);
        }
    }
}
