// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre <c>text-translate-anchor</c>: the frame of reference for <c>text-translate</c>.
    /// <see cref="Map"/> (the style-spec default) is the zero value. A <see cref="Map"/> offset rotates
    /// with the map bearing (<c>SymbolTranslate</c>); a <see cref="Viewport"/> offset stays screen-fixed.
    /// At bearing 0 the two are identical.
    /// </summary>
    public enum TextTranslateAnchor
    {
        Map = 0,
        Viewport,
    }
}
