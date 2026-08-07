// Unity EditMode only — real OffLookAtLabelScene (MapCamera + Camera/RenderTexture + a real
// LabelPlacementSystem.Tick), mesh readback through the live camera.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// Stage W1 — the FIXTURE arm (W1-T1…T5): the assertions Stage P-M built the apparatus for and deliberately
// deferred. Read `OffLookAtLabelScene`'s header first; `OffLookAtLabelFixtureTests` (M1–M13) is the
// apparatus' own acceptance suite and every tooth there still passes unchanged.
//
// THE MODEL, SETTLED, NOT RE-DERIVED HERE: `text-size` under `*-pitch-alignment: map` means X px TOP-DOWN.
// A glyph advance is fixed ONCE as a world length and the perspective divide does the rest, so letters AND
// letter spacing foreshorten together — the same principle as `line-width`.
//
// THE TRAP THESE TEETH EXIST TO AVOID. `CrossNear`/`CrossFar` are ISO-DEPTH by construction, and for an
// iso-depth label a true per-glyph WORLD walk and a screen walk scaled by ONE per-label constant produce
// IDENTICAL output — and that second thing is a model this epic already built and reverted. A stage can be
// green on all 14 P-M teeth while re-implementing the bug. W1-T2/T3/T4 live on the RECEDING
// (depth-spanning) arm and are the falsifiability of this stage; T1 is the inherited regression tooth and T5
// is the DPR tooth.
//
// ORACLE HYGIENE. `AdvanceWorldMetres` is `AdvanceBakedPx` and `TextSizePx` (fixture constants), `OneEm` (a
// unit definition), and `MapCamera.MetresPerDevicePixel × Config.DevicePixelRatio` (the frame ruler, whose
// DPR factor the fixture applies from its OWN constant — never read back out of production, or the two sides
// would drop it together and T5 would be vacuous). Nothing measured feeds it. Where a tooth projects through
// the live camera (T3) the only measured input is a POSITION; the LENGTH projected is always the constant.

