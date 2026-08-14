// Shared between EditMode and PlayMode (pump-split: ReadyTailCount is read by PlayMode's off-main symbol
// teeth too — see MapRenderer.Tests.PlayMode.Text.SymbolTailPumpTests / SymbolLabelSubsystemPumpTests).
// NOT registered in core-tests.csproj (SymbolLabelSubsystem is engine-side).
//
// The Stage 4b test seams that used to sit on SymbolLabelSubsystem. Three of them carried a `*ForTest`
// suffix, which the conventions call out directly: the suffix is the class admitting the member does not
// belong to it. Moving them here lets the names say what they mean — the LOCATION now carries what the
// suffix was carrying, so `ReconcilerForTest` is just `Reconciler`.
//
// They read _reconciler / _reconcileInFlight / _store / _readyTails / _handoffQueue, broadened
// private -> internal, which IS the sanctioned footprint.
//
// NOT moved, deliberately: ReconcileFaultObserved, TailsStartedLastPump, AtlasUploadsLastPump,
// CancelledBuildCount, CollectRecomputeCount. The subsystem WRITES all of those — they are state it
// produces, not queries over it, so an extension method cannot own them. Same split as the three render
// backends.

using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests
{
    internal static class SymbolLabelSubsystemTestExtensions
    {
        /// <summary>
        /// Total not-yet-tailed builds — queued in the pool→main handoff (not yet drained) PLUS drained but
        /// not-yet-started tails. The F-7 budget tooth asserts against this total.
        /// </summary>
        internal static int ReadyTailCount(this SymbolLabelSubsystem subsystem)
            => subsystem._readyTails.Count + subsystem._handoffQueue.Count;

        /// <summary>
        /// The off-main reconcile worker, so a test can gate it in flight
        /// (<see cref="SymbolLabelReconciler.GateForTest"/>), assert it ran off the main thread
        /// (<see cref="SymbolLabelReconciler.LastRunThreadId"/>), or inject a fault
        /// (<see cref="SymbolLabelReconciler.FaultNextRun"/>).
        /// </summary>
        internal static SymbolLabelReconciler Reconciler(this SymbolLabelSubsystem subsystem)
            => subsystem._reconciler;

        /// <summary>Whether an off-main reconcile is currently in flight — proves one-in-flight coalescing
        /// plus the drain.</summary>
        internal static bool ReconcileInFlight(this SymbolLabelSubsystem subsystem)
            => subsystem._reconcileInFlight;

        /// <summary>The label store, so a production-path parity test can run its inline <c>CollectInto</c>
        /// as an oracle against the async front-buffer result.</summary>
        internal static SymbolTileLabelStore Store(this SymbolLabelSubsystem subsystem)
            => subsystem._store;

        /// <summary>D6 (road-shields): builds parked awaiting the sprite fetch to settle — proves the
        /// deadline fallback actually DRAINS the queue once tripped, rather than merely letting one build
        /// commit while the backlog keeps growing.</summary>
        internal static int PendingSpriteCount(this SymbolLabelSubsystem subsystem)
            => subsystem._pendingSpriteQueue.Count;
    }
}
