// Fill.shader — MapRenderer/Fill (S34 structural parity with URP Lit.shader)
//
// Lit fill shader for unity-map-renderer.
// Mirrors URP Lit.shader's pass list (ForwardLit / ShadowCaster / GBuffer / DepthOnly /
// DepthNormals); each pass delegates to our mirror-copied building blocks which carry
// the full URP material surface + map paint additions (_MapColor, _Opacity).
//
// Key design points (S34):
//   • Fragment uses InitializeStandardLitSurfaceData — NEVER hand-assembled SurfaceData.
//   • Full URP Lit property set (base/normal/metallic/occlusion/emission/detail maps, etc.)
//     plus _MapColor/_Opacity map paint properties.
//   • CBUFFER (UnityPerMaterial) is IDENTICAL across all passes — SRP Batcher requires this.
//   • Render state: hardcoded Cull Off (winding follow-up). ZWrite is driven by [_ZWrite]
//     (default 1.0 = opaque depth write, identical to stock URP Lit) so painter's-algorithm
//     flat layers (S07) can set _ZWrite=0 + renderQueue=base+index for coplanar compositing.
//     This restores stock URP Lit's `ZWrite [_ZWrite]` on the forward pass; the prior hardcoded
//     `ZWrite On` was the S34 deviation. Passes 2-5 (ShadowCaster/GBuffer/DepthOnly/DepthNormals)
//     keep their own `ZWrite On` — they must write depth regardless of the forward-pass setting.
//
// Clean-room: this is URP integration, not MapLibre. URP docs/source are fair reference.
// Authored for URP 17.5 / Unity 6000.x.
//
// See docs/lit-rendering-design.md for the full design rationale.
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
Shader "MapRenderer/Fill"
{
    Properties
    {
        // ── Standard URP Lit properties (mirrors URP Lit.shader) ─────────────
        // Specular vs Metallic workflow
        _WorkflowMode("WorkflowMode", Float) = 1.0

        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)

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

        // SRP batching compatibility for Clear Coat (Not used in Lit)
        [HideInInspector] _ClearCoatMask("_ClearCoatMask", Float) = 0.0
        [HideInInspector] _ClearCoatSmoothness("_ClearCoatSmoothness", Float) = 0.0

        // Blending state (set by ShaderGUI; defaults = opaque)
        _Surface("__surface", Float) = 0.0
        _Blend("__blend", Float) = 0.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector] _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0

        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        _QueueOffset("Queue offset", Float) = 0.0

        // Obsolete URP properties (hidden; kept for material upgrade compatibility)
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _GlossMapScale("Smoothness", Float) = 0.0
        [HideInInspector] _Glossiness("Smoothness", Float) = 0.0
        [HideInInspector] _GlossyReflections("EnvironmentReflections", Float) = 0.0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}

        // ── Map paint properties (S34/S13 additions) ─────────────────────────
        // These are the MapLibre-style styling knobs; they modulate the URP surface.
        // _MapColor modulates albedo; _Opacity modulates alpha (active in transparent queue).
        _MapColor  ("Map Color", Color) = (0.4, 0.7, 0.4, 1)
        _Opacity   ("Opacity", Range(0, 1)) = 1.0

        // S13 fill paint properties (MapLibre Style Spec fill layer):
        // _FillOutlineColor: color of the optional fill outline (future outline pass).
        // _FillAntialias: 1=AA on (default), 0=off.
        // _FillTranslate: xy = pixel offset (world/viewport per _FillTranslateAnchor). zw unused.
        // _FillTranslateAnchor: 0=map world-space, 1=viewport screen-space.
        // _FillPattern: sprite atlas index or flag for pattern fills. 0=no pattern (default).
        _FillOutlineColor    ("Fill Outline Color", Color) = (0, 0, 0, 1)
        _FillAntialias       ("Fill Antialias", Float) = 1.0
        _FillTranslate       ("Fill Translate (xy px)", Vector) = (0, 0, 0, 0)
        _FillTranslateAnchor ("Fill Translate Anchor", Float) = 0.0
        _FillPattern         ("Fill Pattern", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"             = "Opaque"
            "Queue"                  = "Geometry"
            "RenderPipeline"         = "UniversalPipeline"
            "UniversalMaterialType"  = "Lit"
            "IgnoreProjector"        = "True"
        }
        LOD 300

        // Cull Off: winding correctness is a deliberate follow-up (see ARCHITECTURE §2 / follow-ups.md).
        // ZWrite [_ZWrite]: default 1.0 → opaque geometry writes depth (stock URP Lit behaviour);
        //   S07 painter's-algorithm flat layers set _ZWrite=0 at runtime + renderQueue=base+index so
        //   coplanar fills/lines composite in declared order with no z-fighting (ARCHITECTURE §2).
        //   Only the ForwardLit pass inherits this; the depth/shadow/GBuffer passes force ZWrite On.
        Cull Off
        ZWrite [_ZWrite]
        ZTest LEqual

        // ─────────────────────────────────────────────────────────────────────
        // Pass 1: UniversalForward — lit pixels, all lights.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            // Blend state driven by _SrcBlend/_DstBlend (set by ShaderGUI or C# for transparent queue).
            // Default values (1,0 / 1,0) = opaque. Set _Surface=1 for transparent mode.
            // AlphaToMask parity with URP Lit.shader ForwardLit pass.
            Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   LitPassVertex
            #pragma fragment LitPassFragment

            // ── Material Keywords (mirrors URP Lit.shader ForwardLit pass) ──
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ _ALPHAPREMULTIPLY_ON _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP

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

            // Include order: pass body first (brings MapLitInput.hlsl + MapLitCore.hlsl
            // which declare MapVertexModify); then Fill_Input.hlsl which defines the body.
            #include "MapLitForwardPass.hlsl"
            #include "Fill_Input.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 2: ShadowCaster — geometry seen by the shadow map.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "MapShadowCasterPass.hlsl"
            #include "Fill_Input.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 3: GBuffer — deferred G-Buffer fill (opaque only).
        // Fills are deferred-eligible; this pass populates the GBuffer for
        // deferred lighting. Deferred renderer activation is out of scope (S34).
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }

            ZWrite On
            ZTest LEqual
            Cull Off

            // Deferred path is not supported on OpenGL (same exclusion as URP Lit.shader).
            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            #pragma vertex   LitGBufferPassVertex
            #pragma fragment LitGBufferPassFragment

            // ── Material Keywords ────────────────────────────────────────────
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF

            // ── Universal Pipeline keywords ──────────────────────────────────
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
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
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            // ── GPU Instancing ───────────────────────────────────────────────
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "MapLitGBufferPass.hlsl"
            #include "Fill_Input.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutputFormat.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 4: DepthOnly — camera depth prepass / _CameraDepthTexture.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "MapDepthOnlyPass.hlsl"
            #include "Fill_Input.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 5: DepthNormals — depth+normals prepass (SSAO).
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "MapDepthNormalsPass.hlsl"
            #include "Fill_Input.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "MapRenderer.Unity.Editor.MapLitShaderGUI"
}
