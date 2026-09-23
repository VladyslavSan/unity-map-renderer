# SPEC-DPR — Device Pixel Ratio

**Status:** normative. This spec is authoritative for the DPR *contract*; a live-production disagreement is
a bug (§10 lists the deliberate exceptions). The *why* — decisions, rejected alternatives, stage history —
lives in [`docs/device-pixel-ratio-design.md`](../docs/device-pixel-ratio-design.md) and is not restated
here. Requirement language is RFC 2119 (`MUST` / `MUST NOT` / `SHOULD` / `MAY`).

**Scope.** This spec governs what the renderer does with a device-pixel ratio: how a style's `px` values
reach the screen, and how physical measurements re-enter the map's logical basis. It does **not** govern
where the ratio *comes from* — the derivation from platform density is unspecified (§9).

---

## 1. Definitions

- **logical pixel** — a CSS pixel; the unit a MapLibre style authors in. A quantity that would mean the
  same thing on a printed map ("how big should this *look*?") is logical.
- **physical / device pixel** — a framebuffer sample. A quantity that exists only because the screen is
  made of discrete samples ("where is the *sample grid*?") is device.
- **device-pixel ratio (`dpr`)** — physical pixels per logical pixel; a pure number. `dpr = 2` means a
  logical pixel is backed by two physical pixels on a side.
- **the seam** — the single conversion boundary a px value crosses on its way to a consumer (§4).

---

## 2. The convention

- **R-1.** A style's `px` values are **logical** pixels. A `line-width: 2` road is 2 logical px — `2 × dpr`
  physical px — on every panel; a `text-size: 16` label is 16 logical px on every panel.
- **R-2.** `dpr` is physical pixels per logical pixel. It is a per-frame input and MAY change while the map
  is live (a window dragged between panels of different density).
- **R-3.** DPR MUST be applied **exactly once per direction**, at a single conversion seam (§4). A live
  production px value MUST NOT be scaled by `dpr` at its individual call site; adding a new px-valued
  property MUST NOT require remembering to divide or multiply. *(Rationale: design § "The itch".)*

> **Observable consequence (not separately testable):** because every px family shares one convention, a
> change in `dpr` scales the *whole map together* — zoom level, label size, line width and halo all move by
> the same factor. Discharged by eyeball, not by a tooth (design § "Invariants").

---

## 3. Classifying a px-valued quantity

- **R-4.** Every px-valued quantity MUST be classified by the **layout-vs-sampling** rule, and its consumer
  measured in the corresponding space:

  | the quantity answers | consumer space | examples |
  |---|---|---|
  | *"how big should this **look**?"* | **logical** | `text-size`, `line-width`, paddings, offsets, collision boxes, translate |
  | *"where is the **sample grid**?"* | **device** | AA straddle pad, hairline floor, SDF sharpness, `fwidth`-derived ramps |

  Design intent is logical; rasterisation is device. *(Rationale + why placement is density-independent:
  design § "The decision procedure — layout vs sampling" and § "Why placement runs in logical px". Read it
  before classifying a new property — the MapLibre spec alone will not settle it.)*

- **R-5.** A newly-added px-valued property MUST be classified per R-4 and recorded in §6's surface table.
  The table — not the compiler — is the guard: a property can still be bound through a non-DPR path and
  silently keep the wrong basis (this is how `line-width` once drifted). A reviewer checks the table.

---

## 4. The conversion interface

