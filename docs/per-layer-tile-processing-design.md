# Per-layer tile processing — decode once, fan out per layer (Epic A)

**Status: COMPLETE (2026-07-15), on `feat/tile-pipeline-unification`.** The tile pipeline used to fork fill/line
vs symbol at the raw fetched bytes — decoding each tile twice. This epic collapsed that fork: decode once per
tile, fan out to a uniform per-layer processor, and raise the source boundary to a decoded-tile handle. This doc
is the design rationale + the landed record. Read after `docs/meshing-design.md` (fill/line build + the
`IRenderLayer`/`RenderLayerSet` model), `docs/labels-and-symbols-design.md` (the symbol path), and
`docs/tile-pipeline-design.md` (the TileManager decomposition + stalls). The geometry-IR follow-on is
`docs/tile-geometry-ir-design.md`.

## The itch

Fill, line, and symbol are all just **style layers over the same vector tile.** Yet the pipeline forked them at
the *earliest possible* seam — the moment raw MVT `byte[]` is fetched:

- Fill/line: `TileManager` decodes the tile and builds meshes (one worker task per tile, tile-atomic consume).
- Symbol: `SymbolLabelSubsystem` received the **same** fetched bytes via a `SymbolTileBytesReady` push and ran a
  parallel pipeline — **decoding the tile a second time.**

So the same protobuf parse ran twice per tile, and "symbol" was architecturally a different *kind of thing* from
"fill"/"line" when conceptually it is a sibling. Both decodes are off the main thread and decode was never a
profiled bottleneck — so this was a **cleanliness / extensibility** problem, not a perf fire.

**Why it ended up this way:** symbols were added after the mesh pipeline, which carries a lot of hard-won
stall/leak fixes and a delicate disposal/ownership contract. The lowest-risk way to add labels was to tap the one
clean seam (`byte[]` ready) and run a decoupled subsystem that "never touches the mesh/disposal pipeline." A
reasonable call that isolated a new feature from a fragile core — but it left the decode duplicated and the layer
model bifurcated.

## The precision that mattered: the fill/line side was already per-layer

The fill/line path is **not** a monolith blending all fill/line layers. It already iterated layers:

```
per tile:  decode MvtTile (once) ──► for each IRenderLayer in RenderLayerSet order:
                                          layer.WriteInto(its own pre-allocated MeshData)
```

Each `IRenderLayer` writes its **own** `MeshData`. What is *shared* per tile is three tile-scoped things — one
decode, one worker task, one tile-atomic consume — not the geometry work. The real outlier was symbol (forked
before the decode). So the rework was (1) hoist the decode to an explicit shared step and (2) make symbol a
processor in the **same** fan-out.

## The hard constraint: decode is tile-scoped, not layer-scoped

You cannot make each layer "trigger its own processing *from the bytes*." MVT layers are interleaved protobuf
messages — reading *any* layer means parsing the *whole* tile. Per-layer-from-bytes would re-parse the tile once
per layer (the symbol duplication, generalized to N). The natural shared root is the **decoded tile**, not the
bytes. Layers fan out *below* the decode:

```
fetch → decode tile (ONCE per tile; tile-scoped, unavoidable)
          └─ fan out per style layer, each a uniform processor:
               ├─ Fill    → MeshData   (worker)
               ├─ Line    → MeshData   (worker)
               └─ Symbol  → extract    (worker) → shape + atlas (main, budgeted)
```

This is the MapLibre / Mapbox GL **WorkerTile + Bucket** model: parse the tile once, then each layer produces its
bucket. Converging on the proven architecture, not inventing one.

## The uniform per-layer processor

Every layer kind implements one contract — this is what makes symbol "just a layer":

```csharp
enum LayerPhase { WorkerOnly, WorkerThenMain }   // does this processor need a main-thread tail?

interface ITileLayerProcessor {
    LayerPhase Phase { get; }
    // Produce this layer's artifact for one tile from the shared decoded tile + tile context.
    LayerArtifact Process(in TileContext ctx, IDecodedTile tile);   // mesh payload OR labels OR icons…
}
```

