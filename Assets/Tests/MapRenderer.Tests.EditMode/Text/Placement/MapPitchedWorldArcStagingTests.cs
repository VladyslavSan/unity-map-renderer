// Unity EditMode only — NOT registered in Tools/core-tests/core-tests.csproj (W1 plan §9: that project's
// file list has no Text/Placement entry, and this stage must not add one; the Unity gate is the only
// instrument here).
//
// Stage W1 — the STAGING arm of the map-pitched WORLD-ARC layout teeth (W1-T6…T10). These call
// SymbolStagingMath.StageCurved directly over hand-built synthetic paths, which is the only way to reach the
// three properties the rendered fixture (MapPitchedWorldArcLayoutTests) structurally cannot:
//
//   • T6/T7 — the two DELIBERATE approximations W1 records. A comment is not a tooth; each of these is the
//     test that goes RED the moment the approximation stops being deliberate.
//   • T8/T9 — the stage INVARIANT and the degradation guard, as properties of the code rather than claims
//     about the suite: a non-map-pitched symbol cannot observe the new per-frame ruler at all, and a
//     map-pitched symbol whose ruler was never patched degrades to the screen walk instead of collapsing.
//   • T10 — R3's named trap. The chord probe's half-width is an ARC quantity; leaving it on the px scale
//     while the walk runs in metres silently undoes the vertex-straddling rotation fix. On a STRAIGHT road
//     that defect is a no-op, so the whole rendered fixture and every existing curved test are blind to it.
//     This tooth is on a BENT path, which is what makes it the observer.
//
// Stage GLOBE-A adds GA-T1…GA-T3 at the bottom of the file: the first fixture anywhere in this repo whose
// ground frame has a NON-ZERO Gram-Schmidt axial term, on a genuine on-sphere arc. It reuses this file's W3
// apparatus (OriginView/ProjectPx/Pools/Stage) rather than standing up a second hand-built camera.
//
// WHY THESE PATHS ARE SYNTHETIC AND NOT "PHYSICAL". StageCurved takes the screen polyline and the world
// polyline as INDEPENDENT parameters. T7 exploits that directly (a screen-kinked, world-straight path is not
// a pose any camera produces, but it is exactly the input that separates a screen-tangent gate from a
// world-tangent one). Where a tooth's claim would be weakened by the two disagreeing, they are built
// geometrically similar and the correspondence is stated at the site.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class MapPitchedWorldArcStagingTests
    {
        private struct Pools
        {
            public SymbolBox[] Boxes; public int BoxCount;
            public PlacedQuad[] Quads; public int QuadCount;
            public SymbolCandidate[] Candidates; public CandidateEmit[] Emit; public int EmitCount;
            public static Pools New() => new Pools
            {
                Boxes = new SymbolBox[64], Quads = new PlacedQuad[64],
                Candidates = new SymbolCandidate[64], Emit = new CandidateEmit[64],
            };
        }

        /// <summary>A glyph cell of the given half-width in baked px, 12 baked px tall, no skirt.</summary>
        private static SymbolQuad Cell(float halfWidthBakedPx) => new SymbolQuad
        {
            TopLeft = new float2(-halfWidthBakedPx, 6f), BottomRight = new float2(halfWidthBakedPx, -6f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1f, 1f), LineIndex = 0,
        };

        private static CurvedStageInput Input(AlignmentMode pitch, float metresPerLogicalPixel,
            float textSizePx, float maxAngleDeg, int featureIndex) => new CurvedStageInput
        {
            TextSizePx = textSizePx, PaddingPx = 0f, SortKey = 0f,
            FeatureIndex = featureIndex, TileKey = 7, Slot = 0,
            TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
            MaxAngleDeg = maxAngleDeg, KeepUpright = false, Color = new float4(1f, 1f, 1f, 1f),
            PitchAlignment = pitch, MetresPerLogicalPixel = metresPerLogicalPixel,
        };

        /// <summary>
        /// Stages one symbol. <paramref name="view"/> defaults to <c>default(SymbolViewTransform)</c> — W3's
        /// "no camera was supplied" state, which keeps the pre-W3 SCREEN collision box byte-identical — so
        /// every W1/W2 caller above is untouched by W3 and its expectations still describe the same code.
        /// <paramref name="worldUpPathOverride"/> likewise defaults to the all-zero up path these fixtures
        /// have always passed (a degenerate ground frame, W3-T6's subject).
        /// </summary>
        private static int Stage(in CurvedStageInput s, float2[] screenPath, double3[] worldPath,
            CurvedGlyph[] glyphs, LineAnchor[] anchors, ref Pools p, float[] depthPathOverride = null,
            SymbolViewTransform view = default, float3[] worldUpPathOverride = null, int ordinal = 0)
        {
            int n = screenPath.Length;
            var depthPath = depthPathOverride ?? new float[n];
            var validPath = new byte[n];
            for (int v = 0; v < n; v++) validPath[v] = 1;
            var worldUpPath = worldUpPathOverride ?? new float3[n];
            var fadeIds = new long[anchors.Length + 1];
            for (int a = 0; a < fadeIds.Length; a++)
                fadeIds[a] = SymbolStagingMath.LineFadeId(s.TileKey, 0, s.FeatureIndex, a == anchors.Length ? -1 : a);
            return SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath,
                glyphs, anchors, fadeIds, new byte[anchors.Length + 1], new float2[n], new float[n],
                bearingRadians: 0f, view: view, ordinal: ordinal,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T6 — R5a's observing tooth: the screen point of a WORLD parameter is the AFFINE lerp.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T6 — the observer for the recorded R5a limitation.</b> Proves: under the world arc walk,
        /// <c>AtWithSegment</c> maps a resolved <c>(seg, t)</c> to the screen with a plain AFFINE
        /// <c>lerp(path[seg], path[seg+1], t)</c>, and NOT with the perspective-correct screen parameter
        /// <c>t' = t·w₁ / ((1−t)·w₀ + t·w₁)</c> — the WORLD→SCREEN map, whose inverse (the familiar
        /// screen→attribute form, with the w's transposed) is a different function and is not what this site
        /// would need.
        ///
        /// <para>The fixture DECLARES the endpoint clip w's (300 m and 600 m, a 2:1 receding segment); it does
        /// not read them from production, because production cannot have them —
        /// <c>SymbolProjectionJob.OutDepth</c> is NDC depth (<c>clip.z / clip.w</c>) and clip <c>w</c> is not
        /// recoverable from it without the projection's m22/m23. That is the whole reason the limitation is
        /// recorded rather than fixed: it is not implementable from today's inputs.</para>
        ///
        /// <para>At the world midpoint (<c>t = 0.5</c>) the perspective-correct parameter collapses to
        /// <c>w₁/(w₀+w₁) = 2/3</c> — PAST the affine ½, toward the far endpoint, because a receding segment's
        /// far half is compressed on screen. It is a sixth of the segment away from the affine answer, which
        /// on this 300-px screen segment is exactly 50 px.</para>
        ///
        /// <para><b>The 50 px is symmetric about ½, so do not re-derive the bound from the sign of the
        /// error.</b> <c>|2/3 − ½|</c> and <c>|1/3 − ½|</c> are both 1/6, so the separation this tooth asserts
        /// is the same whichever direction the perspective correction runs — which is exactly why an inverted
        /// formula survived here undetected until review. The direction is pinned by the doc above and by the
        /// <c>tPrime &gt; 0.5</c> precondition below, not by the 50 px. Both numbers are asserted, so the
        /// tooth cannot pass by coincidence on a degenerate geometry.</para>
        ///
        /// <para>Single-segment on purpose: a lone segment resolves <c>t = 0.5</c> under BOTH walks, so this
        /// tooth pins the <c>(seg,t) → screen</c> MAPPING alone and is deliberately unaffected by injection I1
        /// (kill the world branch). Anyone who implements <c>t'</c> turns it RED and must delete the recorded
        /// limitation from <c>StageCurved</c>'s doc.</para>
        /// </summary>
        [Test]
        public void MapPitched_ScreenPointOfAWorldParameter_IsAffine_NotPerspectiveCorrect()
        {
            var screenPath = new[] { new float2(100f, 300f), new float2(400f, 300f) }; // 300 px long
            var worldPath  = new[] { new double3(0, 0, 0), new double3(0, 0, 2000) };  // 2000 m long
            var glyphs  = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(10f) } };
            var anchors = new[] { new LineAnchor(0, 0.5f) };                            // world arc 1000 m
            CurvedStageInput s = Input(AlignmentMode.Map, metresPerLogicalPixel: 10f,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 6);
            var p = Pools.New();

            // The declared endpoint clip w's are ALSO written into the depth span — not because production
            // reads it (StageCurved uses depthPath for the per-symbol sort depth and nothing else, and this
            // tooth's GREEN result is independent of what is in it), but because reaching for `depthPath` as
            // if it held clip w is the precise wrong move StageCurved's doc warns about. Putting real w's
            // there means an implementation that makes that mistake produces the perspective-correct point
            // and this tooth catches it, instead of silently reading zeros and staying green.
            const double w0 = 300.0, w1 = 600.0;
            var depthPathCarryingW = new[] { (float)w0, (float)w1 };

            int staged = Stage(in s, screenPath, worldPath, glyphs, anchors, ref p, depthPathCarryingW);
            Assert.That(staged, Is.EqualTo(1), "W1-T6 precondition: the single-glyph label must stage.");

            // The two candidate answers, both computed HERE from the fixture's own constants.
            double2 a0 = new double2(screenPath[0].x, screenPath[0].y);
            double2 a1 = new double2(screenPath[1].x, screenPath[1].y);
            double2 affine = math.lerp(a0, a1, 0.5);                       // t = 0.5
            // t' = t·w₁ / ((1−t)·w₀ + t·w₁), which at t = 0.5 collapses to w₁/(w₀+w₁) — 2/3 for a 2:1
            // segment. NOT w₀/(w₀+w₁): that is the inverse (screen→attribute) map and points the correction
            // at the camera instead of at the far endpoint.
            double tPrime = w1 / (w0 + w1);
            double2 perspectiveCorrect = math.lerp(a0, a1, tPrime);
            double separationPx = math.length(perspectiveCorrect - affine);

            // The DIRECTION, pinned separately from the magnitude: the separation below is symmetric about
            // ½, so it alone cannot tell w₁/(w₀+w₁) from w₀/(w₀+w₁). On a receding segment (w₁ > w₀) the
            // world midpoint must project PAST the screen midpoint, toward the far endpoint.
            Assert.That(tPrime, Is.GreaterThan(0.5),
                $"W1-T6 precondition: on a receding segment (w₀={w0:F0} < w₁={w1:F0}) the perspective-correct " +
                $"parameter must exceed the affine ½ — reads {tPrime:F6}. A value below ½ means the formula " +
                "has been transposed into the inverse screen→attribute map.");

            double2 staged0 = new double2(p.Quads[0].AnchorScreenPx.x, p.Quads[0].AnchorScreenPx.y);
            Assert.That(math.length(staged0 - affine), Is.LessThan(1e-3),
                $"W1-T6: the staged screen anchor must be the AFFINE lerp at the world parameter — staged " +
                $"({staged0.x:F4}, {staged0.y:F4}) vs affine ({affine.x:F4}, {affine.y:F4}). This is the " +
                "RECORDED R5a limitation; if you implemented the perspective-correct t', delete the " +
                "limitation from StageCurved's doc rather than widening this bound.");
            Assert.That(separationPx, Is.EqualTo(50.0).Within(1e-6),
                $"W1-T6 precondition: the perspective-correct answer must be a STATED distance away, or this " +
                $"tooth discriminates nothing — t'={tPrime:F6} against 0.5 on a 300 px segment is " +
                $"{separationPx:F6} px (expected 50).");
            Assert.That(math.length(staged0 - perspectiveCorrect), Is.EqualTo(50.0).Within(1e-3),
                $"W1-T6: the staged anchor must differ from the perspective-correct answer " +
                $"({perspectiveCorrect.x:F4}, {perspectiveCorrect.y:F4}) by the full 50 px — measured " +
                $"{math.length(staged0 - perspectiveCorrect):F4} px.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T7 — R5b's observing tooth: the max-angle gate stays on the SCREEN tangent.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T7 — the observer for the recorded R5b limitation.</b> Proves: a map-pitched symbol whose
        /// SCREEN path is kinked past <c>text-max-angle</c> is DROPPED, even though its WORLD path is very
        /// nearly straight. The gate answers "is the projected path too kinky to place a symbol on", and W1
        /// deliberately leaves it there.
        ///
        /// <para>The control clause is what makes it non-vacuous: the SAME world path with a STRAIGHT screen
        /// path stages. So the drop is attributable to the screen kink and not to any other precondition of
        /// this geometry.</para>
        ///
        /// <para>RED-verify: move the gate to the world tangent (injection I6) — the world bend is 2°, well
        /// inside the 30° limit, so the kinked case would stage and this goes RED.</para>
        /// </summary>
        [Test]
        public void MapPitched_MaxAngleGate_ReadsTheScreenTangent_NotTheWorldTangent()
        {
            // World: two 1000 m segments bent by 2° — a real polyline, comfortably inside a 30° gate.
            double worldBendRad = math.radians(2.0);
            var worldPath = new[]
            {
                new double3(0, 0, 0),
                new double3(0, 0, 1000),
                new double3(1000 * math.sin(worldBendRad), 0, 1000 + 1000 * math.cos(worldBendRad)),
            };
            // Screen: the SAME three vertices, kinked 90° — five times the gate.
            var kinkedScreen   = new[] { new float2(0f, 200f), new float2(200f, 200f), new float2(200f, 400f) };
            var straightScreen = new[] { new float2(0f, 200f), new float2(200f, 200f), new float2(400f, 200f) };

            // Two glyphs, one either side of the interior vertex: with arcScale = 10 m/baked-px and a 100
            // baked-px advance, they sit at world arc 500 m (segment 0) and 1500 m (segment 1), so the gate's
            // g > 0 comparison is genuinely BETWEEN the two segments' tangents.
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f,   Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(1, 0f) }; // world arc 1000 m — the interior vertex
            CurvedStageInput s = Input(AlignmentMode.Map, metresPerLogicalPixel: 10f,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 30f, featureIndex: 7);

            var kinkedPools = Pools.New();
            int stagedKinked = Stage(in s, kinkedScreen, worldPath, glyphs, anchors, ref kinkedPools);
            var straightPools = Pools.New();
            int stagedStraight = Stage(in s, straightScreen, worldPath, glyphs, anchors, ref straightPools);

            Assert.That(stagedKinked == 0 && stagedStraight == 1, Is.True,
                $"W1-T7: the max-angle gate must read the SCREEN tangent — the 90°-kinked screen path staged " +
                $"{stagedKinked} candidates (expected 0, dropped) and the straight screen path over the SAME " +
                $"world polyline staged {stagedStraight} (expected 1). The world path bends only 2°, so a " +
                "gate moved onto world curvature would keep BOTH. If that move was deliberate, delete R5b " +
                "from StageCurved's doc rather than relaxing this.");
            Assert.That(kinkedPools.QuadCount, Is.EqualTo(0),
                $"W1-T7: a dropped anchor must roll its partial appends back — {kinkedPools.QuadCount} quads " +
                "were left behind.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T8 / W1-T9 — the stage invariant and the degradation guard, as code properties.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T8 — THE STAGE INVARIANT, AS A TEST.</b> Proves: a symbol whose resolved pitch alignment is
        /// not <see cref="AlignmentMode.Map"/> cannot observe the new per-frame ruler AT ALL — its staged
        /// boxes and quads are BIT-identical with <c>MetresPerLogicalPixel</c> at 0, 1 and 10⁶, four orders of
        /// magnitude apart. Run for <see cref="AlignmentMode.Viewport"/> and for
        /// <see cref="AlignmentMode.Auto"/> (the enum's zero value, which is what every hand-built pre-W1
        /// fixture carries — the hinge the whole 2099-test invariant hangs on).
        ///
        /// <para>This is what turns §1's invariant from a claim about the current test suite into a property
        /// of the code: a future symbol that resolves to viewport pitch cannot start moving because someone
        /// changed the ruler.</para>
        ///
        /// <para>RED-verify: injection I7 (drop the <c>== AlignmentMode.Map</c> conjunct, so every symbol takes
        /// the world walk).</para>
        ///
        /// <para><b>W2 discharged W1's followUp 1 — this tooth is no longer alone.</b> It used to be the SOLE
        /// observer of the <c>PitchAlignment == Map</c> conjunct in the whole suite, so weakening it would
        /// have left the predicate unobserved. W2 added a SECOND, independent consumer of the same
        /// <c>worldArc</c> bool (<c>CandidateEmit.CornerMetresPerLogicalPixel</c>), watched by W2-T6's three
        /// cases below and by W2-T7. They read the same predicate through a different output, so I7 now reds
        /// several teeth rather than one.</para>
        /// </summary>
        [Test]
        public void NonMapPitchedSymbol_CannotObserveTheRuler_AtAnyMagnitude()
        {
            foreach (AlignmentMode mode in new[] { AlignmentMode.Viewport, AlignmentMode.Auto })
            {
                float[] rulers = { 0f, 1f, 1e6f };
                var quadBits = new uint[rulers.Length][];
                var boxBits  = new uint[rulers.Length][];
                for (int r = 0; r < rulers.Length; r++)
                {
                    var p = Pools.New();
                    int staged = StageReferenceSymbol(mode, rulers[r], ref p);
                    Assert.That(staged, Is.EqualTo(1),
                        $"W1-T8 precondition ({mode}, ruler {rulers[r]}): the reference label must stage.");
                    quadBits[r] = Bits(p.Quads, p.QuadCount);
                    boxBits[r]  = Bits(p.Boxes, p.BoxCount);
                }

                for (int r = 1; r < rulers.Length; r++)
                {
                    AssertBitIdentical(quadBits[0], quadBits[r],
                        $"W1-T8 ({mode}): PlacedQuads at MetresPerLogicalPixel={rulers[r]} differ from those " +
                        $"at {rulers[0]}");
                    AssertBitIdentical(boxBits[0], boxBits[r],
                        $"W1-T8 ({mode}): SymbolBoxes at MetresPerLogicalPixel={rulers[r]} differ from those " +
                        $"at {rulers[0]}");
                }
            }
        }

        /// <summary>
        /// <b>W1-T9 — the degradation guard.</b> Proves: a <see cref="AlignmentMode.Map"/> symbol whose
        /// per-frame ruler was never patched (<c>MetresPerLogicalPixel == 0</c>) stages BIT-identically to the
        /// same symbol under <see cref="AlignmentMode.Viewport"/> — i.e. it falls back to the pre-W1 screen
        /// walk rather than collapsing every glyph onto the anchor.
        ///
        /// <para>Without the <c>&gt; 0</c> conjunct, <c>arcScale</c> would be 0, every glyph's <c>arc</c>
        /// would equal <c>centerArc</c>, and the symbol would silently become a point — the ugliest possible
        /// failure for a path that has no channel to report. This tooth is what stops that guard from rotting
        /// into an unobserved branch.</para>
        ///
        /// <para>RED-verify: delete the <c>&amp;&amp; s.MetresPerLogicalPixel &gt; 0f</c> conjunct — the
        /// glyphs pile up on one point and the quads stop matching the viewport reference.</para>
        /// </summary>
        [Test]
        public void MapPitchedSymbol_WithNoRuler_DegradesToTheScreenWalk()
        {
            var mapPools = Pools.New();
            int stagedMap = StageReferenceSymbol(AlignmentMode.Map, 0f, ref mapPools);
            var viewportPools = Pools.New();
            int stagedViewport = StageReferenceSymbol(AlignmentMode.Viewport, 0f, ref viewportPools);

            Assert.That(stagedMap == 1 && stagedViewport == 1, Is.True,
                $"W1-T9 precondition: both references must stage — map {stagedMap}, viewport {stagedViewport}.");
            // The glyphs must not have collapsed onto one point: that is the failure this guard prevents, and
            // asserting it separately means a bit-comparison that somehow matched a degenerate symbol still
            // fails here.
            float spreadPx = math.length(
                mapPools.Quads[mapPools.QuadCount - 1].AnchorScreenPx - mapPools.Quads[0].AnchorScreenPx);
            Assert.That(spreadPx, Is.GreaterThan(1f),
                $"W1-T9: an unpatched map-pitched label must still spread its glyphs along the path — first " +
                $"and last anchors are {spreadPx:F6} px apart, i.e. the label collapsed to a point.");
            AssertBitIdentical(Bits(mapPools.Quads, mapPools.QuadCount),
                Bits(viewportPools.Quads, viewportPools.QuadCount),
                "W1-T9: a map-pitched label with MetresPerLogicalPixel=0 must stage exactly as a viewport one");
            AssertBitIdentical(Bits(mapPools.Boxes, mapPools.BoxCount),
                Bits(viewportPools.Boxes, viewportPools.BoxCount),
                "W1-T9: same, for the collision boxes");
        }

        /// <summary>The symbol T8/T9 compare across rulers: three glyphs on a plain two-vertex path, with the
        /// world span deliberately UNRELATED to the screen span (100 m vs 400 px) so that if the world walk
        /// ever did run here the difference would be enormous, not marginal.</summary>
        private static int StageReferenceSymbol(AlignmentMode pitch, float metresPerLogicalPixel, ref Pools p,
            SymbolViewTransform view = default)
        {
            var screenPath = new[] { new float2(50f, 250f), new float2(450f, 250f) };
            var worldPath  = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f,  Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 30f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            CurvedStageInput s = Input(pitch, metresPerLogicalPixel,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 8);
            return Stage(in s, screenPath, worldPath, glyphs, anchors, ref p, view: view);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T10 — R3's named trap: the chord probe's half-width is an ARC quantity.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T10 — REQUIRED, and the only observer of R3's named trap.</b> Proves: on a map-pitched BENT
        /// path, a glyph whose footprint straddles the interior vertex is rotated to the CHORD across that
        /// footprint — not to either raw segment angle.
        ///
        /// <para><b>Why it is mandatory.</b> The chord probe offsets by <c>halfWidthArc</c>, which must be
        /// scaled by <c>arcScale</c> and is therefore METRES under the world walk. Left on the px scale it
        /// spans a few units of a multi-thousand-metre advance, both probes land on the SAME segment, and the
        /// chord collapses to that segment's raw direction — silently undoing the vertex-straddling fix. On a
        /// STRAIGHT road the collapse is a no-op (the chord is collinear with the segment anyway), so the
        /// entire rendered fixture and every existing curved test are blind to it.</para>
        ///
        /// <para><b>The geometry, and why the expected value is not a re-implementation.</b> The two segments
        /// are the same 100 screen px but DIFFERENT world lengths — 1000 m and 2000 m, i.e. 10 and 20 metres
        /// per screen px, which is what a receding road looks like once projected. That asymmetry is
        /// deliberate and does two jobs at once:
        /// <list type="bullet">
        /// <item>it makes the world walk and the screen walk resolve genuinely DIFFERENT <c>(seg, t)</c> for
        /// the same anchor, so this tooth also fails if the world branch is killed outright (a tooth whose
        /// world path were merely a scaled copy of its screen path could not tell the two walks apart at
        /// all);</item>
        /// <item>it puts the glyph 200 m PAST the interior vertex, so the straddle is asymmetric — and that
        /// is what makes the px-scaled defect visible, because a small px half-width puts BOTH probes inside
        /// segment 1 and the chord then reads segment 1's raw 60° exactly.</item>
        /// </list>
        /// Every parameter is plain arithmetic: world cumulative <c>[0, 1000, 3000]</c>, anchor
        /// <c>(seg 1, t 0.1)</c> ⇒ world arc <c>1000 + 0.1·2000 = 1200 m</c>; half-width
        /// <c>50 baked px · arcScale(20 m per baked px) · 0.5 = 500 m</c>; so the probes sit at 700 m
        /// (segment 0, <c>t = 0.7</c>) and 1700 m (segment 1, <c>t = (1700−1000)/2000 = 0.35</c>), and the
        /// chord across <c>lerp(P₀,P₁,0.7) → lerp(P₁,P₂,0.35)</c> is the answer.</para>
        ///
        /// <para>RED-verify: injection I3 (leave <c>halfWidthArc</c> on the px scale) — confirm the injected
        /// build returns segment 1's raw angle, i.e. that the chord genuinely COLLAPSED rather than merely
        /// shifting. Also RED under I1 (kill the world branch), per the geometry note above.</para>
        /// </summary>
        [Test]
        public void MapPitched_GlyphStraddlingAVertex_RotatesToTheChord_NotARawSegmentAngle()
        {
            double bendRad = math.radians(60.0);
            var screenPath = new[]
            {
                new float2(-100f, 0f),
                new float2(0f, 0f),
                new float2(100f * (float)math.cos(bendRad), 100f * (float)math.sin(bendRad)),
            };
            // Both screen segments are 100 px, but the world ones are 1000 m and 2000 m — the second is
            // twice as compressed on screen, as a receding road's farther half is. See this tooth's doc.
            var worldPath = new[]
            {
                new double3(screenPath[0].x, 0.0, screenPath[0].y) * 10.0,
                new double3(screenPath[1].x, 0.0, screenPath[1].y) * 10.0,
                new double3(screenPath[2].x, 0.0, screenPath[2].y) * 20.0,
            };

            // arcScale = TextSizePx/OneEm × metresPerLogicalPixel = 2 × 10 = 20 m per baked px, so
            // halfWidthArc = cellWidth(50 baked px) × 20 × 0.5 = 500 m.
            var glyphs  = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(25f) } };
            var anchors = new[] { new LineAnchor(1, 0.1f) };   // world arc 1000 + 0.1×2000 = 1200 m
            CurvedStageInput s = Input(AlignmentMode.Map, metresPerLogicalPixel: 10f,
                textSizePx: 2f * TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 10);
            var p = Pools.New();

            int staged = Stage(in s, screenPath, worldPath, glyphs, anchors, ref p);
            Assert.That(staged, Is.EqualTo(1), "W1-T10 precondition: the straddling glyph must stage.");

            // The expected chord, derived here from the design constants (see this tooth's doc) — plain
            // lerps at the hand-derived parameters 0.7 and 0.35, no call into the arc math under test.
            float2 probeLeft  = math.lerp(screenPath[0], screenPath[1], 0.7f);
            float2 probeRight = math.lerp(screenPath[1], screenPath[2], 0.35f);
            float2 chord = probeRight - probeLeft;
            double expectedRad = math.atan2(chord.y, chord.x);
            double segment0Rad = 0.0;
            double segment1Rad = bendRad;
            double stagedRad = p.Quads[0].RotationRadians;

            Assert.That(math.abs(expectedRad - segment0Rad), Is.GreaterThan(math.radians(15.0)),
                $"W1-T10 precondition: the chord answer ({math.degrees(expectedRad):F3}°) must be well clear " +
                "of segment 0's raw angle, or the tooth cannot discriminate.");
            Assert.That(math.abs(expectedRad - segment1Rad), Is.GreaterThan(math.radians(15.0)),
                $"W1-T10 precondition: the chord answer ({math.degrees(expectedRad):F3}°) must be well clear " +
                "of segment 1's raw angle, or the tooth cannot discriminate.");
            Assert.That(stagedRad, Is.EqualTo(expectedRad).Within(math.radians(0.05)),
                $"W1-T10: the straddling glyph must rotate to the CHORD across its own world footprint — " +
                $"staged {math.degrees(stagedRad):F4}°, chord {math.degrees(expectedRad):F4}°, segment " +
                $"angles {math.degrees(segment0Rad):F1}° / {math.degrees(segment1Rad):F1}°. A staged value " +
                $"of exactly {math.degrees(segment1Rad):F1}° means the chord probe COLLAPSED — halfWidthArc " +
                "is on the px scale while the walk runs in metres (R3's named trap).");
        }

        // ── bit-identity comparison ─────────────────────────────────────────────────────────────────────
        //
        // Field-by-field EQUALITY is not what T8/T9 claim; they claim BIT-identity, and NaN/−0.0 make those
        // different statements. So each struct is flattened to its raw bit patterns by walking its value-type
        // fields reflectively — which also means a field added to PlacedQuad/SymbolBox later is compared
        // automatically instead of silently escaping the check.

        private static uint[] Bits<T>(T[] items, int count) where T : struct
        {
            var bits = new List<uint>();
            for (int i = 0; i < count; i++) FlattenBits(items[i], bits);
            return bits.ToArray();
        }

        private static void FlattenBits(object value, List<uint> bits)
        {
            switch (value)
            {
                case float f:  bits.Add(math.asuint(f)); return;
                case int i:    bits.Add(unchecked((uint)i)); return;
                case uint u:   bits.Add(u); return;
                case bool b:   bits.Add(b ? 1u : 0u); return;
                case byte y:   bits.Add(y); return;
                case long l:
                    bits.Add(unchecked((uint)l));
                    bits.Add(unchecked((uint)((ulong)l >> 32)));
                    return;
                case double d:
                {
                    ulong u64 = unchecked((ulong)System.BitConverter.DoubleToInt64Bits(d));
                    bits.Add(unchecked((uint)u64));
                    bits.Add(unchecked((uint)(u64 >> 32)));
                    return;
                }
            }

            System.Type type = value.GetType();
            if (type.IsEnum)
            {
                FlattenBits(System.Convert.ChangeType(value, System.Enum.GetUnderlyingType(type)), bits);
                return;
            }
            Assert.That(type.IsValueType, Is.True,
                $"the bit comparer walks value types only — {type} is a reference type, so the staged " +
                "structs are no longer blittable and this comparison would be meaningless.");
            System.Reflection.FieldInfo[] fields = type.GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.GreaterThan(0),
                $"the bit comparer found no fields on {type} — it would compare nothing and pass vacuously.");
            for (int f = 0; f < fields.Length; f++)
                FlattenBits(fields[f].GetValue(value), bits);
        }

        private static void AssertBitIdentical(uint[] expected, uint[] actual, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length),
                $"{what}: {actual.Length} words against {expected.Length} — a different number of staged " +
                "records, so nothing further is comparable.");
            Assert.That(expected.Length, Is.GreaterThan(0),
                $"{what}: nothing was staged, so this comparison would pass vacuously.");
            for (int i = 0; i < expected.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]),
                    $"{what}: first difference at 32-bit word {i} of {expected.Length} — 0x{actual[i]:X8} " +
                    $"against 0x{expected[i]:X8}. These outputs must be BIT-identical, not merely close.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T6 — the CORNER UNIT's producer: set exactly when the world walk is (R3 + R4.1)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T6, case 1.</b> Proves: a <see cref="AlignmentMode.Map"/> symbol with a live ruler emits
        /// <c>CandidateEmit.CornerMetresPerLogicalPixel</c> equal to EXACTLY that ruler — the same value the
        /// arc walk is spacing its anchors with, so the drawn size and the spacing cannot come from different
        /// constants.
        ///
        /// <para><b>This also discharges W1's followUp 1.</b> Before W2, <c>W1-T8</c> was the SOLE observer of
        /// the <c>PitchAlignment == Map</c> conjunct in a 2109-test suite — injection I7 (drop the conjunct)
        /// reddened T8 and nothing else. After W2 the same <c>worldArc</c> bool is observed through a SECOND,
        /// independent output by these three cases and by W2-T7, so weakening W1-T8 no longer leaves the
        /// predicate unobserved. W1-T8's doc carries the reciprocal cross-reference.</para>
        ///
        /// <para><b>Ruler magnitudes are bounded by the reference symbol's own road, deliberately.</b> Under the
        /// world walk the reference symbol's arc span is <c>60 · ruler</c> metres against a 100 m world path, so
        /// a ruler above ≈ 1.6 makes <c>StageCurved</c> return 0 at its <c>symbolSpanArc &gt; total</c> spill
        /// gate and there is no emit to read. W1-T8 and W1-T9 never hit that because they run this symbol
        /// either non-map-pitched or with a zero ruler. The values below straddle 1 so the assertion is that
        /// the emit carries the ruler EXACTLY, not merely that it is non-zero.</para>
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsTheRuler_WhenMapPitchedAndRulerIsLive()
        {
            foreach (float ruler in new[] { 0.5f, 1f, 1.25f })
            {
                var p = Pools.New();
                int staged = StageReferenceSymbol(AlignmentMode.Map, ruler, ref p);
                Assert.That(staged, Is.EqualTo(1),
                    $"W2-T6 precondition (ruler {ruler}): the reference label must stage — a 0 means its " +
                    $"world arc span ({60f * ruler:F1} m) outgrew its 100 m road at the spill gate.");
                Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(ruler),
                    $"W2-T6: a map-pitched label with MetresPerLogicalPixel = {ruler} must carry exactly " +
                    $"{ruler} as its corner unit, got {p.Emit[0].CornerMetresPerLogicalPixel}. Any other " +
                    "value means the corner scale and the arc scale were derived separately — the state this " +
                    "stage exists to make inexpressible.");
            }
        }

        /// <summary>
        /// <b>W2-T6, case 2.</b> Proves: a NON-map-pitched symbol emits <c>0f</c> as its corner unit whatever
        /// the ruler reads — so its <c>Offset</c> stays LOGICAL PIXELS and every pre-W2 path is untouched.
        /// The zero value is the struct's default, which is why no point emit and no hand-built fixture emit
        /// had to be edited by this stage.
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsZero_WhenNotMapPitched()
        {
            foreach (AlignmentMode mode in new[] { AlignmentMode.Viewport, AlignmentMode.Auto })
                foreach (float ruler in new[] { 0f, 1f, 1e6f })
                {
                    var p = Pools.New();
                    int staged = StageReferenceSymbol(mode, ruler, ref p);
                    Assert.That(staged, Is.EqualTo(1),
                        $"W2-T6 precondition ({mode}, ruler {ruler}): the reference label must stage.");
                    Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(0f),
                        $"W2-T6 ({mode}, ruler {ruler}): a non-map-pitched label must carry 0 as its corner " +
                        $"unit — i.e. logical pixels, the pre-W2 behaviour — got " +
                        $"{p.Emit[0].CornerMetresPerLogicalPixel}. A non-zero here would make every viewport " +
                        "label's corners metres and reinterpret the whole pre-W2 render.");
                }
        }

        /// <summary>
        /// <b>W2-T6, case 3 — the two halves must degrade TOGETHER.</b> Proves: a
        /// <see cref="AlignmentMode.Map"/> symbol whose per-frame ruler was never patched
        /// (<c>MetresPerLogicalPixel == 0</c>) emits <c>0f</c> as its corner unit too.
        ///
        /// <para>W1-T9 pins that such a symbol falls back to the pre-W1 SCREEN arc walk rather than collapsing
        /// to a point. This is the corner-side half of the same guard: the arc ruler and the corner unit come
        /// from ONE predicate, so an unpatched symbol degrades wholly to the pre-W2 behaviour rather than into
        /// a half-converted state where the spacing is screen px and the corners are metres. A test that shows
        /// them degrading together is the point — the failure this prevents is not a crash, it is a silently
        /// mixed pair of rulers, which is the exact shape of the bug this epic kept re-landing.</para>
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsZero_WhenMapPitchedButTheRulerIsMissing()
        {
            var p = Pools.New();
            int staged = StageReferenceSymbol(AlignmentMode.Map, 0f, ref p);
            Assert.That(staged, Is.EqualTo(1), "W2-T6 precondition: the reference label must stage.");
            Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(0f),
                "W2-T6: a map-pitched label with no ruler must degrade its CORNER unit to logical px exactly " +
                "as W1-T9 shows it degrades its ARC ruler to the screen walk — got " +
                $"{p.Emit[0].CornerMetresPerLogicalPixel}. The two must degrade together; a half-converted " +
                "label is worse than either whole behaviour.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T3b — equation (5) past the fixture's cull ceiling, with NO camera
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T3b — the CPU half of equation (6), carried to 50× and beyond.</b> Proves: for a map-pitched
        /// curved symbol, <c>worldAdvance / cellWidth_world</c> equals the purely typographic
        /// <c>ΔArcCenter / cellWidthBaked</c> at every magnitude regime, computed through production
        /// <see cref="SymbolStagingMath.StageCurved"/> and production <see cref="BillboardMath.BuildWorldQuad"/>
        /// with the emit's OWN corner scale.
        ///
        /// <para><b>Why it exists, and exactly what it does and does not add.</b> W2-T3 stops at 8× because
        /// the renderer's B-3 pre-projection distance cull removes the far symbols — the horizon measurement
        /// proved that by observing that <c>PointFar</c>, which has no road and no arc walk at all, disappears
        /// at the same ratio. Equation (5) needs no camera, so this tooth carries the identity past that wall.
        /// <b>It pins the CPU half only and does NOT exercise the shader.</b> The three reaches, named so
        /// nothing is over-read (followUp F-W2-5): rendered ink to ≈ 2.4× (W2-T1/T2), real-mesh world metres
        /// to 8× (W2-T3, W2-T10), CPU identity to 50× (here). The gap above 8× in the RENDERED arms is a
        /// recorded limitation, not an implied claim.</para>
        ///
        /// <para><b>What "depth ratio" means with no camera.</b> Nothing — and that is the structural point.
        /// The world arc walk has NO depth input: a glyph's arc is
        /// <c>centerArc + (ArcCenter − centre)·arcScale</c> and <c>arcScale</c> is a per-FRAME constant, so
        /// spacing CANNOT depend on distance from the camera; it can only fail NUMERICALLY. What the sweep
        /// below varies is therefore the MAGNITUDE regime — the symbol's distance from its tile origin, along
        /// the road axis, which is what a deep pose actually produces — which is the one thing that can
        /// degrade. That is the horizon measurement's §5 method, reused. Displacing ACROSS the road axis would
        /// measure nothing: the large offset lands in a component identical for every glyph and cancels
        /// exactly in the gap.</para>
        /// </summary>
        [Test]
        public void WorldCellToAdvanceRatio_HoldsAtEveryMagnitude(
            [Values(2.0, 5.0, 10.0, 20.0, 50.0)] double magnitude)
        {
            const float advanceBaked   = 30f;
            const float halfWidthBaked = 12f;   // ⇒ cellWidthBaked = 24
            const float ruler          = 305.748f; // metres per logical px, the fixture's own shipped value
            double cellWidthBaked = 2.0 * halfWidthBaked;
            double expected = advanceBaked / cellWidthBaked;

            // The road runs along +x and the whole thing is displaced ALONG that axis (see the doc).
            double baseOffset = 1.0e5 * magnitude;
            double roadLen    = 1.0e5 * magnitude;
            var screenPath = new[] { new float2(50f, 250f), new float2(450f, 250f) };
            var worldPath  = new[]
            {
                new double3(baseOffset, 0, 0),
                new double3(baseOffset + roadLen, 0, 0),
            };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f,                  Cell = Cell(halfWidthBaked) },
                new CurvedGlyph { ArcCenter = advanceBaked,        Cell = Cell(halfWidthBaked) },
                new CurvedGlyph { ArcCenter = 2f * advanceBaked,   Cell = Cell(halfWidthBaked) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            CurvedStageInput s = Input(AlignmentMode.Map, ruler,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 11);

            var p = Pools.New();
            int staged = Stage(in s, screenPath, worldPath, glyphs, anchors, ref p);
            Assert.That(staged, Is.EqualTo(1),
                $"W2-T3b precondition (magnitude {magnitude}): the label must stage; a 0 means the road is " +
                "too short for its world span at this magnitude, which would make the reading vacuous.");

            float cornerScale = p.Emit[0].CornerMetresPerLogicalPixel;
            Assert.That(cornerScale, Is.GreaterThan(0f),
                "W2-T3b precondition: the emit must carry a metre corner unit, or there is no world cell " +
                "width to compare against.");

            // Production BuildWorldQuad, with the emit's OWN scale — exactly the composition
            // WorldSymbolRenderer.Emit performs. The operands are hoisted into locals because CurvedGlyph.Cell
            // is an init-only PROPERTY (the data-carrier convention) and a property value has no address to
            // bind an `in` parameter to.
            SymbolQuad cell0    = glyphs[0].Cell;
            var        white    = new float3(1f, 1f, 1f);
            float2     noTrans  = p.Emit[0].TranslateDeltaPx;
            float3     tangent0 = p.Quads[0].Tangent;
            float3     up0      = p.Quads[0].SurfaceUp;
            float3     anchor0  = p.Quads[0].AnchorLocal;
            BillboardMath.BuildWorldQuad(in cell0, in anchor0,
                p.Quads[0].TextSizePx * cornerScale, in white,
                rotationRadians: 0f, in noTrans, in tangent0,
                in up0, alignFlags: 6f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out _, out _);
            double cellWidthWorld = tr.Offset.x - tl.Offset.x;

            for (int g = 0; g + 1 < glyphs.Length; g++)
            {
                double advanceWorld = math.length(p.Quads[g + 1].AnchorLocal - p.Quads[g].AnchorLocal);
                double ratio = advanceWorld / cellWidthWorld;
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "W2-T3b  magnitude={0:F0}  gap {1}  advanceWorld={2:F3} m  cellWidthWorld={3:F3} m  " +
                    "ratio={4:F8}  expected={5:F8}",
                    magnitude, g, advanceWorld, cellWidthWorld, ratio, expected));

                Assert.That(ratio, Is.EqualTo(expected).Within(0.01).Percent,
                    $"W2-T3b (magnitude {magnitude}, gap {g}): worldAdvance / cellWidth_world must be the " +
                    $"baked {advanceBaked}/{cellWidthBaked} = {expected:F8} at EVERY magnitude, measured " +
                    $"{ratio:F8}. Both sides are produced by the SAME arcScale, so it cancels identically; a " +
                    "drift here at large magnitude is a float-precision floor, not a model failure — the " +
                    "horizon measurement puts that floor at roughly 50 Earth circumferences of road.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3 — the projected-world-corner collision box. Shared apparatus first, then W3-T4…T9.
        //
        // NO CAMERA OBJECT: the view transform is HAND-BUILT here, so every expected pixel number below is
        // plain arithmetic over two constants (the viewport and the field of view) rather than a reading
        // taken off a live scene.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The W3 teeth's viewport, logical px. Square, so the projection's aspect ratio is 1.</summary>
        private const double ViewportPx = 512.0;

        /// <summary>Vertical field of view, radians (60°).</summary>
        private const float FovRadians = 1.0471975511965976f;

        /// <summary>The camera-facing surface normal used by most W3 teeth: with a road along <c>+X̂</c> this
        /// makes <c>x̂ = +X̂</c> and <c>ŷ = cross(x̂, up) = +Ŷ</c>, i.e. the glyph quad lies in the plane
        /// PERPENDICULAR to the view axis, so a corner's projection is a plain uniform scale in BOTH screen
        /// axes and every expectation below is arithmetic instead of a solve.</summary>
        private static readonly float3 CameraFacingUp = new float3(0f, 0f, -1f);

        /// <summary>The ordinary GROUND normal: with a road along <c>+X̂</c> this makes
        /// <c>ŷ = cross(x̂, up) = +Ẑ</c>, i.e. the quad's own y axis runs ALONG the view axis, which is how
        /// W3-T7 pushes a corner behind the camera without moving the anchor.</summary>
        private static readonly float3 GroundUp = new float3(0f, 1f, 0f);

        /// <summary>Screen px per metre of world offset, per metre of view depth:
        /// <c>½·viewport·cot(fov/2)</c>. A projected offset is <c>this · offsetMetres / depthMetres</c>.
        /// Derived here from the two constants above; never read back out of production.</summary>
        private static readonly double PxPerMetreAtUnitDepth = 0.5 * ViewportPx / math.tan(FovRadians * 0.5);

        /// <summary>
        /// The world→view matrix for a camera at <paramref name="eye"/> looking at <paramref name="target"/>:
        /// right-handed, camera looking down <b>−Z</b> — the convention <c>float4x4.PerspectiveFov</c>'s
        /// <c>w = −z_view</c> bottom row expects, and the one Unity's own <c>worldToCameraMatrix</c> uses (it
        /// negates Z on the way out of Unity's left-handed world).
        ///
        /// <para><b>Written out rather than composed as <c>inverse(float4x4.LookAt(eye, target, up))</c>.</b>
        /// <c>float4x4.LookAt</c> returns a camera→WORLD transform whose <c>+Z</c> IS the forward direction, so
        /// its inverse places the target at view <b>+Z</b> — the opposite sense. Composed that way every corner
        /// comes back with <c>clip.w ≤ 0</c>, every projection fails, BOTH arms of W3-T4 silently take the
        /// screen-box fallback, and the tooth fails in a shape that reads like a logic bug rather than like a
        /// convention mismatch. <b>W3-T4-pre is the tooth that proves this matrix actually projects</b> — a
        /// ≈2 px projected half-height against a 15 px screen one is only reachable if it does.</para>
        /// </summary>
        private static float4x4 ViewMatrix(float3 eye, float3 target, float3 up)
        {
            float3 f = math.normalize(target - eye);
            float3 r = math.normalize(math.cross(up, f));
            float3 u = math.cross(f, r);
            return new float4x4(
                 r.x,  r.y,  r.z, -math.dot(r, eye),
                 u.x,  u.y,  u.z, -math.dot(u, eye),
                -f.x, -f.y, -f.z,  math.dot(f, eye),
                 0f,   0f,   0f,   1f);
        }

        /// <summary>A usable <see cref="SymbolViewTransform"/> for a camera at the render-space ORIGIN looking
        /// along <c>+Ẑ</c>, with an identity rebase and a zero scene origin. A world point <c>(x, y, z)</c> with
        /// <c>z &gt; 0</c> then projects exactly where <see cref="ProjectPx"/> says it does.</summary>
        private static SymbolViewTransform OriginView(double viewportPx = ViewportPx)
            => new SymbolViewTransform
            {
                SceneOriginRender = double3.zero,
                Rebase            = float3x3.identity,
                ViewProj          = math.mul(
                    float4x4.PerspectiveFov(FovRadians, 1f, 1f, 1e7f),
                    ViewMatrix(float3.zero, new float3(0f, 0f, 1f), new float3(0f, 1f, 0f))),
                ViewportLogicalPx = new double2(viewportPx, viewportPx),
            };

        /// <summary>Where <see cref="OriginView"/> puts a render-space point, in logical screen px — the
        /// FIXTURE's own projection, calling no production code. It is what makes each synthetic screen
        /// polyline below the TRUE projection of its world polyline, so the pre-W3 screen box these teeth
        /// compare against is the one the shipped code would really have built.</summary>
        private static float2 ProjectPx(double3 world)
            => new float2(
                (float)(0.5 * ViewportPx + PxPerMetreAtUnitDepth * world.x / world.z),
                (float)(0.5 * ViewportPx + PxPerMetreAtUnitDepth * world.y / world.z));

        /// <summary>
        /// W3's staging fixture: ONE glyph, centred on a straight road that runs along <c>+X̂</c> at view depth
        /// <paramref name="depthM"/> and lateral offset <paramref name="lateralM"/>. The screen polyline is the
        /// TRUE projection of the world polyline (<see cref="ProjectPx"/>), so both the pre-W3 screen box and
        /// the W3 projected box are the boxes the shipped code builds for a real pose of this shape.
        /// </summary>
        private static int StageW3Glyph(ref Pools p, in SymbolViewTransform view, in float3 surfaceUp,
            in SymbolQuad cell, float cellSkirt, float textSizePx, float mpp,
            double depthM, double lateralM, double roadHalfM,
            float iconRotateRadians, float sortKey, int featureIndex, int ordinal,
            AlignmentMode pitch = AlignmentMode.Map)
        {
            var worldPath = new[]
            {
                new double3(-roadHalfM, lateralM, depthM),
                new double3( roadHalfM, lateralM, depthM),
            };
            var screenPath = new[] { ProjectPx(worldPath[0]), ProjectPx(worldPath[1]) };
            var ups = new[] { surfaceUp, surfaceUp };
            var glyphs = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = cell, CellSkirt = cellSkirt } };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            CurvedStageInput s = Input(pitch, mpp, textSizePx, maxAngleDeg: 180f, featureIndex);
            s.SortKey = sortKey;
            s.IconRotateRadians = iconRotateRadians;
            return Stage(in s, screenPath, worldPath, glyphs, anchors, ref p,
                view: view, worldUpPathOverride: ups, ordinal: ordinal);
        }

        // ── the W3-T4 suppression pair's sizing, all derived, none typed ────────────────────────────────

        /// <summary>`text-size` for the suppression pair: 2.5 em, so the 6-baked-px cell half-height becomes a
        /// 15 logical-px PRE-W3 box half-height.</summary>
        private const float SuppressionTextSizePx = 2.5f * TextQuadLayout.OneEm;

        private const float  SuppressionCellHalfHeightBaked = 6f;   // what Cell(...) bakes
        private const float  SuppressionCellHalfWidthBaked  = 10f;
        private const float  SuppressionMpp = 1f;                   // metres per logical px
        private const double SuppressionTargetHalfHeightPx = 2.0;   // the W3 box's designed half-height
        private const double SuppressionTargetSeparationPx = 8.0;   // the roads' designed projected gap

        /// <summary>The pre-W3 box's half-height, in logical px: the cell half-height scaled by
        /// <c>TextSizePx / OneEm</c>, with no depth term at all — which is the defect W3 removes.</summary>
        private const double SuppressionScreenHalfHeightPx =
            SuppressionCellHalfHeightBaked * SuppressionTextSizePx / TextQuadLayout.OneEm;

        /// <summary>The same half-height in WORLD METRES — the renderer's own association,
        /// <c>cellHalfBaked · (TextSizePx · mpp) / OneEm</c>.</summary>
        private const double SuppressionHalfHeightWorldM =
            SuppressionCellHalfHeightBaked * (SuppressionTextSizePx * SuppressionMpp) / TextQuadLayout.OneEm;

        /// <summary>The view depth at which that metre half-height foreshortens to the designed 2 px.</summary>
        private static readonly double SuppressionDepthM =
            PxPerMetreAtUnitDepth * SuppressionHalfHeightWorldM / SuppressionTargetHalfHeightPx;

        /// <summary>The world lateral offset whose projection at that depth is the designed 8 px.</summary>
        private static readonly double SuppressionLateralM =
            SuppressionTargetSeparationPx * SuppressionDepthM / PxPerMetreAtUnitDepth;

        /// <summary>Stages the W3-T4 pair — two map-pitched one-glyph symbols on PARALLEL roads at the same
        /// depth, both <c>AllowOverlap = false</c>, with distinct sort keys and feature indices (a total
        /// placement order) — into ONE shared box/candidate pool at ordinals 0 and 1.</summary>
        private static Pools StageSuppressionPair(SymbolViewTransform view)
        {
            Pools p = Pools.New();
            int a = StageW3Glyph(ref p, in view, CameraFacingUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 40, ordinal: 0);
            int b = StageW3Glyph(ref p, in view, CameraFacingUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, SuppressionLateralM, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 1f, featureIndex: 41, ordinal: 1);
            Assert.That(a == 1 && b == 1, Is.True,
                $"W3-T4 precondition: both labels of the pair must stage — staged {a} and {b}. A zero means " +
                "the road is too short for the label's world span or the anchor spilled.");
            Assert.That(p.BoxCount, Is.EqualTo(2),
                $"W3-T4 precondition: the shared pool must hold exactly one box per label, got {p.BoxCount}.");
            return p;
        }

        private static double HalfHeightPx(in SymbolBox b) => 0.5 * (b.Max.y - b.Min.y);
        private static double HalfWidthPx(in SymbolBox b)  => 0.5 * (b.Max.x - b.Min.x);
        private static double CentreY(in SymbolBox b)      => 0.5 * (b.Max.y + b.Min.y);

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T4-pre — the suppression fixture's three sizing rows, asserted directly
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T4-pre — the preconditions of W3-T4, as their own test.</b> A re-tuned constant must red HERE,
        /// with its numbers, rather than quietly making the headline tooth vacuous (a pair whose boxes no
        /// longer straddle the road separation would return the same survivor count on both arms and pass).
        ///
        /// <para>It also proves the <b>hand-built view transform actually projects</b>. If
        /// <c>ViewMatrix</c>/<c>PerspectiveFov</c> disagreed about which way the camera looks, every corner
        /// would come back <c>clip.w ≤ 0</c>, <c>TryBuildProjectedWorldGlyph</c> would return false, and the
        /// "projected" arm would read the pre-W3 15 px rather than 2 px. See <see cref="ViewMatrix"/>.</para>
        /// </summary>
        [Test]
        public void MapPitched_SuppressionFixture_IsSizedSoTheTwoBoxesStraddleTheRoadGap()
        {
            Pools screen = StageSuppressionPair(default);            // D5 ⇒ the pre-W3 screen box
            Pools projected = StageSuppressionPair(OriginView());     // the W3 box

            double screenHalfHeight = HalfHeightPx(screen.Boxes[0]);
            double projectedHalfHeight = HalfHeightPx(projected.Boxes[0]);
            double screenSeparation = CentreY(screen.Boxes[1]) - CentreY(screen.Boxes[0]);
            double projectedSeparation = CentreY(projected.Boxes[1]) - CentreY(projected.Boxes[0]);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T4-pre  depth={0:F2} m  lateral={1:F2} m  screen half-height={2:F4} px  " +
                "projected half-height={3:F4} px  separation: screen={4:F4} px projected={5:F4} px",
                SuppressionDepthM, SuppressionLateralM, screenHalfHeight, projectedHalfHeight,
                screenSeparation, projectedSeparation));

            Assert.That(screenHalfHeight, Is.EqualTo(SuppressionScreenHalfHeightPx).Within(1e-3),
                $"W3-T4-pre row 1: the PRE-W3 box's half-height must be the depth-free " +
                $"cellHalfBaked·TextSizePx/OneEm = {SuppressionScreenHalfHeightPx:F4} px, read " +
                $"{screenHalfHeight:F4}.");
            Assert.That(projectedHalfHeight, Is.EqualTo(SuppressionTargetHalfHeightPx).Within(0.05),
                $"W3-T4-pre row 2: the W3 box's half-height must foreshorten to the designed " +
                $"{SuppressionTargetHalfHeightPx:F2} px at {SuppressionDepthM:F1} m, read " +
                $"{projectedHalfHeight:F4}. A reading of {SuppressionScreenHalfHeightPx:F1} px means every " +
                "corner failed to project and the box fell back — check ViewMatrix's Z sense.");
            Assert.That(projectedSeparation, Is.EqualTo(SuppressionTargetSeparationPx).Within(0.05),
                $"W3-T4-pre row 3: the roads' projected lateral separation must be the designed " +
                $"{SuppressionTargetSeparationPx:F2} px, read {projectedSeparation:F4}.");
            Assert.That(screenSeparation, Is.EqualTo(SuppressionTargetSeparationPx).Within(0.05),
                $"W3-T4-pre row 3 (screen arm): the pre-W3 boxes ride the SAME projected anchors, so their " +
                $"separation must also be {SuppressionTargetSeparationPx:F2} px, read {screenSeparation:F4} " +
                "— otherwise the two arms differ in more than the box construction.");

            // The discriminator, stated as arithmetic: the separation must sit STRICTLY BETWEEN the two box
            // heights, or the two arms cannot disagree about placement.
            Assert.That(projectedSeparation, Is.GreaterThan(2.0 * projectedHalfHeight),
                $"W3-T4-pre: the projected boxes must CLEAR each other — separation {projectedSeparation:F4} " +
                $"px against a full height of {2.0 * projectedHalfHeight:F4} px.");
            Assert.That(screenSeparation, Is.LessThan(2.0 * screenHalfHeight),
                $"W3-T4-pre: the screen boxes must OVERLAP — separation {screenSeparation:F4} px against a " +
                $"full height of {2.0 * screenHalfHeight:F4} px.");
            Assert.That(HalfWidthPx(projected.Boxes[0]), Is.GreaterThan(0.5),
                "W3-T4-pre: the projected box must have a real width too — a collapsed box would clear its " +
                "neighbour for the wrong reason.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T4 — R2: SUPPRESSION. The old box drops a symbol the new one places.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T4 — THE STAGE'S REASON TO EXIST.</b> Two map-pitched symbols on parallel roads, sized by
        /// W3-T4-pre so their PROJECTED boxes clear each other by 8 px while their pre-W3 SCREEN boxes (15 px
        /// half-height, no depth term) overlap. With a usable view transform the collision pass places BOTH;
        /// with <c>default(SymbolViewTransform)</c> — which D5 makes a reachable state of the SHIPPED code, not
        /// something only an edit can produce — it places ONE.
        ///
        /// <para><b>The tooth contains its own before/after, so it needs no injection.</b> Both arms run the
        /// same shipped binary over the same geometry; the only difference is whether a camera was supplied.
        /// That is exactly the over-reservation W3 removes: the screen box reserved ~150× the ink's area at
        /// depth, and over-reservation can only ever SUPPRESS a symbol, never misplace one.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_PlacesBothSymbols_WhereTheScreenBoxSuppressesOne()
        {
            int projectedSurvivors = Survivors(StageSuppressionPair(OriginView()), "projected");
            int screenSurvivors    = Survivors(StageSuppressionPair(default),      "screen (pre-W3)");

            Assert.That(projectedSurvivors, Is.EqualTo(2),
                $"W3-T4: with the four world corners projected, the two labels' boxes clear each other and " +
                $"BOTH must place — {projectedSurvivors} survived. This is the label the pre-W3 box was " +
                "silently eating at tilt.");
            Assert.That(screenSurvivors, Is.EqualTo(1),
                $"W3-T4 (control): the pre-W3 SCREEN box has no depth term, so at this depth it over-reserves " +
                $"and one of the two must be suppressed — {screenSurvivors} survived. If this reads 2 the " +
                "fixture has stopped discriminating; W3-T4-pre says which row moved.");
        }

        /// <summary>Runs the shared collision pass over a staged pair and reports the survivor set, printing
        /// both boxes so a fixture that stops discriminating fails with its numbers.</summary>
        private static int Survivors(Pools p, string armName)
        {
            var survivor = new bool[2];
            int n = NativeCollisionRunner.RunCollision(p.Candidates, 2, p.Boxes, p.BoxCount, survivor);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T4  {0,-16} boxA=[{1:F3}, {2:F3}]×[{3:F3}, {4:F3}]  boxB=[{5:F3}, {6:F3}]×[{7:F3}, " +
                "{8:F3}]  survivors={9} ({10}, {11})",
                armName, p.Boxes[0].Min.x, p.Boxes[0].Max.x, p.Boxes[0].Min.y, p.Boxes[0].Max.y,
                p.Boxes[1].Min.x, p.Boxes[1].Max.x, p.Boxes[1].Min.y, p.Boxes[1].Max.y,
                n, survivor[0], survivor[1]));
            return n;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T5 — R3: the non-map path cannot observe the view transform, at any magnitude
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T5 — the stage invariant's structural half.</b> Proves: a symbol whose resolved pitch alignment
        /// is not <see cref="AlignmentMode.Map"/> cannot observe the new per-frame view transform AT ALL — its
        /// staged <see cref="SymbolBox"/>es and <see cref="PlacedQuad"/>s are BIT-identical with no transform and
        /// with three genuinely different real ones (different eye, target, field of view, aspect, scene origin
        /// and viewport). Run for <see cref="AlignmentMode.Viewport"/> and for <see cref="AlignmentMode.Auto"/>,
        /// the enum's zero value that every hand-built pre-W1 fixture carries.
        ///
        /// <para>The empirical half of the same claim is the full gate at CP-1 (2154/2154 unmoved); the
        /// sensitivity half is injection I6, which drops the <c>cornerMetresPerLogicalPixel &gt; 0f</c>
        /// conjunct.</para>
        ///
        /// <para><b>The fixture is the W3 camera-facing glyph, NOT <c>StageReferenceSymbol</c>, and that choice
        /// is load-bearing.</b> The reference symbol carries an all-zero surface normal and sits at the camera
        /// origin, so its projection would fail at the ground-frame guard and at <c>clip.w</c> — this tooth
        /// would then be bit-identical across transforms for a reason that has nothing to do with the pitch
        /// predicate, and injection I6 would leave it GREEN. It did, on the first version of this tooth; the
        /// fixture below is what fixes that. Here the symbol has a real normal, a live ruler and a depth at
        /// which every corner projects, so the ONLY thing keeping it off the projected branch is its pitch
        /// alignment.</para>
        /// </summary>
        [Test]
        public void NonMapPitchedSymbol_CannotObserveTheViewTransform()
        {
            SymbolViewTransform[] views = ViewSweep();
            foreach (AlignmentMode mode in new[] { AlignmentMode.Viewport, AlignmentMode.Auto })
            {
                var quadBits = new uint[views.Length][];
                var boxBits  = new uint[views.Length][];
                for (int v = 0; v < views.Length; v++)
                {
                    var p = Pools.New();
                    int staged = StageW3Glyph(ref p, views[v], CameraFacingUp,
                        Cell(SuppressionCellHalfWidthBaked), cellSkirt: 0f, SuppressionTextSizePx,
                        SuppressionMpp, SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                        iconRotateRadians: 0f, sortKey: 0f, featureIndex: 46, ordinal: 0, pitch: mode);
                    Assert.That(staged, Is.EqualTo(1),
                        $"W3-T5 precondition ({mode}, view {v}): the reference label must stage.");
                    quadBits[v] = Bits(p.Quads, p.QuadCount);
                    boxBits[v]  = Bits(p.Boxes, p.BoxCount);
                }

                for (int v = 1; v < views.Length; v++)
                {
                    AssertBitIdentical(quadBits[0], quadBits[v],
                        $"W3-T5 ({mode}): PlacedQuads under view transform {v} differ from those with none");
                    AssertBitIdentical(boxBits[0], boxBits[v],
                        $"W3-T5 ({mode}): SymbolBoxes under view transform {v} differ from those with none");
                }
            }
        }

        /// <summary>The default transform followed by three genuinely different real ones — different eye,
        /// target, field of view, aspect, scene origin and viewport, so a leak through any one of the four
        /// carried values shows up.</summary>
        private static SymbolViewTransform[] ViewSweep() => new[]
        {
            default(SymbolViewTransform),
            OriginView(),
            new SymbolViewTransform
            {
                SceneOriginRender = new double3(1.0e5, 2.0e5, -3.0e5),
                Rebase            = float3x3.identity,
                ViewProj          = math.mul(
                    float4x4.PerspectiveFov(0.6f, 1.7778f, 1f, 1.0e7f),
                    ViewMatrix(new float3(50f, 400f, -900f), float3.zero, new float3(0f, 1f, 0f))),
                ViewportLogicalPx = new double2(1920.0, 1080.0),
            },
            OriginView(2048.0),
        };

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T6 — D10 / F-W3-1: a degenerate ground frame falls back to the screen box, bit-identically
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T6 — the observing tooth for followUp F-W3-1.</b> Proves: a map-pitched symbol whose
        /// per-vertex <c>Up</c> is <see cref="float3.zero"/> — the exact state ~10 older fixtures and
        /// <c>SymbolTileBlockBaker</c> (null <c>PathUpRender</c>) still write, and which that site's own
        /// comment calls "a bug signal, not a supported state" — stages its box BIT-identically to the same
        /// symbol with no view transform at all. It takes the pre-W3 SCREEN box.
        ///
        /// <para><b>This is a KNOWING divergence from the shader, recorded, not fixed.</b>
        /// <c>SymbolWorldMapPitchClip</c>'s degenerate fallback is a camera-facing METRE frame, so the ink and
        /// the box disagree in this state. Reproducing that frame on the CPU needs the view basis in render
        /// space and a fresh handedness derivation, to serve a case unreachable in production. This tooth is
        /// what stops the divergence becoming an unobserved branch — the same role W1-T9 plays for the ruler
        /// guard.</para>
        ///
        /// <para>The non-vacuity clause runs FIRST: with a real <c>Up</c> the very same fixture and the very
        /// same transform must produce a DIFFERENT box, or "bit-identical to the screen box" would be a claim
        /// about a transform that was never usable.</para>
        /// </summary>
        [Test]
        public void MapPitched_WithADegenerateGroundFrame_TakesTheScreenBox_BitIdentically()
        {
            SymbolViewTransform view = OriginView();

            var withUp = Pools.New();
            StageDegenerateProbe(ref withUp, view, CameraFacingUp);
            var noView = Pools.New();
            StageDegenerateProbe(ref noView, default, CameraFacingUp);
            var zeroUp = Pools.New();
            StageDegenerateProbe(ref zeroUp, view, float3.zero);

            double projectedHalfHeight = HalfHeightPx(withUp.Boxes[0]);
            double screenHalfHeight    = HalfHeightPx(noView.Boxes[0]);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T6  projected half-height={0:F4} px  screen half-height={1:F4} px  degenerate-Up " +
                "half-height={2:F4} px", projectedHalfHeight, screenHalfHeight, HalfHeightPx(zeroUp.Boxes[0])));

            Assert.That(math.abs(projectedHalfHeight - screenHalfHeight), Is.GreaterThan(1.0),
                $"W3-T6 precondition (non-vacuity): with a REAL surface normal this transform must produce a " +
                $"genuinely different box — projected {projectedHalfHeight:F4} px against screen " +
                $"{screenHalfHeight:F4} px. Without this the bit-identity below would only say the transform " +
                "was never usable.");
            AssertBitIdentical(Bits(noView.Boxes, noView.BoxCount), Bits(zeroUp.Boxes, zeroUp.BoxCount),
                "W3-T6: a map-pitched label with a ZERO surface normal must fall back to the pre-W3 screen box");
        }

        /// <summary>W3-T6/T7's probe symbol: the camera-facing fixture at the suppression pair's own depth, so
        /// its projected box is the well-understood ≈2 px one and the fallback is unmistakably different.</summary>
        private static void StageDegenerateProbe(ref Pools p, SymbolViewTransform view, float3 surfaceUp)
        {
            int staged = StageW3Glyph(ref p, in view, in surfaceUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 42, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T6/T7 precondition: the probe label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T7 — a corner that fails to project falls back, and does not emit a half-built box
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T7 — the per-corner guard.</b> Proves: when ONE of the four world corners lands behind the
        /// camera, the whole box falls back to the pre-W3 screen box BIT-identically — never a box built from
        /// the three corners that did project.
        ///
        /// <para><b>The geometry.</b> The surface normal here is the ordinary GROUND one, so
        /// <c>ŷ = cross(x̂, up)</c> runs ALONG the view axis and a corner offset moves the corner in DEPTH
        /// without moving the anchor. At <c>text-size</c> 1 em with a ruler of 1000 m per logical px the cell's
        /// 6-baked-px half-height is 6000 m, so on a road 100 m from the camera the up-screen corner sits at
        /// <c>z = −5900 m</c> and <c>TryProjectPoint</c> rejects it on <c>clip.w ≤ 0</c>.</para>
        ///
        /// <para><b>Reachability, reported not assumed (followUp F-W2-8).</b> The construction needs a ruler of
        /// 1000 metres per logical pixel at a view depth of 100 metres — a glyph 120× taller than its distance
        /// to the camera. No pose this renderer produces is anywhere near it: the shipped fixture's ruler is
        /// ~306 m/px at a look-at depth of tens of kilometres. So this stays a guard for an unreachable state,
        /// and F-W2-8 is NOT shown to be reachable by it. The deep arm below is the same fixture pushed to a
        /// depth where all four corners DO project, which is what makes the fallback attributable to the corner
        /// rejection rather than to the extreme scale.</para>
        /// </summary>
        [Test]
        public void MapPitched_WhenACornerIsBehindTheCamera_TakesTheScreenBox_BitIdentically()
        {
            SymbolViewTransform view = OriginView();
            const float hugeRulerMpp = 1000f;
            const double shallowDepthM = 100.0;      // corner half-height 6000 m ⇒ one corner at z = −5900 m
            const double deepDepthM = 100000.0;      // both corners in front (94 km / 106 km)

            var deepProjected = Pools.New();
            StageCornerProbe(ref deepProjected, view, hugeRulerMpp, deepDepthM);
            var deepScreen = Pools.New();
            StageCornerProbe(ref deepScreen, default, hugeRulerMpp, deepDepthM);
            var shallowProjected = Pools.New();
            StageCornerProbe(ref shallowProjected, view, hugeRulerMpp, shallowDepthM);
            var shallowScreen = Pools.New();
            StageCornerProbe(ref shallowScreen, default, hugeRulerMpp, shallowDepthM);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T7  deep: projected=[{0:F3}, {1:F3}] screen=[{2:F3}, {3:F3}]  shallow: projected=" +
                "[{4:F3}, {5:F3}] screen=[{6:F3}, {7:F3}]",
                deepProjected.Boxes[0].Min.x, deepProjected.Boxes[0].Max.x,
                deepScreen.Boxes[0].Min.x, deepScreen.Boxes[0].Max.x,
                shallowProjected.Boxes[0].Min.x, shallowProjected.Boxes[0].Max.x,
                shallowScreen.Boxes[0].Min.x, shallowScreen.Boxes[0].Max.x));

            // Non-vacuity first: at a depth where every corner projects, the SAME transform and the SAME cell
            // give a genuinely different box — so the fallback below is attributable to the rejected corner.
            Assert.That(BitsEqual(Bits(deepProjected.Boxes, deepProjected.BoxCount),
                                  Bits(deepScreen.Boxes, deepScreen.BoxCount)), Is.False,
                "W3-T7 precondition (non-vacuity): with every corner in front of the camera the projected box " +
                "must differ from the screen box, or the bit-identity below says nothing about the corner guard.");

            AssertBitIdentical(Bits(shallowScreen.Boxes, shallowScreen.BoxCount),
                Bits(shallowProjected.Boxes, shallowProjected.BoxCount),
                "W3-T7: with one corner behind the camera the box must be the pre-W3 screen box");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T11 — F-W3-8: a corner that projects to a near-plane BLOW-UP falls back, so the AABB is bounded
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T11 — the magnitude guard, F-W3-8.</b> A corner just IN FRONT of the camera plane passes the
        /// behind-camera test (<c>clip.w &gt; 0</c>) and then divides into an arbitrarily large screen
        /// coordinate. It is FINITE, so no NaN check sees it, and before this guard it made the collision AABB
        /// unbounded — where the pre-W3 screen box was bounded by the cell. Proves: such a corner takes the
        /// screen-box fallback, BIT-identically.
        ///
        /// <para><b>Why this is a different state from W3-T7's</b>, and why one tooth cannot cover both: T7's
        /// corner is BEHIND the camera and is rejected by <c>TryProjectPoint</c> itself. This corner
        /// PROJECTS — successfully, to a real finite number — and is rejected only by the magnitude test.
        /// Removing the magnitude guard leaves T7 green (see the RED sweep), which is precisely why this tooth
        /// exists.</para>
        ///
        /// <para><b>The geometry.</b> Same GROUND-normal probe as W3-T7, so ŷ runs along the view axis and a
        /// corner offset moves the corner in DEPTH. The cell's half-height is 6000 m at this ruler, so an
        /// anchor at 6001 m puts the near corner at <c>z ≈ 1 m</c> — in front, so it projects, but with a
        /// <c>clip.w</c> three orders of magnitude below the corner's own lateral offset.</para>
        ///
        /// <para><b>Reachability: NONE, same as W3-T7.</b> This needs 1000 m per logical px at a 6 km depth.
        /// The shipped fixture's ruler is ~306 m/px at a look-at depth of tens of km. It is a guard for an
        /// unreachable state, kept because "unbounded" is not a state this system should be able to enter at
        /// all — not because a pose produces it.</para>
        /// </summary>
        [Test]
        public void MapPitched_WhenACornerBlowsUpNearThePlane_TakesTheScreenBox_BitIdentically()
        {
            SymbolViewTransform view = OriginView();
            const float hugeRulerMpp = 1000f;             // cell half-height 6 baked px ⇒ 6000 m
            const double blowUpDepthM = 6001.0;           // near corner at z ≈ 1 m: in FRONT, but barely
            const double deepDepthM = 100000.0;           // both corners comfortably in front

            var deepProjected = Pools.New();
            StageCornerProbe(ref deepProjected, view, hugeRulerMpp, deepDepthM);
            var blowUpProjected = Pools.New();
            StageCornerProbe(ref blowUpProjected, view, hugeRulerMpp, blowUpDepthM);
            var blowUpScreen = Pools.New();
            StageCornerProbe(ref blowUpScreen, default, hugeRulerMpp, blowUpDepthM);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T11  deep: [{0:F3}, {1:F3}]  blow-up projected: [{2:F3}, {3:F3}]  blow-up screen: [{4:F3}, {5:F3}]",
                deepProjected.Boxes[0].Min.x, deepProjected.Boxes[0].Max.x,
                blowUpProjected.Boxes[0].Min.x, blowUpProjected.Boxes[0].Max.x,
                blowUpScreen.Boxes[0].Min.x, blowUpScreen.Boxes[0].Max.x));

            // Non-vacuity 1: the deep arm must take the PROJECTED branch, or "falls back" below says nothing.
            Assert.That(BitsEqual(Bits(deepProjected.Boxes, deepProjected.BoxCount),
                                  Bits(blowUpScreen.Boxes, blowUpScreen.BoxCount)), Is.False,
                "W3-T11 precondition: at a depth where every corner projects sanely the projected box must " +
                "differ from the screen box.");

            // Non-vacuity 2: this must be a DIFFERENT state from W3-T7's, or the tooth is a duplicate that the
            // behind-camera rejection would satisfy on its own. The near corner sits one cell half-height
            // up-axis of the anchor, so its view depth is (anchor − halfHeight) — assert that is POSITIVE, i.e.
            // the corner is in FRONT of the camera and TryProjectPoint ACCEPTS it. Only the magnitude test can
            // reject it.
            const double cellHalfHeightM = 6.0 * hugeRulerMpp;         // 6 baked px at 1 em, in metres
            const double nearCornerDepthM = blowUpDepthM - cellHalfHeightM;
            Assert.That(nearCornerDepthM, Is.GreaterThan(0.0),
                $"W3-T11 precondition: the near corner must be IN FRONT of the camera (depth " +
                $"{nearCornerDepthM} m) — otherwise this duplicates W3-T7's behind-camera rejection.");

            // Non-vacuity 3: the fallback the tooth asserts must itself be bounded — that is the whole point.
            float guardedWidth = blowUpScreen.Boxes[0].Max.x - blowUpScreen.Boxes[0].Min.x;
            Assert.That(guardedWidth, Is.LessThan(SymbolScreenProjectionMaxProjectedPx),
                $"W3-T11 precondition: the fallback box must be BOUNDED — got width {guardedWidth}.");

            AssertBitIdentical(Bits(blowUpScreen.Boxes, blowUpScreen.BoxCount),
                Bits(blowUpProjected.Boxes, blowUpProjected.BoxCount),
                "W3-T11: a corner projecting past MaxProjectedPx must take the pre-W3 screen box");
        }

        /// <summary>Mirror of <c>SymbolScreenProjection.MaxProjectedPx</c>. Re-stated here rather than read
        /// back, so a change to the production threshold does not silently move this tooth's expectation.</summary>
        private const float SymbolScreenProjectionMaxProjectedPx = 1e5f;

        /// <summary>W3-T7's probe: the GROUND-normal fixture, whose ŷ runs along the view axis so a corner
        /// offset moves the corner in DEPTH.</summary>
        private static void StageCornerProbe(ref Pools p, SymbolViewTransform view, float mpp, double depthM)
        {
            int staged = StageW3Glyph(ref p, in view, GroundUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, textSizePx: TextQuadLayout.OneEm, mpp,
                depthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 43, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T7 precondition: the probe label must stage.");
        }

        private static bool BitsEqual(uint[] a, uint[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T8 — D7: `icon-rotate` is INSIDE the map-pitched box
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T8 — the tooth behind E10's KL-B1 amendment.</b> Proves: the map-pitched box includes the
        /// constant <c>icon-rotate</c> the renderer rotates the drawn corners by. A one-glyph map-pitched icon
        /// on a NON-SQUARE cell is staged at <c>icon-rotate</c> 0 and at 90°, and the box's projected
        /// half-extents SWAP.
        ///
        /// <para><b>The expected numbers are derived from the CELL's own corners</b> — half-extent
        /// <c>= PxPerMetreAtUnitDepth · (cellHalfBaked · TextSizePx · mpp / OneEm) / depth</c> — and
        /// <c>SymbolBearing.IconRotationRadians</c> is deliberately NOT read back. Because the cell is centred on
        /// its anchor, +90° and −90° produce the SAME axis-aligned bound, so this tooth is independent of the
        /// sign that conversion applies; what it pins is that the rotation is APPLIED AT ALL.</para>
        ///
        /// <para><b>Why 90° and not the shipped 180°.</b> 180° is the only <c>icon-rotate</c> any shipped
        /// map-pitched layer carries, and on a centre-anchored cell it is its own inverse — the AABB is
        /// identical, so it CANNOT discriminate. That is also why D7 is numerically a no-op on every shipped
        /// style even though it is a real change to the box.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_IncludesIconRotate()
        {
            SymbolViewTransform view = OriginView();
            const float cellHalfWidthBaked = 20f; // ⇒ a 40 × 12 baked cell: wide, so the swap is visible

            var upright = Pools.New();
            StageRotationProbe(ref upright, view, cellHalfWidthBaked, 0f);
            var turned = Pools.New();
            StageRotationProbe(ref turned, view, cellHalfWidthBaked, (float)(0.5 * math.PI_DBL));

            double metresPerBaked = SuppressionTextSizePx * SuppressionMpp / TextQuadLayout.OneEm;
            double pxPerBaked = PxPerMetreAtUnitDepth * metresPerBaked / SuppressionDepthM;
            double expectedHalfWidth  = cellHalfWidthBaked * pxPerBaked;
            double expectedHalfHeight = SuppressionCellHalfHeightBaked * pxPerBaked;

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T8  upright half-extents=({0:F4}, {1:F4}) px  turned=({2:F4}, {3:F4}) px  expected " +
                "upright=({4:F4}, {5:F4})",
                HalfWidthPx(upright.Boxes[0]), HalfHeightPx(upright.Boxes[0]),
                HalfWidthPx(turned.Boxes[0]), HalfHeightPx(turned.Boxes[0]),
                expectedHalfWidth, expectedHalfHeight));

            Assert.That(math.abs(expectedHalfWidth - expectedHalfHeight), Is.GreaterThan(1.0),
                $"W3-T8 precondition: the cell must be far from square in projection — half-extents " +
                $"{expectedHalfWidth:F4} and {expectedHalfHeight:F4} px — or a 90° rotation changes nothing " +
                "and this tooth discriminates nothing.");
            Assert.That(HalfWidthPx(upright.Boxes[0]), Is.EqualTo(expectedHalfWidth).Within(0.05),
                "W3-T8: at icon-rotate 0 the box's projected half-WIDTH must be the cell's own half-width.");
            Assert.That(HalfHeightPx(upright.Boxes[0]), Is.EqualTo(expectedHalfHeight).Within(0.05),
                "W3-T8: at icon-rotate 0 the box's projected half-HEIGHT must be the cell's own half-height.");
            Assert.That(HalfWidthPx(turned.Boxes[0]), Is.EqualTo(expectedHalfHeight).Within(0.05),
                $"W3-T8: at icon-rotate 90° the half-WIDTH must become the cell's half-HEIGHT " +
                $"({expectedHalfHeight:F4} px), read {HalfWidthPx(turned.Boxes[0]):F4}. An unchanged " +
                "half-width means the box omits icon-rotate and no longer bounds what the renderer draws.");
            Assert.That(HalfHeightPx(turned.Boxes[0]), Is.EqualTo(expectedHalfWidth).Within(0.05),
                $"W3-T8: at icon-rotate 90° the half-HEIGHT must become the cell's half-WIDTH " +
                $"({expectedHalfWidth:F4} px), read {HalfHeightPx(turned.Boxes[0]):F4}.");
        }

        private static void StageRotationProbe(ref Pools p, SymbolViewTransform view,
            float cellHalfWidthBaked, float iconRotateRadians)
        {
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, Cell(cellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians, sortKey: 0f, featureIndex: 44, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T8 precondition: the icon label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T9 — D9: the skirt is removed from the map-pitched box too
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T9 — the skirt contract, carried onto the projected box.</b> Proves: a map-pitched icon whose
        /// cell carries a transparent border (<c>CurvedGlyph.CellSkirt</c>) gets the SAME projected box as the
        /// same symbol with that border already removed from the cell and a zero skirt — i.e. the box bounds the
        /// icon's INK, not its skirt. The exact shape
        /// <c>SymbolStagingMathCurvedVertexTests.BuildRotatedGlyph_WithACellSkirt_EqualsTheSameCellPreShrunkByIt</c>
        /// already asserts for the screen box, now for the projected one.
        ///
        /// <para>Non-vacuity: against the SAME padded cell with a zero skirt the box must be strictly larger,
        /// so "equal to the pre-shrunk cell" cannot pass by the skirt simply being ignored on both sides.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_RemovesTheCellSkirt()
        {
            SymbolViewTransform view = OriginView();
            const float skirt = 3f;
            SymbolQuad padded = Cell(20f);
            var content = new SymbolQuad
            {
                TopLeft     = padded.TopLeft     + new float2(skirt, -skirt),
                BottomRight = padded.BottomRight - new float2(skirt, -skirt),
            };

            var withSkirt = Pools.New();
            StageSkirtProbe(ref withSkirt, view, padded, skirt);
            var preShrunk = Pools.New();
            StageSkirtProbe(ref preShrunk, view, content, 0f);
            var unshrunk = Pools.New();
            StageSkirtProbe(ref unshrunk, view, padded, 0f);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T9  withSkirt half-extents=({0:F4}, {1:F4})  preShrunk=({2:F4}, {3:F4})  unshrunk=" +
                "({4:F4}, {5:F4}) px",
                HalfWidthPx(withSkirt.Boxes[0]), HalfHeightPx(withSkirt.Boxes[0]),
                HalfWidthPx(preShrunk.Boxes[0]), HalfHeightPx(preShrunk.Boxes[0]),
                HalfWidthPx(unshrunk.Boxes[0]), HalfHeightPx(unshrunk.Boxes[0])));

            Assert.That(HalfWidthPx(unshrunk.Boxes[0]), Is.GreaterThan(HalfWidthPx(withSkirt.Boxes[0]) + 1e-4),
                "W3-T9 precondition (non-vacuity): a non-zero cell skirt must SHRINK the projected box — " +
                "otherwise this tooth proves nothing.");
            AssertBitIdentical(Bits(preShrunk.Boxes, preShrunk.BoxCount),
                Bits(withSkirt.Boxes, withSkirt.BoxCount),
                "W3-T9: the projected box of a skirted cell must equal that of the same cell pre-shrunk by it");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T10 — the ŷ SENSE of the projected box, on a cell that can actually see it
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T10 — the observer for <c>TryBuildProjectedWorldGlyph</c>'s <c>ŷ = cross(x̂, up)</c>.</b>
        /// Proves: a positive y-UP cell coordinate lands ABOVE the anchor on screen, by the amount the cell's
        /// own corners predict.
        ///
        /// <para><b>Why it exists, and why nothing else in the stage does this job.</b> The box is an
        /// <b>AABB</b> of the four cell corners, and flipping ŷ maps corner <c>(cx, cy) → (cx, −cy)</c> — a
        /// PERMUTATION of the corner set whenever the cell is symmetric about its anchor in y, and the AABB of
        /// a set is invariant under permutation. The shipped <c>'F'</c> cell is block-centred and W4 centres
        /// curved cells optically on the path, and every other synthetic cell here is <c>Cell(hw)</c> =
        /// <c>(±hw, ±6)</c> — so a flipped ŷ was measured (injection I2) to leave the ENTIRE suite green,
        /// W3-T1, W3-T2 and W3-T3 included. W2's 22.56 px flipped-sign separation was an ink-CENTROID reading
        /// and does not carry to an AABB. A sign must be
        /// read where the code is not inert, and for an AABB that means a cell whose y extent does NOT
        /// straddle its anchor.</para>
        ///
        /// <para>The cell here sits entirely ABOVE its anchor (y from +6 to +18 baked), so a flip translates
        /// the whole box by <c>|top + bottom| · pxPerBaked</c> — asserted as a precondition to be well over a
        /// pixel, against edge expectations derived from the cell's own corners.</para>
        ///
        /// <para><b>What this does NOT pin.</b> Like W3-T1's oracle it re-derives the sense rather than
        /// importing it from an independent reference, so it catches a production-only flip, not a SHARED
        /// convention error. The independent reference for the shader's own sign remains W2's tilt-0 ink
        /// centroid (<c>MapPitchedGlyphSizeTiltZeroTests</c>), and for the CPU box the closest thing is W3-T2's
        /// ink containment — which, being a containment, only bites once the flip exceeds the box.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_PutsAPositiveCellYAboveTheAnchor()
        {
            SymbolViewTransform view = OriginView();
            // Entirely ABOVE the anchor — the whole point (see this tooth's doc).
            const float cellTopBaked = 18f, cellBottomBaked = 6f, cellHalfWidthBaked = 20f;
            var offCentreCell = new SymbolQuad
            {
                TopLeft     = new float2(-cellHalfWidthBaked, cellTopBaked),
                BottomRight = new float2( cellHalfWidthBaked, cellBottomBaked),
            };

            var p = Pools.New();
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, in offCentreCell,
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 47, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T10 precondition: the off-centre-cell label must stage.");

            double metresPerBaked = SuppressionTextSizePx * SuppressionMpp / TextQuadLayout.OneEm;
            double pxPerBaked = PxPerMetreAtUnitDepth * metresPerBaked / SuppressionDepthM;
            double anchorY = 0.5 * ViewportPx;                  // the road is at lateral 0 ⇒ screen centre
            double expectedMinY = anchorY + cellBottomBaked * pxPerBaked;
            double expectedMaxY = anchorY + cellTopBaked * pxPerBaked;
            double flipSeparationPx = (cellTopBaked + cellBottomBaked) * pxPerBaked;

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T10  anchorY={0:F3}  box y=[{1:F4}, {2:F4}]  expected=[{3:F4}, {4:F4}]  a flipped ŷ would " +
                "move it by {5:F4} px", anchorY, p.Boxes[0].Min.y, p.Boxes[0].Max.y, expectedMinY, expectedMaxY,
                flipSeparationPx));

            Assert.That(flipSeparationPx, Is.GreaterThan(1.0),
                $"W3-T10 precondition: the cell must be genuinely OFF-CENTRE in y — a flip would move the box " +
                $"by {flipSeparationPx:F4} px, and below ~1 px this tooth stops discriminating the sign it " +
                "exists to read.");
            Assert.That(p.Boxes[0].Min.y, Is.EqualTo(expectedMinY).Within(0.05),
                $"W3-T10: the box's LOWER edge must be the cell's own +{cellBottomBaked} baked px ABOVE the " +
                $"anchor ({expectedMinY:F4}), read {p.Boxes[0].Min.y:F4}. A value BELOW the anchor means " +
                "ŷ = cross(x̂, up) has been flipped and the box sits on the wrong side of the road.");
            Assert.That(p.Boxes[0].Max.y, Is.EqualTo(expectedMaxY).Within(0.05),
                $"W3-T10: the box's UPPER edge must be the cell's own +{cellTopBaked} baked px above the " +
                $"anchor ({expectedMaxY:F4}), read {p.Boxes[0].Max.y:F4}.");
            Assert.That(p.Boxes[0].Min.y, Is.GreaterThan(anchorY),
                $"W3-T10: a cell lying entirely above its anchor must produce a box lying entirely above the " +
                $"anchor's projection ({anchorY:F3}) — read a lower edge of {p.Boxes[0].Min.y:F4}.");
        }

        private static void StageSkirtProbe(ref Pools p, SymbolViewTransform view, SymbolQuad cell, float skirt)
        {
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, in cell,
                skirt, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 45, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T9 precondition: the icon label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GLOBE-A — the CPU ground frame with a NON-ZERO Gram-Schmidt axial term (GA-T1…GA-T3)
        //
        // SymbolBox.TryBuildProjectedWorldGlyph builds the glyph's ground frame as
        //
        //     axial = dot(tangent, up);   x̂ = normalize(tangent − up·axial);   ŷ = cross(x̂, up)
        //
        // On EVERY other fixture in this repo `axial` is EXACTLY ZERO — Mercator's surface normal is the
        // constant (0,1,0) and every baked road tangent is horizontal, so the tangent already lies IN the
        // surface and the subtraction is a no-op. The orthogonalisation has therefore never been exercised
        // by anything (recorded as followUp F-W3-3, and F-W2-3 for the shader's own copy). These teeth are
        // the first that reach it.
        //
        // SCOPE. The CPU copy ONLY. The shader's copy (SymbolWorldPitchAlign.hlsl:95-106) is unreachable
        // from a staging test — WorldBillboardVertex.Up is carried but never written back, and
        // ShaderStructureTests parses source text rather than running it — so nothing below claims to
        // observe it. That half needs a rendered ink measurement and is a separate stage.
        //
        // THE TRAP THIS SECTION EXISTS TO AVOID, TWICE OVER.
        //   1. `axial ≈ (t − ½)·θ` is EXACTLY ZERO at the chord midpoint. Every existing curved fixture
        //      anchors its symbol at the arc midpoint, so a spherical fixture built the obvious way is
        //      exactly as blind as Mercator while LOOKING like coverage. GA-T2 anchors at t = 0.9 and
        //      GA-T1 asserts the achieved |axial| against an absolute floor with its measured value
        //      printed; GA-T3 stages the midpoint deliberately, as the contrast that proves the placement
        //      is load-bearing rather than decorative.
        //   2. Anchoring at an interior POLYLINE VERTEX is a second, unrecorded inert shape.
        //      StageCurvedAnchor orients the glyph by the CHORD across its own footprint, not by the raw
        //      segment direction; at an interior vertex that probe straddles the bend symmetrically and
        //      reproduces the true surface tangent, so `axial` collapses to ~0 again. Hence a SINGLE
        //      segment with the anchor off its midpoint: the probe then stays inside the one chord, so the
        //      tangent is the chord direction at every t while the sampled up swings with t.
        //
        // HAND-BUILT SPHERE, NOT SphericalProjection.ProjectPoint. The frame math consumes exactly two
        // geometric inputs — tangentRender and surfaceUp — and is indifferent to where they came from; that
        // the projection emits a genuinely varying radial up along a curved feature is already pinned by
        // SymbolUpCarrierChainTests. Building the arc here buys the thing that matters: the expectations
        // below call NO production code at all (no ProjectPoint, no TangentBasisAt), so a shared error
        // cannot hide inside the oracle. What is not hand-waved is the sphere itself — the radius and the
        // segment arc are production's own constants, and GA-T1 asserts both on-sphere invariants directly.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The globe fixture's sphere radius — production's own, so the fixture cannot drift onto a
        /// toy sphere whose arc-to-metre relationship is not the one the renderer ships.</summary>
        private const double GlobeRadiusM = SphericalProjection.Radius;

        /// <summary>The great-circle arc ONE path segment subtends: production's own subdivision cap
        /// (<c>SphericalProjection.MaxCurveSegmentRad</c>, 2°). This is the WORST case a realistically built
        /// spherical path can present to the ground frame, since <c>SymbolFeatureExtractor</c> subdivides to
        /// this bound — so the fixture is not inflating the angle. Narrowing the cap will red GA-T1's
        /// |axial| floor, deliberately: the achieved signal is a function of it.</summary>
        private const double GlobeSegmentRad = SphericalProjection.MaxCurveSegmentRad;

        /// <summary>The map tilt: the angle between the surface normal at the anchor and the direction back
        /// to the camera. The screen consequence of a mis-built x̂ scales with <c>sin(tilt)</c> — it is pure
        /// DEPTH error at tilt 0, which is why <see cref="CameraFacingUp"/> could never observe this — so a
        /// tilt-0 arm would be inert however non-zero the axial term was. 60° is a realistic upper-ish tilt
        /// and costs only 13% of the grazing-case signal.</summary>
        private static readonly double GlobeTiltRad = math.radians(60.0);

        /// <summary>The arc's "east": the direction the road runs at the start of the arc. With
        /// <see cref="OriginView"/>'s camera looking down <c>+Ẑ</c> this puts the symbol across the screen.</summary>
        private static readonly double3 GlobeEast = new double3(1.0, 0.0, 0.0);

        /// <summary>The surface normal at arc angle 0 — tilted <see cref="GlobeTiltRad"/> away from facing
        /// the camera. Together with <see cref="GlobeEast"/> it spans the arc's plane.</summary>
        private static readonly double3 GlobeUpAtArcStart =
            new double3(0.0, math.sin(GlobeTiltRad), -math.cos(GlobeTiltRad));

        /// <summary>The arc plane's normal, <c>ê₁ × ê₂</c>. This is EXACTLY the ground frame's ŷ at every
        /// sample: for x̂ = cos α·ê₁ − sin α·ê₂ and up = sin α·ê₁ + cos α·ê₂,
        /// <c>cross(x̂, up) = (cos²α + sin²α)·(ê₁ × ê₂) = ê₃</c>, independent of α. Stated here as fixture
        /// geometry so the expectation does not have to evaluate production's cross product.</summary>
        private static readonly double3 GlobeAcross = math.cross(GlobeEast, GlobeUpAtArcStart);

        /// <summary>`text-size` for the globe teeth — 7 em. <b>A DELIBERATE AMPLIFICATION, and the tooth
        /// says so.</b> The omitted-orthogonalisation error is <c>cellHalfWidthBaked/OneEm · TextSizePx ·
        /// axial · sin(tilt)</c> px: ≈1.6 px here, but ≈0.19 px at an ordinary 16 px text size, which is
        /// structurally invisible. See GA-T2's doc for what that means about the tooth's claim.</summary>
        private const float GlobeTextSizePx = 7f * TextQuadLayout.OneEm;

        /// <summary>The per-frame ruler. With <see cref="GlobeDepthM"/> derived from it below, one world
        /// metre at the anchor is exactly this many logical px — so the fixture's ruler and its pose agree
        /// and the drawn glyph is the size `text-size` says it is.</summary>
        private const float GlobeMpp = 50f;

        /// <summary>The anchor's view depth, chosen so <see cref="GlobeMpp"/> is the TRUE metres-per-pixel
        /// there (<c>depth = mpp · pxPerMetreAtUnitDepth</c>).</summary>
        private static readonly double GlobeDepthM = GlobeMpp * PxPerMetreAtUnitDepth;

        /// <summary>The parametric position of the anchor along the single chord. <b>0.9, not 0.5</b> — see
        /// this section's header. At the chord midpoint the axial term is exactly zero.</summary>
        private const double GlobeAnchorT = 0.9;

        private const float GlobeCellHalfWidthBaked = 20f;
        private const float GlobeCellTopBaked       = 18f;
        private const float GlobeCellBottomBaked    = 6f;

        /// <summary>The globe teeth's glyph cell: y-OFF-CENTRE, entirely above its anchor. F-W3-6 measured
        /// that an AABB is invariant under a ŷ flip on a y-symmetric cell, so a symmetric cell here would
        /// leave the ŷ leg of the frame unobserved for free. W3-T10's shape, reused for the same reason.</summary>
        private static SymbolQuad GlobeCell() => new SymbolQuad
        {
            TopLeft     = new float2(-GlobeCellHalfWidthBaked, GlobeCellTopBaked),
            BottomRight = new float2( GlobeCellHalfWidthBaked, GlobeCellBottomBaked),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1f, 1f), LineIndex = 0,
        };

        /// <summary>The unit radial normal at arc angle <paramref name="phi"/>, in the arc's own plane.</summary>
        private static double3 GlobeSurfaceUp(double phi)
            => math.sin(phi) * GlobeEast + math.cos(phi) * GlobeUpAtArcStart;

        /// <summary>The on-sphere point at arc angle <paramref name="phi"/>, in the SPHERE's frame (centre at
        /// the origin) — exactly <see cref="GlobeRadiusM"/> from the centre, by construction.</summary>
        private static double3 GlobeSurfacePoint(double phi) => GlobeRadiusM * GlobeSurfaceUp(phi);

        /// <summary>
        /// Builds the fixture's ONE on-sphere segment, translated so the anchor at <paramref name="t"/>
        /// lands at render <c>(0, 0, GlobeDepthM)</c> — dead centre of <see cref="OriginView"/>'s viewport.
        /// The path vertices are on the sphere and <paramref name="worldUps"/> are their exact radial
        /// normals (narrowed to <c>float3</c>, which is the type the carrier chain uses).
        /// </summary>
        private static void BuildGlobeArc(double t, out double3[] worldPath, out float3[] worldUps,
            out double3 sphereCentreRender, out double3 anchorRender)
        {
            double3 startPoint = GlobeSurfacePoint(0.0);
            double3 endPoint   = GlobeSurfacePoint(GlobeSegmentRad);

            anchorRender = new double3(0.0, 0.0, GlobeDepthM);
            sphereCentreRender = anchorRender - math.lerp(startPoint, endPoint, t);

            worldPath = new[] { startPoint + sphereCentreRender, endPoint + sphereCentreRender };
            worldUps  = new[] { NarrowToFloat3(GlobeSurfaceUp(0.0)), NarrowToFloat3(GlobeSurfaceUp(GlobeSegmentRad)) };
        }

        private static float3 NarrowToFloat3(in double3 v) => new float3((float)v.x, (float)v.y, (float)v.z);

        /// <summary>Stages ONE map-pitched glyph on the globe arc, anchored at <paramref name="t"/>. The
        /// screen polyline is the TRUE projection of the world polyline (<see cref="ProjectPx"/>), so the
        /// pre-W3 screen box the control arm reads is the box the shipped code would really have built.</summary>
        private static int StageGlobeGlyph(ref Pools p, in SymbolViewTransform view, double t, int featureIndex)
        {
            BuildGlobeArc(t, out double3[] worldPath, out float3[] worldUps, out _, out _);
            var screenPath = new[] { ProjectPx(worldPath[0]), ProjectPx(worldPath[1]) };
            var glyphs  = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = GlobeCell() } };
            var anchors = new[] { new LineAnchor(0, (float)t) };
            CurvedStageInput s = Input(AlignmentMode.Map, GlobeMpp, GlobeTextSizePx,
                maxAngleDeg: 180f, featureIndex);
            return Stage(in s, screenPath, worldPath, glyphs, anchors, ref p,
                view: view, worldUpPathOverride: worldUps);
        }

        /// <summary>The angle, within the arc's plane, of the surface normal <c>PolylineArcMath.SampleUp</c>
        /// produces at <paramref name="t"/> — <c>normalize(lerp(up₀, up₁, t))</c> written out in closed form.
        /// At <c>t = ½</c> the half-angle identity makes this exactly <c>θ/2</c>, which is the chord's own
        /// angle: that identity IS the midpoint trap.</summary>
        private static double GlobeSampledUpAngle(double t)
            => math.atan2(t * math.sin(GlobeSegmentRad), (1.0 - t) + t * math.cos(GlobeSegmentRad));

        /// <summary>The chord direction of the single segment — the tangent production resolves at EVERY
        /// <c>t</c> on it. Its angle within the arc plane is <c>θ/2</c>, the arc's midpoint tangent.</summary>
        private static double3 GlobeChordDirection()
            => math.cos(GlobeSegmentRad * 0.5) * GlobeEast - math.sin(GlobeSegmentRad * 0.5) * GlobeUpAtArcStart;

        /// <summary>
        /// The screen AABB the fixture's own geometry predicts, built with the ground frame whose x̂ lies at
        /// <paramref name="frameAngleRad"/> within the arc plane. Two callers, and the difference between
        /// them is the whole tooth:
        /// <list type="bullet">
        /// <item><c>GlobeSampledUpAngle(t)</c> — the CORRECT in-surface frame. x̂ is characterised here as
        /// "the sampled surface normal rotated 90° within the arc's plane, on the +ê₁ side", which is a
        /// statement about the fixture's geometry and NOT a second evaluation of
        /// <c>normalize(tangent − up·axial)</c>. ŷ is <see cref="GlobeAcross"/>, exactly.</item>
        /// <item><c>GlobeSegmentRad/2</c> — the chord's own angle, i.e. what the frame degenerates to if the
        /// orthogonalisation is DROPPED and x̂ is just the normalized tangent. Used only to size the
        /// separation GA-T2 asserts as its non-vacuity floor. (It approximates the dropped-subtraction ŷ as
        /// ê₃ too; the real one is <c>cross(tangent, up)</c>, shorter by <c>1 − cos ε ≈ 1e-4</c>, which
        /// moves no pixel that matters here.)</item>
        /// </list>
        /// </summary>
        private static void GlobeExpectedBox(double frameAngleRad, out double2 min, out double2 max)
        {
            double3 anchorRender = new double3(0.0, 0.0, GlobeDepthM);
            double3 alongSurface = math.cos(frameAngleRad) * GlobeEast - math.sin(frameAngleRad) * GlobeUpAtArcStart;
            double3 acrossSurface = GlobeAcross;

            // The same metres-per-baked-px the renderer applies: TextSizePx · metresPerLogicalPixel / OneEm.
            double metresPerBaked = GlobeTextSizePx * (double)GlobeMpp / TextQuadLayout.OneEm;
            double halfWidthM = GlobeCellHalfWidthBaked * metresPerBaked;
            double topM       = GlobeCellTopBaked       * metresPerBaked;
            double bottomM    = GlobeCellBottomBaked    * metresPerBaked;

            double2 topLeft     = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface, -halfWidthM, topM);
            double2 topRight    = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface,  halfWidthM, topM);
            double2 bottomRight = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface,  halfWidthM, bottomM);
            double2 bottomLeft  = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface, -halfWidthM, bottomM);

            min = math.min(math.min(topLeft, topRight), math.min(bottomRight, bottomLeft));
            max = math.max(math.max(topLeft, topRight), math.max(bottomRight, bottomLeft));
        }

        /// <summary>Displaces ONE y-up corner in the stated ground frame and projects it with the fixture's
        /// own <see cref="ProjectPx"/> arithmetic — <c>½·viewport + pxPerMetreAtUnitDepth · offset / depth</c>,
        /// two constants and a divide, reading nothing back out of production.</summary>
        private static double2 GlobeProjectCorner(in double3 anchorRender, in double3 alongSurface,
            in double3 acrossSurface, double cornerX, double cornerY)
        {
            double3 corner = anchorRender + alongSurface * cornerX + acrossSurface * cornerY;
            return new double2(
                0.5 * ViewportPx + PxPerMetreAtUnitDepth * corner.x / corner.z,
                0.5 * ViewportPx + PxPerMetreAtUnitDepth * corner.y / corner.z);
        }

        /// <summary>
        /// Stages the globe fixture TWICE — once with no camera (<c>view: default</c>, W3's D5 state ⇒ the
        /// pre-W3 SCREEN box) and once through <see cref="OriginView"/> (the projected box) — and asserts the
        /// two differ substantially.
        ///
        /// <para><b>This is the control arm, and it is not optional.</b>
        /// <c>TryBuildProjectedWorldGlyph</c> sits behind five gates — <c>cornerMetresPerLogicalPixel &gt; 0</c>,
        /// <c>view.IsUsable</c>, a zero up, a zero tangent, and <c>|axial| &gt; 1 − 1e-3</c> — and every one
        /// of them SILENTLY yields the screen box instead. W3-T5 in this epic held for exactly that reason
        /// (an all-zero surface normal tripped guard 1 long before the quantity it claimed to test mattered),
        /// and it was found only because an injection failed to red it. A globe tooth that never checks which
        /// branch ran would be the same test.</para>
        /// </summary>
        private static SymbolBox StageGlobeProjectedBox(double t, int featureIndex, string toothId)
        {
            var projectedPool = Pools.New();
            int projectedStaged = StageGlobeGlyph(ref projectedPool, OriginView(), t, featureIndex);
            var screenPool = Pools.New();
            int screenStaged = StageGlobeGlyph(ref screenPool, default, t, featureIndex);

            Assert.That(projectedStaged == 1 && screenStaged == 1, Is.True,
                $"{toothId} precondition: both arms must stage exactly one label — staged " +
                $"{projectedStaged} (projected) and {screenStaged} (screen).");

            SymbolBox projected = projectedPool.Boxes[0];
            SymbolBox screen = screenPool.Boxes[0];
            double separation = math.abs(HalfHeightPx(projected) - HalfHeightPx(screen));

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}  control arm: projected half-height={1:F4} px  screen(pre-W3) half-height={2:F4} px  " +
                "separation={3:F4} px", toothId, HalfHeightPx(projected), HalfHeightPx(screen), separation));

            Assert.That(separation, Is.GreaterThan(5.0),
                $"{toothId} precondition (control arm): the projected box must differ from the pre-W3 screen " +
                $"box by well over a pixel, measured {separation:F4} px. If they agree, one of " +
                "TryBuildProjectedWorldGlyph's five gates rejected this fixture and the box below is the " +
                "SCREEN box — the assertions would then be testing BuildRotatedGlyph, not the ground frame.");
            return projected;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GA-T1 — the fixture IS spherical, and its axial term IS non-zero. R1's precondition.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>GA-T1 — the precondition that stops GA-T2/GA-T3 from passing vacuously.</b> Asserts, with every
        /// value printed:
        /// <list type="number">
        /// <item>the path is genuinely ON a sphere of production's radius, and the supplied ups are its exact
        /// radial normals — the entire content of the claim "this is a spherical fixture";</item>
        /// <item>the surface normal and tangent PRODUCTION resolved (read back off the staged
        /// <see cref="PlacedQuad"/>, not recomputed) are the ones this fixture's geometry states;</item>
        /// <item><b>the achieved <c>|axial| = |dot(tangent, up)|</c> clears an ABSOLUTE floor of 0.010</b>,
        /// and separately matches the closed form <c>sin(α(t) − θ/2)</c>.</item>
        /// </list>
        ///
        /// <para><b>Why the floor is a typed literal and not "agrees with the closed form".</b> Agreement is
        /// satisfied with both sides at ZERO — which is precisely what happens if the anchor drifts back
        /// toward the chord midpoint, and it is the failure this whole section exists to prevent. The two
        /// assertions answer different questions: the floor says the fixture still has signal, the closed
        /// form says the signal is the one we think it is.</para>
        ///
        /// <para>The final arm stages the SAME fixture at <c>t = 0.5</c> and measures ~0, which is the
        /// midpoint trap demonstrated in-suite rather than asserted in prose: at the chord midpoint
        /// <c>α = θ/2</c> exactly (half-angle identity), so a spherical fixture anchored there is exactly as
        /// blind as Mercator.</para>
        /// </summary>
        [Test]
        public void MapPitched_SphericalArcOffTheChordMidpoint_HasANonZeroGroundFrameAxialTerm()
        {
            BuildGlobeArc(GlobeAnchorT, out double3[] worldPath, out float3[] worldUps,
                out double3 sphereCentreRender, out _);

            // (1) on-sphere invariants — stated, not assumed.
            for (int v = 0; v < worldPath.Length; v++)
            {
                double radius = math.length(worldPath[v] - sphereCentreRender);
                double radialAgreement = math.dot(
                    new double3(worldUps[v].x, worldUps[v].y, worldUps[v].z),
                    (worldPath[v] - sphereCentreRender) / GlobeRadiusM);
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "GA-T1  vertex {0}: |P − C| = {1:F6} m (R = {2:F6})  up·radial = {3:F9}",
                    v, radius, GlobeRadiusM, radialAgreement));
                Assert.That(radius, Is.EqualTo(GlobeRadiusM).Within(1e-3),
                    $"GA-T1: path vertex {v} must lie ON the sphere of production's own radius.");
                Assert.That(radialAgreement, Is.EqualTo(1.0).Within(1e-6),
                    $"GA-T1: the up supplied for vertex {v} must be its exact radial normal — a non-radial " +
                    "up would make this a curved-path fixture, not a spherical one.");
            }

            // (2) what production actually resolved, read back off the staged quad.
            var p = Pools.New();
            int staged = StageGlobeGlyph(ref p, OriginView(), GlobeAnchorT, featureIndex: 60);
            Assert.That(staged, Is.EqualTo(1), "GA-T1 precondition: the globe label must stage.");

            float3 sampledUp = p.Quads[0].SurfaceUp;
            float3 sampledTangent = p.Quads[0].Tangent;
            double3 expectedUp = GlobeSurfaceUp(GlobeSampledUpAngle(GlobeAnchorT));
            double3 expectedTangent = GlobeChordDirection();

            double upError = math.length(new double3(sampledUp.x, sampledUp.y, sampledUp.z) - expectedUp);
            double tangentError =
                math.length(new double3(sampledTangent.x, sampledTangent.y, sampledTangent.z) - expectedTangent);

            // (3) the achieved axial term — measured off production's own two vectors.
            double measuredAxial = math.dot(sampledTangent, sampledUp);
            double predictedAxial = math.sin(GlobeSampledUpAngle(GlobeAnchorT) - GlobeSegmentRad * 0.5);

            var midpointPool = Pools.New();
            int midpointStaged = StageGlobeGlyph(ref midpointPool, OriginView(), 0.5, featureIndex: 61);
            Assert.That(midpointStaged, Is.EqualTo(1), "GA-T1 precondition: the midpoint arm must stage.");
            double midpointAxial =
                math.dot(midpointPool.Quads[0].Tangent, midpointPool.Quads[0].SurfaceUp);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "GA-T1  segment arc θ={0:F6} rad ({1:F4}°)  anchor t={2}\n" +
                "GA-T1  sampled up=({3:F9}, {4:F9}, {5:F9})  |error|={6:E3}\n" +
                "GA-T1  sampled tangent=({7:F9}, {8:F9}, {9:F9})  |error|={10:E3}\n" +
                "GA-T1  MEASURED |axial| = {11:F9}   (closed form {12:F9}, floor {13:F3})\n" +
                "GA-T1  same fixture at the chord midpoint t=0.5: |axial| = {14:E3}  ← the inert case",
                GlobeSegmentRad, math.degrees(GlobeSegmentRad), GlobeAnchorT,
                sampledUp.x, sampledUp.y, sampledUp.z, upError,
                sampledTangent.x, sampledTangent.y, sampledTangent.z, tangentError,
                math.abs(measuredAxial), math.abs(predictedAxial), GlobeMinimumAxial,
                math.abs(midpointAxial)));

            Assert.That(upError, Is.LessThan(1e-6),
                $"GA-T1: the surface normal production sampled must be the fixture's own radial normal at " +
                $"t = {GlobeAnchorT} — measured error {upError:E3}.");
            Assert.That(tangentError, Is.LessThan(1e-6),
                $"GA-T1: the tangent production resolved must be the segment's chord direction — measured " +
                $"error {tangentError:E3}. A different value means the chord probe left this segment, which " +
                "would change what the axial term below even means.");

            Assert.That(math.abs(measuredAxial), Is.GreaterThan(GlobeMinimumAxial),
                $"GA-T1: the achieved |axial| is {math.abs(measuredAxial):F9}, at or below the {GlobeMinimumAxial:F3} " +
                "floor. The ground frame's Gram-Schmidt is then INERT and GA-T2 proves nothing — this is the " +
                "chord-midpoint trap. Move the anchor away from t = 0.5, or widen the segment arc.");
            Assert.That(math.abs(measuredAxial), Is.EqualTo(math.abs(predictedAxial)).Within(1e-5),
                $"GA-T1: |axial| must be the closed form sin(α(t) − θ/2) = {math.abs(predictedAxial):F9}, " +
                $"measured {math.abs(measuredAxial):F9}.");
            Assert.That(math.abs(midpointAxial), Is.LessThan(1e-5),
                $"GA-T1: at the CHORD MIDPOINT the axial term must vanish ({math.abs(midpointAxial):E3} " +
                "measured). If it does not, the closed form above is wrong and GA-T3's whole premise — that " +
                "the midpoint is the inert case — goes with it.");
        }

        /// <summary>The absolute floor GA-T1 holds the achieved <c>|axial|</c> to. Chosen just under the
        /// 0.013963 this fixture achieves at the production 2° cap, so ordinary float noise cannot trip it
        /// while a drift back toward the midpoint (or a halved segment arc) will.</summary>
        private const double GlobeMinimumAxial = 0.010;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GA-T2 — the headline: the projected box uses the IN-SURFACE frame, not the raw tangent
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>GA-T2 — the first tooth anywhere in this repo that observes
        /// <c>x̂ = normalize(tangent − up·axial)</c> doing any work.</b> Proves: with the symbol anchored OFF
        /// the chord midpoint of an on-sphere segment, the four edges of the projected collision AABB are
        /// where a frame whose x̂ lies IN the surface puts them — and measurably not where the raw chord
        /// tangent would.
        ///
        /// <para><b>What this tooth discriminates, stated honestly.</b> At the production subdivision cap
        /// (2°) the achieved <c>|axial|</c> is 0.0140, so <b>omitting</b> the orthogonalisation tilts x̂ out
        /// of the surface by 0.0140 rad — which at an ordinary 16 px text size moves a corner by ≈0.19 px,
        /// i.e. is structurally invisible. <b>This tooth is therefore NOT guarding a visible production
        /// defect at ordinary text sizes.</b> It guards two things. First, and mainly, a wrong OPERAND ORDER
        /// (<c>normalize(up − tangent·axial)</c> and friends), which does not perturb x̂ — it replaces it
        /// with a different vector entirely, ≈120 px away here. Second, the presence of the subtraction at
        /// all, which is observable only because this fixture deliberately amplifies <c>text-size</c> to
        /// 7 em; the separation at that size is ≈1.6 px per y edge, asserted below as a non-vacuity floor
        /// rather than assumed.</para>
        ///
        /// <para><b>Why the oracle cannot share production's error.</b> Every expected pixel comes from the
        /// fixture's stated geometry — an explicit on-sphere arc, its explicit radial normals, x̂ as "the
        /// sampled normal rotated 90° in the arc's plane", ŷ as the arc plane's normal, and
        /// <see cref="ProjectPx"/>'s two-constant projection. Nothing here evaluates
        /// <c>normalize(tangent − up·axial)</c> or <c>cross(x̂, up)</c>. (W3-T1's ŷ leg and F-W3-6 are what
        /// this rule was learned from.)</para>
        ///
        /// <para><b>Scope.</b> The CPU copy only. The shader's identical expression
        /// (<c>SymbolWorldPitchAlign.hlsl</c>) is NOT observed here and is not claimed to be: no vertex stage
        /// runs in a staging test, and the per-vertex <c>Up</c> is carried unconsumed on readback.</para>
        /// </summary>
        [Test]
        public void MapPitched_SphericalArcOffTheChordMidpoint_ProjectedBoxUsesTheInSurfaceGroundFrame()
        {
            SymbolBox box = StageGlobeProjectedBox(GlobeAnchorT, featureIndex: 62, toothId: "GA-T2");

            GlobeExpectedBox(GlobeSampledUpAngle(GlobeAnchorT), out double2 expectedMin, out double2 expectedMax);
            GlobeExpectedBox(GlobeSegmentRad * 0.5, out double2 rawTangentMin, out double2 rawTangentMax);

            double lowerEdgeSeparation = math.abs(expectedMin.y - rawTangentMin.y);
            double upperEdgeSeparation = math.abs(expectedMax.y - rawTangentMax.y);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "GA-T2  box      x=[{0:F4}, {1:F4}]  y=[{2:F4}, {3:F4}]\n" +
                "GA-T2  expected x=[{4:F4}, {5:F4}]  y=[{6:F4}, {7:F4}]   (in-surface x̂)\n" +
                "GA-T2  dropping the orthogonalisation would give y=[{8:F4}, {9:F4}] — a separation of " +
                "{10:F4} px (lower edge) and {11:F4} px (upper edge)",
                box.Min.x, box.Max.x, box.Min.y, box.Max.y,
                expectedMin.x, expectedMax.x, expectedMin.y, expectedMax.y,
                rawTangentMin.y, rawTangentMax.y, lowerEdgeSeparation, upperEdgeSeparation));

            Assert.That(math.min(lowerEdgeSeparation, upperEdgeSeparation), Is.GreaterThan(1.0),
                $"GA-T2 precondition (non-vacuity): dropping the orthogonalisation must move BOTH y edges by " +
                $"well over the 0.05 px tolerance below — measured {lowerEdgeSeparation:F4} px and " +
                $"{upperEdgeSeparation:F4} px. Below ~1 px this tooth stops discriminating the expression it " +
                "exists to read, however non-zero the axial term is.");

            Assert.That(box.Min.y, Is.EqualTo(expectedMin.y).Within(0.05),
                $"GA-T2: the box's LOWER edge must be where an IN-SURFACE x̂ puts the cell's bottom corners " +
                $"({expectedMin.y:F4}), read {box.Min.y:F4}. {rawTangentMin.y:F4} would mean x̂ is the raw " +
                "chord tangent — the Gram-Schmidt subtraction is gone.");
            Assert.That(box.Max.y, Is.EqualTo(expectedMax.y).Within(0.05),
                $"GA-T2: the box's UPPER edge must be {expectedMax.y:F4}, read {box.Max.y:F4} " +
                $"(raw-tangent frame would give {rawTangentMax.y:F4}).");
            Assert.That(box.Min.x, Is.EqualTo(expectedMin.x).Within(0.05),
                $"GA-T2: the box's LEFT edge must be {expectedMin.x:F4}, read {box.Min.x:F4}.");
            Assert.That(box.Max.x, Is.EqualTo(expectedMax.x).Within(0.05),
                $"GA-T2: the box's RIGHT edge must be {expectedMax.x:F4}, read {box.Max.x:F4}.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GA-T3 — the contrast arm: the SAME fixture at the chord midpoint is inert
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>GA-T3 — the midpoint arm, and it is INERT ON PURPOSE.</b> Stages the identical globe fixture at
        /// <c>t = 0.5</c>, where <c>α = θ/2</c> exactly and the axial term vanishes, and asserts the box
        /// against the same oracle.
        ///
        /// <para><b>Its job is not coverage — it is the RED asymmetry.</b> Read on its own this tooth adds
        /// nothing GA-T2 does not already say. Read as a PAIR with GA-T2 it is the in-suite proof that
        /// GA-T2's off-midpoint anchoring is load-bearing rather than decorative:</para>
        /// <list type="bullet">
        /// <item>DROP the subtraction (<c>x̂ = normalize(tangent)</c>) ⇒ GA-T2 reds, <b>GA-T3 stays
        /// GREEN</b> — at the midpoint the in-surface x̂ and the raw tangent are the same vector.</item>
        /// <item>SWAP the operands (<c>normalize(up − tangent·axial)</c>) ⇒ <b>both</b> red, because at
        /// <c>axial = 0</c> that expression collapses to <c>up</c>, which is not the tangent.</item>
        /// </list>
        /// <para>That asymmetry is a property a reviewer can re-measure, and it is the reason a globe fixture
        /// built the obvious way — at the arc midpoint, as every other curved fixture in this repo is — would
        /// have looked exactly like coverage while observing nothing (F-W3-3, still open for the shader).</para>
        /// </summary>
        [Test]
        public void MapPitched_SphericalArcAtTheChordMidpoint_IsBlindToTheOrthogonalisation()
        {
            const double midpointT = 0.5;
            SymbolBox box = StageGlobeProjectedBox(midpointT, featureIndex: 63, toothId: "GA-T3");

            GlobeExpectedBox(GlobeSampledUpAngle(midpointT), out double2 expectedMin, out double2 expectedMax);
            GlobeExpectedBox(GlobeSegmentRad * 0.5, out double2 rawTangentMin, out double2 rawTangentMax);

            double inertness = math.max(
                math.abs(expectedMin.y - rawTangentMin.y), math.abs(expectedMax.y - rawTangentMax.y));

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "GA-T3  box      x=[{0:F4}, {1:F4}]  y=[{2:F4}, {3:F4}]\n" +
                "GA-T3  expected x=[{4:F4}, {5:F4}]  y=[{6:F4}, {7:F4}]\n" +
                "GA-T3  in-surface x̂ vs raw chord tangent differ by {8:E3} px here — the midpoint is inert",
                box.Min.x, box.Max.x, box.Min.y, box.Max.y,
                expectedMin.x, expectedMax.x, expectedMin.y, expectedMax.y, inertness));

            Assert.That(inertness, Is.LessThan(1e-6),
                $"GA-T3 premise: at the chord midpoint the in-surface frame and the raw chord tangent must be " +
                $"indistinguishable ({inertness:E3} px apart). If they are not, this arm is no longer the " +
                "control GA-T2's doc claims it is and the stated RED asymmetry does not hold.");

            Assert.That(box.Min.y, Is.EqualTo(expectedMin.y).Within(0.05),
                $"GA-T3: LOWER edge expected {expectedMin.y:F4}, read {box.Min.y:F4}.");
            Assert.That(box.Max.y, Is.EqualTo(expectedMax.y).Within(0.05),
                $"GA-T3: UPPER edge expected {expectedMax.y:F4}, read {box.Max.y:F4}.");
            Assert.That(box.Min.x, Is.EqualTo(expectedMin.x).Within(0.05),
                $"GA-T3: LEFT edge expected {expectedMin.x:F4}, read {box.Min.x:F4}.");
            Assert.That(box.Max.x, Is.EqualTo(expectedMax.x).Within(0.05),
                $"GA-T3: RIGHT edge expected {expectedMax.x:F4}, read {box.Max.x:F4}.");
        }
    }
}
#endif // UNITY_EDITOR
