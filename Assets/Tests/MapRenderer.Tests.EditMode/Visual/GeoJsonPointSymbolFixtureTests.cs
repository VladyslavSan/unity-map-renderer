// Unity EditMode only — Stage G-V1, the tilted point-symbol fixture.
// NOT registered in Tools/core-tests/core-tests.csproj (engine-bound: drives a real MapViewComponent
// through the geojson→symbol pipeline and a live SymbolPlacementSystem).
//
// Two point features, IDENTICAL text ("A"), DISTINCT lon/lat, mid-tile, under a tilt=45° camera. Proves the
// geojson→symbol pipeline lands ink where the camera actually projects the authored coordinate — the FIRST
// proof of point-symbol positional correctness in this kit (plan §Deferred scope fence: text point-symbols
// only; no icons, no line/curved placement, no seam interaction, no perspective-foreshortening suite).
//
// THE ORACLE. `PredictedScreenPx` is NOT `IProjection.GroundToScreen` — that method is documented as exact
// only at tilt=0 (IProjection.cs:76, "Exact inverse of ScreenToGround at zero tilt") and its Web-Mercator
// implementation carries no tilt term at all (WebMercatorProjection.cs:98-132), so at this fixture's tilt=45°
// it would predict the WRONG screen position by construction, not merely imprecisely. The oracle instead
// re-derives the two-line formula `MapView.BuildSceneFrame` uses (`SceneOriginRender = proj.Project(lookAt)`)
// from PUBLIC API only (`MapCamera.Projection` / `MapCamera.CurrentProperties`), then projects the resulting
// Unity-world point through the LIVE camera's own matrix (`GroundRuler.ProjectPx` → `WorldToScreenPoint`) —
// the same "project a KNOWN world point through the live camera" oracle discipline `OffLookAtSymbolScene`
// established (`GroundRuler.cs`'s own doc: "a second implementation of the projection inside the test is the
// thing most likely to be wrong"). This is shared reference infra (projection + camera-pose bookkeeping), not
// the mechanism under test (SymbolFeatureExtractor / SymbolPlacementSystem).
//
// NO FLIP. VisualFrame.RawPixels is bottom-left origin (SnapshotRenderer's own doc); Camera.WorldToScreenPoint
// is also bottom-left, +y up. InkStatsIn's window coordinates and the oracle's predicted px therefore compare
// directly, with no coordinate conversion anywhere in this file.

