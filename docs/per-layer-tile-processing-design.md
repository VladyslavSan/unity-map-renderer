# Per-layer tile processing — decode once, fan out per layer

A tile is decoded once per (source, tile), and every style layer over it — fill, line, symbol — is a uniform
per-layer processor fed by that one decode. The source boundary is a decoded-tile handle, not bytes. Read with
`docs/meshing-design.md` (fill/line build and the `IRenderLayer`/`RenderLayerSet` model),
`docs/labels-and-symbols-design.md` (the symbol path), `docs/tile-pipeline-design.md` (the `TileManager`
decomposition), and `docs/tile-geometry-ir-design.md` (the tile-local geometry a decode produces).

## The shared root is the decoded tile, not the bytes

Fill, line, and symbol are all **style layers over the same vector tile**, so they are siblings in one
fan-out rather than separate pipelines. The fan-out cannot start at the bytes: MVT layers are interleaved
protobuf messages, so reading *any* layer means parsing the *whole* tile, and a per-layer-from-bytes design
re-parses the tile once per layer. The shared root is the **decoded tile**, and layers fan out *below* it:

```
fetch → decode tile (ONCE per (source, tile); tile-scoped, unavoidable)
          └─ fan out per style layer, each a uniform processor:
               ├─ Fill            → graph build input  (worker)
               ├─ Line            → graph build input  (worker)
               ├─ Fill-extrusion  → graph build input  (worker)
               └─ Symbol          → extract (worker) → shape + atlas (main, budgeted)
```

What is shared per tile is tile-scoped — one decode, one kick task, one tile-atomic consume — never the
per-layer geometry work.

## The uniform per-layer processor

Every layer kind implements one contract — this is what makes symbol "just a layer":

```csharp
internal enum LayerPhase { WorkerOnly, WorkerThenMain }   // does this processor need a main-thread tail?

internal interface ITileLayerProcessor
{
    LayerPhase Phase { get; }
    void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context);
}
```

- **The input is an already-decoded tile.** A conforming processor cannot decide to decode the bytes for
  itself. It **borrows** `tileLayer.Geometry`: it never disposes, mutates or retains it past the call.
- **The signature is pinned at two parameters** (`NeutralGeometryPathTests`): the geometry belongs to the
  layer, so a separate sidecar parameter could be paired with the wrong layer.
