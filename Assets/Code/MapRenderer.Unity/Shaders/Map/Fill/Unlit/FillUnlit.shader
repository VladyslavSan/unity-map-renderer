// FillUnlit.shader — Map/FillUnlit (unlit twin of Map/Fill; also serves Background — see
// MaterialFactory.CreateBackgroundMaterial, which clones the FILL base for both variants).
//
// Mirrors stock URP Unlit.shader's pass list (Unlit / DepthOnly / DepthNormalsOnly) the way Fill.shader
// mirrors URP Lit.shader — verbatim + minimal marked deltas. GBuffer/Meta/MotionVectors are deferred
// (unlit rendering mode epic §3/§6 — not needed until a gate forces one); ShadowCaster is DROPPED by
// construction (stock URP Unlit has none — an unlit material cannot cast a shadow).
//
// Key design points:
//   • Fragment is flat albedo × _BaseColor × vColor / alpha × _Opacity — no lighting, no SurfaceData.
//     See Fill_UnlitForwardPass.hlsl for the exact composite and its rationale.
//   • CBUFFER (UnityPerMaterial) is IDENTICAL across all passes — SRP Batcher requires this.
//   • Render state: same parameterized contract as Fill.shader (Cull/ZWrite/ZTest/Blend all driven by
//     material floats so FillTweaker's runtime contract binds unchanged) — S37's queue-2000 regression
//     guard applies here exactly as it does to Fill.shader; see the Properties block below.
//   • The DepthOnly/DepthNormals passes reuse Fill_DepthOnlyPass.hlsl/Fill_DepthNormalsPass.hlsl
//     VERBATIM (no new file) — they already carry no lighting math and consume the same Attributes this
//     shader's forward pass does.
//
// Clean-room: this is URP integration, not MapLibre. URP docs/source are fair reference.
// Authored for URP 17.5 / Unity 6000.x.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
Shader "Map/FillUnlit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (0.4, 0.7, 0.4, 1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        // ── Blending state (mirrors Fill.shader; consumed by URP's ValidateMaterial) ──
        // S37: these props must be DECLARED so material.HasProperty(...) is true and the queue resolves
        // from _Surface/_Blend rather than URP defaulting the material to opaque queue 2000 on import
        // (clobbering the SubShader's Queue=Transparent). Defaults mirror Fill.shader's flat-fill
        // contract exactly (transparent, no depth write) — see that file's constraint-1 comment.
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
        _AlphaToMask("__alphaToMask", Float) = 0.0
        _QueueOffset("Queue offset", Float) = 0.0

        // Obsolete URP properties (hidden; kept for material upgrade compatibility — mirrors stock
        // Unity/Unlit/Unlit.shader's obsolete-properties block).
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (0.5, 0.5, 0.5, 1)

        // ── Map paint properties (mirrors Fill.shader's own block — same names, so the C# registry's
        // existing Fill PropertyId/PropertyNames entries and FillTweaker bind onto this material
        // unchanged) ──
        _Opacity ("Opacity", Range(0, 1)) = 1.0
        _FillOutlineColor ("Fill Outline Color", Color) = (0, 0, 0, 1)
        _FillAntialias ("Fill Antialias", Float) = 1.0
        _FillTranslate ("Fill Translate (xy px)", Vector) = (0, 0, 0, 0)
        _FillTranslateAnchor ("Fill Translate Anchor", Float) = 0.0
        _FillPattern ("Fill Pattern", Float) = 0.0

        [NoScaleOffset] _PatternMap ("Fill Pattern Sheet", 2D) = "white" {}
        _PatternRect ("Fill Pattern Rect (xy=px origin, zw=px size)", Vector) = (0, 0, 0, 0)
        _PatternScale ("Fill Pattern Repeats Per Tile", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "IgnoreProjector" = "True"
        }
        LOD 100

        // Forward-pass render state — fully parameterized, same contract as Fill.shader (§ "Blending
        // state" above). Only the forward pass is parameterized; DepthOnly/DepthNormals keep their own
        // forced `ZWrite On` (they must write depth regardless of the forward pass's setting).
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
        BlendOp [_BlendOp]
        AlphaToMask [_AlphaToMask]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Cull [_Cull]

        // ─────────────────────────────────────────────────────────────────────
        // Pass 1: the unlit forward pass. LightMode = UniversalForward — matches this repo's own unlit map
        // precedent (SymbolTextWorld.shader, the only other unlit map shader) and its lit twins (Fill.shader's
        // ForwardLit), NOT stock URP Unlit.shader's untagged SRPDefaultUnlit. Both render under the default
        // forward renderer, but the explicit tag is what keeps a fill visible if the map ever draws through a
        // RenderObjects/custom pass filtered on UniversalForward.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "Unlit"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   UnlitPassVertex
            #pragma fragment UnlitPassFragment

            // ── Material Keywords (mirrors stock Unlit.shader's "Unlit" pass) ──
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHAMODULATE_ON

            // ── Unity defined keywords ───────────────────────────────────────
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            // ── GPU Instancing ───────────────────────────────────────────────
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // Include order (define-before-use): input first (CBUFFER + DOTS bridge), then
            // vertex-modify body (MapVertexModify definition — reused verbatim from the Lit twin), then
            // pass body.
            #include "Fill_UnlitInput.hlsl"
            #include "../Fill_VertexModify.hlsl"
            #include "Fill_UnlitForwardPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 2: DepthOnly — camera depth prepass / _CameraDepthTexture. Reuses Fill_DepthOnlyPass.hlsl
        // verbatim (no new file) — see Fill.shader's own DepthOnly pass, which this mirrors exactly
        // except for the CBUFFER include.
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

            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Fill_UnlitInput.hlsl"
            #include "../Fill_VertexModify.hlsl"
            #include "../Fill_DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 3: DepthNormalsOnly — depth+normals prepass (SSAO). Reuses Fill_DepthNormalsPass.hlsl
        // verbatim. NORMALMAP/PARALLAXMAP/DETAIL keywords are intentionally NOT declared here (unlike
        // Fill.shader's DepthNormals pass): Fill_UnlitInput.hlsl declares no _BumpMap/_DetailAlbedoMap
        // CBUFFER members, so enabling those keywords would try to compile a branch that reads
        // properties this shader never declares. Omitting the pragmas keeps those branches compiled out
        // (the shader_feature macro is simply never defined) — see Fill_DepthNormalsPass.hlsl.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthNormalsOnly"
            Tags
            {
                "LightMode" = "DepthNormalsOnly"
            }

            ZWrite On

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex   DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            #pragma shader_feature_local _ALPHATEST_ON

            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Fill_UnlitInput.hlsl"
            #include "../Fill_VertexModify.hlsl"
            #include "../Fill_DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "MapRenderer.Unity.Editor.FillUnlitShaderGUI"
}
