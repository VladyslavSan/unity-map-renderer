// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// P2 T-2: pins PolylineArcMath.SampleUp's per-glyph sample EXACTLY — a large (90 deg), synthetic per-vertex
// up span, so the difference is not float noise. Every expected value below is spelled out BY HAND (not
// obtained by calling SampleUp or any production sampler).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolStagingMathCurvedUpTests
    {
        private struct Pools
        {
            public SymbolBox[] Boxes; public int BoxCount;
            public PlacedQuad[] Quads; public int QuadCount;
            public SymbolCandidate[] Candidates; public CandidateEmit[] Emit; public int EmitCount;
            public static Pools New(int maxBoxes = 64, int maxQuads = 64, int maxCandidates = 8) => new Pools
            {
                Boxes = new SymbolBox[maxBoxes], Quads = new PlacedQuad[maxQuads],
                Candidates = new SymbolCandidate[maxCandidates], Emit = new CandidateEmit[maxCandidates],
            };
        }

        private static SymbolQuad Cell() => new SymbolQuad
        {
            TopLeft = new float2(-5f, 8f), BottomRight = new float2(5f, -2f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };

        // A straight 100-px line (screen AND world congruent — world embeds the same 2D shape on the XZ
        // plane, so a glyph's screen-arc-derived (seg,t) samples the world/up arrays at the identical
        // fraction). Ups 90 deg apart: (0,1,0) at vertex 0, (1,0,0) at vertex 1.
        [Test]
        public void StageCurved_SurfaceUp_MatchesHandComputedLerpAndNormalize_AtEachGlyphsOwnSample()
        {
            var screenPath = new[] { new float2(0f, 0f), new float2(100f, 0f) };
            var depthPath  = new[] { 0f, 0f };
            var validPath  = new byte[] { 1, 1 };
            var worldPath  = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var worldUpPath = new[] { new float3(0f, 1f, 0f), new float3(1f, 0f, 0f) }; // 90 deg apart

            // 3 glyphs at ArcCenter 0/50/100, scale 1 (TextSizePx == OneEm), centred at the line's midpoint
            // (LineAnchor(0, 0.5) -> centerArc = 50 of a 100-px total) -> symbolCenterBaked = 50, so
            // arc_i = 50 + (ArcCenter_i - 50): arc_0=0, arc_1=50, arc_2=100 -> t_0=0, t_1=0.5, t_2=1.
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 50f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell() },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                // KeepUpright false: the line runs strictly rightward (tangent along +X, cos(0) > 0), so
                // KeepUpright's reversal never triggers here regardless — false keeps the t/seg mapping the
                // simplest to hand-verify.
                MaxAngleDeg = 90f, KeepUpright = false, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { SymbolStagingMath.LineFadeId(1, 0, 1, 0), SymbolStagingMath.LineFadeId(1, 0, 1, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathPoints = new float2[2];
            var cumulativeLengths = new float[2];
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath,
                glyphs, anchors, fadeIds, wasPlaced, pathPoints, cumulativeLengths, bearingRadians: 0f,
                view: default, ordinal: 0, // W3: no view transform ⇒ the pre-W3 screen box, byte-identical
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(3, p.QuadCount, "one quad per glyph");

            // Glyph 0 (t=0): AtWithSegment's arc<=0 branch -> seg=0, t=0 -> SampleUp = lerp(up0, up1, 0) =
            // up0 = (0,1,0), already unit -> no renormalize needed.
            AssertUp(p.Quads[0].SurfaceUp, new float3(0f, 1f, 0f), "glyph 0 (t=0): exactly worldUpPath[0]");

            // Glyph 1 (t=0.5): lerp((0,1,0),(1,0,0),0.5) = (0.5,0.5,0), length = sqrt(0.5) ~ 0.70710678 ->
            // normalized = (0.70710678, 0.70710678, 0).
            float invSqrt2 = 0.70710678f;
            AssertUp(p.Quads[1].SurfaceUp, new float3(invSqrt2, invSqrt2, 0f), "glyph 1 (t=0.5): normalize(lerp) at 45 deg");

            // Glyph 2 (t=1): AtWithSegment's arc>=total branch -> seg=0, t=1 -> SampleUp = lerp(up0,up1,1) =
            // up1 = (1,0,0), already unit.
            AssertUp(p.Quads[2].SurfaceUp, new float3(1f, 0f, 0f), "glyph 2 (t=1): exactly worldUpPath[1]");

            // Every SurfaceUp is unit length (kills a lerp-without-renormalize implementation, which would
            // leave glyph 1 at length sqrt(0.5) ~ 0.707, not 1).
            for (int i = 0; i < p.QuadCount; i++)
                Assert.AreEqual(1.0, math.length(p.Quads[i].SurfaceUp), 1e-5, $"glyph {i}: SurfaceUp must be unit length");
        }

        private static void AssertUp(float3 actual, float3 expected, string message)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-5f, $"{message} (x)");
            Assert.AreEqual(expected.y, actual.y, 1e-5f, $"{message} (y)");
            Assert.AreEqual(expected.z, actual.z, 1e-5f, $"{message} (z)");
        }
    }
}
