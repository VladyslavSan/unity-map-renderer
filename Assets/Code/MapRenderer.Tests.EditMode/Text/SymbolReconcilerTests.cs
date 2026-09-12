// Unity EditMode only — the off-main reconciler + snapshot carriers + the store's pin guard now read the
// Unity.Collections-backed SymbolTileBlock directly (reader cutover, symbols-async-reconcile stage 4.2), so
// this file left the core-tests.csproj fast loop (still compiled + run by the Unity EditMode runner).

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolTileBuffer — the direct-buffer-builder fixture idiom

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Stage 4b (symbols-async-reconcile): the off-main <see cref="SymbolReconciler"/> + the store's native
    /// pin guard (SPEC A). T1 proves the extracted worker is BYTE-IDENTICAL to an independent string oracle; T2
    /// pins the disposal-bumps-generation edge; T3 the reused-result SHRINK; T4 the per-site defer/flush TIMING;
    /// T5 the cross-snapshot refcount OVERLAP. T3/T4/T5 are RED-verified against the un-guarded code.
    ///
    /// <para><b>Reader cutover (4.2).</b> Every commit that participates in a <c>CaptureSnapshot</c>/<c>Run</c>
    /// call now bakes and commits a REAL <see cref="SymbolTileBlock"/> (via <see cref="Commit"/>) — the
    /// reconciler reads the block, so a block-less (<c>Block == null</c>) entry is no longer collected at all
    /// (see <c>SymbolTileStore.CaptureSnapshot</c>'s guard). Winner identity is <c>(BlockId, LocalIndex)</c>;
    /// where a test needs to know WHICH symbol a winner is, it reads the winner's <c>OrderedBlocks[BlockId]</c>
    /// block's own columns (<c>TileKey</c>/<c>PairRoles</c>/…) — never a re-derivation of the oracle's own
    /// indices, which would just restate them (see the T1 note below).
    /// </summary>
    [TestFixture]
    public class SymbolReconcilerTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here — real
        // blocks via DebugLiveAllocCount, and FakeBlock via the same mirrored counter below.
        private long _liveBlocks;
        private int _liveFakes;
        [SetUp] public void BaselineBlocks()
        {
            _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
            _liveFakes = FakeBlock.LiveCount;
        }
        [TearDown] public void NoLeakedBlocks()
        {
            Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
                "this test baked a block it never disposed — release the snapshot and Clear() the store");
            Assert.AreEqual(_liveFakes, FakeBlock.LiveCount,
                "this test committed a FakeBlock it never disposed — Clear() the store");
        }

        private const double ParityQ = 50.0;

        private sealed class FakeBlock : IDisposable
        {
            // Mirrors SymbolTileBlock.DebugLiveAllocCount's idiom so the fixture's leak-guard TearDown can
            // catch an abandoned fake commit too — every DisposeCount assertion in this fixture is 0 or 1,
            // so decrementing unconditionally on every Dispose() call stays correct.
            internal static int LiveCount;
            public int DisposeCount;
            public FakeBlock() => LiveCount++;
            public void Dispose() { DisposeCount++; LiveCount--; }
        }

        private static SymbolTileStore.Key Key(TileId t) => new SymbolTileStore.Key("src", t);

        // Bakes a REAL block for `buffer` and commits it — the reconciler cutover means every commit a test wants
        // CaptureSnapshot/Run to see needs a real SymbolTileBlock (a block-less entry is no longer collected;
        // see the type doc). One shared SymbolStringTable per store (store.StringTable) so cross-tile ids match, as
        // production does (SymbolTileBlockBaker.Bake interns into the SAME table CompleteBuild would).
        private static bool Commit(SymbolTileStore store, SymbolTileStore.Key key, int gen, SymbolTileBuffer buffer)
            => store.CompleteBuild(key, gen, SymbolTileBlockBaker.Bake(buffer, slotCount: 1, double3.zero, store.StringTable));

        // Appends one point (or icon, via `icon`) symbol into `buffer` and returns the resulting record — so a
        // caller can both group it into its tile's buffer AND hold it for a later assertion, mirroring the
        // pre-migration per-symbol managed carrier local var it replaces. §10 D8/D9: pairRole/pairId default to None/0 —
        // every existing call site (unpaired symbols) is unaffected.
        private static ShapedSymbol Point(SymbolTileBuffer buffer, double3 anchor, int layer, string text, string icon,
            int feature, TileId tile, SymbolPairRole pairRole = SymbolPairRole.None, int pairId = 0)
        {
            TestSymbolTileBuffer.AddPoint(buffer, anchor, null, float2.zero, float2.zero,
                text: text, iconImage: icon, materialIndex: layer, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile),
                pairRole: pairRole, pairId: pairId);
            return buffer.Symbols[buffer.Symbols.Count - 1];
        }

        private static ShapedSymbol Curved(SymbolTileBuffer buffer, string text, int feature, TileId tile)
        {
            TestSymbolTileBuffer.AddCurved(buffer, null, null, null,
                placement: SymbolPlacement.LineCenter, text: text, materialIndex: 0, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));
            return buffer.Symbols[buffer.Symbols.Count - 1];
        }

        // Independent STRING-keyed oracle — the SAME scan order + finest-zoom rule + per-tile blockId assignment
        // as the reconciler, but keyed on the string CrossTileSymbolKey (NOT the code under test), and walking the
        // fixture's OWN ShapedSymbol lists (NOT the block the reconciler reads) — a genuinely separate
        // computation. activeTiles / departingTiles are in the store's _active / _departing enumeration order.
        private struct OracleOut
        {
            public List<ShapedSymbol> Output; public int ActiveCount;
            public List<int> BlockId; public List<int> LocalIndex; public List<byte> IsDeparting;
        }

        private static OracleOut Oracle(List<List<ShapedSymbol>> activeTiles, List<List<ShapedSymbol>> departingTiles)
        {
            var output = new List<ShapedSymbol>(); var blockIds = new List<int>();
            var localIndices = new List<int>(); var isDeparting = new List<byte>();
            var dedup = new Dictionary<CrossTileSymbolKey, (ShapedSymbol symbol, int z, long tileKey, int blockId, int localIndex)>();
            int nextBlock = 0;

            foreach (List<ShapedSymbol> tile in activeTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    ShapedSymbol symbol = tile[i];
                    if (symbol.Placement != SymbolPlacement.Point)
                    {
                        output.Add(symbol); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(0);
                        continue;
                    }
                    var key = CrossTileSymbolKey.For(symbol.AnchorRender, symbol.MaterialIndex, symbol.Text, symbol.IconImage, CrossTileSymbolKey.CanonicalGridMeters);
                    int z = (int)(symbol.TileKey >> 44);
                    if (!dedup.TryGetValue(key, out var cur) || z > cur.z || (z == cur.z && symbol.TileKey < cur.tileKey))
                        dedup[key] = (symbol, z, symbol.TileKey, myBlock, i);
                }
            }
            foreach (var kv in dedup)
            {
                output.Add(kv.Value.symbol); blockIds.Add(kv.Value.blockId); localIndices.Add(kv.Value.localIndex); isDeparting.Add(0);
            }
            int activeCount = output.Count;

            foreach (List<ShapedSymbol> tile in departingTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    ShapedSymbol symbol = tile[i];
                    if (symbol.Placement == SymbolPlacement.Point)
                    {
                        var key = CrossTileSymbolKey.For(symbol.AnchorRender, symbol.MaterialIndex, symbol.Text, symbol.IconImage, CrossTileSymbolKey.CanonicalGridMeters);
                        if (dedup.ContainsKey(key)) continue;
                        dedup[key] = (symbol, 0, symbol.TileKey, myBlock, i);
                    }
                    output.Add(symbol); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(1);
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

            var pBuffer = new SymbolTileBuffer();
            var parentShared = Point(pBuffer, cellShared + new double3(1, 0, 1), 0, "Shared", null, 1, tileP);
            var cBuffer = new SymbolTileBuffer();
            var childShared = Point(cBuffer, cellShared, 0, "Shared", null, 2, tileC);
            var mBuffer = new SymbolTileBuffer();
            var curved = Curved(mBuffer, "Road", 3, tileM);
            var bAlpha = Point(mBuffer, cellB, 0, "Alpha", null, 4, tileM);
            var bBeta = Point(mBuffer, cellB, 0, "Beta", null, 5, tileM);
            var icoA = Point(mBuffer, cellIcon, 0, null, "ico-a", 6, tileM);
            var icoB = Point(mBuffer, cellIcon, 0, null, "ico-b", 7, tileM);
            var depBuffer = new SymbolTileBuffer();
            var depShared = Point(depBuffer, cellShared, 0, "Shared", null, 8, tileDep);
            var depUnique = Point(depBuffer, cellUnique, 0, "Unique", null, 9, tileDep);

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileP), store.BeginBuild(Key(tileP)), pBuffer);
            Commit(store, Key(tileC), store.BeginBuild(Key(tileC)), cBuffer);
            Commit(store, Key(tileM), store.BeginBuild(Key(tileM)), mBuffer);
            Commit(store, Key(tileDep), store.BeginBuild(Key(tileDep)), depBuffer);
            store.ReconcileActiveSet(
                new List<SymbolTileStore.Key> { Key(tileP), Key(tileC), Key(tileM) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(3, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            OracleOut oracle = Oracle(
                new List<List<ShapedSymbol>> { pBuffer.Symbols, cBuffer.Symbols, mBuffer.Symbols },
                new List<List<ShapedSymbol>> { depBuffer.Symbols });

            Assert.IsTrue(oracle.Output.Contains(childShared) && !oracle.Output.Contains(parentShared),
                "sanity: the fixture exercises the finest-zoom merge");
            Assert.IsFalse(oracle.Output.Contains(depShared), "sanity: the departing twin of the shared identity is claim-skipped");
            Assert.IsTrue(oracle.Output.Contains(icoA) && oracle.Output.Contains(icoB), "sanity: distinct icons both survive");

            // Reader cutover: the reconciler has no Output list any more — winner identity is (BlockId, LocalIndex).
            // Comparing these element-by-element against the INDEPENDENT string oracle's own (blockId, localIndex)
            // IS the parity check (both assign blockId in the SAME per-tile scan order over the SAME source lists,
            // and localIndex == raw list position by the null-slot invariant) — not a restatement of anything the
            // reconciler itself computed.
            Assert.AreEqual(oracle.Output.Count, result.BlockId.Count, "same total emitted count");
            Assert.AreEqual(oracle.ActiveCount, result.ActiveCount, "same active/departing split");
            for (int i = 0; i < oracle.Output.Count; i++)
            {
                Assert.AreEqual(oracle.BlockId[i], result.BlockId[i], $"blockId mismatch at {i}");
                Assert.AreEqual(oracle.LocalIndex[i], result.LocalIndex[i], $"localIndex mismatch at {i}");
                Assert.AreEqual(oracle.IsDeparting[i], result.IsDeparting[i], $"isDeparting mismatch at {i}");
            }
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // ═══ T2: a disposal-causing mutation (over-cap FIFO evict) bumps the collect generation ═══

        [Test]
        public void CollectGeneration_BumpsOnOverCapFifoEvict()
        {
            var store = new SymbolTileStore(cacheCap: 1);
            var a = new TileId { Z = 5, X = 1, Y = 0 };
            var b = new TileId { Z = 5, X = 2, Y = 0 };
            store.CompleteBuild(Key(a), store.BeginBuild(Key(a)), new FakeBlock());
            store.CompleteBuild(Key(b), store.BeginBuild(Key(b)), new FakeBlock());

            store.Release(Key(a), transferredToCache: true); // → cached (cap 1)
            int g0 = store.CollectGeneration;
            store.Release(Key(b), transferredToCache: true); // over cap → evicts a (disposes its block) — must bump
            Assert.AreNotEqual(g0, store.CollectGeneration, "an over-cap FIFO evict changed the collected set → it must bump");
            store.Clear(); // b is still cached (only a was evicted)
        }

        // ═══ T3: the reused RESULT (and index) SHRINKS — {A,B} → {B} → empty ═══
        // RED-verify: strip `result.Clear()` / `_dedup.Clear()` from Run → the reused result accumulates prior
        // runs' records → the counts stop shrinking → this fails.

        [Test]
        public void Reconciler_ReusedResult_Shrinks_AcrossRuns()
        {
            var a = new TileId { Z = 5, X = 1, Y = 0 };
            var b = new TileId { Z = 5, X = 2, Y = 0 };
            var bufferA = new SymbolTileBuffer();
            Point(bufferA, new double3(1000, 0, 1000), 0, "A", null, 1, a);
            var bufferB = new SymbolTileBuffer();
            Point(bufferB, new double3(2000, 0, 2000), 0, "B", null, 2, b);

            var store = new SymbolTileStore(cacheCap: 8);
            Commit(store, Key(a), store.BeginBuild(Key(a)), bufferA);
            Commit(store, Key(b), store.BeginBuild(Key(b)), bufferB);

            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();   // REUSED across all three runs
            var snapshot = new SymbolSnapshot();        // REUSED across all three runs

            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(2, result.BlockId.Count, "{A,B} → two winners");

            // Released here to keep the reference model explicit — CaptureSnapshot would release it anyway
            // (its leading into.Clear() releases whatever the snapshot previously held).
            store.ReleasePins(snapshot);
            store.Release(Key(a), transferredToCache: false); // drop A → active {B}
            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(1, result.BlockId.Count, "{B} → the reused result must SHRINK to one (no stale A)");
            Assert.AreEqual(SymbolTileKey.Pack(b), result.OrderedBlocks[result.BlockId[0]].TileKey, "…and the surviving winner is B's tile");
            Assert.AreEqual(0, result.LocalIndex[0], "…B's only (raw index 0) record");

            store.ReleasePins(snapshot); // release the SECOND capture's pin on B before dropping it — same reason
            store.Release(Key(b), transferredToCache: false); // drop B → empty
            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(0, result.BlockId.Count, "empty set → the reused result must shrink to zero");
            Assert.AreEqual(0, result.LocalIndex.Count, "…and every parallel list too");
            Assert.AreEqual(0, result.OrderedBlocks.Count);
            Assert.AreEqual(0, result.ActiveCount);
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // ═══ T4: per-site defer/flush TIMING — a block a snapshot still references is NOT disposed at the drop
        //         site; it frees only when the last reference (the snapshot's) is released ═══
        // RED-verify, per site: replace that site's `?.Release()` with `?.Value.Dispose()` → the block disposes
        // at the site itself, so ReleasePins has nothing left to free → the `:361` "frees exactly once" assertion
        // fails FOR THAT [Values] CASE ONLY (the `live0` baseline is taken after the drop site runs, so the
        // preceding "unchanged" assert can't observe an already-completed dispose) — four independent,
        // individually discriminating recipes.
        //
        // Reader cutover: CaptureSnapshot now casts Entry.Block to SymbolTileBlock, so a FakeBlock committed
        // here would throw at capture time — every block under test is a REAL baked block, and disposal is
        // observed via SymbolTileBlock.DebugLiveAllocCount (the established leak-guard idiom, prior art
        // SymbolReconcileAsyncTests.Pin_PreventsNativeUseAfterFree_InGather) rather than a fake's counter.
        // The delta is taken relative to a baseline captured AFTER every block this test creates already exists
        // (including the FifoEvict case's second tile), so only `t`'s own block's alive→disposed transition moves it.

        public enum DeferSite { CommitOverwrite, ReleaseActiveTrueEvict, ReleaseStaleCached, FifoEvict }

        [Test]
        public void DropSite_DefersWhileReferenced_FreesOnReleasePins([Values] DeferSite site)
        {
            var store = new SymbolTileStore(cacheCap: site == DeferSite.FifoEvict ? 1 : 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, new double3(1000, 0, 1000), 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            // For the stale-cached site, the tile must be cached AND departing before capture (CaptureSnapshot only
            // pins active + departing tiles) — reconcile to an empty loaded set with grace stamps it departing.
            if (site == DeferSite.ReleaseStaleCached)
                store.ReconcileActiveSet(new List<SymbolTileStore.Key>(), keepWarmOnRelease: true,
                    nowSeconds: 10.0, departingGraceSeconds: 1000.0);

            // Pin `t`'s block by holding a live snapshot referencing it (as an active slice, or a departing one above).
            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot); // pins t's block

            switch (site)
            {
                case DeferSite.CommitOverwrite:
                {
                    var t2Buffer = new SymbolTileBuffer();
                    Point(t2Buffer, default, 0, "t2", null, 2, t);
                    Commit(store, Key(t), store.BeginBuild(Key(t)), t2Buffer);
                    break;
                }
                case DeferSite.ReleaseActiveTrueEvict:
                    store.Release(Key(t), transferredToCache: false); // active → true evict
                    break;
                case DeferSite.ReleaseStaleCached:
                    store.Release(Key(t), transferredToCache: false); // stale cached copy → drop
                    break;
                case DeferSite.FifoEvict:
                {
                    var u = new TileId { Z = 5, X = 2, Y = 0 };
                    store.Release(Key(t), transferredToCache: true); // t → cached (cap 1)
                    var uBuffer = new SymbolTileBuffer();
                    Point(uBuffer, default, 0, "u", null, 3, u);
                    Commit(store, Key(u), store.BeginBuild(Key(u)), uBuffer);
                    store.Release(Key(u), transferredToCache: true); // over cap → evicts t (disposes its block)
                    break;
                }
            }

            // Baseline AFTER every block this test creates already exists — only t's block is expected to move.
            long live0 = SymbolTileBlock.DebugLiveAllocCount;
            Assert.AreEqual(live0, SymbolTileBlock.DebugLiveAllocCount,
                $"{site}: the drop site must DEFER — t's block is pinned by a live snapshot");
            store.ReleasePins(snapshot);
            Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                $"{site}: ReleasePins drops the pin to 0 → the deferred block frees exactly once");
            store.Clear(); // the CommitOverwrite/FifoEvict sites leave an unpinned live block (t2 / u) behind
        }

        // R-4 — genuinely discriminating: Clear() must RELEASE the entry's own reference, not dispose the block
        // outright. At Clear() time the block has exactly two references — the entry's and the snapshot's — so
        // a Clear() that disposes instead of releases frees the block while the snapshot still references it:
        // the exact restyle-mid-reconcile use-after-free this mechanism exists to prevent.
        // RED-verify: replace SymbolTileStore.cs Clear()'s `kv.Value.Block?.Release()` with
        // `kv.Value.Block?.Value.Dispose()` → the block frees at Clear() while the snapshot still references it
        // → the "must NOT dispose" assertion below fails.
        [Test]
        public void Clear_KeepsPinnedDeferredBlockAlive_FreedOnReleasePins()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);                 // pins t's block
            // Track THIS specific block — the global live count can't distinguish it surviving from any other
            // block this test creates, so key the tooth on this block's own array liveness.
            SymbolTileBlock pinnedBlock = snapshot.Slices[0].Block;

            store.Clear(); // teardown WITHOUT ReleasePins — releases the entry's own reference only
            Assert.IsTrue(pinnedBlock.Kinds.IsCreated,
                "Clear must NOT dispose a still-referenced block (self-safe against UAF)");

            store.ReleasePins(snapshot); // the snapshot leaves service → the last reference drops → frees once
            Assert.IsFalse(pinnedBlock.Kinds.IsCreated,
                "ReleasePins frees the (now-unreferenced) block exactly once — no leak");
        }

        // ═══ T5: cross-snapshot refcount OVERLAP — a block referenced by TWO snapshots survives one release ═══
        // R-3 — DECLARED SURVIVOR: s1 and s2 are two distinct SymbolSnapshot objects, each holding one slice, so
        // any per-snapshot-dedupe injection is a no-op here. The only injection that isolates cross-snapshot
        // accumulation is "don't acquire/release at all", which also reds R-1, R-2 and every T4 case — not
        // discriminating. The property is now Interlocked.Increment: accumulation across independent acquirers
        // is what the primitive IS, not a discipline this test can falsify in isolation. Kept for its assertions.

        [Test]
        public void Pin_RefcountsAcrossTwoSnapshots_SurvivesSingleRelease()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            var s1 = new SymbolSnapshot(); store.CaptureSnapshot(s1); // pin #1
            var s2 = new SymbolSnapshot(); store.CaptureSnapshot(s2); // pin #2 (count → 2)

            store.Release(Key(t), transferredToCache: false); // drop site → pinned (count 2) → deferred, not disposed
            long live0 = SymbolTileBlock.DebugLiveAllocCount;

            store.ReleasePins(s1);
            Assert.AreEqual(live0, SymbolTileBlock.DebugLiveAllocCount,
                "one release drops the count 2→1 — the block SURVIVES (still referenced)");
            store.ReleasePins(s2);
            Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                "the second release drops 1→0 → freed exactly once");
        }

        // ═══ R-1: releasing the SAME snapshot twice must NOT free a block another live snapshot still
        //          references — the SymbolSubsystem.cs:947→:1055 shape (a demoted snapshot's pins released once
        //          on swap, then again at teardown). Needs a SECOND live reference (s2) or the tooth is vacuous:
        //          SymbolTileBlock's dispose is already idempotent, so a premature free with only one reference
        //          left is unobservable. ═══
        // RED-verify: move the release loop back into SymbolTileStore.ReleasePins (reset the slices in
        // SymbolSnapshot.Clear() WITHOUT releasing Pin first) → the second ReleasePins(s1) decrements s1's slices
        // a second time, the block's refcount hits 0 under s2 → the first assert below fails.

        [Test]
        public void ReleasePins_IsIdempotent_SecondReleaseDoesNotFreeUnderAnotherSnapshot()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer); // refs = 1 (the store's own entry)

            var s1 = new SymbolSnapshot(); store.CaptureSnapshot(s1); // refs = 2
            var s2 = new SymbolSnapshot(); store.CaptureSnapshot(s2); // refs = 3
            // Read the block BEFORE any release — Clear() nulls the slice, so this must be captured first.
            SymbolTileBlock pinned = s1.Slices[0].Block;

            store.Release(Key(t), transferredToCache: false); // the store's own reference drops → refs 2, still alive
            store.ReleasePins(s1); // → refs 1 (s2 only)
            store.ReleasePins(s1); // repeated release of the SAME already-empty snapshot — must be a no-op
            Assert.IsTrue(pinned.Kinds.IsCreated,
                "a repeated ReleasePins on the same snapshot must not free a block s2 still references");
            store.ReleasePins(s2); // → refs 0
            Assert.IsFalse(pinned.Kinds.IsCreated, "the block frees exactly once, on s2's release");
        }

        // ═══ R-2: re-capturing into a reused snapshot releases what it PREVIOUSLY captured, so a block dropped
        //          from the store between two captures is freed by the second capture rather than stranded. ═══
        // One assert, deliberately: Assert.AreEqual(0, s.Count) would pass whether or not the fix is present
        // (Release(…, transferredToCache: false) already empties the store, so the re-capture yields Count == 0
        // either way) — riding a vacuous assert on a real one reads as coverage it is not.
        // RED-verify: drop the `Slices[i].Pin.Release()` call from SymbolSnapshot.Clear() → the re-capture
        // resets the slice without releasing its reference → the block's refcount never reaches 0 → this fails.

        [Test]
        public void CaptureSnapshot_ReleasesThePreviousCaptureReferences()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            var s = new SymbolSnapshot();
            store.CaptureSnapshot(s);
            store.Release(Key(t), transferredToCache: false); // store's own reference drops → refs 1 (s only), alive
            long live0 = SymbolTileBlock.DebugLiveAllocCount;

            store.CaptureSnapshot(s); // store is empty → s.Clear() releases the prior capture → refs 0 → freed
            Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                "the re-capture must release what the snapshot previously held");
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

            var aBuffer = new SymbolTileBuffer();
            Point(aBuffer, cell, 0, null, "shield", 0, tileA, SymbolPairRole.Owner, pairId: 0);
            Point(aBuffer, cell, 0, "42", null, 1, tileA, SymbolPairRole.Rider, pairId: 0);
            var bBuffer = new SymbolTileBuffer();
            Point(bBuffer, cell, 0, null, "shield", 0, tileB); // unpaired lone icon, no text at B

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileA), store.BeginBuild(Key(tileA)), aBuffer);
            Commit(store, Key(tileB), store.BeginBuild(Key(tileB)), bBuffer);
            store.ReconcileActiveSet(new List<SymbolTileStore.Key> { Key(tileA), Key(tileB) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            Assert.AreEqual(1, result.BlockId.Count,
                "only the finer tile's icon survives — the coarser tile's rider (its owner lost the race) is never emitted alone");
            Assert.AreEqual(SymbolTileKey.Pack(tileB), result.OrderedBlocks[result.BlockId[0]].TileKey, "the survivor is tile B's block");
            Assert.AreEqual(0, result.LocalIndex[0], "bIcon is tile B's only (raw index 0) record");
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // ═══ §10 D9 — P13: no orphan rider, the INTRA-tile departing case (reconciler step (b)) ═══

        [Test]
        public void CentredPair_Departing_ClaimSkippedOwnerTakesRiderWithIt_NoOrphan()
        {
            double3 cell = new double3(ParityQ * 600.0, 0, ParityQ * 600.0);
            var tileActive = new TileId { Z = 13, X = 10, Y = 10 };
            var tileDeparting = new TileId { Z = 13, X = 20, Y = 20 }; // SAME quantized cell, different tile id

            var activeBuffer = new SymbolTileBuffer();
            Point(activeBuffer, cell, 0, null, "shield", 0, tileActive, SymbolPairRole.Owner, pairId: 0);
            Point(activeBuffer, cell, 0, "7", null, 1, tileActive, SymbolPairRole.Rider, pairId: 0);
            var depBuffer = new SymbolTileBuffer();
            Point(depBuffer, cell, 0, null, "shield", 0, tileDeparting, SymbolPairRole.Owner, pairId: 0);
            Point(depBuffer, cell, 0, "7", null, 1, tileDeparting, SymbolPairRole.Rider, pairId: 0);

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileActive), store.BeginBuild(Key(tileActive)), activeBuffer);
            Commit(store, Key(tileDeparting), store.BeginBuild(Key(tileDeparting)), depBuffer);
            // tileDeparting is never in the active set → it leaves cover, kept warm and departing.
            store.ReconcileActiveSet(new List<SymbolTileStore.Key> { Key(tileActive) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);
            Assert.AreEqual(1, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            long tileKeyDeparting = SymbolTileKey.Pack(tileDeparting);

            Assert.AreEqual(2, result.BlockId.Count, "exactly the active tile's 2 labels — the departing pair drops together");
            Assert.AreEqual(2, result.ActiveCount);
            for (int i = 0; i < result.BlockId.Count; i++)
                Assert.AreNotEqual(tileKeyDeparting, result.OrderedBlocks[result.BlockId[i]].TileKey,
                    "no record may carry the claim-skipped departing tile's key");

            // (ii) the general structural invariant over the WHOLE output: every Rider is immediately preceded
            // by its matching Owner (block-level PairRoles column — not Points[Detail], the §2 hazard), sharing
            // the same block and MaterialIndex. PairId itself is not a baked block column (no reader — deleted
            // in 4.1); block+adjacency is the identity check the block-based winner plan can offer.
            for (int i = 0; i < result.BlockId.Count; i++)
            {
                SymbolTileBlock block = result.OrderedBlocks[result.BlockId[i]];
                int localIndex = result.LocalIndex[i];
                if (block.PairRoles[localIndex] != SymbolPairRole.Rider) continue;
                Assert.Greater(i, 0, "a Rider can never be the first output entry");
                SymbolTileBlock prevBlock = result.OrderedBlocks[result.BlockId[i - 1]];
                int prevLocalIndex = result.LocalIndex[i - 1];
                Assert.AreEqual(SymbolPairRole.Owner, prevBlock.PairRoles[prevLocalIndex], $"output[{i - 1}] must be the matching Owner");
                Assert.AreEqual(block.TileKey, prevBlock.TileKey);
                Assert.AreEqual(block.MaterialIndexes[localIndex], prevBlock.MaterialIndexes[prevLocalIndex]);
            }
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // Guards against a naive "drop every departing rider" fix: with NO active competitor, the departing
        // pair alone must still survive intact — it never gets to claim-skip itself.
        [Test]
        public void CentredPair_DepartingAlone_NoActiveCompetitor_SurvivesIntact()
        {
            double3 cell = new double3(ParityQ * 700.0, 0, ParityQ * 700.0);
            var tileDeparting = new TileId { Z = 13, X = 30, Y = 30 };

            var depBuffer = new SymbolTileBuffer();
            Point(depBuffer, cell, 0, null, "shield", 0, tileDeparting, SymbolPairRole.Owner, pairId: 0);
            Point(depBuffer, cell, 0, "9", null, 1, tileDeparting, SymbolPairRole.Rider, pairId: 0);

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileDeparting), store.BeginBuild(Key(tileDeparting)), depBuffer);
            store.ReconcileActiveSet(new List<SymbolTileStore.Key>(),
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);
            Assert.AreEqual(0, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            Assert.AreEqual(2, result.BlockId.Count, "the departing pair, with no active competitor, must survive INTACT");
            SymbolTileBlock block = result.OrderedBlocks[result.BlockId[0]];
            Assert.AreEqual(SymbolPairRole.Owner, block.PairRoles[result.LocalIndex[0]]);
            Assert.AreEqual(SymbolPairRole.Rider, block.PairRoles[result.LocalIndex[1]]);
            Assert.AreEqual(result.BlockId[0], result.BlockId[1], "the pair shares one block");
            Assert.AreEqual(result.LocalIndex[0] + 1, result.LocalIndex[1], "the rider is the raw record immediately after its owner");
            store.ReleasePins(snapshot);
            store.Clear();
        }
    }
}
