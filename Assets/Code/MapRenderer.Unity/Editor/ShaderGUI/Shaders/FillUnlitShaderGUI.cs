using UnityEngine;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/FillUnlit</c>. It derives from <see cref="BaseShaderGUI"/>, not
    /// <see cref="LitShaderGUI"/>, because <c>Fill_UnlitInput.hlsl</c> declares none of the Lit
    /// shading-model or detail properties. A map shader with styled paint properties still needs an
    /// inspector for its paint foldout and the base keyword sync. It adds only the fill foldout, mirroring
    /// <see cref="FillShaderGUI.DrawFillInputs"/>.
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
