// Line pixel-precision GPU/visual acceptance tests: the AA straddle and the tile-seam clip, both sub-pixel
// boundaries (meshing). Split from the other line files by the CS0104 `CameraProperties`/`Object` collisions.
//
// Contents:
//   LineAaSnapshotTests    — Acceptance teeth for the line analytical-AA rebuild (strict one-device-pixel straddle centred on the styled edge).
//   TileSeamSnapshotTests  — T3 + T5 — the two pixel questions the tile-buffer clip stage exists to settle, measured on real pixels: T3 does clipping remove the double-painted alpha BAND along a seam?

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geometry;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Jobs.Tiles;
using Fill = MapRenderer.Core.Style.Fill;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make

namespace MapRenderer.Tests.Visual
{
    // Unity-only: render tests requiring GPU context (SnapshotRenderer / UnityEngine).
    // NOT included in Tools/core-tests/core-tests.csproj.

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineAaSnapshotTests — Acceptance teeth for the line analytical-AA rebuild (strict one-device-pixel straddle…
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance teeth for line analytical AA (a one-device-pixel straddle centred on the styled edge),
    /// each an ALPHA-WEIGHTED coverage integral, never a thresholded pixel count. Camera: top-down ortho,
    /// orthographicSize 70, 512×512, so <c>row = 256 + z / 0.2734375</c> exactly. Non-obvious why: an edge on
    /// a pixel centre makes a rasterizer tie that could hide an injected half-pixel pad, so the horizontal
    /// fixtures sit a QUARTER pixel off (<see cref="QuarterPixelOffsetM"/>). The snapshot bytes are
    /// sRGB-encoded, and skipping the linear decode bends the alpha ramp and corrupts every integral.
    /// </summary>
    [TestFixture]
    public class LineAaSnapshotTests : BaseTestFixture
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

            // Non-local invariant: MapCamera.SyncToCamera pushes the frame constant, and this hand-built camera
            // has none, so it pushes MetresPerPx itself. A 0 renders a plausible 1 px hairline, not a blank
            // frame; PixelWidthBand_StillRenders_WhenTheFrameConstantIsUnset pins that fallback.
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
        /// World Z placing a horizontal centreline at a chosen distance from a pixel CENTRE. Pixel row
        /// <c>r</c> has its centre at <c>r + 0.5</c>, so the centreline goes at <c>row + 0.5 + phase</c> and
        /// <paramref name="phaseFromCentre"/> IS the distance to the nearest sample. Parametrising on that
        /// distance avoids any ambiguity about which phase is the tie: 0 lands ON a sample, 0.5 is the tie.
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

        /// <summary>
        /// Every AA-ON tooth calls this first, so the suite can never be greened by turning the AA keyword
        /// off. Until the shader declares <c>_EDGE_ANTIALIASING_OFF</c> this reads <c>false</c> on an undeclared
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
        private static float3 SampleLinear(Frame pixels, int column, int row)
        {
            column = math.clamp(column, 0, SnapW - 1);
            row    = math.clamp(row,    0, SnapH - 1);
            Color32 px = pixels[column, row];
            return new float3(ToLinear(px.r), ToLinear(px.g), ToLinear(px.b));
        }

        /// <summary>Mean linear colour over a (2·radius+1)² box — used to read the background and the
        /// interior plateau without picking up single-pixel noise.</summary>
        private static float3 SampleLinearBox(Frame pixels, int column, int row, int radius)
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
        private static float3 BackgroundLinear(Frame pixels) => SampleLinearBox(pixels, 8, 8, 2);

        /// <summary>
        /// Alpha-weighted coverage at one pixel: the composite's position on the background→plateau colour
        /// axis, in linear space. Exactly the ribbon's rendered alpha when the plateau reference is a
        /// fully-covered pixel of the same material.
        /// </summary>
        private static float CoverageAt(
            Frame pixels, int column, int row, float3 background, float3 plateau)
        {
            float3 axis  = plateau - background;
            float  denom = math.dot(axis, axis);
            if (denom < 1e-9f) return 0f;
            return math.saturate(math.dot(SampleLinear(pixels, column, row) - background, axis) / denom);
        }

        /// <summary>The most saturated sample on a column cut — the ribbon's fully-covered interior.</summary>
        private static float3 PlateauOnColumn(
            Frame pixels, int column, int rowFrom, int rowTo, float3 background)
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
            Frame pixels, int column, int rowFrom, int rowTo, float3 background, float3 plateau)
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
        /// (no MSAA) produces only coverage 0 and 1. The ribbon is diagonal because an axis-aligned edge on a
        /// pixel boundary reads binary even with a working ramp.
        /// </summary>
        [Test]
        public void OuterSilhouette_IsAntialiased()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var pts = new List<double2> { new double2(-35, -35), new double2(35, 35) };
            var (lineGo, mat) = BuildLine(pts, width: 12f, widthIsPixels: true,
                                          color: new Color(0.95f, 0.60f, 0.15f, 1f));
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-t1-diagonal.png");

                Frame pixels = snap.Pixels;
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
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── T2: a cased pair keeps a crisp internal boundary ───────────────────────────────────

        private const float CasingWidthPx = 24f;
        private const float FillWidthPx   = 10f;

        /// <summary>
        /// <b>T2.</b> Two coplanar <c>Map/Line</c> draws in painter order — a wide dark casing under a narrow
        /// light fill — must keep the fill OPAQUE across its whole styled band, with at most one blended
        /// pixel per flank at the boundary. It rejects an INSET fade, which lets casing colour through inside
        /// the fill band; a straddle with an <c>a == 1</c> interior passes.
        /// </summary>
        [Test]
        public void CasedPair_InternalBoundary_StaysCrisp()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            // The casing must be BRIGHT: under this dim ambient light a dark navy is within ~0.02 linear of the
            // background, so no fill-vs-casing reading would separate from noise.
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
                snap.WritePng("line-aa-t2-cased-pair.png");

                Frame pixels     = snap.Pixels;
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
        /// by nothing. The <c>_WidthIsPixels = false</c> case catches a pad in the wrong unit: there
        /// <c>pxToWorld</c> is a literal 1.0, so <c>0.5 · pxToWorld</c> would pad half a METRE (~1.8 px).
        /// </summary>
        [TestCase(true,  TestName = "ApparentWidth_Unchanged_MatchesStyledPixels_PixelWidth")]
        [TestCase(false, TestName = "ApparentWidth_Unchanged_MatchesStyledPixels_WorldWidth")]
        public void ApparentWidth_Unchanged_MatchesStyledPixels(bool widthIsPixels)
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            float width = widthIsPixels ? ApparentWidthPx : ApparentWidthPx * MetresPerPx;
            var (lineGo, mat) = BuildHorizontalLine(width, widthIsPixels,
                                                    new Color(0.95f, 0.60f, 0.15f, 1f));
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng(widthIsPixels ? "line-aa-t2b-px-width.png" : "line-aa-t2b-world-width.png");

