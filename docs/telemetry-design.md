# Telemetry — provider / consumer split

How the map's instantaneous levels (tile cover, prepared cache, symbol store, symbol placement) reach a reader:
who owns them, how a reader reaches them, and what they cost when nobody looks. Companions:
`docs/symbol-label-perf-design.md` (the profiling work these levels serve) and `docs/frame-timeline.md` (the
marker taxonomy telemetry sits beside). Proprietary / all rights reserved.

---

## 1. Why

A telemetry surface has three ways to fail:

1. **It costs what its consumer costs.** An Inspector full of live public fields forces an Editor repaint every
   frame. That is real CPU, paid whether or not anyone is looking — so the Inspector panel is **off by
   default**. A pulled, disabled panel costs nothing.
2. **An Editor-only surface cannot give a perf verdict.** A Development standalone build is the only
   measurement the Editor cannot contaminate, and an Inspector panel cannot be read there. **This is the
   problem the profiler-counter consumer solves.**
3. **It can inflate or disguise what it measures.** A telemetry surface that inflates the thing it measures
   is worse than no telemetry. And an unmarked hot spot looks exactly like an Editor artifact, so mark the span
   before blaming the environment (`docs/lessons-learned.md` § "Rendering loop & camera"). An Editor capture
   also shows two `PlayerLoop`s and two render loops per frame; that is a capture caveat, not an explanation.

**Why not a publish channel.** A `TelemetryChannel<T>` between providers and consumers — a generic event with
a `HasSubscribers` early-out — is rejected:

- Its benefit is "zero cost when nobody subscribes", and plain pull plus a disabled component gives that more
  cheaply. That leaves **single-capture fan-out** as its only justification.
- Fan-out does not need a channel. A provider that *owns* its snapshot as a field and hands out a
  `ref readonly` reference serves N readers from one refresh, with no subscriber registry, no delegate, no event.
- The capture cost a channel would protect barely exists. Two of the three providers store every level as they
  run — their refresh is a repackage, not a capture. Only `TileManager` derives anything, and both derivations
  are bounded by the cover plus its pad ring over reused scratch.

The snapshots are engine-free, `init`-only carriers with real tests behind their derived math. This design
fixes who owns them, how a reader reaches them, and what it costs when nobody looks.

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
- **The carriers stay engine-free.** The snapshot carriers are plain `init`-only structs with no `UnityEngine`
  reference, so `Tools/core-tests` compiles them. Anything that touches `Unity.Profiling` stays in the consumer.
- **No test-only production surface** (`docs/conventions-short.md`). A counter exposed solely so a test can read
  it does not belong on a production type.

## 3. Design

**Every type that has telemetry to report is its own provider.** It owns the numbers as a struct field, updates
them as its own pass runs, and hands them out by reference; nothing assembles a snapshot out of another type's
counters. That is the whole rule, and it is what decides everything below.

```csharp
private SymbolPlacementTelemetrySnapshot _telemetry;

internal ref readonly SymbolPlacementTelemetrySnapshot Telemetry => ref _telemetry;
```

`MapView` is **not** a provider — it produces no telemetry of its own, and it does not re-export any either. A
consumer reaches the owner and reads its `Telemetry`: `view.TileManager.Telemetry`, `view.SymbolPlacementSystem.Telemetry`,
`view.SymbolSubsystem.Telemetry`. All three owners are internal members of the view, so there is nothing to forward.

**Why no forwarding properties on the view.** A `ref readonly` property per snapshot type on `MapView` would be
pure redundancy, since the owners are already internal members. Worse, it creates the obvious place for someone
to compose one view-level snapshot out of three providers' counters — the shape this design exists to remove.

| provider | snapshot | refreshes at the end of |
|---|---|---|
| `TileManager` | `TileTelemetrySnapshot` | `Tick` — via a `TickCore` shell, so the clean-cover early return still refreshes |
| `SymbolSubsystem` | `SymbolStoreTelemetrySnapshot` | `CurrentBatch`, where the last of its levels is decided |
| `SymbolPlacementSystem` | `SymbolPlacementTelemetrySnapshot` | `Tick` |

