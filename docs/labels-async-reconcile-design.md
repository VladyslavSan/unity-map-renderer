# Labels — async, event-driven reconcile (design)

**Status:** designed, not started. This supersedes the Phase-2 options (A–E) in
[`symbol-label-perf-design.md`](symbol-label-perf-design.md) — see its **§9 OUTCOME** for the profiling that
falsified the old premise. Companion: [`labels-and-symbols-design.md`](labels-and-symbols-design.md) (the
pipeline). Proprietary / all rights reserved.

---

## 1. The premise (proven, not assumed)

Live z14-15 profile, moving camera: `MapRenderer.Symbol.BatchBuild.Collect.Dedup` ≈ **10.8 ms** — the whole
per-frame label CPU cost. `Collect.Classify` ≈ 0.7 ms, `SoA` ≈ 0.7 ms. So the bottleneck is the **per-frame
cross-tile dedup** (`SymbolTileLabelStore.CollectInto`): for every on-screen point label, every frame, hash a
`CrossTileLabelKey` (which contains the label's **text string**) into a dictionary to pick the finest-zoom
winner.

Two facts make this the wrong shape:

1. **The dedup answer is a pure function of the loaded tile set.** Same tiles → bit-identical winners. Camera
   pan / rotate / tilt do not change it. It is recomputed from scratch every frame for nothing.
2. **Parent/child tile *overlap* does not currently happen** (coarse-under-fine display is future work — a
   tile set is one zoom band at a time). So the *only* real duplication today is **same-zoom edge/buffer +
   cross-source** — which is **static per tile-set**, independent of camera pose *and* fractional zoom. The
   fuzzy pixel-scaled grid + finest-zoom-wins machinery is future-proofing we pay 11 ms/frame to carry.

**Why not "just make the per-frame dedup incremental" (the reverted D2):** D2 maintained the winner set with a
persistent incremental index that *cold-reseeds on every zoom-quantize change*. A moving camera zooms
constantly, so it rebuilt the whole index (`new Contender`/`WinnerRecord` per label) every frame — **~2× worse**
than the plain dict dedup. Right target, wrong mechanism; reverted (`e3a9208e`). Do **not** revive an
incremental winner index.

---

## 2. The model — a tile-event-driven state machine

Stop treating the label batch as a per-frame rebuild. The **deduped visible-label set is STATE**; it changes
only on a discrete tile event, and that recomputation is **scheduled off the main thread** and *picked up*
later.

```
   tile add/remove/rebuild/restyle          (main thread, cheap: just mark dirty + snapshot)
                │
                ▼
        schedule reconcile(genN)  ───────►  WORKER: dedup the snapshot → new set B
                │                                            │
   per frame (main thread):                                 │ (finished)
     if a completed reconcile is pending AND its gen is current:
         diff current set A vs B  →  drive appear/disappear through the FADE machine  →  A := B
     process the CURRENT set: coverage cull → project → collide → fade → draw
```

- **Between event and pickup, render the slightly-stale A.** For labels this is fine — a new tile's labels
  appear a frame or two late; a removed tile's linger and fade. This is the repo's existing "stable-FPS is the
  North Star, momentary staleness OK" stance (responsive-consume / white-tiles-OK).
- **Per-frame work is only the genuinely camera/time-dependent pass:** coverage cull (`ClassifyActive`, ~0.7 ms),
  projection, collision/placement, the fade state machine, world-quad build. That stays on the main thread (it
  reads the camera and must be current). The ~11 ms dedup leaves the per-frame path entirely.

**This is NOT the rejected "batch cache."** That one was keyed on *camera stillness* (smooth static, janky the
instant you move — inconsistent). This is keyed on *tile events* — a discrete, real input change — and is
consistent under all camera motion. It memoizes a provably-static value; it does not gamble on the camera
holding still.

---

## 3. The four make-or-break contracts

The concept is easy; these four are where the correctness (and the race-condition bugs) live. Nail them on
paper before any code.

### 3.1 Invalidation events — what dirties the state (must be EXHAUSTIVE)

A missed trigger = stale labels on screen. Every `SymbolTileLabelStore` mutation that changes the active/
departing membership OR a tile's label content dirties the set. The reverted D2 already mapped these 13
lifecycle points — reuse the **map**, not the code (mark dirty instead of `IndexAddTile`):

- `BeginBuild` (a rebuild starts — stale labels stay until commit), `CompleteBuild` (new labels land / replace),
- `Release` (→ cached / true-evict), `Restore` (cache hit re-enters), the `ReconcileActiveSet` departing stamp,
- `EnqueueCached` FIFO evict, `PurgeExpiredDeparting`, `Clear` / `SetStyle` (restyle — full rebuild),
- **NO zoom trigger at all.** *(LANDED — Stages 3/3b, `08e7fbf3`/`3f6aa95d`.)* The dedup grid was decoupled from
  `MetersPerPixel` — but NOT onto integer tile zoom as originally sketched here. It is now a **fixed geo grid**
  (`CrossTileLabelKey.CanonicalGridMeters = 4.0`), so the dedup answer is a pure function of the tile set with
  **zero** zoom dependence (neither fractional nor a zoom *step* dirties it). The fixed grid is correct because
  parent/child overlap doesn't happen today; merging distinct-but-close features is the collision pass's job. This
  is stronger than the integer-zoom plan (which would still swap grids per band, breaking the seamless same-cell
  hold across a step). It also PRESERVES that hold. **A zoom step still swaps the tile *set*, so it dirties via the
  tile add/remove events below — not via a dedicated zoom trigger.** (Future: when parent/child overlap lands
  (§6), the grid goes back to zoom-scaled — coarser band wins — a one-const change.)

Each fires "mark dirty (bump generation)". Coalesce: many events between two pickups collapse to one reconcile.

### 3.2 Input snapshot + native-block lifetime — THE sharp edge

The worker reads tiles' label lists while the main thread may **release/dispose a tile and its baked
`SymbolTileLabelBlock`**. Reading a disposed block off-thread is a use-after-free. This is the same hazard
D3's B2 atomicity fought and the one flagged for moving symbol decode off-thread (the shared glyph atlas).

Contract options (decide in the plan):
- **Snapshot managed refs** (`LabelInstance` list) at schedule time — cheap and safe *if* `LabelInstance` is
  immutable post-build (verify).
- **Native blocks need a borrow guard:** a refcount, or a "not-disposed-while-a-reconcile-borrows-it" rule, so
  the store cannot free a block the in-flight worker is reading. Simplest safe form: the dedup snapshot holds
  the winning labels *by managed value* it needs, and the block/native slice is resolved on the **main thread**
  at pickup (not on the worker) — so the worker never touches a `NativeArray`. Prefer this if feasible.

### 3.3 Generation / coalescing — one in-flight, apply-stale (LOCKED — implemented Stage 4b)

One in-flight reconcile at a time, keyed on the store's monotonic collect generation. Events during a run bump
the generation but **do NOT start a second worker** (coalesced). When the worker returns, its result is
**applied even if the store generation has advanced since it was scheduled** — the completed set is at most a few
frames stale, and serving a slightly-stale label set for 1–4 frames is exactly the accepted appearance latency
(§2). A completed result is **never discarded**. If the store generation moved during the run, a **reschedule**
is issued so the displayed set catches up on a subsequent pickup. So the rule is: *apply the completed result,
then reschedule iff the generation advanced during the run* — never throw a finished result away (discarding it
would thrash the worker and, under continuous tile churn, could starve the display of any update at all).

Two guards make the apply safe:
- **Swap only on worker success.** A faulted/partial result never reaches the consumer (`SymbolGatherPlan.Build`
  assumes aligned lists). On a fault the old front set is held, the failed run's pins are released, the fault is
  logged once, and the schedule guard waits for a *new* tile event — no busy-retry.
- **Native-block lifetime across the one-in-flight gap is the pin guard (§3.2).** The store defers disposing a
  baked block that the in-flight OR the currently-displayed set references, until that set leaves service (a
  double-buffer front/back swap). This is what lets the worker read, and the main thread later gather, a block
  the store would otherwise have freed on a concurrent tile release/rebuild.

### 3.4 A→B swap → fade reconcile (diff, not replace)

The swap is a *reconcile*, not a hard replace: diff A vs B → labels only in B **fade in**, labels only in A
**fade out** (they become departing), labels in both keep their fade/incumbency state. Feed this through the
existing departing/fade state machine (`LabelPlacementSystem`'s `RecordDeparting` + fade triggers) so nothing
pops. This is the other half of "label system as a state machine" and the trickiest interaction — cross-frame
label *identity* (a label must be recognized as "the same" across an A→B swap to keep its fade) is the key
sub-problem (see `labels-and-symbols-design.md` on cross-tile identity / `FadeId`).

> **Identity: SOLVED — one canonical id.** *(LANDED — Stage 3b, `3f6aa95d`.)* The dedup identity and the point
> fade identity are now the **same** canonical cell — `PointFadeId` and the store `DedupKey` both quantize to
> `CrossTileLabelKey.CanonicalGridMeters` (4.0 m) over `(cell, layer, interned text, interned icon)`. Because that
> id is fixed and camera-independent, a point label keeps its identity across an A→B set swap by construction — the
> reconcile diff can key A↔B matching directly on this id, no bespoke identity scheme. (Curved/line labels keep
> `LineFadeId` — never deduped, per-anchor.) The deeper "fade id IS the literal interned-int" (Design Y) is
> deferred; the *partition* is unified, which is what the reconcile needs.

---

## 4. Off-main mechanics + the GC caveat

- **Scheduling:** the dedup is plain managed C# over label lists (no Unity API, no Burst — it's a `Dictionary`
  + `LabelInstance`), so it runs on a worker via the repo's linear-async idiom (UniTask
  `SwitchToThreadPool` → work → `SwitchToMainThread` to pick up), the same pattern as the async tile pipeline
  (off-main-thread-principle). Pump/pickup on the main-thread label update.
