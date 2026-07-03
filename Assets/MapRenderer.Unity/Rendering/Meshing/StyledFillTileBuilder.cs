using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Jobs;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// S40 managed per-layer fill mesh builder. Receives real <see cref="TileId"/> + origin, a set of
    /// pre-selected features, and a <see cref="Fill.PaintProperties"/> describing the style.
    ///
    /// Pipeline (S89 D2): managed color eval → Burst geometry via <c>TileTessellationPipeline</c>
    ///   (decode → assemble → earcut → project, run on this worker via <c>.Run()</c> into NativeArrays) →
    ///   managed alloc-free stream write into a <c>Mesh.MeshData</c>. The managed Core geometry
    ///   (<c>Earcut</c>/<c>PolygonAssembler</c>) is retired from this path (differential oracle only).
    ///
    /// S89 Stage B — <see cref="WriteMeshData"/> tessellates AND writes directly into a caller-allocated
    /// <see cref="Mesh.MeshData"/> (the writable-mesh advanced API), off the main thread. The bespoke
    /// NativeArray-stream payload + main-thread <c>SetVertexBufferData</c> copy is gone: the worker populates
    /// the mesh buffers in place, and the main thread only allocates (at kick) and applies (at consume). The
    /// off-thread-write threading contract is guarded by <c>MeshDataThreadWriteSpikeTests</c>.
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3) interleaved via <see cref="FillPositionNormal"/>.
    ///   Stream 1 — TexCoord0 UV (Float32x2).
    ///   Stream 2 — Tangent (Float32x4).
    ///   Stream 3 — Color (Float32x4, linearized sRGB).
    ///   Index buffer — UInt32.
    ///
    /// Color (D2): per-feature sRGB color baked via <see cref="StyleProperty{T}"/>; converted to linear via
    /// <c>Color.linear</c> off the main thread. <c>_BaseColor=white</c> on the Material (identity multiply).
    /// Never set <c>_BaseColor</c> to the style color — that would double-apply gamma.
    ///
    /// Thread-safety: <see cref="WriteMeshData"/> touches only pure-managed, stateless Core code plus a
    /// caller-allocated <c>Mesh.MeshData</c> (whose <c>SetVertexBufferParams</c>/<c>GetVertexData</c>/… are
    /// off-main-thread safe). No shared mutable static state — safe to run concurrently per tile.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class StyledFillTileBuilder
    {
        // MapRenderer.Tile.Tessellate — wraps the decode/assemble/earcut/project/write loop. Fires on a
        // background ThreadPool thread (S47/S51); ProfilerMarkerTests tooth-1b uses ProfilerRecorderOptions.Default
        // (not CollectOnlyOnCurrentThread) so cross-thread samples are captured.
        private static readonly ProfilerMarker PmTessellate =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.Tessellate");

        // Skip Unity's main-thread index validation (O(indices)) + the redundant intermediate bounds compute:
        // indices come from earcut and are covered by tests, and the canonical bounds are the worker-computed
        // AABB assigned to Mesh.bounds after apply. Preserves the S48/S55 no-main-thread-scan contract.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // Hoisted fill vertex attribute descriptor array (static readonly — no per-tile alloc). 4 streams
        // (Unity max). Canonical ascending VertexAttribute enum order (Position=0, Normal=1, Tangent=2,
        // Color=3, TexCoord0=4) eliminates the "non-standard order" warning; each attribute on its own stream
        // so the reorder does not change any stream's byte offset.
        private static readonly VertexAttributeDescriptor[] FillVertexDescriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1),
        };

        // Constant tangent: +X direction, +1 bitangent sign (right-handed), valid for flat +Y-normal fill.
        // S91-C bakes the projection's east into this stream for the globe; constant +X is Mercator-correct.
        private static readonly Vector4 FlatTangent = new Vector4(1f, 0f, 0f, 1f);

        // S91-A: the projection the geometry is built with. Launch-time config (Bootstrapper) threads a
        // chosen projection here in S91-C; until then this single seam defaults to WebMercator. The
        // pipeline projects with the chosen Projection struct — WebMercator.Forward is hardcoded nowhere.
        private static readonly IProjection DefaultProjection = new WebMercatorProjection();

        /// <summary>
        /// Tightly-packed Position + Normal struct for stream 0.
        /// Stride = 6 × 4 = 24 bytes, matching the descriptor (Position float3 + Normal float3).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FillPositionNormal
        {
            public Vector3 Position;
            public Vector3 Normal;
        }

        // ── Public API ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tessellate all fill features for one style layer and write the geometry directly into
        /// <paramref name="md"/> (a caller-allocated <c>Mesh.MeshData</c>, count-1 slot). Runs the full
        /// decode/assemble/earcut/project loop plus the sRGB→linear color conversion off the main thread.
        ///
        /// <para>Returns <paramref name="vertexCount"/> = 0 (and leaves <paramref name="md"/> untouched) when
        /// no polygon geometry is produced — the caller then disposes the unused writable-mesh-data without
        /// creating a Mesh. On success, <paramref name="bounds"/> carries the worker-computed tight AABB
        /// (assigned to <c>Mesh.bounds</c> after apply, avoiding a main-thread RecalculateBounds scan).</para>
        /// </summary>
        public static void WriteMeshData(
            Mesh.MeshData             md,
            IReadOnlyList<MvtFeature> selectedFeatures,
            Fill.PaintProperties      paint,
            double                    zoom,
            double                    extent,
            TileId                    id,
            double3                   tileOriginRender,
            out int                   vertexCount,
            out Bounds                bounds,
            IProjection               projection = null) // null ⇒ WebMercator (launch-time config threads this in)
        {
            vertexCount = 0;
            bounds      = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0)
                return;

            using var sTessellate = PmTessellate.Auto();

            // Phase 1 (Burst): collect this layer's polygon features + their per-feature linear color, then
            // run the Burst decode→assemble→earcut→project chain via TileTessellationPipeline (Run(), so it
            // works on this worker thread). The managed List<>/array tessellation garbage is gone — geometry
            // lives in NativeArrays. Color stays managed (paint.Color is an expression over string keys).
            var geoms         = new List<uint[]>(selectedFeatures.Count);
            var featureColors = new List<Vector4>(selectedFeatures.Count); // linearized sRGB, parallel to geoms
            foreach (var feature in selectedFeatures)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon || feature.Geometry == null)
                    continue;

                Color featureColor = Color.white;
                var   adapter      = new MvtFeatureAdapter(feature);
                if (paint.Color.TryEvaluate(zoom, adapter, out var color))
                    featureColor = new Color((float)color.R, (float)color.G, (float)color.B, (float)color.A);

                // S13 D2 gamma fix (off main thread): sRGB→linear here. white.linear == white.
                Color lin = featureColor.linear;
                geoms.Add(feature.Geometry);
                featureColors.Add(new Vector4(lin.r, lin.g, lin.b, lin.a));
            }

            if (geoms.Count == 0)
                return; // no polygon geometry — md left untouched; caller disposes the unused MeshData

            TileMeshBuffers buffers = TileTessellationPipeline.Schedule(new TileTessellationPipeline.LayerInput
            {
                FeatureGeometries = geoms,
                Extent            = extent,
                TileZ             = id.Z, TileX = id.X, TileY = id.Y,
                OriginRender      = tileOriginRender,
                Projection        = projection ?? DefaultProjection,
            });

            try
            {
                if (!buffers.IsCreated)
                    return;

                int totalVerts   = buffers.VertexCount[0];
                int totalIndices = buffers.TotalIndexCount;
                if (totalVerts == 0 || totalIndices == 0)
                    return;

                // The globe curves: earcut's flat triangles chord THROUGH the sphere (fills sink / facet at low
                // zoom), so refine them (C-3) and write the subdivided geometry — which also carries the correct
                // per-vertex east Tangent. The flat Mercator path below stays byte-identical.
                IProjection proj = projection ?? DefaultProjection;
                if (proj.ReversesWinding)
                {
                    WriteGlobeSubdivided(md, in buffers, proj, id, extent, tileOriginRender, featureColors,
                        out vertexCount, out bounds);
                    return; // finally still disposes buffers
                }

                // Phase 2: declare the mesh buffers on the MeshData (off-main-thread safe) and grab stream views.
                md.SetVertexBufferParams(totalVerts, FillVertexDescriptors);
                NativeArray<FillPositionNormal> s0 = md.GetVertexData<FillPositionNormal>(0);
                NativeArray<Vector2>            s1 = md.GetVertexData<Vector2>(1);
                NativeArray<Vector4>            s2 = md.GetVertexData<Vector4>(2);
                NativeArray<Vector4>            s3 = md.GetVertexData<Vector4>(3);
                md.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);
                NativeArray<int> indices = md.GetIndexData<int>();

                // Phase 3: copy the Burst geometry into the stream views, accumulating the tight AABB.
                double extentInv = extent > 0.0 ? 1.0 / extent : 0.0;
                float3 bMin = new float3(float.MaxValue);
                float3 bMax = new float3(float.MinValue);
                for (int i = 0; i < totalVerts; i++)
                {
                    // double3 origin-relative → float3 only here at mesh-write (docs §8.2).
                    float3 v = (float3)buffers.WorldPositions[i];
                    bMin = math.min(bMin, v);
                    bMax = math.max(bMax, v);
                    // Normal = the projection-baked surface up (S91-A). Constant +Y for Mercator (identical
                    // to the old hardcoded Vector3.up); the geodetic normal on the globe (S91-C).
                    float3 up = (float3)buffers.VertexUp[i];
                    s0[i] = new FillPositionNormal
                    {
                        Position = new Vector3(v.x, v.y, v.z),
                        Normal   = new Vector3(up.x, up.y, up.z),
                    };

                    double2 tv = buffers.TileVertices[i];
                    s1[i] = new Vector2((float)(tv.x * extentInv), (float)(tv.y * extentInv));
                    s2[i] = FlatTangent;                                 // Mercator: constant +X east (globe → subdivided path)
                    s3[i] = featureColors[buffers.VertexFeatureIdx[i]];  // per-feature linear color
                }

                for (int i = 0; i < totalIndices; i++)
                    indices[i] = buffers.TriangleIndices[i];

                md.subMeshCount = 1;
                md.SetSubMesh(0, new SubMeshDescriptor(0, totalIndices, MeshTopology.Triangles), NoValidate);

                vertexCount = totalVerts;
                float3 c3 = (bMin + bMax) * 0.5f;
                float3 sz = bMax - bMin;
                bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
            }
            finally
            {
                buffers.Dispose();
            }
        }

        // Globe fill (C-3): refine earcut's flat triangles onto the sphere (Burst job, projection devirtualised),
        // then stream the subdivided geometry — which also carries the per-vertex east Tangent. Allocation-free:
        // the earcut NativeArrays feed the job directly and the refined output lands in Temp-scope NativeLists.
        private static void WriteGlobeSubdivided(
            Mesh.MeshData md, in TileMeshBuffers buffers, IProjection proj, TileId id, double extent,
            double3 tileOriginRender, List<Vector4> featureColors, out int vertexCount, out Bounds bounds)
        {
            vertexCount = 0;
            bounds      = default;

            int srcVerts   = buffers.VertexCount[0];
            int srcIndices = buffers.TotalIndexCount;

            var outV  = new NativeList<GlobeFillVertex>(srcVerts * 4, Allocator.Persistent);
            var outIx = new NativeList<int>(srcIndices * 4, Allocator.Persistent);
            try
            {
                GlobeFillSubdivideDispatch.Run(
                    proj, buffers.TileVertices, buffers.TriangleIndices, buffers.VertexFeatureIdx,
                    srcVerts, srcIndices, id, extent, tileOriginRender,
                    GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                    GlobeFillSubdivideDispatch.DefaultMaxOutputVertices, outV, outIx);

                int n = outV.Length, ni = outIx.Length;
                if (n == 0 || ni == 0) return;

                md.SetVertexBufferParams(n, FillVertexDescriptors);
                NativeArray<FillPositionNormal> s0 = md.GetVertexData<FillPositionNormal>(0);
                NativeArray<Vector2>            s1 = md.GetVertexData<Vector2>(1);
                NativeArray<Vector4>            s2 = md.GetVertexData<Vector4>(2);
                NativeArray<Vector4>            s3 = md.GetVertexData<Vector4>(3);
                md.SetIndexBufferParams(ni, IndexFormat.UInt32);
                NativeArray<int> indices = md.GetIndexData<int>();

                double extentInv = extent > 0.0 ? 1.0 / extent : 0.0;
                float3 bMin = new float3(float.MaxValue);
                float3 bMax = new float3(float.MinValue);
                for (int i = 0; i < n; i++)
                {
                    GlobeFillVertex fv = outV[i];
                    float3 v = (float3)fv.World;
                    bMin = math.min(bMin, v); bMax = math.max(bMax, v);
                    float3 up = (float3)fv.Up;
                    s0[i] = new FillPositionNormal { Position = new Vector3(v.x, v.y, v.z), Normal = new Vector3(up.x, up.y, up.z) };
                    s1[i] = new Vector2((float)(fv.Tile.x * extentInv), (float)(fv.Tile.y * extentInv));
                    float3 east = (float3)fv.East;
                    s2[i] = new Vector4(east.x, east.y, east.z, 1f);      // w=+1: same TBN handedness as the Mercator path
                    s3[i] = featureColors[fv.Feature];
                }
                for (int i = 0; i < ni; i++) indices[i] = outIx[i];

                md.subMeshCount = 1;
                md.SetSubMesh(0, new SubMeshDescriptor(0, ni, MeshTopology.Triangles), NoValidate);
                vertexCount = n;
                float3 c3 = (bMin + bMax) * 0.5f; float3 sz = bMax - bMin;
                bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
            }
            finally
            {
                outV.Dispose();
                outIx.Dispose();
            }
        }
    }
}
