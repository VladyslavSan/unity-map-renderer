# Label pipeline flow — from visible tiles to the per-frame symbol cull

How a symbol label travels from "which tiles are on screen" to a drawn (or culled) glyph, and where the
per-tile screen-coverage pre-cull sits in that flow. Read alongside `docs/label-smoothness-design.md`
(the fade/collision design) and `docs/label-tile-precull-design.md` (the coverage cull specifically).

The label system has **two clocks**: a slow, event-driven **tile lifecycle** (fetch/build/cache, runs only
when the cover changes) and a fast **per-frame placement loop** (projects + collides + draws every frame).
Keeping camera-dependent work out of the slow clock — and slow work out of the fast clock — is the spine of
the whole design.

---

## 1. Tile lifecycle (the slow clock) — data arrives

Driven by `TileManager`, which owns the set of loaded tiles (`_loaded`).

```
TileManager.Tick(cameraProperties, selectionConfig)          // once per frame, but mostly idle
  ├─ CoverSelect      : pick the visible tile cover for this camera (z/x/y set)
  ├─ Request/Release  : fetch newly-covered tiles, release departed ones (kept-warm in a cache)
  └─ on fetched bytes : SymbolTileBytesReady(source, tile, bytes)   ── push ──►  SymbolLabelSubsystem
```

