# MapLibre style spec — support matrix

This file is the one place that lists the MapLibre style spec surface and the status of each item in this
renderer. Other docs point here. They do not keep their own copy.

**Snapshot date: 2026-09-26.**

**How this was traced.** The spec surface comes from the public MapLibre style spec pages
(maplibre.org/maplibre-style-spec: root, sources, layers, light, sky, projection, terrain, expressions).
Each row was traced through the code, not read off the spec: where the key is parsed
(`Assets/Code/MapRenderer.Unity/Style/`), which code reads the parsed value, and whether that value reaches
rendered output. A key that survives only in the raw JSON (`StyleDocument.Root`, `StyleLayer.Raw`,
`SourceDefinition.Raw`) is not support. When the code and a row disagree, the code is correct: trace the
row again.

## Legend

| Status | Meaning |
|---|---|
| `supported` | Parsed, read, and it reaches rendered output for every form the spec allows. |
| `partial` | It reaches rendered output, but some forms, values or cases do not. The note says which. |
| `parsed, inert` | Parsed into a typed value, but nothing uses the value to change output. |
| `not supported` | Not parsed. A style that sets it gets the same output as a style that does not. |
| `no rendering effect` | The spec gives the key no rendering effect. The row is listed for completeness and is not in the totals. |

Rows: 53 supported, 58 partial, 4 parsed but inert, 77 not supported; 6 rows with no rendering effect are not
counted.

**† — evaluated at the tile build zoom.** The spec re-evaluates a zoom-dependent value continuously as the
camera zooms. The renderer does this for layer paint that rides a material uniform: constant and zoom forms
of background, fill, line and fill-extrusion paint, `line-dasharray`, and constant `text-color` /
`text-halo-color`. It evaluates the following values once, at the zoom the tile is built at, and keeps
them until the tile is rebuilt: every data-driven paint value (baked into the mesh), every symbol layout
value, symbol paint other than a constant text or halo colour, `fill-sort-key`, `fill-antialias`. A row
marked † is otherwise complete; its status does not count this limit. The design and the open work are in
[`smooth-transitions-design.md`](smooth-transitions-design.md) § "Camera-driven property re-evaluation".

**What an unsupported expression does.** It depends on where it is:

- In an expression-capable property, or in `light.color`, `light.intensity` or `sky`, it fails the style
  parse. `MapView.SetStyle` logs the error and keeps the previous style. Four properties are exceptions:
  `fill-antialias`, `fill-extrusion-translate`, `fill-extrusion-vertical-gradient` and `line-dasharray`
  catch the error and use their default.
- In a property whose note starts with "Constant only", or in a pattern name, any expression gives that
  property's default.
- In a layer `filter`, the style load checks it once and logs one warning if it does not compile (an
  unsupported operator or a malformed filter). That layer takes no draw slot (`RenderLayerFactory`'s
  `UnsupportedFilter`, the same compatibility summary as an unsupported layer `type`) and draws or labels
  nothing; every other layer, mesh or symbol, still draws.
- In `text-field` or `icon-image`, that feature gets no text or no icon. The rest of the layer draws.

---

## 1. Root properties

| Property | Status | Note |
|---|---|---|
| `version` | `no rendering effect` | Parsed, not read. |
| `name` | `no rendering effect` | Parsed, not read. |
| `metadata` | `no rendering effect` | Kept only in the raw JSON. |
| `center` | `not supported` | The initial camera comes from the host configuration. |
| `centerAltitude` | `not supported` | As `center`. |
| `zoom` | `not supported` | As `center`. |
| `bearing` | `not supported` | As `center`. |
| `pitch` | `not supported` | As `center`. |
| `roll` | `not supported` | As `center`. |
| `state` | `not supported` | `global-state` is not built. |
| `light` | `partial` | See § 2. |
| `sky` | `partial` | See § 3. |
| `projection` | `not supported` | The globe exists, but the host selects it (`MapHost.UseGlobe`), not the style. See [`projection-globe-track-design.md`](projection-globe-track-design.md). |
| `terrain` | `not supported` | No `raster-dem` source and no terrain mesh. See [`depth-and-render-regimes-design.md`](depth-and-render-regimes-design.md) § 6 (D). |
| `sources` | `partial` | See § 4. |
| `sprite` | `partial` | A single URL string only. The array form (several sprite sheets) gives no sprite. Only the 1x sheet is fetched. |
| `glyphs` | `supported` | PBF glyph ranges are fetched into the SDF atlas. |
| `font-faces` | `not supported` | Text uses the `glyphs` PBF path only. |
| `transition` | `not supported` | A restyle eases over `MapView.StyleTransition`, a host setting. The root key and every `*-transition` key are not read. |
| `layers` | `supported` | Declared order is paint order. Per-type status is in § 5. |

