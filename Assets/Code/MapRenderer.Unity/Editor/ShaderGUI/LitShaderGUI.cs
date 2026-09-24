using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;   // CoreUtils.SetKeyword (same helper URP's LitGUI/LitDetailGUI use)
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Adds the URP-Lit <b>Detail Inputs</b> foldout (detail mask / detail albedo / detail normal, with
    /// scales) on top of <see cref="BaseShaderGUI"/>'s Surface Options / Surface Inputs / Advanced —
    /// mirroring URP's <c>LitShader</c> → <c>LitDetailGUI</c> split. Still abstract: a concrete feature
    /// shader (<c>FillShaderGUI</c>/<c>LineShaderGUI</c>) supplies the tweaker and its geometry section.
    /// </summary>
    public abstract class LitShaderGUI : BaseShaderGUI
    {
        /// <summary>
        /// Keyword sync at the Lit level — the clean-room counterpart of URP's <c>LitShader.ValidateMaterial</c>
        /// (which calls <c>SetMaterialKeywords(material, LitGUI.SetMaterialKeywords, LitDetailGUI.SetMaterialKeywords)</c>).
        /// Runs the base surface-level set first, then adds the Lit shading-model keywords (URP <c>LitGUI</c>) and
        /// the Detail keywords (URP <c>LitDetailGUI</c>) — this level draws the Detail foldout, so it owns them.
        /// </summary>
        public override void ValidateMaterial(Material material)
        {
            base.ValidateMaterial(material);

            // ── Shading-model keywords (URP LitGUI.SetMaterialKeywords) ──
            // Workflow (0 = Specular, 1 = Metallic) — L449.
            bool specular = material.HasProperty(ShaderProperties.PropertyId.WorkflowMode)
                            && (WorkflowMode)(int)material.GetFloat(ShaderProperties.PropertyId.WorkflowMode) == WorkflowMode.Specular;
            CoreUtils.SetKeyword(material, ShaderKeywords.SpecularSetup, specular);

            // Metallic / specular gloss map — L465 (the active map depends on the workflow).
            int glossMap = specular ? ShaderProperties.PropertyId.SpecGlossMap : ShaderProperties.PropertyId.MetallicGlossMap;
            CoreUtils.SetKeyword(material, ShaderKeywords.MetallicSpecGlossMap,
                material.HasProperty(glossMap) && material.GetTexture(glossMap) != null);

            // Specular-highlights / environment-reflections OFF toggles — L468/L471 (default 1 → keyword off).
            if (material.HasProperty(ShaderProperties.PropertyId.SpecularHighlights))
                CoreUtils.SetKeyword(material, ShaderKeywords.SpecularHighlightsOff,
                    material.GetFloat(ShaderProperties.PropertyId.SpecularHighlights) == 0f);
            if (material.HasProperty(ShaderProperties.PropertyId.EnvironmentReflections))
                CoreUtils.SetKeyword(material, ShaderKeywords.EnvironmentReflectionsOff,
                    material.GetFloat(ShaderProperties.PropertyId.EnvironmentReflections) == 0f);

            // Occlusion map — L474.
            if (material.HasProperty(ShaderProperties.PropertyId.OcclusionMap))
                CoreUtils.SetKeyword(material, ShaderKeywords.OcclusionMap,
                    material.GetTexture(ShaderProperties.PropertyId.OcclusionMap) != null);

            // Height / parallax map — L477 (URP puts this in LitGUI, NOT BaseShaderGUI — mirrored).
            if (material.HasProperty(ShaderProperties.PropertyId.ParallaxMap))
                CoreUtils.SetKeyword(material, ShaderKeywords.ParallaxMap,
                    material.GetTexture(ShaderProperties.PropertyId.ParallaxMap) != null);

            // Smoothness source channel — L482: albedo-alpha only when selected AND opaque.
            if (material.HasProperty(ShaderProperties.PropertyId.SmoothnessTextureChannel))
                CoreUtils.SetKeyword(material, ShaderKeywords.SmoothnessTextureAlbedoChannelA,
                    material.GetFloat(ShaderProperties.PropertyId.SmoothnessTextureChannel) == 1f && IsOpaque(material));

            // Detail keywords: the scaled variant (per-detail scale ≠ 1) is a separate, slower keyword, and
            // exactly one of the two is on when a detail map is assigned.
            if (material.HasProperty(ShaderProperties.PropertyId.DetailAlbedoMap)
                && material.HasProperty(ShaderProperties.PropertyId.DetailNormalMap)
                && material.HasProperty(ShaderProperties.PropertyId.DetailAlbedoMapScale))
            {
                bool isScaled  = material.GetFloat(ShaderProperties.PropertyId.DetailAlbedoMapScale) != 1f;
                bool hasDetail = material.GetTexture(ShaderProperties.PropertyId.DetailAlbedoMap) != null
                              || material.GetTexture(ShaderProperties.PropertyId.DetailNormalMap) != null;
                CoreUtils.SetKeyword(material, ShaderKeywords.DetailMulx2,  !isScaled && hasDetail);
                CoreUtils.SetKeyword(material, ShaderKeywords.DetailScaled,  isScaled && hasDetail);
            }
        }

        protected override void RegisterMiddleScopes()
        {
            base.RegisterMiddleScopes();
            AddScope("Detail Inputs", Expandable.DetailInputs, DrawDetailInputs);
        }

        protected virtual void DrawDetailInputs(Material material)
        {
            Tex(ShaderProperties.PropertyNames.DetailMask,       "Detail Mask",   null);
            Tex(ShaderProperties.PropertyNames.DetailAlbedoMap,  "Detail Albedo", ShaderProperties.PropertyNames.DetailAlbedoMapScale);
            Tex(ShaderProperties.PropertyNames.DetailNormalMap,  "Detail Normal", ShaderProperties.PropertyNames.DetailNormalMapScale);

            var detailAlbedo = Find(ShaderProperties.PropertyNames.DetailAlbedoMap);
            if (detailAlbedo != null) _editor.TextureScaleOffsetProperty(detailAlbedo);
        }
    }
}
