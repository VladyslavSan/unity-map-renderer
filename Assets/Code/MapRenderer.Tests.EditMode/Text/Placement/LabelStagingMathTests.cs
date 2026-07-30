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
            public LabelCandidate[] Candidates; public CandidateEmit[] Emit; public int EmitCount;
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
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

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
            // §10 P7: an ORDINARY (unpaired) label's candidate satisfies EmitCount == 1, EmitStart == LabelIndex —
            // byte-identical to the pre-§10 shape (one record, one candidate, one emit).
            Assert.AreEqual(1, c.EmitCount, "an ordinary point label has exactly one emit");
            Assert.AreEqual(c.LabelIndex, c.EmitStart, "EmitStart == LabelIndex for an unpaired candidate");

            Assert.AreEqual(0, p.Emit[0].QuadStart); Assert.AreEqual(1, p.Emit[0].QuadCount);
            Assert.AreEqual(new float2(100, 50).x, p.Quads[0].AnchorScreenPx.x, Tol);
            Assert.AreEqual(TextQuadLayout.OneEm, p.Quads[0].TextSizePx, Tol);
            Assert.AreEqual(0.5f, p.Quads[0].Depth, Tol);
        }

        // I5a: AtlasKind (the icon/text discriminator) must ride through emit[ordinal] unchanged — a plain
        // pass-through, not yet consumed anywhere, but the thread must not silently drop it.
        [Test]
        public void StagePoint_IconAtlasKind_CarriesThroughToEmit()
        {
            var s = new PointStageInput
            {
                ScreenPx = new float2(0, 0), Depth = 0f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 0, TileKey = 0, Slot = 0,
                TranslateAnchor = TextTranslateAnchor.Viewport, RotationAlignment = AlignmentMode.Viewport,
                Color = new float4(1, 1, 1, 1),
                AtlasKind = LabelKind.Icon,
            };
            var iconQuad = new SymbolQuad
            {
                TopLeft = new float2(-8, 8), BottomRight = new float2(8, -8),
                UvTopLeft = new float2(0.1f, 0.2f), UvBottomRight = new float2(0.3f, 0.4f),
            };
            var quads = new[] { iconQuad };
            var p = Pools.New();

            int staged = LabelStagingMath.StagePoint(in s, quads, bearingRadians: 0f,
                viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(LabelKind.Icon, p.Emit[0].AtlasKind, "emit must carry the icon discriminator");
            Assert.AreEqual(iconQuad.UvTopLeft.x, p.Quads[0].Quad.UvTopLeft.x, Tol);
            Assert.AreEqual(iconQuad.UvTopLeft.y, p.Quads[0].Quad.UvTopLeft.y, Tol);
        }

        [Test]
        public void StagePoint_DefaultAtlasKind_IsText()
        {
            var s = new PointStageInput
            {
                ScreenPx = new float2(0, 0), Projected = true,
                BoundsMin = new float2(-1, -1), BoundsMax = new float2(1, 1),
                TextSizePx = TextQuadLayout.OneEm,
                TranslateAnchor = TextTranslateAnchor.Viewport, RotationAlignment = AlignmentMode.Viewport,
                Color = new float4(1, 1, 1, 1),
                // AtlasKind left at its default — every pre-I5a caller never sets it.
            };
            var quads = new[] { Cell(1f) };
            var p = Pools.New();

            LabelStagingMath.StagePoint(in s, quads, 0f, new double2(1920, 1080), 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(LabelKind.Text, p.Emit[0].AtlasKind, "default AtlasKind must be Text (zero value)");
        }

        [Test]
        public void StagePoint_BehindCamera_StagesNothing()
        {
            var s = new PointStageInput { Projected = false };
            var quads = new[] { Cell(6f) };
            var p = Pools.New();
            int staged = LabelStagingMath.StagePoint(in s, quads, 0f, new double2(1920, 1080), 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
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
            // World path mirrors the screen path 1:1 (index-aligned, per LabelStageJob's contract) — a flat
            // east-only line at Y=Z=0, TileOriginRender left at its float3-zero default.
            var worldPath  = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var glyphs     = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(6f) } };
            var anchors    = new[] { new LineAnchor(0, 0.5f) };  // arc distance 50 along the 100-px line
            long fid = LabelStagingMath.LineFadeId(9, 0, 3, 0);
            var fadeIds    = new[] { fid, LabelStagingMath.LineFadeId(9, 0, 3, -1) };
            var wasPlaced  = new byte[] { 0, 0 };
            var pathScratch = new float2[2];
            var cumScratch  = new float[2];
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, glyphs, anchors,
                fadeIds, wasPlaced, pathScratch, cumScratch, bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(1, p.BoxCount);
            Assert.AreEqual(1, p.QuadCount);
            // Horizontal line, rotation 0, scale 1, no padding → axis-aligned 12×12 box centred at (50,0).
            Assert.AreEqual(44f, p.Boxes[0].Min.x, Tol); Assert.AreEqual(-6f, p.Boxes[0].Min.y, Tol);
            Assert.AreEqual(56f, p.Boxes[0].Max.x, Tol); Assert.AreEqual(6f, p.Boxes[0].Max.y, Tol);

            LabelCandidate c = p.Candidates[0];
            Assert.AreEqual(1, c.BoxCount); Assert.AreEqual(fid, c.FadeId);
            // §10 P7: a curved label is never paired — EmitCount == 1, EmitStart == LabelIndex, unchanged.
            Assert.AreEqual(1, c.EmitCount, "a curved label has exactly one emit");
            Assert.AreEqual(c.LabelIndex, c.EmitStart, "EmitStart == LabelIndex for a curved candidate");
            Assert.AreEqual(new float2(50, 0).x, p.Quads[0].AnchorScreenPx.x, Tol);
            Assert.AreEqual(0f, p.Quads[0].AnchorScreenPx.y, Tol);
            Assert.AreEqual(0.7f, p.Quads[0].Depth, Tol);
            // Stage AC: the world sample at the same arc=50 midpoint, narrowed against the default-zero
            // TileOriginRender — AnchorLocal == the world point itself; Tangent is the unit +X direction.
            Assert.AreEqual(50f, p.Quads[0].AnchorLocal.x, Tol);
            Assert.AreEqual(0f, p.Quads[0].AnchorLocal.y, Tol);
            Assert.AreEqual(0f, p.Quads[0].AnchorLocal.z, Tol);
            Assert.AreEqual(1f, p.Quads[0].Tangent.x, Tol);
            Assert.AreEqual(0f, p.Quads[0].Tangent.y, Tol);
        }

        [Test]
        public void StageCurved_LabelLongerThanLine_StagesNothing()
        {
            var s = new CurvedStageInput { TextSizePx = TextQuadLayout.OneEm, MaxAngleDeg = 45f, KeepUpright = true,
                Color = new float4(1, 1, 1, 1), TranslateAnchor = TextTranslateAnchor.Viewport };
            var screenPath = new[] { new float2(0, 0), new float2(10, 0) };   // 10-px line
            var depthPath  = new[] { 0f, 0f };
            var validPath  = new byte[] { 1, 1 };
            var worldPath  = new[] { new double3(0, 0, 0), new double3(10, 0, 0) };
            // glyph span 0..100 baked-px * scale 1 = 100 px label, longer than the 10-px line → never fits.
            var glyphs     = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(6f) },
                                     new CurvedGlyph { ArcCenter = 100f, Cell = Cell(6f) } };
            var anchors    = new[] { new LineAnchor(0, 0.5f) };
            var fadeIds    = new[] { 0L, 0L };
            var wasPlaced  = new byte[] { 0, 0 };
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, glyphs, anchors,
                fadeIds, wasPlaced, new float2[2], new float[2], 0f, 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(0, staged);
            Assert.AreEqual(0, p.BoxCount);
        }

        // Regression: LineFadeId must fold in the LAYER dimension. SymbolFeatureExtractor restarts its FeatureIndex
        // ordinal per layer, so two different roads in different line-symbol layers of ONE tile share
        // (tileKey, featureIndex, anchorIndex). Without the layer id their fade ids collide — and a per-candidate fade
        // id collision makes two live labels fight over one opacity and stick at a partial value forever (the
        // "line labels overlap and one never fades" bug). RED against the pre-fix 3-arg id (no layer term).
        [Test]
        public void LineFadeId_DiffersByLayer_ForSameTileFeatureAnchor()
        {
            const long tile = 246313140626018L;
            long layerA = LabelStagingMath.LineFadeId(tile, layerId: 0, featureIndex: 0, anchorIndex: 0);
            long layerB = LabelStagingMath.LineFadeId(tile, layerId: 1, featureIndex: 0, anchorIndex: 0);
            Assert.AreNotEqual(layerA, layerB, "two layers' feature-0 anchor-0 must not share a fade id");

            // Stable identity: the same (tile, layer, feature, anchor) always hashes identically (cross-frame fade
            // continuity depends on it), and the other dimensions still distinguish.
            Assert.AreEqual(layerA, LabelStagingMath.LineFadeId(tile, 0, 0, 0), "same key must be stable");
            Assert.AreNotEqual(layerA, LabelStagingMath.LineFadeId(tile, 0, 1, 0), "feature dimension distinguishes");
            Assert.AreNotEqual(layerA, LabelStagingMath.LineFadeId(tile, 0, 0, 1), "anchor dimension distinguishes");
            Assert.AreNotEqual(layerA, LabelStagingMath.LineFadeId(tile + 1, 0, 0, 0), "tile dimension distinguishes");
        }
    }
}
