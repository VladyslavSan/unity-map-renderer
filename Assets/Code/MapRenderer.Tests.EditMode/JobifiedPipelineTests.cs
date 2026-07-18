// Unity EditMode only — uses NativeArray, Burst jobs, UnityEngine.Application, MeshBuilder.
// NOT included in Tools/core-tests/core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
namespace MapRenderer.Tests
{
    /// <summary>
    /// Parity and integration tests for the jobified decode + mesh pipeline (S04).
    ///
    /// Structure:
    ///   (1) Decode job vs managed MvtGeometry.Decode — ring count + per-ring vertex content hash equal.
    ///   (2) Ring assembly job vs managed PolygonAssembler — polygon/hole count match.
    ///   (3) End-to-end jobified vs managed S02 path — vertex+index CONTENT HASH equal (strict,
    ///       no tolerance — tile-space integer coords are exact; projection uses same job on same input).
    ///       Subsumes the S03 count-only DataSourceRenderPathTests follow-up.
    ///   (4) Multi-tile throughput — N tiles scheduled and completed; total verts == N × single-tile.
    /// </summary>
    [TestFixture]
    public class JobifiedPipelineTests
    {
        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // ── (1) Decode job vs managed MvtGeometry.Decode ─────────────────────────────────────

        [Test]
        public void DecodeJob_RingCountAndVertexHash_MatchManagedReference()
        {
            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);

            var mvtTile = MvtDecoder.Decode(mvtBytes);
            var layer   = mvtTile.GetLayer("countries");
            Assert.IsNotNull(layer);

            var polyGeoms = new List<uint[]>();
            foreach (var f in layer.Features)
                if (f.GeometryType == TileGeometryType.Polygon && f.Geometry != null)
                    polyGeoms.Add(f.Geometry);

            Assert.Greater(polyGeoms.Count, 0, "Expected polygon features");

            // ── Managed reference.
            var managedRings = new List<List<double2>>();
            foreach (var geom in polyGeoms)
            {
                var rings = MvtGeometry.Decode(geom);
                if (rings != null) managedRings.AddRange(rings);
            }

            // ── Job path.
            int featureCount  = polyGeoms.Count;
            int totalCommands = 0;
            for (int fi = 0; fi < featureCount; fi++)
                totalCommands += polyGeoms[fi].Length;

            int maxRings    = totalCommands / 3 + featureCount + 2;
            int maxVertices = totalCommands + 4;

