# Per-layer tile processing — design + landed record (unify the fork at the decoded tile)

**Status: COMPLETE (Epic A, 2026-07-15).** Shipped on `feat/tile-pipeline-unification` (A1–A7 + merge-step
follow-ups — see §"Epic A complete" at the end). This doc is both the original design proposal and the running
landed record: each stage carries its "landed" note + review results. It captures *why* the tile pipeline
forked fill/line vs symbol at the raw bytes, why that was unsatisfying, and the "decode once, fan out per layer"
rework that resolved it. The geometry-IR follow-on epic lives in `docs/tile-geometry-ir-design.md`. Read after
`docs/mesh-pipeline.md`
(fill/line build), `docs/labels-and-symbols-design.md` (the symbol path), `docs/render-layer-unification.md`
(the `IRenderLayer`/`RenderLayerSet` model this builds on) and `docs/tile-pipeline-design.md`.

## The itch

Fill, line, and symbol are all just **style layers over the same vector tile**. Yet the pipeline forks them
at the *earliest possible* seam — the moment raw MVT `byte[]` is fetched:

- Fill/line: `TileManager` decodes the tile and builds meshes (one worker task per tile, tile-atomic consume).
- Symbol: `SymbolLabelSubsystem` receives the **same** fetched bytes via the `SymbolTileBytesReady` push and
  runs an entirely parallel pipeline — **decoding the tile a second time** (`MvtDecoder.Decode` at
  `TileManager.cs:1294` for meshes, again at `SymbolLabelSubsystem.cs:357` for labels).

So the same protobuf parse runs twice per tile, and "symbol" is architecturally a different *kind of thing*
from "fill"/"line" when conceptually it is a sibling. Both decodes are off the main thread and decode has
never been a profiled bottleneck, so this is a **cleanliness / extensibility** problem, not a perf fire.

## Why it ended up this way (honest history)

Symbols were added **after** the mesh pipeline. That pipeline (`TileManager` build → main-thread `MeshData`
allocate → worker write → tile-atomic consume → dispose-once, plus the Model B prepared cache) carries a lot
of hard-won stall/leak fixes and a delicate disposal/ownership contract. The lowest-risk way to add labels
was to tap the one clean, stable seam (`byte[]` ready) and run a **decoupled** subsystem that "never touches
the mesh/disposal pipeline" (its own header says exactly this). That was a reasonable call — it isolated a new
feature from a fragile core — but it left the decode duplicated and the layer model bifurcated.

## The precision that matters: the fill/line side is already per-layer

The fill/line path is **not** a monolith that blends all fill/line layers. It already iterates layers:

```
per tile:  decode MvtTile (once) ──► for each IRenderLayer in RenderLayerSet order:
                                          layer.WriteInto(its own pre-allocated MeshData)
```

Each `IRenderLayer` writes its **own** `MeshData`. What is actually *shared* per tile is three tile-scoped
things — **one decode, one worker task, one tile-atomic consume** — not the geometry work. The real
"monolith" is *one tile build task*, and the true outlier is symbol (forked before the decode). So the
fill/line side is already ~80% of the target model; the rework is (1) hoist the decode to an explicit shared
step and (2) make symbol a processor in the **same** fan-out.

## The hard constraint: decode is tile-scoped, not layer-scoped

You cannot make each layer "trigger its own processing *from the bytes*." MVT layers are interleaved protobuf
messages — reading *any* layer means parsing the *whole* tile. Per-layer-from-bytes would re-parse the tile
once per layer (the current symbol duplication, generalized to N). Therefore the natural shared root is the
**decoded `MvtTile`**, not the bytes. Layers fan out *below* the decode:

```
fetch bytes
   └─ decode MvtTile                     ← ONCE per tile (tile-scoped; unavoidable)
        └─ fan out per style layer, each a uniform processor:
             ├─ FillLayerProcessor    → MeshData   (worker)
             ├─ LineLayerProcessor    → MeshData   (worker)
             └─ SymbolLayerProcessor  → extract    (worker) → shape + atlas (main, budgeted)
```

This is the MapLibre / Mapbox GL **WorkerTile + Bucket** model: parse the tile once, then each layer produces
its bucket (`FillBucket`/`LineBucket`/`SymbolBucket`). We would be converging on the proven architecture, not
inventing one — a useful sanity check on the direction.

## Proposed shape

### A uniform per-layer processor

Every layer kind implements one contract — this is what makes symbol "just a layer":

```csharp
enum LayerPhase { WorkerOnly, WorkerThenMain }   // does this processor need a main-thread tail?

interface ITileLayerProcessor {
    LayerPhase Phase { get; }
    // Produce this layer's artifact for one tile from the shared decoded tile + tile context.
    LayerArtifact Process(in TileContext ctx, MvtTile tile);   // mesh payload OR labels OR icons…
}
```

Fill/line/symbol become three implementations differing only in `Phase`, their artifact type, and their sink.

### A per-tile coordinator

A tile-scoped coordinator owns the decoded `MvtTile`, fans out to each layer's processor grouped by `Phase`
(all `WorkerOnly` work in the worker pass; the `WorkerThenMain` tails after the hop to main), collects each
artifact, and routes it to that layer kind's **sink**. It preserves today's anti-spike batching: the
main-thread `MeshData` allocate/apply and the atlas upload stay batched at the phase boundary, not scattered
per layer.

### What stays pluggable (does NOT unify)

Unification stops at **decode + dispatch**. These downstream realities legitimately differ and must remain
per-processor policy, or the rework would regress what the current design gets right:

