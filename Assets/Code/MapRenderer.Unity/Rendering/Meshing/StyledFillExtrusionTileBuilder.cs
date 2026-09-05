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
    /// S23 I2b managed fill-extrusion mesh builder: roof cap + side walls, with VS height extrusion (the
    /// mesh itself is <b>height-agnostic</b> — see the class-level invariant below). Mirrors
    /// <see cref="StyledFillTileBuilder"/>'s shape (managed color/bake eval → Burst geometry → alloc-free
    /// stream write), but the two geometry kinds diverge structurally:
    ///
    /// <list type="bullet">
    /// <item><b>Roof</b> reuses <see cref="FillMeshGraph.Schedule"/> UNCHANGED (the same earcut+project
    /// chain the flat fill uses) — the roof is exactly a fill polygon at elevation 0, extruded in the VS.</item>
    /// <item><b>Walls</b> are NOT earcut output: they walk the RAW ring vertex sequences directly off the
    /// <b>borrowed</b> <see cref="TileGeometryBuffers"/> (before triangulation, so no bridge-copy duplicate
    /// vertices or hole-order permutation), one quad per boundary edge (both exterior AND hole rings — a
    /// courtyard hole needs interior walls too). The ring gather (<see cref="RingSelectJob"/>), tile→geo
    /// (<see cref="TileToGeoJob"/>), projection (<see cref="ProjectionDispatch"/>) and quad emission
    /// (<c>WallQuadJob</c>, <c>StyledFillExtrusionTileBuilder.WallJob.cs</c>) are four Burst nodes SCHEDULED
    /// by <see cref="FillExtrusionMeshGraph.Schedule"/> alongside the roof (job-scheduling-design.md §8 stage
    /// 5, the wall-job stage) — this class no longer runs them synchronously; see that type's own doc.
    /// "Byte-identical" is NOT inherited at the managed-vs-Burst projection boundary — see job-scheduling-
    /// design.md §8 stage 5's opening invariant block (shared preamble, not line-specific) for the measured
    /// per-field bound.</item>
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
    public static partial class StyledFillExtrusionTileBuilder
    {
        // Skip Unity's main-thread index validation / bounds recompute — same contract as StyledFillTileBuilder.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // internal, not private (vestige sweep): a test-assembly caller (via InternalsVisibleTo) reads this
        // default the same way WriteMeshData used to, before it moved to the test assembly.
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
        /// Interleaved stream-1 payload: the D1 extrusion inputs. <c>ExtrudeUpAndT.xyz</c> is the
        /// <c>sec(φ)</c>-baked extrude-up direction (unit-up × the metres→world factor — see the class doc);
        /// <c>ExtrudeUpAndT.w</c> is <c>t</c> (0 = floor/base, 1 = roof/height). <c>BakedBaseHeight</c> is the
        /// per-vertex data-driven bake (S12): <c>x</c> = evaluated fill-extrusion-base, <c>y</c> = evaluated
        /// fill-extrusion-height, each 0 when that property is constant/zoom (the uniform carries it
        /// instead) — the VS composes them ADDITIVELY (<c>lerp(_ExtrusionBase + x, _ExtrusionHeight + y, t)</c>),
        /// so uniform-only and bake-only both reduce to the plain uniform path when the other is zero.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtrudeAndBake
        {
            public Vector4 ExtrudeUpAndT;
            public Vector2 BakedBaseHeight;
        }

        /// <summary>
        /// job-scheduling-design.md §8 stage 5 (the wall-job-graph stage): the wall geometry
        /// <see cref="FillExtrusionMeshGraph.Schedule"/> builds alongside the roof — native columns owned by
        /// <c>TileBuildGraph.LayerBuild</c> (production) or the caller directly (a test-assembly caller,
        /// reached via <c>InternalsVisibleTo</c>), which the write step later appends after the roof
        /// (<see cref="ScheduleWrite"/>), at indices rebased by the roof's own vertex count. Indices here are
        /// WALL-LOCAL: the first wall vertex is 0, exactly as the retired managed <c>WriteWalls</c> loop's
        /// <c>quadBase</c> always was — only the list it counts against moved from a local to this struct's
        /// field.
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
            /// (<c>default</c>) instance or after <see cref="Dispose"/>. Derived from <see cref="PositionNormal"/>
            /// rather than stored (F1, review): a stored <c>bool</c> lives on the STRUCT VALUE, so a copy —
            /// e.g. <c>FillExtrusionGraphOutput.Walls</c>, itself copied again into a local at every call site
            /// that reads it (<c>ext.Walls</c> in a test-assembly caller, in
            /// <c>TileBuildGraph.CompleteMeasureAndScheduleWrite</c>, in the parity tests) — would clear only
            /// ITS OWN flag on dispose, leaving every other copy still reading <c>true</c> — able to decrement
            /// <see cref="DebugLiveCount"/> a second time if it were ever disposed too. A derived property
            /// reads the shared native handle instead, so every copy agrees.</summary>
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

            /// <summary>Idempotent — a second call on an already-disposed (or never-created, i.e.
            /// <c>default</c>) instance is a no-op. <c>internal</c>, matching the type: every call site is a
            /// direct <c>x.Dispose()</c> in-assembly (<c>FillExtrusionGraphOutput.Dispose</c>,
            /// <c>TileBuildGraph</c>'s catch/teardown, the parity tests) — no <c>using</c>-declaration or
            /// <see cref="System.IDisposable"/>-typed reference needs the wider access <c>public</c> would
            /// buy.</summary>
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

        /// <summary>
        /// job-scheduling-design.md §8 stage 4: the graph arm's prologue — everything the retired synchronous
        /// <c>WriteMeshData</c> (vestige sweep: moved to a test-assembly caller, reached via
        /// <c>InternalsVisibleTo</c>, zero production callers) did BEFORE handing off to the
        /// roof/wall write, lifted out so a graph-arm processor can run it on the seam and hand the result
        /// to the pump. Mirrors <see cref="StyledFillTileBuilder.BuildLayerInput"/>'s
        /// shape exactly: bakes each surviving polygon feature's linear colour (<paramref name="featureColors"/>)
        /// and data-driven base/height (<paramref name="featureBake"/>), and builds the ring visit order —
        /// colour, bake, ring visit order, nothing else. The wall geometry moved out of this method
        /// (job-scheduling-design.md §8 stage 5, the wall-job stage): it is built by
        /// <see cref="FillExtrusionMeshGraph.Schedule"/> instead, from the SAME borrowed <paramref name="geometry"/>
        /// this method still selects rings over — see that type's own doc.
        ///
        /// <para>Returns <c>default</c> (an uncreated <see cref="FillMeshPipeline.LayerInput"/>, with every
        /// out-param also left uncreated) when there is no polygon geometry to build — the caller reads
        /// <c>Input.RingVisitOrder.IsCreated</c> to tell "nothing to build" from a real request. Every
        /// returned container is created iff the return value is (one fate) — a mid-method throw disposes
        /// whatever this call had already allocated.</para>
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

                    Color featureColor = Color.white;
                    if (paint.Color.TryEvaluate(zoom, feature, out var color))
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
        /// job-scheduling-design.md §8 stage 4 Group B: the sizing + <see cref="FillExtrusionStreamWriteJob"/>
        /// schedule half of <see cref="ScheduleWrite"/>, split out so a test-assembly caller (vestige sweep:
        /// the verbatim lift of the retired synchronous <c>WriteMeshData</c>, zero production callers,
        /// reached via <c>InternalsVisibleTo</c>) can run it over its OWN caller-supplied
        /// <paramref name="md"/> instead of a freshly-minted
        /// <see cref="Mesh.MeshDataArray"/> — one write job, two callers (that test-assembly caller
        /// and <see cref="ScheduleWrite"/>), no <c>MeshData</c>-to-<c>MeshData</c> copy. Sizes
        /// <paramref name="md"/> to the roof's completed measure PLUS <paramref name="walls"/>'s own
        /// vertex/index count and schedules one <see cref="FillExtrusionStreamWriteJob"/> that writes the
        /// roof at <c>[0, Vr)</c> then the walls at <c>[Vr, Vr+Vw)</c>, wall indices rebased by <c>+Vr</c>.
        /// Returns a default (uncreated) <see cref="NativeArray{T}"/> bounds and default handle when there is
        /// nothing to write — the caller reads <c>Bounds.IsCreated</c> to tell the two apart. Otherwise
        /// returns UNCOMPLETED — the caller completes the handle before reading bounds, and disposes the
        /// bounds array itself.
        ///
        /// <para><b>Main-thread only</b>: scheduling <see cref="FillExtrusionStreamWriteJob"/> is a Unity
        /// job-system operation.</para>
        /// </summary>
        /// <param name="md">The <c>Mesh.MeshData</c> slot to size and write into — caller-owned.</param>
        /// <param name="roof">A layer's completed measure-graph output — the empty check below covers BOTH
        /// the roof and <paramref name="walls"/> (DIV-A5): a faulted or empty roof still lets a hole-less
        /// footprint's walls alone produce a mesh, matching the managed roof writer's own early-return (an OR
        /// over BOTH counts, not vertex count alone).
        /// <para><b>Named, unclosed sub-case (review D2):</b> that OR does NOT fully mirror the managed arm
        /// for <c>Vr &gt; 0, Ir == 0</c> with non-empty walls — surviving ring vertices whose earcut produces
        /// zero triangles. Recorded, not closed: see job-scheduling-design.md §8 stage 4's own note; closing
        /// it needs an explicit roof-vertex-count field on the job, real work for an apparently unreachable
        /// case.</para></param>
        /// <param name="featureColors">Per-feature linear colour, indexed by the roof's own
        /// <c>VertexFeatureIdx</c>.</param>
        /// <param name="featureBake">Per-feature data-driven base/height bake, indexed the same way.</param>
        /// <param name="walls">This layer's wall geometry, fully computed by
        /// <see cref="FillExtrusionMeshGraph.Schedule"/> — copied verbatim into the mesh, never recomputed
        /// here.</param>
        /// <param name="projection">The layer's build projection — decides the roof's globe/flat arm.</param>
        /// <param name="tile">The layer's tile address — feeds the per-vertex sec φ bake on the flat arm.</param>
        /// <param name="extent">The layer's tile extent — feeds the same sec φ bake.</param>
        /// <remarks><c>internal</c>, not <c>private</c> (vestige sweep): the production write node,
        /// <see cref="ScheduleWrite"/>, plus a test-assembly caller reached
        /// through <c>MapRenderer.Unity</c>'s own <c>InternalsVisibleTo("MapRenderer.Tests.Shared")</c>
        /// grant.</remarks>
        internal static (JobHandle Handle, NativeArray<float3x2> Bounds) ScheduleStreamWrite(
            Mesh.MeshData md, FillGraphOutput roof, NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            WallColumns walls, IProjection projection, TileId tile, double extent)
        {
            int vr = roof.IsCreated ? roof.TileVertices.Length : 0;
            int ir = roof.IsCreated ? roof.TriangleIndices.Length : 0;
            // IsCreated-guarded (R5): FillExtrusionMeshGraph.Schedule's empty guard (RingVisitOrder.Length ==
            // 0, a check BuildLayerInput's old inline WriteWalls path never applied) can return an UNCREATED
            // Walls — neither caller (production's ScheduleWrite nor a test-assembly caller)
            // has an IsCreated skip of its own before reaching this shared site,
            // so an unguarded walls.VertexCount/IndexCount would throw on NativeList.Length against a default list.
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
        /// job-scheduling-design.md §8 stage 4: the write graph's node for one non-empty extrusion layer —
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

        /// <summary>metres→world factor at one vertex (OQ1): 1.0 on the globe (true ECEF metres); the Web
        /// Mercator point-scale factor sec(φ) on the flat sheet (φ = the vertex's OWN latitude — a low-zoom
        /// tile can span enough latitude that a per-tile constant would be visibly wrong; see T2).</summary>
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
