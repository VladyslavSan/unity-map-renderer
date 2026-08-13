using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A2: the source-less sibling of <see cref="TileMeshLayerProcessor"/> — the per-covered-tile
    /// background quad. Mirrors <see cref="TileMeshLayerProcessor"/>'s structure (retained
    /// <see cref="Mesh.MeshDataArray"/>, <c>_completedNormally</c>/<c>_vertexCount</c>/<c>_bounds</c>,
    /// infallible <see cref="Complete"/> wrapping the array as a <see cref="MeshDataPayload"/> — zero-vertex
    /// on fault), but takes no <see cref="ITileMeshRenderLayer"/> — the tile passed to
    /// <see cref="ProcessOnWorker"/> is always <c>null</c> and ignored (design §B Q2): this processor
    /// SYNTHESIZES the full-tile-extent quad's four corners rather than selecting a feature from a decoded
    /// tile layer.
    ///
    /// <para>Reuses <see cref="StyledFillTileBuilder.WriteGeometry"/> (design §B "Geometry, winding,
    /// material, draw order") rather than hand-building a quad, so the background quad gets earcut's
    /// cull-correct winding and the fill path's globe subdivision for free — dodging the <c>_Cull</c>
    /// winding bug that bit E3's hand-wound world-quad.</para>
    ///
    /// <para>IR B5: the corners are handed to a <see cref="PathGeometryMaterializer"/> as plain tile-local
    /// <c>double2</c>. They used to be a hand-authored MVT zigzag command stream, which existed for exactly
    /// one reason — the neutral feature interface declared <c>uint[] Geometry</c>, so a source-less quad had
    /// to transcode itself into MVT to be expressible. It no longer does.</para>
    /// </summary>
    internal sealed class TileBackgroundLayerProcessor : ITileMeshLayerProcessor
    {
        internal const double Extent = 4096.0;

        // The full-tile-extent ring in tile-local units: (0,0)→(4096,0)→(4096,4096)→(0,4096), one closed
        // 4-point ring, ONE feature. Order and count are load-bearing (they are the triangulated quad);
        // TileBackgroundQuadProjectionTests pins both against the retired MVT encoding this replaced.
        // Hoisted static — no per-tile allocation.
        // internal (not private): TileBackgroundQuadProjectionTests materializes these EXACT corners and
        // compares the result against the MVT command stream B5 retired — a changed corner order, count or
        // kind must fail there rather than producing a subtly-wrong background downstream.
        internal static readonly IReadOnlyList<IReadOnlyList<IReadOnlyList<double2>>> FullExtentRingPaths =
            new[]
            {
                new[]
                {
                    new[]
                    {
                        new double2(0.0,    0.0),
                        new double2(Extent, 0.0),
                        new double2(Extent, Extent),
                        new double2(0.0,    Extent),
                    },
                },
            };

        internal static readonly TileGeometryType[] FullExtentRingKinds = { TileGeometryType.Polygon };

        // Constant white — vertex colour is white by construction; the background colour comes from the
        // material uniform (_BaseColor/_Opacity), bound by MaterialFactory.BindBackgroundPaintToApplier
        // over the fill-base clone (design §B). This is the literal the retired {"fill-color":"#ffffff"}
        // paint evaluated to: Color.white.linear == white, and the constant opacity never depended on a
        // feature, so no evaluation is lost. Clean-room: public Style Spec, no MapLibre source.
        private static readonly Vector4[] WhiteColor = { new Vector4(1f, 1f, 1f, 1f) };

        private readonly int                _materialIndex;
        private readonly string             _payloadName;
        private readonly Mesh.MeshDataArray _mda;

        // Committed by ProcessOnWorker ONLY after WriteMeshData returns successfully — mirrors
        // TileMeshLayerProcessor's fault contract exactly (fault ⇒ zero-vertex on Complete()).
        private bool   _completedNormally;
        private int    _vertexCount;
        private Bounds _bounds;

        private TileBackgroundLayerProcessor(int materialIndex, string payloadName, Mesh.MeshDataArray mda)
        {
            _materialIndex = materialIndex;
            _payloadName   = payloadName;
            _mda           = mda;
        }

        /// <summary>Main-thread-only allocation factory, called from <c>TileManager.KickSourcelessBackground</c>'s
        /// <c>PmMeshDataAllocate</c> block — mirrors <see cref="TileMeshLayerProcessor.AllocateForKick"/>.</summary>
        internal static TileBackgroundLayerProcessor AllocateForKick(BackgroundRenderLayer layer, int materialIndex)
        {
            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            string name = layer?.StyleLayer?.Id ?? "background";
            return new TileBackgroundLayerProcessor(materialIndex, name, mda);
        }

        public LayerPhase Phase => LayerPhase.WorkerOnly;

        /// <summary><paramref name="tile"/> is always <c>null</c> (source-less — see
        /// <see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/>) and ignored: the geometry is the
        /// hoisted synthetic full-extent ring, not anything selected from a decoded tile.</summary>
        /// <remarks>This processor <b>is</b> a producer: its geometry is synthesized, belongs to no source
        /// layer, and is therefore minted and owned here rather than borrowed from a decoded tile — the one
        /// consumer for which that is true.</remarks>
        public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
        {
            // If WriteGeometry throws partway through, control never reaches the two lines below — the
            // processor completes as zero-vertex (Complete()'s _completedNormally == false branch), matching
            // TileMeshLayerProcessor's fallback rather than uploading partially-written data.
            // The clip is threaded through even though it is behaviourally inert here — the synthetic ring sits
            // exactly ON the window at any margin ≥ 0, and the boundary is inclusive — so the background quad
            // and the fill layers over it can never be built under different windows.
            TileGeometryBuffers geometry = new PathGeometryMaterializer(
                context.Tile, Extent, FullExtentRingKinds, FullExtentRingPaths).Materialize();
            if (geometry.IsCreated)
            {
                // The trivial visit order: one feature, every ring, in decode order. Fill's rank/selection
                // machinery has nothing to say about a synthesized single-feature quad.
                var visitOrder = new NativeArray<int>(
                    geometry.RingCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                try
                {
                    for (int r = 0; r < geometry.RingCount; r++) visitOrder[r] = r;

                    StyledFillTileBuilder.WriteGeometry(
                        _mda[0], geometry, visitOrder, WhiteColor, context.TileOriginRender,
                        context.Projection, context.BufferClip, out int verts, out Bounds bounds);
                    _vertexCount = verts;
                    _bounds      = bounds;
                }
                finally
                {
                    // Both are OURS: the materializer transferred the buffer to us (we are the producer), and
                    // the visit order was allocated here. Nothing else may free either.
                    visitOrder.Dispose();
                    geometry.Dispose();
                }
            }

            // Reached only if the above didn't throw.
            _completedNormally = true;
        }

        /// <summary>Infallible: only wraps the already-allocated <see cref="Mesh.MeshDataArray"/> into a
        /// <see cref="MeshDataPayload"/> (zero-vertex on the fault path).</summary>
        public IRenderLayerPayload Complete()
        {
            if (_completedNormally)
                return new MeshDataPayload(_mda, _vertexCount, _bounds, _payloadName, _materialIndex);

            // A throwing WriteMeshData, or this processor was never reached because an earlier processor in
            // the same dense pass threw — either way, the kick-allocated array must still be wrapped and
            // disposed via consume/discard (no native leak).
            return new MeshDataPayload(_mda, 0, default, _payloadName, _materialIndex);
        }
    }
}
