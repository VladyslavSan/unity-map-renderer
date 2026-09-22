using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The mesh-settlement capability extending <see cref="ITileLayerProcessor"/> — the sibling
    /// of <see cref="ITileWorkerThenMainLayerProcessor"/> (a main-thread tail, artifact-free). Both
    /// capabilities are invoked through their OWN dedicated runner entries
    /// (<see cref="TileLayerProcessorRunner.RunWorkerPass"/> vs
    /// <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>) rather than one widened
    /// <see cref="ITileLayerProcessor"/> surface — mesh and symbol are separate cadences that meet only at
    /// the shared decoded-tile feed.
    ///
    /// <para><b>Completion contract:</b> <see cref="Release"/> is called EXACTLY ONCE per worker pass, in
    /// dense order, after the worker attempt — even when decode failed, an earlier processor in the same
    /// pass threw, or this processor itself never produced a graph request. It only returns the processor to
    /// its pool, so a conforming implementation's <see cref="Release"/> is infallible (cannot throw for any
    /// input); <see cref="TileLayerProcessorRunner"/>'s settlement loop still guards each call individually
    /// as a belt-and-braces measure against a contract violation.</para>
    ///
    /// This capability does not alter <see cref="MeshDataPayload"/> or its disposal semantics.
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
