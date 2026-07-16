namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A: which cadence(s) a
    /// <see cref="ITileLayerProcessor"/> runs on. A1/A2 implement only <see cref="WorkerOnly"/> — the
    /// fill/line/background fan-out is entirely a worker-thread write into a main-thread-allocated mesh
    /// array, so <see cref="TileLayerProcessorRunner.RunWorkerPass"/> and
    /// <see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/> reject <see cref="WorkerThenMain"/>
    /// (those mesh passes have no main-thread tail to give it — a MESH processor requesting it is a
    /// programming error, not a phase to silently downgrade to worker-only).
    /// <see cref="WorkerThenMain"/> is A3's: symbol worker-side extraction (<c>TileSymbolLayerProcessor</c>)
    /// followed by a main-thread shaping tail, choreographed by
    /// <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> (worker half) plus the symbol
    /// coordinator's batched main-thread tail loop.
    /// </summary>
    internal enum LayerPhase
    {
        /// <summary>The entire processor body runs on the worker thread; nothing further happens on main.</summary>
        WorkerOnly,

        /// <summary>A3+: a worker-side step followed by a main-thread completion step
        /// (<see cref="ITileWorkerThenMainLayerProcessor.CompleteOnMainAsync"/>). The MESH worker-pass
        /// entries (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>,
        /// <see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/>) still reject this phase — they
        /// have no tail; only <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> choreographs it.</summary>
        WorkerThenMain,
    }
}
