// LineUnlit.shader — Map/LineUnlit (unlit twin of Map/Line)
//
// Mirrors stock URP Unlit.shader's pass list (Unlit / DepthOnly / DepthNormalsOnly) the way FillUnlit.shader
// and FillExtrusionUnlit.shader mirror it for their layers — verbatim + minimal marked deltas.
// It has no GBuffer/Meta/MotionVectors pass, and no ShadowCaster (stock URP Unlit has none — an unlit
// material cannot cast a shadow; Line.shader's own ShadowCaster pass is CAPABILITY-ONLY / present-but-inert
// for the transparent queue anyway, so the Lit twin exercises nothing this shader lacks).
//
// Key design points:
//   • TRANSPARENT contract, NOT elevated (unlike FillExtrusionUnlit): _ZWrite=0, painter's-order
//     compositing, coplanar lines. The Properties-block DEFAULTS below mirror Line.shader's OWN declared
//     defaults verbatim for the shared blend-state family (_Surface/_Blend/_SrcBlend/_DstBlend/_ZWrite/
//     _ZTest/_Cull/_BlendOp) — same queue-2000 import guard as the other two twins (declaring the
//     FULL block is what lets URP's ValidateMaterial resolve the queue from _Surface rather than forcing
//     opaque queue 2000 on import). The runtime contract is asserted by LineTweaker.ApplyPainterContract,
//     which MaterialFactory.CreateLineMaterial calls on every clone of this shader's base .mat exactly as
//     it does for the Lit twin.
//   • Line AA is PRESERVED — Line_VertexExtrude.hlsl (reused VERBATIM, unchanged from the Lit twin) owns
//     the extrusion AND the straddle-AA/gap/blur/dash coverage formula (LineCoverage). This shader's
//     forward pass calls LineCoverage(...) exactly as the Lit twin's LinePassFragment does; see
//     Line_UnlitForwardPass.hlsl for the exact composite.
//   • CBUFFER (UnityPerMaterial) is IDENTICAL across all passes — SRP Batcher requires this.
//   • The DepthOnly/DepthNormalsOnly passes reuse Line_DepthOnlyPass.hlsl / Line_DepthNormalsPass.hlsl
//     VERBATIM (no new file) — CAPABILITY-ONLY for the transparent queue, exactly as they are for
//     Line.shader (URP excludes Queue=Transparent materials from the opaque depth/GBuffer prepasses).
//   • Every pass that reaches Line_VertexExtrude/LineCoverage carries the SAME AA/hairline keyword set
//     (_EDGE_ANTIALIASING_OFF, _HAIRLINE_HARD/_HAIRLINE_SOLID_CORE) — Line.shader's own "shared extrusion
//     silhouette" / "shared coverage model" discipline: divergence between passes is impossible by
//     construction because there is exactly one Line_VertexExtrude.hlsl call site per pass, reading the
//     same material keywords.
//   • NORMALMAP/PARALLAXMAP/DETAIL keywords are intentionally NOT declared anywhere in this shader (unlike
//     Line.shader's DepthNormals pass): Line_UnlitInput.hlsl declares no _BumpMap/_DetailAlbedoMap CBUFFER
//     members, so enabling those keywords would try to compile a branch that reads properties this shader
//     never declares. Omitting the pragmas keeps those branches compiled out (mirrors FillUnlit.shader's /
//     FillExtrusionUnlit.shader's own DepthNormalsOnly pass reasoning).
//
// Clean-room: this is URP integration, not MapLibre. URP docs/source are fair reference.
// Authored for URP 17.5 / Unity 6000.x.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
Shader "Map/LineUnlit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (0.3, 0.5, 1.0, 1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        // ── Blending state (mirrors Line.shader's OWN declared defaults for the shared block; consumed by
        // URP's ValidateMaterial — the queue-2000 import guard, see the header comment above) ──
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

        // ── Map line paint properties (mirrors Line.shader's own block — same names, so the C# registry's
        // existing Line PropertyId/PropertyNames entries and LineTweaker bind onto this material
        // unchanged) ──
        _Opacity        ("Opacity (line-opacity)", Range(0, 1)) = 1.0
        _Width          ("Width (line-width, m or px)", Float) = 2.0
        _Blur           ("Line Blur (line-blur, px)", Range(0, 8)) = 0.0
        _GapWidth       ("Gap Width (line-gap-width, px)", Float) = 0.0
        _LineTranslate  ("Line Translate (px xy)", Vector) = (0, 0, 0, 0)
        _LineTranslateAnchor ("Translate Anchor", Float) = 0.0
        _LinePattern    ("Line Pattern (hook)", Float) = 0.0
        _DashArray      ("Dash Array (4 on/off, width units)", Vector) = (0,0,0,0)
        _DashCount      ("Dash Entry Count", Float) = 0.0
        _LineOffset     ("Line Offset (px)", Float) = 0.0

        // (B) Internal render params — NOT style properties (the styler never writes these):
        [Toggle]
        _WidthIsPixels  ("Width In Pixels", Float)    = 0.0
        // [ToggleUI] is UI-only and attaches NO keyword — LineUnlitShaderGUI.ValidateMaterial syncs it in
        // code, mirroring LineShaderGUI. NOT a CBUFFER member.
        [ToggleUI] _EdgeAntialiasing ("Edge Antialiasing", Float) = 1.0
        // [Enum] is a UI-only drawer and attaches NO keyword; LineUnlitShaderGUI.ValidateMaterial syncs it
        // in code, exactly as _EdgeAntialiasing above. NOT a CBUFFER member.
        [Enum(Default, 0, Hard, 1, SolidCore, 2)] _HairlineStrategy ("Hairline Strategy", Float) = 0
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

        // Forward-pass render state — fully parameterized, same contract as Line.shader (§ header comment
        // above). Only the forward pass is parameterized; DepthOnly/DepthNormalsOnly keep their own forced
        // `ZWrite On` (they must write depth regardless of the forward pass's setting).
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
        BlendOp [_BlendOp]
        AlphaToMask [_AlphaToMask]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Cull [_Cull]

        // ─────────────────────────────────────────────────────────────────────
        // Pass 1: the unlit forward pass. LightMode = UniversalForward — matches this repo's own unlit map
        // precedent (SymbolTextWorld.shader / FillUnlit.shader / FillExtrusionUnlit.shader), NOT stock URP
        // Unlit.shader's untagged SRPDefaultUnlit.
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

            #pragma vertex   LineUnlitPassVertex
            #pragma fragment LineUnlitPassFragment

            // ── Material Keywords (mirrors stock Unlit.shader's "Unlit" pass / FillUnlit.shader) ──
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHAMODULATE_ON

            // ── Line AA / hairline keywords (mirrors Line.shader's ForwardLit pass — shared extrusion
            // silhouette + shared coverage model; see the header comment above). PLAIN shader_feature_local
            // (never _fragment) for _EDGE_ANTIALIASING_OFF: the pad is a vertex-stage change, so stripping
            // the keyword from the vertex stage would desync the silhouette. ──
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD

            // ── Unity defined keywords ───────────────────────────────────────
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            // ── GPU Instancing ───────────────────────────────────────────────
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // Include order (define-before-use): input (CBUFFER + DOTS bridge), then Line_VertexExtrude
            // (LineAttributes + Line_VertexExtrude() + LineCoverage() — reused VERBATIM from the Lit twin),
            // then pass body.
            #include "Line_UnlitInput.hlsl"
            #include "../Line_VertexExtrude.hlsl"
            #include "Line_UnlitForwardPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 2: DepthOnly — CAPABILITY-ONLY (present-but-inert for the transparent queue, exactly as for
        // Line.shader — URP only invokes this for opaque-queue materials). Reuses Line_DepthOnlyPass.hlsl
        // verbatim (no new file).
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

            #pragma vertex   LineDepthOnlyVertex
            #pragma fragment LineDepthOnlyFragment

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF // shared extrusion silhouette (see Unlit pass)
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD // shared coverage model (see Unlit pass)

            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Line_UnlitInput.hlsl"
            #include "../Line_VertexExtrude.hlsl"
            #include "../Line_DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pass 3: DepthNormalsOnly — CAPABILITY-ONLY (present-but-inert for the transparent queue). Reuses
        // Line_DepthNormalsPass.hlsl verbatim. See the header comment above for why NORMALMAP/PARALLAXMAP/
        // DETAIL keywords are intentionally NOT declared here.
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

            #pragma vertex   LineDepthNormalsVertex
            #pragma fragment LineDepthNormalsFragment

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local _EDGE_ANTIALIASING_OFF // shared extrusion silhouette (see Unlit pass)
            #pragma shader_feature_local _ _HAIRLINE_SOLID_CORE _HAIRLINE_HARD // shared coverage model (see Unlit pass)

            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Line_UnlitInput.hlsl"
            #include "../Line_VertexExtrude.hlsl"
            #include "../Line_DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "MapRenderer.Unity.Editor.LineUnlitShaderGUI"
}