#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    internal class GeoJsonPointSymbolFixtureTests
    {
        // ── The proof geometry: one tile, two mid-tile points, generous margins on every side ──────────
        private static readonly TileId ProofTile = new TileId { Z = 6, X = 40, Y = 25 };

        // Tile-local unit-square coordinates. Separated in LONGITUDE (U) at a SHARED latitude (V): at
        // heading=0 the camera's cross-azimuth (iso-view-depth) axis is east/west
        // (CameraPoseMath.ComputeRelativePose: pos = (0, alt·cosT, -alt·sinT) at heading 0, so the receding
        // direction is north/south) — so both anchors sit at very nearly the SAME view depth, keeping the
        // glyph's systematic ink-centroid bias the SAME vector for both symbols (what the residual-agreement
        // clause of T-Pos needs). 0.30/0.70 are each ≥25% of the tile's extent from every edge (asserted, not
        // assumed — AssertMidTileFence — the plan's mid-tile fence, plan §3).
        private const double PointAU = 0.30, PointBU = 0.70, PointV = 0.50;
        private const double MidTileFenceMinFraction = 0.25;

        private const string FontName   = "Fixture Point Label Font";
        private const string Text  = "A";
        private const double TextSizePx = 32.0;
        private const double TiltDeg    = 45.0;
        private const int    SizePx     = 512;

        // Half-size of the predicted-anchor ink window, px — generous around a single 32px-text-size glyph
        // cell (measured ~19px wide, ~355 ink px) so a real but small placement error still lands the ink
        // inside the window. This is a PRESENCE FLOOR ONLY (T-Present's job) — 80px is far wider than the
        // real cell on purpose, since T-Pos's own tight tolerance N is what actually bounds the position; do
        // not read WindowHalfPx as a position claim.
        private const int WindowHalfPx = 80;

        // T-Pos: N, the ink-centroid-to-predicted-anchor tolerance, px. Tuned from the MEASURED residual
        // (printed by every T-Pos run via TestContext.WriteLine): a first green run measured |residualA|
        // =1.28px, |residualB|=1.35px — the systematic text-anchor/glyph-metric offset an ink reading
        // carries (this is not OffLookAt's 2px GEOMETRY bound; that reads staged vertex positions, not
        // rendered ink). N=5px is a ~3.7x margin over the observed max, tight enough to catch a real
        // placement defect (RED-verified at a >=40px injected offset) while tolerant of ordinary
        // cross-machine AA/rasterization jitter.
        private const double PositionToleranceN = 5.0;

        // The two symbols' residual VECTORS (identical text ⇒ identical systematic bias) must agree within a
        // few px — the sharper clause that isolates a real per-symbol placement error from the shared bias,
        // which the plain distance-to-anchor bound alone cannot (both could be biased by the same amount in
        // the same direction and still individually pass while genuinely wrong relative to each other — not
        // this fixture's failure mode, but the clause exists for exactly that discrimination). Measured
        // agreement on the same green run: 0.12px; 2.0px leaves a wide margin for jitter while staying far
        // tighter than PositionToleranceN.
        private const double ResidualAgreementToleranceN = 2.0;

        private const int OnScreenMarginPx = 20;

        // Anti-blob floors/ceilings for T-Distinct (plan lessons tooth-membership-is-not-coverage), tuned
        // from a measured green run: inkBetween=0, whole-frame filled fraction=0.27%. Not exactly zero, to
        // tolerate ordinary AA fringe.
        private const int    BetweenBandInkFloor    = 2;
        private const double WholeFrameFillCeiling  = 0.01;

        private const string NoGpuMessage =
            "Scene render and blank-background control are both all-black: no GPU context in batch EditMode. " +
            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode";

        private static byte[] _glyphBytes;

        // ── Fixture geometry helpers ──────────────────────────────────────────────────────────────────

        private static GeoCoordinate3D ProofLookAt()
        {
            double2 c = ProofTile.ToLonLat(0.5, 0.5, 1.0);
            return new GeoCoordinate3D { Longitude = c.x, Latitude = c.y, Altitude = 0.0 };
        }

        private static (double lon, double lat) TileLocal(double u, double v)
        {
            double2 ll = ProofTile.ToLonLat(u, v, 1.0);
            return (ll.x, ll.y);
        }

        private static void AssertMidTileFence()
        {
            AssertFraction(PointAU, "point A's U");
            AssertFraction(PointBU, "point B's U");
            AssertFraction(PointV, "both points' V");

            static void AssertFraction(double f, string what)
            {
                Assert.That(f, Is.GreaterThanOrEqualTo(MidTileFenceMinFraction)
                                .And.LessThanOrEqualTo(1.0 - MidTileFenceMinFraction),
                    $"fixture precondition (mid-tile fence): {what} ({f:F2}) must sit >= " +
                    $"{MidTileFenceMinFraction:P0} of the tile's extent from every edge — no cross-tile " +
                    "seam interaction is in scope for this stage.");
            }
        }

        // ── Scene builders ────────────────────────────────────────────────────────────────────────────

        private static VisualScene BuildScene(
            (double lon, double lat) a, (double lon, double lat) b, int expectedQuads)
        {
            return VisualScene.New()
                .Source("points", GeoJson.Points((a.lon, a.lat, Text), (b.lon, b.lat, Text)))
                .Layer(VisualLayer.SymbolText("labels").Source("points").TextField("name")
                    .TextSize(TextSizePx).TextFont(FontName).TextColor("#ffffff"))
                .Glyphs(FontName, LoadGlyphBytes())
                .Camera(ProofLookAt(), zoom: ProofTile.Z, tilt: TiltDeg)
                .ExpectSymbolQuads(expectedQuads);
        }

        private static VisualScene BuildEmptyScene()
        {
            return VisualScene.New()
                .Source("points", GeoJson.FeatureCollection(
                    "{\"type\":\"FeatureCollection\",\"features\":[]}"))
                .Layer(VisualLayer.SymbolText("labels").Source("points").TextField("name")
                    .TextSize(TextSizePx).TextFont(FontName).TextColor("#ffffff"))
                .Glyphs(FontName, LoadGlyphBytes())
                .Camera(ProofLookAt(), zoom: ProofTile.Z, tilt: TiltDeg)
                .ExpectSymbolQuads(0);
        }

        // ── The oracle (plan §2, see file header) ─────────────────────────────────────────────────────

        private static double2 PredictedScreenPx(VisualFrame frame, double lon, double lat)
        {
            var camera = frame.MapView.Camera;
            IProjection proj = camera.Projection;
            CameraProperties cp = camera.CurrentProperties;
            var lookAt = new GeoCoordinate
            {
                Latitude  = proj.ClampValidLatitude(cp.LookAt.Latitude),
                Longitude = cp.LookAt.Longitude,
            };
            double3 sceneOriginRender = proj.Project(lookAt);
            double3 pointRender       = proj.Project(new GeoCoordinate { Latitude = lat, Longitude = lon });
            double3 worldUnity        = pointRender - sceneOriginRender;
            return GroundRuler.ProjectPx(frame.Camera, worldUnity);
        }

        private static void WindowAround(double2 center, out int x0, out int y0, out int x1, out int y1)
        {
            int cx = (int)math.round(center.x);
            int cy = (int)math.round(center.y);
            x0 = cx - WindowHalfPx; x1 = cx + WindowHalfPx;
            y0 = cy - WindowHalfPx; y1 = cy + WindowHalfPx;
        }

        private static void AssertOnScreenWithMargin(VisualFrame frame, double2 predicted, string label)
        {
            Assert.That(predicted.x, Is.GreaterThanOrEqualTo(OnScreenMarginPx)
                                        .And.LessThanOrEqualTo(frame.Width - OnScreenMarginPx),
                $"label {label}'s predicted screen X ({predicted.x:F1}) must land on-screen with margin " +
                $"(frame width {frame.Width})");
            Assert.That(predicted.y, Is.GreaterThanOrEqualTo(OnScreenMarginPx)
                                        .And.LessThanOrEqualTo(frame.Height - OnScreenMarginPx),
                $"label {label}'s predicted screen Y ({predicted.y:F1}) must land on-screen with margin " +
                $"(frame height {frame.Height})");
        }

        // ── T-Present + T-Pos (payoff) ────────────────────────────────────────────────────────────────

        [Test]
        public void Present_And_Pos_InkLandsAtEachAuthoredAnchor_AndResidualsAgree()
        {
            AssertMidTileFence();

            (double lon, double lat) a = TileLocal(PointAU, PointV);
            (double lon, double lat) b = TileLocal(PointBU, PointV);
            using var scene = BuildScene(a, b, expectedQuads: 2);
            VisualFrame frame = scene.Render(SizePx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            // A size mismatch here would silently bias every centroid rather than failing loud.
            Assert.That(frame.Camera.pixelWidth, Is.EqualTo(frame.Width),
                "render target and Unity camera viewport width must agree");
            Assert.That(frame.Camera.pixelHeight, Is.EqualTo(frame.Height),
                "render target and Unity camera viewport height must agree");

            double2 predA = PredictedScreenPx(frame, a.lon, a.lat);
            double2 predB = PredictedScreenPx(frame, b.lon, b.lat);
            AssertOnScreenWithMargin(frame, predA, "A");
            AssertOnScreenWithMargin(frame, predB, "B");

            WindowAround(predA, out int ax0, out int ay0, out int ax1, out int ay1);
            WindowAround(predB, out int bx0, out int by0, out int bx1, out int by1);
            frame.InkStatsIn(ax0, ay0, ax1, ay1, out double2 centroidA, out int inkA);
            frame.InkStatsIn(bx0, by0, bx1, by1, out double2 centroidB, out int inkB);

            TestContext.WriteLine($"T-Present/T-Pos: predA={predA} inkA={inkA} centroidA={centroidA}");
            TestContext.WriteLine($"T-Present/T-Pos: predB={predB} inkB={inkB} centroidB={centroidB}");

            // ── T-Present: the predicted window must actually carry ink. RED-verify: rendering
            // BuildEmptyScene() through this same window logic must yield inkA == inkB == 0 (T-Neg is this
            // tooth's own negative control — plan §Teeth "shared machinery"). ──────────────────────────────
            Assert.Greater(inkA, 0, "label A's predicted window carries no ink — the label did not reach the screen");
            Assert.Greater(inkB, 0, "label B's predicted window carries no ink — the label did not reach the screen");

            // ── T-Pos: the ink centroid must sit within N px of the predicted anchor, and the two symbols'
            // residual vectors (identical text ⇒ identical systematic bias) must agree. ─────────────────────
            double2 residualA = centroidA - predA;
            double2 residualB = centroidB - predB;
            double distA = math.length(residualA);
            double distB = math.length(residualB);
            double agreement = math.length(residualA - residualB);

            TestContext.WriteLine($"T-Pos: residualA={residualA} |.|={distA:F2}px");
            TestContext.WriteLine($"T-Pos: residualB={residualB} |.|={distB:F2}px");
            TestContext.WriteLine($"T-Pos: residual agreement |A-B|={agreement:F2}px (N={PositionToleranceN}px, agreement bound={ResidualAgreementToleranceN}px)");

            Assert.Less(distA, PositionToleranceN,
                $"label A's ink centroid strayed {distA:F2}px from the predicted anchor (N={PositionToleranceN}px)");
            Assert.Less(distB, PositionToleranceN,
                $"label B's ink centroid strayed {distB:F2}px from the predicted anchor (N={PositionToleranceN}px)");
            Assert.Less(agreement, ResidualAgreementToleranceN,
                $"the two identical-text labels' residual vectors disagree by {agreement:F2}px " +
                $"(bound={ResidualAgreementToleranceN}px) — a shared systematic bias should cancel here; " +
                "disagreement means a real per-label placement error, not just the known bias");
        }

        // ── T-Distinct: two separated ink regions, not one blob, and exactly two survivors ───────────────

        [Test]
        public void Distinct_TwoSeparateInkRegions_AndExactlyTwoSurvivors()
        {
            AssertMidTileFence();

            (double lon, double lat) a = TileLocal(PointAU, PointV);
            (double lon, double lat) b = TileLocal(PointBU, PointV);
            using var scene = BuildScene(a, b, expectedQuads: 2);
            VisualFrame frame = scene.Render(SizePx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            double2 predA = PredictedScreenPx(frame, a.lon, a.lat);
            double2 predB = PredictedScreenPx(frame, b.lon, b.lat);
            WindowAround(predA, out int ax0, out int ay0, out int ax1, out int ay1);
            WindowAround(predB, out int bx0, out int by0, out int bx1, out int by1);

            frame.InkStatsIn(ax0, ay0, ax1, ay1, out _, out int inkA);
            frame.InkStatsIn(bx0, by0, bx1, by1, out _, out int inkB);
            Assert.Greater(inkA, 0, "(a) label A's window must carry ink");
            Assert.Greater(inkB, 0, "(a) label B's window must carry ink");

            // (b) the BETWEEN band (the strip strictly between the two windows) must be ≈ empty — the
            // anti-blob guard: membership in a window is not coverage (lessons tooth-membership-is-not-coverage).
            int leftX1  = math.min(ax1, bx1);
            int rightX0 = math.max(ax0, bx0);
            // The two windows must not overlap or there is no between-band to read — a fixture precondition,
            // not an assumption.
            Assert.Less(leftX1, rightX0,
                $"fixture precondition: the two anchor windows overlap (A=[{ax0},{ax1}), B=[{bx0},{bx1})) — " +
                "no between-band exists to assert against; widen the anchor separation or shrink WindowHalfPx.");
            int betweenY0 = math.min(ay0, by0);
            int betweenY1 = math.max(ay1, by1);
            frame.InkStatsIn(leftX1, betweenY0, rightX0, betweenY1, out _, out int inkBetween);
            TestContext.WriteLine($"T-Distinct: inkA={inkA} inkB={inkB} inkBetween={inkBetween}");
            Assert.LessOrEqual(inkBetween, BetweenBandInkFloor,
                $"(b) the band between the two labels carries {inkBetween} ink pixels — the two windows read " +
                "as one blob rather than two separated labels");

            // (c) ink OUTSIDE both windows is bounded (whole-frame fill stays tiny — two small glyph cells).
            SnapshotVerdict verdict = frame.Coverage();
            TestContext.WriteLine($"T-Distinct: whole-frame filled fraction={verdict.FilledFraction:P2}");
            Assert.LessOrEqual(verdict.FilledFraction, WholeFrameFillCeiling,
                $"(c) whole-frame ink fraction ({verdict.FilledFraction:P2}) exceeds the ceiling " +
                $"({WholeFrameFillCeiling:P0}) for two small glyph cells — ink is spreading outside the two " +
                "windows");

            // A dedup/collision drop shows up HERE, as a survivor-count miss, not as missing ink.
            int survivors = frame.MapView.View.SymbolPlacementSystem.LastSurvivorCount;
            Assert.AreEqual(2, survivors,
                $"exactly 2 labels must survive collision/dedup (got {survivors}) — plan lessons " +
                "flaky-tilesymbolkick-settle: a dedup drop shows here, not as missing ink");
        }

        // ── T-Neg (negative control) ──────────────────────────────────────────────────────────────────

        [Test]
        public void Neg_SymbolLayerPresent_EmptyPointsSource_RendersBackgroundOnly()
        {
            using var scene = BuildEmptyScene();
            VisualFrame frame = scene.Render(SizePx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            // NOT frame.Coverage().IsBlank: SnapshotCoverage.IsBlank fires at >=97% BACKGROUND fraction
            // (SnapshotCoverage.cs), but two small glyph cells cover well under 1% of a 512x512 frame (the
            // POSITIVE fixture measured 0.27% filled) — IsBlank would read TRUE on the positive frame too,
            // making this control vacuous (one instrument's blind spot does not transfer to another: G-V0's
            // fill negative control legitimately used IsBlank because a fill covers tens of percent of the
            // frame; that does not transfer to an instrument reading two glyph cells). InkStatsIn over the
            // WHOLE frame reads the same background predicate at the granularity this fixture needs.
            frame.InkStatsIn(0, 0, frame.Width, frame.Height, out _, out int inkTotal);
            TestContext.WriteLine($"T-Neg: inkTotal={inkTotal}");
            Assert.AreEqual(0, inkTotal,
                $"a symbol layer present but bound to an EMPTY points source must render ZERO non-background " +
                $"ink frame-wide (got {inkTotal} ink px) — the PRIMARY negative control: without it, " +
                "T-Present/T-Pos's positive arm cannot distinguish 'rendered the authored points' from " +
                "'rendered anything at all'. Layer PRESENT + source EMPTY (rather than 'no layer') pins that " +
                "the authored POINTS produced the ink, not the layer's mere existence.");

            // Non-vacuous control: because ExpectSymbolQuads(0) makes SpinUntilSymbolsReady exit on its FIRST
            // iteration (0 >= 0), a zero-ink frame here is ALSO what "the pipeline never staged anything,
            // positive or negative" would look like — a broken glyph/extraction path would pass the inkTotal
            // check above too. LastInputSymbolCount == 0 is what proves the zero-ink frame reflects a
            // genuinely EMPTY points source rather than a pipeline that produced no symbols for any input.
            int inputSymbolCount = frame.MapView.View.SymbolPlacementSystem.LastInputSymbolCount;
            Assert.AreEqual(0, inputSymbolCount,
                $"the empty points source must feed exactly 0 labels into placement (got {inputSymbolCount})");
        }

        // ── G-VR: golden reference-image regression (change detector, layered ALONGSIDE the analytic
        // teeth above — those stay the correctness oracle; this only catches "different from last bake") ──

        [Test]
        public void Golden_Gv1Symbol_MatchesBakedReference()
        {
            AssertMidTileFence();

            (double lon, double lat) a = TileLocal(PointAU, PointV);
            (double lon, double lat) b = TileLocal(PointBU, PointV);
            using var scene = BuildScene(a, b, expectedQuads: 2);
            VisualFrame frame = scene.Render(SizePx);
            GoldenImage.Assert(frame, "gv1-label");
        }

        // ── Glyph fixture loading (mirrors SymbolProcessorParityTests.LoadUp) ────────────────────────────

        private static byte[] LoadGlyphBytes()
            => _glyphBytes ??= LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");

        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, Path.Combine(relative));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException(
                $"could not locate fixture file under any ancestor of the working directory: {Path.Combine(relative)}");
        }
    }
}
#endif // UNITY_EDITOR
