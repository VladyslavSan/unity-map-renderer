# Labels & symbols — design & SSOT

The symbol/label subsystem: how a label travels from tile bytes to a drawn (or culled) glyph, and the design
of each part — placement smoothness, curved along-line text, and projection (globe) support. Code lives in
`MapRenderer.Unity/Text/` (subsystem, per-frame placement) and `MapRenderer.Core/Text/` (engine-free shaping,
layout, metrics).

1. **[Pipeline flow](#1-pipeline-flow)** — the two-clock architecture; tile lifecycle → per-frame placement → cull → fade.
2. **[Smoothness & robustness](#2-smoothness--robustness)** — pull/reconcile, cross-tile identity, the fade state machine, the perf mechanisms.
3. **[Curved along-line text](#3-curved-along-line-text)** — `symbol-placement: line` / `line-center`.
4. **[Projection support](#4-projection-support-globe-ready-labels)** — labels under any `IProjection`, including the globe.
5. **[Icon support](#5-icon-support-sprite-symbols)** — `icon-image` sprite symbols; the sibling of text under one anchor.
6. **[Map-aligned line icons + `icon-rotate`](#6-map-aligned-line-icons--icon-rotate-p-b)** — the third icon emit shape.
7. **[Non-centred icon+text pairing](#7-non-centred-icontext-pairing-p-a)** — a pair is an *instance*, not a coincidence.
8. **[The halo is a second glyph run](#8-the-halo-is-a-second-glyph-run)** — a separately-coloured copy of a label's glyphs, not a shader term.
9. **[SDF text weight](#9-sdf-text-weight-where-the-aa-band-sits)** — where the AA band sits, and why its width is one raster pixel.

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

`TileManager`'s per-tile mesh-build kick (`KickMeshBuild`) drives the symbol pass: it asks `SymbolWorkerFactory` (an `ISymbolTileWorkerFactory`, implemented by `SymbolSubsystem`) for an
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
label (`SkippedSymbolCount` telemetry, throttled once-per-session warn) and lets the rest build + commit.
The case that needs it is single-run bidi: `CodepointTextShaper` throws on mixed strong-direction text
(RTL+LTR), which the liberty `["concat", name:latin, " ", name:nonlatin]` place field produces for every
Arabic/Hebrew-region place — without isolation every low-zoom world tile (which spans an RTL region) would
render **zero** symbols. **Known gaps:** **Pass 1** (glyph fetch/`GlyphPbfDecoder.Decode`) is unguarded — a
corrupt glyph-PBF for one label would blank a tile through the same mechanism; the isolation covers Pass 2
(shape/layout) only. Separately, because of the `when` guard above, a pre-cancelled token reaching a Pass-2
label that throws a non-`OperationCanceledException` bug fails the guard, so the exception propagates and
drops the whole build instead of being skipped — a hole in the per-label isolation guarantee, with no
observable impact and no test. Full UAX #9 bidi (actually *rendering* mixed-direction labels) is a separate,
deferred feature.

**Single-world anchor clip (point labels).** `SymbolFeatureExtractor` drops POINT features whose tile-local
anchor falls outside the tile's half-open `[0, extent)` bounds (per-axis). Low-zoom source tiles carry ±360°
world-copies / buffer duplicates of place-label anchors in the point layer, and `TileId.ToLonLat` does no
wrapping — an unclipped anchor outside that range projects to a render position exactly one world-width off,
even though the mesh path already clips to the tile server-side. Half-open so a shared-edge anchor
is owned by exactly one tile (`x==extent` in tile T is `x==0` in T+1). **Known gap:** the **LINE/curved**
branch's *anchors* are clipped on the same half-open predicate (KL-A1 in "Known limits (accepted)"), but its
**path vertices** are not: the extractor's LineString loop and `ProjectPath` call the same unguarded
`ToLonLat` on every decoded
vertex, so if the source ever ships an out-of-bounds LineString point the ±360° world-copy still fires for the
curved *geometry*. The finite-sheet camera (Mercator bounded pan/zoom) keeps off-world space off-screen so it
is not *drawn*, but a displaced path vertex would still distort the curved layout it carries — narrowed by the
anchor clip, **not** closed by it.

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
   TileManager.Tick(cameraProperties, ...)      // the slow clock (tile lifecycle) — cover select + request/release

3. if SymbolSubsystem.HasSymbolLayers:
     TileManager.CollectLoadedTileKeys(scratch)         // PULL the current loaded set
     SymbolSubsystem.ReconcileLoadedTiles(scratch, now) // release departed / restore cache-hit symbols
     SymbolSubsystem.PumpBuilds()                        // start ≤N queued builds, coalesce one atlas upload
     plan = SymbolSubsystem.CurrentBatch(sceneFrame, minCoverage, now)  // this frame's winner plan (+ coverage pre-cull)
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
time. Gathering the real per-symbol Structure-of-Arrays happens later, inside `SymbolPlacementSystem`'s native
mirror, not here.

The plan is rebuilt **every frame** from the reconciled tile set ("Tile lifecycle"; the reconcile itself is
async — `docs/labels-async-reconcile-design.md`), reusing its native lists (allocation-free once warm).
Between the cross-tile dedup and the plan fill, `CurrentBatch` also runs the tile-coverage pre-cull
("Tile-coverage pre-cull"), which **masks** rather than compacts: every collected winner stays resident
(`WinnerCount` counts them all) and a `Dropped` winner is hard-skipped downstream, in
`SymbolPlacementSystem.GatherSymbolPoints`.

The plan carries a **`WinnerSetVersion`**, bumped on every front-content change (reconcile swap / restyle /
dispose); `SymbolPlacementSystem` mirrors it into native buffers only when the version changes.

## 1.4 The per-frame placement loop — `SymbolPlacementSystem.Tick`

Everything camera-dependent lives here, over reused buffers (no per-frame GC). Stages:

```
Tick(sceneFrame, plan, atlas, dt, materials, spriteTexture)
 │
 ├─ PmGather        → GatherIntoMirror(plan)  : mirror the winner plan's native rows into per-Tick
 │                                               buffers, memoized on WinnerSetVersion
 ├─ PmCollideHarvest → HarvestCollision()     : complete LAST Tick's scheduled collision job and re-key its
 │                                               survivors by FadeId (B-4, Track B below)
 ├─ PmProject
 │   ├─ PmGatherPoints
 │   │   └─ GatherSymbolPoints(...) : Burst CullJob (per-record verdict) + CompactJob — flatten un-Dropped
 │   │                                winners' world points; a culled winner's offset is set to -1 (the
 │   │                                tile-coverage pre-cull already ran upstream, in CurrentBatch)
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

Five fade-out triggers happen **before** any projection/staging/collision, classified by `GatherTrigger` in
`CullJob` (verdict chain: dropped → departing → coverage → zoom → horizon → distance → none) and applied in
`GatherSymbolPoints`:

1. **Departing** (`SymbolDeparting`): the winner's tile is leaving cover ("Retain-as-departing"). Fades out
   unconditionally.
2. **Coverage-fading** (`SymbolCoverageFading`): the winner's tile just dropped below the coverage threshold
   and is easing out over the grace window before it is dropped from the plan entirely. Set by the
   tile-coverage pre-cull; a still-loaded, in-cover tile (distinct from `SymbolDeparting`'s leaving-cover
   meaning).
3. **Zoom** (`GatherTrigger.Zoom`): drop a symbol outside its own live zoom range.
4. **S3 horizon cull** (`GatherTrigger.Horizon`): drop a symbol whose anchor is hidden behind the globe's own
   bulk (no-op under a planar projection).
5. **B-3 distance cull** (`GatherTrigger.Distance`): drop an individual symbol beyond a threshold fraction of
   the camera's far plane (`SymbolMaxDistanceFraction × CurrentFarMetres`, computed once per Tick).

A triggered winner is **not** hard-dropped while its fade is still alive: gather keeps STAGING it (re-projected
to its live position) and forces its opacity toward 0 in emit — so it eases OUT in place instead of popping.
Only once fully faded does gather set its offset to `-1` (the Burst stage job's "skip"). This soft-cull is the
single mechanism behind all five; the stage, projection and collision jobs know nothing of any trigger.

**`SymbolPlacementSystem`'s own identity:** it is a plain class (NOT a MonoBehaviour) — the "placed every
frame" path (`ARCHITECTURE.md` § "Two geometry classes, two paths"), structurally distinct from the static
per-`(tile,layer)` `ITileRenderBackend` meshes it never touches (`SymbolPlacementStructureTests`
grep-checks this). Every on-screen symbol's screen-space AABB (`SymbolBox`, `text-padding` applied) runs
through `CollisionJob` — greedy, sort-key-driven, permutation-invariant selection — over reused arrays (no
per-frame managed allocation). A non-finite `symbol-sort-key` becomes `float.MaxValue` (sorts last) at
candidate build (`SymbolStagingMath.SanitizeSortKey`): with a NaN key (a style such as `["/", 0, 0]`) both
`a < b` and `a > b` are false, the comparator is intransitive, and the survivor set becomes
nondeterministic. This Tick's collision is *scheduled*, not run inline (see
the pipeline above): the world emit reads the PREVIOUS Tick's already-harvested verdict, and the current
Tick's own verdict is harvested at the START of the next one. A slot that produces no quads this Tick (no
atlas, empty batch, everything culled/suppressed) is HIDDEN, not left drawing stale content;
`WorldSymbolRenderer.EndFrame` owns this per-slot show/hide.

## 1.5 Tile-coverage pre-cull

A coarse step *before* the per-label pipeline: skip a tile's labels entirely when the tile covers less than ~N%
of the screen. Small-on-screen tiles are the horizon pile-up under tilt — most of their labels get
collision-culled anyway, so gather → project → stage → collide on them is wasted work. Dropping them whole
stabilizes per-frame label cost with barely any lost information. Complements the per-**label** B-3 distance
cull (a horizon *radius*): this is a per-**tile** *screen-area* metric, which catches the tilt-foreshortened
slivers a radius keeps.

**Where it runs.** The cull runs in `SymbolSubsystem.CurrentBatch`, **after** picking up the async reconciler's
cross-tile dedup (`docs/labels-async-reconcile-design.md`) and **before** filling the gather plan — before any
per-label work (glyph/quad copies, sRGB→linear, fade-id hashing) so a tile about to be dropped never pays for it:

```
CurrentBatch(frame, minCoverage, now)
    PickupCompletedReconcile(); ScheduleReconcileIfDirty()   // pick up the off-main A-3 dedup
    SymbolTileCoverageFilter.ClassifyActive(                 // ◄── pre-build cull (this section):
        blockTileKeys, frontResult.BlockId, frontResult.IsDeparting,    //   PER BLOCK — one classification per
        projection, frame.SceneOriginRender, viewProj, viewportLogicalPx, //  (source, tile), fanned out to
        frame.Rebase, minCoverage,                                        //  every winner sharing that block
        _coverageAbovePrev, _coverageAboveThisFrame, _coverageDepartingUntil, _coverageFadingTiles,
        now, DepartingGraceSeconds, _tileDecisions, _blockDecision, _planDecision, out culled)
    swap(_coverageAbovePrev, _coverageAboveThisFrame); PurgeExpiredCoverageDeadlines(now)
    _gatherPlan.Build(frontResult.BlockId, frontResult.LocalIndex,      // MASKS Dropped winners in place — every
        frontResult.IsDeparting, _planDecision, frontResult.OrderedBlocks, frontSetVersion) // winner stays
                                                                         // resident (winner plan); flags CoverageFading
```

Cull **after** dedup, not before/inside it: culling pre-dedup would change dedup *winners* — a <5%-coverage
child tile culled ahead of the A-3 pass would let its >5% parent win the finest-zoom-wins tiebreak
(`z = TileKey>>44`) and render a symbol the child's copy otherwise hides. Cull-after-dedup preserves winners
and keeps the dedup independent of the cull.

**Scope: active winners only.** A departing winner ("Retain-as-departing", a tile leaving cover retained for a fade-out) is
left at `Keep` unconditionally and never classified — a departing-only block touches none of this filter's
cross-frame state (deadline stamp, above-set, fading-set), regardless of its own tile's coverage.

The filter itself is an engine-free Core seam, **`SymbolTileCoverageFilter.ClassifyActive`** — Core, so it
compiles into `Tools/core-tests` (`SymbolTileCoverageFilterTests`) for a fast, RED-verifiable loop. It
classifies **per BLOCK, not per winner**: every winner produced from one baked `(source, tile)` block shares
that block's tile key, so the classifier resolves each unique tile's coverage ONCE (`SymbolTileCoverage`,
via a reused per-call cache) and fans the decision out to every winner sharing that block — O(blocks) tile
work plus an O(winners) fan-out. Each tile is classified **Keep / Fade / Drop**:

- **Keep** (`≥ threshold`): stays active; the A-4 fade eases it *in* if it is new. Clears any live fade deadline.
- **Fade** (`< threshold` but was visible): every winner on the tile stays resident in the plan, MASKED
  `CoverageFading` → gather's trigger 2 (see the per-frame placement loop) eases it *out* in place instead
  of popping.
- **Drop** (`< threshold`, steady/never-visible/grace-expired): every winner on the tile stays resident too —
  the masking model keeps `WinnerCount` unchanged — but is MASKED `Dropped` and hard-skipped downstream,
  in `SymbolPlacementSystem.GatherSymbolPoints`, which is where the perf win is realized.

**Fade-then-drop, not pop.** A tile crossing below threshold must fade out like every other cull, so
the filter keeps a small amount of reused, alloc-free cross-frame state on the subsystem: `_coverageAbovePrev`
/ `_coverageAboveThisFrame` (ping-pong `HashSet` of tiles that were ≥ threshold, self-bounding — each set is
cleared+refilled and swapped every call) and `_coverageDepartingUntil` (tileKey → wall-clock fade-out
deadline). A **fresh** above→below crossing (`_coverageAbovePrev` contains the tile) stamps `now + grace`
**once**; while below with a live deadline it keeps Fading; once `now ≥ deadline` — or if it was never visible
— it Drops. Crossing back above clears the deadline (fade in), and a later re-crossing re-arms it. Grace
(`DepartingGraceSeconds` = fade duration + 0.2 s) is longer than the fade, and the deadlines self-purge
(`PurgeExpiredCoverageDeadlines`) so an unloaded tile leaves no stranded state. The wall-clock `now` is threaded
from `MapView.LateUpdate` (`Time.timeAsDouble`, shared with `ReconcileLoadedTiles`).

```
ScreenCoverage(4 render corners, sceneOrigin, viewProj, viewport)
    → project each corner to screen (SymbolScreenProjection.TryProjectPoint)
    → if ANY corner is behind the near plane (or viewport degenerate): return +∞   ("never cull")
    → else: |shoelace(4 screen corners)| / viewportArea                            (fraction of screen)

IsCulled(coverage, minCoverage)  →  minCoverage > 0 && coverage < minCoverage
```

`minCoverage ≤ 0` (or a null projection) disables the cull (the kill-switch); `+∞` is never below a finite
threshold, so a near-plane-straddling tile is always kept. Default `MapViewConfig.SymbolTileCoverageCull =
0.05` (threaded through `MapView.LateUpdate` → `CurrentBatch`) — a maintainer eyeball-tunable. Telemetry:
`SymbolSubsystem.LastTileCoverageCulledCount` (the Drop count) + `SymbolPlacementSystem.LastCoverageFadingCulledCount`
(the fully-faded coverage-fade count, the gather-side companion).

**The transition is preserved, never popped.** A tile crossing below threshold **fades out** over the grace
window (via `CoverageFading`) — only a tile that was *never* on screen is dropped silently (nothing to pop).
This matches the rest of the label system, all of which fades rather than pops.

**Open items:** a green/red survived-vs-culled debug overlay (tune the threshold by eye);
tilt-scaling the threshold; explicit hysteresis *on the threshold itself* (distinct from the fade grace).
Culling ahead of the cross-tile dedup is rejected (it changes dedup winners — see above). Telemetry is surfaced through
`SymbolStoreTelemetrySnapshot.CoverageDroppedSymbols` / `SymbolPlacementTelemetrySnapshot.CoverageFadingSymbols`
→ the `MapTelemetryPanel` (beside the
distance cull), so the threshold is tunable by watching the live drop/fade counts. `viewProj` and the logical
viewport are single shared definitions (`SymbolPlacementSystem.ViewProj(Camera)` + `MapCamera.ViewportLogicalPx`)
read by both `CurrentBatch` and `Tick`.

## 1.6 Retain-as-departing — fading a tile out when it leaves cover

The B-3 distance / S3 horizon culls fade a winner still *in* the plan, and the tile-coverage pre-cull masks
Fade/Drop before the plan is filled. A normal **tile unload** is different: the tile leaves cover, its symbols
leave the collected set, the plan rebuilds without them, and they would pop. Retain-as-departing keeps them
in the plan for a grace window.

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
an unloaded tile still pops. The cache is on by default, so production and the demo get the fade. **Open
decoupling:** the fade only needs the labels for the grace window (~0.5 s), independent of the cache-hit
retention (minutes), so `_departing` could *own* the released entry for the grace window when the cache is
off (a second, short-lived retention path) instead of borrowing the cached FIFO.

---

# 2. Smoothness & robustness

**Rejected: a stateless placement layer.** Clearing everything and rebuilding projection → collision →
billboard quads from scratch every frame, with labels aggregated per `(source, tile)` and no cross-frame or
cross-tile memory, has four user-visible problems:

1. **Pop, not fade.** A label that stops being placed (collision, or its tile leaves cover) vanishes instantly.
2. **Blink when idle.** Even with zero camera motion, the per-frame rebuild lets an async build landing / a
   transient tile release / a hair of collision-input drift flip a survivor for a frame → flicker.
3. **Line labels slide on zoom.** `symbol-placement:line` anchors at fixed *screen-px* distances from the
   projected line start move to a different world point whenever the projected length changes.
4. **Cost.** At some zooms ~20k symbols reach the per-frame pass. Track B keeps that pass cheap (Burst
   projection, culling before projection). It does not skip the pass when the camera is idle, because
   B-1 is rejected.

**Rejected: lifecycle push-callbacks** (`Released`/`Restored` mirroring the tile lifecycle into the symbol
store) — they carry ordering and reentrancy hazards and a hand-threaded cache-vs-evicted bit. **Rejected: a
single glyph atlas page** — it overflows on a large script set (a CJK capacity limit).

Track A is one idea: the placement layer is a **state machine keyed by a stable cross-tile symbol id** that
**owns each label's on-screen lifetime** — it decides what stays drawn and for how long, independent of which
tiles are loaded. Track B is performance; Track C is capacity.

## Track A — placement state machine

**A-1 — Pull / reconcile foundation.** *Data delivery* and *lifecycle* are separate. Membership is
**pulled**: each frame the subsystem asks `TileManager` for the current loaded `(source,tile)` set + a
**version stamp** (bumped only when the set changes) and reconciles — a tile in the set with no labels yet →
build; a tile-with-labels no longer in the set → hand to the fade-out path (A-4), not an instant drop;
present-and-built → no-op. Self-healing (a missed change is corrected next frame) with no reentrancy. Tile
data still arrives by a push — the per-tile build kick (`TryBeginBuild`, "Tile lifecycle") — but that push
carries data only, never lifecycle. The `SymbolTileStore` is the reconcile's backing store; the reconcile
runs off-main (`docs/labels-async-reconcile-design.md`), and the pull/no-reentrancy invariant governs it.

**A-2 — Stable WORLD line anchors.** Anchors are computed **once at build time in tile/world space** —
positions along the line spaced by `symbol-spacing` at a reference scale — and each is carried as a world
position. Per frame, each *anchor* is projected and the glyphs are laid out around it by walking only the
*local* neighbourhood of the projected line. The anchor does not slide on zoom because it is a fixed world
point, not a screen offset. `line-center` = one anchor at the world midpoint. A stable anchor also gives a
stable id: `LineFadeId` is `(tile, feature, anchorIndex)`, so build-time anchors keep it constant across
frames, which the A-5 total order relies on.

**A-3 — Cross-tile symbol identity + dedup (seamless no-op replacement).** A point symbol's cross-tile id is
`(quantize(anchorRender), layer, text, icon image)` (`CrossTileSymbolKey`), where `anchorRender` is the
pre-RTC render-space anchor — the same for a geo point at any zoom — snapped on all three render axes to the
fixed `CrossTileSymbolKey.CanonicalGridMeters` grid, so small reprojection differences between zoom levels
collapse to one id. The dedup runs off-main, inside `SymbolReconciler` (`Dictionary<DedupKey, DedupEntry>`,
the integer-keyed form of the same grid — see `docs/labels-async-reconcile-design.md`); at **collection**
time, when the same id appears in more than one tile, one copy wins deterministically (finest zoom, then
lowest tile key). Same id across a tile swap ⇒ the winner **persists** ⇒ opacity stays 1 ⇒ no fade cycle, a
true no-op — and the symbol count drops by the duplicate factor. Curved (line) labels are not deduped; each
keeps a per-anchor `LineFadeId`.

**A-4 — Placement state machine + fade.** The placement layer holds a persistent opacity per fade id
(`_fadeOpacity`, keyed by `FadeId`). Each `Tick` eases each staged candidate's opacity toward
`(placed && !Suppressed && !ForceFadeOut) ? 1 : 0` (collision-placed, in its live zoom range, and not
fading out on a gather trigger) over a fixed **fade duration** (`FadeDurationSeconds`, 0.3 s;
`symbol-fade-duration` is not wired), emits it while its opacity is above epsilon, and decays any id not
staged this Tick toward 0, dropping it at epsilon. Fade state lives here, not in the tile-keyed store,
because the placement layer **owns the on-screen lifetime**; a label whose tile leaves cover keeps staging
through retain-as-departing, so it fades at its live position. Cheap on the GPU: `PlacedQuad.Color.w` is the
per-vertex alpha the shader emits — no shader/vertex-format cost. This also masks the idle blink: a
one-frame flip becomes a sub-perceptual alpha step rather than a visible pop.

**A-5 — Collision fighting / placement hysteresis.** A hair of camera motion can flip which of two near-tied
candidates wins the greedy collision, like z-fighting; a *slowly-moving* camera re-runs collision every frame
and can oscillate a marginal pair. **Sticky placement:** at EQUAL sort key, a candidate placed last frame
(the incumbent, keyed by the A-3/A-4 identity) sorts first in `ComparePlacementOrder`; a newcomer with a
higher-priority sort key still sorts first and wins, so incumbency never blocks a higher-priority label. This
is the one place placement history feeds back into collision. Because incumbency only ever raises priority,
last frame's survivor set is a one-step fixed point — provided the order is **total**. Its last key is
`FadeId`: without it, a curved feature's repeated anchors share `(SortKey, FeatureIndex, TileKey)` and
compare equal, the unstable sort resolves the tie arbitrarily, and the incumbency feedback can drive a limit
cycle even on a still camera. Together with the finite sort key (see "The per-frame placement loop"), this
keeps `ComparePlacementOrder` a strict total order.

## Track B — performance

**Rejected: B-1, a static-frame skip.** Reusing last frame's billboard buffers when the view-projection, the
reconcile version, and the fade state are all unchanged is smooth on a still camera and janky on a moving
one — a motion-keyed 0-or-full cost cliff (`docs/symbol-label-perf-design.md` § "Constraints").

**B-2 — Burst-jobified projection (`SymbolProjectionJob`).** Candidate world anchors are gathered into a
`NativeArray<double3>` and projected in a Burst job → screen + depth + validity mask. This attacks the
per-anchor matrix-mul cost that dominates projection when the camera moves.

**B-3 — Far-distance label culling (in `CullJob`).** In a tilted view the far half of the frustum compresses a
huge ground area into a thin horizon band, where labels pile up, get collision-discarded, and jitter
(projection is numerically unstable as depth → the far plane). So labels are culled **before** projecting,
by render-space distance from the camera beyond `SymbolMaxDistanceFraction × CurrentFarMetres`. Labels want
their own, tighter distance cut than the tile far-plane policy (`GeometryAwareFarPlane` /
`RaySphereFarPlane`), because they stop being legible well before tiles stop drawing. This is the per-label
radius companion to the per-tile coverage cull ("Tile-coverage pre-cull").

**B-4 — Pipelined placement — the decision is decoupled from the render (`SymbolPlacementSystem`).** The
collision runs in the frame "loophole" (the worker-thread time after LateUpdate, while the render thread
submits) because the placement DECISION is one frame stale. Every frame (cheap, on-path): project the
*current* label set to screen (positions must be live) and emit the survivors **decided at the end of the
previous frame**, through the A-4 fade. End of frame (off-path): `Schedule` the collision over THIS frame's
projected boxes as a Burst job and `Complete()` it only at next frame's LateUpdate. This holds because the
camera moves a hair per frame (a 1-frame-stale survivor set over *current* positions matches the fresh
answer almost always) and A-4 absorbs the residual. Collision is serial (ONE Burst job, not a parallel
fan-out). The full contract is `docs/symbol-label-perf-design.md` § "The collision verdict applies one frame
late (B-4b)".

## Track C — capacity

**C-1 — Multiple glyph-atlas pages.** One fixed atlas page overflows on a large script set. `GlyphAtlas` is
`Texture2DArray`-backed: when the current page's packer can't fit a glyph, a new fixed-size page opens and
becomes current, and `GlyphAtlasEntry.Page` records which layer a glyph's UV samples from — every page shares
the same `(width, fixedHeight)`, so a layout site's `uv = AtlasOrigin / Size` math never changes. Page count is
capped (`GlyphAtlas.MaxPages`); a glyph that would need a page beyond the cap is dropped as overflow
(`OverflowCount`) rather than growing without bound.

---

# 3. Curved along-line text

`symbol-placement: line` / `line-center` — per-glyph text that follows a line feature's geometry (the real
subsystem, not a straight-block approximation). Clean-room from the public MapLibre Style Spec.

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

**This is the `viewport`-aligned case.** Under `*-pitch-alignment: map`, `text-size` is a WORLD size, not a
device-pixel one — the same settled model as `line-width` (`docs/line-rendering-design.md` § "The width
model"): the style
value fixes a world quantity once, and the perspective divide does the rest, so a receding map-pitched label
shrinks with distance instead of holding a constant screen size. A map-pitched curved label therefore walks
its arc length in WORLD metres (`SymbolStagingMath.StageCurved`'s `worldArc`), not screen pixels — see the
`icon-pitch-alignment` entry in "Known limits (accepted)" for the corner-unit mechanism
(`CandidateEmit.CornerMetresPerLogicalPixel`).

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
different anchors/rotations instead of N sharing one. The rest is two pure Core helpers plus the per-frame walk.

> **Road shields (docs/road-shields-design.md).** `symbol-placement: line` does not always curve: when the
> layer's resolved rotation-alignment is `viewport` (an explicit style choice, not the `auto`→map default this
> section describes), symbols are laid out **upright at each along-line anchor** instead — the road-shield
> look. See that doc's D3/D4; every `auto`-aligned line layer covered above takes the curved path.

**Core pieces (engine-free, headless-tested):**
- **`PolylineArcMath`** — static functions over a screen-space `float2` polyline: `BuildCumulative` walks the
  vertices into a cumulative-length table, and `At(arcDistance)` (given that table plus a caller-owned
  resumable cursor) returns `(float2 point, float tangentRadians)` by lerping within the containing segment
  (tangent = segment direction via `atan2`; clamps to the ends).
- **`CurvedTextLayout`** — `Layout(ShapedRun, IGlyphAtlasView) → IReadOnlyList<CurvedGlyph>` where
  `CurvedGlyph { float ArcCenter; SymbolQuad Cell }`. A single forward pass (mirrors `TextQuadLayout.PlaceGlyph`)
  places each glyph's cell relative to **its own** pen origin, centered **horizontally** on the glyph's
  advance-center, and **vertically on the run's optical (cap-band) centre** — the same
  `TextQuadLayout.OpticalCentreBelowReferencePx` a centre-anchored point label applies (`docs/road-shields-design.md`
  D12), so a curved and a point label of the same string sit the same way on their anchor.
  `ArcCenter` = cumulative advance to that center.

  > **Rejected: centering each glyph on its own ink.** The centering above adds exactly **one constant per
  > LABEL**, with no per-glyph term, so every glyph in a run keeps its exact relative offset and descenders
  > still descend. Centering each glyph on its own ink instead is a different rule, and it is the one that
  > would make ascenders and descenders bob independently of each other. `CurvedTextCentringTests`'s per-glyph
  > tooth goes RED on that alternative while a cap-glyph-only tooth stays green.
  >
  > **Why the Burst job needs no change for this.** The cell→screen/world map is **linear and homogeneous about
  > the anchor** (`BillboardMath.BuildWorldQuad`, `SymbolBox.BuildRotatedGlyph`), so cell `y = 0` lands on the
  > anchor under every branch and the tangent rotation is correct for **any** vertical placement of the cell —
  > baseline-anchoring is not what makes that work, it is one arbitrary choice among many the linearity
  > permits.

**Placement (`SymbolPlacementSystem`, per frame):** each build's `ShapedSymbol` carries a `Path`/`PathUp` span
(in its `SymbolTileBuffer`) for line labels (empty for point labels — `AnchorRender` untouched) plus
`Placement` + the extractor's evaluated `SpacingPx`. Project each path vertex with `TryProjectAnchor` (skip the label if any vertex is behind the
camera — partial-visibility clipping is a refinement); build a cumulative-length table
(`PolylineArcMath.BuildCumulative`) over the projected polyline, which also returns the polyline's total length;
choose anchor arc-distances (`line-center` → half that total; `line` → centered multiples of `symbol-spacing`,
label needs `labelWidthPx` ≤ the total); apply keep-upright (walk reversed when the net direction points
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
tile edge-to-edge. The `text-max-angle` **cull** gate stays on the raw **segment** tangent
(`StageCurvedAnchor` decouples cull from render: cull answers "is the path too kinky to place a label here"; the
chord blend is purely how each glyph is oriented), so the cull verdict does not depend on the orientation
rule and the Burst/managed staged-count parity holds.

> **Known limitation (open) — corner spacing compression.** Glyphs are positioned at equal
> **arc-length** along the polyline (`PolylineArcMath.At`), but render as straight rigid quads. Where the line
> bends, the straight-line (chord) distance between adjacent glyph centres is **shorter than their arc-length
> advance**, so the boxes overlap by ≈ `advance − chord` at sharp corners — letters visibly bunch at real road
> bends (worst where a vertex falls mid-label). The chord orientation above corrects the *rotation* at bends
> but NOT this *spacing* compression. A real fix is structural (chord-distance glyph stepping, or a corner-rounded
> placement path) with real trade-offs (chord-walking distorts text at sharp corners; rounding changes label
> shape). Not blocking; curved-label polish.

**Zero per-frame alloc.** The per-frame walk runs over **reused** scratch (a persistent projected-polyline
buffer grown geometrically, a reused walker), never a fresh `float2[]`/`List` per label per frame.
`CurvedTextLayout` stays build-time (its per-glyph result cached on the label), so only the walk is per-frame.

**Collision (the one placement concern curved text adds).** A point label submits one `SymbolBox` (AABB). A
long curved label's single AABB would bound a whole road → almost nothing places. So a curved label submits
a **per-glyph box set** and places iff its boxes don't collide with already-placed boxes. The unified model:
`SymbolCandidate` is a contiguous box range; `CollisionJob` treats a point label as a 1-box candidate and a
curved label as an N-glyph-box candidate, **all-or-nothing** (one colliding glyph drops the whole
label; a placed label blocks across its whole run). Candidates sort; boxes stay put (grid stores absolute
indices; adjacent glyph boxes never self-block).

**Limitation no headless test observes:** the tangent is screen-space, so it is correct under bearing/tilt
(taken from projected points), but the look under rotation/tilt, the keep-upright flip threshold, and the
`line` spacing/centering need a maintainer eyeball.

---

# 4. Projection support (globe-ready labels)

The subsystem renders correctly under any `IProjection` — notably `SphericalProjection` (the globe) — at the
same projection-correctness the fill/line mesh path has via `ProjectPointsJob<TProj>`. A label on the globe
sits on the curved surface at its true geographic anchor, is culled behind the horizon, and (for line labels)
follows the projected curve.

## Why the per-frame pipeline needs only four globe mechanisms

The per-frame pipeline ("Pipeline flow") is almost entirely **screen-space**. At build time it projects geodetic anchors/paths
to render space through `IProjection.ProjectPoint` (`SymbolFeatureExtractor.cs`); the tile-coverage filter
projects a tile's corners the same way (`SymbolTileCoverageFilter.ProjectCorner`); each frame it projects those
to screen and does arc-walk / AABB / billboard in pixels. So **billboarding, per-glyph orientation, and
collision are already projection-generic** — glyph quads are built in 2D screen space
(`BillboardMath.BuildWorldQuad`), and curved-text rotation is the screen-space tangent of the already-projected
polyline (`SymbolStagingMath`). There is no world up-vector or great-circle tangent to get right. What the globe
needs beyond that is four mechanisms, one foundational, tagged S1–S4 below.
All four are byte-identical on Mercator.

### S2 — the projection seam applies the rebase rotation (foundational)

The Unity camera places the look-at at the world origin in an idealized Y-up ENU orbit frame
(`MapCamera.SyncToCamera`), so `ViewProj` expects points in the **rebased look-at frame**. Mesh tiles
honor this: `TileToSceneRebased = Rebase·(origin − sceneOrigin)` (`FloatingOrigin.cs`),
`Rebase = transpose(TangentBasisAt(lookAt))` (`SceneFrame.cs`). The label seam carries a `float3x3 rebase` into
the same rotation, applied after translating and before `viewProj` —
`SymbolScreenProjection.TryProjectPoint` and `SymbolTileCoverageFilter.ProjectCorner` both take it. On Mercator
`Rebase = identity` (inert); under `SphericalProjection` an anchor away from the look-at needs it or it
projects to the wrong pixel. This is the keystone the other three globe mechanisms below build on — all globe label
correctness rests on the seam applying `Rebase` correctly.

### S1 — cross-tile dedup quantizes all three render axes

`CrossTileSymbolKey.For` quantizes render `.x`/`.y`/`.z`, not just `.x`/`.z`. Under
`SphericalProjection.ProjectPoint` render X/Z ∝ cosφ (even in latitude) and Y ∝ sinφ (odd), so a two-axis key
would collide a same-longitude pair mirrored across the equator (e.g. 30°N and 30°S) at any grid size, merging
two distinct labels into one. Byte-identical on Mercator, where surface labels have render.y ≡ 0. The grid is
the fixed `CrossTileSymbolKey.CanonicalGridMeters` (see "Track A").

### S3 — the horizon cull

`SymbolProjectionJob.OutValid` alone culls only behind-camera points (`clip.w ≤ 0`); without more, a
far-side-of-globe label projects in front and draws through the earth (labels draw with `ZTest Always`, so no
depth hides it, and its collision box still contests near-side space). `HorizonCull` tests each anchor against
`IProjection.TryGetHorizonOccluder` as a rad-0 point — the algebra `FrustumTileSelector` already uses — and
folds the result into the cull as a **fade** (`GatherTrigger.Horizon`), not a hard `OutValid` drop, so an
occluded label eases out instead of popping. Correct only once the seam applies `Rebase` (S2) — the occluder is
expressed in the rebased look-at frame. Mercator returns no occluder ⇒ no-op.

### S4 — long line segments subdivide instead of chording

Projecting only the original MVT vertices renders a long segment as a straight screen chord instead of the
projected curve. `SymbolFeatureExtractor` subdivides the tile-local path per `IProjection.MaxRefineAngleRad`
(Mercator = ∞ ⇒ no split), the same policy the mesh line path uses (`SubdivideJob`)
via the shared engine-free `Core` helper
`LineCurvatureSubdivision`. **The hazard this avoids:** `LineAnchor.Segment` indexes the path vertex array —
computed against the tile-local path (`LineAnchorPlacement.Compute`) but resolved against the render path
(`SymbolStagingMath`). Subdividing only the projected path (not the tile-local one first) would desync those
indices silently (in range, wrong vertex); subdividing the tile-local path first and feeding the SAME finer
sequence to both anchor-compute and projection keeps them aligned.

**Cast overflow in the subdivision-count formula (architecture-dependent).** `LineCurvatureSubdivision` shares
the mesh path's `(int)math.ceil(ang / maxRefineAngleRad)`, which casts an out-of-range double for a pathological
tolerance — and the cast's failure mode is **architecture-dependent**: x64 truncates to `int.MinValue` (defeats
the `< 1` guard, so it returns 1 — *no* subdivision where the most was needed); arm64 saturates to
`int.MaxValue` (still trips the `> MaxCurveSegments` cap, so it looks correct at 128). The formula clamps in
double space *before* the cast (`(int)math.clamp(math.ceil(…), 1, MaxCurveSegments)`), which is deterministic on any
architecture (and also handles NaN → `MaxCurveSegments`). **Instrument blind spot:** the two teeth guarding this
only go RED on x64 — the Unity EditMode gate (x64 Mono/Rosetta) can catch a regression here; the fast
`dotnet test` loop (native arm64) passes them fix-or-no-fix, so an arm64-only contributor cannot trust it as a
tripwire for this specific defect.

Screen-space collision needs no change for any of S1–S4 — it is already projection-blind; it only needs
occluded labels removed first, which S3 provides.

## Open: globe end-to-end validation (S5)

Labels are not validated end-to-end on the globe (near-hemisphere-only visibility, curved road text
tracking the projected curve to the limb, collision stable under a slow globe rotate) because that needs a
committed globe label fixture and an exercised globe camera path on screen, and neither exists
(`SphericalProjection.ScreenToGround`/`GroundToScreen` exist; an interactive globe demo does not). This is an
on-device eyeball, not a headless tooth. S1–S4 do not depend on it: all four are byte-identical on Mercator.

**Rejected alternative: a geodetic dedup key** (store/quantize `GeoCoordinate`). The dedup is render-space so
that per-tile quantization noise in one feature's anchor collapses into one cell (`CrossTileSymbolKey.For`);
geodetic storage reintroduces an equivalent tolerance plus more plumbing, and 3-axis render quantization
already keeps equator-mirrored labels apart. One tunable worth surfacing once a globe is on screen: the
horizon-cull grazing margin.

---

## Grounding (touch points)

`MapRenderer.Unity/Text/`: `SymbolSubsystem` (queue/pump/store, + the pre-build tile-coverage cull in
`CurrentBatch`), `SymbolTileStore` (active/cached/departing), `SymbolReconciler` (the off-main
cross-tile dedup — `docs/labels-async-reconcile-design.md`), `Placement/SymbolPlacementSystem` (`Tick`),
`Placement/SymbolGatherPlan` (the per-frame winner plan), `SymbolFeatureExtractor` (`ProjectPath`,
`LineAnchorPlacement.Compute`). `MapRenderer.Core/Text/`: `CodepointTextShaper`, `TextQuadLayout`,
`CurvedTextLayout`, `Placement/SymbolStagingMath` (`StageCurved`, tangent), `Placement/PolylineArcMath`,
`Placement/CrossTileSymbolKey`, `Placement/LineAnchor`, `Placement/SymbolTileCoverage`,
`Placement/SymbolTileCoverageFilter` (the pre-build cull), `Placement/SymbolScreenProjection` (the
projection seam), `Placement/BillboardMath`. `MapRenderer.Jobs/Symbols/`: `CullJob` (the per-record
`GatherTrigger` verdict, including the B-3 distance and S3 horizon culls), `CompactJob`, `SymbolProjectionJob`
(`OutValid`), `StageJob`, `CollisionJob` — the billboard build itself is not a job: `WorldSymbolRenderer`
calls `BillboardMath.BuildWorldQuad` directly at emit time. `MapRenderer.Core/Coordinates/`: `IProjection`
(`ProjectPoint`, `TryGetHorizonOccluder`, `MaxRefineAngleRad`), `SphericalProjection`, `WebMercator`,
`CameraPoseMath`. `MapRenderer.Unity/View/`: `FloatingOrigin`. `MapRenderer.Unity/Rendering/Backend/`:
`SceneFrame`. Mesh-path prior art: `SubdivideJob`, which shares the engine-free `Core` helper
`LineCurvatureSubdivision` with this path, `ProjectPointsJob<TProj>`, `FrustumTileSelector`.

## Relationship to other work

- **Projection support** makes the label subsystem projection-agnostic (`docs/coordinates-and-projections.md`),
  as the mesh path is through `ProjectPointsJob<TProj>`. It is independent of the tile geometry IR
  (`docs/tile-geometry-ir-design.md`); both follow the two-waist principle (tile-local frames for placement
  math, projection at a defined seam).
- **Camera-driven property re-evaluation** (`docs/smooth-transitions-design.md` § "Camera-driven property
  re-evaluation") is where a label's zoom-dependent paint would smoothly re-evaluate — orthogonal to
  projection support (which is *where* an anchor projects, not *when* its style re-evaluates).
- The build-time half (decode → per-layer extract) rides the per-layer tile pipeline
  (`docs/per-layer-tile-processing-design.md`).

---

# 5. Icon support (sprite symbols)

The icon pipeline runs end to end through the on-GPU draw, with cross-tile identity; the actual rasterized
result is verified by a maintainer eyeball, not a headless test — no static-frame snapshot can prove an icon
draws at the right sprite, size, and orientation the way it can prove text is byte-identical (see "What
I1–I6 name"). A `symbol` layer has two decoupled elements sharing one anchor: **text** (`text-*`, glyph-SDF
atlas — the sections above) and **icons** (`icon-*`, a *sprite* — a named rectangle in a pre-baked sprite
sheet). Icons are the simpler sibling: **no shaping, no per-codepoint glyph fetch, no curved layout** — one
feature → one anchor → one sprite quad. The data/layout path reuses the text seams almost verbatim
(`SymbolQuad` is glyph/sprite-agnostic; the point-label projection/collision/billboard machinery is
text-agnostic screen-space). What icons add is a **second atlas** (sprite sheet, not the
dynamically-packed glyph atlas) and a **second material** (plain RGBA sample, not SDF+halo).

## 5.1 Scope fences — what "simpler than text" means

The "icons are a simpler version of text" framing holds **only** with these fences. They are load-bearing;
a change may not quietly cross one.

- **IN:** `symbol-placement: point` icons; `icon-image` (incl. **data-driven** — evaluated per feature, an
  unknown sprite name is skipped, not fatal); `icon-size` (with sprite `pixelRatio` applied); `icon-offset`;
  `icon-anchor`; `icon-rotation-alignment` (point: `auto`→`viewport`); `icon-allow-overlap`;
  `icon-ignore-placement`; `icon-padding`; `icon-opacity`. **Non-SDF (RGBA) sprites only.**
- **OUT (not built):**
  - **`icon-text-fit`(+`-padding`)** — the spec's icon-sized-to-text stretch model (resizing an icon's box
    to fit its paired text box).
  - **SDF / recolorable sprites** — the `"sdf": true` sprite variant + `icon-color`/`icon-halo-*`. Leaving
    these out keeps the icon shader trivial (a straight `tex2D` sample, no SDF median-distance, no halo).
    `icon-color`/`icon-halo-*` may be parsed and carried, but stay inert without an SDF path.
  - `icon-translate` and `icon-image` **stretchable** (`content`/`stretchX/Y`).
- **Not fenced:** `icon-line-placement` (icons along a line / `symbol-placement: line` with an icon) works in
  both resolved shapes ("Map-aligned line icons + `icon-rotate`"): a `viewport`-resolved line icon emits as
  an upright point-icon at each along-line anchor (`docs/road-shields-design.md` D4); a `map`-resolved line
  icon (`icon-rotation-alignment: map`, e.g. `road_one_way_arrow*`) emits as a one-glyph curved label rotated
  to the line tangent (P-B). `icon-keep-upright` is decided — along-line icons hard-set it `false` (see
  "`icon-rotate` — a constant composed on top of the alignment"). `icon-pitch-alignment` is parsed and
  consumed ("Known limits (accepted)").

**`text-optional`/`icon-optional` and the combined text+icon collision box are supported, not fenced.** A
paired icon+text symbol is ONE `SymbolCandidate` spanning both boxes, tested all-or-nothing; `*-optional` is
a per-box collision verdict via `SymbolCandidate.OptionalBoxMask` (bit 0 = owner/icon, bit 1 = rider/text;
pinned in `SymbolShieldExtractionTests`). See "Non-centred icon+text pairing (P-A)", D-PA-3, and
`docs/road-shields-design.md` D11.

## 5.2 The sprite sheet (vs. the glyph atlas)

A style-spec **sprite** is a pre-baked sheet: one PNG (`ofm.png`) + one JSON index (`ofm.json`) mapping each
sprite name to a rectangle. The style's top-level `"sprite"` URL (liberty.json: `.../sprites/ofm_f384/ofm`)
gets `.json`/`.png` (and `@2x` for hi-DPI) appended. Index shape (clean-room, from the public spec):

```
{ "airport-11": { "x": 0, "y": 0, "width": 22, "height": 22, "pixelRatio": 2, "sdf": false }, … }
```

Contrast with `GlyphAtlas` (C-1 in "Track C"): the glyph atlas is **dynamically packed** at runtime as codepoints arrive.
A layout site bakes a glyph's UV as `texel / Size` at layout time, so if `Size` changed later — an atlas
that keeps growing to fit new glyphs — every UV baked before the growth would silently point at the wrong
texel: a tile built early would sample low and its text would read as garbled. `GlyphAtlas` is therefore
allocated **big and FIXED** (`SymbolSubsystem.AtlasDimension`) so `Size` never changes; once a page fills, a
glyph that no longer fits opens a NEW page (its own buffer, same fixed size) instead of growing the current
one, so an already-baked UV is never invalidated. The sprite sheet is **fixed and pre-baked** — decoded
once, never grows, UVs are stable. So the icon atlas is far simpler: a parsed
`SpriteIndex` (name → rect) over an immutable texture. `pixelRatio` is the sheet's DPI scale: the sprite's
**logical** size is `width/pixelRatio` × `height/pixelRatio`, and `icon-size` scales *that*.

### 5.2.1 Sampling the sheet — bilinear + a one-texel padded repack

The sheet binds **`FilterMode.Bilinear`**, and `SpriteSheet` **repacks it at decode time so every sprite
gets a one-texel transparent border**. The two are one decision and neither is correct alone.

**Why not nearest-neighbour.** An icon's magnification is `icon-size × dpr / pixelRatio`. The sheet is
fetched @1x and `dpr` is `Screen.dpi / 160`, so the product is essentially never an integer — and
nearest-neighbour is exact *only* at integer magnification. Off it, each source texel covers `N` or `N+1`
device pixels and **which** depends on the quad's sub-pixel phase, so panning re-quantises an icon's
interior every frame: the pixels inside the icon warp while zooming or panning.

It is resampling, not geometry, because of one structural fact: `BillboardMath.BuildWorldQuad` gives all
four corners the **same bitwise `anchorLocal`** plus static per-corner `OffsetPx`, so a quad is **rigid** in
screen space. An anchor precision error therefore *translates* an icon and can never deform its interior —
which rules out the entire geometry/precision family and leaves resampling.

**Why the border — the silhouette.** Bilinear alone keeps the interior stable but not the outline: an
unpadded icon's outline wobbles ±1 device pixel while panning, because:

1. The shipped style's sheet (`tiles.openfreemap.org/sprites/ofm_f384/ofm`) has **228 of its 264 sprites
   full-bleed** — ink touching the rect edge — and **371 abutting sprite pairs, none separated by even one
   pixel**.
2. MSAA is **off** project-wide: `Assets/Settings/RPAsset.asset` `m_MSAA: 1` and
   `ProjectSettings/QualitySettings.asset` `antiAliasing: 0`. One binary coverage sample per pixel per
   polygon edge.
3. For a full-bleed sprite the silhouette **IS** the quad's polygon edge, so it flips whole pixels in and out
   as the quad slides sub-pixel. No sampler setting can help: bilinear can only produce a soft edge if there
   is a transparent texel to ramp into, and a full-bleed sprite has none.

So the border is neither a filter change nor a shader change — it is a **content** change. `SpriteSheet`
repacks the decoded sheet, giving every sprite its own cell with a one-texel border, and `IconQuadLayout`
**draws** that border. The silhouette becomes a *texture alpha edge* with a one-texel ramp, which bilinear
antialiases. (The sheet counts above are measured from the published sheet + its JSON index, which are
style assets.)

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
  (50.0 px on the same fixture). This one makes the icon LARGER, not smaller — the "squeezed into a
  smaller share of the quad" reading is backwards, because the quad's UV span, not its extent, is what
  sets the drawn size of a texel.

The invariant that excludes both, checked in Core (`IconQuadLayoutTests`):

> **texels-per-drawn-pixel is the same for the border and for the content** —
> `uvWidth × sheetWidth / quadWidth == PixelRatio / iconSize`, independent of the sprite and of the padding.

**The content rect stays the content rect.** `SpriteEntry.X/Y/Width/Height` mean the sprite's own ink; the
repack only *relocates* them, and `SpriteEntry.Padding` records how many texels of border surround them.
That single decision keeps every other consumer independent of the padding:

* `IconQuadLayout`'s logical-size maths (`entry.Width / entry.PixelRatio`) reads the content rect, so
  padding cannot change `icon-size` arithmetic or silently grow an icon.
* `FillPattern.TryResolve` reads the relocated content rect, which is what a point-sampled
  `frac()`-wrapped pattern must have. `LogicalSizePixels` and every pattern period do not depend on the
  padding.
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
disjointness is provable from its shape. Determinism is a hard requirement (the sheet must not differ
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

**The fallback is a degraded mode, not a free one.** There is only one sampling path, so a `Padding == 0`
sprite is drawn edge-to-edge — and on an unpadded, abutting, full-bleed sheet a bilinear edge tap then
reaches into the neighbouring sprite. The fallback therefore **has neighbour bleed**. That is the accepted
price of a single sampling path: do not re-add a conditional inset for it, because two sampling code paths
is how this bug class returns.

Cost on the real sheet: ~**+20 %** sheet area (and VRAM), once, at style load. Icon silhouettes are ~1 texel
× magnification device px soft at the edge, which is the antialiasing.

**Rejected: a half-texel UV inset.** Insetting each sprite's UV rect by half a texel also keeps bilinear's
edge taps off the neighbouring sprite, but never draws the sprite's outer half-texel, which magnifies
content by `W/(W−1)` (worse for a small sprite: e.g. 14.3% on an 8px sheet rect, 4.8% on a 22px one). The
padded border does that job with nothing to reach across and no magnification. The contract scale is
`PixelRatio / iconSize` texels-per-drawn-pixel — the unpadded quad and the padded quad both draw at it —
so the inset's `W/(W−1)`-inflated scale is not a contract to preserve.

**No mip chain.** Mips on a *packed* atlas average neighbouring sprites together at every level ≥ 1 —
a worse artifact than the minification aliasing they would fix.

**`fill-pattern` shares the texture, and keeps point sampling.** `filterMode` is state on the **texture**,
not on a sampler, and `RenderLayerSet` binds ONE shared sprite texture — as `_MainTex` for icon materials and
`_PatternMap` for fill layers. `Fill_LitInput.hlsl` therefore samples patterns through an inline
`sampler_PointClamp` so pattern filtering does not depend on the shared texture's `filterMode`. The border
does **not** replace that: it is *transparent*, and a pattern's tiling seam must continue
into the opposite edge's pixels rather than fade out. Patterns keep sampling point-wise inside their content
rect and never touch the border — pinned by `FillPatternThroughSpriteSheetTests`, which is the only test that
drives a pattern through the real `SpriteSheet` (`FillPatternSnapshotTests` builds its own `FilterMode.Point`
texture and never touches it).

**Open follow-ons (not built).**

* **P1 — wrap-replicated pattern borders.** The same `SpriteSheetPadder`/`SpriteSheetComposer` mechanism with
  a second border-fill *role* selected per sprite (opposite-edge replication instead of transparent). Only
  then can `Fill_LitInput.hlsl` drop `sampler_PointClamp` and take a correctly-filtered bilinear tap at a
  tiling seam. The role is not derivable from the sprite JSON — it depends on which style layers reference
  the sprite as `fill-pattern`.
* **P2 — minified icons.** `label_village` / `label_town` / `label_city` dots run at `icon-size` 0.2–0.5, i.e.
  magnification 0.30–0.75. The ramp's on-screen width is `1 texel × magnification` device px, so below 1× the
  whole ramp is sub-pixel and padding cannot fix it. That needs mips-with-per-sprite-guard-bands or a
  downsampled sprite variant.

**Only a phase sweep observes this.** A test that renders ONE static frame cannot see a defect whose whole
signature is "the render changes when it should not". The trap compounds: at 1× magnification
nearest-neighbour is a pixel-perfect blit, so the *interior* resampling defect is invisible at the one
setting an eyeball checks first — and 1× is where the unpadded *silhouette* wobble is worst. The guards are
the sub-pixel phase sweeps in `SymbolIconResamplingTests`:
`IconInterior_TracksSubPixelPhaseSmoothly_DoesNotSnapToTheTexelGrid` and
`IconSilhouette_TracksSubPixelPhase_ForAFullBleedSprite`.

## 5.3 Architecture — where each piece lives (mirrors the text path)

| Concern | Text | Icon |
|---|---|---|
| Atlas index (Core, engine-free) | `GlyphAtlas`/`GlyphAtlasEntry` (dynamic) | **`SpriteIndex`** (parsed once; name→`SpriteEntry{x,y,w,h,pixelRatio,sdf}`) |
| Style parse (Core) | `Symbol.LayoutProperties`/`PaintProperties` (`text-*`) | **`icon-*`** on the same `Symbol.StyleLayer` (Layout/Paint; `PropertyNames`) |
| Extract (Unity) | `SymbolFeatureExtractor` → `SymbolFeature` (text) | same extractor emits **icon** `SymbolFeature`s (icon fields) |
| Layout → quad (Core) | `TextQuadLayout`/`CurvedTextLayout` → `SymbolQuad[]` | **`IconQuadLayout`** → one `SymbolQuad` (sprite UVs) |
| Atlas texture (Unity) | `GlyphAtlasTexture` + `GlyphManager` | **`SpriteSheet`** (PNG→`Texture2D`) + a sprite source (JSON+PNG fetch) |
| Per-frame batch (Core) | `SymbolBatch` (`Kind{Point,Curved}`) | icon records in the batch (see "The load-bearing decision (I5)") |
| Draw (Unity) | `SymbolRenderLayer` + `SymbolTextWorld.shader` (SDF) | **`SymbolIconWorld.shader`** (RGBA) + a sprite-texture bind |

The build-time half rides the same per-layer tile pipeline (`TileSymbolLayerProcessor`); the per-frame half is
the same `SymbolPlacementSystem.Tick`. Collision is the same global grid — an icon is just another candidate box.

## 5.4 The load-bearing decision (I5): how icons ride `SymbolBatch`

`SymbolBatch`, the Burst stage job, and the billboard build all switch on `SymbolPlacementKind{Point,Curved}`
— how a symbol is *placed* — which is a **different axis** than `SymbolKind{Text,Icon}` — what atlas its
glyphs are drawn *from* (`SymbolPlacementKind.cs`). The icon draw must bind a different texture (the sprite
sheet) than the glyph atlas, and the design keeps that a property of the second axis: **an icon rides
the `Point` staging path and carries an atlas discriminator**, rather than adding a third `SymbolPlacementKind`
value. `PointStageInput.AtlasKind` / `CurvedStageInput.AtlasKind` / `CandidateEmit.AtlasKind` carry
`SymbolKind.Text` or `SymbolKind.Icon` through staging to the emit/draw boundary, which is the only place
that needs to know which texture to bind — staging and collision are texture-blind, since an icon is a
single axis-aligned quad, the degenerate point-text case.

**Rejected: a new `SymbolPlacementKind.Icon`.** A point-like record whose quad's UVs index the sprite sheet,
routed to a separate material slot/draw with the sprite texture bound, is the cleanest separation on
paper, but it touches every `SymbolPlacementKind` switch (stage job, billboard build, the gather mirror)
for a distinction the draw step alone needs. The atlas discriminator carries the same information at the
one site that reads it.

## 5.5 What I1–I6 name

Each tag names a mechanism, not a plan step:

- **I1** — `SpriteIndex` (Core): parses sprite JSON into name → `SpriteEntry`. Backed by a committed,
  network-free fixture (`Assets/Fixtures/sprites/sample-sprite.{json,png}`).
- **I2** — `icon-*` style parse (Core): the scope fences' IN keys land on
  `Symbol.LayoutProperties`/`PaintProperties`, data-driven where the spec allows it.
- **I3** — extract (Unity) + layout (Core): `SymbolFeatureExtractor` emits an icon `SymbolFeature` per point feature
  whose `icon-image` resolves to a known sprite (unknown → skipped, not fatal); `IconQuadLayout` builds the
  single `SymbolQuad` from the resolved `SpriteEntry` + `icon-size`(×`pixelRatio`) + `icon-anchor` +
  `icon-offset` (`SymbolFeature.Kind{Text,Icon}`). Along-line icons are the line branch's shapes ("Three
  icon emit shapes, not two").
- **I4** — sprite atlas texture + source (Unity): `SpriteSheet` decodes the sheet PNG into an immutable
  `Texture2D`, **row-flipped** so the sprite texture shares the glyph atlas's "top-left coord ==
  `GetPixel(coord)`" contract — one shader binds either texture with no UV re-flip.
- **I5a/I5b** — how an icon rides the staging/render pipeline; "The load-bearing decision (I5)" states it
  (icons ride the `Point` staging path with an atlas discriminator) and its data-path/render consequences.
- **I6** — icon cross-tile identity: `SymbolFeature`/`ShapedSymbol` carry `IconImage` (the sprite name), folded
  into `CrossTileSymbolKey` so two distinct co-located icons dedup/fade as two while the same icon across a
  zoom swap stays one; the fold is guard-skipped on `IconImage == null`, so a text symbol's key/fade is
  untouched.

**Invariant across I1–I5:** a text-only style is byte-identical to the icon path being absent — the icon path
is inert whenever no `icon-image` resolves.

**Verification split.** I1–I3 (Core) and I4/I5a are fully headless-verified, including orientation, staging,
and Burst/managed parity. I5b (the shader + draw partition) is compile- and snapshot-verified for text
byte-identity and for binding the sprite texture/material — the actual rasterized icon (sprite, size, opacity,
orientation, at the right anchor) is a maintainer eyeball, the same class of gap as the S5 globe eyeball
("Open: globe end-to-end validation (S5)"): a static-frame snapshot can prove text did not regress, not what
an icon looks like on screen. The icon material is pre-wired into the material set assets under
`Assets/Settings/Map/` (`LitMaterialSet.asset`/`UnlitMaterialSet.asset`), and the shipped `liberty` style
carries a `sprite` URL, so that eyeball needs no extra setup.

## 5.6 Grounding (touch points)

Core: `Style/Symbol/PropertyNames`, `Style/Symbol/{StyleLayer,LayoutProperties,PaintProperties}`,
`Style/Symbol/SymbolFeature` (icon fields), `Text/SymbolQuad` (the reused sprite/glyph-agnostic quad),
`Text/TextQuadLayout` (prior art for `IconQuadLayout`), `Text/Sprites/SpriteIndex`+`SpriteEntry`.
The padded repack ("Sampling the sheet") uses
`Text/Sprites/{SpriteBlit,SpritePadPlan,ShelfRectPacker,SpriteSheetPadder,SpriteSheetComposer}` — rect
planning and RGBA32 pixel composition, both engine-free, so the load-bearing border rule is checked
byte-for-byte on the fast `dotnet test` loop rather than behind a GPU readback. Unity: `SymbolFeatureExtractor`
(the `isLine`/point branches — icons ride point), `Text/GlyphManager`/
`GlyphAtlasTexture` (prior art for the sprite `Texture2D`), `Rendering/Source/GlyphSourceFactory`+
`UnityWebRequestGlyphSource` (prior art for the sprite source), `Text/Placement/SymbolGatherPlan`,
`Text/Placement/SymbolPlacementSystem`, `Rendering/Style/SymbolRenderLayer`, `Shaders/Map/Symbol/Text/*`
(template for `Shaders/Map/Symbol/Icon/*`). Jobs: `SymbolProjectionJob`, `StageJob`, `CollisionJob`
(texture-blind). Style: top-level `"sprite"` in `StyleDocument`/`StyleParser` and in `liberty.json`.

---

# 6. Map-aligned line icons + `icon-rotate` (P-B)

`road_one_way_arrow` and `road_one_way_arrow_opposite` resolve `icon-rotation-alignment: auto` to `Map` under
line placement (the third row of the table below), and the extractor emits a label for that case.

## 6.1 Three icon emit shapes, not two

| `symbol-placement` | resolved `icon-rotation-alignment` | shape | mechanism |
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
with no shader change (brings to icons the rotation/anchor drift under motion that the shader-side tangent
avoids for text); and a dedicated line-icon record kind (touches block/gather/mirror/oracle/batch for no
gain).

**The shader half is load-bearing.** `SymbolIconWorld_ForwardPass.hlsl` reads `alignFlags` and `tangentOS`;
without them every arrow draws unrotated — all pointing screen-right. The text pass's tangent branch is
**duplicated verbatim** into the icon pass, because by convention a `Map/` shader layer may not include
another's.

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
  `StageCurvedAnchor` and read at `WorldSymbolRenderer`. Curved text leaves it 0. *Rejected:* reusing
  `PlacedQuad.RotationRadians`, because curved text writes a live tangent angle there and a future reader
  would double-rotate.

**Sign — measured, not derived.** The staging frame's rotation is **counter-clockwise-positive on screen**,
while `icon-rotate` is clockwise-positive, so the two senses are opposite and one negation reconciles them:
`SymbolBearing.IconRotationRadians`, called by *both* `AppendPointHalf` and `StageCurvedAnchor`.
`icon-rotate` keeps the spec's own sense on every carrier (`SymbolFeature` → `ShapedSymbol` → the stage
inputs) and flips only there, at the boundary where it becomes a staging rotation.

The frame itself: `BillboardMath.BuildWorldQuad` rotates corners in the quad's **y-up local** frame and then
negates Y, which lands `OffsetPx` in a **y-DOWN screen** frame — a rotation read through a mirrored axis
reverses, so a positive `rotationRadians` appears counter-clockwise on screen. The tempting paper derivation
(`N·R(θ)·N = R(-θ)` ⇒ clockwise) is self-contradictory: it reads the negation as supplying the clockwise
sense *and* leaves `OffsetPx` y-up, when the negation is what makes `OffsetPx` y-down. **Do not re-derive
this on paper.** The authority is a rendered tooth,
`SymbolIconRenderSnapshotTests.AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen`: it measures ink
*centroid* (not a bounding box) at a **45° road**, calibrating the buffer's sense against a rotation whose
physical direction is known — the road swinging counter-clockwise on the map, which a map-aligned icon
follows. It uses `icon-rotate: 90` because **90° is the smallest angle at which +φ and −φ differ**, and
180° — liberty's only live value, its own inverse — can never show a sign inversion.
`SymbolStagingMathTests` part (d) pins the same sense on the point path at the `OffsetPx` level, stated in
the true (y-down) frame.

Open: `SymbolBearing.MapAlignedSign` is the *other* sign on this composition and is **chosen, not
derived**, pending an eyeball pass — the same class of risk.

**`icon-keep-upright` is `false`, by decision.** Its spec default is `false` (unlike `text-keep-upright`,
which defaults `true`), and for a one-way arrow that default is the **only correct** behaviour: the arrow
encodes the road's direction of travel, so flipping it to stay "upright" would point it the wrong way.
Arrows on westward roads therefore point left, and an asymmetric arrow sprite reads mirrored end-to-end —
the spec-default behaviour, not a defect. Along-line icons hard-set `KeepUpright = false`. **Do not "fix"
this by copying `TextKeepUpright`'s default.**

## 6.3 Known limits (accepted)

* **KL-A1 — the along-line anchor clip keeps arrows from doubling at tile seams.** Curved labels are
  excluded from cross-tile dedup and do not need it here: the LINE branch's shared `anchors` array is
  filtered by `KeepAnchorsInsideTile` before any of the three arms consumes it, keeping only anchors
  whose resolved tile-space point `lerp(densePath[Segment], densePath[Segment+1], T)` satisfies
  `x >= 0 && x < extent && y >= 0 && y < extent`. A path left with **no** surviving anchor emits no label at
  all rather than an anchor-less one that can never place.
  * Applied to the **shared** array, so it covers curved text and along-line icons alike — the same defect
    with a different collision outcome (a ~20 px arrow box lets both seam copies survive collision; a
    ~100 px road-name box usually overlaps its twin, so one gets culled and the bug reads as quieter).
  * The **at-anchors** arm sees nothing new: `EmitAtAnchor` applies the identical predicate to the
    identical expression over the identical inputs. `EmitAtAnchor`'s own clip **stays** — the POINT branch
    feeds it unclipped anchor points. One side effect: `ordinal` is per-`Extract` call, so a LINE feature
    that loses its curved label entirely to this filter shifts every later feature's `FeatureIndex` down by
    one. That index is a collision tiebreak only, layer- and tile-local — no cross-tile key reads it.
  * The bound is **half-open because tile coordinates are per-tile**, not by preference: world position
    `x == extent` in tile T is `x == 0` in tile T+1, so `[0, extent]` duplicates every anchor on a shared
    edge and `(0, extent)` orphans it. Only `[0, extent)` makes adjacent tiles' anchor sets a true
    **partition** of world space — exactly one owner per position, no gaps.
  * The bound is a **hard 0** and does **not** read `MapViewConfig.FillTileBufferClip`. A
    buffer is a *margin* for geometry (a wider polygon; a cosmetic cost that degrades smoothly); anchor
    assignment is an *ownership partition*, and a partition with overlap is not a partition — any `b > 0`
    would re-instate exactly this defect. The knob keeps its fill-scoped name because fills remain its only
    reader.
  *Scope note:* the filter sits in the `isLine` branch, and `isLine` is `placement != Point` — so
  **`line-center` labels are clipped too**, not just `line`. That is the consistent outcome (a line-center
  label is a single along-line anchor and was equally duplicable), but worth stating, because "along-line"
  reads as the `line` mode alone.
* **KL-A2 — along-line icons add per-frame candidates.** At z16 each `oneway` road emits one candidate per
  `symbol-spacing` (250 px default) per tile, in the per-frame placement pass
  (`docs/symbol-label-perf-design.md`). Both layers are `minzoom: 16` and filtered.
* **KL-A3 — mixed per-side alignment on one line layer is unpaired (NOT triggered today).** A style setting
  `text-rotation-alignment: viewport` *and* `icon-rotation-alignment: map` on the same line layer would get
  independent, unpaired text (at-anchors) and icon (along-line) candidates from the two branches. liberty
  never does this — the shields set both to viewport, the arrows are icon-only, the name layers are
  text-only. Not supported.
* **KL-A4 — `symbol-spacing` phase is not aligned across a seam (accepted).** Each tile computes anchors at
  `spacing·(k+0.5)` from **its own** copy of the path, so the interval spanning a seam is irregular even
  though no anchor is duplicated. Aligning phase would need a world-space anchor parameterisation shared
  between neighbouring tiles, which no part of this pipeline has. Curved text has the same property.
* **KL-A5 — a buffer-dominated path can lose its label (accepted).** A path whose every anchor falls in
  the buffer strip emits nothing in this tile.
  The partition argument is airtight for *positions* — every world position has exactly one owning tile — but
  it does NOT carry to roads: each tile derives anchors from its own copy of the path at its own phase, so
  neighbouring tiles' anchor sets are not partitions of one shared set. In practice the neighbour owning that
  stretch emits its own anchors and the road stays labelled; a short stub clipping a tile corner is the case
  that can genuinely go unlabelled there.
  No test reproduces this case, so nothing tracks how often it occurs on the committed fixtures.
* **KL-B1 — `icon-rotate` does not rotate the point collision box.** The point path's box is the unrotated
  `BoundsMin/BoundsMax` AABB even under a live map bearing, so rotating it for `icon-rotate` alone would
  make the convention inconsistent with the case it must match. (The *along-line* box IS rotated — by the
  tangent, via `BuildRotatedGlyph` — it simply does not include the extra constant.)
  This distinction is per along-line arm: the *viewport* along-line box stays `BuildRotatedGlyph` (tangent
  only, no `icon-rotate`), unrotated by the constant like the point path above. A *map-pitched* along-line
  glyph's box is instead the screen AABB of its four projected WORLD corners
  (`SymbolBox.TryBuildProjectedWorldGlyph`) and it **does** include `icon-rotate` — it must, because that is
  what the renderer rotates the drawn corners by, and a box that omits it does not bound the ink.
  Numerically a no-op on every shipped style: the only production `icon-rotate` on a map-pitched layer is
  180°, which on a centre-anchored cell maps the cell onto itself and leaves the AABB identical.
* Out of scope, no liberty consumer: `icon-keep-upright` (see "`icon-rotate` — a constant composed on top of
  the alignment"), `icon-translate`/`-anchor`,
  `icon-text-fit`, `icon-color`, `icon-halo-*`.
* `icon-pitch-alignment` (`LayoutProperties.IconPitchAlignment`, resolved via `AlignmentResolution.ResolvePitch`)
  IS consumed — `road_one_way_arrow`/`road_one_way_arrow_opposite` resolve it to `map` (both
  `icon-rotation-alignment` and `icon-pitch-alignment` unset, `symbol-placement: line`), and the resolved
  value reaches `CurvedStageInput.PitchAlignment`, which selects the WORLD-metre arc walk
  (`SymbolStagingMath.StageCurved`'s `worldArc`), the world-metre corner unit
  (`CandidateEmit.CornerMetresPerLogicalPixel` → `SymbolWorldPitchAlign.hlsl`), and the projected-world-corner
  collision box (KL-B1 above) together.

## 6.4 Grounding (touch points)

Core: `Style/Symbol/PropertyNames` (`icon-rotate`), `Style/Symbol/LayoutProperties` (`IconRotate`),
`Style/Symbol/SymbolFeature` (`IconRotateRadians`), `Text/CurvedGlyph` (two producers, two vertical
conventions), `Text/Placement/SymbolStageInputs` (`CurvedStageInput.AtlasKind`, both `IconRotateRadians`),
`Text/Placement/CandidateEmit` (`ExtraRotationRadians`), `Text/Placement/SymbolStagingMath`
(`AppendPointHalf`'s one addition; `StageCurvedAnchor`'s emit), `Text/Placement/ShapedSymbol`. Unity:
`Text/SymbolFeatureExtractor` (`iconAlongLine`, `AlongLineIconContext`, `EmitAlongLineIcon`,
`KeepAnchorsInsideTile`/`IsAnchorInsideTile` — the KL-A1 along-line anchor clip),
`Text/StyledSymbolTileBuilder` (the point/curved icon split), `Text/Placement/SymbolTileBlockBaker`
(both inputs), `Text/Placement/WorldSymbolRenderer` (the along-line rotation arm),
`Shaders/Map/Symbol/Icon/SymbolIconWorld_ForwardPass.hlsl` (the tangent branch). Jobs: `StageJob` passes
`CurvedStageInput` through wholesale.

**Instrument blind spot.** Headless teeth cover the emit shape, the atlas routing, the mesh geometry, and the
shader's tangent rotation, but not the live sprite sheet's `arrow` entry — that is observable only at runtime,
on screen, so it needs a maintainer eyeball.

---

# 7. Non-centred icon+text pairing (P-A)

The Style Spec's model is an *instance* of icon + text placed together, not two symbols that happen to
coincide. The predicate this subsystem uses to decide "is this a pair" must therefore not depend on the two
halves sharing one anchor position — a pairing rule that requires `TextAnchor.Center` and zero
`TextOffset`/radial-offset and `IconAnchor.Center` and zero `IconOffset` fails every non-centred symbol,
letting its dot place while its name is collision-culled independently (dots visible without their text).

**D-PA-1 — the predicate is `hasIcon && text != null`** (`SymbolFeatureExtractor`). Symmetric and
anchor/offset-independent; it needs no per-feature `text-radial-offset` expression evaluation.

**D-PA-2 — the ICON is always the pair Owner.** `SymbolTileBlockBaker` derives a pair's `FadeId` from the
owner's icon identity, and `SymbolPairing` / `StageJob` / `AssertPairAdjacency` all encode
owner-immediately-then-rider. The text half is never the owner.

**D-PA-3 — `icon-optional`/`text-optional` do NOT gate pair formation** (`docs/road-shields-design.md`
D11). The centred-pair argument — their boxes overlap, so un-pairing makes the halves mutually exclusive
rather than independent — does not apply to non-centred boxes, which are disjoint and would not self-block.
The argument that does apply is the *instance* one: `text-optional` means "this instance may render
icon-only", which presupposes the instance. liberty's `airport` sets it and nothing else; without pairing,
its halves would be collision-tested independently, and the text could place with the icon culled — the
case the flag forbids. Optionality is a per-BOX verdict inside the test-all-then-insert loop
(`SymbolCandidate.OptionalBoxMask`).

**D-PA-4 — the line branch re-gates on BOTH suppressions.** The pair is formed only when
`pairedInstance && iconAtAnchors && textAtAnchors`. Gating on the icon suppression alone lets a line layer
with `text-rotation-alignment: map` + `icon-rotation-alignment: viewport` stamp an Owner with no Rider; the
double gate makes `EmitAtAnchor`'s "PairedInstance implies both halves" hold. Every shipped shield layer
resolves both sides to viewport, so the gate does not change them.

**D-PA-5 — naming follows meaning:** the local is `pairedInstance` and the context property
`PairedInstance`. "Centred" names only what is centred (the shield-specific test names,
`docs/road-shields-design.md` § "Symbol pairing").

**Affected layers.** Nine liberty layers pair under this predicate beyond the three centred shield layers;
the predicate is a strict superset of centred pairing, so the shields' verdict is the same.

| layer | what makes it non-centred | notes |
|---|---|---|
| `label_city`, `label_city_capital` | `text-anchor: bottom` (+ `text-offset` −0.1/−0.2 em) | dot only **below z9**; `icon-allow-overlap: true`, `icon-optional: false` |
| `label_town`, `label_village` | `text-anchor: bottom` | dot only **below z10**; same flags |
| `airport` | `text-anchor: top`, `text-offset [0, 0.6]` | `text-optional: true` — D-PA-3's case |
| `poi_r1`, `poi_r7`, `poi_r20` | `text-anchor: top`, `text-offset [0, 0.6]` | minzoom 15/16/17 |
| `poi_transit` | `text-anchor: left`, `text-offset [0.9, 0]` | the only horizontally-offset pair |

**Consequence, intended:** `icon-allow-overlap: true` does not buy a `label_*` dot a free pass — the
candidate's `AllowOverlap` is the AND of the halves, so a colliding city label drops **both**, and city/town
dots are visibly fewer at z<9/10 than independent halves would give. That follows the instance model; do
not re-widen the predicate to compensate. The icon takes the lower `FeatureIndex` of the two, so among
equal sort keys the pair is ordered by its icon's ordinal, and the pair's `FadeId` is the icon's.

The dot exists only **below** z9 (`label_city`, `label_city_capital`) / z10 (`label_town`, `label_village`) —
liberty gates it with `icon-image: ["step",["zoom"],"circle_11_black", 9|10, ""]`, so at z10–13 those layers
carry no icon and nothing there can orphan.

`poi_r1`/`poi_r7`/`poi_r20`/`poi_transit` have no committed test data: the `poi` source layer is absent from
every fixture in `Assets/Fixtures/`. They are covered only by hand-built layers carrying liberty's exact layout
JSON, and a maintainer eyeball at z15+ is the only real check. (`airport` is nearly the same story: the Berlin
fixture carries the `aerodrome_label` feature, but at tile x=4929 — outside `[0, extent)`, so the single-world
clip drops it; its own test exercises the real `airport` layer over a hand-built tile instead.)

**Grounding.** Unity: `Text/SymbolFeatureExtractor` (`pairedInstance`,
`AnchorEmitContext.PairedInstance`, `EmitAtAnchor`). No other code decides pairing: the halves'
anchors/offsets are baked anchor-relative into their own quads and bounds by `TextQuadLayout.Layout` /
`IconQuadLayout.Layout`, so `SymbolStagingMath.StagePointPair` appending both at the owner's `ScreenPx` is
correct for a displaced half.

## 7.1 Open findings

**F-PA-1 — a spec divergence that non-centred pairing makes reachable. Read this before blaming the
predicate.** `SymbolStagingMath.StagePointPair` sets the candidate's
`AllowOverlap = owner.AllowOverlap && rider.AllowOverlap`, and `CollisionJob` consults only that
**candidate** flag — `SymbolBox.AllowOverlap` is carried per box but never read there. Per the Style Spec,
each `*-allow-overlap` governs its own element. The two agree when the text is blocked (both drop) and
**diverge in the mirror case**: dot's box blocked, text's box free — the spec places both, this renderer
drops both. No shield layer sets allow-overlap, but `label_city`/`_capital`/`_town`/`_village` all set
`icon-allow-overlap: true`, so the case is reachable. *Consequence:* slightly fewer city labels at z<9/10,
**on top of** the intended reduction above. **If the eyeball reads "too few labels", this is the suspect —
not the pairing predicate.** Do not re-widen the predicate to compensate. Fix shape when it matters: test
each box with its own carried `AllowOverlap`, keep the all-or-nothing AND on the verdicts.

**F-PA-2 — `AnchorEmitContext.PairedInstance` could be structural instead of hand-maintained.** It is an
`init` property set at two call sites in `SymbolFeatureExtractor`, and both reduce to `hasIcon && text !=
null` gated by the suppression fences. A computed `public bool PairedInstance => …` would delete both
initializers and make D-PA-4's bug class — an initializer that forgets one of the suppression fences —
unrepresentable. Open.

**F-PA-3 — per-half viewport culling.** `StagePointPair` gates the whole pair on the owner's `ScreenPx`, so
a rider displaced by up to 0.9 em (`poi_transit`) is culled or kept by the *icon's* position. Negligible at
liberty's offsets; real with `icon-text-fit` or larger offsets.

---

# 8. The halo is a second glyph run

**Rejected: fill and halo combined in one fragment** (`lerp(_HaloColor.rgb, textColor, fillAlpha)` +
`max(fillAlpha, haloAlpha·haloA)`). It cannot produce correct text. With one quad per glyph and
`ZWrite Off`, submission order is the only layering there is, so the *next* glyph's quad paints its halo
over the *previous* glyph's fill wherever the two quads overlap — which, for kerned text, is most of them.
No per-fragment combine can fix it: by the time glyph n+1's fragment runs, glyph n's ink is already in the
framebuffer and indistinguishable from background.

**The shader renders one thing.** It takes a colour (vertex `COLOR`) and a pair of device-px widenings
(`WorldBillboardVertex.SdfWidenPx`, TEXCOORD7) that push the SDF fill edge out and widen the AA transition,
and emits that colour at the resulting coverage. It has no halo width or blur uniform and no halo branch,
because there is no halo concept — the fragment cannot tell the two runs apart and does not need to.

`_HaloColor` is only the CONSTANT arm of `text-halo-color`'s two-carrier split, the mirror of `_TextColor`
(see "…except one colour uniform, for the restyle ease").

**A halo is a second copy of the label's glyphs.** `WorldSymbolRenderer.Emit` writes each label's whole glyph
run twice into the same mesh: the halo run first, carrying `text-halo-color` in the colour stream and
`text-halo-width`/`-blur` in `SdfWidenPx`, then the text run with the text colour and zero widening. The two
runs are contiguous blocks of one index buffer, so the whole label's halo rasterizes before any of its text.

Decisions worth keeping:

- **Grouping is per label, not per layer.** One `CandidateEmit` carries every glyph of every line a label
  shapes to, so "per `Emit` call" *is* "per label". A full halo-layer-then-text-layer separation would also
  order two overlapping labels against each other, but that costs a second index accumulator per slot and
  collision already keeps overlapping labels from arising. Per-label is enough.
- **One mesh, one material, one draw call.** Not two materials at two queues: index order inside one draw
  call is ordered by the rasterization rules, while two materials at the same `renderQueue` are not, and
  forcing them apart means a third `LayerSubSlot` — which drags in `LayerDrawOrder.QueueFor`,
  `RenderLayerSet.Build`, `SetDrawOrder` and the D7 icon-under-text invariant (`docs/road-shields-design.md`).
- **The halo comes from where it is already evaluated, not from a per-layer carrier.**
  `SymbolFeatureExtractor` evaluates `text-halo-color/-width/-blur` PER FEATURE into `SymbolPaint`. They ride
  `PointStageInput`/`CurvedStageInput` → `CandidateEmit` → the vertex stream, as `text-color` does.
  *Rejected:* resolving the halo per layer to keep those three blittable structs untouched — it builds a
  second path for a value the extractor already computes. Consequence: a **data-driven** and a
  zoom-expression `text-halo-*` both work, because both use the per-feature evaluation rather than a
  style-load-zoom snapshot.
- **`text-halo-color` is `.linear` on the CPU for the stream carrier**, in
  `SymbolPlacementSystem.LinearHaloColor` — the sibling of `LinearColor`. `_HaloColor` is a Color-TYPED
  material property, which Unity converts on upload, so the uniform carrier must NOT be pre-converted;
  Unity does not convert a vertex stream, so on the stream carrier the conversion happens upstream. One
  rule per carrier (see the next section); same rendered colour either way. `SymbolHaloColorRenderTests` has
  an arm per carrier and carries the reasoning in its header; without it a future reader finds one
  rationale and "fixes" the other side to match.
- **Width and blur stay LOGICAL px all the way to the emit**, where they take the logical→device conversion
  together against the LIVE ratio. So a dpr change re-scales the halo on the next Tick with no re-bake, no
  change detection, and no frozen-zoom bookkeeping — `SymbolRenderLayer.ApplyZoom` has no halo work.
- **The halo copy is the same quad**, not an inflated one. A halo wider than the glyph cell's SDF padding
  clips at the cell edge — as a one-fragment combine would too, so this is not a defect to chase.
- **A haloless label pays nothing.** A zero `text-halo-width` or a transparent `text-halo-color` gates the
  second run off, and a zero width is the spec default. Icons never take it — no `icon-halo-*` path.

## 8.1 …except one colour uniform, for the restyle ease

A **CONSTANT** `text-color` rides `_TextColor` rather than the vertex stream, because a value baked into a
mesh cannot be eased: the in-place restyle path re-binds uniforms and never re-bakes a tile. `text-color`
and `text-halo-color` are the two paint keys the shipped liberty → liberty-night pair differs on across its
symbol layers, so without both of them free, the restyle gate refuses the whole document and the ease never
runs at all.

So `text-halo-color` takes the same split: **Constant → the `_HaloColor` uniform, every other kind → the
vertex stream**, with the unused carrier left at identity white and the fragment multiplying the two. Exactly
one of them is ever non-white, so the colour is converted once and applied once on either path.

- **Which uniform a run uses is read off `SdfWidenPx`, in the vertex stage.** One material draws both runs,
  so a single tint uniform would paint the halo with the text's colour. The discriminator is exact rather
  than a threshold: `Emit` emits a halo run only when `text-halo-width > 0`, so a halo vertex always has
  `SdfWidenPx.x > 0` and a text vertex always has exactly `0`. It costs no vertex attribute and no flag bit —
  and a flag bit is not free, because `AlignFlags` is tested with float comparisons (`>= 1.5`) that a new
  high bit would break.
- **Only the colours, and only at Constant.** `text-halo-width`/`-blur` stay per-feature on the vertex stream;
  the restyle gate refuses a change to either, and refuses a non-Constant colour change on the same grounds.
  A colour's ALPHA also stays on the stream — for the halo it additionally decides whether the second run is
  emitted at all — so an alpha-only change refuses too.
- **What this costs.** A zoom-expression `text-halo-color` does not ease across a restyle. It is the same
  rule `text-color` lives under, and the shipped pair uses constants throughout.

Two consequences that are correct, not bugs:

- **Fade compositing.** A half-faded label's interior alpha is `o + h·o·(1−o)` rather than `o`: the halo
  shows through the half-transparent fill, as in any two-pass composite.
- **A data-driven `text-halo-*` renders** from its own per-feature value, not from the base material's
  inherited halo (an arbitrary asset default).

**Residual, out of scope:** two tiles in the same layer are separate renderers at the same `renderQueue`, so
halo-over-fill across a tile boundary is order-undefined. Global collision is what keeps those labels from
overlapping in the first place.

---

# 9. SDF text weight: where the AA band sits

**The AA band sits entirely OUTSIDE the outline.** A ramp centred on the iso (`saturate(screenDist / aa +
0.5)`) puts the 50% point at the iso, so half the band eats INWARD — up to `aa/2` device px removed from both
edges of every stroke. On a 1–2 px stem that is most of the ink. It also caps coverage: glyph interiors peak
at **0.878** of the field, not 1.0 (thin stems never saturate a distance field), so a minified glyph — a
distant label, a foreshortened along-road name — could never reach full alpha and would wash out. The
`+ 1.0` form puts the whole band OUTSIDE the outline: at or inside the iso is solid at any scale, and the
falloff is the band just beyond.

**`_SdfEdge` is 0.75, the fontnik iso.** A lower edge dilates every stroke — at 0.60, `0.15 × 8 = 1.2`
texels per edge, ~2.4 texels of extra stroke — which reads as bold and is easy to mistake for a font-weight
bug. The edge is not a per-source calibration: the live openfreemap PBFs and the committed fixture are the
same fontnik bake (global max 255, median glyph interior peak 224 ≈ 0.878, p10 219). The note in
`GlyphPbfDecodeTests` carries the measurement so the calibration argument is not re-derived.

**Naming.** `_SdfAaDevicePx` and `_SdfRangeTexels` state the unit and whether the value is a look knob at
all (`_SdfRangeTexels` is not — it describes how the glyphs were BAKED). DEVICE px is qualified because this
shader also carries `_ScreenParamsLogical`, which is logical px, and mixing the two is an easy error.

**The band width is one raster pixel, and it is a constant on purpose.** The fragment converts field
distance to screen px through `screenPxRange`, which already carries the glyph's on-screen size, so the
band needs no size term. A linear ramp one sample pitch wide is the only width whose summed ink does not
depend on sub-pixel phase. A narrower ramp freezes a stroke's ink and then jumps it as the map pans. MSAA
is off, so this ramp is the only antialiasing text has. `SymbolTextResamplingTests` pins the phase
behaviour and the shipped material's value.
