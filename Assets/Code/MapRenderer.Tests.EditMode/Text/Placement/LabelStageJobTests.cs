// Unity EditMode only — needs the job runtime (NativeArray / IJob). NOT registered in core-tests.csproj.
// NOTE: EditMode batch runs the job Burst-compiled; this differential validates that the Burst LabelStageJob and
// the managed LabelStagingMath reference make the SAME placement DECISIONS. Unlike LabelCollisionJob (integer/branch
// logic → bit-identical), staging has float trig (sincos/atan2 per glyph), so Burst may differ ~1 ULP from Mono.
// The hazard is a 1-ULP flip at a text-max-angle / span-fit boundary changing WHICH anchors stage — that shows up
// as a different staged/box/quad COUNT (asserted EXACT) or candidate integer field, not as sub-ULP geometry noise
// (asserted within a tight tolerance). Runs over bent lines whose curvature sits near text-max-angle.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Lever C step 3b: the Burst <see cref="LabelStageJob"/> must make the same placement decisions as the managed
    /// <see cref="LabelStagingMath"/> reference it wraps — the differential the codebase requires for a Burst numeric
    /// port (cf. <see cref="LabelCollisionJobTests"/>).
    /// </summary>
    [TestFixture]
    public class LabelStageJobTests
    {
        private const float GeomTol = 1e-2f; // px — absorbs Burst-vs-Mono ULP trig noise; a real flip moves counts

        private struct Result
        {
            public int Staged, BoxCount, QuadCount;
            public LabelBox[] Boxes; public PlacedQuad[] Quads; public LabelCandidate[] Candidates;
        }

        // The managed reference: LabelStagingMath.StageCurved over one curved label.
        private static Result Managed(in CurvedStageInput s, float2[] screen, float[] depth, byte[] valid,
            CurvedGlyph[] glyphs, LineAnchor[] anchors, long[] fadeIds, byte[] wasPlaced, float bearing)
        {
            int maxBoxes = (anchors.Length + 1) * glyphs.Length + 1;
            var boxes = new LabelBox[maxBoxes]; var quads = new PlacedQuad[maxBoxes];
            var cands = new LabelCandidate[anchors.Length + 1]; var emit = new CandidateEmit[anchors.Length + 1];
            int bc = 0, qc = 0;
            int staged = LabelStagingMath.StageCurved(in s, screen, depth, valid, glyphs, anchors, fadeIds, wasPlaced,
                new float2[screen.Length], new float[screen.Length], bearing, 0,
                boxes, ref bc, quads, ref qc, cands, emit);
            return new Result { Staged = staged, BoxCount = bc, QuadCount = qc, Boxes = boxes, Quads = quads, Candidates = cands };
        }

        // The Burst path: LabelStageJob over a one-record curved batch mirror.
        private static Result Native(in CurvedStageInput s, float2[] screen, float[] depth, byte[] valid,
            CurvedGlyph[] glyphs, LineAnchor[] anchors, long[] fadeIds, byte[] wasPlaced, float bearing)
        {
            int pathLen = screen.Length;
            int maxBoxes = (anchors.Length + 1) * glyphs.Length + 1;
            var alloc = Allocator.TempJob;

            NativeArray<T> One<T>(T v) where T : unmanaged { var a = new NativeArray<T>(1, alloc); a[0] = v; return a; }
            NativeArray<T> From<T>(T[] src) where T : unmanaged { var a = new NativeArray<T>(src.Length, alloc); for (int i = 0; i < src.Length; i++) a[i] = src[i]; return a; }

            var kinds = One<byte>(1); var detail = One(0); var worldCount = One(pathLen);
            var points = new NativeArray<PointStageInput>(1, alloc); var pqs = One(0); var pqc = One(0);
            var curveds = One(s);
            var cgs = One(0); var cgc = One(glyphs.Length); var cas = One(0); var cac = One(anchors.Length); var cafs = One(0);
            var nQuads = new NativeArray<SymbolQuad>(1, alloc);
            var nGlyphs = From(glyphs); var nAnchors = From(anchors); var nFade = From(fadeIds);
            var pointOffset = One(0); var nScreen = From(screen); var nDepth = From(depth); var nValid = From(valid);
            var pwp = One<byte>(0); var awp = From(wasPlaced);
            var path = new NativeArray<float2>(pathLen, alloc); var cum = new NativeArray<float>(pathLen, alloc);
            var oBoxes = new NativeArray<LabelBox>(maxBoxes, alloc); var oQuads = new NativeArray<PlacedQuad>(maxBoxes, alloc);
            var oCands = new NativeArray<LabelCandidate>(anchors.Length + 1, alloc); var oEmit = new NativeArray<CandidateEmit>(anchors.Length + 1, alloc);
            var counts = new NativeArray<int>(3, alloc);

            new LabelStageJob
            {
                Kinds = kinds, Detail = detail, WorldCount = worldCount, Count = 1,
                Points = points, PointQuadStart = pqs, PointQuadCount = pqc,
                Curveds = curveds, CurvedGlyphStart = cgs, CurvedGlyphCount = cgc,
                CurvedAnchorStart = cas, CurvedAnchorCount = cac, CurvedAnchorFadeStart = cafs,
                Quads = nQuads, Glyphs = nGlyphs, Anchors = nAnchors, AnchorFadeIds = nFade,
                PointOffset = pointOffset, Screen = nScreen, Depth = nDepth, Valid = nValid,
                PointWasPlaced = pwp, AnchorWasPlaced = awp, Bearing = bearing, Viewport = new double2(1920, 1080),
                PathScratch = path, CumScratch = cum,
                Boxes = oBoxes, StagedQuads = oQuads, Candidates = oCands, Emit = oEmit, OutCounts = counts,
            }.Run();

            var r = new Result
            {
                Staged = counts[0], BoxCount = counts[1], QuadCount = counts[2],
                Boxes = oBoxes.ToArray(), Quads = oQuads.ToArray(), Candidates = oCands.ToArray(),
            };
            kinds.Dispose(); detail.Dispose(); worldCount.Dispose(); points.Dispose(); pqs.Dispose(); pqc.Dispose();
            curveds.Dispose(); cgs.Dispose(); cgc.Dispose(); cas.Dispose(); cac.Dispose(); cafs.Dispose();
            nQuads.Dispose(); nGlyphs.Dispose(); nAnchors.Dispose(); nFade.Dispose();
            pointOffset.Dispose(); nScreen.Dispose(); nDepth.Dispose(); nValid.Dispose(); pwp.Dispose(); awp.Dispose();
            path.Dispose(); cum.Dispose(); oBoxes.Dispose(); oQuads.Dispose(); oCands.Dispose(); oEmit.Dispose(); counts.Dispose();
            return r;
        }

        // A curved label along a polyline with a bend of `bendDeg` at its middle, `maxAngleDeg` = text-max-angle.
        [Test]
        public void BurstStage_MatchesManaged_CurvedBends(
            [Values(0f, 10f, 29f, 31f, 44f, 46f, 90f)] float bendDeg,
            [Values(30f, 45f)] float maxAngleDeg)
        {
            // A 3-vertex line: straight run, then a bend of bendDeg. Glyphs span the joint so the per-glyph tangent
            // delta straddles maxAngleDeg near the boundary values (29/31, 44/46).
            float rad = math.radians(bendDeg);
            var screen = new[] { new float2(0, 0), new float2(60, 0), new float2(60 + 60 * math.cos(rad), 60 * math.sin(rad)) };
            var depth  = new[] { 0.5f, 0.5f, 0.5f };
            var valid  = new byte[] { 1, 1, 1 };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 40f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 55f, Cell = Cell() }, // straddles the joint at arc 60
                new CurvedGlyph { ArcCenter = 70f, Cell = Cell() },
            };
            var anchors = new[] { new LineAnchor(0, 1f) }; // anchor at the joint (arc 60)
            var fadeIds = new[] { 111L, 222L };            // anchor + centred fallback
            var wasPlaced = new byte[] { 0, 0 };
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 2f, SortKey = 0f, FeatureIndex = 1, TileKey = 5,
                Slot = 0, TranslateAnchor = TextTranslateAnchor.Viewport, MaxAngleDeg = maxAngleDeg,
                KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };

            Result m = Managed(in s, screen, depth, valid, glyphs, anchors, fadeIds, wasPlaced, 0f);
            Result n = Native(in s, screen, depth, valid, glyphs, anchors, fadeIds, wasPlaced, 0f);

            // Placement DECISIONS must be identical (a ULP flip at the bend would change these).
            Assert.AreEqual(m.Staged, n.Staged, $"staged count (bend {bendDeg}, maxAngle {maxAngleDeg})");
            Assert.AreEqual(m.BoxCount, n.BoxCount, "box count");
            Assert.AreEqual(m.QuadCount, n.QuadCount, "quad count");
            for (int i = 0; i < m.Staged; i++)
            {
                Assert.AreEqual(m.Candidates[i].BoxStart, n.Candidates[i].BoxStart, "candidate BoxStart");
                Assert.AreEqual(m.Candidates[i].BoxCount, n.Candidates[i].BoxCount, "candidate BoxCount");
                Assert.AreEqual(m.Candidates[i].FadeId, n.Candidates[i].FadeId, "candidate FadeId");
            }
            // Geometry must match within a tight tolerance (ULP trig noise only).
            for (int i = 0; i < m.BoxCount; i++)
            {
                Assert.AreEqual(m.Boxes[i].Min.x, n.Boxes[i].Min.x, GeomTol);
                Assert.AreEqual(m.Boxes[i].Min.y, n.Boxes[i].Min.y, GeomTol);
                Assert.AreEqual(m.Boxes[i].Max.x, n.Boxes[i].Max.x, GeomTol);
                Assert.AreEqual(m.Boxes[i].Max.y, n.Boxes[i].Max.y, GeomTol);
            }
            for (int i = 0; i < m.QuadCount; i++)
            {
                Assert.AreEqual(m.Quads[i].AnchorScreenPx.x, n.Quads[i].AnchorScreenPx.x, GeomTol);
                Assert.AreEqual(m.Quads[i].AnchorScreenPx.y, n.Quads[i].AnchorScreenPx.y, GeomTol);
                Assert.AreEqual(m.Quads[i].RotationRadians, n.Quads[i].RotationRadians, 1e-4f);
            }
        }

        private static SymbolQuad Cell() => new SymbolQuad
        {
            TopLeft = new float2(-6, 6), BottomRight = new float2(6, -6),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };
    }
}
