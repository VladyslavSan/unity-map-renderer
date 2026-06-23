using UnityEditor;
using UnityEngine;
using MapRenderer.Unity.Rendering;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/Fill</c> (S58). The final successor in the hierarchy
    /// (<see cref="BaseShaderGUI"/> → <see cref="LitShaderGUI"/> → this): inherits the full Lit layout
    /// (Surface Options / Surface Inputs / Detail Inputs / Advanced) and adds a fill-specific foldout.
    /// The fill declares no extra shader-feature keywords, so it inherits <see cref="LitShaderGUI"/>'s full
    /// keyword sync unchanged; its runtime render-state contract is <see cref="FillMaterialTweaker"/>.
    /// </summary>
    public sealed class FillShaderGUI : LitShaderGUI
    {
        protected override void RegisterMiddleScopes()
        {
            base.RegisterMiddleScopes();   // Detail Inputs
            AddScope("Map — Fill", Expandable.MapFeature, DrawFillInputs);
        }

        private void DrawFillInputs(Material material)
        {
            Prop(ShaderProperties.Opacity,          "Opacity");
            Prop(ShaderProperties.FillOutlineColor, "Outline Color");
            Prop(ShaderProperties.FillAntialias,    "Antialias");
        }
    }
}
