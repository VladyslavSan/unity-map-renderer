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
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Line = MapRenderer.Core.Style.Line;
using CoreColor = MapRenderer.Core.Expressions.Color;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
                                                        // 'Color' ambiguous with UnityEngine's

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// Per-layer line mesh builder. Mirrors <see cref="StyledFillTileBuilder"/> but for line-type style
    /// layers.
    ///
    /// Pipeline (on the Burst job graph, <see cref="LineMeshGraph"/>): BORROW the source-layer's shared
    /// tile-local
    ///   <see cref="TileGeometryBuffers"/> (Waist 1 — materialized once inside the DECODE and owned by the
    ///   decoded layer, lent to every style layer naming that source-layer) → gather this layer's
    ///   <b>selection</b> (by <see cref="SelectedTileFeature.Ordinal"/>) of rings with
    ///   <c>FeatureGeometryType == LineString</c> and span <c>&gt;= 2</c> → curvature-subdivide the centerline
    ///   in tile space per the projection's <see cref="IProjection.MaxRefineAngleRad"/> policy (a no-op for the
    ///   flat Mercator, whose tolerance is ∞) → project the subdivided centerline to an origin-relative
    ///   render-space <c>(point, up)</c> array → build the ribbon in 3D (<c>across = cross(along, up)</c>) →
    ///   stream write into a <c>Mesh.MeshData</c>.
    ///
    /// <para>Line does its own ring iteration and has <b>zero</b> interaction with <c>RingAssemblyJob</c> /
    /// <c>EarcutJob</c> / <c>FillMeshPipeline</c>: it has no polygon or hole concept, and its filter is a
    /// COUNT threshold (<c>&gt;= 2</c>), never an area threshold — a straight polyline has exactly zero
    /// signed area and fill's degenerate-area filter would drop it. The two consumers read the same
    /// unfiltered buffer through different filters; that is the point of the buffer being unfiltered.</para>
    ///
    /// <para>ONE code path for every projection. Winding is always correct — <c>across</c> is tied to the
    /// same <c>up</c> the centerline was projected with (one frame), so there is no per-projection winding flip,
    /// no separate tangent-basis second frame, and no curvature/handedness branch. This line ordering
    /// (Subdivide → Project → Triangulate) mirrors the fill one; see <c>GlobeLineWindingTests</c>.</para>
    ///
    /// Production reaches the graph asynchronously through <see cref="ScheduleWrite"/>, driven by
    /// <c>TileBuildGraph</c>. The synchronous, main-thread convenience over the graph — schedule
    /// <c>LineMeshGraph</c>, complete it right here, then write into a caller-allocated
    /// <c>Mesh.MeshData</c> — has zero production callers and lives in a test-assembly caller
    /// (reached via <c>InternalsVisibleTo</c>).
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3, surface up; +Y for Mercator) interleaved via <see cref="LinePositionNormal"/>.
    ///   Stream 1 — TexCoord0: extrusion across-direction (Float32x3, 3D tangent-plane; Y=0 for Mercator).
    ///   Stream 2 — TexCoord1: side + distanceAlong (Float32x2).
    ///   Stream 3 — Color (Float32x4) + TexCoord2/widthScale (Float32x1) interleaved via <see cref="LineWidthColor"/>. 20B stride.
    ///   Index buffer — UInt32.
    ///
    /// Color: a DATA-DRIVEN line-color is baked per feature via <see cref="StyleProperty{T}"/>, converted
    /// to linear via <c>Color.linear</c> off the main thread, over a white <c>_BaseColor</c>. A constant or
    /// zoom-only line-color is NOT baked — it rides <c>_BaseColor</c>, bound by
    /// <see cref="Materials.MaterialFactory.BindLinePaintToApplier"/> for exactly the complementary case, over a
    /// white vertex. Exactly one of the two carries the color, both rgb AND alpha; the fragment multiplies them,
    /// so baking a constant color as well renders it squared.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static partial class StyledLineTileBuilder
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
        /// Color + WidthScale interleaved on stream 3. Canonical field order matches the canonical
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

        // RibbonJob's round-arc division default. MiterLimit/RoundLimit are NOT defaulted here:
        // layout.MiterLimit/layout.RoundLimit thread the style's own values (or LayoutProperties' own
        // 2.0/1.05 defaults when unset).
        private const int DefaultRoundSegments = 4;

        // Lines project through the SAME projection surface as fills, not a bespoke hardcoded
        // WebMercator.Forward. This single seam defaults to WebMercator.
        private static readonly IProjection DefaultProjection = new WebMercatorProjection();

        // White vertex color = identity multiply.
        private static readonly Vector4 WhiteColor = new Vector4(1f, 1f, 1f, 1f);

        /// <summary>
        /// The per-feature bake — the work that precedes the ring gather, so a graph-arm processor can
        /// run it on the seam and hand the result to the pump. Mirrors <c>StyledFillTileBuilder.BuildLayerInput</c>'s split: bakes this
        /// layer's per-feature membership, colour and width columns (<paramref name="featureColors"/>/
        /// <paramref name="featureWidths"/>, caller-owned from here on, indexed by feature ORDINAL — the same
        /// index the buffer's <c>RingFeatureIdx</c> names), and returns a <see cref="LayerInput"/> whose
        /// <c>FeatureSelected</c> column the ring-gather node reads.
        ///
        /// <para>Returns <c>default</c> (an uncreated <see cref="LayerInput"/>, with
        /// <paramref name="featureColors"/>/<paramref name="featureWidths"/> also left uncreated) when there
        /// is nothing to build — an absent <paramref name="selectedFeatures"/> list or an uncreated
        /// <paramref name="geometry"/>. Unlike fill, EVERY selected feature marks its ordinal selected
        /// (line has no polygon-only gate at this stage — the LineString kind filter lives in the ring-gather
        /// node, not here), so a non-empty <paramref name="selectedFeatures"/> always returns a created
        /// value; the caller reads <c>Input.FeatureSelected.IsCreated</c> to tell "nothing to build" from a
        /// real request, exactly as fill's caller reads <c>Input.RingVisitOrder.IsCreated</c>.</para>
        /// </summary>
        /// <param name="featureColors">Per-feature linear colour, Persistent-allocated and owned by the
        /// caller from here on — created iff the return value is (both share one fate).</param>
        /// <param name="featureWidths">Per-feature width scale, same ownership as <paramref name="featureColors"/>.</param>
        internal static LayerInput BuildLayerInput(
            IReadOnlyList<SelectedTileFeature> selectedFeatures,
            TileGeometryBuffers                geometry, // BORROWED — the store owns it; never disposed here
            Line.PaintProperties               paint,
            Line.LayoutProperties              layout,
            double                             zoom,
            double3                            tileOriginRender,
            out NativeArray<Vector4>           featureColors,
            out NativeArray<float>             featureWidths,
            IProjection                        projection = null) // null ⇒ WebMercator (launch-time config threads this in)
        {
            featureColors = default;
            featureWidths = default;

            if (selectedFeatures == null || selectedFeatures.Count == 0 || !geometry.IsCreated)
                return default;

            projection ??= DefaultProjection; // null ⇒ WebMercator; the launch-time projection is threaded via BuildGraphRequest

            // The per-feature columns below are sized to the LAYER and indexed by
            // SelectedTileFeature.Ordinal, because the ordinal is what the buffer's RingFeatureIdx names. A
            // slot-indexed column would permute colours and widths the moment this layer's filter rejects
            // anything — the geometry would stay right and only the attribution would be wrong.

            // Every owned native handle below is Allocator.Persistent, NOT TempJob: a build can span MORE
            // than 4 main-thread frames off the main thread, which trips TempJob's 4-frame lifetime check.
            // featureColors/featureWidths/featSelected are RETURNED (caller-owned from here), so they are
            // plain locals, not `using var`; a try/catch disposes them on a mid-method throw.
            NativeArray<Vector4> colors   = new(geometry.FeatureCount, Allocator.Persistent);
            NativeArray<float>   widths   = new(geometry.FeatureCount, Allocator.Persistent);
            NativeArray<bool>    selected = new(geometry.FeatureCount, Allocator.Persistent);
            try
            {
                for (int si = 0; si < selectedFeatures.Count; si++)
                {
                    SelectedTileFeature sel     = selectedFeatures[si];
                    IFeature            feature = sel.Feature;
                    int                 ordinal = sel.Ordinal;

                    selected[ordinal] = true;
                    colors[ordinal]   = WhiteColor;
                    widths[ordinal]   = 1f;

                    // The paint bakes stay LINE-ONLY: a non-LineString feature's rings are dropped by the kind
                    // gate, so evaluating its expressions would be new work with no output.
                    if (feature.GeometryType != TileGeometryType.LineString)
                        continue;

                    // Bake per-feature vertex color from the data-driven paint expression (sRGB→linear here,
                    // off-main-thread) — DATA-DRIVEN ONLY. BindLinePaintToApplier binds _BaseColor for exactly
                    // !DependsOnFeature, and the fragment computes `_BaseColor.rgb * vColor.rgb` (alpha likewise),
                    // so baking a constant color here too would render it squared. Ungated, the vertex stays
                    // WhiteColor and the uniform carries the whole color.
                    if (paint.Color.DependsOnFeature &&
                        paint.Color.TryEvaluate(zoom, feature, out CoreColor c))
                    {
                        var unityColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
                        var linear     = unityColor.linear;
                        colors[ordinal] = new Vector4(linear.r, linear.g, linear.b, linear.a);
                    }

                    // Data-driven opacity: bake evaluated opacity into vertex alpha, ONLY when opacity
                    // depends on the feature (else BindLinePaintToApplier already bound _Opacity and baking
                    // double-applies). NativeArray's indexer returns a value, not a variable, so this is a
                    // read-modify-write.
                    if (paint.Opacity.DependsOnFeature)
                    {
                        if (paint.Opacity.TryEvaluate(zoom, feature, out float opacityVal))
                        {
                            Vector4 col = colors[ordinal];
                            col.w *= opacityVal;
                            colors[ordinal] = col;
                        }
                    }

                    // Data-driven width: bake evaluated width into WidthScale (multiplier on _Width). When
                    // width depends on the feature, BindLinePaintToApplier binds _Width as a device-px constant
                    // of 1 — so _Width == the device-pixel ratio (1.0 at dpr 1) and WidthScale carries the full
                    // evaluated LOGICAL pixel width; otherwise WidthScale stays the ribbon's factor. The bake
                    // is dpr-free: scaling it would put the ratio inside the geometry, so a live ratio change
                    // would need a full mesh rebuild and PreparedTileCache would serve it stale.
                    if (paint.Width.DependsOnFeature)
                    {
                        if (paint.Width.TryEvaluate(zoom, feature, out float widthVal))
                            widths[ordinal] = math.max(0f, widthVal);
                    }
                }

                featureColors = colors;
                featureWidths = widths;
                return new LayerInput
                {
                    Geometry          = geometry,
                    FeatureSelected   = selected,
                    OriginRender      = tileOriginRender,
                    Projection        = projection,
                    Join              = layout.Join, // Join/Cap are already parsed enums (no per-build string switch)
                    Cap               = layout.Cap,
                    MiterLimit        = layout.MiterLimit,
                    RoundLimit        = layout.RoundLimit,
                    RoundSegments     = DefaultRoundSegments,
                    MaxOutputVertices = LineMeshGraph.DefaultMaxOutputVertices,
                };
            }
            catch
            {
                colors.Dispose();
                widths.Dispose();
                selected.Dispose();
                throw;
            }
        }

        // ── Write step (job-scheduling-design.md) ─────────────────────────────────────────────────

        /// <summary>
        /// The write graph's node for one NON-EMPTY line layer — owns the vertex-stream layout and the
        /// <see cref="Mesh.MeshData"/> boundary. Declares <paramref name="md"/>'s buffers and schedules
        /// <see cref="LineStreamWriteJob"/>. Returns UNCOMPLETED — the caller completes the handle before
        /// reading bounds, and disposes the bounds array itself.
        ///
        /// <para><b>Main-thread only</b>: scheduling <see cref="LineStreamWriteJob"/> is a Unity job-system
        /// operation.</para>
        /// </summary>
        /// <param name="md">The <c>Mesh.MeshData</c> slot to size and write into — caller-owned.</param>
        /// <param name="output">A layer's completed measure-graph output — caller-verified non-empty
        /// (<c>Vertices.Length &gt; 0 &amp;&amp; Indices.Length &gt; 0</c>) and error-free.</param>
        /// <param name="featureColors">Per-feature linear colour, indexed by <c>output.VertexFeatureIdx</c>.</param>
        /// <param name="featureWidths">Per-feature width scale, same indexing.</param>
        /// <remarks><c>internal</c>, not <c>private</c>:
        /// <c>TestTileMeshBuilder.BuildLineFromLayer{TProj}</c> (the generic, Burst-unregistered-projection
        /// entry point) completes the write step through this method directly, reached across the assembly
        /// boundary via <c>MapRenderer.Unity</c>'s own <c>InternalsVisibleTo("MapRenderer.Tests.Shared")</c>
        /// grant.</remarks>
        internal static (JobHandle Handle, NativeArray<float3x2> Bounds) ScheduleStreamWrite(
            Mesh.MeshData md, LineGraphOutput output, NativeArray<Vector4> featureColors, NativeArray<float> featureWidths)
        {
            int vertexCount = output.Vertices.Length;
            int indexCount  = output.Indices.Length;

            md.SetVertexBufferParams(vertexCount, LineVertexDescriptors);
            md.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles), NoValidate);

            var bounds = new NativeArray<float3x2>(1, Allocator.Persistent);

            JobHandle handle = new LineStreamWriteJob
            {
                Vertices = output.Vertices, VertexFeatureIdx = output.VertexFeatureIdx, Indices = output.Indices,
                FeatureColors = featureColors, FeatureWidths = featureWidths,
                Md = md, OutBounds = bounds,
            }.Schedule();

            return (handle, bounds);
        }

        /// <summary>
        /// The write graph's per-layer entry point — allocates one
        /// exact-size <see cref="Mesh.MeshDataArray"/> sized to <paramref name="output"/>'s real
        /// vertex/index count and hands it to <see cref="ScheduleStreamWrite"/>. Returns UNCOMPLETED — the
        /// caller polls/completes <see cref="MeshWriteOutput.Handle"/> before taking the payload. Mirrors
        /// <c>StyledFillTileBuilder.ScheduleWrite</c> step for step; <see cref="MeshWriteOutput"/> is already
        /// mesh-neutral, so this is a second caller, not a second type.
        /// </summary>
        /// <param name="output">A layer's completed measure-graph output — caller-verified non-empty
        /// (<c>Vertices.Length &gt; 0 &amp;&amp; Indices.Length &gt; 0</c>) and error-free.</param>
        /// <param name="featureColors">Per-feature linear colour, indexed by <c>output.VertexFeatureIdx</c>.</param>
        /// <param name="featureWidths">Per-feature width scale, same indexing.</param>
        internal static MeshWriteOutput ScheduleWrite(
            LineGraphOutput output, NativeArray<Vector4> featureColors, NativeArray<float> featureWidths)
        {
            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            (JobHandle handle, NativeArray<float3x2> bounds) =
                ScheduleStreamWrite(mda[0], output, featureColors, featureWidths);

            return new MeshWriteOutput
            {
                Mda = mda, Bounds = bounds, VertexCount = output.Vertices.Length, Handle = handle, IsCreated = true,
            };
        }
    }
}
