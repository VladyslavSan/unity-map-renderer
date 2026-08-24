// Unity EditMode only — SymbolTileStore / SymbolTileBlock touch Unity.Collections. NOT registered in
// core-tests.csproj. Split out of CrossTileIdentityTests.cs at the reader cutover (symbols-async-reconcile stage
// 4.2): that file's OWN CrossTileSymbolKey.For-math teeth stay engine-free; these Store_* cases construct a
// SymbolTileStore, which left the engine-free set at the same cutover.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// A-3: cross-tile point-symbol identity — <see cref="SymbolTileStore"/>'s dedup (the store-level half of
    /// <c>CrossTileIdentityTests</c>, moved here at the reader cutover, 4.2 — see that file's header).
    ///
    /// <para><b>Stage 3:</b> the store dedup no longer takes a caller grid — it keys on the fixed
    /// <see cref="CrossTileSymbolKey.CanonicalGridMeters"/> (4 m), so the cases below pass a gate <c>q</c> whose
    /// MAGNITUDE the store ignores; their anchors are spaced to merge/split under the fixed 4 m grid: (3) a
    /// symbol present in both a parent and child tile dedups to ONE, finest-zoom wins; (4) different text in the
    /// same cell does NOT merge; (5) line symbols are not deduped.</para>
    ///
    /// <para><b>Retired (4.2), not converted:</b> the pre-cutover file also had a <c>Store_QuantizeDisabled_EmitsEverything</c>
    /// case pinning "<c>quantizeMeters</c> ≤ 0 disables dedup, both copies emitted". The reader cutover's
    /// <see cref="SymbolTileStore.CollectInto(List{int},List{int},List{byte},double,out int)"/> is the ONLY
    /// surviving overload and it has no no-dedup branch left (the plan-aware shim already always ran the
    /// reconciler before this stage) — "fixing" that case to assert 1 instead of 2 would assert the OPPOSITE of
    /// its own name, so it is retired rather than repurposed.</para>
    /// </summary>
    [TestFixture]
    public class CrossTileIdentityStoreTests
    {
        // Appends one point symbol straight into `buffer` (the direct-buffer-builder idiom — see
        // Assets/Code/MapRenderer.Tests.EditMode/Text/Placement/TestSymbolTileBuffer.cs) and returns the resulting
        // record, so a caller can both group it into its tile's buffer AND hold it for a later assertion.
        private static ShapedSymbol AddPointSymbol(SymbolTileBuffer buffer, double3 anchor, int layer, string text, TileId tile)
        {
            TestSymbolTileBuffer.AddPoint(buffer, anchor, null, float2.zero, float2.zero,
                text: text, materialIndex: layer, tileKey: SymbolTileKey.Pack(tile));
            return buffer.Symbols[buffer.Symbols.Count - 1];
        }

        // Test-only: a baked block's source buffer, so Collect() can read back the SAME ShapedSymbols the
        // pre-cutover managed-list CollectInto overload did (as records, not managed-object references).
        private readonly Dictionary<SymbolTileBlock, SymbolTileBuffer> _blockSources = new();

        private bool Commit(SymbolTileStore store, SymbolTileStore.Key key, int gen, SymbolTileBuffer buffer)
        {
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, slotCount: 1, double3.zero, store.StringTable);
            bool committed = store.CompleteBuild(key, gen, block);
            _blockSources[block] = buffer;
            return committed;
        }

        private List<ShapedSymbol> Collect(SymbolTileStore store, double q)
        {
            var blockId = new List<int>(); var localIndex = new List<int>(); var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, q, out _);
            var output = new List<ShapedSymbol>(blockId.Count);
            for (int i = 0; i < blockId.Count; i++)
                output.Add(_blockSources[store.OrderedBlocks[blockId[i]]].Symbols[localIndex[i]]);
            return output;
        }

        // ── (3) THE seamless-swap dedup: the same symbol active in a parent + child tile collapses to ONE,
        //    keeping the finest (child) zoom. ──
        [Test]
        public void Store_ParentAndChildSameSymbol_DedupToFinest()
        {
            const double q = 50.0; // Stage 3: the store IGNORES this magnitude — it grids on the fixed 4 m; q only gates dedup ON.
            double3 anchor = new double3(5000.0, 0, 5000.0); // a CanonicalGridMeters=4 cell centre (5000 = 4·1250)
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };

            var parentBuffer = new SymbolTileBuffer();
            AddPointSymbol(parentBuffer, anchor + new double3(1, 0, 1), 0, "Metropolis", parent); // 1 m — well inside the 4 m cell
            var childBuffer = new SymbolTileBuffer();
            AddPointSymbol(childBuffer, anchor, 0, "Metropolis", child);

            var store = new SymbolTileStore(cacheCap: 8);
            var kParent = new SymbolTileStore.Key("src", parent);
            var kChild = new SymbolTileStore.Key("src", child);
            Commit(store, kParent, store.BeginBuild(kParent), parentBuffer);
            Commit(store, kChild, store.BeginBuild(kChild), childBuffer);

            List<ShapedSymbol> output = Collect(store, q);
            Assert.AreEqual(1, output.Count, "the duplicate parent+child symbol collapses to one");
            // ShapedSymbol is a struct — no reference identity (the pre-migration Assert.AreSame here compared
            // managed-object references). TileKey is the distinguishing field: it is the only one that
            // differs between the parent and child copies (both carry the same text/layer/anchor cell).
            Assert.AreEqual(SymbolTileKey.Pack(child), output[0].TileKey, "the finest (child) tile's label wins");
        }

        // ── (4) two GENUINELY distinct nearby symbols (different cells) both survive. ──
        [Test]
        public void Store_DistinctSymbols_BothSurvive()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };

            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, new double3(q * 10.5, 0, q * 10.5), 0, "A", tile);
            AddPointSymbol(buffer, new double3(q * 40.5, 0, q * 10.5), 0, "B", tile);

            var store = new SymbolTileStore(cacheCap: 8);
            var key = new SymbolTileStore.Key("src", tile);
            Commit(store, key, store.BeginBuild(key), buffer);
            Assert.AreEqual(2, Collect(store, q).Count, "distinct-cell symbols are not merged");
        }

        // ── (5) line symbols are excluded from dedup in v1 (two coincident line symbols both pass through). ──
        [Test]
        public void Store_LineSymbols_AreNotDeduped()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer, null, null, null,
                placement: SymbolPlacement.Line, text: "Main St", materialIndex: 0, tileKey: SymbolTileKey.Pack(tile));
            TestSymbolTileBuffer.AddCurved(buffer, null, null, null,
                placement: SymbolPlacement.LineCenter, text: "Main St", materialIndex: 0, tileKey: SymbolTileKey.Pack(tile));

            var store = new SymbolTileStore(cacheCap: 8);
            var key = new SymbolTileStore.Key("src", tile);
            Commit(store, key, store.BeginBuild(key), buffer);
            Assert.AreEqual(2, Collect(store, q).Count, "line labels pass through undeduped (per-anchor identity is a follow-up)");
        }
    }
}
