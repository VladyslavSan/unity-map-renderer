// Unity EditMode only — NativeArray/NativeList + a Burst job. NOT registered in Tools/core-tests.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// Authors MVT geometry command streams (spec §4.3: <c>command = id &amp; 0x7</c>,
    /// <c>count = id &gt;&gt; 3</c>; MoveTo=1, LineTo=2, ClosePath=7; parameters are zigzag deltas against a
    /// running cursor) so a test can hand <see cref="MvtGeometryMaterializer"/> an exact ring layout.
    /// The encoding is the one <c>MvtDecodeJob</c> decodes; production hand-authored no such stream after
    /// IR B5 retired the background quad's (see <c>FullExtentRingCommandStream</c>, its frozen record).
    /// </summary>
    internal static class MvtCommandStream
    {
        private const uint MoveTo    = 1;
        private const uint LineTo    = 2;
        private const uint ClosePath = 7;

        /// <summary>One feature's geometry: each ring becomes MoveTo×1 + LineTo×(n−1) + ClosePath, with the
        /// cursor carrying across rings exactly as the spec requires.</summary>
        public static uint[] Feature(params IReadOnlyList<double2>[] rings)
        {
            var commands = new List<uint>();
            long cursorX = 0, cursorY = 0;

            foreach (IReadOnlyList<double2> ring in rings)
            {
                if (ring.Count == 0) continue;

                commands.Add((1u << 3) | MoveTo);
                AppendDelta(commands, ring[0], ref cursorX, ref cursorY);

                if (ring.Count > 1)
                {
                    commands.Add(((uint)(ring.Count - 1) << 3) | LineTo);
                    for (int i = 1; i < ring.Count; i++)
                        AppendDelta(commands, ring[i], ref cursorX, ref cursorY);
                }

                commands.Add((1u << 3) | ClosePath);
            }

            return commands.ToArray();
        }

        public static List<double2> Ring(params double[] xyPairs)
        {
            var ring = new List<double2>(xyPairs.Length / 2);
            for (int i = 0; i < xyPairs.Length; i += 2)
                ring.Add(new double2(xyPairs[i], xyPairs[i + 1]));
            return ring;
        }

        private static void AppendDelta(List<uint> commands, double2 point, ref long cursorX, ref long cursorY)
        {
            long x = (long)point.x;
            long y = (long)point.y;
            commands.Add(ZigZag(x - cursorX));
            commands.Add(ZigZag(y - cursorY));
            cursorX = x;
            cursorY = y;
        }

        /// <summary>Protobuf zigzag encode — the exact inverse of <c>MvtDecodeJob.ZigZag</c>.</summary>
        private static uint ZigZag(long n) => (uint)((n << 1) ^ (n >> 63));
    }

    /// <summary>
    /// IR stage B2: the teeth on the MVT end of Waist 1's producer seam — what the materializer puts in the
    /// buffer, and who owns the buffer afterwards.
    ///
    /// <para><c>RingCapacity</c> is exercised here rather than beside the other
    /// <c>TileGeometryBuffers</c> cases because it exists for this seam: once the sizing pre-pass moved into
    /// the materializer, the capacity <c>FillMeshPipeline.Schedule</c> sizes Stage 2 from has to come back off
    /// the buffer.</para>
    /// </summary>
    [TestFixture]
    public class MvtGeometryMaterializerTests
    {
        private static readonly TileId SampleTile = new TileId { Z = 8, X = 135, Y = 80 };
        private const double SampleExtent = 8192.0;

        /// <summary>
        /// T3 — the shared buffer carries <b>every</b> ring the source expresses, including rings too short
        /// to be a polygon. Fill's <c>rLen &lt; 3</c> filter belongs to <c>RingAssemblyJob</c>; a filter that
        /// crept into the shared decode stage would starve the line consumer, and
        /// <c>Schedule</c>'s output cannot see it (ring assembly re-filters, and nothing reads
        /// <c>TileMeshBuffers.RingCount</c>). So the materializer is driven directly.
        /// </summary>
        [Test]
        public void Materialize_CarriesShortRingsUnfiltered()
        {
            uint[] stream = MvtCommandStream.Feature(
                MvtCommandStream.Ring(10, 10, 20, 10),                        // 2 points
                MvtCommandStream.Ring(30, 30),                                // 1 point
                MvtCommandStream.Ring(100, 100, 200, 100, 200, 200, 100, 200) // 4 points
            );

            var materializer = MakeMaterializer(SampleTile, SampleExtent, Carrier(TileGeometryType.Polygon, stream));

            TileGeometryBuffers geometry = materializer.Materialize();
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the materializer produced a buffer");
                Assert.AreEqual(3, geometry.RingCount,
                    "all three rings must survive — a < 3-point filter here starves the line consumer");
                Assert.AreEqual(7, geometry.VertexCount, "2 + 1 + 4 vertices, none dropped");

                int[] spans =
                {
                    geometry.RingOffsets[1] - geometry.RingOffsets[0],
                    geometry.RingOffsets[2] - geometry.RingOffsets[1],
                    geometry.RingOffsets[3] - geometry.RingOffsets[2],
                };
                Assert.AreEqual(new[] { 2, 1, 4 }, spans,
                    "ring order and length must be exactly as authored — this pins WHICH rings survived, " +
                    "not merely how many");

                // Non-vacuity: the surviving 4-point ring really is the authored one, and really is last.
                Assert.AreEqual(new double2(100.0, 100.0), geometry.Vertices[geometry.RingOffsets[2]],
                    "the last ring must be the 4-point ring, at its authored position");
                Assert.AreEqual(new[] { 0, 0, 0 },
                    new[] { geometry.RingFeatureIdx[0], geometry.RingFeatureIdx[1], geometry.RingFeatureIdx[2] },
                    "all three rings belong to the single authored feature");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>
        /// T4a — ownership TRANSFERS on return: every call mints a fresh buffer, so one materializer may be
        /// materialized more than once (<c>RingClipJobTests</c> does exactly that) and disposing one result
        /// cannot touch another. A cached buffer would double-free instead.
        /// </summary>
        [Test]
        public void Materialize_TransfersOwnership_AndMintsAFreshBufferPerCall()
        {
            uint[] stream = MvtCommandStream.Feature(
                MvtCommandStream.Ring(100, 100, 200, 100, 200, 200, 100, 200));

            var materializer = MakeMaterializer(SampleTile, SampleExtent, Carrier(TileGeometryType.Polygon, stream));

            TileGeometryBuffers first  = materializer.Materialize();
            TileGeometryBuffers second = materializer.Materialize();

            // Non-vacuity: two `default` results would satisfy every claim below trivially.
            Assert.IsTrue(first.IsCreated, "the first call must mint a buffer");
            Assert.IsTrue(second.IsCreated, "the second call must mint a buffer");
            Assert.AreEqual(4, first.VertexCount, "precondition: the first buffer really holds the decode");
            Assert.AreEqual(4, second.VertexCount, "precondition: the second buffer really holds the decode");

            // Distinct allocations: writing through one must not be visible through the other.
            first.Vertices[0] = new double2(-1.0, -1.0);
            Assert.AreEqual(new double2(100.0, 100.0), second.Vertices[0],
                "the two calls must return distinct allocations — a cached buffer would alias here and " +
                "double-free on the second Dispose");

            first.Dispose();
            Assert.DoesNotThrow(() => { var _ = second.Vertices[0]; },
                "disposing one result must leave the other usable");

            second.Dispose();
            Assert.DoesNotThrow(() => first.Dispose(), "re-disposing is a no-op, not a double free");
            Assert.DoesNotThrow(() => second.Dispose(), "re-disposing is a no-op, not a double free");

            // The empty-input exit path allocates nothing and hands back a buffer nobody has to free.
            var empty = MakeMaterializer(SampleTile, SampleExtent);
            TileGeometryBuffers nothing = empty.Materialize();
            Assert.IsFalse(nothing.IsCreated, "an empty feature list must materialize to default, not to an allocation");
            Assert.DoesNotThrow(() => nothing.Dispose(), "disposing the empty result must be a no-op");
        }

        /// <summary>
        /// T4b — <c>RingCapacity</c> is the producer's sized upper bound, never the decoded count.
        /// <c>Schedule</c> sizes Stage 2's per-polygon arrays from it, so a value that tracked
        /// <c>RingCount</c> would under-allocate the moment the two differ.
        /// </summary>
        [Test]
        public void Allocate_RingCapacity_IsTheSizedBound_NotTheDecodedCount()
        {
            var geometry = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 2, maxRings: 7, maxVertices: 30);

            Assert.AreEqual(7, geometry.RingCapacity, "RingCapacity is the maxRings Allocate was given");
            Assert.AreEqual(0, geometry.RingCount,
                "precondition: capacity and count differ here, or the test could not tell them apart");

            geometry.RingCount = 3;
            Assert.AreEqual(7, geometry.RingCapacity,
                "reporting a decoded count must not shrink the capacity the buffers were sized to");

            geometry.Dispose();

            // List-backed: a length-authoritative stage sizes exactly, so capacity IS the count.
            var vertexList  = new NativeList<double2>(4, Allocator.Persistent);
            var offsetList  = new NativeList<int>(4, Allocator.Persistent);
            var featureList = new NativeList<int>(3, Allocator.Persistent);
            for (int i = 0; i < 3; i++)
            {
                vertexList.Add(new double2(i, i));
                offsetList.Add(i);
                featureList.Add(0);
            }
            offsetList.Add(3); // trailing sentinel ⇒ 3 rings

            var kinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            var adopted = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, kinds, vertexList, offsetList, featureList);
            kinds.Dispose(); // AdoptDerivedLists COPIES the column; this one is still ours

            Assert.AreEqual(3, adopted.RingCount, "precondition: the adopted lists describe 3 rings");
            Assert.AreEqual(3, adopted.RingCapacity,
                "the list-backed mode is sized exactly, so capacity equals the count");

            adopted.Dispose();
        }

        /// <summary>
        /// B5 T6 — the kind column is filled from <b>each feature's own declared kind</b>, never a literal.
        /// This is the surviving half of the retired parallel-list desync guard (B3 T9): with one feature
        /// list instead of two positionally-joined columns there is nothing left to desync, so that guard is
        /// retired <b>by construction</b> rather than weakened. What still needs an observer is landmine #1 —
        /// a materializer that wrote a constant <c>Polygon</c> would make every consumer's ring gate
        /// inert, and every downstream stage would keep passing.
        /// </summary>
        [Test]
        public void Materialize_FillsTheKindColumnFromEachFeature_NotALiteral()
        {
            uint[] first  = MvtCommandStream.Feature(MvtCommandStream.Ring(10, 10, 20, 10, 20, 20));
            uint[] second = MvtCommandStream.Feature(MvtCommandStream.Ring(30, 30, 40, 30, 40, 40));

            TileGeometryBuffers geometry = MakeMaterializer(SampleTile, SampleExtent, Carrier(TileGeometryType.LineString, first), Carrier(TileGeometryType.Polygon, second)).Materialize();
            try
            {
                // Non-vacuity: the buffer really decoded, so the column below is not merely a zeroed
                // allocation, and the two kinds DIFFER — a literal-writing implementation could not pass.
                Assert.IsTrue(geometry.IsCreated, "precondition: the materializer produced a buffer");
                Assert.Greater(geometry.RingCount, 0, "precondition: the streams really decoded");
                Assert.AreEqual(2, geometry.FeatureCount, "the column is one entry per feature");

                Assert.AreEqual(TileGeometryType.LineString, geometry.FeatureGeometryType[0],
                    "feature 0's kind must come from the feature — a literal Polygon would show here");
                Assert.AreEqual(TileGeometryType.Polygon, geometry.FeatureGeometryType[1],
                    "…and feature 1's too, so an off-by-one or a constant fill is visible either way");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>
        /// B5 T7 — the MVT materializer reads bytes off <see cref="IMvtGeometryCarrier"/>, MVT's own
        /// contract, not off the neutral feature interface. A feature that is an <see cref="IFeature"/>
        /// but not a carrier is a <b>wiring error</b> — some other format's feature routed to the MVT
        /// producer — and must fail loudly, naming the index, rather than materialize as an empty layer that
        /// renders nothing and reports success.
        ///
        /// <para>The other half of the same contract, asserted here so the two cannot drift apart: a real
        /// carrier whose <c>Geometry</c> is <c>null</c> is <b>not</b> an error — it is zero commands. Line
        /// and symbol depend on that, since they hand the materializer every selected feature, geometry or
        /// not.</para>
        /// </summary>
        [Test]
        public void Materialize_KindColumnLengthMismatch_ThrowsBeforeAllocating_ButANullStreamIsZeroCommands()
        {
            uint[] stream = MvtCommandStream.Feature(MvtCommandStream.Ring(10, 10, 20, 10, 20, 20));

            // IR C1 P3 — RESTATED. This clause used to assert that a feature the MVT materializer could not
            // downcast to IMvtGeometryCarrier threw, naming its index and type. That hazard no longer exists
            // as a shape: geometry does not travel on features at all, so there is nothing to downcast and no
            // non-carrier to reject — a non-MVT source has its OWN layer with its own buffer. What survives
            // is the hazard the two-list input introduces in its place: the kind column and the command
            // column are joined by POSITION, so a length mismatch would mis-classify every ring. It must
            // throw BEFORE Allocate (the same standard PathGeometryMaterializer is held to), or four
            // Allocator.Persistent arrays are stranded with no caller able to free them.
            var ex = Assert.Throws<ArgumentException>(
                () => new MvtGeometryMaterializer(
                        SampleTile, SampleExtent,
                        new List<TileGeometryType> { TileGeometryType.Polygon },      // 1 kind
                        new List<uint[]> { stream, stream }).Materialize(),           // 2 command streams
                "a kind column that does not span every feature must throw, not silently mis-classify rings");
            StringAssert.Contains("2", ex.Message,
                "the message must name the feature count the column had to match");

            // The contract's other half, in the SAME test: a carrier with no stream is zero commands.
            TileGeometryBuffers geometry = MakeMaterializer(SampleTile, SampleExtent, Carrier(TileGeometryType.Polygon, stream), Carrier(TileGeometryType.Point, null)).Materialize();
            try
            {
                Assert.IsTrue(geometry.IsCreated, "a null stream must not suppress the other feature's rings");
                Assert.AreEqual(1, geometry.RingCount, "exactly the one real feature's ring");
                Assert.AreEqual(0, geometry.RingFeatureIdx[0],
                    "…still attributed to feature 0 — the null-geometry feature keeps its ordinal");
                Assert.AreEqual(TileGeometryType.Point, geometry.FeatureGeometryType[1],
                    "the geometry-less feature is still in the column, at its own position");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        // ── Fixture helpers ────────────────────────────────────────────────────────────────────────

        /// <summary>IR C1 P3: the materializer takes (tile, extent, kinds, commands) rather than a feature
        /// list — the sidecar interface it used to downcast through is gone. This adapter keeps the fixtures
        /// authored as features, which is still the readable shape, and splits the two columns here.</summary>
        private static MvtGeometryMaterializer MakeMaterializer(
            TileId tile, double extent, params IFeature[] features)
        {
            var kinds    = new List<TileGeometryType>(features.Length);
            var commands = new List<uint[]>(features.Length);
            foreach (IFeature f in features)
            {
                kinds.Add(f.GeometryType);
                commands.Add((f as ITileCommandStreamFeature)?.Geometry);
            }
            return new MvtGeometryMaterializer(tile, extent, kinds, commands);
        }

        private static IFeature Carrier(TileGeometryType kind, uint[] geometry)
            => new InMemoryTileFeature { GeometryType = kind, Geometry = geometry };
    }
}
