using UnityEditor;
using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

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
            Prop(ShaderProperties.PropertyNames.Opacity,                   "Opacity (line-opacity)");
            Prop(ShaderProperties.Line.PropertyNames.Width,                 "Width (line-width)");
            Prop(ShaderProperties.Line.PropertyNames.Blur,                  "Line Blur (line-blur)");
            Prop(ShaderProperties.Line.PropertyNames.GapWidth,              "Gap Width (line-gap-width)");
            Prop(ShaderProperties.Line.PropertyNames.LineOffset,            "Line Offset (line-offset)");
            Prop(ShaderProperties.Line.PropertyNames.DashArray,             "Dash Array (line-dasharray)");
            Prop(ShaderProperties.Line.PropertyNames.DashCount,             "Dash Count");
            Prop(ShaderProperties.Line.PropertyNames.LinePattern,           "Line Pattern (hook)");
            Prop(ShaderProperties.Line.PropertyNames.LineTranslate,         "Line Translate (line-translate)");
            Prop(ShaderProperties.Line.PropertyNames.LineTranslateAnchor,   "Translate Anchor");

            // (B) Internal render params — NOT style properties.
            Prop(ShaderProperties.Line.PropertyNames.WidthIsPixels,  "Width In Pixels");
            Prop(ShaderProperties.Line.PropertyNames.MetersPerPixel, "Meters Per Pixel");
            Prop(ShaderProperties.Line.PropertyNames.AaEdgeWidth,    "AA Edge Width (px / side)");
        }
    }
}