## 2. `light`

`light` drives one scene directional light (`SunLight`). That light shades every lit layer (lit fills,
lines and fill-extrusions), not only fill-extrusions as the spec describes.

| Property | Status | Note |
|---|---|---|
| `anchor` | `not supported` | Known deviation: always behaves as `"map"`. The spec default is `"viewport"`. |
| `position` | `partial` | Constant only. Azimuthal and polar angles set the light direction; the radial part is not used. |
| `color` | `partial` | Evaluated at the zoom the style is applied at: a zoom expression keeps that value until the next style load. |
| `intensity` | `partial` | Evaluated at the zoom the style is applied at: a zoom expression keeps that value until the next style load. |

## 3. `sky`

`sky-color` and `horizon-color` paint a gradient behind the map (`SkyGradient`). `fog-color` tints distant
ground through URP linear fog (`DistanceHaze`).

| Property | Status | Note |
|---|---|---|
| `sky-color` | `partial` | Evaluated at the zoom the style is applied at: a zoom expression keeps that value until the next style load. |
| `horizon-color` | `partial` | Evaluated at the zoom the style is applied at: a zoom expression keeps that value until the next style load. |
| `fog-color` | `partial` | Evaluated at the zoom the style is applied at: a zoom expression keeps that value until the next style load. |
| `sky-horizon-blend` | `not supported` | The gradient span is fixed at the spec default. |
| `horizon-fog-blend` | `not supported` | Not built. |
| `fog-ground-blend` | `not supported` | The haze range follows the camera far plane instead. |
| `atmosphere-blend` | `not supported` | No globe atmosphere. |

## 4. Sources

A source is fetched only when a visible `fill`, `line`, `symbol` or `fill-extrusion` layer uses it
(`RenderLayerFactory.TryGetFetchSource`).

| Source type | Status | Note |
|---|---|---|
| `vector` | `partial` | MVT only. Key status is below. |
| `raster` | `not supported` | No raster layer renderer, so the source is never fetched. See [`meshing-design.md`](meshing-design.md) § "Fill-extrusion and the raster seat". |
| `raster-dem` | `not supported` | No terrain or hillshade. |
| `geojson` | `partial` | Inline `data` object only, sliced locally. Key status is below. |
| `image` | `not supported` | Not built. |
| `video` | `not supported` | Not built. |

**`vector` keys**

| Key | Status | Note |
|---|---|---|
| `url` (TileJSON) | `supported` | Fetched once per style load. Inline `tiles` wins. |
| `tiles` | `partial` | Only the first URL template is used. |
| `minzoom` / `maxzoom` | `partial` | A cover tile above `maxzoom` is not requested, so the source draws nothing at that zoom; there is no per-source overzoom, only past the host `TileSelection.MaxZoom` cap. |
| `bounds` | `supported` | Gates tile requests: a cover tile whose ground quad does not strictly overlap `bounds` is never fetched. |
| `scheme` | `supported` | `"tms"` flips only the fetch address (`TileUrlTemplate`); every other identity (loaded/cache keys) stays XYZ. |
| `attribution` | `no rendering effect` | Not read. |
| `promoteId` | `not supported` | Feature ids come from the tile. |
| `volatile` | `not supported` | Tile caching does not read it. |
| `encoding` | `not supported` | MVT is the only decoder. |

**`geojson` keys**

