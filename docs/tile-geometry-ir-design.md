# Tile geometry IR — the two-waist unification (design / SSOT)

**Status: ✅ EXECUTING — the trigger fired.** A separate epic that follows the tile-pipeline unification
(`docs/per-layer-tile-processing-design.md`, complete through A7). Its natural trigger was **a second format
actually pulling it**, and that has happened: the GeoJSON source is scheduled and its carrier question resolved
**IR-first**, so B1→B5 run as one motivated arc with a live consumer downstream. **B1 has landed.**

The former *"defer the whole epic, including B1"* verdict at the end (§Should we build this yet?) is
**historical** — read it for the reasoning, not as a live instruction.

## The itch

`ITileFeature.Geometry : uint[]` is MVT's tile-local *command encoding* (MoveTo/LineTo/ClosePath + zigzag
deltas). It is not a universal tile-data representation: a non-MVT format (GeoJSON, MLT) would have to transcode
*into* it. The neutral interface should carry neutral geometry, not one format's wire encoding.

The fill path carries geometry in three shapes in series: (1) `uint[]` MVT command stream on
`ITileFeature.Geometry` (`DecodedTile.cs`) — the **interface carrier**; (2) tile-local `double2` flat buffer
(`MvtDecodeJob` out) — an **internal working buffer** downstream of (1); (3) geodetic
`NativeArray<GeoCoordinate>` (`TileToGeoJob` out) — already exists. The itch is about **(1), the interface
carrier**. Promoting **(2)** to a named `TileGeometryBuffers` does **not** by itself remove (1) — so the real
target is neutralizing the interface carrier (decision B, stage B5), not merely naming the internal buffer.

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

> ⚠️ **Waist 1 as drawn is the TARGET, not what B1–B5 shipped** (purpose audit, 2026-08-08 — see
> §"Purpose audit" below). In the shipped code Waist 1 is not a waist but a **comb of 108 independent
> materialization points per tile** (one per style layer per consumer kind, `liberty.json`), each constructing
> its own producer, because materialization sits **downstream** of per-layer feature selection. Format
> polymorphism therefore does **not** collapse here yet: MVT command words still reach every consumer on the
> feature (`IMvtGeometryCarrier`), and `new MvtGeometryMaterializer(…)` is hard-coded at all three consumers.
> Read this diagram as the north star the epic moved toward, not as a description of the tree.

The north-star — **no format-polymorphism in the monomorphic middle** — is satisfied by two monomorphic segments
(tile-local, then geodetic), each single-implementation, not literally one buffer type. Format polymorphism
collapses at Waist 1; projection polymorphism is already handled at Waist 2 (the generic
`ProjectPointsJob<TProj>`), so the globe/projection-agnostic direction is **already banked** regardless of what
the decoder emits.

## Why NOT a single geodetic waist (the rejected over-correction)

