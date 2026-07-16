# Camera-driven property re-evaluation (smooth, complete) — design / SSOT

**Status: design stub, not started — needs a big audit first.** Captures the goal + the (partial) current
state; the full design is TBD and deliberately large. Closely connected to **style switching**
(`docs/style-switching-design.md`) — both are about *changing rendered properties smoothly over time*, and
should share the easing/animation machinery.

## The goal

As the camera parameters change — **zoom** primarily, but also **pitch / bearing / center** where a property
depends on them — every style property should be **re-evaluated correctly, completely, and smoothly**
(animated/eased where appropriate), not baked once and left stale, and not stepped/popped. This is the MapLibre
behaviour: zoom-dependent paint/layout interpolate continuously as you zoom.

## Current state — partial, zoom-only, un-eased

**Re-evaluates per frame today** (`MapView.LateUpdate` → `Layers.ApplyZoom(cameraProperties.Zoom)`,
`MapView.cs:333` → `RenderLayerSet.ApplyZoom` → each `IRenderLayer.ApplyZoom` → `ZoomStyleApplier`, an
alloc-free hot path evaluating only the Zoom-kind bindings):
- **Fill** — zoom-expression paint (color/opacity) → material uniforms.
- **Line** — zoom-expression paint + **line width** + **dasharray** re-evaluated in `ApplyZoom`.
- **Background** — zoom-expression color/opacity.

**Does NOT re-evaluate (the gaps):**
- **Symbols** — `SymbolRenderLayer.ApplyZoom(zoom) { }` is a **no-op** (`:107`, "zoom-expression halo is a
  documented follow-up"). Symbol layout/paint (text-size etc.) is evaluated **at build time** (see the tracked
  A4 zoom follow-up in `docs/per-layer-tile-processing-design.md` — symbols currently bake at the camera zoom
  captured at build start; the interim direction is to bake at *tile* zoom). Either way, nothing re-evaluates
  as the camera zooms.
- **Data-driven / per-feature properties** (feature-property expressions → per-vertex color, etc.) are
  evaluated at **build time** in `StyledFillTileBuilder` / `StyledLineTileBuilder` and **baked into the mesh** —
  they don't re-evaluate on camera change (re-evaluating them means rebuilding geometry).
- **Camera params other than zoom** — only `Zoom` drives `ApplyZoom`. Pitch/bearing/center-dependent
  properties (e.g. pitch-scaled sizes) are not re-evaluated on those changes.
- **No easing / animation** — re-evaluation is **instantaneous** per frame; there is no smooth transition of a
  property from its old value to its new one.

## The "big check" (the audit this needs before a design)

1. **Inventory every style property** by *what it depends on* (zoom / pitch / bearing / center / feature-data /
   constant) and *where it currently lives* (per-frame material uniform re-eval / baked into geometry at build /
   baked at symbol build / not evaluated at all). Find every gap against MapLibre's continuous model.
2. **Decide the re-evaluation tier per property:** cheap uniform re-eval per frame (already the fill/line/bg
   path) vs needs-geometry-rebuild (data-driven baked properties — when, if ever, do these re-eval on zoom?)
   vs symbol re-eval (the currently-no-op path).
3. **Symbols** — the biggest gap: make symbol properties re-evaluate on zoom (superseding the interim
   "bake at tile zoom" follow-up), which interacts with the label build/cache lifecycle.
4. **Beyond zoom** — which properties legitimately depend on pitch/bearing/center, and drive `ApplyZoom`-like
   passes from those camera changes too.
5. **Smoothness / animation** — ease property changes over a short duration instead of popping; this is the
   **shared machinery with style switching** (§4 there). Decide where easing lives (a per-property animator over
   the applier layer?) and how it composes with continuous zoom interpolation (which is already "smooth" in the
   sense of continuous, but not *eased* across a discrete change).

## Grounding (touch points)

`MapView.cs:330-333` (the per-frame `ApplyZoom` drive); `RenderLayerSet.ApplyZoom`; `ZoomStyleApplier`
(the Zoom-kind binding evaluator, alloc-free); `IRenderLayer.ApplyZoom`; `FillRenderLayer` / `LineRenderLayer`
(width + dasharray re-eval) / `BackgroundRenderLayer` / `SymbolRenderLayer` (`:107` no-op);
`StyledFillTileBuilder` / `StyledLineTileBuilder` (build-time data-driven evaluation baked into geometry);
`MapRenderer.Core` expression/`EvaluationContext` layer.

## Relationship to other work

- **Style switching** (`docs/style-switching-design.md`) — shares the easing/animation machinery; a compatible
  restyle and a camera-zoom change are two triggers for the *same* "smoothly move properties to new values"
  mechanism. Design them together.
- **A4 zoom follow-up** (`docs/per-layer-tile-processing-design.md`) — "symbols bake at tile zoom" is an
  interim determinism fix; this topic's end state (continuous symbol re-eval) eventually **supersedes** it.
- **Symbol projection support** (`docs/labels-and-symbols-design.md` §4) — orthogonal (that's *where* a
  label projects; this is *what value* its properties take), but both touch the symbol build/apply lifecycle.
