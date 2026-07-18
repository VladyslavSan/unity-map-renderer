// SymbolIcon.shader — Map/Symbol/Icon (icon-support I5b: sprite billboards)
//
// Cloned from Map/Symbol/Text (Shaders/Map/Symbol/Text/SymbolText.shader) verbatim, with EXACTLY 3 deltas
// (see SymbolIcon_ForwardPass.hlsl / SymbolIcon_Input.hlsl's header comments for (a)/(b); the vertex stage
// is delta (c) — kept byte-identical): (a) the fragment samples the RGBA sprite atlas directly (no SDF
// median/screenPxRange, no halo) — icon-opacity rides vertex color.a exactly like text-opacity does;
// (b) the SDF props (_SdfEdge/_SdfSoftness/_SdfPixelRange) and halo props (_HaloColor/_HaloWidthPx/
// _HaloBlurPx) are dropped — icons have neither a distance field nor a halo; (c) the vertex stage
// (SymbolPassVertex) is untouched — same px→clip screen-space bypass, same y-flip, same reverse-Z pin. See
// SymbolText.shader's header for the full F2-divergence rationale (unlit, screen-space, Graphics.RenderMesh
// bypass) — it applies identically here.
Shader "Map/Symbol/Icon"
{
    Properties
    {
        _MainTex("Sprite Atlas (RGBA)", 2D) = "white" {}

        // (B) Internal render/engine params — NOT style properties; refreshed every frame by
        // LabelPlacementSystem (mirrors SymbolText's _ScreenParamsLogical exactly).
        _ScreenParamsLogical ("Screen Params Logical (px)", Vector) = (1920, 1080, 0, 0)

        // (C) Render state — material-UI knobs (S58 pattern), verbatim from SymbolText.
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
            Name "SymbolIconUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex   SymbolPassVertex
            #pragma fragment SymbolPassFragment

            #include "SymbolIcon_Input.hlsl"
            #include "SymbolIcon_ForwardPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
