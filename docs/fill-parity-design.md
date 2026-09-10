# Fill-layer parity — design

**SSOT for the fill-parity epic.** Closes the gap between the MapLibre Style Spec's `fill` layer and what
this renderer actually paints. Written against verified source (2026-07-28, `main` @ `2905c9d4`), not
against `docs/maplibre-spec.md` — whose fill rows carried stale *reasons* (see §1.3; that doc is now updated).

**Status:** P1–P5 landed on `feat/fill-parity` (`766b5efb`, `55d6418e`, `fab420cb`), each RED-verified and
gated. `fill-pattern`, `fill-sort-key`, data-driven `fill-opacity`, `fill-translate` and
`fill-translate-anchor` are all real. `fill-outline-color` has been moved OUT of this epic (it is polygon-boundary line
geometry, not fill paint — see the stage table). **P7 (`fill-antialias`) is no longer deferred and is no
longer tied to P6** — fill silhouettes are antialiased by an outward one-device-pixel band grown from the
polygon boundary, and `fill-antialias: false` suppresses that band for the layer. The 2026-07-29 reasoning
that deferred it rested on a claim that had already been retracted; §P7 keeps the record and the correction.
Pattern sizing is maintainer-verified in the demo (continuous zoom across the overzoom
range, 2026-07-28). Still unverified visually: `FilterMode.Point` on a tiled ground fill, and P5's
`fill-translate` under tilt/bearing — the snapshot camera is top-down ortho and never asks that question.

Clean-room: semantics from the public MapLibre Style Spec. No MapLibre source read.

---

## 1. Why

### 1.1 The visible defect

Two Liberty fill layers render as **solid black patches**:

| Layer | Paint | Rendered |
|---|---|---|
| `road_area_pattern` | `fill-pattern: pedestrian_polygon` | opaque black plazas |
| `landcover_wetland` | `fill-pattern: wetland_bg_11`, `fill-opacity: 0.8` | 80%-black wetlands |

Both declare `fill-pattern` and **no** `fill-color`. `Style/Fill/PaintProperties` defaults `Color` to the
spec's `rgba(0,0,0,1)`; `StyledFillTileBuilder` bakes that per-feature into the COLOR stream. Nothing in the
pipeline consults `PatternName`. Verified against both the shipped
`Assets/StreamingAssets/Fixtures/liberty.json` and live `tiles.openfreemap.org/styles/liberty` — those two
are the **only** fill layers in Liberty missing `fill-color`, so the black is fully accounted for.

A *failing* colour expression is **not** a second black source: `StyleProperty.TryEvaluate` swallows and
returns false, and `StyledFillTileBuilder` falls back to `Color.white`.

### 1.2 The spec rule being violated

When `fill-pattern` is present, `fill-color` is **not used at all**, and a layer whose pattern image cannot be
resolved is **not painted**. Painting the colour default is wrong twice over.

### 1.3 What the stale doc got wrong

`docs/maplibre-spec.md` says `fill-pattern | ❌ | no sprite sheet to sample`. That blocker no longer exists:
sprite loading landed with the icon epic — `Core/Text/Sprites/{SpriteIndex,SpriteEntry,SpriteAtlasView,
ISpriteSource,SpriteResponse}`, `Unity/Text/SpriteSheet.cs`, `Rendering/Source/{SpriteSourceFactory,
UnityWebRequestSpriteSource}`. The real blocker is **ownership**, not existence (§2.1). That doc is updated
as the last step of this epic.

### 1.4 Verified property inventory

Re-checked against source, not the doc. **This table is the PRE-EPIC state** — the starting point this epic
was scoped against, kept as written so the stages below can be read against it. For current status see
§4's stage table and `docs/maplibre-spec.md`, both updated as each stage landed.

