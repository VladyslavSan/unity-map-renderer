using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The <see cref="ITileMeshLayerProcessor"/> adapter around one <see cref="ITileMeshRenderLayer"/>:
    /// the per-layer body <c>TileManager.KickMeshBuild</c> fans out to — feature select → source-layer
    /// resolve → build the graph request. The graph is the only mesher, so this adapter has no
    /// kick-allocated <see cref="Mesh.MeshDataArray"/> and no seam branch.
    /// </summary>
    internal sealed class TileMeshLayerProcessor : ITileMeshLayerProcessor
    {
        private ITileMeshRenderLayer _layer;
        private int                  _materialIndex;

        /// <summary>The graph build <see cref="ProcessOnWorker"/> produced, held until
        /// <see cref="TryTakeGraphRequest"/> hands it to the runner once. <see cref="Reset"/> disposes a build
        /// that was never taken, a backstop that turns a forgotten take into a bounded non-leak.</summary>
        private ILayerMeshBuild _build;

        // Pool-only: real construction happens via Reset, called from AllocateForKick after
        // TileMeshLayerProcessorPool.Rent(). Never invoked directly outside the pool's Rent() fallback.
        internal TileMeshLayerProcessor() { }

        /// <summary>Re-initializes a pooled (or freshly-minted) instance to a clean state — every field
        /// <see cref="Release"/> reads, so a reused instance never leaks a prior build's state into the
        /// next one.</summary>
        internal void Reset(ITileMeshRenderLayer layer, int materialIndex)
        {
            _layer         = layer;
            _materialIndex = materialIndex;
            // The null-out is mandatory: Dispose() returns the build to its own pool, so a later Reset would
            // otherwise free a build that another tenant has since rented.
            if (_build != null) _build.Dispose();
            _build = null;
        }

        /// <summary>Called from <c>TileManager.KickMeshBuild</c> — no main-thread allocation: every
        /// layer's mesh is allocated later, by the graph's write step.</summary>
        internal static TileMeshLayerProcessor AllocateForKick(ITileMeshRenderLayer layer, int materialIndex)
        {
            TileMeshLayerProcessor processor = TileMeshLayerProcessorPool.Rent();
            processor.Reset(layer, materialIndex);
            return processor;
        }

        public LayerPhase Phase => LayerPhase.WorkerOnly;

        public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
        {
            // Resolve the source layer once: it is both the selection input and the owner of the buffer the
            // ordinals index, so the borrowed geometry always matches the ordinals.
            ITileLayer tileLayer = SourceLayerResolver.ResolveTileLayer(_layer.StyleLayer, tile);
            if (tileLayer != null)
            {
                // Non-obvious why: a pooled pass (RunWorkerPass) hands non-null Buffers, so selection fills a
                // grow-only array, not a new List per (tile × style-layer); null (tests) takes the allocating
                // path. Read Features once: the accessor may be a lease, and a re-read is what
                // RunWorkerPass_ObtainsGeometryWithoutReReadingTheFeatureList forbids.
                var features = tileLayer.Features; // IReadOnlyList<IFeature> — read the accessor ONCE
                IReadOnlyList<SelectedTileFeature> selected;
                if (context.Buffers != null)
                {
                    SelectedTileFeature[] buffer = context.Buffers.SelectionBuffer(features.Count);
                    int selectedCount = FeatureSelector.SelectFeatures(_layer.StyleLayer, tileLayer, features, context.Zoom, buffer);
                    selected = context.Buffers.SelectionView(selectedCount);
                }
                else
                {
                    var list = new List<SelectedTileFeature>();
                    FeatureSelector.SelectFeatures(_layer.StyleLayer, tileLayer, features, context.Zoom, list);
                    selected = list;
                }

                // A filter that matches nothing never calls BuildGraphRequest. The decode has already
                // materialized the geometry, so this gate saves no materialization.
                if (selected.Count > 0)
                {
                    // BORROWED from the decoded tile — never disposed here, and its Tile/Extent came from the
                    // decode, so there is no second copy of the address for this call site to get wrong.
                    TileGeometryBuffers geometry = tileLayer.Geometry;

                    // An empty layer materializes to nothing; skipping the request here is the same
                    // zero-vertex result the pipeline reaches by scheduling over an uncreated buffer.
                    if (geometry.IsCreated)
                    {
                        // The graph's write step allocates and writes later. The layer picks its Rent and
                        // emptiness gate; this adapter stores the result (null: nothing to build).
                        _build = _layer.BuildGraphRequest(
                            selected, geometry, in context, _materialIndex, _layer.StyleLayer?.Id ?? "TileMesh");
                    }
                }
            }

            // Reached only if the above didn't throw — including the legitimate no-features /
            // no-source-layer / no-request empty paths (no request is itself a normal completion there).
        }

        public bool TryTakeGraphRequest(out ILayerMeshBuild build)
        {
            if (_build == null)
            {
                build = null;
                return false;
            }

            build  = _build;
            _build = null;
            return true;
        }

        /// <summary>Infallible: only returns this processor to its pool. The settle loop of
        /// <see cref="TileLayerProcessorRunner.RunWorkerPass"/> calls it once per processor, and nothing touches
        /// the processor afterward. It never disposes <see cref="_build"/>: the caller owns a taken build, and
        /// <see cref="Reset"/> frees one never taken.</summary>
        public void Release() => TileMeshLayerProcessorPool.Return(this);
    }
}