Fill/line/symbol are three implementations differing only in `Phase`, artifact type, and sink. A tile-scoped
coordinator owns the decoded tile, fans out grouped by `Phase` (all `WorkerOnly` work in the worker pass; the
`WorkerThenMain` tails after the hop to main), and routes each artifact to that kind's sink — preserving the
anti-spike batching (the main-thread `MeshData` allocate/apply and the atlas upload stay batched at the phase
boundary, not scattered per layer).

### What stays pluggable (does NOT unify)

Unification stops at **decode + dispatch.** These downstream realities legitimately differ and remain
per-processor policy:

| Shared reality | Why it resists per-layer independence | Where it lives |
|---|---|---|
| **Decode** | tile-scoped parse | the shared root above the fan-out (the whole point) |
| **Glyph atlas** | ONE fixed atlas across all symbol layers *and* tiles (growth-staleness hazard) | an injected shared resource symbol processors coordinate through — never per-layer |
| **Main-thread tails** | `MeshData` allocate/apply + atlas upload are main-thread only | `Phase` groups them; the coordinator batches the main-thread hop |
| **Budgets** | symbol shaping is bursty; mesh build has its own cadence | per-processor budget knob, not one global rate |
| **Lifecycle** | mesh = dispose-once + Model B prepared cache; labels = keep-warm store | sinks stay separate; only decode + dispatch unify |
| **Draw / paint order** | fills under lines under symbols | already handled by `RenderLayerSet` ordering at draw time |

The rule: **"each layer is a first-class entity" ✓; "each layer fully independent from raw bytes" ✗** — the
tile-scoped decode and the cross-layer shared atlas forbid the second.

## The source-driven axis (background + generic sources)

The uniform-processor model is vector/MVT-centric. Generalize the layer→data relationship:

- A layer **declares its source** (`StyleLayer.Source`) — or **null.** The coordinator gathers the sources its
  layers need, **fetches/decodes each once** (shared across all layers of that source), then triggers each
  layer's prepare with **its source's decoded data.**
- **Null-source layers (background) prepare immediately** with null data — a full-tile-extent quad per covered
  tile, no fetch/decode. This is the clean resolution of the background problem: background stops being a bespoke
  world-quad and becomes a **source-less `TileMesh` processor.**
- **Generic data source:** abstract the source so MVT / GeoJSON / raster are peers. `IDataSource` was a per-tile
  *byte* fetcher (HTTP tile pyramids); GeoJSON is a whole dataset loaded once and sliced client-side (geojson-vt).
  The target seam is `ITileFeatureSource.GetTile(TileId) → IDecodedTileHandle` (a lazy decode-once handle):
  MVT = fetch-bytes + decode-protobuf; GeoJSON = load-once + slice; raster = fetch-bytes + a texture artifact.
  Source output is polymorphic by kind (vector→features, raster→texture).

Because each layer's per-tile prepare projects through the same `IProjection` as fill/line, two things fall out
for free: background is **projection-correct** (flat on Mercator, curved on the sphere — no Mercator-only gate),
and background/raster go through the **active `ITileRenderBackend`** (Entities/BRG/GameObjects), not bespoke
GameObjects. Only **symbols** stay `FramePlaced`/GameObject (genuinely screen-space — the one non-tile kind).

### What falls out — Epic A's payload

| Previously framed as | Under Epic A |
|---|---|
| "redo background per-tile" | a null-source `TileMesh` processor (replaces the interim world-quad) |
| **Raster** | a raster-source processor (non-MVT source, texture artifact) |
| GeoJSON support | a second `ITileFeatureSource` impl behind the decoded-tile seam |
| **Fill-extrusion** | a `TileMesh` processor + ZWrite — one class + one arm |
| symbol double-decode | symbol fed by the shared decode |

## What it buys vs what it costs

