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

        // ── S71: IVisibleTileSelector / ViewportCornerTileSelector ──────────────────────────────
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
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(padTiles: 1, minZoom: 0, maxZoom: 22);
            var buf = new List<TileId>();

            foreach (var fc in FrameCases())
            {
                var cam = Cam(fc.Lon, fc.Lat, fc.Zoom, fc.HeadingDeg);
                int z   = cam.IntegerZoom;
                double mpp   = WebMercator.GroundResolution(cam.Zoom);
                double halfV = RefH * mpp / 2.0;
                double halfH = halfV * fc.Aspect;

                sel.SelectVisibleTiles(View(cam, fc.Aspect), buf);
                var S = new HashSet<TileId>(buf);

                // The four framing corners AND a dense interior grid must all be covered. One miss ⇒ a
                // visible pixel with no tile ⇒ white border.
                const int G = 16; // 17×17 incl. corners
                for (int i = 0; i <= G; i++)
                for (int j = 0; j <= G; j++)
                {
                    double a = -halfH + (2.0 * halfH) * i / G;
                    double b = -halfV + (2.0 * halfV) * j / G;
                    double2 ground = FramingGround(in cam, a, b);
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
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(1, 0, 22);
            var buf = new List<TileId>();

            foreach (var fc in FrameCases())
            {
                var cam = Cam(fc.Lon, fc.Lat, fc.Zoom, fc.HeadingDeg);
                int z   = cam.IntegerZoom;
                double2 vp = new double2(RefH * fc.Aspect, RefH);

                sel.SelectVisibleTiles(View(cam, fc.Aspect), buf);
                var S = new HashSet<TileId>(buf);

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
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(pad, 0, 22);
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
                long bound = (long)((math.ceil(spanX) + 2 * pad + 1) * (math.ceil(spanY) + 2 * pad + 1));

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
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(0, 0, 22);
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
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(0, 0, 22);
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
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(1, 0, 22);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(View(Cam(0, 0, 2.0), 1.0), buf);

            var cameraTile = MercToTile(Cam(0, 0, 2.0).CenterMercator(), 2);
            Assert.IsTrue(buf.Contains(cameraTile),
                $"Cover must contain the camera's own tile {cameraTile.X},{cameraTile.Y}.");

            var xs = buf.Select(t => t.X).Distinct().OrderBy(v => v).ToList();
            var ys = buf.Select(t => t.Y).Distinct().OrderBy(v => v).ToList();
            Assert.AreEqual(xs.Count, xs.Last() - xs.First() + 1, "x columns must be contiguous (no holes).");
            Assert.AreEqual(ys.Count, ys.Last() - ys.First() + 1, "y rows must be contiguous (no holes).");
            Assert.AreEqual(xs.Count * ys.Count, buf.Count, "the block must be a full rectangle (no holes).");
            foreach (var t in buf) Assert.AreEqual(2, t.Z, "single selection zoom z2 for this case.");
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
            // algorithm knob (no padTiles / minZoom / maxZoom / selectionZoom of any kind).
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

        // ── T-WRAP / T-POLE / T-ALLOC (preserved guarantees, now through the seam) ──────────────

        [Test]
        public void Selector_TWrap_LongitudeAtAntimeridian()
        {
            // Center near +180° at z2 (n=4), wide viewport: the x-span straddles the seam → wrap includes 0.
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(1, 0, 22);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(View(Cam(179.5, 0, 2.0), 16.0 / 9.0), buf);

            var xs = new HashSet<int>(buf.Select(t => t.X));
            Assert.IsTrue(xs.Contains(0), "Antimeridian wrap must include column x=0.");
            Assert.IsTrue(xs.Contains(3), "…and the western neighbour x=3.");
            foreach (var t in buf) Assert.IsTrue(t.X >= 0 && t.X < 4, $"Wrapped x must stay in [0,4): {t.X}");
            Assert.AreEqual(buf.Count, buf.Distinct().Count(), "No duplicate (z,x,y) even at full world width.");
        }

        [Test]
        public void Selector_TPole_ClampsLatitudeRange()
        {
            // Near the north pole at z3 (n=8): every emitted y ∈ [0,7], never negative / never ≥ n.
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(1, 0, 22);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(View(Cam(0, 85.0, 3.0), 1.0), buf);
            foreach (var t in buf)
                Assert.IsTrue(t.Y >= 0 && t.Y <= 7, $"y must be clamped to [0,7]: {t.Y}");
            Assert.Greater(buf.Count, 0);
        }

        [Test]
        public void Selector_ClampsSelectionZoom()
        {
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(1, 0, 14);
            var buf = new List<TileId>();
            sel.SelectVisibleTiles(View(Cam(0, 0, 20.0), 1.0), buf);
            foreach (var t in buf) Assert.AreEqual(14, t.Z, "Selection zoom must clamp to maxZoom.");
            Assert.Greater(buf.Count, 0);
        }

        [Test]
        public void Selector_TAlloc_ReusesBuffer_NoGrowthOnRepeat()
        {
            var sel = (IVisibleTileSelector)new ViewportCornerTileSelector(1, 0, 22);
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
            var liveSelector = (IVisibleTileSelector)new ViewportCornerTileSelector(padTiles: 1, minZoom: 0, maxZoom: LiveZoom);

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
                            double3 truth    = FloatingOrigin.RenderVertexTruth(v, sceneOrigin);

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
            double3 truth    = FloatingOrigin.RenderVertexTruth(tMax, sceneOrigin);
            double err = Math.Abs(rendered.x - truth.x);

            Assert.Greater(err, 0.5,
                "With NO rebasing at world scale (~20M m from origin) the float32 round-trip error must be " +
                "large (>0.5 m) — this is exactly the jitter floating-origin rebasing eliminates.");
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
    }
}
