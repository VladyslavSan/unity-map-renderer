using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Fill = MapRenderer.Core.Style.Fill;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
                                                        // 'Color' ambiguous with UnityEngine's

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// S40 managed per-layer fill mesh builder. Receives real <see cref="TileId"/> + origin, a set of
    /// pre-selected features, and a <see cref="Fill.PaintProperties"/> describing the style.
    ///
    /// Pipeline (S89 D2): managed color eval → Burst geometry via <c>FillMeshPipeline</c>
    ///   (decode → assemble → earcut → project, run on this worker via <c>.Run()</c> into NativeArrays) →
    ///   managed alloc-free stream write into a <c>Mesh.MeshData</c>. The managed Core geometry
    ///   (<c>Earcut</c>/<c>PolygonAssembler</c>) is retired from this path (differential oracle only).
    ///
    /// S89 Stage B — <see cref="ScheduleWrite"/> builds the mesh AND writes directly into a caller-allocated
    /// <see cref="Mesh.MeshData"/> (the writable-mesh advanced API), off the main thread. The bespoke
    /// NativeArray-stream payload + main-thread <c>SetVertexBufferData</c> copy is gone: the worker populates
    /// the mesh buffers in place, and the main thread only allocates (at kick) and applies (at consume). The
    /// off-thread-write threading contract is guarded by <c>MeshDataThreadWriteSpikeTests</c>.
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3) interleaved via <see cref="FillPositionNormal"/>.
    ///   Stream 1 — TexCoord0 UV (Float32x2) + TexCoord3 band (Float32x3) interleaved via
    ///              <see cref="FillPatternUvBand"/>, 20 B. The band attribute shares a stream because all
    ///              four are already spoken for — Unity's cap — so it could not have one of its own.
    ///   Stream 2 — Tangent (Float32x4).
    ///   Stream 3 — Color (Float32x4, linearized sRGB).
    ///   Index buffer — UInt32.
    ///
    /// Color (D2): two-carrier split for <c>fill-color</c>. Data-driven bakes the per-feature sRGB colour,
    /// converted to linear via <c>Color.linear</c> off the main thread, and leaves <c>_BaseColor</c> white.
    /// Constant/zoom leaves the vertex white and rides the material's <c>_BaseColor</c> uniform instead,
    /// bound by <see cref="Materials.MaterialFactory.BindFillPaintToApplier"/> (Unity converts sRGB→linear
    /// on upload for a Color-typed shader property, so that site must NOT pre-convert).
    ///
    /// Thread-safety: <see cref="ScheduleWrite"/> touches only pure-managed, stateless Core code plus a
    /// caller-allocated <c>Mesh.MeshData</c> (whose <c>SetVertexBufferParams</c>/<c>GetVertexData</c>/… are
    /// off-main-thread safe). No shared mutable static state — safe to run concurrently per tile.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static partial class StyledFillTileBuilder
    {
        /// <summary>Profiler marker name constants — the single source of truth for this builder's telemetry
        /// contract. Referenced by both the <see cref="ProfilerMarker"/> fields below and the marker tests
        /// (<c>ProfilerMarkerTests</c>, <c>MapViewAsyncMeshBuildTests</c>) so each string lives in exactly one
        /// place; renaming a marker is a one-line edit here that the tests pick up automatically.</summary>
        public static class ProfilerMarkerNames
        {
            // job-scheduling-design.md §8 stage 3: the fill prologue's own marker — the production path's
            // only WriteMeshData-family marker since the synchronous WriteMeshData method (which used to
            // fire its own PmWriteMeshData sample) moved to the test assembly (vestige sweep) with no
            // production caller left.
            public const string BuildLayerInput = "MapRenderer.Meshing.StyledFillTileBuilder.BuildLayerInput";
        }

        // Brackets BuildLayerInput's selection-order + paint-bake + ring-visit-order work — the prologue half
        // WriteMeshData used to delegate to. Opens AFTER the early-out guard — see BuildLayerInput's own doc —
        // so an empty layer still fires no PmBuildLayerInput sample. This is BuildLayerInput's OWN marker.
        private static readonly ProfilerMarker PmBuildLayerInput =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BuildLayerInput);

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
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent,   VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 3, stream: 1),
        };

        // S91-A: the projection the geometry is built with. Launch-time config (the host) threads a
        // chosen projection here in S91-C; until then this single seam defaults to WebMercator. The
        // pipeline projects with the chosen Projection struct — WebMercator.Forward is hardcoded nowhere.
        // internal, not private (vestige sweep): a test-assembly caller (via InternalsVisibleTo) reads this
        // default the same way WriteGeometry used to, before it moved to the test assembly.
        internal static readonly IProjection DefaultProjection = new WebMercatorProjection();

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

        /// <summary>
        /// Tightly-packed pattern UV + boundary-band attribute struct for stream 1.
        /// Stride = 5 × 4 = 20 bytes, matching the descriptors (TexCoord0 float2 + TexCoord3 float3).
        ///
        /// <para><see cref="Band"/> reaches the shaders as TEXCOORD3 as <c>(dirEast, dirNorth, side)</c> in
        /// the vertex's own surface frame — the <c>up</c>/<c>east</c>/<c>north</c> frame the mesh already
        /// supplies through NORMAL and TANGENT, which is what makes it correct on the globe as well as flat.
        /// <c>(0,0,0)</c> on an interior vertex (coverage 1, nothing displaced); an outward miter with
        /// <c>side = 1</c> on a boundary-band outer vertex, which <c>Fill_VertexModify.hlsl</c> turns into a
        /// one-device-pixel displacement and <c>Fill_BandCoverage.hlsl</c> into a coverage ramp. Produced by
        /// <c>FillBandJob</c> on BOTH arms; the curved arm carries it through subdivision, which lerps
        /// it at every split midpoint — so <c>side</c> takes intermediate values there, never only 0 or 1.</para>
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FillPatternUvBand
        {
            public Vector2 PatternUv;
            public Vector3 Band;
        }

        /// <summary>
        /// Returns <paramref name="features"/> reordered by <c>fill-sort-key</c> ascending, or the SAME
        /// instance when the layer declares no sort key — the common case, which must stay allocation-free
        /// and order-identical so every existing snapshot keeps its exact triangle order.
        ///
        /// <para>The sort is made STABLE by folding the declared index in as the tiebreak:
        /// <c>Array.Sort</c> is an introsort and is not stable on its own, and features with equal sort keys
        /// must keep source order (the spec's implicit ordering). An unevaluable key falls to 0, matching
        /// <c>TryEvaluate</c>'s contract elsewhere in this builder.</para>
        ///
        /// <para><paramref name="buffers"/> (perf/gc-elimination): when non-null, the working buffers and the
        /// sort comparer are drawn from the caller's pooled <see cref="TileBuildBuffers"/> instead of being
        /// allocated fresh — byte-identical output, zero managed allocation once the buffers have grown to
        /// this tile's peak feature count. <c>null</c> (tests, non-pooled callers) keeps the original
        /// allocating behaviour verbatim.</para>
        /// </summary>
        private static IReadOnlyList<SelectedTileFeature> OrderBySortKey(
            IReadOnlyList<SelectedTileFeature> features, Fill.LayoutProperties layout, double zoom,
            TileBuildBuffers buffers)
        {
            if (layout == null || layout.SortKeyIsDefault) return features;

            int count = features.Count;
            float[] sortKeys;
            int[]   declaredOrder;
            if (buffers != null)
            {
                sortKeys      = buffers.SortKeys(count);
                declaredOrder = buffers.DeclaredOrder(count);
            }
            else
            {
                sortKeys      = new float[count];
                declaredOrder = new int[count];
            }

            for (int i = 0; i < count; i++)
            {
                declaredOrder[i] = i;
                sortKeys[i] = layout.SortKey.TryEvaluate(zoom, features[i].Feature, out float key) ? key : 0f;
            }

            if (buffers != null)
            {
                // The pool's reusable IComparer<int> field — no per-call closure/delegate allocation, unlike
                // the lambda overload below. Sorted range is [0, count) — the backing arrays may be LONGER
                // (grow-only, sized to a prior build's peak), so the 3-arg range overload is load-bearing,
                // not cosmetic.
                System.Array.Sort(declaredOrder, 0, count, buffers.SortKeyComparer(sortKeys));

                SelectedTileFeature[] orderedBuffer = buffers.OrderedFeaturesBuffer(count);
                for (int i = 0; i < count; i++) orderedBuffer[i] = features[declaredOrder[i]];
                // A fixed-length [0, count) VIEW over the buffer, never the raw (possibly longer) array —
                // returning the array itself would let a shorter later build's Count read as a stale larger one.
                return buffers.OrderedFeaturesView(count);
            }

            System.Array.Sort(declaredOrder, (left, right) =>
            {
                int byKey = sortKeys[left].CompareTo(sortKeys[right]);
                return byKey != 0 ? byKey : left.CompareTo(right); // stable: declared order breaks ties
            });

            var ordered = new SelectedTileFeature[count];
            for (int i = 0; i < count; i++) ordered[i] = features[declaredOrder[i]];
            return ordered;
        }

        /// <summary>
        /// The tile's span in WORLD UNITS (Web-Mercator metres) at its OWN zoom — not the display zoom.
        /// This is why the pattern survives overzoom: OpenFreeMap's source stops at z14 while the camera keeps
        /// going, so a z14 tile is stretched across display zooms 14→18+. Anything derived from the display
        /// zoom would be up to 16× wrong for those tiles; the tile's own <c>Z</c> is exact for all of them,
        /// and equally for the coarser far tiles a mixed-zoom cover produces.
        /// </summary>
        private static double TileSpanWorldUnits(TileId id)
            => EarthConstants.EquatorialCircumferenceMetres / math.pow(2.0, id.Z);

        /// <summary>
        /// Stream 1 for one vertex: its offset from the tile origin in WORLD UNITS, rather than the 0..1 tile
        /// fraction this used to write.
        ///
        /// <para>The change is what makes pattern sizing correct at all. A tile fraction only means something
        /// once you know the tile's world size, which the per-layer material uniform cannot know — it sees the
        /// display zoom, and a tile's own zoom differs from it under overzoom and under mixed-zoom cover. In
        /// world units the shader needs no tile knowledge at all: it multiplies by repeats-per-world-unit,
        /// which is a function of the display zoom alone.</para>
        ///
        /// <para>Precision: values run 0..tileSpan, which is ~2.4 km at z14 — comfortably inside float32
        /// (~1e-4 there). It degrades toward z0, where a tile spans the world, but a pattern at z0 is far past
        /// the point of caring.</para>
        ///
        /// <para>Non-pattern fills are unaffected: this stream feeds <c>_BaseMap</c>, which is the default
        /// white texture for every map fill, so its scaling is unobservable.</para>
        /// </summary>
        private static Vector2 PatternCoord(double2 tileVertex, double extentInv, double tileSpanWorldUnits)
        {
            double perTileUnit = extentInv * tileSpanWorldUnits;
            return new Vector2((float)(tileVertex.x * perTileUnit), (float)(tileVertex.y * perTileUnit));
        }

        /// <summary>
        /// job-scheduling-design.md §8 stage 3: the graph arm's prologue — everything the retired synchronous
        /// <c>WriteMeshData</c> did BEFORE handing off to <c>FillMeshPipeline.Schedule</c> (now
        /// <c>TileBuildGraph</c>'s job), lifted out so a graph-arm processor can run it on the seam and hand
        /// the result to the pump (vestige sweep: <c>WriteMeshData</c> itself had zero production callers and
        /// moved to a test-assembly caller, reached via <c>InternalsVisibleTo</c>). Selects the
        /// fill-sort-key order, bakes each surviving polygon feature's linear colour (<paramref name="featureColors"/>,
        /// caller-owned, indexed by feature ORDINAL), and builds the ring visit order.
        ///
        /// <para>Returns <c>default</c> (an uncreated <see cref="FillMeshPipeline.LayerInput"/>, with
        /// <paramref name="featureColors"/> also left uncreated) when there is no polygon geometry to build —
        /// an absent <paramref name="selectedFeatures"/> list, an uncreated <paramref name="geometry"/>, or
        /// every selected feature failing the polygon-only gate. The caller reads
        /// <c>Input.RingVisitOrder.IsCreated</c> to tell "nothing to build" from a real request.</para>
        /// </summary>
        /// <param name="featureColors">Per-feature linear colour, Persistent-allocated and owned by the
        /// caller from here on — created iff the return value is (both share one fate).</param>
        internal static FillMeshPipeline.LayerInput BuildLayerInput(
            IReadOnlyList<SelectedTileFeature> selectedFeatures,
            TileGeometryBuffers                geometry, // BORROWED — the store owns it; never disposed here
            Fill.PaintProperties               paint,
            double                             zoom,
            double3                            tileOriginRender,
            out NativeArray<Vector4>           featureColors,
            IProjection                        projection = null,
            Fill.LayoutProperties              layout     = null,
            TileBufferClip                     clip       = default,
            TileBuildBuffers                   buffers    = null)
        {
            featureColors = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0 || !geometry.IsCreated)
                return default;

            // fill-sort-key: features draw in ASCENDING key order, so a higher key lands LATER in the index
            // buffer and therefore ON TOP — this layer's features share one mesh drawn under a
            // painter's-algorithm ZWrite-Off contract, where triangle order IS draw order for coincident
            // polygons. Absent key ⇒ no sort at all, keeping the source's declared order byte-for-byte.
            selectedFeatures = OrderBySortKey(selectedFeatures, layout, zoom, buffers);

            using var sBuild = PmBuildLayerInput.Auto();

            // Bake this layer's per-feature linear colour, and record each surviving polygon feature's RANK —
            // its position in fill-sort-key order. Both are indexed by the feature's ORDINAL in the source
            // layer, because that is what the shared buffer's RingFeatureIdx names; a slot-indexed array would
            // permute colours the moment this layer's filter rejects anything.
            // Allocator.Persistent, NOT TempJob: this runs off-main (UniTask.RunOnThreadPool) and a build can
            // span >4 main-thread frames — TempJob's 4-frame lifetime check would flag/reclaim it mid-build.
            // featureColors is RETURNED (caller-owned from here), so it is a plain local, not `using var`; a
            // try/catch below disposes it (and RingVisitOrder, once built) on a mid-method throw.
            NativeArray<Vector4> colors = new(geometry.FeatureCount, Allocator.Persistent);
            NativeArray<int>     order  = default;
            try
            {
                using var rankByOrdinal = new NativeArray<int>(geometry.FeatureCount, Allocator.Persistent);
                // A `using`-declared local is read-only for index-ASSIGNMENT (CS1654) — reads through
                // rankByOrdinal (including passing it by value to BuildRingVisitOrder below) are unaffected;
                // only the write needs a plain-local alias. GetSubArray(0, Length) is a normal method call
                // returning a NativeArray<T> VIEW over the same memory, assignable to a non-readonly local.
                NativeArray<int> rankByOrdinalWritable = rankByOrdinal.GetSubArray(0, rankByOrdinal.Length);
                // KEEP the -1 fill: a default NativeArray<int> is 0, a VALID rank — so without this, non-drawn
                // features (never ranked below) would read rank 0 and BuildRingVisitOrder would visit their rings.
                for (int i = 0; i < rankByOrdinalWritable.Length; i++) rankByOrdinalWritable[i] = -1; // -1 ⇒ not drawn

                int rank = 0;
                for (int si = 0; si < selectedFeatures.Count; si++)
                {
                    SelectedTileFeature selected = selectedFeatures[si];
                    IFeature feature = selected.Feature;

                    // Polygon-only. Since B7 this is no longer the sole guard — RingAssemblyJob has its own kind
                    // gate — but it stays, because it also decides which ordinals get a colour and how many rings
                    // are gathered. Two independent guards, each RED-verifiable on its own.
                    if (feature.GeometryType != TileGeometryType.Polygon)
                        continue;

                    Color featureColor = Color.white;
                    // DATA-DRIVEN ONLY: BindFillPaintToApplier binds _BaseColor for exactly
                    // !DependsOnFeature. Ungated, the vertex stays white and the uniform carries the colour.
                    if (paint.Color.DependsOnFeature && paint.Color.TryEvaluate(zoom, feature, out var color))
                        featureColor = new Color((float)color.R, (float)color.G, (float)color.B, (float)color.A);

                    // S13 D2 gamma fix (off main thread): sRGB→linear here. white.linear == white.
                    Color lin = featureColor.linear;

                    // P4 — data-driven fill-opacity: bake the per-feature alpha, since one uniform cannot express
                    // a value that varies per feature. Constant/zoom opacity stays on the _Opacity uniform (bound
                    // by MaterialFactory) and is NOT folded in here, or the two would multiply twice; the
                    // data-driven branch is exactly the case MaterialFactory declines to bind. Same split
                    // BindLinePaintToApplier makes for data-driven line-width.
                    //
                    // Alpha is NOT gamma-converted — Color.linear transforms rgb only, and alpha is linear by
                    // definition. Reading it off `lin` would be a silent no-op today but wrong if that changed.
                    float featureAlpha = lin.a;
                    if (paint.Opacity.DependsOnFeature && paint.Opacity.TryEvaluate(zoom, feature, out float opacity))
                        featureAlpha *= opacity;

                    colors[selected.Ordinal] = new Vector4(lin.r, lin.g, lin.b, featureAlpha);
                    rankByOrdinalWritable[selected.Ordinal] = rank++;
                }

                if (rank == 0)
                {
                    colors.Dispose();
                    featureColors = default;
                    return default; // no polygon geometry
                }

                order = BuildRingVisitOrder(geometry, rankByOrdinal, rank, buffers);
                featureColors = colors;
                return new FillMeshPipeline.LayerInput
                {
                    Geometry       = geometry,
                    RingVisitOrder = order,
                    OriginRender   = tileOriginRender,
                    Projection     = projection ?? DefaultProjection, // exactly as WriteGeometry passes it
                    Clip           = clip,
                    // fill-antialias: the ONLY consumer of the parsed property. Encoded 1.0 = true, and the
                    // parse rejects a data-driven expression, but a ZOOM expression survives — so this is a
                    // threshold, not an equality, and it resolves at the BUILD zoom (a zoom-varying value
                    // only takes effect when the tile is re-meshed).
                    // The `_FillAntialias` uniform is deliberately NOT the consumer: it stays declared,
                    // instanced and bound, and is read by no pass.
                    // fill-antialias, already resolved: a layer that omitted it was parsed against the
                    // project default (MapViewConfig.FillAntialiasing, via StyleParser), so there is nothing
                    // left to decide here. A ZOOM expression survives the parse, so this resolves at the
                    // BUILD zoom — a zoom-varying value only takes effect when the tile is re-meshed.
                    SuppressBoundaryBand = !paint.Antialias.Evaluate(zoom),
                };
            }
            catch
            {
                colors.Dispose();
                order.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The ring indices this layer wants triangulated, in draw order: a <b>counting sort</b> of the shared
        /// buffer's rings bucketed on their feature's <c>fill-sort-key</c> rank, skipping rings whose feature
        /// this layer does not draw (<c>rank == -1</c>).
        ///
        /// <para>Two properties make this the byte-identical form of the pre-B7 "reorder the feature list, then
        /// decode it" shape, and both come free from the counting sort being <b>stable</b>:</para>
        /// <list type="bullet">
        /// <item>rings of one feature stay <b>contiguous</b> — <c>RingAssemblyJob</c> resets its exterior sign
        /// on a feature change, so a split feature's second run would be re-read as a fresh exterior;</item>
        /// <item>within a feature, rings keep <b>ascending ring index</b> = decode order, which is what makes
        /// earcut's hole-bridge sort (tiebroken on ring index) land where it did before.</item>
        /// </list>
        ///
        /// <para><paramref name="buffers"/> (perf/gc-elimination): when non-null, <c>rankStart</c> and the
        /// cursor it seeds are drawn from the pool instead of a fresh <c>new int[]</c> + <c>Array.Clone</c> —
        /// same counting-sort arithmetic, byte-identical <c>order</c>. The returned <see cref="NativeArray{T}"/>
        /// itself is UNCHANGED by pooling — still a fresh <c>Allocator.Persistent</c> array the caller disposes
        /// (D1a idiom); only the two MANAGED <c>int[]</c> buffers move to the pool.</para>
        /// </summary>
        private static NativeArray<int> BuildRingVisitOrder(
            TileGeometryBuffers geometry, NativeArray<int> rankByOrdinal, int rankCount, TileBuildBuffers buffers)
        {
            int   rankStartLength = rankCount + 1;
            int[] rankStart       = buffers != null ? buffers.RankStart(rankStartLength) : new int[rankStartLength];
            int   visited         = 0;
            for (int r = 0; r < geometry.RingCount; r++)
            {
                int rank = rankByOrdinal[geometry.RingFeatureIdx[r]];
                if (rank < 0) continue;
                rankStart[rank + 1]++;
                visited++;
            }
            for (int i = 0; i < rankCount; i++) rankStart[i + 1] += rankStart[i];

            // Allocator.Persistent, NOT TempJob: owned + disposed by the caller via `using var`,
            // but off-main a build can span >4 main-thread frames, so TempJob's 4-frame check would trip.
            var order = new NativeArray<int>(visited, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int[] cursor;
            if (buffers != null)
            {
                cursor = buffers.RankCursor(rankStartLength);
                System.Array.Copy(rankStart, cursor, rankStartLength); // pooled Clone() replacement
            }
            else
            {
                cursor = (int[])rankStart.Clone();
            }
            for (int r = 0; r < geometry.RingCount; r++)
            {
                int rank = rankByOrdinal[geometry.RingFeatureIdx[r]];
                if (rank < 0) continue;
                order[cursor[rank]++] = r;
            }
            return order;
        }

        /// <summary>
        /// job-scheduling-design.md §8 stage 4 Group B: the sizing + <see cref="FillStreamWriteJob"/>
        /// schedule half of <see cref="ScheduleWrite"/>, split out so a test-assembly caller (vestige sweep:
        /// the verbatim lift of the retired synchronous <c>WriteGeometry</c>, zero production callers, reached
        /// via <c>InternalsVisibleTo</c>) can run it over its OWN caller-supplied
        /// <paramref name="md"/> instead of a freshly-minted
        /// <see cref="Mesh.MeshDataArray"/> — one write job, two callers (that test-assembly caller and
        /// <see cref="ScheduleWrite"/>), no <c>MeshData</c>-to-<c>MeshData</c> copy. Declares
        /// <paramref name="md"/>'s buffers (same order both callers always used) and schedules the job.
        /// Returns UNCOMPLETED — the caller completes the handle before reading bounds, and disposes the
        /// bounds array itself.
        ///
        /// <para><b>Main-thread only</b>: scheduling <see cref="FillStreamWriteJob"/> is a Unity job-system
        /// operation.</para>
        /// </summary>
        /// <param name="md">The <c>Mesh.MeshData</c> slot to size and write into — caller-owned.</param>
        /// <param name="output">A layer's completed measure-graph output — caller-verified non-empty
        /// (<c>TileVertices.Length &gt; 0 &amp;&amp; TriangleIndices.Length &gt; 0</c>) and error-free.</param>
        /// <param name="featureColors">Per-feature linear colour, indexed by <c>output.VertexFeatureIdx</c>.</param>
        /// <param name="tile">The layer's tile address — feeds <see cref="TileSpanWorldUnits"/>.</param>
        /// <param name="extent">The layer's tile extent — feeds the pattern-coordinate scale.</param>
        /// <remarks><c>internal</c>, not <c>private</c> (vestige sweep): the production write node is
        /// <see cref="ScheduleWrite"/>; a test-assembly caller reaches this second, through
        /// <c>MapRenderer.Unity</c>'s own <c>InternalsVisibleTo("MapRenderer.Tests.Shared")</c>
        /// grant.</remarks>
        internal static (JobHandle Handle, NativeArray<float3x2> Bounds) ScheduleStreamWrite(
            Mesh.MeshData md, FillGraphOutput output, NativeArray<Vector4> featureColors, TileId tile, double extent)
        {
            int vertexCount = output.TileVertices.Length;
            int indexCount  = output.TriangleIndices.Length;

            // No GetVertexData/GetIndexData here — FillStreamWriteJob takes the whole Md and resolves every
            // stream/index view INSIDE Execute() (see that job's doc: taking the views here, as separate job
            // fields, reproducibly made two of them alias at Schedule).
            md.SetVertexBufferParams(vertexCount, FillVertexDescriptors);
            md.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles), NoValidate);

            var bounds = new NativeArray<float3x2>(1, Allocator.Persistent);
            double extentInv = extent > 0.0 ? 1.0 / extent : 0.0;
            double tileSpanWorldUnits = TileSpanWorldUnits(tile);

            JobHandle handle = new FillStreamWriteJob
            {
                WorldPositions = output.WorldPositions, VertexUp = output.VertexUp, VertexEast = output.VertexEast,
                VertexBand = output.VertexBand,
                TileVertices = output.TileVertices, VertexFeatureIdx = output.VertexFeatureIdx,
                TriangleIndices = output.TriangleIndices,
                FeatureColors = featureColors,
                ExtentInv = extentInv, TileSpanWorldUnits = tileSpanWorldUnits,
                Md = md, OutBounds = bounds,
            }.Schedule();

            return (handle, bounds);
        }

        /// <summary>
        /// job-scheduling-design.md §3.2/§8 stage 2: the write graph's node for one NON-EMPTY layer — owns
        /// the vertex-stream layout and the <see cref="Mesh.MeshData"/> boundary, so a caller (the graph
        /// arm's owner) never needs to know either. Allocates one exact-size <see cref="Mesh.MeshDataArray"/>
        /// sized to <paramref name="output"/>'s real vertex/index count and hands it to
        /// <see cref="ScheduleStreamWrite"/>. Returns UNCOMPLETED — the caller polls/completes
        /// <see cref="MeshWriteOutput.Handle"/> before taking the payload.
        /// </summary>
        /// <param name="output">A layer's completed measure-graph output — caller-verified non-empty
        /// (<c>TileVertices.Length &gt; 0 &amp;&amp; TriangleIndices.Length &gt; 0</c>) and error-free.</param>
        /// <param name="featureColors">Per-feature linear colour, indexed by <c>output.VertexFeatureIdx</c>.</param>
        /// <param name="tile">The layer's tile address — feeds <see cref="TileSpanWorldUnits"/>.</param>
        /// <param name="extent">The layer's tile extent — feeds the pattern-coordinate scale.</param>
        internal static MeshWriteOutput ScheduleWrite(
            FillGraphOutput output, NativeArray<Vector4> featureColors, TileId tile, double extent)
        {
            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            (JobHandle handle, NativeArray<float3x2> bounds) =
                ScheduleStreamWrite(mda[0], output, featureColors, tile, extent);

            return new MeshWriteOutput
            {
                Mda = mda, Bounds = bounds, VertexCount = output.TileVertices.Length, Handle = handle, IsCreated = true,
            };
        }
    }
}