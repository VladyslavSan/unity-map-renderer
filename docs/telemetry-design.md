# Telemetry — provider / consumer split (design sketch)

**Status:** SKETCH, not started. Enough to start a stage from in a later session; the open questions in §7 are
deliberately unresolved. Proprietary / all rights reserved.

Companion: `docs/symbol-label-perf-design.md` §10.4 (the profiling episode that motivated this),
`docs/frame-timeline.md` (the marker taxonomy telemetry sits beside).

---

## 1. Why

Telemetry today is **pushed into one hard-wired consumer**: `MapView.CaptureTelemetry()` /
`CaptureSymbolTelemetry()` produce snapshots and `MapTelemetryPanel.Tick()` copies them into public
MonoBehaviour fields, every frame, unconditionally. Three problems:

1. **It costs what the consumer costs.** An Inspector full of live public fields forces an Editor repaint every
   frame. That is real CPU, and it is paid whether or not anyone is looking.
2. **It is Editor-only.** The panel cannot be read in a Development standalone build — which is exactly where
   perf verdicts have to come from (§3 below).
3. **It confounded a real measurement.** While profiling R1, the Editor showed **two `PlayerLoop`s and two render
   loops per captured frame**, and a "camera moving costs +5 ms" delta that did not appear in the main loop's
   marker tree at all. Editor-side repaint (which scales with mouse activity, i.e. with panning) is the leading
   explanation. A telemetry surface that inflates the thing it measures is worse than no telemetry.

The snapshots themselves are fine — engine-free, pull-based, `init`-only carriers with real tests behind their
derived math. **This design changes who consumes them and what it costs when nobody does.**

## 2. Principles

- **Zero cost when nobody subscribes.** With no consumer registered, the frame pays one branch — not a capture,
  not a copy. Telemetry must never be a thing you have to remember to turn off.
- **Pull stays pull.** The existing snapshot contract (`SymbolTelemetrySnapshot`: *"every field is an
  instantaneous LEVEL at the instant of capture, never a rate"*) is correct and stays. Rates and ratios are
  **derived by consumers**, as `MapTelemetryPanel` already does for the prepared-cache hit rate.
- **Core stays engine-free.** Interfaces and carriers in `MapRenderer.Core`; anything touching `UnityEngine` or
  `Unity.Profiling` lives in `MapRenderer.Unity`.
- **No test-only production surface** (`docs/conventions-short.md`). A counter exposed solely so a test can read
  it does not belong on a production type.

## 3. Design

`MapView` becomes the telemetry **publisher**; consumers subscribe to the snapshot type they care about and
unsubscribe when they stop caring.

**A generic channel per snapshot type, published as a C# event.**

```csharp
// MapRenderer.Core/View — engine-free, next to the snapshots it carries
public delegate void TelemetryHandler<T>(in T snapshot) where T : struct;

public sealed class TelemetryChannel<T> where T : struct
{
    public event TelemetryHandler<T> Published;
    public bool HasSubscribers => Published != null;
    public void Publish(in T snapshot) => Published?.Invoke(in snapshot);
}
```

`in T` per the convention — the snapshots are `readonly struct`s well over 16 bytes. It stays unboxed **as long as
`T` is never erased** to `object` or a non-generic interface; that is the one rule to hold when wiring consumers.

`MapView` owns one channel per snapshot type, and the frame reads:

```csharp
if (_tileChannel.HasSubscribers)   _tileChannel.Publish(CaptureTelemetry());
if (_symbolChannel.HasSubscribers) _symbolChannel.Publish(CaptureSymbolTelemetry());
```

**Why generic + per-type channels.** A single `Consume(in tiles, in symbols)` would force the provider to capture
BOTH snapshots when anyone subscribes to EITHER. Per-type channels make each capture independently conditional, so
§2's principle sharpens to *"you pay only for what someone is actually reading."* New snapshot types (a
worker-side one, say) become additive — a new channel, no new interfaces, no wider `Consume` signature to break
every consumer.

**Why an event rather than a consumer-interface registry.** With the channel object in place, `Published != null`
IS the has-subscribers check, so the early-out falls out for free and there is no list plus `Subscribe`/
`Unsubscribe` pair to keep symmetric. The interface's discoverability argument does not earn its keep against a
three-line type. What is given up, both minor and both accepted here:
- **Exception isolation** — one throwing subscriber abandons the rest of the multicast (a list could `try`/`catch`
  per consumer). For telemetry a throwing consumer should be loud, not silently skipped.
- **Lambda-leak discipline** — `Published += x => …` cannot be unsubscribed without keeping the delegate. Method
  groups (`Published -= OnTelemetry`) unsubscribe correctly, which is what a MonoBehaviour's `OnEnable`/
  `OnDisable` uses anyway. A documentation point, not a design flaw.