A reader takes a reference whenever it wants one. Nothing registers, nothing unregisters.

**Two of the three refreshes are free.** `SymbolPlacementSystem` and `SymbolSubsystem` already store every
level their pass produces (`LastQuadCount`, `ActiveTileCount`, …), so the refresh just repackages fields. Only
`TileManager` derives anything — `TileCoverStats.Compute` plus a walk of `_loaded` — and it does so once per `Tick`
unconditionally, because both are bounded by the cover plus its pad ring over reused scratch arrays. That is the
whole reason no staleness flag or demand counter exists anywhere in this design.

**Why the symbol snapshot is two.** Store state (active/cached tiles, the coverage drop) and placement results
(candidates, survivors, quads, fades, mirror rebuilds) have two owners. One carrier for both needs something in
the middle to assemble it, so the snapshot is split by owner (`SymbolStoreTelemetrySnapshot`,
`SymbolPlacementTelemetrySnapshot`). That is what makes the rule above true rather than aspirational.

**What this costs: one-instant coherence.** Refreshing at the end of each provider's own pass means the levels
are *that pass's* — fresher than a shared end-of-frame capture — but two providers' numbers do not come from the
same instant. A value derived across providers can be a phase off. That is accepted: for observability
freshness beats simultaneity, and one shared capture point is what forces a middleman. Anything that genuinely
needs two providers' numbers as of one instant needs a snapshot type of its own, owned by whoever owns that
instant.

**A provider that did not run holds its last values.** A frame with no symbol layers never reaches `CurrentBatch`,
so the store does not refresh — the struct keeps what it last really measured rather than zeroing itself. "The pass
did not run" is a different claim from "it ran and found zero", and a provider should not invent the second. The
counters omit `ResetToZeroOnFlush` so they hold the same way, and no consumer needs a staleness stamp.

**`ref readonly`, not a returned copy.** The accessor hands back a reference to the provider's own field:

```csharp
internal ref readonly TileTelemetrySnapshot Telemetry => ref _telemetry;
```

The snapshots are `readonly struct`s well over 16 bytes, so `ref readonly` is the same convention as `in` on a
parameter — and being `readonly` is what stops member access through the reference forcing a defensive copy per
field read. Two things silently undo it: returning by value, and erasing the type to `object` or a non-generic
interface anywhere on the path. An allocation test that pulls into a panel across N frames covers both
(see "Risks").

Requires C# 7.2; Unity is on C# 9, and `Tools/core-tests` compiles the same carrier files against a newer language
version, so the floor is Unity's.

**Why not a channel, an event, or an `ITelemetryProvider<T>`.** None survives the observation that a provider
already *has* the numbers in a field:
- A **channel/event** buys a `HasSubscribers` early-out, which protects a capture cost two of the three providers
  do not have — and its fan-out benefit is what a shared `ref readonly` gives for free ("Why" has the reasoning).
- A **consumer registry** adds a `Subscribe`/`Unsubscribe` pair to keep symmetric, and asymmetry there is a leak.
- An **`ITelemetryProvider<T>` interface** would buy nothing but a name: each provider exposes one snapshot
  type, and an interface cannot declare a `ref readonly` returning member without pushing every implementer through
  explicit implementation. A plain `Telemetry` property per provider is the whole contract.

**What a new provider costs.** A field, a one-line accessor, and a refresh call at the end of its own pass — no
registration, no new interface, no wider signature for existing consumers to absorb. A worker-thread provider is
additive in the same way, but it must declare its own thread affinity (see "Decisions"); every provider today
is main-thread because each captures live managed state the frame is mutating.

**Direct access, not a registry.** With three providers, reading the owner keeps everything compile-time typed and
has no lifetime bookkeeping. A registry earns its keep only when providers become *dynamic* — per-source fetch
pipelines, or worker-thread producers — and it reintroduces the erasure hazard in "Risks": anything that stores
providers generically must keep the snapshot type a type parameter end to end, or the copy and the box come back.

