# Labels & symbols — design & SSOT

The symbol/label subsystem: how a label travels from tile bytes to a drawn (or culled) glyph, and the design
of each part — placement smoothness, curved along-line text, and projection (globe) support. Code lives in
`MapRenderer.Unity/Text/` (subsystem, per-frame placement) and `MapRenderer.Core/Text/` (engine-free shaping,
layout, metrics).

1. **[Pipeline flow](#1-pipeline-flow)** — the two-clock architecture; tile lifecycle → per-frame placement → cull → fade.
2. **[Smoothness & robustness](#2-smoothness--robustness)** — pull/reconcile, cross-tile identity, the fade state machine, the perf tracks.
3. **[Curved along-line text](#3-curved-along-line-text)** — `symbol-placement: line` / `line-center` (mostly landed).
4. **[Projection support](#4-projection-support-globe-ready-labels)** — globe-ready labels under any `IProjection` (designed, not started).
5. **[Icon support](#5-icon-support-sprite-symbols)** — `icon-image` sprite symbols; the sibling of text under one anchor (in progress).
6. **[Map-aligned line icons + `icon-rotate`](#6-map-aligned-line-icons--icon-rotate-p-b)** — the third icon emit shape (P-B, landed).
7. **[Non-centred icon+text pairing](#7-non-centred-icontext-pairing-p-a--landed)** — a pair is an *instance*, not a coincidence (P-A, landed).

---

# 1. Pipeline flow

How a symbol label travels from "which tiles are on screen" to a drawn glyph. The system has **two clocks**: a
slow, event-driven **tile lifecycle** (fetch/build/cache, runs only when the cover changes) and a fast
**per-frame placement loop** (projects + collides + draws every frame). Keeping camera-dependent work out of
the slow clock — and slow work out of the fast clock — is the spine of the whole design.

## 1.1 Tile lifecycle (the slow clock) — data arrives

Driven by `TileManager`, which owns the loaded-tile set (`_loaded`):

```
TileManager.Tick(cameraProperties, selectionConfig)          // once per frame, but mostly idle
  ├─ CoverSelect      : pick the visible tile cover for this camera (z/x/y set)
  ├─ Request/Release  : fetch newly-covered tiles, release departed ones (kept-warm in a cache)
  └─ per-tile kick    : SymbolWorkerFactory.TryBeginBuild(source, tile)   ── kick ──►  SymbolSubsystem
                          → RunWorkerAndHandoff(decode)
```

`TileManager`'s per-tile mesh-build kick (`KickMeshBuild`) drives the symbol pass now, not a push event:
it asks `SymbolWorkerFactory` (an `ISymbolTileWorkerFactory`, implemented by `SymbolSubsystem`) for an
`ISymbolTileWorkerPass` via `TryBeginBuild`, then hands that pass the decoded tile through
`RunWorkerAndHandoff`. `SymbolSubsystem` does **not** build inline there — `RunWorkerAndHandoff` **enqueues**
the MVT bytes. The
decode → feature-extract (off the main thread) → glyph-shape → atlas-append is drained a bounded number per
frame by `PumpBuilds`. Decode + per-layer extract run through
`Rendering.Tile.Processing.TileLayerProcessorRunner.RunSymbolWorkerPass` (the symbol cadence's own decode-once
worker-pass entry) over one `TileSymbolLayerProcessor` per symbol style layer, each writing into the build's
shared label list; the main-thread tail (`RunTailAsync`) first collects every processor's required glyph
ranges and awaits them (the build's ONE suspension point), then runs the per-layer `CompleteOnMain` loop —
synchronous, no atlas mutation — started by `PumpBuilds`' once-per-frame drain of the pool→main
`ConcurrentQueue` handoff (`_handoffQueue`). Finished per-tile labels land in
`SymbolTileStore`, which:

- keeps an **active** set (in cover) and a **cached** set (out of cover, kept warm for cache-hit re-entry);
- bumps a monotonic **`Version`** on every set-changing mutation — the key the fast clock uses to know whether
  anything actually changed.

**Per-label build isolation.** One label whose build throws must NOT blank the whole tile's symbols. The
per-label body in `StyledSymbolTileBuilder.Shape` (shape → layout → `output.Add`) is wrapped in a
`catch … when (!(ex is OperationCanceledException) && !ct.IsCancellationRequested)` that skips + counts the one
label (`SkippedSymbolCount` telemetry, throttled once-per-session warn) and lets the rest build + commit. The
trigger this fixes is the deferred single-run bidi: `CodepointTextShaper` throws on mixed strong-direction text
(RTL+LTR), which the liberty `["concat", name:latin, " ", name:nonlatin]` place field produces for every
Arabic/Hebrew-region place — without isolation every low-zoom world tile (which spans an RTL region) rendered
**zero** symbols. **Known gaps (conscious, not fixed):** (1) **Pass 1** (glyph fetch/`GlyphPbfDecoder.Decode`) is
still unguarded — a corrupt glyph-PBF for one label would blank a tile via the same mechanism; out of this
stage's Pass-2/bidi scope. (2) A **pre-cancelled `ct`** reaching a Pass-2 label that throws a *genuine* (non-OCE)
bug propagates (whole-build drop) rather than skips — nil observable impact (a cancelled build's results are
discarded by `RunTailAsync`'s post-loop `ct` gate anyway), untested. (3) The Pass-2 `when` OCE-exclusion is
inspection-verified, not test-pinned (the cancel-safety tooth throws in unguarded Pass 1). Full UAX #9 bidi
(actually *rendering* mixed-direction labels) remains a separate deferred feature.

**Single-world anchor clip (point labels).** `SymbolFeatureExtractor` now drops POINT features whose tile-local
anchor falls outside the tile's half-open `[0, extent)` bounds (per-axis). Low-zoom source tiles carry ±360°
world-copies / buffer duplicates of place-label anchors in the point layer (`TileId.ToLonLat` does no wrapping,
so an out-of-range `px` projects to a render position exactly one world-width off); the mesh path is clipped to
the tile server-side, but symbols were not — so on Mercator every label rendered as three copies one world-width
apart while the base map stayed single (measured: 108 records = 36 labels × 3). Half-open so a shared-edge anchor
is owned by exactly one tile (`x==extent` in tile T is `x==0` in T+1). **Known gaps (deferred, recorded at merge
from the Stage-B dual review):** (1) the **LINE/curved** branch's *anchors* are now clipped on the same
half-open predicate (§6.3 KL-A1), but its **path vertices** are not: the extractor's LineString loop and
`ProjectPath` still call the same unguarded `ToLonLat` on every decoded vertex, so if the source ever ships
an out-of-bounds LineString point the ±360° world-copy still fires for the curved *geometry*. The finite-sheet
camera (Mercator bounded pan/zoom) keeps off-world space off-screen so it is not *drawn*, but a displaced path
vertex would still distort the curved layout it carries, so this is a data fix still owed — narrowed by the
anchor clip, **not** closed by it. (2) the regression test
(`Extract_PointPlacement_ClipsOutOfBoundsAnchorsToTile`) is **synthetic** (hand-encoded MultiPoint), pinning the
`[0,extent)` boundary logic but not the real OpenFreeMap z0 place layer's actual coordinates — a real-tile smoke
check is worth adding to the epic.

So a `ShapedSymbol` exists per `(tile, feature)` once its tile's bytes are decoded and shaped, held in the
build's `SymbolTileBuffer` — one dense `Symbols` list plus the pooled columns (`Quads`, `Glyphs`, `Anchors`,
`Path`/`PathUp`) each symbol's own span indexes into. It carries `AnchorRender` (point) or a `Path`/`Anchors`
span (curved), its `TileKey` (packed z/x/y), text, material index, and style-evaluated sizes. Nothing here
depends on the camera.

## 1.2 Per-frame driver — `MapView.LateUpdate`

Runs in `LateUpdate` so it sees this frame's committed camera (input mutates the camera in `Update`). The order
is load-bearing:

```
1. Camera.SyncToCamera()                        // commit this frame's pose
   cameraProperties = Camera.CurrentProperties  // ONE snapshot — tiles + symbols share it
   sceneFrame       = BuildSceneFrame(...)      // floating origin: origin = look-at, + ENU rebase

2. Layers.ApplyZoom(...)                        // zoom uniforms, px→device basis, before anything else moves
   TileManager.InstancedRebuild(sceneFrame)     // place tile meshes relative to the origin
   TileManager.Tick(cameraProperties, ...)      // the slow clock (§1.1) — cover select + request/release

3. if SymbolSubsystem.HasSymbolLayers:
     TileManager.CollectLoadedTileKeys(scratch)         // PULL the current loaded set
     SymbolSubsystem.ReconcileLoadedTiles(scratch, now) // release departed / restore cache-hit symbols
     SymbolSubsystem.PumpBuilds()                        // start ≤N queued builds, coalesce one atlas upload
     plan = SymbolSubsystem.CurrentBatch(sceneFrame, minCoverage, now)  // this frame's winner plan (§1.5)
     SymbolPlacementSystem.Tick(sceneFrame, plan, atlas, dt, layerMaterials, iconTexture)
   else:
     nothing to place — a style with no symbol layers skips this step entirely.
```

Tiles and symbols are placed against the **same** `sceneFrame` and `cameraProperties` snapshot, so they can
never diverge.

## 1.3 The winner plan — `SymbolSubsystem.CurrentBatch()`

The bridge between the two clocks: `CurrentBatch` returns a `SymbolGatherPlan` — one entry per cross-tile
dedup winner, in final render order, pointing at its pre-baked `SymbolTileBlock` symbol via `BlockId`/
`LocalIndex`, plus three per-frame overrides (`Departing`, `CoverageFading`, `Dropped`) applied at plan-fill
time. This replaces the fully-built Structure-of-Arrays batch this section used to describe: gathering the
real per-symbol SoA now happens later, inside `SymbolPlacementSystem`'s native mirror.

The plan is rebuilt **every frame** from the reconciled tile set (§1.1; the reconcile itself is now async —
`docs/labels-async-reconcile-design.md`), reusing its native lists (allocation-free once warm). Between the
cross-tile dedup and the plan fill, `CurrentBatch` also runs the tile-coverage pre-cull (§1.5) — it now
**masks** rather than compacts: every collected winner stays resident (`WinnerCount` counts them all) and a
`Dropped` winner is hard-skipped downstream, in `SymbolPlacementSystem.GatherSymbolPoints`.

The plan carries a **`WinnerSetVersion`**, bumped on every front-content change (reconcile swap / restyle /
dispose); `SymbolPlacementSystem` mirrors it into native buffers only when the version changes.

## 1.4 The per-frame placement loop — `SymbolPlacementSystem.Tick`

Everything camera-dependent lives here, over reused buffers (no per-frame GC). Stages:

```
Tick(sceneFrame, plan, atlas, dt, materials, spriteTexture)
 │
 ├─ PmGather        → GatherIntoMirror(plan)  : mirror the plan's (§1.3) native winner rows into per-Tick
 │                                               buffers, memoized on WinnerSetVersion
 ├─ PmCollideHarvest → HarvestCollision()     : complete LAST Tick's scheduled collision job and re-key its
 │                                               survivors by FadeId (B-4, Track B below)
 ├─ PmProject
 │   ├─ PmGatherPoints
 │   │   └─ GatherSymbolPoints(...) : flatten un-Dropped winners' world points; a culled winner's offset
 │   │                                is set to -1 (B-3 distance cull, then S3 horizon cull — the
 │   │                                tile-coverage pre-cull already ran upstream, in CurrentBatch, §1.5)
 │   ├─ PmProjectPositions
 │   │   └─ ProjectSymbols(...)     : Burst SymbolProjectionJob — world → screen/depth for all at once
 │   └─ PmStage
 │       └─ RunStageJob(...)        : Burst StageJob — collision boxes + rotated-glyph quads (skips any
 │                                    winner whose gather offset is -1)
 ├─ PmEmit  (reads LAST Tick's collision verdict — this Tick's is only scheduled below, not yet complete)
 │   ├─ PmEmitLoop  → A-4 fade + per-slot quad assembly : ease each candidate's opacity toward 1 (placed
 │   │                last Tick) / 0 (dropped/suppressed); emit its quads via WorldSymbolRenderer.Emit
 │   └─ PmEmitDecay → DecayUnseenFadeSymbols(...) : ease out any fade id absent from this Tick
 └─ PmCollide → ScheduleCollision(...)  : schedule THIS Tick's Burst CollisionJob (greedy, sort-key-driven,
                                          uniform-grid) — its verdict is harvested next Tick's PmCollideHarvest

then, every Tick regardless: WorldSymbolRenderer.EndFrame(...) builds + submits every non-empty material
slot's mesh (Graphics.RenderMesh), hiding any slot left empty this Tick.
```

Five fade-out triggers happen **before** any projection/staging/collision, classified by `GatherTrigger` and
applied in `GatherSymbolPoints`:

1. **Departing** (`SymbolDeparting`): the winner's tile is leaving cover (§1.6). Fades out unconditionally.
2. **Coverage-fading** (`SymbolCoverageFading`): the winner's tile just dropped below the §1.5 coverage
   threshold and is easing out over the grace window before it is dropped from the plan entirely. Set by the
   §1.5 pre-cull; a still-loaded, in-cover tile (distinct from `SymbolDeparting`'s leaving-cover meaning).
3. **B-3 distance cull** (`GatherTrigger.Distance`): drop an individual symbol beyond a threshold fraction of
   the camera's far plane (`SymbolMaxDistanceFraction × CurrentFarMetres`, computed once per Tick).
4. **Zoom** (`GatherTrigger.Zoom`): drop a symbol outside its own live zoom range.
5. **S3 horizon cull** (`GatherTrigger.Horizon`): drop a symbol whose anchor is hidden behind the globe's own
   bulk (no-op under a planar projection).

A triggered winner is **not** hard-dropped while its fade is still alive: gather keeps STAGING it (re-projected
to its live position) and forces its opacity toward 0 in emit — so it eases OUT in place instead of popping.
Only once fully faded does gather set its offset to `-1` (the Burst stage job's "skip"). This soft-cull is the
single mechanism behind all five; no job changed to add any of them.

**`SymbolPlacementSystem`'s own identity** (moved from its class doc, UMR-118): it is a plain class (NOT a
MonoBehaviour) — the "placed every frame" path (`ARCHITECTURE.md` §"Two geometry classes"), structurally
distinct from the static per-`(tile,layer)` `ITileRenderBackend` meshes it never touches
(`SymbolPlacementStructureTests` grep-checks this). Every on-screen symbol's screen-space AABB (`SymbolBox`,
`text-padding` applied) runs through `SymbolCollision.SelectSurvivors` — greedy, sort-key-driven,
permutation-invariant selection — over reused arrays (no per-frame managed allocation). This Tick's collision
is *scheduled*, not run inline (see the pipeline above): the world emit reads the PREVIOUS Tick's
already-harvested verdict, and the current Tick's own verdict is harvested at the START of the next one.
A slot that produces no quads this Tick (no atlas, empty batch, everything culled/suppressed) is HIDDEN, not
left drawing stale content — the mirror image of the pre-E2 Editor blink; `WorldSymbolRenderer.EndFrame` owns
this per-slot show/hide.

## 1.5 Tile-coverage pre-cull  *(IMPLEMENTED — moved ahead of the SoA build)*

A coarse step *before* the per-label pipeline: skip a tile's labels entirely when the tile covers less than ~N%
of the screen. Small-on-screen tiles are the horizon pile-up under tilt — most of their labels get
collision-culled anyway, so gather → project → stage → collide on them is wasted work. Dropping them whole
stabilizes per-frame label cost with barely any lost information. Complements the per-**label** B-3 distance
cull (a horizon *radius*): this is a per-**tile** *screen-area* metric, which catches the tilt-foreshortened
slivers a radius keeps.

**Where it runs.** Originally the cull ran post-build (flagged in `SymbolPlacementSystem.Tick`, applied in
gather) to preserve a per-batch version cache that has since been removed (`cb0786c7`) — once that cache was
gone, nothing justified paying for the SoA build (glyph/quad copies, sRGB→linear, fade-id hashing) on a tile
whose labels were about to be discarded. That original rationale still explains why the cull runs where it
does, but the mechanism it runs inside has since moved off-main (`docs/labels-async-reconcile-design.md`): the
cull now runs in `SymbolSubsystem.CurrentBatch`, **after** picking up the async reconciler's cross-tile dedup
and **before** filling the gather plan:

```
CurrentBatch(frame, minCoverage, now)
    PickupCompletedReconcile(); ScheduleReconcileIfDirty()   // pick up the off-main A-3 dedup (§1.1)
    SymbolTileCoverageFilter.ClassifyActive(                 // ◄── pre-build cull (this section):
        blockTileKeys, frontResult.BlockId, frontResult.IsDeparting,    //   PER BLOCK — one classification per
        projection, frame.SceneOriginRender, viewProj, viewportLogicalPx, //  (source, tile), fanned out to
        frame.Rebase, minCoverage,                                        //  every winner sharing that block
        _coverageAbovePrev, _coverageAboveThisFrame, _coverageDepartingUntil, _coverageFadingTiles,
        now, DepartingGraceSeconds, _tileDecisions, _blockDecision, _planDecision, out culled)
    swap(_coverageAbovePrev, _coverageAboveThisFrame); PurgeExpiredCoverageDeadlines(now)
    _gatherPlan.Build(frontResult.BlockId, frontResult.LocalIndex,      // MASKS Dropped winners in place — every
        frontResult.IsDeparting, _planDecision, frontResult.OrderedBlocks, frontSetVersion) // winner stays
                                                                         // resident (§1.3); flags CoverageFading
```

Cull **after** dedup, not before/inside it: culling pre-dedup would change dedup *winners* — a <5%-coverage
child tile culled ahead of the A-3 pass would let its >5% parent win the finest-zoom-wins tiebreak
(`z = TileKey>>44`) and render a symbol that is hidden today. Cull-after-dedup preserves winners exactly and
keeps `SymbolTileStore`/`CollectInto` untouched.

**Scope: active winners only.** A departing winner (§1.6, a tile leaving cover retained for a fade-out) is
left at `Keep` unconditionally and never classified — a departing-only block touches none of this filter's
cross-frame state (deadline stamp, above-set, fading-set), regardless of its own tile's coverage.

The filter itself is an engine-free Core seam, **`SymbolTileCoverageFilter.ClassifyActive`** — Core, so it
compiles into `Tools/core-tests` (`SymbolTileCoverageFilterTests`) for a fast, RED-verifiable loop. It
classifies **per BLOCK, not per winner**: every winner produced from one baked `(source, tile)` block shares
that block's tile key, so the classifier resolves each unique tile's coverage ONCE (`SymbolTileCoverage`,
same metric as before, via a reused per-call cache) and fans the decision out to every winner sharing that
block — O(blocks) tile work plus an O(winners) fan-out. Each tile is classified **Keep / Fade / Drop**:

- **Keep** (`≥ threshold`): stays active; the A-4 fade eases it *in* if it is new. Clears any live fade deadline.
- **Fade** (`< threshold` but was visible): every winner on the tile stays resident in the plan, MASKED
  `CoverageFading` → gather's trigger 2 (§1.4) eases it *out* in place instead of popping.
- **Drop** (`< threshold`, steady/never-visible/grace-expired): every winner on the tile stays resident too —
  D1's masking model keeps `WinnerCount` unchanged — but is MASKED `Dropped` and hard-skipped downstream,
  in `SymbolPlacementSystem.GatherSymbolPoints`, which is where the perf win is realized.

**Fade-then-drop, not pop.** A tile crossing below threshold must fade out like every other cull (§1.4), so
the filter keeps a small amount of reused, alloc-free cross-frame state on the subsystem: `_coverageAbovePrev`
/ `_coverageAboveThisFrame` (ping-pong `HashSet` of tiles that were ≥ threshold, self-bounding — each set is
cleared+refilled and swapped every call) and `_coverageDepartingUntil` (tileKey → wall-clock fade-out
deadline). A **fresh** above→below crossing (`_coverageAbovePrev` contains the tile) stamps `now + grace`
**once**; while below with a live deadline it keeps Fading; once `now ≥ deadline` — or if it was never visible
— it Drops. Crossing back above clears the deadline (fade in), and a later re-crossing re-arms it. Grace
(`DepartingGraceSeconds` = fade duration + 0.2 s) is deliberately > the fade, and the deadlines self-purge
(`PurgeExpiredCoverageDeadlines`) so an unloaded tile leaves no stranded state. The wall-clock `now` is threaded
from `MapView.LateUpdate` (`Time.timeAsDouble`, shared with `ReconcileLoadedTiles`).

```
ScreenCoverage(4 render corners, sceneOrigin, viewProj, viewport)
    → project each corner to screen (SymbolScreenProjection.TryProjectPoint)
    → if ANY corner is behind the near plane (or viewport degenerate): return +∞   ("never cull")
    → else: |shoelace(4 screen corners)| / viewportArea                            (fraction of screen)

IsCulled(coverage, minCoverage)  →  minCoverage > 0 && coverage < minCoverage
```

`minCoverage ≤ 0` (or a null projection) disables the cull (the kill-switch, same convention as before); `+∞`
is never below a finite threshold, so a near-plane-straddling tile is always kept. Default
`MapViewConfig.SymbolTileCoverageCull = 0.05` (threaded through `MapView.LateUpdate` → `CurrentBatch`) — a
maintainer eyeball-tunable. Telemetry: `SymbolSubsystem.LastTileCoverageCulledCount` (the Drop count,
moved from `SymbolPlacementSystem`) + `SymbolPlacementSystem.LastCoverageFadingCulledCount` (the fully-faded
coverage-fade count, the gather-side companion).

**Behaviour vs the old post-build cull:** the *rendered set* is unchanged (a tile below threshold is hidden
either way; cull stays after the A-3 dedup so winners are preserved), and static-frame GPU snapshots are
byte-identical (no re-bake). The one change is the **transition is preserved**: a tile crossing below threshold
**fades out** over the grace window (via `CoverageFading`, §1.4) exactly like the old gather cull did,
rather than popping — only a tile that was *never* on screen is dropped silently (nothing to pop). This was a
correction over a first (pop) cut of this stage; matches the rest of the label system, all of which fades.

**Deferred / follow-ups:** a green/red survived-vs-culled debug overlay (tune the threshold by eye);
tilt-scaling the threshold; explicit hysteresis *on the threshold itself* (distinct from the fade grace);
culling ahead of the A-3 dedup (would change dedup winners — see above). Telemetry is surfaced through
`SymbolStoreTelemetrySnapshot.CoverageDroppedSymbols` / `SymbolPlacementTelemetrySnapshot.CoverageFadingSymbols` → the `MapTelemetryPanel` (beside the
distance cull), so the threshold is tunable by watching the live drop/fade counts. `viewProj` and the logical
viewport are single shared definitions (`SymbolPlacementSystem.ViewProj(Camera)` + `MapCamera.ViewportLogicalPx`)
read by both `CurrentBatch` and `Tick`.

## 1.6 Retain-as-departing — fading a tile out when it leaves cover

The B-3 distance / S3 horizon culls fade a winner still *in* the plan (§1.4; the tile-coverage pre-cull, §1.5,
now masks Fade/Drop instead of popping — it runs before the plan is even filled). A normal **tile unload** is
different: the tile leaves cover, its symbols leave the collected set, the plan rebuilds without them, and they
would pop. The fix keeps them in the plan for a grace window.

When a tile leaves cover, `SymbolTileStore` releases it to the **warm cached side** and, if a grace window
is set, stamps it **departing** (`key → wall-clock expiry`). While departing:

- `CollectInto` still emits its winners — appended **after** the active ones — and reports the split via
  `out activeCount`. A departing point winner whose cross-tile identity is already claimed by an active winner is
  skipped (the active copy shows → seamless tile-to-tile transfer, no fade).
- `SymbolGatherPlan.Build` copies each winner's `IsDeparting` flag straight into its `Departing` mask, which
  gather treats as an unconditional fade-out trigger.
- The store **purges** the stamp once `now ≥ expiry`. The grace (`DepartingGraceSeconds`, derived from
  `FadeDurationSeconds`) exceeds the fade, so a purge only ever drops an already-invisible winner. Re-entry
  within grace clears the stamp (via `RemoveCached`, the single "leaves cached" chokepoint that keeps the
  invariant *departing ⊆ cached*) → the winner fades back in.

Because a departing winner is a **real plan entry**, it re-projects to its live position every frame — so it
eases out correctly even while the camera pans. The wall-clock is threaded from `MapView.LateUpdate`
(`Time.timeAsDouble`). Telemetry: `LastDepartingCulledCount`, `DepartingTileCount`.

**Known limitation (accepted):** the fade-out **borrows the cache's warm copy** of the labels, so it is gated on
the prepared-mesh cache being enabled — cache off ⇒ `Release` drops the labels immediately (grace set to 0), so
an unloaded tile still pops. The cache is on by default, so production and the demo get the fade. **Decouple
path if it matters later:** the fade only needs the labels for the grace window (~0.5 s), independent of the
cache-hit retention (minutes), so `_departing` could *own* the released entry for the grace window when the
cache is off (a second, short-lived retention path) instead of borrowing the cached FIFO. Small change + a test;
not done.

---

# 2. Smoothness & robustness

The placement layer was originally **stateless**: `SymbolPlacementSystem.Tick` cleared everything and rebuilt
projection → collision → billboard quads from scratch every frame, with labels aggregated per `(source, tile)`
and no cross-frame or cross-tile memory. That produced four user-visible problems:

1. **Pop, not fade.** A label that stops being placed (collision, or its tile leaves cover) vanishes instantly.
2. **Blink when idle.** Even with zero camera motion, the per-frame rebuild lets an async build landing / a
   transient tile release / a hair of collision-input drift flip a survivor for a frame → flicker.
3. **Line labels slide on zoom.** `symbol-placement:line` anchors were fixed *screen-px* distances from the
   projected line start, so changing the projected length moved every anchor to a different world point.
4. **Cost.** ~20k symbols at some zooms → `Project` ~20 ms **every frame, even idle**.

Plus two structural issues: **fragile push-callbacks** (lifecycle mirrored via `Released`/`Restored` callbacks —
ordering, reentrancy, a hand-threaded cache-vs-evicted bit) and a **single glyph atlas** that overflows (one
fixed 4096², a CJK capacity limit).

The spine (**track A**) is one idea: the placement layer becomes a **state machine keyed by a stable cross-tile
symbol id** and **owns each label's on-screen lifetime** — it decides what stays drawn and for how long,
independent of which tiles are loaded. **B** is performance, **C** is capacity — sequenced apart. The
`A-1 → A-2 → A-3 → A-4 → B-1` spine has landed; `B-2`/`C-1` and the future improvements below are the remainder.

## Track A — placement state machine

**A-1 — Pull / reconcile foundation (retire the push-callbacks).** Split *data delivery* from *lifecycle*.
Membership is **pulled**: each frame the subsystem asks `TileManager` for the current loaded `(source,tile)` set
+ a **version stamp** (bumped only when the set changes) and reconciles — a tile in the set with no labels yet →
build; a tile-with-labels no longer in the set → hand to the fade-out path (A-4), not an instant drop;
present-and-built → no-op. Self-healing (a missed change is corrected next frame) with no reentrancy. Bytes stay
a push, but only as a pure *data* event (`SymbolTileBytesReady`); the fragile *lifecycle* callbacks are deleted.
The `SymbolTileStore` survives as the reconcile backing store — this A-1 description is the foundation the
reconcile has since moved off-main onto (`docs/labels-async-reconcile-design.md`); the pull/no-reentrancy
invariant it states still holds. *Teeth:* reconcile is idempotent; a removed tile is no longer collected; the
zoom-out-then-in case works without any release callback.

**A-2 — Stable WORLD line anchors (fix labels sliding on zoom).** Compute anchors **once at build time in
tile/world space** — positions along the line spaced by `symbol-spacing` at a reference scale — and carry each
as a world position. Per frame, project each *anchor* and lay the glyphs out around it by walking only the
*local* neighbourhood of the projected line. The anchor no longer slides because it is a fixed world point, not
a screen offset. `line-center` = one anchor at the world midpoint. *Teeth:* a line label's world anchor is
invariant under a zoom change. Prerequisite for cross-tile line identity (A-3): a stable world anchor → a stable
id.

**A-3 — Cross-tile symbol identity + dedup (seamless no-op replacement).** A stable cross-tile id =
`hash(quantize(worldAnchor), resolvedText, layerId)`, where `worldAnchor` is `AnchorRender` (pre-RTC
world/mercator — the same for a geo point at any zoom), quantized to ~1px-at-max-zoom so tiny reprojection
differences between zoom levels collapse to one id. This now runs off-main, inside `SymbolReconciler`
(`Dictionary<DedupKey, DedupEntry>`, keyed the same way as `CrossTileSymbolKey.For` — see
`docs/labels-async-reconcile-design.md`); at **collection** time, when the same id appears in parent + child
tiles, pick one deterministically (finest zoom, then lowest tile key). Same id across a tile swap ⇒ the winner
**persists** ⇒ opacity stays 1 ⇒ no fade cycle, a true no-op — and the symbol count drops by the duplicate
factor. Line labels in v1: key each *placed world anchor* as its own id; if noisy, fall back to excluding line
labels from dedup. *Teeth:* two overlapping loaded tiles carrying the same point symbol → one placement; the
id is stable across a simulated parent→child swap.

**A-4 — Placement state machine + fade.** The placement layer holds a persistent record per cross-tile id:
`{ opacity, lastPlaced, lastScreenPlacement }`. Each `Tick`: gather this frame's candidates (post A-3 dedup) →
match to records by id → ease each record's opacity toward `(present && collision-placed) ? 1 : 0` over a fixed
**fade duration** (~300 ms; wire `symbol-fade-duration` later) → **emit every record with opacity > 0** using
its last known placement, *even if its tile is no longer loaded* → drop records that reach 0 and are absent. So
a label whose tile just left cover keeps drawing at decaying alpha — the placement layer **owns the on-screen
lifetime** (why fade state lives here, not in the tile-keyed store). Cheap on the GPU: `PlacedQuad.Color.w` is
already the per-vertex alpha the shader emits — no shader/vertex-format change. *Teeth:* a removed label's alpha
decays over the fade window and reaches 0; a re-appearing id within the window keeps its opacity (no re-fade).
Also masks the idle blink (a one-frame flip becomes a sub-perceptual alpha step).

## Track B — performance

**B-1 — Static-frame skip *(landed)*.** When the view-projection, the reconcile version stamp, **and** the fade
state are all unchanged since last frame, reuse the last frame's built billboard buffers verbatim — skip
project/collide/build entirely. Kills the idle `Project` and removes the idle blink at the root. "No active
fades" is part of the unchanged test, so fades still animate.

**B-2 — Burst-jobified projection *(landed, `SymbolProjectionJob`)*.** For the moving case at ~20k labels: gather candidate world anchors into a
`NativeArray<double3>` and project (+ cull) them in a Burst job → screen + mask; the managed greedy collision
stays (it is serial). Attacks the per-anchor matrix-mul cost that dominates `Project` when the camera moves.

**B-3 — Horizon / far-distance label culling *(landed, `CullJob`)*.** In a tilted view the far half of the frustum compresses a huge
ground area into a thin horizon band, where labels pile up, get collision-discarded, and jitter (projection is
numerically unstable as depth → the far plane). Cull labels near/beyond the horizon **before** projecting:
prefer (a) a **world-space ground distance** from the look-at beyond a pitch-dependent threshold (pre-projection
— skips the matrix mul); backstop (b) a **far-depth / above-horizon screen cull** after projecting. Labels want
their own, tighter distance cut than the tile far-plane policy (`GeometryAwareFarPlane` / `RaySphereFarPlane`) —
they stop being legible well before tiles stop drawing (MapLibre has an analogous pitch-scaled fade of distant
symbols). Pairs with B-1: fewer candidates per frame *and* the static-skip avoids redoing them when idle.
*(This is the per-label radius companion to the per-tile coverage cull in §1.5.)*

**B-4 — Pipelined (deferred) placement — decouple the decision from the render *(landed, `SymbolPlacementSystem`)*.**
Move the expensive collision/culling **off** the main-thread critical path into the frame "loophole" (the
worker-thread time after LateUpdate, while the render thread submits) by making the placement DECISION one frame
stale — the MapLibre placement/render split mapped onto Unity's job timeline. Every frame (cheap, on-path):
project the *current* label set to screen (positions must be live) and emit the survivors **decided at the end
of the previous frame**, through the A-4 fade. End of frame (loophole, off-path): `Schedule` the collision over
THIS frame's projected boxes as a Burst job and don't `Complete()` it until next frame's LateUpdate — it decides
visibility for frame N+1 overlapping the render thread. Holds because the camera moves a hair per frame (a
1-frame-stale survivor set over *current* positions matches the fresh answer almost always) and A-4 absorbs the
residual. Caveats: collision is serial (ONE Burst job, not a parallel fan-out); needs native/blittable collision
data (share B-2's); a camera teleport forces a synchronous recompute. Composes with B-1 (skip when idle) and B-2
(cheap parallel projection).

**A-5 — Collision fighting / placement hysteresis.** Labels flicker on a subtle camera change — a hair of motion
flips which of two near-tied candidates wins the greedy collision, like z-fighting. A-4 eases the alpha flip and
B-1 skips fully static frames, but a *slowly-moving* camera still re-runs collision every frame and can
oscillate a marginal pair. Fix: **sticky placement** — bias a candidate that was placed last frame to stay
placed (a small bonus in the placement order, or a "kept" flag that only yields to a clearly-higher-priority
intruder, not a near-tie), keyed by the A-3/A-4 cross-frame identity. This deliberately feeds placement history
back into collision (the one place the "downstream of `SelectSurvivors`, never fed back" rule is relaxed) — tune
with a hysteresis margin, not an absolute lock, so a stale winner can't block a genuinely higher-priority label.

## Track C — capacity

**C-1 — Multiple glyph atlases.** One fixed atlas overflows on large scripts. Give `GlyphAtlasEntry` an **atlas
index**; when an atlas fills, spill new glyphs into an additional atlas. Rendering: either a per-quad atlas
index → `Texture2DArray` layer (one vertex-format field + a tiny shader change), or partition draws by
`(material slot × atlas)` like the existing per-layer material slots. The **only** stage needing a
vertex-format/shader change, and a capacity fix (not smoothness) — sequence it independently.

---

# 3. Curved along-line text

`symbol-placement: line` / `line-center` — per-glyph text that follows a line feature's geometry (the real
subsystem, not a straight-block approximation). Clean-room from the public MapLibre Style Spec. **Status: landed**
(sub-slices B1–B4 + max-angle/keep-upright done); the remaining items are visual-verify.

**What it does (spec-confirmed):** `line` repeats the label along the line at `symbol-spacing` intervals (pixels,
default 250); `line-center` places exactly one label at the line center. Each glyph is placed at its along-line
arc position and **rotated to the local tangent**. `text-keep-upright` (default true) flips the walk direction so
text never renders upside-down (without it ~half of all line labels are upside-down). `text-max-angle` (degrees,
default 45) drops a label whose adjacent-glyph tangent change exceeds it. Line-placed text is single-line
(`text-max-width` does not apply).

**Inherently screen-space (load-bearing).** `symbol-spacing` and the tangent are pixels/screen-space. A
build-time fixed anchor count would make spacing drift with zoom (a bug, not an approximation), and a build-time
render-space tangent is only correct at bearing-0/no-tilt. So placement — project the line, walk it in pixels,
place+rotate each glyph — happens **per frame** in `SymbolPlacementSystem`, exactly like point-label projection.
Build time only extracts the line geometry and shapes the text.

```
BUILD (Core, per tile, once)                    PER FRAME (SymbolPlacementSystem, screen-space)
─────────────────────────                       ────────────────────────────────────────────
extract LineString feature                      project the render-space path → screen polyline
  → carry the render-space PATH on the label      (per-vertex TryProjectAnchor; drop if any behind camera)
  → placement mode (line / line-center)         choose anchor arc-distances:
  → symbol-spacing (px)                            line-center → [totalLen/2]
shape text (existing CodepointTextShaper)         line        → every symbol-spacing px, centered
CurvedTextLayout(run, atlas):                   for each anchor, for each glyph:
  → per glyph: (arcCenter, centeredCell)          screenPt, tangent = walker.At(anchorArc + glyphArcCenter)
  single-line, glyph cell centered on its         emit PlacedQuad{ AnchorScreenPx = screenPt,
  own pen origin                                                    RotationRadians = tangentAngle,
                                                                    Quad = centeredCell }
                                                keep-upright: if the label's net screen direction is
                                                  right-to-left, walk the arc reversed
```

The reuse that makes this tractable: `PlacedQuad` already carries **per-quad** `AnchorScreenPx` +
`RotationRadians`, so the Burst billboard job needs **no change** — a curved label is just N `PlacedQuad`s with N
different anchors/rotations instead of N sharing one. New work is two pure Core helpers plus the per-frame walk.

> **Road shields (docs/road-shields-design.md).** `symbol-placement: line` no longer always curves: when the
> layer's resolved rotation-alignment is `viewport` (an explicit style choice, not the `auto`→map default this
> section describes), MapLibre lays symbols out **upright at each along-line anchor** instead — the road-shield
> look. See that doc's D3/D4 for the reframe; every `auto`-aligned line layer covered above is unaffected.

**New Core pieces (engine-free, headless-tested):**
- **`PolylineArcWalker`** — over a screen-space `float2` polyline: `TotalLength` and
  `At(arcDistance) → (float2 point, float tangentRadians)` by walking cumulative segment lengths and lerping
  within the containing segment (tangent = segment direction via `atan2`; clamps to the ends).
- **`CurvedTextLayout`** — `Layout(ShapedRun, IGlyphAtlasView) → IReadOnlyList<CurvedGlyph>` where
  `CurvedGlyph { float ArcCenter; SymbolQuad Cell }`. A single forward pass (mirrors `TextQuadLayout.PlaceGlyph`)
  places each glyph's cell relative to **its own** pen origin, centered **horizontally** on the glyph's
  advance-center, and **vertically on the run's optical (cap-band) centre** — the same
  `TextQuadLayout.OpticalCentreBelowReferencePx` a centre-anchored point label applies (`docs/road-shields-design.md`
  §11 D12), so a curved and a point label of the same string sit the same way on their anchor.
  `ArcCenter` = cumulative advance to that center.

  > **Corrected in W4 (symbol pitch-alignment epic) — this paragraph used to assert the opposite, and that is
  > why the defect survived three stages.** It read: cells *"stay baseline-relative (NOT vertically centered —
  > that would make ascenders/descenders straddle the line)"*, and that *"this baseline-center anchoring is
  > what lets the Burst job stay unchanged"*. **Both clauses were false**, and the maintainer's very first
  > reported defect — road labels drawn a fixed offset above or below the road, at tilt 0 included — was
  > exactly this text sitting `17.5` baked px off its line.
  >
  > 1. **The rejected-alternative confusion.** "Vertically centered" does not imply centring each glyph on its
  >    OWN ink; that is a *different* rule, and it is the one that would make ascenders and descenders bob. The
  >    correct rule adds **one constant per LABEL**, with no per-glyph term, so every glyph in a run keeps its
  >    exact relative offset and descenders still descend. The doc argued against the per-glyph rule and
  >    thereby rejected the per-label one. Pinned by `CurvedTextCentringTests`' W4-T2, which goes RED on the
  >    per-glyph alternative while a cap-glyph-only tooth stays green.
  > 2. **The rationale attributed the wrong cause.** The Burst job needs no change because the cell→screen/world
  >    map is **linear and homogeneous about the anchor** (`BillboardMath.BuildWorldQuad`,
  >    `SymbolBox.BuildRotatedGlyph`), so cell `y = 0` lands on the anchor under every branch and the tangent
  >    rotation is correct for **any** vertical placement of the cell. Baseline-anchoring was never what made
  >    that work — it was one arbitrary choice among many that the linearity permits. (The retired
  >    `BillboardMath.BuildQuad` this sentence named no longer exists; the two live sites are those above.)

**Placement (`SymbolPlacementSystem`, per frame):** each build's `ShapedSymbol` carries a `Path`/`PathUp` span
(in its `SymbolTileBuffer`) for line labels (empty for point labels — `AnchorRender` untouched) plus
`Placement` + the extractor's evaluated `SpacingPx`. Project each path vertex with `TryProjectAnchor` (skip the label if any vertex is behind the
camera — partial-visibility clipping is a refinement); build a `PolylineArcWalker` over the projected polyline;
choose anchor arc-distances (`line-center` → `TotalLength/2`; `line` → centered multiples of `symbol-spacing`,
label needs `labelWidthPx ≤ TotalLength`); apply keep-upright (walk reversed when the net direction points
leftward); emit one `PlacedQuad` per glyph.

**Double-rotation trap.** A curved glyph's rotation is the projected-line **tangent only**. The projection
already baked the bearing in (path vertices go through the bearing-aware camera), and line placement defaults
`rotation-alignment: auto → map` — exactly when the point-label path adds `SymbolBearing.BillboardRotationRadians`.
So the curved path sets `PlacedQuad.RotationRadians = tangent` and must **NOT** also route through
`BillboardRotationRadians` (tangent + bearing = double rotation). Point labels keep the bearing path; curved
labels take the tangent branch.

**Per-glyph orientation = chord across the glyph's footprint (not the single-segment tangent).** Each glyph is
rotated by `atan2(At(arc+halfWidth) − At(arc−halfWidth))` — the chord its own footprint spans — rather than the
raw piecewise-constant segment tangent (`PolylineArcMath.SegmentTangent` returns one angle for a whole segment,
so a glyph straddling a polyline vertex would otherwise snap to one segment's angle while its neighbour snaps to
the other, and their inner corners collide). The chord blends the two segment angles across a vertex so glyphs
tile edge-to-edge (MapLibre's approach). The `text-max-angle` **cull** gate stays on the raw **segment** tangent
(`StageCurvedAnchor` decouples cull from render: cull answers "is the path too kinky to place a label here"; the
chord blend is purely how each glyph is oriented), so culling is behaviour-preserving and the Burst/managed
staged-count parity holds. Teeth: `SymbolStagingMathCurvedVertexTests` (RED-verified — a glyph on a bend gets the
blended chord angle, not a raw segment angle; a straight run is unchanged).

> **Known limitation (tracked, not fixed) — corner spacing compression.** Glyphs are positioned at equal
> **arc-length** along the polyline (`PolylineArcMath.At`), but render as straight rigid quads. Where the line
> bends, the straight-line (chord) distance between adjacent glyph centres is **shorter than their arc-length
> advance**, so the boxes overlap by ≈ `advance − chord` at sharp corners — letters visibly bunch at real road
> bends (worst where a vertex falls mid-label). The chord-orientation fix above corrects the *rotation* at bends
> but NOT this *spacing* compression. A real fix is structural (chord-distance glyph stepping, or a corner-rounded
> placement path) with genuine trade-offs (chord-walking distorts text at sharp corners; rounding changes label
> shape) — deferred to a dedicated curved-text-placement slice, gated on a MapLibre-algorithm comparison. Not
> blocking; curved-label polish.

**Zero per-frame alloc.** The per-frame walk runs over **reused** scratch (a persistent projected-polyline
buffer grown geometrically, a reused walker), never a fresh `float2[]`/`List` per label per frame.
`CurvedTextLayout` stays build-time (its per-glyph result cached on the label), so only the walk is per-frame.

**Collision (the one genuinely-new placement concern).** A point label submits one `SymbolBox` (AABB). A long
curved label's single AABB would bound a whole road → almost nothing places. So a curved label submits a
**per-glyph box set** and places iff its boxes don't collide with already-placed boxes (MapLibre uses per-glyph
collision circles; per-glyph AABBs are the tractable analogue). The unified model: `SymbolCandidate` is a
contiguous box range; `SymbolCollision.SelectSurvivors(candidates, boxes, …)` treats a point label as a 1-box
candidate and a curved label as an N-glyph-box candidate, **all-or-nothing** (one colliding glyph drops the whole
label; a placed label blocks across its whole run). Candidates sort; boxes stay put (grid stores absolute
indices; adjacent glyph boxes never self-block).

**Visual-verify (owed):** the tangent is screen-space so it's correct under bearing/tilt (taken from projected
points), but the look under rotation/tilt wants a maintainer eyeball; the keep-upright flip threshold and the
`line` spacing/centering are see-it-to-believe-it.

---

# 4. Projection support (globe-ready labels)

**Status: S1 + S2 + S3 + S4 landed** (all Mercator byte-identical, all real latent-globe bug fixes) on branch
`feat/symbol-projection-support`. **S5 deferred** — it is an on-device eyeball that needs a committed globe
label fixture and an exercised globe camera path on screen (there is none yet).

Make the subsystem render correctly under any `IProjection` — notably `SphericalProjection` (the globe) —
reaching the projection-correctness the fill/line mesh path already has via `ProjectPointsJob<TProj>`. A label on
the globe should sit on the curved surface at its true geographic anchor, be culled behind the horizon, and (for
line labels) follow the projected curve.

## What's already right, and what isn't

**S1–S4 below have since landed** (see Stages); this subsection states the problem as it stood before them,
so several of its present-tense claims describe code that no longer works this way — each fix's own numbered
entry says what changed.

The per-frame pipeline (§1) is almost entirely **screen-space**. At build time it projects geodetic anchors/paths
to render space through `IProjection.ProjectPoint` (`SymbolFeatureExtractor.cs`); the tile-coverage filter
projects a tile's corners the same way (`SymbolTileCoverageFilter.ProjectCorner`); each frame it projects those
to screen and does arc-walk / AABB / billboard in pixels. So **billboarding, per-glyph orientation, and
collision are already projection-generic** — glyph quads are built in 2D screen space
(`BillboardMath.BuildWorldQuad`), and curved-text rotation is the screen-space tangent of the already-projected
polyline (`SymbolStagingMath`). There is no world up-vector or great-circle tangent to get right. What remains
is four fixes, one foundational.

### 1. The projection seam drops the rebase rotation (foundational)

The Unity camera places the look-at at the world origin in an idealized Y-up ENU orbit frame
(`MapCamera.SyncToCamera`), so `ViewProj` expects points in the **rebased look-at frame**. Mesh tiles
honor this: `TileToSceneRebased = Rebase·(origin − sceneOrigin)` (`FloatingOrigin.cs`),
`Rebase = transpose(TangentBasisAt(lookAt))` (`SceneFrame.cs`). The label seam did not —
`SymbolScreenProjection.TryProjectPoint`, `SymbolProjectionJob.Execute`, and the tile-corner projector then
inside the per-frame batch builder only subtracted `sceneOrigin`, never applying `Rebase`. On Mercator `Rebase = identity`
(inert); under `SphericalProjection` every anchor away from the look-at projected to the wrong pixel.
**Fix (landed, S2):** carry a `float3x3 rebase` into the seam and rotate after translating, before `viewProj`
— `SymbolScreenProjection.TryProjectPoint` and `SymbolTileCoverageFilter.ProjectCorner` both take it today.

### 2. Cross-tile dedup drops render-Y

`CrossTileSymbolKey.For` quantized render `.x`/`.z` only — the key had no Y axis. Under
`SphericalProjection.ProjectPoint` render X/Z ∝ cosφ (even in latitude) and Y ∝ sinφ (odd), so a
same-longitude pair mirrored across the equator (e.g. 30°N and 30°S) collided exactly at any grid size — two
distinct labels merged into one. Byte-identical on Mercator (surface labels have render.y ≡ 0). **Fix (landed,
S1):** `CrossTileSymbolKey.For` now quantizes on all three axes; the quantization scale was renamed
`WebMercator.GroundResolution` → `CameraPoseMath.MetersPerPixel` in the same stage, dropping a Mercator-only
name from a camera quantity — no behavior change from the rename itself.

### 3. No horizon cull

`SymbolProjectionJob.OutValid` culled only behind-camera points (`clip.w ≤ 0`); a far-side-of-globe label
projected in front and drew through the earth (labels ship `ZTest Always`, so no depth hides it, and its
collision box still contests near-side space). **Fix (landed, S3):** test each anchor against
`IProjection.TryGetHorizonOccluder` as a rad-0 point — the algebra `FrustumTileSelector` already uses — and
fold the result into the cull. Landed as a **fade** (`GatherTrigger.Horizon`, §1.4), not a hard `OutValid`
drop, so an occluded label eases out instead of popping. Correct only once the seam applies `Rebase` (the
occluder is expressed in the rebased look-at frame). Mercator returns no occluder ⇒ no-op.

### 4. Long line segments chord instead of curving

`SymbolFeatureExtractor.ProjectPath` projected only the original MVT vertices, so a long segment rendered as a
straight screen chord instead of the projected curve. **Fix (landed, S4):** subdivide per
`IProjection.MaxRefineAngleRad` (Mercator = ∞ ⇒ no split), as the mesh line path does
(`SubdivideJob`, job-scheduling-design.md §8 stage 5 Group B — this note's own engine-free `Core` helper,
`LineCurvatureSubdivision`, is exactly the ported form it already recommends). **Hazard, resolved:**
`LineAnchor.Segment` indexes the path vertex array — computed against the tile-local path
(`LineAnchorPlacement.Compute`) but resolved against the render path (`SymbolStagingMath`). Subdividing only
`ProjectPath` would have silently desynced those indices (in range, wrong vertex); the landed fix subdivides
the tile-local path first and feeds the same sequence to both anchor-compute and projection.

Screen-space collision needs no change — it is already projection-blind; it only needs occluded labels removed
first, which the horizon cull provides.

## Stages

S1–S4 share the invariant **Mercator snapshots byte-identical**; each is headless-gated (Editor closed).

| Stage | Change | Falsifiable teeth |
|---|---|---|
| **S1** ✅ | Dedup: add a Y axis to `CrossTileSymbolKey` + rename the scale to `MetersPerPixel` | A 30°N/30°S same-longitude pair must NOT dedup to one cell; no symbol/label file references `WebMercator.GroundResolution`; Mercator dedup + snapshot parity |
| **S2** ✅ (keystone) | Apply `SceneFrame.Rebase` in the label projection seam | Under a non-origin globe rebase, the label seam matches the mesh-RTC oracle (`FloatingOrigin.TileToSceneRebased`) and differs from the no-rebase result — RED-verified off by ~48M px; Burst-vs-inline parity within ULP noise; Mercator snapshot parity |
| **S3** ✅ | Horizon cull via `TryGetHorizonOccluder` — done as a FADE in `GatherSymbolPoints` (not a hard `OutValid` drop → labels would pop) | Far-hemisphere anchor culled, near-side kept; occluded label eases out (kept staged), absent from collision once faded |
| **S4** ✅ (hardest) | Subdivide the tile-local path once (shared `LineCurvatureSubdivision` policy — the `SegmentSteps`/`MaxCurveSegments` leaf unified with the mesh path); feed the ONE finer sequence to both anchor-compute + projection | Forced-long 2-vertex globe segment splits (mid-vertex off the chord); anchor count/tile-position invariant, only `Segment` refined; each anchor resolves to its correct arc position; **RED-verified** vs the injected coarse-`Compute`/finer-`PathRender` desync; Mercator byte-identical |
| **S5** | Globe end-to-end validation + eyeball | Labels only on the near hemisphere; curved road text tracks the projected curve to the limb; collision stable under a slow globe rotate |

S1 and S2 are independent and Mercator-identical. S2 must precede S3 (horizon math needs the rebased frame) and
S4's validation. **S2 is the keystone** — all globe label correctness rests on it, though it fails loudly
(visibly wrong) the moment anyone looks. **S4 is the hardest** — an index desync mis-anchors labels silently,
surviving even an eyeball.

### S4 post-stage findings (recorded at merge; non-blocking, from the dual review)

- **Arch-conditional RED-verify (the one worth knowing).** The S4 subdivision unified the mesh path's
  `SegmentSteps` policy into `Core/Geometry/LineCurvatureSubdivision`, which surfaced a latent overflow inherited
  verbatim from the mesh path: `(int)math.ceil(ang / maxRefineAngleRad)` casts an out-of-range double for a
  pathological tolerance. The cast is **architecture-dependent** — x64 truncates to `int.MinValue` (defeats the
  `< 1` guard → returns 1, *no* subdivision where the most was needed); arm64 saturates to `int.MaxValue` (still
  trips the `> MaxCurveSegments` cap → correct-looking 128). Fixed by clamping in double space *before* the cast
  (`(int)math.clamp(math.ceil(…), 1, MaxCurveSegments)`), deterministic on any arch (also traced NaN → 128). But
  the two guarding teeth (`SegmentSteps_Extremes…`, `Subdivide_PathologicalTolerance…`) only go **RED on x64** —
  the Unity EditMode gate (x64 Mono/Rosetta) is where they caught it; the fast `dotnet test` loop (native arm64)
  passes them fix-or-no-fix. An arm64-only contributor must NOT trust those two as a tripwire on the fast loop.
- **Tooth-c fixture fragility (S4-T6).** `Extract_GlobeProjection_…`'s resolve-position tooth passes at ~0 error
  only because the z=1 diagonal subdivides into an *even* step count, landing the geographic midpoint on an exact
  finer-path vertex. If the synthetic geometry is ever retuned to an odd count, the anchor falls mid-sub-segment
  (~1 km sagitta > the 1.0 m tolerance) and flips falsely RED with no real bug. Teeth (b) `Segment > 0` and the
  desync RED-verify carry the real weight; retune the tolerance if the fixture changes.
- **Weak chord threshold (S4-T6a).** `distFromChord > 1.0` (metre-scale render units) distinguishes curved-from-
  flat but does not tightly pin the sag magnitude. Acceptable — (b)/(c) pin the alignment.

## Verdict

Worth doing; gated on the globe being an active target. It brings labels to parity with the already-globe-ready
mesh path — today the globe renders geometry correctly but labels project to the wrong pixel (#1), merge mirrored
hemispheres (#2), and draw through the far side (#3). **Land S1 + S2 regardless of the globe schedule** — both
are Mercator byte-identical and both fix real latent globe bugs. **Defer S3–S5** until there is a committed globe
label fixture and an exercised globe camera path (`SphericalProjection.ScreenToGround`/`GroundToScreen` exist;
confirm an interactive globe demo); S3–S5 cannot be validated without a globe on screen.

Rejected alternative: a geodetic dedup key (store/quantize `GeoCoordinate`). The dedup is deliberately
render-space to collapse parent/child tile-quantization noise into one cell (`CrossTileSymbolKey.For`);
geodetic storage reintroduces an equivalent tolerance plus more plumbing, and 3-axis render quantization already
fixes the defect. One tunable to surface in S3: the horizon-cull grazing margin.

---

## Grounding (touch points)

`MapRenderer.Unity/Text/`: `SymbolSubsystem` (queue/pump/store, + the pre-build tile-coverage cull in
`CurrentBatch`, §1.5), `SymbolTileStore` (active/cached/departing), `SymbolReconciler` (the off-main
cross-tile dedup — `docs/labels-async-reconcile-design.md`), `Placement/SymbolPlacementSystem` (`Tick`),
`Placement/SymbolGatherPlan` (the per-frame winner plan, §1.3), `Placement/SymbolScreenProjection` (the
projection seam), `Placement/BillboardMath`, `SymbolFeatureExtractor` (`ProjectPath`,
`LineAnchorPlacement.Compute`). `MapRenderer.Core/Text/`: `CodepointTextShaper`, `TextQuadLayout`,
`CurvedTextLayout`, `Placement/SymbolStagingMath` (`StageCurved`, tangent), `Placement/PolylineArcWalker`,
`Placement/CrossTileSymbolKey`, `Placement/LineAnchor`, `Placement/SymbolTileCoverage`,
`Placement/SymbolTileCoverageFilter` (the pre-build cull, §1.5) — the B-3 per-symbol distance cull (§1.4) has
no dedicated type; it is inline in `SymbolPlacementSystem`. `MapRenderer.Jobs/Symbols/`: `SymbolProjectionJob`
(`OutValid`), `StageJob`, `CollisionJob` — the billboard build itself is not a job: `WorldSymbolRenderer`
calls `BillboardMath.BuildWorldQuad` directly at emit time. `MapRenderer.Core/Coordinates/`: `IProjection`
(`ProjectPoint`, `TryGetHorizonOccluder`, `MaxRefineAngleRad`), `SphericalProjection`, `WebMercator`.
`MapRenderer.Core/View/`: `CameraPoseMath`, `FloatingOrigin`. `MapRenderer.Unity/Rendering/Backend/`:
`SceneFrame`. Mesh-path prior art: `SubdivideJob` (job-scheduling-design.md §8 stage 5 Group B — this note's
own engine-free `LineCurvatureSubdivision` is the ported form the mesh path's `SubdivideCenterline` retired in
favour of), `ProjectPointsJob<TProj>`, `FrustumTileSelector`.

## Relationship to other work

- **Projection support (§4)** completes the projection-agnostic / globe track
  (`docs/coordinates-and-projections.md`) — the mesh path is already generic via `ProjectPointsJob<TProj>`; this
  closes the last Mercator-coupled subsystem. Independent of the geometry-IR epic
  (`docs/tile-geometry-ir-design.md`); both share the two-waist principle (tile-local frames for placement math,
  projection at a defined seam).
- **Camera-driven property re-evaluation** (`docs/smooth-transitions-design.md` §2) is where a label's
  zoom-dependent paint would smoothly re-evaluate — orthogonal to §4 (which is *where* an anchor projects, not
  *when* its style re-evaluates).
- The build-time half (decode → per-layer extract) rides the per-layer tile pipeline
  (`docs/per-layer-tile-processing-design.md`).

---

# 5. Icon support (sprite symbols)

**Status: I1–I5b + the I6 icon-identity fix landed (icon pipeline complete through the on-GPU draw + cross-tile
identity); I6 remaining = the maintainer on-screen eyeball only (see §5.5).** MapLibre's `symbol` layer has two
decoupled elements sharing one anchor: **text**
(`text-*`, glyph-SDF atlas — §1–§4) and **icons** (`icon-*`, a *sprite* — a named rectangle in a pre-baked
sprite sheet). Icons are the genuinely-simpler sibling: **no shaping, no per-codepoint glyph fetch, no curved
layout** — one feature → one anchor → one sprite quad. The data/layout path reuses the text seams almost
verbatim (`SymbolQuad` was left glyph/sprite-agnostic on purpose; the point-label projection/collision/billboard
machinery is already text-agnostic screen-space). The genuinely-new work is a **second atlas** (sprite sheet,
not the dynamically-packed glyph atlas) and a **second material** (plain RGBA sample, not SDF+halo).

## 5.1 Scope fences (v1) — what "simpler than text" means, precisely

The "icons are a simpler version of text" framing holds **only** with these fences. They are load-bearing;
a stage plan may not quietly cross one.

- **IN:** `symbol-placement: point` icons; `icon-image` (incl. **data-driven** — evaluated per feature, an
  unknown sprite name is skipped, not fatal); `icon-size` (with sprite `pixelRatio` applied); `icon-offset`;
  `icon-anchor`; `icon-rotation-alignment` (point: `auto`→`viewport`); `icon-allow-overlap`;
  `icon-ignore-placement`; `icon-padding`; `icon-opacity`. **Non-SDF (RGBA) sprites only.**
- **OUT (separate epics, do not build):**
  - **Combined text+icon collision / layout coupling** — `icon-text-fit`(+`-padding`), `text-optional`,
    `icon-optional`. v1 treats an icon as its **own** collision candidate with its **own** box, sharing only
    the feature anchor with any co-located text. The fully-correct MapLibre model (one symbol = a
    text-box + icon-box union placed all-or-nothing, with `*-optional` fallbacks) is the hard part and is
    a deliberate follow-up.
  - **SDF / recolorable sprites** — the `"sdf": true` sprite variant + `icon-color`/`icon-halo-*`. Deferring
    these is *exactly* what keeps the icon shader trivial (a straight `tex2D` sample, no SDF median-distance,
    no halo). `icon-color`/`icon-halo-*` parse-and-carry is allowed but stays inert until the SDF path lands.
  - `icon-line-placement` (icons along a line / `symbol-placement: line` with an icon) — **partially lifted**
    by the road-shields epic (docs/road-shields-design.md D4): a `viewport`-resolved line icon now emits as
    an upright point-icon at each along-line anchor (the existing point-icon path, a different anchor). A
    **map-aligned** line icon (`icon-rotation-alignment` resolving `map`, e.g. `road_one_way_arrow*`) stays
    fenced — that needs icons rotated to the line tangent, a genuinely new staging path. `icon-keep-upright`,
    `icon-translate`, `icon-image` **stretchable** (`content`/`stretchX/Y`). Icons on line features are
    dropped in v1 (text still places along the line as today). (`icon-pitch-alignment` — as of the
    pitch-alignment epic P1, parsed and resolved via `AlignmentResolution.ResolvePitch`, but unconsumed; see
    §6.3's "Known limits" list below — it is no longer the out-of-scope key this paragraph originally meant.)

## 5.2 The sprite sheet (vs. the glyph atlas)

A MapLibre **sprite** is a pre-baked sheet: one PNG (`ofm.png`) + one JSON index (`ofm.json`) mapping each
sprite name to a rectangle. The style's top-level `"sprite"` URL (liberty.json: `.../sprites/ofm_f384/ofm`)
gets `.json`/`.png` (and `@2x` for hi-DPI) appended. Index shape (clean-room, from the public spec):

```
{ "airport-11": { "x": 0, "y": 0, "width": 22, "height": 22, "pixelRatio": 2, "sdf": false }, … }
```

Contrast with `GlyphAtlas` (§1): the glyph atlas is **dynamically packed** at runtime as codepoints arrive and
**grows** (the UV-staleness hazard, `glyph-atlas-uv-growth-staleness`). The sprite sheet is **fixed and
pre-baked** — decoded once, never grows, UVs are stable. So the icon atlas is far simpler: a parsed
`SpriteIndex` (name → rect) over an immutable texture. `pixelRatio` is the sheet's DPI scale: the sprite's
**logical** size is `width/pixelRatio` × `height/pixelRatio`, and `icon-size` scales *that*.

### 5.2.1 Sampling the sheet — bilinear + a one-texel padded repack (settled)

The sheet binds **`FilterMode.Bilinear`**, and `SpriteSheet` **repacks it at decode time so every sprite
gets a one-texel transparent border**. The two are one decision and neither is correct alone. (This
supersedes the half-texel UV inset that shipped first; see "What the inset was, and why it went" below.)

**Why not nearest-neighbour.** An icon's magnification is `icon-size × dpr / pixelRatio`. The sheet is
fetched @1x and `dpr` is `Screen.dpi / 160`, so the product is essentially never an integer — and
nearest-neighbour is exact *only* at integer magnification. Off it, each source texel covers `N` or `N+1`
device pixels and **which** depends on the quad's sub-pixel phase, so panning re-quantises an icon's
interior every frame. That was the first reported bug: "pixels inside the icon warp while zooming/panning".

The diagnosis turned on one structural fact, not on measurement: `BillboardMath.BuildWorldQuad` gives all
four corners the **same bitwise `anchorLocal`** plus static per-corner `OffsetPx`, so a quad is **rigid** in
screen space. An anchor precision error therefore *translates* an icon and can never deform its interior —
which rules out the entire geometry/precision family and leaves resampling.

**Why the border — the SILHOUETTE defect.** Bilinear fixed the interior and left the outline. The icon's
outline still wobbled ±1 device pixel while panning, and the cause chain is:

1. The shipped style's sheet (`tiles.openfreemap.org/sprites/ofm_f384/ofm`) has **228 of its 264 sprites
   full-bleed** — ink touching the rect edge — and **371 abutting sprite pairs, none separated by even one
   pixel**.
2. MSAA is **off** project-wide: `Assets/Settings/RPAsset.asset` `m_MSAA: 1` and
   `ProjectSettings/QualitySettings.asset` `antiAliasing: 0`. One binary coverage sample per pixel per
   polygon edge.
3. For a full-bleed sprite the silhouette **IS** the quad's polygon edge, so it flips whole pixels in and out
   as the quad slides sub-pixel. No sampler setting can help: bilinear can only produce a soft edge if there
   is a transparent texel to ramp into, and a full-bleed sprite has none.

So the fix is neither a filter change nor a shader change — it is a **content** change. `SpriteSheet` repacks
the decoded sheet, giving every sprite its own cell with a one-texel border, and `IconQuadLayout` **draws**
that border. The silhouette becomes a *texture alpha edge* with a one-texel ramp, which bilinear antialiases.

*(Measured from the published sheet + its JSON index, which are style assets, not implementation. This repo
is clean-room with respect to MapLibre: no MapLibre source has been read, and no design here is justified by
what their implementation does.)*

**The border's content: alpha 0, RGB replicated from the adjacent edge pixel.** Not `(0,0,0,0)`. Bilinear
interpolates RGB and alpha *independently*, so a mid-ramp texel with a zeroed RGB contributes black to the
colour while still contributing coverage — a dark fringe all the way round every icon. Replicating the
nearest content pixel's RGB makes the ramp colour→same colour and alpha 1→0. A corner border texel replicates
the diagonal content corner (clamp-to-content sampling makes edges and corners one rule).

**Two structural rules, and the identity that ties them together.** Two shallow implementations would look
done and both fail:

* *Pad the atlas, leave the quad nominal* → the skirt is never rasterized, no ramp is drawn, the silhouette
  is still the polygon edge. And the whole padded rect is squeezed into the unchanged quad, so the ink
  **shrinks** by `W/(W+2P)` (32.0 px on the U2 fixture, against a 40.0 nominal).
* *Grow the quad, leave the UV rect on the content* → the ramp is drawn, but only the *content* texels are
  sampled across the now-larger quad, so each texel draws bigger and the ink **grows** by `(W+2P)/W`
  (50.0 px on the same fixture). Note the direction: this one makes the icon LARGER, not smaller — the
  "squeezed into a smaller share of the quad" reading is backwards, because the quad's UV span, not its
  extent, is what sets the drawn size of a texel.

The invariant that excludes both, checked in Core (`IconQuadLayoutTests`):

> **texels-per-drawn-pixel is the same for the border and for the content** —
> `uvWidth × sheetWidth / quadWidth == PixelRatio / iconSize`, independent of the sprite and of the padding.

**The content rect stays the content rect.** `SpriteEntry.X/Y/Width/Height` continue to mean the sprite's own
ink; the repack only *relocates* them, and a new `SpriteEntry.Padding` records how many texels of border
surround them. That single decision is what keeps everything else still:

* `IconQuadLayout`'s logical-size maths (`entry.Width / entry.PixelRatio`) is untouched, so `icon-size`
  arithmetic cannot change and icons cannot silently grow — guaranteed by construction, not by a test.
* `FillPattern.TryResolve` needs zero edits: it reads the relocated content rect, which is exactly what a
  point-sampled `frac()`-wrapped pattern must have. `LogicalSizePixels` and every pattern period are
  unchanged.
* The **collision box stays on the content**: `IconQuadLayout.ToLayoutResult(quad, skirtPx)` and
  `CurvedGlyph.CellSkirt` remove the skirt again for placement. A padded box would grow every icon's
  collision footprint by ~1 logical px per side (up to ~9 % of a 22 px icon's area), silently changing which
  labels win and which fade.

So there are two representations with two consumers: the **padded** `SymbolQuad` (geometry + UVs) goes to
render only; the **content** box goes to collision and placement only. The skirt is carried as one baked-px
float (`IconQuadLayout.SkirtPx` → `SymbolFeature.IconSkirtPx` → `CurvedGlyph.CellSkirt`), never re-derived, so
the grow and the un-grow cannot drift.

**The packer.** `ShelfRectPacker` — next-fit-decreasing-height shelf packing, chosen over MaxRects/skyline
because a few hundred cells that grow two texels do not need the extra 3–5 % occupancy, and a shelf layout's
disjointness is provable by construction. Determinism is a hard requirement (the sheet must not differ
between machines or runs): cells sort by height desc, width desc, then the group's lexicographically-smallest
name by `string.CompareOrdinal`, which makes the order total and independent of dictionary insertion order.
Names sharing one source rect are **grouped** so aliases stay aliased and are copied once. Sheet width is the
first of `{W₀, 2W₀, 4W₀, …}` (with `W₀ = max(sourceWidth, widest cell)`) whose packed height fits 8192. If
nothing fits, `Plan` returns the source sheet unchanged with `Padding = 0` and `SpriteSheet` logs a warning
rather than throwing. Degenerate and out-of-bounds entries pass through untouched with `Padding = 0`, matching
`SpriteIndex.Parse`'s forward-compat posture. Out-of-bounds is tested **overflow-safely** (`long`): in 32-bit
signed arithmetic `x + width` for `x: 2147483647` wraps negative and passes the bound, and such an entry would
be packed, blitted from a negative offset, and throw out of the `SpriteSheet` constructor — installing neither
texture nor index, i.e. every icon on the map gone, from one malformed line of sprite JSON.

**The fallback is a degraded mode, not a free one.** It is tempting to record it as harmless ("icons render as
they did before this stage"); that is false. Because the half-texel inset is retired there is deliberately only
one sampling path, so a `Padding == 0` sprite is drawn edge-to-edge — and on an unpadded, abutting, full-bleed
sheet a bilinear edge tap then reaches into the neighbouring sprite. The fallback therefore **re-introduces
neighbour bleed**, which is worse than the pre-stage inset, not equal to it. That is the accepted price of a
single sampling path (two is exactly how this bug class returns), but it must be recorded as a price.

Cost on the real sheet: ~**+20 %** sheet area (and VRAM), once, at style load.

**What the inset was, and why it went.** The first fix insetting each sprite's UV rect by half a texel kept
bilinear's edge taps off the neighbouring sprite, but at the cost of never drawing the sprite's outer
half-texel — so its content rendered `W/(W−1)` larger than the quad implied, and that grew as sprites shrank:

| sheet rect `W` | content magnification `W/(W−1) − 1` | shrink vs shipped `1 − (W−1)/W` | cropped per side |
|---|---|---|---|
| 8 px (`dot`, `sample-sprite.json`) | 14.3 % | **12.5 %** | 6.3 % |
| 22 px (`airport-11`, §5.2's example) | 4.8 % | **4.5 %** | 2.3 % |
| 64 px | 1.6 % | 1.6 % | 0.8 % |

The two percentage columns are the *same ratio read from opposite ends* and they are not interchangeable:
`W/(W−1)` is how much too large the inset drew, `1/W` is how much smaller the fix draws relative to that. Quote
the second one whenever the sentence is "smaller than it shipped".

The border does the inset's job better (the sprites are now *physically* separated, so there is nothing to
reach across) and the inset's magnification is retired with it. **This is user-visible: icon ink is now
4.5–12.5 % SMALLER than it shipped** — that is the intended return to nominal, not a regression. Icon
silhouettes are also ~1 texel × magnification device px softer at the edge; that softness *is* the
antialiasing.

**Rejected — "keep the ink at 45.714 px".** An independent review arm called the return to nominal a
contract violation and asked for the U2 fixture's five-texel bar separation to stay at 45.714 px. It is
rejected, and the arithmetic is recorded here so it is not re-litigated:

| tree | UV span over the quad | device px per texel | 5-texel separation |
|---|---|---|---|
| before `ed930d95` (`9113888f`) | `W` = 8 texels over 64 px | 8.000 | **40.000 px** |
| `ed930d95` (the half-texel inset) | `W−1` = 7 texels over 64 px | 9.143 | 45.714 px |
| this stage | 10 padded texels over an 80 px quad | 8.000 | **40.000 px** |

40.000 px is the ORIGINAL, correct scale, restored. 45.714 px is the artifact the half-texel inset introduced
hours earlier — documented in that commit as a temporary cost — and retiring it is the point of this stage.
Pinning 45.714 would pin a one-commit-old known-bad state as the contract.

There is deliberately **one** sampling path: `Padding == 0` draws the rect edge-to-edge and is not a fallback
branch. Do not re-add a conditional inset for it — two sampling code paths is exactly how this bug class
returns.

**Still no mip chain.** Mips on a *packed* atlas average neighbouring sprites together at every level ≥ 1 —
a worse artifact than the minification aliasing they would fix.

**`fill-pattern` shares the texture, and keeps point sampling.** `filterMode` is state on the **texture**,
not on a sampler, and `RenderLayerSet` binds ONE shared sprite texture — as `_MainTex` for icon materials and
`_PatternMap` for fill layers. `Fill_LitInput.hlsl` therefore samples patterns through an inline
`sampler_PointClamp` so pattern filtering does not depend on the shared texture's `filterMode`. The repack
does **not** retire that: the border it lays down is *transparent*, and a pattern's tiling seam must continue
into the opposite edge's pixels rather than fade out. Patterns keep sampling point-wise inside their content
rect and never touch the border — pinned by `FillPatternThroughSpriteSheetTests`, which is the only test that
drives a pattern through the real `SpriteSheet` (`FillPatternSnapshotTests` builds its own `FilterMode.Point`
texture and never touches it).

**Follow-on stages (not built here).**

* **P1 — wrap-replicated pattern borders.** The same `SpriteSheetPadder`/`SpriteSheetComposer` mechanism with
  a second border-fill *role* selected per sprite (opposite-edge replication instead of transparent). Only
  then can `Fill_LitInput.hlsl` drop `sampler_PointClamp` and take a correctly-filtered bilinear tap at a
  tiling seam. Needs its own plan: the role is not derivable from the sprite JSON — it depends on which style
  layers reference the sprite as `fill-pattern`.
* **P2 — minified icons.** `label_village` / `label_town` / `label_city` dots run at `icon-size` 0.2–0.5, i.e.
  magnification 0.30–0.75. The ramp's on-screen width is `1 texel × magnification` device px, so below 1× the
  whole ramp is sub-pixel and padding cannot fix it. That needs mips-with-per-sprite-guard-bands or a
  downsampled sprite variant.

**Teeth.** `SymbolIconResamplingTests` carries three, all sweeping magnification (a tooth pinned at one
magnification is weak, because the whole defect family is "which magnification you happen to be at"):

* **interior** — a one-texel stripe swept across one full device pixel in eighths; the ink centroid must
  advance every step. Nearest-neighbour's smallest advance is exactly `0.000` px; bilinear advances ~0.11–0.14.
  Swept at 1.37 / 2.5 / 3.25 — all non-integer, because at an integer magnification the defect does not exist.
* **silhouette** — the same sweep over a **full-bleed** sprite (uniform opaque ink touching all four rect
  edges, abutting an opaque neighbour). Swept at 1.0 / 1.37 / 2.5 / 4.0, **including 1.0**, where the interior
  tooth is vacuous and this one is sharpest. Measured RED against the un-fixed tree: the centroid sat on
  `127.5000` px for four consecutive phases, then jumped `+1.0000`, at every magnification.
* **nominal ink size** — an 8×8 sprite carrying two one-texel bars five texels apart, rendered at
  magnification 8; the distance between the bars' ink centroids must read `5 × 8 == 40.0` device px.
  A bar's rendered profile is symmetric about its centre and a monotone transfer curve maps a symmetric
  profile to a symmetric one, so the separation is independent of whether the framebuffer is gamma-encoded —
  which a coverage-threshold width would not be. Measured RED: `45.714` px, exactly `40 × 8/7`, the inset's
  magnification.

Plus: the neighbour-bleed guard (green before and after, now green for a better reason — the sprites are
physically separated); `IconSkirtCarrierChainTests` — the **carrier chain**, from a genuinely padded
`SpriteAtlasView` through real extraction and `StyledSymbolTileBuilder` to the point label's `Layout.Bounds*`
and the along-line label's `CurvedGlyph.CellSkirt`. It is its own tooth because every other skirt test calls
the two ends directly (`ToLayoutResult(quad, SkirtPx(…))`, `BuildRotatedGlyph(…, skirt: 3f)`) and so stays
green against an implementation that never computes the skirt during extraction or emits `CellSkirt = 0`; the
render snapshots cannot see it either, since they draw the padded quad, which a lost skirt does not change.
`SpriteSheetPadderTests` (separation ≥ 2 texels between any two content rects, plan
determinism across dictionary insertion order, alias preservation, field preservation, degenerate
pass-through, an **overflowing** `x: 2147483647` rect that must pass through unpadded *and* leave composition
runnable, the cannot-fit fallback); `SpriteSheetComposerTests` (content copied byte-for-byte, border
alpha 0 on all eight bands, **no dark fringe** — a plain `Array.Clear` border fails it — corner replication,
and everything outside a cell left transparent); `SpriteSheetTests` (the border is real in the bound texture
and replicates the adjacent content RGB); and `IconQuadLayoutTests` (the content box is unchanged for all
nine anchors × three icon-sizes × three sprites, the UV rect covers the padded cell, and the
texels-per-drawn-pixel identity).

**Why this shipped.** Every pre-existing icon test rendered ONE static frame, and no static frame can see a
defect whose whole signature is "the render changes when it should not". The trap compounds twice over: at
exactly 1× magnification nearest-neighbour is a pixel-perfect blit, so the *interior* defect is invisible at
the one setting an eyeball would check first — and 1× is precisely where the *silhouette* defect is worst.

## 5.3 Architecture — where each piece lives (mirrors the text path)

| Concern | Text (existing) | Icon (this epic) |
|---|---|---|
| Atlas index (Core, engine-free) | `GlyphAtlas`/`GlyphAtlasEntry` (dynamic) | **`SpriteIndex`** (parsed once; name→`SpriteEntry{x,y,w,h,pixelRatio,sdf}`) |
| Style parse (Core) | `Symbol.LayoutProperties`/`PaintProperties` (`text-*`) | **`icon-*`** on the same `Symbol.StyleLayer` (add to Layout/Paint; new `PropertyNames`) |
| Extract (Core) | `SymbolFeatureExtractor` → `SymbolFeature` (text) | same extractor emits **icon** `SymbolFeature`s (icon fields) |
| Layout → quad (Core) | `TextQuadLayout`/`CurvedTextLayout` → `SymbolQuad[]` | **`IconQuadLayout`** → one `SymbolQuad` (sprite UVs) |
| Atlas texture (Unity) | `GlyphAtlasTexture` + `GlyphManager` | **`SpriteSheet`** (PNG→`Texture2D`) + a sprite source (JSON+PNG fetch) |
| Per-frame batch (Core) | `SymbolBatch` (`Kind{Point,Curved}`) | icon records in the batch (see §5.4 — the crux) |
| Draw (Unity) | `SymbolRenderLayer` + `SymbolText.shader` (SDF) | **`SymbolIconWorld.shader`** (RGBA) + a sprite-texture bind |

The build-time half rides the same per-layer tile pipeline (`TileSymbolLayerProcessor`); the per-frame half is
the same `SymbolPlacementSystem.Tick`. Collision is the same global grid — an icon is just another candidate box.

## 5.4 The load-bearing decision (I5): how icons ride `SymbolBatch`

`SymbolBatch`, the Burst stage job, and the billboard build all switch on `Kind{Point,Curved}`, and the icon
draw must bind a **different texture** (the sprite sheet) than the glyph atlas. This is the crux that decides how
invasive the render plumbing (I5) is; the I5 plan must settle it explicitly. Two candidates:

- **(A) New `Kind.Icon`** — a point-like record whose quad's UVs index the sprite sheet, routed to a separate
  material slot / draw with the sprite texture bound. Cleanest separation; touches every `Kind` switch.
- **(B) Ride `Point` + an atlas discriminator** — icons stage exactly like point text but carry an
  "atlas = sprite" flag that partitions the *draw* (like the existing per-layer material slots) so the sprite
  texture binds for icon quads. Smaller stage/billboard-job change; the discriminator lives at emit/draw.

Bias toward **(B)** unless staging genuinely differs (it should not — an icon is a single axis-aligned quad, the
degenerate point-text case): staging/collision are texture-blind; only the *draw* needs the other texture. But
the plan must name and defend the choice; a wrong call here is the expensive rework.

## 5.5 Stages

Core-first: I1–I3 are pure `MapRenderer.Core`, fully verifiable on the fast `dotnet test` loop (~0.1s) — the
**honestly shippable** deliverable. I4–I5 are Unity render plumbing whose "an icon actually draws" is a
**maintainer eyeball** (headless verifies compile + existing snapshots only — same class as the deferred §4 S5
globe eyeball). Each stage: plan → develop → review → headless gate (Editor closed) → one revertible commit.

| Stage | Change | Falsifiable teeth |
|---|---|---|
| **I1** ✅ | `SpriteIndex` (Core): parse sprite JSON → name→`SpriteEntry`; `TryGetSprite`. **Committed fixture** (`Assets/Fixtures/sprites/sample-sprite.{json,png}`, hand-authored, network-free) so I3+ have real test data. | Parse the fixture; a known name resolves to its exact rect + `pixelRatio`; an unknown name → false; malformed JSON → empty index, no throw. |
| **I2** ✅ | `icon-*` style parse (Core): `PropertyNames` + `Symbol.LayoutProperties`/`PaintProperties` gain the §5.1-IN keys (data-driven `icon-image`, `icon-size`, `icon-offset`, `icon-anchor`, `icon-rotation-alignment`, `icon-allow-overlap`, `icon-ignore-placement`, `icon-padding`, `icon-opacity`). | Each key parses to its typed property/default; the no-`icon-*` layer is byte-identical to today; the `PropertyNames`-only-source test still passes. |
| **I3** ✅ | Extract + layout (Core): `SymbolFeatureExtractor` emits an icon `SymbolFeature` per point feature whose `icon-image` resolves to a known sprite (unknown → skip); `IconQuadLayout` builds the single `SymbolQuad` from `SpriteEntry`+`icon-size`(×`pixelRatio`)+`icon-anchor`+`icon-offset`. Text-only tiles unchanged. Icons ride the extractor via a trailing optional `SpriteAtlasView = null` (the sole prod caller passes null ⇒ byte-identical until I5); `SymbolFeature` gains `Kind{Text,Icon}`+`IconQuad`. | An icon-image feature yields one icon label with the right anchor/size/UVs; unknown sprite → no label; anchor/offset shift the quad correctly; a text-only layer emits zero icon labels (parity). |
| **I4** ✅ | Sprite atlas texture + source (Unity): `SpriteSheet` (PNG→`Texture2D` via `LoadImage`, immutable) + a JSON+PNG sprite source (fetch, prior art `UnityWebRequestGlyphSource`/`GlyphSourceFactory`; fixture-backed source for tests, prior art `FixtureGlyphSource`); produce the runtime `SpriteAtlasView`. `SpriteSheet` **row-flips** on decode so the sprite texture shares the glyph atlas's "top-left coord == GetPixel(coord)" contract (⇒ I5 binds either texture through the same shader, no UV re-flip). | Fixture PNG loads to a `Texture2D` of the right dims (64×64); a known sprite's UVs sample the correct texel colour (marker red/star green/dot blue — the orientation pin + anti-flip guard); missing sprite URL → graceful no-icons (warn once), style still loads. |
| **I5a** ✅ | Data-path plumbing (Core+Unity, **§5.4-B**): `Kind` + `AtlasKind` on `PointStageInput`/`CandidateEmit` (carried but NOT consumed ⇒ inert); `StyledSymbolTileBuilder` shapes an icon `ShapedSymbol` (skip glyph shaping, `IconQuad`→`Layout`, `TextSizePx=OneEm` ⇒ scale 1). Fully **headless-verified**. | Icon label stages as a 1-box point candidate at scale 1 (box = quad + padding, no double-scale); its `PlacedQuad` carries the sprite UVs; text path byte-identical; Burst-vs-managed parity green. |
| **I5b** ✅ | Render (Unity): `SymbolIconWorld.shader` (RGBA, template + 3 deltas — straight sample, no SDF/halo, no UV re-flip) + `MapSymbolIconWorld.mat`; per-`(slot,AtlasKind)` draw partition binding the **sprite** texture for the icon bucket; `SymbolRenderLayer` icon material/presenter; `SymbolSubsystem` loads the `SpriteSheet` at `SetStyle` + threads the real `SpriteAtlasView` (the null→real flip). Compile-green + snapshots; **on-screen render EYEBALL-OWED**. | Shader compiles; the icon bucket binds the SPRITE texture + icon material (`SymbolIconWiringTests`); text snapshots byte-identical (icons off ⇒ no change). **Icon identity fenced to I6** (icons carry null `Text` ⇒ co-located distinct icons share fade/dedup). |
| **I6 code** ✅ | Icon identity: `SymbolFeature`/`ShapedSymbol` carry `IconImage` (the sprite name); folded into `CrossTileSymbolKey` (⇒ `PointFadeId`) so co-located distinct icons dedup/fade as two while the same icon across a zoom swap stays one. Guarded-skip fold ⇒ text keys/fades/snapshots byte-identical. | Two distinct co-located icons → distinct keys/FadeIds (RED pre-fix); same icon parent+child → one identity; text parity byte-identical. |
| **Padded repack** (§5.2.1) | `SpriteSheet` repacks the decoded sheet so every sprite carries a one-texel transparent border (`SpriteSheetPadder` plans the rects, `SpriteSheetComposer` writes the pixels); `IconQuadLayout` draws that border and retires the half-texel UV inset; the skirt is removed again for collision (`ToLayoutResult(quad, skirtPx)`, `CurvedGlyph.CellSkirt`). **Icon ink returns to nominal — 4.5–12.5 % smaller than it shipped — and edges are ~1 texel softer: EYEBALL-OWED.** | Silhouette phase sweep over a FULL-BLEED sprite at magnification 1.0/1.37/2.5/4.0 (RED: centroid frozen at 127.500 for four phases, then +1.000); nominal-ink size via two bar centroids (RED: 45.714 px vs 40.000 nominal, i.e. exactly ×8/7); packer separation/determinism/alias/degenerate/**overflowing rect**/fallback; composer byte-identical content + alpha-0 + RGB-replicated border; the content box unchanged for all nine anchors; the texels-per-drawn-pixel identity; the skirt's carrier chain driven through real extraction → builder → collision box; a pattern driven through the real `SpriteSheet` samples neither the neighbour nor the border. |
| **I6 eyeball** (maintainer) | The icon material is pre-wired into `Assets/Settings/Map/MapMaterialSet.asset`; liberty already carries the `sprite` URL — press Play and verify. | On-screen: icons draw at POI anchors, correct sprite/size/opacity, upright (not double-flipped), interleaved with text/fills; a real `sprite`-URL style lights up POI markers; icon-vs-text z-order. |
**Invariant across I1–I5:** *text-only styles are byte-identical* — an icon change never perturbs the existing
text snapshots (the icon path is inert when no `icon-image` resolves). I1–I3 RED-verify their regression teeth;
I4 pins orientation headlessly; I5a is fully headless; I5b proves compile + byte-identical text + the sprite
texture bind, with the rasterized result **eyeball-owed**.

**Landed (2026-07-18, autonomous plan→develop→review chain, each stage headless-gated + committed):** I1–I5b +
the I6 icon-identity fix — the full icon pipeline from sprite-JSON parse to an on-GPU draw partition, with
cross-tile identity keyed on the sprite name. **Remaining (I6, maintainer):** only the on-screen eyeball (the
icon material is pre-wired into `Assets/Settings/Map/MapMaterialSet.asset`; liberty already carries the `sprite`
URL — press Play).

## 5.6 Grounding (touch points)

Core: `Style/Symbol/PropertyNames`, `Style/Symbol/{StyleLayer,LayoutProperties,PaintProperties}`,
`Style/Symbol/SymbolFeatureExtractor` (the `isLine`/point branches — icons ride point), `Style/Symbol/SymbolFeature`
(icon fields), `Text/SymbolQuad` (the reused sprite/glyph-agnostic quad), `Text/TextQuadLayout` (prior art for
the new `IconQuadLayout`), a new `Text/Sprites/SpriteIndex`+`SpriteEntry`. The padded repack (§5.2.1) adds
`Text/Sprites/{SpriteBlit,SpritePadPlan,ShelfRectPacker,SpriteSheetPadder,SpriteSheetComposer}` — rect
planning and RGBA32 pixel composition, both engine-free, so the load-bearing border rule is checked
byte-for-byte on the fast `dotnet test` loop rather than behind a GPU readback. Unity: `Text/GlyphManager`/
`GlyphAtlasTexture` (prior art for the sprite `Texture2D`), `Rendering/Source/GlyphSourceFactory`+
`UnityWebRequestGlyphSource` (prior art for the sprite source), `Text/Placement/SymbolGatherPlan`,
`Text/Placement/SymbolPlacementSystem`, `Rendering/Style/SymbolRenderLayer`, `Shaders/Map/Symbol/Text/*`
(template for `Shaders/Map/Symbol/Icon/*`). Jobs: `SymbolProjectionJob`, `StageJob`, `CollisionJob`
(unchanged — texture-blind). Style: top-level `"sprite"` in `StyleDocument`/`StyleParser`; `liberty.json:17`.

---

# 6. Map-aligned line icons + `icon-rotate` (P-B)

**Status: LANDED.** Closes the last fence `docs/road-shields-design.md` §6 still carried. Before this,
liberty's `road_one_way_arrow` and `road_one_way_arrow_opposite` emitted **zero** labels: the extractor
gated icon resolution on `!isLine || iconAtAnchors`, and `icon-rotation-alignment` is unset on both layers,
so `AlignmentResolution` resolved `auto → Map` under line placement and fell outside the gate.

## 6.1 Three icon emit shapes, not two

| `symbol-placement` | resolved `icon-rotation-alignment` | shape | since |
|---|---|---|---|
| point | any | point icon label | I3 |
| line / line-center | viewport (explicit, or `auto` under point) | point-shaped icon at each along-line anchor, screen-upright — the road shields | D4 |
| line / line-center | **map** (explicit, or `auto` under line) | **curved label with exactly ONE glyph = the icon quad**, rotated to the projected tangent by the shader | **P-B** |

**The load-bearing observation: an along-line icon IS a one-glyph curved label.** A `CurvedGlyph.Cell` is a
`SymbolQuad` horizontally centred on 0, and an `IconQuadLayout` quad with the default `icon-anchor: center`
is exactly that shape. So the icon reuses the curved machinery wholesale — per-anchor candidates, the
projected-path arc walk, the per-glyph **rotated** collision box (`SymbolBox.BuildRotatedGlyph`), the baked
world tangent, and the world-anchored emit — for **zero** new record kinds, gather changes or oracle
changes. Two curved gates go inert at one glyph: `labelSpanPx == 0` so the end-spill test never rejects, and
the `text-max-angle` check is `g > 0`-guarded so it never fires.

Three alternatives were rejected: a new "point icon + per-frame tangent" record kind (forks
`LineAnchorPlacement`/the arc walk for a shape the curved path already produces); CPU-baked screen rotation
with no shader change (reintroduces, for icons, the rotation/anchor drift under motion that Stage AC fixed
for text); and a dedicated line-icon record kind (touches block/gather/mirror/oracle/batch for no gain).

**The shader half was blocker-grade.** `SymbolIconWorld_ForwardPass.hlsl` declared `alignFlags` as
"WRITTEN, UNREAD" and had no `tangentOS`, so without an HLSL change every arrow would draw unrotated —
all pointing screen-right. Stage AC's tangent branch is therefore **duplicated verbatim** into the icon
pass (a `Map/` shader layer may not include another's, by convention — the duplication is deliberate).

**Pairing stays out structurally.** A pair is proposed only in the extractor's `EmitAtAnchor`, on the point
path; an along-line icon is emitted from the line branch and can never carry a `PairRole`. No guard needed.

## 6.2 `icon-rotate` — a constant composed on top of the alignment

`icon-rotate` is a zoom-capable degrees value, converted to radians **once** at extract (the `Angle`
single-conversion rule) and composed as one addition on whatever the alignment already produced:

* **Point icons** (and viewport-resolved line icons): one term at `SymbolStagingMath.AppendPointHalf` —
  `BillboardRotationRadians(alignment, bearing) + IconRotationRadians(s.IconRotateRadians)`. Viewport ⇒
  `icon-rotate` alone; map ⇒ `bearing + icon-rotate`. 2D rotations commute, so one addition is the whole
  composition.
* **Along-line icons**: the renderer forces an along-line candidate's per-quad rotation to 0 (the shader
  supplies the tangent instead), so the constant rides on `CandidateEmit.ExtraRotationRadians`, written by
  `StageCurvedAnchor` and read at `WorldSymbolRenderer`. Curved text leaves it 0, so the renderer passes
  exactly the `0f` it used to hardcode. *Rejected:* reusing `PlacedQuad.RotationRadians`, because curved
  text writes a live tangent angle there and a future reader would double-rotate.

**Sign — measured, and the paper derivation that preceded it was wrong.** The staging frame's rotation is
**counter-clockwise-positive on screen**, while `icon-rotate` is clockwise-positive, so the two senses are
opposite and exactly one negation reconciles them: `SymbolBearing.IconRotationRadians`, called by *both*
`AppendPointHalf` and `StageCurvedAnchor`. `icon-rotate` keeps MapLibre's own sense on every carrier
(`SymbolFeature` → `ShapedSymbol` → the stage inputs) and flips only there, at the boundary where it becomes
a staging rotation.

The frame itself: `BillboardMath.BuildWorldQuad` rotates corners in the quad's **y-up local** frame and then
negates Y, which lands `OffsetPx` in a **y-DOWN screen** frame — a rotation read through a mirrored axis
reverses, so a positive `rotationRadians` appears counter-clockwise on screen. P-B originally argued the
opposite on paper (`N·R(θ)·N = R(-θ)` ⇒ clockwise), which was self-contradictory: it read the negation as
supplying the clockwise sense *and* left `OffsetPx` y-up, when the negation is precisely what makes
`OffsetPx` y-down. **Do not re-derive this on paper.** What settled it is a rendered tooth,
`SymbolIconRenderSnapshotTests.AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen`: it measures ink
*centroid* (not a bounding box) at a **45° road**, calibrating the buffer's sense against a rotation whose
physical direction is known — the road swinging counter-clockwise on the map, which a map-aligned icon
follows. It found `icon-rotate: 90` rendering at exactly +90° instead of −90°, a full inversion that every
other tooth was blind to: **90° is the smallest angle at which +φ and −φ differ**, and 180° — liberty's only
live value, its own inverse — can never show it. `SymbolStagingMathTests` part (d) pins the same sense on the
point path at the `OffsetPx` level, stated in the true (y-down) frame.

Related and still open: `SymbolBearing.MapAlignedSign` is the *other* sign on this composition and remains
**chosen, not derived**, deferred to an eyeball pass. It is the same class of risk this finding realised.

**`icon-keep-upright` — ruled out on purpose, not overlooked.** Its spec default is `false` (unlike
`text-keep-upright`, which defaults `true`), and for a one-way arrow that default is the **only correct**
behaviour: the arrow encodes the road's direction of travel, so flipping it to stay "upright" would point
it the wrong way. Arrows on westward roads therefore point left, and an asymmetric arrow sprite reads
mirrored end-to-end — that is what MapLibre draws, not a defect this stage introduces. Along-line icons
hard-set `KeepUpright = false`. **Do not "fix" this by copying `TextKeepUpright`'s default.**

## 6.3 Known limits (accepted)

* **KL-A1 — arrows doubled at tile seams. FIXED (along-line anchor clip).** Curved labels are still
  excluded from cross-tile dedup, but they no longer need it here: the LINE branch's shared `anchors` array
  is now filtered by `KeepAnchorsInsideTile` before any of the three arms consumes it, keeping only anchors
  whose resolved tile-space point `lerp(densePath[Segment], densePath[Segment+1], T)` satisfies
  `x >= 0 && x < extent && y >= 0 && y < extent`. A path left with **no** surviving anchor emits no label at
  all rather than an anchor-less one that can never place.
  * Applied to the **shared** array, so it covers curved text and along-line icons alike — the same defect
    with a different collision outcome (a ~20 px arrow box lets both seam copies survive collision; a
    ~100 px road-name box usually overlaps its twin, so one gets culled and the bug reads as quieter).
  * The **at-anchors** arm is byte-identical: `EmitAtAnchor` already applied the identical predicate to the
    identical expression over the identical inputs, so hoisting the test upstream only moves its position.
    `EmitAtAnchor`'s own clip **stays** — the POINT branch still feeds it unclipped anchor points.
  * The bound is **half-open because tile coordinates are per-tile**, not by preference: world position
    `x == extent` in tile T is `x == 0` in tile T+1, so `[0, extent]` duplicates every anchor on a shared
    edge and `(0, extent)` orphans it. Only `[0, extent)` makes adjacent tiles' anchor sets a true
    **partition** of world space — exactly one owner per position, no gaps.
  * The bound is a **hard 0** and deliberately does **not** read `MapViewConfig.FillTileBufferClip`. A
    buffer is a *margin* for geometry (a wider polygon; a cosmetic cost that degrades smoothly); anchor
    assignment is an *ownership partition*, and a partition with overlap is not a partition — any `b > 0`
    would re-instate exactly this defect. The knob keeps its fill-scoped name because fills remain its only
    reader.
  *Scope note:* the filter sits in the `isLine` branch, and `isLine` is `placement != Point` — so
  **`line-center` labels are clipped too**, not just `line`. That is the consistent outcome (a line-center
  label is a single along-line anchor and was equally duplicable), but worth stating, because "along-line"
  reads as the `line` mode alone.
  *Carve-out on "the at-anchors arm is byte-identical":* true per feature — a dropped anchor is exactly one
  `EmitAtAnchor` already early-returned on, before any `ordinal++` or `output.Add`. But `ordinal` is per
  `Extract` call, so when a LINE feature loses its curved label entirely, later features' `FeatureIndex`
  shift down by one. That index is a collision tiebreak only, is layer- and tile-local, and no cross-tile
  key reads it — 0 fixtures affected. Stated because "byte-identical" is otherwise read as unconditional.
* **KL-A2 — new per-frame collision/stage work.** At z16 each `oneway` road emits one candidate per
  `symbol-spacing` (250 px default) per tile, in the profiled hot path
  (`docs/symbol-label-perf-design.md`). Both layers are `minzoom: 16` and filtered, so this is not expected
  to matter — but `LastCandidateCount` at z16 is worth re-checking at the eyeball.
* **KL-A3 — mixed per-side alignment on one line layer is unpaired (NOT triggered today).** A style setting
  `text-rotation-alignment: viewport` *and* `icon-rotation-alignment: map` on the same line layer would get
  independent, unpaired text (at-anchors) and icon (along-line) candidates from the two branches. liberty
  never does this — the shields set both to viewport, the arrows are icon-only, the name layers are
  text-only. Recorded so the next reader knows it was considered, not missed. Not built for.
* **KL-A4 — `symbol-spacing` phase is not aligned across a seam (accepted).** Each tile computes anchors at
  `spacing·(k+0.5)` from **its own** copy of the path, so the interval spanning a seam is irregular even
  though no anchor is duplicated any more. Aligning phase would need a world-space anchor parameterisation
  shared between neighbouring tiles, which no part of this pipeline has. Pre-existing (it is today's
  curved-text behaviour), not introduced by the clip, not built for.
* **KL-A5 — a buffer-dominated path can now lose its label (accepted).** A path whose every anchor falls in
  the buffer strip emits nothing in this tile.
  The partition argument is airtight for *positions* — every world position has exactly one owning tile — but
  it does NOT carry to roads: each tile derives anchors from its own copy of the path at its own phase, so
  neighbouring tiles' anchor sets are not partitions of one shared set. In practice the neighbour owning that
  stretch emits its own anchors and the road stays labelled; a short stub clipping a tile corner is the case
  that can genuinely go unlabelled there.
  *Measured 0 occurrences across the committed fixtures on 2026-08-03* — a one-off manual replay of
  `LineAnchorPlacement.Compute` plus the predicate, NOT a standing check: no test reproduces it, and a
  fixture change can invalidate it silently.
* **KL-B1 — `icon-rotate` does not rotate the point collision box.** The point path's box is the unrotated
  `BoundsMin/BoundsMax` AABB even under a live map bearing, so rotating it for `icon-rotate` alone would
  make the convention inconsistent with the case it must match. (The *along-line* box IS rotated — by the
  tangent, via `BuildRotatedGlyph` — it simply does not include the extra constant.)
  **Amended by the pitch-alignment epic W3, for the map-pitched along-line arm only.** The *viewport*
  along-line box is unchanged (still `BuildRotatedGlyph`, tangent only, no `icon-rotate`). A *map-pitched*
  along-line glyph's box is now the screen AABB of its four projected WORLD corners
  (`SymbolBox.TryBuildProjectedWorldGlyph`) and it **does** include `icon-rotate` — it must, because that is
  what the renderer rotates the drawn corners by, and a box that omits it does not bound the ink.
  Numerically a no-op on every shipped style: the only production `icon-rotate` on a map-pitched layer is
  180°, which on a centre-anchored cell maps the cell onto itself and leaves the AABB identical. The point
  path is untouched by W3 and this bullet's own claim about it still stands.
* Out of scope, no liberty consumer: `icon-keep-upright` (§6.2), `icon-translate`/`-anchor`,
  `icon-text-fit`, `icon-color`, `icon-halo-*`.
* `icon-pitch-alignment` — parsed as of the pitch-alignment epic P1 (`LayoutProperties.IconPitchAlignment`,
  resolved via `AlignmentResolution.ResolvePitch`), so it is NOT the "no liberty consumer" case above:
  `road_one_way_arrow`/`road_one_way_arrow_opposite` resolve it to `map` (both `icon-rotation-alignment` and
  `icon-pitch-alignment` unset, `symbol-placement: line`). **CONSUMED as of the same epic's W1/W2/W3** — the
  "still unconsumed, no render caller yet" note that stood here was inherited staleness and has been false
  since W2. The resolved value reaches `CurvedStageInput.PitchAlignment`, where W1 makes it select the
  WORLD-metre arc walk (`SymbolStagingMath.StageCurved`'s `worldArc`), W2 makes it select the world-metre
  corner unit (`CandidateEmit.CornerMetresPerLogicalPixel` → `SymbolWorldPitchAlign.hlsl`), and W3 makes it
  select the projected-world-corner collision box. (docs/maplibre-spec.md's `text-pitch-alignment` table
  carries the `icon-pitch-alignment` row too, since `symbol — icon` has no per-key table of its own; that
  table's own rows are separately stale and are recorded for the merge step, not edited here.)

## 6.4 Grounding (touch points)

Core: `Style/Symbol/PropertyNames` (`icon-rotate`), `Style/Symbol/LayoutProperties` (`IconRotate`),
`Style/Symbol/SymbolFeatureExtractor` (`iconAlongLine`, `AlongLineIconContext`, `EmitAlongLineIcon`,
`KeepAnchorsInsideTile`/`IsAnchorInsideTile` — the KL-A1 along-line anchor clip),
`Style/Symbol/SymbolFeature` (`IconRotateRadians`), `Text/CurvedGlyph` (two producers, two vertical
conventions), `Text/Placement/SymbolStageInputs` (`CurvedStageInput.AtlasKind`, both `IconRotateRadians`),
`Text/Placement/CandidateEmit` (`ExtraRotationRadians`), `Text/Placement/SymbolStagingMath`
(`AppendPointHalf`'s one addition; `StageCurvedAnchor`'s emit), `Text/Placement/ShapedSymbol`. Unity:
`Text/StyledSymbolTileBuilder` (the point/curved icon split), `Text/Placement/SymbolTileBlockBaker`
(both inputs), `Text/Placement/WorldSymbolRenderer` (the along-line rotation arm),
`Shaders/Map/Symbol/Icon/SymbolIconWorld_ForwardPass.hlsl` (the ported tangent branch). Jobs: unchanged —
`StageJob` passes `CurvedStageInput` through wholesale.

**Maintainer eyeball DISCHARGED (2026-07-30):** confirmed on `OpenStreetMapLiberty.unity` — road shields
and road arrows both render. Headless teeth cover the emit shape, the atlas routing, the mesh geometry and
(via A6) the shader's tangent rotation, but not the live sprite sheet's `arrow` entry, which is only
observable at runtime — that is what this confirms. It also confirms the `icon-rotate` sign correction
(§ the 45°-tangent tooth) holds in the real renderer and not merely in the tooth that derived it.

Two things the eyeball did NOT settle. **Both have since been settled** — one by §7 (P-A), one below.

**CONFIRMED then FIXED:** arrows DID double at tile seams. Maintainer-confirmed on screen 2026-08-03, and
closed by the along-line anchor clip (§6.3 KL-A1). The fix is applied to the shared anchors array, so road
**names** at seams changed too — a name that rendered as two half-labels now renders once. Worth an eyeball
on names as well as arrows.

---

# 7. Non-centred icon+text pairing (P-A) — LANDED

The gate was discharged by maintainer confirmation — *"dots are still visible without text"* — so orphan
dots were a real on-screen artefact, not a theoretical gap. The cause was the pairing predicate in
`Style/Symbol/SymbolFeatureExtractor`: `centredPair` required `TextAnchor.Center` **and** zero `TextOffset`
**and** zero radial offset **and** `IconAnchor.Center` **and** zero `IconOffset`. Every non-centred symbol
failed that test and so never paired, which is why a dot could place while its name was collision-culled
independently. MapLibre's model is an *instance* of icon + text placed together, not two symbols that
happen to coincide.

**D-PA-1 — the predicate is `hasIcon && text != null`.** Symmetric and anchor/offset-independent; all five
conjuncts retire (including one per-feature `text-radial-offset` expression evaluation).

**D-PA-2 — the ICON stays the pair Owner.** `SymbolTileBlockBaker` derives a pair's `FadeId` from the
owner's icon identity, and `SymbolPairing` / `StageJob` / `AssertPairAdjacency` all encode
owner-immediately-then-rider. Flipping the roles to the text half was explicitly out of scope.

**D-PA-3 — `icon-optional`/`text-optional` still do NOT gate pair formation; D11 stays refuted, for a NEW
reason.** Stage C's argument (a centred pair's boxes overlap by construction, so un-pairing makes the halves
mutually exclusive rather than independent) genuinely does *not* transfer — non-centred boxes are disjoint
and would not self-block. The argument that does is the *instance* one: `text-optional` means "this instance
may render icon-only", which presupposes the instance. liberty's `airport` sets it and nothing else; under
D11 it would not pair, its halves would be collision-tested independently, and the text could place with the
icon culled — exactly what the flag forbids. Optionality remains a per-BOX verdict inside the
test-all-then-insert loop (`SymbolCandidate.OptionalBoxMask`).

**D-PA-4 — the line branch re-gates on BOTH suppressions.** The old `centredPair && iconAtAnchors` re-gated
the icon suppression but not the text one, so a line layer with `text-rotation-alignment: map` +
`icon-rotation-alignment: viewport` stamped an Owner with no Rider. Now `&& textAtAnchors` as well, which
makes `EmitAtAnchor`'s "PairedInstance implies both halves" true by construction. Byte-identical on every
shipped layer (the shields resolve both sides to viewport).

**D-PA-5 — naming follows meaning:** the local `centredPair` and the context's matching property both
renamed to `pairedInstance`/`PairedInstance`. "Centred" survives only where it is still true (the
shield-specific test names, §10).

**Affected layers.** Nine liberty layers newly pair; the three shield layers were already centred and the
new predicate is a strict superset, so their verdict is unchanged.

| layer | what makes it non-centred | notes |
|---|---|---|
| `label_city`, `label_city_capital` | `text-anchor: bottom` (+ `text-offset` −0.1/−0.2 em) | dot only **below z9**; `icon-allow-overlap: true`, `icon-optional: false` |
| `label_town`, `label_village` | `text-anchor: bottom` | dot only **below z10**; same flags |
| `airport` | `text-anchor: top`, `text-offset [0, 0.6]` | `text-optional: true` — D-PA-3's case |
| `poi_r1`, `poi_r7`, `poi_r20` | `text-anchor: top`, `text-offset [0, 0.6]` | minzoom 15/16/17 |
| `poi_transit` | `text-anchor: left`, `text-offset [0.9, 0]` | the only horizontally-offset pair |

**Consequence, intended:** `icon-allow-overlap: true` no longer buys a `label_*` dot a free pass — the
candidate's `AllowOverlap` is the AND of the halves, so a colliding city label now drops **both**. Expect
visibly fewer city/town dots at z<9/10. That is MapLibre-correct; do not re-widen the predicate to
compensate. The icon also now takes the lower `FeatureIndex` of the two, so placement order among equal
sort keys shifts (the pair is ordered by its icon's ordinal), and the pair's `FadeId` becomes the icon's.

**Two corrections to what this section previously recorded:**

1. **The gate's "z10–13" was wrong.** liberty gates the dot with
   `icon-image: ["step",["zoom"],"circle_11_black", 9|10, ""]` — the dot exists only **below** z9
   (`label_city`, `label_city_capital`) / z10 (`label_town`, `label_village`). At z10–13 those layers have
   no icon at all, so nothing there could orphan. The maintainer's observation was a low-zoom one.
2. **`poi_r1`/`poi_r7`/`poi_r20`/`poi_transit` have no committed test data** — the `poi` source layer is
   absent from every fixture in `Assets/Fixtures/`, and committing a new binary fixture was out of scope.
   They are covered only by hand-built layers carrying liberty's exact layout JSON. Their only real check is
   a maintainer eyeball at z15+. (`airport` is nearly the same story: the Berlin fixture *does* carry the
   aerodrome_label feature, but at tile x=4929 — outside `[0, extent)`, so the single-world clip drops it.
   T5 therefore runs the REAL `airport` layer over a hand-built tile.)

**Grounding.** Core: `Style/Symbol/SymbolFeatureExtractor` (`pairedInstance`,
`AnchorEmitContext.PairedInstance`, `EmitAtAnchor`). Teeth: `Style/SymbolPairPredicateTests` (T2–T8),
`Style/SymbolShieldExtractionTests.NonCentredPair_NowEmitsIconThenText_OwnerRider` (T1),
`Style/SymbolTestFixtures` (the shared loaders + `StageInputFor`). Nothing else changed — the halves'
anchors/offsets were already baked anchor-relative into their own quads and bounds by
`TextQuadLayout.Layout` / `IconQuadLayout.Layout`, so `SymbolStagingMath.StagePointPair` appending both at
the owner's `ScreenPx` was already correct for a displaced half. That premise is pinned by T3: staging the
rider from the *owner's* bounds — the precise way the premise fails — reds T3 and **nothing else in 2017
tests**. Do not read that as "T3 is the only tooth guarding the premise": perturb the layout/staging code it
rests on in other ways and several teeth notice (the dual review measured 7 and 5 failures for two such
injections). T3's claim is narrower and is the one that matters — it is what makes a *displaced* half's box
provably its own.

## 7.1 Review findings recorded at merge (dual-arm, 2026-08-03)

Non-blocking. Recorded here rather than fixed in-stage, per `AGENTS.md` ("non-blocking findings are
recorded, not necessarily fixed in-stage").

**F-PA-1 — a MapLibre divergence that P-A makes LIVE. Read this before blaming the predicate.**
`SymbolStagingMath.StagePointPair` sets the candidate's `AllowOverlap = owner.AllowOverlap && rider.AllowOverlap`,
and `SymbolCollision.SelectSurvivors` consults only that **candidate** flag — `SymbolBox.AllowOverlap` is
carried per box but never read there. MapLibre GL JS instead tests each half against the grid with *its own*
allow-overlap and then ANDs the two **verdicts**. The two agree in the case this stage is about (text blocked
⇒ both drop) and **diverge in the mirror case**: dot's box blocked, text's box free — MapLibre places both,
we drop both. That case was unreachable before P-A, because no shield layer sets allow-overlap. It is live
now: `label_city`/`_capital`/`_town`/`_village` all set `icon-allow-overlap: true`.
*Consequence:* slightly fewer city labels than MapLibre at z<9/10, **on top of** the intended R2 reduction.
**If the eyeball reads "too few labels", this is the suspect — not the pairing predicate.** Do not re-widen
the predicate to compensate. Fix shape when it matters: test each box with its own carried `AllowOverlap`,
keep the all-or-nothing AND on the verdicts.

**F-PA-2 — `AnchorEmitContext.PairedInstance` could be structural instead of hand-maintained.** It is an
`init` property set at two sites, and both reduce to exactly `HasIcon && Text != null`. A computed
`public bool PairedInstance => HasIcon && Text != null;` deletes both initializers and makes D-PA-4's bug
class — an initializer that forgets one of the suppression fences — *unrepresentable* rather than
fixed-once-and-commented. T2/T8 stay meaningful either way: they exercise the fences, not the field.

**F-PA-3 — per-half viewport culling.** `StagePointPair` gates the whole pair on the owner's `ScreenPx`, so
a rider displaced by up to 0.9 em (`poi_transit`) is culled or kept by the *icon's* position. Negligible at
liberty's offsets; real if `icon-text-fit` or larger offsets ever land.

**F-PA-4 — stale "centred pair" wording** survives in `SymbolStagingMath.cs`, `SymbolCandidate.cs:34`,
`CandidateEmit.cs:14`. All sit inside P-A's stop-listed files; fix on the next legitimate visit to each.
Related: `SymbolCollision.cs:180` justifies test-all-then-insert with "a centred pair's two boxes overlap by
construction" — still true of centred pairs, so the code is right, but it now reads as the general rationale
and no longer is (P-A's pairs are disjoint).

**F-PA-5 — `poi_r1`/`_r7`/`_r20`/`_transit` remain headlessly unverifiable** (no `poi` source layer in any
committed fixture), as do `label_town`/`label_village` at their real offsets. `poi_transit` is the only
horizontally-offset layer in the style and so the only exercise of a non-vertical pair. Maintainer eyeball
at z15+ is the only check.

---

# 8. The halo left the shader

The SDF text shader used to compute the glyph fill and its halo in ONE fragment and combine them
(`lerp(_HaloColor.rgb, textColor, fillAlpha)` + `max(fillAlpha, haloAlpha·haloA)`). That could not produce
correct text. With one quad per glyph and `ZWrite Off`, submission order is the only layering there is, so
the *next* glyph's quad painted its halo over the *previous* glyph's fill wherever the two quads overlap —
which, for kerned text, is most of them. No per-fragment combine can fix it: by the time glyph n+1's fragment
runs, glyph n's ink is already in the framebuffer and indistinguishable from background.

**The shader now renders one thing.** It takes a colour (vertex `COLOR`) and a pair of device-px widenings
(`WorldBillboardVertex.SdfWidenPx`, TEXCOORD7) that push the SDF fill edge out and widen the AA transition,
and emits that colour at the resulting coverage. `_HaloWidthPx` and `_HaloBlurPx` are gone from the CBUFFER,
the Properties block and `SymbolRenderLayer`'s binds. There is no halo branch, because there is no halo
concept — the fragment cannot tell the two runs apart and does not need to.

`_HaloColor` survives, but as something else entirely: not the old combine term, but the CONSTANT arm of
`text-halo-color`'s two-carrier split, the exact mirror of `_TextColor` (see §8.1).

**A halo is a second copy of the label's glyphs.** `WorldSymbolRenderer.Emit` writes each label's whole glyph
run twice into the same mesh: the halo run first, carrying `text-halo-color` in the colour stream and
`text-halo-width`/`-blur` in `SdfWidenPx`, then the text run with the text colour and zero widening. The two
runs are contiguous blocks of one index buffer, so the whole label's halo rasterizes before any of its text.

Decisions worth keeping:

- **Grouping is per label, not per layer.** One `CandidateEmit` carries every glyph of every line a label
  shapes to, so "per `Emit` call" *is* "per label". A full halo-layer-then-text-layer separation would also
  order two overlapping labels against each other, but that costs a second index accumulator per slot and
  collision already keeps overlapping labels from arising. Per-label is enough; it is what the Unreal
  renderer does.
- **One mesh, one material, one draw call.** Not two materials at two queues: index order inside one draw
  call is ordered by the rasterization rules, while two materials at the same `renderQueue` are not, and
  forcing them apart means a third `LayerSubSlot` — which drags in `LayerDrawOrder.QueueFor`,
  `RenderLayerSet.Build`, `SetDrawOrder` and the G7/D7 icon-under-text invariant.
- **The halo comes from where it was already evaluated, not from a new per-layer carrier.**
  `SymbolFeatureExtractor` has always evaluated `text-halo-color/-width/-blur` PER FEATURE into `SymbolPaint`
  — and nothing read them. They now ride `PointStageInput`/`CurvedStageInput` → `CandidateEmit` → the vertex
  stream, exactly as `text-color` does. A first cut resolved the halo per layer instead, to avoid touching
  those three blittable structs; that was the wrong trade, because it built a second path for a value the
  codebase already computed and discarded. Consequence: a **data-driven** `text-halo-*` now works, and the
  zoom-expression halo deferred as §7 risk 9 stops being deferred — both fall out of using the per-feature
  evaluation rather than a style-load-zoom snapshot.
- **`text-halo-color` is `.linear` on the CPU for the stream carrier**, in
  `SymbolPlacementSystem.LinearHaloColor` — the exact sibling of `LinearColor`. UMR-135 found that
  `_HaloColor` was a Color-TYPED material property, which Unity converts on upload, so pre-converting
  double-applied it; Unity does not convert a vertex stream, so on that carrier the conversion has to move
  upstream instead. Both statements are now true at once, one per carrier (§8.1). Same rendered colour
  either way. `SymbolHaloColorRenderTests` has an arm per carrier and carries the reasoning in its header;
  without it a future reader finds one rationale and "fixes" the other side back.
- **Width and blur stay LOGICAL px all the way to the emit**, where they take the logical→device conversion
  together (S107) against the LIVE ratio. So a dpr change re-scales the halo on the next Tick with no
  re-bake, no change detection, and no frozen-zoom bookkeeping — `SymbolRenderLayer.ApplyZoom` became a
  genuine no-op and its `_haloZoom`/`_haloDpr` pair is gone.
- **The halo copy is the same quad**, not an inflated one. A halo wider than the glyph cell's SDF padding
  clips at the cell edge — exactly as it did when one fragment computed both, so this is not a regression to
  chase.
- **A haloless label pays nothing.** A zero `text-halo-width` or a transparent `text-halo-color` gates the
  second run off, and a zero width is the spec default. Icons never take it — no `icon-halo-*` path.

## 8.1 …except one colour uniform, for the restyle ease

Merging the style-transitions work (UMR-95) put one uniform back. That branch made a **CONSTANT** `text-color`
ride `_TextColor` rather than the vertex stream, because a value baked into a mesh cannot be eased: the
in-place restyle path re-binds uniforms and never re-bakes a tile. `text-color` and `text-halo-color` are the
two paint keys the shipped liberty → liberty-night pair differs on across its symbol layers, so without both
of them free, the restyle gate refuses the whole document and the ease never runs at all.

So `text-halo-color` takes the same split: **Constant → the `_HaloColor` uniform, every other kind → the
vertex stream**, with the unused carrier left at identity white and the fragment multiplying the two. Exactly
one of them is ever non-white, so the colour is converted once and applied once on either path.

- **Which uniform a run uses is read off `SdfWidenPx`, in the vertex stage.** One material draws both runs,
  so a single tint uniform would paint the halo with the text's colour. The discriminator is exact rather
  than a threshold: `Emit` emits a halo run only when `text-halo-width > 0`, so a halo vertex always has
  `SdfWidenPx.x > 0` and a text vertex always has exactly `0`. It costs no vertex attribute and no flag bit —
  and a flag bit was not free, because `AlignFlags` is tested with float comparisons (`>= 1.5`) that a new
  high bit would break.
- **Only the colours, and only at Constant.** `text-halo-width`/`-blur` stay per-feature on the vertex stream;
  the restyle gate refuses a change to either, and refuses a non-Constant colour change on the same grounds.
  A colour's ALPHA also stays on the stream — for the halo it additionally decides whether the second run is
  emitted at all — so an alpha-only change refuses too.
- **What this costs.** A zoom-expression `text-halo-color` no longer eases across a restyle, where it did
  while the whole trio was uniform-bound. It is the same rule `text-color` already lives under, and the
  shipped pair uses constants throughout.

Two behaviour changes that are correct, not bugs:

- **Fade compositing.** A half-faded label's interior alpha is now `o + h·o·(1−o)` rather than `o`: the halo
  shows through the fill it used to be replaced by. That is what a real two-pass renderer does.
- **A data-driven `text-halo-*` now renders** instead of silently falling back to the base material's
  inherited halo, an arbitrary asset default. It was never a design position that it should not — only that
  no carrier reached the GPU.

**Residual, out of scope:** two tiles in the same layer are separate renderers at the same `renderQueue`, so
halo-over-fill across a tile boundary is still order-undefined. Global collision (D8) is what keeps those
labels from overlapping in the first place.

---

# 9. SDF text weight: where the AA band sits, and the knob that is still wrong

Two defects hid behind each other while the glyph atlas was handing out the wrong face (§8 of this doc /
the font-key fix). With the right faces drawing, both became visible at once.

**The AA ramp was centred on the outline.** `saturate(screenDist / aa + 0.5)` puts the 50% point exactly at
the iso, so half the band eats INWARD — up to `aa/2` device px removed from both edges of every stroke. On a
1–2 px stem that is most of the ink. It also caps coverage: glyph interiors peak at **0.878** of the field,
not 1.0 (thin stems never saturate a distance field), so a minified glyph — a distant label, a foreshortened
along-road name — can never reach full alpha and washes out. The `+ 1.0` form puts the whole band OUTSIDE
the outline: at or inside the iso is solid at any scale, and the falloff is the band just beyond.

**`_SdfEdge` was 0.60 against a 0.75 iso.** That is `0.15 × 8 = 1.2` texels of dilation per edge, ~2.4
texels of extra stroke — visibly bold, and easy to mistake for a font-weight bug. It was NOT per-source
calibration: measured, the live openfreemap PBFs and the committed fixture are the same fontnik bake
(global max 255, median glyph interior peak 224 ≈ 0.878, p10 219). The note in `GlyphPbfDecodeTests` carries
the measurement so the calibration argument is not re-derived. 0.60 predates the analytic AA and was a
compensator for the fwidth-based AA it replaced.

**Naming.** `_SdfSoftness` → `_SdfAaDevicePx` and `_SdfPixelRange` → `_SdfRangeTexels`. The old names
described implementation trivia; the new ones state the unit and whether the value is a look knob at all
(`_SdfRangeTexels` is not — it describes how the glyphs were BAKED). DEVICE px is qualified because this
shader also carries `_ScreenParamsLogical`, which is logical px, and mixing the two has bitten it before.

**Open — the band width is a constant where the model wants a function.** `_SdfAaDevicePx` is a fixed device-px
width for every glyph at every size. The physically right band scales with how many device px one unit of
field distance covers, i.e. with glyph size: one constant cannot be simultaneously right for a 10 px street
name and a 28 px city label, so the current setting is a compromise and small text still reads slightly
thin. Fixing it means deriving the band from `screenPxRange` (already computed in the fragment) rather than
from a uniform — a shader change, not a knob. The render fixtures will not catch a regression here: they
build their material from `Shader.Find` and assert ink counts and centroids with tolerances, so a
half-pixel edge shift passes. Only an eyeball tells.
