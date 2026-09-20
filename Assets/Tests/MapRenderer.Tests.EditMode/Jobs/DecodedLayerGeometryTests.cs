// Unity EditMode only — NativeArray/NativeList + Burst jobs. NOT registered in Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR B7a's two store teeth, REHOMED to their P3 owner. The claims are unchanged — a source-layer is
    /// materialized <b>once</b> (T4c) and what a consumer reads is genuinely <b>borrowed</b>, so it can be run
    /// over twice and left intact (T2) — but their subject moved: the memo that used to live in
    /// <c>TileGeometryStore</c> is now the decoded LAYER's own field, so "the store memoizes" becomes
    /// "the layer holds one buffer" and "the store's Dispose frees what it lent" becomes "the TILE's Dispose
    /// does".
    ///
    /// <para>Deleting these with the store would have retired two live guarantees. Restated, the first is
    /// strictly harder to satisfy accidentally: a store could be memoized correctly and still be one of
    /// several, whereas a layer's buffer is singular by construction — so what the first test now
    /// discriminates is a re-materializing <c>Geometry</c> getter, the one remaining way to reintroduce
    /// N-decodes-per-source-layer.</para>
    /// </summary>
    [TestFixture]
    public class DecodedLayerGeometryTests
    {
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        private static readonly TileId SampleTile = new TileId { Z = 8, X = 135, Y = 80 };
        private const double SampleExtent = 4096.0;

        // ── T4c — the memo ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Two reads of the SAME <see cref="ITileLayer.Geometry"/> hand back the same allocation; two
        /// different layers of one tile do not share one.
        ///
        /// <para><c>NativeArray&lt;T&gt;</c> equality is pointer + length, so <c>AreEqual</c> on
        /// <c>Vertices</c> is an identity test rather than a content test — which is exactly what
        /// discriminates "the layer holds ONE buffer" from "the getter materializes again with identical
        /// contents". The latter is the P3 shape of the 108-materializations-per-tile defect, and it is
        /// invisible to every output test.</para>
        /// </summary>
        [Test]
        public void LayerGeometry_IsOneBufferPerSourceLayer_ByReference()
        {
            InMemoryDecodedTile tile = TestDecodedTiles.OfLayers(
                Layer("water", Square(10)), Layer("roads", Square(20)), Layer("empty"));

            TileGeometryBuffers a1 = tile.GetLayer("water").Geometry;
            TileGeometryBuffers a2 = tile.GetLayer("water").Geometry;
            TileGeometryBuffers b1 = tile.GetLayer("roads").Geometry;

            // Non-vacuity: both really decoded, so the comparisons below are between live allocations
            // and not between two `default`s.
            Assert.IsTrue(a1.IsCreated, "precondition: the first layer materialized");
            Assert.IsTrue(b1.IsCreated, "precondition: the second layer materialized");
            Assert.AreEqual(4, a1.VertexCount, "precondition: the first layer's square really decoded");
            Assert.AreEqual(4, b1.VertexCount, "precondition: the second layer's square really decoded");

            Assert.AreEqual(a1.Vertices, a2.Vertices,
                "the SECOND read of one layer's Geometry must return the SAME allocation — NativeArray " +
                "equality is pointer+length, so this fails for a re-materialization even though its contents " +
                "would be identical. This is the whole point: N style layers naming one source-layer decode " +
                "it once, across both cadences of a kick.");
            Assert.AreNotEqual(a1.Vertices, b1.Vertices,
                "…and two DIFFERENT source layers must not share a buffer");

            // A layer with nothing in it allocates nothing — the same empty result every consumer handles.
            Assert.IsFalse(tile.GetLayer("empty").Geometry.IsCreated,
                "a feature-less layer must materialize to default, not to an allocation");
            Assert.IsNull(tile.GetLayer("absent"), "an unresolved source-layer name must resolve to null");
        }

        /// <summary>The TILE frees what its layers lent. A borrower that had disposed its loan would
        /// double-free here — and on the array-backed buffer a consumer borrows that is a LOUD double free,
        /// not a silent no-op, so the contract is stated as "the tile's Dispose is the one that works".</summary>
        [Test]
        public void TileDispose_FreesEveryLayersBuffer()
        {
            var tile = new InMemoryDecodedTile(Layer("water", Square(10)));
            TileGeometryBuffers lent = tile.GetLayer("water").Geometry;

            Assert.IsTrue(lent.IsCreated, "precondition: the layer holds a live buffer");
            Assert.DoesNotThrow(() => { var _ = lent.Vertices[0]; },
                "precondition: the borrowed buffer is readable while the tile is alive");

            tile.Dispose();

            Assert.That(() => { var _ = lent.Vertices[0]; }, Throws.Exception,
                "the tile's Dispose must free every layer's buffer — which is also why a borrower must " +
                "never retain one past the decode scope");
            Assert.DoesNotThrow(() => tile.Dispose(), "a second Dispose must be a no-op, not a double free");
        }

        // ── T2 — borrow, not transfer ─────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>FillMeshGraph.Schedule</c> may be called <b>twice over the same buffer</b> — the production
        /// shape, where several fill layers of one source-layer each run against the store's single buffer.
        ///
        /// <para>Three claims, and the third is the one that catches the subtle version: (a) the second run
        /// produces buffers at all; (b) it is bit-identical to the same run over a FRESH buffer, so nothing
        /// the first run did leaked into the input; (c) the shared buffer's four arrays are still created and
        /// still hold their original contents afterwards.</para>
        ///
        /// <para>Catches a re-introduced <c>input.Geometry.Dispose()</c> (the first run kills the input; with
        /// <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> the second throws), an <c>AdoptDerivedLists</c> that MOVES
        /// the kind column instead of copying it (claim c), and a clip that wrote back into the shared arrays
        /// (claims b and c).</para>
        /// </summary>
        [Test]
        public void Schedule_TwiceOverOneBorrowedBuffer_IsIdenticalAndLeavesItIntact()
        {
            InMemoryDecodedTile tile = TestDecodedTiles.OfLayers(Layer("water", Square(100)));

            TileGeometryBuffers shared = tile.GetLayer("water").Geometry;
            Assert.IsTrue(shared.IsCreated, "precondition: the layer holds a live buffer");

            // Snapshot the shared buffer's contents so "unchanged afterwards" is a content claim, not just
            // an IsCreated claim.
            var beforeVerts  = new double2[shared.VertexCount];
            var beforeOffs   = new int[shared.RingCount + 1];
            var beforeFeat   = new int[shared.RingCount];
            var beforeKinds  = new TileGeometryType[shared.FeatureCount];
            for (int i = 0; i < beforeVerts.Length; i++) beforeVerts[i] = shared.Vertices[i];
            for (int i = 0; i < beforeOffs.Length;  i++) beforeOffs[i]  = shared.RingOffsets[i];
            for (int i = 0; i < beforeFeat.Length;  i++) beforeFeat[i]  = shared.RingFeatureIdx[i];
            for (int i = 0; i < beforeKinds.Length; i++) beforeKinds[i] = shared.FeatureGeometryType[i];

            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(shared);

            FillGraphOutput unclipped = Run(shared, visitOrder, TileBufferClip.Disabled);
            FillGraphOutput clipped   = Run(shared, visitOrder, TileBufferClip.KeepTileUnits(0.0));

            // The control arm: the SAME clipped run over a buffer nothing has touched.
            TileGeometryBuffers fresh = TestTileMeshBuilder.Materialize(
                new List<IFeature> { Square(100) }, SampleTile, SampleExtent);
            NativeArray<int> freshOrder = TestTileMeshBuilder.FullVisitOrder(fresh);
            FillGraphOutput control = Run(fresh, freshOrder, TileBufferClip.KeepTileUnits(0.0));

            try
            {
                // (a)
                Assert.IsTrue(unclipped.IsCreated, "the FIRST run over the borrowed buffer produced no mesh");
                Assert.IsTrue(clipped.IsCreated,
                    "the SECOND run over the SAME buffer produced no mesh — the first run consumed its input");
                Assert.Greater(unclipped.TileVertices.Length, 0, "precondition: there is geometry to compare");

                // (b)
                Assert.AreEqual(control.TileVertices.Length, clipped.TileVertices.Length,
                    "the second run over a REUSED buffer must match the same run over a fresh one");
                Assert.AreEqual(control.TriangleIndices.Length, clipped.TriangleIndices.Length, "…index counts too");
                for (int i = 0; i < control.TileVertices.Length; i++)
                    Assert.AreEqual(control.TileVertices[i], clipped.TileVertices[i], $"TileVertices[{i}]");
                for (int i = 0; i < control.TriangleIndices.Length; i++)
                    Assert.AreEqual(control.TriangleIndices[i], clipped.TriangleIndices[i], $"TriangleIndices[{i}]");

                // (c) — the borrowed buffer is byte-for-byte what it was.
                Assert.IsTrue(shared.Vertices.IsCreated,            "the borrowed Vertices must survive");
                Assert.IsTrue(shared.RingOffsets.IsCreated,         "the borrowed RingOffsets must survive");
                Assert.IsTrue(shared.RingFeatureIdx.IsCreated,      "the borrowed RingFeatureIdx must survive");
                Assert.IsTrue(shared.FeatureGeometryType.IsCreated,
                    "the borrowed kind column must survive — an AdoptDerivedLists that TOOK it instead of " +
                    "copying it would have moved it into the first derived buffer and freed it there");
                for (int i = 0; i < beforeVerts.Length; i++)
                    Assert.AreEqual(beforeVerts[i], shared.Vertices[i], $"borrowed Vertices[{i}] was mutated");
                for (int i = 0; i < beforeOffs.Length; i++)
                    Assert.AreEqual(beforeOffs[i], shared.RingOffsets[i], $"borrowed RingOffsets[{i}] was mutated");
                for (int i = 0; i < beforeFeat.Length; i++)
                    Assert.AreEqual(beforeFeat[i], shared.RingFeatureIdx[i], $"borrowed RingFeatureIdx[{i}] was mutated");
                for (int i = 0; i < beforeKinds.Length; i++)
                    Assert.AreEqual(beforeKinds[i], shared.FeatureGeometryType[i], $"borrowed kind[{i}] was mutated");
            }
            finally
            {
                unclipped.Dispose();
                clipped.Dispose();
                control.Dispose();
                visitOrder.Dispose();
                freshOrder.Dispose();
                fresh.Dispose();
            }
        }

        // ── Fixture ───────────────────────────────────────────────────────────────────────────────

        private static FillGraphOutput Run(
            TileGeometryBuffers geometry, NativeArray<int> visitOrder, TileBufferClip clip)
        {
            var (bMin, _) = SampleTile.MercatorBounds();
            FillGraphOutput output = FillMeshGraph.Schedule(new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                Clip           = clip,
            });
            output.Handle.Complete();
            return output;
        }

        /// <summary>One axis-aligned square polygon feature, well inside the tile.</summary>
        private static IFeature Square(int origin) => new InMemoryTileFeature
        {
            GeometryType = TileGeometryType.Polygon,
            Geometry     = MvtCommandStream.Feature(MvtCommandStream.Ring(
                origin, origin, origin + 300, origin, origin + 300, origin + 300, origin, origin + 300)),
        };

        private static InMemoryTileLayer Layer(string name, params IFeature[] features)
            => new InMemoryTileLayer(name, SampleTile, features, (uint)SampleExtent);
    }
}