### Consumers

| consumer | assembly | when it runs | what it is for |
|---|---|---|---|
| `ProfilerCounterTelemetry` | `MapRenderer.Unity` | whole file gated on `ENABLE_PROFILER`; wired into `MapViewComponent`; `Mirror()` **early-outs unless `Profiler.enabled`** | `ProfilerCounterValue<T>` per level, under a `MapRenderer` profiler category — charts over time, **works in a Development standalone build**, stripped from release |
| `MapTelemetryPanel` | `MapRenderer.App` | **opt-in**: `Pull()` from `Update`, and a disabled component gets no `Update` | the quick Inspector read, for when opening the Profiler and hunting a frame is more hassle than the question deserves |

The panel earns its place — it answers "what is this number right now" without a profiling session — but it is
not mandatory: **disabled by default in both demo scenes** (`MapDemo`, `OpenStreetMapLiberty`). Disabled costs
nothing for a structural reason rather than a bookkeeping one: Unity does not call `Update` on a disabled
component, so there is no subscription state that could get it wrong.

The counter consumer is what makes the levels readable in a Development build (problem 2 in "Why"). It is
**wired into `MapViewComponent`**, not dropped into a scene: a consumer that only exists when someone remembered
to add a component is missing when a build misbehaves. `MapViewComponent` declares partial methods
whose implementing half (`MapViewComponent.ProfilerCounters.cs`) is entirely inside `#if ENABLE_PROFILER`, so a
release player compiles the calls away. Wiring it permanently is affordable because `Mirror()` reads one static
bool and returns before touching a provider.

**Read order matters for a puller.** `MapViewComponent.LateUpdate` runs `View.LateUpdate()` **first** and mirrors
the counters after, because a consumer that pulls has to run once every provider has refreshed. The opposite
order silently mirrors last frame's numbers.

**Nothing has to notice that the view was replaced.** `MapHost` → `SetCamera` builds the view after a panel's
`OnEnable` and can replace it wholesale later. A pull reads `Map.View` at the moment it reads, so no
re-attachment is needed; `MapViewComponent` only drops its counter-consumer reference so a torn-down view is
never mirrored.

## 4. Snapshot carriers

The three snapshot carriers — `TileTelemetrySnapshot`, `SymbolStoreTelemetrySnapshot`,
`SymbolPlacementTelemetrySnapshot` — are engine-free, `init`-only `readonly struct`s in `MapRenderer.Unity/View`,
compiled by both runners and by `Tools/core-tests`. They are the only telemetry state: no channel, no delegate,
no logic. `MapView` captures nothing. The panel's Inspector block is
split under two symbol headers naming the owning provider, so the readout mirrors the ownership.

`ProfilerCounterTelemetry` mirrors one `ProfilerCounterValue<T>` per varying level, with names in a
`CounterNames` SSOT block. The three configured limits (`PreparedCacheEnabled`, `PreparedCacheMaxCount`,
`PreparedCacheByteBudget`) are **not** charted — a flat line telling you what you set; the panel shows them.

## 5. What no test observes

**That the tile provider refreshes on a clean tick.** `TileManager.Tick` is a thin shell over `TickCore`, and the
refresh is one line in `Tick`, outside `TickCore`, so the clean-cover early return still refreshes. A pull has no
callback to count, and no captured value can be perturbed from a test without production surface the
no-test-only-members rule forbids. The guarantee is therefore structural. A test still fails if a clean tick
zeroes the readout; it cannot tell "refreshed" from "left alone".

## 6. Risks

- **Scene reference.** Removing or restructuring the panel component without editing the `.unity` scenes leaves a
  missing-script warning. The scene edit is part of any such change.
- **Counter stripping.** The counters must compile out without `ENABLE_PROFILER`, and the consumer wiring must be
  gated too — a registered no-op consumer defeats the early-out. The class, `MapViewComponent`'s counter field, and
  the partial-method implementations are all inside `#if ENABLE_PROFILER`; the `Profiler.enabled` gate covers the
  Editor/Development case where they are compiled. **Not yet verified in an actual release build** — see "Open".
