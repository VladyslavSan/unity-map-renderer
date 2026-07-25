// Unity EditMode only — SymbolTileLabelBlock/Baker need Unity.Collections' NativeArray despite living under
// MapRenderer.Unity/Text/Placement; internal, reached here via InternalsVisibleTo("MapRenderer.Tests.EditMode").
// NOT registered in core-tests.csproj (the engine-free dispose-lifecycle teeth live in
// Tests.EditMode/Text/SymbolTileLabelStoreTests.cs instead, using a fake IDisposable counter).

using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 1 (design §4, §5 B): <see cref="SymbolTileLabelBlockBaker.Bake"/>
    /// against a REAL <see cref="SymbolTileLabelBlock"/> — the native-lifetime half of the Stage-1 acceptance
    /// teeth (the engine-free dispose-SITE teeth — commit-overwrite / FIFO-evict / true-release / Clear — live
    /// in <c>SymbolTileLabelStoreTests</c> against a fake <see cref="IDisposable"/> counter, since the store
    /// itself must stay Unity.Collections-free).
    /// </summary>
    [TestFixture]
    public class SymbolTileLabelBlockBakerTests
    {
        private static readonly SymbolQuad Quad = new SymbolQuad
        {
            TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
            UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(0.5f, 0.5f), LineIndex = 0,
        };

        private static LabelInstance PointLabel(int featureIndex, long tileKey) => new LabelInstance
        {
            AnchorRender = new double3(100.0, 0.0, 200.0),
            Placement = SymbolPlacement.Point,
            Layout = new TextLayoutResult
            {
                Quads = new List<SymbolQuad> { Quad },
                BoundsMin = new float2(-8f, -8f), BoundsMax = new float2(8f, 8f), LineCount = 1,
            },
            Paint = LabelPaint.Default,
            Text = "Point",
            TextSizePx = 16f, PaddingPx = 2f, SortKey = 0f,
            FeatureIndex = featureIndex, TileKey = tileKey,
        };

        private static LabelInstance CurvedLabel(int featureIndex, long tileKey) => new LabelInstance
        {
            AnchorRender = new double3(10.0, 0.0, 20.0),
            Placement = SymbolPlacement.Line,
            PathRender = new[] { new double3(0, 0, 0), new double3(10, 0, 0), new double3(20, 0, 0) },
            LineAnchors = new[] { new LineAnchor(0, 0.5f) },
            CurvedGlyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 4f, Cell = Quad } },
            Paint = LabelPaint.Default,
            Text = "Curved",
            TextSizePx = 16f, PaddingPx = 2f, SortKey = 1f,
            FeatureIndex = featureIndex, TileKey = tileKey,
            MaxAngleDeg = 45f, KeepUpright = true,
        };

        // ── The real native lifetime: bake a mixed point/curved/null-slot list, then dispose. ──
        [Test]
        public void Bake_MixedLabels_ProducesExpectedShape_ThenDisposeFreesEveryArray()
        {
            const long tileKey = 5L;
            var labels = new List<LabelInstance> { PointLabel(0, tileKey), null, CurvedLabel(2, tileKey) };

            SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(labels, slotCount: 1, tileOriginRender: double3.zero);
            try
            {
                Assert.AreEqual(3, block.Count, "raw list length, INCLUDING the null slot");
                Assert.AreEqual(2, block.PointCount, "the real point label + the inert null-slot placeholder");
                Assert.AreEqual(1, block.CurvedCount);
                Assert.AreEqual(tileKey, block.TileKey, "every label shares one physical tile");

                // Null-slot invariant: localIndex == raw index for every slot, including AFTER the null.
                Assert.AreEqual((byte)SymbolLabelBatch.Kind.Point, block.Kinds[0]);
                Assert.AreEqual((byte)SymbolLabelBatch.Kind.Point, block.Kinds[1], "an inert null slot bakes as Kind=Point");
                Assert.AreEqual(0, block.WorldCount[1], "…with zero world-point contribution");
                Assert.AreEqual((byte)SymbolLabelBatch.Kind.Curved, block.Kinds[2]);

                // The inert slot contributes NOTHING to the staging upper bounds — only the two real labels do
                // (1 point box/quad/candidate + 1 curved placement's worth: (1 anchor + 1 fallback) * 1 glyph).
                Assert.AreEqual(1 + 2, block.MaxBoxes, "point: 1 box; curved: (anchor+fallback)=2 placements * 1 glyph");
                Assert.AreEqual(1 + 2, block.MaxQuads);
                Assert.AreEqual(1 + 2, block.MaxCandidates);

                // The point label's baked quad survives the bake byte-identically.
                int pointDetail = block.Detail[0];
                int quadStart = block.PointQuadStart[pointDetail];
                Assert.AreEqual(1, block.PointQuadCount[pointDetail]);
                Assert.AreEqual(Quad.UvTopLeft.x, block.Quads[quadStart].UvTopLeft.x, 1e-6f);

                // The curved label's anchor-fade-ids: one per anchor (1) + the trailing centred fallback.
                int curvedDetail = block.Detail[2];
                Assert.AreEqual(1, block.CurvedAnchorCount[curvedDetail]);
                Assert.AreEqual(2, block.AnchorFadeCount, "1 anchor + 1 fallback");
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
            long afterFirstDispose = SymbolTileLabelBlock.DebugLiveAllocCount;
            Assert.DoesNotThrow(() => block.Dispose(), "Dispose must be idempotent");
            Assert.AreEqual(afterFirstDispose, SymbolTileLabelBlock.DebugLiveAllocCount,
                "a second Dispose must not decrement the live-alloc counter again");
        }

        // ── Positive control (S48/S51 idiom): an un-Disposed block IS visible in DebugLiveAllocCount, proving
        //    the counter has teeth before the throwing-bake test below leans on it. ──
        [Test]
        public void Bake_PositiveControl_UndisposedBlock_CounterNonZeroDelta()
        {
            long before = SymbolTileLabelBlock.DebugLiveAllocCount;
            SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(
                new List<LabelInstance> { PointLabel(0, 5L) }, slotCount: 1, tileOriginRender: double3.zero);

            Assert.Greater(SymbolTileLabelBlock.DebugLiveAllocCount, before,
                "a freshly-baked, not-yet-disposed block must show as a live allocation");

            block.Dispose();
            Assert.AreEqual(before, SymbolTileLabelBlock.DebugLiveAllocCount, "disposing returns the counter to baseline");
        }

        // A fake quad list that lies about its own length: Count claims 2 (so the sizing pass allocates a
        // 2-slot Quads pool) but the SECOND element throws — a synthetic per-label build defect exercised
        // purely via test-side data, no production hook needed.
        private sealed class ThrowingQuadList : IReadOnlyList<SymbolQuad>
        {
            public int Count => 2;
            public SymbolQuad this[int index] => index == 0 ? Quad : throw new InvalidOperationException("synthetic bake failure");
            public IEnumerator<SymbolQuad> GetEnumerator() { yield return Quad; yield return this[1]; }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        // ── (G) Exception-safety: a bake that throws mid-fill (after every array was already allocated at its
        //    final size) must leak no NativeArray — Bake's catch disposes the partial block before rethrowing. ──
        [Test]
        public void Bake_ThrowingLabel_DisposesPartialBlock_NoLeak()
        {
            long before = SymbolTileLabelBlock.DebugLiveAllocCount;

            var throwing = new LabelInstance
            {
                AnchorRender = new double3(100.0, 0.0, 200.0), Placement = SymbolPlacement.Point,
                Layout = new TextLayoutResult { Quads = new ThrowingQuadList(), BoundsMin = float2.zero, BoundsMax = float2.zero, LineCount = 1 },
                Paint = LabelPaint.Default, Text = "Boom",
                TextSizePx = 16f, PaddingPx = 2f, SortKey = 0f, FeatureIndex = 0, TileKey = 5L,
            };
            var labels = new List<LabelInstance> { throwing };

            Assert.Throws<InvalidOperationException>(
                () => SymbolTileLabelBlockBaker.Bake(labels, slotCount: 1, tileOriginRender: double3.zero));

            Assert.AreEqual(before, SymbolTileLabelBlock.DebugLiveAllocCount,
                "a throwing bake must dispose its partially-allocated block — no leaked live block, no leaked NativeArray");
        }
    }
}
