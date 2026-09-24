// Synchronous test-only mesh writes; production reaches Styled*TileBuilder.ScheduleWrite asynchronously.
// Non-local invariant: each body mirrors its production counterpart statement for statement, so re-derive
// it from production before any "clean up".

using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Fill = MapRenderer.Core.Style.Fill;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The synchronous "one call in, a written <see cref="Mesh.MeshData"/> out" convenience: schedules the
    /// same Burst measure/write graphs production reaches through <c>Styled*TileBuilder.ScheduleWrite</c>
    /// (driven by <c>TileBuildGraph</c>), but completes them here instead of returning an uncompleted
    /// handle.
    /// </summary>
    internal static class SyncMeshWrite
    {
        /// <summary>Composes <see cref="StyledFillTileBuilder.BuildLayerInput"/> and
        /// <see cref="FillGeometry"/>.</summary>
        internal static void Fill(
            Mesh.MeshData                      md,
            IReadOnlyList<SelectedTileFeature> selectedFeatures,
            TileGeometryBuffers                geometry, // BORROWED — the store owns it; never disposed here
            Fill.PaintProperties               paint,
            double                             zoom,
            double3                            tileOriginRender,
            out int                            vertexCount,
            out Bounds                         bounds,
            IProjection                        projection = null, // null ⇒ WebMercator (launch-time config threads this in)
            Fill.LayoutProperties              layout     = null, // null ⇒ no fill-sort-key (declared feature order)
            TileBufferClip                     clip       = default, // default ⇒ disabled ⇒ the whole tile buffer is drawn
            TileBuildBuffers                   buffers    = null, // null ⇒ allocate (non-pooled caller)
            // A test-only oracle knob: true builds WITHOUT the outward boundary band, which is what a
            // comparison against a band-free reference has to compare against. Production never sets it.
            bool                               suppressBoundaryBand = false)
        {
            vertexCount = 0;
            bounds      = default;

            FillMeshPipeline.LayerInput input = StyledFillTileBuilder.BuildLayerInput(
                selectedFeatures, geometry, paint, zoom, tileOriginRender, out NativeArray<Vector4> featureColors,
                projection, layout, clip, buffers);

            if (!input.RingVisitOrder.IsCreated)
                return; // no polygon geometry — md left untouched; caller disposes the unused MeshData

            using var ringVisitOrder = input.RingVisitOrder;
            using var colors         = featureColors;
            // OR, not overwrite: FillGeometry rebuilds the LayerInput, so an overwrite would drop the
            // fill-antialias value and band a layer that production leaves hard.
            FillGeometry(md, geometry, ringVisitOrder, colors,
                tileOriginRender, projection, clip, out vertexCount, out bounds,
                suppressBoundaryBand || input.SuppressBoundaryBand);
        }

        /// <summary>The geometry half <see cref="Fill"/> hands off to: schedules
        /// <see cref="FillMeshGraph.Schedule"/> and streams the result into <paramref name="md"/> via
        /// <see cref="StyledFillTileBuilder.ScheduleStreamWrite"/>.</summary>
        internal static void FillGeometry(
            Mesh.MeshData               md,
            TileGeometryBuffers         geometry,
            NativeArray<int>            ringVisitOrder,
            NativeArray<Vector4>        featureColors,
            double3                     tileOriginRender,
            IProjection                 projection,
            TileBufferClip              clip,
            out int                     vertexCount,
            out Bounds                  bounds,
            bool                        suppressBoundaryBand = false)
        {
            vertexCount = 0;
            bounds      = default;

            var input = new FillMeshPipeline.LayerInput
            {
                Geometry          = geometry,
                RingVisitOrder    = ringVisitOrder,
                OriginRender      = tileOriginRender,
                Projection        = projection ?? StyledFillTileBuilder.DefaultProjection,
                Clip              = clip,
                SuppressBoundaryBand = suppressBoundaryBand,
            };

            // The graph is the only mesher; complete it synchronously here. Production, background quad
            // included, reaches it asynchronously through ScheduleWrite.
            FillGraphOutput output = FillMeshGraph.Schedule(input);
            output.Handle.Complete();
            try
            {
                if (!output.IsCreated || output.Error.Value != FillGraphCounts.Ok
                    || output.TileVertices.Length == 0 || output.TriangleIndices.Length == 0)
                    return;

                (JobHandle handle, NativeArray<float3x2> boundsArr) =
                    StyledFillTileBuilder.ScheduleStreamWrite(md, output, featureColors, geometry.Tile, geometry.Extent);
                handle.Complete();
                try
                {
                    float3x2 b = boundsArr[0];
                    float3 c3 = (b.c0 + b.c1) * 0.5f;
                    float3 sz = b.c1 - b.c0;
                    bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
                    vertexCount = output.TileVertices.Length;
                }
                finally { boundsArr.Dispose(); }
            }
            finally
            {
                output.Dispose();
            }
        }

        /// <summary>Composes <see cref="StyledFillExtrusionTileBuilder.BuildLayerInput"/>,
        /// <see cref="FillExtrusionMeshGraph.Schedule"/> and
        /// <see cref="StyledFillExtrusionTileBuilder.ScheduleStreamWrite"/>.</summary>
        internal static void FillExtrusion(
            Mesh.MeshData                       md,
            IReadOnlyList<SelectedTileFeature>  selectedFeatures,
            TileGeometryBuffers                 geometry,
            FillExtrusion.PaintProperties       paint,
            double                              zoom,
            double3                             tileOriginRender,
            out int                             vertexCount,
            out Bounds                          bounds,
            IProjection                         projection = null,
            TileBufferClip                      clip       = default,
            TileBuildBuffers                    buffers    = null)
        {
            vertexCount = 0;
            bounds      = default;

            FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                selectedFeatures, geometry, paint, zoom, tileOriginRender,
                out NativeArray<Vector4> featureColors, out NativeArray<Vector2> featureBake,
                projection, clip, buffers);

            if (!input.RingVisitOrder.IsCreated)
                return; // no polygon geometry — md left untouched

            using var ringVisitOrder = input.RingVisitOrder;
            using var colors         = featureColors;
            using var bake           = featureBake;

            IProjection proj = projection ?? StyledFillExtrusionTileBuilder.DefaultProjection;

            // The graph schedules BOTH the roof measure AND the wall chain; complete it synchronously here,
            // then hand the combined output to ScheduleStreamWrite.
            FillExtrusionGraphOutput ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
            ext.Handle.Complete();
            try
            {
                (JobHandle handle, NativeArray<float3x2> boundsArr) =
                    StyledFillExtrusionTileBuilder.ScheduleStreamWrite(md, ext.Roof, colors, bake, ext.Walls, proj, geometry.Tile, geometry.Extent);
                if (!boundsArr.IsCreated)
                    return; // roof + walls both empty — nothing to write (mirrors ScheduleWrite's own guard)
                handle.Complete();
                try
                {
                    float3x2 b = boundsArr[0];
                    float3 c3 = (b.c0 + b.c1) * 0.5f;
                    float3 sz = b.c1 - b.c0;
                    bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
                    vertexCount = (ext.Roof.IsCreated ? ext.Roof.TileVertices.Length : 0) + ext.Walls.VertexCount;
                }
                finally { boundsArr.Dispose(); }
            }
            finally
            {
                ext.Dispose();
            }
        }

        /// <summary>Composes <see cref="StyledLineTileBuilder.BuildLayerInput"/>,
        /// <see cref="LineMeshGraph.Schedule"/> and
        /// <see cref="StyledLineTileBuilder.ScheduleStreamWrite"/>.</summary>
        internal static void Line(
            Mesh.MeshData                      md,
            IReadOnlyList<SelectedTileFeature> selectedFeatures,
            TileGeometryBuffers                geometry, // BORROWED — the store owns it; never disposed here
            Line.PaintProperties               paint,
            Line.LayoutProperties              layout,
            double                             zoom,
            double3                            tileOriginRender,
            out int                            vertexCount,
            out Bounds                         bounds,
            IProjection                        projection = null) // null ⇒ WebMercator (launch-time config threads this in)
        {
            vertexCount = 0;
            bounds      = default;

            LayerInput input = StyledLineTileBuilder.BuildLayerInput(
                selectedFeatures, geometry, paint, layout, zoom, tileOriginRender,
                out NativeArray<Vector4> featureColors, out NativeArray<float> featureWidths, projection);

            if (!input.FeatureSelected.IsCreated)
                return; // no line geometry — md left untouched; caller disposes the unused MeshData

            using var featSelected = input.FeatureSelected;
            using var featColors   = featureColors;
            using var featWidths   = featureWidths;

            // `using var` disposes in REVERSE declaration order (boundsArr, then output), so a mid-method
            // throw cannot strand either.
            using var output = LineMeshGraph.Schedule(input);
            output.Handle.Complete();

            if (!output.IsCreated || output.Error.Value != LineGraphCounts.Ok
                || output.Vertices.Length == 0 || output.Indices.Length == 0)
                return;

            (JobHandle handle, NativeArray<float3x2> boundsArr) =
                StyledLineTileBuilder.ScheduleStreamWrite(md, output, featColors, featWidths);
            using var ownedBounds = boundsArr;
            handle.Complete();

            float3x2 b = boundsArr[0];
            float3 c3 = (b.c0 + b.c1) * 0.5f;
            float3 sz = b.c1 - b.c0;
            bounds      = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
            vertexCount = output.Vertices.Length;

            // Non-local invariant: `geometry` is BORROWED and never disposed here. The decoded LAYER lends it
            // to every style layer on this source-layer and to the same kick's symbol pass, so a free here
            // corrupts the heap under a sibling reader. StyledLineBuilderStructureTests pins the rule.
        }
    }
}