**Buys** — kills the duplicate per-tile decode; symbol becomes a first-class layer processor (one conceptual
model, not two hand-written pipelines); one scheduling/lifecycle *framework* with per-processor policy; new layer
kinds (icons, heatmap, fill-extrusion, circle) slot in as processors, not new subsystems.

**Costs** — it opens the **most delicate seam in the codebase** (the mesh build/consume/disposal path) to insert
the coordinator; the payoff is mostly architectural (the duplicate decode is off-main and on no profile). A
structural-debt paydown, not a perf win.

---

## Epic A — landed stages

Each stage was independently green (full EditMode gate + byte-identical `Visual/` snapshots), dual-reviewed, one
revertible commit. Hard fences respected until each stage's own scope lifted one: the symbol decode/build path,
the mesh consume/dispose Model-B contract, and the `IDataSource`/`MvtTile` generalization.

- **A1 — processor contract + fill/line fan-out.** `ITileLayerProcessor` (worker invocation over a shared decoded
  tile) + `ITileMeshLayerProcessor` (the mesh-settlement capability, `Complete()` → `IRenderLayerPayload`) +
  `TileLayerProcessorRunner.RunWorkerPass` (decode once, dense-order invoke, settle every processor exactly once)
  + the `TileMeshLayerProcessor` adapter around `ITileMeshRenderLayer.WriteInto`, all under
  `Rendering/Tile/Processing/`. `TileManager.KickMeshBuild` now allocates one processor per this-source layer at
  kick and calls the runner once — zero direct `MvtDecoder.Decode`/`.WriteInto` calls (structural tooth). Fault
  policy byte-for-byte preserved: a decode fault or processor exception aborts the remaining invocations but
  every processor still settles exactly once (per-processor guarded), so no kick-allocated `MeshDataArray` is
  stranded.
- **A2 — source-less background processor.** `TileBackgroundLayerProcessor` (reuses
  `StyledFillTileBuilder.WriteMeshData` over a synthetic full-tile-extent feature — earcut winding + globe
  subdivision for free) + `RunSourcelessWorkerPass` (decode-free sibling) + a dedicated source-less
  `SourcePipeline` slot. Background is now a per-covered-tile `TileMesh` layer registered with the active
  backend, projected through the same `IProjection` — the interim world-cap quad and its Mercator-only gate are
  retired; `RenderLayerBuild.ViewGeometry` is removed (the axis collapses to `{ TileMesh, FramePlaced }`).
  `MapMaterialSet.Validate()` makes an unconfigured base material fail loud; `MapView.SetStyle` is transactional
  across its one `await BuildSourceSpecs` (closing the blank-background/destroyed-material windows). (Whole-restyle
  transactional atomicity — a mid-commit-throw rollback — remains a pre-existing gap shared by fill/line, filed
  to `docs/smooth-transitions-design.md`. Full-sphere polar background is on the globe track.)
- **A3 — symbol as the first `WorkerThenMain` processor** (still self-decoding). `TileSymbolLayerProcessor` (one
  per symbol style layer per (source, tile) build; worker step = `StyledSymbolTileBuilder.ExtractLayers`, main
  tail = `ShapeAsync`) + `RunSymbolWorkerPass` (the symbol cadence's own decode-once worker pass — faults
  propagate rather than settle, since symbol holds only managed state). `SymbolLabelSubsystem.BuildTileAsync`
  becomes the per-tile coordinator: one `SwitchToThreadPool`/`SwitchToMainThread(ct)` hop batches every layer's
  tail. Zero `TileManager` edits. Two self-decoding cadences persist by design until A4. Non-vacuity of the
  differential parity test (`SymbolProcessorParityTests`) verified by injecting each falsifier (reversed order,
  skipped tail, ordinal-vs-global material index, tile-vs-camera zoom).
