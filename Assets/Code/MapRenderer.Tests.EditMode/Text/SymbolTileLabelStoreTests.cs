// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). No UnityEngine — the store is pure (source, tile) → labels bookkeeping.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
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

        // ═══ Stage 3: the SEAMLESS same-cell HOLD across a zoom step (the fixed-grid pivot) ═══

        // ── THE pivot-defining tooth: an ACTIVE point label at z=9 and a DEPARTING point label with the SAME text
        //    and IDENTICAL AnchorRender at z=8 key on the SAME fixed CanonicalGridMeters cell — so the departing
        //    copy is claim-skipped (a seamless hold across the zoom step), NOT emitted as a fading duplicate. Under
        //    the superseded per-zoom grid (MetersPerPixel(tileZoom)) the two bands' grids differ for the same
        //    anchor → different cells → the claim-skip would MISS → a transient fading duplicate on every zoom step.
        //    RED-verified by pointing the store's grid input back at CameraPoseMath.MetersPerPixel((int)(TileKey>>44)). ──
        [Test]
        public void DepartingCrossBand_ClaimSkipped_UnderFixedGrid()
        {
            // 5000 m is large enough that a per-zoom grid WOULD split z8 (~305.7 m/px) vs z9 (~152.9 m/px) for this
            // same anchor (round(5000/305.7)=16 ≠ round(5000/152.9)=33) — so the fixed grid is what makes the hold work.
            double3 anchor = new double3(5000.0, 0, 5000.0);
            var tileActive = new TileId { Z = 9, X = 256, Y = 256 };
            var tileDeparting = new TileId { Z = 8, X = 128, Y = 128 };
            var active = ParityPoint(anchor, 0, "Hold", null, 1, tileActive);
            var departing = ParityPoint(anchor, 0, "Hold", null, 2, tileDeparting);

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var kActive = new SymbolTileLabelStore.Key("src", tileActive);
            var kDeparting = new SymbolTileLabelStore.Key("src", tileDeparting);
            store.CompleteBuild(kActive, store.BeginBuild(kActive), new List<LabelInstance> { active });
            store.CompleteBuild(kDeparting, store.BeginBuild(kDeparting), new List<LabelInstance> { departing });

            // Release the z=8 tile (not in the loaded set) with grace → cached + departing; z=9 stays active.
            store.ReconcileActiveSet(Loaded(kActive), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "the z=8 tile is departing");

            var output = new List<LabelInstance>();
            store.CollectInto(output, quantizeMeters: 1.0, out int activeCount); // gate ON (magnitude ignored)
            Assert.AreEqual(1, output.Count, "the departing copy shares the active cell (fixed grid) → claim-skipped, no fading duplicate");
            Assert.AreEqual(1, activeCount, "…and nothing is appended after the active split");
            Assert.AreSame(active, output[0], "the surviving copy is the active (z=9) one");
        }

        // ── Companion sanity: the same-band hold (both z=9, co-located) — a same-z re-tiling never changed grids,
        //    so this held even under the superseded per-zoom design; it pins the general claim-skip. ──
        [Test]
        public void DepartingSameBand_ClaimSkipped()
        {
            double3 anchor = new double3(5000.0, 0, 5000.0);
            var tileActive = new TileId { Z = 9, X = 256, Y = 256 };
            var tileDeparting = new TileId { Z = 9, X = 257, Y = 256 }; // same band, different tile
            var active = ParityPoint(anchor, 0, "Hold", null, 1, tileActive);
            var departing = ParityPoint(anchor, 0, "Hold", null, 2, tileDeparting);

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var kActive = new SymbolTileLabelStore.Key("src", tileActive);
            var kDeparting = new SymbolTileLabelStore.Key("src", tileDeparting);
            store.CompleteBuild(kActive, store.BeginBuild(kActive), new List<LabelInstance> { active });
            store.CompleteBuild(kDeparting, store.BeginBuild(kDeparting), new List<LabelInstance> { departing });
            store.ReconcileActiveSet(Loaded(kActive), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "the second z=9 tile is departing");

            var output = new List<LabelInstance>();
            store.CollectInto(output, quantizeMeters: 1.0, out int activeCount);
            Assert.AreEqual(1, output.Count, "co-located same-band departing copy is claim-skipped");
            Assert.AreEqual(1, activeCount, "…nothing appended after the split");
            Assert.AreSame(active, output[0], "the active copy survives");
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
                TileKey = LabelTileKey.Pack(tile),
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

        // ═══ Symbol-label perf Phase 1 / Stage 1 (design §4, §5 B): Block dispose lifecycle ═══
        //
        // SymbolTileLabelStore.Entry.Block is held ONLY as System.IDisposable (the engine-free constraint —
        // see this file's header + SymbolTileLabelStore's Entry doc); a fake counter stands in for the real
        // native-array-backed SymbolTileLabelBlock (Unity-only — see Tests.EditMode/Text/Placement's block
        // test for the REAL NativeArray lifetime).

        private sealed class FakeDisposableBlock : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        // ── (a) Commit-overwrite disposes exactly the OLD block, once — the new one stays live. ──
        [Test]
        public void Block_CommitOverwrite_DisposesOldBlockOnce()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            var oldBlock = new FakeDisposableBlock();
            var newBlock = new FakeDisposableBlock();

            store.CompleteBuild(key, store.BeginBuild(key), Labels(1), oldBlock);
            Assert.AreEqual(0, oldBlock.DisposeCount, "the first commit's block is not disposed yet");

            store.CompleteBuild(key, store.BeginBuild(key), Labels(2), newBlock); // a rebuild commits over it
            Assert.AreEqual(1, oldBlock.DisposeCount, "commit-overwrite disposes the OLD block exactly once");
            Assert.AreEqual(0, newBlock.DisposeCount, "the new block stays live — it is now the entry's block");
        }

        // ── A superseded build's block never lands — CompleteBuild disposes the CALLER's block on the
        //    non-commit path (the caller never re-owns it). ──
        [Test]
        public void Block_SupersededCommit_DisposesPassedInBlock()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            int gen1 = store.BeginBuild(key);
            store.BeginBuild(key); // a newer build takes the slot — gen1 is now stale

            var staleBlock = new FakeDisposableBlock();
            bool committed = store.CompleteBuild(key, gen1, Labels(1), staleBlock);
            Assert.IsFalse(committed, "the superseded build must not commit");
            Assert.AreEqual(1, staleBlock.DisposeCount, "…and its block is disposed — never leaked, never landed");
        }

        // ── (b) FIFO eviction over cap disposes the evicted (oldest-released) tile's block. ──
        [Test]
        public void Block_FifoEvictOverCap_DisposesEvictedBlock()
        {
            var store = new SymbolTileLabelStore(cacheCap: 2);
            var a = Key("src", 1); var b = Key("src", 2); var c = Key("src", 3);
            var blockA = new FakeDisposableBlock();
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1), blockA);
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2), new FakeDisposableBlock());
            store.CompleteBuild(c, store.BeginBuild(c), Labels(3), new FakeDisposableBlock());

            store.Release(a, true); // oldest released
            store.Release(b, true);
            Assert.AreEqual(0, blockA.DisposeCount, "still within the cap (2) — not evicted yet");

            store.Release(c, true); // over cap (2) → evicts a (the oldest)
            Assert.AreEqual(1, blockA.DisposeCount, "FIFO eviction disposes the evicted tile's block");
        }

        // ── (c) Release true-eviction (cache disabled / not built) disposes the block outright. Covers BOTH
        //    Release branches: the tile still active at release time, and a stale cached copy. ──
        [Test]
        public void Block_ReleaseWithoutCache_FromActive_DisposesBlock()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1), block);

            store.Release(key, transferredToCache: false); // true eviction — the tile was active
            Assert.AreEqual(1, block.DisposeCount, "a true eviction (from active) disposes the block");
        }

        [Test]
        public void Block_ReleaseWithoutCache_FromStaleCached_DisposesBlock()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1), block);
            store.Release(key, transferredToCache: true); // → cached side first

            store.Release(key, transferredToCache: false); // a stale cached copy → dropped (cache-disabled path)
            Assert.AreEqual(1, block.DisposeCount, "a true eviction (from a stale cached copy) disposes the block");
        }

        // ── (d) Clear disposes every ACTIVE and CACHED entry's block. ──
        [Test]
        public void Block_Clear_DisposesActiveAndCachedBlocks()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            var blockA = new FakeDisposableBlock(); var blockB = new FakeDisposableBlock();
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1), blockA);
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2), blockB);
            store.Release(b, true); // b cached, a stays active

            store.Clear();
            Assert.AreEqual(1, blockA.DisposeCount, "Clear disposes the active entry's block");
            Assert.AreEqual(1, blockB.DisposeCount, "Clear disposes the cached entry's block");
        }

        // ── NO dispose on a BeginBuild pull-to-active (stale-survives — the rebuild keeps the old block alive
        //    until its OWN commit, per Block_CommitOverwrite_DisposesOldBlockOnce above). ──
        [Test]
        public void Block_BeginBuildPull_DoesNotDisposeSurvivingBlock()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1), block);

            store.BeginBuild(key); // a rebuild starts (e.g. zoom re-fetch) — pulls the SAME entry active again
            Assert.AreEqual(0, block.DisposeCount, "BeginBuild's stale-survives pull must not dispose the block");
        }

        // ── NO dispose on a Restore cache-hit move (the entry — and its block — just moves back to active). ──
        [Test]
        public void Block_RestoreCacheHit_DoesNotDisposeBlock()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), Labels(1), block);
            store.Release(key, transferredToCache: true); // → cached, block kept warm

            store.Restore(key); // cache-hit move back to active
            Assert.AreEqual(0, block.DisposeCount, "a cache-hit Restore must not dispose the moved block");
        }

        // ═══ Stage 2 (labels-async-reconcile): interned-id dedup key parity vs an independent STRING oracle ═══
        //
        // The invariant tooth: swapping the dedup key from a string CrossTileLabelKey to an integer DedupKey
        // (via the store's LabelTextIntern) must produce BYTE-IDENTICAL winners AND emission ORDER (and the
        // plan-aware (BlockId, LocalIndex, IsDeparting) arrays). The oracle below reimplements the dedup with
        // the STRING CrossTileLabelKey.For — NOT the code under test — so a divergence between the interned
        // path and the string partition fails element-by-element.

        private const double ParityQ = 50.0;

        private static LabelInstance ParityPoint(double3 anchor, int layer, string text, string icon, int feature, TileId tile)
            => new LabelInstance
            {
                Placement = SymbolPlacement.Point,
                AnchorRender = anchor,
                MaterialIndex = layer,
                Text = text,
                IconImage = icon,
                FeatureIndex = feature,
                TileKey = LabelTileKey.Pack(tile),
            };

        private static LabelInstance ParityCurved(string text, int feature, TileId tile)
            => new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                MaterialIndex = 0,
                Text = text,
                FeatureIndex = feature,
                TileKey = LabelTileKey.Pack(tile),
            };

        private struct OracleResult
        {
            public List<LabelInstance> Output;
            public int ActiveCount;
            public List<int> BlockId;
            public List<int> LocalIndex;
            public List<byte> IsDeparting;
        }

        // Independent string-keyed reimplementation of the store's dedup — the same scan order, the same
        // finest-zoom rule, the same per-tile blockId assignment — but keyed on the STRING CrossTileLabelKey.
        // activeTiles/departingTiles are in the SAME order the store enumerates _active / _departing.
        // Stage 3: the oracle grids on the fixed CrossTileLabelKey.CanonicalGridMeters (mirroring the store, which
        // no longer takes a caller grid) — so parity holds by construction, still proving interned-key ==
        // string-key under the SAME grid. `q` is passed through unchanged as the store's dedup GATE (magnitude
        // irrelevant now); it is not the oracle grid.
        private static OracleResult Oracle(
            List<List<LabelInstance>> activeTiles, List<List<LabelInstance>> departingTiles, double q)
        {
            var output = new List<LabelInstance>();
            var blockIds = new List<int>();
            var localIndices = new List<int>();
            var isDeparting = new List<byte>();
            var dedup = new Dictionary<CrossTileLabelKey, (LabelInstance label, int z, long tileKey, int blockId, int localIndex)>();
            int nextBlock = 0;

            foreach (List<LabelInstance> tile in activeTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    LabelInstance label = tile[i];
                    if (label == null) continue;
                    if (label.Placement != SymbolPlacement.Point)
                    {
                        output.Add(label); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(0);
                        continue;
                    }
                    var key = CrossTileLabelKey.For(label.AnchorRender, label.MaterialIndex, label.Text, label.IconImage, CrossTileLabelKey.CanonicalGridMeters);
                    int z = (int)(label.TileKey >> 44);
                    if (!dedup.TryGetValue(key, out var cur) || z > cur.z || (z == cur.z && label.TileKey < cur.tileKey))
                        dedup[key] = (label, z, label.TileKey, myBlock, i);
                }
            }
            foreach (var kv in dedup)
            {
                output.Add(kv.Value.label); blockIds.Add(kv.Value.blockId); localIndices.Add(kv.Value.localIndex); isDeparting.Add(0);
            }
            int activeCount = output.Count;

            foreach (List<LabelInstance> tile in departingTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    LabelInstance label = tile[i];
                    if (label == null) continue;
                    if (label.Placement == SymbolPlacement.Point)
                    {
                        var key = CrossTileLabelKey.For(label.AnchorRender, label.MaterialIndex, label.Text, label.IconImage, CrossTileLabelKey.CanonicalGridMeters);
                        if (dedup.ContainsKey(key)) continue;
                        dedup[key] = (label, 0, label.TileKey, myBlock, i);
                    }
                    output.Add(label); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(1);
                }
            }

            return new OracleResult
            {
                Output = output, ActiveCount = activeCount,
                BlockId = blockIds, LocalIndex = localIndices, IsDeparting = isDeparting,
            };
        }

        // ── THE tooth: a multi-tile fixture exercising every dedup path, asserted element-by-element (winners
        //    AND order AND the plan arrays) against the independent string oracle. RED-verified by making
        //    LabelTextIntern.Intern return a constant (collapsing distinct text/icons → spurious merges). ──
        [Test]
        public void InternedDedup_MatchesStringOracle_MultiTile_DuplicateTextAcrossTiles()
        {
            // Cells: each coordinate is a multiple of 4 (a CanonicalGridMeters=4 cell centre), thousands of metres
            // apart, so they stay in distinct fixed-grid cells and ≤1 m of jitter stays in-cell.
            double3 cellShared = new double3(ParityQ * 100.0, 0, ParityQ * 100.0);
            double3 cellB = new double3(ParityQ * 200.0, 0, ParityQ * 200.0);
            double3 cellIcon = new double3(ParityQ * 300.0, 0, ParityQ * 300.0);
            double3 cellUnique = new double3(ParityQ * 400.0, 0, ParityQ * 400.0);

            var tileP = new TileId { Z = 10, X = 500, Y = 400 };   // parent
            var tileC = new TileId { Z = 11, X = 1000, Y = 800 };  // child of P
            var tileM = new TileId { Z = 12, X = 3, Y = 4 };       // the "many kinds" tile
            var tileDep = new TileId { Z = 11, X = 1000, Y = 801 }; // departing

            // (a) same text + same cell across two tiles at different z → merge, finest z (child) wins. The 1 m
            // jitter is well inside the 4 m cell (unlike +2 m, which lands on the cell boundary at 5002).
            var parentShared = ParityPoint(cellShared + new double3(1, 0, 1), 0, "Shared", null, 1, tileP);
            var childShared = ParityPoint(cellShared, 0, "Shared", null, 2, tileC);
            // (d) a curved label rides through undeduped; (b) distinct text same cell; (c) distinct icons co-located.
            var curved = ParityCurved("Road", 3, tileM);
            var bAlpha = ParityPoint(cellB, 0, "Alpha", null, 4, tileM);
            var bBeta = ParityPoint(cellB, 0, "Beta", null, 5, tileM);         // distinct text, same cell → no merge
            var icoA = ParityPoint(cellIcon, 0, null, "ico-a", 6, tileM);
            var icoB = ParityPoint(cellIcon, 0, null, "ico-b", 7, tileM);       // distinct icon, same cell → no merge
            // (e) a departing entry: one label shares the active winner's identity (skipped), one is unique (appended).
            var depShared = ParityPoint(cellShared, 0, "Shared", null, 8, tileDep); // claimed by active → skipped
            var depUnique = ParityPoint(cellUnique, 0, "Unique", null, 9, tileDep); // not claimed → appended departing

            var pLabels = new List<LabelInstance> { parentShared };
            var cLabels = new List<LabelInstance> { childShared };
            var mLabels = new List<LabelInstance> { curved, bAlpha, bBeta, icoA, icoB };
            var depLabels = new List<LabelInstance> { depShared, depUnique };

            var store = new SymbolTileLabelStore(cacheCap: 16);
            var kP = new SymbolTileLabelStore.Key("src", tileP);
            var kC = new SymbolTileLabelStore.Key("src", tileC);
            var kM = new SymbolTileLabelStore.Key("src", tileM);
            var kDep = new SymbolTileLabelStore.Key("src", tileDep);

            // BeginBuild order fixes _active insertion (enumeration) order: P, C, M, Dep.
            store.CompleteBuild(kP, store.BeginBuild(kP), pLabels);
            store.CompleteBuild(kC, store.BeginBuild(kC), cLabels);
            store.CompleteBuild(kM, store.BeginBuild(kM), mLabels);
            store.CompleteBuild(kDep, store.BeginBuild(kDep), depLabels);
            // Release Dep (not in the loaded set) with grace → cached + departing; P/C/M stay active in order.
            store.ReconcileActiveSet(
                new List<SymbolTileLabelStore.Key> { kP, kC, kM },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(3, store.ActiveTileCount, "P, C, M active");
            Assert.AreEqual(1, store.DepartingTileCount, "Dep is departing");

            OracleResult oracle = Oracle(
                new List<List<LabelInstance>> { pLabels, cLabels, mLabels },
                new List<List<LabelInstance>> { depLabels },
                ParityQ);

            // Precondition: the fixture actually exercises the paths (not a degenerate all-merge/all-distinct set).
            Assert.IsTrue(oracle.Output.Contains(childShared), "sanity: the child (finest) copy is the shared-cell winner");
            Assert.IsFalse(oracle.Output.Contains(parentShared), "the coarser parent copy loses the merge");
            Assert.IsFalse(oracle.Output.Contains(depShared), "the departing copy of the shared identity is skipped");
            Assert.IsTrue(oracle.Output.Contains(bAlpha) && oracle.Output.Contains(bBeta), "distinct text both survive");
            Assert.IsTrue(oracle.Output.Contains(icoA) && oracle.Output.Contains(icoB), "distinct icons both survive");

            // ── Plain overload: winners + ORDER + activeCount, element-by-element vs the oracle. ──
            var plainOut = new List<LabelInstance>();
            store.CollectInto(plainOut, ParityQ, out int plainActive);
            Assert.AreEqual(oracle.Output.Count, plainOut.Count, "same total emitted count as the string oracle");
            Assert.AreEqual(oracle.ActiveCount, plainActive, "same active/departing split");
            for (int i = 0; i < oracle.Output.Count; i++)
                Assert.AreSame(oracle.Output[i], plainOut[i], $"plain winner+order mismatch at index {i}");

            // ── Plan-aware overload: same output+order PLUS the (BlockId, LocalIndex, IsDeparting) arrays. ──
            var po = new List<LabelInstance>();
            var pBlock = new List<int>();
            var pLocal = new List<int>();
            var pDep = new List<byte>();
            store.CollectInto(po, pBlock, pLocal, pDep, ParityQ, out int planActive);
            Assert.AreEqual(oracle.Output.Count, po.Count, "plan-aware total count matches the oracle");
            Assert.AreEqual(oracle.ActiveCount, planActive, "plan-aware active split matches the oracle");
            for (int i = 0; i < oracle.Output.Count; i++)
            {
                Assert.AreSame(oracle.Output[i], po[i], $"plan-aware winner+order mismatch at index {i}");
                Assert.AreEqual(oracle.BlockId[i], pBlock[i], $"blockId mismatch at index {i}");
                Assert.AreEqual(oracle.LocalIndex[i], pLocal[i], $"localIndex mismatch at index {i}");
                Assert.AreEqual(oracle.IsDeparting[i], pDep[i], $"isDeparting mismatch at index {i}");
            }
        }

        // ═══ Stage 4a (labels-async-reconcile): collect-generation invalidation completeness + purity ═══
        //
        // The memo (SymbolLabelSubsystem.CurrentBatch reuses its collected buffers when CollectGeneration is
        // unchanged) is only correct if EVERY collect-relevant mutation bumps the generation (a missed bump = stale
        // labels on screen) AND a stable cover / no-op mutation does NOT bump (else the memo never fires). These pin
        // both directions + the purity premise the reuse rests on. Each Y case is its own assertion — omit that one
        // MarkCollectDirty() and the case goes RED (the parameterized completeness test IS the RED harness).

        // The Y rows of the invalidation map (§1) — each MUST bump the collect generation.
        public enum BumpCase
        {
            BeginBuildNew,               // §1 #1 — first-seen tile pulled active
            BeginBuildRefetchCached,     // §1 #1 — re-fetch of a cached/departing tile (labels move to active)
            CompleteBuildCommit,         // §1 #2 — label content lands
            ReleaseTrue,                 // §1 #3 — active tile released to warm cache
            ReleaseFalseTrueEvict,       // §1 #3b — active tile true-evicted (cache disabled)
            ReleaseFalseStaleCachedDrop, // §1 #3c — a stale cached copy dropped
            RestoreReal,                 // §1 #4 — cached → active (cache hit)
            PurgeExpiredDeparting,       // §1 #7 — an expired departing key removed
            Clear,                       // §1 #8 — whole set → empty
        }

        [Test]
        public void CollectGeneration_BumpsOnEveryMutation([Values] BumpCase mutation)
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            int g0;
            switch (mutation)
            {
                case BumpCase.BeginBuildNew:
                    g0 = store.CollectGeneration;
                    store.BeginBuild(key);
                    break;
                case BumpCase.BeginBuildRefetchCached:
                    store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
                    store.Release(key, transferredToCache: true); // → cached
                    g0 = store.CollectGeneration;
                    store.BeginBuild(key); // re-fetch pulls the cached tile (with its labels) back onto the active side
                    break;
                case BumpCase.CompleteBuildCommit:
                    int gen = store.BeginBuild(key);
                    g0 = store.CollectGeneration;
                    store.CompleteBuild(key, gen, Labels(1)); // commit path
                    break;
                case BumpCase.ReleaseTrue:
                    store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: true);
                    break;
                case BumpCase.ReleaseFalseTrueEvict:
                    store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: false); // active branch, true eviction
                    break;
                case BumpCase.ReleaseFalseStaleCachedDrop:
                    store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
                    store.Release(key, transferredToCache: true); // → cached (stale copy)
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: false); // stale-cached-drop branch
                    break;
                case BumpCase.RestoreReal:
                    store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
                    store.Release(key, transferredToCache: true); // → cached
                    g0 = store.CollectGeneration;
                    store.Restore(key); // cache hit
                    break;
                case BumpCase.PurgeExpiredDeparting:
                    store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
                    store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
                    Assert.AreEqual(1, store.DepartingTileCount, "precondition: the tile is departing");
                    g0 = store.CollectGeneration;
                    // Second reconcile past the grace window: nothing releases/restores (active empty, loaded empty), so
                    // the ONLY collect-relevant change is PurgeExpiredDeparting removing the expired departing key.
                    store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 11.0, departingGraceSeconds: 0.5);
                    Assert.AreEqual(0, store.DepartingTileCount, "precondition: the departing key was purged");
                    break;
                case BumpCase.Clear:
                    store.CompleteBuild(key, store.BeginBuild(key), Labels(1));
                    g0 = store.CollectGeneration;
                    store.Clear();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.AreNotEqual(g0, store.CollectGeneration,
                $"{mutation} changes the collected set → it MUST bump the collect generation (a missed bump = stale labels)");
        }

        // The N rows of §1 — a no-op / superseded mutation must NOT bump (else a stable cover dirties every frame and
        // the memo never fires). ReconcileActiveSet idempotency is elevated to its own headline tooth below.
        public enum NoBumpCase
        {
            CompleteBuildSuperseded, // §1 #2b — a stale build's commit is discarded, no state change
            RestoreNoOp,             // §1 #4b — nothing cached to restore
            ReleaseNoOpEdge,         // §1 #3d — key neither active nor cached, transferredToCache=true
        }

        [Test]
        public void CollectGeneration_HoldsOnNoOpMutation([Values] NoBumpCase mutation)
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = Key("src", 1);
            int g0;
            switch (mutation)
            {
                case NoBumpCase.CompleteBuildSuperseded:
                    int gen1 = store.BeginBuild(key);
                    store.BeginBuild(key); // a newer build supersedes gen1
                    g0 = store.CollectGeneration;
                    Assert.IsFalse(store.CompleteBuild(key, gen1, Labels(1)), "precondition: the stale build is discarded");
                    break;
                case NoBumpCase.RestoreNoOp:
                    g0 = store.CollectGeneration;
                    store.Restore(key); // nothing cached → no move
                    break;
                case NoBumpCase.ReleaseNoOpEdge:
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: true); // not active, transferredToCache=true → neither branch
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.AreEqual(g0, store.CollectGeneration,
                $"{mutation} changes no collected state → it must NOT bump (a stable cover must not dirty the memo)");
        }

        // ── THE headline tooth: ReconcileActiveSet runs EVERY frame; on a stable loaded set it moves nothing and MUST
        //    NOT bump — else the memo dirties every frame and the entire CPU win silently evaporates (a regression here
        //    leaves the render byte-identical, caught by nothing else but this + the subsystem's recompute counter). ──
        [Test]
        public void ReconcileActiveSet_IdempotentCall_DoesNotBumpGeneration()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1));
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2));

            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            int g0 = store.CollectGeneration;
            // Same loaded set, a later `now` still inside any grace (nothing to purge — nothing is departing anyway).
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true, nowSeconds: 10.2, departingGraceSeconds: 0.5);
            Assert.AreEqual(g0, store.CollectGeneration,
                "a second reconcile with the SAME loaded set moves nothing → the collect generation must be unchanged");
            Assert.AreEqual(2, store.ActiveTileCount, "…and both tiles stay active");
        }

        // ── CollectInto is a PURE function of the tile set (Stage 3): two back-to-back collects with no mutation
        //    between produce element-identical outputs (+ reference-identical OrderedBlocks) — the premise the memo's
        //    clean-frame reuse rests on (gen-unchanged ⇒ state-unchanged ⇒ collect-identical ⇒ safe to reuse). ──
        [Test]
        public void CollectInto_IsPureFunction_TwoCallsNoMutation_ElementIdentical()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2); var dep = Key("src", 3);
            var blockA = new FakeDisposableBlock(); var blockB = new FakeDisposableBlock(); var blockDep = new FakeDisposableBlock();
            store.CompleteBuild(a, store.BeginBuild(a), Labels(1), blockA);
            store.CompleteBuild(b, store.BeginBuild(b), Labels(2), blockB);
            store.CompleteBuild(dep, store.BeginBuild(dep), Labels(3), blockDep);
            // dep leaves cover with grace → departing (exercises the AppendDeparting branch too); a/b stay active.
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);

            var out1 = new List<LabelInstance>(); var blk1 = new List<int>(); var loc1 = new List<int>(); var d1 = new List<byte>();
            store.CollectInto(out1, blk1, loc1, d1, ParityQ, out int active1);
            var blocks1 = new List<IDisposable>(store.OrderedBlocks); // snapshot before the second collect refills it

            var out2 = new List<LabelInstance>(); var blk2 = new List<int>(); var loc2 = new List<int>(); var d2 = new List<byte>();
            store.CollectInto(out2, blk2, loc2, d2, ParityQ, out int active2);

            Assert.AreEqual(active1, active2, "same active split");
            Assert.AreEqual(out1.Count, out2.Count, "same emitted count");
            Assert.Greater(out1.Count, 0, "sanity: the fixture actually emits labels");
            for (int i = 0; i < out1.Count; i++)
            {
                Assert.AreSame(out1[i], out2[i], $"winner+order identical at {i}");
                Assert.AreEqual(blk1[i], blk2[i], $"blockId identical at {i}");
                Assert.AreEqual(loc1[i], loc2[i], $"localIndex identical at {i}");
                Assert.AreEqual(d1[i], d2[i], $"isDeparting identical at {i}");
            }
            Assert.AreEqual(blocks1.Count, store.OrderedBlocks.Count, "OrderedBlocks count identical");
            for (int i = 0; i < blocks1.Count; i++)
                Assert.AreSame(blocks1[i], store.OrderedBlocks[i], $"OrderedBlocks[{i}] reference-identical");
        }
    }
}