#if UNITY_EDITOR
using System.Text;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Tests.Text.Placement;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class MapPitchedWorldArcLayoutTests
    {
        private static readonly OffLookAtLabelId[] CurvedIds =
        {
            OffLookAtLabelId.CrossNear, OffLookAtLabelId.CrossFar,
            OffLookAtLabelId.RecedingNear, OffLookAtLabelId.RecedingFar,
        };

        private static readonly OffLookAtLabelId[] RecedingIds =
        {
            OffLookAtLabelId.RecedingNear, OffLookAtLabelId.RecedingFar,
        };

        private static OffLookAtLabelScene CreateFixture(double devicePixelRatio = 1.0)
            => OffLookAtLabelScene.Create(new OffLookAtLabelSceneConfig
            {
                DevicePixelRatio = devicePixelRatio,
            });

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T1 — the inherited regression tooth.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T1 — THE REGRESSION TOOTH the whole epic was chasing.</b> Proves: the cross-azimuth pair's
        /// mean screen spacing halves when the view depth doubles — far/near reads 0.500, where the pre-W1
        /// screen-constant layout read 1.0000.
        ///
        /// <para>It is a RATIO of two readings from ONE frame and ONE label pair, so any uniform scale error
        /// (OneEm, TextSizePx, mpp, DPR, atlas scale, a wrong P11) multiplies both and CANCELS. Only the depth
        /// dependence survives, which is exactly the question.</para>
        ///
        /// <para><b>Does NOT prove that spacing foreshortens per-GLYPH rather than per-LABEL.</b> Both labels
        /// are iso-depth, so a screen walk scaled by one per-label constant passes this tooth. That is what
        /// W1-T2 is for, and why T1 alone would not be an acceptable stage.</para>
        ///
        /// <para>RED-verify: injection I1 (force <c>worldArc = false</c>) — reads 1.0000.</para>
        /// </summary>
        [Test]
        public void CrossAzimuthPair_ScreenSpacing_HalvesWithDepth()
        {
            using var f = CreateFixture();
            double nearMean = OffLookAtLabelScene.Mean(f.Measure(OffLookAtLabelId.CrossNear).ScreenSpacingPx);
            double farMean  = OffLookAtLabelScene.Mean(f.Measure(OffLookAtLabelId.CrossFar).ScreenSpacingPx);
            double ratio = farMean / nearMean;
            // Printed as well as asserted: the depth-derived expectation is what the 0.500 constant stands
            // for, and seeing both makes a pose change legible instead of mysterious.
            double depthDerived = f.NearAnchorViewDepthMetres / f.FarAnchorViewDepthMetres;

            Assert.That(ratio, Is.EqualTo(0.500).Within(3).Percent,
                $"W1-T1: a map-pitched glyph advance is a WORLD length, so at twice the view depth it must " +
                $"project to half the screen spacing — far/near reads {ratio:F4} (near {nearMean:F3} px, far " +
                $"{farMean:F3} px; the pose's own w_near/w_far is {depthDerived:F4}). A reading near 1.0000 " +
                "is the pre-W1 screen-constant layout: the advance stayed a screen length and did not " +
                "foreshorten at all.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T2 — THE HEADLINE. Per gap, all four curved labels, both directions, both depths.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T2 — THE HEADLINE TOOTH.</b> Proves: EVERY gap of ALL FOUR curved labels — 16 gaps across two
        /// directions and two depths — measures <c>AdvanceWorldMetres</c> in the world, within 1 %. That is
        /// the model stated directly: one glyph advance is one fixed world length, everywhere in the frame.
        ///
        /// <para><b>Why it is not self-referential:</b> the comparand is two fixture constants, a unit
        /// definition and the frame ruler. Nothing measured. The MEASURAND is the staged world anchors read
        /// back off the built meshes.</para>
        ///
        /// <para><b>Why a per-label constant cannot pass it — the iso-depth trap closed.</b> On the RECEDING
        /// arms a screen-uniform walk (or a world walk scaled by one per-label constant, which is the same
        /// thing) produces world gaps that GROW along the label as depth increases, reading well over the
        /// oracle at the far end. Against a 1 % bound that is enormous. The cross arms cannot see this; the
        /// receding arms are where the tooth has teeth.</para>
        ///
        /// <para>Both roads are straight two-vertex segments, so chord distance IS arc distance and the
        /// expectation is exact to float — the 1 % is headroom for the RTC bake's float narrowing, not for
        /// model slop.</para>
        ///
        /// <para>RED-verify: injection I1 (force <c>worldArc = false</c>).</para>
        /// </summary>
        [Test]
        public void EveryCurvedGap_MeasuresOneWorldAdvance_AtBothDepths_BothDirections()
        {
            using var f = CreateFixture();
            AssertEveryGapMatchesTheWorldAdvance(f, "W1-T2", boundPercent: 1.0);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T3 — the same claim, through the LIVE projection, on the depth-spanning arm.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T3 — the world claim carried into SCREEN space through the live camera.</b> For each gap of
        /// each receding label, the expectation is
        /// <c>|ProjectPx(A_i + d̂·AdvanceWorldMetres) − ProjectPx(A_i)|</c>, where <c>A_i</c> is glyph
        /// <c>i</c>'s MEASURED world position and <c>d̂</c> is the fixture's own road direction. The measured
        /// screen gap must match it within 3 %.
        ///
        /// <para><b>Discipline:</b> the only measured input is a POSITION. The LENGTH projected is the fixture
        /// constant — the exact analogue of <c>OracleAtDepth</c> taking only <c>w</c>. A spacing is never fed
        /// back in.</para>
        ///
        /// <para><b>Why not a closed form.</b> <c>spacingWorld·|P11|·H/(2w)</c> is the PERPENDICULAR span; a
        /// receding displacement's perpendicular component is <c>L·cos θ</c>, so on this arm the closed form
        /// reads far low by construction. Projecting the two real endpoints through the live camera is exact
        /// at any depth AND any direction, needs no <c>cos θ</c>, and imports no second-order correction. The
        /// closed-form number is REPORTED alongside so the substitution is auditable.</para>
        ///
        /// <para>Proves: the screen reading a later stage (the render arm) will consume is the projection of a
        /// world-welded advance. RED-verify: injection I1.</para>
        /// </summary>
        [Test]
        public void RecedingGaps_ProjectAsAWorldWeldedAdvance_ThroughTheLiveCamera()
        {
            using var f = CreateFixture();
            double worstErrorPercent = 0.0;
            var table = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;
            table.AppendLine();
            table.AppendLine("   label          gap  measuredPx   projectedPx   err%    closedFormPx (reported)");

            foreach (OffLookAtLabelId id in RecedingIds)
            {
                LabelMeasurement m = f.Measure(id);
                for (int g = 0; g + 1 < m.Glyphs.Length; g++)
                {
                    double3 a = m.Glyphs[g].WorldUnity;
                    // A DIRECTION taken from the measurement, never a length: the road runs along ±ĝ and
                    // which sign is a fact about how the label was laid out, not about how far apart the
                    // glyphs are.
                    double sign = math.sign(math.dot(m.Glyphs[g + 1].WorldUnity - a, f.RecedingDir));
                    double3 b = a + f.RecedingDir * (sign * f.AdvanceWorldMetres);
                    double projectedPx = math.length(
                        GroundRuler.ProjectPx(f.UnityCamera, b) - GroundRuler.ProjectPx(f.UnityCamera, a));
                    double measuredPx = m.ScreenSpacingPx[g];
                    double errorPercent = 100.0 * math.abs(measuredPx / projectedPx - 1.0);
                    worstErrorPercent = math.max(worstErrorPercent, errorPercent);

                    double gapDepth = 0.5 * (m.Glyphs[g].ViewDepthMetres + m.Glyphs[g + 1].ViewDepthMetres);
                    table.AppendLine(string.Format(c, "   {0,-13} {1,3} {2,11:F3} {3,13:F3} {4,7:F3} {5,15:F3}",
                        id, g, measuredPx, projectedPx, errorPercent, f.OracleAtDepth(gapDepth)));
                }
            }

            Assert.That(worstErrorPercent, Is.LessThan(3.0),
                $"W1-T3: each receding gap's screen size must be the live projection of ONE world advance " +
                $"({f.AdvanceWorldMetres:F1} m) laid along the road from that glyph's own measured position " +
                $"— worst error {worstErrorPercent:F3} % (bound 3 %).{table}" +
                "   (the closedFormPx column is the PERPENDICULAR closed form, reported only: it reads low " +
                "on a receding arm by construction — see this tooth's doc.)");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T4 — monotone decrease, the cheap model-discriminating tooth.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T4 — the cheapest discriminating reading in the stage.</b> Proves: along each receding
        /// label, screen gaps decrease STRICTLY with view depth, and on <c>RecedingNear</c> the nearest gap is
        /// more than 1.10× the farthest.
        ///
        /// <para>Model-discriminating on its own and with no oracle at all: a screen-constant walk gives
        /// UNIFORM gaps (ratio 1.000, no monotonicity), so this cannot pass on the pre-W1 layout. It is the
        /// tooth that survives even if every closed form and every ruler in the fixture were wrong.</para>
        ///
        /// <para>Ordered by DEPTH rather than by glyph index: which end of the road glyph 0 sits at is a
        /// layout detail (the keep-upright walk direction), and a tooth that assumed one would be pinning the
        /// wrong thing.</para>
        ///
        /// <para>RED-verify: injection I1 — gaps go uniform and both clauses fail.</para>
        /// </summary>
        [Test]
        public void RecedingGaps_ShrinkStrictlyWithDepth()
        {
            using var f = CreateFixture();
            foreach (OffLookAtLabelId id in RecedingIds)
            {
                LabelMeasurement m = f.Measure(id);
                int gaps = m.ScreenSpacingPx.Length;
                Assert.That(gaps, Is.GreaterThan(1),
                    $"W1-T4 precondition ({id}): need at least two gaps to speak of monotonicity, got {gaps}.");

                // Gap g's own depth, so "along the receding direction" is read off the geometry rather than
                // assumed from the index order.
                var gapDepth = new double[gaps];
                for (int g = 0; g < gaps; g++)
                    gapDepth[g] = 0.5 * (m.Glyphs[g].ViewDepthMetres + m.Glyphs[g + 1].ViewDepthMetres);
                bool depthRisesWithIndex = gapDepth[gaps - 1] > gapDepth[0];

                double worstStep = double.MaxValue;
                var report = new StringBuilder();
                for (int g = 0; g + 1 < gaps; g++)
                {
                    // (shallower gap) − (deeper gap): must be strictly positive at every step.
                    double step = depthRisesWithIndex
                        ? m.ScreenSpacingPx[g] - m.ScreenSpacingPx[g + 1]
                        : m.ScreenSpacingPx[g + 1] - m.ScreenSpacingPx[g];
                    worstStep = math.min(worstStep, step);
                }
                for (int g = 0; g < gaps; g++)
                    report.Append(string.Format(CultureInfo.InvariantCulture,
                        " [{0}] {1:F3} px @ {2:F0} m;", g, m.ScreenSpacingPx[g], gapDepth[g]));

                double nearest = depthRisesWithIndex ? m.ScreenSpacingPx[0] : m.ScreenSpacingPx[gaps - 1];
                double farthest = depthRisesWithIndex ? m.ScreenSpacingPx[gaps - 1] : m.ScreenSpacingPx[0];

                Assert.That(worstStep, Is.GreaterThan(0.0),
                    $"W1-T4 ({id}): screen gaps must shrink STRICTLY as view depth grows — smallest step " +
                    $"{worstStep:F6} px. Gaps:{report} A flat sequence is the pre-W1 screen-constant walk.");

                if (id == OffLookAtLabelId.RecedingNear)
                    Assert.That(nearest / farthest, Is.GreaterThan(1.10),
                        $"W1-T4 ({id}): the nearest gap must exceed the farthest by more than 10 % — reads " +
                        $"{nearest / farthest:F4} ({nearest:F3} px vs {farthest:F3} px). RecedingFar is " +
                        "deliberately excluded from this clause: at ~2× the depth the same world span is a " +
                        "smaller relative spread, and a bound with no margin is not a tooth.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T5 — R2, the device-pixel ratio.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T5 — the DPR tooth (R2).</b> W1-T2 re-run on a fixture built at
        /// <c>DevicePixelRatio = 2</c>: every gap of every curved label must still measure
        /// <c>AdvanceWorldMetres</c>, the SAME number of metres as at DPR 1.
        ///
        /// <para><b>The expectation is deliberately NOT "twice the DPR-1 value".</b> The altitude framing uses
        /// <c>ViewportLogicalPx</c>, so at DPR 2 the orbit radius halves and <c>MetresPerDevicePixel</c>
        /// halves with it — leaving <c>metresPerLogicalPixel</c>, and therefore the advance in metres,
        /// INVARIANT. What does change is that a fixed world length projects to twice as many device px.
        /// Writing "2×" here would encode the wrong law.</para>
        ///
        /// <para><b>What it catches.</b> <c>TextSizePx</c> is LOGICAL px while
        /// <c>MapCamera.MetresPerDevicePixel</c> is per DEVICE px by its own doc. A production ruler that
        /// dropped the <c>× DevicePixelRatio</c> would be half the correct value at DPR 2 — so the measured
        /// world gap reads 0.5× here and exactly 1.0× at DPR 1, where the omission is invisible and no
        /// self-referential oracle could see it either.</para>
        ///
        /// <para>RED-verify: injection I2 (drop <c>* _camera.DevicePixelRatio</c> in
        /// <c>LabelPlacementSystem</c>) — DPR 1 stays green, this reads 0.5×.</para>
        /// </summary>
        [Test]
        public void EveryCurvedGap_MeasuresTheSameWorldAdvance_AtDevicePixelRatioTwo()
        {
            using var f = CreateFixture(devicePixelRatio: 2.0);
            Assert.That(f.Config.DevicePixelRatio, Is.EqualTo(2.0),
                "W1-T5 precondition: this tooth means nothing unless the scene really was built at DPR 2.");
            AssertEveryGapMatchesTheWorldAdvance(f, "W1-T5 (DPR 2)", boundPercent: 1.0);
        }

        // ── shared ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Computes the world residual for every gap of all four curved labels, asserts the WORST,
        /// and puts the full per-label/per-gap table in the failure message (NUnit throws on the first
        /// failure, so asserting per gap would let the first one shadow the rest).</summary>
        private static void AssertEveryGapMatchesTheWorldAdvance(
            OffLookAtLabelScene f, string what, double boundPercent)
        {
            double expectedM = f.AdvanceWorldMetres;
            Assert.That(expectedM, Is.GreaterThan(0.0),
                $"{what} precondition: AdvanceWorldMetres reads {expectedM} — the expectation is degenerate " +
                "and every residual below would be meaningless.");

            double worstErrorPercent = 0.0;
            int gapsChecked = 0;
            var table = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;
            table.AppendLine();
            table.AppendLine(string.Format(c,
                "   expected advance = {0:F2} m  (AdvanceBakedPx {1:F1} / OneEm × TextSizePx {2:F1} × " +
                "metresPerLogicalPixel {3:F4}, DPR {4:F2})",
                expectedM, f.Config.AdvanceBakedPx, f.Config.TextSizePx, f.MetresPerLogicalPixel,
                f.Config.DevicePixelRatio));
            table.AppendLine("   label          gap      worldM        err%     viewDepthM");

            foreach (OffLookAtLabelId id in CurvedIds)
            {
                LabelMeasurement m = f.Measure(id);
                Assert.That(m.WorldSpacingM.Length, Is.EqualTo(f.Config.GlyphCount - 1),
                    $"{what} precondition ({id}): expected {f.Config.GlyphCount - 1} gaps, got " +
                    $"{m.WorldSpacingM.Length} — the label did not stage every glyph.");
                for (int g = 0; g < m.WorldSpacingM.Length; g++)
                {
                    double errorPercent = 100.0 * math.abs(m.WorldSpacingM[g] / expectedM - 1.0);
                    worstErrorPercent = math.max(worstErrorPercent, errorPercent);
                    gapsChecked++;
                    table.AppendLine(string.Format(c, "   {0,-13} {1,3} {2,11:F1} {3,11:F4} {4,14:F1}",
                        id, g, m.WorldSpacingM[g], errorPercent,
                        0.5 * (m.Glyphs[g].ViewDepthMetres + m.Glyphs[g + 1].ViewDepthMetres)));
                }
            }

            Assert.That(gapsChecked, Is.EqualTo(CurvedIds.Length * (f.Config.GlyphCount - 1)),
                $"{what} precondition: {gapsChecked} gaps were checked, not " +
                $"{CurvedIds.Length * (f.Config.GlyphCount - 1)} — a label is missing and the worst-case " +
                "assertion below would be taken over the wrong set.");
            Assert.That(worstErrorPercent, Is.LessThan(boundPercent),
                $"{what}: every glyph advance must be ONE fixed world length, everywhere in the frame — " +
                $"worst residual {worstErrorPercent:F4} % over {gapsChecked} gaps (bound " +
                $"{boundPercent:F2} %).{table}" +
                "   A receding label reading well ABOVE the expected advance, growing with depth, is a " +
                "SCREEN-uniform walk (or a world walk scaled by one per-label constant, which is the same " +
                "thing) — the model this epic already reverted twice. A uniform 0.5× at DPR 2 with DPR 1 " +
                "green is the dropped DevicePixelRatio factor.");
        }
    }
}
#endif // UNITY_EDITOR
