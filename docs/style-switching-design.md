# Style switching — smooth transitions between compatible styles (design / SSOT)

**Status: design stub, not started.** Captures the topic + the known first piece (transactional atomicity);
the full design is TBD. Spun out of the Epic A merge-step follow-up "whole-restyle transactional atomicity"
(`docs/per-layer-tile-processing-design.md`, §A2 follow-ups), which turned out to be one facet of a much larger
goal.

## The goal

Switching from style A to style B should be **smooth**: when the two styles are *compatible*, transition
between them rather than tear everything down and rebuild from scratch — no blank frame, warm tiles/caches
reused, and (ideally) paint changes eased rather than popped. A full teardown+rebuild stays the fallback for
*incompatible* changes (different sources, different source-layers, structural layer changes).

## Current state (why it's not smooth today)

`SetStyle` is **destructive and non-transactional**:
- `RenderLayerSet.ClearLayers` destroys **every** old layer's material before the new set is built, so there
  is a window with no render layers.
- `MapView.SetStyle` drives `Layers.Build` / `_symbols.SetStyle` / `TileManager.SetSources` in sequence; a
  throw *during* that commit leaves a **partially-mutated** `RenderLayerSet` — no rollback (the transactional
  atomicity gap). This predates Epic A and affects fill/line/symbol identically.
- Warm reuse is **partial**: `TileManager.SetSources` already keeps a source's pipeline (warm
  `TileScheduler`/cache) when its `SourceKey` matches (decision 7a), so unchanged sources don't re-fetch. But
  the render layers, materials, and label state are rebuilt wholesale regardless.
- There is **no cross-style diffing** — nothing detects "these two styles differ only in paint, keep the
  geometry" or "only layer X changed."

## Sub-topics (each likely its own stage)

1. **Transactional atomicity (the known first piece).** Build the new layer/backend/label state *off-side*,
   then **atomically swap**; a throw mid-build rolls back cleanly, leaving the old style fully intact. Removes
   the partial-mutation gap and the blank window. This is the smallest, most self-contained slice and a natural
   first stage — it makes *any* restyle safe before we make *compatible* restyles smooth.
2. **Compatibility detection.** A diff over (style A, style B) classifying the change: paint-only (same
   geometry, re-bind materials), layer add/remove/reorder (patch the `RenderLayerSet`), source change (needs
   re-fetch), structural (full rebuild). Decides which transition path applies.
3. **Warm reuse beyond sources.** Extend today's `SourceKey` warm-cache reuse to render layers / prepared
   meshes / labels when the diff says the geometry is unchanged — so a paint-only restyle keeps every tile
   mesh and only re-applies materials.
4. **Smooth visual transition.** For compatible changes, ease paint properties (color/opacity/width) from A to
   B over a short duration, or crossfade — instead of popping. Interacts with the `ZoomStyleApplier` /
   material-applier layer.
5. **In-flight preservation.** Don't cancel in-flight tile fetches / label builds that are still valid under
   the new style.

## Grounding (touch points)

`MapView.SetStyle`; `RenderLayerSet.ClearLayers` / `RenderLayerSet.Build`; `RenderLayerFactory`;
`TileManager.SetSources` (the 7a `SourceKey` warm-reuse — the one place partial warm reuse already lives);
`SymbolLabelSubsystem.SetStyle`; the material appliers (`ZoomStyleApplier`, `MapMaterialSet`).

## Relationship to other work

- **Camera-driven property re-evaluation** (`docs/camera-property-reevaluation-design.md`) — shares the
  easing/animation machinery: a compatible restyle and a camera-zoom change are two triggers for the *same*
  "smoothly move properties to new values" mechanism (§4). Design them together.
- The transactional-atomicity slice (§1) is the item filed out of Epic A's merge step.
- Warm reuse (§3) builds on Epic A's per-source pipeline model and the geometry-IR epic's shared buffers.
- This is orthogonal to the projection/globe track and the geometry-IR epic — it's about the *style* axis,
  not the *geometry* or *projection* axes.
