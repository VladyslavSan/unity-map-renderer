# Text &amp; Symbols (labels)

The MapLibre-style **text/symbol label** subsystem: turning a vector tile's label features into placed,
collision-resolved, camera-following glyphs on screen. It is a coherent module, but it deliberately
**spans all three assemblies** — engine-free primitives in `Core`, engine orchestration in `Unity`,
Burst hot loops in `Jobs`. This README is the entry point: what it does, where the pieces live, what
works today (the milestone), and the known problems with an analysis of how to fix them.

> Status in one line: **it works, and it looks good in general — but per-frame it is expensive and
> mostly on the main thread.** The correctness story is largely done; the performance story is not.
> The two design docs below own the deep designs; this file owns the map + the honest problem list.

## The pipeline (data flow across assemblies)

```
TileManager's per-tile KICK ──▶ SymbolSubsystem.TryBeginBuild (main, prologue)
                 │  + RunWorkerAndHandoff (pool, inside the SAME kick task as the mesh pass)  (Unity/Text)
                 │  decode + extract features — off the main thread (thread pool), sharing the mesh
                 │  pass's decode (A4/A5b: one decode-once entry, no parallel push feed)
                 │  shape glyphs (HarfBuzz-free CodepointTextShaper, bidi, Arabic joining)  (Core/Text)
                 │  Shape writes each raw label into a reused SymbolTileBuffer (no per-label alloc), after
                 │  the tail's one glyph-ensure suspension has already populated the atlas
                 ▼
     SymbolTileBuffer ─▶ Bake ─▶ SymbolTileBlock ──▶ SymbolTileStore   (Unity/Text)
                 │  native per-tile SoA block; per-(source,tile) sets, collected-set Version, static-frame skip
                 ▼
      ┌── SymbolPlacementSystem.Tick(frame, labels, atlas)  (Unity/Text/Placement)  [PER FRAME, MAIN]
      │      1. ProjectPositions  — gather world points + project to screen  (SymbolProjectionJob, Jobs — .Run())
      │      2. Stage             — lay each glyph onto the projected curve, build collision boxes + quads
      │      3. Collide           — greedy all-or-nothing placement          (CollisionJob, Jobs)
      │      4. Emit              — A-4 fade + assemble per-slot quad buckets
      │      5. BuildSubmit       — write the billboard Mesh + upload        (SymbolBillboardJob, Jobs)
      └──────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Where the code lives

| Concern | Assembly / path | Key files |
|---|---|---|
| Shaping, bidi, glyph atlas/SDF, font stacks | `Core/Text/` | `CodepointTextShaper`, `BidiReorder`, `ArabicJoining`, `GlyphAtlas*`, `FontStack*`, `TextQuadLayout`, `CurvedTextLayout` |
| Placement math (engine-free) | `Core/Text/Placement/` | `SymbolBox`, `SymbolCollision`, `SymbolScreenProjection`, `PolylineArcMath`, `SymbolTileBuffer`/`ShapedSymbol` (the reused per-build shape buffer), `PlacedQuad`, `SymbolBearing` |
| Tile build orchestration (off-thread) | `Unity/Text/` | `SymbolSubsystem`, `StyledSymbolTileBuilder`, `SymbolTileStore` |
| Glyph atlas texture (GPU) | `Unity/Text/` | `GlyphAtlasTexture`, `GlyphManager` |
| Per-frame placement | `Unity/Text/Placement/` | `SymbolPlacementSystem` (the god-method: project → stage → collide → emit) |
| Burst hot loops | `Jobs/` | `SymbolProjectionJob`, `CollisionJob`, `SymbolBillboardJob` |

## What works today (the milestone)

- **Point labels** (`symbol-placement: point`) and **curved along-line labels** (`line` / `line-center`)
  — per-glyph text that follows the line tangent.
- **Collision** — one global greedy all-or-nothing pass over a uniform grid; point and line labels
  compete for the same space. Runs as the Burst `CollisionJob`.
- **Fade** (A-4) — labels ease in/out instead of popping; **cross-tile identity** (A-3) keeps a label's
  opacity across a parent/child tile swap; **sticky-placement hysteresis** (A-5) resists flicker.
- **Static-frame skip** (B-1) — an idle camera with an unchanged label set re-submits the cached mesh
  and skips project/collide/build entirely.
- **Shaping** — a clean-room codepoint shaper with bidi reordering + Arabic joining, SDF glyphs from
  glyph-PBF ranges, multi-font stacks.
- **Off-main tile build** — decode + feature-extract run on the thread pool; only glyph shaping and the
  atlas upload touch the main thread (see `SymbolSubsystem.TryBeginBuild`/`RunWorkerAndHandoff`,
  driven by `TileManager`'s per-tile kick, A5b).

For the exact MapLibre `text-*` property coverage (wired vs. parsed-but-dead vs. missing), see the
feature-gap notes tracked with the symbol-text-parity work.

## Known problems

Profiled in-Editor on a pan frame, 2026-07-11 (markers below). One `LateUpdate` = **22.3 ms**, of which
labels are **~21.8 ms** — labels *are* the frame cost right now.

1. **Staging dominates the frame — `Symbol.Stage` ≈ 12.77 ms (76% of the label Tick), managed, on the
   main thread.** For every visible label, every frame, `StagePoint` / `StageCurved`
   (`SymbolPlacementSystem.cs`) re-lay-out each glyph onto the freshly-projected screen curve and build a
   collision box + render quad per glyph. Curved (road) labels dominate: per label × per anchor × per
   glyph it walks the arc for a screen point + tangent and runs ~6 transcendental ops (keep-upright
   `cos`, max-angle `atan2`/`sin`/`cos`, rotated-box `sin`/`cos`). It is all `math.*` in **managed C#**
   (no Burst SIMD/inlining) and re-runs every frame because the projection changes.

2. **The arc lookup is quadratic.** `PolylineArcMath.At()` (`Core/Text/Placement/PolylineArcMath.cs`)
   does a **linear scan over the whole polyline to find the segment — for every glyph**. Glyph
   arc-distances are monotonically increasing along a label, so this should be a single forward-walking
   cursor (O(pathVerts + glyphs)); as written it is O(anchors × glyphs × pathVertices) per label. If
   roads carry many vertices, this term alone is a large fraction of #1.

3. **Stateless rebuild every frame** → pop/blink/slide and cost. `Tick` clears and rebuilds from scratch
   with no cross-frame memory. The static-skip (B-1) hides this for a truly idle camera, but any motion
   pays full price.

4. **`CollisionJob` is `Schedule().Complete()` with no interleaved work.** A single `IJob` scheduled
   onto a worker and immediately blocked on — worker hand-off + fence for zero parallelism. `.Run()`
   (inline Burst) is strictly cheaper today; deferring the `Complete()` a frame (B-4b) is the real win.
   Note `SymbolProjectionJob` was already switched to inline `.Run()` (no schedule round-trip, no count
   threshold) — but projection is only ~0.74 ms, so that was a cleanup, not a budget win.

5. **`Symbol.Collect` ≈ 4.97 ms** upstream in `LateUpdate` — a separate second-biggest cost (label
   aggregation), also a candidate for moving off-main.

## Possible solutions (analysis)

The dominant cost (#1) is a **main-thread-occupancy** problem, not a per-op-cost problem — so the levers
are, cheapest first:

| Lever | What | Reduces | Effort |
|---|---|---|---|
| **A. De-quadratic the arc walk** | single forward cursor in `PolylineArcMath.At()` / place all a label's glyphs in one polyline pass | CPU (algorithmic), independent of thread/Burst | small, Core-only, test-covered |
| **B. Move `Stage` off the main thread** | run the managed staging loop on a worker — it builds plain data (`PlacedQuad`/`SymbolBox`/`SymbolCandidate`) and touches **no Unity API**, so it needs no Burst/nativization to relocate | main-thread occupancy (the 12.77 ms) | medium |
| **C. Nativize + Burst `Stage`** | blittable inputs/outputs → the trig-heavy loop becomes a Burst job (SIMD + inlined trig) that also runs off-main | CPU **and** occupancy — the endgame | large |

Whether A or C matters more depends on road vertex counts vs. glyph volume (grab `LastCandidateCount`
plus a typical road's `PathRender.Length` to size it). B is worth doing regardless — it is the
[off-main-thread principle](../../../../AGENTS.md) applied where the cost actually is.

**Architectural target** (ties B+C together): model placement as a **separate async flow**, not an inline
phase. Once the camera is settled, *kick* `project → stage → collide` on a worker in `Update`, let it run
alongside MapView's independent tile-pipeline work, and *join* in `LateUpdate` for `Emit` + `BuildSubmit`
(the only genuinely main-thread-only steps — Mesh write/upload). This is the same **task-parallel** model
the tile-mesh pipeline already uses (whole pipeline `.Run()` inline on a pool thread); note the codebase
has **no `JobHandle` dependency graph** anywhere (`PipelineHandle` is vestigial), so the idiom to follow
is task-parallelism, not a DAG. The managed step between the jobs (staging) is exactly why an inline
`JobHandle` chain does not apply — but on a worker it does not need to.

## Profiler markers

All under `ProfilerCategory.Scripts`, so a re-profile is self-serve:

| Marker | Covers |
|---|---|
| `MapRenderer.Symbol.SymbolTick` | the whole per-frame placement Tick |
| `MapRenderer.Symbol.Project` | umbrella: gather + projection + staging |
| ` ├ MapRenderer.Symbol.ProjectPositions` | gather world points + project (job-wait / inline `.Run()`) |
| ` └ MapRenderer.Symbol.Stage` | the managed staging loop (**the hot spot**) |
| `MapRenderer.Symbol.Collide` | `CollisionJob` schedule + complete |
| `MapRenderer.Symbol.Emit` | A-4 fade + per-slot quad-bucket assembly |
| `MapRenderer.Symbol.BuildSubmit` | billboard Mesh write + upload |
| `MapRenderer.Symbol.Collect` | (upstream, MapView) label aggregation |

Profile in a **Development Build** for real magnitudes — the Editor's job-safety-system tax inflates the
`Schedule`/`Complete` job markers on the main thread.

## Related docs

- `../../../../AGENTS.md` — repo conventions (math types, off-main principle, test workflow).
