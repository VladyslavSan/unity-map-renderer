# Tile geometry IR — the two-waist unification (design / SSOT)

**Status: designed, DEFERRED — do not start speculatively.** A separate epic that follows Epic A (per-layer
tile processing, `docs/per-layer-tile-processing-design.md`, complete through A7 + merge-step follow-ups).
**Natural trigger: the GeoJSON payload (A8)** — a real non-command-stream format proves the seam rather than
another synthetic fixture. This doc is the SSOT; it captures the 2026-07-15 Opus + Codex work in three passes:
(1) cross-validation of the two-waist conclusion, (2) a refinement round that settled the struct shape and the
open decisions, (3) a benefit review whose verdict is **defer the whole epic — including B1 — until a second
format actually pulls it** (see **§Should we build this yet?** at the end). Bank the design; execute on demand.

## Provenance (how we got here)

- **Epic A / A6 "Decision X"** deferred neutralizing the geometry *encoding* (the `uint[]` command stream on
  `ITileFeature.Geometry`) with the byte-identical-mesh invariant as the reason — a scope fence, not a
  permanent architecture.
- **Round-5** (design.md) proposed exposing the *tile-local* decode buffer (`MvtDecodeJob`'s output) as a
  named `TileGeometryBuffers` — Codex found "the IR already exists unexposed."
- **Round-6** (maintainer) challenged that altitude: `uint[]` — and even the tile-local buffer — still bakes
  in MVT's tile/extent contract, so it is not *universal*. Proposed **geodetic** (`NativeArray<GeoCoordinate>`)
  as the single waist, with an eager whole-tile decode and the double-decoder unification folded in.
- **Cross-validation (Opus architect + Codex adversarial, 2026-07-15)** — this doc's conclusion: the
  maintainer's instinct that `uint[]` is wrong is **correct**, but the single-geodetic-waist flow
  **over-corrects**. The answer is **two monomorphic waists in series**.

## The itch

`ITileFeature.Geometry : uint[]` is MVT's tile-local *command encoding* (MoveTo/LineTo/ClosePath + zigzag
deltas). It is not a universal tile-data representation: a non-MVT format (GeoJSON, MLT) would have to
transcode *into* it. The neutral interface should carry neutral geometry, not one format's wire encoding.

