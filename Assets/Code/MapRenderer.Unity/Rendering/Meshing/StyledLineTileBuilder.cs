using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using Line = MapRenderer.Core.Style.Line;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// S14 managed per-layer line mesh builder. Mirrors <see cref="StyledFillTileBuilder"/> but for
    /// line-type style layers.
    ///
    /// Pipeline per feature (S100): MvtGeometry.Decode (managed) → curvature-subdivide the centerline in tile
    ///   space per the projection's <see cref="IProjection.MaxRefineAngleRad"/> policy (a no-op for the flat
    ///   Mercator, whose tolerance is ∞) → project the subdivided centerline to an origin-relative render-space
    ///   <c>(point, up)</c> ARRAY through the managed <see cref="IProjection.ProjectPoint"/> → Burst
    ///   <see cref="LineRibbonJob"/> (projection-agnostic; builds the ribbon in 3D with
    ///   <c>across = cross(along, up)</c>) → stream write into a <c>Mesh.MeshData</c>.
    ///
    /// <para>ONE code path for every projection. Winding is correct BY CONSTRUCTION — <c>across</c> is tied to the
    /// same <c>up</c> the centerline was projected with (one frame), so there is no per-projection winding flip,
    /// no separate tangent-basis second frame, and no curvature/handedness branch. The managed
    /// <see cref="LineTessellator"/> is the planar differential oracle for <see cref="LineRibbonJob"/>
    /// (<c>LineRibbonJobTests</c>). See <c>docs/mesh-pipeline.md</c> for how this line ordering
    /// (Subdivide → Project → Triangulate) mirrors the fill one, <c>docs/coordinates-and-projections.md §7.1</c>,
    /// and <c>GlobeLineWindingTests</c>.</para>
    ///
    /// S89 Stage B — <see cref="WriteMeshData"/> builds AND writes directly into a caller-allocated
    /// <see cref="Mesh.MeshData"/>, off the main thread (see <see cref="StyledFillTileBuilder"/> for the
    /// choreography). The centerline projection is a managed virtual-call loop (small polylines; the heavy
    /// ribbon topology stays in Burst), which is what lets any <see cref="IProjection"/> — including one never
    /// registered for Burst — flow through this single path (the S100 decisive tooth).
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3, surface up; +Y for Mercator) interleaved via <see cref="LinePositionNormal"/>.
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

        // LineTessellator.Triangulate defaults — LineRibbonJob (and its managed oracle) share them for parity.
        private const double DefaultMiterLimit    = 2.0;
        private const int    DefaultRoundSegments = 4;

        // Per-segment curvature-subdivision cap: however far a segment's projected arc reaches, it is split into at
        // most this many parts so a degenerate span can't blow up. The ANGLE tolerance is projection-supplied
        // (IProjection.MaxRefineAngleRad); this is only the safety cap.
        private const int MaxCurveSegments = 128;

        // S91-B: lines project through the SAME projection surface as fills, not a bespoke hardcoded
        // WebMercator.Forward. Launch-time projection config threads a chosen projection here; until then this
        // single seam defaults to WebMercator (Mercator output is bit-for-bit).
        private static readonly IProjection DefaultProjection = new WebMercatorProjection();

        // White vertex color = identity multiply.
        private static readonly Vector4 WhiteColor = new Vector4(1f, 1f, 1f, 1f);

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Build all line features for one style layer and write the geometry directly into <paramref name="md"/>
        /// (a caller-allocated <c>Mesh.MeshData</c>, count-1 slot), off the main thread. Returns
        /// <paramref name="vertexCount"/> = 0 (leaving <paramref name="md"/> untouched) when no line geometry is
        /// produced. <paramref name="bounds"/> carries the worker-computed centerline AABB (parity with the
        /// pre-S55 RecalculateBounds, which also ignored shader width extrusion).
        /// </summary>
        public static void WriteMeshData(
            Mesh.MeshData               md,
            IReadOnlyList<ITileFeature> selectedFeatures,
            Line.PaintProperties        paint,
            Line.LayoutProperties       layout,
            double                      zoom,
            double                      extent,
            TileId                      id,
            double3                     tileOriginRender,
            out int                     vertexCount,
            out Bounds                  bounds,
            IProjection                 projection = null) // null ⇒ WebMercator (launch-time config threads this in)
        {
            vertexCount = 0;
            bounds      = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0)
                return;

            // S60: Join/Cap are already parsed enums on LayoutProperties (no per-build string switch).
            JoinType joinType = layout.Join;
            CapType  capType  = layout.Cap;

            // First, build all features into temporary managed lists.
            var tempVerts0  = new List<LinePositionNormal>(512);
            var tempVerts1  = new List<Vector3>(512);
            var tempVerts2  = new List<Vector2>(512);
            var tempVerts3  = new List<LineWidthColor>(512);
            var tempIndices = new List<int>(1024);
            // S55: tight AABB accumulator — centerline positions only (parity with old RecalculateBounds).
            float3 bMin = new float3(float.MaxValue);
            float3 bMax = new float3(float.MinValue);

            projection ??= DefaultProjection; // null ⇒ WebMercator; the launch-time projection is threaded via WriteInto
            double maxRefineAngleRad = projection.MaxRefineAngleRad; // ∞ ⇒ flat sheet, subdivide never fires
            double3 tileOrigin3 = tileOriginRender; // the caller-supplied SW-corner render origin

            // Reusable Burst scratch: NativeLists own their own grow-only capacity (Resize sizes each ring; the
            // list keeps the high-water buffer), so there is no per-ring malloc/free churn. Sequential .Run() means
            // each buffer is free for reuse before the next ring.
            var inTile    = new NativeList<double2>(Allocator.Persistent);       // original ring tile coords
            var geoOrig   = new NativeList<GeoCoordinate>(Allocator.Persistent);  // original ring → geodetic
            var upOrig    = new NativeList<double3>(Allocator.Persistent);        // original-point surface up (subdivision metric)
            var subTile   = new NativeList<double2>(Allocator.Persistent);        // curvature-subdivided tile centerline
            var subGeo    = new NativeList<GeoCoordinate>(Allocator.Persistent);  // subdivided → geodetic
            var world     = new NativeList<double3>(Allocator.Persistent);        // subdivided, projected, origin-relative
            var up        = new NativeList<double3>(Allocator.Persistent);        // subdivided per-point surface up
            var outV      = new NativeList<LineRibbonVertex>(Allocator.Persistent);
            var outI      = new NativeList<int>(Allocator.Persistent);
            var vcArr = new NativeArray<int>(1, Allocator.Persistent);
            var icArr = new NativeArray<int>(1, Allocator.Persistent);

            try
            {
            foreach (var feature in selectedFeatures)
            {
                if (feature.GeometryType != TileGeometryType.LineString)
                    continue;

                // Bake per-feature vertex color from the data-driven paint expression (sRGB→linear here,
                // off-main-thread). _BaseColor=white on the material → identity multiply.
                Vector4 featureColor = WhiteColor;
                if (paint.Color.TryEvaluate(zoom, feature, out CoreColor c))
                {
                    var unityColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
                    var linear     = unityColor.linear;
                    featureColor = new Vector4(linear.r, linear.g, linear.b, linear.a);
                }

                // S14 data-driven opacity: bake evaluated opacity into vertex alpha, ONLY when opacity depends
                // on the feature (else BindLinePaintToApplier already bound _Opacity and baking double-applies).
                if (paint.Opacity.DependsOnFeature)
                {
                    if (paint.Opacity.TryEvaluate(zoom, feature, out float opacityVal))
                        featureColor.w *= opacityVal;
                }

                // S14 data-driven width: bake evaluated width into WidthScale (multiplier on _Width). When
                // width depends on the feature, _Width is set to 1.0 by BindLinePaintToApplier, so WidthScale
                // carries the full evaluated pixel width; otherwise WidthScale stays the ribbon's factor.
                float featureWidthScale = 1f;
                if (paint.Width.DependsOnFeature)
                {
                    if (paint.Width.TryEvaluate(zoom, feature, out float widthVal))
                        featureWidthScale = math.max(0f, widthVal);
                }

                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
                if (rings == null || rings.Count == 0) continue;

                foreach (var ring in rings)
                {
                    if (ring == null || ring.Count < 2) continue;

                    int n = ring.Count;

                    // 1) Project the ORIGINAL ring to per-point surface ups (the subdivision metric). Tile→geo via
                    //    the shared projection-independent job; geo→up via the managed ProjectPoint (works for any
                    //    IProjection, incl. types never registered for Burst — the S100 decisive tooth).
                    inTile.Resize(n, NativeArrayOptions.UninitializedMemory);
                    geoOrig.Resize(n, NativeArrayOptions.UninitializedMemory);
                    upOrig.Resize(n, NativeArrayOptions.UninitializedMemory);
                    for (int k = 0; k < n; k++) inTile[k] = ring[k];
                    new TileToGeoJob
                    {
                        Tile = id, Extent = extent,
                        TileCoords = inTile.AsArray(), OutGeo = geoOrig.AsArray(),
                    }.Run(n);
                    for (int k = 0; k < n; k++) upOrig[k] = projection.ProjectPoint(geoOrig[k]).Up;

                    // 2) Curvature-subdivide the centerline in TILE space (linear sub-points that project onto the
                    //    surface). Driven entirely by the projection's tolerance — ∞ ⇒ 1 step/segment ⇒ the
                    //    original ring, so the flat Mercator path is the degenerate value, not a branch.
                    int m = SubdivideCenterline(ring, upOrig.AsArray(), n, subTile, maxRefineAngleRad);

                    // 3) Project the SUBDIVIDED centerline to the origin-relative (point, up) render-space array.
                    subGeo.Resize(m, NativeArrayOptions.UninitializedMemory);
                    world.Resize(m, NativeArrayOptions.UninitializedMemory);
                    up.Resize(m, NativeArrayOptions.UninitializedMemory);
                    new TileToGeoJob
                    {
                        Tile = id, Extent = extent,
                        TileCoords = subTile.AsArray(), OutGeo = subGeo.AsArray(),
                    }.Run(m);
                    for (int k = 0; k < m; k++)
                    {
                        ProjectedPoint pp = projection.ProjectPoint(subGeo[k]);
                        world[k] = pp.World - tileOrigin3; // origin-relative (RTC); translation-invariant ribbon math
                        up[k]    = pp.Up;
                    }

                    // 4) Build the ribbon in 3D from the (point, up) array — one path, winding by construction.
                    outV.Resize(LineRibbonJob.MaxVertexCount(m, DefaultRoundSegments), NativeArrayOptions.UninitializedMemory);
                    outI.Resize(LineRibbonJob.MaxIndexCount(m, DefaultRoundSegments),  NativeArrayOptions.UninitializedMemory);
                    new LineRibbonJob
                    {
                        Points         = world.AsArray(),
                        Ups            = up.AsArray(),
                        PointCount     = m,
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
                    for (int k = 0; k < nv; k++)
                    {
                        LineRibbonVertex rv = outV[k];
                        float3 pos    = (float3)rv.Position; // origin-relative render space
                        float3 upN    = (float3)rv.Up;       // surface up (Normal / lighting)
                        float3 across = (float3)rv.Across;   // 3D across, |across| = miter factor (Y=0 for Mercator)
                        bMin = math.min(bMin, pos);
                        bMax = math.max(bMax, pos);
                        tempVerts0.Add(new LinePositionNormal
                        {
                            Position = new Vector3(pos.x, pos.y, pos.z),
                            Normal   = new Vector3(upN.x, upN.y, upN.z),
                        });
                        tempVerts1.Add(new Vector3(across.x, across.y, across.z));
                        tempVerts2.Add(new Vector2(rv.Side, (float)rv.DistanceAlong));
                        tempVerts3.Add(new LineWidthColor
                        {
                            WidthScale = rv.WidthScale * featureWidthScale,
                            Color      = featureColor,
                        });
                    }

                    // No winding flip: `across = cross(along, up)` ties the ribbon to the same `up` the centerline
                    // was projected with (one frame), so front-faces point outward for every projection.
                    for (int k = 0; k < ni; k++)
                        tempIndices.Add(offset + outI[k]);
                }
            }
            }
            finally
            {
                inTile.Dispose();
                geoOrig.Dispose();
                upOrig.Dispose();
                subTile.Dispose();
                subGeo.Dispose();
                world.Dispose();
                up.Dispose();
                outV.Dispose();
                outI.Dispose();
                vcArr.Dispose();
                icArr.Dispose();
            }

            if (tempVerts0.Count == 0 || tempIndices.Count == 0)
                return; // no geometry — md left untouched; caller disposes the unused MeshData

            // Then declare the mesh buffers on the MeshData and grab stream views.
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

        // ── Curvature subdivision (projection-policy driven) ────────────────────────────────────────

        /// <summary>
        /// Densifies a tile-space centerline (<paramref name="ring"/>) so each segment's projected arc stays under
        /// <paramref name="maxRefineAngleRad"/> — the projection's <see cref="IProjection.MaxRefineAngleRad"/>
        /// policy. The arc a segment spans is read from the per-point surface normals (<paramref name="up"/>, unit):
        /// <c>acos(dot(up[k], up[k+1]))</c>. Each segment is split into that many equal parts by LINEAR
        /// interpolation in tile space; every sub-point projects onto the surface (step 3 re-projects it), so long
        /// chords stop faceting. Writes the subdivided points into <paramref name="subPts"/> (grown on demand) and
        /// returns their count.
        /// <para>A flat projection returns <c>maxRefineAngleRad = ∞</c>, so <see cref="SegmentSteps"/> yields 1 step
        /// per segment and the output is the original ring — the Mercator path is the degenerate value of the SAME
        /// code, with no capability flag.</para>
        /// </summary>
        internal static int SubdivideCenterline(
            List<double2> ring, NativeArray<double3> up, int n, NativeList<double2> subPts, double maxRefineAngleRad)
        {
            int count = 1; // the first point, then `segs` points per segment (sub-points + the segment end)
            for (int k = 0; k < n - 1; k++) count += SegmentSteps(up[k], up[k + 1], maxRefineAngleRad);

            subPts.Resize(count, NativeArrayOptions.UninitializedMemory);

            subPts[0] = ring[0];
            int w = 1;
            for (int k = 0; k < n - 1; k++)
            {
                int     segs = SegmentSteps(up[k], up[k + 1], maxRefineAngleRad);
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

        /// <summary>Number of equal sub-segments a centerline segment is split into so its projected arc (the angle
        /// between the two unit surface normals) stays under <paramref name="maxRefineAngleRad"/>; ≥1, capped at
        /// <see cref="MaxCurveSegments"/>. A ∞ tolerance (flat projection) always yields 1 (no subdivision).</summary>
        private static int SegmentSteps(double3 upA, double3 upB, double maxRefineAngleRad)
        {
            double ang  = math.acos(math.clamp(math.dot(upA, upB), -1.0, 1.0));
            int    segs = (int)math.ceil(ang / maxRefineAngleRad);
            if (segs < 1) segs = 1;
            if (segs > MaxCurveSegments) segs = MaxCurveSegments;
            return segs;
        }
    }
}
