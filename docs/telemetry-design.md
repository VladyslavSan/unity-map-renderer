# Telemetry — provider / consumer split (design sketch)

**Status:** LANDED. TWO of §1's premises were wrong and are corrected in place: the routing (pull, not push)
and §1.3's "+5 ms is probably Editor repaint" (it was real unmarked work, since removed). §7's questions are
resolved there; §5 records what shipped. What remains is build-side verification and the Profiler module —
see §8. Proprietary / all rights reserved.

Companion: `docs/symbol-label-perf-design.md` §10.4 (the profiling episode that motivated this),
`docs/frame-timeline.md` (the marker taxonomy telemetry sits beside).

---

## 1. Why

> **Premise correction (2026-07-26).** The first draft of this section said telemetry is *"pushed into one
> hard-wired consumer … every frame, unconditionally."* That was backwards. `MapView` has **no publish path at
> all**: `MapTelemetryPanel.Update()` **pulls** (`Tick()` → `Map.View.CaptureTelemetry()`), and the only other
> callers of `Capture*` are tests. What follows is the corrected reasoning; it changes which problem each part
> of the design actually solves.

Telemetry today is **pulled by one hard-wired consumer**: `MapTelemetryPanel.Update()` calls
`MapView.CaptureTelemetry()` / `CaptureSymbolTelemetry()` and copies the results into public MonoBehaviour
fields every frame. Three problems:

1. **It costs what the consumer costs.** An Inspector full of live public fields forces an Editor repaint every
   frame. That is real CPU, and it is paid whether or not anyone is looking. (Pull already means a *disabled*
   panel costs nothing — the fix for this is that the panel must be **off by default**, not that the routing
   changes.)
2. **It is Editor-only.** The panel cannot be read in a Development standalone build — which is exactly where
   perf verdicts have to come from (§3 below). **This is the problem the counter consumer solves, and it is the
   one that actually blocks measurement.**
3. **It was suspected of confounding a real measurement — and that suspicion turned out to be wrong.** While
   profiling R1, the Editor showed **two `PlayerLoop`s and two render loops per captured frame**, and a "camera
   moving costs +5 ms" delta that did not appear in the main loop's marker tree at all. Editor-side repaint
   (which scales with mouse activity, i.e. with panning) was recorded here as the leading explanation.

   **Correction (2026-07-26).** It was not repaint. The delta was **real, unmarked main-thread work**: the
   all-or-nothing mirror rebuild, which runs on virtually every frame under motion because the memo is
   structurally dead there (`symbol-label-perf-design.md` §10.4). Bursting that rebuild closed the gap —
   13 ms vs 19 ms still-vs-moving became ~identical (§"R2 + R3 + Burst gather" re-profile). An Editor repaint
   artifact would not have been fixed by a Burst job. The reason it was invisible in the marker tree is that
   the hot spot had no marker yet; `Symbol.BatchBuild` was added later, precisely to surface it.

   The durable lesson is the inverse of what was written: **an unmarked hot spot can look exactly like an
   Editor artifact — mark the span before blaming the environment.** The observation about the double
   `PlayerLoop` stands as an Editor-capture caveat; it just was not the explanation here. And the general
   point survives untouched: a telemetry surface that inflates the thing it measures is worse than no
   telemetry, which is why the panel is now off by default.

**Why not a publish channel (the shape this doc first landed).** The first implementation put a
`TelemetryChannel<T>` between providers and consumers — a generic event with a `HasSubscribers` early-out. It was
removed, and the reasoning is worth keeping because it is the reasoning that produced the current shape:

- Its stated benefit was "zero cost when nobody subscribes", but §1.1 above already concedes that plain pull plus a
  disabled component gives that more cheaply. That left **single-capture fan-out** as the only justification: two
  independent pullers would each pay for `CaptureTelemetry()`.
- Fan-out turned out not to need a channel. A provider that simply *owns* its snapshot as a field and hands out a
  `ref readonly` reference serves N readers from one refresh, with no subscriber registry, no delegate, no event.
