using UnityEngine;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/FillUnlit</c>. Unlike <see cref="FillShaderGUI"/> this derives directly from
    /// <see cref="BaseShaderGUI"/> rather than <see cref="LitShaderGUI"/>: the unlit twin declares none of
    /// <see cref="LitShaderGUI"/>'s shading-model/Detail-Inputs properties (no normal map, workflow mode,
    /// metallic/specular, or detail maps — <c>Fill_UnlitInput.hlsl</c> has no CBUFFER member for any of
    /// them), so that level's keyword sync and foldout would draw nothing but empty scaffolding.
    ///
    /// <para>A ShaderGUI is still required — <c>map-lit-shader-needs-shadergui</c>: every map shader needs
    /// SOME inspector for the map-paint foldout + <see cref="BaseShaderGUI.ValidateMaterial"/>'s
    /// surface/cull/alpha-clip keyword sync (Symbol's GUI-less shaders are the documented exception, not
    /// the rule for a shader with styled paint properties). Adds only the fill-specific foldout, mirroring
    /// <see cref="FillShaderGUI.DrawFillInputs"/>.</para>
    /// </summary>
    public sealed class FillUnlitShaderGUI : BaseShaderGUI
    {
        protected override void RegisterMiddleScopes()
        {
            base.RegisterMiddleScopes();
            AddScope("Map — Fill", Expandable.MapFeature, DrawFillInputs);
        }

        private void DrawFillInputs(Material material)
        {
            Prop(ShaderProperties.PropertyNames.Opacity,              "Opacity");
            Prop(ShaderProperties.Fill.PropertyNames.FillOutlineColor, "Outline Color");
            Prop(ShaderProperties.Fill.PropertyNames.FillAntialias,    "Antialias");
        }
    }
}
