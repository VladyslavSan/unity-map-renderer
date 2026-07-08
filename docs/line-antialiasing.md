# Line antialiasing — state, the trilemma, and the way forward

**Status (2026-07-06, commit `0b910c7`):** line edge antialiasing is **removed**. Lines render with a
**hard edge**, the configured width drawn **solid**, thin lines held at ≥ 1 px by a **min-width floor**.
`line-blur` (`_Blur`) is kept as an opt-in soft edge; dashing is unchanged. This document is the SSOT for
*why* there is no edge AA and what a correct version looks like. It supersedes the "opaque-core + feathered
edge" AA model previously described in `docs/lit-rendering-design.md`, the Line shader `README.md`, and
stages `S70` / `S77`.

## 1. What the shader does now

`Line_VertexExtrude.hlsl` extrudes the ribbon to exactly the styled half-width (screen-space pixel width
from S104's projection measurement, `_MetersPerPixel` retired). `LineCoverage`:

- **Outer edge:** hard — the ribbon spans `|side| ≤ 1`, coverage is `1` up to the rasterized triangle
  silhouette. No feather.
- **Gap hole (cased/hollow lines):** hard cut at `|side| < innerFrac`.
- **`_Blur` (MapLibre line-blur):** opt-in inward soft edge, `0` ⇒ no-op. Not antialiasing.
- **Dash:** unchanged (its own along-line `fwidth` feather stays — a different aliasing axis).

The **only** thin-line safeguard is the min-width floor: half-width `≥ 0.5 px`, so a sub-pixel line renders a
stable 1 px hairline instead of dropping out. Removing edge AA does **not** remove this.

## 2. Why edge AA was removed — the trilemma

A shader-space alpha fade (any of inset / straddle / outset) cannot satisfy all three of these at once:

1. the configured width renders **solid** (full intensity to the edge),
2. the total apparent width is **unchanged** (AA doesn't fatten or thin the line),
3. the edge is **antialiased** (partial-alpha pixels around the teeth).

Antialiasing a hard edge *means* placing partial-alpha pixels near it, and those pixels are physically
either **outside** the configured edge or **inside** it — there is no third place:

| approach | solid core | width unchanged | AA'd | what gives |
| --- | --- | --- | --- | --- |
| **outset** (fade past the edge) | ✅ | ❌ | ✅ | fattens by the fade width |
| **inset** (fade inward — the removed S70/S104 model) | ❌ | ✅ | ✅ | edge/core dims, reads thinner |
| **straddle** (50 % at the edge) | ~ (outer ½ px dims) | ✅ | ✅ | balanced, but a thin line goes soft |

Two corollaries fell out of the exploration:

- **`_AaEdgeWidth` was a fade-*width* knob, not a quality dial.** Correct coverage-AA is a ~1 px transition.
  Cranking the width just **blurs** (trades sharpness for smoothness) — it never adds *resolution*. That is
  why widening it "helped but never fixed" the staircase.
- **Extruding geometry + hard-clipping `|side| > 1` does nothing.** With one sample per pixel, a pixel is lit
  iff its centre passes the boundary test; whether that test is the triangle edge or a fragment
  `discard(|side| > 1)`, it is the same test at the same point (linear `side` interpolates exactly at pixel
  centres) → the identical staircase. Only **more samples per pixel** (MSAA/SSAA) or a **coverage gradient**
  (the fade) put intermediate values on a diagonal.

## 3. The real blocker — cased lines are two stacked *transparent* draws

A cased road (e.g. `road_motorway_casing` + `road_motorway`) is **two separate transparent draws**: the
casing (wider, drawn under) and the fill (narrower, drawn over), both `Queue=Transparent`, `ZWrite Off`,
painter-ordered. This is where every shader-fade AA fails, and it is a **compositing** problem, not a
coverage one:

- The fill's AA edge is a skirt that ramps to **transparent**. "Transparent" over the casing means **the
  casing colour bleeds through the fill's edge pixels** → the crisp fill↔casing boundary becomes a muddy
  blend band. The casing's solid rim reads as "blurred."
- This is the general form of S77's "adjacent lines mush": **alpha-fade AA does not compose when transparent
  layers stack.** Every fade skirt blends whatever is beneath it, and a cased line is two layers by
  construction. No fade width — inset, outset, or straddle — escapes it.

**MSAA does not fix this.** MSAA anti-aliases geometry *coverage*, but the fill and casing are still blended
(not replaced), so 4× samples just give a slightly cleaner version of the same mush. The problem was never
coverage; it is the compositing model.

## 4. The way forward — single-pass cased line (not yet built)

The fix changes the **architecture**, not a shader knob. Two viable directions:

1. **Single-pass cased line (preferred).** Fuse the `*_casing` + main layers into **one** mesh / one draw.
   The fragment picks fill-vs-casing colour by **signed distance** from the centreline, antialiases only the
   **outer** silhouette, and treats the internal fill↔casing boundary as a hard colour swap (or a 1 px
   colour-to-colour AA, which is what you actually want). No transparent stacking → nothing to bleed. This is
   how SDF text and MapLibre-style casing work. Cost: the two style layers must be recognised and fused at
   build time.
2. **Opaque line compositing.** Draw lines opaque (`ZWrite On`, opaque queue, depth-offset off the fills) so
   the fill *replaces* the casing instead of blending, with MSAA for the outer edge. Crisp boundary because
   it is a replace. Cost: coplanar-depth handling against the fills.

MSAA is **orthogonal** and complementary once compositing is fixed: with hard geometry edges and no shader
fade, it cleans the outer silhouettes cheaply. It is currently off (`m_MSAA: 1`) in the URP assets.

## 5. What was deliberately kept

- **Min-width floor** (`Line_VertexExtrude.hlsl`) — the sole thin-line safeguard now.
- **`_Blur` (line-blur)** — a real MapLibre paint property, opt-in soft edge, default 0 = hard. Not AA.
- **Dash** coverage and its along-line feather.
- **Front-face cull** (`MapLine.mat` `_Cull: 1`, commit `e747136`) — matches `MapFill`; culls far-side globe
  lines. Unrelated to AA but landed alongside.

## 6. References

- Removal commit: `0b910c7 refactor(shaders): remove line edge AA, keep min-width floor`.
- Parked experiments: a git **stash** holds the `_LINE_AA_OUTSET` outset-AA toggle + `sideScale` groundwork
  (extrude past the edge, scale `side` so `|side| = 1` stays at the styled edge). Useful raw material if the
  single-pass route reuses a distance parametrization. `git stash list`.
- Superseded: `S70` (opaque-core AA buffer — the removed model), `S77` (sub-pixel over-thicken — moot once the
  buffer is gone). `S104` (screen-space width + min-width floor) — the width machinery that stays.
- Naming gotcha that still stands: never name an internal shader prop after a MapLibre style term (the
  `_Blur` = `line-blur` collision) — see `docs/lessons-learned.md` § Shaders & HLSL.