| Property | State | Evidence |
|---|---|---|
| `fill-color` | ✅ per-feature bake, data-driven works | `StyledFillTileBuilder.cs:151` |
| `fill-opacity` | 🟡 `_Opacity` uniform, constant/zoom only | `MaterialFactory.cs:60` |
| `fill-translate` | 🟡 read, but raw px with no px→world scale | `Fill_VertexModify.hlsl:43` |
| `fill-translate-anchor` | 🟠 bound; shader implements only the `map` path | `Fill_VertexModify.hlsl:21` |
| `fill-outline-color` | 🟠 bound to `_FillOutlineColor`; **read by no pass** | `Fill_LitInput.hlsl:68` |
| `fill-antialias` | 🟠 bound to `_FillAntialias`; **read by no pass** | `Fill_LitInput.hlsl:70` |
| `fill-pattern` | ❌ name parsed; `_FillPattern` never even set | `MaterialFactory` sets `_LinePattern` only |
| `fill-sort-key` | ❌ not parsed | absent from `Fill/PropertyNames.cs` |

---

## 2. The two structural findings

### 2.1 The sprite sheet is private to the symbol subsystem

`SymbolLabelSubsystem` owns `_spriteSheet`/`_spriteAtlas` and fetches them **fire-and-forget** from its own
`SetStyle` (`FetchSpriteSheetAsync`). Fill materials are built eagerly and once, synchronously, in
`MapView.SetStyle` → `Layers.Build` → `FillRenderLayer.TryCreate`. So at fill-material-bind time the sheet
provably does not exist yet.

**Rejected — awaiting the sprite fetch in `MapView.SetStyle`'s pre-commit phase.** It reads clean (resolve
everything, then commit synchronously) but makes style commit block on a sprite PNG, and any EditMode test
that `SetStyle`s a sprite-bearing style would start blocking on a live network fetch. Style-load latency is
not worth trading for it.

**Adopted — late resolve through a notification seam, ownership left where it is (for now).** The *data* is
already reachable from the composition root: `SymbolLabelSubsystem` exposes `SpriteAtlas` (line 256) and
`IconTexture` (line 250), and `MapView` holds `Symbols`. So P1 does **not** need to move ownership — it needs
a push from `MapView` to `Layers` when the sheet arrives.

A full hoist would drag the hard parts with it: the fetch is cancelled via the subsystem's `_buildCts`, the
restyle race contract ("a restyle/teardown racing ahead of the fetch never touches the possibly-disposed next
style's state") and the dispose-once paths at lines 349/924 would all have to be rebuilt around a new owner —
and `MapView.Teardown`'s comment (line 524) explicitly encodes today's contract. Worse, P1's teeth (icon
snapshots byte-identical, sheet disposed once) would **not** catch a cancellation-scope regression: that
surfaces as a restyle race, not a pixel diff. Full hoist is the better end state; it lands *after* P2 proves
the feature out, not as a gate in front of it.

Fill pattern layers start *unresolved* (painting nothing) and resolve when the sheet lands. This preserves
today's async posture exactly: icons already pop in late, and patterns popping in late is the same contract.

### 2.2 Pattern fill needs **no mesh changes**

`StyledFillTileBuilder.cs:225` already writes tile-normalized UVs into stream 1:

```csharp
s1[i] = new Vector2((float)(tv.x * extentInv), (float)(tv.y * extentInv));
```

and `Fill_LitForwardPass.hlsl:213` already carries `TEXCOORD0` through to the fragment stage. So pattern
space already exists in the vertex data, and resolving a pattern late is a **pure material-uniform change** —
no tile rebuild, no re-mesh, no restyle. This is what makes §2.1's late-resolve cheap.

**Verified on both mesh paths**, not just the flat one: `WriteGlobeSubdivided` (line 258+) writes
`s1[i] = fv.Tile * extentInv` for every subdivided vertex, so the refined globe geometry carries interpolated
tile UVs too. The claim holds under `UseGlobe` — no globe fence needed.

---

## 3. Uniform contract for `fill-pattern`

Added to `Fill_LitInput.hlsl` (and the DOTS instancing bridge, like every other fill prop):

