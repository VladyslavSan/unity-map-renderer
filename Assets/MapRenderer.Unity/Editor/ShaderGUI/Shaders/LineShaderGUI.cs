using UnityEditor;
using UnityEngine;
using MapRenderer.Unity.Rendering;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>MapRenderer/Line</c> (S58). The final successor
    /// (<see cref="BaseShaderGUI"/> → <see cref="LitShaderGUI"/> → this): inherits the full Lit layout
    /// and adds a line-specific foldout (width / blur / gap / offset / dash). The line declares no extra
    /// shader-feature keywords, so it inherits <see cref="LitShaderGUI"/>'s full keyword sync unchanged; its
    /// runtime render-state contract is <see cref="LineMaterialTweaker"/>.
    /// </summary>
    public sealed class LineShaderGUI : LitShaderGUI
    {
        protected override void RegisterMiddleScopes()
        {
            base.RegisterMiddleScopes();   // Detail Inputs
            AddScope("Map — Line", Expandable.MapFeature, DrawLineInputs);
        }

        private void DrawLineInputs(Material material)
        {
            Prop(ShaderProperties.Opacity,            "Opacity");
            Prop(ShaderProperties.Width,              "Width");
            Prop(ShaderProperties.WidthIsPixels,      "Width In Pixels");
            Prop(ShaderProperties.MetersPerPixel,     "Meters Per Pixel");
            Prop(ShaderProperties.Blur,               "Blur (AA feather)");
            Prop(ShaderProperties.GapWidth,           "Gap Width");
            Prop(ShaderProperties.LineOffset,         "Line Offset");
            Prop(ShaderProperties.DashArray,          "Dash Array");
            Prop(ShaderProperties.DashCount,          "Dash Count");
            Prop(ShaderProperties.LinePattern,        "Line Pattern (hook)");
            Prop(ShaderProperties.LineTranslate,      "Line Translate");
            Prop(ShaderProperties.LineTranslateAnchor, "Translate Anchor");
        }
    }
}
