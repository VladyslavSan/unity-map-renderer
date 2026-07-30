// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). No UnityEngine — the reconciler + snapshot carriers + the store's pin guard are pure.

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
    /// Stage 4b (labels-async-reconcile): the off-main <see cref="SymbolLabelReconciler"/> + the store's native
    /// pin guard (SPEC A). T1 proves the extracted worker is BYTE-IDENTICAL to an independent string oracle; T2
    /// pins the disposal-bumps-generation edge; T3 the reused-result SHRINK; T4 the per-site defer/flush TIMING;
    /// T5 the cross-snapshot refcount OVERLAP. T3/T4/T5 are RED-verified against the un-guarded code.
    /// </summary>
    [TestFixture]
    public class SymbolLabelReconcilerTests
    {
        private const double ParityQ = 50.0;

        private sealed class FakeBlock : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private static SymbolTileLabelStore.Key Key(TileId t) => new SymbolTileLabelStore.Key("src", t);

        // §10 D8/D9: pairRole/pairId default to None/0 — every existing call site (unpaired labels) is unaffected.
        private static LabelInstance Point(double3 anchor, int layer, string text, string icon, int feature, TileId tile,
            LabelPairRole pairRole = LabelPairRole.None, int pairId = 0)
            => new LabelInstance
            {
                Placement = SymbolPlacement.Point, AnchorRender = anchor, MaterialIndex = layer,
                Text = text, IconImage = icon, FeatureIndex = feature, TileKey = SymbolFeatureExtractor.PackTileKey(tile),
                PairRole = pairRole, PairId = pairId,
            };

        private static LabelInstance Curved(string text, int feature, TileId tile)
            => new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter, MaterialIndex = 0, Text = text,
                FeatureIndex = feature, TileKey = SymbolFeatureExtractor.PackTileKey(tile),
            };

        // Independent STRING-keyed oracle — the SAME scan order + finest-zoom rule + per-tile blockId assignment
        // as the reconciler, but keyed on the string CrossTileLabelKey (NOT the code under test). activeTiles /
        // departingTiles are in the store's _active / _departing enumeration order.
        private struct OracleOut
        {
            public List<LabelInstance> Output; public int ActiveCount;
            public List<int> BlockId; public List<int> LocalIndex; public List<byte> IsDeparting;
        }

        private static OracleOut Oracle(List<List<LabelInstance>> activeTiles, List<List<LabelInstance>> departingTiles)
        {
            var output = new List<LabelInstance>(); var blockIds = new List<int>();
            var localIndices = new List<int>(); var isDeparting = new List<byte>();
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
            return new OracleOut { Output = output, ActiveCount = activeCount, BlockId = blockIds, LocalIndex = localIndices, IsDeparting = isDeparting };
        }

        // ═══ T1: reconciler byte-identical (mixed curved / point / icon / departing) ═══

        [Test]
        public void Reconciler_Run_MatchesStringOracle_MultiTile()
        {
            double3 cellShared = new double3(ParityQ * 100.0, 0, ParityQ * 100.0);
            double3 cellB = new double3(ParityQ * 200.0, 0, ParityQ * 200.0);
            double3 cellIcon = new double3(ParityQ * 300.0, 0, ParityQ * 300.0);
            double3 cellUnique = new double3(ParityQ * 400.0, 0, ParityQ * 400.0);

            var tileP = new TileId { Z = 10, X = 500, Y = 400 };
            var tileC = new TileId { Z = 11, X = 1000, Y = 800 };
            var tileM = new TileId { Z = 12, X = 3, Y = 4 };
            var tileDep = new TileId { Z = 11, X = 1000, Y = 801 };

            var parentShared = Point(cellShared + new double3(1, 0, 1), 0, "Shared", null, 1, tileP);
            var childShared = Point(cellShared, 0, "Shared", null, 2, tileC);
            var curved = Curved("Road", 3, tileM);
            var bAlpha = Point(cellB, 0, "Alpha", null, 4, tileM);
            var bBeta = Point(cellB, 0, "Beta", null, 5, tileM);
            var icoA = Point(cellIcon, 0, null, "ico-a", 6, tileM);
            var icoB = Point(cellIcon, 0, null, "ico-b", 7, tileM);
            var depShared = Point(cellShared, 0, "Shared", null, 8, tileDep);
            var depUnique = Point(cellUnique, 0, "Unique", null, 9, tileDep);

            var pLabels = new List<LabelInstance> { parentShared };
            var cLabels = new List<LabelInstance> { childShared };
            var mLabels = new List<LabelInstance> { curved, bAlpha, bBeta, icoA, icoB };
            var depLabels = new List<LabelInstance> { depShared, depUnique };

            var store = new SymbolTileLabelStore(cacheCap: 16);
            store.CompleteBuild(Key(tileP), store.BeginBuild(Key(tileP)), pLabels);
            store.CompleteBuild(Key(tileC), store.BeginBuild(Key(tileC)), cLabels);
            store.CompleteBuild(Key(tileM), store.BeginBuild(Key(tileM)), mLabels);
            store.CompleteBuild(Key(tileDep), store.BeginBuild(Key(tileDep)), depLabels);
            store.ReconcileActiveSet(
                new List<SymbolTileLabelStore.Key> { Key(tileP), Key(tileC), Key(tileM) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(3, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolLabelSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolLabelReconciler();
            var result = new SymbolLabelReconcileResult();
            reconciler.Run(snapshot, result);

            OracleOut oracle = Oracle(
                new List<List<LabelInstance>> { pLabels, cLabels, mLabels },
                new List<List<LabelInstance>> { depLabels });

            Assert.IsTrue(oracle.Output.Contains(childShared) && !oracle.Output.Contains(parentShared),
                "sanity: the fixture exercises the finest-zoom merge");
            Assert.IsFalse(oracle.Output.Contains(depShared), "sanity: the departing twin of the shared identity is claim-skipped");
            Assert.IsTrue(oracle.Output.Contains(icoA) && oracle.Output.Contains(icoB), "sanity: distinct icons both survive");

            Assert.AreEqual(oracle.Output.Count, result.Output.Count, "same total emitted count");
            Assert.AreEqual(oracle.ActiveCount, result.ActiveCount, "same active/departing split");
            for (int i = 0; i < oracle.Output.Count; i++)
            {
                Assert.AreSame(oracle.Output[i], result.Output[i], $"winner+order mismatch at {i}");
                Assert.AreEqual(oracle.BlockId[i], result.BlockId[i], $"blockId mismatch at {i}");
                Assert.AreEqual(oracle.LocalIndex[i], result.LocalIndex[i], $"localIndex mismatch at {i}");
                Assert.AreEqual(oracle.IsDeparting[i], result.IsDeparting[i], $"isDeparting mismatch at {i}");
            }
        }

        // ═══ T2: a disposal-causing mutation (over-cap FIFO evict) bumps the collect generation ═══

        [Test]
        public void CollectGeneration_BumpsOnOverCapFifoEvict()
        {
            var store = new SymbolTileLabelStore(cacheCap: 1);
            var a = new TileId { Z = 5, X = 1, Y = 0 };
            var b = new TileId { Z = 5, X = 2, Y = 0 };
            store.CompleteBuild(Key(a), store.BeginBuild(Key(a)), new List<LabelInstance> { Point(default, 0, "a", null, 1, a) }, new FakeBlock());
            store.CompleteBuild(Key(b), store.BeginBuild(Key(b)), new List<LabelInstance> { Point(default, 0, "b", null, 2, b) }, new FakeBlock());

            store.Release(Key(a), transferredToCache: true); // → cached (cap 1)
            int g0 = store.CollectGeneration;
            store.Release(Key(b), transferredToCache: true); // over cap → evicts a (disposes its block) — must bump
            Assert.AreNotEqual(g0, store.CollectGeneration, "an over-cap FIFO evict changed the collected set → it must bump");
        }

        // ═══ T3: the reused RESULT (and index) SHRINKS — {A,B} → {B} → empty ═══
        // RED-verify: strip `result.Clear()` / `_dedup.Clear()` from Run → the reused result accumulates prior
        // runs' records → the counts stop shrinking → this fails.

        [Test]
        public void Reconciler_ReusedResult_Shrinks_AcrossRuns()
        {
            var a = new TileId { Z = 5, X = 1, Y = 0 };
            var b = new TileId { Z = 5, X = 2, Y = 0 };
            var la = new List<LabelInstance> { Point(new double3(1000, 0, 1000), 0, "A", null, 1, a) };
            var lb = new List<LabelInstance> { Point(new double3(2000, 0, 2000), 0, "B", null, 2, b) };

            var store = new SymbolTileLabelStore(cacheCap: 8);
            store.CompleteBuild(Key(a), store.BeginBuild(Key(a)), la);
            store.CompleteBuild(Key(b), store.BeginBuild(Key(b)), lb);

            var reconciler = new SymbolLabelReconciler();
            var result = new SymbolLabelReconcileResult();   // REUSED across all three runs
            var snapshot = new SymbolLabelSnapshot();        // REUSED across all three runs

            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(2, result.Output.Count, "{A,B} → two winners");

            store.Release(Key(a), transferredToCache: false); // drop A → active {B}
            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(1, result.Output.Count, "{B} → the reused result must SHRINK to one (no stale A)");
            Assert.AreSame(lb[0], result.Output[0], "…and the surviving winner is B");

            store.Release(Key(b), transferredToCache: false); // drop B → empty
            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(0, result.Output.Count, "empty set → the reused result must shrink to zero");
            Assert.AreEqual(0, result.BlockId.Count, "…and every parallel list too");
            Assert.AreEqual(0, result.OrderedBlocks.Count);
            Assert.AreEqual(0, result.ActiveCount);
        }

        // ═══ T4: per-site defer/flush TIMING — a pinned block's dispose is DEFERRED until ReleasePins ═══
        // RED-verify: revert DisposeOrDefer at the site back to a direct block?.Dispose() → the block disposes at
        // the site (DisposeCount == 1 while still pinned) → the "==0 while pinned" assertion fails.

        public enum DeferSite { CommitOverwrite, ReleaseActiveTrueEvict, ReleaseStaleCached, FifoEvict }

        [Test]
        public void DisposeOrDefer_DefersWhilePinned_FreesOnReleasePins([Values] DeferSite site)
        {
            var store = new SymbolTileLabelStore(cacheCap: site == DeferSite.FifoEvict ? 1 : 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var block = new FakeBlock();
            var labels = new List<LabelInstance> { Point(new double3(1000, 0, 1000), 0, "t", null, 1, t) };
            store.CompleteBuild(Key(t), store.BeginBuild(Key(t)), labels, block);

            // For the stale-cached site, the tile must be cached AND departing before capture (CaptureSnapshot only
            // pins active + departing tiles) — reconcile to an empty loaded set with grace stamps it departing.
            if (site == DeferSite.ReleaseStaleCached)
                store.ReconcileActiveSet(new List<SymbolTileLabelStore.Key>(), keepWarmOnRelease: true,
                    nowSeconds: 10.0, departingGraceSeconds: 1000.0);

            // Pin `block` by holding a live snapshot referencing it (as an active slice, or a departing one above).
            var snapshot = new SymbolLabelSnapshot();
            store.CaptureSnapshot(snapshot); // pins `block`
            Assert.AreEqual(0, block.DisposeCount, "captured/pinned — not disposed yet");

            switch (site)
            {
                case DeferSite.CommitOverwrite:
                    store.CompleteBuild(Key(t), store.BeginBuild(Key(t)), new List<LabelInstance> { Point(default, 0, "t2", null, 2, t) }, new FakeBlock());
                    break;
                case DeferSite.ReleaseActiveTrueEvict:
                    store.Release(Key(t), transferredToCache: false); // active → true evict
                    break;
                case DeferSite.ReleaseStaleCached:
                    store.Release(Key(t), transferredToCache: false); // stale cached copy → drop
                    break;
                case DeferSite.FifoEvict:
                    var u = new TileId { Z = 5, X = 2, Y = 0 };
                    store.Release(Key(t), transferredToCache: true); // t → cached (cap 1)
                    store.CompleteBuild(Key(u), store.BeginBuild(Key(u)), new List<LabelInstance> { Point(default, 0, "u", null, 3, u) }, new FakeBlock());
                    store.Release(Key(u), transferredToCache: true); // over cap → evicts t (disposes `block`)
                    break;
            }

            Assert.AreEqual(0, block.DisposeCount, $"{site}: the drop site must DEFER — the block is pinned by a live snapshot");
            store.ReleasePins(snapshot);
            Assert.AreEqual(1, block.DisposeCount, $"{site}: ReleasePins drops the pin to 0 → the deferred block frees exactly once");
        }

        // The restyle/teardown defensive flush: a block deferred while pinned, then Clear() called WITHOUT a prior
        // ReleasePins (an out-of-order teardown) must still free it — the _pendingDispose flush in Clear.
        // Clear must be SELF-SAFE: it must NEVER free a block a live snapshot still pins (still owes a ReleasePins),
        // even on an out-of-order teardown (no prior ReleasePins). A block deferred while pinned survives Clear and
        // is freed only when its snapshot is released — no UAF, no leak.
        // RED-verify: the un-fixed UNCONDITIONAL flush disposes the still-pinned block at Clear → the "still alive
        // after Clear" assertion fails (block freed while pinned).
        [Test]
        public void Clear_KeepsPinnedDeferredBlockAlive_FreedOnReleasePins()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var block = new FakeBlock();
            store.CompleteBuild(Key(t), store.BeginBuild(Key(t)), new List<LabelInstance> { Point(default, 0, "t", null, 1, t) }, block);

            var snapshot = new SymbolLabelSnapshot();
            store.CaptureSnapshot(snapshot);                 // pins `block`
            // Overwrite the entry → DisposeOrDefer sees it pinned → defers into _pendingDispose (not disposed).
            store.CompleteBuild(Key(t), store.BeginBuild(Key(t)), new List<LabelInstance> { Point(default, 0, "t2", null, 2, t) }, new FakeBlock());
            Assert.AreEqual(0, block.DisposeCount, "deferred while pinned");

            store.Clear(); // teardown WITHOUT ReleasePins — must NOT free a block the live snapshot still pins
            Assert.AreEqual(0, block.DisposeCount, "Clear must NOT dispose a still-pinned deferred block (self-safe against UAF)");

            store.ReleasePins(snapshot); // the snapshot leaves service → NOW the deferred, unpinned block frees once
            Assert.AreEqual(1, block.DisposeCount, "ReleasePins frees the (now-unpinned) deferred block exactly once — no leak");
        }

        // ═══ T5: cross-snapshot refcount OVERLAP — a block pinned by TWO snapshots survives one release ═══
        // RED-verify: make Pin a set-to-1 (no accumulation) → releasing the first snapshot drops it to 0 and
        // disposes the block prematurely (while the second snapshot still references it) → this fails.

        [Test]
        public void Pin_RefcountsAcrossTwoSnapshots_SurvivesSingleRelease()
        {
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var block = new FakeBlock();
            store.CompleteBuild(Key(t), store.BeginBuild(Key(t)), new List<LabelInstance> { Point(default, 0, "t", null, 1, t) }, block);

            var s1 = new SymbolLabelSnapshot(); store.CaptureSnapshot(s1); // pin #1
            var s2 = new SymbolLabelSnapshot(); store.CaptureSnapshot(s2); // pin #2 (count → 2)

            store.Release(Key(t), transferredToCache: false); // drop site → pinned (count 2) → deferred, not disposed
            Assert.AreEqual(0, block.DisposeCount, "pinned by two snapshots → deferred");

            store.ReleasePins(s1);
            Assert.AreEqual(0, block.DisposeCount, "one release drops the count 2→1 — the block SURVIVES (still referenced)");
            store.ReleasePins(s2);
            Assert.AreEqual(1, block.DisposeCount, "the second release drops 1→0 → freed exactly once");
        }

        // ═══ §10 D9 — P6: cross-tile identity, no Frankenstein pair ═══
        // Tile A (coarser) holds the COMPLETE pair; tile B (finer, SAME quantized cell) holds ONLY its icon
        // (its text never resolved there — the window D6 closed). A rider has no DedupKey of its own (D9), so
        // it can only ride with ITS OWN winning owner: since B's finer icon beats A's icon at the shared
        // (cell, layer, iconImage) key, A's rider is never emitted — not "text from A, icon from B".
        // Pre-fix (independent icon/text keys), this would show TWO tile keys — icon from B, text from A.

        [Test]
        public void CentredPair_CrossTile_NoFrankenstein_OrphanedRiderNeverSurvivesAloneAcrossTiles()
        {
            double3 cell = new double3(ParityQ * 500.0, 0, ParityQ * 500.0);
            var tileA = new TileId { Z = 13, X = 1000, Y = 800 };  // coarser — holds the complete pair
            var tileB = new TileId { Z = 14, X = 2000, Y = 1600 }; // finer — holds ONLY the icon

            LabelInstance aIcon = Point(cell, 0, null, "shield", 0, tileA, LabelPairRole.Owner, pairId: 0);
            LabelInstance aText = Point(cell, 0, "42", null, 1, tileA, LabelPairRole.Rider, pairId: 0);
            LabelInstance bIcon = Point(cell, 0, null, "shield", 0, tileB); // unpaired lone icon, no text at B

            var aLabels = new List<LabelInstance> { aIcon, aText };
            var bLabels = new List<LabelInstance> { bIcon };

            var store = new SymbolTileLabelStore(cacheCap: 16);
            store.CompleteBuild(Key(tileA), store.BeginBuild(Key(tileA)), aLabels);
            store.CompleteBuild(Key(tileB), store.BeginBuild(Key(tileB)), bLabels);
            store.ReconcileActiveSet(new List<SymbolTileLabelStore.Key> { Key(tileA), Key(tileB) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);

            var snapshot = new SymbolLabelSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolLabelReconciler();
            var result = new SymbolLabelReconcileResult();
            reconciler.Run(snapshot, result);

            Assert.AreEqual(1, result.Output.Count,
                "only the finer tile's icon survives — the coarser tile's rider (its owner lost the race) is never emitted alone");
            Assert.AreSame(bIcon, result.Output[0]);
        }

        // ═══ §10 D9 — P13: no orphan rider, the INTRA-tile departing case (reconciler step (b)) ═══

        [Test]
        public void CentredPair_Departing_ClaimSkippedOwnerTakesRiderWithIt_NoOrphan()
        {
            double3 cell = new double3(ParityQ * 600.0, 0, ParityQ * 600.0);
            var tileActive = new TileId { Z = 13, X = 10, Y = 10 };
            var tileDeparting = new TileId { Z = 13, X = 20, Y = 20 }; // SAME quantized cell, different tile id

            LabelInstance activeIcon = Point(cell, 0, null, "shield", 0, tileActive, LabelPairRole.Owner, pairId: 0);
            LabelInstance activeText = Point(cell, 0, "7", null, 1, tileActive, LabelPairRole.Rider, pairId: 0);
            LabelInstance depIcon = Point(cell, 0, null, "shield", 0, tileDeparting, LabelPairRole.Owner, pairId: 0);
            LabelInstance depText = Point(cell, 0, "7", null, 1, tileDeparting, LabelPairRole.Rider, pairId: 0);

            var activeLabels = new List<LabelInstance> { activeIcon, activeText };
            var depLabels = new List<LabelInstance> { depIcon, depText };

            var store = new SymbolTileLabelStore(cacheCap: 16);
            store.CompleteBuild(Key(tileActive), store.BeginBuild(Key(tileActive)), activeLabels);
            store.CompleteBuild(Key(tileDeparting), store.BeginBuild(Key(tileDeparting)), depLabels);
            // tileDeparting is never in the active set → it leaves cover, kept warm and departing.
            store.ReconcileActiveSet(new List<SymbolTileLabelStore.Key> { Key(tileActive) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);
            Assert.AreEqual(1, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolLabelSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolLabelReconciler();
            var result = new SymbolLabelReconcileResult();
            reconciler.Run(snapshot, result);

            long tileKeyDeparting = SymbolFeatureExtractor.PackTileKey(tileDeparting);

            Assert.AreEqual(2, result.Output.Count, "exactly the active tile's 2 labels — the departing pair drops together");
            Assert.AreEqual(2, result.ActiveCount);
            foreach (LabelInstance l in result.Output)
                Assert.AreNotEqual(tileKeyDeparting, l.TileKey, "no label may carry the claim-skipped departing tile's key");

            // (ii) the general structural invariant over the WHOLE output: every Rider is immediately preceded
            // by its matching Owner (same PairId/TileKey/MaterialIndex).
            for (int i = 0; i < result.Output.Count; i++)
            {
                LabelInstance l = result.Output[i];
                if (l.PairRole != LabelPairRole.Rider) continue;
                Assert.Greater(i, 0, "a Rider can never be the first output entry");
                LabelInstance prev = result.Output[i - 1];
                Assert.AreEqual(LabelPairRole.Owner, prev.PairRole, $"output[{i - 1}] must be the matching Owner");
                Assert.AreEqual(l.PairId, prev.PairId);
                Assert.AreEqual(l.TileKey, prev.TileKey);
                Assert.AreEqual(l.MaterialIndex, prev.MaterialIndex);
            }
        }

        // Guards against a naive "drop every departing rider" fix: with NO active competitor, the departing
        // pair alone must still survive intact — it never gets to claim-skip itself.
        [Test]
        public void CentredPair_DepartingAlone_NoActiveCompetitor_SurvivesIntact()
        {
            double3 cell = new double3(ParityQ * 700.0, 0, ParityQ * 700.0);
            var tileDeparting = new TileId { Z = 13, X = 30, Y = 30 };

            LabelInstance depIcon = Point(cell, 0, null, "shield", 0, tileDeparting, LabelPairRole.Owner, pairId: 0);
            LabelInstance depText = Point(cell, 0, "9", null, 1, tileDeparting, LabelPairRole.Rider, pairId: 0);
            var depLabels = new List<LabelInstance> { depIcon, depText };

            var store = new SymbolTileLabelStore(cacheCap: 16);
            store.CompleteBuild(Key(tileDeparting), store.BeginBuild(Key(tileDeparting)), depLabels);
            store.ReconcileActiveSet(new List<SymbolTileLabelStore.Key>(),
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);
            Assert.AreEqual(0, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolLabelSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolLabelReconciler();
            var result = new SymbolLabelReconcileResult();
            reconciler.Run(snapshot, result);

            Assert.AreEqual(2, result.Output.Count, "the departing pair, with no active competitor, must survive INTACT");
            Assert.AreEqual(LabelPairRole.Owner, result.Output[0].PairRole);
            Assert.AreEqual(LabelPairRole.Rider, result.Output[1].PairRole);
            Assert.AreEqual(depIcon.PairId, result.Output[1].PairId);
        }
    }
}
