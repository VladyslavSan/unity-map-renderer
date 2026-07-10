// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre <c>text-justify</c>. <see cref="Auto"/> (the style-spec default, and the zero value so
    /// a stray <c>default(TextJustify)</c> degrades to the spec default) resolves from
    /// <see cref="TextAnchor"/> at layout time — see <c>TextQuadLayout</c>'s justify resolution.
    /// </summary>
    public enum TextJustify
    {
        Auto = 0,
        Left,
        Center,
        Right,
    }
}
