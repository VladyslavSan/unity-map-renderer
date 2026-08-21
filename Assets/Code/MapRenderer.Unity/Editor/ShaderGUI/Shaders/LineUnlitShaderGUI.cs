using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/LineUnlit</c>. Like <see cref="FillUnlitShaderGUI"/>/
    /// <see cref="FillExtrusionUnlitShaderGUI"/> this derives directly from <see cref="BaseShaderGUI"/>
    /// rather than <see cref="LitShaderGUI"/>: the unlit twin declares none of <see cref="LitShaderGUI"/>'s
    /// shading-model/Detail-Inputs properties (no normal map, workflow mode, metallic/specular, or detail
    /// maps — <c>Line_UnlitInput.hlsl</c> has no CBUFFER member for any of them), so that level's keyword
    /// sync and foldout would draw nothing but empty scaffolding.
    ///
    /// <para>A ShaderGUI is still required — <c>map-lit-shader-needs-shadergui</c>: every map shader needs
    /// SOME inspector for the map-paint foldout + <see cref="BaseShaderGUI.ValidateMaterial"/>'s
    /// surface/cull/alpha-clip keyword sync. This twin ALSO needs its own <see cref="ValidateMaterial"/>
    /// override — unlike Fill/FillExtrusion, <c>Map/LineUnlit</c> declares the same two UI-only keyword
    /// drivers <see cref="LineShaderGUI"/> syncs (<c>_EdgeAntialiasing</c>/<c>_HairlineStrategy</c>), and
    /// without this override they are completely inert (mirrors <see cref="LineShaderGUI.ValidateMaterial"/>
    /// exactly).</para>
    /// </summary>
    public sealed class LineUnlitShaderGUI : BaseShaderGUI
    {
        /// <summary>
        /// Keyword sync at the line level, mirroring <see cref="LineShaderGUI.ValidateMaterial"/> — the
        /// unlit twin has no Lit-level keyword set to sync first, so this calls straight to the base
        /// contract then the line's own two drivers.
        /// </summary>
        public override void ValidateMaterial(Material material)
        {
            base.ValidateMaterial(material);

            // _OFF polarity: the keyword is set only when the toggle is off, so the shipping default
            // carries no keyword and its variant can never be stripped from a player build.
            if (material.HasProperty(ShaderProperties.Line.PropertyId.EdgeAntialiasing))
                CoreUtils.SetKeyword(material, ShaderKeywords.EdgeAntialiasingOff,
                    material.GetFloat(ShaderProperties.Line.PropertyId.EdgeAntialiasing) == 0f);

            // Hairline strategy. `_` (0, Default) is the shipping state and sets NO keyword; each strategy
            // takes its own independent == test, so exactly one keyword can ever be set.
            if (material.HasProperty(ShaderProperties.Line.PropertyId.HairlineStrategy))
            {
                float strategy = material.GetFloat(ShaderProperties.Line.PropertyId.HairlineStrategy);
                CoreUtils.SetKeyword(material, ShaderKeywords.HairlineHard, strategy == 1f);
                CoreUtils.SetKeyword(material, ShaderKeywords.HairlineSolidCore, strategy == 2f);
            }
        }

        protected override void RegisterMiddleScopes()
        {
            base.RegisterMiddleScopes();
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
