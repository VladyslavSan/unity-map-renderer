using System.Threading;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The <see cref="LayerPhase.WorkerThenMain"/> capability — the sibling of
    /// <see cref="ITileMeshLayerProcessor"/>'s mesh-settlement capability — for a processor whose worker
    /// step (<see cref="ITileLayerProcessor.ProcessOnWorker"/>) is followed by exactly one main-thread
    /// completion step. Implemented by <see cref="TileSymbolLayerProcessor"/>. ARTIFACT-FREE — unlike
    /// <see cref="ITileMeshLayerProcessor"/>, whose <see cref="ITileMeshLayerProcessor.TryTakeGraphRequest"/>
    /// hands the caller a real artifact before its own settlement step runs, this contract's completion
    /// step returns nothing at any point: the tail's output (e.g. a symbol layer's shaped symbol records)
    /// is exposed and sunk by the implementor itself — sinks stay separate, only decode + dispatch unify.
    /// </summary>
    internal interface ITileWorkerThenMainLayerProcessor : ITileLayerProcessor
    {
        /// <summary>MAIN-THREAD, SYNCHRONOUS: runs after (and only after) this processor's own worker step
        /// for the same build — called AT MOST ONCE per build, after the worker pass that fed it has
        /// returned. If the worker step never ran (an earlier processor's fault aborted the pass, or this
        /// pass's own decode faulted), this must complete as a no-op emitting nothing. Non-local invariant:
        /// this step never suspends, because <c>StyledSymbolTileBuilder.Shape</c> does not. The CALLER
        /// (<c>SymbolSubsystem.RunTailAsync</c>'s collect → await ensure → ct-check sequencing) guarantees
        /// any glyph-atlas fetch/append the build needs has completed before this call.
        /// Cancellation via <paramref name="ct"/> leaves shared state (glyph cache/atlas) untouched — the
        /// caller's trailing check, not this method, is the commit gate.</summary>
        void CompleteOnMain(CancellationToken ct);
    }
}
