using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties.Fill
{
    /// <summary>
    /// Cached <c>Shader.PropertyToID</c> integer ids for every property in <see cref="PropertyNames"/>.
    /// Use these for all <c>Material.SetFloat/SetColor/SetVector/HasProperty/GetFloat/…</c> calls that
    /// target <c>Map/Fill</c>-specific properties. (S78)
    /// </summary>
    public static class PropertyId
    {
        public static readonly int FillOutlineColor    = Shader.PropertyToID(PropertyNames.FillOutlineColor);
        public static readonly int FillAntialias       = Shader.PropertyToID(PropertyNames.FillAntialias);
        public static readonly int FillTranslate       = Shader.PropertyToID(PropertyNames.FillTranslate);
        public static readonly int FillTranslateAnchor = Shader.PropertyToID(PropertyNames.FillTranslateAnchor);
        public static readonly int FillPattern         = Shader.PropertyToID(PropertyNames.FillPattern);
        public static readonly int PatternRect         = Shader.PropertyToID(PropertyNames.PatternRect);
        public static readonly int PatternScale        = Shader.PropertyToID(PropertyNames.PatternScale);
    }
}
