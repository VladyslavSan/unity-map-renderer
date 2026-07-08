# Map/SymbolText — the affirmed F2 divergence

`Shaders/README.md` states the project convention: every map layer mirrors stock URP Lit verbatim plus a
documented logic delta (see `shaders-mirror-unitylit`). **`Symbol` does not follow that convention** — this
is a deliberate, maintainer-affirmed exception (S20 stage doc §6, fork F2 — "LOCKED: unlit SDF + halo
shader... Affirmed convention divergence from the shaders-mirror-UnityLit rule — text is emissive
UI-like, the documented exception"), not a silent shortcut. This file records why and how.

## Why Symbol diverges

1. **Text is unlit, UI-like content, not lit surface geometry.** An SDF glyph billboard has no meaningful
   normal/albedo/specular — it is a crisp, always-legible label drawn over the map, closer to a UI text
   element than a lit mesh. Lighting it would be both wrong (glyphs would shade differently across the
   screen depending on the sun) and wasted cost (the full URP Lit keyword/pass set Fill/Line carry).
2. **The vertex stage bypasses the normal transform entirely.** Fill/Line vertices are real object/world
   positions run through `TransformObjectToHClip`. A Symbol vertex's `POSITION` is **already a logical
   screen-pixel coordinate** — computed in C# by `LabelScreenProjection.TryProjectAnchor` +
   `BillboardMath.BuildQuad` from the real camera's view-projection matrix, every frame, per label. The
   vertex shader converts px → clip directly (see `Symbol_ForwardPass.hlsl`) and never touches
   `UNITY_MATRIX_M`/`UNITY_MATRIX_VP` — the object-to-world matrix `Graphics.RenderMesh` is given is inert.
   This is what makes labels upright and zoom-independent (billboards): re-deriving a lit surface frame
   for a "mesh" whose vertices aren't really in world space at all would be nonsensical.
3. **One pass, not five.** Fill/Line carry ForwardLit + ShadowCaster + DepthOnly + DepthNormals +
   (Line) GBuffer — capability passes for a lit, potentially-opaque, potentially-shadow-casting surface.
   None of that applies to unlit screen-space text; `Symbol.shader` has exactly one pass.

## Graphics.RenderMesh screen-space technique (S20 plan Risk #1)

Because the vertex shader treats `POSITION.xy` as **logical screen pixels** and manually builds clip
space, two platform-specific correctness points are load-bearing and are exercised by a headless test,
not left to a hopeful guess:

- **Y orientation.** `LabelScreenProjection` computes screen pixels in Unity's own "logical" convention —
  Y-up, origin bottom-left, matching `Camera.WorldToScreenPoint` — the same convention regardless of the
  underlying graphics API. The GPU's actual clip-space Y convention differs by platform (D3D/Metal/Vulkan
  vs OpenGL), so the vertex shader applies the SAME correction URP's own manually-constructed clip
  position helper does (`Core.hlsl`'s full-screen-triangle helper): flip Y when
  `UNITY_UV_STARTS_AT_TOP`. See `Symbol_ForwardPass.hlsl`'s `SymbolPassVertex`.
- **CPU frustum culling must not reject the draw.** `Graphics.RenderMesh`'s `RenderParams.worldBounds` is
  evaluated in the NORMAL sense (as if `POSITION` were a real object-space position transformed by the
  object-to-world matrix) — but our vertex data holds screen-pixel values, not real-world coordinates, so
  a tight/default bounds would make Unity's CPU-side cull reject the draw long before the GPU ever runs
  the vertex shader. `LabelPlacementSystem` passes an enormous `worldBounds` (effectively "never cull") to
  sidestep this.

Both points are verified against the REAL shader + REAL uploaded SDF atlas by
`Assets/MapRenderer.Tests.EditMode/Text/Placement/SymbolAtlasOrientationSnapshotTests.cs` (off-screen
`SnapshotRenderer` render + coverage-shape analysis) — see that file's doc comment for exactly what it
checks and why an upside-down or CPU-culled render would fail it.

## Non-colliding shader property names (the `_Blur` lesson)

`Symbol_Input.hlsl`'s `UnityPerMaterial` CBUFFER keeps two labeled groups, exactly like `Line_LitInput.hlsl`:

- **(A) Style-bound** — `_HaloColor`/`_HaloWidthPx`/`_HaloBlurPx` are the genuine `text-halo-color`/
  `text-halo-width`/`text-halo-blur` spec terms; a future S105 styler binds them BY NAME. `text-color`/
  `text-opacity` are deliberately **not** material properties at all — Slice 1 (and production) bake them
  into the per-vertex `COLOR` stream instead (mirrors how `line-color` already rides vertex color in
  `StyledLineTileBuilder`), so there is nothing for those two spec terms to collide with.
- **(B) Internal** — `_ScreenParamsLogical`, `_SdfEdge`, `_SdfSoftness`, `_SdfDistancePerPixel` are engine
  plumbing with names that do not resemble any `text-*`/`symbol-*` style-spec term, so a future style
  binding can never silently clobber them (the `_Blur`/`line-blur` regression this rule exists to prevent
  — see `docs/lessons-learned.md`).

## SDF threshold + halo

- `_SdfEdge` defaults to **0.75** (not the generic-SDF 0.5) — matches S18's on-disk convention
  (`GlyphSdf`/`SdfDistanceFieldTests.IsoLevel = 191/255 ≈ 0.75`).
- Antialiasing is `fwidth`-derived (the Green/Valve "Improved Alpha-Tested Magnification for Vector
  Textures and Special Effects" technique — a public paper; no MapLibre source read), scaled by the
  tunable `_SdfSoftness`.
- The halo is a second, WIDER threshold under the fill (`haloAlpha` composited under `fillAlpha`).
  **Slice 1 ships a simple LINEAR px→normalized-SDF-distance approximation** (`_SdfDistancePerPixel`,
  default `1/24`) — a defensible first cut, not a reverse-engineered MapLibre constant (clean-room: no
  MapLibre source read). Precise px-calibration of halo width/blur is explicitly a **Slice 3** (paint
  tuning) follow-up; do not treat Slice 1's halo appearance as final.

## Render state

Straight alpha blend, `ZWrite Off`, **`ZTest Always`** (unlit, UI-like text renders on top of the map
unconditionally — MapLibre point labels are not depth-occluded by ground geometry in this project's
current scope), `Cull Off` (billboard triangle winding is incidental — see `BillboardMath`'s doc comment),
`Queue = Transparent+50` (after fills/lines, painter's-algorithm-safe).
