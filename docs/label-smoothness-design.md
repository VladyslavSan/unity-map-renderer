# Label smoothness & robustness — design

The label subsystem today is **stateless**: `LabelPlacementSystem.Tick` clears everything and rebuilds
projection → collision → billboard quads from scratch every frame, and labels are aggregated per
`(source, tile)` with no cross-frame or cross-tile memory. That produces four user-visible problems:

1. **Pop, not fade.** A label that stops being placed (collision, or its tile leaves cover) vanishes instantly.
2. **Blink when idle.** Even with zero camera motion, the per-frame rebuild lets an async build landing / a
   transient tile release / a hair of collision-input drift flip a survivor for a frame → flicker.
3. **Line labels slide on zoom.** `symbol-placement:line` anchors are fixed *screen-px* distances from the
   projected line start, so changing the projected length moves every anchor to a different world point.
4. **Cost.** ~20k symbols at some zooms → `MapRenderer.Symbol.Project` ~20ms **every frame, even idle**.

Plus two structural issues:
5. **Fragile push-callbacks.** The subsystem mirrors the tile lifecycle via `SymbolTileBytesReady/Released/
   Restored` callbacks — ordering, reentrancy, and a hand-threaded released-to-cache-vs-evicted bit.
6. **Single glyph atlas overflows** (one fixed 4096², mostly a CJK capacity limit).

This is the plan to fix them. The spine (A) is one idea: **the placement layer becomes a state machine keyed
by a stable cross-tile symbol id, and it owns each label's on-screen lifetime** (it decides what stays drawn
and for how long, independent of which tiles are loaded). B is performance, C is capacity — sequenced apart.

---

## A‑1 — Pull / reconcile foundation (retire the push-callbacks)

**Problem.** Lifecycle-by-callback is the fragile part: a missed/mis-ordered `Released`/`Restored` desyncs the
label set (that was the zoom-out-then-in bug), and it needed a "cached vs evicted" bit threaded through.

**Design.** Split *data delivery* from *lifecycle*:
- **Membership is PULLED.** Each frame the subsystem asks `TileManager` for the current loaded `(source,tile)`
  set + a **version stamp** (bumped only when the set changes). It reconciles: a tile in the set with no labels
  yet → build; a tile-with-labels no longer in the set → hand to the fade-out path (A‑4), not an instant drop;
  present-and-built → no-op. Self-healing: a missed change is corrected next frame; no reentrancy because
  nobody mutates mid-callback.
- **Bytes stay a push** — but only as a pure *data* event (`SymbolTileBytesReady`), which is legitimate (it
  delivers the fetched bytes exactly when they exist). The fragile *lifecycle* callbacks (`Released`,
  `Restored`, the transferred-to-cache bool) are **deleted** — membership derives them.
- The `SymbolTileLabelStore` (built this batch: active/cached split, async stale-guard, identity) **survives as
  the reconcile backing store**; `Release`/`Restore` become internal reconcile transitions rather than
  externally-driven callbacks.

**New TileManager surface (pull):** `LoadedTileKeys(version)` — returns the current `(source,tile)` set + a
monotonic version; cheap to call every frame (reconcile early-outs when the version is unchanged → feeds B‑1).

**Teeth.** Reconcile is idempotent (calling twice with the same loaded set = no change); a tile removed from
the pulled set is no longer collected; the zoom-out-then-in case works without any release callback.

---

## A‑2 — Stable WORLD line anchors (fix labels sliding on zoom)

**Problem.** `StageCurvedLabel` walks the *projected screen* polyline and places `line` repeats at fixed
screen-px arc distances from the start. Zoom changes the projected length → the same arc maps to a different
world position → the text slides, and the anchor count changes → labels pop.

**Design (MapLibre `getAnchors`).** Compute anchors **once at build time in tile/world space** — stable
positions along the line, spaced by `symbol-spacing` resolved at a reference scale — and carry each as a world
position on `SymbolLabel`/`LabelInstance`. Per frame, project each *anchor* and lay the glyphs out around it by
walking only the *local* neighbourhood of the projected line. The anchor no longer slides because it is a fixed
world point, not a screen offset. `line-center` = one anchor at the line's world midpoint.

**Teeth.** A line label's world anchor is invariant under a zoom change (the decisive test: project the same
line at two zooms → the anchor's *geo/world* position matches). Removes the per-frame screen-space anchor
generation loop entirely (also retires the A‑0 hang-cap's reason to exist for the common path — keep the cap as
defence-in-depth).

**Scope note.** This is also the prerequisite for giving line labels a cross-tile identity (A‑3): a stable
world anchor → a stable id.

