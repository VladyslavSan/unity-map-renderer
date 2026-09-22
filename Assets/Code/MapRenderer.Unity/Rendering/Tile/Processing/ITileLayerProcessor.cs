using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The non-artifact-specific base contract for one render layer's per-tile worker-pass
    /// participation in <see cref="TileLayerProcessorRunner"/>. The input is an already-decoded
    /// <see cref="IDecodedTile"/>, not raw bytes — that makes the decode/fan-out boundary structural: a
    /// conforming processor cannot decide to decode the fetched bytes for itself. The decode boundary is a
    /// <c>SharedDisposable{IDecodedTile}</c> — every runner entry (mesh, source-less, symbol) reads through
    /// it instead of decoding for its own cadence — and the decode itself is encoding-driven
    /// (<see cref="ITileDecoder"/>), so this contract is not MVT-specific at all.
    ///
    /// Independent of mesh disposal and the symbol sink — the two sibling capability
    /// interfaces extend this base without touching each other's code: <see cref="ITileMeshLayerProcessor"/>
    /// (mesh settlement — returns a disposable <see cref="Style.MeshDataPayload"/>) and
    /// <see cref="ITileWorkerThenMainLayerProcessor"/> (a main-thread tail — artifact-free; the symbol
    /// sink stays the coordinator's own <c>SymbolTileStore</c>).
    /// </summary>
    internal interface ITileLayerProcessor
    {
        /// <summary>Which cadence(s) this processor runs on. The mesh/source-less runner entries only invoke
        /// <see cref="LayerPhase.WorkerOnly"/> processors; <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>
        /// only invokes <see cref="LayerPhase.WorkerThenMain"/> processors.</summary>
        LayerPhase Phase { get; }

        /// <summary>Worker-thread invocation: process this layer's share of <paramref name="tile"/> (already
        /// decoded once by the runner and shared, by reference, across every processor in the dense pass).
        ///
        /// <para><b>EXACTLY TWO PARAMETERS, and the count is a pinned tooth</b>
        /// (<c>NeutralGeometryPathTests</c>). The geometry belongs to the layer
        /// (<c>ITileLayer.Geometry</c>), so there is nothing for a sidecar parameter to carry. A future
        /// change that wants to "just pass a little extra through here" should cost a visible test edit: a
        /// detached sidecar is the shape that produces a mispairing hazard.</para>
        ///
        /// <para>A processor reads <c>tileLayer.Geometry</c> and <b>borrows</b> it: never dispose, never
        /// mutate, never retain past this call. A processor that is itself a producer (background, which
        /// synthesizes its quad) mints and owns its own buffer instead.</para></summary>
        void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context);
    }
}
