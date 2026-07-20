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
            public LabelCandidate[] Candidates; public CandidateEmit[] Emit;
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

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, glyphs, anchors,
                fadeIds, wasPlaced, pathScratch, cumScratch, bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit);

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

            int staged = LabelStagingMath.StageCurved(in s, screenPath, depthPath, validPath, glyphs, anchors,
                fadeIds, wasPlaced, pathScratch, cumScratch, bearingRadians: 0f, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit);

            Assert.AreEqual(1, staged);
            for (int q = 0; q < p.QuadCount; q++)
                Assert.AreEqual(tiltRad, p.Quads[q].RotationRadians, Tol);
        }
    }
}
