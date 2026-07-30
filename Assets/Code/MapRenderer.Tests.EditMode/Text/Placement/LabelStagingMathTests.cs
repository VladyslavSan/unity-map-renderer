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

        // ── B3 (P-B): icon-rotate on the POINT path — the SIGN tooth. 90° is the discriminator on purpose:
        //    a sign-flipped impl passes the 180° case (R(pi) == -I is its own inverse) and fails this one,
        //    which is why liberty's only real value is not what pins the direction. ──
        private static PointStageInput RotatedIconInput(float iconRotateRadians, AlignmentMode alignment)
            => new PointStageInput
            {
                ScreenPx = new float2(100, 50), Depth = 0f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 7, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                RotationAlignment = alignment, Color = new float4(1, 1, 1, 1),
                AtlasKind = LabelKind.Icon, IconRotateRadians = iconRotateRadians,
            };

        [Test]
        public void StagePoint_IconRotate_RotatesTheQuadClockwiseOnScreen_AndComposesWithAlignment()
        {
            var quads = new[] { Cell(6f) };

            // (a) Viewport alignment ⇒ icon-rotate is the WHOLE rotation (bearing contributes nothing). The
            // staged angle is NEGATED icon-rotate: the staging frame turns counter-clockwise on screen for a
            // positive angle and icon-rotate is clockwise-positive, so LabelBearing.IconRotationRadians flips
            // it — see (d), which is what observes that the flip lands the right way round.
            PointStageInput viewportInput = RotatedIconInput(math.PI / 2f, AlignmentMode.Viewport);
            var p = Pools.New();
            LabelStagingMath.StagePoint(in viewportInput, quads,
                bearingRadians: 0.7f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
            Assert.AreEqual(-math.PI / 2f, p.Quads[0].RotationRadians, Tol,
                "viewport + icon-rotate 90 -> exactly -pi/2 (the bearing must not leak in)");

            // (b) Map alignment ⇒ bearing + icon-rotate, one addition, both terms present — and the bearing
            // term is NOT flipped, only the icon-rotate one (a blanket negation would read -0.7 - pi/2).
            PointStageInput mapInput = RotatedIconInput(math.PI / 2f, AlignmentMode.Map);
            var pMap = Pools.New();
            LabelStagingMath.StagePoint(in mapInput, quads,
                bearingRadians: 0.7f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                pMap.Boxes, ref pMap.BoxCount, pMap.Quads, ref pMap.QuadCount, pMap.Candidates, pMap.Emit, ref pMap.EmitCount);
            Assert.AreEqual(0.7f - math.PI / 2f, pMap.Quads[0].RotationRadians, Tol,
                "map + icon-rotate 90 -> bearing - pi/2 composed");

            // (c) The BOX is deliberately NOT rotated by icon-rotate (KL-B1): the point path's box is the
            // unrotated AABB even under a live bearing, so rotating it here would make the convention
            // inconsistent with the case it must match.
            Assert.AreEqual(100f - 12f, p.Boxes[0].Min.x, Tol, "the collision box stays the unrotated AABB");
            Assert.AreEqual(100f + 12f, p.Boxes[0].Max.x, Tol);

            // (d) SIGN, observed on the offsets the quad actually draws with — the same observable, now
            // stated in the frame OffsetPx is ACTUALLY in.
            //
            //   BuildWorldQuad rotates the corners in the quad's y-UP LOCAL frame and then negates Y, which
            //   puts OffsetPx in a y-DOWN SCREEN frame. That negation is the frame flip, not a sense
            //   correction: read through a mirrored axis a rotation reverses, so a positive angle handed to
            //   BuildWorldQuad appears COUNTER-clockwise on screen. This test previously asserted the
            //   opposite (OffsetPx y-up, so the negation supplied the clockwise sense) — two claims that
            //   cannot both hold, and the pair of them mechanised the very convention they assumed. What
            //   settled it is a RENDERED tooth, not a derivation:
            //   SymbolIconRenderSnapshotTests.AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen.
            //
            // So a CLOCKWISE-on-screen quarter-turn is +90 deg in OffsetPx components: (x, y) -> (-y, x).
            // That is what a +90 icon-rotate must produce, since MapLibre defines icon-rotate as clockwise.
            // Drop LabelBearing.IconRotationRadians' negation and this lands at (y, -x) instead — which is
            // precisely the shipped defect, and which the 180 deg case in (e) could never see.
            SymbolQuad cell = Cell(6f);
            var zeroAnchor = new float3(0f, 0f, 0f);
            var white = new float3(1f, 1f, 1f);
            BillboardMath.BuildWorldQuad(in cell, in zeroAnchor, TextQuadLayout.OneEm, in white, 0f,
                in float2.zero, in zeroAnchor, 0f,
                out _, out WorldBillboardVertex unrotatedTopRight,
                out WorldBillboardVertex unrotatedBottomRight, out _);
            BillboardMath.BuildWorldQuad(in cell, in zeroAnchor, TextQuadLayout.OneEm, in white,
                p.Quads[0].RotationRadians, in float2.zero, in zeroAnchor, 0f,
                out _, out WorldBillboardVertex rotatedTopRight, out _, out _);

            float2 before = unrotatedTopRight.OffsetPx;
            var clockwiseQuarterTurn = new float2(-before.y, before.x);
            Assert.AreEqual(clockwiseQuarterTurn.x, rotatedTopRight.OffsetPx.x, Tol,
                "a +90 deg icon-rotate turns the drawn corner offsets CLOCKWISE on screen, which in the " +
                "y-DOWN OffsetPx frame is (x, y) -> (-y, x)");
            Assert.AreEqual(clockwiseQuarterTurn.y, rotatedTopRight.OffsetPx.y, Tol,
                "…in y too — an un-negated icon-rotate lands at (y, -x), i.e. counter-clockwise on screen");
            // Cross-check against a named corner, which is where this reads as a picture rather than as
            // algebra: turn a sprite clockwise by a quarter and its TOP-right corner goes to where its
            // BOTTOM-right corner sat. (The version of this line that expected the top-LEFT corner was
            // describing a counter-clockwise turn — the bug, asserted.)
            Assert.AreEqual(unrotatedBottomRight.OffsetPx.x, rotatedTopRight.OffsetPx.x, Tol);
            Assert.AreEqual(unrotatedBottomRight.OffsetPx.y, rotatedTopRight.OffsetPx.y, Tol);
            Assert.AreEqual(unrotatedTopRight.Uv.x, rotatedTopRight.Uv.x, Tol, "UVs never rotate with the corners");
            Assert.AreEqual(unrotatedTopRight.Uv.y, rotatedTopRight.Uv.y, Tol);

            // (e) 180 deg negates every corner offset exactly (the liberty `_opposite` case). Deliberately
            // sign-BLIND — R(pi) == -I is its own inverse — which is why (d) carries the sign coverage.
            PointStageInput flipInput = RotatedIconInput(math.PI, AlignmentMode.Viewport);
            var pFlip = Pools.New();
            LabelStagingMath.StagePoint(in flipInput, quads,
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                pFlip.Boxes, ref pFlip.BoxCount, pFlip.Quads, ref pFlip.QuadCount, pFlip.Candidates, pFlip.Emit, ref pFlip.EmitCount);
            BillboardMath.BuildWorldQuad(in cell, in zeroAnchor, TextQuadLayout.OneEm, in white,
                pFlip.Quads[0].RotationRadians, in float2.zero, in zeroAnchor, 0f,
                out _, out WorldBillboardVertex flippedTopRight, out _, out _);
            Assert.AreEqual(-unrotatedTopRight.OffsetPx.x, flippedTopRight.OffsetPx.x, Tol, "180 deg negates x");
            Assert.AreEqual(-unrotatedTopRight.OffsetPx.y, flippedTopRight.OffsetPx.y, Tol, "180 deg negates y");
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

        // ── C7 (stage C) — a masked pair leaves every OTHER candidate shape untouched, and does not perturb
        //    the box-range tiling / fade-id uniqueness the collision + fade layers depend on. ──────────────
        [Test]
        public void OptionalPair_DoesNotLeakIntoLoneOrCurvedCandidates_OrBreakRangeTiling()
        {
            var p = Pools.New();
            var quads = new[] { Cell(6f) };
            int candidateCount = 0;

            PointStageInput Lone(int featureIndex, float2 screenPx, long fadeId, LabelKind kind) => new PointStageInput
            {
                ScreenPx = screenPx, Depth = 0f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = featureIndex, TileKey = 7, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                RotationAlignment = AlignmentMode.Viewport, Color = new float4(1, 1, 1, 1),
                AtlasKind = kind, FadeId = fadeId,
            };

            PointStageInput lonePoint = Lone(1, new float2(100, 100), 1001L, LabelKind.Text);
            PointStageInput loneIcon  = Lone(2, new float2(300, 100), 1002L, LabelKind.Icon);
            candidateCount += LabelStagingMath.StagePoint(in lonePoint, quads, 0f, new double2(1920, 1080), candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
            candidateCount += LabelStagingMath.StagePoint(in loneIcon, quads, 0f, new double2(1920, 1080), candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            var curved = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 5, TileKey = 9, Slot = 0,
                TranslateAnchor = TextTranslateAnchor.Viewport, MaxAngleDeg = 45f, KeepUpright = true,
                Color = new float4(1, 1, 1, 1),
            };
            long curvedFadeId = LabelStagingMath.LineFadeId(9, 0, 5, 0);
            candidateCount += LabelStagingMath.StageCurved(in curved,
                new[] { new float2(0, 400), new float2(200, 400) }, new[] { 0f, 0f }, new byte[] { 1, 1 },
                new[] { new double3(0, 0, 0), new double3(200, 0, 0) },
                new[] { new CurvedGlyph { ArcCenter = -8f, Cell = Cell(6f) }, new CurvedGlyph { ArcCenter = 8f, Cell = Cell(6f) } },
                new[] { new LineAnchor(0, 0.5f) },
                new[] { curvedFadeId, LabelStagingMath.LineFadeId(9, 0, 5, -1) }, new byte[] { 0, 0 },
                new float2[2], new float[2], 0f, candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            // A BOTH-optional pair, with a stale last-frame verdict carried in — the shape most likely to leak.
            // Staged LAST on purpose: a pair consumes TWO emits, so any candidate AFTER one legitimately has
            // EmitStart > LabelIndex (§10 P7's identity holds only while every prior candidate emitted once).
            PointStageInput owner = Lone(3, new float2(500, 100), 1003L, LabelKind.Icon);
            owner.PairOptional = true;
            PointStageInput rider = Lone(4, new float2(500, 100), 1004L, LabelKind.Text);
            rider.PairOptional = true;
            rider.TranslatePx = new float2(60f, 0f);
            candidateCount += LabelStagingMath.StagePointPair(in owner, in rider, quads, quads, 0f,
                new double2(1920, 1080), candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount,
                droppedHalvesLastFrame: 0b10);

            Assert.AreEqual(4, candidateCount, "precondition: lone point + lone icon + curved + pair all staged");

            // The three NON-pair candidates keep their pre-stage-C shape exactly.
            foreach (int i in new[] { 0, 1, 2 })
            {
                LabelCandidate c = p.Candidates[i];
                Assert.AreEqual(0, c.OptionalBoxMask, $"candidate[{i}] must carry no optional mask");
                Assert.AreEqual(0, c.DroppedBoxMask, $"candidate[{i}] must carry no dropped mask");
                Assert.AreEqual(1, c.EmitCount, $"candidate[{i}] must have exactly one emit");
                Assert.AreEqual(c.LabelIndex, c.EmitStart, $"candidate[{i}]: EmitStart == LabelIndex");
            }
            Assert.AreEqual(1, p.Candidates[0].BoxCount, "a lone point label is a one-box candidate");
            Assert.AreEqual(1, p.Candidates[1].BoxCount, "a lone icon label is a one-box candidate");
            Assert.AreEqual(2, p.Candidates[2].BoxCount, "the curved label keeps its per-glyph boxes");

            // The pair carries the mask, and the seeded verdict is masked to its optional halves — the RANGE
            // is untouched (shrinking it on a drop would desync the tiling invariant and the node bound).
            LabelCandidate pair = p.Candidates[3];
            Assert.AreEqual(0b11, pair.OptionalBoxMask, "both halves optional");
            Assert.AreEqual(0b10, pair.DroppedBoxMask, "last frame's per-half verdict is seeded for the emit gate");
            Assert.AreEqual(2, pair.BoxCount, "a dropped half must NOT shrink the box range");
            Assert.AreEqual(2, pair.EmitCount, "a dropped half must NOT shrink the emit range");

            Assert.IsFalse(LabelCandidate.TryFindRangeTilingViolation(
                    new System.ReadOnlySpan<LabelCandidate>(p.Candidates, 0, candidateCount), p.BoxCount,
                    out int bad, out int expected),
                $"candidate box ranges must still tile [0,{p.BoxCount}) — candidate[{bad}] expected BoxStart {expected}");

            var seen = new System.Collections.Generic.HashSet<long>();
            for (int i = 0; i < candidateCount; i++)
                Assert.IsTrue(seen.Add(p.Candidates[i].FadeId),
                    $"candidate[{i}]'s FadeId must be unique — a pair contributes ONE id (the owner's), not two");
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
