// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Turns a <see cref="ShapingRequest"/> into a <see cref="ShapedRun"/> (positioned glyphs in
    /// visual order). See <see cref="CodepointTextShaper"/> for the S18 Slice 3 clean-room
    /// implementation (Model A / Option Y — no HarfBuzz).
    /// </summary>
    public interface ITextShaper
    {
        ShapedRun Shape(in ShapingRequest request);
    }
}
