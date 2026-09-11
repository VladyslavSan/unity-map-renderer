# Device-pixel ratio — one logical-pixel convention for the whole map

**Status:** **Stages 1 + 2 LANDED (S107); Stage 3 LANDED (S108); Stage 4a LANDED (S109); Stage 4b is a
written-up decision brief AWAITING THE MAINTAINER.** SSOT for the DPR **epic** — the decisions, history and
rationale. The DPR **contract** (the standing requirements a conforming implementation must hold) is the
normative [`specs/device-pixel-ratio.md`](../specs/device-pixel-ratio.md); this doc is its *why*.
**Symptom that opened it:** symbols look "barely visible"; sliding `DevicePixelRatio` live changes the zoom
level and the label sizes, but **road widths do not move**.

---

## 1. What DPR is supposed to mean

A style's `px` values are **logical** (CSS) pixels. The device-pixel ratio is the number of physical pixels
per logical pixel, so a `line-width: 2` road is 2 logical px — `2 × dpr` physical px — on every panel, and a
`text-size: 16` label is 16 logical px on every panel. **One convention, applied once, inherited everywhere.**

When this doc was opened it was not one convention: a division performed at five call sites, with one whole
family of style properties never meeting it at all. Stage 1 (S107) added the missing conversion — one named
function, one seam; Stage 3 (S108) collapsed the five divisions onto its mirror, `DeviceToLogicalPx`.

### 1.1 The decision procedure — layout vs sampling

**This is the rule to apply when adding any new px-valued quantity.** Everything in §2.4's table is an
instance of it, and getting it wrong is how `line-width` drifted.

| the quantity answers | unit | examples |
|---|---|---|
| *"how big should this **look**?"* | **logical** | `text-size`, `line-width`, paddings, offsets, collision boxes, translate |
| *"where is the **sample grid**?"* | **device** | the AA straddle pad, the hairline floor, SDF sharpness, `fwidth`-derived ramps |

Design intent is logical; rasterisation is device. A quantity that would mean the same thing on a printed map
is logical. A quantity that only exists because the screen is made of discrete samples is device.

### 1.2 Why placement runs in LOGICAL px — the part that is not obvious

Label collision, staging and the collision grid all work in logical px (`SymbolProjectionJob.ViewportLogicalPx`
→ `LabelStagingMath`). Two questions this raises, because it looks like the "wrong" space given the screen is
physically made of device pixels:

**Is it correct?** Yes, and identically so. At dpr *d*, the boxes stay in logical px **and the viewport also
converts to logical px** (`ViewportPx / d`). Collision is a purely relative test — box against box, box
against viewport — so scaling every term by the same *d* leaves every overlap verdict unchanged. Placement in
logical px and placement in device px provably agree. Nothing in the placement path ever compares against a
physical-pixel quantity, which is what makes this safe. *(This is why a `text-size: 20` label has a
20-logical-px box while painting 40 device px at dpr 2 — those are one fact in two units, not a discrepancy.)*

**Then why prefer it?** Because the two spaces only agree *within* one frame. Across devices they do not:
collide in device px and a denser panel gets a **more cluttered map** — same physical size, same apparent
scale, same style, but a 3× panel's viewport is 50 % "bigger" in device px than a 2× panel's, so more labels
clear the test. Whether a map reads as crowded is a question about **apparent** size, and the logical pixel is
the unit of apparent size. A retina screen must make the *same* cartographic decisions as a non-retina one and
merely render them more sharply.

So the placement space was already right. The defect was never that labels used the wrong unit — it was that
`line-width` had drifted onto the wrong side of §1.1's table.

---

## 2. Verified facts

Every claim here was read out of the tree, not assumed.

### 2.1 Where DPR comes from

| | |
|---|---|
| `MapViewConfig.DevicePixelRatio` | serialized default `1.0` (`MapViewConfig.cs:31`) |
| Runtime derivation | `Bootstrapper.cs:139` — `DeviceScaling.DevicePixelRatioFromDpi(Screen.dpi)` |
| The formula | `screenDpi / 160.0` (`DeviceScaling.cs:61`), 160 = Android **mdpi** baseline |
| Observed once, in an Editor session | **1.56** (⇒ `Screen.dpi ≈ 250`). **Neither a device measurement nor a backing scale** — see below |
| In tests | **always `1.0`** — tests drive `Wire`, not `Start`, and keep the serialized value |

**What the 1.56 actually is (S109).** It was read from an Editor session on this Mac, whose built-in panel is
a 3456 × 2234 16.2-inch Liquid Retina XDR — **254.0 ppi** (`hypot(3456, 2234) / 16.2`; Apple publishes 254).
`Screen.dpi ≈ 250` is within 1.7 % of that, and the residual has a mechanism: **EDID reports physical size in
whole centimetres**, so a 345.6 × 223.4 mm panel is reported as 35 × 23 cm and
`3456 / (35 / 2.54) = 250.8 dpi`. So `Screen.dpi` is reporting a **real physical density, correctly** — a
Game-view subwindow has a *size*, not a density, and Unity documents its Editor divergence for
`Screen.width`/`height` and says nothing about `dpi`.

**That does not rescue 1.56.** macOS's own scale factor on this panel — what a browser reports as
`window.devicePixelRatio`, which is the definition this epic is chasing (§1) — is **exactly 2.0**
(`NSScreen.backingScaleFactor` is 1 or 2, never a density quotient). `254 / 160 = 1.5875`, so the map is
**21 % thinner** than the browser's rendering of the same style, and `÷160` would need a 320-dpi reading to
reach 2.0. The mismatch is **structural, not a bad constant**: no divisor makes a physical density track a
two-valued OS scale. 1.56 is therefore a *hypothetical* ratio wherever this doc uses it as an illustration,
never "the ratio the map ships at".

### 2.2 The five places DPR was divided in — and their Stage-3 disposition

Six raw divisions across five sites. Line numbers are as of `2adc7d88` (the pre-Stage-3 tree); the original
table's were stale by a stage and are corrected here.

| # | site | kind | Stage-3 disposition |
|---|---|---|---|
| A | `MapCamera.cs:110` `ViewportLogicalPx` — the label/coverage basis | **the definition** | **KEPT**, body routed through `DeviceScaling.DeviceToLogicalPx`. It is the named concept every logical-px consumer already reads; it gains the guard it never had. |
| B | `MapCamera.cs:142-143` — camera **altitude** | re-derives A | **DELETED**; frames from `ViewportLogicalPx.y`. Byte-identical at *every* ratio: `double2 operator /` is component-wise, so `(ViewportPx / d).y` and `ViewportPx.y / d` are the same expression, and both sides already shared the ≤ 0 fallback. |
| C | `MapView.cs:526` `FramingViewportPx` — tile cover selection | re-derives A | **REPLACED** with `Camera.ViewportLogicalPx`. Framing is what `MapCamera` owns ("the camera IS the viewport"); altitude framing and cover framing must agree by construction (S86/S92 "render far == selection far"), and two derivations are what permits divergence. Costs an ordering dependency — `LateUpdate` refreshes the camera's ratio (`:380`) before building the selector config (`:411`) — but that ordering is pre-existing and already load-bearing for `SyncToCamera` one line later. |
| D | `Controller.cs:122,125,153` — interaction viewport + cursor | **input boundary** | **BOUNDARY KEPT, conversion routed.** The local guard is gone; both the viewport and the cursor call `DeviceToLogicalPx`. |
| E | `TouchController.cs:140,143,176` — touch viewport + positions | **input boundary** | Same as D, at the viewport and at each touch contact. |

