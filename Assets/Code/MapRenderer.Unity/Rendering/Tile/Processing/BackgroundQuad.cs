using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The per-covered-tile background quad's geometry, a producer-only static class. A background tile
    /// kicks straight through <see cref="TileBuildGraph"/>, via
    /// <c>TileManager.KickSourcelessBackground</c>, with no processor of its own.
    ///
    /// <para>Feeds the SAME <see cref="TileBuildGraph"/> write step every fill layer's geometry does,
    /// rather than hand-building a quad, so the background quad gets earcut's cull-correct winding and the
    /// fill path's globe subdivision for free — and avoids the <c>_Cull</c> winding hazard of a hand-wound
    /// quad.</para>
    ///
    /// <para>The corners are handed to a <see cref="PathGeometryMaterializer"/> as plain tile-local
    /// <c>double2</c>, never an MVT zigzag command stream.</para>
    /// </summary>
    internal static class BackgroundQuad
    {
        internal const double Extent = 4096.0;

        // The full-tile-extent ring in tile-local units: (0,0)→(4096,0)→(4096,4096)→(0,4096), one closed
        // 4-point ring, ONE feature. Order and count are load-bearing (they are the triangulated quad).
        // Hoisted static — no per-tile allocation.
        // internal (not private): TileBackgroundQuadProjectionTests materializes these EXACT corners and
        // compares the result against the retired MVT command stream — a changed corner order, count or
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

        // Constant white — vertex colour is always white; the background colour comes from the
        // material uniform (_BaseColor/_Opacity), bound by MaterialFactory.BindBackgroundPaintToApplier
        // over the fill-base clone. Color.white.linear == white, and the background's opacity never
        // depends on a feature, so nothing is lost by not evaluating a paint here.

        /// <summary>Mints the full-tile-extent quad's geometry — the one thing every dense background layer
        /// over the same tile shares. A per-tile caller (<see cref="TileManager.KickSourcelessBackground"/>)
        /// mints it ONCE and shares it across every layer via <see cref="BuildLayerInput"/>'s borrowed
        /// <c>geometry</c> parameter, rather than each layer minting — and owning — its own copy. The caller
        /// owns the result; nothing in this file disposes it on the caller's behalf.</summary>
        internal static TileGeometryBuffers MintFullExtentGeometry(TileId tile) =>
            new PathGeometryMaterializer(tile, Extent, FullExtentRingKinds, FullExtentRingPaths).Materialize();

        /// <summary>Builds the graph-arm input over a minted (or shared) <paramref name="geometry"/> — the
        /// trivial one-feature visit order and the one white vertex colour every background layer needs.
        ///
        /// <para><paramref name="geometry"/> is BORROWED (<see cref="MintFullExtentGeometry"/> mints it,
        /// matching <c>FillMeshPipeline.LayerInput.Geometry</c>'s own documented contract) — this method
        /// never disposes it, so the same minted quad can back every dense background layer over one tile
        /// without this method knowing or caring whether it is shared. <paramref name="visitOrder"/>/
        /// <paramref name="featureColors"/> are the CALLER'S to dispose. When <paramref name="geometry"/> is
        /// uncreated, both are left uncreated too and the returned <see cref="FillMeshPipeline.LayerInput"/>
        /// is <c>default</c> — the graph's own empty-input guard (<c>FillMeshGraph.Schedule</c>) already
        /// normalises that shape.</para>
        /// </summary>
        internal static FillMeshPipeline.LayerInput BuildLayerInput(
            in TileLayerProcessContext ctx, in TileGeometryBuffers geometry,
            out NativeArray<int> visitOrder, out NativeArray<Vector4> featureColors)
        {
            if (!geometry.IsCreated)
            {
                visitOrder    = default;
                featureColors = default;
                return default;
            }

            // The trivial visit order: one feature, every ring, in decode order. Fill's rank/selection
            // machinery has nothing to say about a synthesized single-feature quad. Allocator.Persistent,
            // NOT TempJob: the graph arm builds this on the main thread at kick and the buffer stays a live
            // [ReadOnly] job input for the whole measure step, which spans several main-thread frames —
            // past TempJob's 4-frame lifetime check either way.
            visitOrder = new NativeArray<int>(
                geometry.RingCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // One white vertex colour, indexed by the quad's single feature ordinal 0.
            featureColors = new NativeArray<Vector4>(1, Allocator.Persistent);

            featureColors[0] = new Vector4(1f, 1f, 1f, 1f);

            NativeArray<int> visitOrderWritable = visitOrder.GetSubArray(0, visitOrder.Length);
            for (int r = 0; r < geometry.RingCount; r++) visitOrderWritable[r] = r;

            return new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = ctx.TileOriginRender,
                Projection     = ctx.Projection,
                Clip           = ctx.BufferClip,
                // Every edge of a full-tile quad is a tile seam abutting the neighbour's identical quad, so a
                // boundary band here would only lay a 1 px double-composited rim along every seam — there is
                // no silhouette to antialias.
                SuppressBoundaryBand = true,
            };
        }
    }
}
