using UnityEditor;
using UnityEngine;
using MapRenderer.Unity.Rendering;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/Line</c> (S58). The final successor
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
            // (A) Style-bound — MapLibre line-* paint/layout (written by the styler).
            Prop(ShaderProperties.Opacity,            "Opacity (line-opacity)");
            Prop(ShaderProperties.Width,              "Width (line-width)");
            Prop(ShaderProperties.Blur,               "Line Blur (line-blur)");
            Prop(ShaderProperties.GapWidth,           "Gap Width (line-gap-width)");
            Prop(ShaderProperties.LineOffset,         "Line Offset (line-offset)");
            Prop(ShaderProperties.DashArray,          "Dash Array (line-dasharray)");
            Prop(ShaderProperties.DashCount,          "Dash Count");
            Prop(ShaderProperties.LinePattern,        "Line Pattern (hook)");
            Prop(ShaderProperties.LineTranslate,      "Line Translate (line-translate)");
            Prop(ShaderProperties.LineTranslateAnchor, "Translate Anchor");

            // (B) Internal render params — NOT style properties.
            Prop(ShaderProperties.WidthIsPixels,      "Width In Pixels");
            Prop(ShaderProperties.MetersPerPixel,     "Meters Per Pixel");
            Prop(ShaderProperties.AaEdgeWidth,        "AA Edge Width (px / side)");
        }
    }
}
