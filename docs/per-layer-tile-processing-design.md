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
  production site naming `MvtTileFeatureSource`. A future non-byte source (GeoJSON/geojson-vt) returns an EAGER
  handle wrapping a pre-sliced `IDecodedTile` — same interface, no coordinator change.

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

## Deliberately left filed (out of scope)

Whole-restyle transactional atomicity → the first piece of `docs/smooth-transitions-design.md` §1; full-sphere
polar background → the globe track (`docs/projection-globe-track-design.md`); per-tile background overdraw (N
quads, one per covered tile — unprofiled, a bottom-slot-opaque→camera-clear optimization); the dead-but-tested
mesh-entry `WorkerThenMain` guards (runner widening permanently rejected — kept as typed-contract defense for a
hypothetical dual-capability implementor); A8+ payloads (raster texture / GeoJSON slice / fill-extrusion) and,
triggered by GeoJSON, the geometry-IR epic.
