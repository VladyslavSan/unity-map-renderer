// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// A-3: cross-tile point-symbol identity (<see cref="CrossTileSymbolKey"/>) — the <c>For</c>-math primitive,
    /// engine-free.
    ///
    /// <para><see cref="CrossTileSymbolKey.For"/> stays a GENERAL grid primitive — still
    /// <c>quantizeMeters</c>-parameterized, its snapping math unchanged — so the teeth below assert it at an
    /// explicit grid: (1) a grid is COARSE ENOUGH — a point's real parent (z10) vs child (z11) reprojection
    /// lands within one cell (the doc's original "1px-at-max-zoom" would not); (2) FINE ENOUGH — distinct symbols
    /// &gt; a few cells apart keep different keys; plus the 3-axis (globe Y), icon, and text parity cases.</para>
    ///
    /// <para><b>Reader cutover (4.2).</b> The <c>Store_*</c> cases that exercised
    /// <see cref="MapRenderer.Unity.Text.SymbolTileStore"/>'s dedup moved to
    /// <c>CrossTileIdentityStoreTests</c> (EditMode-only, alongside this file) — the store now reads a baked
    /// native block, which needs <c>Unity.Collections</c> that this project's core-tests shim lacks. This file's
    /// OWN <see cref="CrossTileSymbolKey.For"/> math teeth are untouched and stay in the fast loop.</para>
    /// </summary>
    [TestFixture]
    public class CrossTileIdentityTests
    {
        // ── (1) The decisive falsifier: a point's REAL adjacent-zoom reprojection collapses. Same physical
        //    location as MVT-quantized in a z10 tile vs its z11 child lands within ONE display-zoom pixel cell,
        //    so a display-pixel grid (GroundResolution at the coarser zoom) collapses them. ──
        [Test]
        public void AdjacentZoomReprojection_LandsWithinOneDisplayPixelCell()
        {
            var proj = new WebMercatorProjection();
            const double extent = 4096;
            // z11 tile (1000,800) is the top-left child of z10 tile (500,400) — they share a corner origin, so
            // matching local coords address ~the same geo. Slightly different integer coords model the two
            // tiles' independent MVT quantization of the same feature.
            var z11 = new TileId { Z = 11, X = 1000, Y = 800 };
            var z10 = new TileId { Z = 10, X = 500, Y = 400 };
            double2 g11 = z11.ToLonLat(101, 101, extent);
            double2 g10 = z10.ToLonLat(50, 50, extent);
            double3 a11 = proj.Project(new GeoCoordinate { Latitude = g11.y, Longitude = g11.x });
            double3 a10 = proj.Project(new GeoCoordinate { Latitude = g10.y, Longitude = g10.x });

            double q = WebMercator.GroundResolution(10); // one logical px at the coarser (display) zoom
            Assert.Less(math.abs(a10.x - a11.x), q, "the parent/child anchor x-diff is under one display pixel");
            Assert.Less(math.abs(a10.z - a11.z), q, "the parent/child anchor z-diff is under one display pixel");

            // …and 1px-at-MAX-zoom (the doc's original number) is FAR too fine to collapse it — the tooth that
            // proves the granularity had to be display-relative, not a fixed max-zoom grid.
            double qMax = WebMercator.GroundResolution(22);
            Assert.Greater(math.abs(a10.x - a11.x), qMax, "a max-zoom grid would NOT collapse the diff (why display-zoom)");
        }

        // ── (S1) the 3-axis defect: on the globe, two equator-mirrored anchors (30°N / 30°S, same longitude)
        //    share render X/Z (both ∝ cosφ) but differ in Y (=sinφ·R, opposite sign) — an x/z-only key collides
        //    them into one symbol. Non-look-at latitudes so cosφ≠0 (X/Z genuinely equal, not both ~0). ──
        [Test]
        public void GlobeEquatorMirroredAnchors_AreDistinct()
        {
            var proj = new SphericalProjection();
            double3 render30N = proj.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 45.0 });
            double3 render30S = proj.Project(new GeoCoordinate { Latitude = -30.0, Longitude = 45.0 });
            double q = CameraPoseMath.MetersPerPixel(6.0);

            Assert.AreNotEqual(CrossTileSymbolKey.For(render30N, 0, "X", null, q), CrossTileSymbolKey.For(render30S, 0, "X", null, q),
                "30°N and 30°S at the same longitude share render X/Z but must stay distinct labels (the Y axis)");
        }

        // ── (1b)/(2) synthetic mid-cell control: within a cell → same key; a few cells away → different key
        //    (deterministic, no projection-boundary flakiness). ──
        [Test]
        public void Quantization_CollapsesWithinCell_SeparatesBeyond()
        {
            const double q = 50.0;
            double3 mid = new double3(q * 100.0, 0, q * 100.0); // a cell CENTRE (k·q), safe from a round boundary
            double3 near = mid + new double3(3.0, 0, -3.0);     // a few metres → same cell
            double3 far = mid + new double3(3.0 * q, 0, 0);     // 3 cells away → different

            Assert.AreEqual(CrossTileSymbolKey.For(mid, 0, "T", null, q), CrossTileSymbolKey.For(near, 0, "T", null, q),
                "anchors within a grid cell share one identity");
            Assert.AreNotEqual(CrossTileSymbolKey.For(mid, 0, "T", null, q), CrossTileSymbolKey.For(far, 0, "T", null, q),
                "anchors several cells apart are distinct labels");
        }

        // ── (2b) same cell, DIFFERENT text → different identity (full-text equality, not a hash collision). ──
        [Test]
        public void SameCellDifferentText_AreDistinct()
        {
            const double q = 50.0;
            double3 a = new double3(q * 10.5, 0, q * 10.5);
            Assert.AreNotEqual(CrossTileSymbolKey.For(a, 0, "Paris", null, q), CrossTileSymbolKey.For(a, 0, "Lyon", null, q));
            Assert.AreNotEqual(CrossTileSymbolKey.For(a, 0, "Paris", null, q), CrossTileSymbolKey.For(a, 1, "Paris", null, q),
                "same anchor+text on a different layer is a different label");
        }

        // ── I6 (a): two icon symbols (text=null) at the SAME cell/layer but DIFFERENT icon-image must stay
        //    distinct — the I5b-deferred gap this stage closes (pre-fix they collide: same text==null, so the
        //    old 4-arg key ignored the icon entirely). ──
        [Test]
        public void IconIdentity_DistinctIconImage_AreDistinct()
        {
            const double q = 50.0;
            double3 a = new double3(q * 10.5, 0, q * 10.5);
            var keyA = CrossTileSymbolKey.For(a, 0, null, "sprite-a", q);
            var keyB = CrossTileSymbolKey.For(a, 0, null, "sprite-b", q);
            Assert.AreNotEqual(keyA, keyB, "same cell/layer, different icon-image → distinct identity");
            Assert.AreNotEqual(keyA.GetHashCode(), keyB.GetHashCode(), "…and distinct hashes");
        }

        // ── I6 (b): the SAME icon-image across a parent/child reprojected anchor (the real adjacent-zoom
        //    diff from teeth (1)) still collapses to one identity — icons get the same seamless-swap dedup
        //    text symbols already have. ──
        [Test]
        public void IconIdentity_SameIconAcrossParentChildAnchor_AreEqual()
        {
            var proj = new WebMercatorProjection();
            const double extent = 4096;
            var z11 = new TileId { Z = 11, X = 1000, Y = 800 };
            var z10 = new TileId { Z = 10, X = 500, Y = 400 };
            double2 g11 = z11.ToLonLat(101, 101, extent);
            double2 g10 = z10.ToLonLat(50, 50, extent);
            double3 a11 = proj.Project(new GeoCoordinate { Latitude = g11.y, Longitude = g11.x });
            double3 a10 = proj.Project(new GeoCoordinate { Latitude = g10.y, Longitude = g10.x });
            double q = WebMercator.GroundResolution(10);

            Assert.AreEqual(CrossTileSymbolKey.For(a10, 0, null, "sprite-a", q), CrossTileSymbolKey.For(a11, 0, null, "sprite-a", q),
                "the same icon reprojected parent→child lands in the same identity cell");
        }

        // ── I6 (c) text parity: two text keys (IconImage explicitly null) are equal + hash equal — the guard-skip
        //    fold must not perturb the text-only path. ──
        [Test]
        public void TextParity_ExplicitNullIconImage_EqualsAndHashesSame()
        {
            const double q = 50.0;
            double3 a = new double3(q * 3.5, 0, q * 3.5);
            var k1 = CrossTileSymbolKey.For(a, 0, "Paris", null, q);
            var k2 = CrossTileSymbolKey.For(a, 0, "Paris", null, q);
            Assert.AreEqual(k1, k2);
            Assert.AreEqual(k1.GetHashCode(), k2.GetHashCode());
        }

    }
}
