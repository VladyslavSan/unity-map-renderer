// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre alignment enum shared by <c>text-rotation-alignment</c> and <c>text-pitch-alignment</c>.
    /// <see cref="Auto"/> (the style-spec default, and the zero value) resolves by placement:
    /// point → <see cref="Viewport"/>, line/line-center → <see cref="Map"/>. For <c>text-pitch-alignment</c>,
    /// <see cref="Auto"/> instead matches the resolved <c>text-rotation-alignment</c>.
    /// </summary>
    public enum AlignmentMode
    {
        Auto = 0,
        Map,
        Viewport,
    }
}