Two directions, deliberately **not** unified into one signature (design § "Why the two directions are not
one signature", `DeviceScaling` docs).

- **R-6.** The logical→device conversion (a style `px` value out to its consumer's space) MUST be
  parameterized by the consumer's **target space**: identity for a logical consumer, `× dpr` for a device
  consumer. It carries no default — a property routed through it cannot be bound without declaring its
  space.
- **R-7.** The device→logical conversion (a physical **measurement** — framebuffer size, cursor or finger
  coordinate — back into the logical basis) MUST take **no** target space: a physical measurement has
  exactly one meaningful target (logical). Every site that takes a physical measurement into the logical
  basis — the camera framing viewport, the tile-selector framing viewport, the mouse and touch seams —
  MUST route through it.
- **R-8.** Both conversions MUST use **division / multiplication by the ratio directly**, never
  multiplication by a precomputed reciprocal: at a non-dyadic `dpr`, `v × (1/d)` differs from `v / d` in
  the last bit for a third of all values.

---

## 5. Density independence of placement

- **R-9.** Label collision, staging and the collision grid MUST run in **logical** px, against a viewport
  also converted to logical px. A conforming implementation MUST NOT compare any placement quantity against
  a **physical**-pixel value.

  *(Placement is a purely relative test — box against box, box against viewport — so a logical basis and a
  device basis agree within a frame; but only the logical basis makes a dense panel render the* same *map
  rather than a more cluttered one. Rationale: design § "Why placement runs in logical px".)*

---

## 6. The px-valued property surface

Authoritative classification of every px-valued style property, verified against source. A new px property
MUST be added here (R-5). The classification cross-cuts the mechanism: a property is **device**-space only
if its consumer measures the physical framebuffer, and that is exactly the set that crosses the seam. Every
other px property is logical, and reaches its consumer *without* a conversion — either as a material uniform
the shader divides by the logical viewport, or baked CPU-side into geometry/placement.

**Device-space — crosses the seam (`× dpr`).** Exactly eight, all bound through the DPR seam (the
`BindDevicePixelFloat`/`BindDevicePixelVector` call sites in `MaterialFactory` and `SymbolRenderLayer.BindTextPaint`):

| property | consumer measures | at the seam |
|---|---|---|
| `line-width` | physical framebuffer | `× dpr` |
| `line-gap-width` | physical framebuffer | `× dpr` |
| `line-offset` | physical framebuffer | `× dpr` |
| `line-blur` | physical framebuffer | `× dpr` |
| `line-translate` / `fill-translate` | physical framebuffer | `× dpr` |
| `text-halo-width` / `text-halo-blur` | physical framebuffer | `× dpr` |

**Logical-space — never crosses the seam; factor 1.** These never route through any `LogicalToDevicePx`
call. `text-size`, `text-padding` and `icon-padding` are evaluated CPU-side (`SymbolFeatureExtractor`) and
collided/consumed against the logical viewport (`MapCamera.ViewportLogicalPx`, via `LabelStagingMath` /
`SymbolProjectionJob`) — the same logical space the glyph bounds live in. `symbol-spacing`, `text-translate`
/ `icon-translate`, `text-offset` / `icon-offset` resolve through `WebMercator.TilePixelSize` /
`ViewportLogicalPx`. None reaches a material as a px uniform.

- **`line-dasharray`** — device in both halves (the width `× dpr`, the per-device-pixel ruler `÷ dpr`), so
  the ratio **cancels** in the world-space period. Its conformance is a **uniform readback**, not a
  world-period assertion — a CPU round-trip cannot prove the basis (design
  § "`line-dasharray`, and why a CPU round-trip cannot prove a basis").
- `icon-size` is a **unitless multiplier** on sprite logical size, divided by the *sprite sheet's own*
  `pixelRatio` — a different ratio. It MUST NOT be conflated with `dpr`.

---

## 7. The plausibility band

- **R-10.** A `dpr` inside the band `[0.25, 8]` (inclusive) MUST be applied unchanged, including sub-1
  ratios (a ~100-dpi desktop panel is legitimately 0.625). The bounds sit far from real hardware (Android's
  densest bucket is exactly 4.0, sparsest 0.75), so nothing a panel legitimately reports lands near a
  bound.
- **R-11.** A `dpr` outside the band — including `NaN` and `±∞` — MUST fall back to **exactly `1.0`**. This
  is a **fallback to the neutral default, not a clamp to the nearest bound**: a clamp would invent a
  plausible-looking `0.25` or `8.0`, invisible to inspection, whereas the fallback yields the documented
  baseline every test and the eyeball run at.
- **R-12.** The out-of-band fallback MUST have a **single definition**, shared by both conversion
  directions (R-6, R-7), so the paint basis, the camera framing, the tile-cover framing and the interaction
  seams cannot disagree about an unusable ratio.

---

## 8. Invariants

- **R-13.** At `dpr == 1`, the mechanism is the **identity**: every conversion returns its input and every
  rendered output is byte-identical to the no-DPR baseline. *(This is why `dpr == 1` cannot be the only
  ratio a test exercises — see conformance.)*
- **R-14.** For every px-valued property, the quantity it controls MUST be **linear in `dpr`** when measured
  in **device pixels**. This holds for logical-space properties too: `text-size` is not scaled at the seam,
  yet its device footprint doubles at `dpr 2` because the logical viewport halves. A conformance tooth MUST
  NOT assert that a logical-space property (e.g. `text-padding`) is *multiplied at the seam* — that would be
  false (design § "Invariants").

---

## 9. Unspecified

- **The derivation of `dpr` from the platform is unspecified** and awaits a maintainer decision
  (design § "Open — where the ratio comes from"). Unity exposes no backing-scale factor to managed player
  code, so `Screen.dpi / 160` is correct on Android, an approximation on iOS, and structurally wrong on desktop (the OS scale factor is
  two-valued; no divisor recovers it). Consequently the reference density (`160`) and the
  `dpi → ratio` formula are **not** part of this contract; a conforming renderer MAY derive `dpr` by any
  means and this spec governs only what it does with the result.

---

## 10. Known non-conformance

Deliberate, recorded deviations in the live tree. Each is intentional; none is a licence to add more.

| site | deviation | why it stands |
|---|---|---|
| `MapTelemetryTests.cs` (PlayMode assembly) | a hand-written `ViewportPx / DevicePixelRatio`, bypassing R-7's single conversion (the EditMode file of the same name does not) | it is the **independent recompute a tooth exists to check**; routing it through the production conversion would make the test verify itself (design § "Rejected alternatives"). Test code, not a production path. |
| `Core/Style/Line/LineOffset.cs` | a CPU mirror of the offset formula, un-converted | **zero production callers** (test-only); belongs to the separate test-only-production-code cleanup, not the DPR seam (design § "Rejected alternatives"). |
| R-3, R-9 invariants held by argument | some are pinned by reasoning, not a tooth (design § "Invariants") | recorded so the gap is visible; each is safe by construction and its argument is stated in the design doc. |

---

## 11. Conformance

Each requirement and the tooth that pins it. Tooth names are in the test assemblies.

| requirement | pinned by |
|---|---|
| R-1, R-3, R-14 | `DevicePixelRatioSnapshotTests` (rendered ratios: line width and label size scale together across dpr); `DevicePixelRatioBindingTests` (uniform readbacks, table-driven) |
| R-6, R-7, R-8 | `DevicePixelRatioBindingTests`; `DeviceScaling` division-exactness sweep (`ViewMathTests.DeviceToLogicalPx_IsExactDivision_NotReciprocalMultiplication`, tolerance 0, witnessed at a non-dyadic ratio) |
| R-9 | placement runs against `ViewportLogicalPx`; the collision-grid tests |
| R-10, R-11 | the plausibility-band sweep (`ViewMathTests.ImplausibleDpr_FallsBackToExactlyOne_InBOTHDirections` / `ViewMathTests.PlausibleDpr_PassesThroughUnchanged_AndTheBandIsInclusiveAtBothBounds`): out-of-band incl. `NaN`/`±∞` → exactly `1.0`; in-band incl. sub-1 → bit-identical; bounds bracketed. RED-verified against both an un-widened guard and an injected **clamp**. |
| R-12 | `DevicePixelRatioFramingTests.RatioFallback_HasExactlyOneHome_InDeviceScaling` (structural: sweeps production sources, requires one match) |
| R-13 | the whole pre-DPR suite is byte-identical at `dpr 1` — the cheap check, structurally blind to everything DPR can get wrong, so never the only one |

**Conformance caveats.**

- A rendered-ratio tooth (R-1/R-14) MUST keep an `IsAllBlack → Inconclusive` no-GPU guard, and a run MUST
  be checked for Inconclusive before a green gate is believed.
- **Do not** write a rendered halo-*softness* ratio tooth: the shader's transition denominator is
  `_SdfSoftness + _HaloBlurPx` (an AA constant in device px plus the scaled style blur), so the rendered
  ratio at `dpr 2` is `(s + 2b)/(s + b)`, not `2` — such a tooth would go RED against a correct
  implementation. The uniform readback is the right form (design § "Invariants").
