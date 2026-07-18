// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). No UnityEngine — the store is pure (source, tile) → labels bookkeeping.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
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

        // ═══ Retain-as-departing: a tile leaving cover keeps its labels COLLECTED (fading) for a grace window ═══

        // ── A released tile is stamped departing and STILL collected (so its labels fade out, not pop) until the
        //    grace window elapses and a later reconcile purges it — the label staying warm on the cached side. ──
        [Test]
        public void Departing_ReleaseWithGrace_CollectedThenPurgedAfterWindow()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1));

            // Leaves cover at t=10 with a 0.5s grace → kept warm AND stamped departing.
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "the released tile is departing");
            Assert.AreEqual(0, store.ActiveTileCount, "…and no longer active");
            List<LabelInstance> during = Collect(store);
            Assert.AreEqual(1, during.Count, "a departing tile is STILL collected (its labels fade out, not pop)");
            Assert.AreEqual(1, during[0].FeatureIndex, "…and they are its own labels");

            // A reconcile still inside the window keeps it departing + collected.
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.3, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "still within the grace window");
            Assert.AreEqual(1, Collect(store).Count, "…still collected");

            // Past the window → purged. The labels stay WARM (a cache hit still restores them) but are no longer
            // collected as departing (by now they have fully faded, so this is not a pop).
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.6, departingGraceSeconds: 0.5);
            Assert.AreEqual(0, store.DepartingTileCount, "grace elapsed → purged");
            Assert.AreEqual(0, Collect(store).Count, "…no longer collected");
            Assert.AreEqual(1, store.CachedTileCount, "but still kept warm for a cache-hit re-entry");
        }

        // ── A tile that re-enters cover WITHIN the grace window is restored to active (fades back in), not left
        //    departing — the RemoveCached chokepoint clears the stamp (the departing ⊆ cached invariant). ──
        [Test]
        public void Departing_ReEntryWithinGrace_RestoredActive()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "departing after leaving cover");

            store.ReconcileActiveSet(Loaded(key), keepWarmOnRelease: true, nowSeconds: 10.2, departingGraceSeconds: 0.5);
            Assert.AreEqual(0, store.DepartingTileCount, "re-entry within grace clears the departing stamp");
            Assert.AreEqual(1, store.ActiveTileCount, "…and the tile is active again");
            Assert.AreEqual(1, Collect(store).Count, "…rendered as a normal active label (fades back in)");
        }

        // ── With no grace window (the default / cache-disabled path) a release retains nothing — pre-fade behaviour. ──
        [Test]
        public void Departing_GraceZero_NoRetention()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true); // grace defaults to 0 → feature off
            Assert.AreEqual(0, store.DepartingTileCount, "grace 0 ⇒ nothing retained");
            Assert.AreEqual(0, Collect(store).Count, "a released tile is not collected");
        }

        // ── CollectInto's out-activeCount splits active labels (first) from departing labels (appended last) — the
        //    split the batch builder reads to flag which records fade out. ──
        [Test]
        public void Departing_CollectInto_SplitsActiveFromDeparting()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1));
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2));
            // b leaves cover with grace → departing; a stays active.
            store.ReconcileActiveSet(Loaded(a), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);

            var output = new List<LabelInstance>();
            store.CollectInto(output, quantizeMeters: 0.0, out int activeCount);
            Assert.AreEqual(2, output.Count, "both the active and the departing label are collected");
            Assert.AreEqual(1, activeCount, "exactly one is active; the departing come after the split");
            Assert.AreEqual(1, output[0].FeatureIndex, "active label first");
            Assert.AreEqual(2, output[activeCount].FeatureIndex, "departing label after the split");
        }

        // ── A departing POINT label whose cross-tile identity is already shown by an ACTIVE label is NOT collected
        //    twice (no ghost fade under it) — the multi-source / dedup-winner case the per-record flag must handle. ──
        [Test]
        public void Departing_ClaimedByActiveLabel_NotDoubleCollected()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            // Same anchor (0)/material (0)/text → the SAME cross-tile identity, so the active copy claims it.
            var la = new List<LabelInstance> { new LabelInstance { FeatureIndex = 1, Text = "x" } };
            var lb = new List<LabelInstance> { new LabelInstance { FeatureIndex = 2, Text = "x" } };
            store.CompleteBuild(a, store.BeginBuild(a), la);
            store.CompleteBuild(b, store.BeginBuild(b), lb);
            store.ReconcileActiveSet(Loaded(a), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "b is departing");

            var output = new List<LabelInstance>();
            store.CollectInto(output, quantizeMeters: 1.0, out int activeCount); // dedup ON
            Assert.AreEqual(1, output.Count, "the active copy shows; the departing twin is skipped (no double-draw)");
            Assert.AreEqual(1, activeCount, "…and it is the active one (nothing appended after the split)");
            Assert.AreEqual(1, output[0].FeatureIndex, "the surviving label is the active tile's");
        }

        // ═══ I6: icon cross-tile identity dedup (the I5b-deferred gap this stage closes) ═══

        // A point label pinned to a fixed co-located anchor (double3.zero for every call — "co-located" needs no
        // coordinate math since every test here only varies text/iconImage/tile) — mirrors CrossTileIdentityTests'
        // PointLabel helper, extended with an iconImage param.
        private static LabelInstance IconOrTextLabel(int layer, string text, string iconImage, int feature, TileId tile)
            => new LabelInstance
            {
                Placement = SymbolPlacement.Point,
                MaterialIndex = layer,
                Text = text,
                IconImage = iconImage,
                FeatureIndex = feature,
                TileKey = SymbolFeatureExtractor.PackTileKey(tile),
            };

        private static List<LabelInstance> CollectQuantized(SymbolTileLabelStore store, double q)
        {
            var output = new List<LabelInstance>();
            store.CollectInto(output, q);
            return output;
        }

        // ── ★ RED pre-fix: two co-located icons (same anchor cell + layer, text=null, DISTINCT icon-image) used
        //    to collide into one (label.Text == null for both ⇒ the pre-I6 4-arg key ignored the icon). Now both
        //    survive CollectInto. ──
        [Test]
        public void IconDedup_DistinctIconImage_SameCellAndLayer_BothSurvive()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };
            var a = IconOrTextLabel(0, null, "sprite-a", 1, tile);
            var b = IconOrTextLabel(0, null, "sprite-b", 2, tile);

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = new SymbolTileLabelStore.Key("src", tile);
            store.CompleteBuild(key, store.BeginBuild(key), new List<LabelInstance> { a, b });

            Assert.AreEqual(2, CollectQuantized(store, q).Count, "distinct icon-image at the same cell are NOT merged");
        }

        // ── Same icon-image in a parent + child tile still dedups to ONE (the seamless-swap property icons
        //    now share with text). ──
        [Test]
        public void IconDedup_SameIconImage_ParentAndChildTile_DedupsToOne()
        {
            const double q = 50.0;
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };
            var parentLabel = IconOrTextLabel(0, null, "sprite-a", 1, parent);
            var childLabel = IconOrTextLabel(0, null, "sprite-a", 2, child);

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var kParent = new SymbolTileLabelStore.Key("src", parent);
            var kChild = new SymbolTileLabelStore.Key("src", child);
            store.CompleteBuild(kParent, store.BeginBuild(kParent), new List<LabelInstance> { parentLabel });
            store.CompleteBuild(kChild, store.BeginBuild(kChild), new List<LabelInstance> { childLabel });

            List<LabelInstance> output = CollectQuantized(store, q);
            Assert.AreEqual(1, output.Count, "the same icon in a parent+child tile collapses to one");
            Assert.AreSame(childLabel, output[0], "the finest (child) tile's label wins");
        }

        // ── Byte-identical control: the pre-I6 text-dedup case, unaffected by the icon change — a text label at
        //    the same cell/layer in a parent+child tile still dedups to one, finest-zoom wins. ──
        [Test]
        public void TextDedup_SameTextAndCell_ParentAndChildTile_DedupsToOne_Unaffected()
        {
            const double q = 50.0;
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };
            var parentLabel = IconOrTextLabel(0, "Metropolis", null, 1, parent);
            var childLabel = IconOrTextLabel(0, "Metropolis", null, 2, child);

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var kParent = new SymbolTileLabelStore.Key("src", parent);
            var kChild = new SymbolTileLabelStore.Key("src", child);
            store.CompleteBuild(kParent, store.BeginBuild(kParent), new List<LabelInstance> { parentLabel });
            store.CompleteBuild(kChild, store.BeginBuild(kChild), new List<LabelInstance> { childLabel });

            List<LabelInstance> output = CollectQuantized(store, q);
            Assert.AreEqual(1, output.Count, "text dedup is unchanged by I6");
            Assert.AreSame(childLabel, output[0], "the finest (child) tile's label wins");
        }
    }
}
