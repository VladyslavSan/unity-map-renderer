# Device-pixel ratio — one logical-pixel convention for the whole map (design / SSOT)

This doc is the *why*, including where the ratio comes from (§ 7). The *contract* — the standing
requirements a conforming implementation must hold — is the normative
[`specs/device-pixel-ratio.md`](../specs/device-pixel-ratio.md), and is not restated here.

**Read with:** `docs/line-rendering-design.md` § "The width model" (the frame-constant ruler the line width family rides),
`docs/coordinates-and-projections.md` (the logical-pixel tile definition `WebMercator.TilePixelSize` sets),
and `docs/meshing-design.md` (what the px-valued paint properties reach).

---

## 1. The itch

A style's `px` values are **logical** (CSS) pixels. The device-pixel ratio is the number of physical pixels
per logical pixel, so a `line-width: 2` road is 2 logical px — `2 × dpr` physical px — on every panel, and a
`text-size: 16` label is 16 logical px on every panel.

That is one convention, and one convention is only worth having if it is applied **once**. The failure mode
is not getting a conversion wrong; it is a px-valued property reaching its consumer without meeting the
conversion at all, because the consumer measures the physical framebuffer and nothing in the path says so.
Such a property is silently rebased, and it is invisible at `dpr == 1` — which is every ratio a headless
test runs at, because tests drive `MapView.Wire` rather than `MapHost.Start` and keep the serialized
`MapViewConfig.DevicePixelRatio` of `1.0`. A whole style family can drift onto the wrong side of the
convention and every gate stays green.

So the design problem is **structural, not arithmetic**: make the conversion a single named seam per
direction, and make the space a property lands in something a reader can enumerate.

## 2. The convention

### 2.1 The decision procedure — layout vs sampling

This is the rule to apply when adding any new px-valued quantity. Everything in the table of § "The px-valued
surface" below is an instance of it.

| the quantity answers | unit | examples |
|---|---|---|
| *"how big should this **look**?"* | **logical** | `text-size`, `line-width`, paddings, offsets, collision boxes, translate |
| *"where is the **sample grid**?"* | **device** | the AA straddle pad, the hairline floor, SDF sharpness, `fwidth`-derived ramps |

Design intent is logical; rasterisation is device. A quantity that would mean the same thing on a printed map
is logical. A quantity that exists only because the screen is made of discrete samples is device.

The MapLibre spec appeal alone does not settle a new property, because the spec says a `px` value is a CSS
pixel and says nothing about which framebuffer its consumer measures. Both halves of the table are `px`.

### 2.2 Why placement runs in logical px

Label collision, staging and the collision grid all work in logical px (`SymbolProjectionJob.ViewportLogicalPx`
→ `SymbolStagingMath`). Two questions this raises, because it looks like the wrong space given the screen is
physically made of device pixels.

**Is it correct?** Yes, and identically so. At dpr *d*, the boxes stay in logical px **and the viewport also
converts to logical px** (`MapCamera.ViewportLogicalPx`). Collision is a purely relative test — box against
box, box against viewport — so scaling every term by the same *d* leaves every overlap verdict unchanged.
Placement in logical px and placement in device px agree. Nothing in the placement path compares against a
physical-pixel quantity, which is what makes this safe. A `text-size: 20` label therefore has a
20-logical-px box while painting 40 device px at dpr 2 — one fact in two units, not a discrepancy.

**Then why prefer it?** Because the two spaces only agree *within* one frame. Across devices they do not:
collide in device px and a denser panel gets a **more cluttered map** — same physical size, same apparent
scale, same style, but a 3× panel's viewport is 50 % larger in device px than a 2× panel's, so more labels
clear the test. Whether a map reads as crowded is a question about **apparent** size, and the logical pixel
is the unit of apparent size. A dense panel must make the *same* cartographic decisions as a sparse one and
merely render them more sharply.

## 3. The mechanism — two named conversions, one fallback

`DeviceScaling` holds both directions and nothing else holds either.

- **`LogicalToDevicePx(logicalPx, PixelSpace target, dpr)`** takes a style's px value out to the space its
  consumer measures in. It carries **no default `target`**, so a property routed through it cannot be
  converted without naming its space.
- **`DeviceToLogicalPx(devicePx, dpr)`** takes a physical measurement — a framebuffer size, a cursor or
  finger coordinate — back into the logical basis.