| Shared reality | Why it resists per-layer independence | Where it lives in the model |
|---|---|---|
| **Decode** | tile-scoped parse | the shared root above the fan-out (the whole point) |
| **Glyph atlas** | ONE fixed atlas across all symbol layers *and* tiles (growth-staleness hazard) | an injected shared resource symbol processors coordinate through — never per-layer |
| **Main-thread tails** | `MeshData` allocate/apply + atlas upload are main-thread only | `Phase` groups them; the coordinator batches the main-thread hop |
| **Budgets** | symbol shaping is bursty (today's `MaxBuildsPerFrame` pump); mesh build has its own cadence | per-processor budget knob, not one global rate |
| **Lifecycle** | mesh = dispose-once + Model B prepared cache; labels = keep-warm store | sinks stay separate; only decode + dispatch unify |
| **Draw / paint order** | fills under lines under symbols | already handled by `RenderLayerSet` ordering at draw time |

The rule: **"each layer is a first-class entity" ✓; "each layer fully independent from raw bytes" ✗** — the
tile-scoped decode and the cross-layer shared atlas forbid the second.

## What it buys vs what it costs

**Buys**
- Kills the duplicate per-tile decode (one parse feeds every layer kind).
- Symbol becomes a first-class layer processor — one conceptual model, not two hand-written pipelines.
- One scheduling/lifecycle *framework* with per-processor policy instead of two bespoke ones.
- New layer kinds (icons, heatmap, fill-extrusion, circle) slot in as processors, not new subsystems.

**Costs**
- It opens the **most delicate seam in the codebase** — the mesh build/consume/disposal path with all its
  stall/leak fixes — to insert the coordinator. That risk is exactly why the original decoupling avoided it.
- The payoff is mostly architectural: the duplicate decode is off-main and not on any profile, so this is a
  structural-debt paydown, not a perf win. Prioritize accordingly.

## Staged migration (each stage independently green)

Do this deliberately, never opportunistically. Every stage must pass the full EditMode gate before the next.

1. **Extract the decode as an explicit shared step.** Inside `TileManager`'s build, make `MvtTile` a named
   product of a distinct decode step (no behaviour change) — the future fan-out point. Pure refactor.
2. **Introduce `ITileLayerProcessor` behind the existing fill/line loop.** Wrap today's `IRenderLayer.WriteInto`
   as a `WorkerOnly` processor. The loop now dispatches processors; output identical (byte-parity meshes).
3. **Model the symbol path as a `WorkerThenMain` processor — still fed by the symbol subsystem's own decode.**
   Prove the phase/coordinator machinery with symbol *before* touching its decode. Byte-parity labels.
4. **Feed the symbol processor from the shared `MvtTile`; delete the second decode.** The actual win. Retain
   the tile just long enough for both consumers (they run on different cadences — a short-lived per-tile
   decoded-tile hold, released when both are done or on eviction).
5. **Retire the parallel `SymbolLabelSubsystem` build path**, keeping its *sinks* (the keep-warm
   `SymbolTileLabelStore`, the atlas, the reconcile) as the symbol processor's sink + shared resources.

Stages 1–3 are safe internal refactors that de-risk the change; the coupling risk concentrates in 4–5.

**A1 landed (2026-07-12), folding stages 1+2 above into one stage.** Plan:
`docs/per-layer-tile-processing-a1-plan.md`. Stage 1 ("extract the decode as an explicit shared step") was
deliberately **not** shipped standalone — `TileManager.KickMeshBuild` already named its decoded tile as a
local (`MvtTile mvtTile = MvtDecoder.Decode(mvtBytes)`), so merely hoisting that expression into a helper
would pass compilation and snapshot parity without proving any new architecture — a vacuous stage with a
hollow acceptance gate. A1 instead landed the real contract (stage 2) with the decode extraction built in as
its natural consequence: `ITileLayerProcessor` (worker invocation over a shared decoded `MvtTile`) +
`ITileMeshLayerProcessor` (the mesh-settlement capability, `Complete()` → `IRenderLayerPayload`) +
`TileLayerProcessorRunner.RunWorkerPass` (decode once, dense-order invoke, settle every processor exactly
once) + the `TileMeshLayerProcessor` adapter around today's `ITileMeshRenderLayer.WriteInto` — all under
`Assets/Code/MapRenderer.Unity/Rendering/Tile/Processing/`. `TileManager.KickMeshBuild` now only allocates
one processor per this-source layer at kick and calls `TileLayerProcessorRunner.RunWorkerPass` once; it
contains zero direct `MvtDecoder.Decode(`/`.WriteInto(` calls (RED→GREEN structural tooth,
`TileProcessingStructureTests`). Fault policy is byte-for-byte preserved: a decode fault or a processor
exception aborts the remaining worker invocations for that pass, but every processor still settles exactly
once (per-processor guarded), so no kick-allocated `MeshDataArray` is ever stranded
(`TileLayerProcessorRunnerTests`). Every `Visual/` snapshot and the named regression suites (fixture, async
build, S51 leak guard incl. positive control, prepared cache, throttle, release budget, backend null-slot,
fetch cancellation) stayed green, byte-identical, unmodified.

**A1 review (2026-07-12) — both reviewers APPROVED** (Codex `gpt-5.6-sol` high effort: no findings; Opus:
8/8 teeth OK, gate independently re-run 1389/1389). Two **non-blocking** follow-ups recorded here for the
merge step, neither reachable today:
- *Runner belt-and-braces null slot.* `TileLayerProcessorRunner.RunWorkerPass`'s per-processor settlement
  guard emits a `null` payload slot if a `Complete()` throws. `ConsumeMeshBuild` **tolerates** null slots
  (`TileManager.cs:1385` `if (payload == null || …)`; `DisposeResult` `:1466` `?.Dispose()`) so there is no
  NPE (Opus's "potential NPE" framing was over-stated — verified against source). The real adapter's
  `Complete()` is infallible, so the guard can't fire; if it ever did the residual risk is a *leaked*
  `MeshDataArray` (the wrap that would free it is exactly what threw), not a crash. Optional hardening: assert
  non-null / document the guard as unreachable. Not worth a stage on its own.
- *`PmTileDecode` profiler marker* — **RESOLVED** (A4): wired into `Processing/SharedTileDecode.GetOrDecode`
  (the decode's sole home now); `"MapRenderer.Tile.Decode"` fires once per decode. No longer unwired.

Stage 3 onward (A2+: null-source background, symbol as `WorkerThenMain`, shared-decode retention, retiring
the symbol build fork, decode/feature generalization, the `ITileFeatureSource` seam, and the payload stages)
remain explicitly **pending** — not pre-built by A1. The hard fences A1 respected (and A2+ must keep
respecting until their own stage explicitly lifts one): `SymbolLabelSubsystem`'s own decode/build path,
`TileManager.SymbolTileBytesReady`/label store/atlas, `ConsumeMeshBuild`/`MeshBuildResult`/
`MeshDataPayload`/`IRenderLayerPayload`/`PreparedTileCache`/the dispose-once Model-B contract, `IDataSource`/
`MvtDecoder`/`MvtTile`/`MvtFeature` generalization, and `BackgroundRenderLayer`/`RenderLayerBuild.ViewGeometry`.

## Open questions

- ~~**Decoded-tile retention.**~~ **Answered by A4** (2026-07-13): a per-fetch shared handle
  (`SharedTileDecode`), not a keyed cache or ref-count-as-correctness — see the A4 landed note above.
- ~~**Coordinator ownership.**~~ **Answered by A4** (2026-07-13): no standing coordinator in A4 — the
  entry self-coordinates (`GetOrDecode`'s lazy first-arrival decode); `TileManager` mints at the one fork
  site it already owns. A *per-tile* coordinator fanning both kinds out in one pass is still A5+'s shape —
  see the A4 landed note above.
- ~~**Budget unification.**~~ **Answered by A5 (2026-07-13):** two budgets, clarified non-overlapping roles
  — the design's "probably the latter" guess confirmed. End state (A5b): the shared kick cap
  (`MaxMeshBuildsPerTick`) gates WORKER extraction for both kinds; `MaxBuildsPerFrame` becomes purely the
  TAIL budget (max symbol tails started/frame) — the stall-#1 bound it always really was. A5a's transitional
  state (landed): `MaxBuildsPerFrame` gates BOTH worker starts (unchanged) and tail starts (new) — see the
  A5a landed note below.
- **Does the shared atlas force symbol processors to be tile-serialized** at the main-thread tail anyway,
  eroding some of the "independent processor" cleanliness? **Confirmed yes by A5a** — tails run on the main
  thread and the GPU upload remains the one coalesced `PumpBuilds` blit; the atlas stays serialized by
  construction, unchanged by the worker-phase/tail split.
- **Runner widening (A3/A4's Q5 deferral) — REJECTED PERMANENTLY by A5 (2026-07-13), not deferred again.**
  The merged coordinator (A5b) fans out to mesh and symbol via TWO runner calls (grouped by `Phase`), never
  one mixed-processor pass — the two phase groups differ in context (mesh bakes at `id.Z`, symbol at camera
  zoom), fault policy (mesh settles-on-abort, symbol propagates-no-settlement), and output shape (mesh
  settles to `IRenderLayerPayload[]`, symbol produces nothing at the worker boundary) — three load-bearing
  differences a single entry would have to paper over. `RunSymbolWorkerPass` survives; only its caller moves
  (A5b). The mesh entries' `WorkerThenMain` guards stay, permanently, as typed-contract defense for a
  hypothetical dual-capability implementor — "dead-but-tested" is the right end state, not a deferral.

## Decision

Direction endorsed (converges on the proven MapLibre WorkerTile/Bucket model, kills the duplicate decode, and
makes symbol a first-class layer). **Not scheduled** — it is structural-debt paydown through the riskiest
seam for a non-perf payoff. Execute when the tile-build seam is being touched for another reason, or if decode
ever surfaces in a trace. Until then this doc is the record of intent and the staged plan.

---

## Round-2 update (2026-07-12) — this is now the foundation epic ("Epic A")

**Status change:** the "Not scheduled" decision above is superseded. The trigger it named ("execute when the
seam is touched for another reason") has arrived. The render-layer round-2 work (E1–E3, shipped on
`feat/render-layer-unification-r2`) unified the *layering model* but left the non-tile kinds **projection- and
pipeline-incomplete**, and three separate needs — projection-agnostic **background**, **raster**, **GeoJSON**
— all converge on *exactly this seam*. This is now the next foundational epic. **Most other pending items are
its payload, not peers.** (SKETCH — turn into a file-level plan before implementing.)

### Extension 1 — the source-driven axis (folds in background + null sources)

The uniform-processor model above is vector/MVT-centric. Generalize the layer→data relationship:

- A layer **declares its source** (`StyleLayer.Source`) — or **null**.
- The per-tile coordinator gathers the sources its layers need, **fetches/decodes each once** (shared across
  all layers of that source — the decode-once rule), then triggers each layer's prepare with **its source's
  decoded data**.
- **Null-source layers (background) prepare immediately** with null data — a full-tile-extent quad per covered
  tile, no fetch/decode.

This is the clean resolution of the background problem: background stops being a bespoke `ViewGeometry`
world-quad and becomes a **source-less `TileMesh` processor**. E3's world-quad is the *interim* Mercator
implementation; Epic A replaces it. (Consequence: `RenderLayerBuild.ViewGeometry` loses its only user and the
build axis collapses to `TileMesh` vs `FramePlaced`.)

### Extension 2 — generic data source (MVT is one impl; GeoJSON-ready)

Abstract the source so MVT / GeoJSON / raster are peers. Current state (verified 2026-07-12):

| Seam | Today | Generic already? |
|---|---|---|
| Fetch | `IDataSource.FetchAsync(TileId) → {bytes, TileEncoding}` | ✅ yes (`Core/Data/IDataSource.cs`) |
| Feature evaluation (filters/expr/paint) | `IFeature` + `MvtFeatureAdapter` | ✅ yes (`Core/Expressions/IFeature.cs`) |
| **Decode** | `MvtDecoder.Decode → MvtTile`, hardcoded (`TileManager.cs:1306`, `SymbolLabelSubsystem.cs:324`) | ✅ (A6) — `ITileDecoder`, encoding-driven |
| **Geometry consumption** | `WriteInto(IReadOnlyList<MvtFeature>)` | ✅ (A6) — `WriteInto(IReadOnlyList<ITileFeature>)` |

*(Table as of 2026-07-12, the pre-A6 snapshot the plan was scoped against; both bottom rows landed 2026-07-14
— see the "A6 landed" note further down for the post-A6 state, including the `MvtFeatureAdapter` retirement.)*

So "abstract from MVT" is only the bottom two: move decode **behind the source** (encoding-driven → a neutral
`DecodedTile`) and neutralize the geometry representation so `WriteInto` consumes a source-agnostic tile-feature
(MVT and GeoJSON both produce it; `IFeature` is already its evaluation face).

**Raise the source interface to the decoded-tile level — landed (A7, 2026-07-15).** `IDataSource` was a
per-tile *byte* fetcher (HTTP tile pyramids). **GeoJSON is not tiled bytes** — it is a whole dataset loaded
once and sliced client-side (geojson-vt). So the target seam is `ITileFeatureSource.GetTile(TileId) →
IDecodedTileHandle` (a LAZY decode-once handle, not an eager `DecodedTile` — see the A7-landed note below for
why): MVT = fetch-bytes + decode-protobuf, wrapped by `MvtTileFeatureSource`; GeoJSON = load-once + slice;
raster = fetch-bytes + a **texture** artifact (source output is polymorphic by kind: vector→features,
raster→texture — GeoJSON/raster payloads are A8+, not built by A7). Byte-fetch is now an MVT/raster
implementation detail, invisible at the coordinator.

### Extension 3 — projection-correctness + backend-consistency, both for free

Because each layer's per-tile prepare projects through the same `IProjection` as fill/line:
- **Background is projection-correct** (flat on Mercator, curved on the sphere) — deletes E3's Mercator-only
  gate and the flat-earth `y=0` assumption. (Symbol far-side occlusion is NOT fixed here — that is Track B,
  `docs/projection-globe-track-design.md`.)
- Background/raster go through the **active `ITileRenderBackend`** (Entities/BRG/GameObjects), not bespoke
  GameObjects — resolving the "why always GameObjects?" inconsistency. Only **symbols** stay
  GameObject/`FramePlaced` (genuinely screen-space; defensible as the one non-tile kind).

### What falls out — Epic A's payload (NOT separate epics)

| Previously framed as | Becomes, under Epic A |
|---|---|
| "redo background per-tile" | a null-source `TileMesh` processor (replaces E3's world-quad) |
| **Raster (F)** (scoped out of round-2) | a raster-source processor (non-MVT source, texture artifact) |
| GeoJSON support | a second `ITileFeatureSource` impl behind the decoded-tile seam |
| **Fill-extrusion (G)** (scoped out) | a `TileMesh` processor + ZWrite — one class + one arm |
| symbol double-decode | symbol fed by the shared decode (stages 3–5 above) |

### Sequencing note

The staged migration (stages 1–5 above) is still the spine. It gains: a **decode-generalization** stage
(encoding→decoder→`DecodedTile`; neutralize `MvtTile`/`MvtFeature`), a **source-interface-raise** stage
(`IDataSource`→`ITileFeatureSource`, GeoJSON-ready), and the **null-source/background processor** as an early,
low-risk proof (simplest processor — no fetch, immediate prepare). Still the riskiest seam in the codebase
(the mesh build/consume/disposal path) — do it design-first, stage-by-stage, each independently green.

**A1 (processor contract + fill/line fan-out, folding staged-migration stages 1+2) landed 2026-07-12** — see
the note under "Staged migration" above for the exact processor/runner names, the RED→GREEN structural tooth,
and why decode-only was rejected as a vacuous standalone stage. `docs/per-layer-tile-processing-a1-plan.md`
also carries a lightweight map of A2 (null-source background) through A8+ (payload stages); those remain
sketches, each getting its own source-grounded file-level plan before implementation — A1 does not pre-build
any of them, and the symbol/source/disposal fences listed above stay intact until a later stage's own plan
explicitly lifts one.

**A2 (source-less per-covered-tile background processor) landed 2026-07-13** — see
`docs/per-layer-tile-processing-a2-plan.md` (four adversarial review rounds). Lands the source-less arm of the
fan-out: `TileBackgroundLayerProcessor` (reuses `StyledFillTileBuilder.WriteMeshData` over a synthetic
full-tile-extent `MvtFeature` + constant-white paint — earcut winding + globe subdivision for free, no
hand-built quad) + `TileLayerProcessorRunner.RunSourcelessWorkerPass` (decode-free sibling of `RunWorkerPass`)
+ a dedicated source-less `SourcePipeline` slot in `TileManager` (`IsSourceless`, no `Scheduler`/`Source`,
kicked through the shared `PumpPending` build cap, settled by `DrainMeshBuilds` too). Background is now a
per-covered-tile `RenderLayerBuild.TileMesh` layer registered with the active `ITileRenderBackend`, projected
through the same `IProjection` fill/line use — E3's Mercator-only gate and self-owned world-cap
`MeshRenderer`/`Mesh` are retired; `RenderLayerBuild.ViewGeometry` is REMOVED (the axis collapses to
`{ TileMesh, FramePlaced }`). `MapMaterialSet.Validate()` (DECISION 2) makes an unconfigured base material
fail loud instead of silently producing a null-material backend slot; `MapView.SetStyle` is now transactional
across its one `await BuildSourceSpecs` (identity + `Layers.Build` + the captured-set `Validate()` all move
into the synchronous post-await commit), closing the blank-background/destroyed-material windows A2 would
otherwise have introduced. Whole-restyle transactional atomicity (a mid-commit-throw rollback) remains a
**pre-existing, unfixed** gap shared by fill/line — filed as a scoped follow-up, not built in A2 (DECISION 1,
option 3: background joins fill/line's existing restyle-failure behaviour). Full-sphere polar background
(beyond the Mercator pyramid's ±85.051°) is deferred to the globe track (see
`docs/projection-globe-track-design.md`).

**Follow-ups filed by A2 (not scheduled):**
- **Whole-restyle transactional atomicity** — off-side layer/backend construction + an atomic swap so a throw
  *during* the destructive `SetStyle` commit (`Layers.Build`/`_symbols.SetStyle`/`TileManager.SetSources`)
  rolls back cleanly instead of leaving a partially-mutated `RenderLayerSet`. This predates A2 and affects
  fill/line identically (`RenderLayerSet.ClearLayers` destroys every old layer's material before the new set
  is built) — A2 does not widen the gap, it just makes background share it.
- **Full-sphere / polar-cap background surface** — tracked in `docs/projection-globe-track-design.md` (Track
  B), shared by fill/line (the tile cover *is* the Mercator pyramid).

**A2 review (2026-07-13) — both reviewers APPROVED** (Codex `gpt-5.6-sol` high effort: no findings; Opus:
10/10 items OK, gate independently re-run 1409/1409, `BackgroundSnapshotTests` confirmed a real GPU render —
full-frame green with a red mid-stack occluder, not Inconclusive; all three dev deviations judged sound; the
source-less slot-stability worry resolved — `SetSources` fully evicts `_loaded` before re-slotting). Two
additional **non-blocking** code-quality follow-ups for the merge step:
- *Over-broad structural grep.* `RenderLayerRegistryStructureTests.MapView_NeverCallsSetVisible_OnABackgroundLayer`
  greps `MapView.cs` for ANY `SetVisible(` — a future unrelated `SetVisible` call would falsely trip it.
  Narrow to a background-layer-scoped pattern.
- *Dead `parent` parameter.* `BackgroundRenderLayer.Create`'s `Transform parent` arg is now unused (kept only
  for factory-arm signature uniformity). Unify the `RenderLayerFactory` dispatch signatures so source-less
  layers don't carry a dead `parent`.
- *Per-tile background overdraw* — N background quads (one per covered tile) instead of one, each ZWrite-off at
  one queue. Not profiled; a bottom-slot-opaque-background → camera-clear optimization is deferred.

**A3 (symbol as the first `WorkerThenMain` processor, still self-decoding) landed 2026-07-13** — see
`docs/per-layer-tile-processing-a3-plan.md` (converged over two adversarial review rounds). Gives
`LayerPhase.WorkerThenMain` its first real implementor and choreography: `ITileWorkerThenMainLayerProcessor`
(the artifact-free sibling capability of A1's `ITileMeshLayerProcessor` — one main-thread tail,
`CompleteOnMainAsync(ct)`, sink stays implementor-owned), `TileSymbolLayerProcessor` (one per symbol style
layer per (source, tile) build — worker step = `StyledSymbolTileBuilder.ExtractLayers` for its one layer,
main tail = `ShapeAsync` for its extraction, appending into the build's shared `List<LabelInstance>`), and
`TileLayerProcessorRunner.RunSymbolWorkerPass` (the symbol cadence's own decode-once worker-pass entry — no
settlement loop, no tail invocation, faults PROPAGATE rather than settle since symbol holds only managed
state to strand). `SymbolLabelSubsystem.BuildTileAsync` is refactored in place as the per-tile coordinator:
cadence (the bytes queue + `MaxBuildsPerFrame` pump), cancellation scope (`_buildCts`), and the sink
(`SymbolTileLabelStore.BeginBuild`/`CompleteBuild`) are all unchanged; one `SwitchToThreadPool`/
`SwitchToMainThread(ct)` hop pair now batches every layer's tail behind it, exactly today's shape. **Zero
`TileManager.cs` edits** (verified: an empty diff) — the `SymbolTileBytesReady` push, `KickMeshBuild`,
consume/dispose paths are all untouched. The symbol decode legitimately MOVES from
`SymbolLabelSubsystem.cs` into `RunSymbolWorkerPass` — a second, separate self-decoding cadence in
`TileLayerProcessorRunner.cs` alongside the mesh pass's (two decodes per (source, tile) persist by design
until A4 unifies the feed). The A1 decode-boundary structural test was re-scoped from a file-wide "exactly
one decode call" claim to per-method-body counts (mesh body 1 / source-less body 0), paired with the
`RunSymbolWorkerPass` edit so no checkpoint went phantom-red; a new assembly-wide boundary tooth
(`MapRendererUnity_DecodesMvtOnlyInsideTheProcessorRunner`) and the symbol body's own count
(`RunSymbolWorkerPass_DecodesExactlyOnce`) pin all three cadences. The mesh runner entries
(`RunWorkerPass`/`RunSourcelessWorkerPass`) are byte-identical (diff-verified) — A1's
`RunWorkerPass_WorkerThenMainProcessor_IsNotInvoked_AndStillSettles` guard test is retained, unmodified; the
`WorkerThenMain` guards in the mesh entries stay dead-but-tested through A3 (a deliberate, recorded
deferral to A4, not an oversight). Teeth: two structural RED→GREEN
(`SymbolSubsystem_DelegatesDecodeExtractAndShapeToTheProcessorMachinery`,
`MapRendererUnity_DecodesMvtOnlyInsideTheProcessorRunner`), a two-symbol-layer differential
(`SymbolProcessorParityTests` — production output deep-equals a single-pass `StyledSymbolTileBuilder.BuildAsync`
oracle over an independent glyph pipeline, label-for-label and batch-for-batch), a main-tail thread-pin
(`SymbolMainTail_RunsOnMainThread_AfterWorkerPass`, paired with the unmodified
`SymbolDecodeAndExtract_RunOffTheMainThread` worker-off-main recorder), and the runner-entry contract suite
(`TileSymbolWorkerPassTests` — dense order, shared-tile `ReferenceEquals`, no-tail-in-runner, `WorkerOnly`
rejected, malformed bytes propagate). Every named regression net (A1/A2 suites, `SymbolLabelSubsystemPumpTests`,
`FullPipelineTests`, `MapViewAsyncMeshBuildTests`, `S51`/`S82`/`S55`/`Stall2`/`BackendNullSlot`/
`TileFetchCancellation` suites, all 127 `Visual/` snapshots) stayed green, unmodified, byte-identical — final
gate 1418/1418.

**Dev-side empirical finding (recorded for the reviewer, not a plan deviation):** the plan characterizes the
differential parity test (`SymbolProcessorParityTests`) as one of the "genuinely RED-before-rewire" teeth,
alongside the two `SymbolLabelSubsystem`-scoped structural tests. RED-verification (temporarily reverting
`SymbolLabelSubsystem.cs` to the committed pre-A3 source, `Assets/Code` otherwise unchanged) showed the two
structural teeth DID fail as expected, but the differential parity + thread-pin tests PASSED against the
un-rewired subsystem (after an unrelated harness bug — the oracle's `GlyphManager` used the default
non-fixed atlas size instead of matching production's fixed `4096×4096`, so glyph UVs differed for a reason
having nothing to do with A3 — was fixed). This is expected on reflection: pre-A3 `BuildTileAsync` already
calls `ExtractLayers`/`ShapeAsync` with the exact same zoom/projection/materialIndices the oracle uses, so a
structurally-faithful pre-A3 implementation and the oracle compute IDENTICAL values — the differential is a
**wrong-impl falsifier** (catches a broken A3 rewire: wrong zoom, remapped material indices, dropped/reordered
layers, a skipped tail), not a tooth that distinguishes "pre-A3" from "post-A3" by construction. Both
structural teeth remain the load-bearing RED→GREEN proof that the rewire actually happened.

**Non-vacuity, verified by injection (advisor-flagged, closed).** The original two-layer/constant-text-size
style made §F-3(i) (wrong zoom) and §F-3(ii) (ordinal-vs-global material index) accidentally-green even
under a broken rewire — a single source meant global index == within-build ordinal, and constant text-size
+ zoom-independent point anchors meant tile-integer-zoom vs camera-zoom produced identical labels. The style
was strengthened: a third layer on a DIFFERENT source occupies global index 0 (so the "s"-source layers'
global indices `{1,2}` diverge from their ordinals `{0,1}`), and the two "s"-source layers use a
zoom-interpolated `text-size` (so zoom 3 vs 5 diverge). All four §F-3 falsifiers were then RED-verified by
injecting each into a scratch copy of the post-A3 `BuildTileAsync` (reversed processor order, skipped tail
loop, ordinal instead of global material index, tile-integer instead of camera zoom) and confirming
`ProductionBuild_MatchesSinglePassOracle_LabelForLabel`/`ProductionBatch_MatchesOracleBatch_…` fail for each
— then restoring the correct code and re-confirming the full 1418/1418 gate. The differential is confirmed
non-vacuous for all four named falsifiers.

**A3 review (2026-07-13) — both reviewers APPROVED.** Codex (`codex:codex-rescue` peer agent, read-only,
static diff review): faithful implementation, no static defects — it independently re-derived the parity
test's non-vacuity (the strengthened 3-layer style makes all four falsifiers structurally distinguishable).
Opus (`cadence:reviewer`, independent gate re-run **1418/1418**, all 10 new test names in fresh XML, snapshots
byte-identical): all 9 checklist items OK, both disclosed deviations sound. A snapshot guard (`sha256` of all
`.cs`/`.md`/`.asmdef` before/after) proved the reviewers modified no source. One **non-blocking** follow-up
beyond the A4 items below:
- **Multi-layer-tail cancellation-window test.** A3 introduces a genuinely new interleaving — the sequential
  per-layer `CompleteOnMainAsync(ct)` loop in `SymbolLabelSubsystem.BuildTileAsync`. A `ct` landing *between*
  processor tails `k` and `k+1` is safe by construction (the `_store.CompleteBuild` commit is after the whole
  loop + a `ThrowIfCancellationRequested`, so partial `labels` never commit), and is argued-equivalent to
  today's mid-`ShapeAsync` cancel — but it is covered only indirectly by the unmodified pump suite. Worth a
  dedicated structural test pinning "partial labels never reach `CompleteBuild`" on a mid-loop cancel.
- *Scope caveat (inherent, not a gap):* the differential parity validates one fixture/style/camera-snapshot —
  intrinsic to differential testing, acceptable for this invariant.
- A4 also inherits: retire the dead-but-tested mesh-entry `WorkerThenMain` guards when the runner is widened
  to the general `ITileLayerProcessor` surface (the signed-off Q1 deferral).

**A4 handoff notes (recorded, not solved here):**
- **Zoom-source mismatch.** `TileLayerProcessContext.Zoom` now carries two different meanings by cadence:
  mesh passes bake at the tile's INTEGER zoom (S82 Decision 2); the symbol pass evaluates at the CAMERA zoom
  captured at build start (pre-A3 parity, unchanged). A3 made this pre-existing mismatch visible in one
  shared carrier rather than resolving it — A4's shared per-tile pass must decide how (or whether) to
  reconcile the two zoom sources.
- **`RunSymbolWorkerPass`'s decode is the exact deletion target.** A4 collapses the runner's two
  self-decoding cadences into one: replace `MvtTile tile = MvtDecoder.Decode(mvtBytes);` inside
  `RunSymbolWorkerPass` with the shared decoded tile the mesh pass already produced for the same
  (source, tile) — a narrow, single-method change once the shared-decode feed exists.
- **Decoded-tile retention** *(resolved by the A4-landed note below — neither ref-count nor keyed TTL: a
  per-fetch GC-owned handle, retention == plain reachability).* A4 needed the `MvtTile` to outlive two
  differently-scheduled consumers (mesh's per-kick cadence vs symbol's push/pump cadence); the shipped
  answer is `SharedTileDecode`, discarded with its holder on the exact paths the raw `byte[]` dies on today.

**A4 (feed symbol from the shared decode; delete the second decode) landed 2026-07-13** — see
`docs/per-layer-tile-processing-a4-plan.md`. Answers the two "A4 handoff notes" retention/mechanism
questions above with a per-fetch, **GC-owned** holder, `SharedTileDecode` (new,
`Rendering/Tile/Processing/SharedTileDecode.cs`) — `{readonly byte[] bytes, MvtTile tile, cached fault,
object gate}` and exactly ONE operation, `GetOrDecode()`. **No refcount, no `Acquire`/`Release`, no clear,
no lifetime protocol of any kind** — an earlier draft of this plan carried a refcount as "memory hygiene";
it was deleted, not demoted: a release protocol whose own justification is "correctness never depends on
it" is dead weight and a review-surface tax bought for an unmeasured memory benefit. `TileManager`'s
fetch-observe site (`PumpPending`) mints one
entry per fetched (source, tile) via `new SharedTileDecode(bytes)`, stores it in `LoadedTile.ReadyDecode`
(renamed from `ReadyBytes`), and pushes it through `SymbolTileBytesReady` (retyped
`Action<string, TileId, SharedTileDecode>`) instead of raw bytes; `KickMeshBuild` and
`SymbolLabelSubsystem.BuildTileAsync` each just read through `GetOrDecode()` — no `finally`, no lifecycle
call. **Retention is plain GC reachability, full stop**: because `MvtTile` is pure managed data (no
`IDisposable`, no native memory), the entry is reachable only from its in-flight holders
(`LoadedTile.ReadyDecode` until kick, the kick task's closure, `PendingSymbolBuild` queue entries,
`BuildTileAsync` locals) — the *exact same* paths the raw `byte[]` dies on today, one edge deeper.
`RenderTeardownRecord`/`DoDispose`/`SetStyle`/`Dispose` get **zero edits** for the handle: a never-kicked
or dropped entry is simply discarded with its record, exactly as `ReadyBytes` is today. `GetOrDecode()` is
lazy and idempotent: whichever cadence arrives first decodes under the entry's lock (also the
safe-publication barrier for the effectively-immutable post-decode `MvtTile` across pool threads — the one
genuinely new cross-thread hazard A4 introduces, independently re-verified via the mutation grep — zero
production write sites to `Properties[`/`.Features.Add`/`.Keys.Add`/`.Values.Add` outside `MvtDecoder`);
a decode fault is cached (`ExceptionDispatchInfo`) and rethrown with the same instance to every caller, so
a malformed tile still faults exactly once. `RunSymbolWorkerPass`'s `MvtTile tile = MvtDecoder.Decode(mvtBytes);`
— the A3 handoff note's named deletion target — is now `MvtTile tile = decode.GetOrDecode();`, identical
to `RunWorkerPass`'s own read; **the runner itself stays stateless** (no cache, no refcount) — retention
lives entirely in the caller-owned `SharedTileDecode`. The zoom dual-meaning (the other A3 handoff note)
is kept, not resolved: `SharedTileDecode` carries only `{bytes → MvtTile}`, no context, so sharing the
decode cannot conflate the two cadences' zoom sources — reconciliation stays deferred to the
cadence-merging stage (`TileLayerProcessContext.cs`'s `Zoom` doc updated to record this). Widening
`RunWorkerPass` to a general mixed-processor pass (Q5) was deferred a second time — A4 shares decoded
*data*, not a shared *pass*; mesh and symbol still run in separate worker passes on separate cadences, so a
widened entry would still have no production caller (the same non-fiction test A3 applied) — the
`WorkerThenMain` guards in the mesh entries stay dead-but-tested through A4. Structural teeth re-scoped in
the same step as the decode move (no phantom-red window): the assembly-wide sweep's permitted file flipped
from `TileLayerProcessorRunner.cs` to `SharedTileDecode.cs`
(`MapRendererUnity_DecodesMvtOnlyInsideSharedTileDecode`), and both runner entries' per-body counts now
pin "0 direct decode calls, exactly 1 `.GetOrDecode(`"
(`TileLayerProcessorRunner_MeshWorkerPass_ReadsTheSharedDecode_AndNeverDecodesItself`,
`RunSymbolWorkerPass_DoesNotDecodeItself_ReadsTheSharedEntry`). New teeth: a decode-once cross-cadence
`ReferenceEquals` proof (both mesh-first and symbol-first arrival orders, F-1), a shared-fault same-instance
proof (F-4), and a bounded two-thread `GetOrDecode` race smoke (F-5) — no lifetime-protocol teeth, since
none exists. `SymbolLabelSubsystemPumpTests` got only a mechanical call-site retype (bytes → minted entry);
its existing assertions (a pumped build still commits labels) are the F-3 wiring/parity proof, unmodified.
All 127 `Visual/` snapshots and the full named regression net (`SymbolProcessorParityTests`,
`TileSymbolWorkerPassTests`, A1's settlement suite, `FullPipelineTests`, `MapViewAsyncMeshBuildTests`,
S51/S82/S55/Stall2/BackendNullSlot/TileFetchCancellation suites) stayed green, unmodified, byte-identical.
`MapRenderer.Core/` diff: empty (A4 is entirely a `MapRenderer.Unity` change).

**Accepted tradeoff (§G-4, no machinery built against it):** the decoded tile now lives exactly as long as
its last holder — including a completed kick task lingering in an S48 disposal pen, a `.Preserve()`d
closure awaiting budgeted consume, or a mesh-decoded tile rooted by a still-queued symbol build (bounded
by `_buildQueue` depth ≤ cover size, drained at `MaxBuildsPerFrame`/frame). Pre-A4 those holders rooted
only the `byte[]`; post-A4 they root bytes + decoded tile. Bounded, transient, and deliberately
**unmeasured** — A4 ships no early-null/early-release mechanism against it; measure-first if a device
trace ever shows the window matters.

**Open questions above, resolved by A4:** "Decoded-tile retention" and "Coordinator ownership" (§"Open
questions") are answered — a per-fetch GC-owned holder with **no lifetime protocol at all** (not a keyed
cache, not a refcount, not a standing coordinator type); see the A4 note above and
`docs/per-layer-tile-processing-a4-plan.md` §B for the full reasoning (Q1/Q2). "Budget unification" and
the shared-atlas serialization question remain open — untouched by A4.

---

## Round-3 update (2026-07-13) — A5a: split the symbol build into worker phase + budgeted tail pump

**A5a (split the symbol build; retire the parallel build path's monolith) landed 2026-07-13** — see
`docs/per-layer-tile-processing-a5-plan.md`. Decomposition: A5 as originally scoped (cadence merge + the two
due Q4/Q5 deferrals + budgets + zoom, spanning `SymbolLabelSubsystem`'s whole trigger architecture AND
`TileManager`'s kick path AND every symbol suite's drive) was split into **A5a (this stage) — the tail
machinery, entirely inside `SymbolLabelSubsystem`, ZERO `TileManager`/runner/`Processing/`/`MapView` edits**
(the A3 discipline) — and **A5b (its own plan doc, not yet written) — the feed swap**, which retires
`SymbolTileBytesReady`/`OnTileBytesReady`/`_buildQueue` and wires `TileManager.KickMeshBuild` to a factory
seam that runs `RunSymbolWorkerPass` inside the mesh kick task itself.

`SymbolLabelSubsystem.BuildTileAsync` no longer runs start-to-commit as one coroutine. It keeps its
prologue (`BeginBuild`, camera-zoom + projection capture, one `TileSymbolLayerProcessor` per style layer)
and its pool hop (`RunSymbolWorkerPass` on the thread pool), but after hopping back to the main thread it
now only enqueues a `ReadySymbolTail` (key, generation, processors, the shared `labels` list, the build's
token) and returns. The tail itself — every layer's `CompleteOnMainAsync` run sequentially behind that ONE
hop, then the whole-loop-then-`ct`-check-then-`CompleteBuild` order — moved verbatim into a new
`RunTailAsync`, started by `PumpBuilds`' new tail-start loop (a second `while` loop, its own
`TailsStartedLastPump` counter, budgeted at `MaxBuildsPerFrame` exactly like the build-start loop above it).
Both loops run inside the SAME `PumpBuilds` call, so a build whose worker phase lands synchronously before
the tail loop runs can still tail-and-commit within one pump (the pre-A5a single-build/single-pump tests'
observed timing is unaffected) — the budget only bites when MORE tails become ready in a frame than the
cap allows, which two builds started on consecutive frames (both landing their hops before the same pump)
can already produce.

**Deliberately preserved, not hardened:** the tail-start loop has NO stale/loaded re-check — a released-
to-cache tile's labels must still commit to the warm side (the store's generation guard is the sole commit
arbiter, unchanged since pre-A5a); adding a drop here would be a behaviour change, not a refactor. Restyle
(`SetStyle`) and teardown (`Dispose`) both gained `_readyTails.Clear()` beside the existing `_buildQueue.Clear()`
— a cleared tail's build was already cancelled by `_buildCts.Cancel()` just above, and its store slot dies
with `_store.Clear()`, the same fate as an in-flight pre-A5a build cancelled between hop and commit.

**Accepted tradeoff, user-signed-off:** under a burst of K tiles becoming tail-ready around the same frame
(an initial big cover, a fast pan), the Kth tile's shaping now starts ~K / `MaxBuildsPerFrame` pumps later
than the old inline start — a backlog-drain bound, not a flat one-frame bound. This is a deliberate,
accepted label-**appearance-timing** tradeoff (instant-under-burst label appearance traded for bounded
per-frame main-thread tail work — the epic's stable-FPS North Star); label **content** is untouched (same
extraction, same shape inputs, same commit guard) and every existing label-observing test settles through
pump/yield loops that absorb the shift.

**Teeth:** `SymbolTailPumpTests.cs` (new) — F-1 the structural split (`BuildTileAsync`'s body has zero
`.CompleteOnMainAsync(`/`.CompleteBuild(` call sites, `RunTailAsync`'s has exactly one each; RED-verified
against the pre-A5a source, where both lived inside `BuildTileAsync` and `RunTailAsync` did not exist), F-2
the tail budget gates tail starts (the two-worker-phases-then-flip-the-budget drive — a serial single-build
arrangement cannot exercise the gate, since one `PumpBuilds` call both produces and consumes), F-3 restyle
between the worker phase landing and the tail starting drops the ready tail with no partial commit (both the
between-phases variant and the finer during-tail-execution variant, gated on a `TestGlyphSource` held open),
F-4 (folded into the F-3 test) the dropped tail never starts. `SymbolLabelSubsystemPumpTests` and
`SymbolProcessorParityTests` stayed green, assertion content unmodified. Every `Visual/` snapshot
byte-identical; `MapRenderer.Core/` diff empty (A5a is entirely a `MapRenderer.Unity` change, confined to
`SymbolLabelSubsystem.cs`).

*Dual-review precision notes (both reviewers APPROVE; recorded, not fixes):* (1) the during-tail-execution
F-3 variant pins cancellation **handling** (`CancelledBuildCount==1`/silent cancel), not commit **order** —
the awaited `CompleteOnMainAsync(tail.Ct)` throws on the cancelled gate before either the `ct` check or
`CompleteBuild` is reached (and `SetStyle` has already `_store.Clear()`ed the slot), so the commit guard is
never the thing under test in that scenario; the guard is still correct, this is defense-in-depth. (2)
`RunTailAsync`'s explicit `tail.Ct.ThrowIfCancellationRequested()` is currently redundant with the store's
generation/clear guards (`_buildCts` is cancelled only where `_store.Clear()` also runs) — kept as a
faithful verbatim move and belt-and-suspenders; no test can isolate it today.

---

## Round-4 update (2026-07-13) — A5b: the feed swap (parallel push retired)

**A5b (swap the symbol feed to the shared kick; retire the parallel push) landed 2026-07-13** — see
`docs/per-layer-tile-processing-a5b-plan.md`. `TileManager`'s per-tile kick (`PumpPending`'s kick block)
now drives the symbol worker pass alongside the mesh pass, sharing the A4 shared-decode entry, through one
symbol-agnostic seam: `ISymbolTileWorkerFactory.TryBeginBuild(sourceId, tile)` (MAIN, kick time —
`SymbolLabelSubsystem`'s moved main-thread prologue: `BeginBuild` + camera zoom/projection capture + one
processor per layer, now sampled at KICK time instead of tail-pump time, §Q3) returning
`ISymbolTileWorkerPass.RunWorkerAndHandoff(SharedTileDecode)` (POOL, inside the SAME kick task, after the
mesh pass — runs `RunSymbolWorkerPass` over the shared decode, then enqueues a `ReadySymbolTail` onto a
`ConcurrentQueue`, the worker→main thread-safe handoff). `TileManager` holds only
`ISymbolTileWorkerFactory SymbolWorkerFactory` (replacing `SymbolTileBytesReady`); it never references a
label/store/glyph type. The parallel push — `SymbolTileBytesReady`/`OnTileBytesReady`/`_buildQueue`/the
build-start loop in `PumpBuilds` — is retired entirely; A5a's tail machinery (`RunTailAsync`/`_readyTails`/
the tail-start loop) is unchanged, now fed by draining the handoff queue at the top of `PumpBuilds` instead
of by `BuildTileAsync`'s own `SwitchToMainThread` hop (`BuildTileAsync` itself is gone — its prologue moved
into `TryBeginBuild`, its pool step into `RunWorkerAndHandoff`).

**Two fault domains, one kick task (§Q5):** the mesh `RunWorkerPass` call settles its `MeshBuildResult`
FIRST (mesh never throws — it settles-on-fault internally, per A1); the symbol call is then wrapped in an
OUTER `try/catch` in the kick lambda itself, belt-and-braces over `RunWorkerAndHandoff`'s own inner guard.
**Corrected post-dual-review** (the first RED-verification pass mis-described the failure mode): without
the outer wrap, a contract-violating symbol throw faults the whole `RunOnThreadPool` body, but the tile
does **not** fail to settle — `ConsumeMeshBuild` has its own, independent faulted-task guard that still
settles it (`Built = true`) via a SILENT-DISCARD path (never reaches `UploadMesh`, so the built fill
geometry is thrown away and the kick-allocated `MeshDataArray` is never disposed — a genuine
native-memory leak, not a settle failure). F-3 (RED-verified via `MeshDataPayload.DebugLiveAllocCount`,
mirroring `S51DisposalLeakGuardTests`: before=0/after=1 without the wrap, 0/0 with it, plus
`GetTileMeshes` confirming the fill geometry was actually registered) proves the wrap makes normal
consume — geometry registered, array disposed — structural instead of the silent-discard-and-leak path.

**Budget split (§Q4), landed end state:** `MaxMeshBuildsPerTick` (TileManager's kick cap) now paces BOTH
mesh and symbol worker starts — one knob for all pool-side tile work. `MaxBuildsPerFrame`
(`SymbolLabelSubsystem`) is now PURELY the tail-start budget — A5a's transitional dual role (gating both
worker starts and tail starts) is over. **N1, named honestly:** the two are now decoupled caps (worker
starts can outpace tail starts when `MaxMeshBuildsPerTick` > `MaxBuildsPerFrame`), so the ready-tail backlog
can pile up faster than A5a's coupled-cap regime — the §D6 backlog-drain label-appearance latency is
therefore structurally MORE likely post-A5b, not merely inherited. Still the accepted, user-signed-off
tradeoff, tunable via the two knobs if it ever proves visible.

**`DrainMeshBuilds` stays symbol-silent (§I open item, resolved KEEP):** `symbolPass` is a parameter to
`KickMeshBuild`, computed ONLY at the `PumpPending` kick site; both `DrainMeshBuilds` call sites pass the
default (null). Labels never rendered through the tile backends `DrainMeshBuilds` settles (they render via
`LabelPlacementSystem`), so this is behaviour-preserving by construction — pinned by `TileSymbolKickTests`'
F-4.

**N2, a marginal improving delta named for completeness:** a tile condemned to release now never attempts
a symbol build either (it rides the same kick that already skips `_releaseQueued` tiles) — pre-A5b the push
fired unconditionally at fetch-observe with no condemned check, so a narrow race could commit a symbol
build for a tile about to be evicted. Label content is unaffected either way.

**Teeth:** `TileSymbolKickTests.cs` (new) — F-1 the feed swap is structurally real (grep-narrow, RED
pre-A5b), F-2 the symbol pass rides the kick sharing exactly one decode (a spy factory + the
`MapRenderer.Tile.Decode` profiler marker firing exactly once across a combined mesh+symbol source), F-3
the two-fault-domain lambda wrap (a throwing spy pass; DECISIVE via `MeshDataPayload.DebugLiveAllocCount`
— the tile settles either way, so `AllTilesSettled()` alone is NOT decisive; the wrap's absence leaks the
kick-allocated array via `ConsumeMeshBuild`'s own silent-discard faulted-task path instead of failing to
settle — corrected post-dual-review, RED-verified), F-4 `DrainMeshBuilds` never drives the factory, F-5 a
symbol-only source kicks while a mesh-only source runs no symbol pass (`TryGetFetchSource`'s Q6 claim).
Two subsystem-level teeth retired, coverage relocated (not lost): the build-start throttle
(`PumpBuilds_StartsAtMostMaxBuildsPerFrame_QueuesTheRest`, superseded by `MaxMeshBuildsPerTick`'s existing
S55 kick-cap coverage + F-2 here) and the stale-drop-before-build-start
(`PumpBuilds_DropsBuildForTileThatLeftLoadedSet`, superseded by F-6 — a departed-before-kick tile never
reaches `TryBeginBuild`). `SymbolTailPumpTests`' F-1 re-expressed against `RunWorkerAndHandoff` (the method
`BuildTileAsync_...` anchored on no longer exists). `SymbolLabelSubsystemPumpTests` and
`SymbolProcessorParityTests` stayed green with their drive migrated to `TryBeginBuild` +
pool-run `RunWorkerAndHandoff` and assertion content unmodified. Every `Visual/` snapshot byte-identical;
`MapRenderer.Core/` diff empty.

**Follow-up found, NOT fixed here (pre-existing, out of A5b's scope):** a symbol-only source (zero dense
mesh layers) never fetches or kicks under the default (enabled) prepared-tile cache. `TileManager`'s
cover-request loop probes the S82 prepared cache before fetching — "every dense layer id of this
(tile, source) is present in `_prepared`" — but that check is a `for` loop over the source's dense mesh
layer ids that starts `allCached = true` and only ever falsifies it inside the loop body; for a
symbol-only source the loop body never runs (zero dense layers), so `allCached` stays vacuously true on
**every** cover entry, not just a genuine re-visit. The tile takes the `BuildTileFromCache` path forever
(no fetch, `Built = true` immediately, empty payload) and never reaches `PumpPending`'s kick block at
all — so a symbol-only source's labels never build with the cache on (today's default). This predates
A5b: the old push (`SymbolTileBytesReady`) fired only from the fetch-observe branch too, so it was
equally unreachable for this exact case — not a regression, a latent gap surfaced while writing
`TileSymbolKickTests`' F-5 (worked around there by disabling the prepared cache for that tooth). Needs a
real fix (likely: gate the vacuous-`allCached` short-circuit on `denseLayerIds.Count > 0`) as its own
follow-up, not folded into this stage.

**Open questions above, closed by A5b:** "Runner widening" and "Budget unification" were already answered
permanently by A5 (§"Open questions") describing exactly this end state; A5b is that state, landed.

**Runner widening (Q5) and budget unification:** answered permanently above (§"Open questions").

**A6 (decode + feature generalization: encoding-driven decoder, neutral tile) landed 2026-07-14** — see
`docs/per-layer-tile-processing-a6-plan.md`. A5b landed the *feed* unification (one kick drives mesh +
symbol over one decode); A6 is the first stage to touch the **representation** those passes consume — the
two "❌ MVT-coupled" rows in Extension 2's table above (§"Decode", §"Geometry consumption") flip:

| Seam | Now | Generic already? |
|---|---|---|
| Fetch | `IDataSource.FetchAsync(TileId) → {bytes, TileEncoding}` | ✅ yes (unchanged) |
| Feature evaluation (filters/expr/paint) | `IFeature`, implemented directly by `MvtFeature` (the retired `MvtFeatureAdapter` folded in verbatim) | ✅ yes |
| **Decode** | `ITileDecoder.Decode(bytes) → IDecodedTile`, resolved by `TileEncoding` via `TileDecoders.ForEncoding` (`Core/Tiles/ITileDecoder.cs`); `MvtTileDecoder` is the sole production `MvtDecoder.Decode(` call site | ✅ (A6) |
| **Geometry consumption** | `WriteInto(IReadOnlyList<ITileFeature>)` — the fill/line/symbol fan-out (`FeatureSelector`, `SourceLayerResolver.ResolveTileLayer`, `StyledFillTileBuilder`, `StyledLineTileBuilder`, `SymbolFeatureExtractor`, `StyledSymbolTileBuilder`) references only the neutral `IDecodedTile`/`ITileLayer`/`ITileFeature` surface | ✅ (A6) |

**Decision (X) — two decode layers, only one generalizes here.** The trap the plan named up front: conflating
*tile* decode (`bytes → tile structure`, the MVT-coupled seam A6 neutralizes) with *geometry-command* decode
(`uint[] → rings`, `MvtGeometry.Decode` — a stateless codec over the shared vector-tile geometry encoding that
MVT, MLT, and geojson-vt's sliced output all emit alike). A6 neutralizes the carrier types
(`MvtTile`/`MvtLayer`/`MvtFeature` → `IDecodedTile`/`ITileLayer`/`ITileFeature`, zero-copy — the MVT types
implement the neutral interfaces directly, no re-materialization) and the tile-decode seam
(`MvtDecoder.Decode` → `ITileDecoder`, selected by the previously-dropped `TileResponse.Encoding`), but
**deliberately keeps the `uint[]` tile-space command stream as the neutral feature's geometry payload** — fill
still feeds it raw to `TileMeshPipeline`/`MvtDecodeJob` (Burst-side decode), line/symbol still call
`MvtGeometry.Decode` (managed). Re-plumbing fill to a `loadGeometry()→rings` face would touch the most
delicate perf-sensitive code in the repo (the exact-sizing pre-count + OOB-safety machinery) — a genuine
behaviour change, not a re-seam, and explicitly deferred (not this stage's invariant to spend). The proof
this is structural, not a rename: `A6NonMvtDecoderTests` drives a `FakeTileDecoder` (test-owned, ignores the
injected bytes) through the UNCHANGED fan-out and gets the expected full-extent-quad geometry — a fan-out
that secretly still called `MvtDecoder.Decode` internally would fault on the test's deliberately-malformed
bytes instead.

**`MvtFeatureAdapter` retired, folded into `MvtFeature` verbatim (§B-2).** Every geometry consumer built
`new MvtFeatureAdapter(feature)` purely to get the `IFeature` evaluation face; with `ITileFeature : IFeature`,
`MvtFeature` implements it directly via explicit interface members (the uint64→double id narrowing, the
null-guard property fallback — pinned byte-exact by `A6AdapterFoldTests`, core-tests-visible). The adapter
alloc collapses; the public fields decode still writes are untouched.

**First Core diff in the epic.** A1–A5b's `MapRenderer.Core/` diff was empty; A6's is the neutralization
only — the new `Core/Tiles/{DecodedTile,ITileDecoder}.cs`, the `MvtModels.cs`/`FeatureSelector.cs`/
`SourceLayerResolver.cs`/`SymbolFeatureExtractor.cs` re-seam, and the adapter delete. `dotnet test
Tools/core-tests` is live from here on.

**A7 (raise the source interface to the decoded-tile level) landed 2026-07-15** — see
`docs/per-layer-tile-processing-a7-plan.md`. `IDataSource` stayed a byte fetcher through A6 (only decode +
feature representation generalized); A7 raises the *coordinator* boundary to
`ITileFeatureSource.GetTile(TileId) → IDecodedTileHandle` (§"Extension 2", "Raise the source interface"
above) — the seam a GeoJSON/geojson-vt source needs, since GeoJSON is a whole dataset sliced client-side, not
per-tile bytes to decode.

**The lazy-handle decision (§B), not eager `GetTile → IDecodedTile`.** `IDecodedTileHandle { IDecodedTile
GetOrDecode(); }` is the polymorphic seam `SharedTileDecode` now implements — decode stays LAZY (first
worker-pass arrival, off-main) and SHARED (one handle, minted once per (source, tile) fetch, read by both the
mesh kick and the A5b symbol pass), preserving the A4/A5b decode-once-shared invariant literally rather than
moving decode to fetch-completion time. `MvtTileFeatureSource` (`Rendering/Tile/Processing/
MvtTileFeatureSource.cs`, Unity) wraps the UNCHANGED `TileScheduler`/`TileCache`/`IDataSource` byte-fetch
machinery and mints the `SharedTileDecode` handle inside `GetTile` — the byte-fetch relocated verbatim from
the old `TileManager` fetch-observe site, with no fetch/cache/cancel behaviour change (`TileScheduler.cs`
diff empty). A future non-byte source (in-memory features today via `A7TileFeatureSourceTests`'
`FakeTileFeatureSource`; GeoJSON/geojson-vt later) returns an EAGER handle wrapping a pre-sliced
`IDecodedTile` instead — same interface, no coordinator change, the F-2 tooth's proof.

**`TileManager` is now byte-agnostic.** It names none of `TileResponse`/`IDataSource`/`TileScheduler`/
`SharedTileDecode`/standalone `TileCache` — `SourcePipeline` collapsed to `{ITileFeatureSource FeatureSource}`;
`SourceSpec.CreateSource: Func<ITileFeatureSource>` is wrapped in `MapView.BuildSourceSpecs`, the one
production site naming `MvtTileFeatureSource`. Structurally pinned by
`TileProcessingStructureTests.TileManager_ConsumesNoByteSource` (word-boundary `TileCache` match excluding
the 19 kept `PreparedTileCache` references, §D untouched) and the no-main-thread-hop tooth
(`MvtTileFeatureSource_GetTile_NeverHopsToMainThread`, pinning the `configureAwait:false` pool-completion
invariant `DrainMeshBuilds`' spin depends on).

**Next: A8+ — a second source kind's payload.** The polymorphic-source-output axis (vector→features today;
raster→texture, GeoJSON→sliced features) is documented on `ITileFeatureSource` but not built — A7 reseated
only MVT behind the raised interface and proved the seam with an in-memory *vector* fixture. A8 is the first
stage to add a real second payload kind.

## Round-5 update (2026-07-14) — the canonical-IR direction (supersedes Decision X's deferral)

**Maintainer principle, adopted as the epic's north star for the geometry representation:** *interfaces should be
as generic as possible; specific formats (MVT, MLT, GeoJSON) are implementations dispatched by format. The data
pipeline is generic at the initial "parse" stage, converges on ONE internal settled format, and every later
stage (project geo→world, build meshes) is a single implementation over that settled format.* This is a
canonical-IR / narrow-waist architecture: polymorphic at the edges (per-format decoders in, per-consumer stages
out), monomorphic in the middle.

**This supersedes Decision (X)'s deferral.** A6 kept the `uint[]` MVT command stream as `ITileFeature`'s
geometry payload and deferred neutralizing it "with the invariant as the reason." That deferral is exactly the
smell: the `uint[]` is MVT's *wire geometry encoding*, so the "neutral" feature is neutral only for MVT — every
other vector format (geojson-vt emits coordinate arrays; MLT is columnar) would have to *transcode into* MVT's
command stream, and raster/terrain/3D can't use a command stream at all. A6's Decision X was a sound **scope
fence** (don't touch the perf-critical Burst path in the carrier-neutralization stage); it is **not** a
defensible permanent architecture. The `uint[]` stops being the internal format; a canonical decoded geometry
becomes it.

**Key finding (Codex, verified against `TileMeshPipeline.cs`/`MvtDecodeJob.cs`/`MvtGeometry.cs`): the canonical
IR already exists — it is just unexposed.** `TileMeshPipeline.Schedule`'s Stage 1 (`MvtDecodeJob`) already
produces a blittable, flat, ring-offset geometry buffer — `NativeArray<double2>` verts + `NativeArray<int>` ring
offsets + `NativeArray<int>` per-ring feature index — and Stages 2–4 (ring assembly, earcut, tile→geo→world
projection) consume **only** that shape (zero `uint`/command/MVT references anywhere in them — already
format-agnostic). So the MVT coupling is contained in ONE `IJob` doing `commands → (verts, ringOffsets,
featureIndex)`. That output triple **is** the settled vector IR. The work is therefore **"expose the existing
Stage-1/2 boundary as a named type (`TileGeometryBuffers` or similar) + make Stage 1 pluggable by encoding"**,
NOT "design a new IR" — `MvtDecodeJob` becomes the MVT *implementation* of `→ TileGeometryBuffers`, dispatched
by encoding, mirroring A6's `ITileDecoder` pattern one layer deeper (Burst-side). Realistically **one
A6/A7-sized stage** if scoped tightly.

**Polymorphism by kind is correct, not a compromise.** Raster→texture and terrain→heightfield were never going
to share a representation with vector rings; forcing them into one shape would be the actual smell. So
`IDecodedTile` is the universal, **polymorphic-by-kind** level (vector→feature-IR, raster→texture,
terrain→heightfield); the monomorphic canonical ring IR lives one level down, under the *vector* arm. "One
settled format" reads as "one settled format per geometry-bearing kind" — which is what "source output is
polymorphic by kind" (§Extension 2) already committed to.

**Two fences the canonical-IR stage MUST carry (or it silently regresses perf):**
1. **Decode stays lazy per *selected* feature — never eager per tile.** `FeatureSelector.SelectFeatures` filters
   BEFORE any geometry decode; both `MvtGeometry.Decode` and `MvtDecodeJob` only ever see features that survived
   the style-layer/filter/zoom selection. "Settled format" = *the shape a selected feature converges to when
   decoded, invoked lazily by the consuming builder* — **not** a whole-tile eager transcode at `ITileDecoder`
   time (that would ring-expand features nothing renders, every tile). Definitional, easy for a developer to
   violate by "helpfully" moving decode earlier; state it explicitly in the plan.
2. **Do NOT bundle the managed/Burst double-decoder unification.** Two implementations of "commands → rings"
   exist today — `MvtDecodeJob` (Burst, `NativeArray`) and `MvtGeometry.Decode` (managed, `List<List<double2>>`
   for line/symbol), pinned to agree by hand by `JobifiedPipelineTests`. That duplication is real present debt,
   but collapsing it (making line/symbol also consume `NativeArray` rings) touches their whole managed path —
   a separate, larger stage with its own invariant/teeth. First cut exposes the seam only; unification comes
   later. Same reasoning A6 used to defer ring-encoding out of carrier-neutralization.

**Sequencing (agreed by both reviewers):**
- **`TileGeometryType` rename + `ITileFeature`/`IDecodedTile` honesty reframe** — the immediate down-payment.
  `MvtGeometryType` (enum values `Unknown/Point/LineString/Polygon` — already format-neutral; only the *name*
  and `Core.Mvt` home leak MVT) → **`TileGeometryType`** in `Core.Tiles`. Reframe the docs/naming so
  `ITileFeature` is honestly the *vector-feature* abstraction and `IDecodedTile` the polymorphic-by-kind
  universal level. Trivial, behaviour-identical (values unchanged → zero snapshot risk), ~62 refs across ~25
  files. **Landed 2026-07-15** (`MvtGeometryType`→`TileGeometryType` in `Core.Tiles`, 66 refs/26 files;
  `ITileFeature`/`IDecodedTile` doc honesty reframe; F-1 tooth `GeometryTypeEnum_RenameIsComplete_NoOldTokenSurvives`;
  gate EditMode 1445/0 + core-tests 915/0, dual-checked, byte-identical snapshots).
- **A7 (source raise)** — orthogonal (it raises *who* owns the decode handle; the IR is *what shape* geometry
  takes; `ITileFeature.Geometry`'s type never appears in A7's diff). Lands as planned, either order.
- **Canonical vector IR** (`TileGeometryBuffers` + pluggable Stage 1) and the **double-decoder unification** —
  spun out into their own epic, **`docs/tile-geometry-ir-design.md`** (the two-waist model, cross-validated by
  Opus + Codex 2026-07-15). Natural trigger is the **GeoJSON payload (A8)**. Not built speculatively now; see
  that doc for the settled direction, the stage sequence, and the open decisions.

## Round-6 → spun out (2026-07-15) — the geometry-IR direction moved to its own epic

Round-6 posed the altitude question ("is the neutral geometry IR tile-local or geodetic?"). It was
cross-validated by Opus (architect) + Codex (adversarial) on 2026-07-15 and resolved to a **two-waist** model
(tile-local `TileGeometryBuffers` for format-neutrality; the existing geodetic `NativeArray<GeoCoordinate>` seam
for projection-neutrality). Because it is a distinct, GeoJSON/A8-triggered effort with its own stage sequence
and open decisions, the whole direction — the conclusion, the implementation design, the correctness landmines,
and the stage sequence — lives in its own SSOT: **`docs/tile-geometry-ir-design.md`**. It is not part of
Epic A.

## Epic A complete (2026-07-15)

The per-layer-tile-processing / tile-pipeline-unification epic is **done**: the per-layer processor fan-out
(A1), the source-less background processor (A2), the symbol unification + decode-once-shared (A3→A5b), the
encoding-driven decode seam (A6, + the A6.1 `TileGeometryType` reframe), and the source-interface raise (A7)
all landed on `feat/tile-pipeline-unification`, each an independently-green, dual-reviewed commit. MVT renders;
the coordinator is byte-agnostic; decode runs once, off-main, shared across mesh + symbol.

**Merge-step follow-ups — resolved** (commit `fix(tile-pipeline): resolve the Epic A merge-step follow-ups`,
gate EditMode 1452/1452 + core-tests 915/915, RED→GREEN on S82):
- **S82** — a symbol-only source (zero dense mesh layers) never fetched/kicked with the prepared cache on
  (default); fixed by seeding the cover-loop `allCached` probe from `denseLayerIds.Count > 0` (a zero-dense
  source is never "all cached"). RED-verified regression test.
- **A1 `PmTileDecode`** — was already wired in A4 (`Processing/SharedTileDecode.GetOrDecode`); the "declared but
  unwired" note above (§A1 follow-ups) is closed.
- **A1 runner null-slot guard** — documented as unreachable-in-production (tolerated null slot downstream,
  bounded native leak at worst, never a crash) rather than asserted.
- **A2 over-broad `SetVisible(` grep** — scoped to the gate's own file (`BackgroundRenderLayer.cs`).
- **A2 dead `Transform parent`** — dropped from `BackgroundRenderLayer.Create` + the factory arm.
- **A3 mid-loop cancellation guard** — structural test pinning `RunTailAsync`'s commit-after-loop-and-ct-check
  order (partial labels never reach `CompleteBuild`).

**Deliberately left filed** (out of Epic A's scope — pre-existing, other-track, or accepted-as-is): whole-restyle
transactional atomicity — now the first piece of the broader **style-switching** topic
(`docs/style-switching-design.md`), whose goal is smooth transitions between compatible styles; full-sphere
polar background (globe track,
`docs/projection-globe-track-design.md`); per-tile background overdraw (unprofiled optimization); the
dead-but-tested mesh-entry `WorkerThenMain` guards (runner widening permanently rejected — they stay as
typed-contract defense).

**Tracked follow-up (needs its own stage) — resolve the A4 zoom dual-meaning: symbols evaluate at the TILE
zoom, not camera zoom.** `TileLayerProcessContext.Zoom` currently carries two meanings by cadence: the mesh
passes bake at the tile's INTEGER zoom (S82 Decision 2), while the symbol pass evaluates style expressions
(text-size etc.) at the **camera** zoom captured at build start (`SymbolLabelSubsystem.cs:279`
`_camera.CurrentProperties.Zoom`, flowing into the context at `:290`). **Direction (for now): change the symbol
pass to use the tile's integer zoom** — aligning it with the mesh and making a tile's labels deterministic,
cacheable, and independent of the camera zoom at build time. Scope: only the style-expression zoom at
`:279`/`:290` — NOT the `WebMercator.GroundResolution(_camera…Zoom)` cross-tile quantization at `:467`/`:484`
(that belongs to `docs/labels-and-symbols-design.md` §4). It IS a visual behaviour change —
`SymbolProcessorParityTests` + label `Visual/` snapshots will shift and must be **intentionally re-baked** (not
a refactor). "For now" flags that quantized-to-integer-zoom text sizing diverges from continuous-zoom
interpolation and may be revisited. Not built in Epic A.

**Next:** A8+ payloads (raster texture / GeoJSON slice / fill-extrusion) and, triggered by GeoJSON, the
geometry-IR epic (`docs/tile-geometry-ir-design.md`).