---

## A‑3 — Cross-tile symbol identity + dedup (seamless no-op replacement)

**Problem.** A symbol present in a parent tile and its child tile during a zoom transition are two unrelated
`LabelInstance`s → the swap re-collides and re-fades = a pop, and both are projected (wasted work).

**Design.** A stable **cross-tile id** = `hash(quantize(worldAnchor), resolvedText, layerId)`.
`worldAnchor` is `AnchorRender` (pre-RTC world/mercator — the same for a geo point at any zoom), quantized to
~1px-at-max-zoom so tiny reprojection differences between zoom levels collapse to one id. A `CrossTileLabelIndex`
maps id → the chosen instance; at **collection** time, when the same id appears in multiple loaded tiles (parent
+ child), pick one deterministically (finest zoom, then lowest tile key). Same id across a tile swap ⇒ the
record **persists** ⇒ opacity stays 1 ⇒ **no fade cycle, a true no-op** (the user's "seamless connect"), and the
label count drops by the duplicate factor.

**Decisions (user gave latitude):** quantization granularity is tunable (start ~1px-at-maxzoom). **Line labels
in v1:** key each *placed world anchor* (from A‑2) as its own id; if that proves noisy, fall back to excluding
line labels from dedup (they behave as today) — call it out, don't let it silently misbehave.

**Teeth.** Two overlapping loaded tiles carrying the same point symbol → one id, one placement; the id is stable
across a simulated parent→child tile swap.

---

## A‑4 — Placement state machine + fade

**Problem.** No persistent opacity; changes pop.

**Design.** The placement layer holds a persistent record per cross-tile id:
`{ opacity, lastPlaced, lastScreenPlacement }`. Each `Tick`: gather this frame's candidates (post A‑3 dedup) →
match to records by id → ease each record's opacity toward `(present && collision-placed) ? 1 : 0` over a
fixed **fade duration** (~300ms, hardcoded for now; wire `symbol-fade-duration` later) → **emit every record
with opacity > 0**, using its last known placement, *even if its tile is no longer loaded* → drop records that
reach 0 and are absent. So a label whose tile just left cover keeps drawing at decaying alpha — the placement
layer **owns the on-screen lifetime** (this is why fade state lives here, not in the tile-keyed store).

**Cheap on the GPU.** `PlacedQuad.Color.w` is already the per-vertex alpha the shader emits — **no shader /
vertex-format change**. Fade is pure CPU state.

**Teeth.** A label removed from the candidate set does not vanish — its emitted alpha decays over the fade
window across successive Ticks and reaches 0; a re-appearing id within the window keeps its opacity (no
re-fade). This also masks the idle blink (a one-frame flip no longer pops — it's a sub-perceptual alpha step).

---

## B‑1 — Static-frame skip

When the view-projection, the reconcile version stamp, **and** the fade state are all unchanged since last
frame, reuse the last frame's built billboard buffers verbatim — skip project/collide/build entirely. Kills the
~20ms `Project` when idle and removes the idle blink at the root (no rebuild → nothing to flicker). "No active
fades" is part of the unchanged test, so fades still animate.

## B‑2 — Burst-jobified projection

For the moving case at ~20k labels: gather candidate world anchors into a `NativeArray<double3>` and project
them (+ cull) in a Burst job → `NativeArray<float2>` screen + mask; the managed greedy collision stays (it is
serial). This attacks the per-anchor matrix-mul cost that dominates `Project` when the camera moves.

## C‑1 — Multiple glyph atlases (capacity, separate track)

One fixed atlas overflows on large scripts. Give `GlyphAtlasEntry` an **atlas index**; when an atlas fills,
spill new glyphs into an additional atlas. Rendering: either a per-quad atlas index → `Texture2DArray` layer
(one vertex-format field + a tiny shader change), or partition draws by `(material slot × atlas)` like the
existing per-layer material slots. This is the **only** stage needing a vertex-format/shader change, and it is a
capacity fix (not smoothness) — sequence it independently of A/B.

---

## Sequencing

`A‑1 → A‑2 → A‑3 → A‑4 → B‑1` is the smoothness + idle-perf spine (each independently shippable, gated +
committed per slice). `B‑2` is the moving-case perf follow-up. `C‑1` is a separate capacity track, done whenever.
The intermittent idle **blink** is expected to be resolved by A‑4 + B‑1; confirm the root cause when there
rather than assuming.

---

## Future improvements (captured, not yet scoped)

Requested by the maintainer 2026‑07‑10 after the A‑1→A‑4 spine landed. Both attack the same thing — the
**per‑frame label processing cost/quality** — and are natural extensions of the B (perf) and A (stability) tracks.

### B‑3 — Horizon / far-distance label culling (perf)

In a **tilted** view the far half of the frustum compresses a huge ground area into a thin band near the
horizon, so most labels there **pile up, get collision-discarded, and jitter** (projection is numerically
unstable as depth → the far plane). Processing them every frame is near-pure waste of the ~20 ms `Project` +
the collision pass.

- **Cull labels near/beyond the horizon before projecting them** — a per-label test cheaper than the projection
  it avoids. Two candidate cheap discriminators: (a) **world-space ground distance** from the camera look-at
  (or the tile's distance) beyond a pitch-dependent threshold — computed pre-projection, so it skips the matrix
  mul entirely; (b) a **far-depth / above-horizon screen cull** after projecting (rejects what (a) misses but
  cannot save the projection cost). Prefer (a) as the primary gate, (b) as a backstop.
- Relate to the existing **far-plane policy** (`GeometryAwareFarPlane` / `RaySphereFarPlane`): tiles already
  have a far cull; labels want their **own, tighter** distance cut (labels stop being legible/stable well
  before tiles stop being drawn). MapLibre has an analogous pitch-scaled fade-out of distant symbols.
- A "distance-based discarding" knob (maintainer's phrase) fits here — fade or drop symbols past a distance so
  the far band thins out gracefully rather than churning.
- Pairs with **B‑1**: fewer candidates every frame *and* the static-skip avoids redoing them when idle.

### A‑5 — Collision fighting / placement hysteresis (stability)

Labels **flicker on a subtle camera change** — a hair of motion flips which of two near-tied candidates wins
the greedy collision, exactly like **z‑fighting**. A‑4's fade *eases* the alpha flip and B‑1 skips *fully*
static frames, but a **slowly-moving** camera still re-runs collision every frame and can oscillate a marginal
pair.

- **Sticky placement / hysteresis:** bias a candidate that was **placed last frame** to stay placed (a small
  bonus in the placement order, or a "kept" flag that only yields to a clearly-higher-priority intruder, not a
  near-tie). Keyed by the A‑3/A‑4 cross-frame identity that already exists — the placement layer knows what it
  drew last frame.
- This is a **deliberate, careful feedback of placement history into collision** — the one place the "downstream
  of `SelectSurvivors`, never fed back" rule is intentionally relaxed. Must be tuned to break ties *consistently*
  without introducing oscillation or letting a stale winner block a genuinely higher-priority label (add a
  hysteresis margin, not an absolute lock).
- Analogous fixes to depth-fighting: a bias/epsilon that makes the tie-break **stable across frames** rather
  than recomputed cold each frame.

### B‑4 — Pipelined (deferred) placement — decouple the decision from the render (perf, biggest moving-case lever)

The expensive part (collision/culling) runs on the **main-thread critical path every frame** today. Move it
**off** the critical path into the frame "loophole" — the worker-thread time after LateUpdate finishes and the
render thread is submitting — by making the placement DECISION one frame stale. This is the MapLibre
placement/render split, mapped onto Unity's job timeline.

- **Every frame (cheap, on-path):** project the *current* label set to screen (positions MUST be live — labels
  track the camera) and emit the survivors **decided at the end of the previous frame**, through the A‑4 fade.
  Positions are current; only the *survivor set* lags one frame.
- **End of frame (loophole, off-path):** `Schedule` the collision over THIS frame's projected boxes as a Burst
  job and do **not** `Complete()` it until next frame's LateUpdate — it runs on worker threads overlapping the
  render thread, deciding visibility for frame N+1. One projection per frame feeds both the emit (N‑1's
  decision) and the job (deciding N+1).

**Why it holds:** the camera moves a hair per frame, so a 1-frame-stale survivor set over *current* positions
matches the fresh answer almost always; and **A‑4 absorbs the residual** — a late appear/disappear eases in/out
instead of popping (the fade and the pipeline are complementary by construction).

**Caveats:** (1) collision is **serial** (greedy — each placement depends on prior survivors), so it is ONE
Burst job, not a parallel fan-out; it still overlaps the render thread, just not across cores (the grid keeps it
~O(n·k)). (2) It needs **native/blittable** collision data to be a job — the same native-storage work B‑2
implies; share it. (3) On a **camera teleport / large delta**, force a synchronous recompute (or let the fade
cover the one bad frame). (4) Composes with the rest: **B‑1** skips scheduling when idle, **B‑2** makes the
per-frame projection the cheap parallel job, **B‑4** moves the collision decision to the loophole — together the
whole moving-case story, and B‑4 is the biggest lever.
