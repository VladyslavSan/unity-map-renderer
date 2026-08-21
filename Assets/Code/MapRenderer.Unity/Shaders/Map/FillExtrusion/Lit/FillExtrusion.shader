// FillExtrusion.shader — Map/FillExtrusion (S23 I2b, structural parity with URP Lit.shader / Map/Fill)
//
// Lit fill-extrusion shader for unity-map-renderer: roof cap + side walls, height extrusion entirely in
// the vertex shader (see FillExtrusion_VertexModify.hlsl).
// Mirrors URP Lit.shader's pass list (ForwardLit / ShadowCaster / GBuffer / DepthOnly /
// DepthNormals) — same shape as Map/Fill, with the D1 extrusion vertex attributes added and no
// fill-pattern (the spec has none for this layer).
//
// Key design points (mirrors S34's Fill.shader design, "Key design points"):
//   • Fragment uses InitializeStandardLitSurfaceData — NEVER hand-assembled SurfaceData.
//   • Full URP Lit property set (base/normal/metallic/occlusion/emission/detail maps, etc.)
//     plus the _Opacity map paint property (the layer color is the standard _BaseColor).
//   • CBUFFER (UnityPerMaterial) is IDENTICAL across all passes — SRP Batcher requires this.
//   • Render state: the property DEFAULTS below mirror URP/Map/Fill (_ZWrite=0, alpha blend), but the
//     RUNTIME contract for fill-extrusion is ELEVATED-3D — opaque, depth-writing (ZWrite On, ZTest LEqual,
//     One/Zero blend) — asserted by FillExtrusionTweaker.ApplyElevatedContract and baked into the
//     Map/FillExtrusion .mat (I3). Buildings must occupy the depth buffer to occlude one another and their
//     own walls; see docs/depth-and-render-regimes-design.md §5. [_Cull] drives winding as Fill's does.
//
// Clean-room: this is URP integration, not MapLibre. URP docs/source are fair reference.
// Authored for URP 17.5 / Unity 6000.x.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
Shader "Map/FillExtrusion"
{
    Properties
    {
        // ── Standard URP Lit properties (mirrors URP Lit.shader / Map/Fill) ─────────────
        _WorkflowMode("WorkflowMode", Float) = 1.0

        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (0.6, 0.6, 0.6, 1)

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

        // ── Blending state (mirrors Map/Fill; consumed by URP's ValidateMaterial) ──
        // I2b keeps the FILL transparent-band defaults — opaque/depth render state is I3 (fence).
        _Surface("__surface", Float) = 1.0
        _Blend("__blend", Float) = 0.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        _SrcBlend("__src", Float) = 5.0
        _DstBlend("__dst", Float) = 10.0
        _SrcBlendAlpha("__srcA", Float) = 1.0
        _DstBlendAlpha("__dstA", Float) = 10.0
        _ZWrite("__zw", Float) = 0.0
        _ZTest("__ztest", Float) = 4.0
        _Cull("__cull", Float) = 0.0
        _BlendOp("__blendop", Float) = 0.0
        _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        _AlphaToMask("__alphaToMask", Float) = 0.0
        _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0
        _XRMotionVectorsPass("_XRMotionVectorsPass", Float) = 1.0

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

        // ── Map paint properties (S23 I2b) ─────────────────────────
        // These are the MapLibre-style styling knobs; they modulate the URP surface.
        // The layer color is the standard _BaseColor above; _Opacity modulates alpha.
        _Opacity ("Opacity", Range(0, 1)) = 1.0

        // fill-extrusion-height/-base: constant/zoom uniforms (S11); data-driven bakes per-vertex instead
        // (S23 I2b builder) — the VS composes them additively (see FillExtrusion_VertexModify.hlsl).
        // NOT named _Height/_Base — _Base collides with nothing today, but _ExtrusionBase reserves the
        // name against a future generic "base" property the way _FillOutlineColor etc. do.
        _ExtrusionHeight ("Extrusion Height (m)", Float) = 0.0
        _ExtrusionBase ("Extrusion Base (m)", Float) = 0.0

        // fill-extrusion-translate: xy = pixel offset (world/viewport per _FillExtrusionTranslateAnchor).
        _FillExtrusionTranslate ("FillExtrusion Translate (xy px)", Vector) = (0, 0, 0, 0)
        _FillExtrusionTranslateAnchor ("FillExtrusion Translate Anchor", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        // Forward-pass render state — fully parameterized (mirrors Map/Fill's S58 shape). I2b defaults
        // reproduce the FILL transparent-band behaviour: Blend driven by _SrcBlend/_DstBlend, BlendOp Add,
        // ZWrite [_ZWrite] (0 here — painter's-algorithm transparent), ZTest LEqual, Cull Off. Opaque/depth
        // render state (a per-layer tweaker) is I3.
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
        BlendOp [_BlendOp]
        AlphaToMask [_AlphaToMask]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Cull [_Cull]

        // ─────────────────────────────────────────────────────────────────────
        // Pass 1: UniversalForward — lit pixels, all lights.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   LitPassVertex
            #pragma fragment LitPassFragment

            // ── Material Keywords (mirrors URP Lit.shader ForwardLit pass / Map/Fill) ──
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

            // Include order (define-before-use): input first (CBUFFER + DOTS bridge),
            // then vertex-modify body (MapVertexModify definition), then pass body.
            #include "FillExtrusion_LitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "FillExtrusion_LitForwardPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 2: ShadowCaster — geometry seen by the shadow map.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            ZWrite On
            ZTest LEqual
            ColorMask 0

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

            #include "FillExtrusion_LitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "FillExtrusion_ShadowCasterPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 3: GBuffer — deferred G-Buffer fill (opaque only).
        // Fills are deferred-eligible; this pass populates the GBuffer for
        // deferred lighting. Deferred renderer activation is out of scope (S34/S23).
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "GBuffer"
            Tags
            {
                "LightMode" = "UniversalGBuffer"
            }

            ZWrite On
            ZTest LEqual

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

            #include "FillExtrusion_LitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "FillExtrusion_LitGBufferPass.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutputFormat.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 4: DepthOnly — camera depth prepass / _CameraDepthTexture.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "FillExtrusion_LitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "../FillExtrusion_DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 5: DepthNormals — depth+normals prepass (SSAO).
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            ZWrite On

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

            #include "FillExtrusion_LitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "../FillExtrusion_DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "MapRenderer.Unity.Editor.FillExtrusionShaderGUI"
}
