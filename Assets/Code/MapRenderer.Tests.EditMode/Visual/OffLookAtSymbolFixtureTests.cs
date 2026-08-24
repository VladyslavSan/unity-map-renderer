// Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real
// SymbolPlacementSystem.Tick), off-screen GPU render + CPU readback.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// Stage P-M — the off-look-at fixture's OWN acceptance teeth (M1–M13). Read OffLookAtSymbolScene's header
// first: it states what the fixture is, why the CROSS-AZIMUTH arm is the headline one and the RECEDING arm
// is soundness-only, and why the headline ratio is immune to a uniform miscalibration.
//
// THIRTEEN INDEPENDENT [Test] METHODS, not one. NUnit's Assert.That throws on the FIRST failure, so a method
// running clauses in sequence lets an early clause's exception SHADOW every later clause — this epic has been
// bitten by that three times (TiltFixtureSelfTests' T1 header records the precedent). Where a tooth checks
// the same property at BOTH depths, it computes both readings first and asserts the WORSE one in a single
// clause whose message carries both numbers, rather than asserting twice.
//
// THE FIX LANDED IN STAGE W1 — this file's teeth did NOT move. M1–M13 all still pass, and that is a
// deliberate property of how they were written: every one of them is either about the frame geometry, the
// oracle, or a claim true under BOTH the pre-W1 screen walk and W1's world walk. They calibrate the
// instrument; they do not pin the defect. THE FAR ASSERTIONS P-M DEFERRED NOW LIVE IN
// `MapPitchedWorldArcLayoutTests` (W1-T1…T5) — that is where "far/near screen spacing == 0.500", the
// per-gap world-metre reading, and the DPR tooth are, and where their RED verification against the pre-fix
// production code is recorded. M13 still PRINTS the whole table; on the post-W1 tree its
// `far measured/oracle` row reads ≈ 1.00 where it read ≈ 2.00.
//
// ANTI-TEETH, deliberately absent (writing any of them would be a defect):
//   • any tooth asserting far/near spacing ≈ 1.0 — that PINS the old defect;
//   • any tooth whose expected value is derived from the near symbol's MEASURED spacing scaled by anything —
//     that is the P3a self-referential-oracle failure re-imported.
//   • (The third P-M anti-tooth, "any spacing tooth on the RECEDING symbol", is RETIRED: it existed because
//     the pre-W1 screen walk anchored a receding symbol at the screen arc midpoint, which is not the
//     projection of the world midpoint. The world walk anchors by world arc length, so the receding arm is
//     now the fixture's DEPTH-SPANNING measurement arm — see W1-T2/T3/T4. This file still asserts no
//     spacing there; W1's own tooth file does.)
//
// THE RED-verifications named on the teeth BELOW are injections into the FIXTURE or the ORACLE, because
// these teeth are about the apparatus. W1's teeth are RED-verified against PRODUCTION.

