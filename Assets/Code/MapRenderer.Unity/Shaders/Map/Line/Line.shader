// Line.shader — Map/Line (S33: lit, forward-transparent, world-space extrusion)
//
// S67: FIVE passes — ForwardLit + ShadowCaster + DepthOnly + DepthNormals + GBuffer.
// Passes 2-5 are CAPABILITY-ONLY (present-but-inert). URP excludes Queue=Transparent (>=2501)
// materials from the opaque depth/GBuffer prepasses, so they never execute at runtime.
// S69 will activate them when line rendering mode is changed (opaque-queue or forced shadows).
//
// Key design points (S33/S66/S67):
//   • Fragment uses InitializeStandardLitSurfaceData — NEVER hand-assembled SurfaceData (S34).
//   • CBUFFER (UnityPerMaterial) in Line_LitInput.hlsl; byte-IDENTICAL across all passes (SRP Batcher).
//   • Line_VertexExtrude.hlsl: shared helper included before every pass body.
//     Defines: LineAttributes struct, Line_VertexExtrude() (world-space extrusion, S05 fix),
//     LineCoverage() (fwidth-smoothstep outer+inner+dash coverage). Single extrusion site.
//   • NORMAL stream = constant +Y (lighting); extrudeN on TEXCOORD0 (not overloaded).
//   • Alpha = LineCoverage() × _Opacity (S05 formula; makes _Opacity functional).
//   • Tiny +Y lift (0.001m) in Line_VertexExtrude, applied to every pass equally.
//   • ForwardLit: S58-parameterized Blend/ZWrite/ZTest/Cull.
//   • Passes 2-5: inherit the SubShader-parameterized Cull [_Cull]; override ZWrite On + ZTest LEqual
//     (the SubShader ZWrite default 0 would break their depth/shadow writes).
//
// Shader name: Map/Line (replaces Hidden/Map/Line_S05_Deprecated in Materials/).
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
// Authored for URP 17.5 / Unity 6000.x. Clean-room URP integration (not MapLibre source).
Shader "Map/Line"
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

        // ── Map line properties ──────────────────────────────────────────────
        // TWO separate groups — see Line_LitInput.hlsl for the rule:
        //   (A) STYLE-BOUND : MapLibre line-* paint/layout, written by the styler (line-X → _X).
        //   (B) INTERNAL    : engine render params the styler never touches — names MUST avoid the
        //                     line-*/fill-* namespace (the AA width was once `_Blur` = line-blur → bug).

        // (A) Style-bound — MapLibre line-* paint/layout:
        // The line color is the standard _BaseColor above; _Opacity modulates alpha.
        _Opacity        ("Opacity (line-opacity)", Range(0, 1)) = 1.0
        _Width          ("Width (line-width, m or px)", Float) = 2.0
        // line-blur (px, spec default 0): opt-in soft edge (MapLibre line-blur, NOT antialiasing). 0 = hard.
        _Blur           ("Line Blur (line-blur, px)", Range(0, 8)) = 0.0
        // S14: line-gap-width — hollow/cased line. 0 = solid (default). Units = pixels (same as _Width).
        _GapWidth       ("Gap Width (line-gap-width, px)", Float) = 0.0
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

        // (B) Internal render params — NOT style properties (the styler never writes these):
        [Toggle]
        _WidthIsPixels  ("Width In Pixels", Float)    = 0.0
        // Edge antialiasing (internal): 1 = on (default), 0 = off ⇒ _EDGE_ANTIALIASING_OFF.
        // [ToggleUI] is UI-only and attaches NO keyword — LineShaderGUI.ValidateMaterial syncs it in code,
        // mirroring _ReceiveShadows (:96). NOT a CBUFFER member.
        [ToggleUI] _EdgeAntialiasing ("Edge Antialiasing", Float) = 1.0
        // Hairline strategy (internal): 0 = Default (today's straddle, no keyword); 1 = Hard
        // (_HAIRLINE_HARD — the ramp narrows toward a step below ~2 px band); 2 = SolidCore
        // (_HAIRLINE_SOLID_CORE — the band is clamped to 2 px and coverage scaled back down).
        // [Enum] is a UI-only drawer and attaches NO keyword; LineShaderGUI.ValidateMaterial syncs it in
        // code, exactly as _EdgeAntialiasing above. NOT a CBUFFER member.
        [Enum(Default, 0, Hard, 1, SolidCore, 2)] _HairlineStrategy ("Hairline Strategy", Float) = 0
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
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
        BlendOp [_BlendOp]
        AlphaToMask [_AlphaToMask]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Cull [_Cull]

        // ─────────────────────────────────────────────────────────────────────
        // Pass: UniversalForward — lit forward-transparent pixels.
        // The only pass that renders for the transparent queue; passes 2-5 (ShadowCaster/GBuffer/DepthOnly/
        // DepthNormals) are S67 capability passes — present-but-inert (URP excludes Queue=Transparent).
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   LinePassVertex
            #pragma fragment LinePassFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            // Straddle-AA off switch. PLAIN shader_feature_local (never _fragment): the pad is a
            // vertex-stage change, so stripping the keyword from the vertex stage would desync the
            // silhouette across passes.
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF
            // Hairline strategy. `_` (no keyword) is the shipping default and can never be stripped.
            // Plain shader_feature_local, not _fragment: A6a is fragment-only, but a keyword SET cannot be
            // half-fragment and A6b extends this same set with a vertex-stage member.
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD
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

            // -------------------------------------
            // Universal Pipeline keywords