Both divide or multiply by the ratio directly, never by a precomputed reciprocal: at a non-dyadic ratio,
`v × (1/d)` differs from `v / d` in the last bit for a third of all values. A test that only sweeps dpr 1
and 2 cannot see this, because the reciprocal of a power of two is exact.

### 3.1 Why the two directions are not one signature

`DeviceToLogicalPx` takes no `PixelSpace` because its input is a **measurement**, and a measurement in
physical pixels has exactly one meaningful target. The parameter would be a constant at every call site. A
style `px` value is the asymmetric case: its target depends on which consumer reads it. Unifying the
signatures would put a degenerate parameter on one direction to make the two look alike.

### 3.2 The plausibility band, and why a fallback rather than a clamp

A ratio inside `[0.25, 8]` is applied unchanged; anything outside it — including `NaN` and both infinities —
degrades to exactly `1.0`, through a single private predicate shared by both directions.

**The bounds sit far from real hardware.** Android's densest bucket (`xxxhdpi`, 640 dpi) is exactly 4.0 and
its sparsest (`ldpi`, 120 dpi) is 0.75, so nothing a panel legitimately reports lands within a doubling of a
bound. That one fact settles three questions.

* *Why not a floor of 1?* Sub-1 ratios are legitimate — a ~100-dpi desktop panel is 0.625. A floor of 1
  would silently rebase the map on every low-density device instead of rejecting a bad reading.
* *Why not the tighter `[0.5, 4]`?* It would put a real flagship **on** the ceiling, one `<`-vs-`<=` slip
  away from a 4×-too-small map.
* *Why a fallback and not a clamp?* A clamp **invents a plausible-looking value** — a map drawn at 0.25 still
  looks like a map, so the substitution survives inspection. The fallback yields the neutral default every
  test and every eyeball baseline already runs at. It also keeps one behaviour for "unusable" instead of
  splitting the guard's semantics at the bounds. Since the bounds are far from anything real, the objection
  that clamping 0.24 would only move it 4 % never fires on a real value: anything reaching a bound is
  garbage, and garbage degrades to the default.

`NaN` falls back because it fails both comparisons, like any other implausible value — the same rule, not a
special case.

### 3.3 Why the fallback needs one home

An unusable ratio must not reach the paint basis, the camera altitude, the tile-cover framing and the
interaction seams by four different routes, because the failure is not uniform and is not loud.

At a ratio of `0` or `+∞`, `MapCamera.ViewportLogicalPx` goes non-finite. That feeds the label collision
viewport, which then rejects nothing. It also feeds `TileSelectorBinding.BuildConfig`'s
`FramingViewportPx`, which survives the selector's `vp <= 0` early-out, drives `CameraPoseMath.AltitudeForZoom`
to `+∞` and the camera pose to NaN — so **every** frustum-plane comparison is false, `IntersectsAabb` accepts
every tile, while `lodRatio = finite/∞ = 0` means the LOD stop never fires. The consequence is
projection-dependent: the planar cover enumerates the full quadtree to `maxZoom` (a practical hang — depth is
bounded, breadth is not), while the globe path culls everything through `IntersectsSphere` and renders blank.

None of that presents as a bad ratio. The band is therefore defence at the **consumers**, not at the source:
routing the framing through the conversion *is* the guard, and a variant that skipped it would be an
unguarded path authored on purpose. `MapViewConfig.DevicePixelRatio` remains a plain serialized `double` with
no clamp and no `OnValidate`, so any Inspector edit or scene asset can carry `0` into the system — this
reaches the guard without needing any claim about what a platform reports.

### 3.4 Why the interaction seams convert at their own boundary

`Controller` and `TouchController` call `DeviceToLogicalPx` themselves rather than reading
`MapCamera.ViewportLogicalPx`, at the viewport **and** at each cursor or finger position. Three reasons, in
increasing weight:

1. They convert **positions**, and nothing upstream computes or consumes a "logical cursor".
2. Their viewport falls back to `Screen.width`/`height` when the **component's own** serialized camera field
   is null. `MapCamera` wraps a constructor-enforced non-null camera and has no such fallback, so pointing
   them at `ViewportLogicalPx` would delete a live path.
3. Converting where a physical measurement *enters* the system is the correct seam.

What they share with the camera is the definition and the fallback, which is what the single-home rule asks
for. They do not share a viewport.

