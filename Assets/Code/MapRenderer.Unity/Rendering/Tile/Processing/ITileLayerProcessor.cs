using MapRenderer.Core.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A: the non-artifact-specific base contract for one render layer's per-tile worker-pass
    /// participation in <see cref="TileLayerProcessorRunner"/>. The input is an already-decoded
    /// <see cref="IDecodedTile"/>, not raw bytes — that makes the decode/fan-out boundary structural: a
    /// conforming processor cannot decide to decode the fetched bytes for itself. As of A4 the decode
    /// boundary is <see cref="IDecodedTileHandle"/> — every runner entry (mesh, source-less, symbol) reads
    /// through it instead of decoding for its own cadence; as of A6 the decode itself is encoding-driven
    /// (<see cref="ITileDecoder"/>), so this contract is not MVT-specific at all.
    ///
    /// Deliberately independent of mesh disposal and the symbol label sink — the two sibling capability
    /// interfaces extend this base without touching each other's code: <see cref="ITileMeshLayerProcessor"/>
    /// (A1, mesh settlement — returns a disposable <see cref="IRenderLayerPayload"/>) and
    /// <see cref="ITileWorkerThenMainLayerProcessor"/> (A3, a main-thread tail — artifact-free; the symbol
    /// sink stays the coordinator's own <c>SymbolTileLabelStore</c>).
    /// </summary>
    internal interface ITileLayerProcessor
    {
        /// <summary>Which cadence(s) this processor runs on. The mesh/source-less runner entries only invoke
        /// <see cref="LayerPhase.WorkerOnly"/> processors; <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>
        /// only invokes <see cref="LayerPhase.WorkerThenMain"/> processors.</summary>
        LayerPhase Phase { get; }

        /// <summary>Worker-thread invocation: process this layer's share of <paramref name="tile"/> (already
        /// decoded once by the runner and shared, by reference, across every processor in the dense pass).</summary>
        void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context);
    }
}
