using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties.FillExtrusion
{
    /// <summary>
    /// Cached <c>Shader.PropertyToID</c> integer ids for every property in <see cref="PropertyNames"/>.
    /// Use these for all <c>Material.SetFloat/SetColor/SetVector/HasProperty/GetFloat/…</c> calls that
    /// target <c>Map/FillExtrusion</c>-specific properties. (S23 I2b, mirrors <c>Fill.PropertyId</c>.)
    /// </summary>
    public static class PropertyId
    {
        public static readonly int ExtrusionHeight               = Shader.PropertyToID(PropertyNames.ExtrusionHeight);
        public static readonly int ExtrusionBase                 = Shader.PropertyToID(PropertyNames.ExtrusionBase);
        public static readonly int FillExtrusionTranslate         = Shader.PropertyToID(PropertyNames.FillExtrusionTranslate);
        public static readonly int FillExtrusionTranslateAnchor   = Shader.PropertyToID(PropertyNames.FillExtrusionTranslateAnchor);
    }
}