- **A4 — feed symbol from the shared decode; delete the second decode.** `SharedTileDecode` (new,
  `Processing/SharedTileDecode.cs`) — `{readonly byte[], MvtTile, cached fault, gate}` and exactly ONE operation,
  `GetOrDecode()`. **No refcount, no lifetime protocol** — retention is plain GC reachability: `MvtTile` is pure
  managed data (no native memory), reachable only from its in-flight holders (the *exact same* paths the raw
  `byte[]` dies on today, one edge deeper). `TileManager` mints one entry per fetched (source, tile), stores it in
  `LoadedTile.ReadyDecode`, and pushes it through `SymbolTileBytesReady`; `KickMeshBuild` and the symbol pass each
  read through `GetOrDecode()`. Lazy + idempotent: whichever cadence arrives first decodes under the entry's lock
  (also the safe-publication barrier for the effectively-immutable post-decode `MvtTile` across pool threads — the
  one genuinely new cross-thread hazard, verified zero production write sites outside `MvtDecoder`); a decode
  fault is cached and rethrown to every caller. *Accepted tradeoff:* the decoded tile now lives as long as its
  last holder (a lingering kick task, a queued symbol build) — bounded, transient, deliberately unmeasured.
- **A5 — retire the parallel push; one kick drives both.** A5a split `BuildTileAsync` into a worker phase +
  a budgeted tail pump (`RunTailAsync`/`_readyTails`, the tail machinery entirely inside `SymbolLabelSubsystem`).
  A5b swapped the feed: `TileManager`'s per-tile kick now drives the symbol worker pass alongside the mesh pass,
  sharing the A4 decode, through one symbol-agnostic seam — `ISymbolTileWorkerFactory.TryBeginBuild(sourceId,
  tile)` (MAIN, kick time) returning `ISymbolTileWorkerPass.RunWorkerAndHandoff(SharedTileDecode)` (POOL, inside
  the same kick task, after the mesh pass → enqueues a `ReadySymbolTail` onto a `ConcurrentQueue`). `TileManager`
  holds only `ISymbolTileWorkerFactory` — it never references a label/store/glyph type. The parallel push
  (`SymbolTileBytesReady`/`OnTileBytesReady`/`_buildQueue`) is retired. **Two fault domains, one kick task:** the
  mesh call settles first (mesh never throws — settles-on-fault internally); the symbol call is wrapped in an
  outer `try/catch` — without it a contract-violating symbol throw makes `ConsumeMeshBuild`'s own faulted-task
  guard settle the tile via a SILENT-DISCARD path that throws away the built fill geometry and never disposes the
  kick-allocated `MeshDataArray` (a native-memory leak, RED-verified via `MeshDataPayload.DebugLiveAllocCount`).
  **Budget split:** `MaxMeshBuildsPerTick` now paces both mesh and symbol worker starts; `MaxBuildsPerFrame`
  becomes purely the tail-start budget. *Accepted, user-signed-off:* under a burst, the Kth tile's shaping starts
  ~K/`MaxBuildsPerFrame` pumps later (a backlog-drain bound) — label **content** is untouched, only appearance
  timing.
- **A6 — decode + feature generalization.** The two MVT-coupled seams neutralized: `ITileDecoder.Decode(bytes) →
  IDecodedTile`, resolved by `TileEncoding` via `TileDecoders.ForEncoding` (`MvtTileDecoder` the sole production
  `MvtDecoder.Decode` call site); and `WriteInto(IReadOnlyList<ITileFeature>)` — the fill/line/symbol fan-out
  references only the neutral `IDecodedTile`/`ITileLayer`/`ITileFeature` surface. Carrier types neutralized
  zero-copy (the MVT types implement the neutral interfaces directly). `MvtFeatureAdapter` retired, folded into
  `MvtFeature` verbatim (`ITileFeature : IFeature`). **Decision X — two decode layers, only one generalizes
  here:** *tile* decode (`bytes → tile structure`) is neutralized; *geometry-command* decode (`uint[] → rings`,
  `MvtGeometry.Decode`/`MvtDecodeJob`) is deliberately kept — the `uint[]` tile-space command stream stays the
  neutral feature's geometry payload. Re-plumbing fill to a `loadGeometry()→rings` face would touch the most
  delicate perf-sensitive code in the repo — deferred, and later superseded by the canonical-IR direction
  (`docs/tile-geometry-ir-design.md`). First Core diff in the epic (`dotnet test Tools/core-tests` live from here).
