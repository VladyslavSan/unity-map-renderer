# Device-pixel ratio — one logical-pixel convention for the whole map

**Status:** designed, not started. SSOT for the DPR epic.
**Symptom that opened it:** symbols look "barely visible"; sliding `DevicePixelRatio` live changes the zoom
level and the label sizes, but **road widths do not move**.

---

## 1. What DPR is supposed to mean

A style's `px` values are **logical** (CSS) pixels. The device-pixel ratio is the number of physical pixels
per logical pixel, so a `line-width: 2` road is 2 logical px — `2 × dpr` physical px — on every panel, and a
`text-size: 16` label is 16 logical px on every panel. **One convention, applied once, inherited everywhere.**

Today it is not one convention. It is a division performed at five call sites, and one whole family of style
properties never meets it at all.

---

## 2. Verified facts

Every claim here was read out of the tree, not assumed.

### 2.1 Where DPR comes from

| | |
|---|---|
| `MapViewConfig.DevicePixelRatio` | serialized default `1.0` (`MapViewConfig.cs:28`) |
| Runtime derivation | `Bootstrapper.cs:133` — `DeviceScaling.DevicePixelRatioFromDpi(Screen.dpi)` |
| The formula | `screenDpi / 160.0` (`DeviceScaling.cs:33`), 160 = Android **mdpi** baseline |
| Measured on the maintainer's machine | **1.56** (⇒ `Screen.dpi ≈ 250`) |
| In tests | **always `1.0`** — tests drive `Wire`, not `Start`, and keep the serialized value |

### 2.2 The five places DPR is divided in

| site | what it normalises |
|---|---|
| `MapCamera.cs:132` | camera **altitude** — `AltitudeForZoom(zoom, ViewportPx.y / dpr, fov)` |
| `MapCamera.cs:99` | `ViewportLogicalPx` — the label/coverage basis |
| `MapView.cs:516` | `FramingViewportPx` — tile cover selection |
| `Controller.cs:125,153` | interaction viewport + cursor |
| `TouchController.cs:143,176` | touch viewport + positions |

### 2.3 The two pixel bases (this is the defect)

| family | basis | scales with DPR? |
|---|---|---|
| `text-size`, label quads, halos, paddings | **logical** px — shader divides by `ViewportLogicalPx` | **yes** |
| `line-width` and the whole line family | **device** px — `_Width` with `_WidthIsPixels = 1` | **NO** |
| world geometry (fills, ribbons' geometry) | via camera altitude ÷ dpr | yes |

`DevicePixelRatio` appears **nowhere** in `MapRenderer.Unity/Shaders`, `MapRenderer.Core/Style/Line` or
`MapRenderer.Jobs` — zero occurrences. `MaterialFactory.cs:193` sets `WidthIsPixels = 1f` and the evaluated
`line-width` is handed to `_Width` raw.

**Consequence, exactly as observed:** raising DPR lowers the altitude (map zooms in) and enlarges labels
(logical basis), while every line keeps its literal screen width. Labels and roads drift apart.

### 2.4 The px-valued style surface (8 properties)

`line-width`, `line-gap-width`, `line-offset`, `line-blur`, `text-size`, `text-halo-width`, `text-padding`,
`icon-padding`. Also `*-translate` (px) and `icon-size` (a unitless multiplier on sprite logical size, which
is already divided by the sprite's own `pixelRatio` in `IconQuadLayout` — a *different* ratio; do not conflate
the two).

### 2.5 Why 1842 tests never caught it

**Every test runs at `DevicePixelRatio = 1.0`, where all five divisions are the identity.** The DPR ≠ 1
behaviour — the only one that ever runs on a real device — is untested. That is the root cause of the class,
not just of this instance.

---

## 3. Decisions

**D1 — logical px is the canonical basis; lines are wrong, labels are right.** MapLibre defines every `px`
style value as a CSS pixel. Do not "fix" labels down to device px to match lines.

**D2 — DPR is applied ONCE, at the px→device conversion, not per call site.** The current shape (five
divisions) is what allowed one family to be missed silently. There must be a single named conversion that the
whole px family flows through; adding a new px-valued property must not require remembering to divide.

**D3 — at `dpr == 1` the change is the IDENTITY.** This is the stage invariant and it is falsifiable: every
existing golden and all 1842 tests must stay byte-identical, because that is the DPR every test runs at. A
moved golden means the conversion is wrong, not that the golden needs re-baking.

**D4 — `Screen.dpi / 160` is a separate, unresolved question.** The mdpi baseline is an Android convention;
on desktop `Screen.dpi` is not a backing-scale factor and 1.56 may not be the ratio the platform intends.
Fixing the *plumbing* (D1–D3) is independent of choosing the right *value*, and must land first — otherwise a
value change silently rescales the map with nothing pinning what "correct" means. **Deliberately deferred to
its own stage.**

**D5 — expect a visible change.** At the maintainer's 1.56, correcting the line family makes roads ~1.56×
wider. That is the correct MapLibre appearance at that density, not a regression. Text does not move.

---

## 4. Staged plan

### Stage 1 — one DPR-aware px→device conversion (the fix)
Route the 8 px-valued properties through a single conversion so `line-*` shares the basis `text-*` already
uses. Prefer widening the existing logical-px seam over adding a parallel one.
*Invariant:* byte-identical at `dpr == 1` — all 1842 green, no golden moves.

### Stage 2 — the tooth that would have caught this
Render the same scene at `dpr = 1` and `dpr = 2` and assert a ground feature's on-screen span and a rendered
line's width scale by the **same factor**. Nothing in the suite asserts this today.
*This must run at `dpr ≠ 1`* — a DPR test at 1.0 is vacuous, which is precisely how the bug survived.
Ships with Stage 1; the fix is meaningless unpinned.

### Stage 3 — collapse DPR into the logical-pixel definition
Fold the ratio into the definition that feeds `MetersPerPixel` (`WebMercator.TilePixelSize = 512.0`,
`WebMercator.cs:41`) so the five scattered `÷ dpr` sites disappear and downstream code inherits the basis.
Touches camera framing and the interaction seam — its own stage, its own review.

### Stage 4 — revisit `Screen.dpi / 160` (D4)
Decide the right desktop/mobile derivation once Stages 1–3 make "correct" observable.

---

## 5. Teeth

| | |
|---|---|
| T1 | at `dpr == 1`, every existing test and golden is byte-identical (the Stage-1 invariant) |
| T2 | at `dpr = 1` vs `2`, a line's rendered width and a ground feature's on-screen span scale by the same factor |
| T3 | a label's on-screen size and a line's rendered width scale by the same factor across DPR — the symptom, pinned |
| T4 | each of the 8 px properties scales with DPR (table-driven; a newly-added px property that skips the conversion fails) |

T2/T3 are RED against today's tree by construction: lines do not move at all.

---

## 6. Deferred / fenced

* `Screen.dpi / 160` correctness (D4, Stage 4).
* `icon-size` and the sprite sheet's own `pixelRatio` — a different ratio, already handled in
  `IconQuadLayout`; not in scope, do not conflate with DPR.
* Whether tile *selection* should use logical or physical px is settled today (logical) and is not reopened
  here; Stage 3 only removes the duplication, it does not change the answer.

---

## 7. Provenance

Opened 2026-07-30 by the maintainer's observation that symbols were barely visible, then sharpened by them to
the decisive form: *"DPR changes the zoom level but not the line width."* That single observation is what
separated a label-sizing question from a whole-map convention defect — the code had been read twice before
without finding it, because on paper the altitude term already divides by DPR and everything looked
consistent.
