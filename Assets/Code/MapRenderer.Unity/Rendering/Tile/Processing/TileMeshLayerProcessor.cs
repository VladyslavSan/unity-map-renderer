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
    /// Epic A / A1: the <see cref="ITileMeshLayerProcessor"/> adapter around one existing
    /// <see cref="ITileMeshRenderLayer"/> — the moved form of the per-layer body
    /// <c>TileManager.KickMeshBuild</c> used to run directly (feature select → source-layer resolve →
    /// build the graph request). Behaviour-preserving: same order, same arguments (A1 invariant).
    /// job-scheduling-design.md §8 stage 5 Group B: the graph is the only mesher — this adapter has no
    /// kick-allocated <see cref="Mesh.MeshDataArray"/> and no seam branch any more.
    /// </summary>
    internal sealed class TileMeshLayerProcessor : ITileMeshLayerProcessor
    {
        private ITileMeshRenderLayer _layer;
        private int                  _materialIndex;

        /// <summary>The graph build produced by <see cref="ProcessOnWorker"/> — held until
        /// <see cref="TryTakeGraphRequest"/> hands it to the runner, exactly once. If a prior build was never
        /// taken (the runner never called <see cref="TryTakeGraphRequest"/> before this processor was
        /// returned to the pool), <see cref="Reset"/> disposes it first — a never-fired backstop that turns a
        /// forgotten take into a bounded, observable non-leak rather than a silent one. The unconditional
        /// null-out below is what makes this safe within a pooled processor's NEXT lease, not just this
        /// one — see its own comment.</summary>
        private ILayerMeshBuild _build;

        // Pool-only: real construction happens via Reset, called from AllocateForKick after
        // TileMeshLayerProcessorPool.Rent(). Never invoked directly outside the pool's Rent() fallback.
        internal TileMeshLayerProcessor() { }

        /// <summary>Re-initializes a pooled (or freshly-minted) instance to the same state the retired
        /// constructor used to establish — every field <see cref="Release"/> reads, so a reused instance
        /// never leaks a prior build's state into the next one.</summary>
        internal void Reset(ITileMeshRenderLayer layer, int materialIndex)
        {
            _layer         = layer;
            _materialIndex = materialIndex;
            // Mandatory, not tidiness: Dispose() returns a build to ITS OWN pool, so without the
            // unconditional null-out below, a later Reset on this (re-rented) processor would Dispose a
            // build some OTHER tenant has since rented — freeing that tenant's columns out from under it.
            if (_build != null) _build.Dispose();
            _build = null;
        }

        /// <summary>Called from <c>TileManager.KickMeshBuild</c> — no main-thread allocation any more
        /// (job-scheduling-design.md §8 stage 5 Group B): every layer's mesh is allocated later, by the
        /// graph's write step.</summary>
        internal static TileMeshLayerProcessor AllocateForKick(ITileMeshRenderLayer layer, int materialIndex)
        {
            TileMeshLayerProcessor processor = TileMeshLayerProcessorPool.Rent();
            processor.Reset(layer, materialIndex);
            return processor;
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
                // perf/gc-elimination: a pooled worker pass (RunWorkerPass) hands a non-null Buffers, so
                // selection is appended into its grow-only SelectedTileFeature[] instead of a fresh
                // `new List<>()` per (tile × style-layer) — this was the single biggest managed allocator on
                // the mesh-build path (~318 KB/tile-build). `null` (tests, non-pooled callers) keeps the
                // original allocating behaviour verbatim, mirroring StyledFillTileBuilder.OrderBySortKey.
                // Read Features ONCE per layer (the accessor may be a lease/decorator, and re-reading it is
                // exactly the "re-derive the buffer" shape RunWorkerPass_ObtainsGeometryWithoutReReadingThe-
                // FeatureList forbids): the same list sizes the scratch buffer and drives the selection loop.
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

                // Preserves the pre-B7 "no selected features ⇒ do nothing" gate. Since IR C1 P3 it no longer
                // avoids any materialization (the decode already did that, once, for every layer) — it stays
                // because it is what keeps a filter that matches nothing from calling BuildGraphRequest at
                // all.
                if (selected.Count > 0)
                {
                    // BORROWED from the decoded tile — never disposed here, and its Tile/Extent came from the
                    // decode, so there is no second copy of the address for this call site to get wrong.
                    TileGeometryBuffers geometry = tileLayer.Geometry;

                    // An empty layer materializes to nothing; skipping the request here is the same
                    // zero-vertex result the pipeline reaches by scheduling over an uncreated buffer.
                    if (geometry.IsCreated)
                    {
                        // job-scheduling-design.md §8 stage 5 Group B: the graph is the only mesher — build
                        // the request, the graph's write step allocates and writes later. The layer itself
                        // picks its kind's Rent (DIV-A1) and its own emptiness gate; this adapter only stores
                        // the result — null when there is nothing to build.
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

        /// <summary>Infallible: only returns THIS processor to its pool — safe because <c>Release</c> is
        /// <see cref="ITileMeshLayerProcessor"/>'s sole "done with this processor" signal, called exactly
        /// once per processor (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>'s settle loop), with
        /// nothing touching the processor afterward. Never disposes/consumes <see cref="_build"/> — the
        /// caller owns whatever <see cref="TryTakeGraphRequest"/> already handed it; a build never taken
        /// is <see cref="Reset"/>'s backstop to free, not this method's.</summary>
        public void Release() => TileMeshLayerProcessorPool.Return(this);
    }
}