- **A6.1 — `TileGeometryType` reframe.** `MvtGeometryType` (values already format-neutral; only the name/`Core.Mvt`
  home leaked MVT) → `TileGeometryType` in `Core.Tiles`; `ITileFeature`/`IDecodedTile` doc reframed as the honest
  vector-feature / polymorphic-by-kind abstractions. Behaviour-identical (values unchanged → zero snapshot risk).
- **A7 — raise the source interface to the decoded-tile level.** `ITileFeatureSource.GetTile(TileId) →
  IDecodedTileHandle` (a LAZY `{ IDecodedTile GetOrDecode(); }` handle that `SharedTileDecode` now implements —
  decode stays lazy + shared, preserving the A4/A5b decode-once invariant). `MvtTileFeatureSource` wraps the
  unchanged `TileScheduler`/`TileCache`/`IDataSource` byte-fetch and mints the handle inside `GetTile` (byte-fetch
  relocated verbatim, no fetch/cache/cancel behaviour change). `TileManager` is now **byte-agnostic** — it names
  none of `TileResponse`/`IDataSource`/`TileScheduler`/`SharedTileDecode`/standalone `TileCache`;
  `SourcePipeline` collapsed to `{ ITileFeatureSource FeatureSource }` and `MapView.BuildSourceSpecs` is the one
  production site naming `MvtTileFeatureSource`. A non-byte source (GeoJSON) returns a **lazy handle too** —
  ~~an EAGER handle wrapping a pre-sliced `IDecodedTile`~~, which is what this said until GeoJSON S2 and which
  **leaks under the IR C1 P3 lease**: a decoded tile owns `Allocator.Persistent` memory that only the last
  scope close frees, and `TileManager.Tick` calls `GetTile` for every cover tile while only a subset ever
  opens a scope. `GeoJsonTileFeatureSource` therefore mints the same `SharedTileDecode`, over a decoder that
  carries a projected dataset instead of bytes, and slices inside `GetOrDecode()`. Same interface, no
  coordinator change — and the slice lands off-main for free, inside the kick's pool lambda.

  **One MVT-reachable behaviour did move, and the record should not say otherwise.** GeoJSON S2 was planned as
  "only the empty-`source-layer` guard and the resolver dispatch touch a vector path". That is not quite true:
  `StyleParser` now reads `data` for **every** source type, and `TileManager.SourceKey` includes both that
  field and `type`, so a **vector** source carrying a `data` key produces a different key than it did before
  S2. It is inert in practice — no such source exists here, and the only consequence is one pipeline rebuild
  on the first restyle after the change — but the correct statement is "the key's domain widened for all
  source types", not "nothing vector-reachable moved".

**Canonical-IR direction (adopted north star, spun out).** Decision X's `uint[]`-kept deferral is a sound *scope
fence*, not a permanent architecture: the `uint[]` is MVT's wire geometry encoding, so the "neutral" feature is
neutral only for MVT. The settled direction — expose the tile-local decode buffer `MvtDecodeJob` already produces
as a named `TileGeometryBuffers` and make Stage 1 pluggable by encoding, plus the geodetic Waist 2 that already
exists — is a **two-waist** model. It is a distinct, GeoJSON/A8-triggered effort with its own stage sequence and
open decisions, so it lives in its own SSOT: `docs/tile-geometry-ir-design.md`. **Not part of Epic A; deferred.**

## Tracked follow-up (needs its own stage) — resolve the zoom dual-meaning

