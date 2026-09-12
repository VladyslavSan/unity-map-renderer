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

`Allocator.Persistent`, minted **once per (source-layer, worker pass)**, disposed at the end of that **same
call** (no refcount, no cross-thread handoff). This is a **genuinely new** ownership question — native
memory disposed exactly once under job-safety rules, *unlike* `SharedTileDecode` (pure managed data,
GC-reachable, no dispose). It does **not** round-trip through the `MeshDataArray`/`ApplyAndDisposeWritableMeshData`
boundary — that is main-thread GPU-bound *output*; this is worker-thread decode *scratch*. Two unrelated disposal
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

### B1 — recorded findings (non-blocking, for the merge step)

- **Pre-existing leak on the `EnsureCapacity` throw path.** `FillMeshPipeline.Schedule`'s two capacity
  backstops throw *after* the decode buffers are allocated, leaking them. Documented-unreachable with the exact
  `PrecountRingsAndVertices` sizing, and **deliberately untouched by B1** — wrapping `Schedule` in
  `try/finally` is a control-flow change on an unreachable path with no tooth, and it is not the extraction.
- **Landmine #2 has no tooth at B1, measured not assumed.** B1's RED sweep injected the real defect — a
  `< 3`-point ring filter moved into the shared decode stage — and the **entire gate stayed green**
  (2233/2233, zero failures). Fill's own downstream `rLen < 3` filter in `RingAssemblyJob` makes an earlier
  filter invisible in the output, and `TileMeshBuffers.RingCount[0]`, the one value that does change, has zero
  readers anywhere in the repo. So "the shared buffer carries all rings unfiltered" is currently an
  **unobserved** invariant; **B3 must bring its own instrument for it**, because nothing inherited from B1
  will red.
- **`Extent` ROUTING is only weakly observed — no tooth exists.** `TileGeometryBuffers` carries `Extent` as
  provenance, and a tooth pins that `Allocate` *carries* the value it is handed. But **replacing
  `input.Clip.TryWindow(geometry.Extent, …)` with a literal `4096.0` would still pass the entire gate**,
  because every fill fixture in the repo is extent 4096. So "the pipeline routes the buffer's own extent
  rather than a constant" is a **reviewer inspection point, not a tooth**. B1 deliberately did not invent one
  (a non-4096 fill fixture is new surface, not an extraction). **B2 gets the instrument for free** — a GeoJSON
  producer's extent is a source-level parameter, so a non-4096 tile flowing through the same path discriminates
  it; B2 should take that shot rather than let this sit unobserved into B3.
- **Disposing an `AsArray()` view is a silent no-op, not a throw.** Measured under Collections 6.5.0 in the
  same sweep: mis-setting the backing-mode discriminator so `Dispose()` frees the views instead of the lists
  produced **no exception and no behavioural failure** — the clipped lists simply leaked, and every clip test
  stayed green. The type-level ownership tooth
  (`TileGeometryBuffersTests.AdoptDerivedLists_ThenDispose_FreesTheBackingLists_AndNeverTheViews` — renamed
  from `AdoptClippedLists_…` in B7a, when the clip stopped being the only deriving caller) was the
  only observer. Do not assume the safety system catches view-vs-list ownership mistakes.

### B7a — recorded findings (non-blocking, for the merge step)

- **The snapshot corpus is BLIND to fill draw order.** `fill-sort-key` appears in **no** committed fixture
  style — a grep of the whole test tree finds it only in `FillSortKeyAndOpacityTests` and
  `Style/FillLayoutTests`. So the byte-identity bar's most dangerous quantity (hard part #3) is observed by
  exactly two synthetic tests plus B7a's new `FillSharedBufferTests`, and by nothing else. Consequence for
  future stages: `TestTileMeshBuilder.BuildFill` must keep routing through the real selection → rank →
  visit-order construction. If it is ever "simplified" past that path, the entire instrument goes dark
  silently.
- **T0's hole-bridge property is now PINNED rather than argued.** Every hole `RingAssemblyJob` produces
  belongs to the same feature as its polygon's outer ring — proved by reading the `prevFeature`-before-area
  ordering, and measured over the committed `.pbf` corpus (41 layers, 8769 polygons, **511 with holes**, 1900
  holes) by `Jobs/RingAssemblyHoleAttributionTests`. That property is what makes earcut's hole sort (tiebroken
  on ring index) order-preserving under a reordering visit order, so it is worth its permanent cheap test.
- **The B7a intermediate wart, and its observer.** `ITileMeshRenderLayer.WriteInto` takes its final shape in
  B7a, and **line accepts the shared buffer and ignores it**, still minting its own. This is pinned, not
  merely commented: `StyledLineBuilderStructureTests.LineBuilderMaterializesOnceAndDisposesInTheFinally`
  asserts exactly 1 `.Materialize()` and exactly 1 `geometry.Dispose()`, and its failure text now names B7b as
  the stage that inverts it to 0/0.
- **Transient cost, deliberately not optimised:** `TileMeshLayerProcessor` cannot know whether its layer is
  fill or line without a type test, so a source-layer named only by line layers is materialized once by the
  store (unused) **and** once per line layer. Against a baseline where it was materialized once per line
  layer anyway (up to 61×), that is +1 out of 62. It vanishes in B7b. A kind test in the processor would be a
  worse wart than the cost it saves.
- **A dropped kind column is now behaviourally observable, which it was not in B3.** B3 recorded that
  injecting "the clip handover passes `default` instead of the column" left the entire gate green, because the
  column was write-only after the clip. With B7a's `RingAssemblyJob` gate in place, an empty column makes
  every ring read `Unknown`, the assembler produces zero polygons, and every fill snapshot sees it. The
  structural call-site tooth is kept anyway.
- **Three test harnesses drive `RingAssemblyJob` field-by-field** (`JobifiedPipelineTests`, `RingClipJobTests`,
  and T0 itself) and had to be given the new column. They were not in the plan's enumerated call-site list;
  they surfaced as `InvalidOperationException: … has not been assigned or constructed` on the measurement
  gate, which is the loud failure mode, not a silent one.

#### Closed in the B7a fix round (recorded so the shape is not repeated)

- **B7b MUST NOT INHERIT R1's SHAPE — this is now a standing check, not a one-off.** Three separate ordinal /
  visit-order joins in one stage turned out to be observed only in a *non-production* configuration, and the
  third (`RingClipJob`'s `int ri = RingVisitOrder[k];`) was on the branch that actually runs: with
  `MapViewConfig.FillTileBufferClip = 0.0` the knob resolves to `KeepTileUnits(0.0)`, whose `IsEnabled` is
  **true**, so every fill layer of a real style goes down the clip branch. Every clip fixture in the repo
  supplied an *identity* visit order (under which `ri == k` is true by construction), and the one fixture with
  a permuted, subsetted order ran `clip: default` ⇒ disabled ⇒ the `RingSelectJob` branch — so collapsing the
  indirection passed the entire 2279-test gate. **Line and symbol acquire their own ordinal joins in B7b: for
  each new join, ask explicitly *"is the production configuration the one under test?"* before calling it
  observed.** Closed for fill by `RingClipJobTests.Clip_VisitsTheRingsTheVisitOrderNames_…` (direct, sparse
  order `[2, 0]`) and `FillSharedBufferTests.SharedBuffer_OnTheClipBranch_…` (integration, which also pins the
  `DeriveVisitedRings` wiring the unit test cannot see).
- **`DeriveVisitedRings` leaks its out-lists and scratch if either job's `Run()` throws.** **Not a regression**
  — the pre-B7 clip block had the identical shape — but it is the one remaining unguarded native-allocation
  window in the fill pipeline, and the only one left after B2/B3 closed the `EnsureCapacity` twins.
- **`TileGeometryStore.Dispose()` is not idempotent-by-flag.** It relies on `TileGeometryBuffers.Dispose()`
  being idempotent plus `_entries.Clear()`. Fine while the store has exactly one owner and one `using` scope;
  it matters the moment a second owner appears.
- **`StyledLineTileBuilder.WriteMeshData` allocates a transient `List<ITileFeature>` per line layer per tile**
  purely to feed the mint it still owns. B7b deletes it with the mint. Noted so it is not mistaken for a
  permanent cost.
- **Deviation from plan a19, worth carrying into B7b's brief:** `TestTileMeshBuilder.BuildLine` does *not*
  construct-and-ignore a buffer as a19 expected; `extent` stayed on line's builder signature with
  `LineRenderLayer` sourcing it from `geometry.Extent`. Strictly better, but B7b's developer should not go
  looking for harness to delete that does not exist.
- **The store's memoisation makes `MvtGeometryMaterializer` walk EVERY feature of a source layer** (Points and
  LineStrings included) where fill previously walked only its polygons. Behaviourally inert — the kind gate
  rejects them — but a real per-tile decode-cost increase for polygon-sparse layers. **B8's decoder-side
  production should be measured against it**, since B8 is where that walk becomes the decode itself.
