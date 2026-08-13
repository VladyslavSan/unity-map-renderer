// Stage 0 (globe-fill-subdivision epic), Edit 4 — Unity-only source-of-truth parity tooth. Proves the
// managed mirror in SubdivisionCoverageValidator (Assets/.../Meshing/SubdivisionCoverageValidator.cs) is
// faithful to the REAL Burst GlobeFillSubdivideJob<TProj>, by ORDERED OUTPUT-STREAM equality — Tile and
// Feature are exact, World/Up/East within a tight numeric tolerance (R*1e-9 / 1e-9), NOT literal bitwise —
// NOT by matching aggregate gap numbers (a FIFO-vs-LIFO pop, a reordered child push, a dropped/corrupted
// East/Feature, or a missing origin subtraction would all still pass an aggregate-only check on the
// shallow water-6-32-20 corpus, which is depth-1/no-budget). See
// docs/mesh-triangulation-robustness-design.md §6.2 and the Stage 0 plan's Invariant section.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Meshing
{
    public class GlobeSubdivisionJobParityTests
    {
        private const double Extent = 4096;
        private static readonly double R = EarthConstants.A;

        private static byte[] LoadFixture(string name)
        {
            string dir = Directory.GetCurrentDirectory();
            for (int i = 0; i < 16 && dir != null; i++)
            {
                string p = Path.Combine(dir, "Assets", "Fixtures", name);
                if (File.Exists(p)) return File.ReadAllBytes(p);
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new FileNotFoundException(name);
        }

        // -----------------------------------------------------------------------------------------------
        // Shared ordered-parity assertion: dispatches the REAL Burst job AND the managed mirror over the
        // SAME raw (tileVerts, triangleIndices, vertexFeatureIdx) triple + dispatch constants, then asserts
        // their emitted streams are identical, position by position (LIFO traversal ⇒ the two streams are
        // in lockstep). Returns the mirror's lineage (roots/leaves/splitEvents) + the real job's raw output
        // so a caller can additionally run the SAME gap analysis over both (the corpus corollary).
        // -----------------------------------------------------------------------------------------------

        private readonly struct ParityRun
        {
            public readonly List<SubdivisionCoverageValidator.RootTri> Roots;
            public readonly List<SubdivisionCoverageValidator.LeafRef> MirrorLeaves;
            public readonly int MaxDepthReached;
            public readonly bool BudgetFired;
            public readonly double3[] RealWorld;
            public readonly double2[] RealTile;

            public ParityRun(
                List<SubdivisionCoverageValidator.RootTri> roots, List<SubdivisionCoverageValidator.LeafRef> mirrorLeaves,
                int maxDepthReached, bool budgetFired,
                double3[] realWorld, double2[] realTile)
            {
                Roots = roots; MirrorLeaves = mirrorLeaves;
                MaxDepthReached = maxDepthReached; BudgetFired = budgetFired;
                RealWorld = realWorld; RealTile = realTile;
            }
        }

        private static ParityRun AssertOrderedParity(
            IProjection proj, TileId id, double extent, double3 origin,
            double2[] tileVerts, int[] triangleIndices, int[] vertexFeatureIdx, int srcVertCount, int srcIndexCount,
            double maxEdgeAngleRad, int maxDepth, int maxOutputVertices)
        {
            var nativeVerts = new NativeArray<double2>(tileVerts, Allocator.Persistent);
            var nativeTris = new NativeArray<int>(triangleIndices, Allocator.Persistent);
            var nativeFeat = new NativeArray<int>(vertexFeatureIdx, Allocator.Persistent);
            var outVerts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            var outIndices = new NativeList<int>(64, Allocator.Persistent);
            try
            {
                // Explicit constants — no implicit defaults (Run has none, but be explicit at every call site).
                GlobeFillSubdivideDispatch.Run(
                    proj, nativeVerts, nativeTris, nativeFeat, srcVertCount, srcIndexCount, id, extent, origin,
                    maxEdgeAngleRad, maxDepth, maxOutputVertices, outVerts, outIndices);

                var roots = SubdivisionCoverageValidator.BuildRootsFromRaw(
                    tileVerts, triangleIndices, vertexFeatureIdx, srcVertCount, srcIndexCount);
                SubdivisionCoverageValidator.RunManagedMirror(
                    roots, id, proj, extent, origin, maxEdgeAngleRad, maxDepth, maxOutputVertices,
                    out var mirrorLeaves, out int maxDepthReached, out bool budgetFired);

                Assert.AreEqual(mirrorLeaves.Count, outVerts.Length, "vertex count must match (same LIFO traversal)");
                Assert.AreEqual(outVerts.Length, outIndices.Length, "no dedup ⇒ one sequential index per vertex");
                for (int i = 0; i < outIndices.Length; i++)
                    Assert.AreEqual(i, outIndices[i], $"OutIndices[{i}]: sequential, no dedup");

                var realWorld = new double3[outVerts.Length];
                var realTile = new double2[outVerts.Length];
                for (int i = 0; i < mirrorLeaves.Count; i++)
                {
                    SubdivisionCoverageValidator.LeafRef m = mirrorLeaves[i];
                    GlobeFillVertex real = outVerts[i];
                    realWorld[i] = real.World;
                    realTile[i] = real.Tile;

                    Assert.AreEqual(m.Tile.x, real.Tile.x, 0.0, $"vertex {i}: Tile.x exact (same Mid() arithmetic)");
                    Assert.AreEqual(m.Tile.y, real.Tile.y, 0.0, $"vertex {i}: Tile.y exact (same Mid() arithmetic)");
                    Assert.AreEqual(m.Feature, real.Feature, $"vertex {i}: Feature exact (first-index pick)");

                    Assert.AreEqual(m.World.x, real.World.x, R * 1e-9, $"vertex {i}: World.x");
                    Assert.AreEqual(m.World.y, real.World.y, R * 1e-9, $"vertex {i}: World.y");
                    Assert.AreEqual(m.World.z, real.World.z, R * 1e-9, $"vertex {i}: World.z");

                    Assert.AreEqual(m.Up.x, real.Up.x, 1e-9, $"vertex {i}: Up.x");
                    Assert.AreEqual(m.Up.y, real.Up.y, 1e-9, $"vertex {i}: Up.y");
                    Assert.AreEqual(m.Up.z, real.Up.z, 1e-9, $"vertex {i}: Up.z");

                    Assert.AreEqual(m.East.x, real.East.x, 1e-9, $"vertex {i}: East.x");
                    Assert.AreEqual(m.East.y, real.East.y, 1e-9, $"vertex {i}: East.y");
                    Assert.AreEqual(m.East.z, real.East.z, 1e-9, $"vertex {i}: East.z");
                }

                return new ParityRun(roots, mirrorLeaves, maxDepthReached, budgetFired, realWorld, realTile);
            }
            finally
            {
                nativeVerts.Dispose(); nativeTris.Dispose(); nativeFeat.Dispose();
                outVerts.Dispose(); outIndices.Dispose();
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Corpus — the confirmed artefact tile. Ordered-stream parity PLUS the end-to-end corollary: feed
        // the real job's own (World, Tile) through the SAME gap analysis (paired with the mirror's lineage,
        // reconstructed by ordered-output pairing — the real GlobeFillVertex carries no parent lineage) and
        // assert its MaxGapMeters matches the mirror's own Report.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Corpus_Water_6_32_20_Globe_OrderedParityAndGapCorollary()
        {
            var id = new TileId { Z = 6, X = 32, Y = 20 };
            var proj = new SphericalProjection();
            // IR C1 P3: command streams read from the bytes (MvtFixtureStreams), not off a decoded feature.
            var layer = MvtFixtureStreams.ReadLayer(LoadFixture("water-6-32-20.pbf.bytes"), "water");
            Assert.IsNotNull(layer);
            double extent = layer.Extent;

            var verts = new List<double2>();
            var indices = new List<int>();
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;
                foreach (var poly in PolygonAssembler.Assemble(MvtGeometry.Decode(layer.Commands[fi])))
                {
                    var res = Earcut.Triangulate(poly.Outer, poly.Holes);
                    int baseIdx = verts.Count;
                    verts.AddRange(res.Vertices);
                    for (int i = 0; i < res.Indices.Length; i++) indices.Add(baseIdx + res.Indices[i]);
                }
            }
            double2[] tileVerts = verts.ToArray();
            int[] triangleIndices = indices.ToArray();
            int[] vertexFeatureIdx = new int[tileVerts.Length]; // single feature 0 — irrelevant to gap analysis

            ParityRun run = AssertOrderedParity(
                proj, id, extent, new double3(0, 0, 0), tileVerts, triangleIndices, vertexFeatureIdx,
                tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxOutputVertices);

            // Hybrid stream: real job's World/Tile, mirror's lineage (reconstructed by ordered pairing).
            var hybridLeaves = new List<SubdivisionCoverageValidator.LeafRef>(run.MirrorLeaves.Count);
            for (int i = 0; i < run.MirrorLeaves.Count; i++)
            {
                SubdivisionCoverageValidator.LeafRef m = run.MirrorLeaves[i];
                hybridLeaves.Add(new SubdivisionCoverageValidator.LeafRef(
                    run.RealWorld[i], m.Up, m.East, run.RealTile[i], m.Feature, m.RootIndex, m.Depth));
            }

            var hybridReport = SubdivisionCoverageValidator.AnalyzeLeafStream(
                run.Roots, hybridLeaves, run.MaxDepthReached, run.BudgetFired, id, proj, extent);
            var mirrorReport = SubdivisionCoverageValidator.AnalyzeLeafStream(
                run.Roots, run.MirrorLeaves, run.MaxDepthReached, run.BudgetFired, id, proj, extent);

            Assert.AreEqual(mirrorReport.MaxGapMeters, hybridReport.MaxGapMeters, mirrorReport.MaxGapMeters * 1e-6 + 1e-6,
                $"real job's own gap analysis must match the mirror's: mirror={mirrorReport.Summary} hybrid={hybridReport.Summary}");
        }

        // -----------------------------------------------------------------------------------------------
        // Discriminating crafted cases — each fires a path the shallow (depth-1, no-budget) water corpus
        // under-exercises, and each is proven by the SAME ordered-stream equality.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Discriminating_MaxDepthStop_WholeGlobeTriangleAtDefaultDepth()
        {
            var tileVerts = new[] { new double2(0, 0), new double2(Extent, 0), new double2(0, Extent) };
            var triangleIndices = new[] { 0, 1, 2 };
            var vertexFeatureIdx = new[] { 0, 0, 0 };

            AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 0, X = 0, Y = 0 }, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, 3, 3,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxOutputVertices);
        }

        [Test]
        public void Discriminating_BudgetFiring_TinyBudgetMidTraversal()
        {
            var tileVerts = new[] { new double2(0, 0), new double2(Extent, 0), new double2(0, Extent) };
            var triangleIndices = new[] { 0, 1, 2 };
            var vertexFeatureIdx = new[] { 0, 0, 0 };

            ParityRun run = AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 0, X = 0, Y = 0 }, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, 3, 3,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, 8, 2000); // depth 8 unbounded would explode; budget 2000 must fire

            Assert.IsTrue(run.BudgetFired, "the tiny budget must actually fire on this crafted case (mirror side)");
        }

        [Test]
        public void Discriminating_NonZeroOrigin_WorldIsOriginRelativeOnBothSides()
        {
            var tileVerts = new[] { new double2(0, 0), new double2(Extent, 0), new double2(0, Extent) };
            var triangleIndices = new[] { 0, 1, 2 };
            var vertexFeatureIdx = new[] { 0, 0, 0 };
            var origin = new double3(R * 0.3, R * 0.1, -R * 0.2); // an arbitrary non-zero render-space origin

            AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 3, X = 3, Y = 3 }, Extent, origin,
                tileVerts, triangleIndices, vertexFeatureIdx, 3, 3,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxOutputVertices);
        }

        [Test]
        public void Discriminating_MultipleFeatureIds_PropagatePerFirstIndexOnBothSides()
        {
            // Two disjoint triangles with DIFFERENT feature ids on their first index — the job/mirror both
            // pick feature[triangleIndices[3*t]] per triangle (GlobeFillSubdivider.cs:63-64).
            var tileVerts = new[]
            {
                new double2(0, 0), new double2(Extent * 0.4, 0), new double2(0, Extent * 0.4),           // tri 0
                new double2(Extent * 0.6, Extent * 0.6), new double2(Extent, Extent * 0.6), new double2(Extent * 0.6, Extent), // tri 1
            };
            var triangleIndices = new[] { 0, 1, 2, 3, 4, 5 };
            var vertexFeatureIdx = new[] { 7, 7, 7, 42, 42, 42 };

            AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 4, X = 5, Y = 5 }, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxOutputVertices);
        }
    }
}
