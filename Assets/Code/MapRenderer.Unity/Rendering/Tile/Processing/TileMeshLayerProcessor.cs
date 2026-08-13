using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Jobs.Tiles;

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
            // The source layer is resolved FIRST: it is both the selection input and the owner of the buffer
            // the ordinals index, so resolving it once is what makes "the geometry I borrow is the geometry
            // my ordinals index" true by construction rather than by matching two lookups.
            ITileLayer tileLayer = SourceLayerResolver.ResolveTileLayer(_layer.StyleLayer, tile);
            if (tileLayer != null)
            {
                var selected = new List<SelectedTileFeature>();
                FeatureSelector.SelectFeatures(_layer.StyleLayer, tileLayer, context.Zoom, selected);

                // Preserves the pre-B7 "no selected features ⇒ do nothing" gate. Since IR C1 P3 it no longer
                // avoids any materialization (the decode already did that, once, for every layer) — it stays
                // because it is what keeps a filter that matches nothing from calling WriteInto at all.
                if (selected.Count > 0)
                {
                    // BORROWED from the decoded tile — never disposed here, and its Tile/Extent came from the
                    // decode, so there is no second copy of the address for this call site to get wrong.
                    TileGeometryBuffers geometry = tileLayer.Geometry;

                    // An empty layer materializes to nothing; skipping WriteInto here is the same zero-vertex
                    // result the pipeline reached by scheduling over an uncreated buffer.
                    if (geometry.IsCreated)
                    {
                        // If WriteInto throws partway through, control never reaches the two lines below — the
                        // adapter completes as zero-vertex (Complete()'s _completedNormally == false branch),
                        // matching today's fallback rather than uploading partially-written data.
                        _layer.WriteInto(_mda[0], selected, geometry, context.Zoom,
                            context.TileOriginRender, context.Projection, context.BufferClip,
                            out int verts, out Bounds bounds);
                        _vertexCount = verts;
                        _bounds      = bounds;
                    }
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
