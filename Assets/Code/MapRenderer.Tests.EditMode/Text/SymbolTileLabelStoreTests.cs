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

        // ═══ A-1: the PULL/reconcile model (replaces the release/restore push-callbacks) ═══

        private static List<SymbolTileLabelStore.Key> Loaded(params SymbolTileLabelStore.Key[] keys)
            => new List<SymbolTileLabelStore.Key>(keys);

        // ── THE zoom-out-then-in bug, now via reconcile: build (loaded) → reconcile without it (leaves cover,
        //    kept warm) → reconcile with it again (cache-hit re-entry) restores the labels. No callbacks. ──
        [Test]
        public void Reconcile_LeaveThenReenter_RestoresLabels()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
            Assert.AreEqual(1, Collect(store).Count, "active tile renders");

            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true); // tile left cover
            Assert.AreEqual(0, Collect(store).Count, "out of cover → not rendered");
            Assert.AreEqual(1, store.CachedTileCount, "…but kept warm");

            store.ReconcileActiveSet(Loaded(key), keepWarmOnRelease: true); // cache-hit re-entry (no re-fetch)
            List<LabelInstance> back = Collect(store);
            Assert.AreEqual(1, back.Count, "reconcile restores the kept-warm labels on re-entry");
            Assert.AreEqual(1, back[0].FeatureIndex, "the SAME tile's labels");
        }

        // ── keepWarmOnRelease:false (mesh cache disabled) → a released tile is dropped, not kept warm, so a
        //    later re-entry has nothing to restore (a re-fetch would rebuild it via BeginBuild instead). ──
        [Test]
        public void Reconcile_ReleaseWithoutKeepWarm_Drops()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1));

            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: false);
            Assert.AreEqual(0, store.CachedTileCount, "cache disabled → not kept warm");
            store.ReconcileActiveSet(Loaded(key), keepWarmOnRelease: false);
            Assert.AreEqual(0, Collect(store).Count, "nothing to restore — a revisit must re-fetch/rebuild");
        }

        // ── Idempotent: reconciling twice with the same loaded set moves nothing (self-healing, no churn). ──
        [Test]
        public void Reconcile_SameSetTwice_IsNoOp()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1));
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2));

            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true);
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true);
            Assert.AreEqual(2, store.ActiveTileCount, "both stay active");
            Assert.AreEqual(0, store.CachedTileCount, "nothing released");
            Assert.AreEqual(2, Collect(store).Count);
        }

        // ── A loaded tile with no label entry yet (still fetching) is left untouched — its build is kicked by
        //    the bytes-ready push, not by reconcile. Reconcile must not fabricate an entry for it. ──
        [Test]
        public void Reconcile_LoadedButUnbuilt_LeavesForBytesPush()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var pending = Key("src", 9);
            store.ReconcileActiveSet(Loaded(pending), keepWarmOnRelease: true);
            Assert.AreEqual(0, store.ActiveTileCount, "reconcile does not build — it only moves existing entries");
            Assert.AreEqual(0, store.CachedTileCount);
        }

        // ── BeginBuild keeps an existing tile's stale labels visible through a rebuild (no empty flash) — the
        //    labels only swap when the new build commits. ──
        [Test]
        public void BeginBuild_Rebuild_KeepsStaleLabelsUntilCommit()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1));

            int gen2 = store.BeginBuild(key); // a rebuild starts (e.g. zoom re-fetch)
            Assert.AreEqual(1, Collect(store).Count, "the old labels keep rendering during the rebuild — no flash");
            Assert.AreEqual(1, Collect(store)[0].FeatureIndex, "…and they are still the OLD labels");

            store.CompleteBuild(key, gen2, Labels(2));
            Assert.AreEqual(2, Collect(store)[0].FeatureIndex, "only when the rebuild commits do they swap");
        }

        // ═══ B-1: the collected-set Version stamp (drives the static-frame skip) ═══

        // ── Every mutation that can change what CollectInto emits bumps Version. ──
        [Test]
        public void Version_BumpsOnEverySetChangingMutation()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);

            long v0 = store.Version;
            int gen = store.BeginBuild(key);
            Assert.Greater(store.Version, v0, "BeginBuild bumps (an active entry appeared)");

            long v1 = store.Version;
            Assert.IsTrue(store.CompleteBuild(key, gen, Labels(1)));
            Assert.Greater(store.Version, v1, "a committed CompleteBuild bumps (labels changed)");

            long v2 = store.Version;
            store.Release(key, transferredToCache: true);
            Assert.Greater(store.Version, v2, "Release bumps (an active tile left the set)");

            long v3 = store.Version;
            store.Restore(key);
            Assert.Greater(store.Version, v3, "Restore bumps (labels re-entered the set)");

            long v4 = store.Version;
            store.Clear();
            Assert.Greater(store.Version, v4, "Clear bumps (restyle purge)");
        }

        // ── A SUPERSEDED CompleteBuild (returns false) must NOT bump — it changes nothing. ──
        [Test]
        public void Version_SupersededCompleteBuild_DoesNotBump()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            int gen1 = store.BeginBuild(key);
            store.BeginBuild(key); // gen2 supersedes gen1
            long v = store.Version;
            Assert.IsFalse(store.CompleteBuild(key, gen1, Labels(1)), "the stale build does not commit");
            Assert.AreEqual(v, store.Version, "…and does not bump the version");
        }

        // ── THE decisive tooth: a NO-OP ReconcileActiveSet (loaded set == active set) does NOT bump — otherwise
        //    the static-frame skip would never engage (B-1 would be silent dead code). ──
        [Test]
        public void Version_NoOpReconcile_DoesNotBump()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1));
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2));

            long v = store.Version;
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true); // nothing moves
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true);
            Assert.AreEqual(v, store.Version, "a stable-membership reconcile must not bump (else the skip never fires)");
        }

        // ── A reconcile that ACTUALLY moves a tile (one left cover) bumps. ──
        [Test]
        public void Version_ReconcileThatReleases_Bumps()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1);
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1));
            long v = store.Version;
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true); // 'a' left cover → released
            Assert.Greater(store.Version, v, "a reconcile that releases a tile bumps");
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
