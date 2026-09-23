# Depth & render regimes — how 2D and 3D map layers compose

**Status:** design SSOT for the depth architecture. Fill-extrusion (3D buildings) is the **degenerate case**
of the model, and it ships (§ "Fill-extrusion — the degenerate case" below). The **general model**
(§ "The general model — sub-areas" below) is not built. It retrofits fill-extrusion onto the general seam and
adds bridges, tunnels, terrain, and translucent 3D. This doc is the *why*; the open epic `UMR-74` — "3D depth
layers & the depth-regime architecture" holds the order and the acceptance. Where this doc and the code
disagree, the difference is the general model that is not built.

Related: [`meshing-design.md`](meshing-design.md) (§ "Styling as material properties", § "Fill-extrusion and
the raster seat"), `LayerDrawOrder.cs` / `RenderLayerSet.cs` (the queue model).

---

## 1. The problem

Without fill-extrusion, the renderer is **entirely flat**: every other style layer produces coplanar geometry
(on the ground plane / sphere surface) and lives in one **transparent band**, painter-ordered by `renderQueue`,
`ZWrite Off`. Nothing flat needs depth. 3D buildings are geometry that needs a depth buffer — buildings must
occlude one another.

Two facts make "just add depth" the wrong instinct:

1. **The style file is generic.** A MapLibre style is portable *data* — render **order** + **parameters**,
   nothing more. It imposes no semantic constraints: you can place a `fill-extrusion` layer first (under
   everything), interleave 3D and 2D arbitrarily, etc. The renderer cannot assume a "sensible" ordering.
2. **3D-ness is not a property of the layer type.** `fill-extrusion` is always 3D, but a `line` can be a flat
   road, an **elevated bridge**, or a **sunken tunnel** — the same type, three depth behaviours, chosen by
   feature data. So depth participation cannot be inferred from the type token, and certainly not from a
   layer's *position*.

The architecture therefore cannot win by restricting or normalizing the input. It wins by being **robust**:
any ordering must render cleanly.

---

## 2. Principles (settled)

**P1 — The style file is the contract.** The renderer honors any spec-valid style literally: it never
reorders, normalizes, or rejects. A messy style must render **ugly-but-not-broken** — clean, predictable,
merely semantically-weird output (buildings under the map, because that is what was asked), *never*
z-fighting garbage or nondeterministic occlusion. "Sanity" lives only in an optional, non-mutating
**linter** (§ "The general model — sub-areas", item F), never in the render path. Mechanism (render
faithfully) and policy (warn) stay separate.

**P2 — Regime comes from the layer's *resolved geometry/config*, not the type token.** The seam is "the
layer **answers** what its geometry's depth profile is," computed from type + paint + feature attributes —
not a `switch (layerType)`. `fill-extrusion ⇒ elevated` is merely the degenerate case where the answer is
constant.

**P3 — Depth participation is a two-axis policy, not a boolean.** A single `is3D` flag dies at the first
bridge/tunnel. The two independent axes are:

- **writes depth?** — does this geometry occupy the depth buffer (so it can occlude / be occluded)?
- **conforms to depth below?** — does it get *occluded by* the depth already written under it?

**P4 — 2D is painter's-by-default (`ZTest Always`).** Flat layers ignore depth entirely — exactly as labels
already do (`ZTest Always` + Overlay). This is the spec-faithful reading of "later draws over earlier"
applied uniformly, and it is what makes arbitrary orderings work (§ "Why this handles any ordering —
walk-throughs" below).

---

## 3. Three orthogonal axes

The keystone realization: **draw order, depth-writing, and depth-conformance are independent.** Conflating
them (as "put it in the opaque band" would) is the source of every ordering bug.

| axis | source | what it controls |
|---|---|---|
| **order** | `renderQueue` number = style position | who paints over whom (painter's) |
| **writes depth** | the layer's resolved regime → `ZWrite` | whether it occupies the depth buffer |
| **conforms to depth** | the layer's resolved regime → `ZTest` | whether prior depth occludes it |

Corollaries:

- **The opaque/transparent *band* is not a depth lever.** Depth-correctness is `ZWrite`/`ZTest` (material
  state); ordering is the queue *number*. A 3D layer does **not** need to move to the opaque band to get
  depth — it stays at its style-order queue slot and simply writes depth. (§ "Rejected alternatives" below.)
- **A distinct queue per layer preserves style order in *either* band.** Both URP passes sort by
  `renderQueue` first, so monotonic per-layer queues keep style order regardless of band.

### Regime → render state

| regime | writes | conforms | `ZWrite` | `ZTest` | Blend | example |
|---|---|---|---|---|---|---|
| **flat** | no | no | Off | **Always** | alpha | road, fill, water |
| **elevated-3D** | yes | self only | **On** | LEqual | off (opaque) | building, bridge |
| **sunken/conforming** | no* | **yes** | Off* | LEqual | alpha | tunnel (under ground) |
| **surface** | yes | (conformed-*to*) | On | LEqual | — | terrain |

\* a tunnel does not need to *write* depth (nothing conforms to *it*), but it must *test* against the ground
above — the mirror of terrain, which writes the depth tunnels/roads conform to.

---

## 4. Why this handles any ordering — walk-throughs

**Sensible order** (flat road below, building above — Liberty's shape, where buildings are the *last data
layer* and only depth-immune symbols follow):
- Road draws first (painter's). Building draws after with `ZWrite On` and **paints over** the road pixels →
  the road is correctly hidden behind the building. Occlusion comes from **overpaint + draw order**, not from
  the road depth-testing. Building-vs-building resolves via depth. Labels (`ZTest Always`/Overlay) stay on
  top. Correct.

**Adversarial order** (`fill-extrusion` placed *first*, under everything):
- Buildings draw first, self-occlude via depth. Flat layers draw after with `ZTest Always` → paint cleanly
  *over* the buildings. Result: buildings under the map — exactly what the style ordered, **no artifacts**.
  Ugly-but-not-broken (P1).

**Coplanar flats** (two ground fills overlapping): both `ZTest Always` + `ZWrite Off` ⇒ pure painter's, no
z-fight — `ZTest Always` also *removes* the coplanar-depth-fight hazard that `LEqual` would introduce once
anything writes depth.

The single load-bearing choice is **P4**: flat layers ignore depth (`ZTest Always`), so 2D never
depth-couples to 3D. The flat materials use `ZTest LEqual` (`BaseTweaker.ApplyBaseContract`) — an arbitrary
default from a depthless world. Flipping them to `Always` is what generalizes the renderer to any ordering;
the flip is sub-area A of § "The general model — sub-areas" below.

---

## 5. Fill-extrusion — the degenerate case

Fill-extrusion is the minimum for **buildings only**, and it needs no general refactor:

- `fill-extrusion` resolves (trivially, by type) to **elevated-3D**: the material carries `ZWrite On`,
  `ZTest LEqual`, no blend (opaque case) (`FillExtrusionTweaker.ApplyElevatedContract`). It sits at its
  **style-order queue slot in the transparent band**, like every flat layer; only its render state differs.
- Height is extruded **in the vertex shader** along a per-vertex, `sec φ`-scaled extrude-up. The mesh is built
  once; a constant or zoom-driven height is a uniform, and a data-driven height is baked per vertex
  (`FillExtrusion_VertexModify.hlsl`). This is orthogonal to depth but is why buildings are "3D".
- **Flat layers stay at `ZTest LEqual`.** This is correct for the *sensible* orderings real styles use
  (buildings are the last data layer; nothing flat is declared above them). Flat layers are `ZWrite Off`, so
  a ground fill drawn before the buildings leaves no depth to fight. The residual artifact needs a flat
  layer declared *above* the buildings: its `LEqual` test then meets building depth, and it z-fights where
  it is coplanar with a building base. The general `ZTest LEqual → Always` flip is
  sub-area A below; this case does not need it.
- **Depth is a self-contained concern of the building layer:** the depth buffer buildings write is consumed
  only *by buildings themselves* — flat layers below drew earlier and do not read it; symbols above ignore it
  (`ZTest Always`/Overlay). Nothing else in the frame touches building depth. (This is why this case can be
  minimal; terrain breaks this containment — sub-area D below.)

Translucent buildings (`fill-extrusion-opacity < 1`) are sub-area E below.

---

## 6. The general model — sub-areas

This model is not built (open: `UMR-74`). Its seam is "**the layer resolves a two-axis depth-participation
policy from its config**," per-layer and extensible to per-feature. The sub-areas:

- **A. Depth-regime abstraction + refactor (keystone).** Replace the "fill-extrusion ⇒ 3D" special case
  with the general resolve-from-config seam. Flip flat Fill/Line to `ZTest Always` (P4). Retrofit
  fill-extrusion as the degenerate case.
- **B. Elevated lines (bridges).** Read the OSM/OpenMapTiles `layer`/brunnel z-order attribute (the tiles
  carry it; styles fake it with painter's-ordered filtered layers). Positive `layer` → real elevation,
  write + self-occlude. Introduces **per-feature** regime within one line layer (mixed flat + bridged segments).
- **C. Tunnels.** Negative `layer` → sunken geometry; the **conforming** side — occluded by the ground/terrain
  above it (real under camera tilt, where the painter's fake is not).
- **D. Terrain integration.** A ground mesh under everything, depth-writing; flat layers must **drape/conform**
  onto it — the hard case, because it is geometry displacement, not just render state. Terrain is the
  *conformed-to* surface; tunnels are its mirror. This is where building-style "self-contained depth" breaks:
  the flat layers now *read* terrain depth. Relates to `UMR-56` (terrain DEM mesh) and `UMR-53`
  (raster-DEM hillshade).
- **E. Translucent 3D (`fill-extrusion-opacity < 1`).** The depth-prepass variant of fill-extrusion: a
  building tile is one mesh, and per-object back-to-front transparent sort cannot order a shared mesh's own
  walls, so a translucent building needs a **depth prepass** (`ZWrite On`, colour off) + a **colour pass**
  (`ZTest LEqual/Equal`, `ZWrite Off`, blend) — front-surface-correct without triangle sorting. Full OIT
  (translucent-behind-translucent) is out.
- **F. Style linter (DX, non-mutating).** Load-time detection of likely-mistake orderings (a non-3D layer
  above a 3D layer; `fill-extrusion` at the bottom) → developer-console warning. Changes **no** output.

---

## 7. Rejected alternatives

- **Move 3D layers to the opaque *band* (queue < 2500).** Draws them before the entire transparent band,
  breaking style order (a flat layer declared *below* a building would paint over it). Rejected: the band is
  not a depth lever (§ "Three orthogonal axes" above); `ZWrite On` at the style slot gets depth without
  disturbing order.
- **Normalize / reorder 3D layers to a "sensible" slot.** Violates P1 — the renderer would lie about the
  style, break portability (a style tuned elsewhere renders differently here), and be nondeterministic across
  versions.
- **Reject "nonsensical" styles.** They are spec-valid; "nonsensical" is subjective; hostile to authors.
- **A single `is3D` boolean.** Dies at bridges/tunnels, which split along the *conforms* axis P3 introduces.
- **`ZTest LEqual` for flat layers (the depthless-world default).** Wrongly lets a 3D layer occlude a flat
  layer declared *above* it, and reintroduces coplanar z-fight once depth exists. P4 (`Always`) is correct.

---

## 8. Open questions

Each question names its sub-area of § "The general model — sub-areas" above.

- **Terrain draping mechanism** (D): geometry displacement of flat layers onto the terrain mesh vs a
  depth-conforming projection — undecided; the biggest unknown in the general model.
- **Per-feature regime plumbing** (B): how a single line layer's mesh carries mixed flat/elevated features
  and what render state the (shared, per-layer) material must therefore hold.
- **Translucent-3D depth-prepass** (E): pass wiring across the three backends; interaction with URP depth
  priming.
- **Linter rule set** (F): which orderings warrant a warning without false positives on legitimate styles.
