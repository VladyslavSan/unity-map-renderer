# Shaders

The map renderer's HLSL shader tree. Layout, include rules, and naming conventions live here so a
future layer (or a file move) has a rule to follow rather than a precedent to reverse-engineer.

## Layout

```
Shaders/
  Common/        shared URP-lit pass framework (the lit passes + CBUFFER/DOTS bridge)
  Map/
    <Layer>/     per-geometry shader + its layer-specific input (e.g. Map/Fill, Map/Line)
```

- `Map/<Layer>/` holds one geometry kind's `.shader` plus its `<Layer>_Input.hlsl` (and any
  layer-only pass, as Line keeps its own forward pass).
- `Common/` (a sibling of `Map/`) holds the shared lit framework every *lit* layer reuses:
  `MapLitInput.hlsl`, `MapLitCore.hlsl`, and the five lit passes (`MapLitForwardPass`,
  `MapLitGBufferPass`, `MapShadowCasterPass`, `MapDepthOnlyPass`, `MapDepthNormalsPass`). Today only
  Fill consumes it; future lit layers will too.

## Include order (lit layers)

Every map-lit pass `#include`s, in this order (see `MapLitCore.hlsl`'s header for the rationale):

1. `MapLitInput.hlsl` — **first**. The `UnityPerMaterial` CBUFFER + the DOTS-instancing bridge.
2. `MapLitCore.hlsl` — the vertex hook declaration + the antialiasing helper.
3. `<Layer>_Input.hlsl` — the layer's `MapVertexModify` body (the per-layer vertex seam).
4. the pass body — the vertex/fragment entry points.

### `MapVertexModify` hook

Each layer supplies its own `MapVertexModify` implementation in `<Layer>_Input.hlsl`
(e.g. `Map/Fill/Fill_Input.hlsl`). This is the per-layer vertex-customization seam the shared passes
call into; the `Common/` passes never hard-code layer geometry.

## Lines are separate on purpose

Line passes `#include` `Map/Line/MapLineInput.hlsl`, **not** `Common/MapLitInput.hlsl`. The two
declare clashing TEXCOORD attribute sets (documented in `MapLineInput.hlsl`), so they cannot share an
input header — never merge them. Line therefore keeps its own `MapLineForwardPass.hlsl` under
`Map/Line/`.

## Naming

- Shaders declare `Shader "Map/<Layer>"` (e.g. `Map/Fill`, `Map/Line`).
- Materials bind their shader **by GUID** (the `.mat`'s `m_Shader` guid → the `.shader.meta` guid), so
  renaming the `Shader "…"` string does not break a material. Only `Shader.Find("Map/<Layer>")` (used
  in tests) depends on the declared name.
- The custom inspector is `MapLitShaderGUI` (bound via `CustomEditor` by C# class name, not by shader
  name — unaffected by shader renames).

## DOTS / BRG instancing

Lit shaders carry `#pragma multi_compile_instancing` and `UNITY_DOTS_INSTANCED_PROP` property sets so
they render under the BatchRendererGroup backend (S49). The CBUFFER, the `UNITY_DOTS_INSTANCING`
block, and any BRG SoA packing must stay byte-aligned — see `docs/lessons-learned.md`.

## Include-path convention

Cross-folder includes use a **relative path from the including file**: a `Map/<Layer>/` shader reaches
the shared passes via `../../Common/<Name>.hlsl`. Intra-folder includes stay bare
(`#include "Fill_Input.hlsl"`). Keep this relative form for every future move so include rewrites stay
mechanical.
