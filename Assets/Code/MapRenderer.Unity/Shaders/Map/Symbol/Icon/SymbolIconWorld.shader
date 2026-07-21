// SymbolIconWorld.shader — Map/Symbol/IconWorld (Epic A / A1: world-anchored icon billboards).
//
// The world-anchored sibling of the retired screen-space icon shader — the A0 SymbolTextWorld vertex stage
// (world MVP + px offset + near-pin) with the retired shader's fragment (plain sprite sample, no SDF/halo).
// Reuses SymbolIcon_Input.hlsl verbatim (its CBUFFER already carries exactly what this needs — engine-only
// _ScreenParamsLogical + a plain Texture2D _MainTex, no SDF/halo props) — no new Input file. See
// SymbolTextWorld.shader's header for the shared "object-to-world transform is MEANINGFUL here" rationale.
//
// Submission: the world-anchored label renderer (WorldLabelRenderer) sets this material's per-frame
// transform via FloatingOrigin.TileToSceneRebased — never Graphics.RenderMesh, never a static tile mesh.
Shader "Map/Symbol/IconWorld"
{
    Properties
    {
        _MainTex("Sprite Atlas (RGBA)", 2D) = "white" {}

        // (B) Internal render/engine params — NOT style properties; refreshed every frame by
        // WorldLabelRenderer (mirrors SymbolTextWorld's/SymbolIcon's identical _ScreenParamsLogical).
        _ScreenParamsLogical ("Screen Params Logical (px)", Vector) = (1920, 1080, 0, 0)

        // (C) Render state — material-UI knobs (S58 pattern), verbatim from SymbolIcon.
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
            Name "SymbolIconWorldUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex   SymbolIconWorldPassVertex
            #pragma fragment SymbolIconWorldPassFragment

            #include "SymbolIcon_Input.hlsl"
            #include "SymbolIconWorld_ForwardPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
