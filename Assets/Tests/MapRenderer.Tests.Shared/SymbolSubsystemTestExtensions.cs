// Shared between EditMode and PlayMode (pump-split: ReadyTailCount is read by PlayMode's off-main symbol
// teeth too — see MapRenderer.Tests.PlayMode.Text.SymbolTailPumpTests / SymbolSubsystemPumpTests). NOT
// registered in core-tests.csproj (SymbolSubsystem is engine-side).
//
// Non-obvious why: these are the SymbolSubsystem test seams, reading _reconciler / _reconcileInFlight /
// _store / _readyTails / _handoffQueue, broadened private -> internal, the sanctioned footprint — the
// LOCATION carries what a `*ForTest` suffix would, so `Reconciler` needs no suffix. NOT here:
// ReconcileFaultObserved, TailsStartedLastPump, AtlasUploadsLastPump, CancelledBuildCount,
// CollectRecomputeCount — the subsystem WRITES those (state it produces, not queries over it), so an
// extension method cannot own them, the same split as the three render backends.

using System.Collections.Generic;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests
{
    internal static class SymbolSubsystemTestExtensions
    {
        // The store materializes no managed symbol list. A caller that polls "has anything committed yet" uses
        // CollectedWinnerCount; one needing a tile's exact baked columns reads SymbolTileStore.DebugBlockFor directly.

        /// <summary>Winner count from the store's plan-aware CollectInto shim — a build-completion poll
        /// ("has anything committed yet").</summary>
        internal static int CollectedWinnerCount(this SymbolSubsystem subsystem)
        {
            var blockId = new List<int>(); var localIndex = new List<int>(); var isDeparting = new List<byte>();
            subsystem.Store().CollectInto(blockId, localIndex, isDeparting, SymbolSubsystem.DedupEnabled, out _);
            return blockId.Count;
        }

        /// <summary>
        /// Total not-yet-tailed builds — queued in the pool→main handoff (not yet drained) PLUS drained but
        /// not-yet-started tails. The pump-budget tooth asserts against this total.
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

        /// <summary>Road-shield builds parked awaiting the sprite fetch to settle — proves the
        /// deadline fallback actually DRAINS the queue once tripped, rather than merely letting one build
        /// commit while the backlog keeps growing.</summary>
        internal static int PendingSpriteCount(this SymbolSubsystem subsystem)
            => subsystem._pendingSpriteQueue.Count;

        /// <summary>The shared symbol build pipeline, so a test can read its telemetry (e.g.
        /// <c>SkippedSymbolCount</c>) to observe shaping progress mid-build — the production dispatch path
        /// (<c>TryBeginBuild</c>/<c>PumpBuilds</c>/<c>RunTailAsync</c>) exposes no other window onto its
        /// internal, per-build <see cref="SymbolTileBuffer"/>.</summary>
        internal static StyledSymbolTileBuilder Builder(this SymbolSubsystem subsystem)
            => subsystem._builder;
    }
}
