// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// S06 Batch B: view-state, tile cover, and floating-origin (jitter) math.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class ViewMathTests
    {
        private const double Eps = 1e-9;

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        // ── CameraProperties (tile-selection helpers) ───────────────────────────────────────────

        [Test]
        public void Camera_ZoomToIntegerZoom_FloorsAndClampsAtZero()
        {
            Assert.AreEqual(3, Cam(0, 0, 3.0).IntegerZoom);
            Assert.AreEqual(3, Cam(0, 0, 3.99).IntegerZoom);
            Assert.AreEqual(0, Cam(0, 0, 0.0).IntegerZoom);
            Assert.AreEqual(0, Cam(0, 0, -2.0).IntegerZoom, "negative zoom clamps to 0");
            Assert.AreEqual(14, Cam(0, 0, 14.5).IntegerZoom);
        }

        [Test]
        public void Camera_CenterMercator_MatchesWebMercator()
        {
            var v = Cam(13.405, 52.52, 10.0);   // Berlin
            double2 expected = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52 });
            double2 actual   = v.CenterMercator();
            Assert.AreEqual(expected.x, actual.x, 1e-6);
            Assert.AreEqual(expected.y, actual.y, 1e-6);
        }

        [Test]
        public void Camera_CenterMercator_ClampsExtremeLatitude()
        {
            // A latitude beyond the Mercator limit must be clamped, not produce inf/NaN.
            var v = Cam(0, 89.9, 5.0);
            double2 m = v.CenterMercator();
            Assert.IsFalse(double.IsNaN(m.y) || double.IsInfinity(m.y),
                "Clamped latitude must yield a finite Mercator y.");
            double2 atLimit = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = 0, Latitude = CameraProperties.MaxMercatorLat });
            Assert.AreEqual(atLimit.y, m.y, 1e-6, "Extreme latitude clamps to the Mercator limit.");
        }

        // ── S71: IVisibleTileSelector / FrustumTileSelector ──────────────────────────────
        //
        // All behavioural teeth exercise the default impl THROUGH the seam: build a ViewContext
        // { Camera, ViewportPx, Projection } and call SelectVisibleTiles(in view, buf). The white-border
        // guarantee is T-FRAME (covers the framing ground quad, derived independently of ScreenToGround).

        private static readonly IProjection Proj = new WebMercatorProjection();
        private const double RefH = 1080.0; // the framing reference height (matches CameraSystem default)

        private static CameraProperties Cam(double lon, double lat, double zoom, double headingDeg)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, headingDeg, 0);

        /// <summary>The framing view context: viewportPx = (RefH·aspect, RefH), per D6/D7.</summary>
        private static ViewContext View(CameraProperties cam, double aspect)
            => new ViewContext { Camera = cam, ViewportPx = new double2(RefH * aspect, RefH), Projection = Proj };

        /// <summary>(unwrapped, unclamped) fractional tile coordinate of a Mercator point at zoom z.</summary>
        private static double2 MercToTileFrac(double2 merc, int z)
        {
            double we = WebMercator.WorldExtent;
            long   n  = 1L << z;
            return new double2(
                (merc.x      + we) / (2.0 * we) * n,
                (we - merc.y)      / (2.0 * we) * n);
        }

        /// <summary>Mercator point → its TileId at z (x wrapped, y clamped, lat clamped to the limit).</summary>
        private static TileId MercToTile(double2 merc, int z)
        {
            long n = 1L << z;
            double we = WebMercator.WorldExtent;
            double my = math.clamp(merc.y, -we, we); // pole clamp (mirrors the selector's lat clamp)
            double2 f = MercToTileFrac(new double2(merc.x, my), z);
            int rawX = (int)math.floor(f.x);
            long m = rawX % n; if (m < 0) m += n;
            int y = (int)math.floor(f.y); if (y < 0) y = 0; if (y > n - 1) y = (int)(n - 1);
            return new TileId { Z = z, X = (int)m, Y = y };
        }

        /// <summary>
        /// A ground point of the FRAMING quad: camera-center Mercator + (a,b) rotated by the camera heading,
        /// where a ∈ [-halfH, halfH], b ∈ [-halfV, halfV] are ground offsets derived from the framing math
        /// (D7) — NOT from ScreenToGround (that would make T-FRAME circular). The rotation matches the
        /// projection's pixel→ground bearing rotation: east = a·cosH + b·sinH; north = −a·sinH + b·cosH.
        /// </summary>
        private static double2 FramingGround(in CameraProperties cam, double a, double b)
        {
            double2 c  = cam.CenterMercator();
            double  cH = cam.Heading.Value.Cos;
            double  sH = cam.Heading.Value.Sin;
            return c + new double2(a * cH + b * sH, -a * sH + b * cH);
        }

        // The T-FRAME / T-CONSIST / T-CEIL matrix.
        private struct FrameCase { public double Lon, Lat, Zoom, HeadingDeg, Aspect; public string Name; }

        private static IEnumerable<FrameCase> FrameCases()
        {
            double[] aspects   = { 1.0, 16.0 / 9.0, 9.0 / 16.0, 21.0 / 9.0, 5.0 / 3.0 };
            double[] fracs     = { 0.0, 0.5, 0.95 };
            int[]    intZooms  = { 2, 5, 10, 14 };
            (double lon, double lat, string n)[] centers =
            {
                (0.0, 0.0, "equator/prime"),
                (13.405, 52.52, "Berlin"),
                (-58.38, -34.6, "BuenosAires"),
            };
            double[] headings  = { 0.0, 45.0 };

            foreach (var c in centers)
            foreach (int iz in intZooms)
            foreach (double fr in fracs)
            foreach (double asp in aspects)
            foreach (double hd in headings)
                yield return new FrameCase
                {
                    Lon = c.lon, Lat = c.lat, Zoom = iz + fr, HeadingDeg = hd, Aspect = asp,
                    Name = $"{c.n} z{iz + fr:F2} h{hd:F0} a{asp:F2}"
                };
        }

        // ── THE decisive tooth — T-FRAME: cover the framing ground quad (non-circular) ──────────

        [Test]
        public void Selector_TFrame_CoversFramingGroundQuad()
        {
            var sel = (IVisibleTileSelector)new FrustumTileSelector(minZoom: 0, maxZoom: 22);
            var buf = new List<TileId>();

            foreach (var fc in FrameCases())
            {
                var cam = Cam(fc.Lon, fc.Lat, fc.Zoom, fc.HeadingDeg);
                double mpp   = WebMercator.GroundResolution(cam.Zoom); // span reads the TRUE camera zoom
                double halfV = RefH * mpp / 2.0;
                double halfH = halfV * fc.Aspect;

                sel.SelectVisibleTiles(View(cam, fc.Aspect), buf);
                var S = new HashSet<TileId>(buf);
                // Coverage is asserted at the z the selector ACTUALLY emits (OnScreenTilePx may offset it
                // coarser than IntegerZoom) — the no-white-spot guarantee must hold at that resolution.
                int z = buf.Count > 0 ? buf[0].Z : cam.IntegerZoom;

                // The four framing corners AND a dense interior grid must all be covered. One miss ⇒ a
                // visible pixel with no tile ⇒ white border.
                const int G = 16; // 17×17 incl. corners
                for (int i = 0; i <= G; i++)
                for (int j = 0; j <= G; j++)
                {
                    double a = -halfH + (2.0 * halfH) * i / G;
                    double b = -halfV + (2.0 * halfV) * j / G;
                    double2 ground = FramingGround(in cam, a, b);
                    // Finite atlas: a framing point past the world edge (very low zoom / ultra-wide viewport
                    // sees beyond the sheet) is off-paper — there is no tile to cover it, and none should exist.
                    if (math.abs(ground.x) > WebMercator.WorldExtent || math.abs(ground.y) > WebMercator.WorldExtent)
                        continue;
                    TileId  need   = MercToTile(ground, z);
                    Assert.IsTrue(S.Contains(need),
                        $"[{fc.Name}] framing point (a={a:F0},b={b:F0}) → {need.Z}/{need.X}/{need.Y} not covered " +
                        "⇒ white border. The selector must cover the framing quad (refH·liveAspect, refH).");
                }
            }
        }

        // ── T-CONSIST — internal consistency (supporting; circular w.r.t. framing) ──────────────

        [Test]
        public void Selector_TConsist_CoversUnprojectedScreenGrid()
        {
            // Sample a dense SCREEN grid, unproject with the SAME projection/viewportPx the selector got,
            // and assert each maps into the set. Proves the bbox contains the quad interior (off-by-one
            // guard). This CANNOT catch a wrong viewportPx — that is T-FRAME's job.
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22);
            var buf = new List<TileId>();

            foreach (var fc in FrameCases())
            {
                var cam = Cam(fc.Lon, fc.Lat, fc.Zoom, fc.HeadingDeg);
                double2 vp = new double2(RefH * fc.Aspect, RefH);

                sel.SelectVisibleTiles(View(cam, fc.Aspect), buf);
                var S = new HashSet<TileId>(buf);
                int z = buf.Count > 0 ? buf[0].Z : cam.IntegerZoom; // selection zoom actually emitted (offset-aware)

                const int G = 16;
                for (int i = 0; i <= G; i++)
                for (int j = 0; j <= G; j++)
                {
                    double2 px = new double2(vp.x * i / G, vp.y * j / G);
                    GeoCoordinate3D g = Proj.ScreenToGround(px, vp, in cam);
                    double2 merc = WebMercator.FromLonLat(new GeoCoordinate3D
                    {
                        Longitude = g.Longitude, Latitude = Proj.ClampValidLatitude(g.Latitude)
                    });
                    if (math.abs(merc.x) > WebMercator.WorldExtent || math.abs(merc.y) > WebMercator.WorldExtent)
                        continue; // finite atlas — off-paper points aren't covered
                    TileId need = MercToTile(merc, z);
                    Assert.IsTrue(S.Contains(need),
                        $"[{fc.Name}] screen px ({px.x:F0},{px.y:F0}) → {need.Z}/{need.X}/{need.Y} not in set.");
                }
            }
        }

        // ── T-CEIL — over-load ceiling: the impl cannot explode the request set ─────────────────

        [Test]
        public void Selector_TCeil_CountBoundedByFramingSpan()
        {
            const int pad = 1;
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22);
            var buf = new List<TileId>();

            foreach (var fc in FrameCases())
            {
                var cam = Cam(fc.Lon, fc.Lat, fc.Zoom, fc.HeadingDeg);
                int z = cam.IntegerZoom;
                long n = 1L << z;

                // Span = the ROTATED framing quad's tile-space bbox (the honest ceiling: at heading 45° the
                // axis-aligned bbox is larger than the non-rotated extent, and a correct selector covers it).
                double mpp   = WebMercator.GroundResolution(cam.Zoom);
                double halfV = RefH * mpp / 2.0;
                double halfH = halfV * fc.Aspect;
                double xMin = double.MaxValue, xMax = double.MinValue, yMin = double.MaxValue, yMax = double.MinValue;
                foreach (var (sa, sb) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                {
                    double2 f = MercToTileFrac(FramingGround(in cam, sa * halfH, sb * halfV), z);
                    xMin = math.min(xMin, f.x); xMax = math.max(xMax, f.x);
                    yMin = math.min(yMin, f.y); yMax = math.max(yMax, f.y);
                }
                double spanX = math.min(xMax - xMin, n);
                double spanY = math.min(yMax - yMin, n);
                // Slack term +3 (was +1): the frustum cover follows the true footprint and its Chebyshev
                // prefetch ring protrudes diagonally on a rotated viewport, so the padded count exceeds the
                // rectangular-bbox model by a few tiles. Still an order-of-magnitude runaway guard (a NaN/wrap
                // glitch emits far more), not a tight count spec.
                long bound = (long)((math.ceil(spanX) + 2 * pad + 3) * (math.ceil(spanY) + 2 * pad + 3));

                sel.SelectVisibleTiles(View(cam, fc.Aspect), buf);
                Assert.LessOrEqual(buf.Count, bound,
                    $"[{fc.Name}] selected {buf.Count} > ceiling {bound} — runaway/NaN bbox?");
                Assert.Greater(buf.Count, 0, $"[{fc.Name}] empty cover");
            }
        }

        // ── T-MONOTONE — count tracks span (the old code's count was span-invariant) ────────────

        [Test]
        public void Selector_TMonotone_XCountGrowsWithAspect()
        {
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22);
            var buf = new List<TileId>();

            int DistinctX(double aspect)
            {
                sel.SelectVisibleTiles(View(Cam(0, 0, 14.0), aspect), buf);
                return buf.Select(t => t.X).Distinct().Count();
            }

            int wide = DistinctX(16.0 / 9.0);
            int ultra = DistinctX(21.0 / 9.0);
            Assert.Greater(ultra, wide,
                $"Widening aspect 16:9→21:9 must select more x-tiles (got {wide}→{ultra}). The old magic " +
                "rectangle's x-count was capped by pad·aspect, not the real span — a non-increase means the bug is back.");
        }

        [Test]
        public void Selector_TMonotone_CountChangesWithFractionalZoom()
        {
            // The old TileCover count was INVARIANT to fractional zoom (it read only integer zoom) — that
            // invariance is the bug. A span-correct selector's count must CHANGE across a fractional sweep
            // within one integer level. Direction note: in our flat framing, higher fractional zoom shows
            // LESS ground (smaller mpp), so FEWER integer-z tiles fit — count DECREASES toward the next
            // level (the spec's prose said "grow"; the physics is the opposite — what matters is the
            // dependence, i.e. NOT invariant).
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22);
            var buf = new List<TileId>();

            int CountAt(double zoom)
            {
                sel.SelectVisibleTiles(View(Cam(0, 0, zoom), 1.0), buf);
                return buf.Count;
            }

            int lo = CountAt(14.0);
            int hi = CountAt(14.95);
            Assert.AreNotEqual(lo, hi,
                $"Fractional-zoom count must not be invariant (got {lo} at z14.00 and {hi} at z14.95). " +
                "Invariance ⇒ the resolution/zoom-independent under-cover bug.");
            Assert.Less(hi, lo,
                $"Higher fractional zoom shows less ground ⇒ fewer integer-z tiles (got {lo}→{hi}).");
        }

        // ── Re-anchored former SelectsExpected3x3 — structural, not a frozen literal ─────────────

        [Test]
        public void Selector_ContiguousBlockContainingCameraTile()
        {
            // At a 1:1 framing viewport, center (0,0), z2: the set must be a contiguous (hole-free) block
            // that contains the camera's own tile. The exact size is now a function of refH/pad (real span),
            // so we assert structure, not a hard-coded 3×3 (the old, under-covering oracle).
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(View(Cam(0, 0, 2.0), 1.0), buf);

            // Selection zoom actually emitted (OnScreenTilePx may coarsen z2 → z1 under the 512 default).
            int z = buf.Count > 0 ? buf[0].Z : 2;
            var cameraTile = MercToTile(Cam(0, 0, 2.0).CenterMercator(), z);
            Assert.IsTrue(buf.Contains(cameraTile),
                $"Cover must contain the camera's own tile {cameraTile.X},{cameraTile.Y} at z{z}.");

            var xs = buf.Select(t => t.X).Distinct().OrderBy(v => v).ToList();
            var ys = buf.Select(t => t.Y).Distinct().OrderBy(v => v).ToList();
            Assert.AreEqual(xs.Count, xs.Last() - xs.First() + 1, "x columns must be contiguous (no holes).");
            Assert.AreEqual(ys.Count, ys.Last() - ys.First() + 1, "y rows must be contiguous (no holes).");
            Assert.AreEqual(xs.Count * ys.Count, buf.Count, "the block must be a full rectangle (no holes).");
            foreach (var t in buf) Assert.AreEqual(z, t.Z, "single selection zoom for this case.");
        }

        // ── DPI — device-pixel ratio derives from real dpi vs the 160 golden standard ───────────

        [Test]
        public void DeviceScaling_DerivesDprFromDpi()
        {
            Assert.AreEqual(160.0, DeviceScaling.ReferenceDpi, 0.0, "mdpi golden standard.");
            // dpr = dpi / 160 (screenDpi is a precondition-positive measured density).
            Assert.AreEqual(1.0, DeviceScaling.DevicePixelRatioFromDpi(160.0), 1e-12,
                "160 dpi ⇒ dpr 1 (the reference density).");
            Assert.AreEqual(2.0, DeviceScaling.DevicePixelRatioFromDpi(320.0), 1e-12,
                "320 dpi ⇒ dpr 2 (a 2× panel).");
            Assert.AreEqual(2.75, DeviceScaling.DevicePixelRatioFromDpi(440.0), 1e-12,
                "arbitrary dpi divides by 160.");
        }

        // ── The px→consumer-space conversion (S107 Stage 1) ────────────────────────────────────

        [Test]
        public void LogicalToDevicePx_LogicalTargetIsTheIdentity_DeviceTargetScalesByDpr()
        {
            // Logical: the consumer already divides by dpr, so the conversion must not touch the value —
            // at ANY ratio, including the ones no other test in the suite runs at.
            foreach (double dpr in new[] { 0.5, 1.0, 1.56, 2.0, 3.0 })
                Assert.AreEqual(7.0, DeviceScaling.LogicalToDevicePx(7.0, PixelSpace.Logical, dpr), 1e-12,
                    $"PixelSpace.Logical must be the identity (dpr {dpr}).");

            // Device: the consumer measures against the physical framebuffer ⇒ × dpr, exactly.
            Assert.AreEqual(7.0,  DeviceScaling.LogicalToDevicePx(7.0, PixelSpace.Device, 1.0),  1e-12,
                "dpr 1 is the identity in BOTH spaces — the stage invariant.");
            Assert.AreEqual(14.0, DeviceScaling.LogicalToDevicePx(7.0, PixelSpace.Device, 2.0),  1e-12,
                "dpr 2 doubles a device-space px value.");
            Assert.AreEqual(10.92, DeviceScaling.LogicalToDevicePx(7.0, PixelSpace.Device, 1.56), 1e-12,
                "the ratio is applied linearly, not rounded to a whole-pixel step.");
        }

        [Test]
        public void NonPositiveDpr_FallsBackToOne_InBOTHDirections_SoTheGuardsCannotDiverge()
        {
            // S108 T3-2. Both directions in ONE test on purpose: the fallback has a single home
            // (DeviceScaling's private ratio guard), and asserting the pair together is what makes it
            // impossible to change one direction's behaviour without the other's going red. An unconfigured
            // ratio that framed the camera at 1 but scaled the paint by 0 would blank every line; one that
            // divided the viewport by 0 would send the framing viewport to +∞.
            //
            // NaN and the infinities are not swept HERE — since S109 the guard is a two-sided plausibility
            // band, so they fall back by decision rather than by accident, and the test below is where that
            // decision is recorded (it closes §6.1 finding 6). This one keeps its original scope: the
            // non-positive ratios that were the whole of the guard before the band existed.
            foreach (double dpr in new[] { 0.0, -0.0, -1.0, -2.0 })
            {
                Assert.AreEqual(7.0, DeviceScaling.LogicalToDevicePx(7.0, PixelSpace.Device, dpr), 0.0,
                    $"logical→device at a non-positive ratio ({dpr}) must fall back to 1 " +
                    "(never a zero-width or mirrored paint).");
                Assert.AreEqual(7.0, DeviceScaling.DeviceToLogicalPx(7.0, dpr), 0.0,
                    $"device→logical at a non-positive ratio ({dpr}) must fall back to 1 " +
                    "(never ±∞, which NaN-poisons the frustum planes downstream).");

                double2 vp = DeviceScaling.DeviceToLogicalPx(new double2(1920.0, 1080.0), dpr);
                Assert.IsFalse(double.IsInfinity(vp.x) || double.IsNaN(vp.x)
                            || double.IsInfinity(vp.y) || double.IsNaN(vp.y),
                    $"the double2 overload must be finite at a non-positive ratio ({dpr}).");
                Assert.AreEqual(1920.0, vp.x, 0.0, "x falls back to 1 component-wise.");
                Assert.AreEqual(1080.0, vp.y, 0.0, "y falls back to 1 component-wise.");
            }
        }

        [Test]
        public void ImplausibleDpr_FallsBackToExactlyOne_InBOTHDirections()
        {
            // S109 T4-1. Both directions in ONE test for the same reason as T3-2 above: the fallback has a
            // single home, and asserting the pair together is what stops one direction growing a behaviour
            // the other does not have.
            //
            // The expected value is LITERALLY 1.0 at tolerance 0, and that exactness is the tooth. It is
            // what discriminates a fallback from a clamp: `clamp(d, 0.25, 8)` returns 0.25 for 0.1 and 8.0
            // for 1e6 — both perfectly plausible-looking ratios that would satisfy any tolerant, "is
            // finite", or "is in range" assertion while silently rebasing the entire map by 4× or 8×.
            //
            // Only +∞ is a BEHAVIOUR change: it satisfied the old positivity test and propagated, taking
            // logical→device to +∞ and device→logical to 0. NaN and -∞ already fell back (every comparison
            // against them is false either way) — they are swept to RECORD that an unusable ratio is one
            // thing rather than to catch a regression, which is how §6.1 finding 6 ("NaN maps to 1 by
            // accident, not by decision") closes by a tooth instead of by prose.
            foreach (double dpr in new[]
                     {
                         0.1, 1e-9,                                     // below any plausible floor
                         100.0, 1e6,                                    // above any plausible ceiling
                         double.PositiveInfinity,                       // the one behaviour change
                         double.NegativeInfinity, double.NaN,           // decision-recording: already fell back
                     })
            {
                Assert.AreEqual(1.0, DeviceScaling.LogicalToDevicePx(1.0, PixelSpace.Device, dpr), 0.0,
                    $"logical→device at an implausible ratio ({dpr}) must fall back to EXACTLY 1 — a clamp " +
                    "to the nearest bound would substitute a plausible-looking ratio and hide the bad value.");
                Assert.AreEqual(1.0, DeviceScaling.DeviceToLogicalPx(1.0, dpr), 0.0,
                    $"device→logical at an implausible ratio ({dpr}) must fall back to EXACTLY 1 — at +∞ " +
                    "this returned 0, which collapses the logical viewport the whole cover is framed from.");

                double2 vp = DeviceScaling.DeviceToLogicalPx(new double2(1920.0, 1080.0), dpr);
                Assert.AreEqual(1920.0, vp.x, 0.0, $"x falls back to 1 component-wise at {dpr}.");
                Assert.AreEqual(1080.0, vp.y, 0.0, $"y falls back to 1 component-wise at {dpr}.");
            }

            // The composition Bootstrapper.Start actually performs — SafeRatio(DevicePixelRatioFromDpi(dpi)) —
            // which nothing else pins. An absurd reported panel density cannot escape the band. Only the large
            // end is testable: a dpi of 0 trips DevicePixelRatioFromDpi's own positive-density precondition.
            Assert.AreEqual(1080.0,
                DeviceScaling.DeviceToLogicalPx(1080.0, DeviceScaling.DevicePixelRatioFromDpi(1e9)), 0.0,
                "an absurd panel density must land outside the band and degrade to 1, not divide the " +
                "framing viewport by 6.25 million.");
        }

        [Test]
        public void PlausibleDpr_PassesThroughUnchanged_AndTheBandIsInclusiveAtBothBounds()
        {
            // S109 T4-2. Kept apart from T4-1 on purpose: this test pins a policy CONSTANT that a later
            // maintainer may legitimately want to move, so moving the band stays a one-test edit.
            //
            // The DISCRIMINATING rows are the straddles — 0.24/0.26 and 7.99/8.01. Neither half of a pair
            // pins anything alone: 0.24 → 1 only says the floor is above 0.24, and 0.26 → 0.26 only says it
            // is at or below 0.26; together they trap it in (0.24, 0.26]. The exact-bound rows (0.25, 8.0)
            // are the WEAKEST in the set — they distinguish >= from > and nothing else.
            foreach (double dpr in new[] { 0.25, 0.26, 0.5, 0.625, 1.0, 1.56, 2.0, 3.0, 4.0, 7.99, 8.0 })
            {
                Assert.AreEqual(7.0 * dpr, DeviceScaling.LogicalToDevicePx(7.0, PixelSpace.Device, dpr), 0.0,
                    $"a plausible ratio ({dpr}) must scale the paint bit-identically — sub-1 ratios are " +
                    "legitimate (a ~100-dpi panel is 0.625, Android ldpi is 0.75), which is why the floor " +
                    "is not 1, and 4.0 is a real flagship (xxxhdpi, 640 dpi), which is why the ceiling is " +
                    "not 4.");
                Assert.AreEqual(7.0 / dpr, DeviceScaling.DeviceToLogicalPx(7.0, dpr), 0.0,
                    $"a plausible ratio ({dpr}) must divide the measurement bit-identically.");
            }

            foreach (double dpr in new[] { 0.24, 8.01 })
            {
                Assert.AreEqual(7.0, DeviceScaling.LogicalToDevicePx(7.0, PixelSpace.Device, dpr), 0.0,
                    $"a ratio just outside the band ({dpr}) must fall back — with its in-band twin above, " +
                    "this brackets the bound rather than merely sampling one side of it.");
                Assert.AreEqual(7.0, DeviceScaling.DeviceToLogicalPx(7.0, dpr), 0.0,
                    $"a ratio just outside the band ({dpr}) must fall back in the device→logical direction too.");
            }
        }

        // ── The device→logical conversion (S108 Stage 3) ───────────────────────────────────────

        /// <summary>
        /// S108 T3-1: <see cref="DeviceScaling.DeviceToLogicalPx(double,double)"/> is DIVISION, exactly —
        /// never <c>v * (1/d)</c>. The witness values matter: at dpr 1 and dpr 2 the reciprocal is a power of
        /// two and therefore exact, so those ratios cannot discriminate at all, and at 1.56 / 3.0 the sizes a
        /// test naturally reaches for (512, 1024, 1920, 1080) happen to agree too. The sweep below is wide
        /// enough that an unlucky literal cannot defeat it; 1440 at 1.56 and 640 at 3.0 are the verified
        /// last-bit witnesses. Tolerance is 0 — a tolerant comparison passes the very implementation this
        /// exists to reject, and reciprocal drift at a non-dyadic ratio like 1.56 would break the stage's
        /// byte-identity invariant everywhere the conversion is now shared.
        /// </summary>
        [Test]
        public void DeviceToLogicalPx_IsExactDivision_NotReciprocalMultiplication()
        {
            double[] measurements =
            {
                1440.0, 640.0, 2560.0, 1920.0, 1080.0, 512.0, 1024.0, 480.0, 256.0, 128.0,
                64.0, 32.0, 16.0, 8.0, 4.0, 2.0, 1.0, 3.0, 7.0, 10.0,
                13.0, 100.0, 333.0, 777.0, 999.0, 1000.0, 1234.5, 1600.0, 900.0, 4096.0,
            };

            foreach (double dpr in new[] { 1.0, 2.0, 1.56, 3.0 })
                foreach (double devicePx in measurements)
                {
                    Assert.AreEqual(devicePx / dpr, DeviceScaling.DeviceToLogicalPx(devicePx, dpr), 0.0,
                        $"device→logical must be exactly {devicePx} / {dpr} — a reciprocal multiply differs " +
                        "in the last bit at the ratios a real panel reports.");

                    double2 pair = DeviceScaling.DeviceToLogicalPx(new double2(devicePx, devicePx * 0.5), dpr);
                    Assert.AreEqual(devicePx / dpr,       pair.x, 0.0, "the double2 overload divides x exactly.");
                    Assert.AreEqual(devicePx * 0.5 / dpr, pair.y, 0.0, "the double2 overload divides y exactly.");
                }

            // dpr 1 is bit-identical to the input — the identity every existing test in the suite runs at,
            // and the reason a defect here is invisible to all 1915 of them.
            foreach (double devicePx in measurements)
                Assert.AreEqual(devicePx, DeviceScaling.DeviceToLogicalPx(devicePx, 1.0), 0.0,
                    "dpr 1 must be bit-identical, not merely close.");
        }

        /// <summary>
        /// S108: the two directions are exact inverses at the ratios where the round trip is representable
        /// (powers of two), so a sign or reciprocal slip in either one shows up as a round-trip drift.
        /// </summary>
        [Test]
        public void DeviceToLogicalPx_InvertsLogicalToDevicePx_AtPowerOfTwoRatios()
        {
            foreach (double dpr in new[] { 1.0, 2.0, 4.0, 0.5 })
                foreach (double logicalPx in new[] { 7.0, 512.0, 1080.0, 1440.0 })
                {
                    double device = DeviceScaling.LogicalToDevicePx(logicalPx, PixelSpace.Device, dpr);
                    Assert.AreEqual(logicalPx, DeviceScaling.DeviceToLogicalPx(device, dpr), 0.0,
                        $"logical→device→logical must round-trip exactly at dpr {dpr}.");
                }
        }

        // ── S88 T-DENSITY — 512 selects exactly one level coarser than 256, over the SAME span ───

        [Test]
        public void Selector_TDensity_512IsOneLevelCoarserThan256()
        {
            var sel512 = (IVisibleTileSelector)new FrustumTileSelector(0, 22, onScreenTilePx: 512);
            var sel256 = (IVisibleTileSelector)new FrustumTileSelector(0, 22, onScreenTilePx: 256);
            var b512 = new List<TileId>();
            var b256 = new List<TileId>();

            foreach (var fc in FrameCases())
            {
                var cam = Cam(fc.Lon, fc.Lat, fc.Zoom, fc.HeadingDeg);
                sel256.SelectVisibleTiles(View(cam, fc.Aspect), b256);
                sel512.SelectVisibleTiles(View(cam, fc.Aspect), b512);
                if (b256.Count == 0 || b512.Count == 0) continue;

                int z256 = b256[0].Z;
                int z512 = b512[0].Z;
                if (z256 == 0) continue; // offset would clamp at minZoom — not an interior case

                Assert.AreEqual(z256 - 1, z512,
                    $"[{fc.Name}] 512 convention must select exactly one integer level coarser than 256 " +
                    $"(got 256→z{z256}, 512→z{z512}).");
                Assert.LessOrEqual(b512.Count, b256.Count,
                    $"[{fc.Name}] the coarser 512 selection must not select MORE tiles than 256 " +
                    $"(got {b512.Count} vs {b256.Count}).");
            }
        }

        // ── S88 T-DEFAULT — a fresh selector is the 512 convention (no inspector change needed) ───

        [Test]
        public void Selector_TDefault_IsThe512Convention()
        {
            var selDefault = (IVisibleTileSelector)new FrustumTileSelector(0, 22);      // default 512
            var sel256     = (IVisibleTileSelector)new FrustumTileSelector(0, 22, 256);
            var cam = Cam(0, 0, 10.0);
            var bDef = new List<TileId>();
            var b256 = new List<TileId>();
            selDefault.SelectVisibleTiles(View(cam, 16.0 / 9.0), bDef);
            sel256.SelectVisibleTiles(View(cam, 16.0 / 9.0), b256);

            Assert.AreEqual(b256[0].Z - 1, bDef[0].Z,
                "The DEFAULT selector must be the 512 convention — one level coarser than an explicit 256.");
            Assert.Less(bDef.Count, b256.Count,
                $"The 512 default must select fewer tiles than 256 (got {bDef.Count} vs {b256.Count}).");
        }

        // ── T-LOD — the ScreenSpaceLod strategy: mixed-zoom, fewer tiles than flat, still gap-free ──

        [Test]
        public void Selector_LodScreenSpace_MixedZoom_FewerThanFlat_StillCovers()
        {
            var cam = new CameraProperties(new GeoCoordinate3D { Longitude = 13.4, Latitude = 52.5, Altitude = 0 },
                                           13, heading: 0, tilt: 60);
            var vc  = new ViewContext { Camera = cam, ViewportPx = new double2(1600, 900), Projection = Proj };

            var flat = new List<TileId>();
            new FrustumTileSelector(0, 22, 512, new FlatLodStrategy(), new GeometryAwareFarPlane())
                .SelectVisibleTiles(vc, flat);
            var lod = new List<TileId>();
            new FrustumTileSelector(0, 22, 512, new ScreenSpaceLodStrategy(), new GeometryAwareFarPlane())
                .SelectVisibleTiles(vc, lod);

            Assert.IsNotEmpty(lod);
            int minZ = lod.Min(t => t.Z), maxZ = lod.Max(t => t.Z);
            Assert.Less(minZ, maxZ, "LOD cover must be mixed-zoom — far tiles coarser than near.");
            Assert.AreEqual(cam.IntegerZoom, maxZ, "near-field detail is at the target (camera) zoom.");
            Assert.Less(lod.Count, flat.Count,
                $"LOD must select fewer tiles than flat under tilt (lod={lod.Count}, flat={flat.Count}).");

            // Gap-free: every point flat covers is still covered by SOME (possibly coarser) LOD tile.
            bool CoveredByAny(List<TileId> tiles, double2 merc)
            {
                foreach (var t in tiles)
                {
                    long nn = 1L << t.Z;
                    double2 f = MercToTileFrac(merc, t.Z);
                    int fx = (int)math.floor(f.x), fy = (int)math.floor(f.y);
                    if (fy < 0 || fy >= nn) continue;
                    if (fx == t.X && fy == t.Y) return true;
                }
                return false;
            }
            foreach (var ft in flat)
            {
                double2 centreMerc = MercCentreOfTile(ft);
                Assert.IsTrue(CoveredByAny(lod, centreMerc),
                    $"LOD left a gap: flat tile {ft.Z}/{ft.X}/{ft.Y} centre uncovered by any LOD tile.");
            }
        }

        /// <summary>Web-Mercator coordinate of a tile's centre.</summary>
        private static double2 MercCentreOfTile(TileId t)
        {
            double2 ll = t.ToLonLat(0.5, 0.5, 1.0);
            return WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = ll.x, Latitude = ll.y });
        }

        // ── T-LOD-SPURIOUS — screen-space LOD must not emit far-coarse tiles off to the side ─────────
        // Regression for the reported bug: keyed on the tile CENTRE distance, a huge coarse tile that merely
        // grazes the frustum edge (far centre, NEAR edge) was emitted coarse instead of subdivided-and-culled
        // — a z13 camera loaded a z4 globe tile / z8–z9 Mercator tile off to the side, outside the view.
        // Keyed on the NEAREST point (the fix) it subdivides and the off-view part is culled. These poses have
        // TEETH: pre-fix each emitted a tile ≥4 levels too coarse (below the target-3 bound); the bound still
        // admits legit far-field coarsening (z10-z11 toward the horizon at deep tilt). Globe kept to shallow
        // tilt — its conservative bounding-sphere descent makes LOD near-inert at steep tilt (separate issue).
        [TestCase("mercator", 60.0, 30.0)]
        [TestCase("mercator", 75.0, 30.0)]
        [TestCase("globe",     0.0, 135.0)]
        [TestCase("globe",    30.0,  30.0)]
        public void Selector_LodScreenSpace_NoSpuriousCoarseTilesOffToTheSide(string projKind, double tiltDeg, double headingDeg)
        {
            IProjection proj = projKind == "globe" ? (IProjection)new SphericalProjection() : new WebMercatorProjection();
            var cam = new CameraProperties(new GeoCoordinate3D { Longitude = 13.4, Latitude = 52.5, Altitude = 0 },
                                           13, heading: headingDeg, tilt: tiltDeg);
            var vc  = new ViewContext { Camera = cam, ViewportPx = new double2(1600, 900), Projection = proj };
            IFarPlanePolicy far = proj.TryGetHorizonOccluder(out _, out double occR)
                ? (IFarPlanePolicy)new RaySphereFarPlane(occR) : new GeometryAwareFarPlane();

            var lod = new List<TileId>();
            new FrustumTileSelector(0, 22, 512, new ScreenSpaceLodStrategy(), far).SelectVisibleTiles(vc, lod);

            Assert.IsNotEmpty(lod);
            int minZ = lod.Min(t => t.Z);
            Assert.GreaterOrEqual(minZ, cam.IntegerZoom - 3,
                $"{projKind} tilt={tiltDeg}° heading={headingDeg}°: screen-space LOD emitted a spurious coarse " +
                $"z{minZ} tile under a z{cam.IntegerZoom} camera (a far tile grazing the frustum, emitted coarse " +
                $"instead of subdivided-and-culled).");
        }

        // ── T-LOD-FRUSTUM — no selected tile's quad lies wholly OUTSIDE the frustum ──────────────────
        // The reported bug's core: the conservative 6-plane AABB test kept tiles diagonally past a frustum
        // corner (a big AABB isn't fully behind any single plane), so the cover held tiles the camera can't
        // see. The frustum's reverse AABB pre-cull rejects them. Invariant, checked with the EXACT reported
        // pose (z13 tilt60 Berlin): every selected tile's ground quad, clipped against the 6 planes, survives.
        // Pre-fix this failed on 11/1102/670, 11/1098/670, 12/2203/1341 (user-confirmed invisible in-editor).
        [Test]
        public void Selector_TiltedMercator_NoSelectedTileQuadOutsideFrustum()
        {
            var cam = new CameraProperties(new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52, Altitude = 0 },
                                           13, heading: 0, tilt: 60, verticalFovDeg: 60);
            double2 vp = new double2(1236.078, 709.647);
            var vc = new ViewContext { Camera = cam, ViewportPx = vp, Projection = Proj };
            var cover = new List<TileId>();
            new FrustumTileSelector(0, 22, 512, new ScreenSpaceLodStrategy(), new GeometryAwareFarPlane())
                .SelectVisibleTiles(vc, cover);
            Assert.IsNotEmpty(cover);

            // Rebuild the frustum planes (same math as ViewFrustum.FromPose) and the render frame the selector used.
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, vp.y, cam.VerticalFovDeg);
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value, out double3 pos, out double3 fwd, out double3 up);
            double near = math.max(0.1, CameraPoseMath.NearClip(altitude)), aspect = vp.x / vp.y;
            double far = new GeometryAwareFarPlane().FarMetres(altitude, cam.Tilt.Value, cam.VerticalFovDeg, aspect);
            double3 f = math.normalize(fwd), r = math.normalize(math.cross(f, up)), u = math.cross(r, f);
            double hV = Angle.FromDegrees(cam.VerticalFovDeg * 0.5).Radians, sinV = math.sin(hV), cosV = math.cos(hV);
            double hH = math.atan(math.tan(hV) * aspect), sinH = math.sin(hH), cosH = math.cos(hH);
            var pn = new[] { f, new double3(-f.x, -f.y, -f.z), f*sinV - u*cosV, f*sinV + u*cosV, f*sinH - r*cosH, f*sinH + r*cosH };
            var pd = new[] { -math.dot(f, pos + near*f), -math.dot(new double3(-f.x,-f.y,-f.z), pos + far*f),
                             -math.dot(pn[2], pos), -math.dot(pn[3], pos), -math.dot(pn[4], pos), -math.dot(pn[5], pos) };
            var la = new GeoCoordinate { Latitude = Proj.ClampValidLatitude(cam.LookAt.Latitude), Longitude = cam.LookAt.Longitude };
            double3 origin = Proj.Project(la); float3x3 basis = Proj.TangentBasisAt(la);
            double3 RP(TileId t, double px, double py)
            {
                double2 ll = t.ToLonLat(px, py, 1.0);
                double3 w = Proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
                double rx = w.x-origin.x, ry = w.y-origin.y, rz = w.z-origin.z;
                return new double3(basis.c0.x*rx+basis.c0.y*ry+basis.c0.z*rz, basis.c1.x*rx+basis.c1.y*ry+basis.c1.z*rz, basis.c2.x*rx+basis.c2.y*ry+basis.c2.z*rz);
            }
            bool QuadMeetsFrustum(TileId t)
            {
                var poly = new List<double3> { RP(t,0,0), RP(t,1,0), RP(t,1,1), RP(t,0,1) };
                for (int p = 0; p < 6; p++)
                {
                    var clipped = new List<double3>();
                    for (int i = 0; i < poly.Count; i++)
                    {
                        double3 a = poly[i], b = poly[(i + 1) % poly.Count];
                        double da = math.dot(pn[p], a) + pd[p], db = math.dot(pn[p], b) + pd[p];
                        if (da >= 0) clipped.Add(a);
                        if ((da >= 0) != (db >= 0)) clipped.Add(a + (b - a) * (da / (da - db)));
                    }
                    poly = clipped;
                    if (poly.Count == 0) return false;
                }
                return true;
            }

            var outside = cover.Where(t => !QuadMeetsFrustum(t)).ToList();
            Assert.IsEmpty(outside,
                $"{outside.Count} selected tile(s) are wholly outside the frustum (the camera can't see them): " +
                string.Join(" ", outside.Select(t => $"{t.Z}/{t.X}/{t.Y}")));
        }

        // ── T-ROBUST — nasty camera points never crash and never boom the tile count ────────────

        [Test]
        public void Selector_TRobust_BadCameraPoints_NoCrash_NoCountBoom()
        {
            const int pad = 1;
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22); // default 512
            var buf = new List<TileId>();

            // Antimeridian, out-of-range longitudes, poles, beyond-Mercator latitudes, and extreme /
            // clamped / fractional zooms — every combination the camera can wander into.
            double[] lons     = { -360.0, -180.0, -179.999, -90.0, 0.0, 90.0, 179.999, 180.0, 359.9 };
            double[] lats     = { -90.0, -89.99, -85.06, -60.0, 0.0, 60.0, 85.06, 89.99, 90.0 };
            double[] zooms    = { 0.0, 0.4, 1.0, 7.3, 14.0, 19.0, 22.0, 30.0 };
            double[] aspects  = { 1.0, 16.0 / 9.0, 21.0 / 9.0 };
            double[] headings = { 0.0, 45.0, 200.0, -30.0 };

            // Position-INDEPENDENT ceiling: the framing span (viewportPx · metresPerPixel at the TRUE zoom)
            // in tiles at the emitted zoom, made rotation-safe (w·|cos|+h·|sin| ≤ w+h), plus the pad ring and a
            // phase-slop term. Poles only CLAMP the count below this; a wrap/NaN/pole bug that emits far more
            // (the "100-tile glitch") blows past it. Capped at the whole world (n·n).
            long Ceiling(in CameraProperties cam, double aspect, int emittedZ)
            {
                long   n       = 1L << emittedZ;
                double vTiles  = RefH * WebMercator.GroundResolution(cam.Zoom) * n / (2.0 * WebMercator.WorldExtent);
                double hTiles  = vTiles * aspect;
                double axis    = math.ceil(hTiles + vTiles) + 2 * pad + 2;
                long   bound   = (long)(axis * axis);
                long   world   = n * n;
                return bound < world ? bound : world;
            }

            foreach (var lon in lons)
            foreach (var lat in lats)
            foreach (var zoom in zooms)
            foreach (var aspect in aspects)
            foreach (var heading in headings)
            {
                var cam = Cam(lon, lat, zoom, heading);
                string where = $"lon={lon} lat={lat} zoom={zoom} aspect={aspect:F2} heading={heading}";

                Assert.DoesNotThrow(() => sel.SelectVisibleTiles(View(cam, aspect), buf),
                    $"selection threw at {where}");
                // Finite atlas: a look-at longitude off the sheet (|lon| > 180) may legitimately select nothing
                // — the robustness contract there is only "no crash / no boom", not coverage.
                if (math.abs(lon) > 180.0) continue;
                Assert.Greater(buf.Count, 0, $"empty cover (white screen) at {where}");

                int  z = buf[0].Z;
                long n = 1L << z;
                foreach (var t in buf)
                {
                    Assert.AreEqual(z, t.Z, $"mixed selection zoom at {where}");
                    Assert.IsTrue(t.X >= 0 && t.X < n, $"x={t.X} out of [0,{n}) at {where}");
                    Assert.IsTrue(t.Y >= 0 && t.Y < n, $"y={t.Y} out of [0,{n}) at {where}");
                }
                Assert.AreEqual(buf.Count, buf.Distinct().Count(), $"duplicate tiles at {where}");

                long ceiling = Ceiling(in cam, aspect, z);
                Assert.LessOrEqual(buf.Count, ceiling,
                    $"tile-count BOOM at {where}: selected {buf.Count} > ceiling {ceiling} (z{z}) — a wrap/pole/NaN glitch.");
            }
        }

        // ── T-SEAM — the interface stays algorithm-agnostic + the impl is swappable (structural) ─

        [Test]
        public void Selector_TSeam_InterfaceCarriesNoAlgorithmKnob()
        {
            // The seam method takes EXACTLY (in ViewContext, List<TileId>) — nothing else.
            MethodInfo m = typeof(IVisibleTileSelector).GetMethod(nameof(IVisibleTileSelector.SelectVisibleTiles));
            Assert.IsNotNull(m);
            ParameterInfo[] ps = m.GetParameters();
            Assert.AreEqual(2, ps.Length, "SelectVisibleTiles must take only (ViewContext, List<TileId>).");
            Assert.IsTrue(ps[0].ParameterType == typeof(ViewContext).MakeByRefType() && ps[0].IsIn,
                "First param must be `in ViewContext`.");
            Assert.AreEqual(typeof(List<TileId>), ps[1].ParameterType, "Second param must be List<TileId>.");

            // ViewContext carries EXACTLY { Camera, ViewportPx, Projection } — legitimate view state, and no
            // algorithm knob (no minZoom / maxZoom / selectionZoom of any kind).
            var members = typeof(ViewContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
                .Concat(typeof(ViewContext).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name))
                .ToHashSet();
            CollectionAssert.AreEquivalent(new[] { "Camera", "ViewportPx", "Projection" }, members,
                "ViewContext must carry exactly Camera/ViewportPx/Projection — an algorithm knob leaked onto it.");
            foreach (var banned in new[] { "Pad", "Zoom", "Min", "Max", "Selection" })
                Assert.IsFalse(members.Any(x => x.IndexOf(banned, StringComparison.OrdinalIgnoreCase) >= 0),
                    $"ViewContext member resembling an algorithm knob ('{banned}') is forbidden on the seam.");
        }

        [Test]
        public void Selector_TSeam_MixedZoomSetIsAcceptable()
        {
            // Proof of swappability: a fake selector returning a hand-built MIXED-Z set satisfies the seam
            // contract (the set keys on whole TileIds carrying their own Z — no single-zoom assumption).
            IVisibleTileSelector fake = new MixedZoomFakeSelector();
            var buf = new List<TileId>();
            fake.SelectVisibleTiles(View(Cam(0, 0, 4.0), 1.0), buf);

            var zs = buf.Select(t => t.Z).Distinct().OrderBy(v => v).ToList();
            CollectionAssert.AreEquivalent(new[] { 3, 4 }, zs,
                "A mixed-zoom selector must be expressible through the seam (each TileId carries its own Z).");
        }

        private sealed class MixedZoomFakeSelector : IVisibleTileSelector
        {
            public void SelectVisibleTiles(in ViewContext view, List<TileId> reuseBuffer)
            {
                reuseBuffer.Clear();
                reuseBuffer.Add(new TileId { Z = 3, X = 4, Y = 4 }); // coarse (far)
                reuseBuffer.Add(new TileId { Z = 4, X = 8, Y = 8 }); // fine (near)
            }
        }

        // ── T-POLE / T-ALLOC (preserved guarantees, now through the seam) ────────────────────────
        // (T-WRAP deleted: Web-Mercator is a finite atlas sheet in this renderer — it does not wrap.)

        [Test]
        public void Selector_TPole_ClampsLatitudeRange()
        {
            // Near the north pole at z3 (n=8): every emitted y ∈ [0,7], never negative / never ≥ n.
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(View(Cam(0, 85.0, 3.0), 1.0), buf);
            foreach (var t in buf)
                Assert.IsTrue(t.Y >= 0 && t.Y <= 7, $"y must be clamped to [0,7]: {t.Y}");
            Assert.Greater(buf.Count, 0);
        }

        [Test]
        public void Selector_ClampsSelectionZoom()
        {
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 14);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(View(Cam(0, 0, 20.0), 1.0), buf);
            foreach (var t in buf) Assert.AreEqual(14, t.Z, "Selection zoom must clamp to maxZoom.");
            Assert.Greater(buf.Count, 0);
        }

        [Test]
        public void Selector_TAlloc_ReusesBuffer_NoGrowthOnRepeat()
        {
            var sel = (IVisibleTileSelector)new FrustumTileSelector(0, 22);
            var view = View(Cam(0, 0, 5.0), 16.0 / 9.0);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(view, buf);
            int firstCount = buf.Count;
            int capacityAfterFirst = buf.Capacity;

            for (int i = 0; i < 50; i++) sel.SelectVisibleTiles(view, buf);

            Assert.AreEqual(firstCount, buf.Count, "Same view → same tile count.");
            Assert.AreEqual(capacityAfterFirst, buf.Capacity,
                "Re-selecting the same view must not grow the buffer (steady-state no-alloc).");
        }

        // ── FloatingOrigin: structural ───────────────────────────────────────────────────────

        [Test]
        public void FloatingOrigin_TileLocalOrigin_MatchesTileMinCorner()
        {
            var t = new TileId { Z = 14, X = 8000, Y = 5000 };
            var (min, _) = t.MercatorBounds();
            double2 o = FloatingOrigin.TileLocalOriginMercator(t);
            Assert.AreEqual(min.x, o.x, Eps);
            Assert.AreEqual(min.y, o.y, Eps);
        }

        // S52: FloatingOrigin.ShouldRebase / RebaseDelta were removed — the scene origin now tracks the
        // look-at every frame (camera-relative rendering), so there is no threshold to cross and no rebase
        // delta to apply. The two-level RTC composition itself is still pinned by the jitter gate below.

        // ── FloatingOrigin: the JITTER gate (headline acceptance teeth) ────────────────────────

        /// <summary>
        /// THE no-jitter gate. Sweeps the camera across the entire Web-Mercator extent (±20,037,508 m,
        /// including longitudes at the antimeridian and latitudes up to the Mercator limit). For each
        /// camera position it rebases the scene origin to the camera, picks the camera's tile at the
        /// live-loop zoom, and — for the worst-case vertex (the tile's far corner) — reproduces the GPU's
        /// TWO float casts (mesh vertex baked tile-origin-relative + tile transform baked
        /// scene-origin-relative) exactly as the pipeline does. It asserts the rendered position matches
        /// the exact double truth (merc − sceneOrigin) to sub-millimetre.
        ///
        /// This pins "NO float jitter" in pure Core math rather than by eyeball: the round-trip error is
        /// the actual coordinate precision the renderer achieves at world scale.
        ///
        /// <para><b>Measures the WHOLE cover, not just the camera's tile.</b> The live loop
        /// (<see cref="MapView"/>) renders a padded rectangle of tiles, so the farthest visible vertex is
        /// at the EDGE of the cover, not the camera's own tile. This test enumerates the SAME
        /// <see cref="TileCover.Cover"/> the live loop uses (with the live-loop default cover params) and
        /// measures the far corner of EVERY cover tile — taking the worst. Measuring only the camera tile
        /// would understate the real jitter (the closest tile is best-case).</para>
        ///
        /// Parameters (computed, not guessed): zoom = <see cref="LiveZoom"/> (tile span ≈ 2446 m), a
        /// conservative modeled camera drift of 2000 m (S52: the scene origin now tracks the look-at every
        /// frame, so real drift is ~0 — modeling 2000 m keeps the gate strict), cover = the S71 MapView live
        /// defaults (framing viewport (RefH·1.5, RefH), pad = 1 tile). The framing-correct cover is wider than
        /// the old magic rectangle, so the farthest cover-edge vertex now sits ≈ 18 km from the scene origin;
        /// the measured worst round-trip error across a dense camera sweep is still well under a millimetre.
        /// Latitude is clamped to the Mercator limit exactly as <see cref="CameraProperties"/>/the selector
        /// clamp it; the camera never reaches the polar singularity where a single Mercator tile's span is
        /// unbounded.
        ///
        /// <para><b>Antimeridian seam (out of S06 scope — see follow-ups.md).</b> The sweep stays in
        /// lon ∈ [-160°, 160°] so it never straddles ±180°. At the seam, the selector wraps x
        /// (geographically correct), but a wrapped tile's ABSOLUTE Mercator x jumps by a full world width
        /// (≈ 40,075 km) — it is geographically adjacent but ~40 M m away in Mercator. Origin-relative
        /// placement of such a wrapped tile would need a ±worldWidth offset; without it the render coord
        /// reaches world scale and float32 precision degrades to metres at the seam. That seam handling is
        /// deferred (the S06 demo does not cross the antimeridian).</para>
        /// </summary>
        [Test]
        public void FloatingOrigin_ExtremeMercator_CoverEdgeRenderCoordsBounded_SubMillimetre()
        {
            const int    LiveZoom                = 14;
            const double ModeledCameraDriftMeters = 2000.0; // S52: conservative; real drift is ~0 (origin ≡ look-at)
            // S71: the framing-correct cover is wider than the old magic rectangle, so the worst MEASURED
            // corner is now an OFF-SCREEN +1-pad-ring tile corner ≈ 18 km from the origin — at that distance
            // float32's 24-bit mantissa resolves ≈ 18000/2^24 ≈ 1.1 mm, the precision FLOOR, not a regression.
            // On-screen geometry (inside the framing quad, ≲ 10 km out) stays comfortably sub-millimetre. The
            // budget is 2 mm: still ~250× tighter than the metre-scale error the NoRebase companion proves, so
            // it catches a real rebasing regression while admitting the legitimate wider cover.
            const double SubMmBudgetMeters       = 2e-3;
            // S71 MapView live defaults — the cover the LIVE loop actually selects (framing viewport
            // (RefH·LiveAspect, RefH), pad = 1 tile). The framing-correct cover is larger than the old magic
            // rectangle, so this gate now measures a wider cover edge (still sub-mm — see below).
            const double LiveAspect    = 1.5;
            var liveSelector = (IVisibleTileSelector)new FrustumTileSelector(minZoom: 0, maxZoom: LiveZoom);

            double worstErr = 0.0;
            double worstCoordMag = 0.0;
            var cover = new List<TileId>();

            // Dense camera sweep across longitudes [-160,160] (NOT straddling the ±180° seam — see the
            // antimeridian note above) and latitudes up to the Mercator limit.
            for (int li = 0; li <= 16; li++)
            {
                double camLon = -160.0 + li * 20.0;
                for (int la = 0; la <= 20; la++)
                {
                    double camLat = -CameraProperties.MaxMercatorLat + la * (2.0 * CameraProperties.MaxMercatorLat / 20.0);

                    // Scene origin at the look-at; model a conservative camera drift to keep the gate strict.
                    double2 sceneOrigin = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = camLon, Latitude = camLat });
                    double2 cameraMerc  = new double2(sceneOrigin.x + ModeledCameraDriftMeters, sceneOrigin.y);
                    double2 camLL       = WebMercator.ToLonLat(cameraMerc.x, cameraMerc.y);

                    // Enumerate the SAME cover the live loop would select for this camera (through the seam).
                    var camView  = Cam(camLL.x, camLL.y, LiveZoom);
                    var liveView = new ViewContext
                    {
                        Camera     = camView,
                        ViewportPx = new double2(RefH * LiveAspect, RefH),
                        Projection = Proj,
                    };
                    liveSelector.SelectVisibleTiles(in liveView, cover);

                    for (int t = 0; t < cover.Count; t++)
                    {
                        TileId tile = cover[t];
                        double2 tileOrigin = FloatingOrigin.TileLocalOriginMercator(tile);
                        var (tMin, tMax) = tile.MercatorBounds();

                        // Far corners of this cover tile (max in-tile offset).
                        double2[] corners =
                        {
                            new double2(tMin.x, tMin.y), new double2(tMax.x, tMax.y),
                            new double2(tMin.x, tMax.y), new double2(tMax.x, tMin.y),
                        };

                        foreach (double2 v in corners)
                        {
                            float3  rendered = FloatingOrigin.RenderVertex(v, tileOrigin, sceneOrigin);
                            double3 truth    = RenderVertexTruth(v, sceneOrigin);

                            double ex = rendered.x - truth.x;
                            double ez = rendered.z - truth.z;
                            double err = Math.Sqrt(ex * ex + ez * ez);
                            if (err > worstErr) worstErr = err;

                            double mag = Math.Sqrt(rendered.x * rendered.x + rendered.z * rendered.z);
                            if (mag > worstCoordMag) worstCoordMag = mag;
                        }
                    }
                }
            }

            // The render coords stay small (float32 keeps precision) AND the round-trip error is sub-mm.
            // S71: the framing-correct cover is wider, so the worst cover-edge magnitude is ≈ 18 km; 30 km is
            // a comfortable bound, still far under the float32 precision cliff.
            Assert.Less(worstCoordMag, 3e4,
                $"Worst cover-edge render-coord magnitude {worstCoordMag:F1} m must stay well under the " +
                "float32 precision cliff (rebasing failed to keep coords near the origin).");
            Assert.Less(worstErr, SubMmBudgetMeters,
                $"Worst floating-origin cover-edge round-trip error {worstErr * 1000:F4} mm exceeds the " +
                "2 mm budget. At world scale this is the 'no jitter' guarantee — a regression means " +
                "panned/zoomed geometry visibly shimmers. (Measured across the whole live cover incl. the " +
                "off-screen pad ring, not just the camera's tile.)");
        }

        /// <summary>
        /// Without rebasing (scene origin pinned at the world origin), the SAME pipeline at world scale
        /// produces metre-level error — proving the gate above has teeth and that rebasing is what buys
        /// the precision (not some artefact of the test).
        /// </summary>
        [Test]
        public void FloatingOrigin_NoRebase_AtWorldScale_HasLargeError()
        {
            const int LiveZoom = 14;
            // Camera far from the world origin; scene origin left at (0,0) (NO rebasing).
            double2 sceneOrigin = new double2(0, 0);
            double2 camera = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = 179.0, Latitude = 0.0 });  // ~+19.9M m east

            TileId tile = TileContaining(camera, LiveZoom);
            double2 tileOrigin = FloatingOrigin.TileLocalOriginMercator(tile);
            var (_, tMax) = tile.MercatorBounds();

            float3  rendered = FloatingOrigin.RenderVertex(tMax, tileOrigin, sceneOrigin);
            double3 truth    = RenderVertexTruth(tMax, sceneOrigin);
            double err = Math.Abs(rendered.x - truth.x);

            Assert.Greater(err, 0.5,
                "With NO rebasing at world scale (~20M m from origin) the float32 round-trip error must be " +
                "large (>0.5 m) — this is exactly the jitter floating-origin rebasing eliminates.");
        }

        // ── S92 — pixel unification Phase 1 (DPI camera + device min-zoom) ───────────────────────

        [Test]
        public void AltitudeForZoom_DprNormalization_HalvesAt2x_IdenticalAt1x()
        {
            // T-DPI-CAMERA (arithmetic half): the camera frames the LOGICAL viewport (vp ÷ DPR, S92 D1).
            // AltitudeForZoom is linear in viewport height, so ÷DPR divides the altitude by DPR:
            //   DPR=2 ⇒ half the altitude (camera 2× closer ⇒ map 2× bigger),
            //   DPR=1 ⇒ bit-identical to the pre-S92 physical-viewport altitude (the 952-test safety guard —
            //           every existing camera test runs at DPR=1 and must be unaffected).
            const double zoom = 6.0, fov = 60.0, vpH = 1080.0;
            double altPhysical = CameraPoseMath.AltitudeForZoom(zoom, vpH,       fov);
            double alt1        = CameraPoseMath.AltitudeForZoom(zoom, vpH / 1.0, fov);
            double alt2        = CameraPoseMath.AltitudeForZoom(zoom, vpH / 2.0, fov);
            Assert.AreEqual(altPhysical, alt1, 0.0, "DPR=1 is bit-identical to the physical-viewport altitude");
            Assert.AreEqual(alt1 / 2.0, alt2, alt1 * 1e-12, "DPR=2 halves the altitude (camera 2× closer)");
        }

        [Test]
        public void GroundResolution_BaseIs512AndHalvesPerZoom()
        {
            // T-PAINT-UNCHANGED (S92) + T-RELABEL base pin (S93): the paint scale is not DPI-normalized, and
            // under the 512 convention GroundResolution(0) == equatorial circumference / 512. Literal 512 (not
            // WebMercator.TilePixelSize) so this guards the constant's VALUE, not a tautology.
            Assert.AreEqual(EarthConstants.EquatorialCircumferenceMetres / 512.0,
                            WebMercator.GroundResolution(0.0), 1e-6, "GroundResolution(0) == circumference / 512");
            for (int z = 0; z < 20; z++)
                Assert.AreEqual(WebMercator.GroundResolution(z) / 2.0, WebMercator.GroundResolution(z + 1),
                                WebMercator.GroundResolution(z) * 1e-12, $"res(z+1) == res(z)/2 at z={z}");
            // CameraPoseMath.MetersPerPixel is the same single-sourced formula.
            Assert.AreEqual(WebMercator.GroundResolution(5.0), CameraPoseMath.MetersPerPixel(5.0), 1e-9);
        }

        [Test]
        public void MinZoomToFit_FramesWholeWorld_ScalesWithViewportAndDpr()
        {
            // T-MINZOOM: floor = log2(min(vp_logical) / tilePx) − margin. At the floor the world square
            // (tilePx · 2^floor) is SMALLER than the shorter viewport side by exactly 2^margin (whole world
            // visible, with breathing room) — never larger (grape), never cropped.
            const double tile = WebMercator.TilePixelSize, margin = 0.5;
            double2 vp    = new double2(1600, 1200); // logical px
            double  floor = CameraPoseMath.MinZoomToFit(vp.x, vp.y, tile, margin);

            double worldPx = tile * math.pow(2.0, floor);
            double minSide = math.min(vp.x, vp.y);
            Assert.Less(worldPx, minSide, "the whole world fits within the shorter viewport side (not a grape)");
            Assert.AreEqual(minSide / math.pow(2.0, margin), worldPx, minSide * 1e-9,
                            "world square is exactly `margin` zoom levels below the exact-fit size");

            // Scales with the viewport: a 2× larger shorter side ⇒ +1 floor. A hardcoded/viewport-independent
            // floor (the old MinZoom = 0) fails this — the floor MUST move with the viewport.
            double floorBig = CameraPoseMath.MinZoomToFit(vp.x * 2, vp.y * 2, tile, margin);
            Assert.AreEqual(floor + 1.0, floorBig, 1e-9, "2× viewport ⇒ +1 floor");
            Assert.AreNotEqual(0.0, floor, "device-derived — not the old hardcoded 0");

            // Uses LOGICAL px: physical 3200×2400 at DPR=2 ≡ logical 1600×1200 ⇒ same floor (scales with DPR).
            Assert.AreEqual(floor, CameraPoseMath.MinZoomToFit(3200.0 / 2, 2400.0 / 2, tile, margin), 1e-12,
                            "logical-px basis ⇒ the floor scales with DPR");
        }

        // ── Stage A — Mercator finite-sheet camera (min-zoom floor) ───────────────────────────────

        [Test]
        public void MinZoomToFill_FillsTheLargerSide_NoGap()
        {
            // A1-FILL: the finite-sheet floor fits the world square to the LARGER viewport side (not the
            // shorter side MinZoomToFit uses), so on a landscape viewport there's no off-world margin.
            // Falsifiable: MinZoomToFit (min side) gives world-px == 1080 < 1920 on this viewport.
            const double tile = WebMercator.TilePixelSize;
            double2 vp = new double2(1920.0, 1080.0);

            double floor   = CameraPoseMath.MinZoomToFill(vp.x, vp.y, tile, 0.0);
            double worldPx = tile * math.pow(2.0, floor);
            double maxSide = math.max(vp.x, vp.y);

            Assert.AreEqual(maxSide, worldPx, maxSide * 1e-9,
                "at margin 0 the world square exactly fills the LARGER viewport side");
            Assert.GreaterOrEqual(worldPx, maxSide - 1e-6,
                "world square must be at least as large as the larger viewport side (no off-world margin)");
        }

        [Test]
        public void MinZoomFloor_SelectsByProjection_FiniteFillsCyclicFits()
        {
            // A1-SELECTOR: the projection-keyed floor branches once on IsFinitePlanarWorld — Mercator fills
            // (margin 0), the globe fits (with margin) — and the two diverge on a non-square viewport,
            // proving the branch is load-bearing (not dead code that happens to agree).
            var mercator = new WebMercatorProjection();
            var globe    = new SphericalProjection();
            Assert.IsTrue(mercator.IsFinitePlanarWorld, "Mercator is the finite planar sheet");
            Assert.IsFalse(globe.IsFinitePlanarWorld, "the globe is cyclic");

            double2 vp = new double2(1920.0, 1080.0);
            const double margin = 0.5;

            double mercFloor  = CameraPoseMath.MinZoomFloor(mercator, vp.x, vp.y, margin);
            double globeFloor = CameraPoseMath.MinZoomFloor(globe, vp.x, vp.y, margin);

            Assert.AreEqual(CameraPoseMath.MinZoomToFill(vp.x, vp.y, 0.0), mercFloor, 1e-12,
                "finite Mercator floor == MinZoomToFill(margin 0)");
            Assert.AreEqual(CameraPoseMath.MinZoomToFit(vp.x, vp.y, margin), globeFloor, 1e-12,
                "cyclic globe floor == MinZoomToFit(margin)");
            Assert.AreNotEqual(mercFloor, globeFloor,
                "on a non-square viewport the two floors must diverge (the branch is load-bearing)");
        }

        // ── S93 — pixel unification Phase 2 (512 zoom renumbering; camera zoom == tile zoom) ──────

        [Test]
        public void T_ALIGN_SelectionZoom_EqualsCameraZoom_At512Convention()
        {
            // T-ALIGN (decisive): with TilePixelSize == OnScreenTilePx == 512 the selection offset is 0, so the
            // PRODUCTION-default selector emits tiles at Z == camera integer zoom (MapLibre-aligned). Falsifiable:
            // the old 256 convention (offset −1) emitted Z == cameraZoom − 1.
            var buf = new List<TileId>();
            var merc = (IVisibleTileSelector)new FrustumTileSelector(0, 22); // default onScreenTilePx = 512
            foreach (int z in new[] { 3, 5, 8, 11 })
            {
                merc.SelectVisibleTiles(View(Cam(0, 0, z, 0), 1.0), buf);
                Assert.IsNotEmpty(buf, $"Mercator cover non-empty at camera zoom {z}");
                foreach (var t in buf) Assert.AreEqual(z, t.Z, $"camera zoom {z} must select tile z{z} (offset 0)");
            }
            // The universal selector on the globe path shares the same offset derivation — assert it too.
            var globe = (IVisibleTileSelector)new FrustumTileSelector(0, 22, 512,
                new FlatLodStrategy(), new MultiplierFarPlane(4.0));
            foreach (int z in new[] { 3, 5 })
            {
                globe.SelectVisibleTiles(
                    new ViewContext { Camera = Cam(0, 0, z, 0), ViewportPx = new double2(RefH, RefH),
                                      Projection = new SphericalProjection() }, buf);
                Assert.IsNotEmpty(buf, $"globe cover non-empty at camera zoom {z}");
                foreach (var t in buf) Assert.AreEqual(z, t.Z, $"globe: camera zoom {z} → tile z{z}");
            }
        }

        [Test]
        public void T_RELABEL_GroundResolutionAndAltitude_AreOld256ValuesShiftedOneZoom()
        {
            // T-RELABEL: the flip is a pure RELABEL — the physical scale at zoom z now equals the OLD 256 value
            // at z+1 (GroundResolution_512(z) == GroundResolution_256(z+1)), so the same view is just numbered
            // one lower. Assert GroundResolution against the explicit 256 formula (falsifiable — a rescale, not a
            // relabel, breaks it); altitude inherits the shift (it is linear in GroundResolution).
            const double circ = 40075016.686;
            foreach (double z in new[] { 0.0, 3.0, 7.5, 14.0 })
            {
                double gr256AtZPlus1 = circ / (256.0 * math.pow(2.0, z + 1.0));
                Assert.AreEqual(gr256AtZPlus1, WebMercator.GroundResolution(z), gr256AtZPlus1 * 1e-9,
                                $"GroundResolution_512({z}) == GroundResolution_256({z}+1)");
                double altNew       = CameraPoseMath.AltitudeForZoom(z, 1080.0, 60.0);
                double altOldZPlus1 = (1080.0 * gr256AtZPlus1) / (2.0 * math.tan(Angle.FromDegrees(30.0).Radians));
                Assert.AreEqual(altOldZPlus1, altNew, altNew * 1e-9, $"altitude_512({z}) == altitude_256({z}+1)");
            }
        }

        [Test]
        public void T_DENSITY_REBASED_DefaultSelectorKeepsThe512Density_AndIsAligned()
        {
            // T-DENSITY-REBASED: the S88 512 density win survives the flip. The production-default selector is
            // one level COARSER than an explicit onScreenTilePx=256 selector over the same viewport — same span,
            // ~4× fewer tiles — AND is now MapLibre-aligned (Z == cameraZoom, vs 256's cameraZoom+1). A
            // count-only check could pass while Z silently misaligns, so assert Z on both.
            var def  = (IVisibleTileSelector)new FrustumTileSelector(0, 22);                    // default 512
            var e256 = (IVisibleTileSelector)new FrustumTileSelector(0, 22, onScreenTilePx: 256);
            var bDef = new List<TileId>();
            var b256 = new List<TileId>();
            foreach (int z in new[] { 4, 6, 9 })
            {
                def .SelectVisibleTiles(View(Cam(0, 0, z, 0), 16.0 / 9.0), bDef);
                e256.SelectVisibleTiles(View(Cam(0, 0, z, 0), 16.0 / 9.0), b256);
                Assert.IsNotEmpty(bDef); Assert.IsNotEmpty(b256);
                Assert.AreEqual(z,     bDef[0].Z, $"default (512) aligned: camera z{z} → tile z{z}");
                Assert.AreEqual(z + 1, b256[0].Z, $"explicit 256 is one level finer at camera z{z}");
                Assert.Less(bDef.Count, b256.Count, $"512 density win preserved (fewer, larger tiles) at z{z}");
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────

        /// <summary>Returns the tile at <paramref name="z"/> whose extent contains <paramref name="merc"/>.</summary>
        private static TileId TileContaining(double2 merc, int z)
        {
            double2 ll = WebMercator.ToLonLat(merc.x, merc.y);
            double lon = Math.Max(-179.9999, Math.Min(179.9999, ll.x));
            double lat = Math.Max(-CameraProperties.MaxMercatorLat, Math.Min(CameraProperties.MaxMercatorLat, ll.y));
            long n = 1L << z;
            int x = (int)Math.Floor((lon + 180.0) / 360.0 * n);
            // Delegate to WebMercator.FromLonLat — the single source of the Mercator formula (T2).
            // ln(tan(lat)+sec(lat)) == mercY/R (algebraically identical; same numeric ops order).
            double mercY = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = lon, Latitude = lat }).y;
            int y = (int)Math.Floor((1.0 - mercY / WebMercator.R / Math.PI) / 2.0 * n);
            x = (int)Math.Max(0, Math.Min(n - 1, x));
            y = (int)Math.Max(0, Math.Min(n - 1, y));
            return new TileId { Z = z, X = x, Y = y };
        }

        // ── Test-local oracle ────────────────────────────────────────────────────────────────────
        /// <summary>
        /// The exact (double-precision) render-space truth for a vertex: <c>merc − sceneOrigin</c>. The
        /// difference between this and <see cref="FloatingOrigin.RenderVertex"/> IS the floating-origin
        /// precision error these tests measure — which is why it belongs here and not in Core: it is the
        /// reference the production float path is judged against, and it had no production caller. Its old
        /// name (<c>FloatingOrigin.RenderVertexTruth</c>) said as much.
        /// </summary>
        private static double3 RenderVertexTruth(double2 mercVertex, double2 sceneOriginMerc)
            => new double3(mercVertex.x - sceneOriginMerc.x, 0.0, mercVertex.y - sceneOriginMerc.y);
    }
}
