// Unity EditMode only — SymbolLabelBatchBuilder.Build needs UnityEngine.Color (sRGB→linear text-color
// conversion) despite living under MapRenderer.Unity/Text/Placement; internal, reached here via
// InternalsVisibleTo("MapRenderer.Tests.EditMode"). NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// I5a — the batch/stage tooth: an icon <see cref="LabelInstance"/> (Kind=Icon, TextSizePx=OneEm, a
    /// single-quad Layout) flows through <see cref="SymbolLabelBatchBuilder.Build"/> into a
    /// <see cref="PointStageInput"/> carrying <see cref="LabelKind.Icon"/>, and through
    /// <see cref="LabelStagingMath.StagePoint"/> into exactly one collision box (scale 1 — text-size ==
    /// OneEm means NO double-scale of an already-baked icon quad) and one candidate whose emit carries the
    /// icon discriminator. Draw is NOT yet partitioned by it (I5b) — this only pins the DATA thread.
    /// </summary>
    [TestFixture]
    public class SymbolLabelBatchBuilderIconTests
    {
        private static readonly SymbolQuad IconQuad = new SymbolQuad
        {
            TopLeft = new float2(-10f, 10f), BottomRight = new float2(10f, -10f),
            UvTopLeft = new float2(0.0f, 0.0f), UvBottomRight = new float2(0.5f, 0.5f), LineIndex = 0,
        };

        private static LabelInstance IconLabelInstance() => new LabelInstance
        {
            AnchorRender = new double3(100.0, 0.0, 200.0),
            Placement = SymbolPlacement.Point,
            Kind = LabelKind.Icon,
            Layout = IconQuadLayout.ToLayoutResult(IconQuad),
            Paint = LabelPaint.Default,
            TextSizePx = TextQuadLayout.OneEm,
            PaddingPx = 4f,
            SortKey = 0f,
            FeatureIndex = 2,
            TileKey = 5L,
        };

        [Test]
        public void Build_IconLabel_ProducesPointDetailCarryingIconAtlasKind()
        {
            var batch = new SymbolLabelBatch();
            var labels = new List<LabelInstance> { IconLabelInstance() };

            SymbolLabelBatchBuilder.Build(batch, labels, slotCount: 1);

            Assert.AreEqual(1, batch.Count, "one record");
            Assert.AreEqual(1, batch.PointCount, "an icon is point-placement");
            Assert.AreEqual(SymbolLabelBatch.Kind.Point, batch.Kinds[0]);
            Assert.AreEqual(LabelKind.Icon, batch.Points[0].AtlasKind, "the discriminator must survive the batch build");
            Assert.AreEqual(1, batch.PointQuadCount[0], "a sprite is exactly one quad");
            Assert.AreEqual(IconQuad.UvTopLeft.x, batch.Quads[batch.PointQuadStart[0]].UvTopLeft.x, 1e-6f);
        }

        [Test]
        public void StagePoint_OverIconBatchDetail_OneBoxNoDoubleScale_EmitCarriesIcon()
        {
            var batch = new SymbolLabelBatch();
            var labels = new List<LabelInstance> { IconLabelInstance() };
            SymbolLabelBatchBuilder.Build(batch, labels, slotCount: 1);

            // Patch this frame's dynamic fields (SymbolLabelBatchBuilder leaves them default — the consumer's job).
            PointStageInput input = batch.Points[0];
            input.ScreenPx = new float2(300f, 150f);
            input.Projected = true;

            var boxes = new LabelBox[4];
            var quads = new PlacedQuad[4];
            var candidates = new LabelCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0;

            var quadSpan = new System.ReadOnlySpan<SymbolQuad>(batch.Quads, batch.PointQuadStart[0], batch.PointQuadCount[0]);
            int staged = LabelStagingMath.StagePoint(in input, quadSpan, bearingRadians: 0f,
                viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(1, boxCount, "an icon is a single-box point candidate");
            Assert.AreEqual(1, quadCount);
            Assert.AreEqual(LabelKind.Icon, emit[0].AtlasKind, "emit must carry the icon discriminator (I5a; not yet consumed — I5b)");

            // scale == TextSizePx/OneEm == 1 (no double-scale of an already-baked icon quad): box extent is
            // exactly the icon quad's own bounds, offset by the screen anchor and grown by padding.
            float2 expectedMin = input.ScreenPx + batch.Points[0].BoundsMin - new float2(input.PaddingPx, input.PaddingPx);
            float2 expectedMax = input.ScreenPx + batch.Points[0].BoundsMax + new float2(input.PaddingPx, input.PaddingPx);
            Assert.AreEqual(expectedMin.x, boxes[0].Min.x, 1e-4f);
            Assert.AreEqual(expectedMin.y, boxes[0].Min.y, 1e-4f);
            Assert.AreEqual(expectedMax.x, boxes[0].Max.x, 1e-4f);
            Assert.AreEqual(expectedMax.y, boxes[0].Max.y, 1e-4f);

            LabelCandidate c = candidates[0];
            Assert.AreEqual(1, c.BoxCount);
            Assert.AreEqual(2, c.FeatureIndex);
            Assert.AreEqual(5L, c.TileKey);
        }
    }
}
