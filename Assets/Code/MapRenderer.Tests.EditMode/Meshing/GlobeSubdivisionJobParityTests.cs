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
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

using MapRenderer.Jobs.Fill;
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
            // Vertex sharing: OutIndices.Length is the EMITTED count
            // (every Emit call adds exactly one), OutVerts.Length is the UNIQUE count (a hit reuses an
            // index). Captured here so a caller can assert sharing actually happened without re-scheduling.
            public readonly int EmittedCount;
            public readonly int UniqueVertexCount;

            public ParityRun(
                List<SubdivisionCoverageValidator.RootTri> roots, List<SubdivisionCoverageValidator.LeafRef> mirrorLeaves,
                int maxDepthReached, bool budgetFired,
                double3[] realWorld, double2[] realTile, int emittedCount, int uniqueVertexCount)
            {
                Roots = roots; MirrorLeaves = mirrorLeaves;
                MaxDepthReached = maxDepthReached; BudgetFired = budgetFired;
                RealWorld = realWorld; RealTile = realTile;
                EmittedCount = emittedCount; UniqueVertexCount = uniqueVertexCount;
            }
        }

        private static ParityRun AssertOrderedParity(
            IProjection proj, TileId id, double extent, double3 origin,
            double2[] tileVerts, int[] triangleIndices, int[] vertexFeatureIdx, int srcVertCount, int srcIndexCount,
            double maxEdgeAngleRad, int maxDepth, int maxOutputVertices)
        {
            var nativeVerts = new NativeList<double2>(tileVerts.Length, Allocator.Persistent);
            nativeVerts.CopyFrom(tileVerts);
            var nativeTris = new NativeList<int>(triangleIndices.Length, Allocator.Persistent);
            nativeTris.CopyFrom(triangleIndices);
            var nativeFeat = new NativeList<int>(vertexFeatureIdx.Length, Allocator.Persistent);
            nativeFeat.CopyFrom(vertexFeatureIdx);
            var nativeBand = new NativeList<float3>(tileVerts.Length, Allocator.Persistent);
            nativeBand.Resize(tileVerts.Length, NativeArrayOptions.ClearMemory);
            var outVerts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            var outIndices = new NativeList<int>(64, Allocator.Persistent);
            try
            {
                // Explicit constants — no implicit defaults (Schedule has none, but be explicit at every call
                // site). job-scheduling-design.md §8 stage 4 Group B: the synchronous Run entry point is
                // retired — srcVertCount/srcIndexCount are unused by the scheduled form (it reads the lists'
                // own lengths) but stay as parameters, still read by the mirror/roots build below.
                JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                    proj, nativeVerts, nativeTris, nativeFeat, nativeBand, id, extent, origin,
                    maxEdgeAngleRad, maxDepth, maxOutputVertices,
                    GlobeFillSubdivideDispatch.DefaultMaxTotalVertices, outVerts, outIndices, default);
                JobHandle.ScheduleBatchedJobs();
                handle.Complete();

                var roots = SubdivisionCoverageValidator.BuildRootsFromRaw(
                    tileVerts, triangleIndices, vertexFeatureIdx, srcVertCount, srcIndexCount);
                SubdivisionCoverageValidator.RunManagedMirror(
                    roots, id, proj, extent, origin, maxEdgeAngleRad, maxDepth, maxOutputVertices,
                    out var mirrorLeaves, out int maxDepthReached, out bool budgetFired);

                // T-C1 (de-indexed stream identity): the job now shares vertices
                // storage — OutVerts.Length (unique) can be LESS than OutIndices.Length (emitted) — but the
                // EMITTED count must still match the mirror's leaf stream 1:1 (same LIFO traversal), and every
                // index must resolve to a real, in-range vertex. De-indexing (outVerts[outIndices[i]]) below
                // reconstructs the emitted stream regardless of how much storage sharing happened.
                Assert.AreEqual(mirrorLeaves.Count, outIndices.Length,
                    "emitted-vertex count must match (same LIFO traversal) — sharing changes STORAGE, not emission");
                Assert.LessOrEqual(outVerts.Length, outIndices.Length,
                    "sharing can only reduce or preserve unique storage, never exceed the emitted count");
                var referenced = new bool[outVerts.Length];
                for (int i = 0; i < outIndices.Length; i++)
                {
                    int idx = outIndices[i];
                    Assert.GreaterOrEqual(idx, 0, $"OutIndices[{i}]: must reference a valid vertex");
                    Assert.Less(idx, outVerts.Length, $"OutIndices[{i}]: must reference a valid vertex");
                    referenced[idx] = true;
                }
                // T-C3 (no orphan slots): every unique vertex slot allocated must be referenced by at least
                // one emitted index — a bug that allocates a new slot without recording it in the map (or
                // recording the wrong index) would leave a slot no triangle ever points at.
                for (int k = 0; k < referenced.Length; k++)
                    Assert.IsTrue(referenced[k], $"OutVerts[{k}]: unreferenced — every unique slot must be used");

                var realWorld = new double3[outIndices.Length];
                var realTile = new double2[outIndices.Length];
                for (int i = 0; i < mirrorLeaves.Count; i++)
                {
                    SubdivisionCoverageValidator.LeafRef m = mirrorLeaves[i];
                    GlobeFillVertex real = outVerts[outIndices[i]]; // de-indexed: the vertex THIS emitted position resolves to
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

                return new ParityRun(
                    roots, mirrorLeaves, maxDepthReached, budgetFired, realWorld, realTile,
                    emittedCount: outIndices.Length, uniqueVertexCount: outVerts.Length);
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
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);

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
        // T-C4 (vertex sharing, earcut-sdf-vertex-cost.md §6): the z0 "countries" fixture
        // is the plan's own vertex-sharing measurement corpus. The plan's headline 346,542 → 75,733 is the BANDED
        // scenario — it assumes the per-vertex band/side column that lives only on the parked
        // feat/fill-boundary-antialiasing branch (off `main`, no Band field). THIS branch realises the
        // plan's NO-BAND row instead: 161,676 emitted → 44,915 unique. The banded 75,733 figure only becomes
        // reachable if/when the band branch rebases onto this change and extends GlobeFillVertexKey.
        // Same ordered-parity + gap-corollary treatment as the water tile above, so the
        // SubdivisionCoverageValidator report (gap/coverage/quality) is proven unchanged on the SAME real-job
        // run T-C2 measures the sharing ratio from.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Corpus_Countries_Z0_Globe_OrderedParityAndGapCorollaryAndVertexSharingRatio()
        {
            var id = new TileId { Z = 0, X = 0, Y = 0 };
            var proj = new SphericalProjection();
            var layer = MvtFixtureStreams.ReadLayer(LoadFixture("sample-tile.bytes"), "countries");
            Assert.IsNotNull(layer);
            double extent = layer.Extent;

            var verts = new List<double2>();
            var featureIdx = new List<int>();
            var indices = new List<int>();
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;
                foreach (var poly in PolygonAssembler.Assemble(MvtGeometry.Decode(layer.Commands[fi])))
                {
                    var res = Earcut.Triangulate(poly.Outer, poly.Holes);
                    int baseIdx = verts.Count;
                    verts.AddRange(res.Vertices);
                    // T-C3 (vertex sharing): REAL per-source-feature indices, not a
                    // single constant 0 — a fixture where every vertex reads feature 0 makes "drop Feature
                    // from the key" a no-op (nothing to wrongly merge), which would make T-C3's RED recipe
                    // ("hash on Tile only ⇒ two features share an index") untestable here.
                    for (int k = 0; k < res.Vertices.Length; k++) featureIdx.Add(fi);
                    for (int i = 0; i < res.Indices.Length; i++) indices.Add(baseIdx + res.Indices[i]);
                }
            }
            double2[] tileVerts = verts.ToArray();
            int[] triangleIndices = indices.ToArray();
            int[] vertexFeatureIdx = featureIdx.ToArray();

            ParityRun run = AssertOrderedParity(
                proj, id, extent, new double3(0, 0, 0), tileVerts, triangleIndices, vertexFeatureIdx,
                tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);

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

            // T-C2 ("it actually shares"): a fence with headroom bracketing the measured value (earcut-sdf-
            // vertex-cost.md §4: mirror-measured 44,915 unique of 161,676 emitted, no band, on this fixture).
            // RED-verified: reverting Emit to sequential indices (git stash the production edit) makes
            // UniqueVertexCount == EmittedCount == 161,676, well outside this fence.
            Assert.Less(run.UniqueVertexCount, 55_000,
                $"sharing must collapse the countries z0 tile's unique vertex count well below its emitted " +
                $"count: emitted={run.EmittedCount} unique={run.UniqueVertexCount}");
            Assert.Greater(run.UniqueVertexCount, 35_000,
                $"the fence's lower bound guards a vertex key that over-merges (e.g. dropping Feature): " +
                $"emitted={run.EmittedCount} unique={run.UniqueVertexCount}");
            Assert.Less(run.UniqueVertexCount, run.EmittedCount,
                "a genuinely curved z0 tile must have SOME shared conforming split-edge midpoints");
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
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);
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
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);
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
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);
        }

        // -----------------------------------------------------------------------------------------------
        // T-C5 (vertex sharing): conforming split-edge midpoints must actually merge.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Discriminating_ConformingMidpointsMerge_Z2Quad_UniqueCountMatchesMirrorsDistinctTileFeatureCount()
        {
            // Same whole-tile square + tile as GlobeSubdivisionTests.Synthetic_Deep_Z2_Quad_..._IsConforming
            // (z2/0/0 — top of the globe, strong curvature, reaches depth 5): earcut into 2 triangles along
            // the diagonal, so a correct vertex key must merge every conforming split-edge midpoint the two
            // root triangles compute — Mid() is exactly order-symmetric ((a+b)*0.5 commutes bit-for-bit), so
            // every genuine shared-edge midpoint is bit-identical on both sides.
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(Extent, 0), new double2(Extent, Extent), new double2(0, Extent),
            };
            var res = Earcut.Triangulate(outer, new List<List<double2>>());
            double2[] tileVerts = res.Vertices;
            int[] triangleIndices = res.Indices;
            int[] vertexFeatureIdx = new int[tileVerts.Length];

            var id = new TileId { Z = 2, X = 0, Y = 0 };
            ParityRun run = AssertOrderedParity(
                new SphericalProjection(), id, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);

            // The mirror's own leaf stream is an INDEPENDENT oracle for "how many distinct (Tile, Feature)
            // bit-patterns should exist" — counted directly, with no dependency on the job's own hash map or
            // GlobeFillVertexKey (which keys the WHOLE emitted struct, not just Tile/Feature — see that
            // struct's doc). Tile/Feature alone is still the right oracle here: World/Up/East are pure
            // functions of Tile (Project()), so two vertices with equal (Tile,Feature) are bit-identical on
            // every field the whole-struct key also reads — the two counts must coincide. A correct sharing
            // must therefore produce exactly this many unique vertices: fewer would mean two DIFFERENT
            // tuples wrongly collided, more would mean a genuine conforming duplicate was missed. This is now
            // CONFIRMING a derived property (Mid() is exactly order-symmetric, marking is per-edge with no
            // connectivity, so a shared split edge's derived fields are bit-identical on both sides) rather
            // than discovering one — if it goes red, that reasoning is what's wrong, not this test.
            var distinct = new HashSet<(ulong, ulong, int)>();
            foreach (SubdivisionCoverageValidator.LeafRef leaf in run.MirrorLeaves)
                distinct.Add((math.asulong(leaf.Tile.x), math.asulong(leaf.Tile.y), leaf.Feature));

            Assert.AreEqual(distinct.Count, run.UniqueVertexCount,
                $"unique emitted vertices ({run.UniqueVertexCount}) must equal the distinct (Tile,Feature) " +
                $"bit-patterns the mirror's own leaf stream carries ({distinct.Count}) — every conforming " +
                "split-edge midpoint must actually merge");
        }
    }
}
