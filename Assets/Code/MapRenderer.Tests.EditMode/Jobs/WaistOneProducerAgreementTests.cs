// Unity EditMode only — TileGeometryBuffers holds Unity.Collections NativeArrays. NOT registered in
// core-tests.csproj.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR C1 fix stage, B3/B4: <b>Waist 1's two producers agree on the feature count, and the lockstep is a
    /// property of the type rather than of one writer.</b>
    ///
    /// <para>Three consumers size a per-feature column from a count and then index it by
    /// <c>SelectedTileFeature.Ordinal</c>. That only works while the buffer's feature column and
    /// <c>ITileLayer.Features</c> hold the same number of entries. <see cref="MvtGeometryMaterializer"/>
    /// guaranteed it (it early-outs on the FEATURE count); <see cref="PathGeometryMaterializer"/> did not
    /// (it early-outed on the RING total, reachable with features present), and <c>MvtLayer.Geometry</c> was
    /// a public mutable field that anything could desync. Both are closed here.</para>
    /// </summary>
    [TestFixture]
    public class WaistOneProducerAgreementTests
    {
        private static readonly TileId Tile = new TileId { Z = 3, X = 4, Y = 5 };

        // ── B3 root cause · the two producers early-out on the same thing ─────────────────────────────

        /// <summary>Three features, none of which carries a path — a GeoJSON feature list sliced away at
        /// this tile is the realistic source. The old <c>ringTotal == 0</c> early-out returned
        /// <c>default</c>, i.e. <c>FeatureCount == 0</c> beside three features.</summary>
        [Test]
        public void PathProducer_FeaturesWithNoPaths_StillMintsAFeatureColumn()
        {
            var kinds = new List<TileGeometryType>
            {
                TileGeometryType.LineString, TileGeometryType.Point, TileGeometryType.Polygon,
            };
            var noPaths = new List<IReadOnlyList<IReadOnlyList<double2>>>
            {
                new List<IReadOnlyList<double2>>(), null, new List<IReadOnlyList<double2>>(),
            };

            TileGeometryBuffers geometry =
                new PathGeometryMaterializer(Tile, 4096.0, kinds, noPaths).Materialize();
            try
            {
                Assert.IsTrue(geometry.IsCreated,
                    "features present ⇒ a buffer, even with no rings. Returning `default` here is what puts " +
                    "FeatureCount == 0 next to a non-empty ITileLayer.Features.");
                Assert.AreEqual(3, geometry.FeatureCount, "one kind slot per feature");
                Assert.AreEqual(0, geometry.RingCount,    "no paths ⇒ no rings");
                Assert.AreEqual(0, geometry.VertexCount,  "no paths ⇒ no vertices");

                // The kind column is not merely present, it is CORRECT — a buffer that carried the right
                // length and the wrong kinds would satisfy the count assertions above and still mis-classify
                // every consumer's ring gate.
                Assert.AreEqual(TileGeometryType.LineString, geometry.FeatureGeometryType[0]);
                Assert.AreEqual(TileGeometryType.Point,      geometry.FeatureGeometryType[1]);
                Assert.AreEqual(TileGeometryType.Polygon,    geometry.FeatureGeometryType[2]);

                Assert.AreEqual(Tile, geometry.Tile,   "the producer is the sole authority for the address");
                Assert.AreEqual(4096.0, geometry.Extent, 0.0, "…and for the extent");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>The agreement itself, stated as a comparison rather than as two separate numbers: given
        /// the same three features carrying no geometry, the MVT producer and the path producer must report
        /// the same feature count. This is the property B3's consumer change relies on.</summary>
        [Test]
        public void BothWaistOneProducers_ReportTheSameFeatureCount_ForFeaturesWithNoGeometry()
        {
            var kinds = new List<TileGeometryType>
            {
                TileGeometryType.LineString, TileGeometryType.Point, TileGeometryType.Polygon,
            };

            TileGeometryBuffers fromMvt = MvtGeometryMaterializerTestFactory.Materialize(
                Tile, 4096.0, kinds, new List<uint[]> { null, null, null });
            TileGeometryBuffers fromPaths = new PathGeometryMaterializer(
                Tile, 4096.0, kinds,
                new List<IReadOnlyList<IReadOnlyList<double2>>> { null, null, null }).Materialize();
            try
            {
                Assert.AreEqual(fromMvt.IsCreated, fromPaths.IsCreated,
                    "the two producers must make the same allocate-or-not decision for equivalent input");
                Assert.AreEqual(fromMvt.FeatureCount, fromPaths.FeatureCount,
                    "…and report the same feature count, which is what every ordinal-indexed consumer sizes from");
                Assert.AreEqual(3, fromMvt.FeatureCount, "anti-vacuity: neither may agree at zero");
                Assert.AreEqual(fromMvt.RingCount, fromPaths.RingCount, "…and the same ring count");
            }
            finally { fromMvt.Dispose(); fromPaths.Dispose(); }
        }

        /// <summary>The early-out that remains: no features at all allocates nothing, in BOTH producers.
        /// Pinned so "align the early-outs" cannot drift into "always allocate".</summary>
        [Test]
        public void BothWaistOneProducers_NoFeatures_AllocateNothing()
        {
            TileGeometryBuffers fromMvt = MvtGeometryMaterializerTestFactory.Materialize(
                Tile, 4096.0, new List<TileGeometryType>(), new List<uint[]>());
            TileGeometryBuffers fromPaths = new PathGeometryMaterializer(
                Tile, 4096.0, new List<TileGeometryType>(),
                new List<IReadOnlyList<IReadOnlyList<double2>>>()).Materialize();

            Assert.IsFalse(fromMvt.IsCreated,   "no features ⇒ no buffer (MVT)");
            Assert.IsFalse(fromPaths.IsCreated, "no features ⇒ no buffer (paths)");
            Assert.AreEqual(0, fromMvt.FeatureCount);
            Assert.AreEqual(0, fromPaths.FeatureCount);
        }

        // ── B4 · the lockstep is enforced by MvtLayer, not by MvtDecoder's discipline ──────────────────

        private static MvtLayer LayerWith(int featureCount)
        {
            var layer = new MvtLayer { Name = "probe", Extent = 4096 };
            for (int i = 0; i < featureCount; i++)
                layer.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Point });
            return layer;
        }

        private static TileGeometryBuffers BufferFor(int featureCount)
        {
            var kinds    = new List<TileGeometryType>(featureCount);
            var commands = new List<uint[]>(featureCount);
            for (int i = 0; i < featureCount; i++) { kinds.Add(TileGeometryType.Point); commands.Add(null); }
            return MvtGeometryMaterializerTestFactory.Materialize(Tile, 4096.0, kinds, commands);
        }

        [Test]
        public void MvtLayer_RejectsABufferWhoseFeatureColumnDoesNotMatchItsFeatures()
        {
            MvtLayer layer = LayerWith(2);
            TileGeometryBuffers mismatched = BufferFor(3);

            Assert.AreEqual(3, mismatched.FeatureCount, "precondition: the buffer really does hold 3");
            Assert.AreEqual(2, layer.Features.Count,    "precondition: the layer really does hold 2");

            ArgumentException ex = Assert.Throws<ArgumentException>(() => layer.AdoptGeometry(mismatched),
                "a layer must refuse a buffer it is not in lockstep with — silently accepting it is what " +
                "mis-buckets every ordinal-indexed consumer");
            StringAssert.Contains("3", ex.Message);
            StringAssert.Contains("2", ex.Message);

            Assert.IsFalse(layer.Geometry.IsCreated, "a rejected buffer must not be adopted");

            // Cleanup runs AFTER the assertions, deliberately not in a `finally`. If the guard is gone the
            // layer adopts `mismatched`, and a finally disposing both the local and the layer would free the
            // same three NativeArrays twice — an ObjectDisposedException that REPLACES the assertion failure
            // and hides which property actually broke. A failing run leaking two buffers is the cheaper
            // trade (batch leak detection is off, and a red gate is not a shipping state).
            mismatched.Dispose();
            layer.Dispose();
        }

        [Test]
        public void MvtLayer_AdoptsItsGeometryExactlyOnce()
        {
            MvtLayer layer = LayerWith(2);
            TileGeometryBuffers first  = BufferFor(2);
            TileGeometryBuffers second = BufferFor(2);

            layer.AdoptGeometry(first);
            Assert.IsTrue(layer.Geometry.IsCreated, "precondition: the first adopt takes");

            Assert.Throws<InvalidOperationException>(() => layer.AdoptGeometry(second),
                "a second adopt would orphan the first buffer — MvtTile.Dispose frees only what the " +
                "layer currently holds, so the first allocation would leak with nothing able to reach it");

            // After the assertions, not in a `finally` — see the sibling test above for why (a finally would
            // double-free `second` once the guard is gone and mask the assertion with ObjectDisposedException).
            second.Dispose();
            layer.Dispose();
        }

        /// <summary>A feature-less layer legitimately adopts a <c>default</c> buffer (the materializer's
        /// own zero-feature result). Without this arm the two guards above could be satisfied by a rule that
        /// simply rejects everything falsy.</summary>
        [Test]
        public void MvtLayer_AdoptsTheEmptyBuffer_ForAFeatureLessLayer()
        {
            MvtLayer layer = LayerWith(0);
            Assert.DoesNotThrow(() => layer.AdoptGeometry(default));
            Assert.IsFalse(layer.Geometry.IsCreated, "an empty layer owns nothing to free");
            layer.Dispose();
        }

        /// <summary>The structural half of B4: <c>Geometry</c> is no longer a writable field, so the only
        /// way in is the guarded one above. A behavioural tooth alone cannot see this — a second writer
        /// would simply bypass both guards.</summary>
        [Test]
        public void MvtLayer_Geometry_HasNoPubliclyWritableSetterOrField()
        {
            Assert.IsNull(typeof(MvtLayer).GetField("Geometry"),
                "Geometry must not be a public field — a field is assignable by anything and the lockstep " +
                "guard would be bypassable");

            System.Reflection.PropertyInfo prop = typeof(MvtLayer).GetProperty("Geometry");
            Assert.IsNotNull(prop, "Geometry must still be publicly READABLE — every consumer borrows it");
            Assert.IsNull(prop.GetSetMethod(nonPublic: false),
                "Geometry must have no public setter; AdoptGeometry is the guarded, set-once way in");
        }

        /// <summary>Disposal must survive the field→property change. <c>TileGeometryBuffers.Dispose</c>
        /// clears its own <c>IsCreated</c> to be idempotent, and a property getter hands out a COPY — so a
        /// naive <c>Geometry.Dispose()</c> frees the arrays and leaves the layer still claiming to own them,
        /// which the tile's second (documented-idempotent) dispose turns into a double free.</summary>
        [Test]
        public void MvtLayer_Dispose_IsIdempotent_AndForgetsTheBuffer()
        {
            MvtLayer layer = LayerWith(2);
            layer.AdoptGeometry(BufferFor(2));
            Assert.IsTrue(layer.Geometry.IsCreated, "precondition: the layer owns a real buffer");

            layer.Dispose();
            Assert.IsFalse(layer.Geometry.IsCreated,
                "after Dispose the layer must no longer claim ownership — otherwise the next Dispose double-frees");

            Assert.DoesNotThrow(() => layer.Dispose(), "Dispose is documented idempotent");
        }
    }
}
