# Smooth transitions — design & SSOT

**Status:** §1 (style switching) is shipped through the style-transitions epic (UMR-151/152 — see
`docs/tile-pipeline-design.md` §1.10/§7.5 for the mechanism); §3 (shared easing) was built as part of that
work and is described below. §2 (camera-driven property re-evaluation) is not started. Two epics that
change **rendered properties smoothly over time** and
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

## Current state (shipped)

`MapView.SetStyle` now runs one of two arms (`docs/tile-pipeline-design.md` §1.10):

- **In-place restyle.** `RenderLayerSet.TryRestyleInPlace` runs an ID-keyed diff over the old/new style's
  layers; when it proves the change is not mesh-affecting, the existing render layers, materials, and warm
  tile pipelines are kept — only uniform bindings retarget, eased over `StyleTransition` (§3, sub-topics 2-4
  below). No teardown, no blank frame, no rebuild.
- **Full rebuild.** Otherwise the destructive path still runs (`RenderLayerSet.ClearLayers` then a full
  rebuild). Its known gap is narrower than it was: the ONE `await BuildSourceSpecs` that resolves sources now
  runs *before* any mutation, so a delayed or cancelled restyle leaves the previous style fully live
  (`MapView.SetStyle`'s "Epic A / A2 ... option (i) bounded" comment). But the rebuild itself still mutates
  `RenderLayerSet` / `TileManager` / `SymbolSubsystem` directly rather than building off-side and swapping
  atomically — a throw mid-rebuild is bounded and observable (`MapView.CommitPhase`, `CommitProbe`) but not
  rolled back. **Sub-topic 1 (full transactional atomicity for this arm) is therefore still open** — recorded
  as a pre-existing gap fill/line share in `docs/per-layer-tile-processing-design.md`'s "Deliberately left
  filed".

## Sub-topics (each likely its own stage)

1. **Transactional atomicity — partially done, not complete.** The pre-mutation await (above) closes the
   window a delayed/cancelled restyle could land in, but the full-rebuild arm's six mutation sites
   (`MapView.CommitPhase`) are still committed directly, with no off-side build + atomic swap and no rollback
   on a mid-commit throw. Still open.
2. **Compatibility detection — delivered.** `RenderLayerSet.TryRestyleInPlace` + `SurvivingLayerGate` classify
   an (old style, new style) pair by an ID-keyed diff: a mesh-affecting change (source, layer add/remove/
   reorder, structural) refuses the in-place arm and falls through to the full rebuild; anything else (paint-
   only) patches in place. See `docs/tile-pipeline-design.md` §1.10.
3. **Warm reuse beyond sources — delivered.** The in-place arm above keeps every render layer, material, and
   prepared mesh when the diff says geometry is unchanged; `TileManager.SetSources`' teardown loop no longer
   unconditionally clears `PreparedTileCache` (`_prepared.Clear()` now runs only on a `BufferClip` change).
4. **Smooth visual transition — delivered.** `ZoomStyleApplier.BindOrRetarget` eases a retargeted paint
   property (color/opacity/px-valued) from its old value to the new one over `StyleTransition`; `RenderLayerSet.
   AdvanceFade` eases a layer's zoom-range fade the same way. Uses the shared machinery (§3).
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

**Re-evaluates per frame today** (`MapView.LateUpdate` → `Layers.ApplyZoom(cameraProperties.Zoom)` →
`RenderLayerSet.ApplyZoom` → each `IRenderLayer.ApplyZoom` → `ZoomStyleApplier`, an alloc-free hot path
evaluating only the Zoom-kind bindings):

- **Fill** — zoom-expression paint (color/opacity) → material uniforms.
- **Line** — zoom-expression paint + **line width** + **dasharray**.
- **Background** — zoom-expression color/opacity.
- **Symbols (paint only)** — `SymbolRenderLayer.ApplyZoom` drives its applier every frame too, covering
  the two symbol colours — `text-color` (`_TextColor`) and `text-halo-color` (`_HaloColor`), each at its
  CONSTANT kind only, which is the kind that rides a uniform. `text-halo-width`/`-blur` are NOT here: the
  halo is geometry now (a second glyph run), so those two ride its vertex stream per feature and move only
  when the tile is rebuilt. This half of the original gap has closed for the colours.
- **Easing** — a restyle no longer pops a changed uniform: `ZoomStyleApplier.BindOrRetarget` eases a
  retargeted binding, and `RenderLayerSet.AdvanceFade` eases a layer's zoom-range fade, both over
  `StyleTransition` (§3). This section's original "no easing / animation" gap is closed for restyle-
  triggered changes; it says nothing about easing a continuous camera-zoom step (§2's own goal, still below).

**Does NOT re-evaluate (the gaps):**

- **Symbols (layout)** — text-size and other *layout* properties (as opposed to paint, above) are still
  evaluated **at build time**; the tracked direction is to bake at *tile* zoom (see the tile-pipeline
  unification epic's zoom-dual-meaning follow-up, `docs/per-layer-tile-processing-design.md`). Nothing
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

- **Where easing lives — settled.** A per-property animator over the applier layer, as this section
  anticipated: `ZoomStyleApplier`'s `Binding<T>` carries `Origin`/`StartSeconds`/`DurationSeconds` per bound
  property, and `BindOrRetarget` arms them on a retarget. The layer-level fade (`minzoom`/`maxzoom`/
  `visibility`) eases the same way, one level up, in `RenderLayerSet.AdvanceFade` — the target/duration/clock
  are the *set*'s, not any one layer's, so no transition and no clock cross `IFadeableRenderLayer`.
- **How it composes with continuous zoom interpolation** — the fill/line/bg zoom path is already *continuous*
  (evaluates every frame) but not *eased* across a discrete change (a restyle, or a stepped property). Easing
  layers on top of, not instead of, continuous interpolation: both the origin and the target of an easing
  binding are evaluated at the live zoom every frame (`ZoomStyleApplier.ApplyZoom`); time only moves the mix
  between them.

## Relationship to other work

- **Symbol projection support** (`docs/labels-and-symbols-design.md` §4) — orthogonal: that is *where* a label
  projects; §2 is *what value* its properties take. Both touch the symbol build/apply lifecycle.
- Warm reuse (§1.3) builds on the tile-pipeline unification epic's per-source pipeline model and the geometry-IR
  epic's shared buffers.
- Orthogonal to the projection/globe track and the geometry-IR epic — this is the *style* / *time* axis, not the
  *geometry* or *projection* axes.