            var commands       = new NativeArray<uint>(totalCommands,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featOffsets    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featLengths    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outVerts       = new NativeArray<double2>(maxVertices,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingOffsets = new NativeArray<int>(maxRings + 1,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingFeat    = new NativeArray<int>(maxRings,          Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var outVertCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);

            try
            {
                int cmdPos = 0;
                for (int fi = 0; fi < featureCount; fi++)
                {
                    featOffsets[fi] = cmdPos;
                    featLengths[fi] = polyGeoms[fi].Length;
                    for (int k = 0; k < polyGeoms[fi].Length; k++)
                        commands[cmdPos + k] = polyGeoms[fi][k];
                    cmdPos += polyGeoms[fi].Length;
                }

                new MvtDecodeJob
                {
                    Commands            = commands,
                    FeatureOffsets      = featOffsets,
                    FeatureLengths      = featLengths,
                    OutVertices         = outVerts,
                    OutRingOffsets      = outRingOffsets,
                    OutRingFeatureIndex = outRingFeat,
                    OutRingCount        = outRingCount,
                    OutVertexCount      = outVertCount,
                }.Schedule().Complete();

                int jobRingCount = outRingCount[0];

                Assert.AreEqual(managedRings.Count, jobRingCount,
                    $"Decode job ring count {jobRingCount} != managed {managedRings.Count}");

                string managedHash = HashRings(managedRings);
                string jobHash     = HashRingsNative(outVerts, outRingOffsets, jobRingCount);

                Assert.AreEqual(managedHash, jobHash,
                    "Decode job ring vertex content hash must match managed MvtGeometry.Decode. " +
                    "A mismatch means the Burst decode path has different zigzag, cursor, or ring-boundary logic.");
            }
            finally
            {
                commands.Dispose(); featOffsets.Dispose(); featLengths.Dispose();
                outVerts.Dispose(); outRingOffsets.Dispose(); outRingFeat.Dispose();
                outRingCount.Dispose(); outVertCount.Dispose();
            }
        }

        // ── (2) Ring assembly job vs managed PolygonAssembler ─────────────────────────────────

        [Test]
        public void RingAssemblyJob_PolygonAndHoleCounts_MatchManagedReference()
        {
            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);

            var mvtTile = MvtDecoder.Decode(mvtBytes);
            var layer   = mvtTile.GetLayer("countries");
            Assert.IsNotNull(layer);

            int managedPolyCount = 0;
            int managedHoleCount = 0;
            var polyGeoms        = new List<uint[]>();
            foreach (var f in layer.Features)
            {
                if (f.GeometryType != TileGeometryType.Polygon || f.Geometry == null) continue;
                polyGeoms.Add(f.Geometry);
                var rings = MvtGeometry.Decode(f.Geometry);
                var polys = PolygonAssembler.Assemble(rings);
                managedPolyCount += polys.Count;
                foreach (var p in polys) managedHoleCount += (p.Holes?.Count ?? 0);
            }

            int featureCount  = polyGeoms.Count;
            int totalCommands = 0;
            foreach (var g in polyGeoms) totalCommands += g.Length;
            int maxRings    = totalCommands / 3 + featureCount + 2;
            int maxVertices = totalCommands + 4;
            int maxPolygons = maxRings;
            int maxHoles    = maxRings;

            var commands       = new NativeArray<uint>(totalCommands,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featOffsets    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featLengths    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outVerts       = new NativeArray<double2>(maxVertices,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingOffsets = new NativeArray<int>(maxRings + 1,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingFeat    = new NativeArray<int>(maxRings,          Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var outVertCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyOuterIdx   = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleStart  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleCount  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var holeRingIdxs   = new NativeArray<int>(maxHoles,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outPolyCount   = new NativeArray<int>(1,            Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var outHoleCount2  = new NativeArray<int>(1,            Allocator.Persistent, NativeArrayOptions.ClearMemory);

            try
            {
                int cmdPos = 0;
                for (int fi = 0; fi < featureCount; fi++)
                {
                    featOffsets[fi] = cmdPos;
                    featLengths[fi] = polyGeoms[fi].Length;
                    for (int k = 0; k < polyGeoms[fi].Length; k++)
                        commands[cmdPos + k] = polyGeoms[fi][k];
                    cmdPos += polyGeoms[fi].Length;
                }

                new MvtDecodeJob
                {
                    Commands = commands, FeatureOffsets = featOffsets, FeatureLengths = featLengths,
                    OutVertices = outVerts, OutRingOffsets = outRingOffsets,
                    OutRingFeatureIndex = outRingFeat,
                    OutRingCount = outRingCount, OutVertexCount = outVertCount,
                }.Schedule().Complete();

                int ringCount = outRingCount[0];

                new RingAssemblyJob
                {
                    Vertices = outVerts, RingOffsets = outRingOffsets, RingFeatureIdx = outRingFeat,
                    RingCount = ringCount,
                    OutPolyOuterRingIdx = polyOuterIdx, OutPolyHoleListStart = polyHoleStart,
                    OutPolyHoleCount = polyHoleCount, OutHoleRingIdxs = holeRingIdxs,
                    OutPolygonCount = outPolyCount, OutHoleCount = outHoleCount2,
                }.Schedule().Complete();

                int jobPolyCount = outPolyCount[0];
                int jobHoleCount = outHoleCount2[0];

                Assert.AreEqual(managedPolyCount, jobPolyCount,
                    $"Ring assembly job polygon count {jobPolyCount} != managed {managedPolyCount}.");
                Assert.AreEqual(managedHoleCount, jobHoleCount,
                    $"Ring assembly job hole count {jobHoleCount} != managed {managedHoleCount}.");
            }
            finally
            {
                commands.Dispose(); featOffsets.Dispose(); featLengths.Dispose();
                outVerts.Dispose(); outRingOffsets.Dispose(); outRingFeat.Dispose();
                outRingCount.Dispose(); outVertCount.Dispose();
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); outPolyCount.Dispose(); outHoleCount2.Dispose();
            }
        }

        // ── (3) End-to-end jobified vs managed — content hash (strict) ────────────────────────

        [Test]
        public void JobifiedPipeline_VertexAndIndexContentHash_MatchManagedPath()
        {
            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);

            var mvtTile = MvtDecoder.Decode(mvtBytes);
            var layer   = mvtTile.GetLayer("countries");
            Assert.IsNotNull(layer);

            double extent  = layer.Extent;
            var tileId     = new TileId { Z = 0, X = 0, Y = 0 };
            var (bMin, _)  = tileId.MercatorBounds();
            double originX = bMin.x, originY = bMin.y;

            // ── Managed reference path.
            var (managedVertHash, managedIdxHash, managedForceClips) =
                BuildManagedHash(mvtBytes, "countries", 0, 0, 0, extent, originX, originY);

            // ── Jobified path.
            var polyGeoms = new List<uint[]>();
            foreach (var f in layer.Features)
                if (f.GeometryType == TileGeometryType.Polygon && f.Geometry != null)
                    polyGeoms.Add(f.Geometry);

            var pipelineInput = new FillMeshPipeline.LayerInput
            {
                FeatureGeometries = polyGeoms,
                Extent    = extent,
                Tile      = new TileId { Z = 0, X = 0, Y = 0 },
                OriginRender = new double3(originX, 0.0, originY), // == TileRenderOrigin.Project bit-for-bit for Mercator
            };

            TileMeshBuffers buffers = FillMeshPipeline.Schedule(pipelineInput);
            try
            {
                int vertCount  = buffers.VertexCount[0];
                int indexCount = buffers.TotalIndexCount;

                string jobVertHash = HashDouble2Array(buffers.TileVertices, vertCount);
                string jobIdxHash  = HashIntArray(buffers.TriangleIndices, indexCount);

                Assert.AreEqual(managedVertHash, jobVertHash,
                    $"Jobified vertex content hash does not match managed reference (vertCount: job={vertCount}). " +
                    "This means the Burst earcut produces different merged ring vertex order.");

                Assert.AreEqual(managedIdxHash, jobIdxHash,
                    $"Jobified index content hash does not match managed reference (indexCount: job={indexCount}). " +
                    "This means the Burst earcut produces different triangle indices.");

                // Force-clip count must match the managed pinned invariant.
                Assert.AreEqual(managedForceClips, buffers.TotalForceClipCount,
                    $"Force-clip count: job={buffers.TotalForceClipCount}, managed={managedForceClips}. " +
                    "Stall-guard behaviour must be identical between paths.");
            }
            finally
            {
                buffers.Dispose();
            }
        }

        // ── (4) Multi-tile throughput ──────────────────────────────────────────────────────────

        [Test]
        public void MultiTile_NTiles_AllProduceSameHashAndCorrectTotalVerts()
        {
            const int N = 4;

            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);

            var mvtTile = MvtDecoder.Decode(mvtBytes);
            var layer   = mvtTile.GetLayer("countries");
            Assert.IsNotNull(layer);

            double extent = layer.Extent;
            var polyGeoms = new List<uint[]>();
            foreach (var f in layer.Features)
                if (f.GeometryType == TileGeometryType.Polygon && f.Geometry != null)
                    polyGeoms.Add(f.Geometry);

            var (bMin, _)  = new TileId { Z = 0, X = 0, Y = 0 }.MercatorBounds();
            var singleInput = new FillMeshPipeline.LayerInput
            {
                FeatureGeometries = polyGeoms, Extent = extent,
                Tile = new TileId { Z = 0, X = 0, Y = 0 },
                OriginRender = new double3(bMin.x, 0.0, bMin.y),
            };

            // Get single-tile reference.
            int    singleVertCount  = 0;
            int    singleIndexCount = 0;
            string singleVertHash   = null;
            string singleIdxHash    = null;
            TileMeshBuffers singleBuffers = FillMeshPipeline.Schedule(singleInput);
            try
            {
                singleVertCount  = singleBuffers.VertexCount[0];
                singleIndexCount = singleBuffers.TotalIndexCount;
                singleVertHash   = HashDouble2Array(singleBuffers.TileVertices, singleVertCount);
                singleIdxHash    = HashIntArray(singleBuffers.TriangleIndices, singleIndexCount);
            }
            finally { singleBuffers.Dispose(); }

            // Schedule N tiles.
            var allBuffers = new TileMeshBuffers[N];
            for (int i = 0; i < N; i++)
                allBuffers[i] = FillMeshPipeline.Schedule(singleInput);

            int totalVerts = 0;
            try
            {
                for (int i = 0; i < N; i++)
                {
                    int verts   = allBuffers[i].VertexCount[0];
                    int indices = allBuffers[i].TotalIndexCount;
                    string vh   = HashDouble2Array(allBuffers[i].TileVertices, verts);
                    string ih   = HashIntArray(allBuffers[i].TriangleIndices, indices);
                    totalVerts += verts;

                    Assert.AreEqual(singleVertHash, vh,
                        $"Multi-tile tile[{i}] vertex hash differs from single-tile reference.");
                    Assert.AreEqual(singleIdxHash, ih,
                        $"Multi-tile tile[{i}] index hash differs from single-tile reference.");
                }

                Assert.AreEqual(singleVertCount * N, totalVerts,
                    $"Total multi-tile vertex count {totalVerts} != {singleVertCount} * {N}.");
            }
            finally
            {
                for (int i = 0; i < N; i++) allBuffers[i].Dispose();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the managed S02 pipeline and returns SHA-256 hashes of the tile-space earcut
        /// vertex array and earcut indices (strict integer hash — no tolerance).
        /// Also returns total force-clip count for the pinned invariant.
        /// </summary>
        private static (string vertHash, string idxHash, int forceClips) BuildManagedHash(
            byte[] mvtBytes, string layerName,
            int tileZ, int tileX, int tileY,
            double extent, double originX, double originY)
        {
            var tile  = MvtDecoder.Decode(mvtBytes);
            var layer = tile.GetLayer(layerName);
            Assert.IsNotNull(layer, $"Layer '{layerName}' must be present");

            var vertBytes  = new List<byte>();
            var idxBytes   = new List<byte>();
            int forceClips = 0;
            int globalVertBase = 0;

            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != TileGeometryType.Polygon) continue;
                var rings    = MvtGeometry.Decode(feature.Geometry);
                var polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    var result = Earcut.Triangulate(polygon.Outer, polygon.Holes);
                    if (result.Indices == null || result.Indices.Length == 0) continue;
                    forceClips += result.ForceClips;

                    // Hash the managed Earcut.Result.Vertices (the merged ring including bridge copies).
                    foreach (var v in result.Vertices)
                    {
                        vertBytes.AddRange(BitConverter.GetBytes(v.x));
                        vertBytes.AddRange(BitConverter.GetBytes(v.y));
                    }
                    // Hash indices re-offset by global vertex base (matches jobified aggregation).
                    foreach (int idx in result.Indices)
                        idxBytes.AddRange(BitConverter.GetBytes(idx + globalVertBase));

                    globalVertBase += result.Vertices.Length;
                }
            }

            using var sha256 = SHA256.Create();
            string vHash = Convert.ToBase64String(sha256.ComputeHash(vertBytes.ToArray()));
            sha256.Initialize();
            string iHash = Convert.ToBase64String(sha256.ComputeHash(idxBytes.ToArray()));
            return (vHash, iHash, forceClips);
        }

        private static string HashRings(List<List<double2>> rings)
        {
            var bytes = new List<byte>();
            foreach (var ring in rings)
                foreach (var v in ring)
                {
                    bytes.AddRange(BitConverter.GetBytes(v.x));
                    bytes.AddRange(BitConverter.GetBytes(v.y));
                }
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static string HashRingsNative(NativeArray<double2> verts, NativeArray<int> ringOffsets, int ringCount)
        {
            var bytes = new List<byte>();
            for (int ri = 0; ri < ringCount; ri++)
            {
                int start = ringOffsets[ri];
                int end   = ringOffsets[ri + 1];
                for (int i = start; i < end; i++)
                {
                    bytes.AddRange(BitConverter.GetBytes(verts[i].x));
                    bytes.AddRange(BitConverter.GetBytes(verts[i].y));
                }
            }
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static string HashDouble2Array(NativeArray<double2> arr, int count)
        {
            var bytes = new List<byte>(count * 16);
            for (int i = 0; i < count; i++)
            {
                bytes.AddRange(BitConverter.GetBytes(arr[i].x));
                bytes.AddRange(BitConverter.GetBytes(arr[i].y));
            }
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static string HashIntArray(NativeArray<int> arr, int count)
        {
            var bytes = new List<byte>(count * 4);
            for (int i = 0; i < count; i++)
                bytes.AddRange(BitConverter.GetBytes(arr[i]));
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }
}
