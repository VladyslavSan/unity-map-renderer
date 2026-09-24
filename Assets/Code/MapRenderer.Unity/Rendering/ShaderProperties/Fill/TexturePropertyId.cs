using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties.Fill
{
    /// <summary>
    /// Cached ids for the <c>Map/Fill</c> shader's TEXTURE properties. Non-local invariant:
    /// <see cref="PropertyId"/>/<see cref="PropertyNames"/> hold exactly the fill-only CBUFFER set
    /// (<c>SharedUnionFillNames_EqualsFillCbuffer</c>, <c>FillPropertyNamesCount_IsExactly7</c>),
    /// and a texture is not a CBUFFER member, so its id lives here. The render layer and its tests
    /// both bind the sheet.
    /// </summary>
    public static class TexturePropertyId
    {
        /// <summary>The style's sprite sheet, sampled by a <c>fill-pattern</c> layer. Unbound (and the
        /// layer clipped) until the asynchronously-fetched sheet resolves.</summary>
        public static readonly int PatternMap = Shader.PropertyToID("_PatternMap");
    }
}
