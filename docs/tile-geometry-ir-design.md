# Tile geometry IR — the two-waist model (design / SSOT)

**Status:** the model below ships. A decoded tile owns its tile-local geometry for the tile's whole life,
and format handling stops at the decoder. Read with `docs/per-layer-tile-processing-design.md` (the
per-layer pipeline this sits under), `docs/job-scheduling-design.md` (how the Burst stages downstream of
Waist 1 chain), and `docs/coordinates-and-projections.md` (the coordinate frames and §7.1's winding
contract).

## The itch

A vector tile's geometry must reach the mesh builders in a shape no single format owns. MVT's wire form is
a tile-local **command encoding** — MoveTo/LineTo/ClosePath plus zigzag deltas. Carrying that encoding on
the neutral tile interface has two costs: every other format (GeoJSON, MLT) must transcode *into* it, and a
format-less producer must hand-author zigzag command words to state something as simple as one rectangle.
The neutral interface carries neutral geometry, not one format's wire encoding.

**Format-neutrality is the goal; space-neutrality is a mirage.** "Universal like geocoordinates" is right
about *format* — one buffer **shape** every format fills natively — and wrong if read as *one buffer for all
coordinate spaces*. A single buffer carrying a `CoordinateSpace` descriptor is worse than two types: it
turns "never feed earcut geodetic coordinates" from a compile-time impossibility into a runtime discipline.
Two distinct monomorphic types — tile-local `TileGeometryBuffers` and geodetic `NativeArray<GeoCoordinate>` —
make "earcut eats only tile-local" a **type fact**. **Monomorphism is the fence.**

## The model: two monomorphic waists in series

```
formats (MVT / GeoJSON / MLT)  ─►  WAIST 1: tile-local TileGeometryBuffers   [format-neutrality collapses here]
                                     (flat coord array + ring/feature offsets; earcut, ring clipping,
                                      line subdivision, symbol anchor spacing — every downstream
                                      algorithm — runs in this planar frame)
                                          │
                                          ▼
                               WAIST 2: geodetic NativeArray<GeoCoordinate>  [projection-neutrality]
                                     (TileToGeoJob out / ProjectPointsJob<TProj> in)
                                          ▼
                               world positions (ProjectPointsJob<TProj>)      [projection-specific, swappable]
```

The north star — **no format polymorphism in the monomorphic middle** — is satisfied by two monomorphic
segments in series, each single-implementation, not by one universal buffer type. Format polymorphism
collapses at Waist 1; projection polymorphism is handled at Waist 2 by the generic `ProjectPointsJob<TProj>`.
The projection-agnostic direction is therefore banked regardless of what a decoder emits, and **Waist 2 stays
where it is** — this design changes only what feeds it.

## Why not a single geodetic waist

Moving the interchange contract up to geodetic coordinates looks like the cleaner over-correction. It is
wrong for four reasons, and each still constrains where the seam may sit.

1. **Triangulation runs in tile-local space, before geodetic conversion.** `FillMeshGraph.Schedule` chains
   `RingClipJob` → `RingAssemblyJob` → `EarcutJob` in the tile-local frame, and only then `TileToGeoJob` →
   `ProjectPointsJob<TProj>`. Ear-clipping's orientation and convexity decisions are **sign tests**, which
   are invariant under an affine map but not under the nonlinear `atan ∘ sinh` tile→geo reprojection.
   Triangulating on geodetic coordinates changes which ears clip and which rings classify as holes.
2. **Scale-dependent epsilons prove tile-local is the working space.** `RingAssemblyJob`'s
   `DegenerateThreshold = 1.0` and `EarcutJob`'s `1e-10 … 1e-14` comparisons are calibrated to tile-integer
   magnitude (`[0, Extent]`, typically 4096). Degrees are a different scale entirely, so the same constants
   would mean something else.