- And the capture cost it was protecting barely exists. Two of the three providers store every level as they run —
  their refresh is a repackage, not a capture. Only `TileManager` derives anything, and both derivations are
  bounded by the cover plus its pad ring over reused scratch.

So the machinery was insurance against a cost that two providers never had and the third pays trivially.

The snapshots themselves are fine — engine-free, `init`-only carriers with real tests behind their derived math.
**This design changes who owns them, how a reader reaches them, and what it costs when nobody looks.**

## 2. Principles

- **Zero cost when nobody looks.** A consumer that does not read costs nothing, because there is no push to
  suppress: a disabled `MonoBehaviour` gets no `Update`, and the counter consumer early-outs on
  `Profiler.enabled`. Telemetry must never be a thing you have to remember to turn off.
- **The provider owns its levels; a reader borrows them.** Each provider keeps one snapshot struct as a field,
  updates it as its own pass runs, and exposes `ref readonly`. No copy per reader, no boxing, nothing in the
  middle. Returning by value would silently reintroduce the copy this exists to avoid.
- **Pull stays pull.** The existing snapshot contract (every snapshot type: *"every field is an
  instantaneous LEVEL at the instant of capture, never a rate"*) is correct and stays. Rates and ratios are
  **derived by consumers**, as `MapTelemetryPanel` already does for the prepared-cache hit rate.
- **Core stays engine-free.** Interfaces and carriers in `MapRenderer.Core`; anything touching `UnityEngine` or
  `Unity.Profiling` lives in `MapRenderer.Unity`.
- **No test-only production surface** (`docs/conventions-short.md`). A counter exposed solely so a test can read
  it does not belong on a production type.

## 3. Design

**Every type that has telemetry to report is its own provider.** It owns the numbers as a struct field, updates
them as its own pass runs, and hands them out by reference; nothing assembles a snapshot out of another type's
counters. That is the whole rule, and it is what decides everything below.

```csharp
private LabelPlacementTelemetrySnapshot _telemetry;

internal ref readonly LabelPlacementTelemetrySnapshot Telemetry => ref _telemetry;
```

`MapView` is **not** a provider — it produces no telemetry of its own, and it does not re-export any either. A
consumer reaches the owner and reads its `Telemetry`: `view.TileManager.Telemetry`, `view.Labels.Telemetry`,
`view.Symbols.Telemetry`. All three owners are internal members of the view, so there is nothing to forward.

**Why no forwarding properties on the view.** An earlier revision had one `ref readonly` property per snapshot type
on `MapView`. Two of the three were pure redundancy — `TileManager` and `Labels` were already internal — and the
trio's real cost was that it created the obvious place for someone to eventually compose one view-level snapshot out
of three providers' counters, which is the exact shape this design exists to remove. Deleting them also deletes that
temptation.

| provider | snapshot | refreshes at the end of |
|---|---|---|
| `TileManager` | `TileTelemetrySnapshot` | `Tick` — via a `TickCore` shell, so the clean-cover early return still refreshes |
| `SymbolLabelSubsystem` | `SymbolStoreTelemetrySnapshot` | `CurrentBatch`, where the last of its levels is decided |
| `LabelPlacementSystem` | `LabelPlacementTelemetrySnapshot` | `Tick` |

A reader takes a reference whenever it wants one. Nothing registers, nothing unregisters.

**Two of the three refreshes are free.** `LabelPlacementSystem` and `SymbolLabelSubsystem` already store every
level their pass produces (`LastQuadCount`, `ActiveTileCount`, …), so the refresh just repackages fields. Only
`TileManager` derives anything — `TileCoverStats.Compute` plus a walk of `_loaded` — and it does so once per `Tick`
unconditionally, because both are bounded by the cover plus its pad ring over reused scratch arrays. That is the
whole reason no staleness flag or demand counter exists anywhere in this design.

**Why the symbol snapshot is two.** The old `SymbolTelemetrySnapshot` mixed store state (active/cached tiles,
the coverage drop) with placement results (candidates, survivors, quads, fades, mirror rebuilds) — two owners,
one carrier, and therefore something in the middle assembling it. Splitting by owner is what makes the rule
above true rather than aspirational.

**What this costs: one-instant coherence.** Refreshing at the end of each provider's own pass means the levels
are *that pass's* — fresher than a shared end-of-frame capture — but two providers' numbers no longer come from
the same instant. A value derived across providers can be a phase off. Accepted deliberately: for observability
freshness beats simultaneity, and the alternative (one shared capture point) is what forced a middleman in the
first place. Anything that genuinely needs two providers' numbers as of one instant needs a snapshot type of
its own, owned by whoever owns that instant.

**A provider that did not run holds its last values.** A frame with no symbol layers never reaches `CurrentBatch`,
so the store does not refresh — the struct keeps what it last really measured rather than zeroing itself. "The pass
did not run" is a different claim from "it ran and found zero", and a provider should not invent the second. Note
this is what both consumers already did under the push model too: the panel only wrote on publish and never
cleared, and the counters deliberately omit `ResetToZeroOnFlush` so they hold. No consumer could ever observe the
difference, which is why the pull model does not need a stamp to preserve it.

**`ref readonly`, not a returned copy.** The accessor hands back a reference to the provider's own field:

```csharp
internal ref readonly TileTelemetrySnapshot Telemetry => ref _telemetry;
```

The snapshots are `readonly struct`s well over 16 bytes, so `ref readonly` is the same convention as `in` on a
parameter — and being `readonly` is what stops member access through the reference forcing a defensive copy per
field read. Two things silently undo it: returning by value, and erasing the type to `object` or a non-generic
interface anywhere on the path. Both are covered by the allocation tooth in §5.

Requires C# 7.2; Unity is on C# 9, and `Tools/core-tests` compiles the same Core files against a newer language
version, so the floor is Unity's.

**Why not a channel, an event, or an `ITelemetryProvider<T>`.** All three were considered and none survives the
observation that a provider already *has* the numbers in a field:
- A **channel/event** buys a `HasSubscribers` early-out, which protects a capture cost two of the three providers
  do not have — and its fan-out benefit is what a shared `ref readonly` gives for free. §1 has the full autopsy.
- A **consumer registry** adds a `Subscribe`/`Unsubscribe` pair to keep symmetric, and asymmetry there is a leak.
- An **`ITelemetryProvider<T>` interface** would buy nothing but a name: each provider exposes exactly one snapshot
  type, and an interface cannot declare a `ref readonly` returning member without pushing every implementer through
  explicit implementation. A plain `Telemetry` property per provider is the whole contract.

**What a new provider costs.** A field, a one-line accessor, and a refresh call at the end of its own pass — no
registration, no new interface, no wider signature for existing consumers to absorb. A worker-thread provider is
additive in the same way, but it must declare its own thread affinity (see §7); every provider today is
main-thread because each captures live managed state the frame is mutating.

**Direct access, not a registry.** With three providers, reading the owner keeps everything compile-time typed and
has no lifetime bookkeeping. A registry earns its keep only when providers become *dynamic* — per-source fetch
pipelines, or worker-thread producers — and it reintroduces the erasure hazard in §6: anything that stores
providers generically must keep the snapshot type a type parameter end to end, or the copy and the box come back.

### Consumers

| consumer | assembly | when it runs | what it is for |
|---|---|---|---|
| `ProfilerCounterTelemetry` | `MapRenderer.Unity` | whole file gated on `ENABLE_PROFILER`; owned by `MapViewComponent`; `Mirror()` **early-outs unless `Profiler.enabled`** | `ProfilerCounterValue<T>` per level, under a `MapRenderer` profiler category — charts over time, **works in a Development standalone build**, stripped from release |
| `MapTelemetryPanel` | `MapRenderer.Unity` | **opt-in**: `Pull()` from `Update`, and a disabled component gets no `Update` | the quick Inspector read, for when opening the Profiler and hunting a frame is more hassle than the question deserves |

The panel keeps earning its place — it answers "what is this number right now" without a profiling session. It
simply stops being mandatory: **disabled by default in both demo scenes** (`MapDemo`, `OpenStreetMapLiberty`).
Disabled costs nothing for a structural reason rather than a bookkeeping one: Unity does not call `Update` on a
disabled component, so there is no subscription state that could get it wrong.

The counter consumer is the one that unblocks §1.2 — a Development build profile is the only measurement the
Editor cannot contaminate, and counters are readable there. It is **owned by `MapViewComponent`**, not dropped
into a scene: a consumer that only exists when someone remembered to add a component is a consumer that is
missing exactly when a build misbehaves. Wiring it permanently is affordable because `Mirror()` reads one static
bool and returns before touching a provider.

**Read order matters for a puller.** `MapViewComponent.LateUpdate` runs `View.LateUpdate()` **first** and mirrors
the counters after, because a consumer that pulls has to run once every provider has refreshed. The push version
had to do the opposite (sync subscriptions *before* the frame so the publish saw the right subscriber set) — the
inversion is easy to get wrong and silently mirrors last frame's numbers.

**Nothing has to notice that the view was replaced.** `Bootstrapper.Wire` → `SetCamera` builds the view after a
panel's `OnEnable` and replaces it wholesale later. Under the push model both consumers re-evaluated their
attachment every frame to survive that. A pull reads `Map.View` at the moment it reads, so the problem does not
exist; `MapViewComponent` only drops its counter-consumer reference so a torn-down view is never mirrored.

## 4. What does NOT change

`TileTelemetrySnapshot` and its derived-math tests: `TileManager` already owned and captured it, so the tile side
needed only a field and an accessor. The three snapshot carriers are byte-for-byte unchanged — engine-free,
`init`-only. Core now holds *only* those carriers: no channel, no delegate, no logic.

What DID change beyond routing: `SymbolTelemetrySnapshot` split by owner into `SymbolStoreTelemetrySnapshot` +
`LabelPlacementTelemetrySnapshot` (§3), and `MapView.CaptureTelemetry` / `CaptureSymbolTelemetry` are gone —
the view no longer captures anything. Every field survived the split unchanged; only their owner is now
explicit.

## 5. Migration — what landed

1. Each provider owns a snapshot field and exposes `internal ref readonly … Telemetry`, refreshed at the end of
   its own pass (§3's table). `MapView` exposes the three OWNERS (`TileManager`, `Labels`, `Symbols`) and no
   per-snapshot forwarders, and produces nothing itself. ✔
2. `ProfilerCounterTelemetry` — one `ProfilerCounterValue<T>` per varying level, names in a `CounterNames`
   SSOT block mirroring `MapView.ProfilerMarkerNames`. The three configured limits (`PreparedCacheEnabled`,
   `PreparedCacheMaxCount`, `PreparedCacheByteBudget`) are deliberately **not** charted — a flat line telling
   you what you set. A custom Profiler *module* grouping them is still open (see §8). ✔
3. `MapTelemetryPanel` is a consumer: `Update` → `Pull()`, reading each provider by reference; the derived
   rate/ratio computations stay. ✔
4. Both demo scenes: panel present but **disabled**. ✔
5. `SymbolTelemetrySnapshot` split by owner into `SymbolStoreTelemetrySnapshot` +
   `LabelPlacementTelemetrySnapshot`; the panel's Inspector block is split under two headers naming the
   owning provider, so the readout mirrors the ownership. ✔
6. Tests. `MapTelemetryPanel_NeverPulled_IsNeverWritten_AndPullingFillsIt` is §2's principle as teeth — and it is
   *stronger* than the subscription version it replaces, which could only assert on `HasSubscribers` and admitted
   it could not distinguish "did not capture" from "captured and told nobody"; here the panel's own fields are the
   evidence. `PullTelemetry_IntoAPanel_IsAllocationFree_AcrossNFrames` is §6's copy/boxing tooth.
   `Tick_RefreshesItsOwnTelemetryStruct_HandedOutByReference` (placement) and
   `CurrentBatch_RefreshesTheStoreTelemetry_HandedOutByReference` (store) pin the ref-return itself: each binds
   `ref readonly` BEFORE the pass and asserts the refresh is visible through it, so a by-value accessor fails them.
   The `CaptureTelemetry_*` derived-math assertions were untouched — they go through `MapViewTestExtensions`, which
   a routing change does not disturb. ✔
7. **Coverage deliberately given up:** `TileTelemetry_PublishesOnACleanTick` counted publish callbacks to prove the
   tile provider refreshes on the clean-cover early-return path. A pull has no callback to count, and no captured
   value can be perturbed from a test without adding production surface the no-test-only-members rule forbids. It is
   now `TileTelemetry_SurvivesACleanTick_WithoutBlankingTheLevels`, which still fails if a clean tick zeroes the
   readout; "the refresh ran at all" is guaranteed structurally instead (one line in `Tick`, outside `TickCore`).
   Also gone: `TelemetryChannelTests` (4 cases), with the channel it tested — the fast core-tests project drops from
   1080 to 1076 and Core keeps only the three data carriers. ✔

## 6. Risks

- **Scene reference.** Removing or restructuring the panel component without editing the `.unity` scene leaves a
  missing-script warning. The scene edit is part of the change, not a follow-up. *(Handled: the panel type is
  unchanged, so both scenes needed only the `m_Enabled` flag.)*
- **Coverage loss during the move.** `MapTelemetryTests` currently drives `panel.Tick()`; repoint those
  assertions, do not delete them. The derived math is the part with real teeth. *(One loss was unavoidable and is
  recorded explicitly as §5 item 7 rather than left to be discovered.)*
- **Counter stripping.** Verify the counters genuinely compile out without `ENABLE_PROFILER`, and that the
  consumer registration itself is gated too — a registered no-op consumer defeats §2's early-out. *(The class,
  the field on `MapViewComponent`, and all three call sites are inside `#if ENABLE_PROFILER`; the `Profiler
  .enabled` gate covers the Editor/Development case where it IS compiled.)* **Not yet verified in an actual
  release build** — see §8.
- **`ProfilerCounterValue<T>` lives in the `Unity.Profiling.Core` package assembly**, not `UnityEngine.CoreModule`
  — an assembly without that `.asmdef` reference gets `CS0246`, not a missing-`using` hint. This is also the
  structural reason the counters must stay a *consumer* in `MapRenderer.Unity`: making them the telemetry SSOT
  would drag a UPM package reference into engine-free `Core` and break `Tools/core-tests`, which compiles the real
  Core sources under plain `dotnet test`.
- **No MapView-level test can reach the label providers.** `MapViewTestExtensions.LoadTestStyle` builds the
  render layers and the tile sources but never applies the style to the symbol subsystem, so
  `Symbols.HasSymbolLayers` is false in EditMode and `MapView.LateUpdate` skips the whole label block —
  `CurrentBatch` and `Labels.Tick` are unreachable through the view. Found by writing a three-provider test
  through `MapView` and watching the store assertion fail against correct code. The label providers are
  therefore tested where they ARE driven (`SymbolLabelSubsystemPumpTests`, `LabelFadeTests`); anything that
  genuinely needs labels end-to-end through the view has to fix the harness first.
- **Namespace collisions.** A namespace segment equal to a bare `UnityEngine` type is `CS0118`; pluralize
  (`Telemetry` is safe today — re-check before adding types).
- **A copy sneaking back in.** `ref readonly` is load-bearing and easy to lose silently. Three ways: declaring the
  accessor as a plain returning property, binding the result to `var`/a by-value local instead of
  `ref readonly …`, or dropping `readonly` from a snapshot struct (member access through a reference to a
  non-readonly struct forces a *defensive copy per field read* — worse than by value). None of these fail to
  compile; §5's allocation tooth is what catches them.
- **A reader outliving its provider.** A `ref readonly` cannot be stored in a field (C# escape rules forbid it), so
  a stale reference cannot be held across frames — the language closes this one. What remains is holding the
  *owner*: `MapViewComponent` nulls its counter-consumer reference on teardown/re-injection so a torn-down view is
  never read.
- **A consumer reading before the provider refreshed.** A puller placed before `View.LateUpdate()` mirrors last
  frame's numbers, silently and plausibly. Ordering is the contract now (see §3's read-order note).

## 7. Resolved questions

Each was an open question in the sketch; the resolution is what the implementation does.

- **Refresh cadence — once per pass, set by the PROVIDER; sampling rate is the CONSUMER's business.** Each
  provider refreshes at the end of its own pass and holds no interval state. A consumer that wants less
  samples less: the panel already throttles its own derived rate over a 0.5 s window, and the profiler wants
  every frame. No cadence hint on the provider — it would be provider state serving one consumer's preference.
  The trade-off this buys and costs is in §3 ("one-instant coherence").
- **Derived rates live in the consumer.** §2 already says levels are the contract and rates are derived; the
  panel keeps its `RebuildRateWindowSeconds` window. A shared derivation step would put per-frame state back
  in the provider to serve a second consumer that does not want it (the profiler charts a level over time —
  the rate is the chart's slope, not a number it needs published).
- **Counter naming — grouped, dotted, mirroring the marker convention.** `MapRenderer.Tiles.*`,
  `MapRenderer.Cache.*` and `MapRenderer.Symbols.*` under one `MapRenderer` `ProfilerCategory`, matching `MapView.ProfilerMarkerNames`'
  existing `MapRenderer.View.LateUpdate` style so the Profiler's flat search groups them the same way. The
  names are an SSOT constant block on the consumer, as with the markers.
- **Multiple providers, no aggregation.** Providers are per *subsystem*, not per map: three today, each reached
  as an internal member of the one `MapView`. A consumer reads whichever owner it wants at the moment it reads, so
  a replaced view (`SetCamera` builds a fresh `MapView`) needs no re-binding. Aggregation ACROSS maps stays YAGNI —
  there is one `MapView`, and summing two maps' levels would have to define what the sum even means.
- **Thread affinity — per provider, not global.** Every provider today is main-thread: it updates its struct from
  live managed state the frame is mutating, so reading it off-thread would be a data race, and each provider's XML
  summary says so. This is a *per-provider* statement, not a property of the design: a worker-side producer (tile
  decode, the label reconcile) would be a new provider owning its own snapshot type, and it must declare its own
  affinity. Per-owner snapshots are what make that additive rather than a widening of everyone else's contract —
  and note a `ref readonly` to a struct a worker is writing is exactly the shape that needs a real answer (a
  double-buffer or a copy at the boundary), not a borrowed reference.
- **The panel keeps its rate window.** It is the one number the panel adds over a raw level, and §10.4 of
  `symbol-label-perf-design.md` is the episode that proves it earns its place (rebuilds/second is what said the
  memo was structurally dead). Raw-levels-only would push that question into a profiling session every time.

## 8. Open / deferred after this change

- **A custom Profiler module** grouping the `MapRenderer.*` counters into one chart panel. The counters exist
  and are searchable today; the module is presentation.
- **Build verification.** IL2CPP reachability of the generic instantiations, and that the counters genuinely
  strip from a release player, can only be confirmed in an actual **build** — which is the same Development
  build this design exists to make measurable, and is the maintainer's step.
- ~~**The measurement this unblocks.**~~ **CLOSED, and not by this change.** §1.3's "+5 ms when the camera
  moves" delta no longer exists: the symbol-label perf epic removed it (still-vs-moving is ~identical on
  `perf/symbol-labels`), which is also what proved it was real work rather than Editor repaint. Nothing to
  re-take. The counter consumer's justification is §1.2 alone — there is still no way to read these levels in
  a Development standalone build — and that is enough on its own.

## 9. Related follow-up (not this doc's scope)

**Done (2026-07-27).** `docs/lessons-learned.md`'s "Rendering loop & camera" entry carried the *retracted*
reading — "a moving-camera delta may be Editor repaint rather than your frame" — which §1.3 corrected. It has
been rewritten to the inverse lesson (a cost with no marker looks exactly like an environment artifact; mark
the span before blaming the environment), keeping the double-`PlayerLoop` observation as a **capture caveat**
rather than an explanation, and keeping the "telemetry that inflates what it measures" point that justifies the
panel being off by default.