1. **Earcut runs in tile-local integer space *before* geodetic conversion.** `FillMeshPipeline.Schedule` order
   is Decode (tile-local) → RingAssembly (tile-local) → **Earcut (tile-local, Stage 3)** → TileToGeo (Stage 4a)
   → Project. Ear-clipping's orientation/convexity are **sign tests** — invariant under affine maps, **not**
   under the nonlinear `atan∘sinh` tile→geo reprojection. Triangulating on geodetic coords would silently break
   every byte-identical fill snapshot and re-open the **globe-winding failure class** the code still carries a
   scar for (`FillMeshPipeline`'s "an earlier globe-only reversal inverted the globe's front-faces").
2. **Hardcoded scale-dependent epsilons prove tile-local is the working space.** `RingAssemblyJob`'s
   `DegenerateThreshold = 1.0` and `EarcutJob`'s `1e-10 … 1e-14` are calibrated to tile-integer magnitude
   (`[0, 4096]`). Geodetic degrees are a different scale entirely.
3. **Line subdivision and symbol spacing need a tile-relative, zoom-invariant frame.** `StyledLineTileBuilder`
   subdivides the centerline in tile space; `SymbolFeatureExtractor` computes anchor spacing in tile units
   (px→spacing needs `extent`, not degrees — degree-per-meter varies with latitude).
4. **Everything is tiled.** GeoJSON is sliced client-side, and the slicer emits extent-scaled **tile-local**
   integer coords, exactly like MVT. **This is now in-repo and verified rather than assumed:**
   `Core/GeoJson/GeoJsonTileSlicer` (GeoJSON S1, `11b083ee`) is a clean-room slicer derived from RFC 7946 that
   emits exactly that, under test in both runners. (This retires the caveat on Decision A below, which flagged
   the claim as external library knowledge with no in-repo fixture to check it against.) There is no non-tiled
   format in the system; tile-local *is* the vector-neutral shape every format slices to. This makes geodetic the wrong altitude for the *interchange
   contract* and the right altitude for the *projection* seam — two different things.

## Eager whole-tile decode: ~~rejected~~ — REJECTION WITHDRAWN, the premise was false (2026-08-08)

> **This section's original argument does not survive contact with the code.** It is kept, struck through,
> because it is the load-bearing premise that made per-consumer materialization look forced — and therefore the
> reason the epic shipped a comb instead of a waist. Superseded by the measured re-derivation below.

~~Today's decode is **lazy per *selected* feature** (each builder calls `FeatureSelector.SelectFeatures` then
decodes only the selected subset). Eager whole-tile would run transcendental geo-conversion for features **no
style renders** (unused source-layers), on **every tile** — new work, unbounded by the style. The real
inefficiency is that a feature selected by N *style layers* is decoded N times (N = style layers touching that
source, often 1 — confirm during planning, don't assert). The fix is **dedupe across selected layers**, not
eager whole-tile. Keep selection upstream of materialization.~~

**Why it is false — two independent errors, both verified against the tree:**

1. **Waist-1 decode contains no transcendental geo-conversion, and does not trigger Waist 2.** The argument
   conflates the two waists. Waist 1 is `MvtGeometryMaterializer.Materialize()` → `MvtDecodeJob.Run()`:
   integer zigzag-varint decode plus cursor accumulation into `double2`, with **zero** `math.*` calls. The
   transcendentals (`atan`, `sinh`, `math.pow`) live in `TileToGeoJob` — **Waist 2**, which is *Stage 4* of
   `FillMeshPipeline.Schedule`, and the Waist-1 buffer is **disposed at `FillMeshPipeline.cs:472`, before
   Stage 4 runs**. Waist-2 volume is a function of what survives earcut and kind-filtering, never of how many
   features Waist 1 decoded. **Eager Waist-1 materialization adds no geo-conversion work whatsoever.**
2. **The "new work, unbounded by the style" is already paid today, in a more expensive form.**
   `MvtDecoder.DecodeFeature` calls `ReadPackedUInt32`, which builds a `List<uint>` and `.ToArray()`s it
   **per feature, for every feature in every layer**, eagerly inside `SharedTileDecode.GetOrDecode()`, before
   any style filter runs. The tile's complete geometry is already varint-walked and copied into per-feature
   managed arrays unconditionally. Eager materialization would be a *second, cheap* pass over resident words
   that **replaces N passes** — and would let that per-feature managed allocation be deleted.

**Measured, from the 11 committed `openmaptiles` `.pbf` fixtures cross-referenced against `liberty.json`:**

| quantity | value |
|---|---|
| cost the rejection was about (source-layers **no** style layer names) | **0.0 %–1.1 %** — exact, filter-independent (only `mountain_peak` in these tiles) |
| per-layer duplication actually paid today (geometry-weighted N) | **2.7×–15.5×** — *upper* bound (assumes every layer selects every feature) |
| the doc's predicted "N often 1" | **false for this repo's own default style** — N = 61 on `transportation`, 9 on `place`, 6 on `landuse`; N = 1 for only 3 of 14 source-layers, each carrying 1 fill layer |

The confirmation the original parenthetical asked for ("confirm during planning, don't assert") was **never
recorded by any stage**, and the assertion it hedged turned out to be wrong by more than an order of magnitude.

**Corrected position.** Keeping selection upstream of materialization has **no measured cost basis**. Eager
per-tile Waist-1 materialization costs ≤1.1 % in work the style never renders — work already being done anyway —
and removes a 2.7×–15.5× duplication. The remaining arguments against it are **structural, not
performance**: where the buffer lives (Core is engine-free; `TileGeometryBuffers` is `Unity.Collections`) and
who disposes it. Those are the real constraints; see §"Purpose audit" for both, and note that the
sequential-worker-pass fact below makes the disposal question much smaller than it looks.

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

**Self-describing (`Tile`/`Extent` folded in).** Today the tile-local coords travel with their `(TileId, Extent)`
*out-of-band* on `LayerInput`; once the buffer is a standalone, possibly-memoized artifact, a caller reading it
without threading the matching `(TileId, Extent)` is a silent-corruption hazard. Bundling them makes Waist 1
self-describing. These are **provenance metadata, not a space discriminator**: they do not make the buffer able
to represent geodetic data, and earcut's signature still only ever accepts `TileGeometryBuffers` — the
monomorphism fence is intact.

**`Vertices`/`RingOffsets`/`RingFeatureIdx` are an extraction; `FeatureGeometryType` is genuinely new.**
`FillMeshPipeline.Schedule` already allocates the first three as private locals, runs `MvtDecodeJob` into them,
and disposes them inline after earcut — promoting them to a named, shared, multi-consumer struct is the bulk of
stage B1. `FeatureGeometryType` does **not** exist yet (grep-empty) — it is added because `RingAssemblyJob` runs
in **Burst** and cannot read the managed `ITileFeature.GeometryType` enum, so the kind must be materialized
**blittable** before classification. It is **per-feature, not per-ring**: no producer emits a mixed-kind feature
(MVT `Feature.type` is singular; geojson-vt maps each feature to one type; the background quad is one feature),
so per-feature type + `RingFeatureIdx` fully determines ring kind. A physical per-ring column is an *optional*
Burst-locality denormalization — adopt only if profiled, never as a correctness claim. Contingent on Decision A
(single-typed features); a true untiled `GeometryCollection` format would make a per-ring tag load-bearing again,
out of scope under A.

**The blittable/managed split lands on the assembly boundary:**
- **Core** keeps the **managed evaluation surface** — `IFeature`/`ITileFeature` (properties, id, `GeometryType`).
  Filters/selection read only these; they never read coordinates.
- **Jobs** owns the **blittable geometry** — `TileGeometryBuffers` + the materializer that fills it.

So the decoded tile is `{ blittable tile-local geometry, managed property bags }`. Properties **cannot** be
blittable (the filter/expression layer needs `string→Value`). `ITileFeature.Geometry : uint[]` is *replaced* by
this split (decision B); `MvtFeature` keeps its `uint[]` as MVT's private raw form that the MVT materializer
reads.

### Pluggable-by-encoding on the Burst side (no vtable in Burst)

Mirror the existing **`ProjectionDispatch`** precedent: a managed dispatcher switches on `TileEncoding` (parallel
to `Decoders.ForEncoding`) and runs the chosen **concrete** Burst job — Burst never holds a managed
interface. A real difference from `ProjectionDispatch`: projections share one input/output shape (one generic
job, N struct instantiations); geometry decoders do **not** share input shape (MVT = command stream; GeoJSON =
coordinate pairs), so this is **N independently-shaped concrete jobs unified only by a common output**
(`TileGeometryBuffers`), dispatched by a managed switch — not a single generic `DecodeJob<TFormat>`.

### The double-decoder unification (its own stage)

`MvtDecodeJob`'s `Execute` is **not** actually polygon-specific — it walks MoveTo/LineTo/ClosePath generically
into paths, the same command walk as managed `MvtGeometry.Decode`. "Polygon-only" describes its *consumer*
(`RingAssemblyJob`), not its decode. So unification collapses from "write a new decoder" to **point the
currently-managed line/symbol consumers at the buffer `MvtDecodeJob` already yields, and retire
`MvtGeometry.Decode`.** A Point feature is a 1-vertex path; a polygon ring is a path `RingAssemblyJob`
classifies — no special-casing, given the per-ring kind tag.

### Ownership / lifetime

> ⚠️ **Corrected 2026-08-08 (purpose audit).** Shipped reality is **once per (layer, tile)** — ~109 buffers per
> tile (108 style-layer sites + 1 background), with **no sharing between the mesh and symbol passes**.
> `ITileGeometryMaterializer`'s own doc says the accurate thing ("called once per (layer, tile)"); this section
> was never corrected, so the SSOT described the waist as achieved. The "(source, tile)" figure below is the
> TARGET.
>
> **The sequencing claim below is TRUE ONLY ON THE UN-PARKED PATH — "per-kick" is the WRONG scope.**
> Corrected 2026-08-08 during B7 planning, after this block first asserted the stronger claim as "verified".
> Recorded rather than silently fixed, because it is the *same* defect class this audit exists to catalogue: a
> claim true of the code I looked at, false of the code I did not, committed as verified.
>
> What is true: `TileManager.KickMeshBuild` (`~:1626–1650`) runs
> `TileLayerProcessorRunner.RunWorkerPass(decode, …)` and then `symbolPass?.RunWorkerAndHandoff(decode)` inside
> one `UniTask.RunOnThreadPool` lambda, sequentially, against the same handle.
>
> What that misses — **D6 park mode.** `SymbolTileWorkerPass.RunWorkerAndHandoff` (`SymbolSubsystem.cs:549-561`)
> does **not** run the extract when `_parked`: it enqueues a `PendingSymbolBuild` **carrying `decode`** and
> returns. `PumpBuilds` (`:649`) later dispatches it on its **own** `UniTask.RunOnThreadPool` (as of this
> 2026-08-08 audit; dispatch now goes through `WorkScheduler`), a different task at a later time,
> outside the originating kick lambda entirely. A buffer scoped to the *kick* would therefore be
> **use-after-dispose** whenever a sprite fetch has not settled. The existing code states the dependency it has
> on the current design outright — *"the decode is retained (legal: `SharedTileDecode`'s own contract is plain
> GC reachability)"* — which is exactly the property that breaks the moment native memory hangs off the handle.
>
> **D1 (2026-08-09) closes the park hazard this paragraph describes, and inverts the quoted comment.** The
> park no longer "retains the decode" on GC reachability alone — it takes a **reference**, while the kick's is
> still live, and disposes it at the drain (or at the purge, or at the ct-drop). The dispatch is a different
> task at a later time exactly as described; what changed is that the tile it reads is provably still alive,
> and is the *same* tile the kick read. The production comment quoted above ("legal: `SharedTileDecode`'s own
> contract is plain GC reachability") is gone with the type.
>
> **Corrected scope: per WORKER PASS, not per kick.** A `using`-scoped store created inside each
> `TileLayerProcessorRunner` entry. Still **no refcount** and still nothing attached to the cached
> `IDecodedTile`, so the conclusion (a lifetime protocol is only needed for the attach-to-`IDecodedTile`
> variant) survives — but the accepted cost changes: a source-layer named by **both** a mesh and a symbol layer
> materializes **twice per kick**. So B7's honest headline is **108 producer sites → 1**, with ≤2
> `Materialize()` calls per (source-layer, kick) — *not* "one buffer per kick".
>
> **Superseded by C1 P3, and re-stated precisely (2026-08-09).** The headline is **108 producer sites → 1** —
> that half is true and verifiable: one `new MvtGeometryMaterializer(` (`MvtDecoder.cs:172`) and one
> `new PathGeometryMaterializer(` (`TileBackgroundLayerProcessor.cs:110`, a genuine producer — a synthesized
> quad belonging to no source layer) are the only production producer sites. What it is **not** is "one
> materialization per tile": `Materialize()` runs **once per source-layer present in the tile**, measured at
> **8–12** on the openmaptiles corpus, and since P3 that count is **style-independent** (eager decode
> materializes layers no style names). B7's "≤2 per (source-layer, kick)" is also retired: P3 makes it
> **one buffer per (source-layer, decode), shared across both cadences of a kick**. What observes it is
> `Meshing/SourceLayerBufferSharingTests.OneKick_MaterializesEachSourceLayerOnce_SharedAcrossFillAndSymbolConsumers`
> — it pins the *sharing* (`DecodeCount == 1`, same buffer instance to three mesh and two symbol probes), and
> deliberately not a per-tile count of 1, because that number is false.

> **SHIPPED in B7a (2026-08-08), for the mesh worker pass.** `TileGeometryStore` (`MapRenderer.Jobs`) mints a
> buffer **once per (source-layer, worker pass)**, lazily on first touch and memoized on the `ITileLayer`
> reference, and is `using`-scoped inside `TileLayerProcessorRunner.RunWorkerPass` /
> `RunSourcelessWorkerPass`. It **owns** every buffer; consumers **borrow**. **Fill** consumes it that way as
> of B7a: `FillMeshPipeline.Schedule` takes the shared buffer plus a caller-supplied `RingVisitOrder` and
> derives its own private list-backed buffer holding exactly the rings it visits, in the order it wants them.
> **Line and symbol still mint their own** — deliberately, and B7b is what converts them; until then the
> materialization count is 3 (store + line + symbol), not 1. `RunSymbolWorkerPass` gets its store in B7b,
> which is where §2.1's park-path scoping lands.
>
> **The two-tier ownership contract, in these words:** a materializer still **transfers** the buffer it mints
> — to the store. The store **lends**; a consumer **borrows** and must never dispose it, never retain it past
> the store's scope, and never mutate it. The only buffer a consumer owns is one it **derived**
> (`TileGeometryBuffers.AdoptDerivedLists`, which **copies** the per-feature kind column so the source is left
> intact) or one it minted because it *is* the producer (the background quad). Getting this wrong fails
> quietly: disposing an `AsArray()` view is a silent no-op under Collections 6.5.0.

~~`Allocator.Persistent`, minted **once per (source-layer, worker pass)**, disposed at the end of that
**same call** (no refcount, no cross-thread handoff).~~ **False of the shipped tree, corrected 2026-09-19 —
this contradicted the lease this same file describes below.** `TileGeometryStore` and its per-worker-pass
buffer are gone: C1 made the decoded tile own its `TileGeometryBuffers` for its whole life
(`MvtLayer.Geometry`, `MapRenderer.Jobs/Mvt/MvtModels.cs`), and D1 put a **reference count** over the whole
decoded tile, not a disposal scope over one buffer. `TileDecodeDispatch.DecodeAsync` mints a
`SharedDisposable<IDecodedTile>` (`MapRenderer.Core.Lifetime`) once per fetched tile; every further owner
takes a reference with `Acquire()` and drops it with `Release()`, and the value disposes on the release that
brings the count to zero — see `### Disposal — SUPERSEDED by D1` below for the full model. This native memory
still does **not** round-trip through the `MeshDataArray`/`ApplyAndDisposeWritableMeshData`
boundary — that is main-thread GPU-bound *output*; this is decode-time *scratch*. Two unrelated disposal
domains.

### GeoJSON's Stage-1 is a slicer, not a switch case

Under the two-waist model, GeoJSON reaching Waist 1 (tile-local) needs the **inverse of `TileToGeoJob`** —
reproject to Mercator + clip to tile bounds + quantize to `[0, extent]` — **a job that does not exist yet**,
structurally ≈ reimplementing geojson-vt's core. Under two-waist, *MVT* is the no-op (already tile-local) and
*GeoJSON* does the real slicing work. Do not scope A8's GeoJSON as "add a case to the encoding switch."

## Correctness landmines (mandatory teeth for the stages that touch them)

1. **Blittable feature-kind (ranked #1; resolved: per-feature, contingent on Decision A).** `RingAssemblyJob`'s
   signed-area classification would treat a LineString ring as a spurious polygon exterior/hole — **silent
   triangulation corruption, not a crash** — if it can't tell the ring's kind. The kind info is **required and
   new** (Burst can't read the managed `GeometryType` enum), carried **per-feature** (`FeatureGeometryType`,
   indexed via `RingFeatureIdx`), *not* per-ring. Gate area-classification on
   `FeatureGeometryType[RingFeatureIdx[r]] == Polygon`. Holds only while Decision A (single-typed features) holds.
2. **Ring-length filter mismatch (ranked #2).** Line discards `< 2`-point rings; fill discards `< 3`-point rings.
   The shared buffer must carry **all** rings unfiltered; each consumer applies its **own** downstream length
   filter. The filter must not move into the shared decode step (it would starve the other consumer).
3. **THE NAMED FENCE (biggest landmine).** Once a tile-local shared buffer and the geodetic Waist 2 coexist,
   "just feed earcut/subdivide the geodetic version" is one wrong line away — and the epsilon evidence (§Why NOT,
   point 2) makes it a real regression, not theoretical. This must be a written fence in every stage plan.
4. **Feature-then-ring order/identity.** Re-expressing "iterate my rings" as "iterate `RingOffsets` slices for my
   feature's ordinal" needs the shared decode to preserve exact feature-then-ring order (both decoders are
   single-pass state machines, so *likely* fine) — pin it with a differential test, and add a `MvtFeature.Ordinal`
   (small, mechanical, a prerequisite either convergence option needs).
5. **The B3 fused-`RingAssemblyJob` fence (sibling to THE NAMED FENCE).** `RingAssemblyJob` is a **fused,
   fill-only** stage: area-based degenerate-filtering **and** outer/hole classification in one pass. When B3 puts
   line onto the shared buffer, the tempting shortcut is reusing `RingAssemblyJob`'s output — line must **not**:
   it needs its own iteration over raw `RingOffsets` slices filtered to `FeatureGeometryType == LineString`, its
   own `Count < 2` filter, **zero `RingAssemblyJob` interaction** (line has no polygon/hole concept; its filter
   is a count threshold, fill's is a shoelace-area threshold fused into classification — structurally different).
   This makes B3 a genuine control-flow rewrite → **verified-equivalent, not byte-identical**.

## Stage sequence (each independently green)

| Stage | Scope | Bar | Key teeth / prereq |
|---|---|---|---|
| **B1** | Extract `TileGeometryBuffers` from `FillMeshPipeline`'s private locals; fill-only, no seam yet | **byte-identical** | fill snapshots unchanged; dispose still frees every array. *Safe pure-refactor down-payment, landable without the A8 trigger* |
| **B2** | `ITileGeometryMaterializer` seam + MVT impl (wraps `MvtDecodeJob`); fill routes through it. **Trigger: A8/GeoJSON** | byte-identical | a non-MVT tile-local fixture flows through unchanged Stages 2–4; per-layer materialize (no union yet) |
| **B3** | Line onto the buffer; delete its `MvtGeometry.Decode` call | **verified-equivalent** (differential oracle, not pixel-byte) | line snapshots equal the managed-decode oracle; `FeatureGeometryType` gate (#1); unfiltered rings (#2); the fused-`RingAssemblyJob` fence (#5) |
| **B4** | Symbol onto the buffer; **move symbol's coordinate half Core→Unity**; retire `MvtGeometry.Decode` | verified-equivalent | `SymbolProcessorParityTests`; Core has no `NativeArray` ref |
| **B5** *(decision B)* ✅ | Remove `uint[]` from `ITileFeature`; add the second producer (`PathGeometryMaterializer`) the background quad needs | behaviour-preserving | structural: `ITileFeature` declares ZERO members (reflection, not a grep) |
| ~~**B6** *(measure-gated)*~~ ❌ **RETIRED** | ~~Shared selected-union materialization (Option A)~~ | — | **Scope retired by maintainer decision, 2026-08-08.** Its measure-gate came back positive (108 sites/tile, N up to 61) but its *design* — Option B's "preserves lazy-per-selected-feature" — is the property that forces `IMvtGeometryCarrier` to exist, so it would have deduped the waste and closed none of the epic's stated purpose. **Superseded by B7 + B8**, which subsume its dedupe by construction. |
| **B7a** ✅ | **Store + fill + the `RingAssemblyJob` kind gate.** `TileGeometryStore` (`MapRenderer.Jobs`) mints Waist-1 geometry **once per (source-layer, worker pass)**, `using`-scoped inside the runner entries; **fill** borrows it and drives `FillMeshPipeline.Schedule` with a caller-built `RingVisitOrder`. `Schedule` stops consuming destructively — it **derives** a private buffer (`AdoptDerivedLists`, which COPIES the kind column). `extent` **and the `TileId`** leave the fill signatures (`geometry.Extent`/`geometry.Tile` are the only copies; the address one landed in the fix round, review N1). Line and symbol keep minting: **deliberate**, see B7b. | **byte-identical** | **B7a carries 100 % of the arc's ORDERING risk** — fill is the only consumer whose conversion changes the order of anything. Fill's `fill-sort-key` reorder moves from *materializer input* to *ring visit order* (a stable counting sort bucketed on rank, which is what preserves feature contiguity and within-feature decode order). Teeth: T0 (hole attribution over the .pbf corpus), T1b (shared buffer holding unselected + non-polygon features), T2/T2b (borrow, not transfer), T4a (Features read once per source-layer), T4c (memo identity), T5 (the kind gate + its anti-vacuity twin), T7 (pattern coords route the buffer's own extent), T8 (…and its own tile address). Fix round: T1e (`RingClipJob` honours a sparse, permuted visit order) + T1b's clip-branch arm — the production configuration, which the first cut left unobserved. |
| **B7b** | **Line + symbol onto the shared buffer.** Delete both remaining mints; re-index `featColors`/`featWidths` and symbol's counting sort onto the ordinal; add line's one new `selectedByOrdinal` gate; `RunSymbolWorkerPass` gets its own store. | **byte-identical** | **B7b introduces NO ordering decision at all** — every edit is a re-index of a per-feature side array plus one selection gate, so if a snapshot moves the cause is the ordinal join, never an ordering choice. Two disjoint diagnoses, which is the reason for the split. Teeth: T3 (line + symbol dispose nothing), T4b (exactly one `new MvtGeometryMaterializer(`), T-b1 (line's new selection gate). This is also where all three `MvtGeometryMaterializer` anti-vacuity premises expire — once each. |
| **B8** *(decision B, completed)* | **Decoder produces IR.** `ITileDecoder` emits `TileGeometryBuffers`; `IMvtGeometryCarrier` and `MvtFeature.Geometry` are **deleted**; the three hard-coded `new MvtGeometryMaterializer(…)` sites vanish rather than needing encoding dispatch. Format is confined to the decoder — the thing the epic existed for. | behaviour-preserving | Needs the **decode seam moved Core → Jobs/Unity** (purpose audit hard part 1, option (a) — the only variant that regresses nothing; (b) and (c) are recorded non-starters). Needs a **lifetime protocol** *only if* the buffer attaches to the cached `IDecodedTile` — `SharedTileDecode` states it has none and drops by GC reachability on several paths. Structural tooth: zero production references to a `Mvt`-named geometry carrier. |

**Byte-identical vs verified-equivalent:** B1/B2 stay byte-identical (pure extraction, fill's decode/earcut math
and input space unchanged). B3/B4 need **verified-equivalent** the moment a consumer swaps its own
`MvtGeometry.Decode` for the shared buffer — a control-flow rewire, even with identical math — using the
`SymbolProcessorParityTests` differential-oracle template, not a pixel snapshot match.

Throughout: **Waist 2 (`TileToGeoJob → ProjectPointsJob<TProj>`) stays exactly where it is.**


### B1 through B7a — stage record (condensed 2026-09-19; RED-verification rows, gate counts, and
review-arm reports cut — none of this range is a citation target elsewhere in the repo)

Stages B1 (extract `TileGeometryBuffers`), B2 (`ITileGeometryMaterializer` seam + the MVT implementation),
B3 (line onto the buffer), B4 (symbol onto the buffer), and B5 (remove `uint[]` from `ITileFeature`) each
landed byte-identical or verified-equivalent against `./Tools/run-tests.sh`. B7a followed: `TileGeometryStore`
mints one `TileGeometryBuffers` per (source-layer, worker pass), and fill borrows it instead of minting its
own.

A purpose audit run after B5 found the epic had shipped a comb, not a waist: **108 independent
materialization sites per `openmaptiles` tile** (61 of them re-decoding the same `transportation` layer —
**2.7×–15.5× duplication**), because materialization still sat downstream of per-layer feature selection.
The maintainer resolved this on 2026-08-08: retire the planned B6 dedupe stage and instead move the decode
seam itself out of Core — stage C1, below.

The durable outcomes of B1–B7a — the `TileGeometryBuffers` shape, the correctness landmines, and the
borrow-not-transfer ownership contract — are stated in `## The design` above, not repeated here.

## C1 — move the decode seam out of Core (supersedes B7b/B8 as separate stages)

**Maintainer ruling, 2026-08-08:** Core-without-Unity is a convenience, not a goal — see **`ARCHITECTURE.md`
§2 "Module boundaries"**, which is now the governing rule. The tile pipeline had been shaped around the Core
boundary rather than around the product, producing three workarounds from one cause. C1 removes the boundary
instead of routing around it: **the decoded tile owns its geometry.** `IMvtGeometryCarrier`,
`TileGeometryStore` and the `ProcessOnWorker` store parameter all disappear as a consequence.

**Cut line (verified against the tree, correcting the first sketch):**

| stays in Core | why |
|---|---|
| `TileGeometryType` | the evaluation surface needs only the **enum** (`Expressions/IFeature.cs:16`, `Ops/FeatureData.cs:50`) — keeps **Expressions (24 files)** untouched |
| `ProtobufReader` → `Core/Protobuf/` | genuinely shared: `Text/GlyphPbfDecoder` decodes glyph PBFs with it — keeps **Text (81 files)** untouched. *Moved out of the `Mvt` namespace so Core does not keep an MVT namespace holding one non-MVT type.* |
| `TileBufferClip` | pure value type |
| **`GeoJson/` (3 files)** | **correction to the first sketch:** they reference only `TileGeometryType`/`TileBufferClip` — nothing from `Mvt` or the tile interfaces. They stay, and keep their fast-runner coverage. |

**Moves to `MapRenderer.Jobs`** (not a new assembly — a `MapRenderer.Decode` would have to absorb
`TileGeometryBuffers`, both materializers, the seam and `MvtDecodeJob` to avoid inverting the dependency, i.e.
it becomes a second jobs assembly): `Tiles/DecodedTile.cs`, `Tiles/ITileDecoder.cs`, `Mvt/` decoder + models,
`Filters/FeatureSelector.cs`, `Style/SourceLayerResolver.cs`.
**To Unity, not Jobs:** `ITileFeatureSource`/`SharedDisposable<IDecodedTile>` — they need UniTask, which
`MapRenderer.Jobs.asmdef` does not reference, and every implementer and consumer already lives in Unity.

**Materialization is EAGER, inside `Decode`.** The decisive reason is structural rather than the ≤1.1 % cost:
lazy would mutate the tile on a second thread *after* `SharedTileDecode` publishes it, invalidating its
documented safe-publication argument. Disposal is identical either way — lazy never avoided the fork below.

**`ITileDecoder.Decode` takes the `TileId`** (available at `MvtTileFeatureSource.GetTile`). This is the
mechanism that makes mispairing impossible by construction: it removes the caller-supplied id that
`TileGeometryStore` stamps into buffers today, which is exactly the detached-cache defect C1 exists to remove.

**`ITileFeature` is deleted** — it declares zero members; `IFeature` is the evaluation surface.

### Disposal — SUPERSEDED by D1: eager decode + reference count (maintainer, 2026-08-09)

> **The scoped lease (option S) shipped in C1 P3 and was replaced in D1.** The section below states what
> replaced it and why; option S's own record is kept underneath, unedited, because the *reason* it was
> chosen is the reason the replacement had to close a leak class that option S never had.

**D1's model.** A tile is decoded **once, at fetch completion**, inside the source's `GetTile` task and on the
pool (`TileDecodeDispatch.DecodeAsync` — the single dispatch site, shared by both `ITileFeatureSource`
implementations). The handle it mints (`SharedDisposable<IDecodedTile>`, replacing `SharedTileDecode`) is a
**reference count**, born with one reference that belongs to whoever received it. Its surface is
`Tile` / `Acquire()` / `Release()`; `Tile` and `Acquire()` throw `ObjectDisposedException` after the last
release, an unbalanced `Release()` throws, and the last release disposes the tile — forgetting it inside the
lock, disposing it outside, verbatim from option S's `CloseScope`.

**What it buys.** Exactly one decode per fetched tile, ever. The parked symbol build's re-decode is **gone**:
the park `Acquire()`s while the kick's reference is still live, so the tile survives the whole
`SetStyle`→`SpritesSettled` window and the drain reads the *same* `IDecodedTile`. A decode can no longer
happen without an owner, because the owner exists before the tile does.

**What it costs, and this is the honest part: D1 lands close to REJECTED OPTION T.** Option T was rejected for
buying disposal sites in `SymbolLabelSubsystem` where option S needed none, and for one named hazard —
`PumpBuilds` dispatching via `RunOnThreadPool(…, cancellationToken: captured.Ct)`, so on cancellation the
delegate never runs and a `finally`-based release never fires. **That hazard is real and D1 hits it.** The fix
is to drop the `cancellationToken:` argument and keep the existing in-lambda `ct` check *inside* the `try`,
which makes the delegate unconditionally scheduled and the `finally` always reachable. It is pinned
structurally (`TileProcessingStructureTests.TheParkedDrain_ReleasesOnEveryExit_…`), because no test can
provoke the cancellation window without a production seam.

The other half of T's cost — "five disposal sites" — was paid down first: **D0 extracted four funnels**, so
D1's release obligation is one line at each of four chokepoints rather than eleven scattered edits.

| abandonment class | funnel | release |
|---|---|---|
| a record leaves `_loaded` (cover change, eviction, restyle, teardown) | `TileManager.RenderTeardownRecord` | `lt.Decode?.Release(); lt.Decode = null;` |
| a fetch completes with nobody to consume it | `TileManager.DiscardFetchOutcome` | `GetResult()?.Release()` |
| the kick's own (transferred) reference | `KickMeshBuild`'s pool lambda | `finally { decode.Release(); }` + a prologue `catch` for the throw-before-the-lambda-exists case |
| a parked symbol entry is dropped | `SymbolSubsystem.DrainAndDiscardParkedBuilds`, plus `PumpBuilds`' ct-drop and drain `finally` | `DecodeRef.Dispose()` |

**Faults moved with the decode.** A decoder throw now faults the `GetTile` task, wrapped in
`TileDecodeException`, and is observed at `TakeDecodeFromFetch` — which gives it its **own** throttled log and
counter, because sharing `LogFetchErrorThrottled`'s counter would let a genuinely broken tile hide behind 64
unrelated network errors and would label it "tile fetch failed", which is a lie. Sticky faults are gone by
construction: a tile decodes once, so there is no second caller to re-parse for. `TileLayerProcessorRunner`'s
catch and its log are **unchanged**; only the decode-fault arm stops arriving there.

**Peak residency rises, deliberately and uncapped** — see
`docs/per-layer-tile-processing-design.md` §"Eager decode: peak resident decoded-tile memory".


### C1 through D2 — stage record (condensed 2026-09-19; RED-verification rows, gate counts, and
review-arm reports cut — none of this range is a citation target elsewhere in the repo)

C1 moved the decode seam out of Core in three phases: **P1** relocated the decoder types (no output risk);
**P2** put line and symbol onto the shared buffer (the never-done B7b, all of the arc's remaining output
risk); **P3** made the decoded tile own its geometry, deleting `IMvtGeometryCarrier` and `TileGeometryStore`.
P3 landed 2026-08-09, gate `2298/2298`, no snapshot moved. P3 shipped an interim lifetime model (option S, a
per-decode-scope counter), replaced the same day by **D1**: eager decode at fetch completion plus the
reference count described in `### Disposal — SUPERSEDED by D1` above — the model that ships today. **D2**
(2026-08-09) added an end-to-end test for the parked-symbol-build path the reference count changed; gate
`2300/2300`, both review arms APPROVE.

A follow-up fix stage (2026-08-09) shipped four production fixes: `RunWorkerPass`'s swallowed exceptions now
log the tile and the exception; `SymbolFeatureExtractor.Extract` reads its tile address and extent off the
buffer instead of a caller-supplied `TileId`; and two waist-1-producer fixes, carried into
`## Resolved decisions` below because nothing restated them there — `PathGeometryMaterializer`'s early-out
condition, and `MvtLayer.Geometry`'s mutability. Gate `2304/2304`, both arms APPROVE, no REQUIRED findings.

## Resolved decisions

- **Two waist-1-producer fixes from the C1 post-review fix stage (2026-08-09), carried here because
  the stage-diary sections above no longer restate them.**
  - `PathGeometryMaterializer` early-outs on `featureCount == 0`, matching the MVT sibling
    (`MvtGeometryMaterializer`), instead of the earlier `ringTotal == 0`. The old check was reachable with
    features present (every feature carrying no paths), yielding a buffer whose `FeatureCount` is 0 beside a
    non-empty `ITileLayer.Features` — a mis-bucketing hazard for any consumer that sizes its per-feature
    columns from one and indexes them by ordinals drawn from the other. Tooth: `Jobs/WaistOneProducerAgreementTests`.
  - `MvtLayer.Geometry` is `{ get; private set; }`, written only once, through a lockstep-checked
    `AdoptGeometry` — not the earlier public mutable field. This makes `FeatureCount == Features.Count` a
    structural fact instead of a one-writer discipline. Tooth: `WaistOneProducerAgreementTests`, including a
    reflection check that no public setter or field remains.
- **(A) "Every source is tiled" → YES, confidence downgraded.** The two-waist / tile-local design is right
  *because* everything is tiled: the only scenario a geodetic geometry seam would win is a genuinely
  **non-tiled, whole-dataset** source (a polygon spanning many tiles → must earcut in a projected plane), and
  the architecture rules that out by tiling everything. **Caveat:** the load-bearing premise — "geojson-vt emits
  tile-local integer coords" — is **external library knowledge, not code-verified** (no GeoJSON source/slicer/
  fixture exists in-repo). So make **B2's "a non-MVT tile-local fixture flows through unchanged" bar double as
  Decision A's falsification test** — if a synthetic non-MVT fixture can't be expressed tile-local without
  contortion, revisit A.
- **(B) Remove `uint[]` from `ITileFeature` → YES, entirely (not null), last, gated twice.** Make the interface a
  pure evaluation surface (`Id`/`Properties`/`GeometryType`, all already on `IFeature`). **~~geometry travels
  only in the blittable buffer, joined by ordinal~~ — FALSE OF THE SHIPPED CODE; corrected 2026-08-08.** What
  shipped: geometry travels **on the feature**, as `uint[]` behind `IMvtGeometryCarrier`, and the join is
  *position in the list the consumer hands the materializer* — exactly as this bullet's own **P3** states three
  sentences below, which the struck clause contradicts. Both sentences sat in "Resolved decisions" disagreeing
  with each other; the reader had to notice. The struck clause describes the **target** (Decision C / the
  source-produces-IR direction), not this decision's outcome. `GeometryType` **stays** (selection needs it; also
  load-bearing for ring-kind per landmine #1).
  **Trigger — CORRECTED IN B5; the original was unsatisfiable.** It read: fire on *(a) a grep proving zero
  production `.Geometry` readers after B3/B4*. That describes B5's **postcondition**, not its precondition.
  The readers B3/B4 removed were **decoder** calls (`MvtGeometry.Decode` — zero production callers since B4);
  the three `ITileFeature.Geometry` **carrier** reads in the fill/line/symbol builders existed only to feed the
  MVT materializer and were always B5's own job to remove, so (a) could never be true beforehand. The real
  predicate is four conditions: **P1** one materialization point per consumer (true after B1–B4); **P2** a typed
  non-interface route for the MVT bytes (B5 builds it — `IMvtGeometryCarrier`); **P3** the feature→geometry join
  expressible without the interface (true — it is *position in the list the consumer hands the materializer*, so
  `MvtFeature.Ordinal` is a Decision-C/B6 prerequisite, **not** a B5 one); **P4** a materializer for non-MVT
  features (B5 builds it — `PathGeometryMaterializer`) — **AND (b)** A8/GeoJSON actually landing (S1 did,
  `11b083ee`). *Any future stage trigger written as "a grep proves zero readers" must say **which** readers.*
  **In-tree evidence:** `TileBackgroundLayerProcessor.cs:37`
  `FullExtentRingGeometry = { 9, 0, 0, 26, 8192, … }` is a hand-authored MVT zigzag command stream built only to
  satisfy `InMemoryTileFeature.Geometry : uint[]` for a non-MVT background quad — a present-tense instance of the
  exact anti-pattern the complaint named. B5 lets it materialize a plain 4-vertex ring instead. **Known cost:**
  this splits the IR across Core (managed evaluation) and Jobs (blittable geometry), joined by ordinal — real
  added indirection, but not a *new* kind of coupling (`MvtFeature` already holds `uint[]` + `Properties` as two
  fields joined by identity; B5 moves the join from same-object to matching-ordinal-across-assemblies).
- **(C) Convergence → Option B first, keyed by SELECTED-FEATURE ORDINAL.**
  > ⚠️ **Reopened by the purpose audit (2026-08-08) — do not plan B6 from this bullet without reading
  > §"Purpose audit" first.** Option B "preserves lazy-per-selected-feature" *by design*, which is precisely the
  > property that forces `IMvtGeometryCarrier` to exist. So B6 as specified dedupes the waste but leaves the
  > maintainer's actual complaint (format should stop mattering after parse) **entirely unaddressed**. The
  > measure-gate this bullet defers Option A behind has now been measured, and it does not favour Option B: the
  > cost that ruled out eager materialization was ≤1.1 %, against a 2.7×–15.5× duplication. Which stage sequence
  > replaces this is an open maintainer decision, not settled here.

  A per-kick-task lazy buffer memoized
  by *selected-feature ordinal* (the `SharedTileDecode` "first-arrival decodes, gate the rest" idiom one level
  down): preserves lazy-per-selected-feature **and** dedupes the real waste — a feature selected by N style layers
  decoded N times (`SymbolFeatureExtractor.cs:87` is a verified present-tense second decode of the same `uint[]`).
  *Not* source-layer-name (two style layers filtering the same source-layer differently would break it or force
  eager decode). Needs `MvtFeature.Ordinal`. **Option A** (union in the coordinator) widens
  `ITileLayerProcessor.ProcessOnWorker` — a contract stable across the tile-pipeline epic — so it is a separate,
  measure-gated later stage, not the first cut.

## Risk ranking

1. Classifying rings without the blittable `FeatureGeometryType` → silent triangulation corruption (#1 above).
2. Ring-length filter mismatch silently starving a consumer (#2).
3. `ITileLayerProcessor` widening (Option A) — wide blast radius; mitigated by doing Option B first.
4. GeoJSON's Waist-1 slicer under-scoped as "another decoder case" (planning-scope risk for A8, not a correctness
   risk to MVT).
5. ~~`MvtFeature.Ordinal` — small, mechanical, but a concrete prerequisite not yet built.~~ **RETIRED in
   B7a.** It must not be built: `ITileFeature` declares zero members and
   `TileFeatureSurfaceTests.ITileFeature_DeclaresNoMembersOfItsOwn` pins that by reflection, so an `Ordinal`
   there would put a geometry-join concern on the neutral *evaluation* surface. The join is carried **beside**
   the feature instead — `FeatureSelector` returns `(Ordinal, Feature)` pairs
   (`Core/Filters/FeatureSelector.cs : SelectedTileFeature`), which is the same "position in a list" join,
   moved one level up: from the consumer's selected list to the decoded layer's feature list.

## Grounding (verified file:symbol touch points)

`MapRenderer.Jobs/`: `MvtDecodeJob` (kind-agnostic path walk; tile-local `double2` out), `TileToGeoJob`
(tile-local→geodetic, projection-independent), `ProjectPointsJob<TProj>` (geodetic→world, generic over
`IProjection`), `FillMeshPipeline.Schedule` (the Decode→RingAssembly→Earcut→TileToGeo→Project order + the private
buffers to promote), `RingAssemblyJob` (signed-area classification + `DegenerateThreshold`), `EarcutJob` (the
tile-scale epsilons), `ProjectionDispatch` (the Burst-dispatch precedent). `MapRenderer.Core/Mvt/`:
`MvtGeometry.Decode` (the managed twin to retire), `MvtModels.MvtFeature` (keeps `uint[]`; **`Ordinal` is NOT
needed and the prerequisite is RETIRED** — see risk #5 below).
`MapRenderer.Unity/Rendering/Meshing/`: `StyledFillTileBuilder`, `StyledLineTileBuilder`. `MapRenderer.Core/Tiles/`:
`IDecodedTile`/`ITileFeature`/`TileGeometryType` (managed-property view). In-tree anti-pattern evidence:
`TileBackgroundLayerProcessor.cs:37` `FullExtentRingGeometry` (the hand-authored MVT zigzag quad B5 removes);
`SymbolFeatureExtractor.cs:87` `MvtGeometry.Decode(feature.Geometry)` (the present-tense second decode B4/Option-B
removes — but the expensive protobuf parse is already shared via `SharedTileDecode`, so the residual duplicate is
only the cheap command-walk). **Correction (B7a):** `SymbolFeatureExtractor` lives in
`MapRenderer.Unity/Text/`, **not** in Core — B4 moved it, and this section still implies otherwise. B7a also
adds `MapRenderer.Jobs/TileGeometryStore.cs` (the single production `new MvtGeometryMaterializer(` site
outside line and symbol) and `MapRenderer.Jobs/RingSelectJob.cs`.

## ✅ TRIGGERED — the deferral below is SPENT (2026-08-07)

**The GeoJSON source is scheduled and its Fork-1 call is IR-FIRST**, so B1→B5 execute now as the one motivated
arc this section asked for. **Read the deferral below as history**, not as a live verdict — in particular
*"do not pre-build B1 as speculative scaffolding"* no longer applies: B1 now has a live consumer downstream.

Two things changed on the ground, both in GeoJSON stage S1 (`11b083ee`):

- **Decision A's unverified premise is retired.** The caveat read: the load-bearing claim that a client-side
  slicer emits tile-local integer coordinates is *"external library knowledge, not code-verified (no GeoJSON
  source/slicer/fixture exists in-repo)"*. One now exists — `Core/GeoJson/GeoJsonTileSlicer`, derived
  clean-room from RFC 7946, emitting tile-local integers at a configurable extent, under test in both runners.
  So **B2's "a non-MVT tile-local fixture flows through unchanged" bar can be met with a REAL producer rather
  than a synthetic one**, which is a materially stronger falsification test than this doc could assume.
- **No transcode shim will exist.** The deferral's *"GeoJSON can ship today via the same
  transcode-into-`uint[]` shim the background quad already proves works"* was the road not taken. GeoJSON S2
  consumes `TileGeometryBuffers` directly; **no MVT encoder is to be written.** That also means payoff (4) —
  *"a second format slots in without transcoding into MVT commands"* — is no longer conditional. It is the
  reason this epic is running.

Payoffs (1)–(3) are unchanged and still small; they are not what justifies the arc. (4) is.

## Should we build this yet? — Deferred (demand-pull on A8) — HISTORICAL, see above

The question is not "is the design correct" (it is) but "is the epic worth executing now." **No — defer until a
second format actually pulls it, and do not pre-build B1 as speculative scaffolding.**

**The whole payoff ledger** (all verified): (1) delete `SymbolFeatureExtractor`'s second decode — real but tiny
and invisible (off-thread, on no profile, and only the *cheap* command-walk remains after the protobuf parse is
shared); (2) delete the `TileBackgroundLayerProcessor.cs:37` hand-authored zigzag quad — genuinely ugly but ~10
lines, correct, shipped; (3) `ITileFeature` becomes a pure evaluation surface — interface honesty, zero runtime
effect; (4) a second format slots in without transcoding into MVT commands — **the one structurally real payoff,
and entirely conditional on that format existing.**

**Nothing is blocked without it.** ~~GeoJSON can ship today via the same transcode-into-`uint[]` shim the
background quad already proves works in production.~~ **False against the shipped tree, corrected
2026-09-19.** No such shim exists. GeoJSON consumes `TileGeometryBuffers` directly — see `## ✅ TRIGGERED`
above, which states this doc's own current position: "No transcode shim will exist." The shim was the road
not taken. Globe/projection is already handled by Waist 2
(`TileToGeoJob`→`ProjectPointsJob<TProj>`), independent of what the decoder emits. Perf is on no profile; the
duplicate is the cheap command-walk. The one *real standing* cost of inaction is DRY/divergence risk — two MVT
decoders (`MvtDecodeJob` Burst vs `MvtGeometry.Decode` managed) kept identical by discipline, not construction —
separable from GeoJSON-readiness.

**Why not even B1:** B1 promotes private locals to a named struct with no second consumer until B2+ (A8-gated).
Alone it fixes nothing, unblocks nothing, is never measured — inventory for a trigger that may never fire. Same
eager-work this doc rejects under *Eager whole-tile decode: rejected*; the logic scales to the whole epic.

**Verdict:** bank this design; **execute B1→B5 as one motivated arc when A8/GeoJSON (or MLT) is scheduled**, so
every stage has a live consumer. Two standalone carve-outs a maintainer *could* pull independently, both still
worth gating: replace the `TileBackgroundLayerProcessor` zigzag quad with direct synthesis (~1 file, no seam
risk — a cleanup, not this epic); and retire `SymbolFeatureExtractor`'s own decode on divergence-risk grounds
(but gate on a profile before paying the Core→Unity symbol-coordinate migration). Neither should gate anything
else.