3. **Line subdivision and symbol spacing need a tile-relative, zoom-invariant frame.**
   `StyledLineTileBuilder` subdivides a centerline in tile space; `SymbolFeatureExtractor` computes anchor
   spacing in tile units. Converting a pixel spacing needs `extent`, not degrees — degrees per meter vary
   with latitude.
4. **Everything is tiled.** There is no whole-dataset source in the system. GeoJSON is sliced client-side by
   `GeoJsonTileSlicer`, which emits extent-scaled tile-local integer coordinates exactly as MVT does. The
   one case a geodetic seam would win — a polygon spanning many tiles, which must triangulate in a projected
   plane — is ruled out by tiling every source. Tile-local *is* the shape every format slices to.

Geodetic is therefore the wrong altitude for the **interchange contract** and the right altitude for the
**projection seam**. They are two different seams, and the model keeps both.

## Why decode is eager and whole-tile

A tile is decoded **once, at fetch completion** (`TileDecodeDispatch.DecodeAsync`, the single dispatch site
both `ITileFeatureSource` implementations route through), and materialization runs inside that decode for
**every** source-layer in the tile, including layers no style names.

- **The structural reason.** A layer's geometry is written once, through a lockstep-checked `AdoptGeometry`
  behind a `{ get; private set; }`. Lazy materialization needs a second write, on a second thread, to a tile
  already handed to other owners — which that shape refuses. Disposal is identical either way, so laziness
  buys nothing it does not immediately spend.
- **Waist-1 decode is integer work.** `MvtDecodeJob` has **zero** `math.*` calls: zigzag-varint decode plus
  cursor accumulation into `double2`. The transcendentals (`atan`, `exp`, `pow`) live in `TileToGeoJob`, a
  later node whose volume is a function of what survives clipping and triangulation, never of how many
  features Waist 1 decoded. Decoding a whole tile adds no geodetic conversion work.
- **The cost of decoding what no style renders is bounded and small.** On the committed `openmaptiles`
  fixtures against `liberty.json`, source-layers no style layer names account for **0.0 %–1.1 %** of a
  tile's geometry (only `mountain_peak` appears at all). Measured 2026-08-09.
- **What it avoids is an order of magnitude larger.** Materializing per (style layer, consumer) instead of
  per (source-layer, decode) re-decodes the same source-layer once per style layer that names it —
  **2.7×–15.5×** duplication on the same corpus, with `transportation` named by 61 `liberty.json` layers.
  Materializing once per source-layer per decode is what keeps that duplication out, and the count is
  style-independent.

The accepted price is **peak resident memory**. A decoded tile holds a median of **~552 KB** and a peak of
**967 KB** of Waist-1 buffers across its **8–12** source-layers (measured 2026-08-09, same corpus). Nothing
caps concurrent kicks: `MapViewConfig.MaxMeshBuildsPerTick` (default 2) rate-limits how many kicks *start*
per tick, not how many are in flight, and a parked symbol build's reference can outlive the kick that
created it. Residency is therefore self-limiting in practice and unbounded in principle — quantified, with
the remedy named, in `docs/per-layer-tile-processing-design.md` §"Eager decode: peak resident decoded-tile
memory".

## The design

### Type shape and the assembly boundary

`TileGeometryBuffers` (`MapRenderer.Jobs`) is Waist 1. It holds `NativeArray`s, so it cannot sit on an
engine-free Core interface.

```csharp
struct TileGeometryBuffers : IDisposable {   // blittable; Allocator.Persistent; single-owner
    TileId                        Tile;                // provenance — not a space tag
    double                        Extent;              // quantization range of Vertices
    NativeArray<double2>          Vertices;            // flat tile-local (x, y) in [0, Extent] — WAIST 1
    NativeArray<int>              RingOffsets;         // per-ring start into Vertices; RingCount + 1 (sentinel)
    NativeArray<int>              RingFeatureIdx;      // which feature each ring belongs to, in decode order
    NativeArray<TileGeometryType> FeatureGeometryType; // per-FEATURE kind; ring kind =
                                                       // FeatureGeometryType[RingFeatureIdx[r]]
    // + ring / vertex / feature counts, reported by the producing job.
}
```