- **GC caveat (do not over-promise):** off-main removes *CPU time* from the render thread, but Unity's Mono GC
  is **stop-the-world** — a heavy-allocating dedup on a worker can still trigger a collection that pauses the
  main thread. So the reconcile must *also* be **low-alloc** (reused buffers; and intern label text → `int` so
  the dedup key is all-integer, killing the per-frame string hash *and* its allocations). Off-main and
  alloc-reduction **compose** — they are not either/or. The interning is not wasted work; it makes the off-main
  job cheap enough to not GC-stall.

---

## 5. Salvage from the reverted D2/D3, and scope fences

**Salvage (cherry-pick from history `6e39e282`/`962c1349`):**
- The `LabelStagingMath` **finite-`SortKey` / NaN collision-order fix** (D2 edit 0) — a real, orthogonal
  hardening in the *placement* path (a NaN sort key makes the collision comparator intransitive →
  nondeterministic survivor set). Independent of aggregation; re-land it.
- The **13-mutation-point map** (§3.1) — knowledge, not code.
- The **`source→int` interning** pattern — extend to text interning (§4).

**Do NOT reuse:** the incremental winner index (`CellState`/`Contender`/`WinnerRecord`/delta log), the native
tombstone/compaction mirror (D3), or any "maintain the set incrementally" machinery. That is the reverted
regression.