| Key | Status | Note |
|---|---|---|
| `data` | `partial` | An inline object only. A URL string skips the source with a warning. |
| `maxzoom` | `partial` | Defaults to 18 (the spec value) when absent. A cover tile above `maxzoom` is not requested, so the source draws nothing at that zoom — the same per-source-overzoom limit as the vector row. |
| `buffer` | `partial` | An authored value is honoured (×8 to reference units, clamped to [0, 512]). An absent key keeps the slicer's own 64-reference-unit default, not the spec's 128. |
| `tolerance` | `not supported` | Not read. The slicer runs at tolerance 0 (`GeoJsonSliceOptions.SimplifyTolerance` accepts no other value). |
| `cluster`, `clusterRadius`, `clusterMaxZoom`, `clusterMinPoints`, `clusterProperties` | `not supported` | No clustering. |
| `lineMetrics` | `not supported` | `line-gradient` is not built. |
| `generateId` / `promoteId` | `not supported` | Not read. |
| `filter` | `not supported` | Not read. |
| `attribution` | `no rendering effect` | Not read. |

---

## 5. Layers

`RenderLayerFactory.Create` is the registry of drawable layer types: `background`, `fill`, `line`,
`symbol` (with a `source`) and `fill-extrusion`. Any other type takes no draw slot, and
`RenderLayerSet.SkippedLayers` records why.

### Common layer keys

| Key | Status | Note |
|---|---|---|
| `id` | `supported` | Also the key for in-place restyle. |
| `type` | `supported` | An unknown type parses and is skipped. |
| `metadata` | `no rendering effect` | Kept only in the raw JSON. |
| `source` / `source-layer` | `supported` | |
| `minzoom` / `maxzoom` | `supported` | Checked against the live camera zoom every frame, so an overzoomed tile still turns a layer on and off. The layer fades over the restyle transition. |
| `filter` | `supported` | Legacy and expression syntax. See § 6. |
| `layout.visibility` | `supported` | A `"none"` layer is not built for that style load. |

### `background`

Drawn as one quad per covered tile, meshed like a fill. See [`meshing-design.md`](meshing-design.md)
§ "Background: a real layer, not a camera hack".

| Property | Status | Note |
|---|---|---|
| `background-color` | `supported` | |
| `background-opacity` | `supported` | |
| `background-pattern` | `parsed, inert` | Not built; the layer draws `background-color`. |

### `fill`

Owning design: [`fill-parity-design.md`](fill-parity-design.md).

| Property | Status | Note |
|---|---|---|
| `fill-sort-key` | `supported` | †. |
| `fill-antialias` | `supported` | †. An absent key takes the host's `MapViewConfig.FillAntialiasing`, whose default is the spec's `true`. See [`fill-boundary-antialiasing-design.md`](fill-boundary-antialiasing-design.md). |
| `fill-opacity` | `supported` | Data-driven values are †. |
| `fill-layer-opacity` | `not supported` | Not built. |
| `fill-color` | `supported` | Data-driven values are †. On a pattern layer it tints the pattern; see `fill-pattern`. |
| `fill-outline-color` | `parsed, inert` | Bound to a uniform that no shader pass reads. It needs line geometry: [`fill-parity-design.md`](fill-parity-design.md) § 7. |
| `fill-translate` | `partial` | Constant only. A zoom expression reads as `[0, 0]`. |
| `fill-translate-anchor` | `partial` | Constant only. |
| `fill-pattern` | `partial` | Constant sprite name only; the spec also allows data-driven. Known deviation: `fill-color` tints the pattern, and an absent `fill-color` is white. See [`fill-parity-design.md`](fill-parity-design.md) § 2. |

### `line`

Owning designs: [`line-rendering-design.md`](line-rendering-design.md),
[`line-translate-parity-design.md`](line-translate-parity-design.md).

| Property | Status | Note |
|---|---|---|
| `line-cap` | `partial` | Constant only; an expression gives `butt`. |
| `line-join` | `partial` | Constant only; the spec also allows data-driven. An expression gives `miter`. |
| `line-miter-limit` | `partial` | Constant only; an expression gives the default 2. |
| `line-round-limit` | `partial` | Constant only; an expression gives the default 1.05. |
| `line-sort-key` | `not supported` | Not built. |
| `line-opacity` | `supported` | Data-driven values are †. |
| `line-layer-opacity` | `not supported` | Not built. |
| `line-color` | `supported` | Data-driven values are †. |
| `line-translate` | `partial` | Constant only. A zoom expression reads as `[0, 0]`. |
| `line-translate-anchor` | `partial` | Constant only. `"map"` is approximate near the limb of a zoomed-out globe: [`line-translate-parity-design.md`](line-translate-parity-design.md) § "The residual". |
| `line-width` | `supported` | Data-driven values are †. |
| `line-gap-width` | `partial` | Constant and zoom only. A data-driven value is ignored. |
| `line-offset` | `partial` | Constant and zoom only. A data-driven value is ignored. |
| `line-blur` | `partial` | Constant and zoom only. A data-driven value is ignored. |
| `line-dasharray` | `partial` | Constant and zoom only; a data-driven value draws a solid line. Up to 4 entries; extra entries are dropped. An odd number of entries among the first four draws a solid line. |
| `line-pattern` | `parsed, inert` | The shader draws a solid line. |
| `line-gradient` | `not supported` | Not built. |

