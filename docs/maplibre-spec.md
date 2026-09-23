# MapLibre spec — parity target & support matrix

Two halves of one reference: **what MapLibre does that this renderer aims to match** (the durable north-star
surface), and **what is actually implemented** against it (traced through the code). Clean-room throughout —
built from public MapLibre specs/docs only. **NEVER read MapLibre source.**

1. **[Parity target](#1-parity-target-north-star)** — the capability surface to reach.
2. **[Support matrix](#2-support-matrix)** — codebase-traced coverage against that surface.

---

# 1. Parity target (north star)

The stable capability target: parity with MapLibre's *feature set* — Style Spec sources, the layer types,
paint/layout properties, expressions/filters, projections, sprites & glyphs, terrain/hillshade. Not a task
tracker; parity is tracked as *capabilities and ordering*, not exhaustive per-property detail — paint/layout
properties are filled in as their layer stages land. The milestone order in `ARCHITECTURE.md` § "Roadmap (Unity milestones)" (Step 0–5)
refines this into small stages.

## Spec surface to reach parity with (from public docs)

- **Source types:** vector, raster, raster-dem (mapbox/terrarium/custom encodings), geojson, image, video.
- **Layer types (10):** background, fill, line, symbol, circle, heatmap, fill-extrusion, raster, hillshade,
  color-relief.
- **Expression categories:** variable binding (`let`/`var`), types (`literal`/`typeof`/`to-color`…),
  lookup (`get`/`has`/`at`/`in`/`length`), decision (`case`/`match`/`coalesce`/comparisons/`all`/`any`),
  ramps/scales/curves (`step`/`interpolate`/`interpolate-hcl`/`interpolate-lab`), math, color
  (`rgb`/`rgba`/`to-rgba`), feature data (`properties`/`feature-state`/`geometry-type`/`id`), `zoom`,
  `heatmap-density`, string ops.
- **Filters:** legacy filter syntax + expression-based filters.
- **Root style props:** version, sources, layers, sprite, glyphs, light, sky, projection, terrain, transition,
  plus view (center/zoom/bearing/pitch/roll).
- **Projections:** Web Mercator (default), globe.

---

# 2. Support matrix

A codebase-traced snapshot of what the renderer *currently implements* against the MapLibre GL Style Spec, and
to what extent. **Snapshot date: 2026-07-10.** Method: each layer type, expression, source, and root property
was traced parse → consume → rendered-output through the code (not read off the spec). **When the code and a
claim disagree, the code wins — re-trace before trusting a row.**

## Legend

| Mark | Meaning |
|:---:|---|
| ✅ | **Full** — parsed and consumed end-to-end into rendered output, matching spec semantics |
| 🟡 | **Partial** — works, with a caveat (constant/zoom only — no data-driven; approximated; capped; edge cases missing) |
| 🟠 | **Parsed-inert** — parsed from JSON but has **no effect** on output. A trap: looks supported, isn't. Cheap to finish (the plumbing to the value exists). |
| ❌ | **Missing** — not implemented at all |

## At a glance

Only the **vector → MVT → fill / line / symbol-text** path is real end-to-end. Everything else is enum-
recognized (so an unknown `type`/`source` doesn't throw) but falls to an untyped `StyleLayer` and renders to
nothing.

| Layer type | Status | Notes |
|---|:---:|---|
| background | ❌ | recognized; no typed parse, no render (no background quad exists) |
| fill | ✅ | color/opacity data-driven; pattern, sort-key, translate(+anchor), antialias all real (with zoom caveats). Only fill-outline-color remains unimplemented |
| line | ✅ | color/opacity/width data-driven; caps at 4 dash entries; several props constant/zoom-only |
| symbol — text | 🟡 | SDF text, halo, collision, BiDi/RTL, Arabic all work; placement/transform features missing |
| symbol — icon | ❌ | text-only; zero icon support; sprite loader absent |
| circle | ❌ | enum only; no parse, no shader, no render |
| fill-extrusion | ❌ | enum only |
| raster | ❌ | enum only; raster source not fetched/decoded |
| hillshade | ❌ | enum only; raster-dem source not loaded; no terrain |
| heatmap | ❌ | enum only |
| color-relief | ❌ | enum only |

**Engine strengths that punch above the layer coverage:** the expression/filter engine is near-complete
(including `interpolate-lab`/`-hcl`, `let`/`var`, `match`/`case`, both filter dialects), the SDF glyph/text
stack is real, and the **globe projection is fully built** (geometry + interaction + label occlusion) — just
not yet selectable from a style.

---

## Sources

| Source | Status | Evidence / notes |
|---|:---:|---|
| vector (MVT) | ✅ | the one real path: `TileDataSourceFactory` → `MvtDecoder` → `ITileMeshRenderLayer.BuildGraphRequest`. Encoding hardcoded MVT. |
| TileJSON | ✅ | `TileJson.Parse` + `SourceResolver.Resolve` at SetStyle; inline `tiles[]` short-circuits |
| raster | 🟠 | `SourceType.Raster` parsed but never fetched/decoded; a raster source used by a layer would be decoded as MVT and fail |
| raster-dem | ❌ | enum discriminator only; no terrain/hillshade path |
| geojson | ❌ | enum only; no GeoJSON parse/tiler |
| image | ❌ | enum only |
| video | ❌ | enum only |

---

## Layer properties

### background
`background-color`, `background-pattern`, `background-opacity` — **all ❌**. No typed layer, no paint parse, no
background quad in the render path.

### fill
Parse: `Style/Fill/{PaintProperties,LayoutProperties,FillPattern}.cs`. Consume: `StyledFillTileBuilder`, `MaterialFactory.BindFillPaintToApplier`, `FillRenderLayer.SetSprites`, `Shaders/Map/Fill/`.

| Property | Status | Notes |
|---|:---:|---|
| fill-color | ✅ | constant/zoom rides the `_BaseColor` uniform; **data-driven** bakes per-feature into the COLOR stream |
| fill-opacity | 🟡 | constant/zoom → `_Opacity` uniform (re-pushed per frame); **data-driven** → baked into COLOR alpha with `_Opacity` pinned to 1 so it cannot double-apply. Caveat: a **Composite** (zoom+feature) expression takes the bake branch, so it is frozen at the tile's build zoom — `ApplyZoom` pushes uniforms, it does not re-mesh |
| fill-outline-color | 🟠 | parsed + bound to `_FillOutlineColor`, but no outline pass/geometry reads it |
| fill-antialias | ✅ | fill silhouettes carry a one-device-pixel antialiasing band grown OUTWARD from the polygon boundary (`docs/fill-boundary-antialiasing-design.md`); `false` suppresses that band for the layer, in geometry, which is what keeps the property per-layer implementable where MSAA would not be. Consumed in C# at `StyledFillTileBuilder.BuildLayerInput` — the `_FillAntialias` uniform stays bound and read by no pass, deliberately. Resolved at the tile's **build zoom**, so a zoom-dependent value does not re-mesh as the user zooms |
| fill-translate | 🟡 | real screen-pixel offset, measured px→world **per axis** in `Fill_VertexModify` (was inert: applied in object units, so it scaled with the tile transform). **Constant only** — parsed as a literal `[x, y]` array, so a zoom expression silently yields `[0, 0]` |
| fill-translate-anchor | 🟡 | both paths implemented — `map` uses the mesh normal+tangent frame, `viewport` the camera basis. **Constant only** (parsed as a literal string) |
| fill-pattern | ✅ | resolved against the style sprite sheet and sampled (`SAMPLE_TEXTURE2D_GRAD`, `frac` tiling); an unresolvable pattern **clips** rather than falling back to `fill-color`'s opaque-black default |
| fill-sort-key | 🟡 | parsed into `Fill/LayoutProperties`; features stably sorted ascending before meshing, so a higher key rasterizes later and lands on top. Evaluated at the tile's **build zoom**, so a zoom-dependent key does not re-sort as the user zooms (no re-mesh on zoom) |

Remaining fill gap: `fill-outline-color` (it needs real boundary-line geometry rather than plumbing — see
`docs/fill-parity-design.md` § "`fill-outline-color` is line geometry"), plus the 🟡 caveats above. The recurring one
is that anything evaluated per feature at build time is frozen at the tile's build zoom, because `ApplyZoom`
pushes uniforms and never re-meshes — that is a pipeline-wide property, not a fill one.

### line
Parse: `Style/Line/{PaintProperties,LayoutProperties,LineDash}.cs`. Consume: `StyledLineTileBuilder`, `MaterialFactory.BindLinePaintToApplier`, `Shaders/Map/Line/Line_VertexExtrude.hlsl`.

| Property | Status | Notes |
|---|:---:|---|
| line-cap | ✅ | enum → `RibbonJob.Cap` |
| line-join | ✅ | enum → `RibbonJob.Join` |
| line-miter-limit | 🟠 | parsed but not passed to the job; builder hardcodes 2.0 |
| line-round-limit | 🟠 | parsed but never consumed; round joins hardcode 4 segments |
| line-color | ✅ | baked per-feature; **data-driven** ✔ |
| line-opacity | ✅ | constant/zoom uniform + per-vertex bake; **data-driven** ✔ |
| line-width | ✅ | screen-space px resolve (S104) + per-vertex bake; **data-driven** ✔ |
| line-gap-width | 🟡 | hollow-line inner cut; **constant/zoom only** |
| line-offset | 🟡 | perpendicular shift; **constant/zoom only** |
| line-blur | 🟡 | opt-in soft edge; constant/zoom only; explicitly *not* full AA |
| line-translate | ✅ | screen-px offset, measured per-axis per-vertex; constant/zoom only (spec allows no expression) |
| line-translate-anchor | ✅ | both anchors; `"map"` east is exact on Mercator, approximate toward a zoomed-out globe's limb |
| line-dasharray | 🟡 | zoom-capable, re-evaluated per frame; **capped at 4 entries** — extras truncated |
| line-pattern | ❌ | sprite-name only; solid fallback, no sampling |
| line-gradient | ❌ | not parsed |
| line-sort-key | ❌ | not parsed |

### circle
Layer **not implemented** — no `Style/Circle/`, no shader, no render slot. `circle-radius/-color/-blur/-opacity/-translate(-anchor)/-stroke-*/-pitch-scale/-pitch-alignment/-sort-key` — **all ❌**.

### symbol — text
Parse: `Style/Symbol/{Layout,Paint}Properties.cs`. Consume: `SymbolFeatureExtractor` → `StyledSymbolTileBuilder` → `TextQuadLayout` → `SymbolPlacementSystem` → `SymbolText` shader.

| Property | Status | Notes |
|---|:---:|---|
| text-field | ✅ | `{token}` + expression resolve |
| text-font | ✅ | font stack + fallback |
| text-size | ✅ | zoom-eval → screen scale |
| text-color / text-opacity | ✅ | text-color: constant rides the `_TextColor` uniform; every other kind (zoom/data-driven) bakes into the COLOR stream. text-opacity: always baked into the vertex opacity stream |
| text-halo-color / -width / -blur | ✅ | the halo is a second glyph run, so all three evaluate per feature into its vertex streams; a constant `text-halo-color` rides the `_HaloColor` uniform instead, on the same two-carrier split as `text-color` |
| symbol-sort-key | ✅ | greedy placement order |
| text-padding | ✅ | collision-box growth |
| text-allow-overlap / text-ignore-placement | ✅ | collision flags |
| text-anchor / text-justify / text-max-width | ✅ | threaded to `TextQuadLayout` via `TextLayoutOptionsBuilder`; vertical centring places the block's **optical centre** (baseline less half a nominal cap height), not the line-box midpoint — `top`/`bottom` still anchor the box edges, a known, recorded deviation (`docs/road-shields-design.md` § "Text vertical centring"). **Under `symbol-placement: line` the curved arm does NOT consult `text-anchor`, and that is a ruling, not a gap** (pitch-alignment epic W4-D1): the property presupposes one anchor for a whole text block, and a line label is decomposed into one cell per glyph with no such anchor. Cells are centred unconditionally — `center` is the property's own default, so this is the curved path honouring it. The *at-anchors* shield arm (`rotation-alignment` resolving `viewport` under `line`) is a block and DOES honour `text-anchor`. |
| text-offset | 🟡 | threaded; **constant only** — parsed as a raw y-down `float2`, not zoom/data-driven; y-flip reconciled in the builder |
| text-line-height / text-letter-spacing / text-radial-offset | ✅ | parsed (zoom-capable `StyleProperty<float>`) + threaded |
| symbol-placement (`line`/`line-center`) | 🟡 | per-glyph curved along-line text — projected line walked per frame, glyphs rotated to the tangent, keep-upright, per-glyph all-or-nothing collision unified with point labels. `line` repeats at `symbol-spacing`. Tangent screen-space (correct under bearing/tilt); a look-under-rotation eyeball is pending. See `docs/labels-and-symbols-design.md` § "Curved along-line text". Now **expression-capable** (e.g. `step`), but **evaluated at the tile's build zoom only** — never re-evaluated per frame, so a tile built below a step boundary keeps its old placement until rebuilt (D1, docs/road-shields-design.md § "Decisions"). When rotation-alignment resolves `viewport` under `line`, the label is NOT curved — it is upright labels at each along-line anchor (the road-shield look; D4, docs/road-shields-design.md § "Decisions"); line-placement icons now work for map alignment too (`road_one_way_arrow*` render flat in the ground plane) as of the pitch-alignment epic |
| text-transform (upper/lowercase) | 🟡 | case-folds the resolved label before shaping (invariant-culture); **constant only** |
| text-variable-anchor | ❌ | a placement-loop feature (try candidate anchors), not layout wiring |
| text-rotation-alignment | 🟡 | `map` rotates the billboard by the bearing (screen-space); point `auto`→viewport = the upright default; under `line`/`line-center` placement `auto`→**map** (curved, unchanged), only an explicit `viewport` switches to upright-at-anchor (D3/D4, docs/road-shields-design.md § "Decisions"). Sign is the single `SymbolBearing.MapAlignedSign` visual-verify constant |
| text-pitch-alignment | 🟠 | parsed, `auto` resolves against the RESOLVED text-rotation-alignment via `AlignmentResolution.ResolvePitch` — an explicit rotation value wins for pitch too (liberty's `highway-name-{path,minor,major}` set `text-rotation-alignment:map` explicitly, so their unset `text-pitch-alignment` resolves to **map** today, not hypothetically); when rotation is itself `auto`, `ResolvePitch` defers to the RESOLVED rotation, never the raw value — see `icon-pitch-alignment`'s `road_one_way_arrow*` grounding below for that auto→auto case. `map` is CONSUMED as of the pitch-alignment epic: curved labels lay out in world arc length, glyph corners are sized in metres, and the collision box is the screen AABB of the four projected world corners. `text-size` under `map` is a WORLD size — X px measured TOP-DOWN, like `line-width` — so letters and letter spacing foreshorten together and never overlap. Point/icon map-pitch is NOT consumed; the point arm stays viewport |
| icon-pitch-alignment | 🟠 | parsed as of the pitch-alignment epic P1 (`LayoutProperties.IconPitchAlignment`), same `ResolvePitch` chain as the text key; liberty's `road_one_way_arrow`/`road_one_way_arrow_opposite` (`line` placement, both `icon-rotation-alignment` and `icon-pitch-alignment` unset — the genuine auto→auto→placement chain) resolve to **map**. CONSUMED for the along-line curved arm as of the pitch-alignment epic (map-aligned line icons such as `road_one_way_arrow*` now render in the ground plane); the POINT arm is still unconsumed and stays viewport |
| text-keep-upright / text-max-angle | ✅ | curved line labels — keep-upright flips a right-to-left label; text-max-angle drops a label bending more than the allowed adjacent-glyph angle round a corner |
| text-writing-mode | ❌ | vertical CJK |
| text-optional | ❌ | (icon/text co-placement) |
| text-translate (+anchor) | 🟡 | paint-time screen offset at placement (constant); **text-translate-anchor:map** rotates the offset by the bearing — correct at bearing 0, sign is a visual-verify constant |
| symbol-spacing | 🟡 | along-line repeat distance in px (default 250), consumed by `symbol-placement:line`; **constant/zoom only** |
| symbol-z-order / symbol-avoid-edges | ❌ | |
| collision fade-in / opacity animation | ❌ | placement is instantaneous, no fade |

Rendering behaviors that **do** work: SDF glyphs, halo, grid collision, greedy sort-key placement, multi-line
wrap, BiDi/RTL (single-run), Arabic joining/shaping.

### symbol — icon
Sprite loading **exists** (`Core/Text/Sprites/*`, `Unity/Text/SpriteSheet.cs`,
`Rendering/Source/{SpriteSourceFactory,UnityWebRequestSpriteSource}`) — the sheet is fetched once per style
and is what `fill-pattern` resolves against. This section's per-`icon-*`-key status was written before that
landed and has NOT been re-verified key by key; treat the rows below as unaudited rather than current.

### fill-extrusion / raster / hillshade / heatmap / color-relief
**All ❌** beyond the `StyleLayerType` enum name. No typed paint/layout model, no property constants, no
mesh/render path for any of them. (raster-dem/terrain and the raster image path are unbuilt; DEM/terrain
elevation does not exist.)

---

## Expressions & filters

Registry: `Expressions/ExpressionParser.cs:Dispatch` (anything not listed throws "Unknown operator" — so it
defines "missing" precisely). Near-complete; no partial implementations found (present ops cover full
arity/semantics). Minor spec gaps: the optional `collator` arg on comparisons, and array-assert element types
beyond boolean/number/string.

| Category | ✅ Implemented | ❌ Missing |
|---|---|---|
| Types/assert | literal, array, boolean, number, string, object, typeof, to-boolean/color/number/string | collator, format, image, number-format |
| Lookup | at, get, has, in, length | index-of, slice, config, global-state |
| Decision | !, ==, !=, <, <=, >, >=, all, any, case, coalesce, match | within |
| Ramps | interpolate, **interpolate-hcl**, **interpolate-lab**, step | — |
| Variables | let, var | — |
| Math | +, -, *, /, %, ^, abs, ceil, floor, round, sqrt, trig (sin/cos/tan/asin/acos/atan), ln/log10/log2, min, max, e, pi, ln2 | distance |
| Color | rgb, rgba, to-rgba | hsl, hsla, standalone hcl/lab constructors |
| Feature data | properties, geometry-type, id | accumulated, feature-state, line-progress, raster-value, raster-particle-speed |
| Zoom | zoom (step/interpolate input) | heatmap-density |
| String | concat, upcase, downcase | is-supported-script, resolved-locale |
| **Filters** | legacy syntax (==,!=,<,<=,>,>=,in,!in,has,!has,all,any,none) **and** expression filters (auto-detected) | — |

---

## Root style properties, common props, projections

| Item | Status | Notes |
|---|:---:|---|
| version / name / metadata | 🟠 | parsed/retained, never acted on |
| sources / layers | ✅ | fill/line/symbol take effect |
| glyphs | ✅ | PBF range fetch → SDF atlas (real text path) |
| sprite | 🟠 | URL string parsed; **no loader** (blocks icons + patterns) |
| projection (style key) | ❌ | root `projection` never read; globe exists but not style-selectable |
| light / sky / terrain / fog / roll | ❌ | not parsed as typed fields |
| center / zoom / bearing / pitch | ❌ | initial camera comes from bootstrap config, not the style doc |
| transition (root + per-property) | ❌ | no timed interpolation type anywhere |
| **per-layer minzoom / maxzoom** | 🟠 | parsed but **never applied** in render (only source-level min/max gates tiles) |
| layer `filter` | ✅ | legacy + expression dialects |
| layout `visibility: none` | ❌ | not parsed; hidden layers still render |
| layer source / source-layer | ✅ | resolved per source |
| per-layer metadata | 🟠 | retained on raw JSON, ignored |

### Projections
| Projection | Status | Notes |
|---|:---:|---|
| Web Mercator | ✅ | geometry + camera + Burst dispatch + labels |
| Globe (sphere) | 🟡 | geometry + interaction built; **not style-selectable** (ctor-injected only); simple sphere, WGS-84 ellipsoid deferred. **Labels are NOT horizon-occluded** on globe — the SymbolText shader is ZTest Always and TryProjectAnchor only culls behind-camera, so far-hemisphere labels bleed through (pre-existing; text features are Mercator-verified only) |

### Data-driven / zoom-function infrastructure
- `StyleProperty<T>` core, zoom functions (`interpolate`/`step` + legacy `{stops}`) — ✅.
- **Data-driven (feature) evaluation — 🟡:** works only where a mesh builder *bakes* it per-feature
  (fill-color; line-color/opacity/width). The material-uniform path deliberately **throws** for
  feature-dependent props, so every other paint property falls back to constant/zoom.
- Feature-state, transitions, time-based tweening — ❌.

---

## The "parsed-inert" traps (🟠) — cheap wins, in rough effort order

These parse today and just need to be threaded/consumed — the value already reaches the code:

1. **line-miter-limit / line-round-limit** — parsed, dropped before the ribbon job (hardcoded 2.0 / 4 seg).
2. **Per-layer minzoom/maxzoom + layout `visibility`** — parsed/omitted but never gate the render.
3. **fill-outline-color, *-translate-anchor, fill-pattern/line-pattern flags** — bound to uniforms no pass
   reads (some need a whole feature: outline pass, sprite loader). `_FillAntialias` is bound and read by no
   pass too, but it is **not** in this list: the property is implemented in geometry, and the uniform is
   inert by decision rather than pending.

(Symbol layout options — text-anchor/-offset/-justify/-max-width + line-height/letter-spacing/radial-offset —
were the original first trap; **done** via `TextLayoutOptionsBuilder`. text-offset remains constant-only.)
