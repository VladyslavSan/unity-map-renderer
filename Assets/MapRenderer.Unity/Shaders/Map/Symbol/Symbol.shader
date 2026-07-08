// Symbol.shader — Map/SymbolText (S20 Slice 1: SDF text billboards)
//
// The affirmed F2 divergence from the "shaders-mirror-UnityLit" convention (Shaders/README.md): every
// OTHER map layer (Fill/Line) mirrors stock URP Lit verbatim plus a documented delta. Symbol does not —
// it is UNLIT, single-pass, and its vertex stage bypasses the normal object/view/projection transform
// entirely (screen-space billboards, built already-projected in C#). See Shaders/Map/SymbolText/README.md
// for the full rationale; this is a deliberate, affirmed exception, not a silent shortcut.
//
// Submission: Graphics.RenderMesh/RenderParams (LabelPlacementSystem), ONE draw call per frame for every
// visible label — never BatchRendererGroup, never a per-tile static mesh (T5: this shader/material is
// never bound by AddTileLayer).
//
// Render state: straight alpha blend, ZWrite off, ZTest Always (unlit UI-like text always renders on
// top — MapLibre point labels are not occluded by ground geometry), Cull off (billboard winding is
// irrelevant — see BillboardMath's doc comment). Queue = Overlay (4000): the styled map layers get
// renderQueue = LayerDrawOrder.TransparentQueue (3000) + layerIndex at runtime (RenderLayerSet), so a
// 100+-layer style (liberty) reaches well past Transparent+50 (3050) and would overdraw labels — Overlay
// sits above the whole 3000+N band (ceiling 5000) for realistic layer counts. (S105 will place symbol
// layers at their real per-style draw position; this is the demo's "labels on top of everything" default.)
Shader "Map/SymbolText"
{
    Properties
    {
        _MainTex("Atlas (R8 SDF)", 2D) = "white" {}

        // (B) Internal render/engine params — NOT style properties (see Symbol_Input.hlsl's doc comment
        // for the full two-group rule); refreshed every frame by LabelPlacementSystem.
        _ScreenParamsLogical ("Screen Params Logical (px)", Vector) = (1920, 1080, 0, 0)
        _SdfEdge             ("SDF Edge (iso, match S18 = 0.75)", Range(0, 1)) = 0.75
        _SdfSoftness         ("SDF AA Softness", Range(0.1, 4)) = 1.0
        _SdfDistancePerPixel ("Halo Px -> SDF-Distance (Slice 3 calibrates)", Float) = 0.04167

        // (A) Style-bound — genuine text-halo-* spec terms (production binds by name once S105 lands).
        // Default: a small visible halo so the Slice-1 demo shows halo-under-fill out of the box.
        _HaloColor    ("Halo Color (text-halo-color)", Color) = (1, 1, 1, 1)
        _HaloWidthPx  ("Halo Width (text-halo-width, px)", Float) = 1.0
        _HaloBlurPx   ("Halo Blur (text-halo-blur, px)", Float) = 0.5
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

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "SymbolUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex   SymbolPassVertex
            #pragma fragment SymbolPassFragment

            #include "Symbol_Input.hlsl"
            #include "Symbol_ForwardPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