#if UNITY_EDITOR
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Tests.Text.Placement;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class OffLookAtSymbolFixtureTests
    {
        private static OffLookAtSymbolScene CreateFixture()
            => OffLookAtSymbolScene.Create(new OffLookAtSymbolSceneConfig());

        /// <summary>Ink is separable from the white background at this threshold; a band containing a whole
        /// five-glyph symbol carries thousands of ink pixels, so this floor only asks "did anything render
        /// here at all".</summary>
        private const int InkFloor = 200;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M1–M5 — the frame geometry and the ORACLE itself. No mesh, no symbol, no render is read here.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M1.</b> Proves: the fixture's two anchors really do sit at the designed far/near VIEW-depth
        /// ratio — the property that makes every later reading a two-depth reading rather than the
        /// single-depth reading every earlier fixture in this epic took. The far anchor is SOLVED (view depth
        /// is affine along ĝ, so two samples determine it exactly), then the achieved ratio is asserted.
        ///
        /// <para>Does NOT prove anything about symbols, staging, spacing or rendering.</para>
        ///
        /// <para>RED-verify: break the affine solve (drop the <c>/ depthSlope</c>, or scale <c>s_far</c>) ⇒ the
        /// achieved ratio no longer matches the configured one. NOT by setting
        /// <c>TargetDepthRatio = 1.0</c>: the second clause below compares the ACHIEVED ratio against the
        /// CONFIGURED one, so lowering the config moves expectation and measurement together and reads green —
        /// the plan's recipe for this tooth is vacuous, which is why the 1.8 floor clause exists.</para>
        /// </summary>
        [Test]
        public void TwoAnchors_SitAtTheDesignedViewDepthRatio()
        {
            using var f = CreateFixture();
            // The floor, not decoration: the whole stage exists to read the two competing rulers apart, and
            // below ~1.8 they are too close to. It is also what makes the clause below falsifiable by a
            // config change — stop rule S2 says to lower the ZOOM, never the ratio, so a config that shrank
            // the ratio to keep the far anchor on-screen must fail HERE.
            Assert.That(f.Config.TargetDepthRatio, Is.GreaterThanOrEqualTo(1.8),
                $"M1: the designed far/near view-depth ratio must be at least 1.8 — configured " +
                $"{f.Config.TargetDepthRatio:F3}. STOP RULE S2: if the far anchor will not fit on-screen, " +
                "lower the zoom and report; do not shrink this.");
            Assert.That(f.AchievedDepthRatio, Is.EqualTo(f.Config.TargetDepthRatio).Within(2).Percent,
                $"M1: the solved far anchor must sit at {f.Config.TargetDepthRatio:F3}× the near anchor's " +
                $"view depth — measured {f.AchievedDepthRatio:F4} (w_near={f.NearAnchorViewDepthMetres:F1} m, " +
                $"w_far={f.FarAnchorViewDepthMetres:F1} m).");
        }

        /// <summary>
        /// <b>M2.</b> Proves: BOTH anchors project in front of the camera and land inside the frame with a
        /// 32 px margin — i.e. the designed depth ratio is actually reachable on-screen at this pose, so the
        /// far arm measures something the renderer would really draw.
        ///
        /// <para>Does NOT prove that the far LABEL fits (that is M8's spill check), nor anything about depth.</para>
        ///
        /// <para><b>STOP RULE S2.</b> If this fails because the far anchor leaves the frame, LOWER THE ZOOM
        /// (more ground per pixel) and report both numbers — do NOT shrink the depth ratio below 1.8, which is
        /// the whole point of the stage.</para>
        ///
        /// <para>RED-verify: double the solved <c>s_far</c> ⇒ the far anchor leaves the margin.</para>
        /// </summary>
        [Test]
        public void TwoAnchors_ProjectOnScreen_WithMargin()
        {
            using var f = CreateFixture();
            const double marginPx = 32.0;
            double2 nearPx = f.Measure(OffLookAtSymbolId.CrossNear).AnchorScreenPx;
            double2 farPx  = f.Measure(OffLookAtSymbolId.CrossFar).AnchorScreenPx;
            double lo = marginPx, hi = f.Config.SizePx - marginPx;

            double worst = math.min(
                math.min(math.min(nearPx.x - lo, hi - nearPx.x), math.min(nearPx.y - lo, hi - nearPx.y)),
                math.min(math.min(farPx.x - lo, hi - farPx.x),   math.min(farPx.y - lo, hi - farPx.y)));
            Assert.That(worst, Is.GreaterThan(0.0),
                $"M2: both anchors must project inside [{lo:F0}, {hi:F0}]² device px — near=({nearPx.x:F1}, " +
                $"{nearPx.y:F1}), far=({farPx.x:F1}, {farPx.y:F1}); worst margin {worst:F1} px. STOP RULE S2: " +
                "if the FAR anchor is the offender, lower the zoom and report — do not shrink the depth ratio.");
        }

        /// <summary>
        /// <b>M3 — THE ORACLE'S OWN ACCEPTANCE TEST.</b> Proves: the depth-general closed form
        /// <see cref="GroundRuler.ClosedFormPerpendicularSpanPx"/> agrees with the LIVE camera's projection of
        /// the SAME world segment at BOTH depths, within 1 %. That is the property no fixture in this epic has
        /// ever had — a comparand that is known to be DEPTH-CORRECT, not merely correct at the look-at.
        ///
        /// <para>Does NOT prove that the closed form is the right MODEL for glyph spacing — only that it
        /// computes the projection of a given world length correctly wherever that length sits.</para>
        ///
        /// <para>RED-verify: evaluate the FAR probe with <c>w_near</c> ⇒ the far reading is ≈ 2× off.</para>
        /// </summary>
        [Test]
        public void ClosedFormSpan_AgreesWithTheLiveProjection_AtBothDepths()
        {
            using var f = CreateFixture();
            double probeM = 30.0 * f.MetresPerDevicePixel;
            var cXZ = new double2(f.CrossAzimuthDir.x, f.CrossAzimuthDir.z);

            double nearProjective = GroundRuler.GroundSegmentSpanPx(
                f.UnityCamera, f.NearAnchorWorldUnity, cXZ, probeM);
            double farProjective = GroundRuler.GroundSegmentSpanPx(
                f.UnityCamera, f.FarAnchorWorldUnity, cXZ, probeM);
            double nearClosed = GroundRuler.ClosedFormPerpendicularSpanPx(
                probeM, f.NearAnchorViewDepthMetres, f.AbsP11, f.ViewportHeightPx);
            double farClosed = GroundRuler.ClosedFormPerpendicularSpanPx(
                probeM, f.FarAnchorViewDepthMetres, f.AbsP11, f.ViewportHeightPx);

            double worstErrorPercent = 100.0 * math.max(
                math.abs(nearProjective / nearClosed - 1.0), math.abs(farProjective / farClosed - 1.0));
            Assert.That(worstErrorPercent, Is.LessThan(1.0),
                $"M3: the depth-general closed form must match the live projection at BOTH depths — " +
                $"near: projective {nearProjective:F4} px vs closed {nearClosed:F4} px; " +
                $"far: projective {farProjective:F4} px vs closed {farClosed:F4} px; " +
                $"worst error {worstErrorPercent:F4} %. A far-only failure means the closed form is carrying " +
                "a look-at-only ruler, which is exactly this epic's blind spot.");
        }

        /// <summary>
        /// <b>M4 — THE RULER IDENTITY (stop rule S1).</b> Proves:
        /// <see cref="MapRenderer.Unity.Rendering.Map.MapCamera.MetresPerDevicePixel"/> IS the lateral
        /// px-per-metre ruler at the look-at — a segment of <c>30·mpp</c> metres laid along ĉ at the look-at
        /// projects to 30 device px. The settled model's "X px TOP-DOWN" enters the oracle at exactly this one
        /// point; nothing else in the repo pins it.
        ///
        /// <para>Does NOT prove anything at any OTHER depth — the whole point of the epic is that the
        /// identity holds only here.</para>
        ///
        /// <para><b>STOP RULE S1.</b> If this reads outside 1 %, DO NOT WIDEN IT. Report both numbers and
        /// STOP: the fix stage's oracle would be calibrated against the wrong ruler, which is this epic's own
        /// failure mode repeating.</para>
        ///
        /// <para>RED-verify: probe with <c>60·mpp</c> ⇒ reads 60 px against a 30 px expectation.</para>
        /// </summary>
        [Test]
        public void MetresPerDevicePixel_IsTheLateralRulerAtTheLookAt()
        {
            using var f = CreateFixture();
            const double probeMultiplier = 30.0;
            double probeM = probeMultiplier * f.MetresPerDevicePixel;
            double spanPx = GroundRuler.GroundSegmentSpanPx(
                f.UnityCamera, f.NearAnchorWorldUnity,
                new double2(f.CrossAzimuthDir.x, f.CrossAzimuthDir.z), probeM);

            // Printed every run, not only on failure: S1 is a STOP RULE, so the number the fix stage's whole
            // oracle is calibrated on should be legible in the results without re-deriving it.
            TestContext.WriteLine(
                $"M4 ruler check (S1): {probeMultiplier:F0}·mpp along ĉ at the look-at projects to " +
                $"{spanPx:F6} px against an expectation of {probeMultiplier:F0} px — " +
                $"{100.0 * (spanPx / probeMultiplier - 1.0):F5} % (bound ±1 %). " +
                $"mpp={f.MetresPerDevicePixel:F4} m, |P11|={f.AbsP11:F6}, H={f.ViewportHeightPx:F0}, " +
                $"w_near={f.NearAnchorViewDepthMetres:F1} m.");

            Assert.That(spanPx, Is.EqualTo(probeMultiplier).Within(1).Percent,
                $"M4 (STOP RULE S1): {probeMultiplier:F0}·mpp of ground laid ACROSS the view axis at the " +
                $"look-at must project to {probeMultiplier:F0} device px — measured {spanPx:F4} px " +
                $"({100.0 * (spanPx / probeMultiplier - 1.0):F3} % off; mpp={f.MetresPerDevicePixel:F3} m). " +
                "If this is a genuine failure, DO NOT widen the tolerance — report both numbers and STOP, " +
                "because the fix stage's oracle would then be calibrated against the wrong ruler.");
        }

        /// <summary>
        /// <b>M5 — THE DISCRIMINATOR the whole stage rests on.</b> Proves: a world length perpendicular to the
        /// view axis projects to HALF as many pixels when its view depth doubles — the 1/w law, measured on
        /// the live camera at the fixture's own two anchors.
        ///
        /// <para>Does NOT prove anything about symbols or layout — this is the projection alone. It is the
        /// tooth that says the fixture's two depths are far enough apart to tell a world-welded ruler from a
        /// screen-constant one.</para>
        ///
        /// <para>RED-verify: place BOTH probes at the near anchor ⇒ reads 1.00 instead of 0.50.</para>
        /// </summary>
        [Test]
        public void PerpendicularSpan_HalvesWhenTheViewDepthDoubles()
        {
            using var f = CreateFixture();
            double probeM = 30.0 * f.MetresPerDevicePixel;
            var cXZ = new double2(f.CrossAzimuthDir.x, f.CrossAzimuthDir.z);

            double nearPx = GroundRuler.GroundSegmentSpanPx(f.UnityCamera, f.NearAnchorWorldUnity, cXZ, probeM);
            double farPx  = GroundRuler.GroundSegmentSpanPx(f.UnityCamera, f.FarAnchorWorldUnity, cXZ, probeM);
            double expected = f.NearAnchorViewDepthMetres / f.FarAnchorViewDepthMetres;

            Assert.That(farPx / nearPx, Is.EqualTo(expected).Within(2).Percent,
                $"M5: the SAME world length must project to w_near/w_far = {expected:F4} of its near size at " +
                $"the far anchor — measured {farPx / nearPx:F4} (near {nearPx:F3} px, far {farPx:F3} px).");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M6–M9 — the staged symbol geometry, read back from the built meshes.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M6 — THE TOOTH THAT VALIDATES THE WHOLE READBACK PATH.</b> Proves: the world anchor the
        /// production staging baked for the near symbol's CENTRE glyph, read back through
        /// <c>WorldSlotTransform(...).TransformPoint(AnchorLocal)</c>, lands where the fixture intended its
        /// anchor — within 2 device px projected. If this holds, the fixture is measuring the renderer's own
        /// geometry, with no RTC, tile origin or floating-origin rebase re-derived on the test side.
        ///
        /// <para>Checked at the LOOK-AT on purpose: on a constant-depth line the projection is affine, so the
        /// <c>(seg, t) = (0, 0.5)</c> anchor resolves to the same point whether the arc walk runs on the screen
        /// polyline (today) or the world polyline (after a fix) — so this tooth does NOT presuppose either
        /// layout model and survives the fix unchanged.</para>
        ///
        /// <para>Does NOT prove anything about glyph SPACING. Run for BOTH arms — see the far twin below.</para>
        ///
        /// <para>RED-verify: offset the intended anchor by <c>20·mpp</c> along ĉ ⇒ fails by ~20 px.</para>
        /// </summary>
        [Test]
        public void GlyphWorldAnchors_LandWhereTheFixtureIntended_AtTheLookAt()
            => AssertCentreGlyphLandsAtTheIntendedAnchor(OffLookAtSymbolId.CrossNear, "M6 near");

        /// <summary>
        /// <b>M6b — the FAR twin, added after both review arms independently flagged the gap.</b> `CrossFar`
        /// has its OWN tile key and its own RTC bake, and nothing else in the suite pins it against a known
        /// ABSOLUTE position: M10 bounds it to only ±45 rows (≈±14 % of <c>w_far</c>), and M7–M9 pin spacing
        /// and span, not placement. A far readback that were systematically displaced would leave every
        /// headline RATIO intact, because the ratios are differences within one symbol — so this is the one
        /// clause standing between the far arm and a silently wrong absolute anchor.
        ///
        /// <para>The look-at is NOT special to M6's argument: the walk-invariance that makes this
        /// model-independent is a property of a CONSTANT-VIEW-DEPTH (cross-azimuth) line, which
        /// <c>CrossFar</c> is by construction. So this survives the fix unchanged, exactly as M6 does.</para>
        ///
        /// <para>RED-verify: same injection as M6, applied to the far arm.</para>
        /// </summary>
        [Test]
        public void GlyphWorldAnchors_LandWhereTheFixtureIntended_AwayFromTheLookAt()
            => AssertCentreGlyphLandsAtTheIntendedAnchor(OffLookAtSymbolId.CrossFar, "M6b far");

        private static void AssertCentreGlyphLandsAtTheIntendedAnchor(OffLookAtSymbolId id, string what)
        {
            using var f = CreateFixture();
            SymbolMeasurement m = f.Measure(id);
            int centre = f.Config.GlyphCount / 2;
            double2 intendedPx = m.AnchorScreenPx;
            double2 stagedPx = m.Glyphs[centre].ScreenPx;
            double errorPx = math.length(stagedPx - intendedPx);

            Assert.That(errorPx, Is.LessThan(2.0),
                $"{what}: the label's centre glyph (index {centre} of {f.Config.GlyphCount}) staged at " +
                $"({stagedPx.x:F2}, {stagedPx.y:F2}) px but the fixture anchored the label at " +
                $"({intendedPx.x:F2}, {intendedPx.y:F2}) px — {errorPx:F2} px apart. Either the readback path " +
                "(slot transform → AnchorLocal) is not what this fixture thinks it is, or the label is not " +
                "where it was placed.");
        }

        /// <summary>
        /// <b>M7.</b> Proves: both cross-azimuth symbols really do lie at CONSTANT view depth — every glyph of
        /// a symbol is within 0.5 % of that symbol's mean depth, at both depths. This is the property the
        /// headline measurement rests on: it is what makes the far/near comparison a pure 1/w law with no
        /// within-symbol foreshortening to correct for.
        ///
        /// <para>Does NOT prove that ĉ is the cross-azimuth direction in any absolute sense, and nothing about
        /// spacing.</para>
        ///
        /// <para>RED-verify: point the first reading at <c>RecedingNear</c>, a symbol that genuinely spans a
        /// depth range ⇒ the spread reads ~36 %, 70× the bound. NOT by rebuilding the cross symbols along ĝ:
        /// that turns the whole fixture red at a construction precondition (the cross arm's
        /// linear-agreement check, then the staging count), so the tooth's own red would not be
        /// attributable to the tooth.</para>
        /// </summary>
        [Test]
        public void CrossAzimuthSymbols_LieAtConstantViewDepth()
        {
            using var f = CreateFixture();
            double nearSpread = RelativeDepthSpread(f.Measure(OffLookAtSymbolId.CrossNear));
            double farSpread  = RelativeDepthSpread(f.Measure(OffLookAtSymbolId.CrossFar));

            Assert.That(math.max(nearSpread, farSpread), Is.LessThan(0.005),
                $"M7: a cross-azimuth label's glyphs must all sit at one view depth — relative spread " +
                $"near {nearSpread:P4}, far {farSpread:P4} (bound 0.5000 %). A large spread means ĉ is not " +
                "perpendicular to the view axis and the far/near comparison is no longer a pure 1/w law.");
        }

        /// <summary>
        /// <b>M8.</b> Proves: both cross-azimuth symbols staged EVERY glyph — <c>4 · GlyphCount</c> stream-0
        /// vertices on each slot mesh. A curved symbol whose road is too short returns 0 from
        /// <c>StageCurved</c>'s spill check and silently disappears; that must be a loud failure, not a
        /// missing measurement.
        ///
        /// <para>(That all four corners of each glyph share ONE <c>AnchorLocal</c> — the precondition that
        /// makes "the glyph's position" a single well-defined point — is asserted by the fixture itself at
        /// readback, on every symbol.)</para>
        ///
        /// <para>Does NOT prove the glyphs are in the right PLACE — that is M6/M9's job.</para>
        ///
        /// <para>RED-verify: set <c>SpillMargin = 0.5</c> ⇒ the roads are shorter than the symbols and both
        /// stage 0.</para>
        /// </summary>
        [Test]
        public void CurvedSymbols_StageEveryGlyph_AtBothDepths()
        {
            using var f = CreateFixture();
            int expected = 4 * f.Config.GlyphCount;
            int nearCount = f.VertexCount(OffLookAtSymbolId.CrossNear);
            int farCount  = f.VertexCount(OffLookAtSymbolId.CrossFar);

            Assert.That(math.min(nearCount, farCount) == expected && math.max(nearCount, farCount) == expected,
                Is.True,
                $"M8: each cross-azimuth slot mesh must carry {expected} vertices (4 per glyph × " +
                $"{f.Config.GlyphCount}) — near {nearCount}, far {farCount}.");
        }

        /// <summary>
        /// <b>M9 — EXTRACTION CALIBRATION.</b> Proves: the per-gap measurement genuinely resolves INDIVIDUAL
        /// glyph gaps — within each cross-azimuth symbol the gaps are uniform to better than 1 %, as uniform
        /// baked advances on a constant-depth line require. A measurement that silently reported one averaged
        /// number, or mis-paired glyphs, could not show this.
        ///
        /// <para>True under BOTH the screen-constant and the world-welded model (uniform advances on a
        /// constant-depth line are uniform either way), so this tooth survives the fix unchanged — it
        /// calibrates the instrument, it does not pin the defect.</para>
        ///
        /// <para>Does NOT prove the gaps are the right SIZE. That is precisely what is at issue and precisely
        /// what this stage does not assert for the far symbol.</para>
        ///
        /// <para>RED-verify: perturb one glyph's <c>ArcCenter</c> in the fixture ⇒ non-uniform.</para>
        /// </summary>
        [Test]
        public void GlyphSpacing_IsUniformWithinEachSymbol()
        {
            using var f = CreateFixture();
            double nearNonUniformity = ScreenSpacingNonUniformity(f.Measure(OffLookAtSymbolId.CrossNear));
            double farNonUniformity  = ScreenSpacingNonUniformity(f.Measure(OffLookAtSymbolId.CrossFar));

            Assert.That(math.max(nearNonUniformity, farNonUniformity), Is.LessThan(0.01),
                $"M9: within one cross-azimuth label the glyph gaps must be uniform — max/min − 1 reads " +
                $"{nearNonUniformity:P4} (near) and {farNonUniformity:P4} (far), bound 1.0000 %. A failure " +
                "means the per-gap extraction is not resolving individual gaps.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M10–M12 — the render, the receding soundness arm, the point arm.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M10 — RENDER CORROBORATION.</b> Proves: both cross-azimuth symbols actually RENDER, each inside
        /// the disjoint row band its own projected anchor defines — so the geometry the mesh readback measured
        /// is the geometry that reaches the screen, at both depths.
        ///
        /// <para>Does NOT measure spacing from ink, deliberately: an ink-width reading conflates the CPU's
        /// glyph spacing with the shader's screen-px glyph SIZE, and conflating those is how the reverted P3c
        /// was approved by two review arms.</para>
        ///
        /// <para><b>W2 — THE FAR FLOOR IS NOW DERIVED, NOT A FLAT CONSTANT, AND THAT IS A CONSEQUENCE OF THE
        /// FIX RATHER THAN A CONCESSION TO IT.</b> <see cref="InkFloor"/> was calibrated when a glyph was a
        /// fixed SCREEN size at every depth, so both bands carried comparable ink. Since W2 a map-pitched
        /// glyph is a fixed WORLD size — the settled model, <c>text-size</c> under <c>pitch-alignment: map</c>
        /// is X px TOP-DOWN — so the far symbol's screen area falls as <c>1/w²</c> BY DESIGN. Keeping one flat
        /// floor for both bands would assert that the far symbol is NOT foreshortened, i.e. it would pin the
        /// defect W2 removes. The far band therefore gets <see cref="FarInkFloor"/>, the same floor divided by
        /// the fixture's OWN achieved depth ratio squared.</para>
        ///
        /// <para><b>The four readings this was derived from, all MEASURED on this fixture at its shipped pose</b>
        /// (tilt 55, ratio 2). The pre-W2 pair was taken by forcing <c>WorldSymbolRenderer</c>'s
        /// <c>mapPitchCorners</c> to <c>false</c> and restoring it:
        /// <code>
        ///            near band    far band    far/near
        ///   pre-W2      1200 px     1262 px     1.052    &lt;- screen-constant size: the far symbol renders the
        ///                                                   SAME size as the near one. The defect, in ink.
        ///   post-W2      801 px       98 px     0.122    &lt;- welded to the ground, and foreshortened.
        /// </code></para>
        ///
        /// <para><b>The NEAR band moves too, and that is correct rather than a regression.</b> A reader
        /// expects it not to, because at the look-at depth the two rulers coincide exactly — but that identity
        /// is about the RULER, not about the PLANE. A map-pitched cell's x̂ arm lies along the road
        /// (cross-azimuth here, perpendicular to the view axis, so it projects 1:1 and does not move), while
        /// its ŷ arm lies along <c>cross(up, x̂)</c> — the RECEDING ground direction — which at tilt 55 is
        /// foreshortened by roughly <c>cos 55° = 0.574</c>. The glyph is lying flat on the ground instead of
        /// facing the camera, so it loses height: 1200 → 801 px is a factor of 0.667 against 0.574 for the
        /// height alone. That IS the stage's headline behaviour, seen from the ink side.</para>
        ///
        /// <para><b>Why the far band falls FASTER than the 1/w² area law</b> (0.122 of the near band where a
        /// pure area model predicts 0.250) — stated so a future reader does not diagnose a size defect. Two
        /// mechanisms, both real and both expected: (1) the far anchor is viewed at a MORE GRAZING angle than
        /// the near one, so its ŷ arm foreshortens harder still — a per-anchor effect that no single global
        /// cosine captures; and (2) ink is COUNTED against a hard threshold
        /// (<c>WorldSymbolInkAnalysis.InkThreshold</c>) while the text shader ramps coverage over ~1 screen px
        /// at the glyph edge, so a stroke whose width has halved loses a larger FRACTION of itself to the
        /// transition. Neither is a property of the glyph's world size. <c>1/ratio²</c> is therefore used only
        /// as a CONSERVATIVE lower bound on the shrinkage; the true falloff is faster, and the achieved margin
        /// is 98/50 = 1.96×.</para>
        ///
        /// <para>That conservatism is also why this tooth stays a PRESENCE check ("did the far symbol reach the
        /// screen at all") instead of being promoted into a size assertion. The size law is asserted where it
        /// can be read without either confound — in WORLD METRES, off the built mesh — by W2-T3 in
        /// <c>MapPitchedGlyphSizeTests</c>.</para>
        ///
        /// <para>Still does NOT measure spacing from ink (see above), and now also asserts that the far band
        /// CONTAINS its cell rather than clipping it — a shrinking cell could in principle have drifted out of
        /// a band sized for the old one, and an ink count that ROSE would mean the band had caught a
        /// neighbour. Verified, not assumed.</para>
        ///
        /// <para>RED-verify: move the far anchor 400 px up-screen ⇒ its band is empty.</para>
        /// </summary>
        [Test]
        public void RenderedInk_AppearsInBothProjectedBands()
        {
            using var f = CreateFixture();
            f.RowBandFor(OffLookAtSymbolId.CrossNear, out int nearFrom, out int nearTo);
            f.RowBandFor(OffLookAtSymbolId.CrossFar,  out int farFrom,  out int farTo);
            Assert.That(farTo < nearFrom || nearTo < farFrom, Is.True,
                $"M10 precondition: the two labels' row bands must be DISJOINT — near [{nearFrom}, {nearTo}], " +
                $"far [{farFrom}, {farTo}]. Overlapping bands cannot attribute ink to a label.");

            WorldSymbolInkAnalysis.AnalyzeInk(f.InkPixels, f.Config.SizePx, f.Config.SizePx, nearFrom, nearTo,
                out _, out _, out _, out _, out _, out _, out int nearInk);
            WorldSymbolInkAnalysis.AnalyzeInk(f.InkPixels, f.Config.SizePx, f.Config.SizePx, farFrom, farTo,
                out int farMinRow, out int farMaxRow, out _, out _, out _, out _, out int farInk);

            int farFloor = FarInkFloor(f);
            // M13's pattern: the numbers reproduce from the COMMITTED suite, not from a throwaway probe.
            // These are the readings W2's re-derivation of the far floor was built on.
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "M10  nearInk={0} px (band [{1}, {2}], floor {3})  farInk={4} px (band [{5}, {6}], derived " +
                "floor {7})  achieved depth ratio={8:F4}  far/near={9:F4}  1/ratio²={10:F4}",
                nearInk, nearFrom, nearTo, InkFloor, farInk, farFrom, farTo, farFloor,
                f.AchievedDepthRatio, (double)farInk / nearInk,
                1.0 / (f.AchievedDepthRatio * f.AchievedDepthRatio)));

            // W2: the far cell is now SMALLER than the band that was sized for the old one, so it must sit
            // strictly inside it. Touching an edge means the band is clipping the measurement; ink at or
            // above the near band's level would mean the band caught a neighbouring symbol instead.
            Assert.That(farMinRow > farFrom && farMaxRow < farTo, Is.True,
                $"M10 precondition: the far band must CONTAIN its cell, not clip it — far ink rows " +
                $"[{farMinRow}, {farMaxRow}] inside band [{farFrom}, {farTo}]. Touching a band edge means the " +
                "reading is clipped, so neither the count nor the floor below means what it says.");

            // The far symbol must be FORESHORTENED, not merely present. The bound is the depth ratio, not 1:
            // `farInk < nearInk` alone separates the post-W2 reading (0.122) from the pre-W2 one (1.052) by
            // only 5 %, which is not a margin. Dividing by the achieved ratio puts the threshold at ~400 px
            // against a measured 98 — roughly 4× either way, and it is still a full ratio× looser than the
            // 1/ratio² area law, so it cannot fail for the "falls faster than the area model" reason this
            // tooth's doc explains.
            double farInkCeiling = nearInk / f.AchievedDepthRatio;
            Assert.That(farInk, Is.LessThan(farInkCeiling),
                $"M10: the far label must be FORESHORTENED — it carries {farInk} px against a ceiling of " +
                $"{farInkCeiling:F0} px (near {nearInk} px / achieved depth ratio {f.AchievedDepthRatio:F3}). " +
                "Pre-W2 the far band read ~1.05× the near band's ink because the glyph was a fixed SCREEN " +
                "size; a reading anywhere near the near band's means the world ruler is not reaching the " +
                "drawn size. Ink at or above the near band would additionally mean the band caught a neighbour.");

            // Each band against ITS OWN floor; the worse MARGIN is the single asserted clause (this file's
            // header: compute both readings, assert the worse one, carry both numbers in the message).
            double nearMargin = nearInk / (double)InkFloor;
            double farMargin  = farInk  / (double)farFloor;
            Assert.That(math.min(nearMargin, farMargin), Is.GreaterThan(1.0),
                $"M10: each label must render meaningful ink inside its OWN row band — near band " +
                $"[{nearFrom}, {nearTo}] carries {nearInk} px against floor {InkFloor} ({nearMargin:F2}×), " +
                $"far band [{farFrom}, {farTo}] carries {farInk} px against derived floor {farFloor} " +
                $"({farMargin:F2}×). An empty far band means the far label did not reach the screen, so " +
                "nothing measured about it corroborates. The far floor is InkFloor / achievedDepthRatio² " +
                "because since W2 a map-pitched glyph is a fixed WORLD size and its screen area falls as " +
                "1/w² by design — see this tooth's doc before widening anything.");
        }

        /// <summary>The far band's ink floor: <see cref="InkFloor"/> scaled by the fixture's OWN achieved
        /// far/near view-depth ratio squared, because since W2 a map-pitched glyph's screen AREA falls as
        /// <c>1/w²</c>. Derived from the fixture's measured pose rather than written as a literal, so it
        /// tracks a re-tuned <c>TargetDepthRatio</c> instead of silently becoming wrong — and so it cannot be
        /// mistaken for a number chosen to make the tooth pass.
        ///
        /// <para><b>The integer truncation is a real vacuity hazard, so it is ASSERTED and not clamped.</b>
        /// <c>(int)(200 / ratio²)</c> reaches <b>0</b> at <c>AchievedDepthRatio ≳ 14.1</c>, and
        /// <c>farInk / 0.0</c> is <c>+Inf</c>, which would PASS SILENTLY — a tooth that cannot fail. That is
        /// reachable by exactly the edit this doc invites (re-tuning <c>TargetDepthRatio</c>), so the failure
        /// must be loud. Clamping to 1 instead would keep the tooth alive but reduce it to "any ink at all",
        /// which is a different and much weaker claim than the one documented above; better to stop and make
        /// the maintainer choose a floor deliberately.</para></summary>
        private static int FarInkFloor(OffLookAtSymbolScene f)
        {
            int floor = (int)(InkFloor / (f.AchievedDepthRatio * f.AchievedDepthRatio));
            Assert.That(floor, Is.GreaterThanOrEqualTo(1),
                $"M10 precondition: the derived far ink floor truncated to {floor} at an achieved depth ratio " +
                $"of {f.AchievedDepthRatio:F3} (InkFloor {InkFloor} / ratio²). A floor of 0 makes the margin " +
                "check +Inf and the tooth VACUOUS — it could never fail. Choose a floor deliberately for this " +
                "pose rather than letting the truncation decide.");
            return floor;
        }

        /// <summary>
        /// <b>M11 — the DEPTH-SPANNING arm's precondition.</b> Proves: the fixture really does build a symbol
        /// that spans a RANGE of view depths (strictly increasing along the glyph run, by at least 15 % end
        /// to end) — the contrast that makes the cross-azimuth arm's constant-depth claim (M7) meaningful
        /// rather than vacuous, and the property W1-T2/T3/T4 need in order to discriminate a per-glyph world
        /// walk from a screen walk scaled by one per-symbol constant.
        ///
        /// <para><b>Stays on <c>RecedingNear</c>, deliberately.</b> Post-W1 the receding roads are symmetric
        /// in world metres, which puts <c>RecedingNear</c>'s span ratio comfortably above this bound;
        /// <c>RecedingFar</c>'s sits much closer to it (its symbol is at ~2× the depth, so the same world span
        /// is a smaller relative spread) and asserting a bound with no margin is not a tooth. That number is
        /// REPORTED by M13's table instead. <c>RecedingFar</c> is still a full participant in W1-T2/T3/T4,
        /// where even a modest depth spread gives large discrimination against a 1 % bound.</para>
        ///
        /// <para>Asserts nothing about SPACING — this file never did and still does not; the spacing
        /// assertions on this arm are W1's (<c>MapPitchedWorldArcLayoutTests</c>).</para>
        ///
        /// <para>RED-verify: rebuild the receding symbols along ĉ ⇒ the depth span collapses to 1.00 and the
        /// monotonicity is noise.</para>
        /// </summary>
        [Test]
        public void RecedingSymbol_SpansAMonotonicDepthRange()
        {
            using var f = CreateFixture();
            GlyphMeasurement[] glyphs = f.Measure(OffLookAtSymbolId.RecedingNear).Glyphs;

            double smallestStep = double.MaxValue;
            for (int g = 0; g + 1 < glyphs.Length; g++)
                smallestStep = math.min(smallestStep,
                    glyphs[g + 1].ViewDepthMetres - glyphs[g].ViewDepthMetres);
            double spanRatio = glyphs[glyphs.Length - 1].ViewDepthMetres / glyphs[0].ViewDepthMetres;

            Assert.That(smallestStep, Is.GreaterThan(0.0),
                $"M11: view depth must increase STRICTLY along the receding label's glyph run — smallest " +
                $"step {smallestStep:F3} m (first {glyphs[0].ViewDepthMetres:F1} m, last " +
                $"{glyphs[glyphs.Length - 1].ViewDepthMetres:F1} m).");
            Assert.That(spanRatio, Is.GreaterThan(1.15),
                $"M11: the receding label must span a real depth RANGE — last/first depth reads " +
                $"{spanRatio:F4} (bound 1.15). A ratio near 1.00 means this arm is not receding at all and " +
                "M7's constant-depth claim has no contrast.");
        }

        /// <summary>
        /// <b>M12 — the POINT arm.</b> Proves: the point-placement path also stages at both depths, and each
        /// point symbol's single staged quad carries a world anchor that projects within 2 px of where the
        /// fixture put it. Point and curved reach the world mesh by different routes
        /// (<c>CandidateEmit.AnchorLocal</c> vs the per-quad one); this pins that both routes land where they
        /// were aimed, so a later stage touching either has a two-depth reference.
        ///
        /// <para>Does NOT prove anything about spacing (a point symbol has one quad) or about ink — the point
        /// symbols are deliberately excluded from the rendered frame.</para>
        ///
        /// <para>RED-verify: offset one intended point anchor by <c>20·mpp</c> ⇒ fails by ~20 px (near) or
        /// ~10 px (far).</para>
        /// </summary>
        [Test]
        public void PointSymbols_StageAtBothDepths_WhereTheOracleProjectsThem()
        {
            using var f = CreateFixture();
            int nearVerts = f.VertexCount(OffLookAtSymbolId.PointNear);
            int farVerts  = f.VertexCount(OffLookAtSymbolId.PointFar);
            Assert.That(nearVerts == 4 && farVerts == 4, Is.True,
                $"M12 precondition: each point label must stage exactly one quad (4 vertices) — near " +
                $"{nearVerts}, far {farVerts}.");

            SymbolMeasurement near = f.Measure(OffLookAtSymbolId.PointNear);
            SymbolMeasurement far  = f.Measure(OffLookAtSymbolId.PointFar);
            double nearErrorPx = math.length(near.Glyphs[0].ScreenPx - near.AnchorScreenPx);
            double farErrorPx  = math.length(far.Glyphs[0].ScreenPx - far.AnchorScreenPx);

            Assert.That(math.max(nearErrorPx, farErrorPx), Is.LessThan(2.0),
                $"M12: each point label's staged anchor must project where the fixture aimed it — near off by " +
                $"{nearErrorPx:F2} px, far off by {farErrorPx:F2} px (bound 2 px).");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M13 — the measurement tooth: the stage's actual deliverable.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M13 — THE EVIDENCE.</b> Proves: every gap at both depths is finite and positive, and the NEAR
        /// (look-at) symbol's mean screen spacing agrees with the model oracle within 3 %.
        ///
        /// <para><b>The near clause is the LOOK-AT CONTROL, and it is the epic's failure in one line.</b> At
        /// the look-at, today's screen-constant ruler and the settled world-welded ("X px TOP-DOWN") model
        /// coincide EXACTLY, so this reads ≈ 1.00 under BOTH models — which is precisely why every earlier
        /// fixture in this epic, all of them anchored here, discriminated nothing.</para>
        ///
        /// <para><b>It asserts NOTHING about the far symbol — deliberately, and still.</b> The far assertion
        /// ("far/near screen spacing == 0.500") is W1-T1 in <c>MapPitchedWorldArcLayoutTests</c>, RED-verified
        /// there against the pre-fix production code. What this tooth does instead is PRINT the full per-gap
        /// table for all six symbols and the four headline ratios, so the evidence is reproducible from the
        /// committed suite rather than from a throwaway probe. Read the printed <c>far measured/oracle</c>
        /// row: it is the number W1 moved, from ≈ 2.00 to ≈ 1.00.</para>
        ///
        /// <para>The headline ratios are ratios of two readings from ONE frame and ONE symbol pair, so any
        /// uniform scale error (OneEm, TextSizePx, mpp, DPR, atlas scale, a wrong P11) multiplies both and
        /// CANCELS; only the depth dependence survives.</para>
        ///
        /// <para>RED-verify: scale <c>TextSizePx</c> on the ORACLE SIDE ONLY by 1.2 ⇒ the near residual blows
        /// past 3 %.</para>
        /// </summary>
        [Test]
        public void CurvedGlyphSpacing_MatchesTheModelAtTheLookAt_AndReportsBothDepths()
        {
            using var f = CreateFixture();
            TestContext.WriteLine(f.FormatMeasurementTable());

            double smallestGapPx = double.MaxValue;
            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.CrossNear, OffLookAtSymbolId.CrossFar })
            {
                double[] gaps = f.Measure(id).ScreenSpacingPx;
                for (int g = 0; g < gaps.Length; g++)
                {
                    Assert.That(double.IsNaN(gaps[g]) || double.IsInfinity(gaps[g]), Is.False,
                        $"M13: {id} gap {g} is not finite ({gaps[g]}) — no ratio taken from it means anything.");
                    smallestGapPx = math.min(smallestGapPx, gaps[g]);
                }
            }
            Assert.That(smallestGapPx, Is.GreaterThan(0.0),
                $"M13: every measured gap must be positive — smallest reads {smallestGapPx:F6} px.");

            double nearMeasured = OffLookAtSymbolScene.Mean(f.Measure(OffLookAtSymbolId.CrossNear).ScreenSpacingPx);
            double nearOracle   = f.ModelPredictedScreenSpacingPx(OffLookAtSymbolId.CrossNear);
            Assert.That(nearMeasured, Is.EqualTo(nearOracle).Within(3).Percent,
                $"M13 (the LOOK-AT CONTROL): the near label's mean screen spacing ({nearMeasured:F4} px) must " +
                $"match the model oracle ({nearOracle:F4} px) within 3 % — measured " +
                $"{100.0 * (nearMeasured / nearOracle - 1.0):F3} % off. This clause reads ≈ 1.00 under BOTH " +
                "competing models; the discriminating reading is the FAR row of the printed table, which this " +
                "tooth deliberately does not assert.");
        }

        // ── shared reductions ────────────────────────────────────────────────────────────────────────────

        private static double RelativeDepthSpread(SymbolMeasurement m)
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0.0;
            for (int g = 0; g < m.Glyphs.Length; g++)
            {
                double w = m.Glyphs[g].ViewDepthMetres;
                min = math.min(min, w);
                max = math.max(max, w);
                sum += w;
            }
            return (max - min) / (sum / m.Glyphs.Length);
        }

        private static double ScreenSpacingNonUniformity(SymbolMeasurement m)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int g = 0; g < m.ScreenSpacingPx.Length; g++)
            {
                min = math.min(min, m.ScreenSpacingPx[g]);
                max = math.max(max, m.ScreenSpacingPx[g]);
            }
            return max / min - 1.0;
        }
    }
}
#endif // UNITY_EDITOR
