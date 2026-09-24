using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
                                                        // 'Color' ambiguous with UnityEngine's

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// Managed fill-extrusion mesh builder. The roof is the fill polygon from <see cref="FillMeshGraph.Schedule"/>;
    /// the walls are one quad per edge of the raw, pre-earcut rings, holes included, built by
    /// <see cref="FillExtrusionMeshGraph.Schedule"/>. The mesh is height-agnostic: the VS extrudes along a baked
    /// <c>extrudeUp</c> stream (docs/depth-and-render-regimes-design.md § "Fill-extrusion — the degenerate case").
    /// Non-local invariant: <c>extrudeUp</c> is <c>unit-up × sec(φ)</c> PER VERTEX on the flat sheet, because a
    /// low-zoom tile spans too much latitude for one factor, and 1 on the globe; it is a separate stream from the
    /// lighting normal. Limitation: <c>WebMercator.Forward</c> passes altitude through 1:1, ignoring sec φ; this
    /// builder projects at altitude 0, so terrain routed through <c>Forward</c> must adopt sec φ. Limitation:
    /// globe walls are flat quads, not subdivided, so their chord sag shows only at very low zoom. The wall's
    /// <c>outward</c> normal must point away from the interior, which <c>StyledFillExtrusionMeshTests</c> pins.
    /// </summary>
    public static partial class StyledFillExtrusionTileBuilder
    {
        // Skip Unity's main-thread index validation / bounds recompute — same contract as StyledFillTileBuilder.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // internal, not private: a test-assembly caller (via InternalsVisibleTo) reads this default the
        // same way WriteMeshData, which lives in the test assembly, does.
        internal static readonly IProjection DefaultProjection = new WebMercatorProjection();

        /// <summary>
        /// Interleaved stream-0 payload: footprint position (elevation-0, world/origin-relative) + the
        /// LIGHTING normal (roof: mesh-supplied surface up; wall: outward-horizontal — see the class doc).
        /// Mirrors <see cref="StyledFillTileBuilder.FillPositionNormal"/>'s shape.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PositionNormal
        {
            public Vector3 Position;
            public Vector3 Normal;
        }

        /// <summary>
        /// Interleaved stream-1 payload. <c>ExtrudeUpAndT.xyz</c> is the <c>sec(φ)</c>-baked extrude-up
        /// direction; <c>.w</c> is <c>t</c> (0 = base, 1 = height). <c>BakedBaseHeight</c> is the data-driven
        /// base (x) and height (y), 0 when the uniform carries the property. The VS adds them:
        /// <c>lerp(_ExtrusionBase + x, _ExtrusionHeight + y, t)</c>.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtrudeAndBake
        {
            public Vector4 ExtrudeUpAndT;
            public Vector2 BakedBaseHeight;
        }

        /// <summary>
        /// The wall geometry <see cref="FillExtrusionMeshGraph.Schedule"/> builds alongside the roof —
        /// native columns owned by <c>TileBuildGraph.LayerBuild</c> (production) or the caller directly
        /// (a test-assembly caller, reached via <c>InternalsVisibleTo</c>), which the write step later
        /// appends after the roof (<see cref="ScheduleWrite"/>), at indices rebased by the roof's own vertex
        /// count. Indices here are WALL-LOCAL: the first wall vertex is 0.
        /// </summary>
        internal struct WallColumns
        {
            /// <summary>Stream-0 payload (footprint position + outward lighting normal) for every wall
            /// vertex, in the same interleaved layout <see cref="ScheduleWrite"/> copies into the mesh.</summary>
            public NativeList<PositionNormal> PositionNormal;

            /// <summary>Stream-1 payload (baked extrude-up + t, + the data-driven base/height bake) for
            /// every wall vertex.</summary>
            public NativeList<ExtrudeAndBake> Extrude;

            /// <summary>Stream-2 payload (the along-edge tangent) for every wall vertex.</summary>
            public NativeList<Vector4>        Tangent;

            /// <summary>Stream-3 payload (the feature's linear colour) for every wall vertex.</summary>
            public NativeList<Vector4>        Color;

            /// <summary>Wall-local triangle indices into the four lists above — see the type doc.</summary>
            public NativeList<int>            Indices;

            /// <summary>True once minted by <see cref="Allocate"/>; false for a never-allocated
            /// (<c>default</c>) instance or after <see cref="Dispose"/>. Derived from <see cref="PositionNormal"/>.
            /// Limitation: <c>NativeList.Dispose</c> clears only the disposing copy, so another copy still reads
            /// <c>true</c>; dispose through one owner.</summary>
            internal bool IsCreated => PositionNormal.IsCreated;

            internal int VertexCount => PositionNormal.Length;
            internal int IndexCount  => Indices.Length;

            private static long _liveCount;
            private static long _totalCreated;

            /// <summary>Net live <see cref="WallColumns"/> allocated by <see cref="FillExtrusionMeshGraph.Schedule"/>
            /// but not yet freed by <see cref="Dispose"/> — the <c>FillGraphOutput</c> allocate-and-count
            /// idiom.</summary>
            internal static long DebugLiveCount => Interlocked.Read(ref _liveCount);

            /// <summary>Monotonic count ever allocated — the non-vacuity witness a "back to zero" live count
            /// alone cannot provide.</summary>
            internal static long DebugTotalCreated => Interlocked.Read(ref _totalCreated);

            internal static WallColumns Allocate()
            {
                Interlocked.Increment(ref _liveCount);
                Interlocked.Increment(ref _totalCreated);
                return new WallColumns
                {
                    PositionNormal = new NativeList<PositionNormal>(Allocator.Persistent),
                    Extrude        = new NativeList<ExtrudeAndBake>(Allocator.Persistent),
                    Tangent        = new NativeList<Vector4>(Allocator.Persistent),
                    Color          = new NativeList<Vector4>(Allocator.Persistent),
                    Indices        = new NativeList<int>(Allocator.Persistent),
                };
            }

            /// <summary>Idempotent: a second call on an already-disposed or <c>default</c> instance is a no-op.
            /// <c>internal</c>, because every call site is a direct in-assembly <c>x.Dispose()</c>.</summary>
            internal void Dispose()
            {
                if (!IsCreated) return;
                PositionNormal.Dispose();
                Extrude.Dispose();
                Tangent.Dispose();
                Color.Dispose();
                Indices.Dispose();
                Interlocked.Decrement(ref _liveCount);
            }
        }

        // 4 streams, Unity's max. Declared in ascending VertexAttribute order, not stream order, to avoid the
        // non-standard-order warning. Unity zero-fills the absent TEXCOORD0-2 the shader declares.
        private static readonly VertexAttributeDescriptor[] VertexDescriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent,   VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 2, stream: 1),
        };

        /// <summary>
        /// The graph arm's prologue, mirroring <see cref="StyledFillTileBuilder.BuildLayerInput"/>: bakes each
        /// polygon feature's linear colour and data-driven base/height, and builds the ring visit order.
        /// <see cref="FillExtrusionMeshGraph.Schedule"/> builds the walls from the same <paramref name="geometry"/>.
        /// Returns <c>default</c>, with every out-param uncreated, when there is no polygon geometry. Every
        /// container is created iff the return value is; a mid-method throw disposes what it allocated.
        /// </summary>
        /// <param name="featureColors">Per-feature linear colour, Persistent-allocated and owned by the
        /// caller from here on.</param>
        /// <param name="featureBake">Per-feature data-driven base/height bake (x=base, y=height),
        /// Persistent-allocated and owned by the caller from here on.</param>
        internal static FillMeshPipeline.LayerInput BuildLayerInput(
            IReadOnlyList<SelectedTileFeature> selectedFeatures,
            TileGeometryBuffers                geometry,
            FillExtrusion.PaintProperties      paint,
            double                             zoom,
            double3                            tileOriginRender,
            out NativeArray<Vector4>           featureColors,
            out NativeArray<Vector2>           featureBake,
            IProjection                        projection = null,
            TileBufferClip                     clip       = default,
            TileBuildBuffers                   buffers    = null)
        {
            featureColors = default;
            featureBake   = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0 || !geometry.IsCreated)
                return default;

            IProjection proj = projection ?? DefaultProjection;

            // ── Per-feature color + data-driven base/height bake + rank (mirrors StyledFillTileBuilder) ──
            NativeArray<Vector4> colors = new(geometry.FeatureCount, Allocator.Persistent);
            NativeArray<Vector2> bake   = new(geometry.FeatureCount, Allocator.Persistent); // x=base, y=height
            NativeArray<int>     order  = default;
            try
            {
                using var rankByOrdinal = new NativeArray<int>(geometry.FeatureCount, Allocator.Persistent);
                NativeArray<int> rankByOrdinalW = rankByOrdinal.GetSubArray(0, rankByOrdinal.Length);
                for (int i = 0; i < rankByOrdinalW.Length; i++) rankByOrdinalW[i] = -1; // -1 ⇒ not drawn

                int rank = 0;
                for (int si = 0; si < selectedFeatures.Count; si++)
                {
                    SelectedTileFeature selected = selectedFeatures[si];
                    IFeature feature = selected.Feature;
                    if (feature.GeometryType != TileGeometryType.Polygon)
                        continue;

                    // DATA-DRIVEN ONLY: the uniform carries a constant color and the fragment multiplies
                    // uniform × vertex, so baking it here too would square color AND alpha.
                    Color featureColor = Color.white;
                    if (paint.Color.DependsOnFeature && paint.Color.TryEvaluate(zoom, feature, out var color))
                        featureColor = new Color((float)color.R, (float)color.G, (float)color.B, (float)color.A);
                    Color lin = featureColor.linear; // sRGB→linear off main thread, same as StyledFillTileBuilder

                    float featureAlpha = lin.a;
                    if (paint.Opacity.DependsOnFeature && paint.Opacity.TryEvaluate(zoom, feature, out float opacity))
                        featureAlpha *= opacity;
                    colors[selected.Ordinal] = new Vector4(lin.r, lin.g, lin.b, featureAlpha);

                    // Data-driven base/height: bake the EVALUATED value; constant/zoom stays at 0 here (the
                    // uniform carries it — see BindFillExtrusionPaintToApplier and ExtrudeAndBake's doc).
                    float bakedBase = 0f, bakedHeight = 0f;
                    if (paint.Base.DependsOnFeature && paint.Base.TryEvaluate(zoom, feature, out float b))
                        bakedBase = b;
                    if (paint.Height.DependsOnFeature && paint.Height.TryEvaluate(zoom, feature, out float h))
                        bakedHeight = h;
                    bake[selected.Ordinal] = new Vector2(bakedBase, bakedHeight);

                    rankByOrdinalW[selected.Ordinal] = rank++;
                }

                if (rank == 0)
                {
                    colors.Dispose();
                    bake.Dispose();
                    featureColors = default;
                    featureBake   = default;
                    return default; // no polygon geometry
                }

                order = BuildRingVisitOrder(geometry, rankByOrdinal, rank);

                featureColors = colors;
                featureBake   = bake;
                return new FillMeshPipeline.LayerInput
                {
                    Geometry       = geometry,
                    RingVisitOrder = order,
                    OriginRender   = tileOriginRender,
                    Projection     = proj,
                    Clip           = clip,
                };
            }
            catch
            {
                colors.Dispose();
                bake.Dispose();
                order.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The sizing and write-job half of <see cref="ScheduleWrite"/>, split out so a test can write into its
        /// own <paramref name="md"/>. Sizes <paramref name="md"/> to roof plus walls and schedules one
        /// <see cref="FillExtrusionStreamWriteJob"/> (roof at <c>[0, Vr)</c>, walls rebased to <c>[Vr, Vr+Vw)</c>).
        /// Returns default bounds and handle when there is nothing to write. Otherwise the handle is UNCOMPLETED;
        /// the caller completes it before reading bounds, and disposes the bounds. Main-thread only.
        /// </summary>
        /// <param name="md">The <c>Mesh.MeshData</c> slot to size and write into — caller-owned.</param>
        /// <param name="roof">A layer's completed measure output; an empty roof still lets the walls alone
        /// produce a mesh. Limitation: a roof with vertices but no triangles, beside walls, is not handled.</param>
        /// <param name="featureColors">Per-feature linear colour, indexed by the roof's own
        /// <c>VertexFeatureIdx</c>.</param>
        /// <param name="featureBake">Per-feature data-driven base/height bake, indexed the same way.</param>
        /// <param name="walls">This layer's wall geometry from <see cref="FillExtrusionMeshGraph.Schedule"/>,
        /// copied verbatim into the mesh.</param>
        /// <param name="projection">The layer's build projection — decides the roof's globe/flat arm.</param>
        /// <param name="tile">The layer's tile address — feeds the per-vertex sec φ bake on the flat arm.</param>
        /// <param name="extent">The layer's tile extent — feeds the same sec φ bake.</param>
        /// <remarks><c>internal</c>, not <c>private</c>: the production write node,
        /// <see cref="ScheduleWrite"/>, plus a test-assembly caller reached
        /// through <c>MapRenderer.Unity</c>'s own <c>InternalsVisibleTo("MapRenderer.Tests.Shared")</c>
        /// grant.</remarks>
        internal static (JobHandle Handle, NativeArray<float3x2> Bounds) ScheduleStreamWrite(
            Mesh.MeshData md, FillGraphOutput roof, NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            WallColumns walls, IProjection projection, TileId tile, double extent)
        {
            int vr = roof.IsCreated ? roof.TileVertices.Length : 0;
            int ir = roof.IsCreated ? roof.TriangleIndices.Length : 0;
            // FillExtrusionMeshGraph.Schedule's empty guard can return UNCREATED walls, and no caller checks
            // before this site, so NativeList.Length on a default list would throw.
            int vw = walls.IsCreated ? walls.VertexCount : 0;
            int iw = walls.IsCreated ? walls.IndexCount : 0;

            if (vr + vw == 0 || ir + iw == 0)
                return default;

            md.SetVertexBufferParams(vr + vw, VertexDescriptors);
            md.SetIndexBufferParams(ir + iw, IndexFormat.UInt32);
            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, ir + iw, MeshTopology.Triangles), NoValidate);

            var bounds = new NativeArray<float3x2>(1, Allocator.Persistent);
            bool globe = !double.IsInfinity(projection.MaxRefineAngleRad);

            JobHandle handle = new FillExtrusionStreamWriteJob
            {
                WorldPositions = roof.WorldPositions, VertexUp = roof.VertexUp, VertexEast = roof.VertexEast,
                TileVertices = roof.TileVertices, VertexFeatureIdx = roof.VertexFeatureIdx,
                TriangleIndices = roof.TriangleIndices,
                FeatureColors = featureColors, FeatureBake = featureBake,
                WallPositionNormal = walls.PositionNormal, WallExtrude = walls.Extrude, WallTangent = walls.Tangent,
                WallColor = walls.Color, WallIndices = walls.Indices,
                Tile = tile, Extent = extent, Globe = globe,
                Md = md, OutBounds = bounds,
            }.Schedule();

            return (handle, bounds);
        }

        /// <summary>
        /// The write graph's node for one non-empty extrusion layer —
        /// the extrusion-specific mirror of <see cref="StyledFillTileBuilder.ScheduleWrite"/>. Allocates one
        /// exact-size <see cref="Mesh.MeshDataArray"/> and hands it to <see cref="ScheduleStreamWrite"/>.
        /// Returns UNCOMPLETED — the caller polls/completes <see cref="MeshWriteOutput.Handle"/> before
        /// taking the payload.
        /// </summary>
        internal static MeshWriteOutput ScheduleWrite(
            FillGraphOutput roof, NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            WallColumns walls, IProjection projection, TileId tile, double extent)
        {
            int vr = roof.IsCreated ? roof.TileVertices.Length : 0;
            int vw = walls.VertexCount;

            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            (JobHandle handle, NativeArray<float3x2> bounds) =
                ScheduleStreamWrite(mda[0], roof, featureColors, featureBake, walls, projection, tile, extent);

            if (!bounds.IsCreated)
            {
                mda.Dispose();
                return default;
            }

            return new MeshWriteOutput
            {
                Mda = mda, Bounds = bounds, VertexCount = vr + vw, Handle = handle, IsCreated = true,
            };
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

        /// <summary>metres→world factor at one vertex: 1.0 on the globe (true ECEF metres); the Web
        /// Mercator point-scale factor sec(φ) on the flat sheet (φ = the vertex's OWN latitude — a low-zoom
        /// tile can span enough latitude that a per-tile constant would be visibly wrong).</summary>
        private static double MetresToWorldFactor(bool globe, double latitudeDegrees)
            => globe ? 1.0 : 1.0 / math.cos(latitudeDegrees * math.PI_DBL / 180.0);

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
