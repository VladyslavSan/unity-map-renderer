# Per-layer tile processing — design proposal (unify the fork at the decoded tile)

**Status: PROPOSED, not scheduled.** Design sketch only — no code exists for this. Captures *why* the tile
pipeline currently forks fill/line vs symbol at the raw bytes, why that is unsatisfying, and a concrete
"decode once, fan out per layer" rework with a staged migration. Read after `docs/mesh-pipeline.md`
(fill/line build), `docs/label-pipeline-flow.md` (the symbol path), `docs/render-layer-unification.md`
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

## Open questions

- **Decoded-tile retention.** Stage 4 needs the `MvtTile` to outlive two differently-scheduled consumers.
  Ref-count, or a small keyed cache with a frame-bounded TTL? (`MvtTile` is immutable managed data, so
  sharing is a reference — only retention is the question.)
- **Coordinator ownership.** Does the per-tile coordinator live in `TileManager` (which owns the tile
  lifecycle) or a new type `TileManager` delegates to? Leaning: a new type, to keep `TileManager` from
  growing a symbol dependency.
- **Budget unification.** Can one budget model express both "N mesh builds/frame" and "N symbol shapes/frame",
  or do processors keep independent budgets forever? (Probably the latter — shaping and meshing scale
  differently.)
- **Does the shared atlas force symbol processors to be tile-serialized** at the main-thread tail anyway,
  eroding some of the "independent processor" cleanliness? (Likely yes — acceptable; the atlas is genuinely
  shared state.)

## Decision

Direction endorsed (converges on the proven MapLibre WorkerTile/Bucket model, kills the duplicate decode, and
makes symbol a first-class layer). **Not scheduled** — it is structural-debt paydown through the riskiest
seam for a non-perf payoff. Execute when the tile-build seam is being touched for another reason, or if decode
ever surfaces in a trace. Until then this doc is the record of intent and the staged plan.
