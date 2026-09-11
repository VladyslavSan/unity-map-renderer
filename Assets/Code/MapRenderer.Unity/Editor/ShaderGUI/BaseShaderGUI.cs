using System;
using UnityEditor;
using UnityEditor.Rendering; // MaterialHeaderScopeList (com.unity.render-pipelines.core editor — a UI utility)
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Our clean-room base material inspector (S58). Subclasses the RAW <see cref="ShaderGUI"/> and
    /// reproduces the familiar URP-Lit inspector — <b>Surface Options</b> / <b>Surface Inputs</b> /
    /// <b>Advanced</b> — as collapsible <see cref="MaterialHeaderScope"/> foldouts, so a map material reads
    /// almost identically to a stock Lit material. Successors extend it: <see cref="LitShaderGUI"/> adds the
    /// Detail-inputs foldout; the feature shaders (<c>FillShaderGUI</c>/<c>LineShaderGUI</c>) add their
    /// geometry-specific foldout.
    ///
    /// <para>(Named <c>BaseShaderGUI</c> in our own namespace — distinct from URP's
    /// <c>UnityEditor.BaseShaderGUI</c>, which is no longer referenced.) The shader-feature <b>keyword sync</b>
    /// lives here in the editor (full editing capability, incl. the editor-only
    /// <c>MaterialEditor.FixupEmissiveFlag</c>) — mirroring URP's <c>BaseShaderGUI.SetMaterialKeywords</c>;
    /// <see cref="ValidateMaterial"/> owns the surface-level set and <see cref="LitShaderGUI"/> adds the Lit
    /// shading-model set. The runtime tweakers (<c>BaseMaterialTweaker</c>/<c>Fill</c>/<c>Line</c>) keep only
    /// the generic render-state contract (depth/blend), not keywords. Uses Unity's public
    /// <see cref="MaterialHeaderScopeList"/> UI utility (Core.Editor); does NOT derive from or copy URP's
    /// <c>BaseShaderGUI</c>/<c>LitShader</c>/<c>LitGUI</c> source.</para>
    /// </summary>
    public abstract class BaseShaderGUI : ShaderGUI
    {
        /// <summary>Foldout bits (shared across the hierarchy). Advanced is collapsed by default.</summary>
        [Flags]
        protected enum Expandable
        {
            SurfaceOptions = 1 << 0,
            SurfaceInputs  = 1 << 1,
            Advanced       = 1 << 2,
            DetailInputs   = 1 << 3,
            MapFeature     = 1 << 4,
        }

        // Smoothness source channel labels (not a domain enum — a texture-channel selector).
        static readonly string[] s_SmoothnessSrc = { "Metallic Alpha", "Albedo Alpha" };

        protected MaterialEditor     _editor;
        protected MaterialProperty[] _props;

        readonly MaterialHeaderScopeList _scopes =
            new MaterialHeaderScopeList(uint.MaxValue & ~(uint)Expandable.Advanced);

        bool _registered;

        // ── foldout registration (ordered: Surface Options, Surface Inputs, [middle], Advanced) ──
        protected void AddScope(string title, Expandable id, Action<Material> draw)
            => _scopes.RegisterHeaderScope(new GUIContent(title), id, draw);

        void RegisterScopes()
        {
            AddScope("Surface Options", Expandable.SurfaceOptions, DrawSurfaceOptions);
            AddScope("Surface Inputs",  Expandable.SurfaceInputs,  DrawSurfaceInputs);
            RegisterMiddleScopes(); // successors insert Detail + feature foldouts here
            AddScope("Advanced", Expandable.Advanced, DrawAdvanced);
        }

        /// <summary>Hook for successors to register foldouts BETWEEN Surface Inputs and Advanced
        /// (Detail inputs, then the geometry-specific section). Override + call base first.</summary>
        protected virtual void RegisterMiddleScopes() { }

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            _editor = materialEditor;
            _props  = properties;
            var material = (Material)materialEditor.target;

            if (!_registered)
            {
                RegisterScopes();
                _registered = true;
            }

            _scopes.DrawHeaders(materialEditor, material);

            // Keep keywords in sync as the user edits — routes through the virtual ValidateMaterial so the
            // right level runs (Base, or Lit which adds the shading-model keywords on top).
            if (GUI.changed)
                foreach (var t in materialEditor.targets)
                    ValidateMaterial((Material)t);
        }

        /// <summary>
        /// Surface-level keyword sync (import / shader assignment / live edit) — the clean-room counterpart of
        /// URP's <c>BaseShaderGUI.SetMaterialKeywords</c>, inlined here (no <c>shadingModelFunc</c>/<c>shaderFunc</c>
        /// indirection). Derives keywords from the <b>material's own values</b> (never a <c>MaterialProperty</c>),
        /// so it is correct under multi-edit / animation / import. <see cref="LitShaderGUI"/> overrides this,
        /// calls <c>base.ValidateMaterial</c>, then adds the Lit shading-model keywords.
        /// </summary>
        public override void ValidateMaterial(Material material)
        {
            // Double-sided GI from cull state (URP BaseShaderGUI L929). URP compares the RenderFace enum
            // (Front=2), which equals CullMode.Back (2) — i.e. double-sided GI is on unless we cull back faces
            // (the single-sided front-rendering default). Compare against CullMode.Back, NOT CullMode.Front:
            // the latter left DoubleSidedGI on for our stock Cull-Back materials.
            if (material.HasProperty(ShaderProperties.PropertyId.CullMode))
                material.doubleSidedGI = (CullMode)material.GetFloat(ShaderProperties.PropertyId.CullMode) != CullMode.Back;

            // Emission (URP BaseShaderGUI L943-953): the editor-only FixupEmissiveFlag reconciles the GI flag
            // with the emission colour first, then the keyword follows the flag (a black colour ⇒ EmissiveIsBlack
            // ⇒ excluded from AnyEmissive ⇒ keyword off). This is exactly why keyword sync lives editor-side now.
            if (material.HasProperty(ShaderProperties.PropertyId.EmissionColor))
                MaterialEditor.FixupEmissiveFlag(material);
            bool emission = (material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.AnyEmissive) != 0;
            CoreUtils.SetKeyword(material, ShaderKeywords.Emission, emission);

            // Normal map (URP BaseShaderGUI L957 — note: height/parallax is the Lit level, not here).
            if (material.HasProperty(ShaderProperties.PropertyId.BumpMap))
                CoreUtils.SetKeyword(material, ShaderKeywords.NormalMap,
                    material.GetTexture(ShaderProperties.PropertyId.BumpMap) != null);

            // Receive-shadows OFF toggle (URP BaseShaderGUI L806; default 1 → keyword off).
            if (material.HasProperty(ShaderProperties.PropertyId.ReceiveShadows))
                CoreUtils.SetKeyword(material, ShaderKeywords.ReceiveShadowsOff,
                    material.GetFloat(ShaderProperties.PropertyId.ReceiveShadows) == 0f);

            // Surface type / alpha clip (URP BaseShaderGUI.SetupMaterialBlendModeInternal L1013/L1022).
            CoreUtils.SetKeyword(material, ShaderKeywords.SurfaceTypeTransparent, !IsOpaque(material));
            CoreUtils.SetKeyword(material, ShaderKeywords.AlphaTestOn,
                material.HasProperty(ShaderProperties.PropertyId.AlphaClip) &&
                material.GetFloat(ShaderProperties.PropertyId.AlphaClip) >= 0.5f);

            // S58 exposes raw blend factors directly rather than a blend preset, so the premultiply/modulate
            // blend-preset keywords are not auto-derived — keep them off (URP sets these in its transparent
            // branch, L1114/L1115).
            CoreUtils.SetKeyword(material, ShaderKeywords.AlphaPremultiplyOn, false);
            CoreUtils.SetKeyword(material, ShaderKeywords.AlphaModulateOn,    false);
        }

        /// <summary>True when the material's surface is opaque (no <c>_Surface</c> prop, or <c>_Surface == 0</c>).</summary>
        protected static bool IsOpaque(Material m)
            => !m.HasProperty(ShaderProperties.PropertyId.Surface) || m.GetFloat(ShaderProperties.PropertyId.Surface) == 0f;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Section 1 — Surface Options
        // ─────────────────────────────────────────────────────────────────────────────────────────
        protected virtual void DrawSurfaceOptions(Material material)
        {
            // Domain enums (runtime) → labels generated from the enum value names by EnumPopup.
            EnumPopup<SurfaceType>(ShaderProperties.PropertyNames.Surface, "Surface Type");
            EnumPopup<WorkflowMode>(ShaderProperties.PropertyNames.WorkflowMode, "Workflow Mode");

            EditorGUILayout.Space();
            // Raw low-level render state (the S58 knobs — what URP's Surface Type/Blend presets hide).
            EnumPopup<DepthWrite>(ShaderProperties.PropertyNames.ZWrite, "Depth Write");
            EnumPopup<CompareFunction>(ShaderProperties.PropertyNames.ZTest, "Depth Test");
            // Label matches the ShaderLab `Cull` directive: the enum names the face that is CULLED
            // (Off/Front/Back), NOT the face rendered. "Render Face" would invert it (Cull Front → the
            // BACK face renders), so this popup is labelled by what it literally sets.
            EnumPopup<CullMode>(ShaderProperties.PropertyNames.CullMode, "Cull");
            EnumPopup<BlendMode>(ShaderProperties.PropertyNames.SrcBlend,      "Src Blend");
            EnumPopup<BlendMode>(ShaderProperties.PropertyNames.DstBlend,      "Dst Blend");
            EnumPopup<BlendMode>(ShaderProperties.PropertyNames.SrcBlendAlpha, "Src Blend Alpha");
            EnumPopup<BlendMode>(ShaderProperties.PropertyNames.DstBlendAlpha, "Dst Blend Alpha");
            EnumPopup<BlendOp>(ShaderProperties.PropertyNames.BlendOp, "Blend Op");

            EditorGUILayout.Space();
            Prop(ShaderProperties.PropertyNames.AlphaClip, "Alpha Clip");
            var ac = Find(ShaderProperties.PropertyNames.AlphaClip);
            if (ac != null && ac.floatValue >= 0.5f)
            {
                EditorGUI.indentLevel++;
                Prop(ShaderProperties.PropertyNames.Cutoff, "Threshold");
                EditorGUI.indentLevel--;
            }

            Prop(ShaderProperties.PropertyNames.ReceiveShadows, "Receive Shadows");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Section 2 — Surface Inputs
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Layout + logic mirror URP's Lit Surface Inputs (BaseShaderGUI.DrawBaseProperties + LitGUI.Inputs +
        // DrawEmissionProperties + DrawTileOffset), collapsed into one section. Strings are our own.
        protected virtual void DrawSurfaceInputs(Material material)
        {
            // ── Base Map + colour (URP BaseShaderGUI.DrawBaseProperties) ──
            var baseMap = Find(ShaderProperties.PropertyNames.BaseMap);
            var color   = Find(ShaderProperties.PropertyNames.BaseColor);
            if (baseMap != null && color != null)
                _editor.TexturePropertySingleLine(new GUIContent("Base Map"), baseMap, color);
            else if (color != null) _editor.ShaderProperty(color, "Color");

            // ── Metallic / Specular area (URP LitGUI.DoMetallicSpecularArea) ──
            // The metallic slider / spec colour is shown only when NO gloss map is assigned (the map drives it).
            var  workflow = Find(ShaderProperties.PropertyNames.WorkflowMode);
            bool specular = workflow != null && (WorkflowMode)(int)workflow.floatValue == WorkflowMode.Specular;
            if (specular)
            {
                var specMap = Find(ShaderProperties.PropertyNames.SpecGlossMap);
                if (specMap != null)
                    _editor.TexturePropertySingleLine(new GUIContent("Specular Map"), specMap,
                        specMap.textureValue != null ? null : Find(ShaderProperties.PropertyNames.SpecColor));
            }
            else
            {
                var metMap = Find(ShaderProperties.PropertyNames.MetallicGlossMap);
                if (metMap != null)
                    _editor.TexturePropertySingleLine(new GUIContent("Metallic Map"), metMap,
                        metMap.textureValue != null ? null : Find(ShaderProperties.PropertyNames.Metallic));
            }

            DrawSmoothness(material);

            // ── Normal / height / occlusion (URP LitGUI.Inputs) — scale/strength shown only when assigned ──
            Tex(ShaderProperties.PropertyNames.BumpMap,      "Normal Map",    ShaderProperties.PropertyNames.BumpScale);
            Tex(ShaderProperties.PropertyNames.ParallaxMap,  "Height Map",    ShaderProperties.PropertyNames.Parallax);
            Tex(ShaderProperties.PropertyNames.OcclusionMap, "Occlusion Map", ShaderProperties.PropertyNames.OcclusionStrength);

            // ── Emission (URP BaseShaderGUI.DrawEmissionProperties(material, keyword: true)) ──
            DrawEmissionProperties(material);

            // ── Base map tiling / offset (URP DrawTileOffset) ──
            if (baseMap != null) _editor.TextureScaleOffsetProperty(baseMap);
        }

        /// <summary>Smoothness slider + source-channel popup (URP LitGUI.DoSmoothness): the popup is indented
        /// under the slider and disabled when the surface is transparent (albedo-alpha can't be the source).</summary>
        protected void DrawSmoothness(Material material)
        {
            var smoothness = Find(ShaderProperties.PropertyNames.Smoothness);
            if (smoothness == null) return;

            EditorGUI.indentLevel += 2;
            _editor.ShaderProperty(smoothness, "Smoothness");

            var channel = Find(ShaderProperties.PropertyNames.SmoothnessTextureChannel);
            if (channel != null)
            {
                bool opaque = IsOpaque(material);
                EditorGUI.indentLevel++;
                EditorGUI.showMixedValue = channel.hasMixedValue;
                if (opaque)
                {
                    MaterialEditor.BeginProperty(channel);
                    EditorGUI.BeginChangeCheck();
                    int src = EditorGUILayout.Popup("Smoothness Source", (int)channel.floatValue, s_SmoothnessSrc);
                    if (EditorGUI.EndChangeCheck()) channel.floatValue = src;
                    MaterialEditor.EndProperty();
                }
                else
                {
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.Popup("Smoothness Source", 0, s_SmoothnessSrc);
                    EditorGUI.EndDisabledGroup();
                }

                EditorGUI.showMixedValue = false;
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel -= 2;
        }

        /// <summary>Emission row (URP BaseShaderGUI.DrawEmissionProperties with keyword: true). The enable
        /// checkbox (<see cref="MaterialEditor.EmissionEnabledProperty"/>) is what actually sets the material's
        /// <c>globalIlluminationFlags</c>; without it the <c>_EMISSION</c> keyword could never turn on from the
        /// inspector. The map+HDR-colour row is disabled while emission is off, and the GI flag is finalized
        /// via <see cref="MaterialEditor.LightmapEmissionFlagsProperty"/>.</summary>
        protected void DrawEmissionProperties(Material material)
        {
            var emMap = Find(ShaderProperties.PropertyNames.EmissionMap);
            var emCol = Find(ShaderProperties.PropertyNames.EmissionColor);

            bool emissive = _editor.EmissionEnabledProperty();
            using (new EditorGUI.DisabledScope(!emissive))
            {
                if (emMap != null && emCol != null)
                    using (new EditorGUI.IndentLevelScope(2))
                        _editor.TexturePropertyWithHDRColor(new GUIContent("Emission Map"), emMap, emCol, false);
            }

            // If a texture is assigned but the colour is black, bump it to white so it actually shows (URP).
            if (emMap                                 != null && emCol != null && emMap.textureValue != null
                && emCol.colorValue.maxColorComponent <= 0f)
                emCol.colorValue = Color.white;

            if (emissive)
                _editor.LightmapEmissionFlagsProperty(MaterialEditor.kMiniTextureFieldLabelIndentLevel, true);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Section 3 — Advanced
        // ─────────────────────────────────────────────────────────────────────────────────────────
        protected virtual void DrawAdvanced(Material material)
        {
            Prop(ShaderProperties.PropertyNames.SpecularHighlights,     "Specular Highlights");
            Prop(ShaderProperties.PropertyNames.EnvironmentReflections, "Environment Reflections");
            EditorGUILayout.Space();
            _editor.RenderQueueField(); // render queue edited directly on the material (S58)
            _editor.EnableInstancingField();
        }

        // ── helpers ──
        protected MaterialProperty Find(string name) => FindProperty(name, _props, false);

        protected void Prop(string name, string label)
        {
            var p = Find(name);
            if (p != null) _editor.ShaderProperty(p, label);
        }

        /// <summary>Texture row with an optional scale/strength field — shown ONLY when the texture is
        /// assigned (URP's <c>TexturePropertySingleLine(label, tex, tex.textureValue != null ? extra : null)</c>
        /// pattern, used for normal / height / occlusion / detail maps).</summary>
        protected void Tex(string texName, string label, string extraName)
        {
            var t = Find(texName);
            if (t == null) return;
            var extra = (extraName != null && t.textureValue != null) ? Find(extraName) : null;
            _editor.TexturePropertySingleLine(new GUIContent(label), t, extra);
        }

        /// <summary>Enum dropdown backed by a float MaterialProperty (undo + mixed-value safe).
        /// Labels are generated from the enum's value names by <see cref="EditorGUILayout.EnumPopup(string, Enum, GUILayoutOption[])"/>.</summary>
        protected void EnumPopup<TEnum>(string propName, string label) where TEnum : struct, Enum
        {
            var p = Find(propName);
            if (p == null) return;
            MaterialEditor.BeginProperty(p);
            EditorGUI.showMixedValue = p.hasMixedValue;
            EditorGUI.BeginChangeCheck();
            var current                                  = (TEnum)Enum.ToObject(typeof(TEnum), (int)p.floatValue);
            var next                                     = EditorGUILayout.EnumPopup(label, current);
            if (EditorGUI.EndChangeCheck()) p.floatValue = Convert.ToInt32(next);
            EditorGUI.showMixedValue = false;
            MaterialEditor.EndProperty();
        }
    }
}
