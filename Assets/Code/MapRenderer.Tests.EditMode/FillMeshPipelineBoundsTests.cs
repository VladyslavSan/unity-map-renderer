// Unity EditMode only — references MapRenderer.Jobs.FillMeshPipeline.
// NOT included in Tools/core-tests (MapRenderer.Jobs depends on Unity.Collections).

using System;
using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Jobs;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S06 Batch A, item (a): exact sizing pre-count + its never-fired capacity backstop.
    ///
    /// The pipeline now sizes its NativeArray decode buffers from
    /// <see cref="FillMeshPipeline.PrecountRingsAndVertices"/> — an exact walk of the command stream
    /// mirroring <c>MvtDecodeJob.Execute</c>. This makes under-allocation (and therefore the in-job
    /// out-of-range write) IMPOSSIBLE for any input, including a malformed multi-point MoveTo. The OOB write is
    /// PREVENTED, not merely reported post-hoc, in every build (the fix does not depend on
    /// <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>).
    ///
    /// <see cref="FillMeshPipeline.EnsureCapacity"/> remains as a defense-in-depth backstop: a plain
    /// <c>if (count &gt; capacity) throw</c> that, with exact sizing, never fires. Its boundary behaviour is
    /// still verified below so a future sizing-vs-decode desync would surface loudly.
    /// </summary>
    [TestFixture]
    public class FillMeshPipelineBoundsTests
    {
        [Test]
        public void EnsureCapacity_CountWithinCapacity_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(0, 0, "empty"));
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(5, 10, "ring"));
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(10, 10, "exact-fit"));
        }

        [Test]
        public void EnsureCapacity_CountExceedsCapacity_ThrowsLoudly()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => FillMeshPipeline.EnsureCapacity(11, 10, "ring"));
            StringAssert.Contains("ring", ex.Message, "message must name the overflowing quantity");
            StringAssert.Contains("11", ex.Message, "message must report the actual count");
            StringAssert.Contains("10", ex.Message, "message must report the capacity");
        }

        [Test]
        public void EnsureCapacity_OffByOne_Throws()
        {
            // The boundary: capacity N admits exactly N, rejects N+1.
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(100, 100, "vertex"));
            Assert.Throws<InvalidOperationException>(
                () => FillMeshPipeline.EnsureCapacity(101, 100, "vertex"),
                "count == capacity + 1 must throw (the first out-of-range write).");
        }

        [Test]
        public void EnsureCapacity_LargeOverflow_Throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => FillMeshPipeline.EnsureCapacity(int.MaxValue, 4096, "polygon"));
        }

        // ── Exact pre-count: the real fix for S06 item (a). ──────────────────────────────────────

        /// <summary>
        /// The malformed-stream counterexample: a single Polygon feature whose geometry is ONE multi-point
        /// MoveTo with count=11. The decode job emits 11 single-vertex rings and 11 vertices from this one
        /// header. The OLD heuristic sized maxRings = 23/3 + 1 + 2 = 10 (under-allocating by one), which let
        /// MvtDecodeJob write OutRingOffsets[10]/OutRingFeatureIndex[10] out of range. The exact pre-count
        /// must report exactly 11 rings and 11 vertices so the buffers are sized to hold them.
        /// </summary>
        [Test]
        public void PrecountRingsAndVertices_MultiPointMoveTo_Count11_ExactCounts()
        {
            var features = new List<uint[]> { MultiPointMoveTo(11) };

            FillMeshPipeline.PrecountRingsAndVertices(features, out int rings, out int vertices);

            Assert.AreEqual(11, rings,    "MoveTo count=11 starts 11 rings");
            Assert.AreEqual(11, vertices, "MoveTo count=11 emits 11 vertices");

            // Sanity: the old heuristic under-allocated rings for exactly this input.
            int totalCommands = features[0].Length;                 // 1 header + 22 params = 23
            int oldMaxRings   = totalCommands / 3 + features.Count + 2; // 23/3 + 1 + 2 = 10
            Assert.Less(oldMaxRings, rings,
                "regression guard: the replaced heuristic under-allocated for this stream");
        }

        /// <summary>
        /// Spec-compliant geometry: one MoveTo count=1 (ring start) followed by a LineTo count=3
        /// (three more vertices) = 1 ring, 4 vertices. Confirms the walk matches the normal path too.
        /// </summary>
        [Test]
        public void PrecountRingsAndVertices_SpecCompliantRing_ExactCounts()
        {
            // [MoveTo count=1, dx, dy, LineTo count=3, dx,dy, dx,dy, dx,dy]
            var geom = new uint[]
            {
                (1u << 3) | 1u, 0u, 0u,            // MoveTo count=1
                (3u << 3) | 2u, 0u, 0u, 0u, 0u, 0u, 0u, // LineTo count=3
            };
            var features = new List<uint[]> { geom };

            FillMeshPipeline.PrecountRingsAndVertices(features, out int rings, out int vertices);

            Assert.AreEqual(1, rings);
            Assert.AreEqual(4, vertices);
        }

        /// <summary>
        /// End-to-end: the count=11 malformed Polygon feature must flow through
        /// <see cref="FillMeshPipeline.Schedule"/> WITHOUT overflow/corruption. Against the old
        /// heuristic sizing this throws inside MvtDecodeJob (the 11th ring write lands out of the length-10
        /// OutRingFeatureIndex array; the EditMode collections-checks turn the silent release-build corruption
        /// into a loud throw). With exact sizing it completes cleanly. The 11 single-vertex rings are all
        /// degenerate (rLen &lt; 3), so the assembler produces 0 polygons and Schedule returns default — the
        /// point of the test is that it reaches that result without an out-of-range write.
        /// </summary>
        [Test]
        public void Schedule_MultiPointMoveTo_Count11_CompletesWithoutOverflow()
        {
            var input = new FillMeshPipeline.LayerInput
            {
                FeatureGeometries = new List<uint[]> { MultiPointMoveTo(11) },
                Extent       = 4096,
                Tile         = new TileId { Z = 0, X = 0, Y = 0 },
                OriginRender = default, // all rings degenerate ⇒ no geometry ⇒ origin irrelevant here
            };

            TileMeshBuffers buffers = default;
            Assert.DoesNotThrow(() => buffers = FillMeshPipeline.Schedule(input),
                "exact pre-count sizing must prevent the in-job out-of-range write for a multi-point MoveTo");

            // All rings degenerate → no polygons → default buffers (IsCreated == false). Dispose is a no-op
            // on default, but call it to mirror real caller cleanup.
            if (buffers.IsCreated)
                buffers.Dispose();
        }

        /// <summary>MVT geometry: a single MoveTo command with the given point count, plus its 2*count
        /// (zero-delta) parameter uints. count=N starts N rings inside MvtDecodeJob.</summary>
        private static uint[] MultiPointMoveTo(int count)
        {
            var geom = new uint[1 + 2 * count];
            geom[0] = ((uint)count << 3) | 1u; // command = MoveTo(1), count = N
            // remaining entries left 0 → zigzag-decodes to delta (0,0) per point
            return geom;
        }
    }
}
