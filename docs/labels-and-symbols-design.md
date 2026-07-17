# Labels & symbols — design & SSOT

The symbol/label subsystem: how a label travels from tile bytes to a drawn (or culled) glyph, and the design
of each part — placement smoothness, curved along-line text, and projection (globe) support. Code lives in
`MapRenderer.Unity/Text/` (subsystem, per-frame placement) and `MapRenderer.Core/Text/` (engine-free shaping,
layout, metrics).

1. **[Pipeline flow](#1-pipeline-flow)** — the two-clock architecture; tile lifecycle → per-frame placement → cull → fade.
2. **[Smoothness & robustness](#2-smoothness--robustness)** — pull/reconcile, cross-tile identity, the fade state machine, the perf tracks.
3. **[Curved along-line text](#3-curved-along-line-text)** — `symbol-placement: line` / `line-center` (mostly landed).
4. **[Projection support](#4-projection-support-globe-ready-labels)** — globe-ready labels under any `IProjection` (designed, not started).

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
  └─ on fetched bytes : SymbolTileBytesReady(source, tile, bytes)   ── push ──►  SymbolLabelSubsystem
```

`SymbolLabelSubsystem.OnTileBytesReady` does **not** build inline — it **enqueues** the MVT bytes. The
decode → feature-extract (off the main thread) → glyph-shape → atlas-append is drained a bounded number per
frame by `PumpBuilds`. Decode + per-layer extract run through
`Rendering.Tile.Processing.TileLayerProcessorRunner.RunSymbolWorkerPass` (the symbol cadence's own decode-once
worker-pass entry) over one `TileSymbolLayerProcessor` per symbol style layer, each writing into the build's
shared label list; the main-thread shape/atlas-append tail runs as `RunTailAsync`'s per-layer
`CompleteOnMainAsync` loop, started by `PumpBuilds`' once-per-frame drain of the pool→main
`ConcurrentQueue` handoff (`_handoffQueue`). Finished per-tile labels land in
`SymbolTileLabelStore`, which:

- keeps an **active** set (in cover) and a **cached** set (out of cover, kept warm for cache-hit re-entry);
- bumps a monotonic **`Version`** on every set-changing mutation — the key the fast clock uses to know whether
  anything actually changed.

So a `LabelInstance` exists per `(tile, feature)` once its tile's bytes are decoded and shaped. It carries
`AnchorRender` (point) or `PathRender` + `LineAnchors` (curved), its `TileKey` (packed z/x/y), text, paint, and
style-evaluated sizes. Nothing here depends on the camera.

## 1.2 Per-frame driver — `MapView.LateUpdate`

Runs in `LateUpdate` so it sees this frame's committed camera (input mutates the camera in `Update`). The order
is load-bearing:

```
1. Camera.SyncToCamera()                       // commit this frame's pose
   cameraProperties = Camera.CurrentProperties // ONE snapshot — tiles + labels share it
   sceneFrame       = BuildSceneFrame(...)     // floating origin: origin = look-at, + ENU rebase

2. TileManager.InstancedRebuild(sceneFrame)    // place tile meshes relative to the origin
   TileManager.Tick(cameraProperties, ...)     // the slow clock (§1.1) — cover select + request/release

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

## 1.3 The batch — `SymbolLabelSubsystem.CurrentBatch()`

The bridge between the two clocks: a blittable `SymbolLabelBatch` (Structure-of-Arrays), rebuilt **every frame**
from the current collected set. The rebuild is **allocation-free** (both `CollectInto` and the builder reuse
their buffers), so the cost is CPU only — the cross-tile dedup + the `LabelInstance → SoA` conversion. Each
rebuild, `SymbolLabelBatchBuilder`:

- flattens each label into the SoA in collected order (so collision ordinals — and the mesh — stay
  byte-identical);
- resolves the managed-only bits (glyph quads, sRGB→linear colour, fade ids);
- projects each unique tile's 4 corners into render space once, for the tile-coverage cull (§1.5).

The batch carries a **`BuildId`** bumped on every rebuild; `LabelPlacementSystem` mirrors it into native buffers
only when `BuildId` changes.

## 1.4 The per-frame placement loop — `LabelPlacementSystem.Tick`

Everything camera-dependent lives here, over reused buffers (no per-frame GC). Stages:

```
Tick(sceneFrame, batch, atlas, dt, materials, version)
 │
 ├─ B-1 static-frame skip  ── if version + camera + sceneFrame + fades ALL unchanged since the last
 │                             real build → re-submit the cached meshes and RETURN. (Idle map = no work.)
 │
 ├─ PmProject
 │   ├─ PmProjectFill
 │   │   ├─ ComputeTileCoverageCull(batch, origin, viewProj, viewport)   ◄── per-tile pre-cull (§1.5)
 │   │   ├─ GatherSymbolPoints(...)   : flatten un-culled records' world points; a culled record's
 │   │   │                              offset is set to -1 (tile-coverage cull, then B-3 distance cull)
 │   │   └─ ProjectSymbols(...)       : Burst SymbolProjectionJob — world → screen/depth for all at once
 │   └─ PmStage
 │       └─ RunStageJob(...)          : Burst LabelStageJob — collision boxes + rotated-glyph quads
 │                                      (skips any record whose gather offset is -1)
 ├─ PmCollide
 │   └─ RunCollision(...)             : Burst LabelCollisionJob — greedy, sort-key-driven survivor selection
 │                                      (global across point + curved labels; uniform-grid accelerated)
 ├─ PmEmit
 │   └─ A-4 fade + per-slot quad assembly : ease each survivor's opacity toward 1 (placed) / 0 (suppressed);
 │                                          emit its quads into its material slot's bucket
 └─ PmBuildSubmit
     └─ BuildAndSubmit(...)           : one Burst SymbolBillboardJob → mesh upload → Graphics.RenderMesh
                                        (one draw per non-empty material slot)
```

Three fade-out triggers happen **before** any projection/staging/collision, in `GatherSymbolPoints`:

1. **Departing** (`RecordDeparting`): the record's tile is leaving cover (§1.6). Fades out unconditionally.
2. **Tile-coverage pre-cull**: drop a whole tile's records when the tile is a screen sliver.
3. **B-3 distance cull** (`LabelViewDistance`): drop an individual label beyond a horizon radius.

A triggered record is **not** hard-dropped while its fade is still alive: gather keeps STAGING it (re-projected
to its live position) and forces its opacity toward 0 in emit — so it eases OUT in place instead of popping.
Only once fully faded does gather set its offset to `-1` (the Burst stage job's "skip"). This soft-cull is the
single mechanism behind all three; no job changed to add any of them.

## 1.5 Tile-coverage pre-cull  *(IMPLEMENTED)*

A coarse step *before* the per-label pipeline: skip a tile's labels entirely when the tile covers less than ~N%
of the screen. Small-on-screen tiles are the horizon pile-up under tilt — most of their labels get
collision-culled anyway, so gather → project → stage → collide on them is wasted work. Dropping them whole
stabilizes per-frame label cost with barely any lost information. Complements the per-**label** B-3 distance
cull (a horizon *radius*): this is a per-**tile** *screen-area* metric, which catches the tilt-foreshortened
slivers a radius keeps.

**Load-bearing constraint:** coverage is camera-dependent, so it must **not** enter the batch build (§1.3). Work
splits by what depends on the camera:

| Camera-**independent** — once per batch (`SymbolLabelBatchBuilder`) | Camera-**dependent** — every frame (`ComputeTileCoverageCull`) |
| --- | --- |
| Dedup tiles by `TileKey`; project each tile's 4 corners to **render space** (`TileId.ToLonLat → IProjection.Project` — globe-correct, no flat-earth `MercatorBounds` shortcut) | Project those corners to **screen**; shoelace area ÷ viewport area |
| Store `SymbolLabelBatch.TileCorners[]` (a `TileQuad` per tile) + `RecordTile[]` | Flag tiles below `MinTileScreenCoverage`; gather drops flagged records |

The metric is an engine-free Core unit, **`LabelTileCoverage`** (`LabelTileCoverageTests`):

```
ScreenCoverage(4 render corners, sceneOrigin, viewProj, viewport)
    → project each corner to screen (LabelScreenProjection.TryProjectPoint)
    → if ANY corner is behind the near plane (or viewport degenerate): return +∞   ("never cull")
    → else: |shoelace(4 screen corners)| / viewportArea                            (fraction of screen)

IsCulled(coverage, minCoverage)  →  minCoverage > 0 && coverage < minCoverage
```

`minCoverage ≤ 0` disables the cull (the kill-switch); `+∞` is never below a finite threshold, so a
near-plane-straddling tile is always kept. Default `MinTileScreenCoverage = 0.05` — a maintainer
eyeball-tunable. Telemetry: `LastTileCoverageCulledCount`. **Deferred:** a green/red survived-vs-culled debug
overlay (would make the threshold easy to tune by eye), tilt-scaling the threshold, and explicit hysteresis (the
A-4 fade softens boundary flicker for v1).

## 1.6 Retain-as-departing — fading a tile out when it leaves cover

The coverage/distance culls fade a record still *in* the batch. A normal **tile unload** is different: the tile
leaves cover, its labels leave the collected set, the batch rebuilds without them, and they would pop. The fix
keeps them in the batch for a grace window.

When a tile leaves cover, `SymbolTileLabelStore` releases it to the **warm cached side** and, if a grace window
is set, stamps it **departing** (`key → wall-clock expiry`). While departing:

- `CollectInto` still emits its labels — appended **after** the active ones — and reports the split via
  `out activeCount`. A departing point label whose cross-tile identity is already claimed by an active label is
  skipped (the active copy shows → seamless tile-to-tile transfer, no fade).
- `SymbolLabelBatchBuilder.Build(…, activeCount)` flags every record at index ≥ `activeCount` as
  `RecordDeparting`, which gather treats as an unconditional fade-out trigger.
- The store **purges** the stamp once `now ≥ expiry`. The grace (`DepartingGraceSeconds`, derived from
  `FadeDurationSeconds`) exceeds the fade, so a purge only ever drops an already-invisible label. Re-entry
  within grace clears the stamp (via `RemoveCached`, the single "leaves cached" chokepoint that keeps the
  invariant *departing ⊆ cached*) → the label fades back in.

Because a departing label is a **real batch record**, it re-projects to its live position every frame — so it
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

The placement layer was originally **stateless**: `LabelPlacementSystem.Tick` cleared everything and rebuilt
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
The `SymbolTileLabelStore` survives as the reconcile backing store. Surface: `LoadedTileKeys(version)`, cheap to
call every frame (reconcile early-outs when the version is unchanged → feeds B-1). *Teeth:* reconcile is
idempotent; a removed tile is no longer collected; the zoom-out-then-in case works without any release callback.

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
differences between zoom levels collapse to one id. A `Dictionary<CrossTileLabelKey, DedupEntry>` inside
`SymbolTileLabelStore` maps id → chosen instance; at
**collection** time, when the same id appears in parent + child tiles, pick one deterministically (finest zoom,
then lowest tile key). Same id across a tile swap ⇒ the record **persists** ⇒ opacity stays 1 ⇒ no fade cycle,
a true no-op — and the label count drops by the duplicate factor. Line labels in v1: key each *placed world
anchor* as its own id; if noisy, fall back to excluding line labels from dedup. *Teeth:* two overlapping loaded
tiles carrying the same point symbol → one placement; the id is stable across a simulated parent→child swap.

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

**B-2 — Burst-jobified projection.** For the moving case at ~20k labels: gather candidate world anchors into a
`NativeArray<double3>` and project (+ cull) them in a Burst job → screen + mask; the managed greedy collision
stays (it is serial). Attacks the per-anchor matrix-mul cost that dominates `Project` when the camera moves.

**B-3 — Horizon / far-distance label culling.** In a tilted view the far half of the frustum compresses a huge
ground area into a thin horizon band, where labels pile up, get collision-discarded, and jitter (projection is
numerically unstable as depth → the far plane). Cull labels near/beyond the horizon **before** projecting:
prefer (a) a **world-space ground distance** from the look-at beyond a pitch-dependent threshold (pre-projection
— skips the matrix mul); backstop (b) a **far-depth / above-horizon screen cull** after projecting. Labels want
their own, tighter distance cut than the tile far-plane policy (`GeometryAwareFarPlane` / `RaySphereFarPlane`) —
they stop being legible well before tiles stop drawing (MapLibre has an analogous pitch-scaled fade of distant
symbols). Pairs with B-1: fewer candidates per frame *and* the static-skip avoids redoing them when idle.
*(This is the per-label radius companion to the per-tile coverage cull in §1.5.)*

**B-4 — Pipelined (deferred) placement — decouple the decision from the render (biggest moving-case lever).**
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
place+rotate each glyph — happens **per frame** in `LabelPlacementSystem`, exactly like point-label projection.
Build time only extracts the line geometry and shapes the text.

```
BUILD (Core, per tile, once)                    PER FRAME (LabelPlacementSystem, screen-space)
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

**New Core pieces (engine-free, headless-tested):**
- **`PolylineArcWalker`** — over a screen-space `float2` polyline: `TotalLength` and
  `At(arcDistance) → (float2 point, float tangentRadians)` by walking cumulative segment lengths and lerping
  within the containing segment (tangent = segment direction via `atan2`; clamps to the ends).
- **`CurvedTextLayout`** — `Layout(ShapedRun, IGlyphAtlasView) → IReadOnlyList<CurvedGlyph>` where
  `CurvedGlyph { float ArcCenter; SymbolQuad Cell }`. A single forward pass (mirrors `TextQuadLayout.PlaceGlyph`)
  places each glyph's cell relative to **its own** pen origin, centered **horizontally** on the glyph's
  advance-center, while `Top`/`Bottom` stay **baseline-relative** (NOT vertically centered — that would make
  ascenders/descenders straddle the line). `ArcCenter` = cumulative advance to that center. This baseline-center
  anchoring is what lets the Burst job stay unchanged: `BuildQuad` rotates the cell about `anchorScreenPx`, so
  that point being the baseline-center makes the tangent rotation correct.

**Placement (`LabelPlacementSystem`, per frame):** `SymbolLabel`/`LabelInstance` carry a `PathRender`
(`double3[]`) for line labels (null for point labels — `AnchorRender` untouched) plus `SymbolPlacement` +
`SymbolSpacingPx`. Project each path vertex with `TryProjectAnchor` (skip the label if any vertex is behind the
camera — partial-visibility clipping is a refinement); build a `PolylineArcWalker` over the projected polyline;
choose anchor arc-distances (`line-center` → `TotalLength/2`; `line` → centered multiples of `symbol-spacing`,
label needs `labelWidthPx ≤ TotalLength`); apply keep-upright (walk reversed when the net direction points
leftward); emit one `PlacedQuad` per glyph.

**Double-rotation trap.** A curved glyph's rotation is the projected-line **tangent only**. The projection
already baked the bearing in (path vertices go through the bearing-aware camera), and line placement defaults
`rotation-alignment: auto → map` — exactly when the point-label path adds `LabelBearing.BillboardRotationRadians`.
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
staged-count parity holds. Teeth: `LabelStagingMathCurvedVertexTests` (RED-verified — a glyph on a bend gets the
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

**Collision (the one genuinely-new placement concern).** A point label submits one `LabelBox` (AABB). A long
curved label's single AABB would bound a whole road → almost nothing places. So a curved label submits a
**per-glyph box set** and places iff its boxes don't collide with already-placed boxes (MapLibre uses per-glyph
collision circles; per-glyph AABBs are the tractable analogue). The unified model: `LabelCandidate` is a
contiguous box range; `LabelCollision.SelectSurvivors(candidates, boxes, …)` treats a point label as a 1-box
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

The per-frame pipeline (§1) is almost entirely **screen-space**. At build time it projects geodetic anchors/paths
to render space through `IProjection.Project` (`SymbolFeatureExtractor.cs:136,170`;
`SymbolLabelBatchBuilder.ProjectCorner` `:87-91`); each frame it projects those to screen and does arc-walk /
AABB / billboard in pixels. So **billboarding, per-glyph orientation, and collision are already
projection-generic** — glyph quads are built in 2D screen space (`BillboardMath.BuildQuad`), and curved-text
rotation is the screen-space tangent of the already-projected polyline (`LabelStagingMath.cs:181`). There is no
world up-vector or great-circle tangent to get right. What remains is four fixes, one foundational.

### 1. The projection seam drops the rebase rotation (foundational)

The Unity camera places the look-at at the world origin in an idealized Y-up ENU orbit frame
(`MapCamera.SyncToCamera`, `:114-141`), so `ViewProj` expects points in the **rebased look-at frame**. Mesh tiles
honor this: `TileToSceneRebased = Rebase·(origin − sceneOrigin)` (`FloatingOrigin.cs:95`),
`Rebase = transpose(TangentBasisAt(lookAt))` (`SceneFrame.cs:14`). The label seam does not —
`LabelScreenProjection.TryProjectPoint` (`:87-88`), `SymbolProjectionJob.Execute` (`:49-52`), and
`SymbolLabelBatchBuilder.ProjectCorner` only subtract `sceneOrigin`, never applying `Rebase`. On Mercator
`Rebase = identity` (inert); under `SphericalProjection` every anchor away from the look-at projects to the wrong
pixel. **Fix:** carry a `float3x3 rebase` into the seam and rotate after translating, before `viewProj`.

### 2. Cross-tile dedup drops render-Y

`CrossTileLabelKey.For` (`:49-55`) quantizes render `.x`/`.z` only — the key has no Y axis (`:32-35`). Under
`SphericalProjection.ProjectPoint` (`:51-55`) render X/Z ∝ cosφ (even in latitude) and Y ∝ sinφ (odd), so a
same-longitude pair mirrored across the equator (e.g. 30°N and 30°S) collides exactly at any grid size — two
distinct labels merge into one. Byte-identical on Mercator (surface labels have render.y ≡ 0). **Fix:** add a Y
axis and quantize on all three. Bundle with renaming the quantization scale `WebMercator.GroundResolution` →
`CameraPoseMath.MetersPerPixel` (`:31-32,46`) at the three sites (`SymbolLabelSubsystem.cs:467,484`,
`LabelPlacementSystem.cs:477`) — the two are the same expression, so this just drops a Mercator-only name from a
camera quantity; no behavior change.

### 3. No horizon cull

`SymbolProjectionJob.OutValid` culls only behind-camera points (`clip.w ≤ 0`); a far-side-of-globe label projects
in front and draws through the earth (labels ship `ZTest Always`, so no depth hides it, and its collision box
still contests near-side space). **Fix:** test each anchor against `IProjection.TryGetHorizonOccluder`
(`SphericalProjection.cs:81-86`) as a rad-0 point — the algebra `FrustumTileSelector` already uses
(`:100-108,178-188`) — and fold the result into `OutValid`, so occluded labels never reach collision. Correct
only once the seam applies `Rebase` (the occluder is expressed in the rebased look-at frame). Mercator returns no
occluder ⇒ no-op.

### 4. Long line segments chord instead of curving

`SymbolFeatureExtractor.ProjectPath` (`:164-173`) projects only the original MVT vertices, so a long segment
renders as a straight screen chord instead of the projected curve. **Fix:** subdivide per
`IProjection.MaxRefineAngleRad` (Mercator = ∞ ⇒ no split), as the mesh line path does
(`StyledLineTileBuilder.SubdivideCenterline`), ported to an engine-free `Core` helper. **Hazard:**
`LineAnchor.Segment` (`:20`) indexes the path vertex array — computed against the tile-local path
(`LineAnchorPlacement.Compute`, `SymbolFeatureExtractor.cs:106`) but resolved against the render path
(`LabelStagingMath.cs:126`). Subdividing only `ProjectPath` silently desyncs those indices (in range, wrong
vertex). Subdivide the tile-local path first and feed the same sequence to both anchor-compute and projection.

Screen-space collision needs no change — it is already projection-blind; it only needs occluded labels removed
first, which the horizon cull provides.

## Stages

S1–S4 share the invariant **Mercator snapshots byte-identical**; each is headless-gated (Editor closed).

| Stage | Change | Falsifiable teeth |
|---|---|---|
| **S1** ✅ | Dedup: add a Y axis to `CrossTileLabelKey` + rename the scale to `MetersPerPixel` | A 30°N/30°S same-longitude pair must NOT dedup to one cell; no symbol/label file references `WebMercator.GroundResolution`; Mercator dedup + snapshot parity |
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
render-space to collapse parent/child tile-quantization noise into one cell (`CrossTileLabelKey.cs:16-23`);
geodetic storage reintroduces an equivalent tolerance plus more plumbing, and 3-axis render quantization already
fixes the defect. One tunable to surface in S3: the horizon-cull grazing margin (cf. `LabelViewportSpans`).

---

## Grounding (touch points)

`MapRenderer.Unity/Text/`: `SymbolLabelSubsystem` (queue/pump/store, scale at `:467,484`),
`SymbolTileLabelStore` (active/cached/departing), `SymbolLabelBatch`/`SymbolLabelBatchBuilder` (SoA bridge,
`ProjectCorner`), `Placement/LabelPlacementSystem` (`Tick`, `ComputeTileCoverageCull`, scale at `:477`),
`Placement/LabelScreenProjection` (`:87-88` — the projection seam), `Placement/LabelStagingMath`
(`StageCurved` `:126`, tangent `:181`), `Placement/BillboardMath`, `SymbolFeatureExtractor` (`ProjectPath`
`:164-173`, `LineAnchorPlacement.Compute` `:106`). `MapRenderer.Jobs/`: `SymbolProjectionJob` (`OutValid`),
`LabelStageJob`, `LabelCollisionJob`, `SymbolBillboardJob`. `MapRenderer.Core/Text/`: `CodepointTextShaper`,
`TextQuadLayout`, `CurvedTextLayout`, `PolylineArcWalker`, `Placement/CrossTileLabelKey`, `Placement/LineAnchor`
(`:20`), `Placement/LabelTileCoverage`, `Placement/LabelViewDistance`. `MapRenderer.Core/Geo/`: `IProjection`
(`Project`, `TryGetHorizonOccluder`, `MaxRefineAngleRad`), `SphericalProjection` (`ProjectPoint` `:51-55`,
occluder `:81-86`), `WebMercator`, `CameraPoseMath`, `SceneFrame`, `FloatingOrigin`. Mesh-path prior art:
`StyledLineTileBuilder.SubdivideCenterline`, `ProjectPointsJob<TProj>`, `FrustumTileSelector`.

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