**Two different "IRs" — name the one the itch is actually about.** The fill path carries geometry in three
shapes in series: (1) `uint[]` MVT command stream on `ITileFeature.Geometry` (`DecodedTile.cs`) — the
**interface carrier**; (2) tile-local `double2` flat buffer (`MvtDecodeJob` out) — an **internal working
buffer** downstream of (1); (3) geodetic `NativeArray<GeoCoordinate>` (`TileToGeoJob` out) — Waist 2, already
exists. The maintainer's complaint is about **(1), the interface carrier**. Promoting **(2)** to a named
`TileGeometryBuffers` (Round-5's proposal, B1) does **not** by itself remove (1) — so the epic's *real target*
is neutralizing the interface carrier (decision B, B5), not merely naming the internal buffer.

**Format-neutrality (the real goal) vs space-neutrality (a mirage).** "Universal like geocoordinates" is right
about *format* — one buffer **shape** every format fills natively — but wrong if read as *one buffer for all
coordinate spaces*. A single buffer carrying a `CoordinateSpace` descriptor is **strictly worse**: it turns
"never feed earcut geodetic coords" (THE NAMED FENCE) from a **compile-time impossibility** into a runtime
discipline. Two distinct monomorphic types — tile-local `TileGeometryBuffers` vs geodetic
`NativeArray<GeoCoordinate>` — make "earcut eats only tile-local" a **type fact**. **Monomorphism is the fence.**
So "universal" here means format-neutral, not space-neutral.

## The conclusion: two monomorphic waists in series

```
formats (MVT / MLT / geojson-vt)  ─►  WAIST 1: tile-local TileGeometryBuffers   [format-neutrality collapses here]
                                       (flat coord array + ring/feature offsets; earcut / subdivide / anchor
                                        spacing — every downstream algorithm — runs in this planar frame)
                                            │
                                            ▼
                                 WAIST 2: geodetic NativeArray<GeoCoordinate>    [projection-neutrality — ALREADY EXISTS]
                                       (TileToGeoJob out / ProjectPointsJob<TProj> in)
                                            ▼
                                 world positions (ProjectPointsJob<TProj>)        [projection-specific, swappable]
```

The maintainer's north-star — **no format-polymorphism in the monomorphic middle** — is *satisfied*: it wants
two monomorphic segments (tile-local, then geodetic), each single-implementation, not literally one buffer
type. Format polymorphism collapses at Waist 1; projection polymorphism is already handled at Waist 2 (the
generic `ProjectPointsJob<TProj>`), so the globe/projection-agnostic direction is **already banked** regardless
of what the decoder emits.

## Why NOT a single geodetic waist (the rejected over-correction)

Both reviewers, independently, from source:

1. **Earcut runs in tile-local integer space *before* geodetic conversion.** `TileMeshPipeline.Schedule` order
   is Decode (tile-local) → RingAssembly (tile-local) → **Earcut (tile-local, Stage 3)** → TileToGeo (Stage 4a)
   → Project. Ear-clipping's orientation/convexity are **sign tests** — invariant under affine maps, **not**
   under the nonlinear `atan∘sinh` tile→geo reprojection. Triangulating on geodetic coords would silently
   break every byte-identical fill snapshot and re-open the **globe-winding failure class** the code still
   carries a scar for (`TileMeshPipeline` "an earlier globe-only reversal inverted the globe's front-faces").
2. **Hardcoded scale-dependent epsilons prove tile-local is the working space.** `RingAssemblyJob`'s
   `DegenerateThreshold = 1.0` and `EarcutJob`'s `1e-10 … 1e-14` are calibrated to tile-integer magnitude
   (`[0, 4096]`). Geodetic degrees are a different scale entirely.
3. **Line subdivision and symbol spacing need a tile-relative, zoom-invariant frame.** `StyledLineTileBuilder`
   subdivides the centerline in tile space; `SymbolFeatureExtractor` computes anchor spacing in tile units
   (px→spacing needs `extent`, not degrees — degree-per-meter varies with latitude).
4. **The "what must a non-tiled format do?" test does not hold in a tile renderer.** The design's own Round-2/4
   commit is that **GeoJSON is sliced client-side (geojson-vt)** — and geojson-vt emits extent-scaled
   **tile-local** integer coords, exactly like MVT. There is no non-tiled format in the system; tile-local *is*
   the vector-neutral shape every format slices to. (This is what makes geodetic the wrong altitude for the
   *interchange contract* and the right altitude for the *projection* seam — two different things.)

## Eager whole-tile decode: rejected

Round-6 proposed decoding the whole tile to the IR once, then fanning out. Rejected: today's decode is
**lazy per *selected* feature** (each builder calls `FeatureSelector.SelectFeatures` then decodes only the
selected subset). Eager whole-tile would run transcendental geo-conversion for features **no style renders**
(unused source-layers), on **every tile** — new work, unbounded by the style. The real inefficiency is that a
feature selected by N *style layers* is decoded N times (N = style layers touching that source, often 1 —
**confirm during planning, don't assert**). The fix is **dedupe across selected layers**, not eager whole-tile.
Keep selection upstream of materialization.

## The design

### Type shape + assembly boundary

`TileGeometryBuffers` (new, **`MapRenderer.Jobs`** — it is `NativeArray`, i.e. `Unity.Collections`, so it
**cannot** live on Core's engine-free interface):

```csharp
struct TileGeometryBuffers : IDisposable {   // blittable; Allocator.Persistent; single-owner
    TileId                        Tile;            // provenance metadata — self-describing, NOT a space tag
    double                        Extent;          // (coords are still always tile-local; see below)
    NativeArray<double2>          Vertices;         // flat tile-local (px,py) in [0,Extent] — WAIST 1
    NativeArray<int>              RingOffsets;       // per-ring start into Vertices; ringCount+1 (sentinel)
    NativeArray<int>              RingFeatureIdx;    // which selected-feature each ring belongs to
    NativeArray<TileGeometryType> FeatureGeometryType; // per-FEATURE kind (length = featureCount); ring kind =
                                                       // FeatureGeometryType[RingFeatureIdx[r]]
    // + ring/vertex counts. Dispose mirrors TileMeshBuffers.Dispose (idempotent).
}
```

**Self-describing (`Tile`/`Extent` folded in), by consensus.** Today the tile-local coords travel with their
`(TileId, Extent)` *out-of-band* on `LayerInput`; once the buffer is a standalone, possibly-memoized artifact,
a caller reading it without threading the matching `(TileId, Extent)` is a silent-corruption hazard. Bundling
them makes Waist 1 self-describing for two fields. These are **provenance metadata, not a space discriminator**:
they do not make the buffer able to represent geodetic data, and earcut's signature still only ever accepts
`TileGeometryBuffers` — the monomorphism fence is intact.

**`Vertices`/`RingOffsets`/`RingFeatureIdx` are an extraction; `FeatureGeometryType` is genuinely new.**
`TileMeshPipeline.Schedule` already allocates the first three as private locals, runs `MvtDecodeJob` into them,
and disposes them inline after earcut — promoting them to a named, shared, multi-consumer struct is the bulk of
stage B1. `FeatureGeometryType` does **not** exist yet (grep-empty) — it is added because `RingAssemblyJob`
runs in **Burst** and cannot read the managed `ITileFeature.GeometryType` enum, so the kind must be materialized
**blittable** before classification. It is **per-feature, not per-ring**: no producer emits a mixed-kind feature
(MVT `Feature.type` is singular; geojson-vt maps each feature to one type; the background quad is one feature),
so per-feature type + `RingFeatureIdx` fully determines ring kind. A physical per-ring column is an *optional*
Burst-locality denormalization (drops one `RingFeatureIdx[r]` indirection) — adopt only if profiled, never as a
correctness claim. **Caveat:** this is contingent on Decision A (single-typed features); a true untiled
`GeometryCollection` format would make a per-ring tag load-bearing again, but that is out of scope under A.

**The blittable/managed split lands on the assembly boundary:**
- **Core** keeps the **managed evaluation surface** — `IFeature`/`ITileFeature` (properties, id,
  `GeometryType`). Filters/selection read only these; they never read coordinates.
- **Jobs** owns the **blittable geometry** — `TileGeometryBuffers` + the materializer that fills it.

So the decoded tile is `{ blittable tile-local geometry, managed property bags }`. Properties **cannot** be
blittable (the filter/expression layer needs `string→Value`). `ITileFeature.Geometry : uint[]` is *replaced*
by this split (see open decision B); `MvtFeature` keeps its `uint[]` as MVT's private raw form that the MVT
materializer reads.

### Pluggable-by-encoding on the Burst side (no vtable in Burst)

Mirror the existing **`ProjectionDispatch`** precedent: a managed dispatcher switches on `TileEncoding`
(parallel to A6's `TileDecoders.ForEncoding`) and runs the chosen **concrete** Burst job — Burst never holds a
managed interface. Note a real difference from `ProjectionDispatch`: projections share one input/output shape
(one generic job, N struct instantiations); geometry decoders do **not** share input shape (MVT = command
stream; GeoJSON = coordinate pairs), so this is **N independently-shaped concrete jobs unified only by a common
output** (`TileGeometryBuffers`), dispatched by a managed switch — not a single generic `DecodeJob<TFormat>`.

### The double-decoder unification (its own stage)

`MvtDecodeJob`'s `Execute` is **not** actually polygon-specific — it walks MoveTo/LineTo/ClosePath generically
into paths, the same command walk as managed `MvtGeometry.Decode`. "Polygon-only" describes its *consumer*
(`RingAssemblyJob`), not its decode. So unification collapses from "write a new decoder" to **point the
currently-managed line/symbol consumers at the buffer `MvtDecodeJob` already yields, and retire
`MvtGeometry.Decode`.** A Point feature is a 1-vertex path; a polygon ring is a path `RingAssemblyJob`
classifies — no special-casing, given the per-ring `RingGeometryType` tag.

### Ownership / lifetime

`Allocator.Persistent`, minted **once per (source, tile) worker pass**, disposed at the end of that **same
call** — A5b already runs the mesh and symbol worker passes sequentially inside one kick task, so a single
`using`-scoped buffer suffices (no refcount, no cross-thread handoff). This is a **genuinely new** ownership
question — it is native memory that must be disposed exactly once under job-safety rules, *unlike* A4's
`SharedTileDecode` (pure managed data, GC-reachable, no dispose). It does **not** round-trip through the
`MeshDataArray`/`ApplyAndDisposeWritableMeshData` boundary — that is main-thread GPU-bound *output*; this is
worker-thread decode *scratch*. Two unrelated disposal domains.

### GeoJSON's Stage-1 is a slicer, not a switch case

Under the two-waist model, GeoJSON reaching Waist 1 (tile-local) needs the **inverse of `TileToGeoJob`** —
reproject to Mercator + clip to tile bounds + quantize to `[0, extent]` — **a job that does not exist yet**,
structurally ≈ reimplementing geojson-vt's core. (This corrects Round-6's "GeoJSON flows in natively" framing:
under two-waist, *MVT* is the no-op — already tile-local — and *GeoJSON* does the real slicing work.) Do not
scope A8's GeoJSON as "add a case to the encoding switch."

## Correctness landmines (mandatory teeth for the stages that touch them)

1. **Blittable feature-kind, ranked #1 (resolved: per-feature, contingent on Decision A).** `RingAssemblyJob`'s
   signed-area classification would treat a LineString ring as a spurious polygon exterior/hole —
   **silent triangulation corruption, not a crash** — if it can't tell the ring's kind. The kind info is
   **required and new** (Burst can't read the managed `GeometryType` enum), but it is carried **per-feature**
   (`FeatureGeometryType`, indexed via `RingFeatureIdx`), *not* per-ring: no producer emits a mixed-kind feature.
   Gate area-classification on `FeatureGeometryType[RingFeatureIdx[r]] == Polygon`. This holds only while
   Decision A (single-typed features) holds; a future true `GeometryCollection` format would reopen the per-ring
   need — a documented contingency, not a live risk.
2. **Ring-length filter mismatch, ranked #2.** Line discards `< 2`-point rings; fill discards `< 3`-point
   rings. The shared buffer must carry **all** rings unfiltered; each consumer applies its **own** downstream
   length filter. The filter must not move into the shared decode step (it would starve the other consumer).
3. **THE NAMED FENCE (biggest landmine).** Once a tile-local shared buffer and the geodetic Waist 2 coexist,
   "just feed earcut/subdivide the geodetic version" is one wrong line away — and the epsilon evidence (§Why
   NOT, point 2) makes it a real regression, not a theoretical one. This must be a written fence in every stage
   plan, not an absence of instruction.
4. **Feature-then-ring order/identity.** Re-expressing "iterate my rings" as "iterate `RingOffsets` slices for
   my feature's ordinal" needs the shared decode to preserve exact feature-then-ring order (both decoders are
   single-pass state machines, so *likely* fine) — pin it with an A3-style differential test, and add a
   `MvtFeature.Ordinal` (small, mechanical, a prerequisite either convergence option needs).
5. **The B3 fused-`RingAssemblyJob` fence (sibling to THE NAMED FENCE).** `RingAssemblyJob` is a **fused,
   fill-only** stage: it does area-based degenerate-filtering **and** outer/hole polygon classification in one
   pass. When B3 puts line onto the shared buffer, the tempting shortcut is reusing `RingAssemblyJob`'s output
   (`OutPolyOuterRingIdx` etc., sitting right there, looks like free ring structure). Line must **not**: it needs
   its own iteration over raw `RingOffsets` slices filtered to `FeatureGeometryType == LineString`, with its own
   `Count < 2` filter, **zero `RingAssemblyJob` interaction** (line has no polygon/hole concept; its filter is a
   count threshold, fill's is a shoelace-area threshold fused into classification — structurally different
   mechanisms, not a shared length constant). Write this fence into B3's plan explicitly. It also means B3 is a
   genuine control-flow rewrite → **verified-equivalent, not byte-identical**.

## Stage sequence (each independently green, A1–A7 discipline)

| Stage | Scope | Bar | Key teeth / prereq |
|---|---|---|---|
| **B1** | Extract `TileGeometryBuffers` from `TileMeshPipeline`'s private locals; fill-only, no seam yet | **byte-identical** | fill snapshots unchanged; dispose still frees every array. *Safe pure-refactor down-payment, landable without the A8 trigger* |
| **B2** | `ITileGeometryMaterializer` seam + MVT impl (wraps `MvtDecodeJob`); fill routes through it. **Trigger: A8/GeoJSON** | byte-identical | a non-MVT tile-local fixture flows through unchanged Stages 2–4; per-layer materialize (no union yet) |
| **B3** | Line onto the buffer; delete its `MvtGeometry.Decode` call | **verified-equivalent** (differential oracle, not pixel-byte) | line snapshots equal the managed-decode oracle; `FeatureGeometryType` gate (#1); unfiltered rings (#2); **the fused-`RingAssemblyJob` fence (#5)** — line iterates raw slices, own `Count<2`, zero `RingAssemblyJob` contact |
| **B4** | Symbol onto the buffer; **move symbol's coordinate half Core→Unity**; retire `MvtGeometry.Decode` | verified-equivalent | `SymbolProcessorParityTests`; Core has no `NativeArray` ref |
| **B5** *(decision B)* | Remove `uint[]` from `ITileFeature` (if adopted) | behaviour-preserving | structural: no interface `.Geometry` reads |
| **B6** *(measure-gated)* | Shared selected-union materialization (Option A) | byte-identical | only if a trace shows per-layer decode matters |

**Byte-identical vs verified-equivalent:** B1/B2 stay byte-identical (pure extraction, fill's decode/earcut
math and input space unchanged). B3/B4 need **verified-equivalent** the moment a consumer swaps its own
`MvtGeometry.Decode` for the shared buffer — a control-flow rewire, even with identical math — using the A3
`SymbolProcessorParityTests` differential-oracle template, not a pixel snapshot match.

Throughout: **Waist 2 (`TileToGeoJob → ProjectPointsJob<TProj>`) stays exactly where it is.**

## Resolved decisions (2026-07-15 Opus + Codex consensus)

- **(A) "Every source is tiled" → YES, but confidence downgraded.** The two-waist / tile-local design is right
  *because* everything is tiled: the only scenario a geodetic geometry seam would win is a genuinely
  **non-tiled, whole-dataset** source (a polygon spanning many tiles → must earcut in a projected plane), and
  the architecture rules that out by tiling everything. **Caveat both signed:** the load-bearing premise —
  "geojson-vt emits tile-local integer coords" — is **external library knowledge, not code-verified**
  (`grep -ri geojson` across `Assets/Code` = zero hits; no GeoJSON source/slicer/fixture exists in-repo). So
  make **B2's "a non-MVT tile-local fixture flows through unchanged" bar double as Decision A's falsification
  test** — if a synthetic non-MVT fixture can't be expressed tile-local without contortion, revisit A.
- **(B) Remove `uint[]` from `ITileFeature` → YES, entirely (not null), last, gated twice.** Make the interface
  a pure evaluation surface (`Id`/`Properties`/`GeometryType`, all already on `IFeature`); geometry travels only
  in the blittable buffer, joined by ordinal. `GeometryType` **stays** (selection needs it; it's also load-bearing
  for ring-kind per landmine #1). Fire B5 on **(a)** a grep proving zero production `.Geometry` readers after
  B3/B4, **AND (b)** A8/GeoJSON actually landing. **Strengthened by in-tree evidence:**
  `TileBackgroundLayerProcessor.cs:37` `FullExtentRingGeometry = { 9, 0, 0, 26, 8192, … }` is a hand-authored
  MVT zigzag command stream built only to satisfy `InMemoryTileFeature.Geometry : uint[]` for a non-MVT
  background quad — a *present-tense, already-merged* instance of the exact anti-pattern the complaint named.
  B5 lets it materialize a plain 4-vertex ring instead. **Known cost, stated plainly:** this splits the IR
  across Core (managed evaluation) and Jobs (blittable geometry), joined by ordinal — real added indirection,
  but not a *new* kind of coupling (`MvtFeature` already holds `uint[]` + `Properties` as two fields joined by
  identity; B5 moves the join from same-object to matching-ordinal-across-assemblies).
- **(C) Convergence → Option B first, keyed by SELECTED-FEATURE ORDINAL.** A per-kick-task lazy buffer memoized
  by *selected-feature ordinal* (the `SharedTileDecode` "first-arrival decodes, gate the rest" idiom one level
  down): this preserves lazy-per-selected-feature **and** dedupes the real waste — a feature selected by N style
  layers decoded N times (`SymbolFeatureExtractor.cs:87` is a verified present-tense second decode of the same
  `uint[]`). *Not* source-layer-name (two style layers filtering the same source-layer differently would break
  it or force eager decode). Needs `MvtFeature.Ordinal`. **Option A** (union in the coordinator) widens
  `ITileLayerProcessor.ProcessOnWorker` — a contract stable across A1–A7 — so it is a separate, measure-gated
  later stage, not the first cut.

## Risk ranking

1. Classifying rings without the blittable `FeatureGeometryType` → silent triangulation corruption (#1 above).
2. Ring-length filter mismatch silently starving a consumer (#2).
3. `ITileLayerProcessor` widening (Option A) — wide blast radius; mitigated by doing Option B first.
4. GeoJSON's Waist-1 slicer under-scoped as "another decoder case" (planning-scope risk for A8, not a
   correctness risk to MVT).
5. `MvtFeature.Ordinal` — small, mechanical, but a concrete prerequisite not yet built.

## Grounding (verified file:symbol touch points, 2026-07-15)

`MapRenderer.Jobs/`: `MvtDecodeJob` (kind-agnostic path walk; tile-local `double2` out), `TileToGeoJob`
(tile-local→geodetic, projection-independent), `ProjectPointsJob<TProj>` (geodetic→world, generic over
`IProjection`), `TileMeshPipeline.Schedule` (the Decode→RingAssembly→Earcut→TileToGeo→Project order + the
private buffers to promote), `RingAssemblyJob` (signed-area classification + `DegenerateThreshold`), `EarcutJob`
(the tile-scale epsilons). `MapRenderer.Core/Mvt/`: `MvtGeometry.Decode` (the managed twin to retire),
`MvtModels.MvtFeature` (keeps `uint[]`, needs `Ordinal`). `MapRenderer.Unity/Rendering/Meshing/`:
`StyledFillTileBuilder`, `StyledLineTileBuilder`. `MapRenderer.Core/Tiles/`: `IDecodedTile`/`ITileFeature`/
`TileGeometryType` (managed-property view). `MapRenderer.Jobs/ProjectionDispatch` (the Burst-dispatch precedent).
**Evidence for the benefit review:** `TileBackgroundLayerProcessor.cs:37` `FullExtentRingGeometry` (the
hand-authored MVT zigzag quad — the in-tree anti-pattern B5 removes); `SymbolFeatureExtractor.cs:87`
`MvtGeometry.Decode(feature.Geometry)` (the present-tense second decode B4/Option-B removes — but note A4 already
shared the expensive protobuf parse via `SharedTileDecode`, so the residual duplicate is only the cheap
command-walk).

## Should we build this yet? — Deferred (demand-pull on A8)

A 2026-07-15 benefit review (Opus + Codex, independent, then reconciled) asked *not* "is the design correct"
(it is) but "is the epic worth executing now." **Both said no — defer until a second format actually pulls it,
and do not pre-build B1 as speculative scaffolding.**

**The whole payoff ledger** (all verified): (1) delete `SymbolFeatureExtractor`'s second decode — real but tiny
and invisible (off-thread, on no profile, and only the *cheap* command-walk remains after A4 shared the parse);
(2) delete the `TileBackgroundLayerProcessor.cs:37` hand-authored zigzag quad — genuinely ugly but ~10 lines,
correct, shipped; (3) `ITileFeature` becomes a pure evaluation surface — interface honesty, zero runtime effect;
(4) a second format slots in without transcoding into MVT commands — **the one structurally real payoff, and
entirely conditional on that format existing.**

**Nothing is blocked without it.** GeoJSON can ship today via the same transcode-into-`uint[]` shim the
background quad already proves works in production. Globe/projection is already handled by Waist 2
(`TileToGeoJob`→`ProjectPointsJob<TProj>`), independent of what the decoder emits. Perf is on no profile; the
duplicate is the cheap command-walk. The one *real standing* cost of inaction is DRY/divergence risk — two MVT
decoders (`MvtDecodeJob` Burst vs `MvtGeometry.Decode` managed) kept identical by discipline, not construction —
which is separable from GeoJSON-readiness.

**Why not even B1:** B1 promotes private locals to a named struct with no second consumer until B2+ (A8-gated).
Alone it fixes nothing, unblocks nothing, is never measured — inventory for a trigger that may never fire. This
is the same eager-work this doc already rejects in *Eager whole-tile decode: rejected*; the logic scales to the
whole epic.

**Verdict:** bank this design (cheap to capture, now fully worked out); **execute B1→B5 as one motivated arc when
A8/GeoJSON (or MLT) is scheduled**, so every stage has a live consumer. Two standalone carve-outs a maintainer
*could* pull independently, both still worth gating: replace the `TileBackgroundLayerProcessor` zigzag quad with
direct synthesis (~1 file, no seam risk — a cleanup, not this epic); and retire `SymbolFeatureExtractor`'s own
decode on divergence-risk grounds (but gate on a profile before paying the Core→Unity symbol-coordinate
migration). Neither should gate anything else.