#if defined(UNITY_PLATFORM_META_QUEST)
            #pragma multi_compile _ META_QUEST_LIGHTUNROLL
#endif
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
#if defined(UNITY_PLATFORM_META_QUEST)
            #pragma multi_compile _ META_QUEST_ORTHO_PROJ
            #pragma multi_compile _ META_QUEST_NO_SPOTLIGHTS_LIGHT_LOOP
#endif
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"


            // -------------------------------------
            // Unity defined keywords
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

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // Include order (define-before-use): input (CBUFFER + DOTS bridge), helper
            // (LineAttributes + Line_VertexExtrude + LineCoverage), then pass body.
            #include "Line_LitInput.hlsl"
            #include "Line_VertexExtrude.hlsl"
            #include "Line_LitForwardPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass: ShadowCaster — CAPABILITY-ONLY (present-but-inert for transparent lines).
        // URP only invokes this for opaque-queue materials. S69 activates it.
        // Inherits the SubShader-parameterized Cull [_Cull]; overrides ZWrite On / ZTest LEqual
        // (the SubShader ZWrite default 0 would break shadow depth writes).
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   LineShadowPassVertex
            #pragma fragment LineShadowPassFragment

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF // shared extrusion silhouette (see ForwardLit)
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD // shared coverage model (see ForwardLit)

            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Line_LitInput.hlsl"
            #include "Line_VertexExtrude.hlsl"
            #include "Line_ShadowCasterPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass: DepthOnly — CAPABILITY-ONLY (present-but-inert for transparent lines).
        // URP only invokes this for opaque-queue materials. S69 activates it.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   LineDepthOnlyVertex
            #pragma fragment LineDepthOnlyFragment

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF // shared extrusion silhouette (see ForwardLit)
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD // shared coverage model (see ForwardLit)

            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Line_LitInput.hlsl"
            #include "Line_VertexExtrude.hlsl"
            #include "Line_DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass: DepthNormals — CAPABILITY-ONLY (present-but-inert for transparent lines).
        // URP only invokes this for opaque-queue materials. S69 activates it.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   LineDepthNormalsVertex
            #pragma fragment LineDepthNormalsFragment

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF // shared extrusion silhouette (see ForwardLit)
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD // shared coverage model (see ForwardLit)

            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Line_LitInput.hlsl"
            #include "Line_VertexExtrude.hlsl"
            #include "Line_DepthNormalsPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass: GBuffer — CAPABILITY-ONLY (present-but-inert for transparent lines).
        // URP only invokes this for opaque-queue materials in deferred mode. S69 activates it.
        // Requires shader target 4.5 and excludes renderers without MRT support.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }

            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            #pragma vertex   LineGBufferPassVertex
            #pragma fragment LineGBufferPassFragment

            // ── Material keywords ────────────────────────────────────────────
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF // shared extrusion silhouette (see ForwardLit)
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD // shared coverage model (see ForwardLit)

            // ── Universal Pipeline keywords ──────────────────────────────────
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // ── Unity defined keywords ───────────────────────────────────────
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            // ── GPU Instancing ───────────────────────────────────────────────
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Line_LitInput.hlsl"
            #include "Line_VertexExtrude.hlsl"
            #include "Line_LitGBufferPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "MapRenderer.Unity.Editor.LineShaderGUI"
}
