// Unity EditMode only — NativeArray/NativeList ownership of MapRenderer.Jobs.TileGeometryBuffers.
// NOT registered in Tools/core-tests (the engine-free runner cannot compile Unity.Collections).

using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR stage B1: the ownership teeth for <see cref="TileGeometryBuffers"/> — the struct that now owns the
    /// ring-stage buffers <c>FillMeshPipeline.Schedule</c> used to hold as private locals.
    ///
    /// <para>Every case asserts a <b>non-vacuity precondition</b> first (the buffers really were allocated,
    /// really were readable) so the post-<c>Dispose</c> assertions cannot pass over a buffer that was never
    /// created.</para>
    ///
    /// <para><b>How "the backing list was freed" is observed.</b> <c>NativeList&lt;T&gt;.IsCreated</c> reads a
    /// pointer stored <i>in the struct copy</i>, and <c>Dispose()</c> nulls it only on the copy it was called
    /// on — so a test-side copy of a list the buffer disposed still reports <c>IsCreated == true</c>. The
    /// observable that <i>does</i> cross copies is the shared atomic safety handle: once the list is freed the
    /// handle is released, and any access through any copy faults. The exact exception type is a Collections-
    /// package detail (an <c>ObjectDisposedException</c> today), so the assertion is "it faults", paired with a
    /// pre-Dispose read that must succeed.</para>
    /// </summary>
    [TestFixture]
    public class TileGeometryBuffersTests
    {
        private static readonly TileId SampleTile = new TileId { Z = 8, X = 135, Y = 80 };
        private const double SampleExtent = 4096.0;

        /// <summary>T4a: the array-backed mode owns its three arrays and frees all of them, once, on
        /// Dispose — and a second Dispose is a no-op rather than a double free.</summary>
        [Test]
        public void Allocate_ThenDispose_FreesEveryArray_AndIsIdempotent()
        {
            var buffers = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 2, maxRings: 4, maxVertices: 16);

            // Non-vacuity: a zero-length NativeArray can report !IsCreated from birth, which would make the
            // post-Dispose assertions below trivially true. These lengths are all non-zero.
            Assert.IsTrue(buffers.IsCreated, "the buffer must report IsCreated immediately after Allocate");
            Assert.IsTrue(buffers.Vertices.IsCreated, "Vertices must be allocated");
            Assert.IsTrue(buffers.RingOffsets.IsCreated, "RingOffsets must be allocated");
            Assert.IsTrue(buffers.RingFeatureIdx.IsCreated, "RingFeatureIdx must be allocated");
            Assert.AreEqual(16, buffers.Vertices.Length, "Vertices is sized to maxVertices");
            Assert.AreEqual(5, buffers.RingOffsets.Length, "RingOffsets is sized to maxRings + 1 (sentinel)");
            Assert.AreEqual(4, buffers.RingFeatureIdx.Length, "RingFeatureIdx is sized to maxRings");

            buffers.Dispose();

            Assert.IsFalse(buffers.IsCreated, "Dispose must flip the struct's IsCreated");
            Assert.IsFalse(buffers.Vertices.IsCreated, "Dispose must free Vertices");
            Assert.IsFalse(buffers.RingOffsets.IsCreated, "Dispose must free RingOffsets");
            Assert.IsFalse(buffers.RingFeatureIdx.IsCreated, "Dispose must free RingFeatureIdx");

            Assert.DoesNotThrow(() => buffers.Dispose(),
                "Dispose must be idempotent — the IsCreated flip guard makes the second call a no-op, not a " +
                "double free");
        }

        /// <summary>T4b: the list-backed mode frees the backing <b>lists</b> and never the <c>AsArray()</c>
        /// views over them. This is the tooth that catches a wrong backing-mode discriminator — and it is
        /// the <b>only</b> one.
        /// <para><b>Measured, not assumed:</b> disposing an <c>AsArray()</c> view is a <b>silent no-op</b>
        /// under Collections 6.5.0 — <i>not</i> a throw. B1's RED sweep set the discriminator so
        /// <c>Dispose()</c> freed the views instead of the lists: no exception was raised, the backing lists
        /// simply leaked, and the <b>entire behavioural clip corpus stayed green</b>; this test was the single
        /// failure. Do not assume the collections safety system catches view-vs-list ownership mistakes, and
        /// do not weaken this test on the belief that a behavioural test backs it up. Nothing does.</para></summary>
        [Test]
        public void AdoptDerivedLists_ThenDispose_FreesTheBackingLists_AndNeverTheViews()
        {
            var vertexList = new NativeList<double2>(4, Allocator.Persistent);
            var offsetList = new NativeList<int>(2, Allocator.Persistent);
            var featureList = new NativeList<int>(1, Allocator.Persistent);

            vertexList.Add(new double2(0.0, 0.0));
            vertexList.Add(new double2(10.0, 0.0));
            vertexList.Add(new double2(10.0, 10.0));
            offsetList.Add(0);
            offsetList.Add(3);
            featureList.Add(0);

            var sourceKinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            var buffers = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, sourceKinds, vertexList, offsetList, featureList);
            sourceKinds.Dispose(); // COPIED into the buffer, so this one is still the caller's

            // Non-vacuity: the views really do window onto live, non-empty lists right now.
            Assert.IsTrue(buffers.IsCreated, "the buffer must report IsCreated immediately after adopting");
            Assert.AreEqual(3, buffers.Vertices.Length, "the vertex view spans the whole backing list");
            Assert.AreEqual(2, buffers.RingOffsets.Length, "the offset view spans the whole backing list");
            Assert.AreEqual(1, buffers.RingFeatureIdx.Length, "the feature view spans the whole backing list");
            Assert.DoesNotThrow(() => { var _ = vertexList[0]; },
                "precondition: the backing vertex list is readable before Dispose");
            Assert.DoesNotThrow(() => { var _ = offsetList[0]; },
                "precondition: the backing offset list is readable before Dispose");
            Assert.DoesNotThrow(() => { var _ = featureList[0]; },
                "precondition: the backing feature list is readable before Dispose");

            Assert.DoesNotThrow(() => buffers.Dispose(),
                "Dispose must free the backing lists — disposing an AsArray() view instead throws, because a " +
                "view is an Allocator.None array");

            // The lists are gone: their shared safety handle is released, so any access through the test's
            // own copies faults.
            Assert.That(() => { var _ = vertexList[0]; }, Throws.Exception,
                "the backing vertex list must have been freed by Dispose");
            Assert.That(() => { var _ = offsetList[0]; }, Throws.Exception,
                "the backing offset list must have been freed by Dispose");
            Assert.That(() => { var _ = featureList[0]; }, Throws.Exception,
                "the backing feature list must have been freed by Dispose");

            // The views themselves were never disposed — that is the whole point of the discriminator.
            Assert.IsTrue(buffers.Vertices.IsCreated,
                "the Vertices view must NOT be disposed — only its backing list is freed");
            Assert.IsTrue(buffers.RingOffsets.IsCreated,
                "the RingOffsets view must NOT be disposed — only its backing list is freed");
            Assert.IsTrue(buffers.RingFeatureIdx.IsCreated,
                "the RingFeatureIdx view must NOT be disposed — only its backing list is freed");

            Assert.IsFalse(buffers.IsCreated, "Dispose must flip the struct's IsCreated");
            Assert.DoesNotThrow(() => buffers.Dispose(),
                "Dispose must be idempotent in the list-backed mode too");
        }

        /// <summary>T4c: adopting derives the counts from the list lengths exactly as the clip handover in
        /// <c>FillMeshPipeline.Schedule</c> used to — ring count is <c>RingOffsets.Length - 1</c> because the
        /// offsets carry a trailing sentinel.</summary>
        [Test]
        public void AdoptDerivedLists_DerivesCountsFromTheListLengths()
        {
            var vertexList = new NativeList<double2>(8, Allocator.Persistent);
            var offsetList = new NativeList<int>(3, Allocator.Persistent);
            var featureList = new NativeList<int>(2, Allocator.Persistent);

            for (int i = 0; i < 7; i++)
                vertexList.Add(new double2(i, i));
            offsetList.Add(0);
            offsetList.Add(4);
            offsetList.Add(7);   // 3 entries ⇒ 2 rings + sentinel
            featureList.Add(0);
            featureList.Add(0);

            var sourceKinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            var buffers = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, sourceKinds, vertexList, offsetList, featureList);
            sourceKinds.Dispose();

            Assert.AreEqual(3, offsetList.Length,
                "precondition: the offsets list holds 2 ring starts plus the trailing sentinel");
            Assert.AreEqual(2, buffers.RingCount,
                "RingCount is RingOffsets.Length - 1 — dropping the sentinel would read one ring past the end");
            Assert.AreEqual(7, buffers.VertexCount,
                "VertexCount is the adopted vertex list's length");

            buffers.Dispose();
        }

        /// <summary>T4d: the counts are the producing job's reported values, <b>stored</b>, not derived from
        /// the buffer capacity. Deriving them would make <c>FillMeshPipeline.EnsureCapacity</c>'s
        /// count-vs-capacity comparison tautological and silently disarm the sizing-vs-decode backstop — a
        /// regression no behavioural test can see, because exact pre-count sizing makes the two values equal
        /// on every fixture in the repo.</summary>
        [Test]
        public void RingCount_IsTheJobReportedCount_NotDerivedFromBufferCapacity()
        {
            var buffers = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 2, maxRings: 8, maxVertices: 32);

            Assert.AreEqual(0, buffers.RingCount, "a freshly allocated buffer has reported no rings yet");
            Assert.AreEqual(0, buffers.VertexCount, "a freshly allocated buffer has reported no vertices yet");

            buffers.RingCount = 3;
            buffers.VertexCount = 11;

            Assert.AreEqual(3, buffers.RingCount,
                "RingCount must be the stored reported count, not RingOffsets.Length - 1");
            Assert.AreEqual(9, buffers.RingOffsets.Length,
                "precondition: the capacity (maxRings + 1 = 9) differs from the reported count, so a derived " +
                "count would be observably wrong here");
            Assert.AreEqual(11, buffers.VertexCount,
                "VertexCount must be the stored reported count, not Vertices.Length");
            Assert.AreEqual(32, buffers.Vertices.Length,
                "precondition: the vertex capacity differs from the reported vertex count");

            buffers.Dispose();
        }

        /// <summary>T4e: the provenance metadata is carried, not dropped — the pipeline reads
        /// <see cref="TileGeometryBuffers.Extent"/> back off the buffer when it sizes the clip window.</summary>
        [Test]
        public void Allocate_CarriesTheTileAndExtentItWasGiven()
        {
            var buffers = TileGeometryBuffers.Allocate(
                SampleTile, 8192.0, featureCount: 1, maxRings: 2, maxVertices: 8);

            Assert.AreEqual(SampleTile.Z, buffers.Tile.Z, "the buffer carries the tile it was minted for");
            Assert.AreEqual(SampleTile.X, buffers.Tile.X, "the buffer carries the tile it was minted for");
            Assert.AreEqual(SampleTile.Y, buffers.Tile.Y, "the buffer carries the tile it was minted for");
            Assert.AreEqual(8192.0, buffers.Extent,
                "the buffer carries the extent it was minted with — deliberately not the 4096 every fill " +
                "fixture uses, so a hardcoded default would be visible here");

            buffers.Dispose();
        }

        /// <summary>
        /// IR B7 T2b — the per-feature kind column must survive a <b>derive</b>, and the derived buffer's
        /// <c>Dispose</c> must not reach into the buffer it was derived from.
        ///
        /// <para>This is the direct replacement for B3's clip-handover tooth. The mechanism inverted: the
        /// column used to be <b>transferred</b> (released from the pre-clip buffer, adopted by the clipped
        /// one), because the source was about to be disposed. Since B7 the source is <b>borrowed</b> — it is
        /// the store's shared buffer, several fill layers derive from it — so it must be left completely
        /// intact, and the derived buffer gets a <b>copy</b>.</para>
        ///
        /// <para>Why this needs a test at all: an <c>AdoptDerivedLists</c> that <i>took</i> the array instead
        /// of copying it would pass every behavioural fill test in the repo. The first layer to derive would
        /// work; the second would read a freed column, or the store's <c>Dispose</c> would double-free — and
        /// the collections safety system does <b>not</b> reliably surface either (B1 measured a whole green
        /// corpus over a leaking-view discriminator). The observable difference is here, in the source
        /// buffer's state after the derived one dies.</para></summary>
        [Test]
        public void AdoptDerivedLists_CopiesTheKindColumn_SoTheSourceSurvivesTheDerivedBuffersDispose()
        {
            var source = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 3, maxRings: 2, maxVertices: 8);

            // Non-vacuity: at least two DISTINCT kinds, so an implementation that carried nothing (or carried
            // a cleared default) cannot pass by accident.
            source.FeatureGeometryType[0] = TileGeometryType.Polygon;
            source.FeatureGeometryType[1] = TileGeometryType.LineString;
            source.FeatureGeometryType[2] = TileGeometryType.Polygon;
            Assert.AreEqual(3, source.FeatureCount, "precondition: FeatureCount is derived from the column");

            var vertexList  = new NativeList<double2>(4, Allocator.Persistent);
            var offsetList  = new NativeList<int>(2, Allocator.Persistent);
            var featureList = new NativeList<int>(1, Allocator.Persistent);
            vertexList.Add(new double2(0.0, 0.0));
            vertexList.Add(new double2(10.0, 0.0));
            vertexList.Add(new double2(10.0, 10.0));
            offsetList.Add(0);
            offsetList.Add(3);
            featureList.Add(1);

            var derived = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, source.FeatureGeometryType, vertexList, offsetList, featureList);

            // (a) the source is untouched — this is the whole borrow contract, and it is what a "take"
            //     implementation breaks.
            Assert.IsTrue(source.FeatureGeometryType.IsCreated,
                "the derive must NOT null or steal the source's column — the source is BORROWED");
            Assert.AreEqual(3, source.FeatureCount, "…so the source still reports its own feature count");

            // (b) the derived buffer carries the values, not a cleared default.
            Assert.AreEqual(3, derived.FeatureCount,
                "the derived buffer carries the whole column — the derive renumbers no feature index");
            Assert.AreEqual(TileGeometryType.Polygon,    derived.FeatureGeometryType[0], "values carried over");
            Assert.AreEqual(TileGeometryType.LineString, derived.FeatureGeometryType[1], "…including the discriminating one");
            Assert.AreEqual(TileGeometryType.Polygon,    derived.FeatureGeometryType[2], "values carried over");
            Assert.AreEqual(TileGeometryType.LineString,
                derived.FeatureGeometryType[derived.RingFeatureIdx[0]],
                "the ring→kind join still resolves through the derived RingFeatureIdx");

            // (c) it really is a COPY, not an alias: writing through one must not be visible through the
            //     other. A take-instead-of-copy passes (a) and (b) but fails here.
            derived.FeatureGeometryType[1] = TileGeometryType.Point;
            Assert.AreEqual(TileGeometryType.LineString, source.FeatureGeometryType[1],
                "the derived column must be a distinct allocation — an alias would show the write here, and " +
                "the two buffers would then double-free it");

            // (d) disposing the derived buffer leaves the source fully usable. Under Collections 6.5.0 a
            //     two-owner mistake is a SILENT no-op rather than a throw, so read the value back.
            derived.Dispose();
            Assert.IsTrue(source.FeatureGeometryType.IsCreated,
                "the source's column must survive the derived buffer's Dispose");
            Assert.DoesNotThrow(() => { var _ = source.FeatureGeometryType[1]; },
                "…and still be readable");
            Assert.AreEqual(TileGeometryType.LineString, source.FeatureGeometryType[1],
                "…with its value intact");

            source.Dispose();
            Assert.DoesNotThrow(() => derived.Dispose(), "a second Dispose is a no-op, not a double free");
        }
    }
}