                Frame pixels = snap.Pixels;
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
        /// <para>It gates that the ramp keys on the C0-continuous <c>|side|</c> varying, so interior edges are
        /// invisible; a <c>|side|</c> dip along an interior edge fails it. Non-obvious why: the corner is 60°
        /// because the annulus spans ⅓ to ⅔ of the half-width, where only the fan draws, and a bevel chord stands
        /// off at half-width·cos(θ/2). At 90° that is 0.71 (8.49 px against the annulus's 8 px); at 60° it is
        /// 0.87, outside the annulus for all three joins. Do not move this fixture to 90°. Limitation: it does not
        /// gate coverage from per-triangle edge distance, which needs <c>SV_Barycentrics</c> and is unreachable
        /// at <c>#pragma target 2.0</c>.</para>
        /// </summary>
        [TestCase(JoinType.Miter)]
        [TestCase(JoinType.Bevel)]
        [TestCase(JoinType.Round)]
        public void Join_NoInteriorSeam(JoinType join)
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
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
                snap.WritePng($"line-aa-t3a-join-{join}.png");

                Frame pixels     = snap.Pixels;
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
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── Joins: the three types must be DISTINGUISHABLE on screen ───────────────────────────
        //
        // Non-obvious why: a chamfer or arc emitted on the CONCAVE side is buried in the band overlap, and
        // no interior probe sees it. The discriminator is the join's reach along the OUTWARD bisector, which
        // at a 90° corner separates the three well beyond AA tolerance:
        //     miter → halfWidth / cos45° = 1.41421·h     (the miter tip)
        //     round → halfWidth          = 1.00000·h     (the arc radius)
        //     bevel → halfWidth · cos45° = 0.70711·h     (the chamfer chord's standoff)
        // The V opens toward −X, so the bisector is screen-RIGHT whatever the buffer's row order.

        private const float JoinReachWidthM     = 24f;                        // world metres, so h = 12 m
        private const float JoinReachHalfWidthM = JoinReachWidthM * 0.5f;
        private const float JoinReachHalfWidthPx = JoinReachHalfWidthM / MetresPerPx;   // ≈ 43.9 px
        private const float JoinReachArmM       = 40f;

        private static (GameObject go, Material mat) BuildApexFixture(JoinType join)
        {
            // Right turn at the apex; interior angle 90°; convex wedge faces +X.
            var pts = new List<double2>
            {
                new double2(-JoinReachArmM,  JoinReachArmM),
                new double2(0.0,             0.0),
                new double2(-JoinReachArmM, -JoinReachArmM),
            };
            return BuildLine(pts, JoinReachWidthM, widthIsPixels: false,
                             color: new Color(0.95f, 0.60f, 0.15f, 1f), join: join, cap: CapType.Butt);
        }

        /// <summary>
        /// Last column, marching +X from the apex along the bisector row, whose coverage is still ≥ half.
        /// Returned in pixels from the apex. Sub-pixel refined by linear interpolation across the AA edge so
        /// the three joins' reaches are resolved well inside their ~13 px separation.
        /// </summary>
        private static float BisectorReachPx(Frame pixels, float3 background, float3 plateau)
        {
            const int apexCol = SnapW / 2;
            const int row     = SnapH / 2;

            float prevCoverage = CoverageAt(pixels, apexCol, row, background, plateau);
            Assert.Greater(prevCoverage, 0.9f,
                $"The apex pixel itself must be covered by every join type (got {prevCoverage:F3}) — " +
                "if it is not, the fixture is not where this tooth thinks it is.");

            for (int col = apexCol + 1; col < SnapW; col++)
            {
                float coverage = CoverageAt(pixels, col, row, background, plateau);
                if (coverage < 0.5f)
                {
                    // Linear crossing between the last ≥0.5 sample and this one.
                    float t = (prevCoverage - 0.5f) / math.max(prevCoverage - coverage, 1e-6f);
                    return (col - 1 - apexCol) + t;
                }
                prevCoverage = coverage;
            }
            Assert.Fail("Coverage never fell below half before the frame edge — fixture too large.");
            return 0f;
        }

        [Test]
        public void JoinTypes_AreDistinguishableOnScreen_ByBisectorReach()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var reaches = new Dictionary<JoinType, float>();
            {
                foreach (var join in new[] { JoinType.Miter, JoinType.Round, JoinType.Bevel })
                {
                    var (lineGo, mat) = BuildApexFixture(join);
                    try
                    {
                        using var snap = new SnapshotRenderer(SnapW, SnapH);
                        snap.Render(camera);

                        Frame pixels     = snap.Pixels;
                        float3 background = BackgroundLinear(pixels);

                        // Plateau: the most saturated sample on a column crossing BOTH arms. A fixed row could land
                        // in the empty wedge and would assume the buffer's row order.
                        float3 plateau = PlateauOnColumn(pixels, SnapW / 2 - 73, 0, SnapH - 1, background);
                        Assert.Greater(math.length(plateau - background), 0.05f,
                            $"{join}: no covered pixel found on the arm-crossing column — the fixture did " +
                            "not render where this tooth looks, so every later reading would be vacuous.");

                        reaches[join] = BisectorReachPx(pixels, background, plateau);
                    }
                    finally { DestroyFixture(lineGo, mat); }
                }

                float h = JoinReachHalfWidthPx;
                // Absolute: each join reaches its own analytic distance. 2 px covers the AA edge and the
                // round join's 4-segment chord secancy; the three targets are ~13 px apart.
                Assert.AreEqual(1.41421f * h, reaches[JoinType.Miter], 2.0f,
                    $"Miter must reach the miter tip at 1.414·h = {1.41421f * h:F1} px. Got {reaches[JoinType.Miter]:F2}.");
                Assert.AreEqual(1.00000f * h, reaches[JoinType.Round], 2.0f,
                    $"Round must reach the arc radius h = {h:F1} px. Got {reaches[JoinType.Round]:F2}. " +
                    "Reading ~1.414·h means the arc is not being emitted and the join fell back to a miter.");
                Assert.AreEqual(0.70711f * h, reaches[JoinType.Bevel], 2.0f,
                    $"Bevel must stop at the chamfer chord, 0.707·h = {0.70711f * h:F1} px. " +
                    $"Got {reaches[JoinType.Bevel]:F2}. Reading ~1.414·h is the pre-correction bug: the " +
                    "chamfer emitted on the concave side, so the silhouette was the miter tip.");

                // Ordering with a hard separation floor. This is the part that cannot be satisfied by a
                // join type degrading into another one, whatever the absolute tolerances allow.
                Assert.Greater(reaches[JoinType.Miter] - reaches[JoinType.Round], 8.0f,
                    $"Miter must out-reach round by ≫0 (got {reaches[JoinType.Miter]:F2} vs {reaches[JoinType.Round]:F2}).");
                Assert.Greater(reaches[JoinType.Round] - reaches[JoinType.Bevel], 8.0f,
                    $"Round must out-reach bevel by ≫0 (got {reaches[JoinType.Round]:F2} vs {reaches[JoinType.Bevel]:F2}).");
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
        /// takes the cap's axis-aligned 0° chord off a pixel boundary.</param>
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
            Frame pixels, int capColumn, int outward, int row, float3 background, float3 plateau)
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
        /// A round cap must reach the screen at all, and its silhouette must be an ARC. Non-obvious why: the
        /// fan's pivot vertex has <c>extrudeN == 0</c>, and a <c>normalize()</c> of it would be NaN, discarding
        /// the whole cap so it renders as <c>butt</c>. A SQUARE cap also reaches past the endpoint, so the
        /// extent must also SHRINK as the row moves off the centreline.
        /// </summary>
        [Test]
        public void RoundCap_ExtendsPastEndpoint_AsAnArc()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (lineGo, mat)      = BuildRoundCappedLine();
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-a2b-round-cap-arc.png");

