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
    /// Per-layer fill mesh builder: managed colour eval, Burst geometry via <c>FillMeshGraph</c>, then
    /// <see cref="FillStreamWriteJob"/> writes a caller-allocated <c>Mesh.MeshData</c> off the main thread. It
    /// has no shared mutable static state, so tiles build concurrently. Streams: 0 position+normal, 1 pattern
    /// UV+band, 2 tangent, 3 linear colour (Unity's max of 4). Data-driven <c>fill-color</c> bakes into the
    /// vertex; constant/zoom rides <c>_BaseColor</c>, which Unity converts to linear, so it is not pre-converted.
    /// </summary>
    public static partial class StyledFillTileBuilder
    {
        /// <summary>Profiler marker name constants — the single source of truth for this builder's telemetry
        /// contract. Referenced by both the <see cref="ProfilerMarker"/> fields below and the marker tests
        /// (<c>ProfilerMarkerTests</c>, <c>MapViewAsyncMeshBuildTests</c>) so each string lives in exactly one
        /// place; renaming a marker is a one-line edit here that the tests pick up automatically.</summary>
        public static class ProfilerMarkerNames
        {
            // The fill prologue's own marker — the production path's only WriteMeshData-family marker.
            public const string BuildLayerInput = "MapRenderer.Meshing.StyledFillTileBuilder.BuildLayerInput";
        }

        // Brackets BuildLayerInput's selection-order + paint-bake + ring-visit-order work. Opens AFTER the
        // early-out guard — see BuildLayerInput's own doc — so an empty layer fires no sample.
        private static readonly ProfilerMarker PmBuildLayerInput =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.BuildLayerInput);

        // Skip Unity's main-thread index validation (O(indices)) + the redundant intermediate bounds compute:
        // indices come from earcut and are covered by tests; the worker-computed AABB becomes Mesh.bounds.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // Hoisted (static readonly — no per-tile alloc). Ascending VertexAttribute enum order avoids the
        // "non-standard order" warning; within each stream it matches the struct field order, so offsets hold.
        private static readonly VertexAttributeDescriptor[] FillVertexDescriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent,   VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 3, stream: 1),
        };

        // The single default-projection seam; no path hardcodes WebMercator.Forward. internal, because a
        // test-assembly caller reads it.
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
        /// Tightly-packed pattern UV + boundary-band attribute struct for stream 1. Stride = 5 × 4 = 20
        /// bytes, matching the descriptors (TexCoord0 float2 + TexCoord3 float3).
        /// Non-local invariant: <see cref="Band"/> reaches the shaders as TEXCOORD3 as
        /// <c>(dirEast, dirNorth, side)</c> in the vertex's own NORMAL/TANGENT surface frame, which is what
        /// makes it correct on the globe as well as flat. <c>(0,0,0)</c> on an interior vertex; an outward
        /// miter with <c>side = 1</c> on a boundary-band outer vertex. Produced by <c>FillBandJob</c> on both
        /// arms. <c>Fill_VertexModify.hlsl</c> turns it into a one-device-pixel displacement, and
        /// <c>Fill_BandCoverage.hlsl</c> into a coverage ramp. The curved arm's subdivision lerps it at every
        /// split midpoint, so <c>side</c> takes intermediate values there, never only 0 or 1.
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
        /// and order-identical so every existing snapshot keeps its exact triangle order. Non-obvious why:
        /// the sort is made STABLE by folding the declared index in as the tiebreak, since <c>Array.Sort</c>
        /// is an introsort and not stable on its own, and features with equal sort keys must keep source
        /// order. An unevaluable key falls to 0, matching <c>TryEvaluate</c>'s contract elsewhere in this
        /// builder. <paramref name="buffers"/>, when non-null, draws the working buffers and sort comparer
        /// from the caller's pooled <see cref="TileBuildBuffers"/> instead of allocating fresh —
        /// byte-identical output, zero managed allocation once the buffers reach this tile's peak feature
        /// count; <c>null</c> (tests, non-pooled callers) allocates fresh buffers per call.
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
                // The pool's reusable comparer allocates no closure. The range overload is load-bearing: the
                // grow-only backing arrays may be LONGER than the sorted [0, count) range.
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
        /// Stream 1 for one vertex: its offset from the tile origin in WORLD UNITS, not a 0..1 tile
        /// fraction. Non-obvious why: a tile fraction only means something once you know the tile's world
        /// size, which the per-layer material uniform cannot know (it sees the display zoom, and a tile's
        /// own zoom differs from it under overzoom and mixed-zoom cover); in world units the shader needs
        /// no tile knowledge, multiplying by repeats-per-world-unit instead. Precision: values run
        /// 0..tileSpan (~2.4 km at z14, where float32 resolves ~1e-4), degrading toward z0, past the
        /// point of caring for a pattern. Non-pattern fills are unaffected — this stream feeds
        /// <c>_BaseMap</c>, the default white texture, so its scaling is unobservable.
        /// </summary>
        private static Vector2 PatternCoord(double2 tileVertex, double extentInv, double tileSpanWorldUnits)
        {
            double perTileUnit = extentInv * tileSpanWorldUnits;
            return new Vector2((float)(tileVertex.x * perTileUnit), (float)(tileVertex.y * perTileUnit));
        }

        /// <summary>
        /// The graph arm's prologue, run before <c>TileBuildGraph</c> schedules. Selects the fill-sort-key
        /// order, bakes each polygon feature's linear colour (indexed by feature ORDINAL), and builds the ring
        /// visit order. Returns <c>default</c>, with <paramref name="featureColors"/> uncreated, when there is
        /// no polygon geometry; the caller reads <c>Input.RingVisitOrder.IsCreated</c> to tell the two apart.
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

            // fill-sort-key: ASCENDING key order, so a higher key lands LATER in the index buffer and ON TOP —
            // under this layer's ZWrite-Off contract, triangle order IS draw order. Absent key ⇒ no sort.
            selectedFeatures = OrderBySortKey(selectedFeatures, layout, zoom, buffers);

            using var sBuild = PmBuildLayerInput.Auto();

            // Colour and RANK are indexed by ORDINAL, as RingFeatureIdx is; a slot index would permute colours
            // under a filter. Persistent, not TempJob: this runs off-main and can outlive 4 main-thread frames.
            NativeArray<Vector4> colors = new(geometry.FeatureCount, Allocator.Persistent);
            NativeArray<int>     order  = default;
            try
            {
                using var rankByOrdinal = new NativeArray<int>(geometry.FeatureCount, Allocator.Persistent);
                // A `using`-declared local rejects index ASSIGNMENT (CS1654), so the write below goes through
                // a plain-local VIEW over the same memory: GetSubArray(0, Length).
                NativeArray<int> rankByOrdinalWritable = rankByOrdinal.GetSubArray(0, rankByOrdinal.Length);
                // KEEP the -1 fill: a default NativeArray<int> is 0, a VALID rank — so without this, non-drawn
                // features (never ranked below) would read rank 0 and BuildRingVisitOrder would visit their rings.
                for (int i = 0; i < rankByOrdinalWritable.Length; i++) rankByOrdinalWritable[i] = -1; // -1 ⇒ not drawn

                int rank = 0;
                for (int si = 0; si < selectedFeatures.Count; si++)
                {
                    SelectedTileFeature selected = selectedFeatures[si];
                    IFeature feature = selected.Feature;

                    // Polygon-only. Not the sole guard — RingAssemblyJob has its own kind gate — but it
                    // stays, because it also decides which ordinals get a colour and how many rings are gathered.
                    if (feature.GeometryType != TileGeometryType.Polygon)
                        continue;

                    Color featureColor = Color.white;
                    // DATA-DRIVEN ONLY: BindFillPaintToApplier binds _BaseColor for exactly
                    // !DependsOnFeature. Ungated, the vertex stays white and the uniform carries the colour.
                    if (paint.Color.DependsOnFeature && paint.Color.TryEvaluate(zoom, feature, out var color))
                        featureColor = new Color((float)color.R, (float)color.G, (float)color.B, (float)color.A);

                    // Gamma: sRGB→linear here, off the main thread. white.linear == white.
                    Color lin = featureColor.linear;

                    // Only data-driven opacity bakes here; constant/zoom stays on _Opacity, or it would multiply
                    // twice. Color.linear converts rgb only, as alpha is already linear.
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
                    // fill-antialias: the only consumer of the parsed property. Limitation: a zoom expression
                    // resolves at the BUILD zoom, so a zoom-varying value applies only when the tile re-meshes.
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
        /// this layer does not draw (<c>rank == -1</c>). Non-local invariant: this is byte-identical to
        /// "reorder the feature list, then decode it" only because the counting sort is <b>stable</b>:
        /// <list type="bullet">
        /// <item>rings of one feature stay <b>contiguous</b> — <c>RingAssemblyJob</c> resets its exterior sign
        /// on a feature change, so a split feature's second run would be re-read as a fresh exterior;</item>
        /// <item>within a feature, rings keep <b>ascending ring index</b> = decode order, which is what makes
        /// earcut's hole-bridge sort (tiebroken on ring index) land where the reorder form puts it.</item>
        /// </list>
        /// <paramref name="buffers"/>, when non-null, draws <c>rankStart</c> and its cursor from the pool
        /// instead of a fresh array — same arithmetic, byte-identical <c>order</c>. The returned
        /// <see cref="NativeArray{T}"/> is always a fresh <c>Allocator.Persistent</c> array the caller disposes.
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
        /// The sizing and <see cref="FillStreamWriteJob"/> half of <see cref="ScheduleWrite"/>, split out so a
        /// test can write into its own <paramref name="md"/>. Declares <paramref name="md"/>'s buffers and
        /// schedules the job. Returns UNCOMPLETED: the caller completes the handle before reading bounds, and
        /// disposes the bounds. Main-thread only.
        /// </summary>
        /// <param name="md">The <c>Mesh.MeshData</c> slot to size and write into — caller-owned.</param>
        /// <param name="output">A layer's completed measure-graph output — caller-verified non-empty
        /// (<c>TileVertices.Length &gt; 0 &amp;&amp; TriangleIndices.Length &gt; 0</c>) and error-free.</param>
        /// <param name="featureColors">Per-feature linear colour, indexed by <c>output.VertexFeatureIdx</c>.</param>
        /// <param name="tile">The layer's tile address — feeds <see cref="TileSpanWorldUnits"/>.</param>
        /// <param name="extent">The layer's tile extent — feeds the pattern-coordinate scale.</param>
        /// <remarks><c>internal</c>, not <c>private</c>: the production write node is
        /// <see cref="ScheduleWrite"/>; a test-assembly caller reaches this second, through
        /// <c>MapRenderer.Unity</c>'s own <c>InternalsVisibleTo("MapRenderer.Tests.Shared")</c>
        /// grant.</remarks>
        internal static (JobHandle Handle, NativeArray<float3x2> Bounds) ScheduleStreamWrite(
            Mesh.MeshData md, FillGraphOutput output, NativeArray<Vector4> featureColors, TileId tile, double extent)
        {
            int vertexCount = output.TileVertices.Length;
            int indexCount  = output.TriangleIndices.Length;

            // No GetVertexData/GetIndexData here — FillStreamWriteJob takes the whole Md and resolves every
            // stream/index view INSIDE Execute(): taken here as separate job fields, two of them would alias at Schedule.
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
        /// The write graph's node for one NON-EMPTY layer — owns the vertex-stream layout and the
        /// <see cref="Mesh.MeshData"/> boundary, so a caller never needs to know either. Allocates one
        /// exact-size <see cref="Mesh.MeshDataArray"/> sized to <paramref name="output"/>'s real
        /// vertex/index count and hands it to <see cref="ScheduleStreamWrite"/>. Returns UNCOMPLETED — the
        /// caller polls/completes <see cref="MeshWriteOutput.Handle"/> before taking the payload.
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