## 4. The px-valued surface

**The partition is by how a px value reaches world or clip space, not by style family.** It cuts *through*
the label family — `text-size` is logical while `text-halo-width`, on the same material, is device. A
family-shaped rule ("labels logical, lines device") is directionally right and wrong at the boundary, which
is where it matters.

| mechanism | space it lands in | what rides it |
|---|---|---|
| CPU: `px × WebMercator.GroundResolution(zoom)` | **logical** — the camera altitude already divides by dpr | `symbol-spacing`, `fill-pattern` sprite size |
| GPU: `off / _ScreenParamsLogical.xy` | **logical** | `text-size`, icon corner offsets |
| GPU: the frame constant `_MapFrameMetersPerDevicePixel` | **device** | `line-width`, `line-gap-width`, `line-offset`, `line-dasharray` |
| GPU: per-vertex `MapPixelsToWorld`, spanning `_ScreenParams` | **device** | `line-translate` / `fill-translate` / `fill-extrusion-translate`, the AA straddle pad, the hairline floor |
| GPU: an `fwidth`-derived screen scale | **device** | `line-blur`, `text-halo-width` / `text-halo-blur`, the SDF coverage ramp |

The frame constant is the one mechanism whose basis is chosen at the **push site** rather than inherited from
whichever screen reference a shader happens to hold: `MapCamera.SyncToCamera` measures it off the live camera
as `2·|CameraRelativePosition|·tan(fov/2) / ViewportPx.y`. `ViewportPx` is physical and the altitude is framed
from `ViewportLogicalPx`, so the ratio enters exactly once and the two halves cannot name it separately and
disagree.

**Where the conversion is applied.** Material-bound device-px properties go through
`ZoomStyleApplier.BindDevicePixelFloat` / `BindDevicePixelVector`, whose call sites are in `MaterialFactory`.
`text-halo-width` / `text-halo-blur` are the exception and are not material-bound: they ride the per-feature
vertex stream, and `SymbolPlacementSystem` reads the ratio once per Update through the same
`LogicalToDevicePx` and hands it to `WorldSymbolRenderer.Emit`, which widens the emitted halo. Reading it
per Update against the **live** ratio is what lets a dpr change take effect with no re-bake anywhere.

**A device-px binding always queues; it never takes the bind-time constant shortcut.** The ratio is a
per-frame input that can change while the map is live — a window dragged between panels — so a Constant
`line-width` must still be re-pushed. For a **data-driven** width the uniform carries the base only, bound as
a device-px constant of `1`, which makes `_Width == dpr` and the world width `dpr × bakedPx × ruler`. Scaling
`StyledLineTileBuilder`'s per-vertex bake instead would put the ratio inside the geometry, where a live ratio
change cannot reach it and `PreparedTileCache` would serve it stale.

**The collision grid is not a third space.** `SymbolStagingMath` mixes padding with the glyph bounds, the
text size and a `ViewportLogicalPx`-projected anchor in one expression — the same quantities the symbol
shader divides by `_ScreenParamsLogical`. One space, logical.

**`icon-size` is not this ratio.** It is a unitless multiplier on sprite logical size, already divided by the
sprite sheet's own `pixelRatio` in `IconQuadLayout`. Do not conflate the two.

### 4.1 `line-dasharray`, and why a CPU round-trip cannot prove a basis

A dash period in world metres is `(w_logical · dpr) × (metres per device px) × Σ`, and metres-per-device-px
already carries `1/dpr` through the altitude framing — **the ratio cancels**. A Core helper written as
`widthLogicalPx × metresPerLogicalPx × Σ` is therefore correct and mentions no ratio at all, and would
round-trip green at every dpr while the shader sits off by exactly `dpr`.

The same cancellation covers the whole width family, since it rides the same frame constant. So the world
result is dpr-invariant by design, and **no world-space assertion can see a basis error here**. Only a
rendered measurement at dpr ≠ 1, or a direct read of the pushed uniform, can.

### 4.2 The table is the guard, not the compiler

`LogicalToDevicePx`'s missing default means a property routed **through** it must name its space. It does not
mean a new px property cannot be added elsewhere: a raw `Material.SetFloat` or a plain `BindFloat` still
binds a px value with whatever basis its consumer happens to have, which is the path by which a px property
keeps a basis nobody chose. Forcing a `PixelSpace` on *every* binding would be wrong, because unitless
properties have no pixel space.

