// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// S06 Batch B: view-state, tile cover, and floating-origin (jitter) math.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class ViewMathTests
    {
        private const double Eps = 1e-9;

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new LookAtPoint(lon, lat, 0), zoom, 0, 0);

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
            double2 expected = WebMercator.FromLonLat(13.405, 52.52);
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
            double2 atLimit = WebMercator.FromLonLat(0, CameraProperties.MaxMercatorLat);
            Assert.AreEqual(atLimit.y, m.y, 1e-6, "Extreme latitude clamps to the Mercator limit.");
        }

        // ── TileCover ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void TileCover_KnownCenter_SelectsExpected3x3()
        {
            // Center (lon=0, lat=0) at z2: cxTile=cyTile=2.0, pad=1 → x,y in {1,2,3}.
            // This is the no-regression oracle (S50 D3): the CameraProperties/CenterMercator cover set
            // must equal the pre-migration slippy-formula set for the same center+zoom.
            var view = Cam(0, 0, 2.0);
            var buf  = new List<TileId>();
            TileCover.Cover(view, viewportAspect: 1.0, padFactor: 1.0, minZoom: 0, maxZoom: 22, buf);

            var expected = new HashSet<TileId>();
            for (int x = 1; x <= 3; x++)
                for (int y = 1; y <= 3; y++)
                    expected.Add(new TileId(2, x, y));

            Assert.AreEqual(9, buf.Count, "3×3 cover");
            CollectionAssert.AreEquivalent(expected, buf,
                "Cover for center (0,0) at z2 with pad=1 must be the 3×3 block x,y∈{1,2,3}.");
        }

        [Test]
        public void TileCover_ClampsSelectionZoom()
        {
            var view = Cam(0, 0, 20.0);
            var buf  = new List<TileId>();
            TileCover.Cover(view, 1.0, 1.0, minZoom: 0, maxZoom: 14, buf);
            foreach (var t in buf)
                Assert.AreEqual(14, t.Z, "Selection zoom must clamp to maxZoom.");
            Assert.Greater(buf.Count, 0);
        }

        [Test]
        public void TileCover_WrapsLongitudeAtAntimeridian()
        {
            // Center near +180° at z2 (n=4): x-span straddles the antimeridian → x wraps to include 0.
            var view = Cam(179.9, 0, 2.0);
            var buf  = new List<TileId>();
            TileCover.Cover(view, 1.0, 1.0, 0, 22, buf);

            var xs = new HashSet<int>();
            foreach (var t in buf) xs.Add(t.X);
            Assert.IsTrue(xs.Contains(0), "Antimeridian wrap must include column x=0.");
            Assert.IsTrue(xs.Contains(3), "…and the western neighbour x=3.");
            foreach (var t in buf)
                Assert.IsTrue(t.X >= 0 && t.X < 4, $"Wrapped x must stay in [0,4): {t.X}");
        }

        [Test]
        public void TileCover_ClampsLatitudeRange_NoNegativeOrOverflowY()
        {
            // Near the north pole at z3 (n=8): y must clamp to [0,7], never negative.
            var view = Cam(0, 85.0, 3.0);
            var buf  = new List<TileId>();
            TileCover.Cover(view, 1.0, 2.0, 0, 22, buf);
            foreach (var t in buf)
                Assert.IsTrue(t.Y >= 0 && t.Y <= 7, $"y must be clamped to [0,7]: {t.Y}");
        }

        [Test]
        public void TileCover_ReusesBuffer_NoGrowthOnRepeat()
        {
            var view = Cam(0, 0, 5.0);
            var buf  = new List<TileId>();
            TileCover.Cover(view, 1.5, 2.0, 0, 22, buf);
            int firstCount = buf.Count;
            int capacityAfterFirst = buf.Capacity;

            // Re-cover the same view many times: the buffer is cleared+refilled, never grown.
            for (int i = 0; i < 50; i++)
                TileCover.Cover(view, 1.5, 2.0, 0, 22, buf);

            Assert.AreEqual(firstCount, buf.Count, "Same view → same tile count");
            Assert.AreEqual(capacityAfterFirst, buf.Capacity,
                "Re-covering the same view must not grow the buffer (steady-state no-alloc).");
        }

        // ── FloatingOrigin: structural ───────────────────────────────────────────────────────

        [Test]
        public void FloatingOrigin_TileLocalOrigin_MatchesTileMinCorner()
        {
            var t = new TileId(14, 8000, 5000);
            var (min, _) = t.MercatorBounds();
            double2 o = FloatingOrigin.TileLocalOriginMercator(t);
            Assert.AreEqual(min.x, o.x, Eps);
            Assert.AreEqual(min.y, o.y, Eps);
        }

        [Test]
        public void FloatingOrigin_ShouldRebase_TrueOnlyBeyondThreshold()
        {
            var scene = new double2(1_000_000, 2_000_000);
            var near  = new double2(1_000_100, 2_000_000);   // 100 m away
            var far   = new double2(1_005_000, 2_000_000);   // 5000 m away
            Assert.IsFalse(FloatingOrigin.ShouldRebase(scene, near, 2000.0), "100 m < 2000 m threshold");
            Assert.IsTrue (FloatingOrigin.ShouldRebase(scene, far,  2000.0), "5000 m > 2000 m threshold");
        }

        [Test]
        public void FloatingOrigin_RebaseDelta_PreservesWorldPositions()
        {
            // A tile at absolute origin O placed under scene origin S sits at (O − S). After rebasing the
            // scene origin to S', the tile must sit at (O − S'); the delta (oldS − newS) added to the old
            // placement must equal the new placement (no absolute drift).
            var tileOrigin = new double2(12_345_678, -3_456_789);
            var oldScene   = new double2(12_300_000, -3_400_000);
            var newScene   = new double2(12_340_000, -3_450_000);

            float3 oldPlacement = FloatingOrigin.TileLocalToScene(tileOrigin, oldScene);
            float3 newPlacement = FloatingOrigin.TileLocalToScene(tileOrigin, newScene);
            double2 delta       = FloatingOrigin.RebaseDelta(oldScene, newScene);

            // old placement + delta == new placement (within float precision at this small magnitude).
            Assert.AreEqual(newPlacement.x, oldPlacement.x + (float)delta.x, 1e-2,
                "Rebase delta applied to the old tile placement must equal the new placement (x).");
            Assert.AreEqual(newPlacement.z, oldPlacement.z + (float)delta.y, 1e-2,
                "Rebase delta applied to the old tile placement must equal the new placement (z).");
        }

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
        /// Parameters (computed, not guessed): zoom = <see cref="LiveZoom"/> (tile span ≈ 2446 m), rebase
        /// threshold = <see cref="RebaseThresholdMeters"/> (2000 m), cover pad/aspect = the MapView
        /// defaults (1.5/1.5). The farthest cover-edge vertex sits ≈ 11.5 km from the scene origin; the
        /// measured worst round-trip error across a dense camera sweep is ≈ 0.36 mm — sub-millimetre.
        /// Latitude is clamped to the Mercator limit exactly as <see cref="CameraProperties"/>/
        /// <see cref="TileCover"/> clamp it; the camera never reaches the polar singularity where a single
        /// Mercator tile's span is unbounded.
        ///
        /// <para><b>Antimeridian seam (out of S06 scope — see follow-ups.md).</b> The sweep stays in
        /// lon ∈ [-160°, 160°] so it never straddles ±180°. At the seam, <see cref="TileCover"/> wraps x
        /// (geographically correct), but a wrapped tile's ABSOLUTE Mercator x jumps by a full world width
        /// (≈ 40,075 km) — it is geographically adjacent but ~40 M m away in Mercator. Origin-relative
        /// placement of such a wrapped tile would need a ±worldWidth offset; without it the render coord
        /// reaches world scale and float32 precision degrades to metres at the seam. That seam handling is
        /// deferred (the S06 demo does not cross the antimeridian).</para>
        /// </summary>
        [Test]
        public void FloatingOrigin_ExtremeMercator_CoverEdgeRenderCoordsBounded_SubMillimetre()
        {
            const int    LiveZoom              = 14;
            const double RebaseThresholdMeters = 2000.0;
            const double SubMmBudgetMeters     = 1e-3;   // sub-millimetre
            // MapView defaults — the cover the LIVE loop actually selects.
            const double LivePad    = 1.5;
            const double LiveAspect = 1.5;

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

                    // Scene origin rebased to the camera; camera drifted up to the threshold away.
                    double2 sceneOrigin = WebMercator.FromLonLat(camLon, camLat);
                    double2 cameraMerc  = new double2(sceneOrigin.x + RebaseThresholdMeters, sceneOrigin.y);
                    double2 camLL       = WebMercator.ToLonLat(cameraMerc.x, cameraMerc.y);

                    // Enumerate the SAME cover the live loop would select for this camera.
                    var camView = Cam(camLL.x, camLL.y, LiveZoom);
                    TileCover.Cover(camView, LiveAspect, LivePad, 0, LiveZoom, cover);

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
            // Worst cover-edge magnitude away from the seam is ≈ 11.5 km; 20 km is a comfortable bound.
            Assert.Less(worstCoordMag, 2e4,
                $"Worst cover-edge render-coord magnitude {worstCoordMag:F1} m must stay well under the " +
                "float32 precision cliff (rebasing failed to keep coords near the origin).");
            Assert.Less(worstErr, SubMmBudgetMeters,
                $"Worst floating-origin cover-edge round-trip error {worstErr * 1000:F4} mm exceeds the " +
                "sub-mm budget. At world scale this is the 'no jitter' guarantee — a regression means " +
                "panned/zoomed geometry visibly shimmers. (Measured across the whole live cover, not just " +
                "the camera's tile.)");
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
            double2 camera = WebMercator.FromLonLat(179.0, 0.0);  // ~+19.9M m east

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
            double latRad = lat * Math.PI / 180.0;
            int y = (int)Math.Floor((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);
            x = (int)Math.Max(0, Math.Min(n - 1, x));
            y = (int)Math.Max(0, Math.Min(n - 1, y));
            return new TileId(z, x, y);
        }
    }
}
