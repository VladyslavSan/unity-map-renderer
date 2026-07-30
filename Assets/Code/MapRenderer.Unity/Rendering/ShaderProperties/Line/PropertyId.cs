using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties.Line
{
    /// <summary>
    /// Cached <c>Shader.PropertyToID</c> integer ids for every property in <see cref="PropertyNames"/>.
    /// Use these for all <c>Material.SetFloat/SetColor/SetVector/HasProperty/GetFloat/…</c> calls that
    /// target <c>Map/Line</c>-specific properties. (S78)
    /// </summary>
    public static class PropertyId
    {
        // ── Style-bound ──
        public static readonly int Width               = Shader.PropertyToID(PropertyNames.Width);
        public static readonly int Blur                = Shader.PropertyToID(PropertyNames.Blur);
        public static readonly int GapWidth            = Shader.PropertyToID(PropertyNames.GapWidth);
        public static readonly int LineTranslate       = Shader.PropertyToID(PropertyNames.LineTranslate);
        public static readonly int LineTranslateAnchor = Shader.PropertyToID(PropertyNames.LineTranslateAnchor);
        public static readonly int LinePattern         = Shader.PropertyToID(PropertyNames.LinePattern);
        public static readonly int DashArray           = Shader.PropertyToID(PropertyNames.DashArray);
        public static readonly int DashCount           = Shader.PropertyToID(PropertyNames.DashCount);
        public static readonly int LineOffset          = Shader.PropertyToID(PropertyNames.LineOffset);

        // ── Internal render params ──
        public static readonly int WidthIsPixels  = Shader.PropertyToID(PropertyNames.WidthIsPixels);

        // ── Editor-only keyword drivers (Properties{} only, not CBUFFER members) ──
        public static readonly int EdgeAntialiasing = Shader.PropertyToID(PropertyNames.EdgeAntialiasing);
        public static readonly int HairlineStrategy = Shader.PropertyToID(PropertyNames.HairlineStrategy);
    }
}
