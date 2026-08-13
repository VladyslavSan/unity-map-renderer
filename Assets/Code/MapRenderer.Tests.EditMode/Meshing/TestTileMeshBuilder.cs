// S89 Stage B test helper: synchronous fill/line mesh build for tests that used the retired
// StyledFill/LineTileBuilder.BuildMesh / BuildMeshData+UploadMesh convenience. Allocates a writable
// MeshDataArray on the (test) main thread, builds the mesh via WriteMeshData, and applies to a Mesh — the same
// worker-write path the production pipeline uses at kick+consume, collapsed to one synchronous call.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Jobs;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests
{
    internal static class TestTileMeshBuilder
    {
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        /// <summary>
        /// IR B7: the production selection shape, for a test that hands over a plain feature list. Ordinals are
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
        /// IR B7: the shared buffer for a test that hands over a plain list of SYNTHETIC features —
        /// materialized from ALL of them (not just the polygons), which is what a whole source layer holds.
        /// Caller owns it.
        ///
        /// <para><b>IR C1 P3: synthetic features only.</b> A real decoded <c>MvtFeature</c> no longer carries
        /// a command stream — its layer owns the geometry — so a caller holding a decoded layer must read
        /// <c>ITileLayer.Geometry</c> (see the <c>ITileLayer</c> overloads of the builders below) instead of
        /// re-materializing here. The guard below makes that mistake LOUD: without it a decoded feature list
        /// would materialize to zero rings and the test would report "no geometry" rather than "you used the
        /// wrong seam".</para>
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
                        $"feature {i} is a {f.GetType().Name}, which carries no command stream. Since IR C1 P3 " +
                        "geometry belongs to the LAYER: read ITileLayer.Geometry (or use the ITileLayer " +
                        "overload of BuildFill/BuildLine) instead of re-materializing a decoded feature list.",
                        nameof(features));
                kinds.Add(f?.GeometryType ?? MapRenderer.Core.Tiles.TileGeometryType.Unknown);
                commands.Add((f as ITileCommandStreamFeature)?.Geometry);
            }
            return new MvtGeometryMaterializer(id, extent, kinds, commands).Materialize();
        }

        /// <summary>
        /// IR B7: "visit every ring, in decode order" — the identity visit order, for a test that drives
        /// <c>FillMeshPipeline.Schedule</c> directly and has no selection or sort key to express. Caller owns
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

        /// <summary>Synchronous fill-layer mesh build. Returns null when the layer produces no geometry.
        /// Pass <paramref name="projection"/> to build with a non-default projection (e.g. the globe).
        /// <para>IR B7: this is the byte-identity harness for fill's ring visit order — it routes through the
        /// REAL selection → rank → visit-order construction (materialize the whole feature list, ordinals
        /// 0..n-1, production <c>WriteMeshData</c>). A "simplification" that bypassed that path would leave
        /// <c>FillSortKeyAndOpacityTests</c> — the only instrument in the repo that discriminates fill draw
        /// order at all — asserting nothing.</para></summary>
        public static Mesh BuildFill(
            IReadOnlyList<IFeature> features, Fill.PaintProperties paint,
            double zoom, double extent, TileId id, IProjection projection = null,
            Fill.LayoutProperties layout = null, // null ⇒ no fill-sort-key (declared feature order)
            // A parity oracle comparing this against the MapView path MUST pass the same clip the view is
            // configured with; leaving it default builds the reference arm under a DIFFERENT window, and the
            // comparison silently stops being one.
            TileBufferClip clip = default)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            // S91-C: the builder bakes relative to the tile's SW corner projected through the SAME projection —
            // Mercator: (mercX, 0, mercZ) == MercatorBounds().min; globe: the ECEF corner. Derive it from
            // (id, projection) here (NOT a caller-supplied Mercator origin) so a globe fixture bakes correctly.
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            TileGeometryBuffers geometry = Materialize(features, id, extent);
            try
            {
                StyledFillTileBuilder.WriteMeshData(mda[0], Selection(features), geometry, paint, zoom,
                    renderOrigin, out int vertexCount, out Bounds bounds, projection, layout, clip);
                return Finish(mda, vertexCount, bounds, "TestFill");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>Synchronous line-layer mesh build. Returns null when the layer produces no geometry.
        /// <para>IR C1 P2: the feature list IS the source layer here, so ordinals are 0..n-1 and the buffer is
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
                StyledLineTileBuilder.WriteMeshData(mda[0], Selection(features), geometry, paint, layout, zoom,
                    new double3(origin.x, 0.0, origin.y), out int vertexCount, out Bounds bounds);
                return Finish(mda, vertexCount, bounds, "TestLine");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>Projection-aware line build (S91-C): bakes relative to the tile's SW corner projected
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
                StyledLineTileBuilder.WriteMeshData(mda[0], Selection(features), geometry, paint, layout, zoom,
                    renderOrigin, out int vertexCount, out Bounds bounds, projection);
                return Finish(mda, vertexCount, bounds, "TestLineGlobe");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>
        /// IR C1 P2: a line build whose SOURCE LAYER is wider than this style layer's selection — the
        /// production shape. <paramref name="layerFeatures"/> is what the shared per-source-layer buffer
        /// covers; <paramref name="selection"/> is the <c>(ordinal, feature)</c> subset the style layer's
        /// filter admitted, so slot and ordinal genuinely differ.
        /// <para>The signature is deliberately stable across P2's conversion: before it, the builder mints
        /// from <paramref name="selection"/> and <paramref name="layerFeatures"/> only names what the layer
        /// holds; after it, the layer's buffer is materialized here and lent to the builder. A tooth written
        /// against this helper therefore measures the pre-conversion output and re-measures the
        /// post-conversion one.</para>
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
                StyledLineTileBuilder.WriteMeshData(mda[0], selection, geometry, paint, layout, zoom,
                    new double3(origin.x, 0.0, origin.y), out int vertexCount, out Bounds bounds);
                return Finish(mda, vertexCount, bounds, "TestLineLayer");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>Build a line layer's mesh and return only the written vertex count (0 = no geometry). Used
        /// by the S14 data-driven-bake teeth that only need "did the bake produce geometry?".</summary>
        public static int LineVertexCount(
            IReadOnlyList<IFeature> features, Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, double2 origin)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            TileGeometryBuffers geometry = Materialize(features, id, extent);
            int vertexCount;
            try
            {
                StyledLineTileBuilder.WriteMeshData(mda[0], Selection(features), geometry, paint, layout, zoom,
                    new double3(origin.x, 0.0, origin.y), out vertexCount, out _);
            }
            finally { geometry.Dispose(); }
            mda.Dispose(); // never applied — we only wanted the count
            return vertexCount;
        }

        // ── IR C1 P3: the DECODED-LAYER overloads ────────────────────────────────────────────────────────
        // A decoded layer owns its buffer, so these BORROW it (never dispose) and take neither an extent nor
        // a materialization step. They also take the SELECTION explicitly, because the buffer spans the whole
        // layer while a style layer's filter may admit a strict subset — the exact pairing production makes.
        // And they assert the buffer's own tile address matches the id the caller bakes the render origin
        // from: those two disagreeing is precisely the mispairing C1 exists to make impossible, and a harness
        // that quietly allowed it would build a mesh at the wrong place.

        private static void AssertLayerPairing(ITileLayer layer, TileId id)
        {
            TileGeometryBuffers geometry = layer.Geometry;
            if (!geometry.IsCreated) return;
            if (!geometry.Tile.Equals(id))
                throw new System.ArgumentException(
                    $"the layer's buffer belongs to tile {geometry.Tile.Z}/{geometry.Tile.X}/{geometry.Tile.Y} " +
                    $"but the build was asked for {id.Z}/{id.X}/{id.Y}. Decode the fixture with the SAME " +
                    "TileId the build uses (MvtDecoder.Decode(id, bytes)) — the buffer is the sole authority " +
                    "for its tile address since IR C1 P3.", nameof(id));
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
            Fill.LayoutProperties layout = null, TileBufferClip clip = default)
        {
            AssertLayerPairing(layer, id);
            var mda = Mesh.AllocateWritableMeshData(1);
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            StyledFillTileBuilder.WriteMeshData(mda[0], selection, layer.Geometry, paint, zoom,
                renderOrigin, out int vertexCount, out Bounds bounds, projection, layout, clip);
            return Finish(mda, vertexCount, bounds, "TestFill");
        }

        /// <summary>Line build over a DECODED layer (flat/Mercator origin form), borrowing its buffer.</summary>
        public static Mesh BuildLineFromLayer(
            ITileLayer layer, IReadOnlyList<SelectedTileFeature> selection, Line.PaintProperties paint,
            Line.LayoutProperties layout, double zoom, TileId id, double2 origin)
        {
            AssertLayerPairing(layer, id);
            var mda = Mesh.AllocateWritableMeshData(1);
            StyledLineTileBuilder.WriteMeshData(mda[0], selection, layer.Geometry, paint, layout,
                zoom, new double3(origin.x, 0.0, origin.y), out int vertexCount, out Bounds bounds);
            return Finish(mda, vertexCount, bounds, "TestLine");
        }

        /// <summary>Line build over a DECODED layer, projection-aware (globe), borrowing its buffer.</summary>
        public static Mesh BuildLineFromLayer(
            ITileLayer layer, IReadOnlyList<SelectedTileFeature> selection, Line.PaintProperties paint,
            Line.LayoutProperties layout, double zoom, TileId id, IProjection projection)
        {
            AssertLayerPairing(layer, id);
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            var mda = Mesh.AllocateWritableMeshData(1);
            StyledLineTileBuilder.WriteMeshData(mda[0], selection, layer.Geometry, paint, layout,
                zoom, renderOrigin, out int vertexCount, out Bounds bounds, projection);
            return Finish(mda, vertexCount, bounds, "TestLineGlobe");
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
