// Unity EditMode only — Stage G-V0, the declarative visual-test authoring kit's proof fixture.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// The single fill-only proof fixture carrying the kit's three acceptance teeth (plan §6): T-Fill (the fill
// actually renders where authored, background where empty), T-Parse (the kit routes through the REAL
// StyleParser.Parse — two arms), T-Binding (layers bind by source id; a dangling id yields no geometry AND
// wires no source). Fill only — no symbols, no lines, no seam-dedup (plan §10 scope fence).

#if UNITY_EDITOR
using System;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    internal class GeoJsonFillVisualProofTests
    {
        // ── The proof geometry: one tile, one polygon, generous margins on every side ──────────────────
        //
        // The tile IS the frame: at tilt 0 a tile always projects to exactly WebMercator.TilePixelSize (512)
        // device px, independent of viewport size (CameraPoseMath.AltitudeForZoom scales altitude to the
        // viewport so MetresPerDevicePixel == MetersPerPixel(zoom) always) — so SnapPx = 512 makes the tile
        // fill the frame exactly, and tile-local unit-square coordinates ARE frame-fraction coordinates.
        private static readonly TileId ProofTile = new TileId { Z = 6, X = 40, Y = 25 };
        private const int SnapPx = 512;

        // Polygon spans the tile-local unit square [0.2,0.8]² (60% of the tile, centered) — comfortably
        // inside GeoJsonSliceOptions.Default's ~1.5%-of-tile buffer, so no tile-edge interaction (seam-dedup
        // is deferred, plan §10).
        private const double PolyLo = 0.2, PolyHi = 0.8;

        // Center sample box: unit square [0.45,0.55]² — well inside the filled [0.2,0.8]² region.
        private const int CenterLo = 230, CenterHi = 282;

        // Corner sample boxes: 51×51 px (~0.1 of the tile) at each frame corner — well outside the filled
        // region, and origin-symmetric (all four corners as a SET, never "the top-left corner" — RawPixels is
        // bottom-left origin, plan §7).
        private const int CornerSize = 51;

        private const string NoGpuMessage =
            "Scene render and blank-background control are both all-black: no GPU context in batch EditMode. " +
            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode";

        private static (double west, double south, double east, double north) ProofRectangle()
        {
            double2 nw = ProofTile.ToLonLat(PolyLo, PolyLo, 1.0);
            double2 se = ProofTile.ToLonLat(PolyHi, PolyHi, 1.0);
            // Tile-local Y grows SOUTHWARD (TileId.cs), so the small-Y corner carries the NORTH latitude.
            return (nw.x, se.y, se.x, nw.y);
        }

        private static GeoCoordinate3D ProofLookAt()
        {
            double2 center = ProofTile.ToLonLat(0.5, 0.5, 1.0);
            return new GeoCoordinate3D { Longitude = center.x, Latitude = center.y, Altitude = 0.0 };
        }

        /// <summary>Renders a one-source, one-fill-layer scene over the authored polygon with
        /// <paramref name="configureLayer"/> applied to its color, and returns the CENTER region's mean colour
        /// — or <c>false</c> if the render hit the GPU-context guard.</summary>
        private static bool TryRenderCenterMean(Action<FillVisualLayer> configureLayer, out double[] centerMean)
        {
            var (west, south, east, north) = ProofRectangle();
            var layer = VisualLayer.Fill("land").Source("cities");
            configureLayer(layer);

            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(layer)
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            if (frame.NoGpuContext) { centerMean = null; return false; }

            centerMean = frame.RegionMeanColor(CenterLo, CenterLo, CenterHi, CenterHi);
            return true;
        }

        // ── Compile checkpoint A (plan §4) — JSON assembly compiles before the render path is exercised ──

        [Test]
        public void BuildStyleJson_ContainsAuthoredSourceAndLayerIds()
        {
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(0.0, 0.0, 1.0, 1.0))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(new GeoCoordinate3D { Longitude = 0.0, Latitude = 0.0 }, zoom: 0.0);

            string json = scene.BuildStyleJson();

            StringAssert.Contains("\"cities\"", json, "the source id must appear in the assembled style JSON");
            StringAssert.Contains("\"land\"", json, "the layer id must appear in the assembled style JSON");
            StringAssert.Contains("\"geojson\"", json, "the source's type must be geojson");
        }

        // ── T-Fill: the fill actually renders where authored, background where empty ─────────────────────

        [Test]
        public void Fill_RendersAuthoredPolygon_CenterFilled_CornersBackground()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            SnapshotVerdict verdict = frame.Coverage();
            Assert.IsFalse(verdict.IsBlank, "the authored polygon must render — the frame must not be blank");
            Assert.IsFalse(verdict.IsUniform, "the frame must show BOTH fill and background, not one flat colour");
            Assert.IsTrue(verdict.Passes(minFill: 0.05f, maxFill: 0.85f, minBuckets: 4),
                $"fill must occupy a tolerant central band of the frame: filled={verdict.FilledFraction:P1}, " +
                $"buckets={verdict.DistinctRegionBucketsHit}/64");

            // White fill under the lit-ambient recipe (0.9 flat ambient + one directional light): the center
            // region must bias toward white — i.e. clearly brighter than the dark-slate background on every
            // channel (relative to background, not an absolute floor, since the exact post-tonemap brightness
            // is a lighting-pipeline detail this kit does not pin).
            double[] center = frame.RegionMeanColor(CenterLo, CenterLo, CenterHi, CenterHi);
            double[] bg = { VisualScene.BackgroundColor.r, VisualScene.BackgroundColor.g, VisualScene.BackgroundColor.b };
            Assert.Greater(center[0], bg[0] + 0.15, "the center region (white fill, lit ambient) must bias toward white — red channel");
            Assert.Greater(center[1], bg[1] + 0.15, "…green channel");
            Assert.Greater(center[2], bg[2] + 0.15, "…blue channel");

            AssertCornersAreBackground(frame, "positive-control render");
        }

        [Test]
        public void Fill_EmptyDataset_RendersBackgroundOnly()
        {
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.FeatureCollection(@"{""type"":""FeatureCollection"",""features"":[]}"))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            Assert.IsTrue(frame.Coverage().IsBlank,
                "an empty FeatureCollection must render background-only — this is the PRIMARY negative " +
                "control: without it, T-Fill's positive arm cannot distinguish 'rendered the authored " +
                "dataset' from 'rendered anything at all'. Meaningful only because the background is the " +
                "mandated non-black slate (plan §7) — a black background would make this pass vacuously on " +
                "a GPU-less machine.");

            double[] center = frame.RegionMeanColor(CenterLo, CenterLo, CenterHi, CenterHi);
            Assert.Less(center[0], 0.3, "the center region specifically must show no fill either");
        }

        [Test]
        public void Fill_ZeroOpacity_RendersBackgroundOnly()
        {
            // Secondary, de-risked negative control (plan §6, optional arm): fills declare
            // _SURFACE_TYPE_TRANSPARENT and opacity 0 is proven invisible (FillPaintSnapshotTests.cs:158-171).
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff").Opacity(0.0))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            Assert.IsTrue(frame.Coverage().IsBlank, "fill-opacity 0 must render nothing");
        }

        // ── G-VR: golden reference-image regression (change detector, layered ALONGSIDE the analytic
        // teeth above — those stay the correctness oracle; this only catches "different from last bake") ──

        // Reference re-baked 2026-09-08 for the fill boundary band, on the maintainer's authorisation and
        // only after the direction was verified. Measured against the previous bake: 1236 differing px in
        // bbox [101,101]-[410,410] — a ~310 px square whose perimeter is ~1240 px, so the changed pixels ARE a
        // one-pixel ring on the silhouette. All 1236 moved TOWARD the fill colour and none away, and none sits
        // farther than 1.5 px from the boundary, leaving the ~96,000 px interior untouched. That is softened
        // edges, not displaced geometry — had geometry moved, the count would be in the tens of thousands.
        // Recorded because a re-baked golden with no reason is indistinguishable from one re-baked to go green.
        [Test]
        public void Golden_Gv0Fill_MatchesBakedReference()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            GoldenImage.Assert(frame, "gv0-fill");
        }

        private static void AssertCornersAreBackground(VisualFrame frame, string context)
        {
            int hi = SnapPx - CornerSize;
            (int x0, int y0)[] corners = { (0, 0), (hi, 0), (0, hi), (hi, hi) };
            foreach ((int x0, int y0) in corners)
            {
                double[] mean = frame.RegionMeanColor(x0, y0, x0 + CornerSize, y0 + CornerSize);
                double variance = frame.RegionColorVariance(x0, y0, x0 + CornerSize, y0 + CornerSize);
                Assert.Less(variance, 0.02,
                    $"[{context}] corner ({x0},{y0}) must be a flat background patch, not speckled fill edge");
                // Background is dark slate (~0.10,0.11,0.15) — a corner touched by fill would read brighter.
                Assert.Less(mean[0] + mean[1] + mean[2], 0.6,
                    $"[{context}] corner ({x0},{y0}) mean {string.Join(",", mean)} reads too bright for the " +
                    "dark-slate background — the fill has bled into a corner region");
            }
        }

        // ── T-Parse: the kit routes through the REAL StyleParser.Parse ────────────────────────────────────

        [Test]
        public void Parse_IdentityArm_ParsedStyleCarriesGeoJsonSourceAndBoundFillLayer()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            SourceDefinition source = frame.ParsedStyle.GetSource("cities");
            Assert.IsNotNull(source, "the parsed style must carry the declared source");
            Assert.AreEqual(SourceType.GeoJson, source.Type, "…typed as geojson");
            Assert.IsNotNull(source.Data, "…with a non-null `data`");
            Assert.IsTrue(source.Data.IsObject,
                "`data` must be a JSON OBJECT — exactly the precondition MapView.cs:323 gates the geojson " +
                "branch on, which can only hold if the emitted JSON string went through StyleParser.Parse " +
                "into a JsonValue tree. A composer that built a StyleDocument directly would have to " +
                "reconstruct this shape by hand.");

            bool boundLayerFound = false;
            foreach (StyleLayer layer in frame.ParsedStyle.Layers)
                if (layer.Id == "land" && layer.Source == "cities") boundLayerFound = true;
            Assert.IsTrue(boundLayerFound, "the parsed layer list must carry the fill layer id bound to \"cities\"");
        }

        [Test]
        public void Parse_ExpressionFormArm_HexAndRgbaSpellingsOfSameColor_AgreeAndDifferFromAThirdColor()
        {
            bool okHexRed   = TryRenderCenterMean(l => l.Color("#ff0000"), out double[] hexRed);
            bool okExprRed  = TryRenderCenterMean(l => l.ColorExpression("[\"rgba\",255,0,0,1]"), out double[] exprRed);
            bool okExprBlue = TryRenderCenterMean(l => l.ColorExpression("[\"rgba\",0,0,255,1]"), out double[] exprBlue);

            if (!okHexRed || !okExprRed || !okExprBlue) { Assert.Inconclusive(NoGpuMessage); return; }

            double agreement = ManhattanDistance(hexRed, exprRed);
            double contrast  = ManhattanDistance(hexRed, exprBlue);

            Assert.Less(agreement, 0.15,
                $"hex \"#ff0000\" and expression [\"rgba\",255,0,0,1] are the SAME colour and must render " +
                $"the same center-region mean (distance={agreement:F3}). A bypass composer that hand-converts " +
                "hex→RGBA and skips the real expression evaluator cannot make these two spellings agree — it " +
                "would have to reimplement the parser to pass.");
            Assert.Greater(contrast, 0.25,
                $"a genuinely different colour (blue, expression-form) must render VISIBLY differently from " +
                $"red (distance={contrast:F3}) — otherwise the agreement above could be explained by " +
                "'renders the same colour regardless of paint', not by the expression evaluator actually running.");
        }

        private static double ManhattanDistance(double[] a, double[] b)
            => math.abs(a[0] - b[0]) + math.abs(a[1] - b[1]) + math.abs(a[2] - b[2]);

        // ── T-Binding: layers bind by source id; a dangling id yields no geometry AND wires no source ──────

        [Test]
        public void Binding_DanglingSourceId_RendersNothing_AndWiresNoSource()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("does-not-exist").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            Assert.IsTrue(frame.Coverage().IsBlank,
                "a fill layer bound to an UNDECLARED source id must render nothing (MapView.cs:311-314 skips it)");
            Assert.AreEqual(0, frame.MapView.WiredFeatureSourceCount(),
                "…and must leave NO source wired. This is the load-bearing arm: 'nothing rendered' alone " +
                "passes for unrelated reasons (no light, mis-framed camera, no GPU) — only a wired-source " +
                "count of zero proves the SKIP actually happened (GeoJsonSourceTests.AssertNothingWasWired:413-417).");
        }

        [Test]
        public void Binding_FlippingTheIdBackToTheRealSource_RendersAndWiresOneSource()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            Assert.IsFalse(frame.Coverage().IsBlank,
                "flipping the layer's source id back to the DECLARED source must render again");
            Assert.AreEqual(1, frame.MapView.WiredFeatureSourceCount(),
                "…and wire exactly the one declared source — proving the discriminator flipping the id is " +
                "the ID MATCH, not some default wiring.");
        }
    }
}
#endif // UNITY_EDITOR
