// Line.shader — MapRenderer/Line (S33: lit, forward-transparent, world-space extrusion)
//
// Lit line shader for unity-map-renderer.
// ONE pass only: UniversalForward (forward-transparent, Queue=Transparent>=2501).
// DO NOT add GBuffer/ShadowCaster/DepthOnly/DepthNormals — lines are transparent,
// excluded from the opaque depth/GBuffer prepasses, and those passes are dead weight.
//
// Key design points (S33):
//   • Fragment uses InitializeStandardLitSurfaceData — NEVER hand-assembled SurfaceData.
//   • CBUFFER (UnityPerMaterial) in MapLineInput.hlsl; IDENTICAL in the single pass (SRP Batcher).
//   • Extrusion done in world space with normalize() in MapLineForwardPass.hlsl (decisive S05 fix).
//   • NORMAL stream = constant +Y (lighting); extrudeN on separate TEXCOORD0 (not overloaded).
//   • Alpha = fwidth-smoothstep coverage × _Opacity (S05 formula; makes _Opacity functional).
//   • Tiny +Y lift (0.001m) in vertex shader for coplanar fill/line z-fighting (#7).
//   • Blend SrcAlpha OneMinusSrcAlpha, ZWrite Off, Cull Off.
//
// Shader name: MapRenderer/Line (replaces Hidden/MapRenderer/Line_S05_Deprecated in Materials/).
//
// See docs/lit-rendering-design.md §"Line specifics (S33)" for full design rationale.
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
// Authored for URP 17.5 / Unity 6000.x. Clean-room URP integration (not MapLibre source).
Shader "MapRenderer/Line"
{
    Properties
    {
        // ── Standard URP Lit properties (mirrors URP Lit.shader) ─────────────
        _WorkflowMode("WorkflowMode", Float) = 1.0

        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (0.3, 0.5, 1.0, 1)

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        _SpecColor("Specular", Color) = (0.2, 0.2, 0.2)
        _SpecGlossMap("Specular", 2D) = "white" {}

        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1.0

        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax("Scale", Range(0.005, 0.08)) = 0.005
        _ParallaxMap("Height Map", 2D) = "black" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        [HDR] _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMapScale("Scale", Range(0.0, 2.0)) = 1.0
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "linearGrey" {}
        _DetailNormalMapScale("Scale", Range(0.0, 2.0)) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}

        [HideInInspector] _ClearCoatMask("_ClearCoatMask", Float) = 0.0
        [HideInInspector] _ClearCoatSmoothness("_ClearCoatSmoothness", Float) = 0.0

        // ── Blending state (mirrors URP Lit.shader; consumed by URP's ValidateMaterial) ──
        // S37: the line is intrinsically transparent (ShaderLab hardcodes Blend/ZWrite Off/
        // Queue=Transparent). These props must be DECLARED here so material.HasProperty(...)
        // is true and URP's ValidateMaterial (run on every import via MapLitShaderGUI :
        // BaseShaderGUI) resolves the queue from _Surface/_QueueControl. Without them
        // ValidateMaterial defaults the material to opaque and forces queue 2000, clobbering
        // the SubShader's Queue=Transparent on import (the S37 regression). Defaults =
        // transparent (_Surface=1, _Blend=0 alpha, _SrcBlend=SrcAlpha, _DstBlend=OneMinusSrcAlpha,
        // _ZWrite off) so a default-constructed line material is import-stable at Queue>=2501.
        // These do NOT fight the fixed ShaderLab render state — they only feed queue resolution.
        _Surface("__surface", Float) = 1.0
        _Blend("__blend", Float) = 0.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        _SrcBlend("__src", Float) = 5.0
        _DstBlend("__dst", Float) = 10.0
        _SrcBlendAlpha("__srcA", Float) = 1.0
        _DstBlendAlpha("__dstA", Float) = 10.0
        _ZWrite("__zw", Float) = 0.0
        // S58: forward render state as parameters (driven by the typed tweaker layer / ShaderGUI).
        // Defaults reproduce the previously-hardcoded line state: ZTest LEqual(4), Cull Off(0), BlendOp Add(0).
        // (_SrcBlend=5 SrcAlpha / _DstBlend=10 OneMinusSrcAlpha / _ZWrite=0 already match the old hardcode.)
        _ZTest("__ztest", Float) = 4.0
        _Cull("__cull", Float) = 0.0
        _BlendOp("__blendop", Float) = 0.0
        _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        _AlphaToMask("__alphaToMask", Float) = 0.0
        _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0
        _XRMotionVectorsPass("_XRMotionVectorsPass", Float) = 1.0

        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        _QueueOffset("Queue offset", Float) = 0.0

        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _GlossMapScale("Smoothness", Float) = 0.0
        [HideInInspector] _Glossiness("Smoothness", Float) = 0.0
        [HideInInspector] _GlossyReflections("EnvironmentReflections", Float) = 0.0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}

        // ── Map paint / line-specific properties ─────────────────────────────
        // The line color is the standard _BaseColor above; _Opacity modulates alpha.
        _Opacity        ("Opacity", Range(0, 1)) = 1.0

        _Width          ("Width (m or px)", Float)    = 2.0
        [Toggle]
        _WidthIsPixels  ("Width In Pixels", Float)    = 0.0
        _MetersPerPixel ("Meters Per Pixel", Float)   = 1.0
        _Blur           ("Blur (AA feather)", Range(0, 4)) = 1.0

        // S14: line-gap-width — hollow/cased line. 0 = solid (default). Units = pixels (same as _Width).
        _GapWidth       ("Gap Width (px)", Float)     = 0.0
        // S14: line-translate — pixel offset for the rendered ribbon.
        _LineTranslate  ("Line Translate (px xy)", Vector) = (0, 0, 0, 0)
        // S14: line-translate-anchor — 0 = map (world-space), 1 = viewport (screen-space).
        _LineTranslateAnchor ("Translate Anchor", Float) = 0.0
        // S14: line-pattern hook — 0 = solid color fallback, 1 = pattern (real sampling deferred to S17).
        _LinePattern    ("Line Pattern (hook)", Float) = 0.0
        // S43: line-dasharray — on/off lengths in line-width units (up to 4 values packed into a Vector).
        // _DashCount = 0 → solid identity (no dashing). _DashCount = 2 → [on, off] pair, etc.
        _DashArray      ("Dash Array (4 on/off, width units)", Vector) = (0,0,0,0)
        _DashCount      ("Dash Entry Count", Float) = 0.0
        // S44: line-offset — perpendicular band-center shift in pixels (same units as _Width).
        // 0 = no shift (default). Positive = left of travel direction.
        _LineOffset     ("Line Offset (px)", Float) = 0.0
    }

    SubShader
    {
        // Transparent: forward-lit, painter's-algorithm coplanar lines, ZWrite Off.
        Tags
        {
            "RenderType"             = "Transparent"
            "Queue"                  = "Transparent"
            "RenderPipeline"         = "UniversalPipeline"
            "UniversalMaterialType"  = "Lit"
            "IgnoreProjector"        = "True"
        }
        LOD 300

        // Two-sided ribbon; no depth write (coplanar layer ordering via render queue).
        // S58: parameterized — defaults (Cull Off / ZWrite Off / ZTest LEqual) reproduce the prior state.
        Cull [_Cull]
        ZWrite [_ZWrite]
        ZTest [_ZTest]

        // ─────────────────────────────────────────────────────────────────────
        // Pass: UniversalForward — lit forward-transparent pixels.
        // ONLY pass. No GBuffer/ShadowCaster/DepthOnly/DepthNormals for transparent lines.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            // S58: fully parameterized forward render state. Defaults reproduce the prior hardcode
            // (Blend SrcAlpha OneMinusSrcAlpha / ZWrite Off / ZTest LEqual / Cull Off / BlendOp Add).
            Blend [_SrcBlend] [_DstBlend]
            BlendOp [_BlendOp]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   LinePassVertex
            #pragma fragment LinePassFragment

            // ── Material keywords ────────────────────────────────────────────
            // No _NORMALMAP / _DETAIL / _PARALLAXMAP for the line (flat +Y normal; UV=0).
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF

            // ── Universal Pipeline keywords ──────────────────────────────────
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // ── Unity defined keywords ───────────────────────────────────────
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            // ── GPU Instancing ───────────────────────────────────────────────
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // Include: MapLineForwardPass.hlsl includes MapLineInput.hlsl (line CBUFFER fork)
            // and defines LinePassVertex + LinePassFragment.
            #include "MapLineForwardPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "MapRenderer.Unity.Editor.LineShaderGUI"
}