                Frame pixels     = snap.Pixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, SnapW / 2, SnapH / 2, 2);
                AssertPlateauDistinct(background, plateau);

                const float capHalfPx = CapWidthPx * 0.5f;   // 20 px — the cap's radius
                const float centreRow = SnapH / 2f;          // the centreline sits on the row boundary

                // A circle reaches sqrt(r² − dy²) (19.99, 17.02, 12.64 px); a square cap reaches r. Rows avoid
                // the too-flat apex and the rim, where the chorded cap leaves the circle.
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

                    // (b) An ARC: the reach tracks sqrt(r² − dy²) and shrinks monotonically. The tolerance absorbs
                    //     the chorded apex (r·cos18° = 19.02 px) and whole-pixel quantisation.
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
                DestroyFixture(lineGo, mat);
            }
        }

        /// <summary>
        /// <b>T3c, pre-AA form.</b> With <c>line-blur</c> on, a round cap's soft edge must be present at
        /// EVERY angular position around the cap's silhouette arc. Non-obvious why: it probes the cap's
        /// <c>side</c> tagging through the blur feather. A fan seeded from a vertex tagged −1 would make a
        /// silhouette edge interpolate <c>side</c> +1 → −1 through 0, and that wedge (about
        /// 180°/(roundSegments+1)) would get no feather.
        /// </summary>
        [Test]
        public void RoundCap_BlurFadesAllTheWayAround()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (lineGo, mat)      = BuildRoundCappedLine();
            mat.SetFloat("_Blur", CapBlurPx);
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-t3c-round-cap-blur.png");

                Frame pixels     = snap.Pixels;
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
                DestroyFixture(lineGo, mat);
            }
        }

        /// <summary>
        /// <b>T3c, AA-ON form.</b> The straddle's fade must be present at EVERY angular position around a
        /// round cap's silhouette — at the shipped default material state, with no <c>line-blur</c>, which
        /// <see cref="RoundCap_BlurFadesAllTheWayAround"/> needs and <c>liberty.json</c> never sets. Non-obvious
        /// why: <c>aaPadWorld</c> is measured PER VERTEX along its own <c>unitDir_WS</c>, and every cap vertex
        /// has a different direction, so this pins the pad on curved geometry.
        /// </summary>
        [Test]
        public void RoundCap_Silhouette_FadesAtEveryAngle()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            // Non-obvious why: unshifted, the 0° chord sits on a pixel boundary (136.98), where a correct straddle
            // reads 1.00 and 0.02 with no partial pixel. The quarter-pixel shift puts it at 137.23, reading 0.77.
            var (lineGo, mat)      = BuildRoundCappedLine(QuarterPixelOffsetM);   // _Blur at its 0 default
            AssertAaKeywordClear(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-t3c-round-cap-straddle.png");

                Frame pixels     = snap.Pixels;
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

                        // Search a 5×5 box at the rim, not the ray: a ray can step over a one-pixel ramp. The box
                        // still finds nothing in a dead arc segment, which spans ~12.6 px.
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
                DestroyFixture(lineGo, mat);
            }
        }

        // ─── T4: the toggle actually turns the whole mechanism off ──────────────────────────────

        /// <summary>
        /// <b>T4.</b> With <c>_EDGE_ANTIALIASING_OFF</c> set, the line has a BINARY coverage profile and the
        /// styled apparent width. Both clauses matter: gating only the ramp leaves a pixel-fat line (clause
        /// ii), and gating only the pad leaves an inset fade (clause i). It is the one test here that sets the
        /// keyword; the rest call <see cref="AssertAaKeywordClear"/>.
        /// </summary>
        [TestCase(true,  TestName = "AaOff_ReproducesHardEdge_PixelWidth")]
        [TestCase(false, TestName = "AaOff_ReproducesHardEdge_WorldWidth")]
        public void AaOff_ReproducesHardEdge(bool widthIsPixels)
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
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
                snap.WritePng(widthIsPixels ? "line-aa-t4-off-px-width.png" : "line-aa-t4-off-world-width.png");

                Frame pixels     = snap.Pixels;
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
        /// edge each contributes ~0.5 alpha, so it reads <c>1 − (1−0.5)(1−0.5) = 0.75</c>, not 1. Limitation:
        /// that is inherent to alpha-blended same-layer overlap; only a whole-layer offscreen composite fixes
        /// it. The number is recorded, not gated. Only pixels DEEP inside the union are measured, because
        /// the union's own silhouette legitimately reads partial.
        /// </summary>
        [Test]
        public void Junction_UnderCoverage_Recorded()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);

            // A shallow V (each arm 20° off +x) so the inner silhouettes converge over background; at a right
            // angle the two bands simply abut.
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
                snap.WritePng("line-aa-t3b-junction.png");

                Frame pixels     = snap.Pixels;
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
                    $"{deficient} of {measured} deep-interior pixels below 0.95. docs/line-antialiasing-design.md " +
                    $"§ \"Where same-layer overlap under-covers\" predicts ≲25% over a ~1–3 px wedge.");

                // Loose by design — a recorded limit, not a gate.
                Assert.That(1f - worst, Is.LessThan(0.60f),
                    $"Junction crotch under-coverage is {(1f - worst):P1} at ({worstColumn}, {worstRow}), " +
                    "far beyond what two overlapping straddles produce. That exceeds the accepted " +
                    "same-layer blend limit — something is punching a hole in the junction.");
            }
            finally
            {
                DestroyFixture(goA, matA);
                DestroyFixture(goB, matB);
            }
        }

        // ─── The AA ramp must be one device pixel in EVERY screen direction ──────────────────────

        private const float DirectionWidthPx = 12f;

        /// <summary>
        /// Projection onto the background→plateau axis WITHOUT the saturate. Ratios of two lit surfaces
        /// must be taken raw: once a value clamps at 1.0 it stops carrying the shading level, and a ratio
        /// against a clamped denominator silently normalises against nothing.
        /// </summary>
        private static float RawProjection(
            Frame pixels, int column, int row, float3 background, float3 plateau)
        {
            float3 axis  = plateau - background;
            float  denom = math.dot(axis, axis);
            if (denom < 1e-9f) return 0f;
            return math.dot(SampleLinear(pixels, column, row) - background, axis) / denom;
        }

        /// <summary>Σ coverage down one image column, alpha-weighted.</summary>
        private static float ColumnCoverageSum(
            Frame pixels, int column, int rowFrom, int rowTo, float3 background, float3 plateau)
        {
            float sum = 0f;
            for (int row = rowFrom; row <= rowTo; row++)
                sum += CoverageAt(pixels, column, row, background, plateau);
            return sum;
        }

        /// <summary>
        /// Apparent width must not depend on which way the line runs on screen. Non-obvious why:
        /// <c>fwidth</c> is the L1 length <c>|ddx| + |ddy|</c>, which over-reads by <c>|cos θ| + |sin θ|</c>, so
        /// a ramp divided by it spans 1.41 px at 45° and the diagonal loses 0.41 px of ink. Both lines render
        /// in ONE frame; the horizontal arm is the control. A vertical cut through a 45° band spans √2 × its
        /// width, and the read is LINEAR, because sRGB luminance errs by as much as the 3.4 % effect.
        /// </summary>
        [Test]
        public void ApparentWidth_IsDirectionIndependent()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
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
                snap.WritePng("line-aa-a60-direction.png");

                Frame pixels     = snap.Pixels;
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

                // The trapezoid integrates to 2H − c with H = styled/2 + 0.5, which gives the ramp width: the same
                // measurement in the defect's units, not independent corroboration.
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
            Frame pixels, int centreRow, float3 background, float3 plateau)
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
            Track(cameraGo);
            var built = BuildHairlineSweep(HairlineHardKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-a6a-hard-phases.png");

                Frame pixels     = snap.Pixels;
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
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>T6.</b> Under <c>_HAIRLINE_HARD</c> the hairline profile is binary, and it still carries the
        /// styled amount of ink. The energy clause is what stops the narrowing being implemented as a step on
        /// the PADDED edge instead of the styled one: that lights two pixels per cross-section rather than
        /// one, so the integral doubles and the line renders a pixel fat — the outset artefact.
        /// </summary>
        [Test]
        public void Hairline_Hard_ProfileIsBinaryAndConservesEnergy()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var built = BuildHairlineSweep(HairlineHardKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                Frame pixels     = snap.Pixels;
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
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>T6b.</b> The threshold is real: a 6 px line is untouched by <c>_HAIRLINE_HARD</c>. Without the
        /// <c>smoothstep</c> gate the whole map would render hard-edged — the one way to undo analytical AA
        /// without any AA tooth noticing.
        /// </summary>
        [Test]
        public void Hairline_Hard_LeavesWideLinesUntouched()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var built = BuildHairlineSweep(HairlineHardKeyword, 6f);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-a6a-hard-wide.png");

                Frame pixels     = snap.Pixels;
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
            Track(cameraGo);
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            var (referenceGo, referenceMat) = BuildHorizontalLineAtZ(
                ZForPhase(ReferenceRow, 0.0), ReferenceWidthPx, widthIsPixels: true, color: color);
            // Gap 20 px, width 1 px ⇒ hole radius 10 px, styled outer radius 11 px: a 1 px ring per flank.
            var (ringGo, ringMat) = BuildHorizontalLineAtZ(
                ZForPhase(SnapH / 2, 0.2), 1f, widthIsPixels: true, color: color);
            ringMat.SetFloat("_GapWidth", 20f);
            AssertAaKeywordClear(referenceMat);
            AssertAaKeywordClear(ringMat);
            // The reference strip stays on the DEFAULT strategy (see BuildHairlineSweep), immune to the bugs
            // this tooth hunts.
            ringMat.EnableKeyword(HairlineHardKeyword);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-a6a-hard-thin-ring.png");

                Frame pixels     = snap.Pixels;
                float3 background = BackgroundLinear(pixels);
                float3 plateau    = SampleLinearBox(pixels, CutColumn, ReferenceRow, 2);
                AssertPlateauDistinct(background, plateau);

                int centreRow = SnapH / 2;
                var profile   = new float[33];
                for (int i = 0; i < profile.Length; i++)
                    profile[i] = CoverageAt(pixels, CutColumn, centreRow - 16 + i, background, plateau);
                string dump = FormatProfile(profile, centreRow - 16);
                TestContext.WriteLine($"A6a thin ring (gap 20 px, width 1 px): {dump}");

                // Binary FIRST, so a half-hardened ring names its cause here; a blank render is trivially binary,
                // which the guard after it catches.
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
                DestroyFixture(referenceGo, referenceMat);
                DestroyFixture(ringGo, ringMat);
            }
        }

        /// <summary>
        /// <b>Recorded, not gated.</b> Peak brightness and ink across the width threshold, so a POP under zoom
        /// shows as a step in these numbers. Continuity is expected: <c>c(d) = saturate((halfStyled − d)/rampPx
        /// + 0.5)</c> integrates to <c>2·halfStyled</c> for ANY rampPx, and rampPx is a C1 <c>smoothstep</c>.
        /// </summary>
        [TestCase("_HAIRLINE_HARD",       TestName = "Hairline_Hard_ThresholdTransition_Recorded")]
        [TestCase("_HAIRLINE_SOLID_CORE", TestName = "Hairline_SolidCore_ThresholdTransition_Recorded")]
        public void Hairline_ThresholdTransition_Recorded(string keyword)
        {
            float[] widths = { 0.8f, 1.0f, 1.2f, 1.4f, 1.6f, 1.8f, 2.0f, 2.4f };
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
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
                snap.WritePng($"line-aa-a6-threshold-sweep-{keyword}.png");

                Frame pixels     = snap.Pixels;
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
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        // ─── A6b: _HAIRLINE_SOLID_CORE ──────────────────────────────────────────────────────────

        private const string HairlineSolidCoreKeyword = "_HAIRLINE_SOLID_CORE";

        /// <summary>
        /// The phase sweep plus an <c>a == 1</c> reference strip, in one frame, under a strategy keyword.
        /// Non-obvious why: <see cref="PlateauOnColumn"/> normalises to its own cut's brightest sample, so without
        /// the strip a dim hairline would read 1.0. A 12 px line is untouched by every strategy.
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
            // The reference stays on the DEFAULT strategy, or a defect that scales coverage at EVERY width would
            // scale the reference too and the ratio would hide it.
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
            Track(cameraGo);
            var built = BuildHairlineSweep(HairlineSolidCoreKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-a6b-solidcore-phases.png");

                Frame pixels     = snap.Pixels;
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
            Track(cameraGo);
            var built = BuildHairlineSweep(HairlineSolidCoreKeyword, HairlineWidthPx);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                Frame pixels     = snap.Pixels;
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
            Track(cameraGo);
            var built = BuildHairlineSweep(HairlineSolidCoreKeyword, 6f);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-a6b-solidcore-wide.png");

                Frame pixels     = snap.Pixels;
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
        /// <b>The whole keyword matrix.</b> 3 strategies × AA on/off = 6 named variants, so a variant that
        /// fails to compile on its path cannot ship green. The AA-OFF rows matter most: both strategy blocks
        /// are guarded on <c>!defined(_EDGE_ANTIALIASING_OFF)</c>, so with AA off all three must render the
        /// same hard edge at the styled width.
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
            Track(cameraGo);
            var built = BuildStrategyScene(aaOff, strategyKeyword, HairlineWidthPx, row, phase);
            string cell = $"AA {(aaOff ? "OFF" : "ON")} × {strategyKeyword ?? "Default"}";

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                Frame pixels     = snap.Pixels;
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
                foreach (var (go, mat) in built) DestroyFixture(go, mat);
            }
        }

        // ─── A7.2 — the strategies on geometry that is not a straight axis-aligned run ──────────

        private static readonly string[] StrategyKeywords =
            { null, HairlineHardKeyword, HairlineSolidCoreKeyword };

        /// <summary>
        /// <b>A7.2a.</b> A strategy's width estimate comes from <c>sideGrad</c>, so if it were
        /// direction-dependent the strategy would engage at a different styled width on a diagonal than on a
        /// horizontal — the same class of bug one level up. Measured at 1.5 px, mid-transition,
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
            Track(cameraGo);
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            // A 1.5 px line has no a == 1 plateau, so a wide in-frame reference normalises it; its own samples
            // would inflate each strategy differently.
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
                snap.WritePng($"line-aa-a7-direction-{label}.png");

                Frame pixels     = snap.Pixels;
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
            Track(cameraGo);
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
                snap.WritePng($"line-aa-a7-miter-{label}.png");

                Frame pixels     = snap.Pixels;
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
                DestroyFixture(refGo, refMat);
                DestroyFixture(go, mat);
            }
        }

        /// <summary>
        /// <b>A7.2c.</b> A round cap must still render under every strategy — the one place a
        /// zero-<c>extrudeN</c> pivot meets the strategy code. Ink past the endpoint on
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
            Track(cameraGo);
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
                snap.WritePng($"line-aa-a7-roundcap-{label}.png");

                Frame pixels     = snap.Pixels;
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
                DestroyFixture(refGo, refMat);
                DestroyFixture(go, mat);
            }
        }

        // ─── T7 — the missing-push fail-safe ────────────────────────────────────────────────────

        /// <summary>
        /// <b>T7.</b> With <c>_MapFrameMetersPerDevicePixel</c> UNSET (0), a pixel-width band must still
        /// render, with a plateau clearly distinct from the background, at its styled width.
        ///
        /// <para>Non-obvious why: an unpushed shader global reads 0, and a zero width collapses every road to
        /// a plausible 1 device-px hairline (the AA pad still extrudes). The fallback, <c>MapPixelsToWorld</c>
        /// at the vertex, only keeps the size plausible; production and <c>MapCamera</c> fixtures never reach
        /// it. Under this ortho camera it returns <see cref="MetresPerPx"/> exactly, so the width clause can be
        /// tight, and that clause is the one a missing guard fails (1.000 px against 16).</para>
        /// </summary>
        [Test]
        public void PixelWidthBand_StillRenders_WhenTheFrameConstantIsUnset()
        {
            const float StyledPx = 16f;

            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (go, mat) = BuildHorizontalLine(StyledPx, widthIsPixels: true,
                                                color: new Color(0.95f, 0.60f, 0.15f, 1f));

            // AFTER BuildCamera, which pushes it — this is precisely the state a render path that forgot the
            // push leaves behind, reproduced rather than simulated.
            Shader.SetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel, 0f);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-aa-t7-frame-constant-unset.png");

                Frame pixels     = snap.Pixels;
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
                DestroyFixture(go, mat);
            }
        }

        // ─── Is interpolating `hairlineScale` sound? ────────────────────────────────────────────

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

            // Non-local invariant: the global is PROCESS state, so this camera pushes its OWN constant,
            // 2·d·tan(fov/2)/H at the look-at, derived like MapCamera.MetresPerDevicePixel and never a literal.
            // Otherwise it would render against the last ortho fixture's 0.2734375 m/px (~1.55× wrong).
            float distanceToLookAt = -camera.transform.position.y / camera.transform.forward.y;
            Shader.SetGlobalFloat(
                ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel,
                2f * distanceToLookAt * math.tan(math.radians(camera.fieldOfView * 0.5f)) / SnapH);
            return (go, camera);
        }

        /// <summary>
        /// <c>hairlineScale</c> is a per-vertex scalar on a varying, and it is NOT constant along a pixel-width
        /// line: <c>widthWorld</c> takes the frame constant while the AA pad stays per-vertex, so a receding
        /// hairline falls below the 2 device-px floor and the compensation dims it. Over a 1 px
        /// <c>_HAIRLINE_SOLID_CORE</c> line against a 4 px <c>a == 1</c> companion at the same depths, it must
        /// (a) never vanish, (b) never rise with depth, (c) never jump between rows, and (d) stay under 1.0.
        /// </summary>
        [Test]
        public void HairlineScale_DegradesSmoothlyWithDepth_UnderTilt()
        {
            var (cameraGo, camera) = BuildTiltedCamera();
            Track(cameraGo);
            var color = new Color(0.95f, 0.60f, 0.15f, 1f);
            // Reference: a wide near line, an a == 1 plateau in the same frame. The ground is visible only
            // from z ≈ -33 outward at this pose, so it sits beyond that.
            var (refGo, refMat) = BuildLine(
                new List<double2> { new double2(-30.0, -20.0), new double2(30.0, -20.0) },
                12f, widthIsPixels: true, color: color);
            // Non-obvious why: the 4 px a == 1 companion's peak IS the local PBR shading level, so dividing by
            // it cancels shading. The lines are SUBDIVIDED along z, because a long segment interpolates its
            // extrusion linearly while the needed world offset grows with depth, which drifts the width.
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
                snap.WritePng("line-aa-a7-tilt-solidcore.png");

                Frame pixels     = snap.Pixels;
                float3 background = BackgroundLinear(pixels);
                // Plateau from the near reference bar, located in the lower frame, not a whole-column max,
                // which could land at any depth.
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
                    // RAW, unsaturated: the same-depth ratio cancels shading and calibration only while
                    // neither term has clamped.
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

                // (a) IT MUST NOT VANISH: the loop skips faint rows, so the count is the vacuity guard.
                // Limitation: five of the seven candidate rows survive, so the bound has zero margin; a drop to
                // four means a hairline stopped rendering at depth.
                Assert.That(rowsMeasured, Is.GreaterThanOrEqualTo(5),
                    $"Only {rowsMeasured} of the sampled depths carried both lines. Either the fixture no " +
                    "longer spans a useful depth range, or a hairline stopped rendering at depth — the " +
                    "second would be a real regression, so do not just widen the sweep.");
                Assert.That(lo, Is.GreaterThan(0.10f),
                    $"The clamped hairline falls to {lo:F3} of an a == 1 line at the same depth — it is " +
                    "vanishing. SolidCore exists so a receding hairline keeps a solid 2 device-px core and " +
                    $"pays for it in alpha; measured 0.284 at the deepest sampled row. {report}");

                // (b) DEGRADATION, not drift: a fixed world width only narrows with depth, so a RISE means the
                // scalar tracks something other than the rendered width.
                Assert.That(maxRise, Is.LessThan(0.02f),
                    $"The hairline's coverage ratio RISES by {maxRise:F4} with depth. Under a constant world " +
                    "width the rendered band can only get narrower, so hairlineScale can only fall. A rise " +
                    $"means the scalar is not tracking the rendered width. {report}");

                // (c) THE INTERPOLATION QUESTION: an unsound varying or a discontinuous clamp shows as a JUMP
                // between adjacent depths (measured steps: 0.127…0.153).
                Assert.That(maxStep, Is.LessThan(0.30f),
                    $"hairlineScale steps by {maxStep:F4} between adjacent sampled depths, against a smooth " +
                    "0.127…0.153 measured. A jump is what an unsound interpolation of this varying, or a " +
                    $"discontinuous clamp, would look like. {report}");

                // (d) THE COMPENSATION IS APPLIED: near 1.0, the band is clamped wider without paying in alpha,
                // so a 1 px road renders twice as prominent as styled.
                Assert.That(hi, Is.LessThan(0.95f),
                    $"The clamped hairline reaches {hi:F3} of an a == 1 line at the same depth; near 1.0 " +
                    $"means the energy compensation is not being applied. {report}");
            }
            finally
            {
                DestroyFixture(refGo, refMat);
                DestroyFixture(wideGo, wideMat);
                DestroyFixture(go, mat);
            }
        }
    }

    // The tile-buffer clip on real pixels: T3, clipping removes the double-painted alpha BAND along a seam; T5,
    // clipping opens no CRACK. Both build two adjacent tiles through the REAL worker fan-out, at real origins.

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileSeamSnapshotTests — T3 + T5 — the two pixel questions the tile-buffer clip stage exists to settle
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileSeamSnapshotTests
    {
        private const int SnapW = 512;
        private const int SnapH = 512;

        private const double TileExtent = 4096.0;

        // z1/x0/y0 and z1/x1/y0 abut along Mercator x = 0. z1 is the WORST case for RTC float magnitudes
        // (a tile spans ~2.0e7 m), so a crack absent here is absent at z14.
        private static readonly TileId WestTile = new TileId { Z = 1, X = 0, Y = 0 };
        private static readonly TileId EastTile = new TileId { Z = 1, X = 1, Y = 0 };

        // The camera looks straight down at a point ON the shared seam, well inside both tiles vertically.
        private const float SeamWorldX = 0f;
        private const float SeamWorldZ = 1.0e7f;
        private const float CamY       = 1.0e6f;

        // The 64-unit-buffered ring (-64,-64) → (4160,-64) → (4160,4160) → (-64,4160), zigzag-encoded per the
        // MVT spec: zigzag(-64) = 127 and zigzag(±4224) = 8448/8447.
        private static readonly uint[] BufferedRingGeometry =
            { 9, 127, 127, 26, 8448, 0, 0, 8448, 8447, 0, 15 };

        // ── T3: the band ──────────────────────────────────────────────────────────────────────────

        // Seam and reference strips at the T3 framing: the seam strip sits inside the 128 px band, and the rows
        // are interior to BOTH tiles.
        private const int BandStripX0 = 236, BandStripX1 = 276;
        private const int LeftRefX0   =  40, LeftRefX1   = 120;
        private const int RightRefX0  = 392, RightRefX1  = 472;
        private const int SlabY0      = 200, SlabY1      = 312;

        // Half-width of the T3 view in world metres. The b=64 overlap strip is 2 × 64/4096 × tileSpan ≈
        // 626 km wide, which lands ~128 px across at this framing.
        private const float BandViewHalfWidth = 1.25e6f;

        [Test]
        public void TwoNeighbours_TranslucentFill_BandAtTheSeamIsPresentUnclipped_AndGoneWhenClipped()
        {
            // α = 0.5, never opaque: an OPAQUE fill composites identically in the double-painted strip, so this
            // tooth would be inert. 1 − (1−α)² = 0.75 against α = 0.5 ⇒ the strip reads 1.5× the interior.
            var (bufferedBand, bufferedLeft, bufferedRight) =
                MeasureSeamStrip(TileBufferClip.KeepTileUnits(64.0), alpha: 0.5f,
                                 BandViewHalfWidth, "tile-seam-band-b64.png");
            var (clippedBand, clippedLeft, clippedRight) =
                MeasureSeamStrip(TileBufferClip.KeepTileUnits(0.0), alpha: 0.5f,
                                 BandViewHalfWidth, "tile-seam-band-b0.png");

            double bufferedInterior = 0.5 * (bufferedLeft + bufferedRight);
            double clippedInterior  = 0.5 * (clippedLeft + clippedRight);

            Debug.Log($"[TileSeam T3] b=64: seam={bufferedBand:F4} interior={bufferedInterior:F4} " +
                      $"ratio={bufferedBand / bufferedInterior:F4} | " +
                      $"b=0: seam={clippedBand:F4} interior={clippedInterior:F4} " +
                      $"ratio={clippedBand / clippedInterior:F4}");

            // `||`, not `&&`: if only ONE arm fails to render, an `&&` guard does not fire and the ratio
            // below divides by ~0, asserting on a NaN with a message that blames the clip.
            if (bufferedInterior < 0.01 || clippedInterior < 0.01)
            {
                Assert.Inconclusive(
                    "Both interior references are ~black — the tiles did not render. Likely no GPU context " +
                    "in batch EditMode; re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
            }

            // Arm 1, the RED arm in the SAME test: unclipped neighbours double-paint the strip, so the linear
            // ratio is 2 − α = 1.5.
            Assert.Greater(bufferedBand / bufferedInterior, 1.4,
                $"unclipped (b=64) the seam strip must read ~1.5× the interior in linear light (double-painted " +
                $"α=0.5 composites to 0.75, not 0.5). Measured seam={bufferedBand:F4}, " +
                $"interior={bufferedInterior:F4}. If this is ~1.0 the band is not being reproduced and arm 2 " +
                "proves nothing.");

            // Arm 2: clipped, the tiles do not overlap and the strip matches the interior. A RATIO stays
            // scale-free as the fill colour darkens.
            Assert.LessOrEqual(math.abs(clippedBand / clippedInterior - 1.0), 0.02,
                $"clipped (b=0) the seam strip must read the same as the interior. Measured " +
                $"seam={clippedBand:F4}, interior={clippedInterior:F4} " +
                $"(ratio={clippedBand / clippedInterior:F4}).");
        }

        // ── The outward boundary band at a seam ───────────────────────────────────────────────────

        // World x = 0 lands on the boundary between columns 255 and 256, so these two columns are exactly the
        // pixels a one-device-pixel band from each neighbour would reach into.
        private const int SeamRimX0 = 255, SeamRimX1 = 257;

        // Non-obvious why: clipped neighbours ABUT, so an outward band along the cut is ink over existing fill,
        // f(1-f) relative and worst on translucent layers, hence alpha 0.3. An unsuppressed band reads two
        // half-covered columns at ~1.35×, 17× the tolerance.
        // Limitation: both tiles are full-extent, so the whole band is suppressed here and deleting the band
        // node would pass. FillBandJobTests and FillBoundaryBandRenderTests pin that a real edge keeps its band.
        [Test]
        public void TwoNeighbours_TranslucentFill_ClippedAtTheTileBoundary_LeaveNoBandRimOnTheSeam()
        {
            var (rim, interior) = MeasureSeamRim(alpha: 0.3f, "tile-seam-band-rim-a03.png");

            if (interior < 0.01)
            {
                Assert.Fail(
                    "The interior reference is ~black — the tiles did not render. Likely no GPU context in " +
                    "batch EditMode; re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
            }

            // Two-sided on purpose. Above 1 is a band rim; BELOW 1 is a background
            // trench, which is the artefact that rejected every inset placement — one bound catches both.
            Assert.LessOrEqual(math.abs(rim / interior - 1.0), 0.02,
                $"the two abutting tiles' seam must read the same as their interiors. Measured rim={rim:F4}, " +
                $"interior={interior:F4} (ratio={rim / interior:F4}). Above 1 is a boundary band painted " +
                "along the tile cut, over a neighbour that already abuts there; below 1 is background " +
                "showing through, which no placement in this mechanism may produce.");
        }

        /// <summary>Renders the clipped two-tile seam and returns the seam columns' mean linear luminance
        /// against the mean of the two interior references, logging the per-column profile across the seam so
        /// a red says WHERE the ink sits rather than only that a ratio moved.</summary>
        /// <param name="alpha">The fill layer's opacity.</param>
        /// <param name="pngName">Snapshot file name.</param>
        /// <returns>The seam strip's luminance and the interior reference's.</returns>
        private static (double rim, double interior) MeasureSeamRim(float alpha, string pngName)
        {
            using var scene = new SeamScene(TileBufferClip.KeepTileUnits(0.0), alpha, BandBackground, BandViewHalfWidth);
            {
                Frame px = scene.Render(pngName);

                double interior = 0.5 * (MeanLuminance(px, LeftRefX0, LeftRefX1)
                                       + MeanLuminance(px, RightRefX0, RightRefX1));
                double rim = MeanLuminance(px, SeamRimX0, SeamRimX1);

                var profile = new System.Text.StringBuilder();
                for (int x = SnapW / 2 - 6; x < SnapW / 2 + 6; x++)
                    profile.Append($" {x}:{MeanLuminance(px, x, x + 1):F4}");
                Debug.Log($"[TileSeam rim] alpha={alpha} rim={rim:F4} interior={interior:F4} " +
                          $"ratio={rim / math.max(interior, 1e-9):F4} profile:{profile}");

                return (rim, interior);
            }
        }

        // ── T5: the crack ─────────────────────────────────────────────────────────────────────────

        // Columns scanned for a background-coloured gap, centred on the seam (world x = 0 projects to the
        // middle column at both framings below).
        private const int CrackScanX0 = 246, CrackScanX1 = 266;

        [Test]
        public void TwoNeighbours_OpaqueFill_ClippedAtTheTileBoundary_LeaveNoCrack()
        {
            // Two altitudes: most of the seam in view, and zoomed so one tile spans ~4× the viewport.
            AssertNoCrackAtAltitude(8.0e6f, "tile-seam-crack-wide.png");
            AssertNoCrackAtAltitude(2.5e6f, "tile-seam-crack-zoomed.png");
        }

        private static void AssertNoCrackAtAltitude(float viewHalfWidth, string pngName)
        {
            using var scene = new SeamScene(TileBufferClip.KeepTileUnits(0.0), alpha: 1f,
                                      background: CrackBackground, viewHalfWidth: viewHalfWidth);
            {
                Frame px = scene.Render(pngName);

                int backgroundPixels = 0;
                int longestRun       = 0;
                for (int y = SlabY0; y < SlabY1; y++)
                {
                    int run = 0;
                    for (int x = CrackScanX0; x < CrackScanX1; x++)
                    {
                        if (IsCrackBackground(px, x, y))
                        {
                            backgroundPixels++;
                            run++;
                            longestRun = math.max(longestRun, run);
                        }
                        else run = 0;
                    }
                }

                double scanLuminance = MeanLuminance(px, CrackScanX0, CrackScanX1);
                Debug.Log($"[TileSeam T5] viewHalfWidth={viewHalfWidth:F0} m " +
                          $"({viewHalfWidth * 2.0 / SnapW:F0} m/px): background pixels on the seam = " +
                          $"{backgroundPixels}, longest run = {longestRun} px, " +
                          $"scan mean luminance = {scanLuminance:F4}");

                // Non-vacuity: "no background-coloured pixel" is trivially true over an unrendered (black)
                // frame, which is neither fill nor magenta. The seam band must actually be covered by fill.
                Assert.Greater(scanLuminance, 0.05,
                    $"the scanned seam band is nearly black (mean luminance {scanLuminance:F4}) — the tiles " +
                    "did not render there, so a zero crack count would prove nothing.");

                Assert.AreEqual(0, backgroundPixels,
                    $"clipping at the tile boundary opened a crack: {backgroundPixels} background-coloured " +
                    $"pixels on the seam (longest run {longestRun} px) at viewHalfWidth={viewHalfWidth:F0} m. " +
                    "The default must then be the smallest margin that closes it — record the width.");
            }
        }

        // ── Measurement ───────────────────────────────────────────────────────────────────────────

        private static readonly Color BandBackground  = new Color(0f, 0f, 0f, 1f);
        private static readonly Color CrackBackground = new Color(1f, 0f, 1f, 1f); // magenta: nothing else is

        /// <summary>Magenta-dominant: red and blue high, green far below them. Stated as a RATIO rather than
        /// absolute thresholds so a linear-vs-sRGB readback cannot flip it. The fill is neutral grey
        /// (R ≈ G ≈ B), so it can never satisfy <c>G × 3 &lt; R</c>.</summary>
        private static bool IsCrackBackground(Frame frame, int x, int y)
        {
            Color32 px = frame[x, y];
            return px.r > 100 && px.b > 100 && px.g * 3 < px.r;
        }

        /// <summary>Mean luminance of the seam strip and of the two interior reference strips.</summary>
        private static (double band, double left, double right) MeasureSeamStrip(
            TileBufferClip clip, float alpha, float viewHalfWidth, string pngName)
        {
            using var scene = new SeamScene(clip, alpha, BandBackground, viewHalfWidth);
            {
                Frame px = scene.Render(pngName);
                return (MeanLuminance(px, BandStripX0, BandStripX1),
                        MeanLuminance(px, LeftRefX0,   LeftRefX1),
                        MeanLuminance(px, RightRefX0,  RightRefX1));
            }
        }

        /// <summary>
        /// Mean Rec.709 luminance in <b>LINEAR</b> light. The readback is sRGB-ENCODED, and the whole point of
        /// this tooth is an alpha-compositing ratio — which is a linear-light quantity. Measured on raw bytes a
        /// true 1.5× overdraw reads as only 1.5^(1/2.4) ≈ 1.18×, so an encoded-space threshold would be
        /// calibrating around the gamma curve instead of measuring the artefact.
        /// </summary>
        private static double MeanLuminance(Frame frame, int x0, int x1)
        {
            double sum = 0.0;
            int count  = 0;
            for (int y = SlabY0; y < SlabY1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Color32 px = frame[x, y];
                    sum += 0.2126 * SrgbToLinear(px.r)
                         + 0.7152 * SrgbToLinear(px.g)
                         + 0.0722 * SrgbToLinear(px.b);
                    count++;
                }
            }
            return count > 0 ? sum / count : 0.0;
        }

        private static double SrgbToLinear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : math.pow((c + 0.055) / 1.055, 2.4);
        }

        // ── The scene ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Two neighbouring tiles built through the real worker fan-out under one clip setting, a
        /// top-down ortho camera on their shared seam, and the off-screen render target.</summary>
        private sealed class SeamScene : System.IDisposable
        {
            private readonly GameObject       _sceneGo;
            private readonly GameObject       _cameraGo;
            private readonly Camera           _camera;
            private readonly SnapshotRenderer _snap;
            private readonly List<Object>     _disposables = new List<Object>();

            private readonly int          _prevQuality;
            private readonly AmbientMode  _prevAmbientMode;
            private readonly Color        _prevAmbientLight;

            public SeamScene(TileBufferClip clip, float alpha, Color background, float viewHalfWidth)
            {
                _prevQuality      = QualitySettings.GetQualityLevel();
                _prevAmbientMode  = RenderSettings.ambientMode;
                _prevAmbientLight = RenderSettings.ambientLight;
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode  = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

                _sceneGo = new GameObject("TileSeamScene");

                var lightGo = new GameObject("DirLight");
                lightGo.transform.SetParent(_sceneGo.transform);
                lightGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // straight down: both tiles shade alike
                var light = lightGo.AddComponent<Light>();
                light.type      = LightType.Directional;
                light.intensity = 1f;

                // A mid-grey base keeps the double-painted strip well clear of saturation, which would flatten
                // the 1.5× ratio T3 measures.
                var material = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
                material.SetColor("_BaseColor", new Color(0.35f, 0.35f, 0.35f, 1f));
                material.SetFloat("_Opacity", alpha);
                // Both tiles are ONE style layer, so they share a queue; the band comes from two draws of the
                // same layer overlapping, not from layer order.
                material.renderQueue = LayerDrawOrder.ComputeQueues(1)[0];
                _disposables.Add(material);

                var projection = new WebMercatorProjection();
                AddTile(WestTile, clip, projection, material);
                AddTile(EastTile, clip, projection, material);

                _cameraGo = new GameObject("TileSeamCamera");
                _camera   = _cameraGo.AddComponent<Camera>();
                _camera.transform.position = new Vector3(SeamWorldX, CamY, SeamWorldZ);
                _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                _camera.orthographic       = true;
                _camera.orthographicSize   = viewHalfWidth; // square target ⇒ half-width == half-height
                _camera.nearClipPlane      = 1f;
                _camera.farClipPlane       = CamY * 4f;
                _camera.clearFlags         = CameraClearFlags.SolidColor;
                _camera.backgroundColor    = background;
                _camera.enabled            = false;

                _snap = new SnapshotRenderer(SnapW, SnapH);
            }

            /// <summary>Renders and returns the frame.</summary>
            public Frame Render(string pngName)
            {
                _snap.Render(_camera);
                _snap.WritePng(pngName);
                return _snap.Pixels;
            }

            private void AddTile(TileId id, TileBufferClip clip, IProjection projection, Material material)
            {
                double3 origin = TileRenderOrigin.Project(id, projection);
                Mesh mesh = BuildTileMesh(id, origin, clip, projection);
                Assert.IsNotNull(mesh, $"tile {id.Z}/{id.X}/{id.Y} produced no fill mesh.");
                _disposables.Add(mesh);

                var go = new GameObject($"Tile_{id.Z}_{id.X}_{id.Y}");
                go.transform.SetParent(_sceneGo.transform, worldPositionStays: false);
                // The RTC bake origin IS the tile placement — the same double3 the production tile transform
                // uses; cast to float only here, at the Unity boundary.
                go.transform.localPosition = new Vector3((float)origin.x, (float)origin.y, (float)origin.z);
                go.AddComponent<MeshFilter>().sharedMesh    = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
            }

            public void Dispose()
            {
                _snap?.Dispose();
                foreach (var d in _disposables) if (d != null) Object.DestroyImmediate(d);
                if (_sceneGo  != null) Object.DestroyImmediate(_sceneGo);
                if (_cameraGo != null) Object.DestroyImmediate(_cameraGo);
                QualitySettings.SetQualityLevel(_prevQuality, false);
                RenderSettings.ambientMode  = _prevAmbientMode;
                RenderSettings.ambientLight = _prevAmbientLight;
            }
        }

        /// <summary>Builds one tile's fill mesh through the production worker fan-out, so the clip travels the
        /// real <see cref="TileLayerProcessContext"/> → <see cref="ITileMeshRenderLayer.BuildGraphRequest"/>
        /// path. With no pump here, it drives the graph synchronously as <c>TileManager.KickMeshBuild</c>'s pump
        /// does (<c>ScheduleMeasureFromDecode</c> → <c>CompleteMeasureAndScheduleWrite</c> →
        /// <c>CompleteWriteAndTakePayloads</c>).</summary>
        private static Mesh BuildTileMesh(TileId id, double3 origin, TileBufferClip clip, IProjection projection)
        {
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false, geometry: BufferedRingGeometry);
            // The synthetic layer owns its buffer, materialized at construction like a decoded
            // one — and stamped with the SAME id the context builds at, which is now the only copy.
            using var seamTile = new InMemoryDecodedTile(
                new InMemoryTileLayer(SeamSourceLayerName, id, new IFeature[] { feature }, (uint)TileExtent));

            var styleLayer = new StyleLayer { Id = "seam-fill", SourceLayer = SeamSourceLayerName };
            var paint      = TestStyle.FillPaint("{\"fill-color\":\"#ffffff\"}");
            var fillLayer  = new SeamFillRenderLayer(styleLayer, paint);

            var context = new TileLayerProcessContext
            {
                Tile             = id,
                Zoom             = id.Z,
                TileOriginRender = origin,
                Projection       = projection,
                BufferClip       = clip,
            };

            var processor = TileMeshLayerProcessor.AllocateForKick(fillLayer, materialIndex: 0);
            var decode    = new SharedDisposable<IDecodedTile>(new SeamTileDecoder(seamTile).Decode(id, SeamDecoderBytes));

            TilePrologueOutput output = TileLayerProcessorRunner.RunWorkerPass(
                decode, in context, new ITileMeshLayerProcessor[] { processor });
            Assert.AreEqual(1, output.Layers.Length);

            // ScheduleMeasureFromDecode takes ownership of `decode` from here — released exactly once, by
            // graph.Dispose() below (see that method's own doc).
            TileBuildGraph graph = TileBuildGraph.ScheduleMeasureFromDecode(output.Layers, decode);
            try
            {
                graph.CompleteMeasureAndScheduleWrite(out _);
                MeshDataPayload[] payloads = graph.CompleteWriteAndTakePayloads();
                Assert.AreEqual(1, payloads.Length);
                return payloads[0]?.Upload();
            }
            finally { graph.Dispose(); }
        }

        // ── Fakes (mirroring NonMvtDecoderFanOutTests' fan-out fixtures) ──────────────────────────

        private const string SeamSourceLayerName = "seam-fixture-layer";

        // Never decoded: SeamTileDecoder ignores the bytes and returns the synthetic tile.
        private static readonly byte[] SeamDecoderBytes = { 0x1A, 0x64 };

        private sealed class SeamTileDecoder : ITileDecoder
        {
            private readonly IDecodedTile _tile;
            public SeamTileDecoder(IDecodedTile tile) => _tile = tile;
            public IDecodedTile Decode(TileId id, byte[] bytes) => _tile;
        }

        /// <summary>Mirrors <c>FillRenderLayer.BuildGraphRequest</c>'s forward without needing a real
        /// Material — this scene binds its own.</summary>
        private sealed class SeamFillRenderLayer : ITileMeshRenderLayer
        {
            private readonly Fill.PaintProperties _paint;
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material Material => null;

            public SeamFillRenderLayer(StyleLayer styleLayer, Fill.PaintProperties paint)
            {
                StyleLayer = styleLayer;
                _paint     = paint;
            }

            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public int TransitioningCount => 0;
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
            {
                FillMeshPipeline.LayerInput input = StyledFillTileBuilder.BuildLayerInput(
                    selected, geometry, _paint, context.Zoom, context.TileOriginRender, out var colors,
                    context.Projection, layout: null, context.BufferClip, context.Buffers);
                if (!input.RingVisitOrder.IsCreated) return null;
                return FillLayerBuild.Rent(input, colors, materialIndex, payloadName);
            }
        }
    }
}
