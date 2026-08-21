using UnityEngine;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/FillExtrusionUnlit</c>. Like <see cref="FillUnlitShaderGUI"/> this derives
    /// directly from <see cref="BaseShaderGUI"/> rather than <see cref="LitShaderGUI"/>: the unlit twin
    /// declares none of <see cref="LitShaderGUI"/>'s shading-model/Detail-Inputs properties (no normal map,
    /// workflow mode, metallic/specular, or detail maps — <c>FillExtrusion_UnlitInput.hlsl</c> has no CBUFFER
    /// member for any of them), so that level's keyword sync and foldout would draw nothing but empty
    /// scaffolding.
    ///
    /// <para>A ShaderGUI is still required — <c>map-lit-shader-needs-shadergui</c>: every map shader needs
    /// SOME inspector for the map-paint foldout + <see cref="BaseShaderGUI.ValidateMaterial"/>'s
    /// surface/cull/alpha-clip keyword sync. Adds only the fill-extrusion foldout, mirroring
    /// <see cref="FillExtrusionShaderGUI.DrawFillExtrusionInputs"/> (the building colour is the standard
    /// <c>_BaseColor</c>, shown under Surface Inputs, so it is not repeated here).</para>
    /// </summary>
    public sealed class FillExtrusionUnlitShaderGUI : BaseShaderGUI
    {
        protected override void RegisterMiddleScopes()
        {
            base.RegisterMiddleScopes();
            AddScope("Map — Fill Extrusion", Expandable.MapFeature, DrawFillExtrusionInputs);
        }

        private void DrawFillExtrusionInputs(Material material)
        {
            Prop(ShaderProperties.PropertyNames.Opacity,                                    "Opacity (fill-extrusion-opacity)");
            Prop(ShaderProperties.FillExtrusion.PropertyNames.ExtrusionHeight,               "Height (fill-extrusion-height)");
            Prop(ShaderProperties.FillExtrusion.PropertyNames.ExtrusionBase,                 "Base (fill-extrusion-base)");
            Prop(ShaderProperties.FillExtrusion.PropertyNames.FillExtrusionTranslate,        "Translate (fill-extrusion-translate)");
            Prop(ShaderProperties.FillExtrusion.PropertyNames.FillExtrusionTranslateAnchor,  "Translate Anchor");
        }
    }
}
