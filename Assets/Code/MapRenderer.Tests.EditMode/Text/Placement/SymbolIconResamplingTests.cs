// Unity EditMode only — off-screen GPU renders of the REAL icon draw path. NOT registered in
// core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D/SpriteSheet).
//
// These are the teeth the icon path was owed for RESAMPLING — how the sprite sheet's texels are mapped onto
// device pixels. Every pre-existing icon test renders ONE static frame, so none of them can see a defect
// whose whole signature is "the render changes when it should not". That is why the bug below shipped.
//
// The reported symptom was "pixels inside the icon warp while zooming/panning". The geometry cannot produce
// that: BillboardMath.BuildWorldQuad gives all four corners the SAME bitwise anchorLocal plus static
// per-corner Offset, so a quad is RIGID in screen space — an anchor precision error TRANSLATES an icon and
// can never deform its interior. Interior deformation therefore has to be resampling, and it was:
// SpriteSheet bound the sheet with FilterMode.Point.
//
// Nearest-neighbour is exact only at INTEGER magnification. An icon's magnification is
// `iconSize * dpr / pixelRatio` — the sheet is always fetched @1x and dpr is Screen.dpi/160, so it is
// essentially never an integer. At a non-integer magnification each source texel covers either N or N+1
// device pixels, and WHICH depends on the quad's sub-pixel phase, so panning re-quantises the icon's
// interior every frame. Note the trap in that: at exactly 1x, Point sampling is a pixel-perfect blit, so the
// defect is INVISIBLE at the one setting anyone would eyeball first.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolIconResamplingTests
    {
        private const int Size = 256;
        private const int SheetSize = 64;

        /// <summary>
        /// The magnification (device px per source texel) the NON-sweeping teeth render at. Deliberately
        /// NON-INTEGER — at an integer magnification nearest-neighbour is exact and the interior-resampling
        /// defect does not exist. 2.5 is chosen over, say, 1.4 because it separates the two filters
        /// furthest: see <see cref="MaxCentroidDeviationPx"/>.
        ///
        /// <para>The sweeping teeth take their magnification as a <c>[TestCase]</c> parameter instead: the
        /// whole defect family is "which magnification you happen to be at", so a tooth pinned at a single
        /// one is weak. Tooth 1 sweeps only non-integer values (its defect is absent at integer
        /// magnification); <see cref="IconSilhouette_TracksSubPixelPhase_ForAFullBleedSprite"/> deliberately
        /// INCLUDES 1.0, where tooth 1 is vacuous and the silhouette defect is at its sharpest.</para>
        /// </summary>
        private const float Magnification = 2.5f;

        /// <summary>Phase step of the sweep, device px. The sweep spans one FULL device pixel — a partial
        /// sweep could sit entirely inside one tread of the nearest-neighbour staircase and read smooth.</summary>
        private const float PhaseStepPx = 0.125f;

        /// <summary>
        /// The PRIMARY tooth, in device px: the least the ink is allowed to advance for one
        /// <see cref="PhaseStepPx"/> of quad shift. Set to half the ideal step, so it states "the icon moves
        /// when the map moves" with a wide tolerance on HOW MUCH.
        ///
        /// <para>Chosen over a deviation-from-ramp bound (kept below as a secondary check) because it
        /// separates the two filters by an order of magnitude rather than a factor of 1.4. Nearest-neighbour
        /// does not move the ink AT ALL until the phase crosses a texel boundary, so its worst step is
        /// exactly <c>0.000</c> px — measured, against the un-fixed tree: the centroid sat on 129.000 px for
        /// three consecutive phases. Bilinear advances ~0.125 px every step.</para>
        /// </summary>
        private const float MinCentroidStepPx = 0.5f * PhaseStepPx;

        /// <summary>
        /// Secondary bound, device px: worst deviation of the measured centroid from the ideal ramp. Weaker
        /// than <see cref="MinCentroidStepPx"/> — measured at 0.2125 px against the un-fixed tree, so it
        /// clears this bound by only 1.4x. It is retained because it catches a defect the step test cannot:
        /// ink that advances smoothly but at the WRONG RATE.
        /// </summary>
        private const float MaxCentroidDeviationPx = 0.15f;

        /// <summary>
        /// The silhouette tooth's bounds, device px: every step of the sweep must fall between a QUARTER and
        /// THREE TIMES the ideal <see cref="PhaseStepPx"/>. Two-sided, and deliberately NOT
        /// <see cref="MinCentroidStepPx"/> — that bound is calibrated for tooth 1's probe, and cannot be
        /// reused here for a structural reason:
        ///
        /// <para>Tooth 1's ink IS its ramp (a one-texel stripe), so its centroid tracks the quad's phase
        /// almost exactly — measured min step 0.092–0.117 px against an ideal 0.125. A full-bleed sprite's
        /// ink is a SLAB whose only moving parts are the two one-texel ramps at its edges, and the readback
        /// does NOT weight those ramps by their coverage. The project renders in Linear colour space into an
        /// sRGB ARGB32 target, so the measured <c>ink = (255 - r)/255</c> is a NONLINEAR function of alpha:
        /// a half-covered pixel (alpha 0.5) reads back ink ≈ 0.265, not 0.5 — the same transfer curve
        /// <see cref="IconInk_RendersAtItsNominalSize_NotTheInsetMagnifiedSize"/>'s measurement is
        /// deliberately built to be immune to (it uses symmetric centroids for exactly this reason). The
        /// curve strongly de-weights every mid-ramp pixel, which sharpens the effective ink profile back
        /// toward the hard edge the border exists to soften, so the centroid's per-step advance is uneven
        /// even though its MEAN rate is exact. Measured after the fix: min 0.062–0.106, max 0.171–0.218
        /// across magnifications 1.0/1.37/2.5/4.0. The residual scales with a ONE-texel ramp being only
        /// <c>M</c> device px wide; a wider border would smooth it, and that is deliberately out of scope.
        ///
        /// <para>What the tooth must discriminate is the STAIRCASE, and a staircase's signature is
        /// two-sided: the centroid holds EXACTLY still and then jumps a WHOLE pixel. Measured against the
        /// un-fixed tree, at every magnification: min step exactly <c>0.0000</c> px (fails the lower bound
        /// outright) and max step exactly <c>1.0000</c> px, 8× the ideal (fails the upper bound by 2.7×).
        /// Both bounds therefore separate fixed from un-fixed absolutely, with ~1.7–2.0× margin on the
        /// passing side.</para>
        /// </summary>
        private const float MinSilhouetteStepPx = 0.25f * PhaseStepPx;

        /// <summary>See <see cref="MinSilhouetteStepPx"/> — the upper half of the same two-sided bound.
        /// A whole-pixel jump (the staircase's tread-and-riser) is 8× <see cref="PhaseStepPx"/>.</summary>
        private const float MaxSilhouetteStepPx = 3f * PhaseStepPx;

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth 1 — sub-pixel phase stability.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [TestCase(1.37f)]
        [TestCase(2.5f)]
        [TestCase(3.25f)]
        public void IconInterior_TracksSubPixelPhaseSmoothly_DoesNotSnapToTheTexelGrid(float magnification)
        {
            // A ONE-TEXEL-wide opaque column is the sharpest probe available: at a non-integer magnification
            // nearest-neighbour renders it as either floor(M) or floor(M)+1 device px wide depending on
            // phase, so its centroid can only sit on the destination pixel grid.
            var index = SpriteIndex.Parse(
                $"{{\"stripe\":{{\"x\":0,\"y\":0,\"width\":{SheetSize},\"height\":{SheetSize},\"pixelRatio\":1}}}}");
            var sheet = new SpriteSheet(BuildStripeSheetPng(), index);
            GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            try
            {
                AssertSheetDecodedAsAuthored(sheet);
                // The REPACKED index — SpriteSheet relocates every sprite into its own padded cell, so the
                // raw parsed rect no longer describes the bound texture.
                Assert.IsTrue(sheet.View.Index.TryGetSprite("stripe", out SpriteEntry entry));

                var phasesPx = new float[8];
                for (int i = 0; i < phasesPx.Length; i++) phasesPx[i] = i * PhaseStepPx;
                var centroids = new float[phasesPx.Length];

                for (int i = 0; i < phasesPx.Length; i++)
                {
                    // IconQuadLayout multiplies icon-offset by icon-size, so pre-divide to land on an exact
                    // device-px shift. The fixture's dpr is 1 (asserted below), so logical px ARE device px.
                    float2 offset = new float2(phasesPx[i] / magnification, 0f);
                    SymbolQuad quad = IconQuadLayout.Layout(
                        entry, sheet.View.Size, magnification, MapRenderer.Core.Text.TextAnchor.Center, offset);
                    centroids[i] = RenderAndMeasureInkCentroidX(sheet, glyphAtlas, quad, "stripe");
                }

                // Ideal: the ink's centroid advances by exactly the phase shift. Fit is not needed — the
                // slope is known to be 1 — so compare against the ramp anchored at the sweep's own mean,
                // which removes the arbitrary absolute position of the anchor's projection.
                float meanCentroid = 0f, meanPhase = 0f;
                for (int i = 0; i < phasesPx.Length; i++) { meanCentroid += centroids[i]; meanPhase += phasesPx[i]; }
                meanCentroid /= phasesPx.Length;
                meanPhase /= phasesPx.Length;

                float worst = 0f;
                int worstAt = 0;
                float smallestStep = float.MaxValue;
                int smallestStepAt = 0;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float expected = meanCentroid + (phasesPx[i] - meanPhase);
                    float deviation = math.abs(centroids[i] - expected);
                    float step = i == 0 ? float.NaN : centroids[i] - centroids[i - 1];
                    report.Append($"\n  phase {phasesPx[i]:F3}px → centroid {centroids[i]:F4}px " +
                                  $"(expected {expected:F4}, off by {deviation:F4}" +
                                  (i == 0 ? ")" : $", step {step:+0.0000;-0.0000})"));
                    if (deviation > worst) { worst = deviation; worstAt = i; }
                    if (i > 0 && step < smallestStep) { smallestStep = step; smallestStepAt = i; }
                }

                // Record the sweep unconditionally, not only on failure: the PASSING margins are what tell
                // the next person whether these bounds are comfortable or a flake waiting for a different
                // GPU, and they cost nothing to keep in the results XML.
                TestContext.Out.WriteLine($"phase sweep (magnification {magnification}):{report}");
                TestContext.Out.WriteLine(
                    $"  smallest step {smallestStep:F4}px (bound {MinCentroidStepPx}, ideal {PhaseStepPx}) | " +
                    $"worst ramp deviation {worst:F4}px (bound {MaxCentroidDeviationPx})");

                // PRIMARY: the ink must MOVE for every sub-pixel step. A zero step is the nearest-neighbour
                // staircase's tread — the icon frozen against a map that is still panning.
                Assert.Greater(smallestStep, MinCentroidStepPx,
                    $"the icon's interior must ADVANCE for every sub-pixel shift of the quad. Smallest " +
                    $"advance was {smallestStep:F4}px at phase {phasesPx[smallestStepAt]:F3}px (bound " +
                    $"{MinCentroidStepPx}px, ideal {PhaseStepPx}px). A step at or near ZERO means the ink " +
                    $"snapped to the sheet's texel grid and held still, then jumped — which on screen is the " +
                    $"icon's interior warping as the map pans.{report}");

                // SECONDARY: smooth but at the wrong rate — a defect the step bound above cannot see.
                Assert.Less(worst, MaxCentroidDeviationPx,
                    $"the icon's ink must advance at the SAME rate as the quad, not merely advance. Worst " +
                    $"deviation from the ideal ramp {worst:F4}px at phase {phasesPx[worstAt]:F3}px " +
                    $"(bound {MaxCentroidDeviationPx}px).{report}");
            }
            finally
            {
                glyphAtlas.Dispose();
                sheet.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth 2 — the atlas-bleed guard.
        //
        // This one is GREEN under nearest-neighbour and exists to fence the FIX: a bare switch to bilinear
        // over an edge-to-edge UV rect in a PACKED sheet turns it RED, because a tap at the rect's boundary
        // blends the sprite packed next door.
        //
        // What keeps it green is now the ONE-TEXEL TRANSPARENT BORDER SpriteSheet's repack lays around every
        // sprite: the sprites are physically separated in the bound texture, so an edge tap reaches the
        // border (this sprite's own colour at alpha 0) rather than the neighbour. That is a better reason
        // than the half-texel inset it replaced — the inset merely kept the sampler away from the seam, at
        // the cost of never drawing the sprite's outer half-texel.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void IconSampling_NeverBleedsTheNeighbouringSprite_AcrossThePackedSheetBoundary()
        {
            // Two sprites sharing an internal edge, in maximally-separated hues so any blend is unambiguous.
            var index = SpriteIndex.Parse(
                "{\"left\":{\"x\":0,\"y\":0,\"width\":32,\"height\":64,\"pixelRatio\":1}," +
                "\"right\":{\"x\":32,\"y\":0,\"width\":32,\"height\":64,\"pixelRatio\":1}}");
            var sheet = new SpriteSheet(BuildTwoSpriteSheetPng(), index);
            GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            try
            {
                // The repacked index: the raw parsed rect describes the FETCHED sheet, not the bound one.
                Assert.IsTrue(sheet.View.Index.TryGetSprite("left", out SpriteEntry left));

                SymbolQuad quad = IconQuadLayout.Layout(
                    left, sheet.View.Size, Magnification, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);

                byte[] px = RenderIcon(sheet, glyphAtlas, quad, "left", LabelPaintWhite(), Color.black, out _);

                // The left sprite is pure RED. Any pixel where blue leads red carries ink from the RIGHT
                // sprite — impossible unless the sampler reached across the rect boundary.
                int bledPixels = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    if (px[i + 2] > px[i] + 24) bledPixels++;
                }

                Assert.Zero(bledPixels,
                    $"{bledPixels} pixels carry the NEIGHBOURING sprite's blue. In the FETCHED sheet these " +
                    $"two sprites abut with a zero-pixel gap, so a bilinear tap at the rect boundary would " +
                    $"blend them. SpriteSheet's one-texel transparent border is what prevents it — if this " +
                    $"went red, the repack stopped separating sprites (or the UV rect grew past its own " +
                    $"border into the next cell).");
            }
            finally
            {
                glyphAtlas.Dispose();
                sheet.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth U1 — the SILHOUETTE, for a sprite with no transparent margin of its own.
        //
        // Nothing above covers the actual reported defect. Tooth 1 probes a one-texel stripe INSIDE a sprite
        // that carries a transparent margin, so it can only see interior resampling. This one probes the
        // OUTER boundary of a sprite whose ink touches all four rect edges — which is what 228 of the 264
        // sprites in the shipped style's sheet look like.
        //
        // For such a sprite the silhouette IS the quad's polygon edge. MSAA is off project-wide
        // (Assets/Settings/RPAsset.asset m_MSAA: 1, ProjectSettings/QualitySettings.asset antiAliasing: 0),
        // so a pixel is covered iff its centre falls inside the quad — one binary sample. Every interior
        // texel being uniformly opaque, the ink is exactly the covered rectangle, its centroid is that
        // rectangle's centre, and it MOVES ONLY WHEN AN EDGE CROSSES A PIXEL CENTRE: several 0.000 steps,
        // then a whole-pixel jump. No sampler setting can help, because bilinear can only soften an edge it
        // has a transparent texel to ramp into.
        //
        // The fix is content: a one-texel transparent border in the sheet turns the silhouette into a
        // texture ALPHA edge, and the quad grows by exactly that border so the ramp is actually rasterized.
        // A "pad the atlas but leave the quad nominal" implementation stays RED here — nothing draws the
        // skirt, so no ramp reaches the framebuffer.
        //
        // Run at magnification 1.0 as well, where tooth 1 is vacuous (nearest-neighbour is a pixel-perfect
        // blit, no filter defect exists) and this one is at its sharpest — the polygon edge still snaps.
        // That case alone proves the two defects are different.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [TestCase(1.0f)]
        [TestCase(1.37f)]
        [TestCase(2.5f)]
        [TestCase(4.0f)]
        public void IconSilhouette_TracksSubPixelPhase_ForAFullBleedSprite(float magnification)
        {
            var index = SpriteIndex.Parse(
                "{\"solid\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"neighbour\":{\"x\":16,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}");
            var sheet = new SpriteSheet(BuildFullBleedSheetPng(), index);
            GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            try
            {
                Assert.IsTrue(sheet.View.Index.TryGetSprite("solid", out SpriteEntry entry));

                // Fixture guard — these fixtures are vertically ASYMMETRIC, so a mis-oriented author would
                // present as a resampling verdict rather than a broken fixture (it did, once).
                Assert.AreEqual(1f, sheet.Texture.GetPixel(entry.X, entry.Y).a, 1e-3f,
                    "the sprite must be FULL-BLEED: its top-left content texel must be opaque.");
                Assert.AreEqual(1f, sheet.Texture.GetPixel(
                    entry.X + entry.Width - 1, entry.Y + entry.Height - 1).a, 1e-3f,
                    "the sprite must be FULL-BLEED: its bottom-right content texel must be opaque.");

                var phasesPx = new float[8];
                for (int i = 0; i < phasesPx.Length; i++) phasesPx[i] = i * PhaseStepPx;
                var centroids = new float[phasesPx.Length];

                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float2 offset = new float2(phasesPx[i] / magnification, 0f);
                    SymbolQuad quad = IconQuadLayout.Layout(
                        entry, sheet.View.Size, magnification, MapRenderer.Core.Text.TextAnchor.Center, offset);
                    centroids[i] = RenderAndMeasureInkCentroidX(sheet, glyphAtlas, quad, "solid");
                }

                float smallestStep = float.MaxValue, largestStep = float.MinValue;
                int smallestStepAt = 0, largestStepAt = 0;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float step = i == 0 ? float.NaN : centroids[i] - centroids[i - 1];
                    report.Append($"\n  phase {phasesPx[i]:F3}px → centroid {centroids[i]:F4}px" +
                                  (i == 0 ? "" : $" (step {step:+0.0000;-0.0000})"));
                    if (i > 0 && step < smallestStep) { smallestStep = step; smallestStepAt = i; }
                    if (i > 0 && step > largestStep) { largestStep = step; largestStepAt = i; }
                }

                TestContext.Out.WriteLine($"full-bleed silhouette sweep (magnification {magnification}):{report}");
                TestContext.Out.WriteLine(
                    $"  smallest step {smallestStep:F4}px (bound {MinSilhouetteStepPx}) | " +
                    $"largest step {largestStep:F4}px (bound {MaxSilhouetteStepPx}) | ideal {PhaseStepPx}");

                // The staircase's TREAD: the outline frozen against a map that is still panning.
                Assert.Greater(smallestStep, MinSilhouetteStepPx,
                    $"a FULL-BLEED icon's silhouette must ADVANCE for every sub-pixel shift of the quad. " +
                    $"Smallest advance was {smallestStep:F4}px at phase {phasesPx[smallestStepAt]:F3}px " +
                    $"(bound {MinSilhouetteStepPx}px, ideal {PhaseStepPx}px). A step at or near ZERO means " +
                    $"the silhouette is still the quad's POLYGON edge getting one binary coverage " +
                    $"sample.{report}");

                // The staircase's RISER: all the motion of a whole tread released in one frame.
                Assert.Less(largestStep, MaxSilhouetteStepPx,
                    $"a FULL-BLEED icon's silhouette must advance SMOOTHLY, not in jumps. Largest advance " +
                    $"was {largestStep:F4}px at phase {phasesPx[largestStepAt]:F3}px (bound " +
                    $"{MaxSilhouetteStepPx}px, ideal {PhaseStepPx}px). A step near a WHOLE PIXEL means the " +
                    $"outline held still and then snapped — the other half of the same staircase.{report}");
            }
            finally
            {
                glyphAtlas.Dispose();
                sheet.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth U2 — the ink SIZE must be nominal.
        //
        // The half-texel UV inset made a sprite's content render W/(W-1) larger than its quad implies
        // (+14.3% at an 8px rect). Retiring it is half the fix; the other half is that the quad grows by
        // exactly the border, so the ink lands at its nominal size. This tooth fails in BOTH directions,
        // because the border can be half-applied either way round — and note that neither shallow
        // implementation is the "shrink" the naming might suggest:
        //   * quad grown, UV left on the content → 8 content texels stretched over the 80px padded quad,
        //     10 px/texel: 50.0 px. The icon GREW.
        //   * atlas padded, quad left nominal    → 10 padded texels squeezed into the 64px nominal quad,
        //     6.4 px/texel: 32.0 px. The icon SHRANK (and the skirt is never rasterized — U1 catches that
        //     one too).
        //   * the retired inset still applied    → 7 texels over 64px, 9.143 px/texel: 45.714 px.
        //
        // MEASUREMENT — deliberately NOT a coverage-threshold width. The readback is gamma-encoded (the
        // project renders in Linear colour space into an sRGB ARGB32 target), so a "50% darkness" crossing
        // does NOT sit at 50% coverage: it sits at alpha ≈ 0.79, which pulls BOTH edges inward by a
        // magnification-proportional amount comparable to the whole signal. Instead the fixture carries two
        // one-texel bars near its edges and the tooth measures the DISTANCE BETWEEN THEIR INK CENTROIDS.
        // Each bar's rendered profile is symmetric about the bar's centre, and any monotone transfer curve
        // applied to a symmetric profile leaves it symmetric — so each centroid is transfer-independent, and
        // so is their separation. It is also a direct scale measurement: the separation is a fixed number of
        // TEXELS, so it reads back exactly `texels × devicePxPerTexel`.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Sheet-rect size of the U2 fixture sprite. 8 px is where the retired inset's W/(W-1)
        /// error was largest (+14.3 %).</summary>
        private const int InsetProbeSpriteSize = 8;

        /// <summary>Texel columns the two probe bars sit on, inside an 8-texel sprite.</summary>
        private const int InsetProbeLeftBarTexel = 1;
        private const int InsetProbeRightBarTexel = 6;

        /// <summary>icon-size for the U2 render. pixelRatio 1 and dpr 1 ⇒ magnification 8.</summary>
        private const float InsetProbeIconSize = 8f;

        [Test]
        public void IconInk_RendersAtItsNominalSize_NotTheInsetMagnifiedSize()
        {
            var index = SpriteIndex.Parse(
                $"{{\"bars\":{{\"x\":0,\"y\":0,\"width\":{InsetProbeSpriteSize}," +
                $"\"height\":{InsetProbeSpriteSize},\"pixelRatio\":1}}}}");
            var sheet = new SpriteSheet(BuildTwoBarSheetPng(), index);
            GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            try
            {
                Assert.IsTrue(sheet.View.Index.TryGetSprite("bars", out SpriteEntry entry));

                // Fixture guard — as above: the bars must be where this test believes, in the sheet as bound.
                int midRow = entry.Y + InsetProbeSpriteSize / 2;
                Assert.AreEqual(1f, sheet.Texture.GetPixel(entry.X + InsetProbeLeftBarTexel, midRow).a, 1e-3f,
                    "the LEFT probe bar must be opaque in the bound sheet.");
                Assert.AreEqual(1f, sheet.Texture.GetPixel(entry.X + InsetProbeRightBarTexel, midRow).a, 1e-3f,
                    "the RIGHT probe bar must be opaque in the bound sheet.");
                Assert.AreEqual(0f, sheet.Texture.GetPixel(
                    entry.X + (InsetProbeLeftBarTexel + InsetProbeRightBarTexel) / 2, midRow).a, 1e-3f,
                    "the gutter between the bars must be transparent.");

                SymbolQuad quad = IconQuadLayout.Layout(
                    entry, sheet.View.Size, InsetProbeIconSize,
                    MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
                float separation = RenderAndMeasureBarSeparationPx(sheet, glyphAtlas, quad, "bars");

                // Nominal: the bars are (6 - 1) == 5 texels apart, and one texel must draw at exactly
                // `icon-size × dpr / pixelRatio` == 8 device px. 5 × 8 == 40.
                const float nominal = (InsetProbeRightBarTexel - InsetProbeLeftBarTexel) * InsetProbeIconSize;
                // Where the retired half-texel inset put it: the quad's 64 px spanned only W-1 == 7 texels,
                // so a texel drew at 64/7 px and the bars sat 5 × 64/7 == 45.71 px apart.
                const float insetMagnified = nominal * InsetProbeSpriteSize / (InsetProbeSpriteSize - 1f);
                // Where "grow the quad but leave the UV on the content" would put it. Note the direction:
                // that implementation makes the icon LARGER, not smaller. The quad still grows to the padded
                // 10 texels' worth of px (80), but only the 8 CONTENT texels are sampled across it, so a
                // texel draws at 80/8 == 10 px and the bars sit 5 × 10 == 50 px apart.
                const float uvNotWidened =
                    nominal * (InsetProbeSpriteSize + 2f) / InsetProbeSpriteSize;
                // …and the other way round — "pad the atlas but leave the quad nominal": the 10 padded
                // texels are squeezed into the unchanged 64 px quad, i.e. 5 × 64/10 == 32 px.
                const float quadNotWidened =
                    nominal * InsetProbeSpriteSize / (InsetProbeSpriteSize + 2f);

                TestContext.Out.WriteLine(
                    $"bar separation {separation:F3}px — nominal {nominal:F3}, inset-magnified " +
                    $"{insetMagnified:F3}, uv-not-widened {uvNotWidened:F3}, quad-not-widened " +
                    $"{quadNotWidened:F3}");

                Assert.AreEqual(nominal, separation, 0.6f,
                    $"the icon's ink must draw at its NOMINAL size. Measured {separation:F3}px between the " +
                    $"two probe bars; nominal is {nominal:F3}px. {insetMagnified:F3}px means the half-texel " +
                    $"UV inset is still magnifying the content; {uvNotWidened:F3}px means the quad was grown " +
                    $"by the border but the UV rect was left on the content, so the content was stretched " +
                    $"across the padded quad and the icon GREW; {quadNotWidened:F3}px means the reverse — " +
                    $"the UV rect widened onto the border while the quad stayed nominal, so the icon SHRANK.");
            }
            finally
            {
                glyphAtlas.Dispose();
                sheet.Dispose();
            }
        }

        // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A minimal one-glyph atlas. Required even though nothing here renders TEXT:
        /// <c>LabelPlacementSystem.TickCore</c> gates its whole placement pass on
        /// <c>atlas?.Texture != null</c>, so a null glyph atlas silently places ZERO icons. An empty
        /// <see cref="GlyphAtlas"/> will not do either — <see cref="GlyphAtlasTexture.Upload"/> no-ops at
        /// <c>Size.y == 0</c> and leaves the texture null, which trips the same gate.
        /// </summary>
        private static GlyphAtlasTexture BuildMinimalGlyphAtlas()
        {
            byte[] pbf = File.ReadAllBytes(Path.Combine(
                Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes"));
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(pbf).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u]); // 'A' — never drawn; it exists only to make the atlas non-empty
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            Assert.IsNotNull(texture.Texture, "precondition: the glyph atlas texture must exist or no icon places.");
            return texture;
        }

        /// <summary>
        /// A 64x64 sheet: RGB is white EVERYWHERE and only alpha carries the shape, so bilinear taps across
        /// the stripe's edge interpolate between white and white — no dark fringe to bias the centroid, and
        /// nothing that depends on how the PNG encoder treats RGB under a zero alpha. The stripe is one
        /// texel wide, and VERTICAL, which makes it invariant under SpriteSheet's row flip.
        /// </summary>
        private static byte[] BuildStripeSheetPng()
        {
            var tex = new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[SheetSize * SheetSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 0);

            const int stripeX = SheetSize / 2;
            for (int y = 2; y < SheetSize - 2; y++) // transparent margin top and bottom
                pixels[y * SheetSize + stripeX] = new Color32(255, 255, 255, 255);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>
        /// The U1 fixture: a 64×64 sheet carrying <c>solid</c> at (0,0,16,16) — <b>fully opaque and
        /// uniformly black, ink touching all four rect edges</b> — abutted at (16,0,16,16) by an opaque
        /// neighbour in a different hue. Uniform RGB so nothing biases the centroid by colour; black-on-white
        /// so <see cref="RenderAndMeasureInkCentroidX"/> applies verbatim. The rest of the sheet is
        /// transparent, which is irrelevant: only <c>solid</c> is ever drawn.
        /// </summary>
        private static byte[] BuildFullBleedSheetPng()
        {
            var pixels = NewTransparentSheet();
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++) SetSpriteJsonTexel(pixels, x, y, new Color32(0, 0, 0, 255));
                for (int x = 16; x < 32; x++) SetSpriteJsonTexel(pixels, x, y, new Color32(0, 0, 255, 255));
            }
            return EncodeSheetPng(pixels);
        }

        /// <summary>
        /// The U2 fixture: an 8×8 sprite at (0,0) that is transparent except for two ONE-TEXEL opaque black
        /// bars at texel columns <see cref="InsetProbeLeftBarTexel"/> and
        /// <see cref="InsetProbeRightBarTexel"/>, inset one row top and bottom so the sprite carries a
        /// margin on every side. Two narrow bars rather than one solid block on purpose: their centroids are
        /// symmetric (hence transfer-curve-independent) and their separation is a fixed count of TEXELS, so
        /// the render reads back the drawn texel size directly.
        /// </summary>
        private static byte[] BuildTwoBarSheetPng()
        {
            var pixels = NewTransparentSheet();
            for (int y = 1; y < InsetProbeSpriteSize - 1; y++)
            {
                SetSpriteJsonTexel(pixels, InsetProbeLeftBarTexel, y, new Color32(0, 0, 0, 255));
                SetSpriteJsonTexel(pixels, InsetProbeRightBarTexel, y, new Color32(0, 0, 0, 255));
            }
            return EncodeSheetPng(pixels);
        }

        private static Color32[] NewTransparentSheet()
        {
            var pixels = new Color32[SheetSize * SheetSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 0);
            return pixels;
        }

        /// <summary>
        /// Writes one texel addressed in the SPRITE-JSON space (top-left origin) into a buffer destined for
        /// <see cref="Texture2D.SetPixels32"/>, which indexes BOTTOM-left. The two fixtures above are
        /// vertically asymmetric, so — unlike the stripe and left/right fixtures, which are invariant under
        /// the flip and can be written naively — they must go through this conversion or the sprite rects
        /// their JSON declares address empty space and the icon renders nothing.
        /// </summary>
        private static void SetSpriteJsonTexel(Color32[] pixels, int x, int yJson, Color32 color)
            => pixels[(SheetSize - 1 - yJson) * SheetSize + x] = color;

        private static byte[] EncodeSheetPng(Color32[] pixels)
        {
            var tex = new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false);
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>Left half opaque RED, right half opaque BLUE — two sprites sharing an internal edge.</summary>
        private static byte[] BuildTwoSpriteSheetPng()
        {
            var tex = new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[SheetSize * SheetSize];
            for (int y = 0; y < SheetSize; y++)
                for (int x = 0; x < SheetSize; x++)
                    pixels[y * SheetSize + x] = x < SheetSize / 2
                        ? new Color32(255, 0, 0, 255)
                        : new Color32(0, 0, 255, 255);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>
        /// Guards the synthetic fixture itself: proves the PNG round-trip, SpriteSheet's row flip and its
        /// padded repack left the one-texel stripe exactly where this test believes it is. Coordinates are
        /// read through the REPACKED entry — the sprite is relocated, so a hand-written sheet coordinate
        /// would now be checking the wrong texel. Without this, a mangled fixture would present as a
        /// resampling verdict.
        /// </summary>
        private static void AssertSheetDecodedAsAuthored(SpriteSheet sheet)
        {
            Assert.IsTrue(sheet.View.Index.TryGetSprite("stripe", out SpriteEntry entry));
            Assert.AreEqual(SheetSize, entry.Width, "the repack must not resize the sprite's content rect");

            int stripeX = entry.X + SheetSize / 2;
            int midY = entry.Y + SheetSize / 2;
            Assert.AreEqual(1f, sheet.Texture.GetPixel(stripeX, midY).a, 1e-3f,
                "the stripe column must be opaque after the PNG round-trip, the row flip and the repack.");
            Assert.AreEqual(0f, sheet.Texture.GetPixel(stripeX - 1, midY).a, 1e-3f,
                "the stripe must be exactly ONE texel wide — its left neighbour must be transparent.");
            Assert.AreEqual(0f, sheet.Texture.GetPixel(stripeX + 1, midY).a, 1e-3f,
                "the stripe must be exactly ONE texel wide — its right neighbour must be transparent.");
        }

        // ── render + measure ──────────────────────────────────────────────────────────────────────────

        private static LabelPaint LabelPaintWhite() => new LabelPaint
        {
            TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
            HaloColor = default, HaloWidthPx = 0f, HaloBlurPx = 0f,
        };

        /// <summary>
        /// Renders one icon and returns the X centroid of its ink, in device px, weighted by per-pixel
        /// darkness against the white background. A centroid is deliberately preferred over an ink MASS or a
        /// measured WIDTH: it is normalised, so a monotonic transfer curve on the readback (sRGB vs linear)
        /// cannot move it the way it moves a raw sum, and it states the property under test directly — where
        /// the icon's content actually sits.
        /// </summary>
        private static float RenderAndMeasureInkCentroidX(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName)
        {
            byte[] px = RenderIcon(sheet, glyphAtlas, quad, iconName, LabelPaint.Default, Color.white, out int width);

            double weighted = 0.0, total = 0.0;
            for (int i = 0, p = 0; i + 3 < px.Length; i += 4, p++)
            {
                double ink = (255.0 - px[i]) / 255.0; // black ink on white
                if (ink <= 0.004) continue;           // ignore readback noise
                weighted += ink * (p % width);
                total += ink;
            }

            Assert.Greater(total, 1.0, "the icon must actually have drawn — no ink found in the frame.");
            return (float)(weighted / total);
        }

        /// <summary>
        /// Renders the two-bar fixture and returns the device-px distance between the two bars' ink
        /// centroids. Each bar's rendered profile is symmetric about the bar's own centre, and a monotone
        /// transfer curve on the readback maps a symmetric profile to a symmetric profile — so both centroids
        /// (and therefore the separation) are independent of whether the framebuffer is gamma-encoded. The
        /// split point is the midpoint of the ink's total extent, which sits in the wide transparent gutter
        /// between the bars.
        /// </summary>
        private static float RenderAndMeasureBarSeparationPx(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName)
        {
            byte[] px = RenderIcon(sheet, glyphAtlas, quad, iconName, LabelPaint.Default, Color.white, out int width);

            var column = new double[width];
            for (int i = 0, p = 0; i + 3 < px.Length; i += 4, p++)
            {
                double ink = (255.0 - px[i]) / 255.0; // black ink on white
                if (ink <= 0.004) continue;           // ignore readback noise
                column[p % width] += ink;
            }

            int first = -1, last = -1;
            for (int x = 0; x < width; x++)
            {
                if (column[x] <= 0.0) continue;
                if (first < 0) first = x;
                last = x;
            }
            Assert.GreaterOrEqual(first, 0, "the icon must actually have drawn — no ink found in the frame.");

            int split = (first + last) / 2;
            Assert.AreEqual(0.0, column[split], 1e-9,
                "precondition: the split column must fall in the transparent gutter BETWEEN the two bars — " +
                "ink there means the bars merged and their centroids are no longer separable.");

            double leftWeighted = 0.0, leftTotal = 0.0, rightWeighted = 0.0, rightTotal = 0.0;
            for (int x = 0; x < width; x++)
            {
                if (column[x] <= 0.0) continue;
                if (x < split) { leftWeighted += column[x] * x; leftTotal += column[x]; }
                else { rightWeighted += column[x] * x; rightTotal += column[x]; }
            }

            Assert.Greater(leftTotal, 1.0, "the LEFT probe bar must have drawn.");
            Assert.Greater(rightTotal, 1.0, "the RIGHT probe bar must have drawn.");
            return (float)(rightWeighted / rightTotal - leftWeighted / leftTotal);
        }

        /// <summary>
        /// Drives ONE icon through the real LabelPlacementSystem → Map/Symbol/IconWorld path and returns the
        /// raw RGBA32 readback. The readback is the vertical mirror of on-screen (Unity's render-to-texture
        /// Y-flip), which is irrelevant to every measurement here — all of them are horizontal.
        /// </summary>
        private static byte[] RenderIcon(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName,
            LabelPaint paint, Color background, out int width)
        {
            var camGo = new GameObject("IconResampling_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = background;

            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));

            // The sweep converts a logical-px icon-offset into an exact DEVICE-px phase, which only holds at
            // dpr 1. Assert it rather than assume it: a fixture default of 2 would halve every phase step and
            // quietly weaken the tooth into a sweep of half a pixel.
            Assert.AreEqual(1.0, mapCamera.DevicePixelRatio, 1e-9,
                "this fixture converts logical px to device px 1:1 — it requires dpr 1.");

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(
                    new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // A realistic containing tile keeps the world-anchored bake float32-safe (TileKey=0 is ~2e7 m
            // away) — the same note SymbolIconRenderSnapshotTests carries.
            long tileKey = TestTileKeys.PackedContaining(
                new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            var labels = new List<LabelInstance>
            {
                new LabelInstance
                {
                    AnchorRender = frame.SceneOriginRender,
                    // 0: the collision box is inert here (one label, AllowOverlap) — only Quads[0], the
                    // padded quad, reaches the framebuffer, and that is what every measurement reads.
                    Layout = IconQuadLayout.ToLayoutResult(quad, skirtPx: 0f),
                    Kind = LabelKind.Icon,
                    Paint = paint,
                    TextSizePx = TextQuadLayout.OneEm, // scale 1 — the real StyledSymbolTileBuilder icon path
                    IconImage = iconName,
                    AllowOverlap = true,
                    SortKey = 0f,
                    FeatureIndex = 0,
                    TileKey = tileKey,
                },
            };

            var system = new LabelPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // The collision verdict is harvested one Tick late (§2.6) — hence the duplicate tick.
                system.Tick(in frame, plan.Build(labels), glyphAtlas, deltaTime: float.PositiveInfinity,
                    spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(labels), glyphAtlas, deltaTime: float.PositiveInfinity,
                    spriteTexture: sheet.Texture);
                Assert.AreEqual(1, system.LastQuadCount, "the single icon quad must place (not culled).");

                snap.Render(uCam);
                if (snap.IsAllBlack())
                    Assert.Inconclusive("render is all-black — no GPU context in this batch session " +
                                        "(see SnapshotRenderer.IsAllBlack).");

                width = snap.Width;
                return (byte[])snap.RawPixels.Clone();
            }
            finally
            {
                snap.Dispose();
                system.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
