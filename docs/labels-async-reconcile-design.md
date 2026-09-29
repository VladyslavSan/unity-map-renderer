# Labels — async, event-driven reconcile (design)

The cross-tile dedup — which copy of a point symbol wins when several loaded tiles carry it — is **state**
that changes only on a tile event. It is recomputed off the main thread when a tile event occurs, not every
frame. Companions: [`labels-and-symbols-design.md`](labels-and-symbols-design.md) (the pipeline) and
[`symbol-label-perf-design.md`](symbol-label-perf-design.md), which owns the per-frame label cost downstream
of this reconcile.

---

## 1. The premise

For every point symbol, the dedup hashes a `DedupKey` (the all-integer form of `CrossTileSymbolKey`) into a
dictionary to pick one winner per identity. Two facts make a per-frame recompute of it the wrong shape:

1. **The dedup answer is a pure function of the loaded tile set.** The same tiles give bit-identical winners.
   Camera pan, rotate, and tilt do not change it.
2. **Active parent/child tile overlap does not happen.** The active cover is a quadtree cut: it can mix zooms
   (the default screen-space LOD does), but no active tile has an active ancestor or descendant.
   Coarse-under-fine display is future work. The extractor's single-world clip drops every point anchor
   outside a tile's own `[0, extent)`, so a buffer-strip or world copy never reaches the dedup, and one point
   is owned by exactly one tile at any zoom mix. The dedup arbitrates what the clip leaves: two blocks that
   each own an anchor of one identity in one cell. That set is **static per tile set**, independent
   of camera pose and of fractional zoom. A departing parent coexists with its active children for the grace
   window; the departing pass handles that pair (see the zoom step below).

A per-frame dedup would therefore recompute a constant every frame, and on a moving camera that constant
would be the largest single label cost.

**Rejected: an incrementally-maintained winner index.** An index that patches the winner set per tile event,
and cold-reseeds whenever its zoom-quantize key changes, costs more than the dictionary dedup it replaces: a
moving camera changes that key almost every frame, so the index rebuilds a contender/winner record per label
every frame. Do not revive an incremental winner index, a native tombstone/compaction mirror of it, or any
other "maintain the set incrementally" machinery.

---

## 2. The model — a tile-event-driven state machine

The label batch is not a per-frame rebuild. The **deduped visible-label set is STATE**; it changes only on a
discrete tile event, and that recomputation is **scheduled off the main thread** and *picked up* later.

```
   tile add/remove/rebuild/restyle          (main thread, cheap: just mark dirty + snapshot)
                │
                ▼
        schedule reconcile(genN)  ───────►  WORKER: dedup the snapshot → new set B
                │                                            │
   per frame (main thread):                                 │ (finished)
     if a completed reconcile is pending AND its gen is current:
         diff current set A vs B  →  drive appear/disappear through the FADE machine  →  A := B
     process the CURRENT set: project → collide → fade → draw
```

- **Between event and pickup, render the slightly-stale A.** For labels this is fine — a new tile's labels
  appear a frame or two late; a removed tile's linger and fade. Stable frame rate outranks momentary
  staleness here, as it does for tiles.
- **Per-frame work is only the camera- and time-dependent pass:**
  projection, collision/placement, the fade state machine, world-quad build. That stays on the main thread (it
  reads the camera and must be current). The dedup is not on the per-frame path at all.

**This is NOT the rejected static-frame skip** (B-1, `symbol-label-perf-design.md` § "Constraints"). That
one is keyed on *camera stillness* (smooth static, janky the instant you move — inconsistent). This is keyed
on *tile events* — a discrete, real input change — and is consistent under all camera motion. It memoizes a provably-static value; it does not gamble on the camera
holding still.

---

## 3. The four make-or-break contracts

These four contracts carry the correctness of the reconcile, and the race conditions live in them.

### 3.1 Invalidation events — what dirties the state (must be EXHAUSTIVE)

A missed trigger means stale labels on screen. Every `SymbolTileStore` mutation that changes the active/
departing membership OR a tile's label content dirties the set:

- `BeginBuild` (a rebuild starts — stale labels stay until commit), `CompleteBuild` (new labels land / replace),
- `Release` (→ cached / true-evict), `Restore` (cache hit re-enters), the `ReconcileActiveSet` departing stamp,
- `EnqueueCached` FIFO evict, `PurgeExpiredDeparting`, `Clear` (called by `SymbolSubsystem.SetStyle` on a
  restyle — full rebuild).

**There is no zoom trigger.** The dedup grid is a **fixed render-space grid**
(`CrossTileSymbolKey.CanonicalGridMeters`, 4 m), so the dedup answer is a pure function of the tile set with
zero zoom dependence: neither fractional zoom nor a zoom step dirties it. The fixed grid is correct for
active copies because active parent/child overlap does not happen; merging distinct-but-close features is the
collision pass's job.
**Rejected: keying the grid on integer tile zoom.** That still swaps grids per zoom band, which breaks the
same-cell hold across a zoom step; the fixed grid keeps it where the copies quantize alike. Below about z13
the parent's and the child's MVT quantization differ by more than the 4 m cell, so the copies can fall in different
cells. The departing pass then matches a departing point to an active winner of the same layer, text and icon
within `CrossZoomMatchUnits` coarser-zoom tile units, and emits a fade alias from the departing fade id to the
winner's. The placement system applies the alias once per winner set: the winner starts at the departing copy's
opacity, so the label holds instead of blinking. A match never displaces or reorders an active winner.
A zoom step still swaps the tile *set*, so it dirties the state through the tile add/remove events above.

