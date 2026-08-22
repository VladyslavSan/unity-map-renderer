using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Tiles;
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
    /// S89 Stage B — <see cref="WriteMeshData"/> builds the mesh AND writes directly into a caller-allocated
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
        /// <summary>Profiler marker name constants — the single source of truth for this builder's telemetry
        /// contract. Referenced by both the <see cref="ProfilerMarker"/> fields below and the marker tests
        /// (<c>ProfilerMarkerTests</c>, <c>MapViewAsyncMeshBuildTests</c>) so each string lives in exactly one
        /// place; renaming a marker is a one-line edit here that the tests pick up automatically.</summary>
        public static class ProfilerMarkerNames
        {
            public const string WriteMeshData = "MapRenderer.Meshing.StyledFillTileBuilder.WriteMeshData";
        }

        // Wraps the decode/assemble/earcut/project/write loop. Fires on a background ThreadPool thread (S47/S51);
        // ProfilerMarkerTests tooth-1b uses ProfilerRecorderOptions.Default (not CollectOnlyOnCurrentThread) so
        // cross-thread samples are captured.
        private static readonly ProfilerMarker PmWriteMeshData =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.WriteMeshData);

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
        };

        // Constant tangent: +X direction, +1 bitangent sign (right-handed), valid for flat +Y-normal fill.
        // S91-C bakes the projection's east into this stream for the globe; constant +X is Mercator-correct.
        private static readonly Vector4 FlatTangent = new Vector4(1f, 0f, 0f, 1f);

        // S91-A: the projection the geometry is built with. Launch-time config (the host) threads a
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
        /// <para><paramref name="scratch"/> (perf/gc-elimination): when non-null, the working buffers and the
        /// sort comparer are drawn from the caller's pooled <see cref="TileBuildScratch"/> instead of being
        /// allocated fresh — byte-identical output, zero managed allocation once the buffers have grown to
        /// this tile's peak feature count. <c>null</c> (tests, non-pooled callers) keeps the original
        /// allocating behaviour verbatim.</para>
        /// </summary>
        private static IReadOnlyList<SelectedTileFeature> OrderBySortKey(
            IReadOnlyList<SelectedTileFeature> features, Fill.LayoutProperties layout, double zoom,
            TileBuildScratch scratch)
        {
            if (layout == null || layout.SortKeyIsDefault) return features;

            int count = features.Count;
            float[] sortKeys;
            int[]   declaredOrder;
            if (scratch != null)
            {
                sortKeys      = scratch.SortKeys(count);
                declaredOrder = scratch.DeclaredOrder(count);
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

            if (scratch != null)
            {
                // The pool's reusable IComparer<int> field — no per-call closure/delegate allocation, unlike
                // the lambda overload below. Sorted range is [0, count) — the backing arrays may be LONGER
                // (grow-only, sized to a prior build's peak), so the 3-arg range overload is load-bearing,
                // not cosmetic.
                System.Array.Sort(declaredOrder, 0, count, scratch.SortKeyComparer(sortKeys));

                SelectedTileFeature[] orderedBuffer = scratch.OrderedFeaturesBuffer(count);
                for (int i = 0; i < count; i++) orderedBuffer[i] = features[declaredOrder[i]];
                // A fixed-length [0, count) VIEW over the buffer, never the raw (possibly longer) array —
                // returning the array itself would let a shorter later build's Count read as a stale larger one.
                return scratch.OrderedFeaturesView(count);
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

        // ── Public API ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the mesh for all fill features of one style layer and write it directly into
        /// <paramref name="md"/> (a caller-allocated <c>Mesh.MeshData</c>, count-1 slot). Runs the full
        /// decode/assemble/earcut/project loop plus the sRGB→linear color conversion off the main thread.
        ///
        /// <para>Returns <paramref name="vertexCount"/> = 0 (and leaves <paramref name="md"/> untouched) when
        /// no polygon geometry is produced — the caller then disposes the unused writable-mesh-data without
        /// creating a Mesh. On success, <paramref name="bounds"/> carries the worker-computed tight AABB
        /// (assigned to <c>Mesh.bounds</c> after apply, avoiding a main-thread RecalculateBounds scan).</para>
        ///
        /// <para><paramref name="clip"/> is how much of the tile's MVT buffer survives into the mesh
        /// (<see cref="TileBufferClip"/>). It is trailing and optional because <c>default</c> means DISABLED:
        /// every caller that does not pass one keeps the pre-clip geometry exactly.</para>
        ///
        /// <para><b>No <c>TileId</c> parameter</b> (B7a review N1): the tile address is
        /// <c>geometry.Tile</c>, the producer's own declaration, exactly as the extent is
        /// <c>geometry.Extent</c>. A second copy alongside the buffer is what would let a caller pair a z0
        /// buffer with a z1 address — base vertices from one tile, pattern scale and globe subdivision from
        /// another — so the seam does not accept one (<c>ITileGeometryMaterializer</c>, "Self-describing").</para>
        ///
        /// <para><paramref name="scratch"/> (perf/gc-elimination): this build's rented <see cref="TileBuildScratch"/>,
        /// threaded into <see cref="OrderBySortKey"/> and <see cref="BuildRingVisitOrder"/> so the pooled worker
        /// path (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>) reuses its scratch buffers instead of
        /// allocating fresh ones every build. <c>null</c> (every non-pooled caller, incl. tests) keeps the
        /// original allocating behaviour.</para>
        /// </summary>
        public static void WriteMeshData(
            Mesh.MeshData                      md,
            IReadOnlyList<SelectedTileFeature> selectedFeatures,
            TileGeometryBuffers                geometry, // BORROWED — the store owns it; never disposed here
            Fill.PaintProperties               paint,
            double                             zoom,
            double3                            tileOriginRender,
            out int                            vertexCount,
            out Bounds                         bounds,
            IProjection                        projection = null, // null ⇒ WebMercator (launch-time config threads this in)
            Fill.LayoutProperties              layout     = null, // null ⇒ no fill-sort-key (declared feature order)
            TileBufferClip                     clip       = default, // default ⇒ disabled ⇒ the whole tile buffer is drawn
            TileBuildScratch                   scratch    = null) // null ⇒ allocate (non-pooled caller)
        {
            vertexCount = 0;
            bounds      = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0 || !geometry.IsCreated)
                return;

            // fill-sort-key: features draw in ASCENDING key order, so a higher key lands LATER in the index
            // buffer and therefore ON TOP — this layer's features share one mesh drawn under a
            // painter's-algorithm ZWrite-Off contract, where triangle order IS draw order for coincident
            // polygons. Absent key ⇒ no sort at all, keeping the source's declared order byte-for-byte.
            selectedFeatures = OrderBySortKey(selectedFeatures, layout, zoom, scratch);

            // The marker string (ProfilerMarkerNames.WriteMeshData) is a telemetry contract asserted by
            // ProfilerMarkerTests + MapViewAsyncMeshBuildTests, which read the same const — rename in one place.
            using var sBuild = PmWriteMeshData.Auto();

            // Bake this layer's per-feature linear colour, and record each surviving polygon feature's RANK —
            // its position in fill-sort-key order. Both are indexed by the feature's ORDINAL in the source
            // layer, because that is what the shared buffer's RingFeatureIdx names; a slot-indexed array would
            // permute colours the moment this layer's filter rejects anything.
            // Rank 3 GC fix: the two per-feature columns are NativeArray, disposed via `using var` (ClearMemory
            // zero-init) — construction and disposal are a single statement, so a partial-construction throw
            // can't strand an already-built handle. Allocator.Persistent, NOT TempJob: this runs off-main
            // (UniTask.RunOnThreadPool) and a build can span >4 main-thread frames — TempJob's 4-frame lifetime
            // check would flag/reclaim it mid-build. Persistent has no frame limit; `using var` still disposes.
            using var featureColors = new NativeArray<Vector4>(geometry.FeatureCount, Allocator.Persistent);
            using var rankByOrdinal = new NativeArray<int>(geometry.FeatureCount, Allocator.Persistent);
            // A `using`-declared local is read-only for index-ASSIGNMENT (CS1654) — reads through
            // featureColors/rankByOrdinal (including passing them by value to WriteGeometry below) are
            // unaffected; only the writes need a plain-local alias. GetSubArray(0, Length) is a normal method
            // call returning a NativeArray<T> VIEW over the same memory, assignable to a non-readonly local.
            NativeArray<Vector4> featureColorsWritable = featureColors.GetSubArray(0, featureColors.Length);
            NativeArray<int>     rankByOrdinalWritable = rankByOrdinal.GetSubArray(0, rankByOrdinal.Length);
            // KEEP the -1 fill: a default NativeArray<int> is 0, a VALID rank — so without this, non-drawn
            // features (never ranked below) would read rank 0 and BuildRingVisitOrder would visit their rings.
            for (int i = 0; i < rankByOrdinalWritable.Length; i++) rankByOrdinalWritable[i] = -1; // -1 ⇒ not drawn by this layer

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
                if (paint.Color.TryEvaluate(zoom, feature, out var color))
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

                featureColorsWritable[selected.Ordinal] = new Vector4(lin.r, lin.g, lin.b, featureAlpha);
                rankByOrdinalWritable[selected.Ordinal] = rank++;
            }

            if (rank == 0)
                return; // no polygon geometry — md left untouched; caller disposes the unused MeshData

            using var ringVisitOrder = BuildRingVisitOrder(geometry, rankByOrdinal, rank, scratch);
            WriteGeometry(md, geometry, ringVisitOrder, featureColors,
                tileOriginRender, projection, clip, out vertexCount, out bounds);
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
        /// <para><paramref name="scratch"/> (perf/gc-elimination): when non-null, <c>rankStart</c> and the
        /// cursor it seeds are drawn from the pool instead of a fresh <c>new int[]</c> + <c>Array.Clone</c> —
        /// same counting-sort arithmetic, byte-identical <c>order</c>. The returned <see cref="NativeArray{T}"/>
        /// itself is UNCHANGED by pooling — still a fresh <c>Allocator.Persistent</c> array the caller disposes
        /// (D1a idiom); only the two MANAGED <c>int[]</c> scratch buffers move to the pool.</para>
        /// </summary>
        private static NativeArray<int> BuildRingVisitOrder(
            TileGeometryBuffers geometry, NativeArray<int> rankByOrdinal, int rankCount, TileBuildScratch scratch)
        {
            int   rankStartLength = rankCount + 1;
            int[] rankStart       = scratch != null ? scratch.RankStart(rankStartLength) : new int[rankStartLength];
            int   visited         = 0;
            for (int r = 0; r < geometry.RingCount; r++)
            {
                int rank = rankByOrdinal[geometry.RingFeatureIdx[r]];
                if (rank < 0) continue;
                rankStart[rank + 1]++;
                visited++;
            }
            for (int i = 0; i < rankCount; i++) rankStart[i + 1] += rankStart[i];

            // Allocator.Persistent, NOT TempJob: owned + disposed by the caller (WriteMeshData) via `using var`,
            // but off-main a build can span >4 main-thread frames, so TempJob's 4-frame check would trip.
            var order = new NativeArray<int>(visited, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int[] cursor;
            if (scratch != null)
            {
                cursor = scratch.RankCursor(rankStartLength);
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
        /// The geometry half of <see cref="WriteMeshData"/>: run the Burst decode→assemble→earcut→project
        /// chain over an already-built <paramref name="geometry"/> producer and stream the result into
        /// <paramref name="md"/>, colouring each vertex from <paramref name="featureColors"/> indexed by the
        /// producer's own feature ordinal.
        ///
        /// <para>Split out so a caller whose geometry is <b>not</b> a selected-feature list — the background
        /// quad, whose four corners come straight from a <c>PathGeometryMaterializer</c> — reuses this path
        /// verbatim instead of hand-authoring a synthetic feature to feed the loop above.</para>
        ///
        /// <para><paramref name="geometry"/> is <b>borrowed</b> (fill's caller borrows it from the store; the
        /// background quad mints and owns its own), and <paramref name="featureColors"/> is indexed by the
        /// buffer's own <b>feature ordinal</b> — the same index <c>RingFeatureIdx</c> carries. The tile
        /// <b>address and extent both come off the buffer</b>, never from a second copy alongside it: the
        /// address sets the pattern stream's world span and the globe subdivision's tile, so a buffer/id
        /// mismatch would render base vertices from one tile with pattern coordinates from another.</para>
        /// </summary>
        internal static void WriteGeometry(
            Mesh.MeshData               md,
            TileGeometryBuffers         geometry,
            NativeArray<int>            ringVisitOrder,
            NativeArray<Vector4>        featureColors,
            double3                     tileOriginRender,
            IProjection                 projection,
            TileBufferClip              clip,
            out int                     vertexCount,
            out Bounds                  bounds)
        {
            vertexCount = 0;
            bounds      = default;

            // The producer is the sole authority for both (ITileGeometryMaterializer, "Self-describing").
            TileId id     = geometry.Tile;
            double extent = geometry.Extent;

            TileMeshBuffers buffers = FillMeshPipeline.Schedule(new FillMeshPipeline.LayerInput
            {
                Geometry          = geometry,
                RingVisitOrder    = ringVisitOrder,
                OriginRender      = tileOriginRender,
                Projection        = projection ?? DefaultProjection,
                Clip              = clip,
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
                // A finite refine tolerance ⇒ a curved surface to subdivide onto; PositiveInfinity ⇒ the flat
                // Mercator sheet, direct write. (Was a curvature capability flag; the fill's own 3° granularity
                // constant in GlobeFillSubdivideDispatch is unchanged, so globe fill output stays byte-identical.)
                if (!double.IsInfinity(proj.MaxRefineAngleRad))
                {
                    WriteGlobeSubdivided(md, in buffers, proj, id, extent, tileOriginRender, featureColors,
                        out vertexCount,     out bounds);
                    return; // finally still disposes buffers
                }

                // Then declare the mesh buffers on the MeshData (off-main-thread safe) and grab stream views.
                md.SetVertexBufferParams(totalVerts, FillVertexDescriptors);
                NativeArray<FillPositionNormal> s0 = md.GetVertexData<FillPositionNormal>(0);
                NativeArray<Vector2>            s1 = md.GetVertexData<Vector2>(1);
                NativeArray<Vector4>            s2 = md.GetVertexData<Vector4>(2);
                NativeArray<Vector4>            s3 = md.GetVertexData<Vector4>(3);
                md.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);
                NativeArray<int> indices = md.GetIndexData<int>();

                // Phase 3: copy the Burst geometry into the stream views, accumulating the tight AABB.
                double extentInv = extent > 0.0 ? 1.0 / extent : 0.0;
                double tileSpanWorldUnits = TileSpanWorldUnits(id);
                float3 bMin      = new float3(float.MaxValue);
                float3 bMax      = new float3(float.MinValue);
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
                        Position = new Vector3(v.x,  v.y,  v.z),
                        Normal   = new Vector3(up.x, up.y, up.z),
                    };

                    double2 tv = buffers.TileVertices[i];
                    s1[i] = PatternCoord(tv, extentInv, tileSpanWorldUnits);
                    s2[i] = FlatTangent; // Mercator: constant +X east (globe → subdivided path)
                    s3[i] = featureColors[buffers.VertexFeatureIdx[i]]; // per-feature linear color
                }

                // Reverse triangle winding at this GPU-index boundary: the canonical earcut IR is CCW in tile
                // space, but the ECEF→render mapping is orientation-reversing (§7.1), so raw winding renders
                // back-faces toward the camera. Swapping the 2nd/3rd index per triangle makes the front face
                // genuinely Unity-front → stock Cull Back (MapFill _Cull:2). Mirrors StyledLineTileBuilder.
                for (int i = 0; i + 2 < totalIndices; i += 3)
                {
                    indices[i + 0] = buffers.TriangleIndices[i + 0];
                    indices[i + 1] = buffers.TriangleIndices[i + 2]; // 2nd/3rd
                    indices[i + 2] = buffers.TriangleIndices[i + 1]; // swapped
                }

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
            Mesh.MeshData md,               in TileMeshBuffers buffers, IProjection proj, TileId id, double extent,
            double3       tileOriginRender, NativeArray<Vector4> featureColors, out int vertexCount, out Bounds bounds)
        {
            vertexCount = 0;
            bounds      = default;

            int srcVerts   = buffers.VertexCount[0];
            int srcIndices = buffers.TotalIndexCount;

            // Allocator.Persistent, NOT TempJob: off-main build can span >4 main-thread frames (TempJob's
            // 4-frame lifetime check would trip). `using var` — construction and disposal are one statement.
            using var outV  = new NativeList<GlobeFillVertex>(srcVerts * 4, Allocator.Persistent);
            using var outIx = new NativeList<int>(srcIndices           * 4, Allocator.Persistent);

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
            double tileSpanWorldUnits = TileSpanWorldUnits(id);
            float3 bMin      = new float3(float.MaxValue);
            float3 bMax      = new float3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                GlobeFillVertex fv = outV[i];
                float3          v  = (float3)fv.World;
                bMin = math.min(bMin, v);
                bMax = math.max(bMax, v);
                float3 up = (float3)fv.Up;
                s0[i] = new FillPositionNormal
                    { Position = new Vector3(v.x, v.y, v.z), Normal = new Vector3(up.x, up.y, up.z) };
                s1[i] = PatternCoord(fv.Tile, extentInv, tileSpanWorldUnits);
                float3 east = (float3)fv.East;
                s2[i] = new Vector4(east.x, east.y, east.z, 1f); // w=+1: same TBN handedness as the Mercator path
                s3[i] = featureColors[fv.Feature];
            }

            // Reverse winding at the GPU-index boundary (same as the flat path above): the subdivided output
            // inherits the canonical earcut CCW order, flipped here to Unity-front for stock Cull Back.
            for (int i = 0; i + 2 < ni; i += 3)
            {
                indices[i + 0] = outIx[i + 0];
                indices[i + 1] = outIx[i + 2]; // 2nd/3rd
                indices[i + 2] = outIx[i + 1]; // swapped
            }

            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, ni, MeshTopology.Triangles), NoValidate);
            vertexCount = n;
            float3 c3 = (bMin + bMax) * 0.5f;
            float3 sz = bMax - bMin;
            bounds = new Bounds(new Vector3(c3.x, c3.y, c3.z), new Vector3(sz.x, sz.y, sz.z));
        }
    }
}