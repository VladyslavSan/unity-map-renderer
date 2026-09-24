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
    /// Per-layer line mesh builder, the line counterpart of <see cref="StyledFillTileBuilder"/>. It bakes the
    /// per-feature columns and schedules the mesh write. <see cref="LineMeshGraph"/> runs the geometry over the
    /// borrowed <see cref="TileGeometryBuffers"/>: gather LineString rings of span <c>&gt;= 2</c>, subdivide,
    /// project to <c>(point, up)</c>, then build the ribbon in 3D (<c>across = cross(along, up)</c>).
    /// Non-obvious why: the ring filter is a vertex count, not fill's area test, because a straight polyline has
    /// zero signed area. One code path serves every projection, because <c>across</c> uses the projection's own
    /// <c>up</c>; see <c>GlobeLineWindingTests</c> and
    /// docs/meshing-design.md § "There is no single linear order — fills and lines compose these differently".
    /// A data-driven colour is baked per vertex; a constant one rides <c>_BaseColor</c>
    /// (docs/meshing-design.md § "Styling as material properties").
    /// </summary>
    /// <remarks>
    /// Streams (Unity's cap is 4): 0 — Position + Normal (Float32x3 each; Normal is surface up, +Y for
    /// Mercator), <see cref="LinePositionNormal"/>. 1 — TexCoord0, across-direction (Float32x3; Y=0 for
    /// Mercator). 2 — TexCoord1, side + distanceAlong (Float32x2). 3 — Color (Float32x4) + TexCoord2
    /// widthScale (Float32x1), <see cref="LineWidthColor"/>. Index buffer UInt32.
    /// </remarks>
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

        // Canonical ascending VertexAttribute order avoids Unity's "non-standard order" warning; stream 3 matches
        // LineWidthColor. Internal so the SyntheticLineMesh test helper reuses the production layout.
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

        // RibbonJob's round-arc division default. MiterLimit/RoundLimit come from the layout, which holds the
        // style's values or its own defaults.
        private const int DefaultRoundSegments = 4;

        // Lines project through the SAME projection surface as fills, not a bespoke hardcoded
        // WebMercator.Forward. This single seam defaults to WebMercator.
        private static readonly IProjection DefaultProjection = new WebMercatorProjection();

        // White vertex color = identity multiply.
        private static readonly Vector4 WhiteColor = new Vector4(1f, 1f, 1f, 1f);

        /// <summary>
        /// The per-feature bake that precedes the ring gather, the line half of
        /// <c>StyledFillTileBuilder.BuildLayerInput</c>. Bakes the selection, colour and width columns, indexed by
        /// feature ORDINAL (the index <c>RingFeatureIdx</c> names), into a <see cref="LayerInput"/>. Returns
        /// <c>default</c>, with both out columns uncreated, when there are no selected features or
        /// <paramref name="geometry"/> is uncreated; the caller tests <c>Input.FeatureSelected.IsCreated</c>.
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

            // The columns are indexed by Ordinal, the index RingFeatureIdx names. A slot-indexed column would
            // permute colours and widths as soon as this layer's filter rejects a feature.

            // Persistent, not TempJob: an off-main build can outlive TempJob's 4-frame limit. The arrays are
            // returned to the caller, so they are not `using`; the catch disposes them on a throw.
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

                    // Bake DATA-DRIVEN colour only (sRGB→linear). BindLinePaintToApplier binds a constant to
                    // _BaseColor, and the fragment multiplies the two, so baking a constant renders it squared.
                    if (paint.Color.DependsOnFeature &&
                        paint.Color.TryEvaluate(zoom, feature, out CoreColor c))
                    {
                        var unityColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
                        var linear     = unityColor.linear;
                        colors[ordinal] = new Vector4(linear.r, linear.g, linear.b, linear.a);
                    }

                    // Bake opacity into vertex alpha only when it is data-driven; otherwise the bound _Opacity
                    // applies it. The indexer returns a copy, so this is a read-modify-write.
                    if (paint.Opacity.DependsOnFeature)
                    {
                        if (paint.Opacity.TryEvaluate(zoom, feature, out float opacityVal))
                        {
                            Vector4 col = colors[ordinal];
                            col.w *= opacityVal;
                            colors[ordinal] = col;
                        }
                    }

                    // Bake data-driven width in LOGICAL px into WidthScale; _Width is then a device-px 1. A ratio
                    // in the bake would force a mesh rebuild on a dpr change, and PreparedTileCache would serve it stale.
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
        /// The write graph's node for one NON-EMPTY line layer. Declares <paramref name="md"/>'s buffers and
        /// schedules <c>LineStreamWriteJob</c>, so it runs on the main thread only. Returns UNCOMPLETED — the
        /// caller completes the handle before reading bounds, and disposes the bounds array itself.
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
        /// The write graph's per-layer entry point, the line twin of <c>StyledFillTileBuilder.ScheduleWrite</c>.
        /// Allocates one <c>Mesh.MeshDataArray</c> and hands it to <c>ScheduleStreamWrite</c>. Returns
        /// UNCOMPLETED — the caller completes <see cref="MeshWriteOutput.Handle"/> before taking the payload.
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
