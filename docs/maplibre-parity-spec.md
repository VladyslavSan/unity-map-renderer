# MapLibre parity — spec surface (north star)

> The capability target for this renderer — the durable **parity reference** for what MapLibre does that
> this project aims to match. It is the stable north-star surface, not a task tracker.
>
> For **what's actually implemented** against this target (full / partial / parsed-inert / missing, traced
> through the code), see the companion [`maplibre-support-matrix.md`](maplibre-support-matrix.md).

**North star:** parity with MapLibre's *feature set* (Style Spec sources, the layer types, paint/layout
properties, expressions/filters, projections, sprites & glyphs, terrain/hillshade) — **clean-room** from
public specs/docs only. NEVER read MapLibre source.

The milestone order in `ARCHITECTURE.md` §3 (Step 0–5) is refined into small, one-cycle stages. Parity is
tracked as *capabilities and ordering*, not exhaustive per-property detail — paint/layout properties are
filled in as their layer stages are refined.

## Spec surface to reach parity with (from public docs)
- **Source types:** vector, raster, raster-dem (mapbox/terrarium/custom encodings), geojson, image, video.
- **Layer types (10):** background, fill, line, symbol, circle, heatmap, fill-extrusion, raster,
  hillshade, color-relief.
- **Expression categories:** variable binding (`let`/`var`), types (`literal`/`typeof`/`to-color`…),
  lookup (`get`/`has`/`at`/`in`/`length`), decision (`case`/`match`/`coalesce`/comparisons/`all`/`any`),
  ramps/scales/curves (`step`/`interpolate`/`interpolate-hcl`/`interpolate-lab`), math, color
  (`rgb`/`rgba`/`to-rgba`), feature data (`properties`/`feature-state`/`geometry-type`/`id`), `zoom`,
  `heatmap-density`, string ops.
- **Filters:** legacy filter syntax + expression-based filters.
- **Root style props:** version, sources, layers, sprite, glyphs, light, sky, projection, terrain,
  transition, plus view (center/zoom/bearing/pitch/roll).
- **Projections:** Web Mercator (default), globe.