`Tile` and `Extent` are **provenance metadata, not a space discriminator**. A standalone buffer whose
`(TileId, extent)` travelled out-of-band would be a silent-corruption hazard for any caller that failed to
thread the matching pair; folding them in makes the buffer self-describing. Carrying them does not let the
buffer represent geodetic data, and no signature downstream accepts anything but `TileGeometryBuffers` — the
monomorphism fence holds.

`FeatureGeometryType` is the one column that exists only for this IR. Ring kind cannot be recovered from
coordinates: a LineString ring and a polygon ring are the same shape of data. It is **per-feature, not
per-ring**, because no producer emits a mixed-kind feature (MVT's `Feature.type` is singular; the GeoJSON
slicer maps one source feature to one kind), so the per-feature column plus `RingFeatureIdx` determines ring
kind fully. A physical per-ring column is an optional Burst-locality denormalization — adopt it only on a
profile, never as a correctness claim.

**The blittable/managed split lands on the assembly boundary:**

- **Core** keeps the **managed evaluation surface** — `IFeature` (id, properties, `GeometryType`) and the
  `TileGeometryType` enum. Filters and selection read only these; they never read coordinates.
- **Jobs** owns the **blittable geometry** — `TileGeometryBuffers`, the materializers that fill it, the
  decoders, and the decoded-tile types (`IDecodedTile` / `ITileLayer`).

So a decoded tile is `{ blittable tile-local geometry on the layer, managed property bags on the features }`.
Properties cannot be blittable, because the filter and expression layer needs `string → Value`. Nothing on
the feature carries coordinates, and the evaluation surface carries no geometry-join concern either: the
join rides **beside** the feature, as the ordinal in the `(Ordinal, Feature)` pairs `FeatureSelector` returns
(`MapRenderer.Jobs/Tiles/FeatureSelector.cs`).

### Ownership and lifetime

**The decoded tile is reference-counted; the buffers inside it are lent, never shared.** Two mechanisms, at
two scales, and they do not interact.

*At tile scale.* `TileDecodeDispatch.DecodeAsync` mints a `SharedDisposable<IDecodedTile>`
(`MapRenderer.Core.Lifetime`) once per fetched tile, born with one reference belonging to whoever received
it. The surface is `Value` / `Acquire()` / `Release()`: a read or an `Acquire()` after the last release is a
use-after-free, an unbalanced `Release()` is a contract violation, and the release that takes the count to
zero disposes the tile. This is what lets a parked
symbol build survive: the park `Acquire()`s while the kick's reference is still live, so the drain reads the
*same* decoded tile rather than re-decoding it. A decode cannot happen without an owner, because the owner
exists before the tile does.

Every abandonment path funnels to one of four release sites, so the obligation is one line per chokepoint
rather than scattered across every exit:

| abandonment class | funnel |
|---|---|
| a record leaves `_loaded` (cover change, eviction, restyle, teardown) | `TileManager.RenderTeardownRecord` |
| a fetch completes with nobody to consume it | `PendingDisposalQueue.DiscardFetchOutcome` |
| the kick's own transferred reference | `TileManager.KickMeshBuild`'s pool lambda |
| a parked symbol entry is dropped | `SymbolSubsystem.DrainAndDiscardParkedBuilds`, plus the pump's ct-drop and drain |

A release obligation that lives only in a dispatched delegate's `finally` is unreachable whenever the
dispatcher can skip the body on a cancelled token. `IWorkScheduler.Schedule` never skips — it polls its
token rather than being pushed by it — and the delegate's own cancellation check sits *inside* the `try`,
so the `finally` runs on every exit.

