// FillExtrusionUnlit.shader — Map/FillExtrusionUnlit (unlit twin of Map/FillExtrusion)
//
// Mirrors stock URP Unlit.shader's pass list (Unlit / DepthOnly / DepthNormalsOnly) the way FillUnlit.shader
// mirrors it for the flat fill layer — verbatim + minimal marked deltas. It has no GBuffer/Meta/
// MotionVectors pass, and no ShadowCaster (stock URP Unlit has none — an unlit material cannot cast a
// shadow). So unlit buildings cast no shadow: that follows from mirroring stock Unlit, not from a design
// decision about building shadows.
//
// Key design points:
//   • Unlit ≠ 2D: the height extrusion happens ENTIRELY in the vertex hook (FillExtrusion_VertexModify.hlsl,
//     reused VERBATIM, unchanged from the Lit twin), so this shader still renders flat-coloured 3D blocks
//     that self-occlude — not a flattened fill. See that file's class doc for the sec(φ) derivation.
//   • Fragment is flat albedo = _BaseColor × vColor / alpha = _BaseColor.a × vColor.a × _Opacity — no
//     lighting, no SurfaceData. See FillExtrusion_UnlitForwardPass.hlsl for the exact composite.
//   • CBUFFER (UnityPerMaterial) is IDENTICAL across all passes — SRP Batcher requires this.
//   • ELEVATED-3D CONTRACT: the property DEFAULTS below mirror FillExtrusion.shader's OWN declared
//     defaults verbatim (which are, as that file documents, the same transparent-band numbers Fill.shader
//     uses). The RUNTIME contract — opaque, ZWrite On, LEqual, One/Zero blend — is asserted by
//     FillExtrusionTweaker.ApplyElevatedContract, which MaterialFactory.CreateFillExtrusionMaterial calls
//     on every clone of this shader's base .mat exactly as it does for the Lit twin; see that tweaker.
//     Declaring the full render-state block (below) is what lets it bind (import guard — omitting a
//     render-state property lets URP force queue 2000 on import).
//   • The DepthOnly/DepthNormalsOnly passes reuse FillExtrusion_DepthOnlyPass.hlsl /
//     FillExtrusion_DepthNormalsPass.hlsl VERBATIM (no new file) — buildings must keep writing depth so
//     overlapping buildings, and a single building's own near/far walls, occlude correctly (SSAO too).
//
// Clean-room: this is URP integration, not MapLibre. URP docs/source are fair reference.
// Authored for URP 17.5 / Unity 6000.x.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
Shader "Map/FillExtrusionUnlit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (0.6, 0.6, 0.6, 1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        // ── Blending state (mirrors FillExtrusion.shader's OWN declared defaults; consumed by URP's
        // ValidateMaterial) — see the header comment above for why these read "transparent" even though the
        // runtime contract is elevated-3D opaque. ──
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

        // ── Map fill-extrusion paint properties (mirrors FillExtrusion.shader's own block — same names, so
        // the C# registry's existing FillExtrusion PropertyId/PropertyNames entries and FillExtrusionTweaker
        // bind onto this material unchanged) ──
        _Opacity ("Opacity", Range(0, 1)) = 1.0
        _ExtrusionHeight ("Extrusion Height (m)", Float) = 0.0
        _ExtrusionBase ("Extrusion Base (m)", Float) = 0.0
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
            "UniversalMaterialType" = "Unlit"
            "IgnoreProjector" = "True"
        }
        LOD 100

        // Forward-pass render state — fully parameterized, same contract as FillExtrusion.shader (§ header
        // comment above). Only the forward pass is parameterized; DepthOnly/DepthNormalsOnly keep their own
        // forced `ZWrite On` (they must write depth regardless of the forward pass's setting).
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
        BlendOp [_BlendOp]
        AlphaToMask [_AlphaToMask]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Cull [_Cull]

        // ─────────────────────────────────────────────────────────────────────
        // Pass 1: the unlit forward pass. LightMode = UniversalForward — matches this repo's own unlit map
        // precedent (SymbolTextWorld.shader / FillUnlit.shader), NOT stock URP Unlit.shader's untagged
        // SRPDefaultUnlit.
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

            // ── Material Keywords (mirrors stock Unlit.shader's "Unlit" pass / FillUnlit.shader) ──
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
            #include "FillExtrusion_UnlitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "FillExtrusion_UnlitForwardPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 2: DepthOnly — camera depth prepass / _CameraDepthTexture. Reuses
        // FillExtrusion_DepthOnlyPass.hlsl verbatim (no new file) — the elevated-3D building silhouette must
        // stay in sync with _CameraDepthTexture (SSAO / occlusion correctness).
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

            #include "FillExtrusion_UnlitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "../FillExtrusion_DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 3: DepthNormalsOnly — depth+normals prepass (SSAO). Reuses
        // FillExtrusion_DepthNormalsPass.hlsl verbatim. NORMALMAP/PARALLAXMAP/DETAIL keywords are
        // intentionally NOT declared here (unlike FillExtrusion.shader's DepthNormals pass):
        // FillExtrusion_UnlitInput.hlsl declares no _BumpMap/_DetailAlbedoMap CBUFFER members, so enabling
        // those keywords would try to compile a branch that reads properties this shader never declares.
        // Omitting the pragmas keeps those branches compiled out (mirrors FillUnlit.shader's own DepthNormalsOnly
        // pass reasoning).
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

            #include "FillExtrusion_UnlitInput.hlsl"
            #include "../FillExtrusion_VertexModify.hlsl"
            #include "../FillExtrusion_DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "MapRenderer.Unity.Editor.FillExtrusionUnlitShaderGUI"
}
