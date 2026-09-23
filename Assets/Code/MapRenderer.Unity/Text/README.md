# Text &amp; Symbols (labels)

The MapLibre-style **text/symbol label** subsystem: turning a vector tile's label features into placed,
collision-resolved, camera-following glyphs on screen. It is a coherent module, but it
**spans all three assemblies** — engine-free primitives in `Core`, engine orchestration in `Unity`,
Burst hot loops in `Jobs`. This README is the entry point: what it does, where the pieces live, what
works, and the known problems.

> Status in one line: **it works, and it looks good in general — but the per-frame placement pass has a
> residual cost** (see "Known problems"). The design docs own the deep designs; this file owns the map +
> the problem list.

## The pipeline (data flow across assemblies)

```
TileManager's per-tile KICK ──▶ SymbolSubsystem.TryBeginBuild (main, prologue)
                 │  + RunWorkerAndHandoff (pool, inside the SAME kick task as the mesh pass)  (Unity/Text)
                 │  decode + extract features — off the main thread (thread pool), sharing the mesh
                 │  pass's decode (one decode-once entry, no parallel push feed)
                 │  shape glyphs (HarfBuzz-free CodepointTextShaper, bidi, Arabic joining)  (Core/Text)
                 │  Shape writes each raw label into a reused SymbolTileBuffer (no per-label alloc), after
                 │  the tail's one glyph-ensure suspension has already populated the atlas
                 ▼
     SymbolTileBuffer ─▶ Bake ─▶ SymbolTileBlock ──▶ SymbolTileStore   (Unity/Text)
                 │  native per-tile SoA block; per-(source,tile) sets, collected-set Version
                 ▼
      ┌── SymbolPlacementSystem.Tick(frame, plan, atlas, …)  (Unity/Text/Placement)  [PER FRAME, MAIN]
      │      1. Gather            — mirror the winner plan into native buffers
      │                                                  (SymbolGatherJob, Jobs — .Run())
      │      2. CollideHarvest    — complete LAST Tick's collision job
      │      3. Project           — cull + compact world points, project to screen
      │                                                  (CullJob, CompactJob, SymbolProjectionJob, Jobs)
      │      4. Stage             — lay each glyph onto the projected curve, build collision boxes + quads
      │                                                  (StageJob, Jobs)
      │      5. Emit              — A-4 fade + per-slot quad assembly, from LAST Tick's verdict
      │      6. Collide           — schedule THIS Tick's greedy placement   (CollisionJob, Jobs)
      │   then WorldSymbolRenderer.EndFrame — build + submit each slot's billboard mesh
      │                                                  (WorldBillboardMeshBuilder, Unity/Text/Placement)
      └──────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Where the code lives

| Concern | Assembly / path | Key files |
|---|---|---|
| Shaping, bidi, glyph atlas/SDF, font stacks | `Core/Text/` | `CodepointTextShaper`, `BidiReorder`, `ArabicJoining`, `GlyphAtlas*`, `FontStack*`, `TextQuadLayout`, `CurvedTextLayout` |
| Placement math (engine-free) | `Core/Text/Placement/` | `SymbolBox`, `SymbolCollision`, `SymbolScreenProjection`, `PolylineArcMath`, `SymbolTileBuffer`/`ShapedSymbol` (the reused per-build shape buffer), `PlacedQuad`, `SymbolBearing` |
| Tile build orchestration (off-thread) | `Unity/Text/` | `SymbolSubsystem`, `StyledSymbolTileBuilder`, `SymbolTileStore` |
| Glyph atlas texture (GPU) | `Unity/Text/` | `GlyphAtlasTexture`, `GlyphManager` |
| Per-frame placement | `Unity/Text/Placement/` | `SymbolPlacementSystem` (gather → project → stage → emit → collide), `WorldSymbolRenderer`, `WorldBillboardMeshBuilder` |
| Burst hot loops | `Jobs/Symbols/` | `SymbolGatherJob`, `CullJob`, `CompactJob`, `SymbolProjectionJob`, `StageJob`, `CollisionJob` |

## What works

- **Point labels** (`symbol-placement: point`) and **curved along-line labels** (`line` / `line-center`)
  — per-glyph text that follows the line tangent.
- **Collision** — one global greedy all-or-nothing pass over a uniform grid; point and line labels
  compete for the same space. Runs as the Burst `CollisionJob`.
- **Fade** (A-4) — labels ease in/out instead of popping; **cross-tile identity** (A-3) keeps a label's
  opacity across a parent/child tile swap; **sticky-placement hysteresis** (A-5) resists flicker.
- **Shaping** — a clean-room codepoint shaper with bidi reordering + Arabic joining, SDF glyphs from
  glyph-PBF ranges, multi-font stacks.
- **Off-main tile build** — decode + feature-extract run on the thread pool; only glyph shaping and the
  atlas upload touch the main thread (see `SymbolSubsystem.TryBeginBuild`/`RunWorkerAndHandoff`,
  driven by `TileManager`'s per-tile kick).

For the exact MapLibre `text-*` property coverage (wired vs. parsed-but-dead vs. missing), see the
feature-gap notes tracked with the symbol-text-parity work.

## Known problems

The design docs own the measured costs and the open levers:
`docs/symbol-label-perf-design.md` § "The residual: what stays expensive after the reconcile". The costs
that stay:

1. **The per-frame pass runs on a still camera — an accepted cost.** The placement layer keeps state
   across frames: a fade opacity per fade id (`_fadeOpacity`), and a collision verdict that one Tick
   schedules and the next harvests (`docs/labels-and-symbols-design.md` § "Track A — placement state
   machine"). A static-frame skip (B-1) is rejected, because it trades this cost for a motion-keyed cost
   cliff (`docs/symbol-label-perf-design.md` § "Constraints (non-negotiable)"). So an idle camera still runs
   project → stage → collide.

2. **The gather's mirror rebuild is all-or-nothing.** Under continuous motion the winner set changes on
   nearly every frame, so the mirror rebuilds on nearly every frame (`MirrorRebuildCount` shows the rate).
   A one-tile change rebuilds every winner's slice.

## Profiler markers

All under `ProfilerCategory.Scripts`, so a re-profile is self-serve:

| Marker | Covers |
|---|---|
| `MapRenderer.Symbol.Gather` | mirror compaction (`SymbolGatherJob`); a same-version frame measures a memo hit |
| `MapRenderer.Symbol.SymbolTick` | the rest of the per-frame placement Tick |
| ` ├ MapRenderer.Symbol.CollideHarvest` | completing LAST Tick's scheduled collision job |
| ` ├ MapRenderer.Symbol.Project` | umbrella: point gather + projection + staging |
| ` │  ├ MapRenderer.Symbol.GatherPoints` | the cull scan (`Gather.Cull`) and the compaction (`Gather.Compact`) |
| ` │  ├ MapRenderer.Symbol.ProjectPositions` | `SymbolProjectionJob` |
| ` │  └ MapRenderer.Symbol.Stage` | `StageJob` |
| ` ├ MapRenderer.Symbol.Emit` | A-4 fade + per-slot quad assembly (`EmitLoop`, `EmitDecay`) |
| ` ├ MapRenderer.Symbol.Collide` | grid sizing + `CollisionJob` schedule (the wait is in `CollideHarvest`) |
| ` └ MapRenderer.Symbol.EndFrame` | billboard mesh build + submit (`WorldSymbolRenderer.EndFrame`) |
| `MapRenderer.Symbol.Collect` | (upstream, MapView) loaded-tile reconcile + symbol build pump |

Profile in a **Development Build** for real magnitudes — the Editor's job-safety-system tax inflates the
`Schedule`/`Complete` job markers on the main thread.

## Related docs

- `../../../../AGENTS.md` — repo conventions (math types, off-main principle, test workflow).