*At buffer scale — two tiers of ownership.* A materializer **transfers** the buffer it mints to the decoded
`ITileLayer`, which owns it for the tile's whole life (`MvtLayer.Geometry`, `{ get; private set; }`, written
once through a lockstep-checked `AdoptGeometry`). The layer then **lends**: every consumer **borrows** the
buffer it reads off `ITileLayer.Geometry`, and must never dispose it, retain it past the decode scope, or
mutate it. The only buffer a consumer owns is one it **derived** for itself (`AdoptDerivedLists`, which
*copies* the per-feature kind column so the source stays intact) or one it minted because it *is* the
producer.

Getting the tier wrong fails differently in each backing mode, so the two must not be read as one rule. An
array-backed buffer — every buffer a consumer borrows — frees three real `NativeArray`s, so a second owner
is a **loud double free**. A list-backed buffer's public fields are `AsArray()` views, and disposing a view
is a **silent no-op** under Collections 6.5.0, so the same mistake there leaks with every test green.

This native memory never round-trips through the `MeshDataArray` /
`ApplyAndDisposeWritableMeshData` boundary. That is main-thread, GPU-bound *output*; this is worker-thread
decode *scratch*. Two unrelated disposal domains.

### Pluggable by encoding, not by vtable

Burst cannot hold a managed interface, so the decoder seam mirrors the `ProjectionDispatch` precedent: a
managed dispatcher switches on the encoding and runs the chosen **concrete** Burst job. One difference from
projections matters. Projections share an input and output shape, so one generic job with N struct
instantiations covers them. Geometry decoders do **not** share an input shape — MVT is a command stream,
GeoJSON is coordinate pairs — so this is **N independently-shaped concrete jobs unified only by their common
output**, `TileGeometryBuffers`. It is not a single generic `DecodeJob<TFormat>`.

### GeoJSON's Waist 1 is a slicer, not a switch case

Reaching Waist 1 from GeoJSON needs the inverse of `TileToGeoJob`: reproject to Mercator, clip to tile
bounds, and quantize to `[0, Extent]`. That is real work, not a case in an encoding switch —
`GeoJsonTileSlicer` is a clean-room slicer derived from RFC 7946 that does it. Under the two-waist model MVT
is the format for which Waist 1 is nearly a no-op, because MVT arrives tile-local already; every other
format pays the slicing cost. Scoping a new format's Stage 1 as "add a decoder case" under-scopes it.

## Invariants the mechanism must hold

1. **A ring is classified by its feature's kind, never by its area alone.** `RingAssemblyJob`'s signed-area
   classification would read a LineString ring as a spurious polygon exterior or hole — silent triangulation
   corruption, not a crash. Area classification is gated on
   `FeatureGeometryType[RingFeatureIdx[r]] == Polygon`. The column is cleared on allocation, so an unfilled
   element reads `Unknown` and every consumer's kind gate rejects it: a producer that forgets to fill it
   renders nothing rather than something wrong.
2. **The shared buffer carries all rings unfiltered; each consumer applies its own length filter.** Line
   discards rings below 2 points, fill discards rings below 3. Moving either filter into the shared decode
   starves the other consumer.
3. **THE NAMED FENCE.** A tile-local Waist 1 and a geodetic Waist 2 coexist, so "just feed
   earcut or subdivide the geodetic version" is one line away, and the epsilon evidence above makes it a
   real regression rather than a theoretical one. The fence is held by the type: `TileGeometryBuffers` has no
   coordinate-space tag, no geodetic overload, and is never reused to hold geo coordinates.
4. **Feature-then-ring order and identity are preserved end to end.** A consumer re-expresses "iterate my
   rings" as "iterate the `RingOffsets` slices whose `RingFeatureIdx` names my feature", which only works
   while decode order survives. `RingFeatureIdx` must never be compacted, sorted, deduped, or re-based —
   the clip stage carries original feature indices through for this reason.
5. **A line consumer does not reuse `RingAssemblyJob`'s output.** `RingAssemblyJob` is a fused, fill-only
   stage: it does area-based degenerate filtering *and* outer/hole classification in one pass. Line has no
   polygon or hole concept, and its filter is a count threshold rather than a shoelace-area threshold, so it
   iterates raw `RingOffsets` slices filtered to `LineString` with zero `RingAssemblyJob` interaction. The
   two are structurally different stages that happen to read the same buffer.