So the guard is the surface table — this section's, and the normative one in the spec — plus a conformance
row per property. A new px-valued property is added to the table, and the table is what a reviewer checks.

## 5. Invariants

1. **At `dpr == 1` the mechanism is the identity.** Every conversion returns its input and every rendered
   output matches the no-DPR baseline. This is also why `dpr == 1` can never be the only ratio a fixture
   exercises: at the identity the mechanism is structurally invisible, so a suite that runs only there is
   blind to everything DPR can get wrong. It is the cheap check, never the real one.
2. **For every px-valued property, the quantity it controls is linear in dpr when measured in device
   pixels.** This holds for logical-space properties too: `text-size` is not scaled at the seam, yet its
   device footprint doubles at dpr 2 because the logical viewport halves. The stronger-sounding form —
   "every px property is multiplied at the seam" — is **false**, and a conformance row asserting that
   `text-size`, `text-padding` or `icon-padding` is scaled would be wrong.
3. **The whole map scales together, and only an eyeball sees it.** Because every px family shares one
   convention, changing the ratio moves zoom, label size, line width and halo by the same factor. Every
   conformance row measures a *magnitude* — a ratio inside one fixture — and none confirms the *direction*
   is right in a live frame. This is a limitation of the instrument, not a gap to close with another row.
4. **A rendered halo-*softness* ratio cannot be asserted.** The SDF transition denominator is
   `_SdfAaDevicePx + sdfWidenPx.y` — an AA constant in device px summed with the scaled style blur — so the
   rendered ratio at dpr 2 is `(a + 2b)/(a + b)`, not `2`. Such an assertion goes red against a correct
   implementation. A uniform readback is the right form.
5. **The CPU-side px family is correct by its route, and unguarded by anything else.** `symbol-spacing`,
   `text-translate` / `icon-translate` and `text-offset` / `icon-offset` never reach a material as a px
   uniform; they resolve through `WebMercator.TilePixelSize` or `ViewportLogicalPx` and are logical because
   of it. Nothing new is expressible about them: `symbol-spacing`'s extractor takes no ratio, so the only
   assertable property is source text. A future slip into device space there would be invisible at every
   ratio, and the thing that actually protects them is that `TilePixelSize` keeps its meaning
   (§ "Rejected alternatives" below).
6. **The camera's framing ordering is held by argument.** `MapView.LateUpdate` refreshes the camera's ratio
   before it builds the tile-selection config, whose framing viewport *is* `MapCamera.ViewportLogicalPx`.
   `BuildTileSelectionConfig` has a single production caller, nothing between the refresh and the consume
   mutates the ratio, and the ordering is already load-bearing for `SyncToCamera` one line later. No
   conformance row pins it, because one would have to drive a frame with the two steps reordered.

## 6. Rejected alternatives

- **One basis everywhere — emit device px for the whole surface.** Moving the label path off
  `_ScreenParamsLogical` is the identity at dpr 1 and so survives every existing gate, while dragging
  `SymbolProjectionJob.ViewportLogicalPx`, `SymbolBox`, the collision grid's cell sizing and every px
  threshold in the fade machinery into device px. That reproduces the failure mode of § "The itch" at
  roughly ten times the surface, to buy uniformity in a half that is already correct.

- **Fixing the basis inside `MapPixelsToWorld` rather than on the CPU.** The shader's `pxToWorld` must keep
  returning metres per **device** pixel, because the AA straddle pad and the hairline floor derived from it
  are genuine sampling-grid quantities — half a physical pixel is half a physical pixel at any density.
  Scaling them would render a dpr-2 road a physical pixel fatter *and* softer.

