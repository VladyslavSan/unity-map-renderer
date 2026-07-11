// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). No UnityEngine — the store is pure (source, tile) → labels bookkeeping.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// The label-lifecycle fix (zoom-out-then-in "no labels" bug): <see cref="SymbolTileLabelStore"/> keeps a
    /// released-to-cache tile's labels WARM and restores them on a prepared-cache hit (which does not re-fetch),
    /// while a truly-evicted tile drops them. Also covers the async race — a tile released WHILE its build is in
    /// flight must still get its labels (into the cached side), and a superseded build must be discarded.
    /// </summary>
    [TestFixture]
    public class SymbolTileLabelStoreTests
    {
        private static SymbolTileLabelStore.Key Key(string source, int x)
            => new SymbolTileLabelStore.Key(source, new TileId { Z = 5, X = x, Y = 0 });

        // A distinct label list (identity via FeatureIndex) so assertions can pin WHICH tile's labels came back.
        private static List<LabelInstance> Labels(int marker)
            => new List<LabelInstance> { new LabelInstance { FeatureIndex = marker } };

        private static List<LabelInstance> Collect(SymbolTileLabelStore store)
        {
            var output = new List<LabelInstance>();
            store.CollectInto(output);
            return output;
        }

        // ── THE bug: build → release-to-cache → (out of cover, not rendered) → cache HIT restore → back. ──
        [Test]
        public void ReleaseToCacheThenRestore_KeepsLabels()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);

            int gen = store.BeginBuild(key);
            store.CompleteBuild(key, gen, Labels(1));
            Assert.AreEqual(1, Collect(store).Count, "an active tile's labels render");

            store.Release(key, transferredToCache: true);
            Assert.AreEqual(0, Collect(store).Count, "a cached (out-of-cover) tile does NOT render — but is kept warm");

            store.Restore(key); // prepared-cache HIT — no fetch, so no rebuild; labels must come from the store
            List<LabelInstance> back = Collect(store);
            Assert.AreEqual(1, back.Count, "the cache hit restores the kept-warm labels (the zoom-out-then-in fix)");
            Assert.AreEqual(1, back[0].FeatureIndex, "it is the SAME tile's labels");
        }

        // ── A true eviction (cache disabled / not built) drops the labels — a later restore finds nothing. ──
        [Test]
        public void ReleaseWithoutCache_DropsLabels()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1));

            store.Release(key, transferredToCache: false);
            store.Restore(key); // nothing was kept — a no-op
            Assert.AreEqual(0, Collect(store).Count, "a truly-evicted tile's labels are gone, not resurrected");
        }

        // ── The async race: a tile RELEASED-to-cache while its build is still in flight must still capture its
        //    labels (into the cached entry) so a later hit restores them — not lose them permanently. ──
        [Test]
        public void ReleasedMidBuild_StillCapturesLabelsIntoCache()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);

            int gen = store.BeginBuild(key);            // build starts
            store.Release(key, transferredToCache: true); // tile leaves cover BEFORE the (awaited) build completes
            Assert.IsTrue(store.CompleteBuild(key, gen, Labels(1)),
                "a build completing after a release-to-cache must still commit (into the cached entry)");
            Assert.AreEqual(0, Collect(store).Count, "still out of cover → not rendered yet");

            store.Restore(key);
            Assert.AreEqual(1, Collect(store).Count, "…and the cache hit then shows the labels captured mid-flight");
        }

        // ── A superseded build (the tile was re-fetched, a newer BeginBuild took the slot) is discarded. ──
        [Test]
        public void SupersededBuild_IsDiscarded()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);

            int gen1 = store.BeginBuild(key);
            int gen2 = store.BeginBuild(key); // a newer fetch/build for the same tile takes the slot

            Assert.IsFalse(store.CompleteBuild(key, gen1, Labels(1)), "the stale (superseded) build must not commit");
            Assert.AreEqual(0, Collect(store).Count, "…so the stale labels never render");

            Assert.IsTrue(store.CompleteBuild(key, gen2, Labels(2)), "the current build commits");
            Assert.AreEqual(2, Collect(store)[0].FeatureIndex, "and only its labels render");
        }

        // ── FIFO cap: the cached side is bounded — the OLDEST-released tile is evicted first, so it cannot be
        //    restored while the newer ones can (sized to the mesh cache's cap, labels outlive their meshes). ──
        [Test]
        public void CachedSide_EvictsOldestReleasedFirst()
        {
            var store = new SymbolTileLabelStore(cacheCap: 2);
            var a = Key("src", 1); var b = Key("src", 2); var c = Key("src", 3);
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1));
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2));
            store.CompleteBuild(c, store.BeginBuild(c), Labels(3));

            store.Release(a, true); // oldest released
            store.Release(b, true);
            store.Release(c, true); // over cap (2) → evicts a (the oldest)
            Assert.AreEqual(2, store.CachedTileCount, "cap of 2 bounds the cached side");

            store.Restore(a);
            Assert.AreEqual(0, Collect(store).Count, "the oldest-released tile was evicted — nothing to restore");
            store.Restore(b); store.Restore(c);
            var back = Collect(store);
            Assert.AreEqual(2, back.Count, "the two newest cached tiles restore fine");
        }

        // ── Multi-source isolation (why we DON'T evict by bare tileId): same TileId, different source are
        //    independent — releasing/restoring one never touches the other. ──
        [Test]
        public void SameTileDifferentSource_AreIndependent()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = new SymbolTileLabelStore.Key("srcA", new TileId { Z = 5, X = 7, Y = 7 });
            var b = new SymbolTileLabelStore.Key("srcB", new TileId { Z = 5, X = 7, Y = 7 }); // same tile, other source
            store.CompleteBuild(a, store.BeginBuild(a), Labels(10));
            store.CompleteBuild(b, store.BeginBuild(b), Labels(20));

            store.Release(a, true);
            store.Release(b, true);
            store.Restore(a); // restore ONLY source A

            var back = Collect(store);
            Assert.AreEqual(1, back.Count, "only source A's labels are active");
            Assert.AreEqual(10, back[0].FeatureIndex, "and they are A's, not B's — keys are (source, tile)");
        }

        // ── Clear (restyle) drops everything, active and cached. ──
        [Test]
        public void Clear_DropsActiveAndCached()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1));
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2));
            store.Release(b, true); // b cached, a active

            store.Clear();
            Assert.AreEqual(0, store.ActiveTileCount);
            Assert.AreEqual(0, store.CachedTileCount);
            store.Restore(b);
            Assert.AreEqual(0, Collect(store).Count, "a restyle purges both sides");
        }
    }
}
