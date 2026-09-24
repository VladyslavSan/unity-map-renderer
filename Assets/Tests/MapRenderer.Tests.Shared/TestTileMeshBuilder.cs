// Test helper: synchronous fill/line mesh build. It writes through SyncMeshWrite, the test assembly's
// synchronous mirror of production's graph write, into a writable MeshDataArray, then applies it to a Mesh.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Lines;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests
{
    internal static class TestTileMeshBuilder
    {
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        /// <summary>
        /// The production selection shape, for a test that hands over a plain feature list. Ordinals are
        /// <c>0..n-1</c> — the identity, which is exactly what an unfiltered selection over a layer whose
        /// features ARE this list produces.
        /// </summary>
        internal static List<SelectedTileFeature> Selection(IReadOnlyList<IFeature> features)
        {
            var selected = new List<SelectedTileFeature>(features.Count);
            for (int i = 0; i < features.Count; i++)
                selected.Add(new SelectedTileFeature { Feature = features[i], Ordinal = i });
            return selected;
        }

        /// <summary>
        /// The shared buffer for a plain list of SYNTHETIC features, materialized from ALL of them (not just
        /// the polygons), as a whole source layer holds. Caller owns it. A decoded <c>MvtFeature</c> carries no
        /// command stream, because its layer owns the geometry; a caller with a decoded layer reads
        /// <c>ITileLayer.Geometry</c>. The guard throws on a decoded feature, which would otherwise materialize
        /// to zero rings and read as "no geometry".
        /// </summary>
        internal static TileGeometryBuffers Materialize(
            IReadOnlyList<IFeature> features, TileId id, double extent)
        {
            int count = features?.Count ?? 0;
            var kinds    = new List<MapRenderer.Core.Tiles.TileGeometryType>(count);
            var commands = new List<uint[]>(count);
            for (int i = 0; i < count; i++)
            {
                IFeature f = features[i];
                if (f != null && !(f is ITileCommandStreamFeature))
                    throw new System.ArgumentException(
                        $"feature {i} is a {f.GetType().Name}, which carries no command stream. Since the decoded tile owns its geometry, " +
                        "geometry belongs to the LAYER: read ITileLayer.Geometry (or use the ITileLayer " +
                        "overload of BuildFill/BuildLine) instead of re-materializing a decoded feature list.",
                        nameof(features));
                kinds.Add(f?.GeometryType ?? MapRenderer.Core.Tiles.TileGeometryType.Unknown);
                commands.Add((f as ITileCommandStreamFeature)?.Geometry);
            }
            return MvtGeometryMaterializerTestFactory.Materialize(id, extent, kinds, commands);
        }

        /// <summary>
        /// "Visit every ring, in decode order" — the identity visit order, for a test that drives
        /// <c>FillMeshGraph.Schedule</c> directly and has no selection or sort key to express. Caller owns
        /// the returned array.
        /// </summary>
        internal static NativeArray<int> FullVisitOrder(TileGeometryBuffers geometry)
        {
            var order = new NativeArray<int>(
                geometry.IsCreated ? geometry.RingCount : 0,
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int r = 0; r < order.Length; r++) order[r] = r;
            return order;
        }

        /// <summary>Synchronous fill-layer mesh build; null when the layer produces no geometry.
        /// Non-local invariant: it routes through the REAL selection → rank → visit-order construction, because
        /// <c>FillSortKeyAndOpacityTests</c>, the only instrument that discriminates fill draw order, relies on
        /// it; a bypass would leave that fixture asserting nothing.</summary>
        public static Mesh BuildFill(
            IReadOnlyList<IFeature> features, Fill.PaintProperties paint,
            double zoom, double extent, TileId id, IProjection projection = null,
            Fill.LayoutProperties layout = null, // null ⇒ no fill-sort-key (declared feature order)
            // A parity oracle against the MapView path MUST pass the view's clip; the default builds the
            // reference arm under a DIFFERENT window.
            TileBufferClip clip = default,
            // null ⇒ the allocating path; a caller measuring the pooled path's steady-state GC footprint
            // passes its own TileBuildBuffers and reuses it across calls.
            MapRenderer.Unity.Rendering.Tile.Processing.TileBuildBuffers buffers = null,
            // Test-only oracle knob — see SyncMeshWrite.Fill. Production never sets it.
            bool suppressBoundaryBand = false)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            // The builder bakes relative to the tile's SW corner through the SAME projection, so derive the
            // origin from (id, projection) here; a caller-supplied Mercator origin would break a globe fixture.
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            TileGeometryBuffers geometry = Materialize(features, id, extent);
            try
            {
                SyncMeshWrite.Fill(mda[0], Selection(features), geometry, paint, zoom,
                    renderOrigin, out int vertexCount, out Bounds bounds, projection, layout, clip, buffers,
                    suppressBoundaryBand);
                return Finish(mda, vertexCount, bounds, "TestFill");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>Synchronous fill-extrusion-layer mesh build (roof + walls). Returns null when the
        /// layer produces no geometry. Mirrors <see cref="BuildFill"/>'s synthetic-feature shape (materialize
        /// the whole feature list, ordinals 0..n-1, written through <c>SyncMeshWrite</c>).</summary>
        public static Mesh BuildFillExtrusion(
            IReadOnlyList<IFeature> features, FillExtrusion.PaintProperties paint,
            double zoom, double extent, TileId id, IProjection projection = null,
            TileBufferClip clip = default)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            TileGeometryBuffers geometry = Materialize(features, id, extent);
            try
            {
                SyncMeshWrite.FillExtrusion(mda[0], Selection(features), geometry, paint, zoom,
                    renderOrigin, out int vertexCount, out Bounds bounds, projection, clip);
                return Finish(mda, vertexCount, bounds, "TestFillExtrusion");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>Synchronous line-layer mesh build. Returns null when the layer produces no geometry.
        /// <para>The feature list IS the source layer here, so ordinals are 0..n-1 and the buffer is
        /// materialized over all of it — the same "whole layer, identity selection" shape
        /// <see cref="BuildFill"/> already uses.</para></summary>
        public static Mesh BuildLine(
            IReadOnlyList<IFeature> features, Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, double2 origin)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            TileGeometryBuffers geometry = Materialize(features, id, extent);
            try
            {
                SyncMeshWrite.Line(mda[0], Selection(features), geometry, paint, layout, zoom,
                    new double3(origin.x, 0.0, origin.y), out int vertexCount, out Bounds bounds);
                return Finish(mda, vertexCount, bounds, "TestLine");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>Projection-aware line build: bakes relative to the tile's SW corner projected
        /// through <paramref name="projection"/> (the SAME origin the vertices use), so a globe fixture lays
        /// its lines on the sphere. Mirrors the projection-aware <see cref="BuildFill"/>.</summary>
        public static Mesh BuildLine(
            IReadOnlyList<IFeature> features, Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, IProjection projection)
        {
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            var mda = Mesh.AllocateWritableMeshData(1);
            TileGeometryBuffers geometry = Materialize(features, id, extent);
            try
            {
                SyncMeshWrite.Line(mda[0], Selection(features), geometry, paint, layout, zoom,
                    renderOrigin, out int vertexCount, out Bounds bounds, projection);
                return Finish(mda, vertexCount, bounds, "TestLineGlobe");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>
        /// A line build whose SOURCE LAYER is wider than this style layer's selection — the production
        /// shape. <paramref name="layerFeatures"/> is what the shared per-source-layer buffer covers;
        /// <paramref name="selection"/> is the <c>(ordinal, feature)</c> subset the style layer's filter
        /// admitted, so slot and ordinal genuinely differ.
        /// </summary>
        public static Mesh BuildLineFromLayer(
            IReadOnlyList<IFeature> layerFeatures, IReadOnlyList<SelectedTileFeature> selection,
            Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, double2 origin)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            TileGeometryBuffers geometry = Materialize(layerFeatures, id, extent);
            try
            {
                SyncMeshWrite.Line(mda[0], selection, geometry, paint, layout, zoom,
                    new double3(origin.x, 0.0, origin.y), out int vertexCount, out Bounds bounds);
                return Finish(mda, vertexCount, bounds, "TestLineLayer");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>Build a line layer's mesh and return only the written vertex count (0 = no geometry). Used
        /// by the data-driven-bake teeth that only need "did the bake produce geometry?".</summary>
        public static int LineVertexCount(
            IReadOnlyList<IFeature> features, Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, double2 origin)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            TileGeometryBuffers geometry = Materialize(features, id, extent);
            int vertexCount;
            try
            {
                SyncMeshWrite.Line(mda[0], Selection(features), geometry, paint, layout, zoom,
                    new double3(origin.x, 0.0, origin.y), out vertexCount, out _);
            }
            finally { geometry.Dispose(); }
            mda.Dispose(); // never applied — we only wanted the count
            return vertexCount;
        }

        // ── The DECODED-LAYER overloads ──────────────────────────────────────────────────────────────────
        // Non-local invariant: a decoded layer owns its buffer, so these BORROW it and never dispose it.
        // They take the SELECTION explicitly, because a style layer's filter may admit a strict subset of
        // the layer. They assert the buffer's tile matches the render-origin id, or the mesh lands in the
        // wrong place.

        private static void AssertLayerPairing(ITileLayer layer, TileId id)
        {
            TileGeometryBuffers geometry = layer.Geometry;
            if (!geometry.IsCreated) return;
            if (!geometry.Tile.Equals(id))
                throw new System.ArgumentException(
                    $"the layer's buffer belongs to tile {geometry.Tile.Z}/{geometry.Tile.X}/{geometry.Tile.Y} " +
                    $"but the build was asked for {id.Z}/{id.X}/{id.Y}. Decode the fixture with the SAME " +
                    "TileId the build uses (MvtDecoder.Decode(id, bytes)) — the buffer is the sole authority " +
                    "for its tile address.", nameof(id));
        }

        /// <summary>The ordinal-bearing selection for one style layer over one decoded layer — the production
        /// pairing (<c>TileMeshLayerProcessor</c> resolves the layer once, then selects against it).</summary>
        internal static List<SelectedTileFeature> Select(
            MapRenderer.Core.Style.StyleLayer styleLayer, ITileLayer tileLayer, double zoom)
        {
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(styleLayer, tileLayer, zoom, selected);
            return selected;
        }

        /// <summary>Fill build over a DECODED layer, borrowing its buffer.</summary>
        public static Mesh BuildFillFromLayer(
            ITileLayer layer, IReadOnlyList<SelectedTileFeature> selection, Fill.PaintProperties paint,
            double zoom, TileId id, IProjection projection = null,
            Fill.LayoutProperties layout = null, TileBufferClip clip = default,
            // Test-only oracle knob — see SyncMeshWrite.Fill. Production never sets it.
            bool suppressBoundaryBand = false)
        {
            AssertLayerPairing(layer, id);
            var mda = Mesh.AllocateWritableMeshData(1);
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            SyncMeshWrite.Fill(mda[0], selection, layer.Geometry, paint, zoom,
                renderOrigin, out int vertexCount, out Bounds bounds, projection, layout, clip,
                suppressBoundaryBand: suppressBoundaryBand);
            return Finish(mda, vertexCount, bounds, "TestFill");
        }

        /// <summary>Line build over a DECODED layer (flat/Mercator origin form), borrowing its buffer.</summary>
        public static Mesh BuildLineFromLayer(
            ITileLayer layer, IReadOnlyList<SelectedTileFeature> selection, Line.PaintProperties paint,
            Line.LayoutProperties layout, double zoom, TileId id, double2 origin)
        {
            AssertLayerPairing(layer, id);
            var mda = Mesh.AllocateWritableMeshData(1);
            SyncMeshWrite.Line(mda[0], selection, layer.Geometry, paint, layout,
                zoom, new double3(origin.x, 0.0, origin.y), out int vertexCount, out Bounds bounds);
            return Finish(mda, vertexCount, bounds, "TestLine");
        }

        /// <summary>Line build over a DECODED layer, projection-aware (globe), borrowing its buffer. A concrete
        /// struct literal (<c>new SphericalProjection()</c>) binds to the generic sibling
        /// <see cref="BuildLineFromLayer{TProj}"/> instead, because an exact-type match beats boxing. Both
        /// drive the SAME <c>ScheduleTyped</c> call, so only the route differs.</summary>
        public static Mesh BuildLineFromLayer(
            ITileLayer layer, IReadOnlyList<SelectedTileFeature> selection, Line.PaintProperties paint,
            Line.LayoutProperties layout, double zoom, TileId id, IProjection projection)
        {
            AssertLayerPairing(layer, id);
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            var mda = Mesh.AllocateWritableMeshData(1);
            SyncMeshWrite.Line(mda[0], selection, layer.Geometry, paint, layout,
                zoom, renderOrigin, out int vertexCount, out Bounds bounds, projection);
            return Finish(mda, vertexCount, bounds, "TestLineGlobe");
        }

        /// <summary>Line build over a DECODED layer for a projection Burst never registers generically: it
        /// calls <c>LineMeshGraph.ScheduleTyped&lt;TProj&gt;</c> directly (via the <c>InternalsVisibleTo</c>
        /// grant), not the closed <c>LineMeshGraph.Schedule</c> switch. It has no production caller, so it
        /// lives in the test assembly. A concrete struct argument binds here without an explicit type
        /// argument.</summary>
        public static Mesh BuildLineFromLayer<TProj>(
            ITileLayer layer, IReadOnlyList<SelectedTileFeature> selection, Line.PaintProperties paint,
            Line.LayoutProperties layout, double zoom, TileId id, TProj projection)
            where TProj : struct, IProjection
        {
            AssertLayerPairing(layer, id);
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);

            LayerInput input = StyledLineTileBuilder.BuildLayerInput(
                selection, layer.Geometry, paint, layout, zoom, renderOrigin,
                out NativeArray<Vector4> featureColors, out NativeArray<float> featureWidths, projection);

            var mda = Mesh.AllocateWritableMeshData(1);
            int    vertexCount = 0;
            Bounds bounds      = default;

            if (!input.FeatureSelected.IsCreated)
                return Finish(mda, vertexCount, bounds, "TestLineGlobeTyped"); // no line geometry

            using var featSelected = input.FeatureSelected;
            using var featColors   = featureColors;
            using var featWidths   = featureWidths;

            LineGraphOutput output = LineMeshGraph.ScheduleTyped(input, projection, default);
            output.Handle.Complete();
            try
            {
                if (output.IsCreated && output.Error.Value == LineGraphCounts.Ok
                    && output.Vertices.Length > 0 && output.Indices.Length > 0)
                {
                    (JobHandle handle, NativeArray<float3x2> boundsArr) =
                        StyledLineTileBuilder.ScheduleStreamWrite(mda[0], output, featColors, featWidths);
                    handle.Complete();
                    try
                    {
                        float3x2 b = boundsArr[0];
                        float3 c3 = (b.c0 + b.c1) * 0.5f;
                        float3 sz = b.c1 - b.c0;
                        bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
                        vertexCount = output.Vertices.Length;
                    }
                    finally { boundsArr.Dispose(); }
                }
            }
            finally { output.Dispose(); }

            return Finish(mda, vertexCount, bounds, "TestLineGlobeTyped");
        }

        private static Mesh Finish(Mesh.MeshDataArray mda, int vertexCount, Bounds bounds, string name)
        {
            if (vertexCount == 0) { mda.Dispose(); return null; }
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            Mesh.ApplyAndDisposeWritableMeshData(mda, mesh, NoValidate);
            mesh.bounds = bounds;
            return mesh;
        }
    }
}