**Why D and E keep their boundary rather than reading `ViewportLogicalPx`.** Three reasons, in increasing
weight: they convert *positions* (a cursor, a finger), and nothing upstream computes or consumes a "logical
cursor"; their viewport deliberately falls back to `Screen.width/height` when the **component's own**
serialized camera field is null, whereas `MapCamera` wraps a ctor-enforced non-null camera and has no such
fallback — pointing them at `ViewportLogicalPx` would delete a live path; and converting where physical
measurements *enter* the system is the correct seam. What they gain is the shared definition and the shared
guard, which is what D2 asks for.

**One test-code copy remains** (`MapTelemetryTests.cs:123`) — recorded in §6.1, deliberately not touched.

### 2.3 The FOUR conversion mechanisms (this is the defect, in its true shape)

The original two-basis split ("labels logical / lines device") is directionally right but is **not** the real
partition. The partition is by **how a px value reaches world/clip space**, and it cuts *through* the label
family — `text-size` is logical while `text-halo-width`, on the same material, is device.

| conversion mechanism | pixel space it lands in | correct before S107? |
|---|---|---|
| CPU: `px × WebMercator.GroundResolution(zoom)` (`Coordinates/WebMercator.cs:102`) | **logical** — the camera altitude already divides by dpr | ✅ |
| GPU: `off / _ScreenParamsLogical.xy` (`SymbolTextWorld_ForwardPass.hlsl:99`, `SymbolIconWorld_ForwardPass.hlsl:97`) | **logical** | ✅ |
| GPU: `MapPixelsToWorld()` — spans against `_ScreenParams.xy` (`Line_VertexExtrude.hlsl:80-104`, char-identical copy in `Fill_VertexModify.hlsl:91-115`) | **device** | ❌ |
| GPU: compare against an `fwidth`-derived screen scale (`SymbolTextWorld_ForwardPass.hlsl:119-128`) | **device** | ❌ |
| GPU: `_MapFrameMetersPerDevicePixel` — the frame constant, read by the line dash divisor (S110) and, since S116, by the whole line WIDTH family | **device** | ✅ correct by construction — it is the only mechanism whose basis is chosen at the push site rather than inherited from a shader's screen reference |

