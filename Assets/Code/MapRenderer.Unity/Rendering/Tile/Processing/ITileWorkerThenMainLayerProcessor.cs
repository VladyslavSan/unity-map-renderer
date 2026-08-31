using System.Threading;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The <see cref="LayerPhase.WorkerThenMain"/> capability — the sibling of
    /// <see cref="ITileMeshLayerProcessor"/>'s mesh-settlement capability — for a processor whose worker
    /// step (<see cref="ITileLayerProcessor.ProcessOnWorker"/>) is followed by exactly one main-thread
    /// completion step. Modelled first by <see cref="TileSymbolLayerProcessor"/>.
    ///
    /// <para>Deliberately ARTIFACT-FREE — unlike <see cref="ITileMeshLayerProcessor.Complete"/>, this
    /// contract returns nothing. The tail's output (e.g. a symbol layer's shaped symbol records) is
    /// exposed and sunk by the implementor itself: sinks stay separate, only decode + dispatch unify.</para>
    /// </summary>
    internal interface ITileWorkerThenMainLayerProcessor : ITileLayerProcessor
    {
        /// <summary>MAIN-THREAD, SYNCHRONOUS: runs after (and only after) this processor's own worker step
        /// for the same build — called AT MOST ONCE per build, after the worker pass that fed it has already
        /// returned. If the worker step never ran (an earlier processor's fault aborted the pass, or this
        /// pass's own decode faulted), this must complete as a no-op emitting nothing. This step itself never
        /// suspends — that is a property of <c>StyledSymbolTileBuilder.Shape(ct)</c>, not a guarantee it
        /// makes about anything upstream; the CALLER (<c>SymbolSubsystem.RunTailAsync</c>'s collect → await
        /// ensure → ct-check sequencing) is what guarantees any glyph-atlas fetch/append the build needs has
        /// already completed before this call is even reached. Cancellation via <paramref name="ct"/> leaves
        /// shared state (glyph cache/atlas) untouched — the caller's trailing cancellation check, not this
        /// method, is the commit gate.</summary>
        void CompleteOnMain(CancellationToken ct);
    }
}
