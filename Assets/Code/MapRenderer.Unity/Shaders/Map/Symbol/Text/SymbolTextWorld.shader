// SymbolTextWorld.shader — Map/Symbol/TextWorld (Epic A / A0: world-anchored SDF text billboards).
//
// The world-anchored sibling of the retired screen-space text shader — copy of its structure verbatim
// (same Properties incl. the Stage-M 2DArray _MainTex, same (B)/(A)/(C) property groups, same render-state
// defaults, same `#pragma require 2darray`), but vertices are TILE-LOCAL WORLD ANCHORS projected by the
// stock object/view/projection transform (TransformObjectToHClip) plus a logical-px screen offset — NOT
// pre-projected screen px. Unlike the retired shader (whose object-to-world transform was INERT — its vertex
// stage bypassed it entirely), the object-to-world transform is MEANINGFUL here: it carries the per-frame
// floating-origin rebase, the same mechanism tile meshes use. The shared F2 (unlit, not a lit surface)
// rationale still applies; see SymbolTextWorld_ForwardPass.hlsl for the vertex-math delta.
//
// Submission: a dedicated per-(tile,DrawIndex,kind) symbol renderer (A1) sets this material's per-frame
// transform via FloatingOrigin.TileToSceneRebased — never Graphics.RenderMesh, never a static tile mesh.
Shader "Map/Symbol/TextWorld"
{
    Properties
    {
        // Stage M: a Texture2DArray — one layer per GlyphAtlas page (see SymbolText_Input.hlsl).
        _MainTex("Atlas (R8 SDF, Texture2DArray)", 2DArray) = "white" {}

        // Internal render/engine params — NOT style properties (see SymbolText_Input.hlsl's doc comment);
        // refreshed every frame by the world-anchored label renderer (A1).
        _ScreenParamsLogical ("Screen Params Logical (px)", Vector) = (1920, 1080, 0, 0)
        _SdfEdge             ("SDF Edge (iso, fontnik = 0.75)", Range(0, 1)) = 0.75
        _SdfAaDevicePx       ("SDF AA Width, outside the edge (RASTER px; 1 = phase-invariant)", Range(0.05, 2)) = 1.0
        _SdfRangeTexels      ("SDF Distance Range (atlas texels; a property of the BAKE, ~8)", Float) = 8.0

        // The two CONSTANT-kind colour tints — multipliers over the vertex COLOR stream, identity white.
        // MUST default white: several tests construct this material raw and never bind either uniform, and
        // every non-Constant expression kind leaves them here (see SymbolText_Input.hlsl).
        _TextColor    ("Text Color (text-color multiplier, identity white)", Color) = (1, 1, 1, 1)
        _HaloColor    ("Halo Color (text-halo-color multiplier, identity white)", Color) = (1, 1, 1, 1)

        // Render state — material-UI knobs (S58 pattern, mirrors Fill/Line's [_Cull]/[_ZWrite]/[_ZTest]
        // and Blend). Defaults match Map/Symbol/Text's: straight alpha, ZWrite Off, ZTest Always (unlit
        // UI-like text always renders on top), Cull Off (billboard corners have no meaningful winding).
        [Enum(UnityEngine.Rendering.BlendMode)]    _SrcBlend      ("Blend Src (RGB)", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)]    _DstBlend      ("Blend Dst (RGB)", Float) = 10
        [Enum(UnityEngine.Rendering.BlendMode)]    _SrcBlendAlpha ("Blend Src (Alpha)", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)]    _DstBlendAlpha ("Blend Dst (Alpha)", Float) = 10
        [Enum(Off, 0, On, 1)]                      _ZWrite   ("Depth Write", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth Test", Float) = 8
        [Enum(UnityEngine.Rendering.CullMode)]     _Cull     ("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"            = "Transparent"
            "Queue"                 = "Overlay"
            "RenderPipeline"        = "UniversalPipeline"
            "IgnoreProjector"       = "True"
        }
        LOD 100

        Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Cull [_Cull]

        Pass
        {
            Name "SymbolWorldUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            // Declares the SPECIFIC capability rather than bumping the whole shader model, so the widest
            // set of platforms compiles the variant (shader-require-over-target convention).
            #pragma require 2darray
            #pragma vertex   SymbolWorldPassVertex
            #pragma fragment SymbolWorldPassFragment

            #include "SymbolText_Input.hlsl"
            #include "SymbolTextWorld_ForwardPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
