using UnityEngine;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/FillExtrusionUnlit</c>. It derives from <see cref="BaseShaderGUI"/>, not
    /// <see cref="LitShaderGUI"/>, because <c>FillExtrusion_UnlitInput.hlsl</c> declares none of the Lit
    /// shading-model or detail properties. It still needs the base keyword sync, and adds only the
    /// fill-extrusion foldout, mirroring <see cref="FillExtrusionShaderGUI.DrawFillExtrusionInputs"/>. The
    /// building colour is the standard <c>_BaseColor</c> under Surface Inputs.
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
