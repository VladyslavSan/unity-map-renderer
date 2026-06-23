using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S40 managed per-layer fill mesh builder. Extracted from the S02-era MapFillBootstrap.BuildMesh
    /// (retired in S54) and generalized to receive real <see cref="TileId"/> + origin, a set of
    /// pre-selected features, and a <see cref="FillPaint"/> describing the style.
    ///
    /// Pipeline per feature:
    ///   MvtGeometry.Decode → PolygonAssembler.Assemble → Earcut.Triangulate
    ///   → ProjectVerticesManaged (pure C#, off-main-thread safe) → stream assembly → NativeArray upload.
    ///
    /// S47 async split:
    ///   <see cref="BuildMeshData"/> runs the full decode/assemble/earcut/project loop off the main
    ///   thread, returning a <see cref="LayerMeshData"/> payload (IDisposable, holds NativeArrays).
    ///   <see cref="UploadMesh"/> uploads a <see cref="LayerMeshData"/> to a <see cref="Mesh"/> on the
    ///   main thread using the advanced NativeArray API. <see cref="BuildMesh"/> is the sync convenience:
    ///   BuildMeshData → UploadMesh → Dispose.
    ///
    /// S48 advanced Mesh API:
    ///   <see cref="LayerMeshData"/> now holds <see cref="NativeArray{T}"/> streams (Allocator.Persistent).
    ///   The sRGB→linear color conversion runs off the main thread inside <see cref="BuildMeshData"/>,
    ///   preserving the S13 D2 gamma fix. <see cref="UploadMesh"/> is upload-only, read-only, and does
    ///   NOT call SetVertices/SetColors/SetTriangles. The caller is responsible for disposing after upload.
    ///
    /// Stream layout (4 streams, matching Unity's max-4-stream cap):
    ///   Stream 0 — Position (Float32x3) + Normal (Float32x3) interleaved via <see cref="FillPositionNormal"/>.
    ///   Stream 1 — TexCoord0 UV (Float32x2).
    ///   Stream 2 — Tangent (Float32x4).
    ///   Stream 3 — Color (Float32x4, linearized sRGB).
    ///   Index buffer — UInt32.
    ///
    /// Color (D2): per-feature sRGB color baked via <see cref="DataDrivenPaintEvaluator"/>; converted
    /// to linear via <c>Color.linear</c> off the main thread (S48). <c>_BaseColor=white</c> on the Material
    /// (identity multiply). Never set <c>_BaseColor</c> to the style color — that would double-apply gamma.
    ///
    /// Thread-safety: <see cref="BuildMeshData"/> touches only pure-managed, stateless Core code
    /// (MvtGeometry.Decode, PolygonAssembler.Assemble, Earcut.Triangulate, DataDrivenPaintEvaluator,
    /// MvtFeatureAdapter). All are allocation-local with no shared mutable static state. Safe to run
    /// concurrently on multiple ThreadPool threads (one per tile).
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class StyledFillTileBuilder
    {
        // MapRenderer.Tile.Tessellate — wraps the decode/assemble/earcut/project loop per BuildMesh call.
        // S46 acceptance: proved-wired target for the profiler recorder test (tooth 1b).
        // S47/S51: this marker now fires on a background ThreadPool thread when called from BuildMeshData
        // inside UniTask.Run. The ProfilerMarkerTests [UnityTest] (tooth 1b) uses
        // ProfilerRecorderOptions.Default (not CollectOnlyOnCurrentThread) so cross-thread samples are captured.
        private static readonly ProfilerMarker PmTessellate = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.Tessellate");

        // Hoisted fill vertex attribute descriptor array — constructed once (static readonly) so
        // UploadMesh does not allocate per-tile on the main thread (S48 no-per-tile-GC contract).
        // Layout: 4 streams (Unity max). Stream 0 = Position+Normal interleaved. Stream 1 = UV.
        // Stream 2 = Tangent. Stream 3 = Color. See class XML doc.
        // Canonical ascending VertexAttribute enum order (Position=0, Normal=1, Tangent=2, Color=3,
        // TexCoord0=4) eliminates the Unity "non-standard order" warning. Each attribute is on its
        // own stream so the reorder does not change any stream's byte offset.
        private static readonly VertexAttributeDescriptor[] FillVertexDescriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent,   VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1),
        };

        // Constant tangent: +X direction, +1 bitangent sign (right-handed), valid for flat +Y-normal fill.
        private static readonly Vector4 FlatTangent = new Vector4(1f, 0f, 0f, 1f);

        // ── Intermediate data payload (holds NativeArrays — must be Disposed) ───────────────────

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
        /// Per-feature vertex payload produced by <see cref="BuildMeshData"/> (LEGACY — kept for
        /// compatibility with existing tests that inspect the managed intermediate. Tests that call
        /// BuildMeshData directly should use <see cref="LayerMeshData.IsCreated"/> and
        /// <see cref="LayerMeshData.VertexCount"/> instead of <c>data.Features</c>).
        /// No UnityEngine types — safe to allocate and pass across threads.
        /// </summary>
        public struct FeatureMeshData
        {
            /// <summary>Origin-relative world-space positions (east=+X, height=+Y, north=+Z).</summary>
            public float3[] Verts;
            /// <summary>Earcut triangle indices (into Verts).</summary>
            public int[] Indices;
            /// <summary>Pre-projection tile-space double2 coordinates (for UV generation).</summary>
            public double2[] TileVerts;
            /// <summary>Tile extent in tile units (from MVT layer, typically 4096).</summary>
            public double Extent;
            /// <summary>Per-feature sRGB vertex color (linearized in LayerMeshData stream assembly).</summary>
            public Color FeatureColor;
        }

        /// <summary>
        /// S48: CPU-computed mesh data for one style layer, held as NativeArray streams.
        /// Produced by <see cref="BuildMeshData"/> off the main thread; uploaded by
        /// <see cref="UploadMesh"/> on the main thread via the advanced NativeArray Mesh API.
        ///
        /// OWNERSHIP: the caller of <see cref="BuildMeshData"/> owns this payload and MUST call
        /// <see cref="Dispose"/> on EVERY exit path (consume OR discard). <see cref="UploadMesh"/>
        /// copies data into the Mesh (via SetVertexBufferData) and does NOT dispose — dispose after
        /// upload. <see cref="BuildMesh"/> is the sync convenience and disposes automatically.
        ///
        /// When no geometry was produced, <see cref="IsCreated"/> is false and Dispose() is a no-op.
        ///
        /// Alloc counting (S48 leak guard): <see cref="DebugLiveAllocCount"/> is a static counter
        /// incremented on allocation and decremented on Dispose. Tests assert the net is zero after
        /// a full load+release cycle. Interlocked for thread-safety (BuildMeshData runs concurrently).
        ///
        /// Also carries <see cref="Features"/> (managed list) for backwards-compatible test assertions
        /// that inspect the intermediate data; do NOT use Features in the live upload path.
        /// </summary>
        public struct LayerMeshData : IDisposable
        {
            // ── Stream 0: Position + Normal interleaved ──────────────────────────────────────────
            public NativeArray<FillPositionNormal> Stream0PositionNormal;
            // ── Stream 1: TexCoord0 (UV) ─────────────────────────────────────────────────────────
            public NativeArray<Vector2> Stream1Uv;
            // ── Stream 2: Tangent ────────────────────────────────────────────────────────────────
            public NativeArray<Vector4> Stream2Tangent;
            // ── Stream 3: Color (linearized sRGB, Float32x4) ─────────────────────────────────────
            public NativeArray<Vector4> Stream3Color;
            // ── Index buffer ──────────────────────────────────────────────────────────────────────
            public NativeArray<int> Indices;

            /// <summary>Vertex count. Zero when no geometry was produced.</summary>
            public int VertexCount;
            /// <summary>Index count. Zero when no geometry was produced.</summary>
            public int IndexCount;

            /// <summary>
            /// True when the NativeArray streams are allocated and valid.
            /// False for default/empty payloads (no geometry produced).
            /// </summary>
            public bool IsCreated;

            /// <summary>
            /// LEGACY compatibility: per-feature managed intermediate data. Still populated by
            /// <see cref="BuildMeshData"/> so existing tests can inspect it. Empty when <see cref="IsCreated"/>
            /// is false. Do NOT use in the live NativeArray upload path.
            /// </summary>
            public List<FeatureMeshData> Features;

            /// <summary>
            /// S48 leak-guard counter: net live NativeArray allocations.
            /// Incremented (Interlocked) when streams are allocated in <see cref="BuildMeshData"/>;
            /// decremented in <see cref="Dispose"/>. Tests assert this is zero after a full cycle.
            /// Positive value means produced-but-not-Disposed payloads exist (leak detected).
            /// </summary>
            internal static long LiveAllocCount;

            /// <summary>
            /// Test accessor for <see cref="LiveAllocCount"/>.
            /// Non-zero means there are live (unDisposed) NativeArray-backed LayerMeshData payloads.
            /// </summary>
            public static long DebugLiveAllocCount => Interlocked.Read(ref LiveAllocCount);

            /// <summary>
            /// Disposes all NativeArray streams. Idempotent (guarded by <see cref="IsCreated"/>).
            /// Call on EVERY exit path after upload or on discard. Must be called on any thread;
            /// NativeArray.Dispose() is thread-safe for Persistent allocator arrays.
            /// </summary>
            public void Dispose()
            {
                if (!IsCreated) return;
                IsCreated = false;
                if (Stream0PositionNormal.IsCreated) Stream0PositionNormal.Dispose();
                if (Stream1Uv.IsCreated)             Stream1Uv.Dispose();
                if (Stream2Tangent.IsCreated)        Stream2Tangent.Dispose();
                if (Stream3Color.IsCreated)          Stream3Color.Dispose();
                if (Indices.IsCreated)               Indices.Dispose();
                Interlocked.Decrement(ref LiveAllocCount);
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// CPU-only half: decode/assemble/earcut/project all features for one style layer, producing
        /// a <see cref="LayerMeshData"/> payload backed by <see cref="NativeArray{T}"/> streams
        /// (Allocator.Persistent). The sRGB→linear color conversion runs here (off the main thread).
        ///
        /// Safe to call from a ThreadPool thread (e.g. inside UniTask.Run):
        /// all code paths use only pure-managed, stateless Core logic until the final NativeArray
        /// allocation step. <c>NativeArray(Allocator.Persistent)</c> is thread-safe off-main.
        ///
        /// When no polygon geometry is produced, returns a default <see cref="LayerMeshData"/> with
        /// <see cref="LayerMeshData.IsCreated"/> == false and no NativeArray allocation (no Dispose needed).
        ///
        /// The <see cref="PmTessellate"/> profiler marker wraps this call. In the async path (S47),
        /// this fires on a background thread — the ProfilerMarkerTests tooth-1b recorder must use
        /// <see cref="ProfilerRecorderOptions.Default"/> (not CollectOnlyOnCurrentThread).
        /// </summary>
        public static LayerMeshData BuildMeshData(
            IReadOnlyList<MvtFeature> selectedFeatures,
            FillPaint paint,
            double zoom,
            double extent,
            TileId id,
            double2 tileOriginMerc)
        {
            // Empty result (IsCreated = false, no NativeArray) for early-out and error cases.
            var empty = new LayerMeshData { Features = new List<FeatureMeshData>() };

            if (selectedFeatures == null || selectedFeatures.Count == 0)
                return empty;

            double originX = tileOriginMerc.x;
            double originY = tileOriginMerc.y;

            // MapRenderer.Tile.Tessellate — wraps the full decode/assemble/earcut/project loop.
            // S46 acceptance: proves-wired marker for profiler recorder test (tooth 1b).
            // S47/S51: fires on a background thread when called inside UniTask.Run from MapView.
            using var sTessellate = PmTessellate.Auto();

            // Phase 1: Run the full decode/assemble/earcut/project loop into temporary managed lists.
            // NativeArray allocation happens AFTER this loop (once counts are known), so a mid-loop
            // exception never leaves half-allocated NativeArrays behind.
            var tempFeatures = new List<FeatureMeshData>(selectedFeatures.Count);

            int totalVerts   = 0;
            int totalIndices = 0;

            foreach (var feature in selectedFeatures)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon)
                    continue;

                // Bake per-feature vertex color from the data-driven paint expression.
                // Color space (D2): Core Color is sRGB [0,1]; converted to linear below (S48).
                // _BaseColor=white on material → identity multiply → no double-gamma.
                Color featureColor = Color.white;
                var adapter = new MvtFeatureAdapter(feature);
                if (paint.DataDrivenColor.TryEvaluateColor(zoom, adapter, out CoreColor c))
                    featureColor = new Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);

                // Decode rings, assemble polygons, earcut, project.
                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
                if (rings == null || rings.Count == 0) continue;

                List<Polygon> polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    Earcut.Result earcutResult = Earcut.Triangulate(polygon.Outer, polygon.Holes);
                    if (earcutResult.Indices == null || earcutResult.Indices.Length == 0) continue;

                    double2[] flatVerts = earcutResult.Vertices;
                    int[] triIndices    = earcutResult.Indices;

                    // Project via pure managed math (off-main-thread safe; no NativeArray).
                    // Formula mirrors ProjectTileVerticesJob.Execute (same precision contract).
                    float3[] worldPos = ProjectVerticesManaged(flatVerts, id.Z, id.X, id.Y, extent,
                        originX, originY);

                    tempFeatures.Add(new FeatureMeshData
                    {
                        Verts        = worldPos,
                        Indices      = triIndices,
                        TileVerts    = flatVerts,
                        Extent       = extent,
                        FeatureColor = featureColor,
                    });

                    totalVerts   += worldPos.Length;
                    totalIndices += triIndices.Length;
                }
            }

            // No polygon geometry produced — return empty (no NativeArray allocation).
            if (tempFeatures.Count == 0 || totalVerts == 0 || totalIndices == 0)
            {
                empty.Features = tempFeatures; // may be empty list but populated for compat
                return empty;
            }

            // Phase 2: Allocate NativeArray streams (now that counts are known) and copy in.
            // Persistent allocator: tiles live for multiple frames; TempJob has a ~4-frame guard.
            // NativeArray(Allocator.Persistent) is thread-safe from any thread (including ThreadPool).
            var result = new LayerMeshData
            {
                VertexCount = totalVerts,
                IndexCount  = totalIndices,
                IsCreated   = true,
                Features    = tempFeatures, // LEGACY: populated for backward-compat test assertions
                Stream0PositionNormal = new NativeArray<FillPositionNormal>(totalVerts, Allocator.Persistent),
                Stream1Uv             = new NativeArray<Vector2>(totalVerts,            Allocator.Persistent),
                Stream2Tangent        = new NativeArray<Vector4>(totalVerts,            Allocator.Persistent),
                Stream3Color          = new NativeArray<Vector4>(totalVerts,            Allocator.Persistent),
                Indices               = new NativeArray<int>(totalIndices,              Allocator.Persistent),
            };

            // Increment the leak-guard counter AFTER all streams are allocated.
            Interlocked.Increment(ref LayerMeshData.LiveAllocCount);

            // Phase 3: Copy managed temp data into NativeArray streams.
            int vBase = 0;
            int iBase = 0;
            foreach (var f in tempFeatures)
            {
                double extentInv = f.Extent > 0.0 ? 1.0 / f.Extent : 0.0;

                // S13 D2 gamma fix (moved off main thread — S48): convert sRGB→linear here.
                // Color.linear applies the IEC 61966-2-1 ramp.
                // white.linear == white: S11 uniform-color behavior is preserved.
                // Thread-safe: Color.linear is pure math (no engine access).
                Color linearColor = f.FeatureColor.linear;
                var   colorVec   = new Vector4(linearColor.r, linearColor.g, linearColor.b, linearColor.a);

                for (int i = 0; i < f.Verts.Length; i++)
                {
                    float3 v = f.Verts[i];
                    result.Stream0PositionNormal[vBase + i] = new FillPositionNormal
                    {
                        Position = new Vector3(v.x, v.y, v.z),
                        Normal   = Vector3.up,  // explicit +Y: flat XZ fill geometry (S34 contract)
                    };

                    // UV0: tile-local [0,1] from pre-projection tile coordinates.
                    // tileVerts[i].x = tile-space east; tileVerts[i].y = tile-space north.
                    float u = (float)(f.TileVerts[i].x * extentInv);
                    float v2 = (float)(f.TileVerts[i].y * extentInv);
                    result.Stream1Uv[vBase + i] = new Vector2(u, v2);

                    // Constant tangent: +X direction, +1 bitangent sign (S34 requirement).
                    result.Stream2Tangent[vBase + i] = FlatTangent;

                    // Color: linearized sRGB (S13 fix, S48: moved off main thread).
                    result.Stream3Color[vBase + i] = colorVec;
                }

                // Copy indices, offsetting by the vertex base within the combined buffer.
                for (int i = 0; i < f.Indices.Length; i++)
                    result.Indices[iBase + i] = vBase + f.Indices[i];

                vBase += f.Verts.Length;
                iBase += f.Indices.Length;
            }

            return result;
        }

        /// <summary>
        /// Main-thread half: uploads a <see cref="LayerMeshData"/> produced by
        /// <see cref="BuildMeshData"/> into a <see cref="Mesh"/> using the advanced NativeArray API.
        ///
        /// S48: uses SetVertexBufferParams + SetVertexBufferData(NativeArray) + SetIndexBufferParams +
        /// SetIndexBufferData + SetSubMesh. No managed SetVertices/SetColors/SetTriangles in this path.
        ///
        /// Must be called on the Unity main thread (Mesh creation / SetVertexBufferParams require it).
        /// Returns null when <paramref name="data"/> contains no polygon geometry.
        ///
        /// OWNERSHIP: UploadMesh does NOT dispose the payload (SetVertexBufferData copies data into the
        /// Mesh GPU buffer; the source NativeArrays are still needed until after this call returns, but
        /// the Mesh is independent). The CALLER must call data.Dispose() after this method returns.
        /// </summary>
        public static Mesh UploadMesh(LayerMeshData data)
        {
            if (!data.IsCreated || data.VertexCount == 0 || data.IndexCount == 0)
                return null;

            var mesh = new Mesh
            {
                name        = "MapFill",
                indexFormat = IndexFormat.UInt32,
            };

            // SetVertexBufferParams: declares the layout (static readonly array — no per-call alloc).
            mesh.SetVertexBufferParams(data.VertexCount, FillVertexDescriptors);

            // Upload each stream from the NativeArray directly (no managed ToArray() copy).
            // SetVertexBufferData<T> copies the data into the Mesh's vertex buffer — the source
            // NativeArray is safe to Dispose after this call returns.
            mesh.SetVertexBufferData(data.Stream0PositionNormal, 0, 0, data.VertexCount, stream: 0);
            mesh.SetVertexBufferData(data.Stream1Uv,             0, 0, data.VertexCount, stream: 1);
            mesh.SetVertexBufferData(data.Stream2Tangent,        0, 0, data.VertexCount, stream: 2);
            mesh.SetVertexBufferData(data.Stream3Color,          0, 0, data.VertexCount, stream: 3);

            // Index buffer. Skip Unity's main-thread index validation (O(indices)): the indices come from
            // the Burst earcut/tessellation job and are covered by tests, so re-validating every index per
            // tile upload is wasted work. DontRecalculateBounds just avoids a redundant intermediate compute
            // here — the canonical bounds are set by RecalculateBounds() below.
            const MeshUpdateFlags NoValidate = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;
            mesh.SetIndexBufferParams(data.IndexCount, IndexFormat.UInt32);
            mesh.SetIndexBufferData(data.Indices, 0, 0, data.IndexCount, NoValidate);

            // One sub-mesh covering all triangles.
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, data.IndexCount, MeshTopology.Triangles), NoValidate);

            // Accurate bounds matter: the snapshot/render tests frustum-cull on the renderer's mesh.bounds,
            // so a loose placeholder breaks fill coverage. RecalculateBounds() is correct and its cost is
            // irrelevant to the real hot path (tile-load is dominated by Tile.AddLayer, not Mesh.Upload).
            // TODO(perf): compute the tight geometry AABB at generation time (accumulate vertex min/max in
            // ProjectVerticesManaged) to skip this scan AND enable tight frustum culling — filed follow-up.
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Synchronous convenience: <see cref="BuildMeshData"/> then <see cref="UploadMesh"/>,
        /// then <see cref="LayerMeshData.Dispose"/>.
        /// Used by tests that call the builder directly and by callers that need sync behavior.
        /// Must be called on the Unity main thread (UploadMesh creates a Mesh).
        /// </summary>
        public static Mesh BuildMesh(
            IReadOnlyList<MvtFeature> selectedFeatures,
            FillPaint paint,
            double zoom,
            double extent,
            TileId id,
            double2 tileOriginMerc)
        {
            LayerMeshData data = BuildMeshData(selectedFeatures, paint, zoom, extent, id, tileOriginMerc);
            try
            {
                return UploadMesh(data);
            }
            finally
            {
                data.Dispose(); // always dispose, even if UploadMesh throws
            }
        }

        // ── Internal: managed projection (mirrors ProjectTileVerticesJob.Execute) ─────────────────

        /// <summary>
        /// Pure C# projection: tile-space double2 → origin-relative float3 world positions.
        /// Formula is bit-identical to <see cref="MapRenderer.Jobs.ProjectTileVerticesJob.Execute"/>
        /// (same double-precision intermediates, same subtract-then-cast pattern).
        /// Safe to call from any thread (no Unity APIs, no NativeArray).
        /// </summary>
        private static float3[] ProjectVerticesManaged(
            double2[] tileCoords,
            int tileZ, int tileX, int tileY,
            double extent,
            double originMercX, double originMercY)
        {
            const double R = 6378137.0; // Earth radius (Web Mercator / EPSG:3857)
            const double TwoPi = 2.0 * Math.PI;

            int n = tileCoords.Length;
            var result = new float3[n];

            double pow2z = Math.Pow(2.0, tileZ);

            for (int i = 0; i < n; i++)
            {
                double px = tileCoords[i].x;
                double py = tileCoords[i].y;

                // Tile → normalised [0,1] map coordinates
                double u = (tileX + px / extent) / pow2z;
                double v = (tileY + py / extent) / pow2z;

                // Normalised → lon/lat (radians)
                double lonRad = u * TwoPi - Math.PI;
                double arg     = Math.PI * (1.0 - 2.0 * v);
                double sinhArg = (Math.Exp(arg) - Math.Exp(-arg)) * 0.5;
                double latRad  = Math.Atan(sinhArg);

                // lon/lat → Web Mercator meters (spherical Mercator, R = semi-major axis)
                double mercX = R * lonRad;
                double halfLat = latRad * 0.5;
                double tanArg  = Math.Tan(Math.PI * 0.25 + halfLat);
                double mercY   = R * Math.Log(tanArg);

                // Subtract origin in double, then cast to float — RTC precision
                double dx = mercX - originMercX;
                double dz = mercY - originMercY;

                result[i] = new float3((float)dx, 0f, (float)dz);
            }

            return result;
        }
    }
}
