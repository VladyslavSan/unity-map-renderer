// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre <c>text-translate-anchor</c>: the frame of reference for <c>text-translate</c>.
    /// <see cref="Map"/> (the style-spec default) is the zero value. Under an active map bearing the two
    /// diverge — a <see cref="Map"/> offset rotates with the map, a <see cref="Viewport"/> offset stays
    /// screen-fixed; at bearing 0 (north-up, the common case) they are identical. The bearing-rotation that
    /// distinguishes them lands with the rotation/pitch-alignment work (roadmap #4); until then the placement
    /// path applies the offset in screen space for both.
    /// </summary>
    public enum TextTranslateAnchor
    {
        Map = 0,
        Viewport,
    }
}
