namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Which cadence(s) an <see cref="ITileLayerProcessor"/> runs on. Mesh processors are <see cref="WorkerOnly"/>:
    /// the fill/line fan-out is all worker-thread work, so <see cref="TileLayerProcessorRunner.RunWorkerPass"/>
    /// rejects <see cref="WorkerThenMain"/> as a programming error rather than downgrade it.
    /// <see cref="WorkerThenMain"/> is the symbol cadence: worker-side extraction, then a main-thread shaping
    /// tail, run by <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> and the symbol coordinator.
    /// </summary>
    internal enum LayerPhase
    {
        /// <summary>The entire processor body runs on the worker thread; nothing further happens on main.</summary>
        WorkerOnly,

        /// <summary>A worker-side step followed by a main-thread completion step
        /// (<see cref="ITileWorkerThenMainLayerProcessor.CompleteOnMain"/>). The MESH worker-pass entry
        /// (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>) still rejects this phase — it has no
        /// tail; only <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> choreographs it.</summary>
        WorkerThenMain,
    }
}
