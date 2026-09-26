# MapLibre spec — parity target

The durable north-star surface: **what MapLibre does that this renderer aims to match**. What is
implemented today is in [`maplibre-style-spec-support-matrix.md`](maplibre-style-spec-support-matrix.md).
Clean-room throughout — built from public MapLibre specs/docs only. **NEVER read MapLibre source.**

1. **[Parity target](#1-parity-target-north-star)** — the capability surface to reach.
2. **[Support matrix](#2-support-matrix)** — a pointer to the traced support matrix.

---

# 1. Parity target (north star)

The stable capability target: parity with MapLibre's *feature set* — Style Spec sources, the layer types,
paint/layout properties, expressions/filters, projections, sprites & glyphs, terrain/hillshade. Not a task
tracker; parity is tracked as *capabilities and ordering*, not exhaustive per-property detail — paint/layout
properties are filled in as their layer stages land. The milestone order in `ARCHITECTURE.md` § "Roadmap (Unity milestones)" (Step 0–5)
refines this into small stages.

## Spec surface to reach parity with (from public docs)

- **Source types:** vector, raster, raster-dem (mapbox/terrarium/custom encodings), geojson, image, video.
- **Layer types (10):** background, fill, line, symbol, circle, heatmap, fill-extrusion, raster, hillshade,
  color-relief.
- **Expression categories:** variable binding (`let`/`var`), types (`literal`/`typeof`/`to-color`…),
  lookup (`get`/`has`/`at`/`in`/`length`), decision (`case`/`match`/`coalesce`/comparisons/`all`/`any`),
  ramps/scales/curves (`step`/`interpolate`/`interpolate-hcl`/`interpolate-lab`), math, color
  (`rgb`/`rgba`/`to-rgba`), feature data (`properties`/`feature-state`/`geometry-type`/`id`), `zoom`,
  `heatmap-density`, string ops.
- **Filters:** legacy filter syntax + expression-based filters.
- **Root style props:** version, sources, layers, sprite, glyphs, font-faces, light, sky, projection, terrain,
  transition, state, plus view (center/centerAltitude/zoom/bearing/pitch/roll).
- **Projections:** Web Mercator (default), globe.

---

# 2. Support matrix

The codebase-traced status of every style spec item is in
[`maplibre-style-spec-support-matrix.md`](maplibre-style-spec-support-matrix.md). That file is the only copy.
