// Shared between EditMode and PlayMode (pump-split: ReadyTailCount is read by PlayMode's off-main symbol
// teeth too — see MapRenderer.Tests.PlayMode.Text.SymbolTailPumpTests / SymbolSubsystemPumpTests).
// NOT registered in core-tests.csproj (SymbolSubsystem is engine-side).
//
// The Stage 4b test seams that used to sit on SymbolSubsystem. Three of them carried a `*ForTest`
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

using System.Collections.Generic;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests
{
    internal static class SymbolSubsystemTestExtensions
    {
        // Reader cutover (symbols-async-reconcile stage 4.2) / resident-graph shed (4.4b): the retired
        // SymbolSubsystem.CollectInto convenience over a per-symbol managed carrier list (its store target —
        // the plain managed-list-producing CollectInto overloads — is gone too; the store no longer materializes a
        // managed symbol list at all). A caller that only polled "has anything committed yet" (build-completion
        // polling loops) uses CollectedWinnerCount; a caller that needed one tile's exact baked columns uses
        // SymbolTileStore.DebugBlockFor directly (Entry.SymbolPlacementSystem itself is gone as of 4.4b).

        /// <summary>Winner count from the store's plan-aware CollectInto shim — a build-completion poll
        /// ("has anything committed yet") that used to read the per-symbol managed carrier list's <c>Count</c>.</summary>
        internal static int CollectedWinnerCount(this SymbolSubsystem subsystem)
        {
            var blockId = new List<int>(); var localIndex = new List<int>(); var isDeparting = new List<byte>();
            subsystem.Store().CollectInto(blockId, localIndex, isDeparting, SymbolSubsystem.DedupEnabled, out _);
            return blockId.Count;
        }

        /// <summary>
        /// Total not-yet-tailed builds — queued in the pool→main handoff (not yet drained) PLUS drained but
        /// not-yet-started tails. The F-7 budget tooth asserts against this total.
        /// </summary>
        internal static int ReadyTailCount(this SymbolSubsystem subsystem)
            => subsystem._readyTails.Count + subsystem._handoffQueue.Count;

        /// <summary>
        /// The off-main reconcile worker, so a test can gate it in flight
        /// (<see cref="SymbolReconciler.GateForTest"/>), assert it ran off the main thread
        /// (<see cref="SymbolReconciler.LastRunThreadId"/>), or inject a fault
        /// (<see cref="SymbolReconciler.FaultNextRun"/>).
        /// </summary>
        internal static SymbolReconciler Reconciler(this SymbolSubsystem subsystem)
            => subsystem._reconciler;

        /// <summary>Whether an off-main reconcile is currently in flight — proves one-in-flight coalescing
        /// plus the drain.</summary>
        internal static bool ReconcileInFlight(this SymbolSubsystem subsystem)
            => subsystem._reconcileInFlight;

        /// <summary>The symbol store, so a production-path parity test can run its inline <c>CollectInto</c>
        /// as an oracle against the async front-buffer result.</summary>
        internal static SymbolTileStore Store(this SymbolSubsystem subsystem)
            => subsystem._store;

        /// <summary>D6 (road-shields): builds parked awaiting the sprite fetch to settle — proves the
        /// deadline fallback actually DRAINS the queue once tripped, rather than merely letting one build
        /// commit while the backlog keeps growing.</summary>
        internal static int PendingSpriteCount(this SymbolSubsystem subsystem)
            => subsystem._pendingSpriteQueue.Count;
    }
}