### `symbol` — layout

Owning designs: [`labels-and-symbols-design.md`](labels-and-symbols-design.md),
[`road-shields-design.md`](road-shields-design.md). Every value in this table is †.

| Property | Status | Note |
|---|---|---|
| `symbol-placement` | `partial` | `point` places Point and LineString features. `line` and `line-center` place LineString features only. A Polygon feature gets no symbol. |
| `symbol-spacing` | `supported` | |
| `symbol-avoid-edges` | `not supported` | Not built. |
| `symbol-sort-key` | `supported` | |
| `symbol-z-order` | `not supported` | Not built. |
| `icon-allow-overlap` | `partial` | Constant only. |
| `icon-overlap` | `not supported` | Not built. |
| `icon-ignore-placement` | `partial` | Constant only. |
| `icon-optional` | `partial` | Constant only. |
| `icon-rotation-alignment` | `partial` | Constant only. |
| `icon-size` | `supported` | |
| `icon-text-fit` | `not supported` | Not built. |
| `icon-text-fit-padding` | `not supported` | Not built. |
| `icon-image` | `partial` | The `image` operator is not built. A legacy function object is never routed to the parser (only an expression array is), so it resolves to no icon, the same as an unrecognised value. |
| `icon-rotate` | `supported` | |
| `icon-padding` | `partial` | A number, or the spec's `[top,right,bottom,left]` array (1-4 entries), parse and evaluate. Collision applies one isotropic value: the largest entry. It does not apply a value per side. A zoom expression with mismatched stop lengths falls back to the spec default (2px) for that feature, instead of failing the tile's whole label build. |
| `icon-keep-upright` | `not supported` | Always `false`, the spec default. |
| `icon-offset` | `partial` | Constant only; the spec also allows zoom and data-driven. |
| `icon-anchor` | `partial` | Constant only; the spec also allows data-driven. |
| `icon-pitch-alignment` | `partial` | Constant only. Used only for icons that follow a line. Point icons always stand upright (`viewport`). |
| `text-pitch-alignment` | `partial` | Constant only. Used only for curved line text. Point text always stands upright (`viewport`). |
| `text-rotation-alignment` | `partial` | Constant only. `viewport-glyph` is not built and reads as `auto`. Under line placement, `viewport` gives upright text at each anchor instead of curved text: [`road-shields-design.md`](road-shields-design.md) § 3 (D4). |
| `text-field` | `partial` | `format` is not built, so that feature gets no text. A legacy function object is never routed to the parser (only an expression array is): a `{token}` string still expands, but a `{"stops": …}` object resolves to no text. |
| `text-font` | `partial` | Constant and zoom expressions evaluate correctly, including a legacy `{"stops": …}` function (it steps, not ramps: `text-font` has no "interpolate" marker). In a `step` or `interpolate` stop, write the font list as `["literal", […]]`; a bare list there fails the style parse. The spec also allows data-driven, which degrades to the default stack (the font stack is resolved once per layer, not per feature). |
| `text-size` | `supported` | |
| `text-max-width` | `supported` | |
| `text-line-height` | `supported` | |
| `text-letter-spacing` | `supported` | |
| `text-justify` | `partial` | Constant only; the spec also allows data-driven. |
| `text-radial-offset` | `supported` | |
| `text-variable-anchor` | `not supported` | Placement tries one anchor only. |
| `text-variable-anchor-offset` | `not supported` | As `text-variable-anchor`. |
| `text-anchor` | `partial` | Constant only; the spec also allows data-driven. Known deviation: `center` uses the optical centre of the text block: [`road-shields-design.md`](road-shields-design.md) § 11. Curved line text is always centred on its anchor. |
| `text-max-angle` | `supported` | |
| `text-writing-mode` | `not supported` | No vertical text. |
| `text-rotate` | `not supported` | Not built. |
| `text-padding` | `supported` | |
| `text-keep-upright` | `partial` | Constant only. |
| `text-transform` | `partial` | Constant only; the spec also allows data-driven. |
| `text-offset` | `partial` | Constant only; the spec also allows zoom and data-driven. |
| `text-allow-overlap` | `partial` | Constant only. |
| `text-overlap` | `not supported` | Not built. |
| `text-ignore-placement` | `partial` | Constant only. |
| `text-optional` | `partial` | Constant only. |
| `symbol-height-offset` | `not supported` | Symbols sit on the ground. |
| `symbol-height-anchor` | `not supported` | As `symbol-height-offset`. |

