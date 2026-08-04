// Unity-only: render tests requiring GPU context (SnapshotRenderer / UnityEngine).
// NOT included in Tools/core-tests/core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geometry;
// Alias, not a plain `using`: the namespace segment `Rendering` would otherwise collide with a bare
// UnityEngine type in lookup — the CS0118 trap this repo names in its conventions.
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Acceptance teeth for the line analytical-AA rebuild (strict one-device-pixel straddle centred on the
    /// styled edge). Every measurement here is an ALPHA-WEIGHTED coverage integral recovered from the
    /// rendered pixels — never a thresholded non-background pixel count, which is what made the earlier
    /// AA work flake.
    ///
    /// <para>Camera and background mirror <see cref="LineSnapshotTests"/>: top-down ortho at (0, 200, 0),
    /// orthographicSize 70, 512×512 ⇒ metresPerPixel = 2·70/512 = 0.2734375 exactly, so world Z maps to the
    /// image row as <c>row = 256 + z / metresPerPixel</c> with no rounding slack. The horizontal fixtures sit
    /// a QUARTER pixel off that mapping (<see cref="QuarterPixelOffsetM"/>) so no ribbon edge ever lands on a
    /// pixel centre: a rasterizer tie would make the hard-edge measurement ambiguous and would let an
    /// injected half-pixel geometry pad hide inside the tie.</para>
    ///
    /// <para>The project renders in LINEAR colour space, so the 8-bit snapshot bytes are sRGB-ENCODED
    /// composites. Coverage is recovered by decoding to linear first and projecting onto the
    /// background→plateau colour axis; skipping the decode would bend the alpha ramp and corrupt every
    /// integral.</para>
    ///
    /// <para>GPU-context guard: an all-black render plus an all-black blank control ⇒ Inconclusive, never
    /// Fail (same contract as every other snapshot fixture).</para>
    /// </summary>
    [TestFixture]
    public class LineAaSnapshotTests
    {
        private const int   SnapW     = 512;
        private const int   SnapH     = 512;
        private const float OrthoSize = 70f;
        private const float CamY      = 200f;

        // Background: the shared dark slate used by every snapshot fixture.
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);

        /// <summary>World metres per screen pixel for this camera/resolution — 0.2734375 exactly.</summary>
        private const float MetresPerPx = 2f * OrthoSize / SnapH;

        /// <summary>
        /// Quarter-pixel world offset applied to every horizontal fixture's centreline. With it, the pixel
        /// centres on the two flanks sit at radial distances {0.25, 1.25, 2.25, …} and {0.75, 1.75, 2.75, …},
        /// so an integer styled half-width never coincides with a sample point and a ±0.5 px change in the
        /// silhouette is always resolvable.
        /// </summary>
        private const float QuarterPixelOffsetM = 0.25f * MetresPerPx;

        /// <summary>Image row (continuous, bottom-up) of a horizontal fixture's centreline.</summary>
        private const float CentreRowF = 0.5f * SnapH + 0.25f;

        /// <summary>Column sampled by every perpendicular cut through a horizontal fixture.</summary>
        private const int CutColumn = SnapW / 2;

        private static readonly bool LinearColorSpace =
            QualitySettings.activeColorSpace == ColorSpace.Linear;

        // ─── Scene construction ─────────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LineAaSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSize;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;

            // The frame constant the line shader converts a PIXEL width with. Production pushes it from
            // MapCamera.SyncToCamera, measured off that camera; this fixture hand-builds a UnityEngine.Camera
            // with no MapCamera, so it must push the equivalent for ITS camera — exactly MetresPerPx, already
            // derived from OrthoSize and the snapshot height above, and what 2*d*tan(fov/2)/H degenerates to
            // under ortho. Any NEW fixture that hand-builds a camera has to do this too, and the failure is
            // QUIET: a 0 here renders every styled width as the same 1 px hairline (measured), not a blank
            // frame. PixelWidthBand_StillRenders_WhenTheFrameConstantIsUnset pins that fallback.
            Shader.SetGlobalFloat(
                ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel, MetresPerPx);
            return (go, camera);
        }

        /// <summary>
        /// Build a polyline with the live <c>Map/Line</c> material at default (AA-on) state. Only the
        /// properties this fixture reasons about are set; everything else keeps the shader's declared
        /// default, which is the inert value for _Blur / _GapWidth / _LineOffset / _LineTranslate / dashes.
        /// </summary>
        private static (GameObject go, Material mat) BuildLine(
            IReadOnlyList<double2> points, float width, bool widthIsPixels, Color color,
            JoinType join = JoinType.Miter, CapType cap = CapType.Butt, int renderQueue = -1)
        {
            var mesh = SyntheticLineMesh.BuildFromPoints(points, join, cap);
            Assert.IsNotNull(mesh, "SyntheticLineMesh produced no mesh for the requested polyline.");

            var go = new GameObject("LineAaFixture");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var shader = Shader.Find("Map/Line");
            Assert.IsNotNull(shader, "Map/Line shader must be present — this fixture measures ITS coverage.");

            var mat = new Material(shader) { name = "LineAaFixtureMat" };
            mat.SetFloat("_Width",         width);
            mat.SetFloat("_WidthIsPixels", widthIsPixels ? 1f : 0f);
            mat.SetColor("_BaseColor",     color);
            mat.SetFloat("_Opacity",       1f);
            if (renderQueue >= 0) mat.renderQueue = renderQueue;

            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return (go, mat);
        }

        /// <summary>A horizontal fixture centred on <see cref="CentreRowF"/>, spanning the frame in X.</summary>
        private static (GameObject go, Material mat) BuildHorizontalLine(
            float width, bool widthIsPixels, Color color, CapType cap = CapType.Butt, int renderQueue = -1)
            => BuildHorizontalLineAtZ(QuarterPixelOffsetM, width, widthIsPixels, color, cap, renderQueue);

        /// <summary>A horizontal fixture at an arbitrary world Z, spanning the frame in X.</summary>
        private static (GameObject go, Material mat) BuildHorizontalLineAtZ(
            double zMetres, float width, bool widthIsPixels, Color color,
            CapType cap = CapType.Butt, int renderQueue = -1)
        {
            var pts = new List<double2>
            {
                new double2(-40.0, zMetres),
                new double2( 40.0, zMetres),
            };
            return BuildLine(pts, width, widthIsPixels, color, JoinType.Miter, cap, renderQueue);
        }

        /// <summary>
        /// World Z placing a horizontal centreline at a chosen distance from a pixel CENTRE.
        ///
        /// <para>Pixel row <c>r</c> has its centre at continuous row <c>r + 0.5</c> — this fixture's own
        /// established convention: with the centreline at 256.25 the flank centres sit at radial distances
        /// {0.25, 1.25, …} and {0.75, 1.75, …} (see <see cref="QuarterPixelOffsetM"/>), which only holds if
        /// the centres are the half-integers. So the centreline goes at <c>row + 0.5 + phase</c>, and
        /// <paramref name="phaseFromCentre"/> IS the distance to the nearest sample.</para>
        ///
        /// <para>Parametrising on that distance rather than on "row + p" is deliberate: it makes the fixture
        /// independent of which of <c>p = 0</c> / <c>p = 0.5</c> is the equidistant tie, a question the plan
        /// and its challenge answered in opposite directions. <c>phaseFromCentre = 0</c> is the centreline
        /// landing exactly ON a sample (unambiguous); <c>0.5</c> is the tie and is never used.</para>
        /// </summary>
        private static double ZForPhase(int pixelRow, double phaseFromCentre)
            => (pixelRow + 0.5 + phaseFromCentre - SnapH * 0.5) * MetresPerPx;

        /// <summary>
        /// Tear down a fixture built by <see cref="BuildLine"/>. The <see cref="Mesh"/> is a single-owner GPU
        /// resource and this fixture is its owner, so it is destroyed here alongside the GameObject and the
        /// material — leaving it to be collected would leak one Mesh per fixture.
        /// </summary>
        private static void DestroyFixture(GameObject go, Material mat)
        {
            if (go != null)
            {
                var filter = go.GetComponent<MeshFilter>();
                if (filter != null) Object.DestroyImmediate(filter.sharedMesh);
                Object.DestroyImmediate(go);
            }
            if (mat != null) Object.DestroyImmediate(mat);
        }

        // ─── Guards ─────────────────────────────────────────────────────────────────────────────

        private static SnapshotRenderer RenderBlank()
        {
            var (go, cam) = BuildCamera();
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try { snap.Render(cam); }
            finally { Object.DestroyImmediate(go); }
            return snap;
        }

        /// <summary>
        /// The no-GPU guard: an all-black render is only inconclusive when a blank control is ALSO all-black.
        /// </summary>
        private static void EnsureGpuContext(SnapshotRenderer snap)
        {
            if (!snap.IsAllBlack()) return;
            using var blank = RenderBlank();
            if (blank.IsAllBlack())
                Assert.Inconclusive(
                    "Both the line render and the blank control are all-black: no GPU context in EditMode " +
                    "batchmode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
        }

        /// <summary>
        /// Every AA-ON tooth calls this first, so the suite can never be greened by turning the AA keyword
        /// off. Until A3 declares <c>_EDGE_ANTIALIASING_OFF</c> this reads <c>false</c> on an undeclared
        /// keyword — valid now, load-bearing once the keyword exists.
        /// </summary>
        private static void AssertAaKeywordClear(Material mat)
            => Assert.IsFalse(mat.IsKeywordEnabled("_EDGE_ANTIALIASING_OFF"),
                "This tooth measures the AA-ON build; _EDGE_ANTIALIASING_OFF must not be set on the material.");

        // ─── Colour / coverage sampling ─────────────────────────────────────────────────────────

        private static float ToLinear(byte encoded)
        {
            float s = encoded / 255f;
            if (!LinearColorSpace) return s;
            return s <= 0.04045f ? s / 12.92f : math.pow((s + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>Linear-space RGB at a pixel. <paramref name="row"/> is bottom-up (SnapshotRenderer's
        /// native convention); out-of-range coordinates clamp to the border.</summary>
        private static float3 SampleLinear(byte[] pixels, int column, int row)
        {
            column = math.clamp(column, 0, SnapW - 1);
            row    = math.clamp(row,    0, SnapH - 1);
            int i  = (row * SnapW + column) * 4;
            return new float3(ToLinear(pixels[i]), ToLinear(pixels[i + 1]), ToLinear(pixels[i + 2]));
        }

        /// <summary>Mean linear colour over a (2·radius+1)² box — used to read the background and the
        /// interior plateau without picking up single-pixel noise.</summary>
        private static float3 SampleLinearBox(byte[] pixels, int column, int row, int radius)
        {
            float3 sum = float3.zero;
            int    n   = 0;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                sum += SampleLinear(pixels, column + dx, row + dy);
                n++;
            }
            return sum / n;
        }

        /// <summary>The frame's background, read from a corner no fixture reaches.</summary>
        private static float3 BackgroundLinear(byte[] pixels) => SampleLinearBox(pixels, 8, 8, 2);

        /// <summary>
        /// Alpha-weighted coverage at one pixel: the composite's position on the background→plateau colour
        /// axis, in linear space. Exactly the ribbon's rendered alpha when the plateau reference is a
        /// fully-covered pixel of the same material.
        /// </summary>
        private static float CoverageAt(
            byte[] pixels, int column, int row, float3 background, float3 plateau)
        {
            float3 axis  = plateau - background;
            float  denom = math.dot(axis, axis);
            if (denom < 1e-9f) return 0f;
            return math.saturate(math.dot(SampleLinear(pixels, column, row) - background, axis) / denom);
        }

        /// <summary>The most saturated sample on a column cut — the ribbon's fully-covered interior.</summary>
        private static float3 PlateauOnColumn(
            byte[] pixels, int column, int rowFrom, int rowTo, float3 background)
        {
            float3 best     = background;
            float  bestDist = 0f;
            for (int row = rowFrom; row <= rowTo; row++)
            {
                float3 c    = SampleLinear(pixels, column, row);
                float  dist = math.distancesq(c, background);
                if (dist > bestDist) { bestDist = dist; best = c; }
            }
            return best;
        }

        /// <summary>Per-row coverage along a perpendicular cut, background-normalised.</summary>
        private static float[] CoverageProfileOnColumn(
            byte[] pixels, int column, int rowFrom, int rowTo, float3 background, float3 plateau)
        {
            var profile = new float[rowTo - rowFrom + 1];
            for (int row = rowFrom; row <= rowTo; row++)
                profile[row - rowFrom] = CoverageAt(pixels, column, row, background, plateau);
            return profile;
        }

        /// <summary>Σ coverage over a cut — the ribbon's APPARENT WIDTH in pixels.</summary>
        private static float CoverageIntegral(float[] profile)
        {
            float sum = 0f;
            foreach (float c in profile) sum += c;
            return sum;
        }

        /// <summary>Fail with a readable profile when a coverage assertion trips.</summary>
        private static string FormatProfile(float[] profile, int rowFrom)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < profile.Length; i++)
                sb.Append($"[{rowFrom + i}]={profile[i]:F3} ");
            return sb.ToString();
        }

        /// <summary>Guard against measuring a ribbon that never rendered (or rendered background-coloured):
        /// every coverage figure below is meaningless if the plateau is not clearly off the background.</summary>
        private static void AssertPlateauDistinct(float3 background, float3 plateau)
            => Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"Plateau colour {plateau} is indistinguishable from background {background} — the ribbon " +
                "did not render, so no coverage measurement is meaningful.");

        // ─── T1: the outer silhouette is antialiased ────────────────────────────────────────────

        /// <summary>
        /// <b>T1.</b> A diagonal ribbon's outer silhouette carries genuine partial coverage on BOTH flanks,
        /// and the profile rises monotonically from background to the plateau. A hard triangle silhouette
        /// (no MSAA) produces only coverage 0 and 1, so this is red until the straddle ramp ships in A3.
        ///
        /// <para>The fixture is diagonal on purpose: an axis-aligned edge can land on a pixel boundary and
        /// produce a clean binary profile even WITH a working ramp, which would make the tooth pass for the
        /// wrong reason.</para>
        /// </summary>
        [Test]
        public void OuterSilhouette_IsAntialiased()
        {
            var (cameraGo, camera) = BuildCamera();
            var pts = new List<double2> { new double2(-35, -35), new double2(35, 35) };
            var (lineGo, mat) = BuildLine(pts, width: 12f, widthIsPixels: true,
                                          color: new Color(0.95f, 0.60f, 0.15f, 1f));
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-t1-diagonal.png");

                byte[] pixels = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);

                // Horizontal cut through the diagonal at its mid-height: the band is centred on column 256.
                const int cutRow = SnapH / 2;
                const int from   = SnapW / 2 - 24;
                const int to     = SnapW / 2 + 24;

                float3 plateau = float3.zero;
                float  bestDist = 0f;
                for (int column = from; column <= to; column++)
                {
                    float3 c    = SampleLinear(pixels, column, cutRow);
                    float  dist = math.distancesq(c, background);
                    if (dist > bestDist) { bestDist = dist; plateau = c; }
                }
                AssertPlateauDistinct(background, plateau);

                var profile = new float[to - from + 1];
                for (int column = from; column <= to; column++)
                    profile[column - from] = CoverageAt(pixels, column, cutRow, background, plateau);

                string dump = FormatProfile(profile, from);

                int firstFull = -1, lastFull = -1;
                for (int i = 0; i < profile.Length; i++)
                {
                    if (profile[i] < 0.95f) continue;
                    if (firstFull < 0) firstFull = i;
                    lastFull = i;
                }
                Assert.That(firstFull, Is.GreaterThanOrEqualTo(0),
                    $"The cut never reaches full coverage — the ribbon is missing or mis-placed. {dump}");

                int leftPartial = 0, rightPartial = 0;
                for (int i = 0; i < firstFull; i++)
                    if (profile[i] > 0.05f && profile[i] < 0.95f) leftPartial++;
                for (int i = lastFull + 1; i < profile.Length; i++)
                    if (profile[i] > 0.05f && profile[i] < 0.95f) rightPartial++;

                Assert.That(leftPartial, Is.GreaterThanOrEqualTo(1),
                    $"No partially-covered pixel on the leading flank: the silhouette is a hard binary edge, " +
                    $"not an antialiased one. {dump}");
                Assert.That(rightPartial, Is.GreaterThanOrEqualTo(1),
                    $"No partially-covered pixel on the trailing flank: the silhouette is a hard binary edge, " +
                    $"not an antialiased one. {dump}");

                // Monotone rise into the plateau and monotone fall out of it (noise tolerance 0.06).
                const float noise = 0.06f;
                for (int i = 0; i < firstFull; i++)
                    Assert.That(profile[i + 1], Is.GreaterThanOrEqualTo(profile[i] - noise),
                        $"Leading flank is not monotonically increasing at index {i}. {dump}");
                for (int i = lastFull; i < profile.Length - 1; i++)
                    Assert.That(profile[i + 1], Is.LessThanOrEqualTo(profile[i] + noise),
                        $"Trailing flank is not monotonically decreasing at index {i}. {dump}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── T2: a cased pair keeps a crisp internal boundary ───────────────────────────────────

        private const float CasingWidthPx = 24f;
        private const float FillWidthPx   = 10f;

        /// <summary>
        /// <b>T2.</b> Two coplanar <c>Map/Line</c> draws in painter order — a wide dark casing under a narrow
        /// light fill — must keep the fill OPAQUE across its whole styled band, with at most one blended
        /// pixel per flank at the boundary.
        ///
        /// <para>This is the tooth that rejects an INSET fade (the model that got edge AA removed): a feather
        /// that eats inward from the fill's edge lets casing colour through well inside the fill band, which
        /// is exactly what a cased road cannot tolerate. A strict straddle with an <c>a == 1</c> interior
        /// passes it.</para>
        /// </summary>
        [Test]
        public void CasedPair_InternalBoundary_StaysCrisp()
        {
            var (cameraGo, camera) = BuildCamera();
            // The casing has to be BRIGHT as well as differently-hued: this scene has only dim ambient
            // light, so a dark navy renders within ~0.02 linear of the background and no fill-vs-casing
            // measurement is separable from background noise.
            var (casingGo, casingMat) = BuildHorizontalLine(
                CasingWidthPx, widthIsPixels: true, color: new Color(0.20f, 0.45f, 0.98f, 1f),
                renderQueue: 3000);
            var (fillGo, fillMat) = BuildHorizontalLine(
                FillWidthPx, widthIsPixels: true, color: new Color(0.98f, 0.82f, 0.25f, 1f),
                renderQueue: 3001);
            AssertAaKeywordClear(casingMat);
            AssertAaKeywordClear(fillMat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-t2-cased-pair.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);

                const float fillHalfPx   = FillWidthPx   * 0.5f;   // 5 px
                const float casingHalfPx = CasingWidthPx * 0.5f;   // 12 px

                // References: the fill's own centre, and the casing well outside the fill band.
                int centreRow = (int)CentreRowF;
                float3 fillColour   = SampleLinearBox(pixels, CutColumn, centreRow, 1);
                float3 casingColour = SampleLinearBox(pixels, CutColumn, centreRow + 8, 0);
                AssertPlateauDistinct(background, fillColour);
                AssertPlateauDistinct(background, casingColour);
                Assert.That(math.distance(fillColour, casingColour), Is.GreaterThan(0.05f),
                    $"Fill {fillColour} and casing {casingColour} must be separable colours for this tooth " +
                    "to mean anything — the painter-order draw did not produce two distinct bands.");

                float3 axis      = casingColour - fillColour;
                float  axisDenom = math.dot(axis, axis);

                float CasingFraction(int row)
                    => math.saturate(math.dot(SampleLinear(pixels, CutColumn, row) - fillColour, axis) / axisDenom);

                // (b) Zero casing contribution strictly inside the fill band. "Strictly inside" = at least a
                //     full pixel in from the styled edge, which the straddle never touches.
                var interiorReport = new System.Text.StringBuilder();
                for (int row = centreRow - 12; row <= centreRow + 12; row++)
                {
                    float radial = math.abs(row + 0.5f - CentreRowF);
                    if (radial > fillHalfPx - 1f) continue;
                    float casingFraction = CasingFraction(row);
                    interiorReport.Append($"[row {row}, d={radial:F2}]={casingFraction:F3} ");
                    Assert.That(casingFraction, Is.LessThanOrEqualTo(0.05f),
                        $"Casing colour bleeds {casingFraction:P1} into the fill band at row {row} " +
                        $"({radial:F2} px from the fill centreline, styled half-width {fillHalfPx} px). " +
                        $"The fill interior must be fully opaque. Profile: {interiorReport}");
                }

                // (a) At most one blended pixel per flank between the fill plateau and the casing plateau.
                for (int flank = -1; flank <= 1; flank += 2)
                {
                    int blended = 0;
                    var flankReport = new System.Text.StringBuilder();
                    // Walk out to the casing's own edge; the guard then trims the last few steps, which is
                    // what keeps the measurement 1 px clear of the casing↔background silhouette.
                    for (int step = 0; step <= 14; step++)
                    {
                        int   row    = centreRow + flank * step;
                        float radial = math.abs(row + 0.5f - CentreRowF);
                        if (radial > casingHalfPx - 1f) continue;
                        float casingFraction = CasingFraction(row);
                        flankReport.Append($"[row {row}, d={radial:F2}]={casingFraction:F3} ");
                        if (casingFraction > 0.05f && casingFraction < 0.95f) blended++;
                    }
                    Assert.That(blended, Is.LessThanOrEqualTo(1),
                        $"{blended} blended pixels on the {(flank < 0 ? "lower" : "upper")} fill↔casing " +
                        $"boundary; a one-pixel straddle can produce at most one. Profile: {flankReport}");
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(fillGo,   fillMat);
                DestroyFixture(casingGo, casingMat);
            }
        }

        // ─── T2b: apparent width is unchanged ───────────────────────────────────────────────────

        private const float ApparentWidthPx    = 6f;
        private const float ApparentWidthTolPx = 0.5f;

        /// <summary>
        /// <b>T2b.</b> The coverage integral across a perpendicular cut equals the STYLED width in pixels.
        /// The straddle's 50 % contour must sit on the styled edge, so adding AA changes the apparent width
        /// by nothing at all.
        ///
        /// <para>The <c>_WidthIsPixels = false</c> case is the one that catches the pad-unit bug: in
        /// world-metre mode the shader's <c>pxToWorld</c> is a literal 1.0 (a unit-conversion factor, not a
        /// metres-per-pixel scale), so a pad written as <c>0.5 · pxToWorld</c> pads by half a METRE — about
        /// 1.8 px per side at this camera — while the pixel-width case still looks correct.</para>
        /// </summary>
        [TestCase(true,  TestName = "ApparentWidth_Unchanged_MatchesStyledPixels_PixelWidth")]
        [TestCase(false, TestName = "ApparentWidth_Unchanged_MatchesStyledPixels_WorldWidth")]
        public void ApparentWidth_Unchanged_MatchesStyledPixels(bool widthIsPixels)
        {
            var (cameraGo, camera) = BuildCamera();
            float width = widthIsPixels ? ApparentWidthPx : ApparentWidthPx * MetresPerPx;
            var (lineGo, mat) = BuildHorizontalLine(width, widthIsPixels,
                                                    new Color(0.95f, 0.60f, 0.15f, 1f));
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng(widthIsPixels ? "line-aa-t2b-px-width.png" : "line-aa-t2b-world-width.png");

                byte[] pixels = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);

                int rowFrom = (int)CentreRowF - 16;
                int rowTo   = (int)CentreRowF + 16;
                float3 plateau = PlateauOnColumn(pixels, CutColumn, rowFrom, rowTo, background);
                AssertPlateauDistinct(background, plateau);

                var   profile  = CoverageProfileOnColumn(pixels, CutColumn, rowFrom, rowTo, background, plateau);
                float measured = CoverageIntegral(profile);

                TestContext.WriteLine(
                    $"apparent width ({(widthIsPixels ? "pixel" : "world-metre")} width): " +
                    $"{measured:F3} px, styled {ApparentWidthPx:F1} px");

                Assert.That(measured,
                    Is.EqualTo(ApparentWidthPx).Within(ApparentWidthTolPx),
                    $"Coverage integral across the cut is {measured:F3} px but the styled width is " +
                    $"{ApparentWidthPx:F1} px ({(widthIsPixels ? "_WidthIsPixels=1" : "_WidthIsPixels=0")}). " +
                    $"AA must not change apparent width. Profile: {FormatProfile(profile, rowFrom)}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── T3a: no interior seam at a join ────────────────────────────────────────────────────

        private const float JoinWidthPx = 24f;

        /// <summary>
        /// <b>T3a.</b> No interior edge of the ribbon-plus-fan seams at a join, for any join type — at
        /// the radii where the join geometry is the only thing drawing. Nearer the corner the two ribbon
        /// quads composite over the fan at <c>a == 1</c>, so a seam there is not observable at all.
        ///
        /// <para>What this gates: <b>the coverage ramp keys on the C0-continuous interpolated
        /// <c>|side|</c> varying</b>, so every internal edge (quad↔fan, fan↔fan) is invisible. It does NOT
        /// gate the "coverage derived from per-triangle edge distance" alternative — that needs
        /// <c>SV_Barycentrics</c> (SM 6.1) and the line's ForwardLit pass is <c>#pragma target 2.0</c>, so
        /// that wrong implementation is unreachable in this shader. The reachable way to break join
        /// continuity is a <c>|side|</c> discontinuity or dip along an interior edge, which is what a
        /// manufactured-flank-sign defect produces and what this measurement catches.</para>
        ///
        /// <para>Measured over an ANNULUS around the corner, from ⅓ to ⅔ of the half-width. The inner
        /// radius is not enough on its own: right at the corner the two ribbon quads still overlap the join
        /// geometry, and a quad drawn at <c>a == 1</c> composites over any seam the fan renders underneath
        /// it. The outer radius is where the fan is the only thing drawing, and is also the binding
        /// constraint on the corner angle — a bevel's chord stands off at only half-width·cos(θ/2), which
        /// for a 90° corner (0.71·half-width) would fall INSIDE the probe. Hence the 60° corner, whose
        /// bevel chord sits at 0.87·half-width, outside the annulus for all three join types. No point in
        /// the annulus is near enough to the silhouette for the straddle ramp to legitimately darken it.</para>
        ///
        /// <para><b>The 60° corner is NECESSARY, not an arbitrary/incidental choice — do not move this
        /// fixture to 90°.</b> This bites harder after the join-side-correction stage: the chamfer/arc now
        /// really do sit on the convex side (the actual silhouette), so their standoff from the corner is
        /// exactly the margin this test depends on. At a 90° corner the corrected chamfer chord would sit
        /// at <c>0.7071·halfWidthPx = 8.49 px</c> against an annulus reaching <c>(2/3)·halfWidthPx = 8 px</c>
        /// — a margin of only ~0.5 px, not a safe one to probe blind.</para>
        /// </summary>
        [TestCase(JoinType.Miter)]
        [TestCase(JoinType.Bevel)]
        [TestCase(JoinType.Round)]
        public void Join_NoInteriorSeam(JoinType join)
        {
            var (cameraGo, camera) = BuildCamera();
            // A 60° corner at the world origin. Miter factor 1/cos30° = 1.155 stays inside the default
            // miter limit of 2, so the Miter case really renders a miter and not a bevel fallback.
            var pts = new List<double2>
            {
                new double2(-30,  0),
                new double2(  0,  0),
                new double2( 15, 25.980762113533),   // 30 m at 60° from +x
            };
            var (lineGo, mat) = BuildLine(pts, JoinWidthPx, widthIsPixels: true,
                                          color: new Color(0.95f, 0.60f, 0.15f, 1f), join: join);
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng($"line-aa-t3a-join-{join}.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);

                // Plateau reference: deep inside the horizontal arm, far from both the join and the cap.
                float3 plateau = SampleLinearBox(pixels, 200, SnapH / 2, 2);
                AssertPlateauDistinct(background, plateau);

                const float joinColumn  = SnapW / 2f;        // world x = 0
                const float joinRow     = SnapH / 2f;        // world z = 0
                const float halfWidthPx = JoinWidthPx / 2f;  // 12 px

                const int samples = 180;
                float worstCoverage = 2f;
                float worstAngleDeg = 0f;
                float worstRadius   = 0f;
                for (float radius = halfWidthPx / 3f; radius <= 2f * halfWidthPx / 3f + 1e-3f; radius += 1f)
                for (int k = 0; k < samples; k++)
                {
                    float angle  = (float)(2.0 * math.PI_DBL * k / samples);
                    int   column = (int)math.round(joinColumn + radius * math.cos(angle));
                    int   row    = (int)math.round(joinRow    + radius * math.sin(angle));
                    float c      = CoverageAt(pixels, column, row, background, plateau);
                    if (c < worstCoverage)
                    {
                        worstCoverage = c;
                        worstAngleDeg = math.degrees(angle);
                        worstRadius   = radius;
                    }
                }

                TestContext.WriteLine(
                    $"{join} join: worst interior coverage {worstCoverage:F3} at {worstAngleDeg:F1}°, " +
                    $"radius {worstRadius:F1} px");

                Assert.That(worstCoverage, Is.GreaterThanOrEqualTo(0.92f),
                    $"{join} join seams: coverage drops to {worstCoverage:F3} at {worstAngleDeg:F1}°, " +
                    $"radius {worstRadius:F1} px from the corner — deep interior for a {JoinWidthPx:F0} px " +
                    $"ribbon (half-width {halfWidthPx:F0} px). An interior edge of the ribbon-plus-fan is " +
                    "visible, so the coverage ramp is not keying on a C0-continuous |side|.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── Round caps: they must actually reach the screen, and be round ──────────────────────

        private const float CapWidthPx     = 40f;                  // half-width 20 px — big enough to resolve
        private const float CapHalfLengthM = 100f * MetresPerPx;   // ±100 px from frame centre, exactly
        private const float CapBlurPx      = 3f;

        /// <summary>A round-capped horizontal line centred in frame: cap centres land on image columns
        /// 156 and 356 (plus <paramref name="columnOffsetM"/>), on the centreline row <see cref="SnapH"/>/2.
        /// </summary>
        /// <param name="columnOffsetM">World-X shift of the whole line. <see cref="QuarterPixelOffsetM"/>
        /// takes the arc's 0° chord — the one facing −X, the only axis-aligned chord on the cap — off the
        /// pixel boundary it otherwise lands on.</param>
        private static (GameObject go, Material mat) BuildRoundCappedLine(float columnOffsetM = 0f)
        {
            var pts = new List<double2>
            {
                new double2(-CapHalfLengthM + columnOffsetM, 0.0),
                new double2( CapHalfLengthM + columnOffsetM, 0.0),
            };
            return BuildLine(pts, CapWidthPx, widthIsPixels: true,
                             color: new Color(0.95f, 0.60f, 0.15f, 1f),
                             join: JoinType.Miter, cap: CapType.Round);
        }

        /// <summary>
        /// Alpha-weighted extent of a cap PAST its endpoint, in pixels, along one image row. Summing coverage
        /// outward from the endpoint column measures how far the silhouette reaches without ever thresholding
        /// a pixel as "lit".
        /// </summary>
        private static float CapExtentPastEndpointPx(
            byte[] pixels, int capColumn, int outward, int row, float3 background, float3 plateau)
        {
            float extent = 0f;
            for (int step = 0; step < 32; step++)
            {
                int column = outward > 0 ? capColumn + step : capColumn - 1 - step;
                extent += CoverageAt(pixels, column, row, background, plateau);
            }
            return extent;
        }

        /// <summary>
        /// A round cap must reach the screen at all, and its silhouette must be an ARC.
        ///
        /// <para>The cap fan is built around a pivot vertex carrying <c>extrudeN == 0</c> (it has to stay on
        /// the centreline). The extrusion helper guarded the divide by that length but then fed the zero
        /// vector to <c>normalize()</c>, which is NaN — and a NaN position discards every triangle referencing
        /// the vertex, which is all of them. The whole cap vanished and <c>line-cap: round</c> rendered
        /// pixel-identical to <c>butt</c>, silently, for as long as the helper has existed.</para>
        ///
        /// <para>Extent alone is not enough — a SQUARE cap also reaches past the endpoint, by a constant
        /// half-width at every offset. What distinguishes an arc is that the extent SHRINKS as the sample row
        /// moves off the centreline, so this asserts both the on-axis extent (≈ the half-width) and a strict
        /// decrease across four offsets. A flat cap edge fails the second.</para>
        /// </summary>
        [Test]
        public void RoundCap_ExtendsPastEndpoint_AsAnArc()
        {
            var (cameraGo, camera) = BuildCamera();
            var (lineGo, mat)      = BuildRoundCappedLine();
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a2b-round-cap-arc.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, SnapW / 2, SnapH / 2, 2);
                AssertPlateauDistinct(background, plateau);

                const float capHalfPx = CapWidthPx * 0.5f;   // 20 px — the cap's radius
                const float centreRow = SnapH / 2f;          // the centreline sits on the row boundary

                // Rows at increasing offset from the centreline. A circle's forward reach is
                // sqrt(r² − dy²) — 19.99, 17.02, 12.64 px here; a square cap's is r at every one of them.
                //
                // The three offsets are chosen, not arbitrary. Near the APEX the arc is almost flat (dy 0.5
                // and 6.5 differ by 1.08 px, which whole-pixel coverage of a hard edge cannot resolve), and
                // near the RIM the chorded silhouette departs from the true circle by more than any sane
                // tolerance — the final chord runs straight from the 36° vertex (reach 11.76 px) to the butt
                // (reach 0), so at dy 18.5 the real geometry reaches 4.6 px where the circle says 7.6. These
                // three sit in the band where a circle is a good model of the chorded cap.
                int[] rows = { SnapH / 2, SnapH / 2 + 10, SnapH / 2 + 15 };

                foreach (int outward in new[] { -1, 1 })
                {
                    int    capColumn = SnapW / 2 + outward * 100;
                    string which     = outward < 0 ? "start" : "end";

                    var extents = new float[rows.Length];
                    var ideals  = new float[rows.Length];
                    var report  = new System.Text.StringBuilder();
                    for (int i = 0; i < rows.Length; i++)
                    {
                        extents[i] = CapExtentPastEndpointPx(
                            pixels, capColumn, outward, rows[i], background, plateau);
                        float dy  = rows[i] + 0.5f - centreRow;
                        ideals[i] = math.sqrt(math.max(capHalfPx * capHalfPx - dy * dy, 0f));
                        report.Append($"[dy={dy:F1}] {extents[i]:F2} px (circle {ideals[i]:F2}) ");
                    }
                    TestContext.WriteLine($"{which} cap: {report}");

                    // (a) It renders at all, at about the right radius. Zero here is the NaN-pivot bug:
                    //     the cap is absent and the line ends flush, exactly like a butt cap.
                    Assert.That(extents[0], Is.EqualTo(capHalfPx).Within(2f),
                        $"{which} cap reaches {extents[0]:F2} px past its endpoint on the centreline; a " +
                        $"round cap of half-width {capHalfPx:F0} px must reach ≈ {capHalfPx:F0} px. " +
                        $"0 means the cap is not rendering at all. Extents: {report}");

                    // (b) It is an ARC, not a flat edge. Two ways, both of which a square cap fails: the
                    //     reach tracks sqrt(r² − dy²) at every offset, and it shrinks monotonically.
                    //     Tolerance absorbs the chorded silhouette (roundSegments=4 ⇒ the apex chord sits at
                    //     r·cos18° = 19.02 px, ~1 px inside the true circle) plus whole-pixel quantisation of
                    //     a hard edge.
                    for (int i = 0; i < extents.Length; i++)
                        Assert.That(extents[i], Is.EqualTo(ideals[i]).Within(2.5f),
                            $"{which} cap reach at sample {i} is {extents[i]:F2} px but a circular cap of " +
                            $"radius {capHalfPx:F0} px reaches {ideals[i]:F2} px there. A constant reach " +
                            $"across offsets is a SQUARE cap. Extents: {report}");

                    for (int i = 1; i < extents.Length; i++)
                        Assert.That(extents[i], Is.LessThanOrEqualTo(extents[i - 1] - 1.5f),
                            $"{which} cap extent does not shrink between sample {i - 1} and {i}, so the " +
                            $"silhouette is flat, not an arc — that is a square/butt cap shape. " +
                            $"Extents: {report}");
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(lineGo, mat);
            }
        }

        /// <summary>
        /// <b>T3c, pre-AA form.</b> With <c>line-blur</c> on, a round cap's soft edge must be present at
        /// EVERY angular position around the cap's silhouette arc.
        ///
        /// <para>This probes the round-cap <c>side</c> tagging directly, through the one <c>|side|</c>-keyed
        /// mechanism that exists before the AA ramp does. The cap fan used to be seeded from the ribbon's
        /// <c>rightButt</c>/<c>rightPrev</c> vertex, which the adjoining quad needs tagged −1; the seam
        /// triangle's outer edge is a true silhouette, so it interpolated <c>side</c> +1 → −1 and passed
        /// through 0 at its midpoint. <c>smoothstep(0, feather·_Blur, 1 − |side|)</c> then read that whole arc
        /// segment as deep interior and applied NO feather — one hard-edged wedge per capped end, roughly
        /// 180°/(roundSegments+1) wide, while the rest of the cap was blurred.</para>
        ///
        /// <para>It stays in the suite after the AA ramp lands, as the guard on the pre-AA mechanism.</para>
        /// </summary>
        [Test]
        public void RoundCap_BlurFadesAllTheWayAround()
        {
            var (cameraGo, camera) = BuildCamera();
            var (lineGo, mat)      = BuildRoundCappedLine();
            mat.SetFloat("_Blur", CapBlurPx);
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-t3c-round-cap-blur.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, SnapW / 2, SnapH / 2, 2);
                AssertPlateauDistinct(background, plateau);

                const float capHalfPx = CapWidthPx * 0.5f;   // 20 px
                const float capRow    = SnapH / 2f;

                // Both capped ends: the start cap bulges toward −x, the end cap toward +x.
                foreach (int outward in new[] { -1, 1 })
                {
                    float capColumn = SnapW / 2f + outward * 100f;

                    // Stop short of ±90°, where the cap meets the ribbon quad and "inside the cap" stops
                    // being well defined.
                    for (int degrees = -85; degrees <= 85; degrees += 2)
                    {
                        float angle = math.radians(degrees);
                        float dirX  = outward * math.cos(angle);
                        float dirY  = math.sin(angle);

                        float innermost = 0f, outermost = 1f;
                        bool  softEdgeFound = false;
                        var   profile = new System.Text.StringBuilder();

                        for (float radius = capHalfPx - 8f; radius <= capHalfPx + 3f; radius += 0.5f)
                        {
                            int   column = (int)math.round(capColumn + radius * dirX);
                            int   row    = (int)math.round(capRow    + radius * dirY);
                            float c      = CoverageAt(pixels, column, row, background, plateau);

                            if (radius <= capHalfPx - 8f + 1e-3f) innermost = c;
                            outermost = c;
                            if (c > 0.15f && c < 0.85f) softEdgeFound = true;
                            profile.Append($"{radius:F1}:{c:F2} ");
                        }

                        string where = $"{(outward < 0 ? "start" : "end")} cap, {degrees}°";
                        Assert.That(innermost, Is.GreaterThanOrEqualTo(0.9f),
                            $"{where}: the probe does not start inside the cap (coverage " +
                            $"{innermost:F3} at radius {capHalfPx - 8f:F1} px). Profile: {profile}");
                        Assert.That(outermost, Is.LessThanOrEqualTo(0.1f),
                            $"{where}: the probe does not end outside the cap (coverage " +
                            $"{outermost:F3} at radius {capHalfPx + 3f:F1} px). Profile: {profile}");
                        Assert.IsTrue(softEdgeFound,
                            $"{where}: the silhouette steps straight from covered to background with no " +
                            $"partially-covered pixel, so line-blur ({CapBlurPx} px) applied NO feather here " +
                            $"while feathering the rest of the cap. The arc segment's outer edge is tagged " +
                            $"side +1 → −1, so |side| dips to 0 across it and reads as deep interior. " +
                            $"Profile: {profile}");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(lineGo, mat);
            }
        }

        /// <summary>
        /// <b>T3c, AA-ON form.</b> The straddle's fade must be present at EVERY angular position around a
        /// round cap's silhouette — at the shipped default material state, with no <c>line-blur</c>.
        ///
        /// <para>Not a duplicate of <see cref="RoundCap_BlurFadesAllTheWayAround"/>. That one only exercises
        /// a configuration production never uses: <c>line-blur</c> is opt-in and <c>liberty.json</c> sets it
        /// on zero layers, so with <c>_Blur = 0</c> its whole mechanism is a no-op branch. This one measures
        /// the DEFAULT state, and against a much tighter expression — a one-pixel ramp rather than
        /// <c>smoothstep</c> over three.</para>
        ///
        /// <para>What it pins that nothing else does: <c>aaPadWorld</c> is measured PER VERTEX, along that
        /// vertex's own <c>unitDir_WS</c>. Around a cap arc every vertex has a different across-direction, so
        /// a uniform fade all the way round is a real claim about the pad on curved geometry, not a
        /// restatement of the straight-edge teeth.</para>
        /// </summary>
        [Test]
        public void RoundCap_Silhouette_FadesAtEveryAngle()
        {
            var (cameraGo, camera) = BuildCamera();
            // Quarter-pixel shift, for the same reason the horizontal fixtures carry one. Without it the
            // arc's 0° chord (facing −X, the cap's only AXIS-ALIGNED chord) sits at 156 − r·cos18° = 136.98,
            // i.e. on a pixel boundary — and a one-pixel straddle centred on a pixel boundary puts BOTH
            // straddling pixel centres ~0.5 px out, so they read 1.00 and 0.02 and there is no partially
            // covered pixel to find. That is correct AA, not a missing fade; T1 avoids the same degeneracy
            // by using a diagonal. Shifted, that chord lands at 137.23 and reads 0.77.
            var (lineGo, mat)      = BuildRoundCappedLine(QuarterPixelOffsetM);   // _Blur at its 0 default
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-t3c-round-cap-straddle.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, SnapW / 2, SnapH / 2, 2);
                AssertPlateauDistinct(background, plateau);

                const float capHalfPx = CapWidthPx * 0.5f;   // 20 px
                const float capRow    = SnapH / 2f;

                foreach (int outward in new[] { -1, 1 })
                {
                    float capColumn = SnapW / 2f + outward * 100f + 0.25f;

                    for (int degrees = -85; degrees <= 85; degrees += 2)
                    {
                        float angle = math.radians(degrees);
                        float dirX  = outward * math.cos(angle);
                        float dirY  = math.sin(angle);

                        float innermost = 0f, outermost = 1f;
                        var   profile   = new System.Text.StringBuilder();

                        // Radial sanity sweep: ±3 px around the rim brackets the chorded silhouette, which
                        // sits between r·cos18° and r depending on the angle.
                        for (float radius = capHalfPx - 3f; radius <= capHalfPx + 3f; radius += 0.25f)
                        {
                            int   column = (int)math.round(capColumn + radius * dirX);
                            int   row    = (int)math.round(capRow    + radius * dirY);
                            float c      = CoverageAt(pixels, column, row, background, plateau);

                            if (radius <= capHalfPx - 3f + 1e-3f) innermost = c;
                            outermost = c;
                            profile.Append($"{radius:F2}:{c:F2} ");
                        }

                        // The fade itself is looked for in the NEIGHBOURHOOD of the rim, not along the ray.
                        // A ray sampled at discrete pixels can straddle a ONE-pixel ramp without landing
                        // inside it — where it crosses the rim between pixel centres it reads 1.00 then 0.04
                        // and the ramp is missed, which is sub-pixel ray placement, not a missing fade. (The
                        // blur probe does not hit this: a 3 px feather always covers several pixels.) A 5×5
                        // box around the rim always contains the ramp band if it exists anywhere nearby, and
                        // still finds nothing inside a dead arc segment — that defect is ~12.6 px of arc,
                        // far wider than the box.
                        bool fadeFound = false;
                        int  rimColumn = (int)math.round(capColumn + (capHalfPx - 0.5f) * dirX);
                        int  rimRow    = (int)math.round(capRow    + (capHalfPx - 0.5f) * dirY);
                        for (int dy = -2; dy <= 2 && !fadeFound; dy++)
                        for (int dx = -2; dx <= 2 && !fadeFound; dx++)
                        {
                            float c = CoverageAt(pixels, rimColumn + dx, rimRow + dy, background, plateau);
                            if (c > 0.05f && c < 0.95f) fadeFound = true;
                        }

                        string where = $"{(outward < 0 ? "start" : "end")} cap, {degrees}°";
                        Assert.That(innermost, Is.GreaterThanOrEqualTo(0.9f),
                            $"{where}: the probe does not start inside the cap (coverage {innermost:F3} at " +
                            $"radius {capHalfPx - 3f:F1} px). Profile: {profile}");
                        Assert.That(outermost, Is.LessThanOrEqualTo(0.1f),
                            $"{where}: the probe does not end outside the cap (coverage {outermost:F3} at " +
                            $"radius {capHalfPx + 3f:F1} px). Profile: {profile}");
                        Assert.IsTrue(fadeFound,
                            $"{where}: no partially-covered pixel anywhere within 2 px of the rim at " +
                            $"({rimColumn}, {rimRow}) — the silhouette steps straight from covered to " +
                            $"background, so the straddle ramp did not reach this part of the arc while " +
                            $"covering the rest of it. Radial profile: {profile}");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── T4: the toggle actually turns the whole mechanism off ──────────────────────────────

        /// <summary>
        /// <b>T4.</b> With <c>_EDGE_ANTIALIASING_OFF</c> set, the line reproduces the pre-AA build exactly:
        /// a BINARY coverage profile, and the same apparent width.
        ///
        /// <para>Both clauses are load-bearing, because the toggle has two halves and gating only one of
        /// them still looks half-right. Gate just the ramp and the geometry keeps its half-pixel pad: the
        /// edge is hard but the line is a pixel fatter than styled — the S70 outset artefact, which clause
        /// (ii) catches and clause (i) does not. Gate just the pad and the ramp has nowhere to land — the
        /// failed inset fade, which clause (i) catches.</para>
        ///
        /// <para>The only test in this fixture that sets the keyword. Everything else asserts the shipped
        /// AA-on state and calls <see cref="AssertAaKeywordClear"/> first.</para>
        /// </summary>
        [TestCase(true,  TestName = "AaOff_ReproducesHardEdge_PixelWidth")]
        [TestCase(false, TestName = "AaOff_ReproducesHardEdge_WorldWidth")]
        public void AaOff_ReproducesHardEdge(bool widthIsPixels)
        {
            var (cameraGo, camera) = BuildCamera();
            float width = widthIsPixels ? ApparentWidthPx : ApparentWidthPx * MetresPerPx;
            var (lineGo, mat) = BuildHorizontalLine(width, widthIsPixels,
                                                    new Color(0.95f, 0.60f, 0.15f, 1f));
            mat.EnableKeyword("_EDGE_ANTIALIASING_OFF");
            Assert.IsTrue(mat.IsKeywordEnabled("_EDGE_ANTIALIASING_OFF"),
                "The keyword must be declared by Map/Line for this tooth to mean anything — if it is not, " +
                "the material silently renders the AA-ON variant and the test asserts nothing.");

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng(widthIsPixels ? "line-aa-t4-off-px-width.png" : "line-aa-t4-off-world-width.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);

                int rowFrom = (int)CentreRowF - 16;
                int rowTo   = (int)CentreRowF + 16;
                float3 plateau = PlateauOnColumn(pixels, CutColumn, rowFrom, rowTo, background);
                AssertPlateauDistinct(background, plateau);

                var   profile  = CoverageProfileOnColumn(pixels, CutColumn, rowFrom, rowTo, background, plateau);
                float measured = CoverageIntegral(profile);
                string dump    = FormatProfile(profile, rowFrom);

                TestContext.WriteLine(
                    $"AA OFF ({(widthIsPixels ? "pixel" : "world-metre")} width): {measured:F3} px, " +
                    $"styled {ApparentWidthPx:F1} px");

                // (i) BINARY — no partially-covered pixel anywhere on the cut.
                for (int i = 0; i < profile.Length; i++)
                    Assert.That(profile[i] < 0.05f || profile[i] > 0.95f, Is.True,
                        $"Row {rowFrom + i} has intermediate coverage {profile[i]:F3} with AA off. The " +
                        $"profile must be binary — the ramp is still running. Profile: {dump}");

                // (ii) Same apparent width — so the PAD is gone, not merely un-ramped. A build that gates
                //      the ramp but keeps the pad still passes (i) and fails here.
                Assert.That(measured, Is.EqualTo(ApparentWidthPx).Within(ApparentWidthTolPx),
                    $"AA-off coverage integral is {measured:F3} px against a styled {ApparentWidthPx:F1} px " +
                    $"({(widthIsPixels ? "_WidthIsPixels=1" : "_WidthIsPixels=0")}). The geometry pad is " +
                    $"still being applied. Profile: {dump}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── T3b: same-layer junction under-coverage — RECORDED, not gated ──────────────────────

        private const float JunctionWidthPx = 24f;

        /// <summary>
        /// Is a pixel centre inside a BUTT-capped ribbon's styled band? The band is a rectangle, so the
        /// point must lie within the segment's extent as well as within a half-width of it — a
        /// distance-to-segment test would model a capsule instead and sweep in the quarter-discs beyond the
        /// endpoints, where a butt-capped ribbon draws nothing at all.
        /// </summary>
        private static bool InsideStyledBand(float2 point, float2 from, float2 to, float halfWidthPx)
        {
            float2 span = to - from;
            float  len2 = math.dot(span, span);
            if (len2 < 1e-6f) return false;

            float t = math.dot(point - from, span) / len2;
            if (t < 0f || t > 1f) return false;                       // past a butt cap
            return math.distance(point, from + t * span) <= halfWidthPx;
        }

        /// <summary>
        /// <b>T3b.</b> Two features of the SAME layer meeting at an angle, measured at their crotch — the
        /// wedge where the two silhouettes converge over background. Where both ribbons sit at their styled
        /// edge each contributes ~0.5 alpha and <c>1 − (1−0.5)(1−0.5) = 0.75</c> rather than 1. That is
        /// inherent to alpha-blended same-layer overlap and is an ACCEPTED limit (design §6.4); the only real
        /// fix is a whole-layer offscreen composite, a separate epic.
        ///
        /// <para><b>Recorded, not gated.</b> The number goes to the test log so a future regression shows up
        /// as a changed measurement; the assertion is deliberately loose enough never to flake. Do not
        /// tighten it into a threshold without deciding to fix the limit.</para>
        ///
        /// <para>Only pixels DEEP inside the union of the two styled bands are measured — a pixel on the
        /// union's own outer silhouette legitimately reads a partial value, and counting those would report
        /// working antialiasing as under-coverage.</para>
        /// </summary>
        [Test]
        public void Junction_UnderCoverage_Recorded()
        {
            var (cameraGo, camera) = BuildCamera();

            // A shallow V: two features sharing a vertex at the world origin, each 20° off +x, so their
            // inner silhouettes converge gradually over background — §6.3's junction-crotch case. A right
            // angle does not produce it: the two bands simply abut.
            const double armLengthM = 30.0;
            double2 armEndA = new double2(armLengthM * math.cos(math.radians(20.0)),
                                          armLengthM * math.sin(math.radians(20.0)));
            double2 armEndB = new double2(armLengthM * math.cos(math.radians(-20.0)),
                                          armLengthM * math.sin(math.radians(-20.0)));

            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var (goA, matA) = BuildLine(new List<double2> { new double2(0, 0), armEndA },
                                        JunctionWidthPx, widthIsPixels: true, color: color);
            var (goB, matB) = BuildLine(new List<double2> { new double2(0, 0), armEndB },
                                        JunctionWidthPx, widthIsPixels: true, color: color);
            AssertAaKeywordClear(matA);
            AssertAaKeywordClear(matB);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-t3b-junction.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);

                const float halfPx  = JunctionWidthPx * 0.5f;   // 12 px
                float       centreX = SnapW / 2f;
                float       centreY = SnapH / 2f;
                float       armPx   = (float)(armLengthM / MetresPerPx);

                var vertex = new float2(centreX, centreY);
                var endA   = new float2(centreX + armPx * (float)math.cos(math.radians(20.0)),
                                        centreY + armPx * (float)math.sin(math.radians(20.0)));
                var endB   = new float2(centreX + armPx * (float)math.cos(math.radians(-20.0)),
                                        centreY + armPx * (float)math.sin(math.radians(-20.0)));

                // Plateau: on the bisector close to the vertex, where both bands overlap solidly.
                float3 plateau = SampleLinearBox(pixels, (int)centreX + 6, (int)centreY, 2);
                AssertPlateauDistinct(background, plateau);

                bool InUnion(float2 p) => InsideStyledBand(p, vertex, endA, halfPx)
                                       || InsideStyledBand(p, vertex, endB, halfPx);

                // Deep inside = a 1.2 px disc around the centre is entirely within the union, so the ideal
                // coverage really is 1 and any shortfall is the blend, not an edge.
                bool DeepInUnion(float2 p)
                {
                    if (!InUnion(p)) return false;
                    for (int k = 0; k < 8; k++)
                    {
                        float a = (float)(2.0 * math.PI_DBL * k / 8);
                        if (!InUnion(p + 1.2f * new float2(math.cos(a), math.sin(a)))) return false;
                    }
                    return true;
                }

                float worst = 1f;
                int   worstColumn = -1, worstRow = -1, measured = 0, deficient = 0;
                for (int row = (int)centreY - 40; row <= (int)centreY + 40; row++)
                for (int column = (int)centreX - 10; column <= (int)centreX + (int)armPx; column++)
                {
                    var p = new float2(column + 0.5f, row + 0.5f);
                    if (!DeepInUnion(p)) continue;

                    measured++;
                    float c = CoverageAt(pixels, column, row, background, plateau);
                    if (c < 0.95f) deficient++;
                    if (c < worst) { worst = c; worstColumn = column; worstRow = row; }
                }

                Assert.That(measured, Is.GreaterThan(200),
                    $"Only {measured} pixels qualified as deep-inside-union — the fixture or the geometry " +
                    "model is wrong and this measurement would be vacuous.");

                TestContext.WriteLine(
                    $"T3b same-layer junction under-coverage: worst composite coverage {worst:F3} " +
                    $"(deficit {(1f - worst):P1}) at ({worstColumn}, {worstRow}); " +
                    $"{deficient} of {measured} deep-interior pixels below 0.95. " +
                    $"Design §6.3 predicts ≲25% over a ~1–3 px wedge.");

                // Loose by design — a recorded limit, not a gate.
                Assert.That(1f - worst, Is.LessThan(0.60f),
                    $"Junction crotch under-coverage is {(1f - worst):P1} at ({worstColumn}, {worstRow}), " +
                    "far beyond what two overlapping straddles produce. That is no longer the accepted " +
                    "same-layer blend limit — something is punching a hole in the junction.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(goA, matA);
                DestroyFixture(goB, matB);
            }
        }

        // ─── A6.0: the AA ramp must be one device pixel in EVERY screen direction ────────────────

        private const float DirectionWidthPx = 12f;

        /// <summary>
        /// Projection onto the background→plateau axis WITHOUT the saturate. Ratios of two lit surfaces
        /// must be taken raw: once a value clamps at 1.0 it stops carrying the shading level, and a ratio
        /// against a clamped denominator silently normalises against nothing.
        /// </summary>
        private static float RawProjection(
            byte[] pixels, int column, int row, float3 background, float3 plateau)
        {
            float3 axis  = plateau - background;
            float  denom = math.dot(axis, axis);
            if (denom < 1e-9f) return 0f;
            return math.dot(SampleLinear(pixels, column, row) - background, axis) / denom;
        }

        /// <summary>Σ coverage down one image column, alpha-weighted.</summary>
        private static float ColumnCoverageSum(
            byte[] pixels, int column, int rowFrom, int rowTo, float3 background, float3 plateau)
        {
            float sum = 0f;
            for (int row = rowFrom; row <= rowTo; row++)
                sum += CoverageAt(pixels, column, row, background, plateau);
            return sum;
        }

        /// <summary>
        /// Apparent width must not depend on which way the line runs on screen.
        ///
        /// <para>The ramp divides by a screen-space gradient of <c>side</c>. <c>fwidth</c> is
        /// <c>|ddx| + |ddy|</c> — the L1/Manhattan length — which over-reads the true (L2) length by
        /// <c>|cos θ| + |sin θ| ∈ [1, √2]</c>. Dividing by that stretches the ramp to 1.41 px on a 45°
        /// diagonal while an axis-aligned line keeps 1.00 px, so the diagonal renders both softer and
        /// THINNER: the perpendicular integral is <c>W + 1 − c</c>, losing 0.41 px of ink at 45°.</para>
        ///
        /// <para>Both lines are rendered in ONE frame with identical material state, so the comparison
        /// cannot drift on exposure or reference. The horizontal arm doubles as the control: <c>fwidth</c>
        /// is exact when the gradient is axis-aligned, so it must read the styled width both before and
        /// after the fix — if it moves, the change did more than intended.</para>
        ///
        /// <para>A vertical cut through a 45° band spans <c>√2</c> × the perpendicular width, hence the
        /// <c>√2</c> divisor. Measured in LINEAR space via <see cref="CoverageAt"/>: an sRGB-luminance read
        /// over-reads partial-coverage pixels by several percent, which is the same order as the 3.4 %
        /// effect being measured and cannot separate the two hypotheses.</para>
        /// </summary>
        [Test]
        public void ApparentWidth_IsDirectionIndependent()
        {
            var (cameraGo, camera) = BuildCamera();
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);

            // Horizontal, low in frame; and a 45° diagonal through the centre. They do not overlap: the
            // diagonal's lowest extent is row ~137 and the horizontal band ends at row ~126.
            const double horizontalRow = 120.25;
            double zHorizontal = (horizontalRow - SnapH * 0.5) * MetresPerPx;
            var (horizontalGo, horizontalMat) = BuildLine(
                new List<double2> { new double2(-40.0, zHorizontal), new double2(40.0, zHorizontal) },
                DirectionWidthPx, widthIsPixels: true, color: color);
            var (diagonalGo, diagonalMat) = BuildLine(
                new List<double2> { new double2(-30.0, -30.0), new double2(30.0, 30.0) },
                DirectionWidthPx, widthIsPixels: true, color: color);
            AssertAaKeywordClear(horizontalMat);
            AssertAaKeywordClear(diagonalMat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a60-direction.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, (int)horizontalRow, 2);
                AssertPlateauDistinct(background, plateau);

                float horizontal = ColumnCoverageSum(pixels, CutColumn, 100, 140, background, plateau);

                // The 45° band is centred on row == column. Average several columns; on a slope-1 line the
                // sub-pixel phase is the same in every column, so the spread reports sampling noise only.
                const float sqrt2 = 1.41421356f;
                float diagonalSum = 0f, worstLo = 99f, worstHi = 0f;
                int   columns = 0;
                for (int column = 240; column <= 272; column++)
                {
                    float perpendicular =
                        ColumnCoverageSum(pixels, column, column - 20, column + 20, background, plateau) / sqrt2;
                    diagonalSum += perpendicular;
                    worstLo = math.min(worstLo, perpendicular);
                    worstHi = math.max(worstHi, perpendicular);
                    columns++;
                }
                float diagonal = diagonalSum / columns;

                // Padded half-width is styled/2 + 0.5, and the perpendicular integral of the trapezoid is
                // 2H − c, so the ramp width falls straight out. This is the same measurement re-expressed
                // in the units the defect is stated in, not independent corroboration of it.
                const float paddedHalfPx = DirectionWidthPx * 0.5f + 0.5f;
                float rampPx = 2f * paddedHalfPx - diagonal;

                TestContext.WriteLine(
                    $"A6.0 apparent width — horizontal {horizontal:F3} px, 45° diagonal {diagonal:F3} px " +
                    $"(per-column {worstLo:F3}…{worstHi:F3} over {columns} columns), styled " +
                    $"{DirectionWidthPx:F1} px. Implied diagonal ramp width {rampPx:F3} px " +
                    $"(1.000 = Euclidean, 1.414 = fwidth/Manhattan).");

                Assert.That(horizontal, Is.EqualTo(DirectionWidthPx).Within(0.15f),
                    $"The AXIS-ALIGNED control reads {horizontal:F3} px against a styled " +
                    $"{DirectionWidthPx:F1} px. fwidth is exact in this direction, so this case must not " +
                    "move — if it has, the change reached further than the ramp's gradient.");

                Assert.That(diagonal, Is.EqualTo(horizontal).Within(0.15f),
                    $"A 45° line renders {diagonal:F3} px of ink where the same styled width renders " +
                    $"{horizontal:F3} px horizontally — a difference of {math.abs(diagonal - horizontal):F3} px. " +
                    "Antialiasing quality must not depend on which way a road runs on screen.");

                Assert.That(rampPx, Is.EqualTo(1f).Within(0.15f),
                    $"The coverage ramp is {rampPx:F3} device pixels wide on a 45° diagonal; it must be 1.0. " +
                    $"1.414 is the signature of dividing by fwidth (Manhattan) instead of the Euclidean " +
                    $"gradient length.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(horizontalGo);
                Object.DestroyImmediate(horizontalMat);
                Object.DestroyImmediate(diagonalGo);
                Object.DestroyImmediate(diagonalMat);
            }
        }
        // ─── A6a: the hairline strategy ─────────────────────────────────────────────────────────

        private const string HairlineHardKeyword = "_HAIRLINE_HARD";
        private const float  HairlineWidthPx     = 1f;
        private const float  ReferenceWidthPx    = 12f;
        private const int    ReferenceRow        = 90;

        /// <summary>
        /// Distances from the centreline to the nearest pixel centre. Distinct magnitudes, and the
        /// equidistant tie (0.5) is excluded — at the tie both neighbours sit exactly at the threshold and
        /// the winner is rasteriser-defined. 0 is the centreline landing exactly ON a sample, the least
        /// ambiguous phase there is, so it is included rather than avoided.
        /// </summary>
        private static readonly double[] HairlinePhases = { 0.0, 0.1, 0.2, 0.3, 0.4 };

        private static readonly int[] HairlineRows = { 160, 200, 240, 280, 320 };

        /// <summary>Per-pixel coverage in a ±6 px window around a hairline's predicted row. The window is
        /// tight on purpose: a row-arithmetic slip then fails loudly instead of quietly measuring
        /// background.</summary>
        private static float[] HairlineProfile(
            byte[] pixels, int centreRow, float3 background, float3 plateau)
        {
            var profile = new float[13];
            for (int i = 0; i < 13; i++)
                profile[i] = CoverageAt(pixels, CutColumn, centreRow - 6 + i, background, plateau);
            return profile;
        }

        private static float Peak(float[] profile)
        {
            float peak = 0f;
            foreach (float c in profile) peak = math.max(peak, c);
            return peak;
        }

        /// <summary>
        /// <b>T5.</b> Under <c>_HAIRLINE_HARD</c> a one-pixel line reaches full brightness at every sub-pixel
        /// phase. This is the maintainer's complaint encoded: the default straddle's profile at <c>W = 1</c>
        /// is a tent of half-width 1 px, so its peak is <c>1 − φ</c> — full only when a pixel centre happens
        /// to land on the centreline, and the line visibly pulses as that phase drifts under motion.
        /// </summary>
        [Test]
        public void Hairline_Hard_PeakIsFullAndPhaseInvariant()
        {
            var (cameraGo, camera) = BuildCamera();
            var built = BuildHairlineSweep(HairlineHardKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a6a-hard-phases.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                float lo = 2f, hi = 0f;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < HairlinePhases.Length; i++)
                {
                    float peak = Peak(HairlineProfile(pixels, HairlineRows[i], background, plateau));
                    report.Append($"[φ={HairlinePhases[i]:F1}] {peak:F3}  ");
                    lo = math.min(lo, peak);
                    hi = math.max(hi, peak);
                }
                TestContext.WriteLine($"A6a hard peaks: {report} (spread {hi - lo:F3})");

                Assert.That(lo, Is.GreaterThanOrEqualTo(0.95f),
                    $"A hairline must reach full brightness at every phase; the dimmest peak is {lo:F3}. " +
                    $"Peaks: {report}");
                Assert.That(hi - lo, Is.LessThanOrEqualTo(0.06f),
                    $"Peak brightness varies by {hi - lo:F3} across sub-pixel phases — that variation IS the " +
                    $"shimmer this strategy exists to remove. Peaks: {report}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>T6.</b> Under <c>_HAIRLINE_HARD</c> the hairline profile is binary, and it still carries the
        /// styled amount of ink. The energy clause is what stops the narrowing being implemented as a step on
        /// the PADDED edge instead of the styled one: that lights two pixels per cross-section rather than
        /// one, so the integral doubles and the line renders a pixel fat — the S70 outset artefact.
        /// </summary>
        [Test]
        public void Hairline_Hard_ProfileIsBinaryAndConservesEnergy()
        {
            var (cameraGo, camera) = BuildCamera();
            var built = BuildHairlineSweep(HairlineHardKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                var report = new System.Text.StringBuilder();
                for (int i = 0; i < HairlinePhases.Length; i++)
                {
                    var   profile  = HairlineProfile(pixels, HairlineRows[i], background, plateau);
                    float integral = CoverageIntegral(profile);
                    report.Append($"[φ={HairlinePhases[i]:F1}] Σ={integral:F3}  ");

                    for (int k = 0; k < profile.Length; k++)
                        Assert.That(profile[k] < 0.05f || profile[k] > 0.95f, Is.True,
                            $"φ={HairlinePhases[i]:F1}, row {HairlineRows[i] - 6 + k}: coverage " +
                            $"{profile[k]:F3} is neither on nor off. Under _HAIRLINE_HARD a 1 px line must " +
                            $"be a step. Profile: {FormatProfile(profile, HairlineRows[i] - 6)}");

                    Assert.That(integral, Is.EqualTo(HairlineWidthPx).Within(0.35f),
                        $"φ={HairlinePhases[i]:F1}: coverage integral {integral:F3} against a styled " +
                        $"{HairlineWidthPx:F1} px. Hardening must not change how much ink the line carries. " +
                        $"Profile: {FormatProfile(profile, HairlineRows[i] - 6)}");
                }
                TestContext.WriteLine($"A6a hard integrals: {report}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>T6b.</b> The threshold is real: a 6 px line is untouched by <c>_HAIRLINE_HARD</c>. Without the
        /// <c>smoothstep</c> gate the whole map would render hard-edged, which is the one way this stage
        /// could quietly undo A3.
        /// </summary>
        [Test]
        public void Hairline_Hard_LeavesWideLinesUntouched()
        {
            var (cameraGo, camera) = BuildCamera();
            var built = BuildHairlineSweep(HairlineHardKeyword, 6f);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a6a-hard-wide.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                // One representative phase is enough — this is about the threshold, not about phase.
                var   profile  = HairlineProfile(pixels, HairlineRows[2], background, plateau);
                float integral = CoverageIntegral(profile);
                string dump    = FormatProfile(profile, HairlineRows[2] - 6);
                TestContext.WriteLine($"A6a wide (6 px) under _HAIRLINE_HARD: Σ={integral:F3}  {dump}");

                int partial = 0;
                foreach (float c in profile) if (c > 0.05f && c < 0.95f) partial++;
                Assert.That(partial, Is.GreaterThanOrEqualTo(2),
                    $"A 6 px line under _HAIRLINE_HARD has {partial} partially-covered pixels; it must keep " +
                    $"an antialiased edge on both flanks. The width threshold is not gating. Profile: {dump}");
                Assert.That(integral, Is.EqualTo(6f).Within(0.5f),
                    $"6 px line reads {integral:F3} px under _HAIRLINE_HARD. Profile: {dump}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>T6c.</b> A hairline-thin casing RING hardens on both of its edges.
        ///
        /// <para><c>_HAIRLINE_HARD</c> measures the painted band, which for a hollow line is the ring, not the
        /// outer radius — so it fires on a thin ring. Hardening only the outer silhouette would leave that
        /// ring crisp outside and still shimmering inside, on the same ring, which is worse than either
        /// consistent choice. Cased roads are exactly where a thin ring occurs.</para>
        /// </summary>
        [Test]
        public void Hairline_Hard_ThinRingIsBinaryOnBothEdges()
        {
            var (cameraGo, camera) = BuildCamera();
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var (referenceGo, referenceMat) = BuildHorizontalLineAtZ(
                ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx, widthIsPixels: true, color: color);
            // Gap 20 px, width 1 px ⇒ hole radius 10 px, styled outer radius 11 px: a 1 px ring per flank.
            var (ringGo, ringMat) = BuildHorizontalLineAtZ(
                ZForPhase(SnapH / 2, 0.2), 1f, widthIsPixels: true, color: color);
            ringMat.SetFloat("_GapWidth", 20f);
            AssertAaKeywordClear(referenceMat);
            AssertAaKeywordClear(ringMat);
            // The reference strip stays on the DEFAULT strategy — see BuildHairlineSweep. A 12 px line is
            // identical under all three, and leaving the keyword off keeps the unit reference immune to the
            // very bugs this tooth hunts.
            ringMat.EnableKeyword(HairlineHardKeyword);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a6a-hard-thin-ring.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                int centreRow = SnapH / 2;
                var profile   = new float[33];
                for (int i = 0; i < profile.Length; i++)
                    profile[i] = CoverageAt(pixels, CutColumn, centreRow - 16 + i, background, plateau);
                string dump = FormatProfile(profile, centreRow - 16);
                TestContext.WriteLine($"A6a thin ring (gap 20 px, width 1 px): {dump}");

                // Binary FIRST, so a half-hardened ring reports the asymmetry it actually has rather than
                // tripping the non-vacuity guard below (an un-hardened inner edge drags the ring's lit
                // pixels under 0.95, so both would fire — but only this one names the cause). A blank render
                // is trivially binary, which is what the guard after it is for.
                for (int i = 0; i < profile.Length; i++)
                    Assert.That(profile[i] < 0.05f || profile[i] > 0.95f, Is.True,
                        $"Row {centreRow - 16 + i}: coverage {profile[i]:F3} is neither on nor off. A 1 px " +
                        $"ring under _HAIRLINE_HARD must be hard on BOTH edges — a partial value here is the " +
                        $"inner gap-hole straddle still running its default ramp. Profile: {dump}");

                int lit = 0;
                foreach (float c in profile) if (c > 0.95f) lit++;
                Assert.That(lit, Is.GreaterThanOrEqualTo(2),
                    $"The casing ring did not render on both flanks — only {lit} fully-covered pixels on " +
                    $"the cut, so this tooth would assert nothing. Profile: {dump}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(referenceGo, referenceMat);
                DestroyFixture(ringGo, ringMat);
            }
        }

        /// <summary>
        /// <b>Recorded, not gated.</b> Peak brightness and ink across the width threshold, so a POP —
        /// a line visibly jumping as it crosses the hard/ramped boundary under zoom — would show up as a
        /// step in these numbers rather than only in the maintainer's eye.
        ///
        /// <para>Continuity is expected by construction: writing the profile in device pixels as
        /// <c>c(d) = saturate((halfStyled − d)/rampPx + 0.5)</c>, its integral over d is <c>2·halfStyled</c>
        /// for ANY rampPx, so narrowing the ramp moves no ink; and rampPx itself is a <c>smoothstep</c>,
        /// which is C1 at both ends, so there is no kink where the transition band opens or closes. This
        /// records the measurement anyway — a derivation is not an observation.</para>
        /// </summary>
        [TestCase("_HAIRLINE_HARD",       TestName = "Hairline_Hard_ThresholdTransition_Recorded")]
        [TestCase("_HAIRLINE_SOLID_CORE", TestName = "Hairline_SolidCore_ThresholdTransition_Recorded")]
        public void Hairline_ThresholdTransition_Recorded(string keyword)
        {
            float[] widths = { 0.8f, 1.0f, 1.2f, 1.4f, 1.6f, 1.8f, 2.0f, 2.4f };
            var (cameraGo, camera) = BuildCamera();
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var built = new List<(GameObject, Material)>
            {
                BuildHorizontalLineAtZ(ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx,
                                       widthIsPixels: true, color: color),
            };
            // Same sub-pixel phase for every width, so only the width varies.
            for (int i = 0; i < widths.Length; i++)
                built.Add(BuildHorizontalLineAtZ(ZForPhase(140 + i * 40, 0.2), widths[i],
                                                 widthIsPixels: true, color: color));
            // Index 0 is the reference strip and stays on the DEFAULT strategy — see BuildHairlineSweep.
            foreach (var (_, mat) in built) AssertAaKeywordClear(mat);
            for (int i = 1; i < built.Count; i++) built[i].Item2.EnableKeyword(keyword);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng($"line-aa-a6-threshold-sweep-{keyword}.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                var   report   = new System.Text.StringBuilder();
                var   peaks    = new float[widths.Length];
                float worstJump = 0f;
                for (int i = 0; i < widths.Length; i++)
                {
                    var profile = HairlineProfile(pixels, 140 + i * 40, background, plateau);
                    peaks[i] = Peak(profile);
                    report.Append($"[W={widths[i]:F1}] peak={peaks[i]:F3} Σ={CoverageIntegral(profile):F3}  ");
                    if (i > 0) worstJump = math.max(worstJump, math.abs(peaks[i] - peaks[i - 1]));
                }
                TestContext.WriteLine($"{keyword} threshold sweep (φ=0.2): {report}");
                TestContext.WriteLine($"{keyword} largest peak step between adjacent widths: {worstJump:F3}");

                Assert.That(worstJump, Is.LessThan(0.35f),
                    $"Peak brightness steps by {worstJump:F3} between adjacent widths across the hard/ramped " +
                    $"threshold — that is a visible pop as a line crosses it under zoom. Sweep: {report}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        // ─── A6b: _HAIRLINE_SOLID_CORE ──────────────────────────────────────────────────────────

        private const string HairlineSolidCoreKeyword = "_HAIRLINE_SOLID_CORE";

        /// <summary>
        /// The phase sweep plus an <c>a == 1</c> reference strip, in one frame, under a strategy keyword.
        ///
        /// <para>The reference strip is not decoration. <see cref="PlateauOnColumn"/> normalises against the
        /// brightest sample on its own cut, which would make any hairline peak measurement a tautology — a
        /// dim hairline would normalise to 1.0 and every peak assertion would pass. A 12 px line is untouched
        /// by every strategy (its band is far above the threshold and above the clamp), so its interior is a
        /// valid unit reference under all three variants.</para>
        /// </summary>
        private static List<(GameObject go, Material mat)> BuildHairlineSweep(string keyword, float widthPx)
        {
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var built = new List<(GameObject, Material)>
            {
                BuildHorizontalLineAtZ(ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx,
                                       widthIsPixels: true, color: color),
            };
            for (int i = 0; i < HairlinePhases.Length; i++)
                built.Add(BuildHorizontalLineAtZ(ZForPhase(HairlineRows[i], HairlinePhases[i]), widthPx,
                                                 widthIsPixels: true, color: color));
            // The reference strip stays on the DEFAULT strategy. A 12 px line renders identically under all
            // three (its band is above every threshold and the clamp is inactive), but leaving the keyword
            // off makes the reference immune to the strategy bugs these teeth hunt: a defect that scales
            // coverage at EVERY width would otherwise scale the reference too, and the ratio would hide it.
            foreach (var (_, mat) in built) AssertAaKeywordClear(mat);
            for (int i = 1; i < built.Count; i++) built[i].Item2.EnableKeyword(keyword);
            Assert.IsTrue(built[1].Item2.IsKeywordEnabled(keyword),
                $"Map/Line must declare {keyword} for this tooth to mean anything — without the pragma the " +
                "material silently renders the default variant and the test asserts nothing.");
            return built;
        }

        /// <summary>
        /// <b>T7.</b> Under <c>_HAIRLINE_SOLID_CORE</c> a 1 px line's peak is phase-INVARIANT, and it sits at
        /// <c>W_true / W_min = 0.5</c> by construction — the clamp gives the band a solid core, the
        /// compensation scales it back down so the ink is still the styled width. The 0.5 is the design, not
        /// a dim bug: do not "fix" it by dropping the compensation, which is what T8 catches.
        /// </summary>
        [Test]
        public void Hairline_SolidCore_PeakIsPhaseInvariant()
        {
            var (cameraGo, camera) = BuildCamera();
            var built = BuildHairlineSweep(HairlineSolidCoreKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a6b-solidcore-phases.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                float lo = 2f, hi = 0f;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < HairlinePhases.Length; i++)
                {
                    float peak = Peak(HairlineProfile(pixels, HairlineRows[i], background, plateau));
                    report.Append($"[φ={HairlinePhases[i]:F1}] {peak:F3}  ");
                    lo = math.min(lo, peak); hi = math.max(hi, peak);
                }
                TestContext.WriteLine($"A6b solid-core peaks: {report} (spread {hi - lo:F3})");

                Assert.That(hi - lo, Is.LessThanOrEqualTo(0.06f),
                    $"Peak brightness varies by {hi - lo:F3} across sub-pixel phases — the clamp is not " +
                    $"giving the hairline a phase-independent core. Peaks: {report}");
                Assert.That(lo, Is.EqualTo(0.5f).Within(0.08f),
                    $"Peak is {lo:F3}; a 1 px line clamped to {2f:F0} px and compensated must read " +
                    $"W_true/W_min = 0.5. A peak near 1.0 means the compensation is missing. Peaks: {report}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>T8.</b> The clamp must not change how much ink a hairline carries. This is the tooth that
        /// makes the two halves ship together: delete the <c>hairlineScale</c> multiply and a 1 px road
        /// renders with twice the styled ink — visibly heavier than the style asked for.
        /// </summary>
        [Test]
        public void Hairline_SolidCore_ConservesEnergy()
        {
            var (cameraGo, camera) = BuildCamera();
            var built = BuildHairlineSweep(HairlineSolidCoreKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                var report = new System.Text.StringBuilder();
                for (int i = 0; i < HairlinePhases.Length; i++)
                {
                    float integral = CoverageIntegral(
                        HairlineProfile(pixels, HairlineRows[i], background, plateau));
                    report.Append($"[φ={HairlinePhases[i]:F1}] Σ={integral:F3}  ");
                    Assert.That(integral, Is.EqualTo(HairlineWidthPx).Within(0.25f),
                        $"φ={HairlinePhases[i]:F1}: coverage integral {integral:F3} against a styled " +
                        $"{HairlineWidthPx:F1} px. ≈2.0 means the band was clamped without paying the " +
                        $"widening back in alpha. Integrals: {report}");
                }
                TestContext.WriteLine($"A6b solid-core integrals: {report}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>T10.</b> Above the clamp nothing may change: a 6 px line keeps the styled ink AND a plateau at
        /// <c>a == 1</c>. The plateau clause is the one that catches an unconditionally-applied
        /// compensation, which would dim every wide line on the map.
        /// </summary>
        [Test]
        public void Hairline_SolidCore_LeavesWideLinesUntouched()
        {
            var (cameraGo, camera) = BuildCamera();
            var built = BuildHairlineSweep(HairlineSolidCoreKeyword, 6f);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a6b-solidcore-wide.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                var   profile  = HairlineProfile(pixels, HairlineRows[2], background, plateau);
                float integral = CoverageIntegral(profile);
                float peak     = Peak(profile);
                string dump    = FormatProfile(profile, HairlineRows[2] - 6);
                TestContext.WriteLine($"A6b wide (6 px): Σ={integral:F3} peak={peak:F3}  {dump}");

                Assert.That(integral, Is.EqualTo(6f).Within(0.5f),
                    $"6 px line reads {integral:F3} px under _HAIRLINE_SOLID_CORE. Profile: {dump}");
                Assert.That(peak, Is.GreaterThanOrEqualTo(0.97f),
                    $"A 6 px line's interior reads {peak:F3}, not a == 1 — the energy compensation is being " +
                    $"applied above the clamp, which dims every wide line on the map. Profile: {dump}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        // ─── A7: the keyword matrix, non-straight geometry, and the interpolation question ──────

        private const string AaOffKeyword = "_EDGE_ANTIALIASING_OFF";

        /// <summary>Reference strip (always DEFAULT state) + one test line, with explicit keywords.</summary>
        private static List<(GameObject go, Material mat)> BuildStrategyScene(
            bool aaOff, string strategyKeyword, float widthPx, int row, double phase)
        {
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var built = new List<(GameObject, Material)>
            {
                BuildHorizontalLineAtZ(ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx,
                                       widthIsPixels: true, color: color),
                BuildHorizontalLineAtZ(ZForPhase(row, phase), widthPx,
                                       widthIsPixels: true, color: color),
            };
            var mat = built[1].Item2;
            if (aaOff) mat.EnableKeyword(AaOffKeyword);
            if (strategyKeyword != null) mat.EnableKeyword(strategyKeyword);
            return built;
        }

        /// <summary>
        /// <b>A7.1 — the whole keyword matrix.</b> 3 strategies × AA on/off = 6 variants; the gate otherwise
        /// only exercises whichever combinations the fixtures happen to set, so a variant that failed to
        /// compile or read a symbol not in scope on its path would ship green. Each case names its own
        /// combination, so a failure localises to one cell rather than "the strategies are broken".
        ///
        /// <para>The three AA-OFF rows are the load-bearing ones: both strategy blocks are guarded on
        /// <c>!defined(_EDGE_ANTIALIASING_OFF)</c>, so with AA off all three must be INERT and identical —
        /// a hard edge at the styled width.</para>
        /// </summary>
        [TestCase(true,  null,                        1.0f, true,  TestName = "Matrix_AaOff_Default")]
        [TestCase(true,  "_HAIRLINE_HARD",            1.0f, true,  TestName = "Matrix_AaOff_Hard")]
        [TestCase(true,  "_HAIRLINE_SOLID_CORE",      1.0f, true,  TestName = "Matrix_AaOff_SolidCore")]
        [TestCase(false, null,                        0.8f, false, TestName = "Matrix_AaOn_Default")]
        [TestCase(false, "_HAIRLINE_HARD",            1.0f, true,  TestName = "Matrix_AaOn_Hard")]
        [TestCase(false, "_HAIRLINE_SOLID_CORE",      0.5f, false, TestName = "Matrix_AaOn_SolidCore")]
        public void Strategy_KeywordMatrix_BehavesAsSpecified(
            bool aaOff, string strategyKeyword, float expectedPeak, bool expectBinary)
        {
            const int   row   = 256;
            const float phase = 0.2f;
            var (cameraGo, camera) = BuildCamera();
            var built = BuildStrategyScene(aaOff, strategyKeyword, HairlineWidthPx, row, phase);
            string cell = $"AA {(aaOff ? "OFF" : "ON")} × {strategyKeyword ?? "Default"}";

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                var    profile  = HairlineProfile(pixels, row, background, plateau);
                float  peak     = Peak(profile);
                float  integral = CoverageIntegral(profile);
                string dump     = FormatProfile(profile, row - 6);
                TestContext.WriteLine($"[{cell}] peak={peak:F3} Σ={integral:F3}  {dump}");

                Assert.That(peak, Is.EqualTo(expectedPeak).Within(0.08f),
                    $"[{cell}] peak {peak:F3}, expected {expectedPeak:F2}. Profile: {dump}");

                bool binary = true;
                foreach (float c in profile) if (c > 0.05f && c < 0.95f) binary = false;
                Assert.That(binary, Is.EqualTo(expectBinary),
                    $"[{cell}] profile is {(binary ? "binary" : "ramped")}, expected " +
                    $"{(expectBinary ? "binary" : "ramped")}. Profile: {dump}");

                // The invariant that must hold in EVERY cell: ink equals the styled width.
                Assert.That(integral, Is.EqualTo(HairlineWidthPx).Within(0.3f),
                    $"[{cell}] ink {integral:F3} against a styled {HairlineWidthPx:F1} px. Profile: {dump}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        // ─── A7.2 — the strategies on geometry that is not a straight axis-aligned run ──────────

        private static readonly string[] StrategyKeywords =
            { null, HairlineHardKeyword, HairlineSolidCoreKeyword };

        /// <summary>
        /// <b>A7.2a.</b> A strategy's width estimate comes from <c>sideGrad</c>, so if it were
        /// direction-dependent the strategy would engage at a different styled width on a diagonal than on a
        /// horizontal — the same class of bug A6.0 fixed, one level up. Measured at 1.5 px, mid-transition,
        /// where the ramp width actually depends on the estimate.
        /// </summary>
        [TestCase(0, TestName = "Strategy_Direction_Default")]
        [TestCase(1, TestName = "Strategy_Direction_Hard")]
        [TestCase(2, TestName = "Strategy_Direction_SolidCore")]
        public void Strategy_ApparentWidth_IsDirectionIndependent(int strategyIndex)
        {
            string keyword = StrategyKeywords[strategyIndex];
            string label   = keyword ?? "Default";
            const float width = 1.5f;

            var (cameraGo, camera) = BuildCamera();
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            // A 1.5 px line has no a == 1 plateau of its own, so it needs a wide reference in-frame:
            // normalising against the thin line's own samples inflates every reading, and inflates each
            // strategy differently because their peaks differ.
            var (refGo, refMat) = BuildHorizontalLineAtZ(ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx,
                                                          widthIsPixels: true, color: color);
            var (hGo, hMat) = BuildLine(
                new List<double2> { new double2(-40.0, (120.25 - SnapH * 0.5) * MetresPerPx),
                                    new double2( 40.0, (120.25 - SnapH * 0.5) * MetresPerPx) },
                width, widthIsPixels: true, color: color);
            var (dGo, dMat) = BuildLine(
                new List<double2> { new double2(-30.0, -30.0), new double2(30.0, 30.0) },
                width, widthIsPixels: true, color: color);
            if (keyword != null) { hMat.EnableKeyword(keyword); dMat.EnableKeyword(keyword); }

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng($"line-aa-a7-direction-{label}.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                float horizontal = ColumnCoverageSum(pixels, CutColumn, 108, 134, background, plateau);
                const float sqrt2 = 1.41421356f;
                float diagonalSum = 0f; int columns = 0;
                for (int column = 240; column <= 272; column++)
                {
                    diagonalSum += ColumnCoverageSum(pixels, column, column - 20, column + 20,
                                                     background, plateau) / sqrt2;
                    columns++;
                }
                float diagonal = diagonalSum / columns;
                TestContext.WriteLine(
                    $"[{label}] 1.5 px — horizontal {horizontal:F3} px, 45° diagonal {diagonal:F3} px");

                Assert.That(diagonal, Is.EqualTo(horizontal).Within(0.2f),
                    $"[{label}] a 45° line renders {diagonal:F3} px of ink where the same styled width " +
                    $"renders {horizontal:F3} px horizontally. The strategy's width estimate is " +
                    "direction-dependent.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(refGo, refMat);
                DestroyFixture(hGo, hMat);
                DestroyFixture(dGo, dMat);
            }
        }

        /// <summary>
        /// <b>A7.2b.</b> A hairline must stay continuous through a join under every strategy. MITER is the
        /// interesting case on purpose: at a miter the extruded half-width is <c>miter·(outer+pad)</c>, so
        /// <c>1/sideGrad</c> reads LARGER there than on the straight run, which could make a width-thresholded
        /// strategy disengage right at the corner. The apparent width near vs far from the corner is logged so
        /// that over-read shows up as a number even while the continuity assertion passes.
        /// </summary>
        [TestCase(0, TestName = "Strategy_MiterJoin_Default")]
        [TestCase(1, TestName = "Strategy_MiterJoin_Hard")]
        [TestCase(2, TestName = "Strategy_MiterJoin_SolidCore")]
        public void Strategy_MiterJoin_IsContinuous(int strategyIndex)
        {
            string keyword = StrategyKeywords[strategyIndex];
            string label   = keyword ?? "Default";

            var (cameraGo, camera) = BuildCamera();
            // 60° corner at the world origin; miter factor 1/cos30° = 1.155, inside the default limit of 2.
            var pts = new List<double2>
            {
                new double2(-30, 0), new double2(0, 0),
                new double2(15, 25.980762113533),
            };
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var (refGo, refMat) = BuildHorizontalLineAtZ(ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx,
                                                          widthIsPixels: true, color: color);
            var (go, mat) = BuildLine(pts, 1.5f, widthIsPixels: true, color: color);
            if (keyword != null) mat.EnableKeyword(keyword);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng($"line-aa-a7-miter-{label}.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                // Walk the incoming arm's centreline into the corner; the line must never drop out.
                float worst = 2f; int worstColumn = -1;
                for (int column = 170; column <= 254; column++)
                {
                    float best = 0f;
                    for (int row = SnapH / 2 - 4; row <= SnapH / 2 + 4; row++)
                        best = math.max(best, CoverageAt(pixels, column, row, background, plateau));
                    if (best < worst) { worst = best; worstColumn = column; }
                }

                float far  = ColumnCoverageSum(pixels, 200, SnapH / 2 - 8, SnapH / 2 + 8, background, plateau);
                float near = ColumnCoverageSum(pixels, 250, SnapH / 2 - 8, SnapH / 2 + 8, background, plateau);
                TestContext.WriteLine(
                    $"[{label}] miter join — weakest centreline coverage {worst:F3} at column {worstColumn}; " +
                    $"ink far from corner {far:F3}, near corner {near:F3}");

                Assert.That(worst, Is.GreaterThanOrEqualTo(0.35f),
                    $"[{label}] the hairline drops to {worst:F3} at column {worstColumn} approaching a miter " +
                    "join — the strategy is not holding the line continuous through the corner.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(refGo, refMat);
                DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>A7.2c.</b> A round cap must still render under every strategy — the geometry A2b resurrected,
        /// and the one place a zero-<c>extrudeN</c> pivot meets the strategy code. Ink past the endpoint on
        /// the centre row should be about half the styled width for all three: the strategies redistribute
        /// coverage, they do not add or remove it.
        /// </summary>
        [TestCase(0, TestName = "Strategy_RoundCap_Default")]
        [TestCase(1, TestName = "Strategy_RoundCap_Hard")]
        [TestCase(2, TestName = "Strategy_RoundCap_SolidCore")]
        public void Strategy_RoundCap_Renders(int strategyIndex)
        {
            string keyword = StrategyKeywords[strategyIndex];
            string label   = keyword ?? "Default";
            const float width = 1.5f;

            var (cameraGo, camera) = BuildCamera();
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var (refGo, refMat) = BuildHorizontalLineAtZ(ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx,
                                                         widthIsPixels: true, color: color);
            var pts = new List<double2>
            {
                new double2(-100.0 * MetresPerPx, QuarterPixelOffsetM),
                new double2( 100.0 * MetresPerPx, QuarterPixelOffsetM),
            };
            var (go, mat) = BuildLine(pts, width, widthIsPixels: true, color: color,
                                      join: JoinType.Miter, cap: CapType.Round);
            if (keyword != null) mat.EnableKeyword(keyword);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng($"line-aa-a7-roundcap-{label}.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                int centreRow = (int)CentreRowF;
                float ink = 0f;
                for (int step = 0; step < 8; step++)
                    ink += CoverageAt(pixels, SnapW / 2 + 100 + step, centreRow, background, plateau);
                TestContext.WriteLine($"[{label}] round cap ink past the endpoint: {ink:F3} px " +
                                      $"(styled half-width {width * 0.5f:F2} px)");

                Assert.That(ink, Is.EqualTo(width * 0.5f).Within(0.4f),
                    $"[{label}] the round cap contributes {ink:F3} px of ink past the endpoint; a " +
                    $"{width:F1} px line's cap should contribute about {width * 0.5f:F2}. 0 means the cap is " +
                    "not rendering under this strategy.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(refGo, refMat);
                DestroyFixture(go, mat);
            }
        }

        // ─── T7 (S116) — the missing-push fail-safe ─────────────────────────────────────────────

        /// <summary>
        /// <b>T7.</b> With <c>_MapFrameMetersPerDevicePixel</c> UNSET (0), a pixel-width band must still
        /// render, with a plateau clearly distinct from the background.
        ///
        /// <para><b>Why the guard exists, and what it is not.</b> A shader global is PROCESS state; there is
        /// no "unset" for one, so a render path that forgets the push reads 0. Multiplying the styled width by
        /// 0 gives zero world width, and — MEASURED by removing the guard, not assumed — every road of every
        /// styled width then collapses to the same <b>1 device-px hairline</b>: the AA pad is still extruded,
        /// so the frame is not empty, it is plausible and wrong. That is the worst kind of diagnostic, and it
        /// is how S116's own investigation ended up in the projection subsystem. The dash divisor's separate
        /// fail-safe (0 ⇒ <c>dashU = 0</c> ⇒ a uniform half-coverage line) is deliberately left as it is:
        /// visible, never corrupt.</para>
        ///
        /// <para>The fallback is <c>MapPixelsToWorld</c> at the vertex — chosen ONLY because it renders a
        /// plausibly-sized line, not because it is the width model. It is the model this epic reverted, and
        /// it is unreachable in production and in any fixture that builds a <c>MapCamera</c>. Under THIS
        /// camera it happens to be exact (an orthographic projection has no depth term, so the per-vertex
        /// probe returns <see cref="MetresPerPx"/> at every vertex), which is why the width clause below can
        /// be tight — that is a property of the fixture, not an endorsement.</para>
        ///
        /// <para>RED-verified by removing the guard: the band renders <b>1.000 px</b> against 16, so the WIDTH
        /// clause is the one that fires and the plateau clause still passes. Both clauses are load-bearing —
        /// do not drop the width one as belt-and-braces.</para>
        /// </summary>
        [Test]
        public void PixelWidthBand_StillRenders_WhenTheFrameConstantIsUnset()
        {
            const float StyledPx = 16f;

            var (cameraGo, camera) = BuildCamera();
            var (go, mat) = BuildHorizontalLine(StyledPx, widthIsPixels: true,
                                                color: new Color(0.95f, 0.60f, 0.15f, 1f));

            // AFTER BuildCamera, which pushes it — this is precisely the state a render path that forgot the
            // push leaves behind, reproduced rather than simulated.
            Shader.SetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel, 0f);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-t7-frame-constant-unset.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);

                int rowFrom = (int)CentreRowF - 24;
                int rowTo   = (int)CentreRowF + 24;
                float3 plateau = PlateauOnColumn(pixels, CutColumn, rowFrom, rowTo, background);

                float[] profile = CoverageProfileOnColumn(
                    pixels, CutColumn, rowFrom, rowTo, background, plateau);
                float measured = CoverageIntegral(profile);
                TestContext.WriteLine(
                    $"T7 unset frame constant: plateau {plateau} vs background {background}; " +
                    $"apparent width {measured:F3} px (styled {StyledPx})");

                Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                    "THE FAIL-SAFE: with the frame constant unset the band did not render at all. A missing " +
                    "push must degrade to a plausibly-sized line, never to an empty frame — an absent layer " +
                    "reads as a geometry bug and sends the investigation to the wrong subsystem. Profile: " +
                    FormatProfile(profile, rowFrom));

                Assert.That(measured, Is.EqualTo(StyledPx).Within(1.0f),
                    $"the fallback rendered {measured:F3} px for a {StyledPx} px styled width. Under this " +
                    "ORTHOGRAPHIC camera the per-vertex probe is depth-free and returns MetresPerPx exactly, " +
                    "so the fallback is bit-exact here; a different reading means it is not taking the " +
                    "branch. Profile: " + FormatProfile(profile, rowFrom));
            }
            finally
            {
                // Restore the value BuildCamera pushes. Leaving 0 would hand the next fixture in the process
                // the very state this arm exists to describe.
                Shader.SetGlobalFloat(
                    ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel, MetresPerPx);
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(go, mat);
            }
        }

        // ─── A7.3 — is interpolating `hairlineScale` sound? ─────────────────────────────────────

        /// <summary>A PERSPECTIVE camera tilted toward the horizon, so a line running away from it spans a
        /// wide range of depths and the rendered width of a fixed world width varies strongly along it.</summary>
        private static (GameObject go, Camera camera) BuildTiltedCamera()
        {
            var go     = new GameObject("LineAaTiltCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 18f, -55f);
            camera.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
            camera.orthographic       = false;
            camera.fieldOfView        = 55f;
            camera.nearClipPlane      = 0.3f;
            camera.farClipPlane       = 2000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;

            // This camera's OWN frame constant, and not BuildCamera's: the global is PROCESS state, so
            // without this push a tilted perspective render runs against whatever the last orthographic
            // fixture left behind (0.2734375 m/px, ~1.55x wrong here). Derived from the camera exactly as
            // MapCamera.MetresPerDevicePixel is — 2·d·tan(fov/2)/H at the look-at, where the look-at is where
            // the view axis meets the ground plane. Never a hand-written literal: a literal would stop
            // tracking the pose above the moment anyone nudges it.
            float distanceToLookAt = -camera.transform.position.y / camera.transform.forward.y;
            Shader.SetGlobalFloat(
                ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel,
                2f * distanceToLookAt * math.tan(math.radians(camera.fieldOfView * 0.5f)) / SnapH);
            return (go, camera);
        }

        /// <summary>
        /// <b>A7.3 (rewritten, S116).</b> <c>hairlineScale</c> is a per-vertex scalar carried on a varying, so
        /// the reviewer asked what perspective-correct interpolation does to it mid-segment. The QUESTION is
        /// live; the PREMISE this arm used to assert is gone.
        ///
        /// <para><b>What changed.</b> The old arm asserted the scalar is exactly CONSTANT along a pixel-width
        /// line, on the algebra <c>aaPadWorld = 0.5·pxToWorld</c> ⇒ <c>minWidthWorld = 2·pxToWorld</c> and
        /// <c>widthWorld = W·ws·pxToWorld</c>, so <c>pxToWorld</c> cancelled out of
        /// <c>saturate(widthWorld / max(widthWorld, minWidthWorld))</c>. Under the world-width model those are
        /// no longer the same quantity: <c>widthWorld</c> takes the frame constant while the pad — a genuine
        /// screen quantity — keeps its per-vertex measurement. Nothing cancels, and nothing should: a
        /// pixel-width hairline holds a fixed WORLD width, so it shrinks below the 2 device-px floor as it
        /// recedes, the clamp engages progressively, and the compensation dims it to keep the coverage
        /// integral honest. The old paragraph's "world-unit widths are the case that DOES vary" now describes
        /// PIXEL widths.</para>
        ///
        /// <para><b>What is asserted instead</b> — the interpolation question, which is what the arm was
        /// really for. Over a 1 px <c>_HAIRLINE_SOLID_CORE</c> line receding from a tilted perspective camera,
        /// against a 4 px <c>a == 1</c> companion at the same depths (which cancels the lit shading exactly):
        /// (a) it never VANISHES at any measured depth; (b) its ratio to the companion is monotonically
        /// NON-INCREASING with depth — degradation, not drift or oscillation; (c) no row-to-row STEP exceeds a
        /// bound, which is what "is interpolating a varying sound?" actually asks — a varying interpolated
        /// wrongly, or a clamp engaging discontinuously, shows up as a jump; and (d) the compensation is
        /// applied at all — a ratio near 1.0 means it is not.</para>
        ///
        /// <para>Measured after the fix: 0.851 / 0.724 / 0.571 / 0.434 / 0.284 over rows 180…300, steps of
        /// 0.127…0.153.</para>
        /// </summary>
        [Test]
        public void HairlineScale_DegradesSmoothlyWithDepth_UnderTilt()
        {
            var (cameraGo, camera) = BuildTiltedCamera();
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            // Reference: a wide line close to the camera, giving an a == 1 plateau in the same frame.
            // Placed inside the frustum: at y=18 tilted 12° with a 55° fov the ground is visible from about
            // z = -33 outward, so anything nearer than that renders nothing at all.
            var (refGo, refMat) = BuildLine(
                new List<double2> { new double2(-30.0, -20.0), new double2(30.0, -20.0) },
                12f, widthIsPixels: true, color: color);
            // Two lines recede side by side. The 4 px companion carries no keyword and is a == 1, so its
            // measured peak at each depth IS the local lit-shading level — this shader is real PBR with a
            // live viewDirectionWS, so colour varies with depth (design §6.2), and that would otherwise be
            // indistinguishable from the scalar drifting. Dividing by it cancels the shading exactly.
            // SUBDIVIDED along z. `pxToWorld` is evaluated per VERTEX, so a pixel-width line only holds its
            // styled device width exactly at its vertices; across one long segment spanning a huge depth
            // range the extrusion interpolates linearly in world space while the required world offset grows
            // with depth, and the rendered width drifts in between. That is a property of the extrusion
            // model, not of any strategy — subdividing removes it and lets this tooth measure the scalar.
            var farPts  = new List<double2>();
            var nearPts = new List<double2>();
            for (int i = 0; i <= 40; i++)
            {
                double z = -12.0 + i * (232.0 / 40.0);
                nearPts.Add(new double2(-7.0, z));
                farPts.Add(new double2(7.0, z));
            }
            var (wideGo, wideMat) = BuildLine(nearPts, 4f, widthIsPixels: true, color: color);
            var (go, mat)         = BuildLine(farPts,  1f, widthIsPixels: true, color: color);
            mat.EnableKeyword(HairlineSolidCoreKeyword);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                EnsureGpuContext(snap);
                snap.WritePng("line-aa-a7-tilt-solidcore.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = BackgroundLinear(pixels);
                // Plateau from a NAMED interior box of the near reference bar, not a whole-column max: the
                // max could land on any row, at any depth, and then everything is normalised against an
                // arbitrary reference. Locate the bar in the lower frame, well below the measured rows.
                int plateauRow = 0; float bestSoFar = -1f;
                for (int row = 20; row < 170; row++)
                {
                    float3 sample = SampleLinear(pixels, CutColumn, row);
                    float  dist   = math.distancesq(sample, background);
                    if (dist > bestSoFar) { bestSoFar = dist; plateauRow = row; }
                }
                float3 plateau = SampleLinearBox(pixels, CutColumn, plateauRow, 2);
                AssertPlateauDistinct(background, plateau);

                // Walk up the frame. The hairline is right of centre, the wide companion left of it; they
                // converge toward the vanishing point but never cross.
                float lo = 2f, hi = 0f; int rowsMeasured = 0;
                var ratios = new List<float>();
                var report = new System.Text.StringBuilder();
                for (int row = 150; row <= 330; row += 30)
                {
                    // RAW, unsaturated: the ratio of two lit surfaces at the same depth cancels both the
                    // shading level and the plateau's absolute calibration — but only while neither term
                    // has clamped.
                    float hair = 0f, wide = 0f;
                    for (int column = SnapW / 2; column < SnapW; column++)
                        hair = math.max(hair, RawProjection(pixels, column, row, background, plateau));
                    for (int column = 0; column < SnapW / 2; column++)
                        wide = math.max(wide, RawProjection(pixels, column, row, background, plateau));
                    if (hair < 0.05f || wide < 0.20f) continue;   // past the vanishing point
                    float ratio = hair / wide;
                    report.Append($"[row {row}] {hair:F3}/{wide:F3}={ratio:F3}  ");
                    lo = math.min(lo, ratio); hi = math.max(hi, ratio);
                    ratios.Add(ratio);
                    rowsMeasured++;
                }

                float maxRise = 0f, maxStep = 0f;
                for (int i = 1; i < ratios.Count; i++)
                {
                    float delta = ratios[i] - ratios[i - 1];   // rows ascend ⇒ depth increases
                    maxRise = math.max(maxRise,  delta);
                    maxStep = math.max(maxStep, math.abs(delta));
                }

                TestContext.WriteLine(
                    $"A7.3 tilted 1 px _HAIRLINE_SOLID_CORE vs 4 px shading reference (RAW projections; " +
                    $"plateau from row {plateauRow}): {report}({rowsMeasured} depths; ratio {hi:F3} → " +
                    $"{lo:F3}; largest rise with depth {maxRise:F4}, largest step {maxStep:F4})");

                // (a) IT MUST NOT VANISH — and every sampled depth must carry BOTH lines. The loop skips a
                // row when either reads too faint, so this doubles as the vacuity guard: a hairline that
                // stopped rendering at depth would silently shrink the sample rather than fail.
                // KNIFE-EDGE, deliberately, and worth knowing before touching it: the sweep offers SEVEN
                // candidate rows (150…330 step 30) and exactly FIVE survive the faint-row filter above —
                // rows 150 and 330 are past the useful range at both ends, and row 300 already reads 0.739
                // raw. So this bound has zero margin. Widening the sweep does not help (the filter, not the
                // range, is what drops them); the honest reading of a drop to 4 is "a hairline stopped
                // rendering at depth", which is a real regression and exactly what this clause is for.
                Assert.That(rowsMeasured, Is.GreaterThanOrEqualTo(5),
                    $"Only {rowsMeasured} of the sampled depths carried both lines. Either the fixture no " +
                    "longer spans a useful depth range, or a hairline stopped rendering at depth — the " +
                    "second would be a real regression, so do not just widen the sweep.");
                Assert.That(lo, Is.GreaterThan(0.10f),
                    $"The clamped hairline falls to {lo:F3} of an a == 1 line at the same depth — it is " +
                    "vanishing. SolidCore exists so a receding hairline keeps a solid 2 device-px core and " +
                    $"pays for it in alpha; measured 0.284 at the deepest sampled row. {report}");

                // (b) DEGRADATION, not drift. A fixed world width shrinks monotonically in device px with
                // depth, so the clamp engages monotonically and the compensation follows it. A RISE would
                // mean the scalar is tracking something other than the rendered width.
                Assert.That(maxRise, Is.LessThan(0.02f),
                    $"The hairline's coverage ratio RISES by {maxRise:F4} with depth. Under a constant world " +
                    "width the rendered band can only get narrower, so hairlineScale can only fall. A rise " +
                    $"means the scalar is not tracking the rendered width. {report}");

                // (c) THE INTERPOLATION QUESTION. hairlineScale is a plain varying; if interpolating a ratio
                // were unsound mid-segment, or the clamp engaged discontinuously, it would show as a JUMP
                // between adjacent sampled depths rather than as a smooth ramp. Measured steps: 0.127…0.153.
                Assert.That(maxStep, Is.LessThan(0.30f),
                    $"hairlineScale steps by {maxStep:F4} between adjacent sampled depths, against a smooth " +
                    "0.127…0.153 measured. A jump is what an unsound interpolation of this varying, or a " +
                    $"discontinuous clamp, would look like. {report}");

                // (d) THE COMPENSATION IS APPLIED AT ALL. Near 1.0 means the band is being clamped wider
                // without paying for it in alpha — tooth T8's failure, a 1 px road rendered twice as
                // prominent as the style asked for.
                Assert.That(hi, Is.LessThan(0.95f),
                    $"The clamped hairline reaches {hi:F3} of an a == 1 line at the same depth; near 1.0 " +
                    $"means the energy compensation is not being applied. {report}");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                DestroyFixture(refGo, refMat);
                DestroyFixture(wideGo, wideMat);
                DestroyFixture(go, mat);
            }
        }
    }
}
