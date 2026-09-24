using System.Threading;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The <see cref="LayerPhase.WorkerThenMain"/> capability: a worker step
    /// (<see cref="ITileLayerProcessor.ProcessOnWorker"/>) followed by exactly one main-thread completion
    /// step. Implemented by <see cref="TileSymbolLayerProcessor"/>. Unlike
    /// <see cref="ITileMeshLayerProcessor"/>, it hands the caller no artifact: the implementor sinks its own
    /// output (e.g. shaped symbol records), so sinks stay separate and only decode + dispatch unify.
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
