using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Tiles;
using Line = MapRenderer.Core.Style.Line;
using CoreColor = MapRenderer.Core.Expressions.Color;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
                                                        // 'Color' ambiguous with UnityEngine's

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// S14 managed per-layer line mesh builder. Mirrors <see cref="StyledFillTileBuilder"/> but for
    /// line-type style layers.
    ///
    /// Pipeline (S100, IR B3; IR C1 P2/P3): BORROW the source-layer's shared tile-local
    ///   <see cref="TileGeometryBuffers"/> (Waist 1 — materialized once inside the DECODE and owned by the
    ///   decoded layer, lent to every style layer naming that source-layer) → iterate ring
    ///   spans, filtered to this layer's <b>selection</b> (by <see cref="SelectedTileFeature.Ordinal"/>) and to
    ///   <c>FeatureGeometryType == LineString</c> and span <c>&gt;= 2</c> → curvature-subdivide the centerline
    ///   in tile space per the projection's <see cref="IProjection.MaxRefineAngleRad"/> policy (a no-op for the
    ///   flat Mercator, whose tolerance is ∞) → project the subdivided centerline to an origin-relative
    ///   render-space <c>(point, up)</c> ARRAY through the managed <see cref="IProjection.ProjectPoint"/> →
    ///   Burst <see cref="LineRibbonJob"/> (projection-agnostic; builds the ribbon in 3D with
    ///   <c>across = cross(along, up)</c>) → stream write into a <c>Mesh.MeshData</c>.
    ///
    /// <para>Line does its own ring iteration and has <b>zero</b> interaction with <c>RingAssemblyJob</c> /
    /// <c>EarcutJob</c> / <c>FillMeshPipeline</c>: it has no polygon or hole concept, and its filter is a
    /// COUNT threshold (<c>&gt;= 2</c>), never an area threshold — a straight polyline has exactly zero
    /// signed area and fill's degenerate-area filter would drop it. The two consumers read the same
    /// unfiltered buffer through different filters; that is the point of the buffer being unfiltered.</para>
    ///
    /// <para>ONE code path for every projection. Winding is correct BY CONSTRUCTION — <c>across</c> is tied to the
    /// same <c>up</c> the centerline was projected with (one frame), so there is no per-projection winding flip,
    /// no separate tangent-basis second frame, and no curvature/handedness branch. The managed
    /// <see cref="LineTessellator"/> is the planar differential oracle for <see cref="LineRibbonJob"/>
    /// (<c>LineRibbonJobTests</c>). This line ordering (Subdivide → Project → Triangulate) mirrors the
    /// fill one; see <c>GlobeLineWindingTests</c>.</para>
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
        ///
        /// <para><b>No <c>TileId</c> and no <c>extent</c> parameter</b> (IR C1 P2): both are read off
        /// <paramref name="geometry"/>, the producer's own declaration, exactly as fill already does. A second
        /// copy alongside the buffer is what would let a caller pair a z0 buffer with a z1 address.</para>
        /// </summary>
        public static void WriteMeshData(
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

            if (selectedFeatures == null || selectedFeatures.Count == 0 || !geometry.IsCreated)
                return;

            // S60: Join/Cap are already parsed enums on LayoutProperties (no per-build string switch).
            JoinType joinType = layout.Join;
            CapType  capType  = layout.Cap;

            // ── Waist 1 pre-pass: three index-aligned per-feature columns over the WHOLE source layer. ──
            // The buffer is shared with every other style layer naming this source-layer, so it holds every
            // feature of the layer — not just this layer's selection, and not just the LineStrings. Its
            // FeatureGeometryType column is therefore what makes the ring kind gate below DISCRIMINATING.
            //
            // IR C1 P2: the columns are sized to the LAYER and indexed by SelectedTileFeature.Ordinal, because
            // the ordinal is what the buffer's RingFeatureIdx names. A slot-indexed column would permute
            // colours and widths the moment this layer's filter rejects anything — the geometry would stay
            // right and only the attribution would be wrong.
            var featColors = new Vector4[geometry.FeatureCount];
            var featWidths = new float[geometry.FeatureCount];
            // Selection membership, by ordinal. Kept SEPARATE from the kind gate below rather than folded into
            // it: the two are independent guards over the same ring loop and each is RED-verifiable on its own
            // (the same split StyledFillTileBuilder makes between `rank == -1` and its polygon check).
            var featSelected = new bool[geometry.FeatureCount];

            for (int si = 0; si < selectedFeatures.Count; si++)
            {
                SelectedTileFeature selected = selectedFeatures[si];
                IFeature            feature  = selected.Feature;
                int                 ordinal  = selected.Ordinal;

                featSelected[ordinal] = true;
                featColors[ordinal]   = WhiteColor;
                featWidths[ordinal]   = 1f;

                // The paint bakes stay LINE-ONLY, exactly as before the rewire: a non-LineString feature's
                // rings are dropped by the kind gate, so evaluating its expressions would be new work with no
                // output.
                if (feature.GeometryType != TileGeometryType.LineString)
                    continue;

                // Bake per-feature vertex color from the data-driven paint expression (sRGB→linear here,
                // off-main-thread). _BaseColor=white on the material → identity multiply.
                if (paint.Color.TryEvaluate(zoom, feature, out CoreColor c))
                {
                    var unityColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
                    var linear     = unityColor.linear;
                    featColors[ordinal] = new Vector4(linear.r, linear.g, linear.b, linear.a);
                }

                // S14 data-driven opacity: bake evaluated opacity into vertex alpha, ONLY when opacity depends
                // on the feature (else BindLinePaintToApplier already bound _Opacity and baking double-applies).
                if (paint.Opacity.DependsOnFeature)
                {
                    if (paint.Opacity.TryEvaluate(zoom, feature, out float opacityVal))
                        featColors[ordinal].w *= opacityVal;
                }

                // S14 data-driven width: bake evaluated width into WidthScale (multiplier on _Width). When
                // width depends on the feature, BindLinePaintToApplier binds _Width as a device-px constant
                // of 1 — so _Width == the device-pixel ratio (1.0 at dpr 1) and WidthScale carries the full
                // evaluated LOGICAL pixel width; otherwise WidthScale stays the ribbon's factor. The bake is
                // deliberately dpr-free (S107): scaling it would put the ratio inside the geometry, so a live
                // ratio change would need a full mesh rebuild and PreparedTileCache would serve it stale.
                if (paint.Width.DependsOnFeature)
                {
                    if (paint.Width.TryEvaluate(zoom, feature, out float widthVal))
                        featWidths[ordinal] = math.max(0f, widthVal);
                }
            }

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
            for (int r = 0; r < geometry.RingCount; r++)
            {
                int featIdx = geometry.RingFeatureIdx[r];

                // IR C1 P2: the buffer spans the WHOLE source layer, so rings of features this layer's filter
                // rejected are in it and must be skipped. Before the conversion the buffer was minted from the
                // selection itself, so this predicate was true by construction and there was nothing to gate.
                if (!featSelected[featIdx]) continue;

                // Ring kind is NOT recoverable from the coordinates — a LineString ring and a polygon ring are
                // the same shape of data. This is the same test the old per-feature `continue` made, moved onto
                // the shared buffer: the buffer carries EVERY feature's rings (that is what makes it
                // shareable), so the KIND filter is the consumer's. Deliberately a SECOND, independent gate
                // beside the membership one above — a selected Polygon passes that and must fail this.
                if (geometry.FeatureGeometryType[featIdx] != TileGeometryType.LineString) continue;

                int rStart = geometry.RingOffsets[r];
                int n      = geometry.RingOffsets[r + 1] - rStart;

                // LINE's OWN length filter, and it is `< 2`, NOT fill's `< 3`. It lives here and nowhere
                // upstream: a filter in the shared decode stage would starve fill (which needs every ring
                // >= 3) or line (which needs every ring >= 2). Two consumers, two thresholds, one unfiltered
                // buffer.
                if (n < 2) continue;

                Vector4 featureColor      = featColors[featIdx];
                float   featureWidthScale = featWidths[featIdx];

                // 1) Project the ORIGINAL ring to per-point surface ups (the subdivision metric). Tile→geo via
                //    the shared projection-independent job; geo→up via the managed ProjectPoint (works for any
                //    IProjection, incl. types never registered for Burst — the S100 decisive tooth).
                inTile.Resize(n, NativeArrayOptions.UninitializedMemory);
                geoOrig.Resize(n, NativeArrayOptions.UninitializedMemory);
                upOrig.Resize(n, NativeArrayOptions.UninitializedMemory);
                for (int k = 0; k < n; k++) inTile[k] = geometry.Vertices[rStart + k];
                new TileToGeoJob
                {
                    Tile = geometry.Tile, Extent = geometry.Extent,
                    TileCoords = inTile.AsArray(), OutGeo = geoOrig.AsArray(),
                }.Run(n);
                for (int k = 0; k < n; k++) upOrig[k] = projection.ProjectPoint(geoOrig[k]).Up;

                // 2) Curvature-subdivide the centerline in TILE space (linear sub-points that project onto the
                //    surface). Driven entirely by the projection's tolerance — ∞ ⇒ 1 step/segment ⇒ the
                //    original ring, so the flat Mercator path is the degenerate value, not a branch.
                int m = SubdivideCenterline(inTile.AsArray(), upOrig.AsArray(), n, subTile, maxRefineAngleRad);

                // 3) Project the SUBDIVIDED centerline to the origin-relative (point, up) render-space array.
                subGeo.Resize(m, NativeArrayOptions.UninitializedMemory);
                world.Resize(m, NativeArrayOptions.UninitializedMemory);
                up.Resize(m, NativeArrayOptions.UninitializedMemory);
                new TileToGeoJob
                {
                    Tile = geometry.Tile, Extent = geometry.Extent,
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

                // Reverse triangle winding at this GPU-index boundary (mirrors the fill reversal in
                // StyledFillTileBuilder). The ribbon `across = cross(along, up)` is tied to the same `up` the
                // centerline was projected with (one frame), so ribbons wind consistently across projections;
                // but the ECEF→render mapping is orientation-reversing (§7.1), so the raw order renders
                // back-faces toward the camera. Swapping the 2nd/3rd index per triangle makes the rendered
                // front face genuinely Unity-front — enabling stock Cull Back (MapLine _Cull:2), which also
                // culls far-side globe ribbons (no more double-sided workaround).
                for (int k = 0; k + 2 < ni; k += 3)
                {
                    tempIndices.Add(offset + outI[k + 0]);
                    tempIndices.Add(offset + outI[k + 2]); // 2nd/3rd
                    tempIndices.Add(offset + outI[k + 1]); // swapped
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
                // IR C1 P2/P3: `geometry` is NOT freed here. It is BORROWED from the decoded LAYER, which
                // owns it and lends the same buffer to every style layer naming this source-layer — and to
                // the symbol pass of the same kick; disposing it here would free geometry a sibling
                // consumer is still reading. On
                // THIS path that second free is LOUD, not quiet: the store hands back an array-backed buffer
                // whose Dispose frees three real NativeArrays, and P2's R6 sweep measured the double free as
                // 32 failures across 19 fixtures — two of them with no line involvement at all, the
                // heap-corruption signature. The rule is also pinned structurally
                // (StyledLineBuilderStructureTests) so it fails on the offending LINE rather than as a
                // scatter of unrelated red fixtures.
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
        /// <para>A flat projection returns <c>maxRefineAngleRad = ∞</c>, so
        /// <see cref="LineCurvatureSubdivision.SegmentSteps"/> yields 1 step per segment and the output is the
        /// original ring — the Mercator path is the degenerate value of the SAME code, with no capability flag.</para>
        /// <para>S4: the split-count POLICY (<c>SegmentSteps</c> + its <c>MaxCurveSegments</c> cap) is shared with
        /// <see cref="MapRenderer.Unity.Text.SymbolFeatureExtractor"/> via
        /// <see cref="LineCurvatureSubdivision"/> — this method keeps its own <c>NativeList</c> densification loop
        /// (container plumbing, not policy).</para>
        /// </summary>
        internal static int SubdivideCenterline(
            NativeArray<double2> ring, NativeArray<double3> up, int n, NativeList<double2> subPts, double maxRefineAngleRad)
        {
            int count = 1; // the first point, then `segs` points per segment (sub-points + the segment end)
            for (int k = 0; k < n - 1; k++)
                count += LineCurvatureSubdivision.SegmentSteps(up[k], up[k + 1], maxRefineAngleRad);

            subPts.Resize(count, NativeArrayOptions.UninitializedMemory);

            subPts[0] = ring[0];
            int w = 1;
            for (int k = 0; k < n - 1; k++)
            {
                int     segs = LineCurvatureSubdivision.SegmentSteps(up[k], up[k + 1], maxRefineAngleRad);
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
    }
}
