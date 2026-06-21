using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
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
    ///   → ProjectVerticesManaged (pure C#, off-main-thread safe) → MeshBuilder.AddFeature (with baked color)
    ///   → MeshBuilder.Build (linearizes vertex colors, returns Mesh or null).
    ///
    /// S47 async split:
    ///   <see cref="BuildMeshData"/> runs the full decode/assemble/earcut/project loop off the main
    ///   thread, returning a <see cref="LayerMeshData"/> payload (no UnityEngine.Object).
    ///   <see cref="UploadMesh"/> converts a <see cref="LayerMeshData"/> to a <see cref="Mesh"/> on the
    ///   main thread. <see cref="BuildMesh"/> is the sync convenience: BuildMeshData → UploadMesh.
    ///
    /// Color (D2): per-feature sRGB color baked via <see cref="DataDrivenPaintEvaluator"/>; stored as
    /// vertex color via <see cref="MeshBuilder"/> which calls <c>Color.linear</c> before
    /// <c>Mesh.SetColors</c>. <c>_MapColor=white</c> on the Material (identity multiply). Never set
    /// <c>_MapColor</c> to the style color — that would double-apply gamma.
    ///
    /// Thread-safety: <see cref="BuildMeshData"/> touches only pure-managed, stateless Core code
    /// (MvtGeometry.Decode, PolygonAssembler.Assemble, Earcut.Triangulate, DataDrivenPaintEvaluator,
    /// MvtFeatureAdapter). All are allocation-local with no shared mutable static state. Safe to run
    /// concurrently on multiple ThreadPool threads (one per tile).
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class StyledFillTileBuilder
    {
        // MapRenderer.Tile.Tessellate — wraps the decode/assemble/earcut/project loop per BuildMesh call.
        // S46 acceptance: proved-wired target for the profiler recorder test (tooth 1b).
        // S47: this marker now fires on a background ThreadPool thread when called from BuildMeshData
        // inside Task.Run. The ProfilerMarkerTests [UnityTest] (tooth 1b) has been updated to use
        // ProfilerRecorderOptions.Default (not CollectOnlyOnCurrentThread) so cross-thread samples are captured.
        private static readonly ProfilerMarker PmTessellate = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.Tessellate");

        // ── Intermediate data payload (engine-free: no UnityEngine.Object) ─────────────────────────

        /// <summary>
        /// Per-feature vertex payload produced by <see cref="BuildMeshData"/> and consumed by
        /// <see cref="UploadMesh"/>. No UnityEngine types — safe to allocate and pass across threads.
        /// </summary>
        public struct FeatureMeshData
        {
            /// <summary>Origin-relative world-space positions (east=+X, height=+Y, north=+Z).</summary>
            public float3[] Verts;
            /// <summary>Earcut triangle indices (into Verts).</summary>
            public int[] Indices;
            /// <summary>Pre-projection tile-space double2 coordinates (for UV generation).</summary>
            public double2[] TileVerts;
            /// <summary>Tile extent in tile units (from MVT layer, typically 4096).</summary>
            public double Extent;
            /// <summary>Per-feature sRGB vertex color (to be linearized by MeshBuilder.Build).</summary>
            public Color FeatureColor;
        }

        /// <summary>
        /// CPU-computed mesh data for one style layer (a list of per-feature payloads).
        /// Produced by <see cref="BuildMeshData"/> off the main thread; consumed by
        /// <see cref="UploadMesh"/> on the main thread.
        /// </summary>
        public struct LayerMeshData
        {
            /// <summary>Per-feature data (may be empty if no polygon geometry was produced).</summary>
            public List<FeatureMeshData> Features;
        }

        // ── Public API ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// CPU-only half: decode/assemble/earcut/project all features for one style layer, producing
        /// a <see cref="LayerMeshData"/> payload that contains no Unity engine objects.
        ///
        /// Safe to call from a <see cref="System.Threading.Tasks.Task"/> (ThreadPool thread):
        /// all code paths use only pure-managed, stateless Core logic (no NativeArray, no Unity.Object).
        ///
        /// The <see cref="PmTessellate"/> profiler marker wraps this call. In the async path (S47),
        /// this fires on a background thread — the ProfilerMarkerTests tooth-1b recorder must use
        /// <see cref="ProfilerRecorderOptions.Default"/> (not CollectOnlyOnCurrentThread).
        /// </summary>
        public static LayerMeshData BuildMeshData(
            IReadOnlyList<MvtFeature> selectedFeatures,
            FillPaint paint,
            double zoom,
            double extent,
            TileId id,
            double2 tileOriginMerc)
        {
            var result = new LayerMeshData { Features = new List<FeatureMeshData>() };
            if (selectedFeatures == null || selectedFeatures.Count == 0)
                return result;

            double originX = tileOriginMerc.x;
            double originY = tileOriginMerc.y;

            // MapRenderer.Tile.Tessellate — wraps the full decode/assemble/earcut/project loop.
            // S46 acceptance: proves-wired marker for profiler recorder test (tooth 1b).
            // S47: fires on a background thread when called inside Task.Run from MapView.
            using var sTessellate = PmTessellate.Auto();

            foreach (var feature in selectedFeatures)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon)
                    continue;

                // Bake per-feature vertex color from the data-driven paint expression.
                // Color space (D2): Core Color is sRGB [0,1]; MeshBuilder.Build() calls Color.linear.
                // _MapColor=white on material → identity multiply → no double-gamma.
                Color featureColor = Color.white;
                var adapter = new MvtFeatureAdapter(feature);
                if (paint.DataDrivenColor.TryEvaluateColor(zoom, adapter, out CoreColor c))
                    featureColor = new Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);

                // Decode rings, assemble polygons, earcut, project.
                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
                if (rings == null || rings.Count == 0) continue;

                List<Polygon> polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    Earcut.Result earcutResult = Earcut.Triangulate(polygon.Outer, polygon.Holes);
                    if (earcutResult.Indices == null || earcutResult.Indices.Length == 0) continue;

                    double2[] flatVerts = earcutResult.Vertices;
                    int[] triIndices    = earcutResult.Indices;
                    int vertCount       = flatVerts.Length;

                    // Project via pure managed math (off-main-thread safe; no NativeArray).
                    // Formula mirrors ProjectTileVerticesJob.Execute (same precision contract).
                    float3[] worldPos = ProjectVerticesManaged(flatVerts, id.Z, id.X, id.Y, extent,
                        originX, originY);

                    result.Features.Add(new FeatureMeshData
                    {
                        Verts        = worldPos,
                        Indices      = triIndices,
                        TileVerts    = flatVerts,
                        Extent       = extent,
                        FeatureColor = featureColor,
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Main-thread half: uploads a <see cref="LayerMeshData"/> produced by
        /// <see cref="BuildMeshData"/> into a <see cref="Mesh"/> via <see cref="MeshBuilder"/>.
        ///
        /// Must be called on the Unity main thread (MeshBuilder.Build creates a UnityEngine.Mesh).
        /// Returns null when <paramref name="data"/> contains no polygon geometry.
        /// </summary>
        public static Mesh UploadMesh(LayerMeshData data)
        {
            if (data.Features == null || data.Features.Count == 0)
                return null;

            var meshBuilder = new MeshBuilder();
            foreach (var f in data.Features)
                meshBuilder.AddFeature(f.Verts, f.Indices, f.TileVerts, f.Extent, f.FeatureColor);

            return meshBuilder.Build(); // null when no geometry was added
        }

        /// <summary>
        /// Synchronous convenience: <see cref="BuildMeshData"/> then <see cref="UploadMesh"/>.
        /// Used by tests that call the builder directly and by the old single-threaded code path.
        /// Must be called on the Unity main thread (UploadMesh → MeshBuilder.Build creates a Mesh).
        /// </summary>
        public static Mesh BuildMesh(
            IReadOnlyList<MvtFeature> selectedFeatures,
            FillPaint paint,
            double zoom,
            double extent,
            TileId id,
            double2 tileOriginMerc)
        {
            LayerMeshData data = BuildMeshData(selectedFeatures, paint, zoom, extent, id, tileOriginMerc);
            return UploadMesh(data);
        }

        // ── Internal: managed projection (mirrors ProjectTileVerticesJob.Execute) ─────────────────

        /// <summary>
        /// Pure C# projection: tile-space double2 → origin-relative float3 world positions.
        /// Formula is bit-identical to <see cref="MapRenderer.Jobs.ProjectTileVerticesJob.Execute"/>
        /// (same double-precision intermediates, same subtract-then-cast pattern).
        /// Safe to call from any thread (no Unity APIs, no NativeArray).
        /// </summary>
        private static float3[] ProjectVerticesManaged(
            double2[] tileCoords,
            int tileZ, int tileX, int tileY,
            double extent,
            double originMercX, double originMercY)
        {
            const double R = 6378137.0; // Earth radius (Web Mercator / EPSG:3857)
            const double TwoPi = 2.0 * Math.PI;

            int n = tileCoords.Length;
            var result = new float3[n];

            double pow2z = Math.Pow(2.0, tileZ);

            for (int i = 0; i < n; i++)
            {
                double px = tileCoords[i].x;
                double py = tileCoords[i].y;

                // Tile → normalised [0,1] map coordinates
                double u = (tileX + px / extent) / pow2z;
                double v = (tileY + py / extent) / pow2z;

                // Normalised → lon/lat (radians)
                double lonRad = u * TwoPi - Math.PI;
                double arg     = Math.PI * (1.0 - 2.0 * v);
                double sinhArg = (Math.Exp(arg) - Math.Exp(-arg)) * 0.5;
                double latRad  = Math.Atan(sinhArg);

                // lon/lat → Web Mercator meters (spherical Mercator, R = semi-major axis)
                double mercX = R * lonRad;
                double halfLat = latRad * 0.5;
                double tanArg  = Math.Tan(Math.PI * 0.25 + halfLat);
                double mercY   = R * Math.Log(tanArg);

                // Subtract origin in double, then cast to float — RTC precision
                double dx = mercX - originMercX;
                double dz = mercY - originMercY;

                result[i] = new float3((float)dx, 0f, (float)dz);
            }

            return result;
        }
    }
}