`TileLayerProcessContext.Zoom` currently carries two meanings by cadence: the mesh passes bake at the tile's
INTEGER zoom, while the symbol pass evaluates style expressions (text-size etc.) at the **camera** zoom captured
at build start (`SymbolLabelSubsystem.cs:279`, into the context at `:293`). **Direction (for now): change the
symbol pass to use the tile's integer zoom** — aligning it with the mesh and making a tile's labels
deterministic, cacheable, and camera-independent at build time. Scope: only the style-expression zoom — NOT the
`WebMercator.GroundResolution(cameraZoom)` cross-tile quantization (that belongs to
`docs/labels-and-symbols-design.md` §4). It IS a visual behaviour change — `SymbolProcessorParityTests` + label
snapshots shift and must be **intentionally re-baked.** "For now" flags that quantized-to-integer-zoom text
sizing diverges from continuous-zoom interpolation and may be revisited (see
`docs/smooth-transitions-design.md` §2). Not built in Epic A.

## S2 open findings — recorded, not fixed

Carried out of the GeoJSON source stage (S2a `cd9dd227` / S2b `75ee9635`) by two independent review arms.
None blocks the stage; each is here so it is not rediscovered from scratch.

- ~~**`GeoJsonSliceOptions.ValidateExtent` does not cover `SimplifyTolerance`.**~~ **CLOSED by D2
  (2026-08-09).** The carve-out existed because the slice-time fault a non-zero tolerance produced was the
  instrument `T5_GetTile_DoesNotSlice_…` used to observe laziness; D1 retired that tooth (the source slices
  eagerly now; `T5_GetTile_SlicesOffTheMainThread` replaced it), which left the carve-out serving nothing.
  D2 renamed `ValidateExtent` → `Validate` and added the `SimplifyTolerance != 0` arm, so the whole option
  set is now checked where the options are ACCEPTED — one validator, called from
  `GeoJsonTileFeatureSource`'s constructor and from `GeoJsonTileSlicer.Slice`. The construction boundary holds
  no copy of the predicate; it calls the validator. `Slice` keeps its own tolerance guard, which is a **kept
  duplicate** — `Slice` calls `Validate()` unconditionally three lines later, so the guard covers no input the
  validator would miss. It survives for the exception *type* alone: running first is what keeps a direct
  `Slice` caller's fault a `NotSupportedException`
  (`T9_NonZeroSimplifyTolerance_ThrowsNotSupported`) rather than the validator's `ArgumentOutOfRangeException`.
  Unifying the two types is a behaviour change on a public API and was deliberately not D2's.
  Teeth: `GeoJsonTileSlicerTests.T9b_ANonZeroSimplifyTolerance_IsRejectedByTheValidator` (the validator
  carries the predicate) and `GeoJsonSourceTests.TheSource_RejectsUnusableOptions_AtConstruction`'s tolerance
  arm (the construction boundary rejects through it).