6. **Ring winding is not part of the Waist-1 contract.** A producer may emit rings of either orientation;
   `RingAssemblyJob` derives the exterior sign per feature and classifies holes as the opposite sign. What a
   producer owes the buffer is only that one feature's own rings are mutually consistent. The canonical-CCW
   rule in `docs/coordinates-and-projections.md` §7.1 governs *tessellator triangle output*, not decoded
   input rings.
7. **A producer that yields no features yields no buffer.** Every materializer early-outs on
   `featureCount == 0`, not on a zero ring total. A zero ring total is reachable with features present —
   every feature carrying no paths — and would produce a buffer whose `FeatureCount` is 0 beside a non-empty
   `ITileLayer.Features`. Any consumer that sizes per-feature columns from one and indexes them by ordinals
   drawn from the other then mis-buckets silently.

## Rejected alternatives

- **One buffer with a `CoordinateSpace` tag.** Demotes the earcut fence from a type fact to a runtime
  discipline, and buys nothing the two monomorphic types do not already give.
- **A geodetic interchange contract (a single geodetic waist).** Four independent failures, above.
- **A per-ring kind column as the primary representation.** No producer emits a mixed-kind feature, so it
  stores the same information redundantly. It becomes load-bearing only for an untiled format with true
  `GeometryCollection` features, which nothing in the system has.
- **Lazy, per-selected-feature materialization.** Preserves the property that forces format to stay visible
  downstream of parse, dedupes only the waste, and mutates a published tile off-thread.
- **A geometry-join ordinal on the evaluation surface.** An `Ordinal` member on the neutral feature
  interface would put a geometry concern on a surface that exists for filters and expressions. The join
  rides beside the feature instead, in `FeatureSelector`'s `(Ordinal, Feature)` pairs.

## Grounding (file:symbol touch points)

`MapRenderer.Jobs/Geometry/`: `TileGeometryBuffers` (Waist 1 — the fence, the two backing modes, the
ownership tiers), `ITileGeometryMaterializer` + `PathGeometryMaterializer` (the format-less producer),
`RingClipJob`, `RingSelectJob`.
`MapRenderer.Jobs/Mvt/`: `MvtDecodeJob` (kind-agnostic path walk, no `math.*`), `MvtGeometryMaterializer`,
`MvtModels.MvtLayer.Geometry` / `AdoptGeometry` (write-once buffer ownership).
`MapRenderer.Jobs/Tiles/`: `IDecodedTile` / `ITileLayer`, `FeatureSelector` (the ordinal join), `GeoJsonTile`.
`MapRenderer.Jobs/Fill/`: `FillMeshGraph.Schedule` (the clip→assemble→earcut→tile-to-geo→project order),
`RingAssemblyJob` (signed-area classification + `DegenerateThreshold`), `EarcutJob` (the tile-scale epsilons).
`TileToGeoJob` (tile-local→geodetic), `ProjectPointsJob<TProj>` (geodetic→world),
`ProjectionDispatch` (the Burst-dispatch precedent).
`MapRenderer.Core/`: `Tiles/TileGeometryType` (the enum the evaluation surface needs),
`Expressions/IFeature`, `Lifetime/SharedDisposable`, `GeoJson/GeoJsonTileSlicer`.
`MapRenderer.Unity/Rendering/Meshing/`: `StyledFillTileBuilder`, `StyledLineTileBuilder`.
`MapRenderer.Unity/Text/`: `SymbolFeatureExtractor` (reads `TileGeometryBuffers` off `ITileLayer.Geometry`;
it does not decode).
`MapRenderer.Unity/Rendering/Tile/`: `TileManager.RenderTeardownRecord` / `KickMeshBuild`,
`PendingDisposalQueue.DiscardFetchOutcome`, `Processing/TileDecodeDispatch.DecodeAsync`.