**Scope fence:** symbol label code only — `MapRenderer.Unity/Text/**`, `MapRenderer.Core/Text/**`, the symbol
build/reconcile hooks. NOT the tile pipeline / camera / projection / fill-line meshing / backends.

---

## 6. Open questions (resolve in the plan)

- **Native-block lifetime (§3.2):** refcount/borrow-guard vs "resolve native on main-thread at pickup." The
  latter is safer; confirm the dedup can produce a purely-managed result the main thread turns into native.
- ~~**Cross-frame label identity across an A→B swap (§3.4):**~~ **RESOLVED (Stage 3b).** One canonical fixed-grid
  id serves dedup + fade; a point label keeps its identity across a swap by construction. See §3.4's note.
- **When parent/child overlap lands (future):** the dedup grid must go back to zoom-scaled + finest-zoom-wins.
  Localized to the one `CrossTileLabelKey.CanonicalGridMeters` use + the store's grid input (a seam comment marks
  it) — *not* integer-zoom keying (that approach was rejected; see §3.1).
- **Curved (line) labels:** never dedup — they pass straight through (keep `LineFadeId`). Confirm they ride the
  state machine as trivial always-winners with no reconcile churn.
- **`ClassifyActive` stays per-frame** (camera-dependent, cheap) operating on the current set — confirm it
  composes with A being stale for a frame (a just-swapped-out tile's coverage decision is harmless).

---

## 7. Target

`Collect.Dedup` → **~0 ms on frames with no tile event**; the ~11 ms happens once per tile-set change, on a
worker, off the render thread. `BatchBuild` drops to the per-frame residual (`Classify` + `SoA` ≈ 1.5 ms).
Byte-identical steady-state render; fade-smooth under set swaps; no motion-keyed cost cliff. **The gate is a
maintainer Play-mode re-profile while panning/zooming — not a headless number.**
