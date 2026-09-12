// Unity EditMode only — SymbolTileBlock/Baker need Unity.Collections' NativeArray despite living under
// MapRenderer.Unity/Text/Placement; internal, reached here via InternalsVisibleTo("MapRenderer.Tests.EditMode").
// NOT registered in core-tests.csproj (the dispose-lifecycle teeth live in
// Tests.EditMode/Text/SymbolTileStoreTests.cs instead, using a fake IDisposable counter — also Unity
// EditMode only, since the reader cutover made it reference the Unity.Collections-backed SymbolTileBlock).

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools.Constraints;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 1 (design §4, §5 B): <see cref="SymbolTileBlockBaker.Bake"/>
    /// against a REAL <see cref="SymbolTileBlock"/> — the native-lifetime half of the Stage-1 acceptance
    /// teeth (the dispose-SITE teeth — commit-overwrite / FIFO-evict / true-release / Clear — live
    /// in <c>SymbolTileStoreTests</c> against a fake <see cref="IDisposable"/> counter, since the store
    /// itself must stay Unity.Collections-free).
    /// </summary>
    [TestFixture]
    public class SymbolTileBlockBakerTests
    {
        private static readonly SymbolQuad Quad = new SymbolQuad
        {
            TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
            UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(0.5f, 0.5f), LineIndex = 0,
        };

        private static void AddPointSymbol(SymbolTileBuffer buffer, int featureIndex, long tileKey) =>
            TestSymbolTileBuffer.AddPoint(buffer,
                anchorRender: new double3(100.0, 0.0, 200.0), quads: new List<SymbolQuad> { Quad },
                boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                text: "Point", textSizePx: 16f, paddingPx: 2f, sortKey: 0f,
                featureIndex: featureIndex, tileKey: tileKey, paint: SymbolPaint.Default);

        private static void AddCurvedSymbol(SymbolTileBuffer buffer, int featureIndex, long tileKey) =>
            TestSymbolTileBuffer.AddCurved(buffer,
                glyphs: new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 4f, Cell = Quad } },
                anchors: new[] { new LineAnchor(0, 0.5f) },
                path: new[] { new double3(0, 0, 0), new double3(10, 0, 0), new double3(20, 0, 0) },
                anchorRender: new double3(10.0, 0.0, 20.0),
                text: "Curved", textSizePx: 16f, paddingPx: 2f, sortKey: 1f,
                maxAngleDeg: 45f, keepUpright: true,
                featureIndex: featureIndex, tileKey: tileKey, paint: SymbolPaint.Default);

        // ── The real native lifetime: bake a mixed point/curved list, then dispose. ──
        [Test]
        public void Bake_MixedSymbols_ProducesExpectedShape_ThenDisposeFreesEveryArray()
        {
            const long tileKey = 5L;
            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, 0, tileKey);
            AddCurvedSymbol(buffer, 2, tileKey);

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, tileOriginRender: double3.zero, new SymbolStringTable());
            try
            {
                Assert.AreEqual(2, block.Kinds.Length, "raw list length");
                Assert.AreEqual(1, block.Points.Length);
                Assert.AreEqual(1, block.Curveds.Length);
                Assert.AreEqual(tileKey, block.TileKey, "every label shares one physical tile");

                // localIndex == raw index for every slot.
                Assert.AreEqual(SymbolPlacementKind.Point, block.Kinds[0]);
                Assert.AreEqual(SymbolPlacementKind.Curved, block.Kinds[1]);

                // Staging upper bounds — the two symbols' contributions
                // (1 point box/quad/candidate + 1 curved placement's worth: (1 anchor + 1 fallback) * 1 glyph).
                Assert.AreEqual(1 + 2, block.MaxBoxes, "point: 1 box; curved: (anchor+fallback)=2 placements * 1 glyph");
                Assert.AreEqual(1 + 2, block.MaxQuads);
                Assert.AreEqual(1 + 2, block.MaxCandidates);

                // The point symbol's baked quad survives the bake byte-identically.
                int pointDetail = block.Detail[0];
                int quadStart = block.PointQuadStart[pointDetail];
                Assert.AreEqual(1, block.PointQuadCount[pointDetail]);
                Assert.AreEqual(Quad.UvTopLeft.x, block.Quads[quadStart].UvTopLeft.x, 1e-6f);

                // The curved symbol's anchor-fade-ids: one per anchor (1) + the trailing centred fallback.
                int curvedDetail = block.Detail[1];
                Assert.AreEqual(1, block.CurvedAnchorCount[curvedDetail]);
                Assert.AreEqual(2, block.AnchorFadeIds.Length, "1 anchor + 1 fallback");
            }
            finally
            {
                block.Dispose();
            }

            // Real native lifetime: every array must report !IsCreated after Dispose (idempotent — a second
            // Dispose() call, e.g. a future double-release, must not throw either).
            Assert.IsFalse(block.Kinds.IsCreated);
            Assert.IsFalse(block.Detail.IsCreated);
            Assert.IsFalse(block.WorldStart.IsCreated);
            Assert.IsFalse(block.WorldCount.IsCreated);
            Assert.IsFalse(block.RepAnchor.IsCreated);
            Assert.IsFalse(block.MaterialIndexes.IsCreated);
            Assert.IsFalse(block.PairRoles.IsCreated);
            Assert.IsFalse(block.TextIds.IsCreated);
            Assert.IsFalse(block.IconImageIds.IsCreated);
            Assert.IsFalse(block.Points.IsCreated);
            Assert.IsFalse(block.PointQuadStart.IsCreated);
            Assert.IsFalse(block.PointQuadCount.IsCreated);
            Assert.IsFalse(block.Curveds.IsCreated);
            Assert.IsFalse(block.CurvedGlyphStart.IsCreated);
            Assert.IsFalse(block.CurvedGlyphCount.IsCreated);
            Assert.IsFalse(block.CurvedAnchorStart.IsCreated);
            Assert.IsFalse(block.CurvedAnchorCount.IsCreated);
            Assert.IsFalse(block.CurvedAnchorFadeStart.IsCreated);
            Assert.IsFalse(block.Quads.IsCreated);
            Assert.IsFalse(block.Glyphs.IsCreated);
            Assert.IsFalse(block.Anchors.IsCreated);
            Assert.IsFalse(block.WorldPoints.IsCreated);
            Assert.IsFalse(block.AnchorFadeIds.IsCreated);
            // A second Dispose() must be a no-op: not throw AND not move the live-alloc counter (a double
            // decrement would under-count and mask a real leak elsewhere — VerifiedDisposable runs DoDispose once).
            long afterFirstDispose = SymbolTileBlock.DebugLiveAllocCount;
            Assert.DoesNotThrow(() => block.Dispose(), "Dispose must be idempotent");
            Assert.AreEqual(afterFirstDispose, SymbolTileBlock.DebugLiveAllocCount,
                "a second Dispose must not decrement the live-alloc counter again");
        }

        // ── Native-representation migration: MaterialIndexes is a per-symbol raw-order column mirroring
        //    symbol.MaterialIndex — baked so the off-main reconciler reads it off the block. ──
        [Test]
        public void Bake_MaterialIndexColumn_MirrorsRawSlots()
        {
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer,
                anchorRender: new double3(1, 0, 2), quads: new List<SymbolQuad> { Quad },
                boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                text: "A", textSizePx: 16f, paddingPx: 2f,
                featureIndex: 0, tileKey: 9L, materialIndex: 3, paint: SymbolPaint.Default);
            TestSymbolTileBuffer.AddPoint(buffer,
                anchorRender: new double3(3, 0, 4), quads: new List<SymbolQuad> { Quad },
                boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                text: "B", textSizePx: 16f, paddingPx: 2f,
                featureIndex: 2, tileKey: 9L, materialIndex: 5, paint: SymbolPaint.Default);

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 8, tileOriginRender: double3.zero, new SymbolStringTable());
            try
            {
                Assert.AreEqual(2, block.MaterialIndexes.Length, "raw list length");
                Assert.AreEqual(3, block.MaterialIndexes[0], "MaterialIndexes[0] mirrors label a (raw, un-clamped)");
                Assert.AreEqual(5, block.MaterialIndexes[1], "MaterialIndexes[1] mirrors label b");
            }
            finally { block.Dispose(); }
        }

        // ── Native-representation migration: PairRoles is a per-raw-slot column of the RESOLVED pair role
        //    (the same SymbolPairing resolution the baker already applies), so the reconciler reads it instead of
        //    re-resolving over the tile list. A resolved owner+rider adjacent pair → Owner/Rider; a plain point
        //    and a curved record → None (§10 fence). ──
        [Test]
        public void Bake_PairRolesColumn_MirrorsResolvedPairing()
        {
            var buffer = new SymbolTileBuffer();
            // owner+rider must match on PairId/TileKey/MaterialIndex (SymbolPairing's ShapedSymbol overload rule).
            void AddPairHalf(string text, SymbolPairRole role) =>
                TestSymbolTileBuffer.AddPoint(buffer,
                    anchorRender: new double3(1, 0, 2), quads: new List<SymbolQuad> { Quad },
                    boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                    text: text, textSizePx: 16f, paddingPx: 2f,
                    featureIndex: 0, tileKey: 9L, materialIndex: 2, pairId: 1, pairRole: role, paint: SymbolPaint.Default);
            AddPairHalf("O", SymbolPairRole.Owner);
            AddPairHalf("R", SymbolPairRole.Rider);
            AddPairHalf("P", SymbolPairRole.None);
            AddCurvedSymbol(buffer, 4, 9L); // curved is never a pair half

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 8, tileOriginRender: double3.zero, new SymbolStringTable());
            try
            {
                Assert.AreEqual(SymbolPairRole.Owner, block.PairRoles[0], "resolved owner");
                Assert.AreEqual(SymbolPairRole.Rider, block.PairRoles[1], "resolved rider (adjacent, matching PairId/TileKey/MaterialIndex)");
                Assert.AreEqual(SymbolPairRole.None, block.PairRoles[2], "a plain point is unpaired");
                Assert.AreEqual(SymbolPairRole.None, block.PairRoles[3], "a curved record is never a pair half (§10 fence)");
            }
            finally { block.Dispose(); }
        }

        // ── Native-representation migration (additive): TextIds/IconImageIds are per-symbol raw-order columns of the
        //    INTERNED text/icon ids (via the store's SymbolStringTable) — the block-side mirror of the entry's parallel
        //    int[] arrays, so the off-main dedup can later key on the block's int columns. Pins: (1) distinct strings
        //    take distinct ids in bake order (Text before IconImage, per symbol, raw index order); (2) a null field
        //    bakes id 0; (3) a shared string (symbol a's icon reused by b) resolves to the SAME id. ──
        [Test]
        public void Bake_TextIdColumns_MirrorInternedIds_SharedAndNullResolveCorrectly()
        {
            var buffer = new SymbolTileBuffer();
            void AddPointWith(string text, string icon, int feature) =>
                TestSymbolTileBuffer.AddPoint(buffer,
                    anchorRender: new double3(feature, 0, feature), quads: new List<SymbolQuad> { Quad },
                    boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                    text: text, iconImage: icon, textSizePx: 16f, paddingPx: 2f,
                    featureIndex: feature, tileKey: 9L, paint: SymbolPaint.Default);
            // a: Text "Alpha"/icon "star"; b: Text "Beta"/icon "star" (shares a's icon).
            AddPointWith("Alpha", "star", 0);
            AddPointWith("Beta", "star", 2);

            var stringTable = new SymbolStringTable();
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 8, tileOriginRender: double3.zero, stringTable);
            try
            {
                Assert.AreEqual(2, block.TextIds.Length, "raw list length");
                // Bake order: i=0 Intern("Alpha")=1,Intern("star")=2; i=1 Intern("Beta")=3,Intern("star")=2.
                Assert.AreEqual(1, block.TextIds[0], "first distinct text → id 1");
                Assert.AreEqual(3, block.TextIds[1], "second distinct text → id 3 (icon 'star' took id 2)");
                Assert.AreEqual(2, block.IconImageIds[0], "first distinct icon → id 2");
                Assert.AreEqual(2, block.IconImageIds[1], "shared icon 'star' → SAME id as label a");
                // Idempotent-mirror: re-interning the same field via the SAME table returns the id the bake stored.
                Assert.AreEqual(stringTable.Intern("Alpha"), block.TextIds[0], "column mirrors the interned text id");
                Assert.AreEqual(stringTable.Intern("star"), block.IconImageIds[1], "column mirrors the interned icon id");
            }
            finally { block.Dispose(); }
        }

        // ── Positive control (S48/S51 idiom): an un-Disposed block IS visible in DebugLiveAllocCount, proving
        //    the counter has teeth before the throwing-bake test below leans on it. ──
        [Test]
        public void Bake_PositiveControl_UndisposedBlock_CounterNonZeroDelta()
        {
            long before = SymbolTileBlock.DebugLiveAllocCount;
            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, 0, 5L);
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, tileOriginRender: double3.zero, new SymbolStringTable());

            Assert.Greater(SymbolTileBlock.DebugLiveAllocCount, before,
                "a freshly-baked, not-yet-disposed block must show as a live allocation");

            block.Dispose();
            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount, "disposing returns the counter to baseline");
        }

        // ── (G) Exception-safety: a bake that throws mid-fill (after every array was already allocated at its
        //    final size) must leak no NativeArray — Bake's catch disposes the partial block before rethrowing.
        //
        //    4.4c VERIFY-ITEM: the pre-buffer injection was a fake IReadOnlyList<SymbolQuad>
        //    that lied about its own Count (a two-pass CountSizes/Fill disagreement). Under ShapedSymbol's
        //    (start,count) spans, CountSizes and Fill read the SAME span, so that two-pass disagreement is no
        //    longer inducible through the (now-deleted) per-symbol-carrier→buffer conversion adapter (its
        //    AppendQuads loop would throw INSIDE the conversion, before Bake ever runs — never exercising Bake's
        //    own catch). The replacement injection
        //    instead builds the buffer directly: a record whose QuadCount claims more quads than the pool
        //    actually holds, so Fill's indexed pool read throws mid-bake — the same failure SHAPE (a throw after
        //    every NativeArray is already allocated, mid second-pass fill), just relocated to where it can still
        //    happen under the new representation. ──
        [Test]
        public void Bake_ThrowingSymbol_DisposesPartialBlock_NoLeak()
        {
            long before = SymbolTileBlock.DebugLiveAllocCount;

            var buffer = new SymbolTileBuffer();
            buffer.Quads.Add(Quad); // the pool holds exactly ONE quad
            buffer.AddSymbol(new ShapedSymbol
            {
                AnchorRender = new double3(100.0, 0.0, 200.0), Placement = SymbolPlacement.Point,
                Paint = SymbolPaint.Default, Text = "Boom",
                TextSizePx = 16f, PaddingPx = 2f, SortKey = 0f, FeatureIndex = 0, TileKey = 5L,
                QuadStart = 0, QuadCount = 2, // claims TWO — Fill's second read overruns the pool
            });

            Assert.Throws<ArgumentOutOfRangeException>(
                () => SymbolTileBlockBaker.Bake(buffer, slotCount: 1, tileOriginRender: double3.zero, new SymbolStringTable()));

            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount,
                "a throwing bake must dispose its partially-allocated block — no leaked live block, no leaked NativeArray");
        }

        // NOTE: there is deliberately NO "warm Bake allocates zero managed" tooth here. Bake mints a fresh
        // `new SymbolTileBlock()` (a sealed CLASS) per tile by design — one block per tile commit — so a
        // zero-managed-alloc claim over Bake is false by construction. The 3b per-build churn win lives entirely
        // in the REUSED buffer's append/Clear path; its zero-alloc tooth is SymbolTileBufferAllocTests
        // (core-tests-only — the EditMode byte meter can't resolve it; see that file's header).
    }
}