- **IL2CPP reachability.** The counters are reached only through generic instantiations over
  `ProfilerCounterValue<int/long/double>`, which must be statically reachable under IL2CPP. Only a Development
  IL2CPP build that produces working counters proves it (`Assets/Editor/BuildScript.cs` builds one).
- **`ProfilerCounterValue<T>` lives in the `Unity.Profiling.Core` package assembly**, not `UnityEngine.CoreModule`
  — an assembly without that `.asmdef` reference gets `CS0246`, not a missing-`using` hint. This is also the
  structural reason the counters must stay a *consumer*: making them the telemetry SSOT would put a UPM package
  reference into the engine-free carriers and break `Tools/core-tests`, which compiles them under plain
  `dotnet test`.
- **No MapView-level EditMode test can reach the label providers.** `MapViewTestExtensions.LoadTestStyle` builds
  the render layers and the tile sources but never applies the style to the symbol subsystem, so
  `SymbolSubsystem.HasSymbolLayers` is false in EditMode and `MapView.LateUpdate` skips the whole label block —
  `CurrentBatch` and `SymbolPlacementSystem.Tick` are unreachable through the view. The label providers are
  therefore tested where they ARE driven; anything that genuinely needs labels end-to-end through the view has to
  fix the harness first.
- **Namespace collisions.** A namespace segment equal to a bare `UnityEngine` type is `CS0118`; pluralize
  (`Telemetry` is safe today — re-check before adding types).
- **A copy sneaking back in.** `ref readonly` is load-bearing and easy to lose silently. Three ways: declaring the
  accessor as a plain returning property, binding the result to `var`/a by-value local instead of
  `ref readonly …`, or dropping `readonly` from a snapshot struct (member access through a reference to a
  non-readonly struct forces a *defensive copy per field read* — worse than by value). None of these fail to
  compile; an allocation test that pulls into a panel across N frames catches them, and tests that bind
  `ref readonly` BEFORE a provider's pass and read the refresh through it catch a by-value accessor.
- **A reader outliving its provider.** A `ref readonly` cannot be stored in a field (C# escape rules forbid it), so
  a stale reference cannot be held across frames — the language closes this one. What remains is holding the
  *owner*: `MapViewComponent` nulls its counter-consumer reference on teardown/re-injection so a torn-down view is
  never read.
- **A consumer reading before the provider refreshed.** A puller placed before `View.LateUpdate()` mirrors last
  frame's numbers, silently and plausibly. Ordering is the contract (see the read-order note in "Design").

## 7. Decisions

- **Refresh cadence — once per pass, set by the PROVIDER; sampling rate is the CONSUMER's business.** Each
  provider refreshes at the end of its own pass and holds no interval state. A consumer that wants less
  samples less: the panel throttles its own derived rate over a 0.5 s window, and the profiler wants every
  frame. No cadence hint on the provider — it would be provider state serving one consumer's preference.
  The trade-off this buys and costs is "one-instant coherence" in "Design".
- **Derived rates live in the consumer.** Levels are the contract and rates are derived ("Principles"); the
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
  affinity. Per-owner snapshots are what make that additive rather than a widening of everyone else's contract.
  A `ref readonly` to a struct a worker is writing needs a real answer (a double-buffer or a copy at the
  boundary), not a borrowed reference.
- **The panel keeps its rate window.** It is the one number the panel adds over a raw level, and rebuilds per
  second is the number that shows the gather memo is structurally dead under motion
  (`docs/symbol-label-perf-design.md` § "The memo is structurally dead under continuous motion"). Raw levels
  only would push that question into a profiling session every time.

## 8. Open

- **A custom Profiler module** grouping the `MapRenderer.*` counters into one chart panel. The counters exist
  and are searchable; the module is presentation.
- **Build verification.** IL2CPP reachability of the generic instantiations, and that the counters strip from a
  release player, can only be confirmed in an actual **build** — the maintainer's step.