### `symbol` — paint

Every value in this table is †, except a constant `text-color` or `text-halo-color`.

| Property | Status | Note |
|---|---|---|
| `icon-opacity` | `supported` | |
| `icon-color` | `not supported` | SDF icons are not built. |
| `icon-halo-color` | `not supported` | SDF icons are not built. |
| `icon-halo-width` | `not supported` | SDF icons are not built. |
| `icon-halo-blur` | `not supported` | SDF icons are not built. |
| `icon-translate` | `not supported` | Not built. |
| `icon-translate-anchor` | `not supported` | Not built. |
| `text-opacity` | `supported` | |
| `text-color` | `supported` | |
| `text-halo-color` | `supported` | |
| `text-halo-width` | `supported` | |
| `text-halo-blur` | `supported` | |
| `text-translate` | `partial` | Constant only; the spec also allows zoom. |
| `text-translate-anchor` | `partial` | Constant only. |

### `fill-extrusion`

Owning design: [`depth-and-render-regimes-design.md`](depth-and-render-regimes-design.md).

| Property | Status | Note |
|---|---|---|
| `fill-extrusion-rounded-corner-distance` | `not supported` | Not built. |
| `fill-extrusion-opacity` | `partial` | The layer draws opaque. Only 0 has an effect (the layer is not drawn). Translucent 3D is § 6 (E) of the owning design. |
| `fill-extrusion-color` | `supported` | Data-driven values are †. |
| `fill-extrusion-translate` | `supported` | |
| `fill-extrusion-translate-anchor` | `partial` | Constant only. |
| `fill-extrusion-pattern` | `not supported` | Not built. |
| `fill-extrusion-height` | `supported` | Data-driven values are †. |
| `fill-extrusion-base` | `supported` | Data-driven values are †. |
| `fill-extrusion-vertical-gradient` | `parsed, inert` | No shader reads it. |

### Layer types with no renderer

Each of these types takes no draw slot. None of its paint or layout keys are parsed.

| Layer type (spec properties) | Status | Note |
|---|---|---|
| `circle` (`circle-sort-key`, `circle-radius`, `circle-color`, `circle-blur`, `circle-opacity`, `circle-translate`, `circle-translate-anchor`, `circle-pitch-scale`, `circle-pitch-alignment`, `circle-stroke-width`, `circle-stroke-color`, `circle-stroke-opacity`) | `not supported` | Not built. |
| `heatmap` (`heatmap-radius`, `heatmap-weight`, `heatmap-intensity`, `heatmap-color`, `heatmap-opacity`) | `not supported` | Not built. |
| `raster` (`raster-opacity`, `raster-hue-rotate`, `raster-brightness-min`, `raster-brightness-max`, `raster-saturation`, `raster-contrast`, `raster-resampling`, `raster-fade-duration`, `resampling`) | `not supported` | Per-tile texture binding is an open design question: [`meshing-design.md`](meshing-design.md) § "Fill-extrusion and the raster seat". |
| `hillshade` (`hillshade-illumination-direction`, `hillshade-illumination-altitude`, `hillshade-illumination-anchor`, `hillshade-exaggeration`, `hillshade-shadow-color`, `hillshade-highlight-color`, `hillshade-accent-color`, `hillshade-method`, `resampling`) | `not supported` | Blocked by `raster-dem`. |
| `color-relief` (`color-relief-opacity`, `color-relief-color`, `resampling`) | `not supported` | Blocked by `raster-dem`. |

