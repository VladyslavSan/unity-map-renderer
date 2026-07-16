# Smooth transitions — design & SSOT

**Status: design stubs, not started.** Two epics that change **rendered properties smoothly over time** and
deliberately **share one easing/animation mechanism**: (1) **style switching** — transition between compatible
styles instead of tearing down and rebuilding; (2) **camera-driven property re-evaluation** — re-evaluate every
style property correctly and continuously as the camera changes. A compatible restyle and a camera-zoom change
are two triggers for the *same* "smoothly move properties to new values" machinery — design them together.

1. **[Style switching](#1-style-switching)** — smooth transitions between compatible styles.
2. **[Camera-driven property re-evaluation](#2-camera-driven-property-re-evaluation)** — continuous, complete
   re-eval on camera change.
3. **[Shared easing machinery](#3-shared-easing-machinery)** — the common mechanism both epics need.

---

# 1. Style switching

## The goal

Switching from style A to style B should be **smooth**: when the two styles are *compatible*, transition
between them rather than tear everything down and rebuild — no blank frame, warm tiles/caches reused, and
(ideally) paint changes eased rather than popped. A full teardown+rebuild stays the fallback for *incompatible*
changes (different sources, different source-layers, structural layer changes).

## Current state (why it's not smooth today)

`SetStyle` is **destructive and non-transactional**:

- `RenderLayerSet.ClearLayers` destroys **every** old layer's material before the new set is built, so there is
  a window with no render layers.
- `MapView.SetStyle` drives `Layers.Build` / `_symbols.SetStyle` / `TileManager.SetSources` in sequence; a
  throw *during* that commit leaves a **partially-mutated** `RenderLayerSet` — no rollback. This affects
  fill/line/symbol identically.
- Warm reuse is **partial**: `TileManager.SetSources` already keeps a source's pipeline (warm
  `TileScheduler`/cache) when its `SourceKey` matches, so unchanged sources don't re-fetch. But the render
  layers, materials, and label state are rebuilt wholesale regardless.
- There is **no cross-style diffing** — nothing detects "these two styles differ only in paint, keep the
  geometry" or "only layer X changed."

## Sub-topics (each likely its own stage)

1. **Transactional atomicity (the known first piece).** Build the new layer/backend/label state *off-side*,
   then **atomically swap**; a throw mid-build rolls back cleanly, leaving the old style fully intact. Removes
   the partial-mutation gap and the blank window. The smallest, most self-contained slice and a natural first
   stage — it makes *any* restyle safe before we make *compatible* restyles smooth. (Filed out of the tile-
   pipeline unification epic's merge step; a pre-existing gap fill/line already share.)
2. **Compatibility detection.** A diff over (style A, style B) classifying the change: paint-only (same
   geometry, re-bind materials), layer add/remove/reorder (patch the `RenderLayerSet`), source change (needs
   re-fetch), structural (full rebuild). Decides which transition path applies.
3. **Warm reuse beyond sources.** Extend today's `SourceKey` warm-cache reuse to render layers / prepared
   meshes / labels when the diff says the geometry is unchanged — so a paint-only restyle keeps every tile mesh
   and only re-applies materials.
4. **Smooth visual transition.** For compatible changes, ease paint properties (color/opacity/width) from A to B
   over a short duration, or crossfade — instead of popping. Uses the shared machinery (§3).
5. **In-flight preservation.** Don't cancel in-flight tile fetches / label builds that are still valid under the
   new style.

## Grounding (touch points)

`MapView.SetStyle`; `RenderLayerSet.ClearLayers` / `RenderLayerSet.Build`; `RenderLayerFactory`;
`TileManager.SetSources` (the `SourceKey` warm-reuse — the one place partial warm reuse already lives);
`SymbolLabelSubsystem.SetStyle`; the material appliers (`ZoomStyleApplier`, `MapMaterialSet`).

---

# 2. Camera-driven property re-evaluation

## The goal

As the camera parameters change — **zoom** primarily, but also **pitch / bearing / center** where a property
depends on them — every style property should be **re-evaluated correctly, completely, and smoothly**
(animated/eased where appropriate), not baked once and left stale, and not stepped/popped. This is the MapLibre
behaviour: zoom-dependent paint/layout interpolate continuously as you zoom.

## Current state — partial, zoom-only, un-eased

**Re-evaluates per frame today** (`MapView.LateUpdate` → `Layers.ApplyZoom(cameraProperties.Zoom)` →
`RenderLayerSet.ApplyZoom` → each `IRenderLayer.ApplyZoom` → `ZoomStyleApplier`, an alloc-free hot path
evaluating only the Zoom-kind bindings):

- **Fill** — zoom-expression paint (color/opacity) → material uniforms.
- **Line** — zoom-expression paint + **line width** + **dasharray**.
- **Background** — zoom-expression color/opacity.

**Does NOT re-evaluate (the gaps):**

- **Symbols** — `SymbolRenderLayer.ApplyZoom(zoom)` is a **no-op**. Symbol layout/paint (text-size etc.) is
  evaluated **at build time**; the tracked direction is to bake at *tile* zoom (see the tile-pipeline
  unification epic's zoom-dual-meaning follow-up, `docs/per-layer-tile-processing-design.md`). Either way,
  nothing re-evaluates as the camera zooms.
- **Data-driven / per-feature properties** (feature-property expressions → per-vertex color, etc.) are evaluated
  at **build time** in `StyledFillTileBuilder` / `StyledLineTileBuilder` and **baked into the mesh** — they
  don't re-evaluate on camera change (re-evaluating them means rebuilding geometry).
- **Camera params other than zoom** — only `Zoom` drives `ApplyZoom`. Pitch/bearing/center-dependent properties
  (e.g. pitch-scaled sizes) are not re-evaluated on those changes.
- **No easing / animation** — re-evaluation is **instantaneous** per frame; no smooth transition of a property
  from its old value to its new one.

## The "big check" (the audit this needs before a design)

1. **Inventory every style property** by *what it depends on* (zoom / pitch / bearing / center / feature-data /
   constant) and *where it currently lives* (per-frame material uniform re-eval / baked into geometry at build /
   baked at symbol build / not evaluated at all). Find every gap against MapLibre's continuous model.
2. **Decide the re-evaluation tier per property:** cheap uniform re-eval per frame (already the fill/line/bg
   path) vs needs-geometry-rebuild (data-driven baked props — when, if ever, do these re-eval on zoom?) vs
   symbol re-eval (the currently-no-op path).
3. **Symbols** — the biggest gap: make symbol properties re-evaluate on zoom (superseding the interim "bake at
   tile zoom" follow-up), which interacts with the label build/cache lifecycle.
4. **Beyond zoom** — which properties legitimately depend on pitch/bearing/center, and drive `ApplyZoom`-like
   passes from those camera changes too.
5. **Smoothness / animation** — ease property changes over a short duration instead of popping. The shared
   machinery with style switching (§3).

## Grounding (touch points)

`MapView.LateUpdate` (the per-frame `ApplyZoom` drive); `RenderLayerSet.ApplyZoom`; `ZoomStyleApplier` (the
Zoom-kind binding evaluator, alloc-free); `IRenderLayer.ApplyZoom`; `FillRenderLayer` / `LineRenderLayer`
(width + dasharray re-eval) / `BackgroundRenderLayer` / `SymbolRenderLayer` (the no-op);
`StyledFillTileBuilder` / `StyledLineTileBuilder` (build-time data-driven evaluation baked into geometry);
`MapRenderer.Core` expression/`EvaluationContext` layer.

---

# 3. Shared easing machinery

Both epics reduce to the same primitive: **smoothly move a property from its old value to a new one over a short
duration.** A compatible restyle (§1.4) and a camera-zoom step (§2.5) are two triggers for that one mechanism.
Open design questions to settle once, for both:

- **Where easing lives** — likely a per-property animator over the applier layer (`ZoomStyleApplier` /
  `MapMaterialSet`), not per-call-site.
- **How it composes with continuous zoom interpolation** — the fill/line/bg zoom path is already *continuous*
  (evaluates every frame) but not *eased* across a discrete change (a restyle, or a stepped property). Easing
  layers on top of, not instead of, continuous interpolation.

## Relationship to other work

- **Symbol projection support** (`docs/labels-and-symbols-design.md` §4) — orthogonal: that is *where* a label
  projects; §2 is *what value* its properties take. Both touch the symbol build/apply lifecycle.
- Warm reuse (§1.3) builds on the tile-pipeline unification epic's per-source pipeline model and the geometry-IR
  epic's shared buffers.
- Orthogonal to the projection/globe track and the geometry-IR epic — this is the *style* / *time* axis, not the
  *geometry* or *projection* axes.
