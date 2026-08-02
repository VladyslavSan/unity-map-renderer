// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Root-cause regression for the curved-label glyph-overlap defect: a glyph straddling a polyline VERTEX
    /// must be rotated by the CHORD across its own footprint (blending the two segment angles either side of
    /// the vertex), not the raw single-segment tangent a point query (<see cref="PolylineArcMath.At"/>) hands
    /// back. The single-segment tangent gives every glyph within a segment the SAME rigid rotation, so a glyph
    /// whose footprint straddles a bend collides its inner corner with its neighbour on the concave side — see
    /// <see cref="LabelStagingMath"/>'s private <c>StageCurvedAnchor</c>.
    /// </summary>
    [TestFixture]
    public class LabelStagingMathCurvedVertexTests
    {
        private const float Tol = 1e-3f;

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

        private static SymbolQuad Cell(float halfWidth) => new SymbolQuad
        {
            TopLeft = new float2(-halfWidth, 6f), BottomRight = new float2(halfWidth, -6f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };

        [Test]
        public void StageCurved_GlyphStraddlingVertex_RotatesToBlendedChordAngle_NotRawSegmentAngle()
        {
            // Bent polyline: segment A at 0°, length 100; segment B at +30°, length 100. Vertex at arc=100.
            float bendRad = math.radians(30f);
            var screenPath = new[]
            {
                new float2(0f, 0f),
                new float2(100f, 0f),
                new float2(100f + 100f * math.cos(bendRad), 100f * math.sin(bendRad)),
            };
            var depthPath = new[] { 0f, 0f, 0f };
            var validPath = new byte[] { 1, 1, 1 };
            // World path mirrors the screen path 1:1 (index-aligned) — a flat XZ-plane bend at the same angle.
            var worldPath = new[]
            {
                new double3(0, 0, 0),
                new double3(100, 0, 0),
                new double3(100 + 100 * math.cos(bendRad), 0, 100 * math.sin(bendRad)),
            };

            // 3 glyphs, ArcCenter cumulative-advance midpoints (40-baked-px advance each), scale = 1
            // (TextSizePx == OneEm). The MIDDLE glyph is 20-baked-px half-wide and lands exactly on the vertex
            // once staged at centerArc=100 → its footprint spans arc [80,120], straddling the bend symmetrically.
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 20f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(20f) },   // straddles the vertex
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(1, 0f) };  // arc distance 100 == the vertex
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { LabelStagingMath.LineFadeId(1, 0, 1, 0), LabelStagingMath.LineFadeId(1, 0, 1, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathScratch = new float2[3];
            var cumScratch = new float[3];
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, glyphs, anchors,
                fadeIds, wasPlaced, pathScratch, cumScratch, bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(3, p.QuadCount);

            float straddlingRotation = p.Quads[1].RotationRadians;

            // The chord fix blends the two segment angles (0° and 30°) in proportion to the symmetric 20-px
            // offset on each side of the vertex → exactly 15° for THIS symmetric geometry (not a general rule;
            // an asymmetric straddle blends in proportion to the split instead of an even 50/50).
            float expectedBlendedDeg = 15f;
            Assert.AreEqual(math.radians(expectedBlendedDeg), straddlingRotation, Tol,
                "straddling glyph should rotate to the blended chord angle, not a raw single-segment angle");

            // Explicitly rule out the two OLD (pre-fix) single-segment answers this glyph could have snapped to.
            Assert.That(math.abs(straddlingRotation - 0f) > 0.05f, "must not equal segment A's raw angle (0°)");
            Assert.That(math.abs(straddlingRotation - bendRad) > 0.05f, "must not equal segment B's raw angle (30°)");
        }

        [Test]
        public void StageCurved_StraightLine_RotationStillEqualsSegmentAngle()
        {
            // Sanity: the chord fix must be a no-op when the label span contains no vertex — a straight
            // line's chord across any footprint is collinear with the segment, so rotation is unchanged.
            float tiltRad = math.radians(20f);
            float2 direction = new float2(math.cos(tiltRad), math.sin(tiltRad));
            var screenPath = new[] { float2.zero, direction * 200f };
            var depthPath = new[] { 0f, 0f };
            var validPath = new byte[] { 1, 1 };
            // World path mirrors the screen path 1:1 (index-aligned) on the flat XZ plane.
            var worldPath = new[] { double3.zero, new double3(direction.x, 0, direction.y) * 200.0 };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 20f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(20f) },
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 2, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { LabelStagingMath.LineFadeId(1, 0, 2, 0), LabelStagingMath.LineFadeId(1, 0, 2, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathScratch = new float2[2];
            var cumScratch = new float[2];
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, glyphs, anchors,
                fadeIds, wasPlaced, pathScratch, cumScratch, bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            for (int q = 0; q < p.QuadCount; q++)
                Assert.AreEqual(tiltRad, p.Quads[q].RotationRadians, Tol);
        }

        // ── A8 (P-B non-regression): curved TEXT must be untouched by the icon work — it leaves
        //    CurvedStageInput.AtlasKind/IconRotateRadians at their defaults, so the emit is the same Text /
        //    zero-rotation record it always was. This is the tooth that goes RED if a future change routes
        //    curved text through the icon arm (or defaults AtlasKind the wrong way). ──
        [Test]
        public void StageCurved_Text_LeavesTheEmitOnTheGlyphAtlas_WithNoExtraRotation()
        {
            var screenPath = new[] { float2.zero, new float2(400f, 0f) };
            var worldPath = new[] { double3.zero, new double3(400, 0, 0) };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 20f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            // Every icon-specific field left DEFAULT — exactly what BuildCurvedInput produces for a text label.
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 4, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { LabelStagingMath.LineFadeId(1, 0, 4, 0), LabelStagingMath.LineFadeId(1, 0, 4, -1) };
            var p = Pools.New();
            int boxCountBefore = p.BoxCount;

            int staged = LabelStagingMath.StageCurved(in s, screenPath, new[] { 0f, 0f }, new byte[] { 1, 1 },
                worldPath, glyphs, anchors, fadeIds, new byte[] { 0, 0 }, new float2[2], new float[2],
                bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(LabelKind.Text, p.Emit[0].AtlasKind, "curved text must still sample the GLYPH atlas");
            Assert.AreEqual(0f, p.Emit[0].ExtraRotationRadians, 1e-9f,
                "curved text carries no icon-rotate — the renderer must see the same 0f it used to hardcode");
            Assert.AreEqual(glyphs.Length, p.BoxCount - boxCountBefore, "one box per glyph, unchanged");
        }

        // ── A4 (P-B): a ONE-GLYPH curved ICON stages one candidate per anchor, routed to the SPRITE atlas
        //    and rotated to the projected tangent — the whole point of reusing the curved path for icons. ──
        [Test]
        public void StageCurved_OneGlyphIcon_StagesPerAnchor_IntoTheIconAtlas_WithARotatedBox()
        {
            // A 45° line, so the staged box is genuinely ROTATED (an axis-aligned line could not discriminate).
            float angle = math.radians(45f);
            float2 dir = new float2(math.cos(angle), math.sin(angle));
            var screenPath = new[] { float2.zero, dir * 600f };
            var depthPath = new[] { 0f, 0f };
            var validPath = new byte[] { 1, 1 };
            var worldPath = new[] { double3.zero, new double3(dir.x, 0, dir.y) * 600.0 };

            // A deliberately WIDE cell (24 x 12) so the rotated box is measurably wider in x than the cell.
            var iconCell = new SymbolQuad
            {
                TopLeft = new float2(-12f, 6f), BottomRight = new float2(12f, -6f),
                UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
            };
            var glyphs = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = iconCell } };
            var anchors = new[] { new LineAnchor(0, 0.25f), new LineAnchor(0, 0.75f) };
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 9, TileKey = 5, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 45f, KeepUpright = false, Color = new float4(1, 1, 1, 1),
                AtlasKind = LabelKind.Icon,
            };
            var fadeIds = new[]
            {
                LabelStagingMath.LineFadeId(5, 0, 9, 0), LabelStagingMath.LineFadeId(5, 0, 9, 1),
                LabelStagingMath.LineFadeId(5, 0, 9, -1),
            };
            var wasPlaced = new byte[] { 0, 0, 0 };
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, glyphs,
                anchors, fadeIds, wasPlaced, new float2[2], new float[2], bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(anchors.Length, staged, "one candidate per along-line anchor");
            Assert.AreEqual(anchors.Length, p.EmitCount, "one emit per candidate");
            for (int c = 0; c < staged; c++)
            {
                Assert.AreEqual(1, p.Candidates[c].BoxCount, "a one-glyph label has exactly one box");
                Assert.AreEqual(1, p.Candidates[c].EmitCount, "…and exactly one emit");
                // An impl that forgot AtlasKind would route these quads to the GLYPH texture and draw garbage.
                Assert.AreEqual(LabelKind.Icon, p.Emit[c].AtlasKind, "the emit must sample the SPRITE atlas");
                // An impl that routed icons through StagePoint would leave AlongLine false and lose the tangent.
                Assert.IsTrue(p.Emit[c].AlongLine, "an along-line icon must take the curved/world emit branch");
                Assert.IsTrue(p.Emit[c].IsWorld, "…and the world-anchored draw sink");
            }

            // The staged box IS the rotated-glyph box, not the axis-aligned cell: assert against
            // LabelBox.BuildRotatedGlyph over the same inputs, with the unrotated width as the precondition
            // that the 45° case is non-degenerate.
            LabelBox actual = p.Boxes[0];
            var expected = LabelBox.BuildRotatedGlyph(p.Quads[0].AnchorScreenPx, iconCell,
                s.TextSizePx, p.Quads[0].RotationRadians, s.PaddingPx, glyphs[0].CellSkirt);
            Assert.AreEqual(expected.Min.x, actual.Min.x, Tol, "box Min.x");
            Assert.AreEqual(expected.Min.y, actual.Min.y, Tol, "box Min.y");
            Assert.AreEqual(expected.Max.x, actual.Max.x, Tol, "box Max.x");
            Assert.AreEqual(expected.Max.y, actual.Max.y, Tol, "box Max.y");

            float unrotatedWidth = iconCell.BottomRight.x - iconCell.TopLeft.x;
            Assert.AreEqual(24f, unrotatedWidth, Tol, "precondition: the cell is 24 px wide unrotated");
            Assert.Greater(actual.Max.x - actual.Min.x, unrotatedWidth + 1f,
                "a 45°-rotated wide cell must bound STRICTLY wider in x than the cell itself");
            Assert.AreEqual(math.radians(45f), p.Quads[0].RotationRadians, Tol,
                "the quad rotates to the 45° line tangent");
        }

        [Test]
        public void StageCurved_TextTranslate_IsCarriedOntoTheWorldEmitDelta()
        {
            // Regression (dual-review, Codex): the curved world path is the ONLY live sink now, and
            // WorldLabelRenderer.Emit adds emit.TranslateDeltaPx to every world corner. If StageCurvedAnchor's
            // emit leaves TranslateDeltaPx defaulted to zero (the bug), any curved label with a nonzero
            // `text-translate` renders at the UNtranslated position in production. Assert the delta is carried.
            var screenPath = new[] { float2.zero, new float2(200f, 0f) };
            var depthPath = new[] { 0f, 0f };
            var validPath = new byte[] { 1, 1 };
            var worldPath = new[] { double3.zero, new double3(200, 0, 0) };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 40f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 80f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            // Viewport anchor, bearing 0 ⇒ ApplyTranslate delta = (tx, -ty) = (7, 3) for translate (7, -3).
            var translatePx = new float2(7f, -3f);
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 3, TileKey = 1, Slot = 0,
                TranslatePx = translatePx, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { LabelStagingMath.LineFadeId(1, 0, 3, 0), LabelStagingMath.LineFadeId(1, 0, 3, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathScratch = new float2[2];
            var cumScratch = new float[2];
            var p = Pools.New();

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, glyphs, anchors,
                fadeIds, wasPlaced, pathScratch, cumScratch, bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.IsTrue(p.Emit[0].IsWorld && p.Emit[0].AlongLine, "curved must route to the world sink");
            float2 expectedDelta = LabelTranslate.ApplyTranslate(float2.zero, translatePx, TextTranslateAnchor.Viewport, 0f);
            Assert.AreEqual(expectedDelta.x, p.Emit[0].TranslateDeltaPx.x, Tol, "world emit must carry text-translate.x");
            Assert.AreEqual(expectedDelta.y, p.Emit[0].TranslateDeltaPx.y, Tol, "world emit must carry text-translate.y");
            Assert.That(math.abs(p.Emit[0].TranslateDeltaPx.x) > Tol || math.abs(p.Emit[0].TranslateDeltaPx.y) > Tol,
                "delta must be nonzero for a nonzero translate (guards the defaulted-to-zero bug)");
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // C13 — the curved collision box bounds the icon's INK, not its transparent border.
        //
        // An along-line icon's cell now carries CurvedGlyph.CellSkirt: the border SpriteSheet's padded
        // repack laid around the sprite, which the cell DRAWS (that is what antialiases the silhouette) but
        // which is not ink. Collision must run on the ink, or every along-line icon's footprint silently
        // grows and changes which labels win.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(45f)]
        [TestCase(90f)]
        [TestCase(137f)]
        public void BuildRotatedGlyph_WithACellSkirt_EqualsTheSameCellPreShrunkByIt(float rotationDeg)
        {
            const float skirt = 3f;
            var anchor = new float2(120f, 75f);
            float rotation = math.radians(rotationDeg);

            var padded = new SymbolQuad
            {
                TopLeft = new float2(-20f, 9f), BottomRight = new float2(20f, -9f), LineIndex = 0,
            };
            var content = new SymbolQuad
            {
                TopLeft = padded.TopLeft + new float2(skirt, -skirt),
                BottomRight = padded.BottomRight - new float2(skirt, -skirt),
                LineIndex = 0,
            };

            LabelBox withSkirt = LabelBox.BuildRotatedGlyph(anchor, padded, 32f, rotation, 4f, skirt);
            LabelBox preShrunk = LabelBox.BuildRotatedGlyph(anchor, content, 32f, rotation, 4f, 0f);

            Assert.AreEqual(preShrunk.Min.x, withSkirt.Min.x, Tol, "Min.x");
            Assert.AreEqual(preShrunk.Min.y, withSkirt.Min.y, Tol, "Min.y");
            Assert.AreEqual(preShrunk.Max.x, withSkirt.Max.x, Tol, "Max.x");
            Assert.AreEqual(preShrunk.Max.y, withSkirt.Max.y, Tol, "Max.y");

            // Non-vacuity: the skirt must actually be REMOVED, not merely accepted. Against the same padded
            // cell with skirt 0 the box has to be strictly larger.
            LabelBox unshrunk = LabelBox.BuildRotatedGlyph(anchor, padded, 32f, rotation, 4f, 0f);
            Assert.Greater(unshrunk.Max.x - unshrunk.Min.x, (withSkirt.Max.x - withSkirt.Min.x) + Tol,
                "a non-zero cell skirt must SHRINK the box — otherwise this tooth proves nothing");
        }

        /// <summary>Text parity: a text glyph carries <c>CellSkirt == 0</c>, and at 0 the box must be the
        /// plain AABB of the four rotated cell corners plus padding — the pre-skirt formula, independently
        /// recomputed here rather than restated from the implementation.</summary>
        [TestCase(0f)]
        [TestCase(45f)]
        [TestCase(200f)]
        public void BuildRotatedGlyph_AtZeroSkirt_IsThePlainRotatedCornerAabb(float rotationDeg)
        {
            var anchor = new float2(-40f, 12f);
            float rotation = math.radians(rotationDeg);
            const float textSizePx = 48f, paddingPx = 2.5f;
            var cell = new SymbolQuad
            {
                TopLeft = new float2(-7f, 11f), BottomRight = new float2(5f, -3f), LineIndex = 0,
            };

            LabelBox actual = LabelBox.BuildRotatedGlyph(anchor, cell, textSizePx, rotation, paddingPx, 0f);

            float scale = textSizePx / TextQuadLayout.OneEm;
            math.sincos(rotation, out float sin, out float cos);
            float2 Rotate(float2 v) => new float2(cos * v.x - sin * v.y, sin * v.x + cos * v.y);
            var corners = new[]
            {
                anchor + Rotate(cell.TopLeft * scale),
                anchor + Rotate(new float2(cell.BottomRight.x, cell.TopLeft.y) * scale),
                anchor + Rotate(cell.BottomRight * scale),
                anchor + Rotate(new float2(cell.TopLeft.x, cell.BottomRight.y) * scale),
            };
            float2 min = corners[0], max = corners[0];
            foreach (float2 c in corners) { min = math.min(min, c); max = math.max(max, c); }

            Assert.AreEqual(min.x - paddingPx, actual.Min.x, Tol, "Min.x");
            Assert.AreEqual(min.y - paddingPx, actual.Min.y, Tol, "Min.y");
            Assert.AreEqual(max.x + paddingPx, actual.Max.x, Tol, "Max.x");
            Assert.AreEqual(max.y + paddingPx, actual.Max.y, Tol, "Max.y");
        }
    }
}
