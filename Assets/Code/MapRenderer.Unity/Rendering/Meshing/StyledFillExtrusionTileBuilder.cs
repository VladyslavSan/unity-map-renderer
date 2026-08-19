using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
                                                        // 'Color' ambiguous with UnityEngine's

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// S23 I2b managed fill-extrusion mesh builder: roof cap + side walls, with VS height extrusion (the
    /// mesh itself is <b>height-agnostic</b> — see the class-level invariant below). Mirrors
    /// <see cref="StyledFillTileBuilder"/>'s shape (managed color/bake eval → Burst geometry → alloc-free
    /// stream write), but the two geometry kinds diverge structurally:
    ///
    /// <list type="bullet">
    /// <item><b>Roof</b> reuses <see cref="FillMeshPipeline.Schedule"/> UNCHANGED (the same earcut+project
    /// chain the flat fill uses) — the roof is exactly a fill polygon at elevation 0, extruded in the VS.</item>
    /// <item><b>Walls</b> are NOT earcut output: they walk the RAW ring vertex sequences directly off the
    /// <b>borrowed</b> <see cref="TileGeometryBuffers"/> (before triangulation, so no bridge-copy duplicate
    /// vertices or hole-order permutation), one quad per boundary edge (both exterior AND hole rings — a
    /// courtyard hole needs interior walls too). Each edge's world position is projected on the spot via
    /// <see cref="TileToGeoJob.GeoAt"/> + <see cref="MapRenderer.Core.Geo.IProjection.ProjectPoint"/> (plain
    /// C# loop, not a scheduled job — wall vertex counts are small next to a tile's triangulated roof).</item>
    /// </list>
    ///
    /// <para><b>metres→world (OQ1, C1-A):</b> height is applied ENTIRELY in the vertex shader along a baked
    /// per-vertex <c>extrudeUp</c> stream — <c>unit-up × sec(φ_vertex)</c> on the flat Web-Mercator sheet
    /// (the standard conformal point-scale factor; <c>1.0</c> at the equator, ~2 at φ=60°), <c>1.0</c> on the
    /// globe (true ECEF metres). <c>extrudeUp</c> is baked PER-VERTEX, not per-tile, because a low-zoom tile
    /// can span enough latitude that a single sec φ would be visibly wrong at one end (see T2 in the mesh
    /// tests). It is a SEPARATE stream from the lighting normal (roof=up, wall=outward-horizontal) — never
    /// overload the two.</para>
    ///
    /// <para><b><c>WebMercator.Forward</c> caveat (OQ1 flag, recorded not fixed):</b>
    /// <c>WebMercator.Forward</c>'s <c>altitude → +Y</c> pass-through is 1:1, which would be wrong under
    /// sec φ — but it is unexercised here: this builder projects every footprint vertex at altitude 0
    /// (<c>WebMercatorProjection.ProjectPoint</c> hardcodes <c>Altitude = 0.0</c>) and applies height
    /// entirely in the VS via the baked <c>extrudeUp</c> stream, never through <c>Forward</c>'s altitude
    /// argument. If elevated SURFACE geometry (terrain) ever routes through <c>Forward</c>, it must adopt
    /// sec φ to match.</para>
    ///
    /// <para><b>Globe wall chording (recorded, not fixed):</b> wall quads are flat (2 triangles, no
    /// curvature subdivision) even on the globe — unlike the roof, which subdivides via
    /// <see cref="GlobeFillSubdivideDispatch"/>. A wall's height (tens of metres) is negligible next to a
    /// chord's sag at building zooms; the sag only becomes visible at very low zoom, where fill-extrusion
    /// layers are not normally styled. Wall subdivision is out of scope for I2b.</para>
    ///
    /// <para><b>Wall winding (calibrated against the render gate):</b> the fill roof's CCW-in-tile-space
    /// convention is for a HORIZONTAL surface; a vertical wall quad's vertex order and outward-normal sign
    /// are an INDEPENDENT choice with no existing convention to inherit. The two knobs are ORTHOGONAL and
    /// only one is a real lever: (a) the emitted triangle winding sets the render front face, and (b) the
    /// <c>outward</c> lighting normal (<c>cross(edgeDir, up)</c>) sets shading. The floor-A/B ring order is a
    /// non-lever — swapping it negates both together. The calibration point is (b): <c>outward</c> must point
    /// away from the interior, which <c>StyledFillExtrusionMeshTests</c> pins against the fixture centroid
    /// (handedness-free, per §7.1's lesson) before its absolute winding check can mean anything.</para>
    ///
    /// Clean-room: design follows the MapLibre Style Spec + this repo's own §2/§7 math. No MapLibre source
    /// read.
    /// </summary>
    public static class StyledFillExtrusionTileBuilder
    {
        // Skip Unity's main-thread index validation / bounds recompute — same contract as StyledFillTileBuilder.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        private static readonly IProjection DefaultProjection = new WebMercatorProjection();

        /// <summary>
        /// Interleaved stream-0 payload: footprint position (elevation-0, world/origin-relative) + the
        /// LIGHTING normal (roof: mesh-supplied surface up; wall: outward-horizontal — see the class doc).
        /// Mirrors <see cref="StyledFillTileBuilder.FillPositionNormal"/>'s shape.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PositionNormal
        {
            public Vector3 Position;
            public Vector3 Normal;
        }

        /// <summary>
        /// Interleaved stream-1 payload: the D1 extrusion inputs. <c>ExtrudeUpAndT.xyz</c> is the
        /// <c>sec(φ)</c>-baked extrude-up direction (unit-up × the metres→world factor — see the class doc);
        /// <c>ExtrudeUpAndT.w</c> is <c>t</c> (0 = floor/base, 1 = roof/height). <c>BakedBaseHeight</c> is the
        /// per-vertex data-driven bake (S12): <c>x</c> = evaluated fill-extrusion-base, <c>y</c> = evaluated
        /// fill-extrusion-height, each 0 when that property is constant/zoom (the uniform carries it
        /// instead) — the VS composes them ADDITIVELY (<c>lerp(_ExtrusionBase + x, _ExtrusionHeight + y, t)</c>),
        /// so uniform-only and bake-only both reduce to the plain uniform path when the other is zero.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtrudeAndBake
        {
            public Vector4 ExtrudeUpAndT;
            public Vector2 BakedBaseHeight;
        }

        // Position(f3)+Normal(f3) on stream 0; Tangent(f4) on stream 2; Color(f4) on stream 3;
        // ExtrudeUpAndT(f4)+BakedBaseHeight(f2) on stream 1 (one interleaved struct, two descriptor
        // entries). 4 streams total — Unity's max. Declared in ASCENDING VertexAttribute enum order
        // (Position=0, Normal=1, Tangent=2, Color=3, TexCoord3=7, TexCoord4=8) regardless of stream
        // grouping — same "avoid the non-standard-order warning" convention as
        // StyledFillTileBuilder.FillVertexDescriptors. staticLightmapUV/dynamicLightmapUV/pattern-UV
        // (TEXCOORD0-2, declared in the shader's Attributes structs only for URP-variant compile
        // completeness) are DELIBERATELY not supplied here — Unity zero-fills an absent vertex stream,
        // exactly like StyledFillTileBuilder's mesh already omits TexCoord1/2 today.
        private static readonly VertexAttributeDescriptor[] VertexDescriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent,   VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 2, stream: 1),
        };

        // Roof tangent: constant +X (Mercator east) — same convention StyledFillTileBuilder's FlatTangent
        // uses; the globe roof path overrides per-vertex with the projection's own east (mirrors WriteGlobeSubdivided).
        private static readonly Vector4 FlatRoofTangent = new Vector4(1f, 0f, 0f, 1f);

        /// <summary>
        /// Build the mesh for all fill-extrusion features of one style layer (roof + walls) and write it
        /// directly into <paramref name="md"/>. Mirrors <see cref="StyledFillTileBuilder.WriteMeshData"/>'s
        /// contract (same borrowed-geometry rules, same MeshData/out-param shape) minus fill-sort-key (no
        /// fill-extrusion equivalent) and fill-pattern (spec has none for this layer).
        /// </summary>
        /// <param name="md">Caller-allocated <c>Mesh.MeshData</c> (count-1 slot) to write into.</param>
        /// <param name="selectedFeatures">This layer's already-selected, already-filtered features.</param>
        /// <param name="geometry">The tile's shared geometry buffers — BORROWED; never disposed here.</param>
        /// <param name="paint">The layer's parsed fill-extrusion paint properties.</param>
        /// <param name="zoom">Current display zoom, for per-feature paint evaluation.</param>
        /// <param name="tileOriginRender">The RTC render-space bake origin (docs §5).</param>
        /// <param name="vertexCount">Out: total vertices written (0 ⇒ <paramref name="md"/> left untouched).</param>
        /// <param name="bounds">Out: the worker-computed tight AABB.</param>
        /// <param name="projection">The build projection; <c>null</c> ⇒ Web Mercator.</param>
        /// <param name="clip">How much of the tile buffer to keep before triangulating; <c>default</c> ⇒ disabled.</param>
        /// <param name="scratch">Unused by this builder today (no pooled scratch path yet) — accepted only
        /// to match <see cref="Style.ITileMeshRenderLayer.WriteInto"/>'s call shape.</param>
        public static void WriteMeshData(
            Mesh.MeshData                      md,
            IReadOnlyList<SelectedTileFeature> selectedFeatures,
            TileGeometryBuffers                geometry,
            FillExtrusion.PaintProperties       paint,
            double                              zoom,
            double3                              tileOriginRender,
            out int                              vertexCount,
            out Bounds                           bounds,
            IProjection                          projection = null,
            TileBufferClip                       clip       = default,
            TileBuildScratch                     scratch    = null)
        {
            vertexCount = 0;
            bounds      = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0 || !geometry.IsCreated)
                return;

            IProjection proj = projection ?? DefaultProjection;

            // ── Per-feature color + data-driven base/height bake + rank (mirrors StyledFillTileBuilder) ──
            using var featureColors = new NativeArray<Vector4>(geometry.FeatureCount, Allocator.Persistent);
            using var featureBake   = new NativeArray<Vector2>(geometry.FeatureCount, Allocator.Persistent); // x=base, y=height
            using var rankByOrdinal = new NativeArray<int>(geometry.FeatureCount, Allocator.Persistent);
            NativeArray<Vector4> featureColorsW = featureColors.GetSubArray(0, featureColors.Length);
            NativeArray<Vector2> featureBakeW   = featureBake.GetSubArray(0, featureBake.Length);
            NativeArray<int>     rankByOrdinalW = rankByOrdinal.GetSubArray(0, rankByOrdinal.Length);
            for (int i = 0; i < rankByOrdinalW.Length; i++) rankByOrdinalW[i] = -1; // -1 ⇒ not drawn

            int rank = 0;
            for (int si = 0; si < selectedFeatures.Count; si++)
            {
                SelectedTileFeature selected = selectedFeatures[si];
                IFeature feature = selected.Feature;
                if (feature.GeometryType != TileGeometryType.Polygon)
                    continue;

                Color featureColor = Color.white;
                if (paint.Color.TryEvaluate(zoom, feature, out var color))
                    featureColor = new Color((float)color.R, (float)color.G, (float)color.B, (float)color.A);
                Color lin = featureColor.linear; // sRGB→linear off main thread, same as StyledFillTileBuilder

                float featureAlpha = lin.a;
                if (paint.Opacity.DependsOnFeature && paint.Opacity.TryEvaluate(zoom, feature, out float opacity))
                    featureAlpha *= opacity;
                featureColorsW[selected.Ordinal] = new Vector4(lin.r, lin.g, lin.b, featureAlpha);

                // Data-driven base/height: bake the EVALUATED value; constant/zoom stays at 0 here (the
                // uniform carries it — see BindFillExtrusionPaintToApplier and ExtrudeAndBake's doc).
                float bakedBase = 0f, bakedHeight = 0f;
                if (paint.Base.DependsOnFeature && paint.Base.TryEvaluate(zoom, feature, out float b))
                    bakedBase = b;
                if (paint.Height.DependsOnFeature && paint.Height.TryEvaluate(zoom, feature, out float h))
                    bakedHeight = h;
                featureBakeW[selected.Ordinal] = new Vector2(bakedBase, bakedHeight);

                rankByOrdinalW[selected.Ordinal] = rank++;
            }

            if (rank == 0)
                return; // no polygon geometry — md left untouched

            using var ringVisitOrder = BuildRingVisitOrder(geometry, rankByOrdinal, rank);

            // ── Roof: the existing fill pipeline, UNCHANGED. ──────────────────────────────────────
            TileMeshBuffers roofBuffers = FillMeshPipeline.Schedule(new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = ringVisitOrder,
                OriginRender   = tileOriginRender,
                Projection     = proj,
                Clip           = clip,
            });

            using var outPosNormal = new NativeList<PositionNormal>(Allocator.Persistent);
            using var outExtrude   = new NativeList<ExtrudeAndBake>(Allocator.Persistent);
            using var outTangent   = new NativeList<Vector4>(Allocator.Persistent);
            using var outColor     = new NativeList<Vector4>(Allocator.Persistent);
            using var outIndices   = new NativeList<int>(Allocator.Persistent);

            try
            {
                if (roofBuffers.IsCreated)
                {
                    bool globe = !double.IsInfinity(proj.MaxRefineAngleRad);
                    if (globe)
                        WriteGlobeRoof(in roofBuffers, proj, geometry.Tile, geometry.Extent, tileOriginRender, featureColors, featureBake,
                            outPosNormal, outExtrude, outTangent, outColor, outIndices);
                    else
                        WriteFlatRoof(in roofBuffers, geometry.Tile, geometry.Extent, featureColors, featureBake,
                            outPosNormal, outExtrude, outTangent, outColor, outIndices);
                }

                WriteWalls(geometry, ringVisitOrder, proj, tileOriginRender, featureColors, featureBake,
                    outPosNormal, outExtrude, outTangent, outColor, outIndices);

                int n  = outPosNormal.Length;
                int ni = outIndices.Length;
                if (n == 0 || ni == 0)
                    return;

                md.SetVertexBufferParams(n, VertexDescriptors);
                NativeArray<PositionNormal>  s0 = md.GetVertexData<PositionNormal>(0);
                NativeArray<ExtrudeAndBake>  s1 = md.GetVertexData<ExtrudeAndBake>(1);
                NativeArray<Vector4>         s2 = md.GetVertexData<Vector4>(2);
                NativeArray<Vector4>         s3 = md.GetVertexData<Vector4>(3);
                md.SetIndexBufferParams(ni, IndexFormat.UInt32);
                NativeArray<int> indices = md.GetIndexData<int>();

                float3 bMin = new float3(float.MaxValue);
                float3 bMax = new float3(float.MinValue);
                for (int i = 0; i < n; i++)
                {
                    PositionNormal pn = outPosNormal[i];
                    s0[i] = pn;
                    s1[i] = outExtrude[i];
                    s2[i] = outTangent[i];
                    s3[i] = outColor[i];

                    float3 p = new float3(pn.Position.x, pn.Position.y, pn.Position.z);
                    bMin = math.min(bMin, p);
                    bMax = math.max(bMax, p);
                }
                for (int i = 0; i < ni; i++) indices[i] = outIndices[i];

                md.subMeshCount = 1;
                md.SetSubMesh(0, new SubMeshDescriptor(0, ni, MeshTopology.Triangles), NoValidate);

                vertexCount = n;
                float3 c3 = (bMin + bMax) * 0.5f;
                float3 sz = bMax - bMin;
                bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
            }
            finally
            {
                roofBuffers.Dispose();
            }
        }

        /// <summary>
        /// The rings this layer wants drawn, in draw order — a plain declared-order selection (no
        /// fill-sort-key equivalent for fill-extrusion), otherwise identical in shape to
        /// <see cref="StyledFillTileBuilder"/>'s counting-sort ring-visit builder: rings of one feature stay
        /// contiguous (<c>RingAssemblyJob</c> resets its exterior sign on a feature change).
        /// </summary>
        private static NativeArray<int> BuildRingVisitOrder(
            TileGeometryBuffers geometry, NativeArray<int> rankByOrdinal, int rankCount)
        {
            var rankStart = new int[rankCount + 1];
            int visited = 0;
            for (int r = 0; r < geometry.RingCount; r++)
            {
                int rk = rankByOrdinal[geometry.RingFeatureIdx[r]];
                if (rk < 0) continue;
                rankStart[rk + 1]++;
                visited++;
            }
            for (int i = 0; i < rankCount; i++) rankStart[i + 1] += rankStart[i];

            var order  = new NativeArray<int>(visited, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var cursor = (int[])rankStart.Clone();
            for (int r = 0; r < geometry.RingCount; r++)
            {
                int rk = rankByOrdinal[geometry.RingFeatureIdx[r]];
                if (rk < 0) continue;
                order[cursor[rk]++] = r;
            }
            return order;
        }

        /// <summary>metres→world factor at one vertex (OQ1): 1.0 on the globe (true ECEF metres); the Web
        /// Mercator point-scale factor sec(φ) on the flat sheet (φ = the vertex's OWN latitude — a low-zoom
        /// tile can span enough latitude that a per-tile constant would be visibly wrong; see T2).</summary>
        private static double MetresToWorldFactor(bool globe, double latitudeDegrees)
            => globe ? 1.0 : 1.0 / math.cos(latitudeDegrees * math.PI_DBL / 180.0);

        // ── Roof: flat (Mercator) path ─────────────────────────────────────────────────────────────
        private static void WriteFlatRoof(
            in TileMeshBuffers buffers, TileId tile, double extent,
            NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            NativeList<PositionNormal> outPosNormal, NativeList<ExtrudeAndBake> outExtrude,
            NativeList<Vector4> outTangent, NativeList<Vector4> outColor, NativeList<int> outIndices)
        {
            int totalVerts   = buffers.VertexCount[0];
            int totalIndices = buffers.TotalIndexCount;
            if (totalVerts == 0 || totalIndices == 0) return;

            int baseIndex = outPosNormal.Length;
            for (int i = 0; i < totalVerts; i++)
            {
                float3 v  = (float3)buffers.WorldPositions[i];
                float3 up = (float3)buffers.VertexUp[i]; // unit — StyledFillTileBuilder's normal convention
                double2 tv = buffers.TileVertices[i];
                double lat = TileToGeoJob.GeoAt(tile, extent, tv).Latitude;
                double f   = MetresToWorldFactor(globe: false, lat);

                outPosNormal.Add(new PositionNormal { Position = new Vector3(v.x, v.y, v.z), Normal = new Vector3(up.x, up.y, up.z) });
                outExtrude.Add(new ExtrudeAndBake
                {
                    ExtrudeUpAndT   = new Vector4((float)(up.x * f), (float)(up.y * f), (float)(up.z * f), 1f), // t=1 roof
                    BakedBaseHeight = featureBake[buffers.VertexFeatureIdx[i]],
                });
                outTangent.Add(FlatRoofTangent);
                outColor.Add(featureColors[buffers.VertexFeatureIdx[i]]);
            }

            // Reverse triangle winding at this GPU-index boundary — same convention as StyledFillTileBuilder
            // (canonical earcut IR is CCW-in-tile-space; swap 2nd/3rd index for Unity-front under Cull Back).
            for (int i = 0; i + 2 < totalIndices; i += 3)
            {
                outIndices.Add(baseIndex + buffers.TriangleIndices[i + 0]);
                outIndices.Add(baseIndex + buffers.TriangleIndices[i + 2]);
                outIndices.Add(baseIndex + buffers.TriangleIndices[i + 1]);
            }
        }

        // ── Roof: globe (curved) path — mirrors StyledFillTileBuilder.WriteGlobeSubdivided ────────
        private static void WriteGlobeRoof(
            in TileMeshBuffers buffers, IProjection proj, TileId tile, double extent, double3 originRender,
            NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            NativeList<PositionNormal> outPosNormal, NativeList<ExtrudeAndBake> outExtrude,
            NativeList<Vector4> outTangent, NativeList<Vector4> outColor, NativeList<int> outIndices)
        {
            int srcVerts   = buffers.VertexCount[0];
            int srcIndices = buffers.TotalIndexCount;
            if (srcVerts == 0 || srcIndices == 0) return;

            using var subV  = new NativeList<GlobeFillVertex>(srcVerts * 4, Allocator.Persistent);
            using var subIx = new NativeList<int>(srcIndices * 4, Allocator.Persistent);

            GlobeFillSubdivideDispatch.Run(
                proj, buffers.TileVertices, buffers.TriangleIndices, buffers.VertexFeatureIdx,
                srcVerts, srcIndices, tile, extent, originRender,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxOutputVertices, subV, subIx);

            int n = subV.Length, ni = subIx.Length;
            if (n == 0 || ni == 0) return;

            int baseIndex = outPosNormal.Length;
            for (int i = 0; i < n; i++)
            {
                GlobeFillVertex fv = subV[i];
                float3 v    = (float3)fv.World;
                float3 up   = (float3)fv.Up; // unit
                float3 east = (float3)fv.East;
                // Globe factor = 1.0 (OQ1) — extrudeUp is the plain surface up, un-scaled.
                outPosNormal.Add(new PositionNormal { Position = new Vector3(v.x, v.y, v.z), Normal = new Vector3(up.x, up.y, up.z) });
                outExtrude.Add(new ExtrudeAndBake
                {
                    ExtrudeUpAndT   = new Vector4(up.x, up.y, up.z, 1f), // t=1 roof
                    BakedBaseHeight = featureBake[fv.Feature],
                });
                outTangent.Add(new Vector4(east.x, east.y, east.z, 1f)); // matches the flat-fill globe TBN handedness
                outColor.Add(featureColors[fv.Feature]);
            }

            for (int i = 0; i + 2 < ni; i += 3)
            {
                outIndices.Add(baseIndex + subIx[i + 0]);
                outIndices.Add(baseIndex + subIx[i + 2]);
                outIndices.Add(baseIndex + subIx[i + 1]);
            }
        }

        // ── Walls: one quad per boundary edge, over the RAW (pre-earcut) ring vertices ─────────────
        //
        // Reads geometry.Vertices/RingOffsets/RingFeatureIdx directly off the BORROWED shared buffer — the
        // same rings ringVisitOrder names for the roof, but walked in their OWN stored order (never earcut's
        // merged/bridged order, which duplicates and reorders boundary vertices). Rings are implicitly
        // closed (MvtDecodeJob: "first vertex not repeated") — edges wrap via (i+1)%len, matching
        // RingAssemblyJob's own area/shoelace convention.
        private static void WriteWalls(
            TileGeometryBuffers geometry, NativeArray<int> ringVisitOrder, IProjection proj, double3 originRender,
            NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            NativeList<PositionNormal> outPosNormal, NativeList<ExtrudeAndBake> outExtrude,
            NativeList<Vector4> outTangent, NativeList<Vector4> outColor, NativeList<int> outIndices)
        {
            bool globe = !double.IsInfinity(proj.MaxRefineAngleRad);
            TileId tile = geometry.Tile;
            double extent = geometry.Extent;

            for (int k = 0; k < ringVisitOrder.Length; k++)
            {
                int ri = ringVisitOrder[k];
                int start = geometry.RingOffsets[ri];
                int len   = geometry.RingOffsets[ri + 1] - start;
                if (len < 2) continue; // degenerate ring — no edges

                Vector4 color = featureColors[geometry.RingFeatureIdx[ri]];
                Vector2 bake  = featureBake[geometry.RingFeatureIdx[ri]];

                for (int i = 0; i < len; i++)
                {
                    // A = this ring vertex, B = the next (edges wrap via (i+1)%len). The A/B order does not
                    // affect front-face correctness: swapping it negates BOTH the geometric face normal and
                    // the 'outward' lighting normal below, leaving their alignment (and the render winding)
                    // invariant — see the class doc's "Wall winding" note.
                    int idxA = i;
                    int idxB = (i + 1) % len;

                    double2 tileA = geometry.Vertices[start + idxA];
                    double2 tileB = geometry.Vertices[start + idxB];

                    GeoCoordinate geoA = TileToGeoJob.GeoAt(tile, extent, tileA);
                    GeoCoordinate geoB = TileToGeoJob.GeoAt(tile, extent, tileB);
                    ProjectedPoint ppA = proj.ProjectPoint(in geoA);
                    ProjectedPoint ppB = proj.ProjectPoint(in geoB);

                    float3 posA = (float3)(ppA.World - originRender);
                    float3 posB = (float3)(ppB.World - originRender);
                    float3 upA  = math.normalize((float3)ppA.Up);
                    float3 upB  = math.normalize((float3)ppB.Up);

                    double factorA = MetresToWorldFactor(globe, geoA.Latitude);
                    double factorB = MetresToWorldFactor(globe, geoB.Latitude);
                    float3 extrudeUpA = upA * (float)factorA;
                    float3 extrudeUpB = upB * (float)factorB;

                    // Outward-horizontal lighting normal + along-edge tangent. cross(edgeDir, up) (NOT
                    // cross(up, edgeDir)) points AWAY from the interior — verified against the fixture
                    // centroid in StyledFillExtrusionMeshTests, not derived from handedness (§7.1's lesson).
                    float3 upAvg   = math.normalize(upA + upB);
                    float3 edgeDir = math.normalize(posB - posA);
                    float3 outward = math.normalize(math.cross(edgeDir, upAvg));
                    Vector3 outwardV3 = new Vector3(outward.x, outward.y, outward.z);
                    Vector4 tangentV4 = new Vector4(edgeDir.x, edgeDir.y, edgeDir.z, 1f);

                    // 4 verts: floorA(t=0), floorB(t=0), roofB(t=1), roofA(t=1). Floor/roof at the SAME
                    // point share the SAME computed Position (T1: height-agnostic footprint) — differ only
                    // by t (+ identical extrudeUp).
                    int quadBase = outPosNormal.Length;
                    AddWallVertex(posA, outwardV3, extrudeUpA, 0f, bake, tangentV4, color, outPosNormal, outExtrude, outTangent, outColor);
                    AddWallVertex(posB, outwardV3, extrudeUpB, 0f, bake, tangentV4, color, outPosNormal, outExtrude, outTangent, outColor);
                    AddWallVertex(posB, outwardV3, extrudeUpB, 1f, bake, tangentV4, color, outPosNormal, outExtrude, outTangent, outColor);
                    AddWallVertex(posA, outwardV3, extrudeUpA, 1f, bake, tangentV4, color, outPosNormal, outExtrude, outTangent, outColor);

                    // quadBase+0=floorA, +1=floorB, +2=roofB, +3=roofA — two triangles whose winding puts the
                    // render front face along `outward` (pinned by StyledFillExtrusionMeshTests' T6).
                    outIndices.Add(quadBase + 0); outIndices.Add(quadBase + 1); outIndices.Add(quadBase + 2);
                    outIndices.Add(quadBase + 0); outIndices.Add(quadBase + 2); outIndices.Add(quadBase + 3);
                }
            }
        }

        private static void AddWallVertex(
            float3 position, Vector3 normal, float3 extrudeUp, float t, Vector2 bake, Vector4 tangent, Vector4 color,
            NativeList<PositionNormal> outPosNormal, NativeList<ExtrudeAndBake> outExtrude,
            NativeList<Vector4> outTangent, NativeList<Vector4> outColor)
        {
            outPosNormal.Add(new PositionNormal { Position = new Vector3(position.x, position.y, position.z), Normal = normal });
            outExtrude.Add(new ExtrudeAndBake
            {
                ExtrudeUpAndT   = new Vector4(extrudeUp.x, extrudeUp.y, extrudeUp.z, t),
                BakedBaseHeight = bake,
            });
            outTangent.Add(tangent);
            outColor.Add(color);
        }
    }
}