- **Folding the ratio into `WebMercator.TilePixelSize`** so the scattered conversions disappear. This is not
  a refactor. `TilePixelSize` is the *definition of the logical pixel* (512 logical px per tile edge), so
  folding dpr in rebases `GroundResolution` from metres-per-**logical**-px to metres-per-**device**-px. That
  is right for the camera altitude and wrong for every other consumer, none of which is paired with a
  physical viewport term. Each breakage below is the identity at dpr 1 and therefore invisible to the whole
  suite:
  * **`symbol-spacing` moves into device space.** `SymbolFeatureExtractor` computes
    `spacing · extent / WebMercator.TilePixelSize`; that halves at dpr 2, so line labels along a road render
    twice as dense on a dense panel while the view is unchanged. A device-space drift in a `px` style
    property is the failure this convention exists to prevent, reintroduced by a change meant to simplify
    it.
  * **`fill-pattern` sprite size halves.** `FillPattern` pairs `GroundResolution` with a member named
    `LogicalSizePixels`. A 32-px sprite would paint 32 *device* px = 16 logical px at dpr 2, against the
    "how big should this *look*?" row of § "The decision procedure — layout vs sampling".
  * **Tile selection's answer changes.** `FrustumTileSelector`'s `_selectionZoomOffset` is
    `round(log2(tilePx / onScreenPx))`; at dpr 2 it becomes 1 and the cover is selected a whole level
    deeper. Exempting that one site immediately reinstates two constants, which is the thing the fold was
    for.
  * **The pixel↔ground service drifts by dpr.** `WebMercatorProjection` uses `GroundResolution(zoom)` as
    metres per **logical** px and is fed an already-converted cursor and viewport by `Controller`. Fold, and
    anchored pan and zoom-to-cursor drift on a dense panel.

  Two further costs. `TilePixelSize` is a `public const double`; a dpr-dependent value cannot be `const`, so
  it becomes a parameter threaded through Core's most-called math surface. And the mechanism does not reach
  the interaction seams at all — they convert a cursor and a finger coordinate, which no zoom-scale constant
  can touch, and both carry a `Screen.width`/`height` null fallback. It removes some conversions and breaks
  four consumers to do it. Scoping the fold to the framing consumers alone removes **none**: it relocates one
  conversion from `MapCamera` into `AltitudeForZoom`.

- **Routing test code's hand-written `ViewportPx / DevicePixelRatio` through `DeviceToLogicalPx`.** A
  fixture that recomputes the logical viewport by hand is the independent recompute a conformance row exists
  to check; sharing the production expression would make it verify itself. The single-home rule is therefore
  scoped to **production** sources, and the hand-written copies in test code stay.

- **Converting `Unity/Style/Line/LineOffset`.** It is a CPU mirror of the shader's offset formula with no
  production caller. Converting it would give a test-only path a production basis and imply a caller that
  does not exist.

- **Threading an initial ratio through `RenderLayerSet.Build`.** It would churn `RenderLayerFactory`'s
  Create/TryCreate overloads and every call site. `MapView.SetStyle` re-applies the ratio immediately after
  the build instead — which it must do anyway: `SetStyle` is async, so its continuation can resume after a
  frame's `LateUpdate` has run, and on a restyle the previous tiles are still loaded. Newly-built layers
  would otherwise draw them once at a seeded ratio of 1, and the symbol halo has no seed at all. A
  doc-comment would not have closed that window.

## 7. Where the ratio comes from

`MapHost.Start` derives the ratio with `DeviceScaling.DevicePixelRatioFor(convention, Screen.dpi)`. The
convention comes from the platform, so the choice is a policy that one pure function holds, not a formula.

The plumbing (§ "The mechanism — two named conversions, one fallback" and § "The px-valued surface"
above) is independent of the value. No test drives `MapHost.Start`, so the gate cannot see the platform
switch or the mode branch. The table-driven test on `DevicePixelRatioFor` pins the policy itself.

### 7.1 The finding that makes it a policy call

**Unity exposes no backing-scale or UI-scaling factor to managed user code, in any player.** There is no
formula that turns `Screen.dpi` into the platform's scale factor on desktop, because the platform's scale
factor is not in the process.

| API | what it actually is |
|---|---|
| `UnityEngine.Screen.dpi` | the only runtime density surface; *"the actual DPI of the screen or physical device"*, `0` when undeterminable. **A density, never a scale factor.** |
| `UnityEngine.DisplayInfo.physicalDpi` | per-display density — the field name says *physical*. `DisplayInfo` has **no** scale field. |
| `UnityEditor.EditorGUIUtility.pixelsPerPoint` | the genuine OS backing scale — **Editor-only**, in no player. |
| `UnityEngine.GUIUtility.pixelsPerPoint` | exists in the shipped player, but its getter is `internal`. Not callable from user code. |
| `UIElements.IPanel.scaledPixelsPerPoint`, `Canvas.scaleFactor` | public at runtime, but each is its own framework's **output**, derived from `PanelSettings.referenceDpi`/`fallbackDpi` or `CanvasScaler` against `Screen.dpi`. Circular. |
| `UnityEngine.Android.AndroidConfiguration.densityDpi` | Android only; exactly `DisplayMetrics.densityDpi`. |
| `QualitySettings.resolutionScalingFixedDPIFactor`, `ScalableBufferManager.*ScaleFactor`, `Camera.scaledPixelWidth`/`Height` | dynamic-resolution and render-scale knobs. **Unrelated** — named so a later reader does not "find" them. |

