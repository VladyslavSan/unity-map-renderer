// Unity EditMode only. GC.GetAllocatedBytesForCurrentThread() reads a dead ZERO in this Mono runner (see
// FillMeshBuildScratchPoolTests) — it cannot discriminate a multi-megabyte allocation here. GC.GetTotalMemory
// IS a live meter at this scale (this stage's baseline was ~2 MB/build, far above the ~100 KB floor below
// which GetTotalMemory's own noise dominates) — the calibration canary below proves it is alive in THIS run
// before the pipeline tooth trusts it. NOT registered in core-tests.csproj (FillMeshPipeline/NativeArray live
// in Jobs, which core-tests does not compile).
//
// perf/gc-elimination: FillMeshPipeline.Schedule's Stage 3 used to allocate 13 `new NativeArray<T>[polyCount]`
// handle-arrays — one managed array PER STREAM, each holding one NativeArray handle per polygon (~43,000 tiny
// allocations, ~2 MB, on the 239-feature/~3,325-ring sample-tile fixture). Stage 3 now allocates a FIXED,
// small set of flat NativeArrays (sized once by a prefix-sum offset table) regardless of polygon count, with
// each polygon's slice taken as a GetSubArray view. This tooth pins both the absolute ceiling and that the
// remaining cost no longer scales with ring count.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class FillMeshPipelineAllocationTests
    {
        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // A 31x floor below the pre-flatten baseline (~2,023,424 B/build on the full fixture).
        private const long Ceiling = 65_536;

        // GC.GetTotalMemory's own noise floor (brief: only trustworthy at >= ~100 KB/op) — the calibration
        // canary must clear this by a wide margin to prove the meter is alive.
        private const long CalibrationFloor = 100_000;

        /// <summary>One materialized fixture (a prefix of the "countries" layer's polygon features) plus the
        /// derived <see cref="FillMeshPipeline.LayerInput"/> ready to <c>Schedule</c> repeatedly.</summary>
        private readonly struct Fixture
        {
            public readonly TileGeometryBuffers Geometry;
            public readonly NativeArray<int> VisitOrder;
            public readonly FillMeshPipeline.LayerInput Input;
            public readonly int RingCount;

            public Fixture(TileGeometryBuffers geometry, NativeArray<int> visitOrder,
                FillMeshPipeline.LayerInput input, int ringCount)
            {
                Geometry = geometry; VisitOrder = visitOrder; Input = input; RingCount = ringCount;
            }

            public void Dispose()
            {
                VisitOrder.Dispose();
                Geometry.Dispose();
            }
        }

        /// <summary>Materializes the first <paramref name="featureLimit"/> polygon features of the real
        /// "countries" layer (sample-tile.bytes) — a real, non-synthetic corpus, so the tooth measures the
        /// actual per-polygon shapes (rings, holes) FillMeshPipeline.Schedule sees in production.</summary>
        private static Fixture BuildFixture(int featureLimit)
        {
            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);
            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer);

            var kinds    = new List<TileGeometryType>();
            var commands = new List<uint[]>();
            for (int fi = 0; fi < layer.Kinds.Count && kinds.Count < featureLimit; fi++)
                if (layer.Kinds[fi] == TileGeometryType.Polygon && layer.Commands[fi] != null)
                { kinds.Add(layer.Kinds[fi]); commands.Add(layer.Commands[fi]); }

            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            TileGeometryBuffers geometry = MvtGeometryMaterializerTestFactory.Materialize(tile, layer.Extent, kinds, commands);
            NativeArray<int> visitOrder  = TestTileMeshBuilder.FullVisitOrder(geometry);
            var (bMin, _) = tile.MercatorBounds();

            var input = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
            };

            return new Fixture(geometry, visitOrder, input, geometry.RingCount);
        }

        /// <summary>Bytes/build over a warmed loop, guarded against a Gen0 collection firing inside the
        /// measurement window (which would deflate — or invert — the delta).</summary>
        private static long BytesPerBuild(in FillMeshPipeline.LayerInput input, int iterations)
        {
            // Warm-up: JIT compilation and any one-shot first-touch allocation must not land in the window.
            for (int w = 0; w < 3; w++)
            {
                TileMeshBuffers warm = FillMeshPipeline.Schedule(input);
                warm.Dispose();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int collectionsBefore = GC.CollectionCount(0);
            long before = GC.GetTotalMemory(false);

            for (int i = 0; i < iterations; i++)
            {
                TileMeshBuffers b = FillMeshPipeline.Schedule(input);
                b.Dispose();
            }

            long after = GC.GetTotalMemory(false);
            int collectionsAfter = GC.CollectionCount(0);

            Assert.AreEqual(collectionsBefore, collectionsAfter,
                "a Gen0 collection fired inside the measurement window — the byte delta is unreliable here; " +
                "this indicates a flaky run, not a pipeline result.");

            return (after - before) / iterations;
        }

        /// <summary>Proves GC.GetTotalMemory is a LIVE meter in this run before the pipeline tooth below
        /// trusts it — GC.GetAllocatedBytesForCurrentThread is dead in this same Mono runner (see
        /// FillMeshBuildScratchPoolTests), and a silently-dead meter would make every assertion below
        /// vacuous.</summary>
        [Test]
        public void Calibration_GetTotalMemory_ReadsALiveAllocation()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(false);
            byte[] block = new byte[8 << 20];
            block[0] = 1; // defeat dead-store elimination
            long after = GC.GetTotalMemory(false);

            Assert.Greater(after - before, CalibrationFloor,
                "GC.GetTotalMemory must read a live 8 MiB allocation well clear of its own noise floor, or " +
                "the meter is dead in this run and the tooth below cannot be trusted.");
            GC.KeepAlive(block);
        }

        /// <summary>
        /// The flatten's headline tooth: Stage 3's per-polygon handle-arrays measured 2,023,424 B/build on
        /// this exact fixture before the flatten (~43,000 allocations). RED-verify by restoring the retired
        /// `new NativeArray&lt;T&gt;[polyCount]` shape and confirming this blows the ceiling.
        /// </summary>
        [Test]
        public void Schedule_FullFixture_AllocatesUnder65536BytesPerBuild()
        {
            Fixture fx = BuildFixture(featureLimit: int.MaxValue);
            try
            {
                Assert.Greater(fx.RingCount, 1000,
                    "precondition: the full fixture must be the large real-data corpus (hundreds of features, " +
                    "thousands of rings) — a small fixture couldn't have exercised the pre-flatten cost either.");

                long bytesPerBuild = BytesPerBuild(fx.Input, iterations: 10);

                Assert.LessOrEqual(bytesPerBuild, Ceiling,
                    $"FillMeshPipeline.Schedule allocated {bytesPerBuild} B/build over the full fixture " +
                    $"({fx.RingCount} rings) — must stay under the {Ceiling} B ceiling (31x below the " +
                    "pre-flatten ~2,023,424 B/build baseline on this same fixture).");
            }
            finally { fx.Dispose(); }
        }

        /// <summary>
        /// The pre-flatten cost scaled ~linearly with ring count (one managed NativeArray handle allocated
        /// per polygon, per stream). Flattened, Stage 3 allocates a FIXED set of buffers sized once — so
        /// bytes/build must stay near its floor across meaningfully different ring counts, not grow with them.
        /// RED-verify the same way as the ceiling tooth: restoring the per-polygon arrays reintroduces the
        /// scaling and blows this spread by roughly two orders of magnitude.
        /// </summary>
        [Test]
        public void Schedule_AllocationDoesNotScaleWithRingCount()
        {
            Fixture small  = BuildFixture(featureLimit: 20);
            Fixture medium = BuildFixture(featureLimit: 80);
            Fixture large  = BuildFixture(featureLimit: int.MaxValue);
            try
            {
                Assert.Less(small.RingCount, medium.RingCount,
                    "precondition: the three fixtures must actually differ in ring count.");
                Assert.Less(medium.RingCount, large.RingCount,
                    "precondition: the three fixtures must actually differ in ring count.");

                long smallBpb  = BytesPerBuild(small.Input,  iterations: 10);
                long mediumBpb = BytesPerBuild(medium.Input, iterations: 10);
                long largeBpb  = BytesPerBuild(large.Input,  iterations: 10);

                Assert.LessOrEqual(smallBpb,  Ceiling);
                Assert.LessOrEqual(mediumBpb, Ceiling);
                Assert.LessOrEqual(largeBpb,  Ceiling);

                // Bound the small→large spread by an absolute margin close to the observed near-zero spread
                // (the ceiling checks above already force spread <= Ceiling = 65,536, which never discriminates
                // — a per-ring managed alloc that stayed under the ceiling would still pass that bound). This
                // threshold instead targets a small per-ring cost directly: ~16 B/ring reintroduced over this
                // fixture's small→large ring-count delta (thousands of rings) moves bytes/build by tens of KB,
                // far past this cap, while the flattened fixed-buffer cost does not scale with ring count at all.
                long spread = Math.Abs(largeBpb - smallBpb);
                Assert.Less(spread, 4_096,
                    $"bytes/build spread across ring counts {small.RingCount}/{medium.RingCount}/{large.RingCount} " +
                    $"was {spread} B (small={smallBpb}, medium={mediumBpb}, large={largeBpb} B/build) — " +
                    "allocation is still scaling with ring count; the flatten did not eliminate the per-polygon cost.");
            }
            finally { small.Dispose(); medium.Dispose(); large.Dispose(); }
        }
    }
}