Each event marks the state dirty (bumps the generation). Many events between two pickups coalesce into one
reconcile.

### 3.2 Input snapshot + native-block lifetime — THE sharp edge

The worker reads tiles' label lists while the main thread may **release/dispose a tile and its baked
`SymbolTileBlock`**. Reading a disposed block off-thread is a use-after-free.

**The contract is a borrow guard.** `SymbolSnapshot` (built by `SymbolTileStore.CaptureSnapshot`) holds each
collected tile's baked block behind a `SharedDisposable` pin acquired on the main thread. The pin lets the
worker read the block's native columns without disposing them, and releases on the snapshot's `Clear()`.
A snapshot of managed refs alone is not enough: it is safe only while the referenced list is immutable after
build, and it does not protect the native columns. *Rejected:* resolving native data on the main thread at
pickup instead of pinning — that puts the cost back on the main thread.

### 3.3 Generation / coalescing — one in-flight, apply-stale

One reconcile is in flight at a time, keyed on the store's monotonic collect generation. Events during a run
bump the generation but **do NOT start a second worker** (coalesced). When the worker returns, its result is
**applied even if the store generation has advanced since it was scheduled** — the completed set is at most a
few frames stale, and a slightly-stale label set for 1–4 frames is the accepted appearance latency (see
"The model"). A completed result is **never discarded**. If the store generation moved during the run, a
**reschedule** is issued so the displayed set catches up on a later pickup. The rule: *apply the completed result, then
reschedule iff the generation advanced during the run*. Discarding a finished result would thrash the worker
and, under continuous tile churn, could starve the display of any update at all.

Two guards make the apply safe:
- **Swap only on worker success.** A faulted/partial result never reaches the consumer (`SymbolGatherPlan.Build`
  assumes aligned lists). On a fault the old front set is held, the failed run's pins are released, the fault is
  logged once, and the schedule guard waits for a *new* tile event — no busy-retry.
- **Native-block lifetime across the one-in-flight gap is the pin guard** ("Input snapshot + native-block
  lifetime"). The store defers disposing a baked block that the in-flight OR the currently-displayed set
  references, until that set leaves service (a double-buffer front/back swap). This is what lets the worker
  read, and the main thread later gather, a block the store would otherwise have freed on a concurrent tile
  release/rebuild.

### 3.4 A→B swap → fade reconcile (diff, not replace)

The swap is a *reconcile*, not a hard replace: diff A vs B → labels only in B **fade in**, labels only in A
**fade out** (they become departing), labels in both keep their fade/incumbency state. The diff feeds the
existing departing/fade state machine (`SymbolPlacementSystem`'s per-symbol departing flag + fade triggers),
so nothing pops. A label must be recognized as "the same" across an A→B swap to keep its fade, so cross-frame
label *identity* is the key sub-problem (see `labels-and-symbols-design.md` on cross-tile identity /
`FadeId`).

**Identity is one canonical id.** The dedup identity and the point fade identity are the **same** canonical
cell: `PointFadeId` and the store `DedupKey` both quantize to `CrossTileSymbolKey.CanonicalGridMeters` over
`(cell, layer, interned text, interned icon)`. That id is fixed and camera-independent, so a point label keeps
its identity across an A→B set swap, and the reconcile diff keys A↔B matching directly on it — no separate
identity scheme. Curved (line) labels keep `LineFadeId`: they are never deduped and are identified per
anchor. The partition is unified; making the fade id the literal interned integer is not done and is not
needed by the reconcile.

Two properties keep the swap stable:

- **Curved (line) labels never dedup:** the reconcile emits every active curved symbol in scan order, with
  no key and `LineFadeId` as identity, so they are always-winners and cannot churn the dedup.

---

## 4. Off-main mechanics + the GC caveat

- **Scheduling:** the dedup is plain managed C# over label lists (no Unity API, no Burst — a `Dictionary`
  keyed on interned text/icon ids), so it runs on a worker through the repo's linear-async idiom (UniTask
  `SwitchToThreadPool` → work → `SwitchToMainThread` to pick up), the same pattern as the async tile pipeline
  (off-main-thread-principle). Pump and pickup run on the main-thread label update.
- **GC caveat:** off-main removes *CPU time* from the render thread, but Unity's Mono GC is
  **stop-the-world** — a heavy-allocating dedup on a worker can still trigger a collection that pauses the
  main thread. So the reconcile is *also* **low-alloc**: buffers are reused, and label text is interned to an
  `int` so the dedup key is all-integer, with no string hash and no allocation per key. Off-main and
  alloc-reduction **compose** — they are not either/or.

---

## 5. Open questions

- **When active parent/child overlap arrives:** the active pass would need finest-zoom-wins across cells, and
  nearness is not transitive, so it cannot reuse the departing pass's match. Until then, two active winners of
  one symbol in different cells would show twice. The cover never produces such a pair today: the extractor
  clip leaves one copy of a point across a screen-space LOD zoom boundary. It is
  *not* integer-zoom keying (rejected; see "Invalidation events").