Unity's own UI faces the identical problem and has nothing better: `CanvasScaler.HandleConstantPhysicalSize`
reads `Screen.dpi`, substitutes a hard-coded fallback when it is 0, and divides by a target DPI. Two reads
follow. Unity's reference density is **96** (the CSS/Windows convention), not 160 (Android's); and uGUI's
*default* mode is `ConstantPixelSize` — Unity's out-of-the-box answer to "what is the density scale?" is **do
not scale**.

### 7.2 What `Screen.dpi` means per platform

| platform | what it returns | is `dpi / 160` the scale factor? |
|---|---|---|
| **Android** | `DisplayMetrics.densityDpi` | **Yes, exactly.** Android defines `density` as a scaling factor that is 1 at 160 dpi and 0.75 at 120 dpi, so `Screen.dpi / 160 == DisplayMetrics.density`. |
| **iOS** | a hardware-id → PPI table Unity ships (no public iOS API reports physical PPI) | approximates `nativeScale` without equalling it: 326/160 = 2.04 against 2; 460/160 = 2.875 against 3 |
| **macOS** | the panel's physical density | **No, and no divisor fixes it.** The backing scale is two-valued (1 or 2), so `÷160` would need a 320-dpi reading to reach 2.0. A 254-ppi panel yields 1.5875, a map ~21 % thinner than a browser renders the same style at `window.devicePixelRatio` 2.0. A threshold does recover it (§ 7.3). |
| **Windows** | not established from a primary source — either the EDID physical density (then `÷160` ignores the user's 125/150/175 % setting) or the effective system DPI `96 × scale` (then `÷160` yields **0.6 at 100 %**). **Both wrong, in opposite directions.** |
| **WebGL** | the browser's ratio **is** in the process natively, but no managed API surfaces it, and the web's reference density is **96**, not 160 |

The mismatch is **structural, not a bad constant**: no divisor makes a physical density track a two-valued OS
scale. A threshold can, on Apple screens only, because their two density classes are far apart.

### 7.3 The policy

| convention | platforms | ratio |
|---|---|---|
| Android | Android | `dpi / 160`. Android defines its density the same way (§ 7.2). |
| iOS | iPhone, iPad | `max(1, round(dpi / 160))`. |
| MacOS | macOS players and the macOS Editor | `2.0` when `dpi >= 150`, else `1.0`. |
| Desktop | Windows and Linux players and Editors, WebGL, tvOS, visionOS | `1.0`. |

A density that is not positive (`Screen.dpi` is `0` when undeterminable) gives `1.0` for every convention.
`SafeRatio` is the only other fallback (§ 3.2).

The iOS rounding is a heuristic. It recovers the true `nativeScale` for current devices (326 → 2, 460 → 3,
iPad 264 → 2). A 401-ppi panel with a `nativeScale` of 2.608 gives 2.506 and rounds to 3. At exactly 2.5 the
round is half-to-even, so 400 dpi gives 2.

The macOS backing scale is 1 or 2. Apple non-Retina panels are at most about 110 ppi and Retina panels at
least about 218 ppi, so a threshold of 150 recovers the scale. **Limitation:** a third-party monitor near
150 dpi sits on the edge of the threshold, and `Manual` mode is the answer there.

Desktop is `1.0` because no divisor recovers a two-valued OS scale from a physical density (§ 7.2), and no
threshold separates the density classes of Windows and Linux monitors. In the Editor, `Screen.dpi` reports
the density of the monitor under the Editor window, not the target device's (observed, not documented by
Unity). The macOS Editor therefore gets the macOS convention, and the Windows and Linux Editors get the
desktop convention. The host reads `UnityEngine.Device.Screen` and `UnityEngine.Device.Application`: the Device
Simulator reports the simulated platform and device, and in a player they forward to the real classes.
**Limitation:** WebGL gets `1.0`, because the browser ratio is in the process natively but no managed API
surfaces it.