- **R14's unreachability is contingent on Decision A** (single-typed features). If mixed-kind features ever
  become representable, `RingAssemblyJob`'s kind gate position *relative to the `prevFeature` update* becomes
  load-bearing rather than inert.

### B2 — recorded findings (non-blocking, for the merge step)

- **✅ CLOSED (verified in the tree during B7 planning, `FillMeshPipeline.cs:311-322`).** Both `Schedule`
  backstops are wrapped in `try/catch { geometry.Dispose(); polyOuterIdx…Dispose(); throw; }`, so the
  asymmetry below no longer exists. Left in place for the history of why it was asymmetric for two stages.
- **The `EnsureCapacity` throw path is now ASYMMETRIC — B3 is its natural home.** B2 closed the leak in
  `MvtGeometryMaterializer.Materialize` (`try/catch { geometry.Dispose(); throw; }`) but its twin at
  `FillMeshPipeline.Schedule`'s polygon/hole backstops still leaks the buffer if it ever fires. B1 declined to
  close it on a "no tooth, not the extraction" argument; that premise weakened once the allocation and the
  throw sat in the same short method, which is why the materializer half was closed. **Closing only one half
  is worse than closing neither for a future reader** — do the `Schedule` half in B3.
- **D10's lesson: the `try/catch` is what makes the backstop observable at all.** Deleting the two
  `EnsureCapacity` calls reds the ownership tooth *only* because the `catch` dies with them. The substantive
  claim is unchanged and still true: **no behavioural test observes the backstop** — `FillMeshPipelineBoundsTests`
  tests the *function*, never its call sites. Do not read D10's red as coverage of the backstop itself.
- **D8 was INERT, not a blind tooth — read the sweep table with this in mind.** The row predicted T1 would red
  on a `<3`-point ring filter in the GeoJSON bridge; nothing red. Cause: the seam fixture's
  `AssertFixtureShape` pins **both** rings at 4 points, so such a filter can never fire. The row was replaced
  by **D8b** (bridge emits only each feature's first path), which does red T1 and T2a. A later reader
  scanning the table without this note would mis-conclude that T1 is blind.
- **The GeoJSON bridge's "no short-ring filter" contract has NO tooth**, and cannot get one from the current
  polygon-only 4-point fixture. `T3` covers the **MVT** materializer only. The bridge is test-support code in
  B2; **the tooth belongs with its promotion to production in GeoJSON S2** — carry this to that stage, it is
  the one B2 obligation that does not live in this epic.
- **Landmine #2's instrument now exists at the seam (T3)** — B1 predicted B3 would have to build it. B3 still
  owes its **own** tooth for the *line* consumer's short-ring filter; T3 observes only that the shared buffer
  stays unfiltered, not what any one consumer does with it.

### B3 — recorded findings, and a DISARMED TOOTH B4 must fix

- **⚠ `WriteIntoPath_ReferencesNoMvtCarrierTypes`'s anti-vacuity clause is DISARMED for the line file, and
  will be for symbol.** The clause counts the **substring** `MvtGeometry` in each file's text, to prove a file
  was not simply gutted along with its MVT carrier types. Two independent things broke it:
  1. **B2's rename** (`MvtTileGeometryMaterializer` → `MvtGeometryMaterializer`, made to escape the *carrier*
     token check) means `StyledLineTileBuilder.cs:207`'s `new MvtGeometryMaterializer(...)` now satisfies the
     clause **by substring**, even though B3 legitimately removed that file's only `MvtGeometry.Decode` call.
     The clause can no longer detect a gutted line builder.
  2. **The count includes comments.** `SymbolFeatureExtractor.cs` carries the token at both `:236` (the real
     call) and `:21` (an XML `<see cref="MvtGeometry.Decode"/>`). When **B4** removes the call, the clause
     still passes — on the doc comment.
  **This is the same name-collision family as B2's deviation #1, inverted**: there a new name made a tooth red
  spuriously; here a new name makes a tooth green spuriously.
  **B4 must fix it deliberately** — the clause's original premise ("these files still decode geometry") is
  *expiring by design* as this epic moves consumers onto the buffer, so it cannot simply be tightened and left
  pointing at the same thing. Whatever replaces it must still detect a gutted file, must not match comments,
  and must not match a longer identifier that merely contains the token. That is an assertion-changing edit to
  an existing structure test and therefore needs its own justification and RED row.