- ~~**`ITileFeatureSource.GetTile`'s `ct` is threaded by no production caller.**~~ **CLOSED by D1 (2026-08-09)
  — decided, not deferred: the parameter is CONTRACT-ONLY, and the interface now says so.** The alternative
  this finding offered (thread the cover pass's token) was weighed and rejected: it needs a per-record
  `CancellationTokenSource` in `TileManager` — a new per-tile lifetime object with its own disposal rules —
  and it *creates a new leak path*, since a decode cancelled after the decoder allocated but before the task
  result is observed would need its own release site. That is the exact hazard D1 exists to close, added back
  for no observed benefit. The cancellation that matters (abort the in-flight HTTP request) already flows
  through `FeatureSource.Release(id)` → `TileScheduler`'s per-tile CTS. `ITileFeatureSource.GetTile`'s XML
  records the parameter as contract-only with no production caller, which is what keeps a future reader from
  concluding "dead guard, delete".

- ~~**`GeoJsonTileFeatureSource.GetTile` throws cancellation synchronously while the MVT one faults its
  task.**~~ **CLOSED by D1 (2026-08-09), as a side effect.** Making the GeoJSON source eager made it `async`,
  so its `ThrowIfCancellationRequested` now surfaces as a faulted/cancelled `UniTask` exactly as the MVT
  source's does. The seam no longer leaves it open either: `GetTile`'s XML states that failures are reported
  through the returned `UniTask`, never thrown synchronously — true of both implementations.
  `GeoJsonSourceTests.GetTile_ObservesCancellation` is the observing tooth and awaits rather than
  `GetAwaiter().GetResult()`-ing (a UniTask that has not completed does not block there).

- **`T4_ASlicedGeoJsonLayer_ListsOnlyTheSurvivingFeatures_AndItsOrdinalsAddressTheBuffer` has two arms that
  cannot fail.** The count arm is pinned by `TileLayerGeometryAdoption.Validate`, which throws during
  `Decode` before any assertion runs; the ordinal-range arm is tautological because `FeatureSelector`
  assigns `Ordinal = i` over the very list the guard sized the column against. **This was measured, not
  argued** — injecting the plan's named defect (`Features` from the dataset, `Geometry` from the slice)
  fails in the guard. Both arms are labelled as such in the test, and the guard's own RED lives in
  `WaistOneProducerAgreementTests`. The test's *live* arm — the surviving features are the dataset's
  non-prefix subset, by name — does fail on the prefix-shaped variant that passes the guard, which is why
  the test was kept rather than retired.

- **Five of `JsonCanonical.WriteString`'s eight escape arms are uncovered.** Not a comparability hole: none
  of the five can forge a collision, because an unescaped control character still cannot make two distinct
  DOM values agree. Documented in the test rather than papered over with five more arms.

## Eager decode: peak resident decoded-tile memory (D1, 2026-08-09) — UNCAPPED by decision

**MAINTAINER DECISION: ship arm A, uncapped, and record the number.** This section is that record. It exists
because the number is a real cost and because the mitigation that actually works is not the obvious one.

### The bound, stated plainly

**A tile is RESIDENT for as long as any reference to its lease is live** — that is the definition, and it is
wider than any single stage. Three populations contribute: *fetched-but-not-yet-kicked* records,
*kicked-but-still-running* builds, and *parked* symbol builds holding an `Acquire()` token across the
sprite-settle window.

**The newly unbounded contribution is the first one, and it is unbounded.** The other two were there before
D1 and are paced (kicks by `MaxMeshBuildsPerTick`, parks by the sprite fetch), though note that neither is
*hard*-capped: there is no cap on how many kicks may be in flight at once, only on how many start per tick.

Before D1 a tile decoded when its kick lambda ran and was freed when that lambda's scope closed, so
residency self-limited to single digits (`MaxMeshBuildsPerTick = 2` × task duration ÷ tick duration). Under
the eager decode a tile is resident from **fetch completion**, and nothing paces fetch completion:

- **`TileScheduler` has no concurrency cap** — `Core/Data/TileScheduler.cs` holds dictionaries only: no
  semaphore, no queue. Every cover tile's `Request` starts immediately.
- **`TileCache` (LRU 256) means a pan-back or a restyle resolves the whole cover synchronously from bytes**,
  so every one of those tiles dispatches its decode on the same tick.
- **Kicks drain at `MaxMeshBuildsPerTick = 2`** (`MapViewConfig.cs:46`), so the backlog leaves slowly.

This sits in tension with the repo's `always-bound-loops` principle. That was raised and the call was made
deliberately: measure first, and the remedy stays cheap.

### The estimate and its derivation

| quantity | value | source |
|---|---|---|
| decoded tile, median | 552 KB | measured 2026-08-09, `docs/tile-geometry-ir-design.md` |
| decoded tile, peak | 967 KB | same |
| tilted cover | 40–60 tiles | the shipped selector's tilted-frustum cover |
| **peak resident** | **~20–60 MB** | median–peak × cover |
| drain time | ~20–30 ticks | 40–60 tiles ÷ `MaxMeshBuildsPerTick = 2` |

Today's equivalent scenario is ~1–2 MB, so this is a **~20× transient peak**, plus a burst of 40–60
concurrent `Decode` calls on one tick. It is transient and self-draining; nobody has measured it on a real
device.

### The remedy, if a device profile ever shows it: **arm C**

**Dispatch the decode at kick-admission rather than at fetch-completion** — roughly ten lines, confined to
`PumpPending`'s fetch-observe block plus `TileDecodeDispatch`. It **removes the fetched-but-not-kicked
contribution entirely**, because a tile is only decoded once something is about to consume it — which
restores the pre-D1 practical profile. It is deliberately **not** described as a hard residency bound: it
does not add one. Kicks can still overlap without limit (there is a per-tick start cap, not a concurrency
cap — see `docs/tile-geometry-ir-design.md`), and a parked build's reference can outlive the kick that
created it, so residency after arm C is bounded only by the same self-limiting behaviour the renderer had
before this stage. A genuine cap would need a concurrent-kick limit, which is a separate change. Arm C keeps
every prize D1 bought — *exactly one decode ever*, *no parked re-decode*, *no decode without an owner* — and
gives back only "as early as possible". It is written down here so nobody has to re-derive it under
pressure.

**Arm B — a semaphore inside `TileDecodeDispatch` — is NOT the fix, and must not be described as one.** It
bounds the CPU burst and the thread-pool saturation, which is a different problem. A decoded tile stays
resident until it is kicked regardless of how its decode was paced, so arm B leaves residency exactly where
arm A does.

## D1 recorded limitations — two things no tooth can observe

Both surfaced in D1's review and were decided deliberately. They are here because
`recorded-limitation-needs-an-observing-tooth` asks "which test goes RED if this stops being deliberate?" —
and for these two the honest answer is **none can**, which is exactly why prose has to carry it.

**1. A throwing `Release()` during teardown aborts the loop, leaving later records untorn.**
`RenderTeardownRecord` now nulls the record's handle before releasing it, so the funnel itself cannot retry a
handle it has already released. The residue is in the three callers (`SetSources`, `ReleaseTile`,
`DoDispose`): each iterates records, and a throw from one record's release abandons the rest of the loop.

*Why no tooth exists.* All three callers pass a **struct copy**, so `lt.Decode = null` is discarded even on
the happy path — there is no production-observable difference between the two orderings. Reflecting into the
method cannot see it either: `MethodInfo.Invoke` does not copy a by-ref argument back when the callee throws
(measured, not assumed). The ordering is therefore pinned **structurally** by
`TileProcessingStructureTests.RenderTeardownRecord_NullsTheRecordsHandleBeforeItReleases`, RED-verifiable by
swapping the two statements, and nothing observes the caller-side residue.

*Why it was left.* After D1's transfer fix there is no known route to a throwing `Release()` — it needs a
decoder whose `IDecodedTile.Dispose()` throws, or a future over-release. This is hardening, not a live bug.
The two alternatives were rejected with reasons worth keeping: a per-record `try/finally` in all three loops
changes teardown from **fail-fast to best-effort**, a real semantic change that needs its own tooth and did
not belong in a fix pass; and making the funnel swallow-and-log a release fault would retire the deliberate
unbalanced-release diagnostic at the exact site where it is most informative.

**2. A thread pool that accepts a work item and never runs it strands a parked reference.**
The dequeue→worker-start guard relinquishes ownership once `UniTask.RunOnThreadPool` has returned, so a pool
that accepts and never dispatches is undetectable from that frame. This is the **same residual the kick lambda
has carried since A1** — it is not new to the parked path. Closing it needs a completion watchdog rather than
an ownership guard, and no test can produce the condition.

## Deliberately left filed (out of scope)

Whole-restyle transactional atomicity → the first piece of `docs/smooth-transitions-design.md` §1; full-sphere
polar background → the globe track (`docs/projection-globe-track-design.md`); per-tile background overdraw (N
quads, one per covered tile — unprofiled, a bottom-slot-opaque→camera-clear optimization); the dead-but-tested
mesh-entry `WorkerThenMain` guards (runner widening permanently rejected — kept as typed-contract defense for a
hypothetical dual-capability implementor); A8+ payloads (raster texture / GeoJSON slice / fill-extrusion) and,
triggered by GeoJSON, the geometry-IR epic.