`SymbolLabelSubsystem.OnTileBytesReady` does **not** build inline — it **enqueues** the MVT bytes. The actual
decode → feature-extract (off the main thread) → glyph-shape → atlas-append is drained a bounded number per
frame by `PumpBuilds` (stall-#1 fix). Epic A / A3: decode + per-layer extract now run through
`Rendering.Tile.Processing.TileLayerProcessorRunner.RunSymbolWorkerPass` (the symbol cadence's own
decode-once worker-pass entry — still symbol's own bytes push, not the mesh pass's decode) over one
`TileSymbolLayerProcessor` per symbol style layer, each writing into the build's shared label list; the
main-thread shape/atlas-append tail runs as `SymbolLabelSubsystem.BuildTileAsync`'s per-layer
`CompleteOnMainAsync` loop after one batched `SwitchToMainThread` hop — the queue/pump/budget/atlas/store
flow below is otherwise unchanged from pre-A3. The finished per-tile labels land in `SymbolTileLabelStore`, which:

- keeps an **active** set (in cover) and a **cached** set (out of cover, kept warm for cache-hit re-entry);
- bumps a monotonic **`Version`** on every set-changing mutation — this is the key the fast clock uses to
  know whether anything actually changed.

So: **a `LabelInstance` exists per (tile, feature) once its tile's bytes are decoded and shaped.** It carries
`AnchorRender` (point) or `PathRender` + `LineAnchors` (curved), its `TileKey` (packed z/x/y), text, paint,
and style-evaluated sizes. Nothing here depends on the camera.

---

## 2. Per-frame driver — `MapView.LateUpdate`

Runs in `LateUpdate` so it sees this frame's committed camera (input mutates the camera in `Update`). The
order is load-bearing:

```
1. Camera.SyncToCamera()                       // commit this frame's pose
   cameraProperties = Camera.CurrentProperties // ONE snapshot — tiles + labels share it
   sceneFrame       = BuildSceneFrame(...)     // floating origin: origin = look-at, + ENU rebase

2. TileManager.InstancedRebuild(sceneFrame)    // place tile meshes relative to the origin
   TileManager.Tick(cameraProperties, ...)     // the slow clock (§1) — cover select + request/release

3. if the style has symbol layers:
     TileManager.CollectLoadedTileKeys(scratch) // PULL the current loaded set
     _symbols.ReconcileLoadedTiles(scratch)     // release departed / restore cache-hit labels
     _symbols.PumpBuilds()                       // start ≤N queued builds, coalesce one atlas upload
     Labels.Tick(sceneFrame, _symbols.CurrentBatch(), atlas, dt, layerMaterials, _symbols.Version)
   else:
     Labels.Tick(sceneFrame, LabelInstances, atlas, dt)   // demo/synthetic fallback
```

Tiles and labels are placed against the **same** `sceneFrame` and `cameraProperties` snapshot, so they can
never diverge.

---

## 3. The batch — `SymbolLabelSubsystem.CurrentBatch()`

The bridge between the two clocks. It returns a blittable `SymbolLabelBatch` (Structure-of-Arrays), rebuilt
**every frame** from the current collected set. (An earlier version cache skipped the rebuild while the
collected set / zoom / slot-count were all stable, keyed on a monotonic `SymbolTileLabelStore.Version`; it was
removed once the B-1 static-frame skip retired left it a single caller — its per-mutation bump discipline
wasn't worth the complexity. The rebuild is **allocation-free** — both `CollectInto` and the builder reuse
their buffers — so the cost is CPU only: the cross-tile dedup + the `LabelInstance → SoA` conversion.) Each
rebuild, `SymbolLabelBatchBuilder`:

- flattens each label into the SoA in collected order (so collision ordinals — and the mesh — stay
  byte-identical);
- resolves the managed-only bits (glyph quads, sRGB→linear colour, fade ids);
- **and, for the tile-coverage cull, projects each unique tile's 4 corners into render space once** (see §5).

The batch carries a **`BuildId`** bumped on every rebuild; `LabelPlacementSystem` mirrors it into native
buffers only when `BuildId` changes. (With the version cache gone the batch rebuilds — and so `BuildId`
advances — every frame, so the mirror now refreshes every frame; the gate still saves the copy on a genuinely
idle frame where nothing calls `CurrentBatch`, and stays correct if batch caching is ever reintroduced.)

---

## 4. The per-frame placement loop — `LabelPlacementSystem.Tick`

Everything camera-dependent lives here. Runs over reused buffers (no per-frame GC). Stages:

```
Tick(sceneFrame, batch, atlas, dt, materials, version)
 │
 ├─ B-1 static-frame skip  ── if version + camera + sceneFrame + fades ALL unchanged since the last
 │                             real build → re-submit the cached meshes and RETURN. (Idle map = no work.)
 │
 ├─ PmProject
 │   ├─ PmProjectFill
 │   │   ├─ ComputeTileCoverageCull(batch, origin, viewProj, viewport)   ◄── the per-tile pre-cull (§5)
 │   │   ├─ GatherSymbolPoints(...)   : flatten un-culled records' world points; a culled record's
 │   │   │                              offset is set to -1 (tile-coverage cull, then B-3 distance cull)
 │   │   └─ ProjectSymbols(...)       : Burst SymbolProjectionJob — world → screen/depth for all at once
 │   │
 │   └─ PmStage
 │       └─ RunStageJob(...)          : Burst LabelStageJob — build collision boxes + rotated-glyph quads
 │                                      (skips any record whose gather offset is -1)
 │
 ├─ PmCollide
 │   └─ RunCollision(...)             : Burst LabelCollisionJob — greedy, sort-key-driven survivor selection
 │                                      (global across point + curved labels; uniform-grid accelerated)
 │
 ├─ PmEmit
 │   └─ A-4 fade + per-slot quad assembly : ease each survivor's opacity toward 1 (placed) / 0 (suppressed);
 │                                          emit its quads into its material slot's bucket
 │
 └─ PmBuildSubmit
     └─ BuildAndSubmit(...)           : one Burst SymbolBillboardJob → mesh upload → Graphics.RenderMesh
                                        (one draw per non-empty material slot)
```

Three fade-out triggers happen **before** any projection/staging/collision, in `GatherSymbolPoints`:

1. **Departing** (`RecordDeparting`): the record's tile is leaving cover — flagged by the batch builder from
   `CollectInto`'s active/departing split (see §6). Fades out unconditionally.
2. **Tile-coverage pre-cull**: drop a whole tile's records when the tile is a screen sliver.
3. **B-3 distance cull** (`LabelViewDistance`): drop an individual label beyond a horizon radius.

A triggered record is **not** hard-dropped if its fade is still alive: gather keeps STAGING it (re-projected to
its live position) and forces its opacity toward 0 in emit — so it eases OUT in place instead of popping. Only
once it has fully faded does gather set its offset to `-1` (the Burst stage job's "skip"). This soft-cull is the
single mechanism behind all three; no job changed to add any of them.

---

## 5. Where the tile-coverage pre-cull plugs in

The load-bearing constraint: **coverage is camera-dependent, so it must not enter the batch build** (that
would defeat §3's version cache). The work is split by what depends on the camera:

| Camera-**independent** — built once in §3 (`SymbolLabelBatchBuilder`)      | Camera-**dependent** — every frame in §4 (`ComputeTileCoverageCull`) |
| -------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| Dedup tiles by `TileKey`; project each tile's 4 corners to **render space** | Project those corners to **screen**; shoelace area ÷ viewport area    |
| Store `SymbolLabelBatch.TileCorners[]` (a `TileQuad` per tile) + `RecordTile[]` | Flag tiles below `MinTileScreenCoverage`; gather drops flagged records |

Corners are projected via `TileId.ToLonLat → IProjection.Project` — the **same** path that produced
`AnchorRender` — so the metric is globe-correct (no flat-earth `MercatorBounds` shortcut).

The metric itself is an engine-free Core unit, **`LabelTileCoverage`** (tested in `LabelTileCoverageTests`):

```
ScreenCoverage(4 render corners, sceneOrigin, viewProj, viewport)
    → project each corner to screen (LabelScreenProjection.TryProjectPoint)
    → if ANY corner is behind the near plane (or viewport degenerate): return +∞   ("never cull")
    → else: |shoelace(4 screen corners)| / viewportArea                            (fraction of screen)

IsCulled(coverage, minCoverage)  →  minCoverage > 0 && coverage < minCoverage
```

`minCoverage ≤ 0` disables the cull entirely (the kill-switch); `+∞` coverage is never below a finite
threshold, so a near-plane-straddling tile is always kept. Default `MinTileScreenCoverage = 0.05` — a
maintainer eyeball-tunable. Telemetry: `LastTileCoverageCulledCount` (mirrors `LastDistanceCulledCount`).

**Why it pays off:** under tilt, the far frustum compresses a large ground area into a thin horizon band;
those tiles' labels mostly lose collision (or project unstably) anyway. Dropping them whole — before the
gather/project/stage/collide chain — stabilizes per-frame label cost with barely any lost information.
It complements B-3: B-3 is a per-label ground *radius*; this is a per-tile screen *area*, which catches the
foreshortened slivers a radius keeps.

Deferred (see `docs/label-tile-precull-design.md`): the green/red survived-vs-culled debug overlay,
tilt-scaling the threshold, and explicit hysteresis (the A-4 fade already softens boundary flicker for v1).

---

## 6. Retain-as-departing — fading a tile out when it leaves cover

The coverage/distance culls (§4–5) fade a record that is still *in* the batch. A normal **tile unload** is
different: the tile leaves cover, its labels leave the collected set, the batch rebuilds without them, and they
would pop (their fade record decays with nothing drawn). The fix keeps them in the batch for a grace window.

When a tile leaves cover, `SymbolTileLabelStore` releases it to the **warm cached side** (kept for a cache-hit
re-entry) and, if a grace window is set, stamps it **departing** (`key → wall-clock expiry`). While departing:

- `CollectInto` still emits its labels — appended **after** the active ones — and reports the split via
  `out activeCount`. A departing point label whose cross-tile identity is already claimed by an active label is
  skipped (the active copy shows → seamless tile-to-tile transfer, no fade).
- `SymbolLabelBatchBuilder.Build(…, activeCount)` flags every record at index ≥ `activeCount` as
  `RecordDeparting`, which gather (§4) treats as an unconditional fade-out trigger.
- The store **purges** the stamp once `now ≥ expiry`. The grace
  (`SymbolLabelSubsystem.DepartingGraceSeconds`, derived from `FadeDurationSeconds`) exceeds the fade, so a purge
  only ever drops an already-invisible label. Re-entry within grace clears the stamp (via `RemoveCached`, the
  single "leaves cached" chokepoint that keeps the invariant *departing ⊆ cached*) → the label fades back in.

Because a departing label is a **real batch record**, it re-projects to its live position every frame — so it
eases out correctly even while the camera pans (the whole reason not to cache frozen quads). The wall-clock is
threaded from `MapView.LateUpdate` (`Time.timeAsDouble`). Telemetry: `LastDepartingCulledCount`,
`SymbolLabelSubsystem.DepartingTileCount`.

**Known limitation (accepted for now):** the fade-out **borrows the cache's warm copy** of the labels, so it is
gated on the prepared mesh cache being enabled — cache off ⇒ `Release` drops the labels immediately (and the
subsystem sets grace 0), so an unloaded tile still pops. The cache is on by default, so production and the demo
get the fade; cache-off is a debug / low-memory toggle where revisits re-fetch anyway. **Decouple path if this
matters later:** the fade only needs the labels for the grace window (~0.5 s), independent of the cache-hit
retention (minutes), so `_departing` could *own* the released entry for the grace window when the cache is off
(a second, short-lived retention path in the store) instead of borrowing the cached FIFO — making "labels never
pop" hold in every config. Small change (+ a test); not done.