| Uniform | Meaning |
|---|---|
| `_FillPattern` | `0` = solid (`fill-color` path, today's behaviour), `1` = this is a pattern layer |
| `_PatternMap` | the sprite sheet texture (unbound until the sheet lands) |
| `_PatternRect` | `xy` = sprite top-left in sheet px, `zw` = sprite size in sheet px |
| `_PatternScale` | pattern repeats across one tile edge |

**Unresolved is a degenerate rect, not a magic third state.** `_FillPattern == 1 && _PatternRect.zw == 0`
means "pattern layer, sprite not resolved" ⇒ the fragment **`clip()`s**. No sentinel constant, no tri-state
enum — a zero-area sprite rect is genuinely unresolvable, so the natural reading is the correct one. This is
what makes the black disappear, and it is also the permanent behaviour for a sprite name absent from the
sheet (spec: layer not painted).

`clip()`, not "write alpha 0". Alpha 0 only disappears in a blended queue, and a fill layer's queue is not
obvious from the call site: `FillTweaker.ApplyPainterContract`'s **doc comment claims an "OPAQUE `One/Zero`
blend" while its own constants set `SrcAlpha`/`OneMinusSrcAlpha`** — the comment is stale, and the material is
in fact alpha-blended with `ZWrite Off` (`BaseTweaker.ApplyBaseContract`). Alpha 0 would happen to work today,
but it would silently start painting black again the day that blend changes. `clip()` is correct in either
queue and expresses "not painted" rather than "painted invisibly". (Fixing that stale comment rides along
with P2.)

`_PatternMap` deliberately does **not** reuse stock `_BaseMap`: `_BaseMap` is the base-look slot on the
shared `MapLit` material and the pattern needs its own UV transform. It also cannot collide with a MapLibre
style term, per the `fill-X → _X` styling-bind collision hazard (`_FillPattern` already holds `fill-pattern`).

### 3.1 Sampling

```
patternUv = input.uv * _PatternScale          // tile-normalized UV → repeats
sheetUv   = (_PatternRect.xy + frac(patternUv) * _PatternRect.zw) / sheetSize
```

`frac()` across a packed atlas breaks both bilinear filtering (neighbour-sprite bleed at the seam) and the
mip derivative (a full-texture gradient every repeat). Sample with **`SAMPLE_TEXTURE2D_GRAD`**, passing
derivatives taken from the *unwrapped* `patternUv` scaled into sheet space — the standard fix for both, and
the same reason the line shader resolves width in screen space rather than trusting interpolation.

### 3.2 Pattern scale

`_PatternScale` = tile edge in css px ÷ sprite logical size, where sprite logical size is
`_PatternRect.z / SpriteEntry.PixelRatio` (the `@2x` sheet divisor). Constant per (layer, sprite) — the
pattern is anchored in **tile space**, which matches MapLibre at integer zoom and drifts only within a
fractional zoom step. Deferred refinement (§6): a per-frame fractional-zoom factor, the same shape as
`ApplyLineDashArray`'s per-frame re-evaluation.

---

## 4. Stages

Each stage is one revertible commit on `feat/fill-parity`, gated on `./Tools/run-tests.sh` green.

| # | Stage | Scope |
|---|---|---|
| **P1** | Sprite-ready notification seam | `MapView` pushes the arriving sheet to `Layers`; ownership, fetch and cancellation scope stay in `SymbolLabelSubsystem` (§2.1). **Behaviour-preserving** — no visual change, no new fetch. |
| **P2** | `fill-pattern` render | §3's uniform contract + `MaterialFactory` bind + late resolve + shader sampling. **Kills the black regions.** |
| **P3** | `fill-sort-key` | Parse (`Fill/PropertyNames` + a new `Fill/LayoutProperties`) + order features within a layer before meshing. Works because these fills run `ZWrite Off` / `LEqual`, so coincident coplanar triangles never depth-reject and the last rasterized wins. **Stable** sort (declared index as tiebreak — `Array.Sort` is an introsort); absent key ⇒ no sort at all, keeping existing meshes byte-identical. |
| **P4** | `fill-opacity` data-driven | Multiply the evaluated per-feature opacity into the baked COLOR stream alpha; colour is already baked per-feature, so this is the same loop. **Must set `_Opacity = 1` on the material when baking** — the fragment does `alpha *= vColor.a * _Opacity`, so leaving the uniform bound double-applies. Exactly the precedent `BindLinePaintToApplier` sets for data-driven `Width` (`MaterialFactory.cs:168-169`). |
| **P5** | `fill-translate` correctness | Screen-pixel offset measured **per axis** in the vertex shader (a single scalar skews under tilt, where the north axis foreshortens and east barely does) + the `viewport` anchor branch the shader lacked entirely. |
| ~~P6~~ | `fill-outline-color` | **MOVED OUT of this epic** (maintainer call). It is not a paint-plumbing property at all: it means generating real LINE geometry along the polygon boundary — the thing style authors have long faked with a second `line` layer drawn after the `fill`. That makes it a geometry feature sharing the line tessellator, not a fill-paint one, so it gets its own epic rather than a stage here. |
| **P7** | `fill-antialias` | ✅ **BUILT** as the outward boundary band (`docs/fill-boundary-antialiasing-design.md`), independently of P6. The 2026-07-29 deferral below is kept as a record, with its false premise corrected in place. |

### P7 deferred, 2026-07-29 — ~~`fill-antialias` is not separable from `fill-outline-color`~~ (SUPERSEDED)

> **CORRECTED 2026-09-09 — the deferral below was wrong, and this is the record of why.** `fill-antialias`
> shipped as an outward one-device-pixel band grown from the polygon boundary, with no outline geometry and
> no MSAA. Its load-bearing premise — the `meshing-design.md` quote in the second bullet — **had already
> been retracted in that same file, seven lines below the sentence quoted**, before this section was
> written. The bullets are kept verbatim with their corrections attached, because a stale citation keeping a
> feature unbuilt for two months is the lesson; the mechanism is
> `docs/fill-boundary-antialiasing-design.md`.

Investigated 2026-07-29 and deferred rather than built, because the two properties are **one mechanism**:

- **The spec couples them.** `fill-outline-color` is drawn *only when* `fill-antialias` is true. In MapLibre
  both are served by the same outline draw along the polygon boundary — outline-color merely recolours it.
  So the MapLibre-faithful implementation of P7 *is* the boundary-line-geometry work that P6 was moved out
  for. Building P7 "first" would build most of P6 under a different name.
  **The spec coupling is real, but ONE-DIRECTIONAL, and the inference does not follow.** The outline
  requires antialiasing to be on; antialiasing does not require the outline. What the spec couples is the
  outline's *visibility*, not the mechanism that antialiases a silhouette — and a silhouette can be
  antialiased with no outline draw at all, which is what shipped.
- ~~**The obvious shortcut is already ruled out by our own SSOT.** `docs/meshing-design.md` §2 — written when
  line edge AA was **removed** — argues a shader alpha-fade cannot work: *"alpha-fade AA does not compose
  when transparent layers stack"*. That now binds harder for fills than when it was written, because
  `4c41545d` (this epic) made `MapFill.mat` transparent (queue 3000, `ZWrite Off`). A fade skirt would let
  the layer beneath bleed through every shared edge between adjacent polygons.~~
  **FALSE PREMISE.** The quoted sentence is `meshing-design.md`'s, and its *retraction* sits seven lines
  below it in the same file: the generalisation was too strong — it is true of an **inset** fade, not of one
  placed outside the boundary. `docs/lessons-learned.md` carries the same correction and notes it "is why AA
  stayed removed longer than it needed to". What replaces the argument: the shipped band is not a fade
  *inside* the styled region at all. It grows **outward**, so every pixel the layer is meant to paint keeps
  coverage exactly 1, and `dst = src·a + dst·(1−a)` cannot show anything through at `a == 1`. The bleed this
  bullet feared is the defect that rejected three inset placements — it is the reason for the mechanism, not
  an objection to it.
- ~~**The remaining candidate is MSAA**, which §2 itself endorses as the right complement once no shader fade
  is in play, and fill edges are already hard. It needs no new geometry. Its costs: it is a global
  render-target setting, so per-layer `fill-antialias: false` becomes *unimplementable* rather than merely
  unimplemented; it costs bandwidth (mobile); and it is currently off (`RPAsset.asset: m_MSAA: 1`).~~
  **Downstream of the withdrawn claim** — MSAA was "remaining" only because the fade was believed dead. It
  is not the answer, and the one true observation in this bullet is the reason: a render-target setting
  cannot be switched off for a single layer, so an MSAA answer would have made `fill-antialias: false`
  permanently unimplementable. The band makes it implementable, and implements it.

**The MSAA question is moot, and the cheap check it prescribed could never have answered it.** The recipe
was: set `m_MSAA: 4` in the RPAsset, render any existing fill snapshot, count partial-alpha pixels along a
diagonal boundary, and read zero intermediates as "MSAA is dead". It cannot fire. URP takes the MSAA sample
count from `camera.targetTexture.antiAliasing` whenever a target texture is set, in preference to the asset
(`UniversalRenderPipelineCore.cs:1553-1567`), and the visual suite's single `camera.Render()`
(`Visual/SnapshotRenderer.cs:89`) always assigns one, built with `antiAliasing = 1`. The RPAsset's `m_MSAA`
never reaches that render, so the recipe would have produced an unchanged frame and read it as a dead
approach. Anyone who does want to measure MSAA must set `antiAliasing` on the **render texture** the
snapshot renderer builds. *(Recipe corrected 2026-09-09; it was never run.)*

~~**De-risked for whoever picks this up:** no visual test in the repo hashes pixels exactly — they are all
tolerance/classification based (`SnapshotCoverage.Tolerance`, `IsBackground`, coverage ratios, centroid
rows). So enabling MSAA globally should not force a mass snapshot re-bake, which was the main feared cost.~~
**INACCURATE.** `Visual/GoldenImage.cs` compares **per pixel** against a committed PNG, with
`MaxChannelDelta = 4` and `MaxDifferingFraction = 0.002` — used by `GeoJsonFillVisualProofTests` and
`GeoJsonPointSymbolFixtureTests`. A global render-target change would have to be checked against those two,
not waved past. *(Corrected 2026-09-09.)*

Prior art worth reading first: the unmerged local stash `line AA experiments — outset/straddle/pure-outset
+ _LINE_AA_OUTSET toggle`.

**Consequence for `_FillAntialias`, as settled:** the parsed property has a real consumer and the **uniform
stays inert, permanently**. `StyledFillTileBuilder.BuildLayerInput` resolves `fill-antialias` at build zoom
and suppresses the layer's band geometry when it is false, which is what keeps the property per-layer
implementable at all. The uniform stays declared, instanced (`MapInstanceData.cs:97`), bound
(`MaterialFactory.cs:79`) and read by no pass — deliberately, and not pending: giving it a shader reader
would fake a consumer for a property that is answered in geometry. Its declaration comment
(`Fill_LitInput.hlsl`) said "Used by future MSAA/AA variant"; that is corrected to say what it is.

P1–P5 are the parity work proper, plus the alpha-compositing fix below. Both P6 and P7 were flagged to the
maintainer as separately epic-sized before this doc was written; P6 has since been moved out entirely and P7
remains unscoped.

## 5. Teeth

- **P1** — a behaviour-preserving refactor: the existing icon snapshot tests (`SymbolIconRenderSnapshotTests`)
  must stay byte-identical, and the sheet must still be disposed exactly once per restyle.
- **P2** — RED-verified **synthetically**, not against Liberty: no committed MVT fixture is known to carry a
  feature selecting into a pattern-declaring layer, so the tooth is a two-layer style over an existing
  `water-*.pbf` fixture where one layer declares `fill-pattern` and no `fill-color`. RED = black pixels on
  today's tree; GREEN = none. Plus a resolve test: an unresolved pattern layer paints nothing, and binding a
  sheet afterwards makes it sample — **with no tile rebuild** (§2.2's claim, made falsifiable). The committed
  `Assets/Fixtures/sprites/` sheet + `FixtureSpriteSource` + the `SpriteSourceFactoryOverride` seam supply the
  sprite side with no network.
- **P2 gate hazard** — this stage adds fields to `UnityPerMaterial` and the DOTS instanced-prop block, so the
  first batch run may execute stale shader variants. Re-run the gate before treating a P2 snapshot failure as
  a real defect.

### P2 RED-verify — observed, not modelled

The snapshot tests reference C# (`PropertyId.PatternRect`, `TexturePropertyId.PatternMap`) that does not
exist before this stage, so they cannot simply be run against the pre-P2 tree — that would be a compile
error, not a RED. The injectable form keeps every line of C# and disables **only** the pattern block in
`Fill_LitForwardPass.hlsl`. Result with the block removed:

| Test | Result | Observed |
|---|---|---|
| `UnresolvedPattern_PaintsNothing` | **RED** | 80.25 % background / **19.75 % filled** — the black default painting, i.e. the original defect reproduced |
| `ResolvedPattern_SamplesTheSheetInsteadOfFillColor` | **RED** | mean G = 46.0 vs mean R = 46.0 — no green at all, the sprite ignored |
| `ResolvingTheSheetLate_DoesNotRebuildTheMesh` | green | correct and worth noting: it asserts CPU-side mesh identity, so it does not depend on the shader and is *not* evidence for the pattern path |

Restoring the block returns all three to green. The `FilledFraction > 0.02` precondition in the first test
is what makes its RED meaningful — the failure is "geometry rendered and it was black", not "nothing
rendered".
- **P3** — coincident polygons whose declared order is the REVERSE of their sort keys must rasterize in sort
  order. Asserted on the **index buffer**, not on `mesh.colors`: the claim is about draw order, so the oracle
  has to be the buffer that determines it. (Vertex order and index order do agree today — `FillMeshPipeline`
  concatenates each polygon's indices in ascending polygon order and polygon order follows feature order —
  but a vertex-order oracle would keep passing if that ever changed.)
- **P4** — a data-driven `fill-opacity` produces per-feature distinct alphas in the COLOR stream (today they
  are uniformly the default), **and** `_Opacity` is pinned to 1 on the material so the fragment's
  `alpha *= vColor.a * _Opacity` cannot double-apply. The second half lives in `ZoomStyleApplierTests`: the
  mesh-side test alone would also pass on a build that dropped `fill-opacity` entirely.

### P3/P4 RED-verify — observed

Two defects injected separately into `StyledFillTileBuilder` (sort short-circuited; opacity bake removed):

| Test | Result | Observed |
|---|---|---|
| `SortKey_HigherKeyIsEmittedLast` | **RED** | red channel 0.0 where 'bottom' was expected — declared order survived |
| `DataDrivenOpacity_BakesDistinctPerFeatureAlpha` | **RED** | alphas `{1.0}` instead of `{0.25, 0.75}` |
| `DataDrivenOpacity_MultipliesWithFillColorAlpha` | **RED** | 0.4 instead of 0.2 |
| `SortKey_EqualKeys_PreserveDeclaredOrder` | green | expected — it guards against an UNSTABLE sort, not against no sort |
| `SortKey_Absent_LeavesDeclaredOrderAndMeshUntouched` | green | expected — it asserts the no-sort path, which the defect *is* |
| `ConstantOpacity_IsNotBaked` | green | expected — non-discriminating on its own; the `ZoomStyleApplierTests` uniform teeth are what cover this case |

### P5 RED-verify — observed, and sharper than expected

`MapVertexModify` reverted to its pre-P5 body (world units, no px conversion, no anchor branch; all C#
intact so it compiles). All three teeth went RED — but via **zero movement**, not a wrong direction:

| Test | Result | Observed |
|---|---|---|
| `Translate_DisplacesTheSameScreenDistance_AtDifferentZooms` | **RED** | 0.005 px shift for a 40 px translate |
| `Anchor_MapRotatesWithBearing_ViewportDoesNot` | **RED** | viewport shift `(0.00, 0.00)` |
| `PositiveY_MovesGeometryDownTheScreen` | **RED** | row shift 0.0 (fails "must decrease") |

That is a stronger finding than "wrong magnitude". The old body added `_FillTranslate` to **positionOS**, and
the fixture's object→world scale is ~1e-5 (raw Mercator-metre bounds fitted into 100 world units), so a
40-unit offset became ~4e-4 world units — invisible. `fill-translate` was not merely mis-scaled, it was
inert at this scale and would have been wildly wrong at another. Applying the offset in WORLD space and
converting back through `GetWorldToObjectMatrix()` is what makes it scale-independent.

Note the sign tooth's RED is by absence, so it discriminates "correct" from "absent or inverted" rather
than pinning direction against a wrong-direction build. Its GREEN is what pins the direction.

### A raster-orientation trap this stage walked into

`SnapshotRenderer.RawPixels` documented itself as top-left origin. It is **bottom-left** (Unity's native
`ReadPixels`/`GetRawTextureData` convention; this class does not flip, unlike `SpriteSheet`, which
deliberately does). The first sign test was built on the doc and reported a correct southward move as a
shader bug. Doc corrected in place. Nothing caught this sooner because every other consumer measures counts,
coverage fractions or mean luminance — all orientation-independent; this is the first test asserting a
vertical *direction*.

The same test was also a near-no-op for an unrelated reason: `FillSceneHelper` fits the fixture into 100
world units, and the camera covered only 70, so the fill was clipped at the frame edge and its visible
centroid could not track a rigid translation (a 40 px translate measured as 4.8 px). `TryCentroid` now
reports whether the fill touches any border and every caller asserts it does not — a framing mistake fails
loudly instead of quietly weakening the assertion.

## 6. Found while building P2

Four things the plan did not anticipate. All are fixed in P2; recorded because each was invisible to the
teeth that were planned for it.

1. **The sprite fetch was gated behind "style has symbol layers."**
   `SymbolLabelSubsystem.SetStyle` returned early — `if (_layersBySource.Count == 0) return;` — *before*
   kicking off `FetchSpriteSheetAsync`. Correct while the sheet was an icons-only resource; a silent
   feature-killer the moment `fill-pattern` resolves against the same sheet. A style with pattern fills and
   no symbol layers would never fetch a sheet, so every pattern layer would clip forever with no error.
   Liberty has symbol layers, so neither the demo nor any snapshot test would have caught it. The fetch never
   depended on `_layersBySource`, so it is simply hoisted above the return; `_buildCts` is already this
   style's fresh scope by that point, so the cancellation contract is untouched. Pinned by
   `SpriteFetchGatingTests`.

2. **The BRG backend packs fill uniforms explicitly.** `_PatternRect`/`_PatternScale` are declared as
   DOTS-instanced props, so on the BRG path an unlisted property reads instance metadata nothing wrote
   rather than the material — a zero-area rect, clipping every pattern fill, while the MeshRenderer-based
   snapshot tests stayed green. Both are now fields on `MapInstanceData`, alongside the other fill uniforms.

3. **Background clones the fill base material.** `_FillPattern` inherited as 1 from a base `.mat` would clip
   the entire background quad. `BindBackgroundPaintToApplier` already zeroed `_FillTranslate` defensively
   for exactly this reason; it now zeroes the pattern pair too.

4. **`ShaderProperties/Fill/PropertyNames` is the CBUFFER set, not "fill shader properties."** The
   structural parity guards (`SharedUnionFillNames_EqualsFillCbuffer`) enforce registry ↔ CBUFFER equality,
   and a texture is not a CBUFFER member. `_PatternMap` therefore lives in a sibling `TexturePropertyId`
   rather than weakening that guard — the same posture as `WorldLabelRenderer.AtlasPropId`.

## 6b. Pattern sizing — and why the pattern coordinate is world-space

Three reports, one root cause. The first cut anchored the pattern in TILE space: repeats across a tile,
corrected by `2^frac(displayZoom)` for the tile's on-screen magnification. Every problem below is that
correction's hidden assumption — **that a tile's own zoom is `floor(displayZoom)`** — failing:

| Reported | Cause |
|---|---|
| pattern pulses through each zoom step | the correction was missing entirely at first |
| stepping/snapping while zooming continuously | rounding repeats to whole numbers (needed so tiles met at their edges) quantised the scale — ~16 snaps per zoom level |
| **"too zoomed in" past z14** | the source's maxzoom is 14; from display 14→18 the SAME tiles are stretched, so the tile's own zoom is 14 throughout and the correction was off by up to **16×** |
| seams between neighbouring tiles | `ScreenSpaceLod` (the default) makes mixed-zoom cover routine, so neighbours have different tile zooms and different pattern scales |

A per-layer material uniform cannot see a tile's zoom, so no correction computed from the display zoom can
be right. **The fix is to remove the tile from the calculation.**

`StyledFillTileBuilder` now writes stream 1 as the vertex's offset from the tile origin in **world units**
(Web-Mercator metres) instead of a 0..1 tile fraction — it knows the tile's real `Z`, so it can bake real
distances. `FillPattern.RepeatsPerWorldUnit` then returns `1 / period`, a function of the DISPLAY zoom alone:

| Mode | Period (world distance per repetition) |
|---|---|
| `ScreenRelative` (default) | `spriteLogicalPixels × WebMercator.GroundResolution(displayZoom)` — the Style Spec's meaning: the sprite occupies its authored pixel size on screen |
| `WorldAbsolute` | the configured period, independent of zoom entirely |

Overzoom, mixed-zoom cover and the stepping all disappear together, because none of them can influence a
quantity that no longer mentions a tile. Rounding is gone with them, so scaling is continuous.

### What this still does not fix

Each tile's pattern starts at **that tile's** origin, so unless the period divides the tile's span there is a
phase step at tile edges. That is a spatial artefact (a static screenshot shows it; zooming does not make it
worse). Removing it needs each tile's GLOBAL origin in the shader — which is per-tile data, and at world
scale needs split-precision floats, the `u_pixel_coord_upper/lower` trick MapLibre uses.

**Left unbuilt on evidence.** The maintainer verified the reworked sizing in the demo (continuous zoom
through the overzoom range) and reported it correct without raising edge phase. So the artefact exists but is
not visible enough on real sprites to justify the split-precision machinery — revisit only if a future
sprite makes it obvious.

### Mode selection

From the style, via ONE key: `x-fill-pattern-metres`, the tiling PERIOD — the world distance one full
repetition spans (at a period of 1, the pattern coordinate advances by exactly one repetition per world
unit). Parsed into `Fill.PaintProperties` beside `PatternName`, so the style stays the SSOT and
`FillRenderLayer` holds no settable knob.

One key rather than a mode plus a size: world sizing is meaningless without a period and a period is
meaningless under screen sizing, so folding them together makes the invalid combination unrepresentable.
Absent ⇒ `ScreenRelative`, which is also the enum's zero value — a stock MapLibre style has no way to ask
for anything else. The `x-` prefix marks it a non-spec extension that can never collide with a key the spec
adds later; MapLibre ignores unknown keys, so such a style still renders there. An unusable value degrades
to `ScreenRelative` rather than throwing: an unreadable EXTENSION must never cost you the layer.

"World units" are Web-Mercator metres — the units the mesh, tile origins and camera already use. They equal
true ground metres at the equator and diverge by 1/cos(latitude) away from it; expressing the period in
anything else would put the pattern in different units from the geometry it sits on.

## 7. Deferred

- Fractional-zoom pattern rescale (§3.2) — constant tile-space anchoring first.
- `SpriteSheet` uses `FilterMode.Point`, chosen for icons. Correct for crisp icons, aliased for a tiled
  ground pattern; revisit once P2 is eyeballed rather than pre-emptively.
- **The pattern clip is applied in the forward and GBuffer passes only.** `Fill_DepthOnlyPass` /
  `Fill_DepthNormalsPass` / `Fill_ShadowCasterPass` carry `uv` only under `_ALPHATEST_ON`, which fill
  materials do not enable, so clipping there would mean adding an unconditional varying to all three. Fill
  materials run `ZWrite Off` under a painter's-algorithm draw order (`BaseTweaker.ApplyBaseContract`), so an
  unresolved pattern layer contributing to a depth-only or shadow pass is not a path this renderer takes
  today. Revisit if fills ever gain depth write or start feeding SSAO.
- `fill-extrusion` — a different layer type, not this epic.