**Root cause, from the history.** `d5406d40` ("S104 P4 — resolve line width from the projection, delete
`_MetersPerPixel`") removed the uniform fed by `CameraPoseMath.MetersPerPixel(zoom)`. That was metres per
**logical** pixel, so `widthWorld = _Width × mpp(zoom)` rendered a `line-width: N` road at N *logical* px —
DPR-correct. Its replacement measures against `_ScreenParams`, the **physical framebuffer**, silently
rebasing the whole line family. The commit's "Behaviour unchanged" was true **at `dpr == 1`, the only DPR
any test ran at** — §2.5's failure mode, caught in the act.

**Consequence, exactly as observed:** raising DPR lowers the altitude (map zooms in) and enlarges labels
(logical basis), while every line keeps its literal screen width. Labels and roads drift apart.

### 2.4 The px-valued style surface

**Material-bound — the family that meets the seam.** Eleven, not eight: `text-halo-blur` was missing from the
original list and the two `*-translate` properties were listed only in passing. Eight of the eleven scale;
`text-size`, `text-padding` and `icon-padding` do not. (`_WidthIsPixels` is a mode flag, not a px value, and
is deliberately left alone.)

| property | consumer | space | S107 |
|---|---|---|---|
| `line-width` | `Line_VertexExtrude.hlsl:165`, via `MapPixelsToWorld` | device | × dpr |
| `line-gap-width` | `Line_VertexExtrude.hlsl:187` | device | × dpr |
| `line-offset` | `Line_VertexExtrude.hlsl:216` | device | × dpr |
| `line-blur` | `Line_VertexExtrude.hlsl:406-410` — `fwidth(side) × _Blur` is a band exactly `_Blur` device px wide | device | × dpr |
| `line-translate` / `fill-translate` | `Line_VertexExtrude.hlsl:272-273` / `Fill_VertexModify.hlsl:113-114` — the same `MapPixelsToWorld` call | device | × dpr |
| `text-halo-width` / `text-halo-blur` | `SymbolTextWorld_ForwardPass.hlsl:128`, added to a `screenDist` in device px | device | × dpr |
| `text-size` | `SymbolTextWorld_ForwardPass.hlsl:99`, divided by `_ScreenParamsLogical` | **logical** | factor 1 |
| `text-padding` / `icon-padding` | `LabelStagingMath.cs:171`/`:384`, collided against `SymbolProjectionJob.ViewportLogicalPx` anchors | **logical** | factor 1 |
| `line-dasharray` | `Line_VertexExtrude.hlsl` `dashMetersPerUnit` — `_Width × _MapFrameMetersPerDevicePixel` (S110) | device (**both halves**) | × dpr, **cancelling** |

**`line-dasharray` is the row that shows why a CPU round-trip cannot prove a basis.** Its period in world
metres is `(w_logical · dpr) × (mpp_logical / dpr) × Σ` — **the dpr cancels**. A Core helper written as
`widthLogicalPx × MetersPerPixel(zoom) × Σ` is therefore *correct and mentions no ratio at all*, and would
round-trip green at every dpr while the shader was off by exactly `dpr`. The error can only exist because
the two halves are owned by different files: `ZoomStyleApplier` multiplies the width by dpr, and the ruler's
push site must divide by it. Only a rendered tooth at dpr ≠ 1 (S110 T2) plus a direct read of the pushed
global (T3) can see it.

*S116 update: the push moved from `RenderLayerSet.ApplyZoom` to `MapCamera.SyncToCamera`, where the ruler is
measured off the camera as `2·d_lookAt·tan(fov/2) / ViewportPx.y`. The `/ dpr` above is now **implicit** —
`ViewportPx` is physical and the ratio enters exactly once, through `ViewportLogicalPx` in the altitude
framing — so the two halves can no longer name it separately and disagree. The pinned values are unchanged;
see `docs/line-rendering-design.md` §1.1.*

**The collision grid is not a third space.** `LabelStagingMath` mixes padding with `BoundsMin/Max`,
`TextSizePx` and a `ViewportLogicalPx`-projected anchor in one expression — the same quantities the shader
divides by `_ScreenParamsLogical`. One space, logical.

**CPU-side px properties never reach the seam at all**, and all are already correct: `symbol-spacing`
(`SymbolFeatureExtractor.cs:256`, via `WebMercator.TilePixelSize`), `text-translate` / `icon-translate`
(`LabelStagingMath.cs:160`, `:381`), `icon-offset` / `text-offset` (ems → logical px in `IconQuadLayout`).
So D2's guarantee is scoped honestly: *the **material-bound** px family cannot compile without naming its
space; the CPU-side px family resolves through `WebMercator.TilePixelSize` / `ViewportLogicalPx` and is
logical by construction.*

`icon-size` is a unitless multiplier on sprite logical size, already divided by the sprite sheet's own
`pixelRatio` in `IconQuadLayout` — a **different** ratio; do not conflate the two.

### 2.5 Why 1905 tests never caught it

**Every test runs at `DevicePixelRatio = 1.0`, where all five divisions are the identity.** The DPR ≠ 1
behaviour — the only one that ever runs on a real device — is untested. That is the root cause of the class,
not just of this instance.

---

## 3. Decisions

**D1 — logical px is the canonical basis; lines are wrong, labels are right.** MapLibre defines every `px`
style value as a CSS pixel. Do not "fix" labels down to device px to match lines. The reasoning behind this —
the layout/sampling split and why placement is density-independent — is **§1.1–1.2**; read it before deciding
which space a new property belongs in, because the spec appeal alone will not tell you.

**D2 — DPR is applied ONCE, at the px→device conversion, not per call site.** The current shape (five
divisions) is what allowed one family to be missed silently. There must be a single named conversion that the
whole px family flows through; adding a new px-valued property must not require remembering to divide.

**D3 — at `dpr == 1` the change is the IDENTITY.** This is the stage invariant and it is falsifiable: every
existing golden and all 1905 pre-change tests must stay byte-identical, because that is the DPR every test
runs at. A moved golden means the conversion is wrong, not that the golden needs re-baking. *(Held in S107:
1905 → 1915, every addition, no expected value edited.)*

**D4 — `Screen.dpi / 160` is a separate, unresolved question.** The mdpi baseline is an Android convention;
on desktop `Screen.dpi` is not a backing-scale factor, and 1.56 is **not** the ratio the platform intends —
on this panel that is 2.0 (§2.1, and §4b's branch-B finding: the platform's scale factor is not in the
process at all).
Fixing the *plumbing* (D1–D3) is independent of choosing the right *value*, and must land first — otherwise a
value change silently rescales the map with nothing pinning what "correct" means. **Deliberately deferred to
its own stage.**

**D5 — expect a visible change.** At a ratio of 1.56, correcting the line family makes roads ~1.56× wider and
halos ~1.56× thicker. That is a *relative* correction — roads now scale with everything else instead of
staying put — not a regression. Text does not move.

> **D5's original wording was FALSE and is corrected here (S109), not filed.** It read *"that is the correct
> MapLibre appearance at that density"*. It is not: MapLibre on this panel uses `window.devicePixelRatio`
> **2.0**, so 1.5875 renders **21 % thinner** than the browser and the browser draws the whole map
> **1.26×** larger (`2.0 / 1.5875`). Getting the *plumbing* right (D1–D3, D6, D9 — every px family scaling
> together) is what Stages 1–3 delivered and what the eyeball below discharged. Getting the *ratio* right is
> Stage 4b, and it is still open. Do not read a green gate or a discharged eyeball as evidence that 1.56 is
> the right number.

**Eyeball DISCHARGED** (2026-07-31, on `feat/device-pixel-ratio`): sliding `DevicePixelRatio` now changes the
scale of **everything** together. That is the whole convention observable in one gesture — the ratio says how
many physical pixels back a logical one, so zoom, labels, roads and halos must all move by the same factor.
The opening symptom was precisely that the line family was the one thing that did **not**. This is the check
no tooth could make: every tooth measures *magnitude* (ratios of 2.0 in a 512² fixture), none confirms the
*direction* is right in a real frame at a live ratio. Note what it does **not** discharge: that the ratio the
Bootstrapper derives is the right one (D5's correction above, Stage 4b).

**D6 (S107) — the conversion lives on the CPU, at ONE seam, with the space named at the binding site.**
`DeviceScaling.LogicalToDevicePx(logicalPx, PixelSpace, dpr)` is the only conversion;
`ZoomStyleApplier.BindDevicePixelFloat` / `BindDevicePixelVector` are the only bindings that apply it, and
they take **no default argument**, so a property routed through them cannot be bound without declaring its
space.

**Do not over-read that guarantee.** A newly-added px property can still be bound via `BindFloat` (or a raw
`mat.SetFloat`) and silently keep the wrong basis — which is precisely how the line family drifted in
`d5406d40`. Forcing a `PixelSpace` on *every* binding would be wrong, because unitless properties have no
pixel space. **The real guard is §2.4's px-surface table plus T4**, not the compiler: a new px property must
be added to that table, and the table is what a reviewer checks.

*Why the CPU and not `MapPixelsToWorld`:* the shader's `pxToWorld` must keep returning metres per **device**
pixel, because the AA straddle pad (`Line_VertexExtrude.hlsl:159`) and the hairline floor derived from it are
genuinely sampling-grid quantities — half a physical pixel is half a physical pixel at any density. Fixing it
inside `MapPixelsToWorld` would drag the just-landed AA work along and render a dpr-2 road a physical pixel
fatter *and* softer. **No shader was edited in Stage 1.**

*Rejected: "one basis everywhere."* Emitting device px for all nine and moving the label path off
`_ScreenParamsLogical` is identity at dpr 1 and so survives every existing test — but it drags
`SymbolProjectionJob.ViewportLogicalPx`, all of `LabelStagingMath`, `LabelInstance.PaddingPx`, the collision
grid's cell sizing and every px threshold in the fade machinery into device px, each site invisible to the
gate at dpr 1. That reproduces this epic's exact failure mode at ~10× the surface, to buy uniformity in a
half that is **already correct**.

**D7 (S107) — device-pixel bindings ALWAYS queue; the data-driven width bake stays dpr-free.** A Constant
`line-width` no longer takes the bind-time shortcut, because the ratio is a per-frame input that can change
live (a window dragged between panels); the cost is ≤ 5 extra `SetFloat` per line layer per frame, allocation-
free, inside a loop that already runs per frame. For **data-driven** width the uniform carries the base only,
so it is bound as a device-px constant of 1 — making `_Width == dpr` and `widthWorld = dpr × bakedPx ×
pxToWorld`. Scaling `StyledLineTileBuilder`'s per-vertex bake instead would put the ratio inside the geometry,
where a live ratio change cannot reach it and `PreparedTileCache` would serve it stale.

**D8 (S107) — `RenderLayerSet.Build` takes no initial ratio; the caller re-applies instead.** Threading one
through `RenderLayerFactory`'s four Create/TryCreate overloads would churn 8 call sites (7 in tests). It is
not free of consequence, though: `SetStyle` is **async**, so its continuation can resume after a frame's
`LateUpdate` has already run, and on a restyle the previous tiles are still loaded — the newly-built layers
would draw them once at the seeded dpr 1 (36 % too thin at a ratio of 1.56). The symbol halo has no seed at
all. So
`MapView.SetStyle` issues `Layers.ApplyZoom(zoom, dpr)` immediately after `Layers.Build`, one line, closing
the window for every layer kind. *(A doc-comment alone would not have.)*

---

**D9 (S108) — device→logical is ONE named conversion, and the non-positive-ratio fallback has ONE home.**
The mirror of D6, and the same shape: `DeviceScaling.DeviceToLogicalPx` is the only device→logical
conversion, and a private `SafeRatio` is the only place the "unusable ratio ⇒ 1" fallback is written, for
**both** directions. Pinned structurally by T3-6, not by a comment.

*What it was papering over — this was a real, reachable defect, not a tidiness argument.* The guard diverged
three ways: `DeviceScaling.LogicalToDevicePx`, `MapCamera`'s altitude and both interaction seams had it;
`MapCamera.ViewportLogicalPx` and `MapView`'s `FramingViewportPx` had **none**, and returned `±∞` at a ratio
of 0. Reachable without any platform claim: `MapViewConfig.DevicePixelRatio` (`MapViewConfig.cs:31`) is a
plain serialized `public double` whose tooltip said *"Must be positive"* with **nothing enforcing it** — no
property, no clamp, no `OnValidate` — so any Inspector edit or scene asset carrying 0 lands there. (S109
replaced that claim: the tooltip now states the contract the code actually keeps — a value outside the
plausible band degrades to 1 *at the conversion*, which is still not an enforcement at the field.) (A second
path exists — `Bootstrapper.cs:139` calls `DevicePixelRatioFromDpi(Screen.dpi)` unconditionally, and its only
protection is a `[Conditional("DEBUG")]` assert — but the config path is verified from source and needs no
platform claim.)

*Blast radius at a ratio of 0, stated precisely* (the obvious reading — "an infinite far plane" — is **not**
what happens): `ViewportLogicalPx = +∞` feeds the label collision viewport, which then rejects nothing. And
`FramingViewportPx = +∞` survives the selector's `vp <= 0` early-out, drives `AltitudeForZoom` to `+∞` and
the camera pose to NaN — so **every** frustum-plane comparison is false, `IntersectsAabb` accepts every tile,
while `lodRatio = finite/∞ = 0` means the LOD stop never fires. The consequence is projection-dependent: the
planar cover enumerates the full quadtree to `maxZoom` (a practical hang — depth is bounded, breadth is not),
the globe path culls everything through `IntersectsSphere` and renders blank.

*Fixing the source* — a `Screen.dpi > 0` test at the bootstrap, or a clamp on `MapViewConfig` — is Stage 4's
question and deliberately **not** in Stage 3. Defence at the consumers is complete on its own: routing
`ViewportLogicalPx` through the conversion *is* the guard, and excluding it would have meant deliberately
authoring an unguarded variant.

## 4. Staged plan

### Stage 1 — one DPR-aware px→device conversion (the fix) — **DONE (S107)**
`DeviceScaling.LogicalToDevicePx` + `PixelSpace` (Core, engine-free) → `ZoomStyleApplier`'s two device-pixel
binding lists → `IRenderLayer.ApplyZoom(zoom, dpr)` → `MapView.LateUpdate` reading `_config.DevicePixelRatio`
(the config OWNS the ratio; going through the camera would imply the camera owns the paint basis, which is
the confusion §2.3's corrected comment came from). Eight properties converted (D6/§2.4); `text-size` /
`text-padding` / `icon-padding` deliberately untouched. *Invariant held: no golden moved, no expected value
was edited.*

### Stage 2 — the teeth that would have caught this — **DONE (S107)**
`DevicePixelRatioSnapshotTests` renders through a real `MapCamera` whose `DevicePixelRatio` is the swept
variable, into a fixed 512×512 framebuffer, and asserts RATIOS. Measured RED→GREEN:

| | dpr 1 → 2, before | after |
|---|---|---|
| styled `line-width` (coverage integral) | 16.00 → **16.00** px (ratio 1.00) | 16.00 → **32.00** px (ratio 2.00) |
| ground feature (world-metre width) | 40.00 → 80.00 px (ratio 2.00) | unchanged — the control |
| label height | 65 → 130 px (ratio 2.00) | unchanged — already correct |

*The fixture shape is load-bearing:* a fixture with its own camera (`LineAaSnapshotTests`' orthographic rig,
`MapViewSnapshotTests`' bounds-framed `SnapCam`) has no dpr in the render at all and would measure nothing.
`DevicePixelRatioBindingTests` adds the uniform-readback table, including the data-driven-width row
(`_Width == dpr`, not 1) and a BRG-packed row that converts "the backends inherit it" from an argument into a
tooth.

### Stage 3 (S108) — one named `device → logical` conversion
`DeviceScaling.DeviceToLogicalPx(devicePx, dpr)` — the exact mirror of Stage 1 — with a single private
`SafeRatio` guard shared by both directions. All six raw divisions route through it (§2.2's disposition
table). The deliverable is **zero hand-written divisions and zero hand-written guards outside
`DeviceScaling.cs`**, which is D2 made structural rather than aspirational. It closes the latent defect D9
records for free. Touches camera framing and the interaction seam — its own stage, its own review.

#### The `TilePixelSize` fold: EVALUATED AND REJECTED

This doc previously specified Stage 3 as *"fold the ratio into the definition that feeds `MetersPerPixel`
(`WebMercator.TilePixelSize = 512.0`) so the five scattered `÷ dpr` sites disappear."* That was **not a
refactor**. `TilePixelSize` is the *definition of the logical pixel* (512 logical px per tile edge), so
folding dpr in rebases `GroundResolution` from metres-per-**logical**-px to metres-per-**device**-px. It is
exactly right for the altitude and wrong for every other consumer, none of which is paired with a physical
viewport term. Five semantic breakages, all verified in the tree, all of them the **identity at dpr 1** and
therefore invisible to the whole suite:

* **`symbol-spacing` moves into device space** (`SymbolFeatureExtractor.cs:256` — `spacing * extent /
  WebMercator.TilePixelSize`). `spacingTileUnits` halves at dpr 2, so line labels along a road render twice
  as dense on a retina panel while the view is unchanged. A device-space drift in a `px` style property —
  this epic's own bug class, reintroduced by its fix.
* **`fill-pattern` sprite size halves** (`FillPattern.cs:116`, on a member literally named
  `LogicalSizePixels`). Today a 32-px sprite paints 32 logical px; folded, it paints 32 *device* px = 16
  logical px at dpr 2. Against §1.1's table ("how big should this *look*?" → logical), a regression.
* **Tile selection's answer changes**, which §6 forbids (`FrustumTileSelector.cs:58-61`:
  `offset = round(log2(tilePx / onScreenPx))`). At dpr 2 the offset becomes 1 — the cover is selected a
  whole level deeper — and `round(log2 1.56) = 1` too, so it also fires at the ratio the Editor happens to
  derive. Exempting this one site immediately reinstates two constants, which is the thing the fold was for.
* **The pixel↔ground service drifts by dpr.** `WebMercatorProjection.cs:78/:114/:149` use
  `GroundResolution(zoom)` as metres per **logical** px and are fed an already-÷dpr cursor and viewport by
  `Controller.cs:220,229`. Fold, and anchored pan and zoom-to-cursor drift on a dense panel — the S92
  B-ZOOMPIN / B-PAN invariants, broken.
* **The label horizon cull radius changes by dpr.** `SymbolPlacementSystem.cs` pairs
  `CameraPoseMath.MetersPerPixel(zoom)` with `viewportLogicalPx` in one `CullRadiusMeters` call; under the
  fold the two operands land in different spaces.

Two further costs. `TilePixelSize` is a `public const double` (re-consted at `CameraPoseMath.cs:32`); a
dpr-dependent value cannot be `const`, so it becomes a new parameter threaded through Core's most-called
math surface (`GroundResolution`, `MetersPerPixel`, `AltitudeForZoom`, `MinZoomToFit` ×2, `MinZoomToFill` ×2,
`MinZoomFloor`, the selector's ctor, ~20 test call sites). And the mechanism **does not reach three of the
five sites** at all: `Controller.cs:153` and `TouchController.cs:176` divide a cursor and a finger
coordinate, which no zoom-scale constant can touch, and both input viewports have a `Screen.width/height`
null fallback. It removes 2 of the 6 divisions and breaks 5 consumers to do it. Scoping the fold to the
framing consumers only removes **zero** divisions — it relocates one from `MapCamera` into `AltitudeForZoom`.

*Mirror of §1.1's "Rejected: one basis everywhere" — the same shape of argument, one layer down.*

### Stage 4a (S109) — the ratio guard becomes a plausibility band — **DONE**
`DeviceScaling.SafeRatio`'s predicate widens from "positive" to a two-sided band, `[0.25, 8]` inclusive
(40 dpi … 1280 dpi), against two **private** constants. One expression, and D9's "one guard, one home" is
what makes it reach all four consumer families — the paint basis, the camera altitude, the cover framing and
both interaction seams — without touching any of them.

**Why a band, and why those bounds.** The bounds are deliberately placed **far from real hardware**:
Android's densest bucket (`xxxhdpi`, 640 dpi) is **exactly 4.0** and its sparsest (`ldpi`, 120 dpi) is 0.75,
so nothing real lands within 2× of a bound. That single fact settles two questions at once.

* *Why not a floor of 1?* Sub-1 ratios are **legitimate** — a ~100-dpi desktop panel is 0.625. A floor of 1
  would silently rebase the map on every low-density device instead of rejecting a bad reading.
* *Why not the tighter `[0.5, 4]`?* It would put a real flagship **on** the ceiling, one `<`-vs-`<=` slip
  away from a 4×-too-small map.
* *Why a fallback and not a clamp?* Because a clamp **invents a plausible-looking value** — a map drawn at
  0.25 still looks like a map, so the substitution is invisible to inspection, where the fallback yields the
  documented neutral default that every test and the eyeball baseline already run at. It also keeps ONE
  behaviour for "unusable" instead of splitting the guard's semantics at the bounds. Since the bounds are far
  from anything real, the "but clamping 0.24 would only move it 4 %" objection never fires on a real value:
  anything reaching a bound is garbage, and garbage → the neutral default.

**It closes a live hole.** `+∞` satisfied `> 0` and **propagated**: `LogicalToDevicePx(7, Device, +∞) = +∞`
and `DeviceToLogicalPx(1080, +∞) = 0`. It also closes §6.1 finding 6 — `NaN` now fails the band by
**decision** (it fails both comparisons like any other implausible value) rather than by the accident of
`NaN > 0` being false.

**No behaviour inside the band changed**, and that is falsifiable: every dpr literal in the whole EditMode
assembly is `{0.5, 1.0, 1.56, 2.0, 3.0, 4.0}` plus the non-positives that already fell back. **No golden
moved and no expected value was edited.** Teeth T4-1/T4-2/T4-3 (§5).

### Stage 4b — the derivation formula: **A MAINTAINER DECISION, OPEN**

> **The finding that makes this a policy call, not a mechanical edit: Unity exposes NO backing-scale or
> UI-scaling factor to managed user code, on any installed target.** Established by static enumeration of
> the public member surface of both the Editor reference assemblies and the **player** assemblies of every
> installed playback engine (`AndroidPlayer`, `MacStandaloneSupport`, `LinuxStandaloneSupport`,
> `WebGLSupport`), reading accessibility out of the metadata. There is no formula that turns `Screen.dpi`
> into the platform's scale factor on desktop, **because the platform's scale factor is not in the process.**

| API | what it actually is |
|---|---|
| `UnityEngine.Screen.dpi` | the only runtime density surface; documented as *"the actual DPI of the screen or physical device"*, `0` when undeterminable. **A density, never a scale factor.** |
| `UnityEngine.DisplayInfo.physicalDpi` | per-display density — Unity's own field name says *physical*. `DisplayInfo` has **no** scale field. |
| `UnityEditor.EditorGUIUtility.pixelsPerPoint` | the genuine OS backing scale (2.0 on Retina) — **Editor-only**, in no player. |
| `UnityEngine.GUIUtility.pixelsPerPoint` | **exists in the shipped player**, but its getter is `internal`. *Not callable from user code* — which is the accurate claim; "does not exist" would be wrong. A web claim that it is "the runtime equivalent" of the Editor property is false. |
| `UIElements.IPanel.scaledPixelsPerPoint`, `Canvas.scaleFactor` | public at runtime, but each is its own framework's **output**, derived from `PanelSettings.referenceDpi`/`fallbackDpi` or `CanvasScaler` against `Screen.dpi`. Circular. |
| `UnityEngine.Android.AndroidConfiguration.densityDpi` | Android only; exactly `DisplayMetrics.densityDpi`. |
| `QualitySettings.resolutionScalingFixedDPIFactor`, `ScalableBufferManager.*ScaleFactor`, `Camera.scaledPixelWidth/Height` | dynamic-resolution / render-scale knobs. **Unrelated** — named here so a later reader does not "find" them. |

**What `Screen.dpi` means per platform.**

| platform | what it returns | is `dpi / 160` the scale factor? |
|---|---|---|
| **Android** | `DisplayMetrics.densityDpi` (Unity's docs say so, and recommend averaging `xdpi`/`ydpi` for real physical accuracy — i.e. Unity itself saying `densityDpi` is not physical) | **Yes, exactly.** Android defines `density` as *"a scaling factor for the DIP unit… 1 on a 160-dpi screen, .75 on a 120-dpi screen"*. `Screen.dpi / 160 == DisplayMetrics.density`. |
| **iOS** | a hardware-id → PPI **table** Unity ships (no public iOS API reports physical PPI; the table has a history of wrong entries) | approximates `nativeScale` without equalling it: 326/160 = 2.04 vs 2; 460/160 = 2.875 vs 3 |
| **macOS** | the panel's physical density (§2.1: 254 ppi here, EDID-quantised to ≈250) | **No, and no divisor can fix it** — the backing scale is two-valued (1 or 2) and `÷160` would need 320 dpi to reach 2.0 |
| **Windows** | not established from a primary source — either the EDID physical density (then `÷160` ignores the user's 125/150/175 % setting) or the effective system DPI `96 × scale` (then `÷160` yields **0.6 at 100 %**). **Both wrong, opposite directions.** |
| **WebGL** | not verified. The browser's dpr **is** in the process natively (`JS_SystemInfo_GetPreferredDevicePixelRatio` in the WebGL playback engine's `SystemInfo.js`) but no managed API surfaces it, and the web's reference density is **96**, not 160 — so `÷160` would be wrong there in the same direction as Windows |

**Unity's own UI faces the identical problem and has nothing better.** `CanvasScaler.HandleConstantPhysicalSize`
reads `Screen.dpi`, substitutes a hard-coded `m_FallbackScreenDPI = 96` when it is 0, and divides by a target
DPI. UI Toolkit's `PanelSettings.referenceDpi`/`fallbackDpi` are the same design one framework over. Two
further reads: Unity's reference density is **96** (the CSS/Windows convention), not our 160 (Android's); and
uGUI's *default* mode is `ConstantPixelSize` — Unity's out-of-the-box answer to "what is the density scale?"
is **do not scale**, which is option B2's `1.0`, arrived at independently.

**A real defect this uncovered, in the tree today.** `Bootstrapper.cs:139` overwrites the ratio from
`Screen.dpi` **unconditionally in `Start`**, and `Start` runs in Play mode — so **every in-Editor Play session
takes its ratio from whichever monitor the Editor window is on.** When the build target is Android or iOS —
the platforms where `dpi/160` is *correct* — Play mode feeds it this Mac's 254 ppi instead of the device's,
silently. `Screen.dpi`'s Editor behaviour is **undocumented** (Unity documents the Editor divergence for
`Screen.width`/`height` and nothing for `dpi`), and community reports conflict about whether it returns 0
there. Stage 4a corrected the **comments** that claimed otherwise; the fix is 4b's.
**Assessed and rejected: a standalone `Application.isEditor` gate.** It would add a third policy branch that
B1+B3 then has to reconcile, and B6 is a strictly better version of the same intent.

| # | option | what it does | risk / cost |
|---|---|---|---|
| **B1** | **Per-platform policy as a pure `Core` function** | `DeviceScaling.DevicePixelRatioFor(convention, screenDpi)`; the Unity boundary picks the convention from `Application.platform`. Android → `dpi/160` (proven correct); desktop → `1.0`; iOS → `dpi/160`, optionally **rounded to the nearest integer ≥ 1**, which recovers the true `nativeScale` for current devices (326→2, 460→3, iPad 264→2) | one enum + one function + a `switch`, **table-testable** in the fast loop. iOS rounding is a heuristic — iPhone 6 Plus (401 ppi, `nativeScale` 2.608) sits at 2.5 and rounds ambiguously |
| **B2** | **Desktop → 1.0, everything else unchanged** | one platform test at `Bootstrapper.cs:139` | smallest possible; leaves iOS on the approximation and hard-codes policy at a Unity boundary no test reaches |
| **B3** | **Explicit override + auto** | `DevicePixelRatioMode {Auto, Manual}` on `MapViewConfig`; `Auto` runs B1/B2, `Manual` honours the serialized value | one serialized enum + one branch. Today the Bootstrapper overwrites the field unconditionally, so there is **no supported way to set a ratio by hand on a device**, and the maintainer's own eyeball workflow (sliding the value live) depends on it |
| **B4** | **Editor-only truth** | `#if UNITY_EDITOR` read `EditorGUIUtility.pixelsPerPoint`; player falls back to B1/B2 | ~3 lines, but **argued against as the shipping mechanism**: Editor and player would disagree, so the eyeball would validate behaviour the build lacks. Genuinely useful as a *measurement* |
| **B5** | **Native plugin** | `NSScreen.backingScaleFactor` / `GetDpiForWindow` per desktop platform | two small native plugins + per-platform build config + maintenance. **Correct, and the only way to get the real factor in a player** — the eventual right answer, out of proportion until desktop DPR matters |
| **B6** | **`UnityEngine.Device.Screen.dpi`** | a **one-token** change at `Bootstrapper.cs:139`. This is the Device-Simulator shim (public type, public `dpi`): in the Editor it yields the *simulated device's* values, in a player it forwards to `UnityEngine.Screen` | removes most of the Play-mode harm above **with no policy attached**, and composes with B1/B3. Does **not** answer the desktop question at all |

**Must be decided before any code** — do not let a developer pick these: **(a)** the desktop default — `1.0`,
or `2.0`-on-Retina via B4/B5; **(b)** whether iOS keeps `dpi/160`, rounds, or gets its own convention;
**(c)** whether B3's override ships with B1.

**Open, and cheap to settle:** what `Screen.dpi` returns on Windows standalone; the **iOS** playback engine is
not installed on this machine, so its platform-specific surface was not swept (it cannot affect the
macOS/Windows legs (a) turns on).

#### How Stage 4b can be judged — and what nothing in-process can judge

* **Can be pinned:** the *mapping* — a table test over `(convention, dpi) → ratio` in the fast loop — and that
  the platform branch has **one home** (a T3-6-shaped sweep asserting `Screen.dpi` is read in exactly one
  production file; it passes today and would stop the new branch being copied).
* **Cannot be pinned, by anything:** that the chosen number matches what the OS intends. That oracle is not in
  the process — it *is* the branch-B finding. `Bootstrapper.Start` never runs under the test runner, and every
  test holds the serialized `1.0`. **A green gate after a formula change carries zero information about the
  formula.**
* **The desktop-default question is settled in-process, without a browser.** `ProjectSettings.asset` has
  `macRetinaSupport: 1`, so a standalone macOS build allocates a full-resolution backing store. Log
  `Screen.width` once in that build: **3456 ⇒ the framebuffer is device pixels ⇒ a dpr of 2.0 is owed;
  1728 ⇒ it is points ⇒ 1.0.** (This does not make a *constant* 2.0 correct — drag the window to a 1× monitor
  and it is wrong again, which is why B5 remains the only correct mechanism.)
* **The browser comparison, if run, needs two controls the obvious version omits.** MapLibre GL JS on the same
  panel, same style, same zoom — but measure the **ground span** (MapLibre ships a `ScaleControl`), not road
  width: since Stage 1 the ratio scales the *whole* map, so a 26 % difference on a 2-px road is a sub-pixel
  judgement while the ground span reads directly. And the two windows must have **equal physical
  (backing-store) pixel size**, or the predicted 1.26 is not what gets measured. Measure at 100 % page zoom
  (browser zoom multiplies `devicePixelRatio`) and on the built-in panel (an external non-Retina monitor is
  1.0).

---

## 5. Teeth

| | |
|---|---|
| T1 | at `dpr == 1`, every existing test and golden is byte-identical (the Stage-1 invariant) — 1905 → 1915, no expected value edited |
| T2 | at `dpr = 1` vs `2`, a line's rendered width and a ground feature's on-screen span scale by the same factor |
| T3 | a label's on-screen size and a line's rendered width scale by the same factor across DPR — the symptom, pinned |
| T4 | each px property's **device footprint** is linear in DPR (table-driven, split by how each is observed) |
| T3-1 | `DeviceToLogicalPx` is exact **division**, not `v × (1/d)` — swept over 30 values × 4 ratios at tolerance **0**. The witnesses matter: at dpr 1 and 2 the reciprocal is a power of two and *cannot* discriminate, and at 1.56 / 3.0 the sizes a test naturally reaches for (512, 1024, 1920, 1080) agree too. Verified RED against an injected reciprocal at `1440 / 1.56` (`923.07692307692309` vs `…298`). |
| T3-2 | the non-positive-ratio fallback is 1 in **both** directions, asserted in one test so they cannot be changed apart |
| T3-3 | **the one named behaviour change**: at a ratio ≤ 0, `MapCamera.ViewportLogicalPx` and `MapView`'s `FramingViewportPx` are FINITE (the dpr-1 value), not `±∞`. RED-verified against the pre-S108 tree |
| T3-4 | at dpr 2 the selector's `FramingViewportPx` **is** `MapCamera.ViewportLogicalPx`, exactly, and equals `ViewportPx / 2` — the only tile-selection coverage at a ratio ≠ 1 anywhere. It does **not** pin `LateUpdate`'s refresh-before-build ordering: it builds the selector config itself after `LateUpdate` returns, so a reorder leaves it green. That ordering is safe **by construction** (single production caller; nothing between refresh and consume mutates the ratio), and the argument is what holds it — see §6.1 |
| T3-5 | *structural.* `Controller.cs` and `TouchController.cs` each route **≥ 2** sites through `DeviceToLogicalPx(` — the viewport AND the cursor/finger positions. Counts the **call form** and ignores comment lines, so the prose naming the conversion cannot inflate the count and let a half-fix through |
| T3-6 | *structural.* The ratio fallback exists in exactly **one** production file (`Core/View/DeviceScaling.cs`) — D2 made checkable rather than hoped-for. Asserts the positive match too, so a broken predicate that matches nothing still fails |
| T4-1 | a ratio below the floor, above the ceiling, `NaN` or `±∞` falls back to **exactly 1.0** in **both** directions, in one test so the two cannot be changed apart. Tolerance **0** and the expected value literally `1.0` — that is what discriminates a fallback from a **clamp**, which would return a plausible-looking `0.25`/`8.0`. RED-verified twice: against the un-widened guard (`0.1` → `0.1`, `+∞` → `+∞`) **and** against an injected clamp (`0.1` → `0.25`, `+∞` → `8.0`). Only `+∞` was a behaviour change; `NaN`/`−∞` rows are **decision-recording** and green on both sides — which is how §6.1 finding 6 closes by a tooth rather than by prose. Plus one composition row, `DeviceToLogicalPx(1080, DevicePixelRatioFromDpi(1e9)) == 1080`, the closest a test gets to the production path |
| T4-2 | in-band ratios — **including sub-1 ones** — are bit-identical to the input in both directions, and the bounds are **bracketed**: `0.24 → 1` with `0.26 → 0.26` traps the floor in `(0.24, 0.26]`, `7.99 → 7.99` with `8.01 → 1` traps the ceiling in `[7.99, 8.01)`. Neither half of a pair pins anything alone. The exact-bound rows (`0.25`, `8.0`) are the weakest in the set — they distinguish `>=` from `>` and nothing else. Kept separate from T4-1 so moving the band stays a one-test edit |
| T4-3 | *structural.* T3-6 still finds the guard in exactly one production file after the predicate widened — and the predicate was **strengthened**, not ported: the old `devicepixelratio[^;]*>\s*0` spanned to the next semicolon, so it matched at `DevicePixelRatioFromDpi(double screenDpi)` and landed on the unrelated `Debug.Assert(screenDpi > 0.0)` — the positive leg would have passed **with `SafeRatio`'s guard deleted outright**. The replacement requires the operator ADJACENT to the ratio's name, and drops `IgnoreCase` so the `[0-9A-Z]` tail cannot latch onto the `s` of a following `<see cref=…` |

**T4 is not "every px property is multiplied."** The doc's original wording was false: `text-size` /
`text-padding` / `icon-padding` must **not** be multiplied at the seam, yet their device footprint still
doubles at dpr 2 via the logical viewport halving. The universally-true form is: *for each px-valued property,
the quantity it controls, measured in device pixels, is linear in dpr.* A row asserting "padding is scaled"
would be **wrong** and must not be written.

**T1 is structurally blind to everything this change can get wrong** (dpr 1 is where the conversion is the
identity), so it is the cheap check, not the real one. Two limits of the real ones, recorded:

* T4's uniform readbacks are restatements of the binding — they pin the **regression**, not the
  *judgement* that "device" was the right space. Only T2/T3's rendered ratios test that, which is why they
  keep an `IsAllBlack → Inconclusive` no-GPU guard **and** why a run must be checked for Inconclusive before
  a green gate is believed. (S107's landing run: 0 inconclusive, 0 skipped.)
* **Do not write a rendered halo-softness ratio tooth.** The shader's transition denominator is
  `_SdfSoftness + _HaloBlurPx` — an AA constant in device px summed with the now-scaled style blur — so the
  rendered ratio at dpr 2 is `(s + 2b)/(s + b)`, *not* 2. That is the intended semantics; such a tooth would
  go RED against a correct implementation. The uniform readback is the right form.

---

## 6. Deferred / fenced

* `Screen.dpi / 160` correctness (D4, **Stage 4b**) — now a written-up decision brief with the branch-B
  finding behind it, awaiting the maintainer's answers to (a)/(b)/(c). Stage 4a shipped independently of it,
  and nothing in 4a reads a platform or a dpi.
* `icon-size` and the sprite sheet's own `pixelRatio` — a different ratio, already handled in
  `IconQuadLayout`; not in scope, do not conflate with DPR.
* Whether tile *selection* should use logical or physical px is settled today (logical) and is not reopened
  here; Stage 3 removed the duplication and **did not** change the answer — a claim now discharged rather
  than merely intended. It is worth recording what would have broken it: the rejected `TilePixelSize` fold
  (§4) changes `_selectionZoomOffset` from `round(log2(512/512)) = 0` to `round(log2(512·dpr/512)) = 1` at
  dpr 2 *and* at a ratio of 1.56, selecting the cover a whole level deeper.
* **`Core/Style/Line/LineOffset.cs`** — a CPU mirror of the shader's offset formula with **zero production
  callers** (every reference is `LineOffsetTests.cs`). It was deliberately left un-converted in Stage 1: it
  belongs to the separate test-only-production-code cleanup, not here.
* **De-duplicating `LineAaSnapshotTests` onto `PixelCoverage`** (the shared coverage helper Stage 2 added).
  That fixture still carries its own private copies; the refactor was fenced out so no AA tooth was touched
  by a change that cannot affect it.
* **A zoom-expression halo** (design §7 risk 9) is still a follow-up. Stage 1's dpr-gated re-bind
  deliberately re-evaluates at the **frozen creation zoom**, not the live one — re-evaluating live would
  silently implement that follow-up and could move a render at dpr 1.

### 6.1 Open findings from the Stage 1 + 2 review (recorded, not fixed)

Both review arms returned APPROVE with **zero REQUIRED**. These are the non-blocking findings, kept here
rather than in a transient report so the next reader inherits them.

| # | finding | why it was not fixed in-stage |
|---|---|---|
| 1 | **The two new binding loops have no zero-alloc tooth.** `ZoomStyleApplierTests`' two `…_AllocatesZeroGCMemory` teeth bind only through `BindFloat`/`BindColor`, so the device-pixel lists iterate **zero** elements there. The S60-locked zero-alloc property is now unpinned for the path carrying every constant `line-width`. It *is* alloc-free in fact (ValueTuple deconstruct, struct `Vector4`, cached constant `Evaluate`, static over doubles) — nothing asserts it. | One extra `BindDevicePixelFloat` in that fixture closes it. Deferred only to keep the gate-verified tree frozen at commit; **take this first** next time this area is touched. |
| 2 | **No tooth pins the frozen-`_haloZoom` fence.** The halo tooth uses one zoom throughout, so it cannot distinguish `BindHalo(_haloZoom, …)` from `BindHalo(zoom, …)` — the invariant break the fence exists to prevent would pass. | Writable today: a zoom-expression `text-halo-width` plus `ApplyZoom(otherZoom, 2.0)`. |
| 3 | **The CPU-side px family has no tooth of any kind** — `symbol-spacing`, `text-`/`icon-translate`, `*-offset` are documented (§2.4) and correct today, but a future slip into device space there would be invisible at every dpr the suite runs. | The same structural condition this epic exists to remove, one layer over. Wants a T4-shaped table row per property. **S108 checked whether it was cheap to close: it is not.** Nothing new is expressible — `symbol-spacing`'s function takes no ratio, so the only assertable property is source-text, and `DevicePixelRatioBindingTests` already asserts the extractor names no ratio. And that tooth is blind to the gap that actually mattered (the `TilePixelSize` fold, which the extractor would *inherit* without ever naming — §4), so what closed it was **rejecting the fold**, not a new test. |
| 4 | `DevicePixelRatioSnapshotTests` leaks a `new Material(Shader.Find("Map/Symbol/TextWorld"))` (never destroyed); `DevicePixelRatioBindingTests` is wrapped in `#if UNITY_EDITOR` while the other two new files are not. | Test hygiene, no behavioural effect. Stage 3 must not perturb those dpr-2 fixtures — they are what give its byte-identity invariant teeth. |
| 5 | **A seventh copy of the conversion, in test code.** `MapTelemetryTests.cs:123` re-derives `view.Camera.ViewportPx / view.Config.DevicePixelRatio` — unguarded, and now the only hand-written device→logical division left in the repo. | Harmless today (that fixture runs at dpr 1 and the expression is the independent recompute the tooth is *for*), and outside T3-6's production sweep. Noted for the merge step. **S109 assessment: recommend NOT closing it.** Routing it through `DeviceToLogicalPx` would make the tooth recompute its expectation with the production expression it exists to check — closing this finding would *weaken* the test. Maintainer's call; the row stays open only so the decision is recorded rather than silently taken. |
| 6 | ~~**A NaN ratio maps to 1 by accident, not by decision.**~~ **CLOSED (S109, Stage 4a).** The plausibility band makes `NaN` fail *both* comparisons, so falling back is now the decided behaviour of "not a plausible ratio" rather than a side effect of `NaN > 0` being false. T4-1 sweeps `NaN` and both infinities and labels them decision-recording. | ~~Adding a NaN branch would be a behaviour change outside Stage 3's one named exception.~~ Closed for free by 4a's one-expression widening — leaving it open would have left this doc misstating the code. |

| 7 | **`LateUpdate`'s refresh-before-build ordering is held by argument, not by a tooth.** Site C makes the selector's framing read `Camera.ViewportLogicalPx`, so the camera's ratio must be refreshed before the selector config is consumed. T3-4 does **not** pin this — it builds the config itself after `LateUpdate` returns, so a reorder leaves it green. | Safe by construction: `BuildTileSelectionConfig` has a single production caller and nothing between the refresh and the consume mutates the ratio; the ordering was already load-bearing for `SyncToCamera` one line later. A tooth would have to drive a frame with the two steps reordered. **The S108 review caught the doc and the test comment claiming this pin — both were corrected before commit; this row is what replaces the claim.** |

**A false claim corrected in passing (S108).** `TouchInputStackTests.cs:157-158` has asserted since S92 that
its structural dpr tooth mirrors *"`MapControllerInputTests` for the mouse seam."* **It does not, and never
did** — `MapControllerInputTests.cs` had **zero** matches for `dpr` / `DevicePixelRatio` / `DpiScale`. The
mouse seam has been structurally unguarded for the whole of its life while a comment one fixture over
asserted it was covered. Stage 3 adds the missing twin (T3-5's `Controller` half). This is the same failure
mode as `d5406d40` and §2.3's corrected `MapCamera` comment: a claim in prose that the code does not keep.

**Two claim-accuracy corrections were made before commit** rather than recorded: D6's "cannot compile without
declaring its space" over-claimed (see D6's own caveat), and Stage 1 said "six properties converted" where the
code converts **eight**. A doc that misstates what the code does is the exact failure mode `d5406d40` and
§2.3's corrected `MapCamera` comment both came from — so in this epic those get fixed, not filed.

---

## 7. Provenance

Opened 2026-07-30 by the maintainer's observation that symbols were barely visible, then sharpened by them to
the decisive form: *"DPR changes the zoom level but not the line width."* That single observation is what
separated a label-sizing question from a whole-map convention defect — the code had been read twice before
without finding it, because on paper the altitude term already divides by DPR and everything looked
consistent.
