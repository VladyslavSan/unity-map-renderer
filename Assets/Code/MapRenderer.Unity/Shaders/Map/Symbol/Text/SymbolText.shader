// Symbol.shader — Map/Symbol/Text (S20 Slice 1: SDF text billboards)
//
// The affirmed F2 divergence from the "shaders-mirror-UnityLit" convention (Shaders/README.md): every
// OTHER map layer (Fill/Line) mirrors stock URP Lit verbatim plus a documented delta. Symbol does not —
// it is UNLIT, single-pass, and its vertex stage bypasses the normal object/view/projection transform
// entirely (screen-space billboards, built already-projected in C#). See Shaders/Map/Symbol/Text/README.md
// for the full rationale; this is a deliberate, affirmed exception, not a silent shortcut.
//
// Submission (E2): persistent per-slot MeshRenderers, one per symbol layer, owned by SymbolRenderLayer
// (LabelSlotPresenter) — Unity redraws them every camera render on its own, no orchestrator. The demo/
// no-style path (no SymbolRenderLayer list) draws through LabelPlacementSystem's own fallback presenters,
// the same mechanism. Graphics.RenderMesh is gone (retired E2) — never BatchRendererGroup, never a
// per-tile static mesh (T5: this shader/material is never bound by AddTileLayer).
//
// Render state: parameterized as material-UI knobs (S58 pattern — see the "(C) Render state" Properties
// block) so blend/depth/cull are tweakable in the inspector without editing the shader. Defaults reproduce
// the original hardcode: straight alpha blend, ZWrite off, ZTest Always (unlit UI-like text always renders
// on top — MapLibre point labels are not occluded by ground geometry), Cull off (billboard winding is
// irrelevant — see BillboardMath's doc comment). Queue = Overlay (4000) is the DEMO/NO-STYLE FALLBACK
// default only (this shader tag is never overridden for it, so it stays as the "labels on top of
// everything" default a style-less material renders with). A production symbol layer's material gets its
// renderQueue OVERRIDDEN at runtime by RenderLayerSet.Build — renderQueue = LayerDrawOrder.TransparentQueue
// (3000) + the layer's global draw index (D7), interleaved with every other painted layer in declared
// order (E2, design docs/render-layer-unification.md §3.5) — so a fill declared above a symbol layer
// composites over its labels, exactly as MapLibre's painter's algorithm requires.
Shader "Map/Symbol/Text"
{
    Properties
    {
        _MainTex("Atlas (R8 SDF)", 2D) = "white" {}

        // (B) Internal render/engine params — NOT style properties (see Symbol_Input.hlsl's doc comment
        // for the full two-group rule); refreshed every frame by LabelPlacementSystem.
        _ScreenParamsLogical ("Screen Params Logical (px)", Vector) = (1920, 1080, 0, 0)
        _SdfEdge             ("SDF Edge (iso, fontnik = 0.75)", Range(0, 1)) = 0.75
        _SdfSoftness         ("SDF AA Width (screen px, ~1 = crisp)", Range(0.1, 4)) = 1.0
        _SdfPixelRange       ("SDF Range (atlas texels, fontnik ~8)", Float) = 8.0

        // (A) Style-bound — genuine text-halo-* spec terms (production binds by name once S105 lands).
        // Default: a small visible halo so the Slice-1 demo shows halo-under-fill out of the box.
        _HaloColor    ("Halo Color (text-halo-color)", Color) = (1, 1, 1, 1)
        _HaloWidthPx  ("Halo Width (text-halo-width, px)", Float) = 1.0
        _HaloBlurPx   ("Halo Blur (text-halo-blur, px)", Float) = 0.5

        // (C) Render state — material-UI knobs (S58 pattern, mirrors Fill/Line's [_Cull]/[_ZWrite]/[_ZTest]
        // and Blend). Unity's built-in [Enum] MaterialPropertyDrawers render exactly a blend/depth/cull
        // dropdown list — no Lit "Surface Options" (SurfaceType/Workflow/AlphaClip/ReceiveShadows). Defaults
        // reproduce the previously-hardcoded straight-alpha overlay text state so existing materials are
        // import-stable: SrcAlpha(5)/OneMinusSrcAlpha(10), ZWrite Off(0), ZTest Always(8), Cull Off(0).
        // Color (RGB) blend factors — default SrcAlpha(5)/OneMinusSrcAlpha(10) = straight alpha.
        [Enum(UnityEngine.Rendering.BlendMode)]    _SrcBlend      ("Blend Src (RGB)", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)]    _DstBlend      ("Blend Dst (RGB)", Float) = 10
        // Alpha-channel blend factors — separate MRT alpha blend (mirrors Fill/Line). Default
        // One(1)/OneMinusSrcAlpha(10) accumulates dst alpha correctly; inert for the on-screen backbuffer
        // (its alpha is not composited), but exposed so render-to-texture / premultiplied setups can tweak it.
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
            Name "SymbolUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex   SymbolPassVertex
            #pragma fragment SymbolPassFragment

            #include "SymbolText_Input.hlsl"
            #include "SymbolText_ForwardPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
