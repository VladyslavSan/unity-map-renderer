using UnityEditor;
using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Inspector for <c>Map/FillExtrusion</c>. It inherits the full Lit layout and keyword sync of
    /// <see cref="LitShaderGUI"/> unchanged, because fill-extrusion declares only the standard URP-Lit
    /// shader_features and no keyword of its own. It adds a foldout for the 3D-building paint properties.
    /// Its runtime render-state contract is <see cref="FillExtrusionTweaker"/> (opaque, depth-writing).
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
