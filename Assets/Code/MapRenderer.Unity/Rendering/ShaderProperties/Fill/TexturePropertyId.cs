using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties.Fill
{
    /// <summary>
    /// Cached ids for the <c>Map/Fill</c> shader's TEXTURE properties.
    ///
    /// <para>Separate from <see cref="PropertyId"/>/<see cref="PropertyNames"/> because those two are, by
    /// contract, exactly the fill-only <c>UnityPerMaterial</c> CBUFFER set — an invariant the structural
    /// parity tests enforce (<c>SharedUnionFillNames_EqualsFillCbuffer</c>,
    /// <c>FillPropertyNamesCount_IsExactly7</c>). A texture is not a CBUFFER member (only its generated
    /// <c>_TexelSize</c>/<c>_ST</c> companions are, and the parser strips those), so listing one there would
    /// break the registry↔CBUFFER equality that makes those tests meaningful.</para>
    ///
    /// <para>Prior art for keeping a texture id off the CBUFFER registry:
    /// <c>WorldLabelRenderer.AtlasPropId</c>. This class exists rather than a private field there because
    /// both the render layer and its tests bind the sheet, and the name should have one source.</para>
    /// </summary>
    public static class TexturePropertyId
    {
        /// <summary>The style's sprite sheet, sampled by a <c>fill-pattern</c> layer. Unbound (and the
        /// layer clipped) until the asynchronously-fetched sheet resolves.</summary>
        public static readonly int PatternMap = Shader.PropertyToID("_PatternMap");
    }
}
