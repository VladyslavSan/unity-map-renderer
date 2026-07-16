using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A1: the mesh-settlement capability extending <see cref="ITileLayerProcessor"/> — the sibling
    /// of A3's <see cref="ITileWorkerThenMainLayerProcessor"/> (a main-thread tail, artifact-free). Both
    /// capabilities are invoked through their OWN dedicated runner entries
    /// (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>/<see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/>
    /// vs <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>) rather than one widened
    /// <see cref="ITileLayerProcessor"/> surface — mesh and symbol are genuinely separate cadences until A4
    /// introduces a shared decoded-tile feed (design §B Q1's signed-off deferral).
    ///
    /// <para><b>Completion contract:</b> <see cref="Complete"/> is called EXACTLY ONCE per worker pass, in
    /// dense order, after the worker attempt — even when decode failed, an earlier processor in the same
    /// pass threw, or this processor itself never produced geometry. It must wrap the kick-allocated mesh
    /// data as an empty (zero-vertex) payload in every one of those cases, so the already-allocated
    /// <c>Mesh.MeshDataArray</c> always reaches consume/dispose (never stranded — a leak). Because it only
    /// wraps an already-allocated array, a conforming implementation's <see cref="Complete"/> is
    /// infallible (cannot throw for any input); <see cref="TileLayerProcessorRunner"/>'s settlement loop
    /// still guards each call individually as a belt-and-braces measure against a contract violation.</para>
    ///
    /// This capability does not alter <see cref="IRenderLayerPayload"/> or its disposal semantics.
    /// </summary>
    internal interface ITileMeshLayerProcessor : ITileLayerProcessor
    {
        /// <summary>Called exactly once after the worker attempt (successful, faulted, or skipped after an
        /// earlier fault) to produce this processor's settled payload. Never disposes/consumes anything
        /// itself — the caller uploads/disposes the returned payload.</summary>
        IRenderLayerPayload Complete();
    }
}
