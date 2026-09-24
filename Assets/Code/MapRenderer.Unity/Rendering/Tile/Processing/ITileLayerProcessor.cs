using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The base contract for one render layer's per-tile worker pass in <see cref="TileLayerProcessorRunner"/>.
    /// The input is the shared, already-decoded <see cref="IDecodedTile"/>, never raw bytes, and the decode is
    /// encoding-driven (<see cref="ITileDecoder"/>), so the contract is not MVT-specific. Two sibling interfaces
    /// extend it: <see cref="ITileMeshLayerProcessor"/> (mesh settlement) and
    /// <see cref="ITileWorkerThenMainLayerProcessor"/> (a main-thread tail).
    /// </summary>
    internal interface ITileLayerProcessor
    {
        /// <summary>Which cadence(s) this processor runs on. The mesh/source-less runner entries only invoke
        /// <see cref="LayerPhase.WorkerOnly"/> processors; <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>
        /// only invokes <see cref="LayerPhase.WorkerThenMain"/> processors.</summary>
        LayerPhase Phase { get; }

        /// <summary>Worker-thread invocation: process this layer's share of <paramref name="tile"/> (already
        /// decoded once by the runner and shared, by reference, across every processor in the dense pass).
        /// Non-local invariant: EXACTLY TWO PARAMETERS, pinned by <c>NeutralGeometryPathTests</c> — the
        /// geometry belongs to the layer (<c>ITileLayer.Geometry</c>), so a sidecar parameter would be a
        /// detached-mispairing hazard. A processor <b>borrows</b> <c>tileLayer.Geometry</c>: never dispose,
        /// mutate, or retain past this call; a processor that is itself a producer (background, which
        /// synthesizes its quad) mints and owns its own buffer instead.</summary>
        void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context);
    }
}
