using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;   // CoreUtils.SetKeyword (same helper URP's LitGUI/LitDetailGUI use)
using MapRenderer.Unity.Rendering;

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
            bool specular = material.HasProperty(ShaderProperties.WorkflowMode)
                            && (WorkflowMode)(int)material.GetFloat(ShaderProperties.WorkflowMode) == WorkflowMode.Specular;
            CoreUtils.SetKeyword(material, ShaderKeywords.SpecularSetup, specular);

            // Metallic / specular gloss map — L465 (the active map depends on the workflow).
            string glossMap = specular ? ShaderProperties.SpecGlossMap : ShaderProperties.MetallicGlossMap;
            CoreUtils.SetKeyword(material, ShaderKeywords.MetallicSpecGlossMap,
                material.HasProperty(glossMap) && material.GetTexture(glossMap) != null);

            // Specular-highlights / environment-reflections OFF toggles — L468/L471 (default 1 → keyword off).
            if (material.HasProperty(ShaderProperties.SpecularHighlights))
                CoreUtils.SetKeyword(material, ShaderKeywords.SpecularHighlightsOff,
                    material.GetFloat(ShaderProperties.SpecularHighlights) == 0f);
            if (material.HasProperty(ShaderProperties.EnvironmentReflections))
                CoreUtils.SetKeyword(material, ShaderKeywords.EnvironmentReflectionsOff,
                    material.GetFloat(ShaderProperties.EnvironmentReflections) == 0f);

            // Occlusion map — L474.
            if (material.HasProperty(ShaderProperties.OcclusionMap))
                CoreUtils.SetKeyword(material, ShaderKeywords.OcclusionMap,
                    material.GetTexture(ShaderProperties.OcclusionMap) != null);

            // Height / parallax map — L477 (URP puts this in LitGUI, NOT BaseShaderGUI — mirrored).
            if (material.HasProperty(ShaderProperties.ParallaxMap))
                CoreUtils.SetKeyword(material, ShaderKeywords.ParallaxMap,
                    material.GetTexture(ShaderProperties.ParallaxMap) != null);

            // Smoothness source channel — L482: albedo-alpha only when selected AND opaque.
            if (material.HasProperty(ShaderProperties.SmoothnessTextureChannel))
                CoreUtils.SetKeyword(material, ShaderKeywords.SmoothnessTextureAlbedoChannelA,
                    material.GetFloat(ShaderProperties.SmoothnessTextureChannel) == 1f && IsOpaque(material));

            // ── Detail keywords (URP LitDetailGUI.SetMaterialKeywords L64-73) ──
            // The scaled variant (mul-x2 with a per-detail scale ≠ 1) is a distinct, less-performant keyword;
            // exactly one of the two is on when a detail map is assigned.
            if (material.HasProperty(ShaderProperties.DetailAlbedoMap)
                && material.HasProperty(ShaderProperties.DetailNormalMap)
                && material.HasProperty(ShaderProperties.DetailAlbedoMapScale))
            {
                bool isScaled  = material.GetFloat(ShaderProperties.DetailAlbedoMapScale) != 1f;
                bool hasDetail = material.GetTexture(ShaderProperties.DetailAlbedoMap) != null
                              || material.GetTexture(ShaderProperties.DetailNormalMap) != null;
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
            Tex(ShaderProperties.DetailMask,       "Detail Mask",   null);
            Tex(ShaderProperties.DetailAlbedoMap,  "Detail Albedo", ShaderProperties.DetailAlbedoMapScale);
            Tex(ShaderProperties.DetailNormalMap,  "Detail Normal", ShaderProperties.DetailNormalMapScale);

            var detailAlbedo = Find(ShaderProperties.DetailAlbedoMap);
            if (detailAlbedo != null) _editor.TextureScaleOffsetProperty(detailAlbedo);
        }
    }
}
