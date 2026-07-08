// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre <c>text-anchor</c>: which point of the label's multi-line block bounding box sits at
    /// the placement anchor. <see cref="Center"/> (the style-spec default) is the zero value so a
    /// stray <c>default(TextAnchor)</c> degrades to the spec default, not an arbitrary corner.
    /// </summary>
    public enum TextAnchor
    {
        Center = 0,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }
}