- **`run-tests.sh` printed FIXTURE names, not test names** (greedy `sed` capturing `classname`'s `name=`).
  Fixed in `57347f23` for the script and the `AGENTS.md` recipe. Every RED-verify run through the script
  before that commit had weaker name evidence than it appeared to.
- **Never chain a sweep behind a watcher.** B3's developer had a chained background watcher outlive its row and
  start a *second concurrent sweep* over the same tree: two injections interleaving, a `Segmentation fault: 11`,
  and three `exit 3` rows. Rows D8–D15 were discarded and re-run serialized. **The working tree is a
  single-owner resource** — the same reason a review arm must not read it while a sweep mutates it.

### B5 — recorded findings (non-blocking, for the merge step)

- **B5 does NOT deliver format dispatch — do not read the structural tooth as more than it is.**
  `new MvtGeometryMaterializer(…)` stays hard-coded at `StyledFillTileBuilder`, `StyledLineTileBuilder` and
  `SymbolFeatureExtractor`. B5 removed the **interface** coupling to MVT, not the builders' **construction**
  coupling. The honest claim: *a source format no longer has to transcode into MVT commands to implement
  `ITileFeature`.* The encoding-keyed dispatcher is **GeoJSON S2's**.
- **`ITileFeature` is now an empty marker over `IFeature`.** Kept as the element type of `ITileLayer.Features`
  and as the name of the "came from a tile layer" role. Collapsing it into `IFeature` is a ~100-site mechanical
  rename with zero behavioural content — a maintainer call, deliberately out of a behaviour-preserving stage.
- **`InMemoryTileFeature` has zero production callers after B5** (its only one was the background quad) and
  survives for ~19 test-fixture sites. It belongs in the test assembly next to `DictionaryFeature`. Not moved
  in-stage because relocation is churn. **Predicate for closing it:** the next stage that already edits those
  fixture files.
- **The desync guard is retired ON THE MVT PRODUCER ONLY — the hazard MOVED, it did not leave the seam.**
  `MvtGeometryMaterializer` took two positionally-joined columns (bytes, kinds) and length-checked them; it now
  takes one feature list, so *there* nothing is left to desync. But the **same stage** introduced
  `PathGeometryMaterializer`, whose `kinds` and `featurePaths` are once again two independent lists joined by
  position — so that producer **keeps** a length check, placed **before `Allocate`** (after it, four
  `Allocator.Persistent` arrays exist and a throw strands them on the one exit path no caller can dispose).
  Pinned by `TileBackgroundQuadProjectionTests.PathMaterializer_KindColumnShorterThanPaths_
  ThrowsBeforeAllocating`, structural for the same reason its `MvtGeometryMaterializer` sibling is: the path is
  unreachable in production, and this seam holds unreachable throw paths to "**by reading the code, not by
  arguing reachability**".
  The surviving half of the original contract — kind read from the feature, never a literal (landmine #1) — is
  observed by `MvtGeometryMaterializerTests.Materialize_FillsTheKindColumnFromEachFeature_NotALiteral`.
- **⚠ `RingAssemblyJob` has no `FeatureGeometryType` gate.** Fill is polygon-only *by its caller's filter* at
  `StyledFillTileBuilder`'s feature loop, in the Unity assembly, with the Burst stage defenceless behind it.
  Landmine #1 is guarded in exactly one place. RED row **D8** (delete that filter) measured what observes it —
  see the dev report. **This is the single largest remaining silent-corruption surface in the epic and it
  outlives B5.**
- **The flatten-and-copy now lives once**, in `MapRenderer.Jobs/PathGeometryMaterializer`, with the GeoJSON
  test bridge (`Tests.EditMode/Jobs/GeoJsonSliceMaterializer`) delegating to it. This partially discharges B2's
  *"the bridge's promotion belongs to GeoJSON S2"* finding: **S2 now needs only the `TileSlice` → paths
  adapter, not a second flatten.** B2's other bridge obligation — the "no short-ring filter" contract having no
  tooth — is untouched and still S2's.
- **B2's `Extent`-routing inspection point is still not a tooth.** A literal `4096.0` in fill would still pass
  the whole gate, because every fill fixture is extent 4096. `PathGeometryMaterializer` makes a non-4096
  fixture cheap to build; the first stage that wants one should take the shot.
- **The retired MVT quad encoding survives as a test-owned oracle**, `Tests.EditMode/TestSupport/
  FullExtentRingCommandStream.cs`. It has three readers (`TileBackgroundQuadProjectionTests`,
  `A6NonMvtDecoderTests`, `A7TileFeatureSourceTests`) — it must never be edited to match a new implementation;
  it is the frozen record the background quad's behaviour preservation is measured against.

## Purpose audit (2026-08-08) — the arc shipped a comb, not a waist

An **arc-level** audit, run after B5 because the maintainer's complaint ("we decided to go through IR, so MVT
should stop mattering after parse — instead there is a public MVT-named interface with multiple implementers")
was not answered by any stage review. The auditor was deliberately denied the stage plans, dev reports and
reviews — they contain the reasoning that produced this shape and the scope fences that excluded the question —
and was told to treat this doc's "resolved decisions" as claims to check, not as a frame to adopt.

**Verdict: the epic achieved its narrow goal and missed its stated purpose.** `ITileFeature` is genuinely empty
and MVT's command stream is off the neutral interface — real, verified, and every stage did what its scope said.
But the shape this doc calls "the anti-pattern" — format-encoded geometry carried on the feature, decoded per
consumer — was **renamed, not removed**. Because materialization sits downstream of per-layer selection, encoded
words must still reach every consumer, which is what forces `uint[]` onto features, forces `IMvtGeometryCarrier`
to be public and MVT-named in engine-free Core, and forces `new MvtGeometryMaterializer(…)` at all three
consumers. `IMvtGeometryCarrier` is load-bearing **only because** decode happens per-consumer — which is the
thing being questioned. That is the circularity that let it look justified.

**Why no stage caught it.** Every brief asked "is this stage correct", never "does the arc reach the goal". The
scope fences were individually right and collectively fatal: B4's plan explicitly noted "B6's memoization payoff
just grew" and fenced it out. "Measure-gated" (Decision C, Option A) read as *optional* rather than as *work
someone must schedule*. And this doc's own false eager-decode premise (above) pre-closed the question, so a
reviewer inheriting it as settled had no reason to reopen it.

### Quantified

- **108 materialization sites per `openmaptiles` tile** (+1 background = 109), from `liberty.json`
  (`Bootstrapper.cs:70`, 111 layers): 16 fill + 67 line + 25 symbol, all with a `source-layer`. **61 of them
  decode the same `transportation` layer.** Mechanism: `TileMeshLayerProcessor` holds **one**
  `ITileMeshRenderLayer` (`:18`) and selects for that layer alone (`:46`); each `WriteInto` mints a fresh
  materializer (`StyledFillTileBuilder.cs:263`, `StyledLineTileBuilder.cs:204`, `SymbolFeatureExtractor.cs:162`
  via `StyledSymbolTileBuilder.cs:98–103`). **Nothing memoizes.** Fill/line sites are gated on
  `features.Count > 0`, so ≤108 actually fire per tile; 108 is the structural count of independent producers.
- **Cost figures** are in §"Eager whole-tile decode" above (≤1.1 % unused-layer cost vs 2.7×–15.5× duplication).
- **Not bounded from data, and stated rather than guessed:** the exact firing count and selected-feature fraction
  per style layer need the filter engine run over these fixtures.

### Claims in the record that the code does not support

| claim | status |
|---|---|
| doc, Decision B: "geometry travels only in the blittable buffer, joined by ordinal" | **False of the code**, and self-contradicted by P3 in the same bullet. Corrected in place above. |
| doc, §Ownership: "minted once per (source, tile) worker pass" | **False** — per (layer, tile), ~109/tile. Corrected in place above. |
| doc, §Eager decode: "transcendental geo-conversion … often 1" | **Refuted** on both clauses. Corrected in place above. |
| doc, §The conclusion: "format-neutrality collapses here" | **Aspirational, not shipped.** Marked as target above. |
| `6683bd8e` (B2): closes the Extent-routing finding "**BY CONSTRUCTION** … no longer a second extent for a stage to substitute a literal for" | **Overclaimed, and refuted one call frame up.** The narrow half is true (`LayerInput.Tile`/`.Extent` are gone), but `StyledFillTileBuilder.WriteGeometry` (`:281`) still takes `double extent` *alongside* the materializer and uses it (`:334` `extentInv`), and `WriteMeshData` (`:263`) passes the same value into both. The second copy **is already a literal in production**: `TileBackgroundLayerProcessor.cs:106–108` passes its own `const double Extent = 4096.0` into both. This violates the rule B2 itself wrote into `ITileGeometryMaterializer`'s doc ("callers … must not carry a second copy alongside it"). A commit message cannot be rewritten, so the correction lives here. B5's own findings already conceded the outcome ("still not a tooth"), contradicting B2's "closed". |
| `78328903` (B5): "a GeoJSON or MLT source **can implement the neutral feature interface without transcoding**" | **True as interface-implementability, misleading as an outcome.** A non-MVT feature can implement the empty `ITileFeature`, but cannot *render*: all three consumers hard-code `new MvtGeometryMaterializer(…)`, which throws for any feature lacking `IMvtGeometryCarrier` (`:56–63`). The commit *does* state the fence, hundreds of words later — overclaimed-then-caveated, not unsupported. |
| `f613c924` (docs): "GeoJSON S2 consumes `TileGeometryBuffers` directly, which makes payoff (4) **unconditional**" | **Premature.** No production path lets a non-MVT source reach `TileGeometryBuffers`; the only non-MVT route is test-side (`Tests.EditMode/Jobs/GeoJsonSliceMaterializer`). Payoff (4) is still conditional on work S2 has not done. |
| `d73128b0` "three dispose sites"; `6683bd8e` "zero `Allocate` in `Schedule`, one in `Materialize`"; `05f7f816` Core free of `Unity.Collections`; `78328903` sole production cast + empty `ITileFeature` | **All confirmed** (recorded so the audit is not one-sided). |

### What the fix requires (four hard parts, ranked by real difficulty)

1. **The decode seam is engine-free; Waist 1 is not.** `ITileDecoder.Decode(byte[]) → IDecodedTile` is in
   `MapRenderer.Core.Tiles` (asmdef: `Unity.Mathematics` + `UniTask` only); `TileGeometryBuffers` is
   `Unity.Collections`, in `MapRenderer.Jobs`, which *references* Core. **The dependency points the wrong way.**
   Options: **(a)** move the decode seam out of Core into Jobs/Unity — the only one that regresses nothing, and
   the largest single piece of work; **(b)** have Core declare a blittable-free buffer interface — reintroduces
   exactly the coupling B5 removed, non-starter; **(c)** reference `Unity.Collections` from Core — retires the
   property B4 spent a stage buying, plus Core's `dotnet test` story. Note this barrier is **not** a justification
   for the current design: it exists only because the epic put Waist 1 in Jobs while leaving decode in Core.
2. **The clip stage consumes its buffer destructively.** ~~`FillMeshPipeline.Schedule` owns and rebinds:
   `ReleaseFeatureGeometryType()` → `Dispose()` → `AdoptClippedLists(…)` (`:267–270`).~~ A tile- or kick-owned
   shared buffer cannot be destroyed by one of N consumers, so clip must become **derive-into-new** — mechanically
   small, but it inverts `TileGeometryBuffers`' documented ownership contract from *transfer* to *borrow*, which
   must change visibly. **Recorded hazard: disposing an `AsArray()` view is a silent no-op** under Collections
   6.5.0, so a two-owner mistake here leaks quietly.
   **✅ SHIPPED in B7a** — read the struck text as history, not as a live claim. `Schedule` no longer touches its
   input: `DeriveVisitedRings` runs `RingClipJob`/`RingSelectJob` into fresh lists and returns
   `TileGeometryBuffers.AdoptDerivedLists(source.Tile, source.Extent, source.FeatureGeometryType, …)`, which
   **copies** the kind column so the borrowed source keeps owning everything it owns. The ownership inversion
   was made visible on `ITileGeometryMaterializer`, `TileGeometryBuffers` and `TileGeometryStore`, and is pinned
   by T2/T2b (borrow, not transfer) plus
   `TileGeometryBuffersTests.AdoptDerivedLists_CopiesTheKindColumn_SoTheSourceSurvivesTheDerivedBuffersDispose`.
3. **The ordinal join changes meaning — the one place output can silently change.** Today `RingFeatureIdx`
   indexes *the caller's own selected list*, after `OrderBySortKey` for fill (`StyledFillTileBuilder.cs:213`). A
   shared buffer indexes the layer's full feature list in decode order, so every per-feature side array
   (`featureColors`, `featColors`/`featWidths`, symbol's counting sort at `SymbolFeatureExtractor.cs:171–176`)
   must re-index. **Fill's sort-key reorder is the sharp edge:** it currently reorders the *materializer input*,
   which reorders triangle emission, which is load-bearing draw order under the ZWrite-off painter's contract.
   With a shared buffer, fill must reorder its *ring iteration* instead. Byte-identity is achievable, but any
   plan here carries a **non-empty snapshot risk** and needs an explicit fill-draw-order tooth.
4. **Lifetime — smaller than it appears, and only for one variant.** Verified (see §Ownership): both worker
   halves run sequentially in one `UniTask.RunOnThreadPool` lambda in `KickMeshBuild`, so a **per-kick
   `using`-scoped buffer needs no refcount**. The lifetime problem exists *only* if the buffer is attached to the
   **cached `IDecodedTile`**, which `SharedTileDecode` explicitly holds with "**no lifetime protocol** … no
   refcount, no `Acquire`/`Release`" and drops by GC reachability on several paths (never-kicked `LoadedTile`,
   dropped symbol-queue entry, completed kick task). Attaching `Allocator.Persistent` there converts every one of
   those into a leak. `IDecodedTileHandle` has exactly one member (`GetOrDecode()`); `ITileFeatureSource.Release`
   is *fetch* interest, not buffer ownership.

**What does not break:** Waist 2 is untouched (post-earcut, never sees Waist 1). Monomorphism holds (still one
tile-local type, no space tag). Filters/selection read only `IFeature`, never coordinates. Earcut's tile-scale
epsilons are unaffected. `PathGeometryMaterializer` survives, demoted from a consumer-facing seam to a
decoder-side one — the background quad becomes a synthesized one-feature *source*, arguably what it always was.

**Favourable pre-existing seam:** `IDecodedTileHandle`'s own doc already anticipates the eager direction — "a
future in-memory/GeoJSON source would return an EAGER handle wrapping a pre-sliced `IDecodedTile` (no bytes, no
fetch) — same interface, no change to the coordinator."

### Consequence for the stage sequence

**B6 as specified fixes none of this.** It is a dedupe keyed by selected-feature ordinal that explicitly
"preserves lazy-per-selected-feature", so `IMvtGeometryCarrier` and the per-consumer producer construction both
survive it unchanged. The hard parts above split naturally into two stages, and the split is deliberate: a
**per-kick materialization** stage (hard parts 2 + 3 — the mechanical output-risk work, no assembly or lifetime
change, and it subsumes B6's dedupe by construction) followed by a **decoder-produces-IR** stage (hard parts
1 + 4 — the structural work that actually deletes `IMvtGeometryCarrier`). Sequencing them this way de-risks the
silent-output-change surface *before* touching assembly boundaries.

**RESOLVED by the maintainer, 2026-08-08:** take this two-stage route. B6's scope is **retired**, not deferred;
the stages are **B7** (materialize once per kick) and **B8** (decoder produces IR) in the sequence table above.
The numbering deliberately skips reusing "B6" so the retired spec stays distinguishable in history from what
replaced it.

### Closed in B7a — the `RingAssemblyJob` kind gate

~~`RingAssemblyJob` has **no `FeatureGeometryType` gate**; landmine #1 is guarded solely by
`StyledFillTileBuilder.cs:231`.~~ **CLOSED in B7a.** `RingAssemblyJob` now takes
`[ReadOnly] NativeArray<TileGeometryType> FeatureGeometryType` and skips any ring whose feature is not a
Polygon, placed before the exterior-sign reset so a non-polygon leaves no trace in the classifier state.

Two consequences worth recording:

- **The fill-side filter at `StyledFillTileBuilder` is DEMOTED**, not deleted. It is no longer the correctness
  guard; it survives as bookkeeping — it decides which ordinals get a colour and how many rings are gathered
  into the visit order. Deleting it is now a behaviour-neutral simplification, and B7a's R15 row measured
  exactly that (nothing reds). Recorded for B8; do not read "nothing reds" as "no tooth exists" — the Burst
  gate subsumes it, which is the honest reason.
- **The job's class doc said "Does NOT reference MapRenderer.Core", which was too broad.** Corrected in place
  to the rule that was actually meant: no Core *code* (and in particular no `System.Math`) on the Burst path;
  blittable Core *value types* are fine. `TileToGeoJob` in the same assembly already takes Core's `TileId` and
  emits Core's `GeoCoordinate`.

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
**To Unity, not Jobs:** `ITileFeatureSource`/`IDecodedTileHandle` — they need UniTask, which
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
implementations). The handle it mints (`DecodedTileLease`, replacing `SharedTileDecode`) is a **reference
count**, born with one reference that belongs to whoever received it. `IDecodedTileHandle` is
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

---

#### The option-S record (superseded, kept for its reasoning)


Once the tile owns `Allocator.Persistent` buffers it needs an owner, and `SharedTileDecode` documents having
**no lifetime protocol**. The leak surface is far smaller than that implies: **native memory exists only after
`GetOrDecode()` runs, and that has exactly two production call sites** (`TileLayerProcessorRunner:40`, `:152`).
So a never-kicked `LoadedTile`, a discarded fetch and teardown all have *nothing to free*. **Exactly one path
escapes the kick task: D6 sprite parking** (`SymbolLabelSubsystem:596–610` enqueues the handle;
`PumpBuilds:679–712` dispatches it later on its own `RunOnThreadPool`).

**Chosen — S:** `IDecodedTileHandle.OpenScope()`, a counter under `SharedTileDecode`'s existing `_gate`; 0→1
decodes, 1→0 disposes and forgets (a later scope re-decodes from retained bytes). `GetOrDecode()` outside a
scope **throws** — a programming error, and a tooth. `_fault` stays sticky across scopes. Exactly **two**
production `using` sites: `KickMeshBuild`'s pool lambda and `PumpBuilds`' parked dispatch.

*Why S:* **every drop path is safe with no edit at all** — a cancelled parked entry, a `SetStyle` drain, a
teardown never opened a scope, so there is nothing to release. Leak-safety comes from construction, not from
remembering to dispose in five places. It is also correct under the one real race (`PumpBuilds` dispatching
while the kick's scope is open — the counter shares or re-decodes, either way correct).

*Accepted costs:* "decode exactly once, **ever**" becomes "once per open **scope**" — that paragraph and the
tests pinning it must be restated to the property that actually matters (the mesh and symbol passes of one kick
share one decode). And a parked build dispatched after its kick's scope closed **re-decodes once**, off-main,
bounded by the `SetStyle`→`SpritesSettled` window.

*Rejected — T (single-owner with explicit transfer):* avoids the re-decode, but buys five disposal sites in
`SymbolLabelSubsystem` instead of zero, and its fifth is a genuine hazard — `PumpBuilds` dispatches via
`RunOnThreadPool(…, cancellationToken: captured.Ct)`, so on cancellation the delegate may never run and the
parked entry's buffer is never freed. A silent leak, which is the failure class this codebase keeps meeting.

### Sequencing — C1 is three commits, not one

**`IMvtGeometryCarrier` cannot be deleted until line and symbol stop minting.** Three production sites
construct `MvtGeometryMaterializer` today (`TileGeometryStore:99`, `StyledLineTileBuilder:212`,
`SymbolFeatureExtractor:162`), so B7b's work does **not** disappear into C1 — it becomes P2.

| phase | scope | output risk |
|---|---|---|
| **P1** | the move + `ProtobufReader`→`Core/Protobuf/` + delete `ITileFeature` | **none** |
| **P2** | line + symbol onto the shared buffer, ordinal re-base (the never-done B7b) | **all of it** |
| **P3** | tile owns geometry; option-S lease; delete `IMvtGeometryCarrier`/`TileGeometryStore`/the store parameter | none |

**Fast-runner cost, stated not hidden:** a `Unity.Collections` shim is ~200–300 lines and viable, but **must not
be written**.

> **The reason, restated 2026-08-09 — the original one went stale.** It read: "it cannot reproduce the
> silent-no-op `AsArray()` dispose, so every ownership tooth would be vacuously green." P3's own per-backing-mode
> correction retired that: **every buffer a consumer borrows is array-backed**, whose double free is *loud*, and a
> shim with a disposed flag would reproduce it fine. The silent mode belongs solely to fill's derived
> `AdoptDerivedLists` buffer, which no consumer borrows and the fast runner never exercised.
> The reason that actually holds is broader: `MvtGeometryMaterializer` runs `MvtDecodeJob.Run()`, so shimming the
> decode chain means shimming `NativeArray`, `NativeList`, `Allocator`, `IJob` **and** Burst semantics —
> reimplementing the thing under test, with the ownership teeth then testing the shim.

`CorePipelineTests`' real-fixture probe plus four test files leave the 0.1 s loop. *(Prediction, written pre-P3;
the measured departure was 133 rows — see the P3 section, where the two fast-runner-local files among them are
retired rather than rehomed.)* **Mitigation that must not be missed:** `DictionaryFeature` becomes a `partial class`
implementing only `IFeature` in the fast-runner half, or the entire Expressions/Filters/Style fast suite dies.

> **Correction, measured in P1 (2026-08-08): the cost above is P3's, not P1's, and P1 did not pay it.**
> `Tools/core-tests` compiles **files**, not assemblies, and everything P1 relocates is plain managed C# with no
> `Unity.Collections` in it — so re-pointing ten `<Compile Include>` paths at `MapRenderer.Jobs/` kept the fast
> suite at **1260 tests, unchanged**, with no shim. (Precedent: the csproj already compiles four engine-free
> `MapRenderer.Unity/Text/*.cs` files.) What actually ends this is **P3** — the decoded tile owning
> `Allocator.Persistent` buffers — and the ban on shimming it away stands. Consequently the `DictionaryFeature`
> partial-class mitigation was **not needed in P1** and was not made: `IFeature` stays in Core and
> `IMvtGeometryCarrier` is still compiled into the fast assembly from its new Jobs path. P3 deletes the carrier
> outright, at which point `DictionaryFeature` simply drops the interface.

### P3 — landed (2026-08-09). Measured outcomes, and the one named gap

**Gate: `VERDICT: PASS — 2298/2298`** (baseline 2286; +12 net). Movable-snapshot set **EMPTY** — no snapshot
moved.

**Hard part 1 is CLOSED.** `ITileDecoder.Decode(TileId, byte[])` lives in `MapRenderer.Jobs.Tiles`, the
decoded layer owns its `TileGeometryBuffers`, and the dependency no longer points the wrong way. Option (a)
was taken, as predicted, across P1–P3.

**Hard part 4 (ownership/lifetime) was CLOSED by the option-S lease, and is now closed differently.** As
shipped in P3: `IDecodedTileHandle.OpenScope()`; a counter under `SharedTileDecode`'s `_gate`; 0→1 decodes,
1→0 disposes and forgets; `GetOrDecode` outside a scope throws; `_fault` sticky across scopes. Exactly two
production `using` sites, and — the property that made S worth choosing — **no drop path needed an edit**,
because a never-kicked tile, a discarded fetch, a cancelled parked entry, a `SetStyle` drain and teardown all
dropped a handle that had never opened a scope.

> **RETIRED by D1 (2026-08-09).** That last property is exactly what the eager decode gives up: the tile is
> already built by the time any drop path can reach it, so **every drop path is now an owner with a release
> in it**. Four funnels, four releases, six teeth — see §"Disposal" above. The paragraph is kept because it
> is the clearest statement of what the reference count had to buy back, and at what price.

> **Fast-runner cost — MEASURED, and ~3× the prediction. 1260 → 1127 (−133 tests, 22 `<Compile>` entries).**
> The prediction was "`CorePipelineTests`' fixture probe + four files". The reason it under-counted: making
> `MvtDecoder` materialize pulls `Unity.Collections` into the **whole decode chain** —
> `MvtDecoder`, `MvtModels`, `DecodedTile`, `ITileDecoder`, `FeatureSelector`, `SourceLayerResolver` — so
> every fast-runner test that decodes the committed fixture *or resolves a source layer* leaves with them:
> `LineMiterHairpinTests`, the three `Meshing/` validators + `GlobeSubdivisionTests`, `PropertyFilterTests`,
> `DataDrivenColorBakeTests`, `FillPaintTests`, `DataSourceTests`, `StyleParserTests`, on top of the five
> predicted. **131 of the 133 still run in the Unity EditMode gate** — for those the coverage is slower, not
> absent. **The other two rows stopped running anywhere, and were retired in the C1 fix stage (2026-08-09):**
> `Tools/core-tests/CorePipelineTests.cs` and `Tools/core-tests/LineMiterHairpinTests.cs` were fast-runner-**local**
> (never in `MapRenderer.Tests.EditMode`), so they had no slower runner to move to, and they no longer compile
> against the tree (one-arg `MvtDecoder.Decode`, `MvtFeature.Geometry`, `IMvtGeometryCarrier` are all gone).
> Their properties are held in the Unity gate by `FullPipelineTests` (239 features via `DecodeTests`;
> clean-holed-polygon area conservation via `Countries_FullPipeline_…_AreaConserved` +
> `HoledPolygon_WorstCase_AreaIsConserved`) and by `BoundaryGlitchMeshTests.Boundary3Mesh_HasNoAcrossTileTriangle`
> over the **same three** boundary fixtures, through the production Burst path rather than the managed oracle.
> **No shim was written**; the ban stands. `GeoJsonForkNeutralityTests` (GeoJSON S1's falsification tooth)
> survived — it names the dead symbols only inside string literals.

**Peak native memory under eager whole-tile materialization — MEASURED 2026-08-09, item CLOSED.** Eager decode
materializes *every* layer of a tile, including layers no style names. Walking every committed fixture and
summing what `TileGeometryBuffers.Allocate` takes per layer
(`16·verts + 4·(rings+1) + 4·rings + 4·features`, ring/vertex counts from the same walk
`FillMeshPipeline.PrecountRingsAndVertices` does):

| | |
|---|---|
| peak per decoded tile | **967 KB** (`boundary-9-274-168`, 11 layers); next `water-real-stockholm-archipelago-9-282-150` 931 KB, `boundary-6-34-21` 882 KB |
| median per decoded tile | **~552 KB** |
| layers per tile | **8–12** on the openmaptiles fixtures (`sample-tile`, a synthetic non-openmaptiles tile, carries 3) |
| eager waste vs. a style-filtered decode | **0–1.3 %** — `liberty.json` names **14** distinct source-layers and these tiles carry 8–12; the only unnamed layer present anywhere in the corpus is `mountain_peak` (0.1–1.3 % of a tile's bytes) |
| live peak | ≈ (kicks in flight) × ~1 MB — **not** hard-bounded, see below |

So the accepted cost of eager whole-tile materialization is, on this corpus, not measurable against liberty:
the "layers no style names" worry does not bite. **Still open, deliberately not claimed:** the *offsetting*
saving is uncomputed. The per-feature `uint[]` command arrays are now consumed and dropped inside
`MvtDecoder.DecodeLayer` rather than retained on every feature for the tile's life, so eager is plausibly
**net negative** on peak bytes — plausibly, because nobody has measured the retained-command-array side.

**There is no concurrent-kick cap** (this paragraph previously claimed the figure above was "bounded by" one).
`MapViewConfig.MaxMeshBuildsPerTick` (default 2, `MapViewConfig.cs:46`) rate-limits how many kicks *start* per
tick (`TileManager.cs:1385,1457`); nothing bounds how many kick lambdas — hence open decode scopes, hence live
decoded tiles — exist at once. In practice it self-limits (2/tick × task duration ÷ tick duration ≈ single
digits), but no hard bound exists.

> **D1 changes the consequence, not the fact.** Kicks-in-flight is no longer what bounds residency: under the
> eager decode a tile is resident from **fetch completion**, so the bound is *fetched-but-not-yet-kicked*, and
> that is bounded only by cover size. The self-limiting argument above no longer applies. Quantified, with the
> remedy named, in `docs/per-layer-tile-processing-design.md` §"Eager decode: peak resident decoded-tile
> memory".

**Recorded finding 0 (line's `TileToGeoJob` extent routing) is CLOSED.** `Meshing/LineExtentRoutingTests`
drives line's own subdivided-projection path over a layer whose extent is **2048, not 4096**, and requires
every vertex to move between the two extents. RED-verified with the exact injection that previously survived
a full green 2286-test gate (`Extent = 4096.0` at both `TileToGeoJob` sites): the tooth fails. P3 was the
right phase for it because the fixture cost collapsed here — a layer owns its buffer, so a non-4096 extent is
a constructor argument.

**P2 recorded finding 2 is OBSOLETE, not deferred.** It asked for an arm asserting *zero* materializations
for a source-layer whose only naming style layer selects nothing. Under P3's eager whole-tile decode every
layer is materialized regardless of selection — the documented accepted cost — so that arm would assert
something false. The `selected.Count == 0` guard is now a plain early-out and its comment says so.
**P2 recorded finding 3** (nothing checks `max(selected.Ordinal) < geometry.FeatureCount`) — **CLOSED
2026-08-09**, see §"The recorded leftovers" below.

**A blind tooth found by injection, and closed.** `DecodedLayerGeometryTests.LayerGeometry_IsOneBuffer…`
(the rehomed store memo tooth) runs over the `InMemoryTileLayer` test double. Injecting a re-materializing
`Geometry` getter into the **production** `MvtLayer` left it GREEN — it pins the double, not production.
`DecodedTileOwnershipTests.ARealDecodedLayer_HandsBackTheSameAllocationOnEveryRead` was added for the
production configuration and does go red. Both are kept: the sibling still pins the contract the fixtures
rely on.

#### tooth D2 (the parked path) — CLOSED (2026-08-09)

`Tests.EditMode/Text/SymbolParkedRedecodeTests` drives a sprite-parked build end-to-end and deep-compares its
committed labels against the same tile built un-parked. **Test-only: production diff zero.** Gate
**2300/2300**. Reviewed by both arms — **APPROVE** from each, no REQUIRED.

**The two numbers it establishes, which were previously only asserted in prose:** an un-parked kick decodes
**once** (one scope wraps the mesh read and the symbol pass); a parked build decodes **twice** (its kick's
tile is freed at scope close, and the drain re-decodes from the retained bytes). Option S's accepted cost is
now a value in an assertion rather than a paragraph here.

> **D1 (2026-08-09): the differential is now 1-vs-1, and the fixture's two lifetime assertions INVERTED.**
> The parked arm's decode count went 2 → 1 (the park holds a reference, so there is nothing to re-decode) and
> its disposed-while-parked count went 1 → 0 (that reference is what keeps the kick's tile alive). The
> inversion is the proof the reference count works; the label differential the fixture exists for is
> unchanged. Two consequences for anyone reading the test: the oracle arm's mirror guard was **replaced, not
> renumbered** — `oracleProbe.DecodeCount == 1` no longer discriminates now that both arms are 1, so the new
> differential is the *lifetime* (`DisposedCount` 0 parked vs 1 un-parked) plus "nothing ever parked"; and the
> off-main assertion moved from the drain's decode (there is no second decode) to the drain's **extract**,
> read from the probe's recorded layer-read threads. The fixture keeps its name; "Redecode" is historical.

**One unexplained observation, recorded as unexplained.** Review arm 2 injected a skipped `Dispose()` and its
batch Editor hung at ~100 % CPU for 10+ minutes *after* the tests had finished. Its report attributes this to
"native-safety-handle bookkeeping stalling on the leaked allocation" — but that is the reviewer's reading,
not a measurement, and **R5 re-ran the same injection and the Editor exited cleanly.** So: not reproduced,
mechanism unknown, and plausibly nothing to do with the leak at all (batch-mode shutdown hangs happen on
their own — licensing client, domain reload). If the leak *is* implicated, shutdown-time leaked-allocation
reporting with stack-trace symbolication is the likelier mechanism, and it would scale with the number of
leaked allocations — meaning it would need a full-gate run with the injection live, not a single filtered
test. **Do not cite this as a property of the lease until someone reproduces it deliberately.** It is written
down only so that a future shutdown hang has a prior to check against.

**Six RED rows, each firing a different assertion** (R1–R4 by the author, R5–R6 added after review moved an
assertion): drain scope deleted → zero labels committed · lease forgets without freeing → the parked-disposal
guard · park bypassed → the vacuity guard · drain loses the sprite atlas → the label-count differential
(248 vs 498) · tile-id corrupted on the re-decode dispatch only → `AnchorRender` mismatch at index 0, which
is what proves the field-by-field comparison is not decoration · drain's scope opened and never closed →
the balance assertion, "1 of 2 were not".

> **A shadowed assertion is an unverified assertion.** R5 (never dispose) was intended to RED-verify the
> balance check and did not reach it — an earlier guard caught the same defect first and the run stopped
> there. The balance assertion only got its own evidence from R6, a defect that leaks the *drain's* tile
> while leaving the kick's disposal intact. Ordering matters when reading a RED sweep: "the row went red"
> is not evidence for any assertion except the one that actually fired.

**Two review findings folded in.** The oracle arm's mirror-image guard now reads the **decode count**, not
the pending-queue depth: the queue is 0 after the pump because a settled queue drains on the first
`PumpBuilds`, and 0 before it because the enqueue happens on a pool thread just dispatched — the count is the
only trace a park leaves that nothing can race away. And `LabelInstanceAssert` gained `UpRender` /
`PathUpRender` (decode-derived exactly like the `AnchorRender` / `PathRender` already compared, so a
re-decode bug isolated to the surface-normal fields would have slipped through) plus `Kind` / `IconImage`.
It still does not compare five style-constant fields, and now says so rather than claiming to be exhaustive.

Full detail: `../unity-map-renderer-devloop/agents-devloop/ir-c1-p3-d2-report.md` and the two arm reports.

### The recorded leftovers — CLOSED (2026-08-09)

Three findings recorded across P2/P3/D2, closed as teeth. **Test-only: production diff zero.** Gate
**2304/2304**. Both review arms **APPROVE**, no REQUIRED. Eight RED rows.

| finding | tooth |
|---|---|
| arm 1's F2 — nothing pinned that `RunWorkerPass` decodes with **zero** processors | `DecodeScopeLifetimeTests.RunWorkerPass_WithZeroProcessors_StillDecodesExactlyOnce` |
| **P2 recorded finding 3** — the ordinal domain vs the buffer it indexes | `Jobs/OrdinalDomainTests`, three clauses |
| arm 1's F3 — the parked drain's worker phase must stay off-main | an assertion inside `SymbolParkedRedecodeTests`, no production seam (the probe records the decode thread) |

**Finding 3 was answered, not merely instrumented: the two counts cannot diverge.**
`MvtDecoder.DecodeLayer` appends to `Features` and `geometryList` in the same switch arm with no filter
between them, so a feature with no geometry contributes a **null slot, not a skip**; `kinds` is built by
walking `Features`; and `MvtGeometryMaterializer` throws on a count mismatch *before* allocating. `MvtLayer`
is the only production `ITileLayer`. **Guards were considered and rejected as the wrong instrument** — a
selection borrowed from a *shorter* sibling layer yields in-range ordinals that pass a bound check while
silently mis-attributing colour and width. The bound is a consequence; the lockstep is the invariant, so the
lockstep is what the tooth pins.

> **The corpus has a hole this stage had to hand-fill.** Injecting "compact geometry-less features out of the
> kind column" left the real-fixture clause GREEN — proof that **no committed `.pbf` contains a feature whose
> `geometry` field is absent or empty**, so the whole compaction defect class was invisible. The tooth
> therefore carries a ~50-line MVT byte writer. Review arm 1 validated it against the MVT 2.1 spec by
> re-implementing it in Python and parsing the output with an independent protobuf reader — the
> "oracle transcribes production" risk is retired, not argued away.
>
> It also corrected a misreading worth keeping: the absent-`geometry` shape was first documented as "legal
> MVT, ordinary in real tiles". The spec **requires** the field, which is the actual explanation for the
> corpus result that had been recorded as coincidence. The fixture now carries **both** shapes — absent
> (out-of-spec but accepted) and present-but-empty (spec-conformant) — because a compaction keyed on
> `geom.Length == 0` is provably **inert** against the absent-field feature alone.

**Carried forward, recorded not fixed:**

1. **`PathGeometryMaterializer` breaks the invariant, and this branch is heading straight at it.**
   `Materialize()` early-outs on `ringTotal == 0`, which — unlike the MVT sibling's `featureCount == 0` —
   is reachable with features present, yielding `FeatureCount == 0` beside a non-empty feature list. Harmless
   today (its only production caller is the source-less background quad, whose synthetic full-extent ring
   guarantees `ringTotal > 0`), but the test-side `GeoJsonSliceMaterializer` already routes through it, and a
   GeoJSON `ITileLayer` is exactly the second `ITileLayer` that makes the invariant load-bearing. **Align the
   two early-outs before GeoJSON S2 lands.** Both arms concur.
2. **The invariant is enforced by one writer, not by the type.** `MvtLayer.Features` is a public readonly
   `List` and `MvtLayer.Geometry` a public *mutable* field — nothing structural prevents divergence. Consider
   `{ get; private set; }` before a second `ITileLayer` exists.
3. **No tooth observes the SYMBOL path's selection/buffer pairing.** Clause C covers `TileMeshLayerProcessor`
   only; `SymbolFeatureExtractor.Extract` chooses both independently and is the consumer that sizes by
   `Features.Count`. Its distinctive hazard *is* pinned (by clause B); the pairing is not.
4. **A swallowed `WriteInto` throw is invisible.** `RunWorkerPass`'s bare `catch` drops the layer to zero
   vertices with **no log**, so an ordinal OOB presents as a silently missing layer.
5. **Fork, decided: leave `ITileMeshRenderLayer.WriteInto` taking the selection and the buffer as independent
   parameters.** Making the mispairing unexpressible (pass the `ITileLayer`, or a paired carrier) is the
   structurally right answer and would close finding 3 by construction, but it is a refactor across the
   interface and both `Styled*RenderLayer`s whose value only materialises once a second `WriteInto` caller
   exists — nothing on this branch creates one. Clause C is the guard until then. Arm 1 concurs.

> **Leftovers 1–4 are CLOSED by the C1 post-review fix stage (2026-08-09); 5 stands.** See
> §"C1 post-review fix stage" below for what each became, the fork taken on 1 vs 3, and the two items that
> stage newly recorded (the line corpus' slot-vs-ordinal blindness, and the now-inert `tileId` parameter on
> `SymbolFeatureExtractor.Extract`).

**Inherited open items, unchanged by D2:** P2 recorded finding 3 (closed later the same day, above) and
the unmeasured peak-native-memory pass. **Raised by D2's review, both closed the same day** (see
"The recorded leftovers" above): nothing pinned that `RunWorkerPass` decodes when `processors.Length == 0`
— a future "skip the decode for symbol-only sources" optimisation would have erased option S's cost while
D2 stayed green asserting 2 (arm 1's F2) — and nothing pinned that the parked drain's worker phase stays
off-main (F3).

<details>
<summary>The original gap statement, kept for the record</summary>

Deferred deliberately (maintainer, 2026-08-09), not forgotten. It is **the phase's most load-bearing
untested path**:

- It is the **only production site where the lease's re-decode actually happens** — a symbol build parked
  because the sprite fetch had not settled, dispatched by `PumpBuilds` after its kick's scope has closed.
- That re-decode is **the half of option S the maintainer accepted a real cost for**. Everything else about
  S is free; this is the thing that was paid for, and nothing observes it end-to-end.
- What it must do: drive `SymbolLabelSubsystem` through a sprite-parked build, then settle and pump, and
  assert the committed `LabelInstance` list is **identical** to the same tile built un-parked, with no
  `ObjectDisposedException` and balanced open/dispose counts.
- **Production-configuration warning, or the tooth is vacuous:** the park is only reachable through
  `TryBeginBuild` while `SpritesSettled` is false. A fixture whose sprites are already settled never parks
  and the tooth would assert nothing. It must **assert that parking happened** (the pending queue received
  an entry) before asserting anything about the labels.

What exists today instead: the five symbol-subsystem drive helpers now open a scope around
`RunWorkerAndHandoff`, mirroring `KickMeshBuild`, and `DecodeScopeLifetimeTests.DisjointScopes_ReDecode_…`
pins the re-decode semantics at the handle level. Neither drives the parked path.

</details>

### C1 post-review fix stage (2026-08-09) — what the two review arms found, and what it became

Both arms **APPROVED** P1–P3; this stage closes what they found *around* the refactor. Landed as two commits:
a no-behaviour-change one (dead code + false claims in this document, corrected above in place) and a
production-hardening one.

**Production changes, and the tooth for each:**

| # | change | tooth |
|---|---|---|
| B1 | `TileLayerProcessorRunner.RunWorkerPass`'s bare `catch` now **logs** (tile + exception). Control flow is untouched — settle-everything, exactly as before. Three unrelated faults landed there silently, one of them the `InvalidOperationException` the option-S lease raises *precisely so* a scopeless read is loud; the mesh cadence converted it back to silence while the symbol cadence already logged. | `TileLayerProcessorRunnerTests.RunWorkerPass_WhenAProcessorThrows_LogsAWarningNamingTheTile` and `…_ReadingTheDecodeOutsideAnyScope_LogsAWarningNamingTheTile` |
| B2 | `SymbolFeatureExtractor.Extract` reads its **address and extent off the buffer** (`geometry.Tile`/`geometry.Extent`), as line does, plus line's `!geometry.IsCreated` early-out. The `tileId` parameter is now **ignored** (see the open item below). | `Text/SymbolBufferAddressTests` — one arm per quantity |
| B3 | `SymbolFeatureExtractor` sizes its ring buckets from **`geometry.FeatureCount`**, as line sizes its three columns. `RingFeatureIdx`'s values index the buffer's own feature column, so that column's length is the domain being bucketed. | `SymbolBufferAddressTests.TheRingBucketsAreSizedFromTheBuffersFeatureColumn_NotTheFeatureList` |
| B3 root | **`PathGeometryMaterializer` early-outs on `featureCount == 0`**, matching the MVT sibling, instead of `ringTotal == 0` (leftover 1). Features-but-no-paths now mints a ring-less buffer carrying the kind column. | `Jobs/WaistOneProducerAgreementTests`, four arms |
| B4 | `MvtLayer.Geometry` is `{ get; private set; }`, written only through a **set-once, lockstep-checked `AdoptGeometry`** (leftover 2). `Dispose` writes the disposed struct back — a property getter hands out a copy, so the old `Geometry.Dispose()` shape would have freed the arrays and left the layer still claiming them. | `WaistOneProducerAgreementTests`, five arms incl. a reflection check that no public setter or field remains |

**The B3 fork — the materializer fix is the root cause, and B3 alone would have been a REGRESSION.**
The brief offered "fix the consumer *or* fix the materializer, don't do both mechanically". Both were needed,
and the order matters: `SymbolFeatureExtractor` sizes `ringStart[layerFeatureCount + 1]` but *indexes* it at
`ringStart[f + 1]` where `f = selected[si].Ordinal`, and ordinals come from `ITileLayer.Features`. Against a
`PathGeometryMaterializer`-backed layer with features but no paths, `geometry.FeatureCount` was **0** while
the selection was non-empty — so switching the consumer to `geometry.FeatureCount` *by itself* converts
today's silent-empty layer into an `IndexOutOfRangeException`, which `RunWorkerPass`'s catch would then have
swallowed into a silently missing layer. Aligning the producers first makes `FeatureCount` both correct and
safe; the consumer change is then the consistency fix it was described as. (Fixing only the materializer
would have left symbol reading a list that merely *happens* to be the same length as the one addressing it.)

**Newly recorded, NOT fixed here:**

1. **The line corpus is blind to slot-vs-ordinal.** Arm 2 injected the realistic typo — index
   `featSelected`/`featColors`/`featWidths` by the loop index `si` instead of `SelectedTileFeature.Ordinal`,
   the bug `StyledLineTileBuilder`'s own surrounding comment warns about *by name* — and the full gate
   returned **2302/2304**. The only two failures were
   `Meshing/LineSharedBufferTests.LineSharedLayerBuffer_AttributesColourAndWidthByOrdinal_NotBySelectedSlot`
   and `…LineRingOrder_OverTheSharedBuffer_IsDecodeOrderOfTheSelectedLineRings`. Every committed line-layer
   snapshot and visual fixture stayed green, which means every one of them selects features where
   `si == ordinal` (the filter accepts everything, or the accepted features are a dense prefix). The tooth is
   real and fires correctly, but it is a **single point of failure**: delete it, weaken it, or repoint its
   fixture at a filter-free style layer and the entire corpus stops noticing this defect class.
   *Cost of closing it:* one committed fixture (or one style layer over an existing fixture) whose filter
   rejects a feature that is **not** in a trailing position — enough that some selected feature has
   `si != ordinal` — plus a snapshot. Small, but it is a corpus addition, deliberately out of this stage.
2. **`SymbolFeatureExtractor.Extract`'s `tileId` parameter is now inert and should be deleted.** Reading the
   address off the buffer closes the defect *behaviourally*; deleting the parameter would close it
   *structurally*, which is how the mesh seam was closed (`ITileMeshRenderLayer.WriteInto` dropped its
   `TileId` in P2/P3). It was kept here for one reason, stated rather than hidden: the tooth for B2 works by
   **corrupting** that parameter and requiring the output not to move, so removing it in the same stage would
   delete the only discriminating observer of the change being made. Removal is ~50 test call sites plus
   `StyledSymbolTileBuilder.ExtractLayers` and `TileSymbolLayerProcessor`, and wants a structure tooth
   (signature carries no `TileId`) in place of the behavioural one.
3. **`OpenScope()` increments before it allocates the token** (`SharedTileDecode:80-84`), so an allocation
   failure between the two pins the counter above zero forever. `OutOfMemoryException`-only; recorded by arm 1
   for completeness, not acted on.
4. **Symbol rebuilds the whole-layer counting sort per symbol style layer** (arm 1's N4) — a pure function of
   `ITileLayer.Geometry` that could be memoized beside the buffer, as the buffer itself now is. Real repeated
   work on a `landcover`-class layer named by several symbol layers; unchanged here.

**Leftover 5 (leave `WriteInto` taking selection and buffer as independent parameters) still stands** —
nothing on this branch creates a second `WriteInto` caller, and both arms concurred with leaving it.

### P2 — recorded findings (non-blocking; P3 inherits them)

**0. ⚠ MEASURED, NOT SUSPECTED: line's `TileToGeoJob` extent routing has NO observing tooth.** Found by
accident when a review agent's defect injection was left in the tree and the **entire 2286-test gate passed with
it live**. The injection was `StyledLineTileBuilder`'s subdivided-projection call:

```csharp
new TileToGeoJob { Tile = geometry.Tile, Extent = 4096.0, /* ← hardcoded */ … }
```

Every committed fixture is extent 4096, so substituting the literal is **inert across the whole corpus**. The
extent teeth that exist do not cover this call site:

| existing tooth | covers |
|---|---|
| `FillSharedBufferTests.PatternCoords_ComeFromTheBuffersOwnExtent_NotA4096Literal` | **fill** only |
| `TileGeometryMaterializerSeamTests.TileToGeo_UsesTheBuffersOwnExtent_…` | the **seam**, driving `LayerInput` directly |
| — | **line's own `TileToGeoJob` call — nothing** |

This upgrades B1/B2/B5's long-standing recorded finding ("*B2's `Extent`-routing inspection point is still not a
tooth — a literal `4096.0` in fill would still pass the whole gate, because every fill fixture is extent
4096*") from an argued limitation to a **demonstrated** one, now on a code path **P2 itself introduced** (line
previously received `extent` as a parameter; P2 made it read `geometry.Extent`).

**What closing it requires:** a non-4096 extent fixture — `PathGeometryMaterializer` makes one cheap, as B5's
findings already noted. The tooth must drive **line's** subdivided-projection path specifically; neither
existing tooth's fixture reaches it. **Production-configuration check:** the fixture's extent must differ from
4096, or the tooth is vacuous by construction — that is the whole reason this defect survived.

**Process note, recorded because it is the reusable part:** the injection was caught by re-verifying the
working-tree diff hash at commit time, **not** by the gate and **not** by the agent's own hygiene statement
(which was honest and hash-backed, but taken before the injection reappeared). A green gate is not evidence a
tree is clean.


Raised by both review arms of C1 P2 and deliberately **not** fixed in P2's fix round — they are P3's work,
listed so its developer inherits them rather than rediscovering them.

1. **⚠ The one that matters most: nothing observes that the production symbol path actually SHARES one buffer
   per source-layer.** Changing `TileSymbolLayerProcessor.cs:79` from `store` to `null` leaves **all 2286 tests
   green** while silently reverting P2's entire benefit for symbol — the fallback in `Extract` mints a private
   store, so the output is byte-identical. The *correctness* of the conversion is well observed; its *point* is
   not. P2's fix round closed the cheapest half (`TileSymbolWorkerPassTests` now asserts the runner hands every
   processor the same non-null store), which catches a null at the **runner**, not a processor that silently
   drops it. **P3's tooth C is the home for the other half — and its fixture must include a symbol-consumer arm
   and a source-layer named by TWO symbol layers**, not just the mesh fan-in, or tooth C repeats the pattern.
2. **The `selected.Count == 0` guard has no observing tooth.** It is output-neutral, so deleting it is
   invisible. Tooth C should carry an arm asserting **zero** materializations for a source-layer whose only
   naming style layer selects nothing.
3. **Tooth B must cover the *selection*/buffer pairing, not only *TileId*/buffer.** Nothing checks
   `max(selected.Ordinal) < geometry.FeatureCount`; correctness currently rests on the caller pairing the right
   layer with the right buffer. Making half the relation structural and leaving half by convention is exactly
   the defect class C1 exists to remove.
4. **Unmeasured: whole-layer vs selected-subset materialization cost** for restrictive line/symbol filters. The
   61× figure was fill's; the line/symbol analogue was never measured. Pairs with the plan's peak-memory item.
5. `TileGeometryStore` hardcodes `MvtGeometryMaterializer`, which throws for any non-`IMvtGeometryCarrier`
   feature; line and symbol now inherit fill's whole-layer exposure to that throw. Relevant when GeoJSON S2
   starts consuming `TileGeometryBuffers`.
6. **`TileGeometryBuffers`' own type doc over-generalises** ("*Getting this wrong fails **quietly***"). P2's fix
   round retracted the same claim at the three line/symbol borrow sites — measured false there: the store lends
   the **array-backed** buffer, whose `Dispose` frees three real `NativeArray`s, and R6 measured the double free
   as 32 failures across 19 fixtures (two with no line involvement — the heap-corruption signature). The quiet
   failure is real only for the list-backed `AsArray()`-view mode, which no consumer borrows. **P3 restates that
   paragraph per backing mode.**

## Resolved decisions

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

**Nothing is blocked without it.** GeoJSON can ship today via the same transcode-into-`uint[]` shim the
background quad already proves works in production. Globe/projection is already handled by Waist 2
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
