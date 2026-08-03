// S89 Stage B test helper: synchronous fill/line mesh build for tests that used the retired
// StyledFill/LineTileBuilder.BuildMesh / BuildMeshData+UploadMesh convenience. Allocates a writable
// MeshDataArray on the (test) main thread, builds the mesh via WriteMeshData, and applies to a Mesh — the same
// worker-write path the production pipeline uses at kick+consume, collapsed to one synchronous call.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Jobs;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests
{
    internal static class TestTileMeshBuilder
    {
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        /// <summary>Synchronous fill-layer mesh build. Returns null when the layer produces no geometry.
        /// Pass <paramref name="projection"/> to build with a non-default projection (e.g. the globe).</summary>
        public static Mesh BuildFill(
            IReadOnlyList<ITileFeature> features, Fill.PaintProperties paint,
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
            StyledFillTileBuilder.WriteMeshData(mda[0], features, paint, zoom, extent, id,
                renderOrigin, out int vertexCount, out Bounds bounds, projection, layout, clip);
            return Finish(mda, vertexCount, bounds, "TestFill");
        }

        /// <summary>Synchronous line-layer mesh build. Returns null when the layer produces no geometry.</summary>
        public static Mesh BuildLine(
            IReadOnlyList<ITileFeature> features, Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, double2 origin)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            StyledLineTileBuilder.WriteMeshData(mda[0], features, paint, layout, zoom, extent, id,
                new double3(origin.x, 0.0, origin.y), out int vertexCount, out Bounds bounds);
            return Finish(mda, vertexCount, bounds, "TestLine");
        }

        /// <summary>Projection-aware line build (S91-C): bakes relative to the tile's SW corner projected
        /// through <paramref name="projection"/> (the SAME origin the vertices use), so a globe fixture lays
        /// its lines on the sphere. Mirrors the projection-aware <see cref="BuildFill"/>.</summary>
        public static Mesh BuildLine(
            IReadOnlyList<ITileFeature> features, Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, IProjection projection)
        {
            double3 renderOrigin = TileRenderOrigin.Project(id, projection);
            var mda = Mesh.AllocateWritableMeshData(1);
            StyledLineTileBuilder.WriteMeshData(mda[0], features, paint, layout, zoom, extent, id,
                renderOrigin, out int vertexCount, out Bounds bounds, projection);
            return Finish(mda, vertexCount, bounds, "TestLineGlobe");
        }

        /// <summary>Build a line layer's mesh and return only the written vertex count (0 = no geometry). Used
        /// by the S14 data-driven-bake teeth that only need "did the bake produce geometry?".</summary>
        public static int LineVertexCount(
            IReadOnlyList<ITileFeature> features, Line.PaintProperties paint, Line.LayoutProperties layout,
            double zoom, double extent, TileId id, double2 origin)
        {
            var mda = Mesh.AllocateWritableMeshData(1);
            StyledLineTileBuilder.WriteMeshData(mda[0], features, paint, layout, zoom, extent, id,
                new double3(origin.x, 0.0, origin.y), out int vertexCount, out _);
            mda.Dispose(); // never applied — we only wanted the count
            return vertexCount;
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