### 7.4 Auto and Manual

`MapViewConfig.DevicePixelRatioMode` is `Auto` or `Manual`. `Auto` runs the policy once in `Start` and
overwrites the serialized ratio. `Manual` keeps the serialized value. Manual is the way to a value the
policy does not give, such as `2.0` on a Windows or Linux HiDPI monitor. On macOS, log `Screen.width` once in
a standalone build to check the policy: `ProjectSettings.asset` has `macRetinaSupport: 1`, so 3456 means the
framebuffer is device pixels and 2.0 is owed, and 1728 means it is points and 1.0 is right. A constant 2.0 is
still wrong on a 1x monitor.

**Limitation:** the platform switch and the mode branch run only in `Start`, and no test drives them.

### 7.5 Rejected alternatives

* **Desktop → 1.0 on macOS too:** makes the whole map half size on a Retina Mac, the default Apple screen.
* **Editor-only truth (`EditorGUIUtility.pixelsPerPoint`):** the Editor and the player would disagree, so
  the eyeball would validate behaviour the build lacks.
* **A native plugin (`backingScaleFactor`, `GetDpiForWindow`):** the only way to the real desktop factor
  in a player. It costs per-platform plugins and build config, and is out of proportion until desktop
  ratio matters.
* **A standalone `Application.isEditor` gate:** adds a third policy branch, and the `Device` classes give
  the same effect with no policy attached.

## 8. Grounding (file:symbol touch points)

`MapRenderer.Unity/View/`: `DeviceScaling` (both conversions, `DevicePixelRatioFor` and `DensityConvention`,
`PixelSpace`, the plausibility band and its single fallback), `FrustumTileSelector` (the selection zoom
offset the `TilePixelSize` fold would move).
`MapRenderer.Unity/Rendering/Map/`: `MapCamera.ViewportLogicalPx` (the logical-viewport definition),
`MapCamera.MetresPerDevicePixel` / `SyncToCamera` (the frame-constant ruler and its push),
`MapCamera.CurrentAltitudeMetres` (where the ratio enters the framing, once),
`TileSelectorBinding.BuildConfig` (the cover framing), `MapView.SetStyle` (the post-build re-apply),
`MapViewConfig.DevicePixelRatio` (the serialized ratio, unclamped at the field), `MapViewConfig.DevicePixelRatioMode`.
`MapRenderer.Unity/Rendering/Layers/`: `ZoomStyleApplier.BindDevicePixelFloat` / `BindDevicePixelVector` (the
material-bound seam), `SymbolRenderLayer` (colour tints only — the halo is not bound here).
`MapRenderer.Unity/Rendering/Materials/MaterialFactory` — the device-px binding call sites.
`MapRenderer.Unity/Text/Placement/`: `SymbolPlacementSystem` (the per-Update halo ratio read),
`WorldSymbolRenderer.Emit` (where the halo widens).
`MapRenderer.Unity/Text/SymbolFeatureExtractor` — `symbol-spacing` in tile units.
`MapRenderer.Unity/Shaders/Map/`: `Line/Line_VertexExtrude.hlsl` (`_MapFrameMetersPerDevicePixel`, the AA
straddle pad, the hairline floor, `_Blur`), `Fill/Fill_VertexModify.hlsl` and `PixelsToWorld.hlsl`
(`MapPixelsToWorld`), `Symbol/Text/SymbolTextWorld_ForwardPass.hlsl` (the SDF coverage ramp and halo widen),
`Symbol/Text/SymbolText_Input.hlsl` (`_ScreenParamsLogical` versus `_SdfAaDevicePx`).
`MapRenderer.Unity/Jobs/Symbols/SymbolProjectionJob` — the logical-px placement input.
`MapRenderer.Unity/Style/Fill/FillPattern.LogicalSizePixels`.
`MapRenderer.Core/`: `Coordinates/WebMercator.TilePixelSize` / `GroundResolution` (the logical-pixel
definition), `Coordinates/WebMercatorProjection` (the pixel↔ground service),
`Text/Placement/SymbolStagingMath` / `SymbolBox` / `SymbolViewTransform` (the collision space).
`MapRenderer.App/`: `MapHost.Start` (the derivation, § 7),
`Controller` / `TouchController` (the interaction seams).
