// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Lever C step 1: the pure blittable-input staging math extracted from LabelPlacementSystem. Hand-computable
    /// cases (axis-aligned point box, horizontal curved line → zero rotation) pin the geometry; the full byte-parity
    /// gate is the engine EditMode Tick tests that now call this same code.
    /// </summary>
    [TestFixture]
    public class LabelStagingMathTests
    {
        private const float Tol = 1e-4f;

        // Pre-sized output pools + cursors for one staging call (the staging math writes via fixed spans).
        private struct Pools
        {
            public LabelBox[] Boxes; public int BoxCount;
            public PlacedQuad[] Quads; public int QuadCount;
            public LabelCandidate[] Candidates; public CandidateEmit[] Emit;
            public static Pools New(int maxBoxes = 64, int maxQuads = 256, int maxCandidates = 64) => new Pools
            {
                Boxes = new LabelBox[maxBoxes], Quads = new PlacedQuad[maxQuads],
                Candidates = new LabelCandidate[maxCandidates], Emit = new CandidateEmit[maxCandidates],
            };
        }

        private static SymbolQuad Cell(float half) => new SymbolQuad
        {
            TopLeft = new float2(-half, half), BottomRight = new float2(half, -half),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };

        [Test]
        public void StagePoint_AxisAlignedBox_QuadsAndCandidate()
        {
            var s = new PointStageInput
            {
                ScreenPx = new float2(100, 50), Depth = 0.5f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 2f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 7, Slot = 0,
                AllowOverlap = false, IgnorePlacement = false,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                RotationAlignment = AlignmentMode.Viewport, Color = new float4(1, 1, 1, 1),
                FadeId = 123, WasPlacedLastFrame = false,
            };
            var quads = new[] { Cell(6f) };
            var p = Pools.New();

            int staged = LabelStagingMath.StagePoint(in s, quads, bearingRadians: 0f,
                viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(1, p.BoxCount);
            Assert.AreEqual(1, p.QuadCount);
            // scale = 24/24 = 1 → box = anchor + bounds ± padding.
            Assert.AreEqual(new float2(100 - 12 - 2, 50 - 12 - 2).x, p.Boxes[0].Min.x, Tol);
            Assert.AreEqual(new float2(100 - 12 - 2, 50 - 12 - 2).y, p.Boxes[0].Min.y, Tol);
            Assert.AreEqual(100 + 12 + 2, p.Boxes[0].Max.x, Tol);
            Assert.AreEqual(50 + 12 + 2, p.Boxes[0].Max.y, Tol);

            LabelCandidate c = p.Candidates[0];
            Assert.AreEqual(0, c.BoxStart); Assert.AreEqual(1, c.BoxCount);
            Assert.AreEqual(1, c.FeatureIndex); Assert.AreEqual(7, c.TileKey);
            Assert.AreEqual(0, c.LabelIndex); Assert.AreEqual(123, c.FadeId);
            Assert.IsFalse(c.WasPlacedLastFrame);

            Assert.AreEqual(0, p.Emit[0].QuadStart); Assert.AreEqual(1, p.Emit[0].QuadCount);
            Assert.AreEqual(new float2(100, 50).x, p.Quads[0].AnchorScreenPx.x, Tol);
            Assert.AreEqual(TextQuadLayout.OneEm, p.Quads[0].TextSizePx, Tol);
            Assert.AreEqual(0.5f, p.Quads[0].Depth, Tol);
        }

        [Test]
        public void StagePoint_BehindCamera_StagesNothing()
        {
            var s = new PointStageInput { Projected = false };
            var quads = new[] { Cell(6f) };
            var p = Pools.New();
            int staged = LabelStagingMath.StagePoint(in s, quads, 0f, new double2(1920, 1080), 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit);
            Assert.AreEqual(0, staged);
            Assert.AreEqual(0, p.BoxCount);
            Assert.AreEqual(0, p.QuadCount);
        }

        [Test]
        public void StageCurved_HorizontalLine_ZeroRotationBoxAtAnchor()
        {
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 3, TileKey = 9, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 45f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var screenPath = new[] { new float2(0, 0), new float2(100, 0) };
            var depthPath  = new[] { 0.3f, 0.7f };            // mid vertex (index 1) → pathDepth 0.7
            var validPath  = new byte[] { 1, 1 };
            var glyphs     = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(6f) } };
            var anchors    = new[] { new LineAnchor(0, 0.5f) };  // arc distance 50 along the 100-px line
            long fid = LabelStagingMath.LineFadeId(9, 3, 0);
            var fadeIds    = new[] { fid, LabelStagingMath.LineFadeId(9, 3, -1) };
            var wasPlaced  = new byte[] { 0, 0 };
            var pathScratch = new float2[2];
            var cumScratch  = new float[2];
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, glyphs, anchors,
                fadeIds, wasPlaced, pathScratch, cumScratch, bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(1, p.BoxCount);
            Assert.AreEqual(1, p.QuadCount);
            // Horizontal line, rotation 0, scale 1, no padding → axis-aligned 12×12 box centred at (50,0).
            Assert.AreEqual(44f, p.Boxes[0].Min.x, Tol); Assert.AreEqual(-6f, p.Boxes[0].Min.y, Tol);
            Assert.AreEqual(56f, p.Boxes[0].Max.x, Tol); Assert.AreEqual(6f, p.Boxes[0].Max.y, Tol);

            LabelCandidate c = p.Candidates[0];
            Assert.AreEqual(1, c.BoxCount); Assert.AreEqual(fid, c.FadeId);
            Assert.AreEqual(new float2(50, 0).x, p.Quads[0].AnchorScreenPx.x, Tol);
            Assert.AreEqual(0f, p.Quads[0].AnchorScreenPx.y, Tol);
            Assert.AreEqual(0.7f, p.Quads[0].Depth, Tol);
        }

        [Test]
        public void StageCurved_LabelLongerThanLine_StagesNothing()
        {
            var s = new CurvedStageInput { TextSizePx = TextQuadLayout.OneEm, MaxAngleDeg = 45f, KeepUpright = true,
                Color = new float4(1, 1, 1, 1), TranslateAnchor = TextTranslateAnchor.Viewport };
            var screenPath = new[] { new float2(0, 0), new float2(10, 0) };   // 10-px line
            var depthPath  = new[] { 0f, 0f };
            var validPath  = new byte[] { 1, 1 };
            // glyph span 0..100 baked-px * scale 1 = 100 px label, longer than the 10-px line → never fits.
            var glyphs     = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(6f) },
                                     new CurvedGlyph { ArcCenter = 100f, Cell = Cell(6f) } };
            var anchors    = new[] { new LineAnchor(0, 0.5f) };
            var fadeIds    = new[] { 0L, 0L };
            var wasPlaced  = new byte[] { 0, 0 };
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, glyphs, anchors,
                fadeIds, wasPlaced, new float2[2], new float[2], 0f, 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit);

            Assert.AreEqual(0, staged);
            Assert.AreEqual(0, p.BoxCount);
        }
    }
}
