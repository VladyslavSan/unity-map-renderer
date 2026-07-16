using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A3: the <see cref="LayerPhase.WorkerThenMain"/> capability — the sibling of
    /// <see cref="ITileMeshLayerProcessor"/>'s mesh-settlement capability — for a processor whose worker
    /// step (<see cref="ITileLayerProcessor.ProcessOnWorker"/>) is followed by exactly one main-thread
    /// completion step. Modelled first by <see cref="TileSymbolLayerProcessor"/>, choreographed by
    /// <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> (worker half, driven by A5b's
    /// <c>MapRenderer.Unity.Text.SymbolLabelSubsystem.RunWorkerAndHandoff</c>) and the symbol
    /// coordinator's main-thread tail loop (<c>MapRenderer.Unity.Text.SymbolLabelSubsystem.RunTailAsync</c>).
    ///
    /// <para>Deliberately ARTIFACT-FREE — unlike <see cref="ITileMeshLayerProcessor.Complete"/>, this
    /// contract returns nothing. The tail's output (e.g. a symbol layer's shaped <c>LabelInstance</c>s) is
    /// exposed and sunk by the implementor itself: sinks stay separate, only decode + dispatch unify (the
    /// design's rule). No <c>LayerArtifact</c> type is invented for A3.</para>
    /// </summary>
    internal interface ITileWorkerThenMainLayerProcessor : ITileLayerProcessor
    {
        /// <summary>MAIN-THREAD: runs after (and only after) this processor's own worker step for the same
        /// build — called AT MOST ONCE per build, after the worker pass that fed it has already returned.
        /// If the worker step never ran (an earlier processor's fault aborted the pass, or this pass's own
        /// decode faulted), this must complete as a no-op emitting nothing. Cancellation via
        /// <paramref name="ct"/> must leave shared state (glyph cache/atlas) untouched-in-flight, exactly as
        /// <c>StyledSymbolTileBuilder.ShapeAsync(ct)</c> already guarantees.</summary>
        UniTask CompleteOnMainAsync(CancellationToken ct);
    }
}
