using UnityEngine;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A1: the <see cref="ITileMeshLayerProcessor"/> adapter around one existing
    /// <see cref="ITileMeshRenderLayer"/> — the moved form of the per-layer body
    /// <c>TileManager.KickMeshBuild</c> used to run directly (feature select → source-layer resolve →
    /// <c>WriteInto</c>). Behaviour-preserving: same order, same arguments, same fallback name/zero-vertex
    /// semantics on every degenerate/faulting path (A1 invariant).
    /// </summary>
    internal sealed class TileMeshLayerProcessor : ITileMeshLayerProcessor
    {
        private readonly ITileMeshRenderLayer _layer;
        private readonly int                  _materialIndex;
        private readonly Mesh.MeshDataArray   _mda;

        // Committed by ProcessOnWorker ONLY after WriteInto returns successfully (or left false/default on
        // the no-features / no-source-layer legitimate empty paths, and on any throw — see ProcessOnWorker).
        private bool   _completedNormally;
        private int    _vertexCount;
        private Bounds _bounds;

        private TileMeshLayerProcessor(ITileMeshRenderLayer layer, int materialIndex, Mesh.MeshDataArray mda)
        {
            _layer         = layer;
            _materialIndex = materialIndex;
            _mda           = mda;
        }

        /// <summary>Main-thread-only allocation factory, called from <c>TileManager.KickMeshBuild</c>'s
        /// <c>PmMeshDataAllocate</c> block. Allocates one writable <see cref="Mesh.MeshDataArray"/>
        /// (<see cref="MeshDataPayload.AllocateTracked"/>) and retains it for the worker write.</summary>
        internal static TileMeshLayerProcessor AllocateForKick(ITileMeshRenderLayer layer, int materialIndex)
        {
            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            return new TileMeshLayerProcessor(layer, materialIndex, mda);
        }

        public LayerPhase Phase => LayerPhase.WorkerOnly;

        public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
        {
            var features = FeatureSelector.SelectFeatures(_layer.StyleLayer, tile, context.Zoom);
            if (features.Count > 0)
            {
                ITileLayer tileLayer = SourceLayerResolver.ResolveTileLayer(_layer.StyleLayer, tile);
                if (tileLayer != null)
                {
                    // If WriteInto throws partway through, control never reaches the two lines below — the
                    // adapter completes as zero-vertex (Complete()'s _completedNormally == false branch),
                    // matching today's fallback rather than uploading partially-written data.
                    _layer.WriteInto(_mda[0], features, context.Zoom, tileLayer.Extent, context.Tile,
                        context.TileOriginRender, context.Projection, context.BufferClip,
                        out int verts, out Bounds bounds);
                    _vertexCount = verts;
                    _bounds      = bounds;
                }
            }

            // Reached only if the above didn't throw — including the legitimate no-features /
            // no-source-layer empty paths (zero-vertex is itself a normal completion there).
            _completedNormally = true;
        }

        /// <summary>Infallible: only wraps the already-allocated <see cref="Mesh.MeshDataArray"/> into a
        /// <see cref="MeshDataPayload"/> (zero-vertex on the degenerate/fault paths).</summary>
        public IRenderLayerPayload Complete()
        {
            if (_completedNormally)
                return new MeshDataPayload(_mda, _vertexCount, _bounds, _layer.StyleLayer?.Id ?? "TileMesh", _materialIndex);

            // Decode fault, a throwing WriteInto, or this processor was never reached because an earlier
            // processor in the same dense pass threw — either way, the kick-allocated array must still be
            // wrapped and disposed via consume/discard (no native leak).
            return new MeshDataPayload(_mda, 0, default, "TileMesh", _materialIndex);
        }
    }
}
