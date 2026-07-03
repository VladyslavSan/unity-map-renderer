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
    /// Pipeline per feature (S89 D2 / S91-B): MvtGeometry.Decode (managed) → project the centerline through
    ///   the shared <c>ProjectPointsJob</c> (the SAME projection surface as fills — no bespoke
    ///   WebMercator.Forward; run on this worker via <c>.Run()</c>) → Burst <c>LineTessellationJob</c> per ring
    ///   (in the projected surface plane) → stream write into a <c>Mesh.MeshData</c>. The managed
    ///   <c>LineTessellator</c> is retired from this path (differential oracle only). Mercator output is
    ///   bit-for-bit the pre-S91 path; S91-C tessellates in the per-vertex tangent frame for the globe.
    ///
    /// S89 Stage B — <see cref="WriteMeshData"/> tessellates AND writes directly into a caller-allocated
    /// <see cref="Mesh.MeshData"/>, off the main thread (see <see cref="StyledFillTileBuilder"/> for the
    /// choreography). The bespoke NativeArray-stream payload + main-thread copy is gone.
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3, projection surface up; +Y for Mercator) interleaved via <see cref="LinePositionNormal"/>.
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

        // S91-C C-3 curvature subdivision (globe only): the max great-circle arc a single centerline segment may
        // span before it is split — sub-points (linear in tile space) project onto the sphere so long chords stop
        // faceting. ~2° ⇒ sagitta ≈ R·(1−cos1°) ≈ 1 km, sub-pixel at whole-globe scale; segments already shorter
        // than this (any real zoom) are untouched. Per-segment split is capped so a degenerate span can't blow up.
        private const double MaxCurveSegmentRad = 2.0 * math.PI_DBL / 180.0;
        private const int    MaxCurveSegments   = 128;

        // S91-B: lines project through the SAME projection surface as fills (ProjectPointsJob), not a bespoke
        // hardcoded WebMercator.Forward. Launch-time projection config threads a chosen projection here in
        // S91-C; until then this single seam defaults to WebMercator (Mercator output is bit-for-bit).
        private static readonly IProjection DefaultProjection = new WebMercatorProjection();

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
            double3                   tileOriginRender,
            out int                   vertexCount,
            out Bounds                bounds,
            IProjection               projection = null) // null ⇒ WebMercator (launch-time config threads this in)
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

            // Reusable Burst scratch: NativeLists own their own grow-only capacity (Resize sizes each ring; the
            // list keeps the high-water buffer), so there is no per-ring malloc/free churn and no hand-tracked
            // caps. Sequential .Run() means each buffer is free for reuse before the next ring.
            projection ??= DefaultProjection; // null ⇒ WebMercator; the launch-time projection is threaded via WriteInto
            // S91-C: a curved-surface projection (the globe — its render mapping reverses winding) needs a
            // PER-VERTEX tangent frame; the planar Mercator keeps the flat path bit-for-bit.
            bool globe = projection.ReversesWinding;
            double3 tileOrigin3 = tileOriginRender; // S91-C: the caller-supplied SW-corner render origin (Mercator: (mercX, 0, mercZ))
            var geoScratch = new NativeList<GeoCoordinate>(Allocator.Persistent); // tile → geodetic surface points
            var projWorld  = new NativeList<double3>(Allocator.Persistent);       // origin-relative projected centerline
            var projUp     = new NativeList<double3>(Allocator.Persistent);       // per-point surface up (constant +Y for Mercator)
            var inPts      = new NativeList<double2>(Allocator.Persistent);       // tile coords in, then projected (x,z) for tessellation
            var subPts     = new NativeList<double2>(Allocator.Persistent);       // S91-C C-3: curvature-subdivided tile centerline (globe)
            var outV       = new NativeList<LineVertex>(Allocator.Persistent);
            var outI       = new NativeList<int>(Allocator.Persistent);
            var vcArr = new NativeArray<int>(1, Allocator.Persistent);
            var icArr = new NativeArray<int>(1, Allocator.Persistent);

            try
            {
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

                    int n = ring.Count;

                    // Size the projection buffers to the original ring (n points) — Resize grows the backing
                    // buffer only when a longer ring appears, otherwise just sets the length.
                    inPts.Resize(n, NativeArrayOptions.UninitializedMemory);
                    geoScratch.Resize(n, NativeArrayOptions.UninitializedMemory);
                    projWorld.Resize(n, NativeArrayOptions.UninitializedMemory);
                    projUp.Resize(n, NativeArrayOptions.UninitializedMemory);

                    // Project the centerline through the SAME jobs as fills (TileToGeoJob → ProjectPointsJob<TProj>) —
                    // no bespoke WebMercator.Forward. inPts carries the tile coords in; projUp is the per-point surface
                    // up (used by the planar +Y frame and the globe curvature test).
                    for (int k = 0; k < n; k++) inPts[k] = ring[k];
                    new TileToGeoJob
                    {
                        TileZ = id.Z, TileX = id.X, TileY = id.Y, Extent = extent,
                        TileCoords = inPts.AsArray(), OutGeo = geoScratch.AsArray(),
                    }.Run(n);
                    ProjectionDispatch.Run(projection, tileOrigin3, geoScratch.AsArray(), projWorld.AsArray(), projUp.AsArray(), n);

                    // Tessellation input. Planar Mercator tessellates in the projected (east,north)=(x,z) plane
                    // (bit-for-bit the old path). The globe tessellates in TILE space (unique per centerline point —
                    // the flattened ECEF xz would collide the sphere's front/back) AND curvature-subdivides each
                    // segment (C-3): sub-points, linear in tile space, project onto the sphere so long chords stop
                    // cutting through it (the ring faceting). The bake reconstructs the 3D frame per output vertex.
                    NativeArray<double2> tessSrc;
                    int                  tessN;
                    if (globe)
                    {
                        tessN   = SubdivideGlobeCenterline(ring, projUp.AsArray(), n, subPts);
                        tessSrc = subPts.AsArray();
                    }
                    else
                    {
                        for (int k = 0; k < n; k++) inPts[k] = new double2(projWorld[k].x, projWorld[k].z);
                        tessSrc = inPts.AsArray();
                        tessN   = n;
                    }

                    // Worst-case tessellation output sizing — from the (possibly subdivided) point count.
                    outV.Resize(LineTessellationJob.MaxVertexCount(tessN, DefaultRoundSegments), NativeArrayOptions.UninitializedMemory);
                    outI.Resize(LineTessellationJob.MaxIndexCount(tessN, DefaultRoundSegments),  NativeArrayOptions.UninitializedMemory);

                    new LineTessellationJob
                    {
                        InputPoints    = tessSrc,
                        PointCount     = tessN,
                        Join           = joinType,
                        Cap            = capType,
                        MiterLimit     = DefaultMiterLimit,
                        RoundSegments  = DefaultRoundSegments,
                        OutVertices    = outV.AsArray(),
                        OutIndices     = outI.AsArray(),
                        OutVertexCount = vcArr,
                        OutIndexCount  = icArr,
                    }.Run();

                    int nv = vcArr[0];
                    int ni = icArr[0];
                    if (nv == 0 || ni == 0) continue;

                    int offset = tempVerts0.Count;
                    if (globe)
                    {
                        // Globe: reconstruct each ribbon vertex's 3D frame from its TILE-space centerline point
                        // (the tessellator preserves it as v.Position). Position lands ON the sphere; the up is
                        // the radial normal; the 2D across is rotated into the local ENU tangent plane
                        // (tile x → east, tile y → south = −north), so |across| stays the miter factor and the
                        // shader extrudes the width within the surface tangent plane (the ribbon hugs the globe).
                        for (int k = 0; k < nv; k++)
                        {
                            LineVertex v  = outV[k];
                            double2    ll = id.ToLonLat(v.Position.x, v.Position.y, extent);
                            var g         = new GeoCoordinate { Latitude = ll.y, Longitude = ll.x };
                            ProjectedPoint pp = projection.ProjectPoint(g);
                            float3   pos    = (float3)(pp.World - tileOrigin3); // origin-relative (RTC)
                            float3   up     = (float3)pp.Up;
                            float3x3 tb     = projection.TangentBasisAt(g);     // c0=east, c1=up, c2=north
                            float3   across = (float)v.Normal.x * tb.c0 - (float)v.Normal.y * tb.c2;
                            bMin = math.min(bMin, pos);
                            bMax = math.max(bMax, pos);
                            tempVerts0.Add(new LinePositionNormal
                            {
                                Position = new Vector3(pos.x, pos.y, pos.z),
                                Normal   = new Vector3(up.x, up.y, up.z),
                            });
                            tempVerts1.Add(new Vector3(across.x, across.y, across.z));
                            tempVerts2.Add(new Vector2(v.Side, (float)v.DistanceAlong));
                            tempVerts3.Add(new LineWidthColor { WidthScale = v.WidthScale * featureWidthScale, Color = featureColor });
                        }
                    }
                    else
                    {
                        // Planar Mercator (unchanged): flat XZ ribbon, constant surface up = projUp[0] (+Y).
                        float3  upN      = (float3)projUp[0];
                        Vector3 upNormal = new Vector3(upN.x, upN.y, upN.z);
                        for (int k = 0; k < nv; k++)
                        {
                            LineVertex v = outV[k];
                            float3 pos = new float3((float)v.Position.x, 0f, (float)v.Position.y);
                            bMin = math.min(bMin, pos);
                            bMax = math.max(bMax, pos);
                            tempVerts0.Add(new LinePositionNormal
                            {
                                Position = new Vector3(pos.x, pos.y, pos.z),
                                Normal   = upNormal,
                            });
                            // Across as a 3D tangent-plane vector (magnitude = miter factor); Y=0 = flat Mercator.
                            tempVerts1.Add(new Vector3((float)v.Normal.x, 0f, (float)v.Normal.y));
                            tempVerts2.Add(new Vector2(v.Side, (float)v.DistanceAlong));
                            tempVerts3.Add(new LineWidthColor
                            {
                                WidthScale = v.WidthScale * featureWidthScale,
                                Color      = featureColor,
                            });
                        }
                    }

                    for (int k = 0; k < ni; k++)
                        tempIndices.Add(offset + outI[k]);
                }
            }
            }
            finally
            {
                inPts.Dispose();
                subPts.Dispose();
                geoScratch.Dispose();
                projWorld.Dispose();
                projUp.Dispose();
                outV.Dispose();
                outI.Dispose();
                vcArr.Dispose();
                icArr.Dispose();
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

        // ── S91-C C-3: globe curvature subdivision ─────────────────────────────────────────────────

        /// <summary>
        /// Densifies a tile-space centerline (<paramref name="ring"/>) so each segment's projected great-circle
        /// arc stays under <see cref="MaxCurveSegmentRad"/>. The arc a segment spans is read directly from the
        /// per-point surface normals (<paramref name="up"/>, unit ECEF): <c>acos(dot(up[k], up[k+1]))</c>. Each
        /// segment is split into that many equal parts by LINEAR interpolation in tile space; every sub-point
        /// projects onto the sphere (the bake re-projects it), so the chord sagitta shrinks and the ribbon hugs
        /// the globe instead of faceting. Writes the subdivided points into <paramref name="subPts"/> (grown on
        /// demand) and returns their count. Globe only — the planar path never calls this.
        /// </summary>
        internal static int SubdivideGlobeCenterline(
            List<double2> ring, NativeArray<double3> up, int n, NativeList<double2> subPts)
        {
            int count = 1; // the first point, then `segs` points per segment (sub-points + the segment end)
            for (int k = 0; k < n - 1; k++) count += SegmentSteps(up[k], up[k + 1]);

            subPts.Resize(count, NativeArrayOptions.UninitializedMemory);

            subPts[0] = ring[0];
            int w = 1;
            for (int k = 0; k < n - 1; k++)
            {
                int     segs = SegmentSteps(up[k], up[k + 1]);
                double2 a    = ring[k];
                double2 b    = ring[k + 1];
                for (int j = 1; j <= segs; j++)
                {
                    double t = (double)j / segs;
                    subPts[w++] = a + (b - a) * t;
                }
            }
            return w;
        }

        /// <summary>Number of equal sub-segments a centerline segment is split into so its projected arc (the
        /// angle between the two unit surface normals) stays under <see cref="MaxCurveSegmentRad"/>; ≥1, capped
        /// at <see cref="MaxCurveSegments"/>.</summary>
        private static int SegmentSteps(double3 upA, double3 upB)
        {
            double ang  = math.acos(math.clamp(math.dot(upA, upB), -1.0, 1.0));
            int    segs = (int)math.ceil(ang / MaxCurveSegmentRad);
            if (segs < 1) segs = 1;
            if (segs > MaxCurveSegments) segs = MaxCurveSegments;
            return segs;
        }
    }
}
