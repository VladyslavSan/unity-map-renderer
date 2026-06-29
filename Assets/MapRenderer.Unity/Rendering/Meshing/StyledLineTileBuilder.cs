using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;
using Line = MapRenderer.Core.Style.Line;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// S14 managed per-layer line mesh builder. Mirrors <see cref="StyledFillTileBuilder"/>
    /// but for line-type style layers.
    ///
    /// Pipeline per feature:
    ///   MvtGeometry.Decode → project to tile-local meters → LineTessellator.Triangulate
    ///   → stream assembly → NativeArray upload.
    ///
    /// S14 split (matching S47 fill async split):
    ///   <see cref="BuildMeshData"/> runs the full decode/tessellate/project loop off the main
    ///   thread, returning a <see cref="LayerMeshData"/> payload (IDisposable, holds NativeArrays).
    ///   <see cref="UploadMesh"/> uploads a <see cref="LayerMeshData"/> to a <see cref="Mesh"/> on the
    ///   main thread using the advanced NativeArray API.
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3, +Y) interleaved via <see cref="LinePositionNormal"/>.
    ///   Stream 1 — TexCoord0: extrusion across-direction (Float32x3, 3D tangent-plane; Y=0 for Mercator).
    ///   Stream 2 — TexCoord1: side + distanceAlong (Float32x2).
    ///   Stream 3 — Color (Float32x4) + TexCoord2/widthScale (Float32x1) interleaved via <see cref="LineWidthColor"/>. 20B stride.
    ///   Index buffer — UInt32.
    ///
    /// Color (D1): per-feature sRGB color baked via <see cref="StyleProperty{T}"/>; converted
    /// to linear via <c>Color.linear</c> off the main thread. <c>_BaseColor=white</c> on the Material
    /// (identity multiply). Never set <c>_BaseColor</c> to the style color for data-driven layers.
    ///
    /// Thread-safety: <see cref="BuildMeshData"/> touches only pure-managed, stateless Core code.
    /// All paths are allocation-local with no shared mutable static state.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class StyledLineTileBuilder
    {
        // ── Structs for interleaved NativeArray streams ─────────────────────────

        /// <summary>
        /// Tightly-packed Position + Normal struct for stream 0.
        /// Stride = 6 × 4 = 24 bytes.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LinePositionNormal
        {
            public Vector3 Position;
            public Vector3 Normal;
        }

        /// <summary>
        /// S14: Color + WidthScale interleaved on stream 3.
        /// Canonical field order matches the canonical descriptor order (Color enum=3 before
        /// TexCoord2 enum=6), so stream-3 byte offsets are Color@0, WidthScale@16.
        /// Stride = 16 (float4) + 4 (float) = 20 bytes.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LineWidthColor
        {
            public Vector4 Color;
            public float   WidthScale;
        }

        // ── Hoisted vertex attribute descriptor ─────────────────────────────────

        // Constructed once (static readonly) so UploadMesh does not allocate per-tile.
        // Canonical ascending VertexAttribute enum order (Position=0, Normal=1, Color=3,
        // TexCoord0=4, TexCoord1=5, TexCoord2=6) eliminates the Unity "non-standard order" warning.
        // Stream-3 interleave: Color (Float32x4, 16 bytes) then TexCoord2/WidthScale (Float32x1, 4 bytes),
        // matching LineWidthColor struct field order { Vector4 Color; float WidthScale }.
        private static readonly VertexAttributeDescriptor[] LineVertexDescriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 1, stream: 3),
        };

        // Constant +Y normal for all line vertices.
        private static readonly Vector3 UpNormal = Vector3.up;

        // White vertex color = identity multiply.
        private static readonly Vector4 WhiteColor = new Vector4(1f, 1f, 1f, 1f);

        // ── Payload ─────────────────────────────────────────────────────────────

        /// <summary>
        /// CPU-computed mesh data for one line style layer, held as NativeArray streams.
        /// Produced by <see cref="BuildMeshData"/> off the main thread; uploaded by
        /// <see cref="UploadMesh"/> on the main thread.
        ///
        /// OWNERSHIP: caller of <see cref="BuildMeshData"/> MUST call <see cref="Dispose"/> on
        /// EVERY exit path. <see cref="UploadMesh"/> does NOT dispose — dispose after upload.
        /// When no geometry was produced, <see cref="IsCreated"/> is false and Dispose() is a no-op.
        /// </summary>
        public struct LayerMeshData : IDisposable
        {
            // Stream 0: Position + Normal interleaved.
            public NativeArray<LinePositionNormal> Stream0PositionNormal;

            // Stream 1: extrusion across-direction (3D tangent-plane vector; Y=0 for Mercator).
            public NativeArray<Vector3> Stream1ExtrudeN;

            // Stream 2: side + distanceAlong.
            public NativeArray<Vector2> Stream2SideAndDist;

            // Stream 3: widthScale + color interleaved.
            public NativeArray<LineWidthColor> Stream3WidthColor;

            // Index buffer.
            public NativeArray<int> Indices;

            /// <summary>Vertex count. Zero when no geometry was produced.</summary>
            public int VertexCount;

            /// <summary>Index count. Zero when no geometry was produced.</summary>
            public int IndexCount;

            /// <summary>True when the NativeArray streams are allocated and valid.</summary>
            public bool IsCreated;

            /// <summary>
            /// S14 leak-guard counter: net live NativeArray allocations.
            /// Mirrors <see cref="StyledFillTileBuilder.LayerMeshData.LiveAllocCount"/>.
            /// </summary>
            internal static long LiveAllocCount;

            /// <summary>Test accessor for <see cref="LiveAllocCount"/>.</summary>
            public static long DebugLiveAllocCount => Interlocked.Read(ref LiveAllocCount);

            public void Dispose()
            {
                if (!IsCreated) return;
                IsCreated = false;
                if (Stream0PositionNormal.IsCreated) Stream0PositionNormal.Dispose();
                if (Stream1ExtrudeN.IsCreated) Stream1ExtrudeN.Dispose();
                if (Stream2SideAndDist.IsCreated) Stream2SideAndDist.Dispose();
                if (Stream3WidthColor.IsCreated) Stream3WidthColor.Dispose();
                if (Indices.IsCreated) Indices.Dispose();
                Interlocked.Decrement(ref LiveAllocCount);
            }
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// CPU-only half: tessellate all line features for one style layer, producing a
        /// <see cref="LayerMeshData"/> payload backed by <see cref="NativeArray{T}"/> streams.
        /// Safe to call from a ThreadPool thread.
        ///
        /// When no line geometry is produced, returns a default <see cref="LayerMeshData"/>
        /// with <see cref="LayerMeshData.IsCreated"/> == false (no Dispose needed).
        /// </summary>
        public static LayerMeshData BuildMeshData(
            IReadOnlyList<MvtFeature> selectedFeatures,
            Line.PaintProperties      paint,
            Line.LayoutProperties     layout,
            double                    zoom,
            double                    extent,
            TileId                    id,
            double2                   tileOriginMerc)
        {
            var empty = new LayerMeshData();

            if (selectedFeatures == null || selectedFeatures.Count == 0)
                return empty;

            // S60: Join/Cap are already parsed enums on LayoutProperties (no per-build string switch).
            JoinType joinType = layout.Join;
            CapType  capType  = layout.Cap;

            // Phase 1: tessellate all features into temporary managed lists.
            var tempVerts0  = new List<LinePositionNormal>(512);
            var tempVerts1  = new List<Vector3>(512);
            var tempVerts2  = new List<Vector2>(512);
            var tempVerts3  = new List<LineWidthColor>(512);
            var tempIndices = new List<int>(1024);

            foreach (var feature in selectedFeatures)
            {
                if (feature.GeometryType != MvtGeometryType.LineString)
                    continue;

                // Bake per-feature vertex color from the data-driven paint expression.
                // Color space: Core Color is sRGB [0,1]; convert to linear here (off-main-thread).
                // _BaseColor=white on the material → identity multiply (D1 / fills convention).
                Vector4 featureColor = WhiteColor;
                var     adapter      = new MvtFeatureAdapter(feature);
                // S60: Color is now StyleProperty<CoreColor>; use TryEvaluate bake path.
                if (paint.Color.TryEvaluate(zoom, adapter, out CoreColor c))
                {
                    // sRGB→linear: use UnityEngine.Color.linear via cast.
                    var unityColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
                    var linear     = unityColor.linear;
                    featureColor = new Vector4(linear.r, linear.g, linear.b, linear.a);
                }

                // S14 data-driven opacity: bake evaluated opacity into vertex alpha.
                // Gate: only when OpacityKind depends on feature (Feature or Composite).
                // For Constant/Zoom opacity, _Opacity uniform is already bound by BindLinePaintToApplier;
                // baking here would double-apply it (shader multiplies vColor.a × _Opacity).
                // S60: OpacityKind → paint.Opacity.DependsOnFeature; DataDrivenOpacity → Opacity.TryEvaluate.
                if (paint.Opacity.DependsOnFeature)
                {
                    if (paint.Opacity.TryEvaluate(zoom, adapter, out float opacityVal))
                    {
                        // featureColor.w starts at 1.0 (from WhiteColor or color bake above).
                        // Multiply by the evaluated opacity so the shader's (vColor.a × _Opacity=1)
                        // produces the correct per-feature alpha.
                        featureColor.w *= opacityVal;
                    }
                }

                // S14 data-driven width: bake evaluated width into WidthScale (multiplier on _Width).
                // Convention: when WidthKind depends on feature, _Width is set to 1.0 by
                // BindLinePaintToApplier (base width = 1 px), so WidthScale = the full evaluated
                // width in pixels. For Constant/Zoom width, WidthScale stays at v.WidthScale (tessellator
                // default 1) and _Width uniform carries the width.
                // WidthScale is per-vertex at loop time; store the per-feature scale and apply below.
                float featureWidthScale = 1f;
                // S60: WidthKind → paint.Width.DependsOnFeature; DataDrivenWidth → Width.TryEvaluate.
                if (paint.Width.DependsOnFeature)
                {
                    if (paint.Width.TryEvaluate(zoom, adapter, out float widthVal))
                    {
                        // widthVal is the evaluated width in pixels. Since _Width=1.0, WidthScale
                        // = v.WidthScale (tessellator miter factor) × widthVal gives final width.
                        featureWidthScale = math.max(0f, widthVal);
                    }
                }

                // Decode line rings from the MVT geometry.
                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
                if (rings == null || rings.Count == 0) continue;

                foreach (var ring in rings)
                {
                    if (ring == null || ring.Count < 2) continue;

                    // Project tile-space coordinates to world-space meters.
                    var worldPts = ProjectLineRing(ring, id.Z, id.X, id.Y, extent,
                        tileOriginMerc.x, tileOriginMerc.y);
                    if (worldPts == null || worldPts.Count < 2) continue;

                    // Tessellate.
                    LineTessellator.Result result = LineTessellator.Triangulate(
                        worldPts, joinType, capType);
                    if (result.Vertices == null || result.Vertices.Length == 0) continue;
                    if (result.Indices  == null || result.Indices.Length  == 0) continue;

                    int offset = tempVerts0.Count;

                    foreach (var v in result.Vertices)
                    {
                        // Position: (east=+X, height=+Y=0, north=+Z).
                        tempVerts0.Add(new LinePositionNormal
                        {
                            Position = new Vector3((float)v.Position.x, 0f, (float)v.Position.y),
                            Normal   = UpNormal,
                        });
                        // Across as a 3D tangent-plane vector (magnitude = miter factor). Y=0 = flat
                        // Mercator frame; a globe projection bakes non-zero Y and the shader consumes it as-is.
                        tempVerts1.Add(new Vector3((float)v.Normal.x, 0f, (float)v.Normal.y));
                        tempVerts2.Add(new Vector2(v.Side, (float)v.DistanceAlong));
                        tempVerts3.Add(new LineWidthColor
                        {
                            // S14 data-driven width: multiply tessellator's miter factor by the
                            // feature-evaluated width. For non-data-driven width, featureWidthScale=1
                            // so v.WidthScale passes through unchanged (miter/cap factors preserved).
                            WidthScale = v.WidthScale * featureWidthScale,
                            Color      = featureColor,
                        });
                    }

                    foreach (int idx in result.Indices)
                        tempIndices.Add(offset + idx);
                }
            }

            if (tempVerts0.Count == 0 || tempIndices.Count == 0)
                return empty;

            // Phase 2: allocate NativeArray streams and copy managed data.
            int vCount = tempVerts0.Count;
            int iCount = tempIndices.Count;

            var payload = new LayerMeshData
            {
                Stream0PositionNormal = new NativeArray<LinePositionNormal>(vCount, Allocator.Persistent),
                Stream1ExtrudeN       = new NativeArray<Vector3>(vCount, Allocator.Persistent),
                Stream2SideAndDist    = new NativeArray<Vector2>(vCount, Allocator.Persistent),
                Stream3WidthColor     = new NativeArray<LineWidthColor>(vCount, Allocator.Persistent),
                Indices               = new NativeArray<int>(iCount, Allocator.Persistent),
                VertexCount           = vCount,
                IndexCount            = iCount,
                IsCreated             = true,
            };

            Interlocked.Increment(ref LayerMeshData.LiveAllocCount);

            for (int i = 0; i < vCount; i++)
            {
                payload.Stream0PositionNormal[i] = tempVerts0[i];
                payload.Stream1ExtrudeN[i]       = tempVerts1[i];
                payload.Stream2SideAndDist[i]    = tempVerts2[i];
                payload.Stream3WidthColor[i]     = tempVerts3[i];
            }

            for (int i = 0; i < iCount; i++)
                payload.Indices[i] = tempIndices[i];

            return payload;
        }

        /// <summary>
        /// Main-thread half: upload a <see cref="LayerMeshData"/> payload to a new <see cref="Mesh"/>
        /// using the advanced NativeArray API. Returns null when <see cref="LayerMeshData.IsCreated"/>
        /// is false (no geometry).
        ///
        /// Does NOT dispose the payload — caller must dispose after this call.
        /// </summary>
        public static Mesh UploadMesh(LayerMeshData data)
        {
            if (!data.IsCreated || data.VertexCount == 0) return null;

            var mesh = new Mesh
            {
                name        = "LineMesh_S14",
                indexFormat = IndexFormat.UInt32,
            };

            mesh.SetVertexBufferParams(data.VertexCount, LineVertexDescriptors);
            mesh.SetVertexBufferData(data.Stream0PositionNormal, 0, 0, data.VertexCount, stream: 0);
            mesh.SetVertexBufferData(data.Stream1ExtrudeN, 0, 0, data.VertexCount, stream: 1);
            mesh.SetVertexBufferData(data.Stream2SideAndDist, 0, 0, data.VertexCount, stream: 2);
            mesh.SetVertexBufferData(data.Stream3WidthColor, 0, 0, data.VertexCount, stream: 3);

            // Skip main-thread index validation (trusted Burst tessellation indices); see
            // StyledFillTileBuilder.UploadMesh. DontRecalculateBounds just avoids a redundant intermediate
            // compute — canonical bounds come from RecalculateBounds() below.
            const MeshUpdateFlags NoValidate =
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;
            mesh.SetIndexBufferParams(data.IndexCount, IndexFormat.UInt32);
            mesh.SetIndexBufferData(data.Indices, 0, 0, data.IndexCount, NoValidate);

            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, data.IndexCount, MeshTopology.Triangles), NoValidate);
            // Accurate bounds: render tests frustum-cull on mesh.bounds (see StyledFillTileBuilder). Cost is
            // irrelevant vs the real hot path (Tile.AddLayer). TODO(perf): tight geometry AABB at generation
            // time — filed follow-up.
            mesh.RecalculateBounds();

            return mesh;
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Project a line ring from tile-space double2 coordinates to world-space double2 (meters,
        /// relative to the scene origin). Uses the same full Web Mercator projection as
        /// StyledFillTileBuilder.ProjectVerticesManaged — identical precision contract.
        /// </summary>
        private static List<double2> ProjectLineRing(
            List<double2> ring,    int    z, int tileX, int tileY, double extent,
            double        originX, double originY)
        {
            if (ring == null || ring.Count < 2) return null;

            const double TwoPi = 2.0 * math.PI_DBL;

            double pow2z  = math.pow(2.0, z);
            var    result = new List<double2>(ring.Count);

            foreach (var pt in ring)
            {
                double px = pt.x;
                double py = pt.y;

                // Tile-space → normalised [0,1] map coordinates.
                double u = (tileX + px / extent) / pow2z;
                double v = (tileY + py / extent) / pow2z;

                // Normalised → lon/lat (radians then degrees for WebMercator.Forward).
                double longitudeRad = u * TwoPi - math.PI_DBL;
                double arg          = math.PI_DBL * (1.0 - 2.0 * v);
                double sinhArg      = (math.exp(arg)     - math.exp(-arg)) * 0.5;
                double latitudeRad  = math.atan(sinhArg);

                double latitudeDeg  = latitudeRad  * (180.0 / math.PI_DBL);
                double longitudeDeg = longitudeRad * (180.0 / math.PI_DBL);

                // Delegate to the shared math module (single source of Mercator literal, T2).
                double3 world = WebMercator.Forward(new GeoCoordinate3D
                    { Longitude = longitudeDeg, Latitude = latitudeDeg, Altitude = 0.0 });

                // Subtract scene origin in double (RTC precision), output double2 for LineTessellator.
                result.Add(new double2(world.x - originX, world.z - originY));
            }

            return result;
        }

        // S60: ParseJoinType/ParseCapType deleted — Join/Cap are now typed enums on LayoutProperties.
    }
}