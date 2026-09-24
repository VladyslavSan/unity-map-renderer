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
    /// The per-covered-tile background quad's geometry. A background tile kicks straight through
    /// <see cref="TileBuildGraph"/>, via <c>TileManager.KickSourcelessBackground</c>, with no processor.
    /// It feeds the same write step as every fill layer, so the quad gets earcut's cull-correct winding and
    /// the globe subdivision, and has no hand-wound <c>_Cull</c> hazard. The corners reach
    /// <see cref="PathGeometryMaterializer"/> as tile-local <c>double2</c>, not an MVT command stream.
    /// </summary>
    internal static class BackgroundQuad
    {
        internal const double Extent = 4096.0;

        // The full-extent ring in tile-local units: one closed 4-point ring, one feature; order and count are
        // load-bearing. Internal so TileBackgroundQuadProjectionTests can compare it with the MVT stream.
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

        // The vertex colour is white; the background colour is the material uniform (_BaseColor/_Opacity) that
        // MaterialFactory.BindBackgroundPaintToApplier binds. No background paint depends on a feature.

        /// <summary>Mints the full-tile-extent quad's geometry — the one thing every dense background layer
        /// over the same tile shares. A per-tile caller (<see cref="TileManager.KickSourcelessBackground"/>)
        /// mints it ONCE and shares it across every layer via <see cref="BuildLayerInput"/>'s borrowed
        /// <c>geometry</c> parameter, rather than each layer minting — and owning — its own copy. The caller
        /// owns the result; nothing in this file disposes it on the caller's behalf.</summary>
        internal static TileGeometryBuffers MintFullExtentGeometry(TileId tile) =>
            new PathGeometryMaterializer(tile, Extent, FullExtentRingKinds, FullExtentRingPaths).Materialize();

        /// <summary>Builds the graph-arm input over a borrowed <paramref name="geometry"/>, so one minted quad
        /// can back every background layer of a tile: a one-feature visit order and one white vertex colour.
        /// The caller disposes <paramref name="visitOrder"/>/<paramref name="featureColors"/>. An uncreated
        /// <paramref name="geometry"/> gives uncreated outputs and a <c>default</c> input, which
        /// <c>FillMeshGraph.Schedule</c>'s empty-input guard handles.
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

            // Persistent, not TempJob: this stays a live job input for the whole measure step, which can span
            // more main-thread frames than TempJob's 4-frame lifetime check allows.
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
                // Every edge of the quad is a tile seam, so a boundary band would only lay a 1 px
                // double-composited rim along each seam; there is no silhouette to antialias.
                SuppressBoundaryBand = true,
            };
        }
    }
}
