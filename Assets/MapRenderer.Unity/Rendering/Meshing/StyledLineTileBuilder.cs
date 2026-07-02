using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Jobs;
using Line = MapRenderer.Core.Style.Line;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// S14 managed per-layer line mesh builder. Mirrors <see cref="StyledFillTileBuilder"/> but for
    /// line-type style layers.
    ///
    /// Pipeline per feature (S89 D2): MvtGeometry.Decode + project to tile-local meters (managed, double2) →
    ///   Burst <c>LineTessellationJob</c> per ring (run on this worker via <c>.Run()</c> into NativeArrays) →
    ///   stream write into a <c>Mesh.MeshData</c>. The managed <c>LineTessellator</c> is retired from this
    ///   path (differential oracle only). Decode + projection stay managed for now.
    ///
    /// S89 Stage B — <see cref="WriteMeshData"/> tessellates AND writes directly into a caller-allocated
    /// <see cref="Mesh.MeshData"/>, off the main thread (see <see cref="StyledFillTileBuilder"/> for the
    /// choreography). The bespoke NativeArray-stream payload + main-thread copy is gone.
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3, +Y) interleaved via <see cref="LinePositionNormal"/>.
    ///   Stream 1 — TexCoord0: extrusion across-direction (Float32x3, 3D tangent-plane; Y=0 for Mercator).
    ///   Stream 2 — TexCoord1: side + distanceAlong (Float32x2).
    ///   Stream 3 — Color (Float32x4) + TexCoord2/widthScale (Float32x1) interleaved via <see cref="LineWidthColor"/>. 20B stride.
    ///   Index buffer — UInt32.
    ///
    /// Color (D1): per-feature sRGB color baked via <see cref="StyleProperty{T}"/>; converted to linear via
    /// <c>Color.linear</c> off the main thread. <c>_BaseColor=white</c> on the Material (identity multiply).
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class StyledLineTileBuilder
    {
        /// <summary>
        /// Tightly-packed Position + Normal struct for stream 0. Stride = 6 × 4 = 24 bytes.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LinePositionNormal
        {
            public Vector3 Position;
            public Vector3 Normal;
        }

        /// <summary>
        /// S14: Color + WidthScale interleaved on stream 3. Canonical field order matches the canonical
        /// descriptor order (Color enum=3 before TexCoord2 enum=6), so stream-3 byte offsets are Color@0,
        /// WidthScale@16. Stride = 16 (float4) + 4 (float) = 20 bytes.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LineWidthColor
        {
            public Vector4 Color;
            public float   WidthScale;
        }

        // Line vertex attribute descriptors (static readonly — no per-tile alloc). Canonical ascending
        // VertexAttribute enum order (Position=0, Normal=1, Color=3, TexCoord0=4, TexCoord1=5, TexCoord2=6)
        // avoids the "non-standard order" warning. Stream-3 interleave: Color (Float32x4, 16B) then
        // TexCoord2/WidthScale (Float32x1, 4B), matching LineWidthColor { Vector4 Color; float WidthScale }.
        // internal (not private): the SyntheticLineMesh test helper reuses the exact production layout.
        internal static readonly VertexAttributeDescriptor[] LineVertexDescriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 1, stream: 3),
        };

        // Skip main-thread index validation + redundant intermediate bounds compute (see StyledFillTileBuilder).
        internal const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // LineTessellator.Triangulate defaults — the Burst LineTessellationJob must match them for parity.
        private const double DefaultMiterLimit    = 2.0;
        private const int    DefaultRoundSegments = 4;

        // Constant +Y normal for all line vertices.
        private static readonly Vector3 UpNormal = Vector3.up;

        // White vertex color = identity multiply.
        private static readonly Vector4 WhiteColor = new Vector4(1f, 1f, 1f, 1f);

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Tessellate all line features for one style layer and write the geometry directly into
        /// <paramref name="md"/> (a caller-allocated <c>Mesh.MeshData</c>, count-1 slot), off the main thread.
        /// Returns <paramref name="vertexCount"/> = 0 (leaving <paramref name="md"/> untouched) when no line
        /// geometry is produced. <paramref name="bounds"/> carries the worker-computed centerline AABB
        /// (parity with the pre-S55 RecalculateBounds, which also ignored shader width extrusion).
        /// </summary>
        public static void WriteMeshData(
            Mesh.MeshData             md,
            IReadOnlyList<MvtFeature> selectedFeatures,
            Line.PaintProperties      paint,
            Line.LayoutProperties     layout,
            double                    zoom,
            double                    extent,
            TileId                    id,
            double2                   tileOriginMerc,
            out int                   vertexCount,
            out Bounds                bounds)
        {
            vertexCount = 0;
            bounds      = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0)
                return;

            // S60: Join/Cap are already parsed enums on LayoutProperties (no per-build string switch).
            JoinType joinType = layout.Join;
            CapType  capType  = layout.Cap;

            // Phase 1: tessellate all features into temporary managed lists.
            var tempVerts0  = new List<LinePositionNormal>(512);
            var tempVerts1  = new List<Vector3>(512);
            var tempVerts2  = new List<Vector2>(512);
            var tempVerts3  = new List<LineWidthColor>(512);
            var tempIndices = new List<int>(1024);
            // S55: tight AABB accumulator — centerline positions only (parity with old RecalculateBounds).
            float3 bMin = new float3(float.MaxValue);
            float3 bMax = new float3(float.MinValue);

            foreach (var feature in selectedFeatures)
            {
                if (feature.GeometryType != MvtGeometryType.LineString)
                    continue;

                // Bake per-feature vertex color from the data-driven paint expression (sRGB→linear here,
                // off-main-thread). _BaseColor=white on the material → identity multiply.
                Vector4 featureColor = WhiteColor;
                var     adapter      = new MvtFeatureAdapter(feature);
                if (paint.Color.TryEvaluate(zoom, adapter, out CoreColor c))
                {
                    var unityColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
                    var linear     = unityColor.linear;
                    featureColor = new Vector4(linear.r, linear.g, linear.b, linear.a);
                }

                // S14 data-driven opacity: bake evaluated opacity into vertex alpha, ONLY when opacity depends
                // on the feature (else BindLinePaintToApplier already bound _Opacity and baking double-applies).
                if (paint.Opacity.DependsOnFeature)
                {
                    if (paint.Opacity.TryEvaluate(zoom, adapter, out float opacityVal))
                        featureColor.w *= opacityVal;
                }

                // S14 data-driven width: bake evaluated width into WidthScale (multiplier on _Width). When
                // width depends on the feature, _Width is set to 1.0 by BindLinePaintToApplier, so WidthScale
                // carries the full evaluated pixel width; otherwise WidthScale stays the tessellator's factor.
                float featureWidthScale = 1f;
                if (paint.Width.DependsOnFeature)
                {
                    if (paint.Width.TryEvaluate(zoom, adapter, out float widthVal))
                        featureWidthScale = math.max(0f, widthVal);
                }

                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
                if (rings == null || rings.Count == 0) continue;

                foreach (var ring in rings)
                {
                    if (ring == null || ring.Count < 2) continue;

                    var worldPts = ProjectLineRing(ring, id.Z, id.X, id.Y, extent, tileOriginMerc.x, tileOriginMerc.y);
                    if (worldPts == null || worldPts.Count < 2) continue;

                    // Burst line tessellation (Run() on this worker — see StyledFillTileBuilder for the choreography;
                    // decode+projection stay managed and feed the double-precision job → strict parity with the
                    // managed LineTessellator on the miter/butt path). Worst-case output sizing; actual counts in
                    // vcArr/icArr. Per-ring native scratch is malloc/free churn (no GC).
                    int n    = worldPts.Count;
                    int capV = LineTessellationJob.MaxVertexCount(n, DefaultRoundSegments);
                    int capI = LineTessellationJob.MaxIndexCount(n, DefaultRoundSegments);

                    var inPts = new NativeArray<double2>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    for (int k = 0; k < n; k++) inPts[k] = worldPts[k];
                    var outV  = new NativeArray<LineVertex>(capV, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    var outI  = new NativeArray<int>(capI, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    var vcArr = new NativeArray<int>(1, Allocator.Persistent);
                    var icArr = new NativeArray<int>(1, Allocator.Persistent);
                    try
                    {
                        new LineTessellationJob
                        {
                            InputPoints    = inPts,
                            PointCount     = n,
                            Join           = joinType,
                            Cap            = capType,
                            MiterLimit     = DefaultMiterLimit,
                            RoundSegments  = DefaultRoundSegments,
                            OutVertices    = outV,
                            OutIndices     = outI,
                            OutVertexCount = vcArr,
                            OutIndexCount  = icArr,
                        }.Run();

                        int nv = vcArr[0];
                        int ni = icArr[0];
                        if (nv == 0 || ni == 0) continue; // finally still disposes the scratch

                        int offset = tempVerts0.Count;
                        for (int k = 0; k < nv; k++)
                        {
                            LineVertex v = outV[k];
                            float3 pos = new float3((float)v.Position.x, 0f, (float)v.Position.y);
                            bMin = math.min(bMin, pos);
                            bMax = math.max(bMax, pos);
                            tempVerts0.Add(new LinePositionNormal
                            {
                                Position = new Vector3(pos.x, pos.y, pos.z),
                                Normal   = UpNormal,
                            });
                            // Across as a 3D tangent-plane vector (magnitude = miter factor). Y=0 = flat Mercator;
                            // a globe projection bakes non-zero Y and the shader consumes it as-is.
                            tempVerts1.Add(new Vector3((float)v.Normal.x, 0f, (float)v.Normal.y));
                            tempVerts2.Add(new Vector2(v.Side, (float)v.DistanceAlong));
                            tempVerts3.Add(new LineWidthColor
                            {
                                WidthScale = v.WidthScale * featureWidthScale,
                                Color      = featureColor,
                            });
                        }

                        for (int k = 0; k < ni; k++)
                            tempIndices.Add(offset + outI[k]);
                    }
                    finally
                    {
                        inPts.Dispose(); outV.Dispose(); outI.Dispose(); vcArr.Dispose(); icArr.Dispose();
                    }
                }
            }

            if (tempVerts0.Count == 0 || tempIndices.Count == 0)
                return; // no geometry — md left untouched; caller disposes the unused MeshData

            // Phase 2: declare the mesh buffers on the MeshData and grab stream views.
            int vCount = tempVerts0.Count;
            int iCount = tempIndices.Count;

            md.SetVertexBufferParams(vCount, LineVertexDescriptors);
            NativeArray<LinePositionNormal> s0 = md.GetVertexData<LinePositionNormal>(0);
            NativeArray<Vector3>            s1 = md.GetVertexData<Vector3>(1);
            NativeArray<Vector2>            s2 = md.GetVertexData<Vector2>(2);
            NativeArray<LineWidthColor>     s3 = md.GetVertexData<LineWidthColor>(3);
            md.SetIndexBufferParams(iCount, IndexFormat.UInt32);
            NativeArray<int> indices = md.GetIndexData<int>();

            // Phase 3: copy temp streams into the MeshData views (worker thread).
            for (int i = 0; i < vCount; i++)
            {
                s0[i] = tempVerts0[i];
                s1[i] = tempVerts1[i];
                s2[i] = tempVerts2[i];
                s3[i] = tempVerts3[i];
            }
            for (int i = 0; i < iCount; i++)
                indices[i] = tempIndices[i];

            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, iCount, MeshTopology.Triangles), NoValidate);

            vertexCount = vCount;
            float3 c3 = (bMin + bMax) * 0.5f;
            float3 sz = bMax - bMin;
            bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Project a line ring from tile-space double2 coordinates to world-space double2 (meters, relative
        /// to the scene origin). Uses the same full Web Mercator projection as
        /// StyledFillTileBuilder.ProjectVerticesManaged — identical precision contract.
        /// </summary>
        private static List<double2> ProjectLineRing(
            List<double2> ring,    int    z, int tileX, int tileY, double extent,
            double        originX, double originY)
        {
            if (ring == null || ring.Count < 2) return null;

            const double TwoPi = 2.0 * math.PI_DBL;

            double pow2z  = math.pow(2.0, z);
            var    result = new List<double2>(ring.Count);

            foreach (var pt in ring)
            {
                double px = pt.x;
                double py = pt.y;

                double u = (tileX + px / extent) / pow2z;
                double v = (tileY + py / extent) / pow2z;

                double longitudeRad = u * TwoPi - math.PI_DBL;
                double arg          = math.PI_DBL * (1.0 - 2.0 * v);
                double sinhArg      = (math.exp(arg)     - math.exp(-arg)) * 0.5;
                double latitudeRad  = math.atan(sinhArg);

                double latitudeDeg  = latitudeRad  * (180.0 / math.PI_DBL);
                double longitudeDeg = longitudeRad * (180.0 / math.PI_DBL);

                double3 world = WebMercator.Forward(new GeoCoordinate3D
                    { Longitude = longitudeDeg, Latitude = latitudeDeg, Altitude = 0.0 });

                result.Add(new double2(world.x - originX, world.z - originY));
            }

            return result;
        }
    }
}
