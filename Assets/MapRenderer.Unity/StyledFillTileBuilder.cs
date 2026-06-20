using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Jobs;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S40 managed per-layer fill mesh builder. Extracted from <see cref="MapFillBootstrap.BuildMesh"/>
    /// and generalized to receive real <see cref="TileId"/> + origin, a set of pre-selected features,
    /// and a <see cref="FillPaint"/> describing the style.
    ///
    /// Pipeline per feature:
    ///   MvtGeometry.Decode → PolygonAssembler.Assemble → Earcut.Triangulate
    ///   → ProjectTileVerticesJob (Burst, TempJob) → MeshBuilder.AddFeature (with baked color)
    ///   → MeshBuilder.Build (linearizes vertex colors, returns Mesh or null).
    ///
    /// Color (D2): per-feature sRGB color baked via <see cref="DataDrivenPaintEvaluator"/>; stored as
    /// vertex color via <see cref="MeshBuilder"/> which calls <c>Color.linear</c> before
    /// <c>Mesh.SetColors</c>. <c>_MapColor=white</c> on the Material (identity multiply). Never set
    /// <c>_MapColor</c> to the style color — that would double-apply gamma.
    ///
    /// Engine-free seam: this class is Unity-dependent (Burst NativeArray, UnityEngine.Mesh). Core logic
    /// stays in MapRenderer.Core (geometry, earcut, style, filters). Runs on the main thread (Burst job
    /// completes synchronously).
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class StyledFillTileBuilder
    {
        /// <summary>
        /// Build a Unity Mesh from the polygon features that pass <paramref name="selectedFeatures"/>
        /// (already filtered by <see cref="FeatureSelector"/>), projected into origin-relative float3
        /// space, with per-feature vertex colors baked from <paramref name="paint"/>.
        ///
        /// Returns null when no polygon geometry is produced (no features, all degenerate, etc.).
        /// </summary>
        /// <param name="selectedFeatures">Pre-selected polygon features (from FeatureSelector or empty).</param>
        /// <param name="paint">Parsed fill-paint for this style layer.</param>
        /// <param name="zoom">Current map zoom (used to evaluate data-driven/zoom expressions).</param>
        /// <param name="extent">Tile extent in tile units (from the MVT layer, typically 4096).</param>
        /// <param name="id">Tile identifier used by the Burst projection job.</param>
        /// <param name="tileOriginMerc">Mercator min-corner of this tile (from FloatingOrigin.TileLocalOriginMercator).</param>
        public static Mesh BuildMesh(
            IReadOnlyList<MvtFeature> selectedFeatures,
            FillPaint paint,
            double zoom,
            double extent,
            TileId id,
            double2 tileOriginMerc)
        {
            if (selectedFeatures == null || selectedFeatures.Count == 0)
                return null;

            double originX = tileOriginMerc.x;
            double originY = tileOriginMerc.y;
            var meshBuilder = new MeshBuilder();

            foreach (var feature in selectedFeatures)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon)
                    continue;

                // Bake per-feature vertex color from the data-driven paint expression.
                // Use DataDrivenColor (which handles ALL expression kinds including Constant/Zoom).
                // Color space (D2): Core Color is sRGB [0,1]; MeshBuilder.Build() calls Color.linear.
                // _MapColor=white on material → identity multiply → no double-gamma.
                UnityEngine.Color featureColor = UnityEngine.Color.white;
                var adapter = new MvtFeatureAdapter(feature);
                if (paint.DataDrivenColor.TryEvaluateColor(zoom, adapter, out CoreColor c))
                    featureColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);

                // Decode rings, assemble polygons, earcut, project.
                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
                if (rings == null || rings.Count == 0) continue;

                List<Polygon> polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    Earcut.Result earcutResult = Earcut.Triangulate(polygon.Outer, polygon.Holes);
                    if (earcutResult.Indices == null || earcutResult.Indices.Length == 0) continue;

                    double2[] flatVerts = earcutResult.Vertices;
                    int[] triIndices = earcutResult.Indices;
                    int vertCount = flatVerts.Length;

                    // Project via Burst: tile-space double2 → origin-relative float3 (TempJob).
                    var tileCoords = new NativeArray<double2>(vertCount, Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory);
                    var worldPos = new NativeArray<float3>(vertCount, Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory);

                    for (int i = 0; i < vertCount; i++)
                        tileCoords[i] = flatVerts[i];

                    var job = new ProjectTileVerticesJob
                    {
                        TileZ        = id.Z,
                        TileX        = id.X,
                        TileY        = id.Y,
                        Extent       = extent,
                        OriginMercX  = originX,
                        OriginMercY  = originY,
                        TileCoords   = tileCoords,
                        WorldPositions = worldPos,
                    };
                    job.Schedule(vertCount, 64).Complete();

                    var verts = new float3[vertCount];
                    for (int i = 0; i < vertCount; i++)
                        verts[i] = worldPos[i];

                    tileCoords.Dispose();
                    worldPos.Dispose();

                    meshBuilder.AddFeature(verts, triIndices, flatVerts, extent, featureColor);
                }
            }

            return meshBuilder.Build(); // null when no geometry was added
        }
    }
}
