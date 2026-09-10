// Unity EditMode only — the outward boundary band, observed in rendered pixels.
//
// The band's whole justification is a placement claim, and a placement claim is only settled on a frame:
// the ramp must lie OUTSIDE the boundary, so the interior keeps full coverage and two abutting fills still
// leave zero background weight. The job-level fixture (FillBandJobTests) proves the geometry and the
// attribute; these prove what the geometry was for.
//
// Frame geometry, borrowed from GeoJsonFillVisualProofTests: at tilt 0 a tile projects to exactly 512
// device px whatever the viewport is, so rendering one tile at SnapPx = 512 makes tile-local unit-square
// coordinates the same thing as frame-fraction coordinates.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    internal class FillBoundaryBandRenderTests
    {
        private static readonly TileId BandTile = new TileId { Z = 6, X = 40, Y = 25 };
        private const int SnapPx = 512;

        private const string NoGpuMessage =
            "Scene render is all-background: no GPU context in batch EditMode. " +
            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode";

        /// <summary>A pixel classified against this scene's two extremes.</summary>
        private enum Ink
        {
            /// <summary>Indistinguishable from the background.</summary>
            Background,

            /// <summary>Indistinguishable from a fully-covered fill pixel.</summary>
            Full,

            /// <summary>Strictly between the two — a partially-covered boundary pixel, which is exactly what
            /// the band exists to produce and what a hard rasterizer never produces.</summary>
            Graded,
        }

        /// <summary>Tile-local unit-square point → lon/lat.</summary>
        /// <param name="x">Tile-local x in [0,1].</param>
        /// <param name="y">Tile-local y in [0,1] — grows SOUTHWARD.</param>
        /// <returns>The (longitude, latitude) of that point.</returns>
        private static double2 LonLat(double x, double y) => BandTile.ToLonLat(x, y, 1.0);

        /// <summary>The scene camera, centred on the tile.</summary>
        /// <returns>The look-at coordinate.</returns>
        private static GeoCoordinate3D LookAt()
        {
            double2 centre = LonLat(0.5, 0.5);
            return new GeoCoordinate3D { Longitude = centre.x, Latitude = centre.y, Altitude = 0.0 };
        }

        /// <summary>One GeoJSON polygon feature from tile-local unit-square corners, closed here.</summary>
        /// <param name="corners">The ring's corners in tile-local unit coordinates, unclosed.</param>
        /// <returns>The feature's JSON.</returns>
        private static string PolygonFeature(params double2[] corners)
        {
            var flat = new List<double>();
            foreach (double2 c in corners) { double2 ll = LonLat(c.x, c.y); flat.Add(ll.x); flat.Add(ll.y); }
            double2 first = LonLat(corners[0].x, corners[0].y);
            flat.Add(first.x); flat.Add(first.y);
            return GeoJsonTestFixtures.Feature("Polygon", $"[{GeoJsonTestFixtures.Positions(flat.ToArray())}]");
        }

        /// <summary>Renders one fill layer over the given features.</summary>
        /// <param name="opacity">The layer's <c>fill-opacity</c>.</param>
        /// <param name="features">The source's polygon features.</param>
        /// <returns>The rendered frame.</returns>
        private static VisualFrame Render(double opacity, params string[] features)
        {
            using var scene = VisualScene.New()
                .Source("shapes", GeoJson.FeatureCollection(GeoJsonTestFixtures.Collection(features)))
                .Layer(VisualLayer.Fill("shapes-fill").Source("shapes").Color("#ffffff").Opacity(opacity))
                .Camera(LookAt(), zoom: BandTile.Z);
            return scene.Render(SnapPx);
        }

        /// <summary>Classifies every pixel against the frame's background and its own brightest pixel — the
        /// fully-covered reference, taken from the SAME frame so shading, opacity and colour management never
        /// have to be modelled.</summary>
        /// <param name="frame">The rendered frame.</param>
        /// <param name="classes">Per-pixel classification, row-major from the bottom-left.</param>
        /// <returns>False when the frame carries no ink at all (no GPU context).</returns>
        private static bool Classify(VisualFrame frame, out Ink[] classes)
        {
            byte[] px = frame.RawPixels;
            int n = frame.Width * frame.Height;
            classes = new Ink[n];

            // The background is the frame's own corner pixel; the full-coverage reference is its brightest.
            var background = new double3(px[0] / 255.0, px[1] / 255.0, px[2] / 255.0);
            var full = background;
            double bestDistance = 0.0;
            for (int i = 0; i < n; i++)
            {
                var c = new double3(px[i * 4] / 255.0, px[i * 4 + 1] / 255.0, px[i * 4 + 2] / 255.0);
                double d = math.length(c - background);
                if (d > bestDistance) { bestDistance = d; full = c; }
            }
            if (bestDistance < 0.05) return false;

            // One LSB of an 8-bit channel is 1/255; the band is a real ramp, so three of them is a
            // comfortable floor that still cannot absorb a partially-covered pixel.
            double tolerance = 3.0 / 255.0 * math.sqrt(3.0);
            for (int i = 0; i < n; i++)
            {
                var c = new double3(px[i * 4] / 255.0, px[i * 4 + 1] / 255.0, px[i * 4 + 2] / 255.0);
                if (math.length(c - background) <= tolerance) classes[i] = Ink.Background;
                else if (math.length(c - full) <= tolerance) classes[i] = Ink.Full;
                else classes[i] = Ink.Graded;
            }
            return true;
        }

        /// <summary>A compact description of what actually reached the frame — the class histogram, the two
        /// reference colours, and a scanline across the polygon's left boundary. Carried in the failure
        /// message so a red here says WHAT rendered, not merely that the count was wrong.</summary>
        /// <param name="frame">The rendered frame.</param>
        /// <param name="classes">Its classification.</param>
        /// <returns>A one-line diagnostic.</returns>
        private static string Diagnose(VisualFrame frame, Ink[] classes)
        {
            byte[] px = frame.RawPixels;
            int bg = 0, full = 0, graded = 0;
            foreach (Ink c in classes)
            {
                if (c == Ink.Background) bg++; else if (c == Ink.Full) full++; else graded++;
            }
            var scan = new System.Text.StringBuilder();
            int row = frame.Height / 2;
            for (int x = 120; x < 140; x++)
            {
                int b = (row * frame.Width + x) * 4;
                scan.Append($" {x}:{px[b]},{px[b + 1]},{px[b + 2]}");
            }
            int corner = 0;
            var cornerPx = $"{px[corner]},{px[corner + 1]},{px[corner + 2]}";

            var meshes = new System.Text.StringBuilder();
            if (frame.MapView != null)
                foreach (UnityEngine.MeshFilter mf in frame.MapView.GetComponentsInChildren<UnityEngine.MeshFilter>(true))
                    if (mf.sharedMesh != null)
                        meshes.Append($" {mf.gameObject.name}:v{mf.sharedMesh.vertexCount}/i{mf.sharedMesh.GetIndexCount(0)}");

            return $"[bg={bg} full={full} graded={graded} corner=({cornerPx}) meshes:{meshes} scan(row {row}):{scan}]";
        }

        /// <summary>Counts graded pixels in the whole frame.</summary>
        /// <param name="classes">A classification from <see cref="Classify"/>.</param>
        /// <returns>The count.</returns>
        private static int GradedCount(Ink[] classes)
        {
            int count = 0;
            foreach (Ink c in classes) if (c == Ink.Graded) count++;
            return count;
        }

        // ── The boundary is soft, the interior is not ──────────────────────────────────────────────────
        //
        // RED: against the tree before the band node, EVERY count below is 0 — a hard rasterizer emits no
        // partially-covered pixel at all. RED for the placement half: displace the band inward and graded
        // pixels appear INSIDE the boundary, which the interior assertion catches.
        [Test]
        public void ASquareFillHasGradedBoundaryPixels_AndAnUngradedInterior()
        {
            const double lo = 0.25, hi = 0.75;
            VisualFrame frame = Render(1.0, PolygonFeature(
                new double2(lo, lo), new double2(lo, hi), new double2(hi, hi), new double2(hi, lo)));
            if (!Classify(frame, out Ink[] classes)) Assert.Ignore(NoGpuMessage);

            int graded = GradedCount(classes);
            Assert.Greater(graded, 0,
                "a fill silhouette must produce partially-covered pixels. Zero means the band never reached " +
                "the frame — the state this whole stage exists to leave. " + Diagnose(frame, classes));

            // The interior, well inside the boundary: [0.35,0.65]² of the tile. Not one graded pixel may be
            // here. This is the lemma's own precondition — coverage stays exactly 1 everywhere the hard fill
            // already was — and it is what an inward-displaced band breaks first.
            int lo35 = (int)(0.35 * SnapPx), hi65 = (int)(0.65 * SnapPx);
            for (int y = lo35; y < hi65; y++)
                for (int x = lo35; x < hi65; x++)
                    Assert.AreEqual(Ink.Full, classes[y * frame.Width + x],
                        $"interior pixel ({x},{y}) is not fully covered. The band must lie strictly OUTSIDE " +
                        "the boundary; a ramp reaching inward is the placement the mechanism forbids.");

            // And no graded pixel may sit far outside it either: the band is ONE device pixel, so nothing
            // beyond a few px of the boundary may be partially covered.
            int outerLo = (int)(lo * SnapPx) - 6, outerHi = (int)(hi * SnapPx) + 6;
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                {
                    bool nearBoundary = x >= outerLo && x <= outerHi && y >= outerLo && y <= outerHi;
                    if (!nearBoundary)
                        Assert.AreNotEqual(Ink.Graded, classes[y * frame.Width + x],
                            $"pixel ({x},{y}) is graded but lies more than 6 px outside the polygon — the band " +
                            "is one device pixel wide, not a halo.");
                }
        }

        /// <summary>The band's PERPENDICULAR width, in device pixels, read off a rendered silhouette.
        ///
        /// <para>Total alpha-weighted coverage over the frame exceeds the hard silhouette's area by exactly
        /// the ramp's integral: an outward ramp of perpendicular width <c>w</c> that falls linearly 1 → 0
        /// contributes <c>perimeter × w / 2</c>. So <c>w = 2 × (Σ coverage − hard area) / perimeter</c> —
        /// sub-pixel, no threshold, and it reads the physical quantity the mechanism specifies rather than a
        /// pixel count that quantisation rounds.</para></summary>
        /// <param name="frame">The rendered frame.</param>
        /// <param name="hardAreaPx">The silhouette's analytic area, device px².</param>
        /// <param name="perimeterPx">Its analytic perimeter, device px.</param>
        /// <param name="plateauX">Column of a SINGLY-covered interior pixel — the coverage-1 reference. The
        /// frame centre is wrong for an abutting pair: it lands on the seam, which is double-coated at
        /// <c>fill-opacity &lt; 1</c>, and every single-coated pixel would then read coverage &lt; 1.</param>
        /// <param name="plateauY">Row of that pixel.</param>
        /// <returns>The measured perpendicular ramp width, device px.</returns>
        private static double MeasuredRampWidthPx(
            VisualFrame frame, double hardAreaPx, double perimeterPx, int plateauX, int plateauY)
        {
            byte[] px = frame.RawPixels;
            float3 background = PixelCoverage.BackgroundLinear(px, frame.Width, frame.Height);
            float3 plateau = PixelCoverage.SampleLinearBox(px, frame.Width, frame.Height, plateauX, plateauY, 4);

            double total = 0.0;
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                    total += PixelCoverage.CoverageAt(px, frame.Width, frame.Height, x, y, background, plateau);

            return 2.0 * (total - hardAreaPx) / perimeterPx;
        }

        // ── The ramp is ONE device pixel, on a diagonal silhouette as much as on an axis-aligned one ────
        //
        // The shipped coverage divides by a EUCLIDEAN gradient. fwidth is Manhattan (|ddx| + |ddy|), which
        // OVER-READS the gradient by up to sqrt(2) — and because coverage is (1 - side) DIVIDED by it, an
        // over-read gradient makes coverage fall FASTER: on a 45° silhouette the transition narrows to
        // 1/sqrt(2) ~ 0.707 device px, and coverage at the boundary itself drops from 1 to 0.707, an
        // under-inked seam. (An axis-aligned edge is unaffected: there ddx or ddy is zero and the two
        // gradients agree bit for bit — which is exactly why the diagonal arm is the one that discriminates
        // and why an axis-aligned fixture cannot stand in for it.)
        //
        // RED: ① against the tree before the band node, both widths are 0; ② swap
        // length(float2(ddx, ddy)) for fwidth in Fill_BandCoverage.hlsl and the DIAMOND arm reds while the
        // SQUARE arm stays green. Two earlier forms of this tooth survived that injection and are recorded
        // because each looked convincing: counting "graded" PIXELS is quantised far too coarsely to separate
        // 1.0 px from 0.707 px, and a [0.7, 1.3] bound on this same integral passes 0.707 by two thousandths.
        //
        // The LOWER bound is the discriminating one and it is what the analysis fixes: Manhattan renders
        // 1/sqrt(2) = 0.707 of the true width, so anything at or below ~0.8 must fail. The upper bound is a
        // sanity rail, not a discriminator, and is left slack: the diagonal arm reads a little over 1 because
        // a diamond's four rasterised corners add ink the perimeter model does not account for.
        [Test]
        public void TheRampIsOneDevicePixelWide_OnADiagonalSilhouetteAsWellAsAnAxisAlignedOne(
            [Values(false, true)] bool diagonal)
        {
            const double c = 0.5, r = 0.25;
            VisualFrame frame;
            double hardAreaPx, perimeterPx;
            if (diagonal)
            {
                // A diamond: same centre, same circumradius, every edge at 45° on screen. Diagonals are
                // 2r of the tile, so the area is d²/2 and each side is r·sqrt(2) of the tile.
                frame = Render(1.0, PolygonFeature(
                    new double2(c, c - r), new double2(c - r, c), new double2(c, c + r), new double2(c + r, c)));
                double diagonalPx = 2.0 * r * SnapPx;
                hardAreaPx  = diagonalPx * diagonalPx / 2.0;
                perimeterPx = 4.0 * r * math.sqrt(2.0) * SnapPx;
            }
            else
            {
                frame = Render(1.0, PolygonFeature(
                    new double2(c - r, c - r), new double2(c - r, c + r), new double2(c + r, c + r), new double2(c + r, c - r)));
                double sidePx = 2.0 * r * SnapPx;
                hardAreaPx  = sidePx * sidePx;
                perimeterPx = 4.0 * sidePx;
            }
            if (!Classify(frame, out _)) Assert.Ignore(NoGpuMessage);

            double width = MeasuredRampWidthPx(frame, hardAreaPx, perimeterPx, frame.Width / 2, frame.Height / 2);
            UnityEngine.Debug.Log($"[FillBoundaryBand] measured ramp width: diagonal={diagonal} w={width:F4} px");

            Assert.Greater(width, 0.85,
                $"the band must be ONE device pixel wide; measured {width:F3} px. Zero means it is not there " +
                "at all (the state before the band node existed); ~0.707 on the diagonal arm means the " +
                "coverage gradient went Manhattan.");
            Assert.Less(width, 1.30,
                $"the band must be ONE device pixel wide; measured {width:F3} px — wider means the ramp is " +
                "reaching past the single pixel the mechanism specifies.");
        }

        // ── The lemma, on the composited frame ────────────────────────────────────────────────────────
        //
        // Two ABUTTING translucent polygons in ONE layer. Background weight along their shared edge must stay
        // 0 — it is 0 today only because hard rasterization gives one of them full coverage, and it stays 0
        // only while the ramp lies strictly outside each boundary. A ramp placed inside leaves
        // (1-a_A)(1-a_B) > 0 and a background trench opens along every shared edge and tile seam.
        //
        // RED, and the honest limits of it. Give the INTERIOR a varying `side` (so its coverage stops
        // reading exactly 1) and this reds — that is the property the lemma actually rests on. The obvious
        // injection, "displace the band inward", does NOT red it and is recorded here so nobody re-derives
        // it: moving the band's outer ring inward leaves earcut's interior triangles covering the boundary
        // at coverage 1 underneath, so the band merely double-coats and no background is exposed. The inset
        // variant this tooth is meant to have killed shrinks the INTERIOR RING, which is a design change to
        // a different node, not a one-line defect in this one.
        //
        // The first assertion is a PRECONDITION, not decoration: without it this tooth passes on a tree with
        // no band at all — a hard silhouette also leaves no background at a shared edge — which is how it sat
        // green through four gate runs while the band was rendering nothing.
        [Test]
        public void AbuttingPolygonsInOneLayerLeaveNoBackgroundAlongTheirSharedEdge()
        {
            const double lo = 0.25, mid = 0.5, hi = 0.75;
            const double opacity = 0.3;
            VisualFrame frame = Render(opacity,
                PolygonFeature(new double2(lo, lo), new double2(lo, hi), new double2(mid, hi), new double2(mid, lo)),
                PolygonFeature(new double2(mid, lo), new double2(mid, hi), new double2(hi, hi), new double2(hi, lo)));

            byte[] px = frame.RawPixels;
            var background = new double3(px[0] / 255.0, px[1] / 255.0, px[2] / 255.0);

            double Inkiness(int x, int y)
            {
                int b = (y * frame.Width + x) * 4;
                return math.length(new double3(px[b] / 255.0, px[b + 1] / 255.0, px[b + 2] / 255.0) - background);
            }

            // A row through the middle of both polygons, and a single-covered reference well inside the left
            // one. Both come from THIS frame, so opacity and shading need no model.
            int row = SnapPx / 2;
            double reference = Inkiness((int)(0.35 * SnapPx), row);
            Assert.Greater(reference, 0.05, NoGpuMessage);

            // Precondition: the pair's OUTER silhouette must actually be banded, or this fixture is
            // measuring a hard-rasterized frame and the seam claim below is vacuous.
            double pairAreaPx      = (hi - lo) * (hi - lo) * SnapPx * SnapPx;
            double pairPerimeterPx = 4.0 * (hi - lo) * SnapPx;
            double outerRampPx = MeasuredRampWidthPx(
                frame, pairAreaPx, pairPerimeterPx, (int)(0.35 * SnapPx), row);
            Assert.Greater(outerRampPx, 0.4,
                $"precondition: the abutting pair's outer silhouette must carry a real band; measured " +
                $"{outerRampPx:F3} px. A hard silhouette leaves no background at a shared edge either, so " +
                "without this the seam assertion below is satisfied by the very state this stage removes.");

            int seamLo = (int)(mid * SnapPx) - 4, seamHi = (int)(mid * SnapPx) + 4;
            double peak = 0.0;
            for (int x = seamLo; x <= seamHi; x++)
            {
                double ink = Inkiness(x, row);
                peak = math.max(peak, ink);
                Assert.GreaterOrEqual(ink, reference - 0.02,
                    $"seam pixel ({x},{row}) is closer to the background than a singly-covered pixel — the " +
                    "background is showing through where two fills abut, which is the artefact that rejected " +
                    "every inset placement.");
            }

            // ── The residual rim, bounded against a PREDICTED value ───────────────────────────────────
            //
            // A tile seam is suppressed exactly (both endpoints on one window line). Two polygons abutting
            // INSIDE one tile share no such predicate: each one's band ramps across the other's interior and
            // the pair composites twice. That is over-ink, never a trench — the assertions above are what say
            // so — and it was accepted rather than closed with an intra-layer edge hash, which would still
            // miss a shared boundary expressed with different vertex counts on the two sides. These two
            // assertions are what observe that acceptance, so "we accepted a rim there" is not a claim
            // nothing checks.
            //
            // The bound is derived, not measured-then-recorded (a bound no bad port fails). A singly-covered
            // pixel is S = f·C + (1−f)·B, so compositing the same source over it again gives
            // D = f·C + (1−f)·S = S + (1−f)(S−B) — ink exactly (2−f)× the reference. The probe fixture
            // measured this ratio at 1.5026 against a predicted 1.5000 at fill-opacity 0.5, which is what
            // says the compositing is linear in the space sampled here rather than assuming it.
            Assert.Greater(peak, reference + 0.05,
                $"the rim this bound exists to bound is not there: peak seam ink {peak:F3} against a " +
                $"singly-covered {reference:F3}. Either the band stopped reaching across the shared edge — " +
                "in which case this bound passes over nothing — or a suppression predicate meant for tile " +
                "seams has started firing on a real shared edge.");

            Assert.LessOrEqual(peak, reference * (2.0 - opacity) + 0.03,
                $"peak seam ink {peak:F3} exceeds double compositing of the same source " +
                $"({reference * (2.0 - opacity):F3} = (2−f)× the singly-covered {reference:F3} at f = " +
                $"{opacity}). The accepted residual is ONE band overlapping one interior; anything past this " +
                "is a second overlap or a band wider than the one pixel it is specified to be.");
        }
    }
}
#endif // UNITY_EDITOR
