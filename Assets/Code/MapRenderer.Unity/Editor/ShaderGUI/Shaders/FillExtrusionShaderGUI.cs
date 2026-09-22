using UnityEditor;
using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/FillExtrusion</c>. The final successor in the hierarchy
    /// (<see cref="BaseShaderGUI"/> → <see cref="LitShaderGUI"/> → this): it inherits the full Lit layout
    /// (Surface Options / Surface Inputs / Detail Inputs / Advanced) AND <see cref="LitShaderGUI"/>'s keyword
    /// sync unchanged, because fill-extrusion declares only the standard URP-Lit shader_features (emission,
    /// normal/parallax/detail maps, receive-shadows, …) and no layer-specific keyword — unlike
    /// <see cref="LineShaderGUI"/>, which overrides <c>ValidateMaterial</c> for its own keywords. Adds a
    /// foldout for the 3D-building paint properties. Its runtime render-state contract is the elevated-3D
    /// <see cref="FillExtrusionTweaker"/> (opaque, depth-writing), not the flat fill painter contract.
    /// </summary>
    public sealed class FillExtrusionShaderGUI : LitShaderGUI
    {
        /// <summary>Insert the fill-extrusion foldout after the inherited Detail Inputs scope.</summary>
        protected override void RegisterMiddleScopes()
        {
            base.RegisterMiddleScopes();   // Detail Inputs
            AddScope("Map — Fill Extrusion", Expandable.MapFeature, DrawFillExtrusionInputs);
        }

        /// <summary>Draw the fill-extrusion-* paint properties. The building colour is the standard
        /// <c>_BaseColor</c> (shown under Surface Inputs), so it is not repeated here.</summary>
        /// <param name="material">The fill-extrusion material being inspected.</param>
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
