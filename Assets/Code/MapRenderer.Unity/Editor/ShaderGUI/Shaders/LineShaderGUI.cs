using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/Line</c>. It inherits the full Lit layout of <see cref="LitShaderGUI"/> and
    /// adds a line foldout (width / blur / gap / offset / dash). The line declares keywords of its own
    /// (<c>_EDGE_ANTIALIASING_OFF</c>, <c>_HAIRLINE_HARD</c> / <c>_HAIRLINE_SOLID_CORE</c>), so
    /// <see cref="ValidateMaterial"/> extends the Lit keyword sync. Its runtime render-state contract is
    /// <see cref="LineTweaker"/>.
    /// </summary>
    public sealed class LineShaderGUI : LitShaderGUI
    {
        /// <summary>
        /// Keyword sync at the line level: the base + Lit sets first, then the line's own.
        /// <c>_EdgeAntialiasing</c> is <c>[ToggleUI]</c>, which attaches no keyword, so only this override
        /// turns the float into <c>_EDGE_ANTIALIASING_OFF</c>; without it the toggle is inert.
        /// This codebase syncs keywords in code, not through drawer attributes.
        /// </summary>
        public override void ValidateMaterial(Material material)
        {
            base.ValidateMaterial(material);

            // _OFF polarity: the keyword is set only when the toggle is off, so the shipping default
            // carries no keyword and its variant can never be stripped from a player build.
            if (material.HasProperty(ShaderProperties.Line.PropertyId.EdgeAntialiasing))
                CoreUtils.SetKeyword(material, ShaderKeywords.EdgeAntialiasingOff,
                    material.GetFloat(ShaderProperties.Line.PropertyId.EdgeAntialiasing) == 0f);

            // Hairline strategy 0 (Default) sets no keyword, so its variant is un-strippable. Each
            // strategy has its own == test, so at most one keyword is set.
            if (material.HasProperty(ShaderProperties.Line.PropertyId.HairlineStrategy))
            {
                float strategy = material.GetFloat(ShaderProperties.Line.PropertyId.HairlineStrategy);
                CoreUtils.SetKeyword(material, ShaderKeywords.HairlineHard, strategy == 1f);
                CoreUtils.SetKeyword(material, ShaderKeywords.HairlineSolidCore, strategy == 2f);
            }
        }

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
            Prop(ShaderProperties.Line.PropertyNames.WidthIsPixels,     "Width In Pixels");
            Prop(ShaderProperties.Line.PropertyNames.EdgeAntialiasing,  "Edge Antialiasing");
            Prop(ShaderProperties.Line.PropertyNames.HairlineStrategy,  "Hairline Strategy");
        }
    }
}
