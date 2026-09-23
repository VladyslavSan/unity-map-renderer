using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The mesh-settlement capability extending <see cref="ITileLayerProcessor"/> — the sibling of
    /// <see cref="ITileWorkerThenMainLayerProcessor"/> (a main-thread tail, artifact-free). Both
    /// capabilities are invoked through their own dedicated runner entries
    /// (<see cref="TileLayerProcessorRunner.RunWorkerPass"/> vs
    /// <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>) — mesh and symbol are separate cadences
    /// that meet only at the shared decoded-tile feed. Non-local invariant: <see cref="Release"/> is called
    /// EXACTLY ONCE per worker pass, in dense order, after the worker attempt — even when decode failed, an
    /// earlier processor threw, or this processor never produced a graph request — and only returns the
    /// processor to its pool, so a conforming <see cref="Release"/> is infallible. This capability does not
    /// alter <see cref="MeshDataPayload"/> or its disposal semantics.
    /// </summary>
    internal interface ITileMeshLayerProcessor : ITileLayerProcessor
    {
        /// <summary>True iff the worker attempt produced a graph build — at
        /// most once, and BEFORE <see cref="Release"/> (which returns the processor to a pool). False when
        /// the worker attempt produced no work (an empty selection, a faulted decode, or a processor never
        /// reached after an earlier one faulted).</summary>
        bool TryTakeGraphRequest(out ILayerMeshBuild build);

        /// <summary>Called exactly once after the worker attempt (successful, faulted, or skipped after an
        /// earlier fault) to return this processor to its pool. Never disposes/consumes the graph build —
        /// the caller owns whatever <see cref="TryTakeGraphRequest"/> already handed it.</summary>
        void Release();
    }
}