- **Two capabilities extend the base without touching each other:** `ITileMeshLayerProcessor` (mesh
  settlement) and `ITileWorkerThenMainLayerProcessor` (a main-thread tail, `CompleteOnMain`; the symbol sink
  stays the coordinator's own store).
- **Two runner entries, one per cadence.** `TileLayerProcessorRunner` is stateless — no decoded-tile cache, no
  refcount; retention lives in the caller-owned handle. `RunWorkerPass` (mesh) invokes only `WorkerOnly`
  processors and **rejects** `WorkerThenMain`, because the mesh pass has no main-thread tail to give it; a
  mesh processor requesting one is a programming error, not a phase to downgrade. `RunSymbolWorkerPass` invokes
  only `WorkerThenMain` processors, and the coordinator batches their tails after the hop to main.
- **Mesh fault policy: every processor settles once.** A processor exception aborts the remaining invocations
  of the pass, but every processor — invoked or not — is still released, once, in dense order, so nothing a kick
  allocated is stranded. A cancelled lifetime token skips the processing block and falls through to the same
  settle loop.
- **Symbol fault policy: faults propagate.** The symbol pass holds only managed state, so there is nothing to
  strand; swallowing a fault would let a tail commit an empty or partial extraction.

The anti-spike batching stays: the main-thread mesh allocate/apply and the atlas upload are batched at the
phase boundary, not scattered per layer.

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
| **Draw / paint order** | style-declared order across all kinds | the one global draw index (`RenderLayerSet`) |

The rule: **"each layer is a first-class entity" ✓; "each layer fully independent from raw bytes" ✗** — the
tile-scoped decode and the cross-layer shared atlas forbid the second.

## The source-driven axis

- A layer **declares its source** (`StyleLayer.Source`) — or **null.** Each source is fetched and decoded
  once, shared across every layer of that source, and each layer processes **its source's decoded tile**.
- **A null-source layer (background) needs no fetch or decode.** `BackgroundQuad` synthesizes a
  full-tile-extent quad per covered tile, and `TileManager.KickSourcelessBackground` schedules it straight into
  the build graph with no worker pass. Background is a **source-less `TileMesh` layer**, not a bespoke world quad.
- **Sources are peers behind one seam.** `ITileFeatureSource.GetTile(TileId)` returns a
  `SharedDisposable<IDecodedTile>`. MVT = fetch bytes + decode protobuf; GeoJSON = load the dataset once +
  slice client-side; a raster source (not built) would be fetch bytes + a texture artifact. Source output is
  polymorphic by kind (vector → features, raster → texture).

Because every per-tile layer projects through the same `IProjection` as fill/line, background is
**projection-correct** (flat on Mercator, curved on the sphere), and tile layers go through the **active
`ITileRenderBackend`** (Entities/BRG/GameObjects). Only **symbols** stay `FramePlaced` — genuinely
screen-space, the one non-tile kind. A new layer kind (heatmap, circle, raster) is a processor, not a new
subsystem; a new source kind is a new `ITileFeatureSource`.

## Decode and the source seam

- **One mint site.** Each `ITileFeatureSource` calls `TileDecodeDispatch.DecodeAsync` from inside its
  `GetTile` task, so "decode at fetch completion, once, under the caller's `IWorkScheduler` policy" is a
  property of one helper, not of each source remembering it. The decoder is resolved by encoding
  (`ITileDecoder`, `Decoders.ForEncoding`); `MvtTileDecoder` is the sole production `MvtDecoder.Decode` call
  site. A decoder throw becomes a `TileDecodeException` and faults the task, and nothing is minted.
- **The GeoJSON source uses the same path.** Its decoder carries a projected dataset instead of bytes and
  slices inside the decode. Same interface, no coordinator change.
- **`TileManager` is byte-agnostic.** It names no byte-fetch type (`IDataSource`, `TileScheduler`,
  `TileCache`); `MvtTileFeatureSource` wraps the byte fetch, and `MapView` is the one production site that
  names it.
- **The fan-out reads only the neutral surface** — `IDecodedTile`/`ITileLayer`/`ITileFeature`, and each
  layer's tile-local geometry (`docs/tile-geometry-ir-design.md`). The MVT types implement the neutral
  interfaces directly, with no copy.
- **Reference ownership.** The `TileManager` record holds the creator's reference from fetch observation until
  `RenderTeardownRecord` releases it. A mesh kick takes its own reference; a parked symbol build takes its own
  via `Acquire`. `ITileFeatureSource.GetTile`'s XML names every release site.
- **Failures arrive through the returned task, never thrown synchronously** — true of both sources, so the
  coordinator observes every outcome in one place.
- **`GetTile`'s `ct` is contract-only.** No production caller threads a token; aborting an in-flight request
  flows through `ITileFeatureSource.Release` into the scheduler's per-tile token instead. Threading the cover
  pass's token was rejected: it needs a per-record `CancellationTokenSource` in `TileManager` — a new per-tile
  lifetime object with its own disposal rules — and it creates a new leak path, because a decode cancelled
  after the decoder allocated but before the result is observed would need its own release site. An
  implementation must still honour the parameter.
- **`TileManager.SourceKey` includes `data` and `type` for every source type**, because `StyleParser` reads
  `data` for every source type. A vector source carrying a `data` key therefore keys differently from one
  without it.
- **GeoJSON slice options are checked where they are accepted.** `GeoJsonSliceOptions.Validate` is the one
  validator, called from `GeoJsonTileFeatureSource`'s constructor and from `GeoJsonTileSlicer.Slice`. `Slice`
  also keeps its own non-zero-`SimplifyTolerance` guard, run first, only for the exception type: a direct
  `Slice` caller gets `NotSupportedException` rather than the validator's `ArgumentOutOfRangeException`.
  Unifying the two types is a behaviour change on a public API.

## One kick drives mesh and symbol

`TileManager`'s per-tile kick drives the symbol worker pass alongside the mesh pass, sharing the one decode,
through one symbol-agnostic seam. `ISymbolTileWorkerFactory.TryBeginBuild(sourceId, tile)` runs on MAIN at
kick time and returns an `ISymbolTileWorkerPass`; its `RunWorkerAndHandoff(decode)` runs on the POOL inside
the same kick task, after the mesh pass, and enqueues a ready tail for the symbol subsystem's budgeted tail
pump. `TileManager` holds only `ISymbolTileWorkerFactory` — it never references a label, store, or glyph type.

- **Two fault domains, one kick task.** The mesh pass runs first and settles its processors on fault
  internally. The symbol call is wrapped in its own `try/catch`: without it, a contract-violating symbol throw
  faults the kick task, and the consume side's faulted-task guard settles the tile through a path that
  discards the built fill geometry and strands its native mesh memory.
- **Budget split.** `MaxMeshBuildsPerTick` paces both mesh and symbol worker starts; `SymbolSubsystem.
  MaxBuildsPerFrame` paces only tail starts. Under a burst, the Kth tile's shaping starts ~K/`MaxBuildsPerFrame`
  pumps later. That bounds when labels appear, never what they contain — an accepted trade.

## Open: the zoom dual meaning

`TileLayerProcessContext.Zoom` carries two meanings by cadence: the mesh pass bakes at the tile's INTEGER zoom,
while the symbol pass evaluates style expressions (text-size etc.) at the fractional **camera** zoom captured at
build start. The shared decode carries no zoom, so sharing it cannot conflate the two; reconciling them belongs
to whatever merges the cadences. **The recorded direction** is to move the symbol pass to the tile's integer
zoom, which makes a tile's labels deterministic, cacheable, and camera-independent at build time. Scope: only
the style-expression zoom — NOT the cross-tile symbol quantization (`docs/labels-and-symbols-design.md`). It is
a visual behaviour change, and quantized text sizing diverges from continuous-zoom interpolation
(`docs/smooth-transitions-design.md` § "Camera-driven property re-evaluation"), so the direction may be
revisited.

## Eager decode: peak resident decoded-tile memory

**Decision: decode at fetch completion, uncapped, and record the cost.** The cost is real, and the mitigation
that works is not the obvious one.

### The bound, stated plainly

**A tile is RESIDENT for as long as any reference to it is live.** Three populations contribute:
*fetched-but-not-yet-kicked* records, *kicked-but-still-running* builds, and *parked* symbol builds holding an
`Acquire()` reference across the sprite-settle window.

**The first population is unbounded.** The other two are paced (kicks by `MaxMeshBuildsPerTick`, parks by the
sprite fetch), but neither is *hard*-capped: nothing caps how many kicks are in flight, only how many start per
tick. A tile is resident from **fetch completion**, and nothing paces fetch completion:

- **`TileScheduler` has no concurrency cap** — it holds dictionaries only: no semaphore, no queue. Every cover
  tile's request starts immediately.
- **`TileCache` (LRU, 256 by default) resolves a pan-back or a restyle's whole cover synchronously from
  bytes**, so every one of those tiles dispatches its decode on the same tick.
- **Kicks drain at `MaxMeshBuildsPerTick` (default 2)**, so the backlog leaves slowly.

This is in tension with the repo's `always-bound-loops` principle. The call is to measure first, because the
remedy stays cheap.

### The estimate and its derivation

| quantity | value | source |
|---|---|---|
| decoded tile, median | 552 KB | measured, `docs/tile-geometry-ir-design.md` |
| decoded tile, peak | 967 KB | same |
| tilted cover | 40–60 tiles | the tilted-frustum cover |
| **peak resident** | **~20–60 MB** | median–peak × cover |
| drain time | ~20–30 ticks | 40–60 tiles ÷ `MaxMeshBuildsPerTick = 2` |

A decode at kick time self-limits residency to single digits of tiles (`MaxMeshBuildsPerTick` × task duration ÷
tick duration), ~1–2 MB, so fetch-completion decode is a **~20× transient peak**, plus a burst of 40–60
concurrent decodes on one tick. It is transient and self-draining. **It has not been measured on a real device.**

### The remedy, if a device profile ever shows it: decode at kick admission

**Dispatch the decode at kick admission rather than at fetch completion** — a small change, confined to the
fetch-observe block of `TileManager`'s pending pump plus `TileDecodeDispatch`. It **removes the
fetched-but-not-kicked population entirely**, because a tile is decoded only once something is about to
consume it. It is **not** a hard residency bound and does not add one: kicks can still overlap without limit
(a per-tick start cap, not a concurrency cap), and a parked build's reference can outlive the kick that
created it, so residency is bounded only by the self-limiting behaviour of a kick-time decode. A genuine cap
needs a concurrent-kick limit, which is a separate change. The remedy keeps every property the current design
has — *one decode per (source, tile)*, *no parked re-decode*, *no decode without an owner* — and gives back
only "as early as possible".

**A semaphore inside `TileDecodeDispatch` is NOT the fix.** It bounds the CPU burst and thread-pool
saturation, which is a different problem. A decoded tile stays resident until it is kicked regardless of how
its decode was paced, so a semaphore leaves residency where it is.

## Recorded limitations — two things no test can observe

A recorded limitation needs a test that fails if it stops being true. For these two, none can, so prose
carries them.

**1. A throwing `Release()` during teardown aborts the loop, leaving later records untorn.**
`RenderTeardownRecord` nulls the record's handle before releasing it, so the funnel itself cannot retry a
handle it has already released. The residue is in the callers: a throw from one record's release abandons
every record the surrounding loop had not yet reached.

- *Why no test exists.* Every caller passes a **struct copy**, so `lt.Decode = null` is discarded even on the
  happy path — no production-observable difference exists between the two orderings. Reflection cannot see it
  either: `MethodInfo.Invoke` does not copy a by-ref argument back when the callee throws. The ordering is
  therefore pinned **structurally** (`RenderTeardownRecord_NullsTheRecordsHandleBeforeItReleases`).
- *No half-torn record survives on a live path.* `TileManager.RemoveAndTeardownRecord` removes the record
  from `_loaded` before tearing it down, so restyle, in-place restyle and eviction leave no half-torn record.
  `DoDispose` tears down in place, and a throw in its loop skips the `_loaded.Clear()` that follows. That is
  accepted because `DoDispose` is **terminal**: nothing reads `_loaded` after the manager is disposed, and
  `VerifiedDisposable.Dispose` sets `IsDisposed` *before* calling `DoDispose`, so a second `Dispose()` does
  not re-enter the loop. A remove-first shape there would need a key snapshot, which breaks the structural
  pin that every record goes through the single teardown funnel.
- *Why it is accepted.* No known route leads to a throwing `Release()` — it needs a decoder whose
  `IDecodedTile.Dispose()` throws, or a future over-release. Two alternatives are rejected: a per-record
  `try/finally` in all three loops changes teardown from **fail-fast to best-effort**, a semantic change that
  needs its own test; and making the funnel swallow-and-log a release fault would remove the unbalanced-release
  diagnostic at the site where it is most informative.

**2. A thread pool that accepts a work item and never runs it strands a reference.**
A parked symbol build hands its decode reference to the body it dispatches through `IWorkScheduler.Schedule`,
and that body is the only release. The dispatcher gives up ownership once `Schedule` has returned, so a pool
that accepts the item and never runs it is undetectable from that frame. The kick lambda carries the same
residual. Closing it needs a completion watchdog rather than an ownership guard, and no test can produce the
condition.

## Out of scope

- **Whole-restyle transactional atomicity** — a mid-commit-throw rollback, a gap fill/line share
  (`docs/smooth-transitions-design.md` § "Style switching").
- **Full-sphere polar background** — the globe track (`docs/projection-globe-track-design.md`).
- **Per-tile background overdraw** — N quads, one per covered tile; unprofiled. A bottom-slot opaque
  background could become the camera clear instead.
- **The mesh-entry `WorkerThenMain` rejection is dead but tested.** No mesh processor requests a tail;
  widening the mesh runner to run one is rejected, and the guard stays as typed-contract defence against a
  future dual-capability implementor.
- **A raster layer** — a raster-source processor with a texture artifact; not built.