**Composition, not multiple interface implementations.** `MapView` implementing `ITelemetryProvider<TileSnapshot>`
AND `ITelemetryProvider<SymbolSnapshot>` is legal, but two `event` members of the same generic interface force
explicit interface implementation with hand-written `add`/`remove`. Owning two channels avoids that entirely.

### Consumers

| consumer | assembly | when it runs | what it is for |
|---|---|---|---|
| `ProfilerCounterTelemetry` | `MapRenderer.Unity` | gated on `ENABLE_PROFILER` | `ProfilerCounterValue<T>` per field, under a `MapRenderer` profiler category — charts over time, **works in a Development standalone build**, ~free when the profiler is off, stripped from release |
| `MapTelemetryPanel` | `MapRenderer.Unity` | **opt-in**, subscribes in `OnEnable` / unsubscribes in `OnDisable` | the quick Inspector read, for when opening the Profiler and hunting a frame is more hassle than the question deserves |

The panel keeps earning its place — it answers "what is this number right now" without a profiling session. It
simply stops being mandatory: **disabled in the demo scene by default**, and disabled means genuinely
unsubscribed, not "runs but writes nowhere."

The counter consumer is the one that unblocks §1.3 — a Development build profile is the only measurement the
Editor cannot contaminate, and counters are readable there.

## 4. What does NOT change

`TileTelemetrySnapshot`, `SymbolTelemetrySnapshot`, `MapView.CaptureTelemetry` / `CaptureSymbolTelemetry`, and the
derived-math tests. The snapshots are the stable contract this design routes differently.

## 5. Migration sketch

1. `TelemetryHandler<T>` + `TelemetryChannel<T>` in `Core/View`; `MapView` owns one channel per snapshot type,
   each published behind its own `HasSubscribers` check.
2. `ProfilerCounterTelemetry` consumer + a custom Profiler module grouping the counters.
3. `MapTelemetryPanel` becomes a consumer: delete its `Update`→`Tick` capture, subscribe/unsubscribe on
   enable/disable, keep the derived rate/ratio computations.
4. Demo scene: panel component present but **disabled**.
5. `MapTelemetryTests`: repoint the derived-math assertions (cache hit rate, fill %, the label mirror rebuild
   rate) from panel fields onto the snapshot/derived layer, so the coverage outlives the panel being optional.
   Add a test that **no subscribers ⇒ no capture** — that is the principle in §2 and it is easy to regress.

## 6. Risks

- **Scene reference.** Removing or restructuring the panel component without editing the `.unity` scene leaves a
  missing-script warning. The scene edit is part of the change, not a follow-up.
- **Coverage loss during the move.** `MapTelemetryTests` currently drives `panel.Tick()`; repoint those
  assertions, do not delete them. The derived math is the part with real teeth.
- **Counter stripping.** Verify the counters genuinely compile out without `ENABLE_PROFILER`, and that the
  consumer registration itself is gated too — a registered no-op consumer defeats §2's early-out.
- **Namespace collisions.** A namespace segment equal to a bare `UnityEngine` type is `CS0118`; pluralize
  (`Telemetry` is safe today — re-check before adding types).
- **Boxing via erasure.** `in T` stays unboxed only while `T` is a type parameter end to end. Storing a consumer
  or a snapshot as `object` / a non-generic interface anywhere in the path silently reintroduces a copy + box.
- **IL2CPP reachability.** Generic instantiations over value types need to be statically reachable. They are here
  (consumers register concrete types), but confirm in an actual Development **build**, not just the Editor — that
  is the same build this design exists to make measurable.

## 7. Open questions (resolve in the plan)

- **Capture cadence.** Every frame, or on an interval? Per-frame levels are what a profiler chart wants; the
  panel would happily read at 10 Hz. Possibly a per-consumer cadence hint rather than one global choice.
- **Where do derived rates live?** The label mirror rebuild rate is currently computed in the panel over a 0.5 s
  window. If two consumers both want it, it belongs in a shared derivation step — but that reintroduces state in
  the provider, which §2 wants to keep dumb.
- **Counter naming + module layout.** A flat `MapRenderer.*` namespace of counters, or grouped
  (`MapRenderer.Tiles.*`, `MapRenderer.Symbols.*`)? Decide before anything depends on the names.
- **Multiple maps / multiple providers.** Today there is one `MapView`. Does a consumer bind to one provider, or
  aggregate?
- **Thread affinity.** Capture is main-thread today. If a consumer ever wants worker-side counters (tile decode,
  the label reconcile), the contract needs to say so explicitly.
- **Does the panel keep the rate windows**, or should it display raw levels only and leave rates to the profiler
  charts? Simpler panel, but loses the thing that made the rebuild-rate readout useful.

## 8. Related follow-up (not this doc's scope)

`docs/lessons-learned.md` still owes an entry on the Editor profiling artifact from §1.3: an Editor Play-mode
capture can show a second `PlayerLoop`/render loop, and a moving-camera delta may be Editor repaint rather than
your frame — take perf verdicts from a Development standalone build.
