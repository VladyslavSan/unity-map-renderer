# Smooth transitions — design & SSOT

**Status:** § "Style switching" ships (the mechanism is `docs/tile-pipeline-design.md` § "Partial-survival
restyle" and `docs/tile-pipeline-design.md` § "The draw gate"), and so does § "Shared easing machinery",
described below. § "Camera-driven
property re-evaluation" is not built. Two epics that change **rendered properties smoothly over time** and
**share one easing/animation mechanism**: (1) **style switching** — transition between compatible
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

## Current state

`MapView.SetStyle` runs one of two arms (`docs/tile-pipeline-design.md` § "Partial-survival restyle"):

- **In-place restyle.** `RenderLayerSet.TryRestyleInPlace` runs an ID-keyed diff over the old/new style's
  layers; when it proves the change is not mesh-affecting, the existing render layers, materials, and warm
  tile pipelines are kept — only uniform bindings retarget, eased over `StyleTransition`
  (§ "Shared easing machinery"; sub-topics 2-4 below). No teardown, no blank frame, no rebuild.
- **Full rebuild.** Otherwise the destructive path runs (`RenderLayerSet.ClearLayers` then a full
  rebuild). The ONE `await BuildSourceSpecs` that resolves sources runs *before* any mutation
  (`MapView.SetStyle`), so a delayed or cancelled restyle leaves the previous style fully live. But the
  rebuild itself mutates `RenderLayerSet` / `TileManager` / `SymbolSubsystem` directly rather than building
  off-side and swapping atomically — a throw mid-rebuild is bounded and observable (`MapView.CommitPhase`,
  `CommitProbe`) but not rolled back. **Sub-topic 1 (full transactional atomicity for this arm) is therefore
  open** — `docs/per-layer-tile-processing-design.md` § "Out of scope" records it as a gap fill/line share.

## Sub-topics

1. **Transactional atomicity — open.** The pre-mutation await (above) closes the window a delayed/cancelled
   restyle could land in, but the full-rebuild arm's six mutation sites (`MapView.CommitPhase`) commit
   directly, with no off-side build + atomic swap and no rollback on a mid-commit throw.
2. **Compatibility detection.** `RenderLayerSet.TryRestyleInPlace` + `SurvivingLayerGate` classify
   an (old style, new style) pair by an ID-keyed diff: a mesh-affecting change (source, layer add/remove/
   reorder, structural) refuses the in-place arm and falls through to the full rebuild; anything else (paint-
   only) patches in place. See `docs/tile-pipeline-design.md` § "Partial-survival restyle".
3. **Warm reuse beyond sources.** The in-place arm above keeps every render layer, material, and prepared
   mesh when the diff says geometry is unchanged; `SetSources` keeps `PreparedTileCache` — its only
   `_prepared.Clear()` runs in `TileManager.TickCore`, on a `BufferClip` change.
4. **Smooth visual transition.** `ZoomStyleApplier.BindOrRetarget` eases a retargeted paint
   property (color/opacity/px-valued) from its old value to the new one over `StyleTransition`; `RenderLayerSet.
   AdvanceFade` eases a layer's zoom-range fade the same way. Uses the shared machinery
   (§ "Shared easing machinery" below).
5. **In-flight preservation.** Don't cancel in-flight tile fetches / label builds that are still valid under the
   new style. Not built.

## Grounding (touch points)

`MapView.SetStyle`; `RenderLayerSet.ClearLayers` / `RenderLayerSet.Build`; `RenderLayerFactory`;
`TileManager.SetSources` (the `SourceKey` warm-reuse — the one place partial warm reuse already lives);
`SymbolSubsystem.SetStyle`; the material appliers (`ZoomStyleApplier`, `MapMaterialSet`).

---

# 2. Camera-driven property re-evaluation

## The goal

As the camera parameters change — **zoom** primarily, but also **pitch / bearing / center** where a property
depends on them — every style property should be **re-evaluated correctly, completely, and smoothly**
(animated/eased where appropriate), not baked once and left stale, and not stepped/popped. This is the MapLibre
behaviour: zoom-dependent paint/layout interpolate continuously as you zoom.

## Current state — partial, zoom-only, layout un-eased

**Re-evaluates per frame** (`MapView.LateUpdate` → `Layers.ApplyZoom(StyleFrameInputs)` →
`RenderLayerSet.ApplyZoom` → each `IRenderLayer.ApplyZoom` → `ZoomStyleApplier`, an alloc-free hot path
evaluating only the Zoom-kind bindings):

- **Fill** — zoom-expression paint (color/opacity) → material uniforms.
- **Line** — zoom-expression paint + **line width** + **dasharray**.
- **Background** — zoom-expression color/opacity.
- **Symbols (paint only)** — `SymbolRenderLayer.ApplyZoom` drives its applier every frame too, covering
  the two symbol colours — `text-color` (`_TextColor`) and `text-halo-color` (`_HaloColor`), each at its
  CONSTANT kind only, which is the kind that rides a uniform. `text-halo-width`/`-blur` are NOT here: the
  halo is geometry (a second glyph run), so those two ride its vertex stream per feature and move only
  when the tile is rebuilt.
- **Easing** — a restyle does not pop a changed uniform: `ZoomStyleApplier.BindOrRetarget` eases a
  retargeted binding, and `RenderLayerSet.AdvanceFade` eases a layer's zoom-range fade, both over
  `StyleTransition` (§ "Shared easing machinery" below). This covers restyle-triggered changes only; it says
  nothing about easing a continuous camera-zoom step (the goal of § "Camera-driven property
  re-evaluation", open in the gaps below).

**Does NOT re-evaluate (the gaps):**

- **Symbols (layout)** — text-size and other *layout* properties (as opposed to paint, above) are
  evaluated **at build time**; the recorded direction is to bake at *tile* zoom
  (`docs/per-layer-tile-processing-design.md` § "Open: the zoom dual meaning"). Nothing
  re-evaluates layout as the camera zooms.
- **Data-driven / per-feature properties** (feature-property expressions → per-vertex color, etc.) are evaluated
  at **build time** in `StyledFillTileBuilder` / `StyledLineTileBuilder` and **baked into the mesh** — they
  don't re-evaluate on camera change (re-evaluating them means rebuilding geometry).
- **Camera params other than zoom** — only `Zoom` drives `ApplyZoom`. Pitch/bearing/center-dependent properties
  (e.g. pitch-scaled sizes) are not re-evaluated on those changes.

## The "big check" (the audit this needs before a design)

1. **Inventory every style property** by *what it depends on* (zoom / pitch / bearing / center / feature-data /
   constant) and *where it currently lives* (per-frame material uniform re-eval / baked into geometry at build /
   baked at symbol build / not evaluated at all). Find every gap against MapLibre's continuous model.
2. **Decide the re-evaluation tier per property:** cheap uniform re-eval per frame (already the fill/line/bg
   path) vs needs-geometry-rebuild (data-driven baked props — when, if ever, do these re-eval on zoom?) vs
   symbol layout re-eval (build-time only).
3. **Symbols** — the biggest gap: make symbol properties re-evaluate on zoom (superseding the interim "bake at
   tile zoom" follow-up), which interacts with the label build/cache lifecycle.
4. **Beyond zoom** — which properties legitimately depend on pitch/bearing/center, and drive `ApplyZoom`-like
   passes from those camera changes too.
5. **Smoothness / animation** — ease property changes over a short duration instead of popping. The shared
   machinery with style switching (§ "Shared easing machinery" below).

## Grounding (touch points)

`MapView.LateUpdate` (the per-frame `ApplyZoom` drive); `RenderLayerSet.ApplyZoom`; `ZoomStyleApplier` (the
Zoom-kind binding evaluator, alloc-free); `IRenderLayer.ApplyZoom`; `FillRenderLayer` / `LineRenderLayer`
(width + dasharray re-eval) / `BackgroundRenderLayer` / `SymbolRenderLayer` (the two paint colours);
`StyledFillTileBuilder` / `StyledLineTileBuilder` (build-time data-driven evaluation baked into geometry);
`MapRenderer.Core` expression/`EvaluationContext` layer.

---

# 3. Shared easing machinery

Both epics reduce to the same primitive: **smoothly move a property from its old value to a new one over a short
duration.** A compatible restyle (§ "Style switching", sub-topic 4) and a camera-zoom step
(§ "Camera-driven property re-evaluation", "big check" item 5) are two triggers for that one mechanism.

- **Where easing lives — settled.** A per-property animator over the applier layer:
  `ZoomStyleApplier`'s `Binding<T>` carries `Origin`/`StartSeconds`/`DurationSeconds` per bound
  property, and `BindOrRetarget` arms them on a retarget. The layer-level fade (`minzoom`/`maxzoom`/
  `visibility`) eases the same way, one level up, in `RenderLayerSet.AdvanceFade` — the target/duration/clock
  are the *set*'s, not any one layer's, so no transition and no clock cross `IFadeableRenderLayer`.
- **How it composes with continuous zoom interpolation** — the fill/line/bg zoom path is already *continuous*
  (evaluates every frame) but not *eased* across a discrete change (a restyle, or a stepped property). Easing
  layers on top of, not instead of, continuous interpolation: both the origin and the target of an easing
  binding are evaluated at the live zoom every frame (`ZoomStyleApplier.ApplyZoom`); time only moves the mix
  between them.

## Relationship to other work

- **Symbol projection support** (`docs/labels-and-symbols-design.md` § "Projection support (globe-ready
  labels)") — orthogonal: that is *where* a label projects; § "Camera-driven property re-evaluation" is *what
  value* its properties take. Both touch the symbol build/apply lifecycle.
- Warm reuse (§ "Style switching", sub-topic 3) builds on the per-source pipeline model
  (`docs/tile-pipeline-design.md`) and the geometry IR's shared buffers (`docs/tile-geometry-ir-design.md`).
- Orthogonal to the projection/globe track and the geometry-IR epic — this is the *style* / *time* axis, not the
  *geometry* or *projection* axes.