---

## 6. Expressions and filters

The operator registry is `ExpressionParser.Dispatch`. An operator it does not list fails to parse; § "What
an unsupported expression does" at the top says what that does to a style.

| Operators | Status | Note |
|---|---|---|
| Variable binding: `let`, `var` | `supported` | |
| Types: `literal`, `array`, `typeof`, `string`, `number`, `boolean`, `object`, `to-string`, `to-number`, `to-boolean`, `to-color` | `supported` | |
| `semiliteral` | `not supported` | Not built. |
| `collator` | `not supported` | Not built. A comparison with a collator argument also fails to parse. |
| `format` | `not supported` | Not built. |
| `image` | `not supported` | Not built. |
| `number-format` | `not supported` | Not built. |
| Lookup: `at`, `in`, `get`, `has`, `length` | `supported` | |
| `index-of` | `not supported` | Not built. |
| `slice` | `not supported` | Not built. |
| `global-state` | `not supported` | Not built. |
| Decision: `case`, `match`, `coalesce`, `==`, `!=`, `>`, `<`, `>=`, `<=`, `all`, `any`, `!` | `supported` | |
| `within` | `not supported` | Not built. |
| Ramps: `step`, `interpolate` (linear, exponential, cubic-bezier), `interpolate-hcl`, `interpolate-lab` | `supported` | |
| Math: `ln2`, `pi`, `e`, `+`, `*`, `-`, `/`, `%`, `^`, `sqrt`, `log10`, `ln`, `log2`, `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `min`, `max`, `round`, `abs`, `ceil`, `floor` | `supported` | |
| `distance` | `not supported` | Not built. |
| Color: `to-rgba`, `rgb`, `rgba` | `supported` | |
| Feature data: `properties`, `geometry-type`, `id` | `supported` | |
| `feature-state` | `not supported` | No feature state. |
| `line-progress` | `not supported` | Not built (needs `line-gradient`). |
| `accumulated` | `not supported` | No clustering. |
| Zoom: `zoom` | `supported` | See † for when a value is evaluated. |
| Heatmap: `heatmap-density` | `not supported` | No heatmap layer. |
| Color relief: `elevation` | `not supported` | No `raster-dem`. |
| String: `upcase`, `downcase`, `concat` | `supported` | |
| `is-supported-script` | `not supported` | Not built. |
| `resolved-locale` | `not supported` | Not built. |
| `split` | `not supported` | Not built. |
| `join` | `not supported` | Not built. |

**Value forms**

| Form | Status | Note |
|---|---|---|
| Legacy function (`{"stops": …}`, or a bare `"type":"identity"`) | `partial` | `type` (`exponential`/`interval`/`categorical`/`identity`), `property`, `default`, `colorSpace` and zoom-and-property stops are honoured. An `identity` function's `default` covers a MISSING property only, not a value of the wrong type. `text-field` and `icon-image` never see this form: both route only an EXPRESSION ARRAY to the parser, so an object resolves to no text / no icon (see those rows). |
| Color strings | `partial` | Hex, `rgb()`/`rgba()`, `hsl()`/`hsla()` and `transparent`. Only a subset of the CSS named colours. |

**Filters**

| Form | Status | Note |
|---|---|---|
| Expression filters | `supported` | A Burst VM evaluates a safe subset; other filters fall back to the managed evaluator with the same result. |
| Legacy filters (`==`, `!=`, `<`, `<=`, `>`, `>=`, `in`, `!in`, `has`, `!has`, `all`, `any`, `none`, keys `$type` and `$id`) | `supported` | Translated to expressions (`LegacyFilterTranslator`). |

---

## Engine extensions

These keys are not in the MapLibre spec. The `x-` prefix keeps them apart from future spec keys.

| Key | Layer | Meaning |
|---|---|---|
| `x-fill-pattern-metres` | `fill` (paint) | Tiles the pattern at a fixed world size, in Web-Mercator metres, instead of at screen size. See [`fill-parity-design.md`](fill-parity-design.md) § "Mode selection — `x-fill-pattern-metres`". |
