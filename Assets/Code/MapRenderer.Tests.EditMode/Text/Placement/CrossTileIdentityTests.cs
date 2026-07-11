// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// A-3: cross-tile point-label identity (<see cref="CrossTileLabelKey"/>) + the store's dedup. Teeth:
    /// (1) the display-zoom pixel grid is COARSE ENOUGH — a point's real parent (z10) vs child (z11)
    /// reprojection lands within one grid cell (the doc's original "1px-at-max-zoom" would not);
    /// (2) it is FINE ENOUGH — distinct labels &gt; a few cells apart keep different keys (no over-merge);
    /// (3) the store dedups a symbol present in both a parent and child tile to ONE, finest-zoom wins;
    /// (4) different text in the same cell does NOT merge; (5) line labels are not deduped; (6) quantize ≤ 0
    /// disables dedup (the pre-A-3 pass-through).
    /// </summary>
    [TestFixture]
    public class CrossTileIdentityTests
    {
        private static LabelInstance PointLabel(double3 anchor, int layer, string text, TileId tile)
            => new LabelInstance
            {
                Placement = SymbolPlacement.Point,
                AnchorRender = anchor,
                MaterialIndex = layer,
                Text = text,
                TileKey = SymbolFeatureExtractor.PackTileKey(tile),
            };

        private static List<LabelInstance> Collect(SymbolTileLabelStore store, double q)
        {
            var output = new List<LabelInstance>();
            store.CollectInto(output, q);
            return output;
        }

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

        // ── (1b)/(2) synthetic mid-cell control: within a cell → same key; a few cells away → different key
        //    (deterministic, no projection-boundary flakiness). ──
        [Test]
        public void Quantization_CollapsesWithinCell_SeparatesBeyond()
        {
            const double q = 50.0;
            double3 mid = new double3(q * 100.0, 0, q * 100.0); // a cell CENTRE (k·q), safe from a round boundary
            double3 near = mid + new double3(3.0, 0, -3.0);     // a few metres → same cell
            double3 far = mid + new double3(3.0 * q, 0, 0);     // 3 cells away → different

            Assert.AreEqual(CrossTileLabelKey.For(mid, 0, "T", q), CrossTileLabelKey.For(near, 0, "T", q),
                "anchors within a grid cell share one identity");
            Assert.AreNotEqual(CrossTileLabelKey.For(mid, 0, "T", q), CrossTileLabelKey.For(far, 0, "T", q),
                "anchors several cells apart are distinct labels");
        }

        // ── (2b) same cell, DIFFERENT text → different identity (full-text equality, not a hash collision). ──
        [Test]
        public void SameCellDifferentText_AreDistinct()
        {
            const double q = 50.0;
            double3 a = new double3(q * 10.5, 0, q * 10.5);
            Assert.AreNotEqual(CrossTileLabelKey.For(a, 0, "Paris", q), CrossTileLabelKey.For(a, 0, "Lyon", q));
            Assert.AreNotEqual(CrossTileLabelKey.For(a, 0, "Paris", q), CrossTileLabelKey.For(a, 1, "Paris", q),
                "same anchor+text on a different layer is a different label");
        }

        // ── (3) THE seamless-swap dedup: the same symbol active in a parent + child tile collapses to ONE,
        //    keeping the finest (child) zoom. ──
        [Test]
        public void Store_ParentAndChildSameSymbol_DedupToFinest()
        {
            const double q = 50.0;
            double3 anchor = new double3(q * 100.0, 0, q * 100.0); // a cell CENTRE
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };
            var parentLabel = PointLabel(anchor + new double3(2, 0, 2), 0, "Metropolis", parent); // within a cell
            var childLabel = PointLabel(anchor, 0, "Metropolis", child);

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var kParent = new SymbolTileLabelStore.Key("src", parent);
            var kChild = new SymbolTileLabelStore.Key("src", child);
            store.CompleteBuild(kParent, store.BeginBuild(kParent), new List<LabelInstance> { parentLabel });
            store.CompleteBuild(kChild, store.BeginBuild(kChild), new List<LabelInstance> { childLabel });

            List<LabelInstance> output = Collect(store, q);
            Assert.AreEqual(1, output.Count, "the duplicate parent+child symbol collapses to one");
            Assert.AreSame(childLabel, output[0], "the finest (child) tile's label wins");
        }

        // ── (4) two GENUINELY distinct nearby symbols (different cells) both survive. ──
        [Test]
        public void Store_DistinctSymbols_BothSurvive()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };
            var a = PointLabel(new double3(q * 10.5, 0, q * 10.5), 0, "A", tile);
            var b = PointLabel(new double3(q * 40.5, 0, q * 10.5), 0, "B", tile);

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = new SymbolTileLabelStore.Key("src", tile);
            store.CompleteBuild(key, store.BeginBuild(key), new List<LabelInstance> { a, b });
            Assert.AreEqual(2, Collect(store, q).Count, "distinct-cell symbols are not merged");
        }

        // ── (5) line labels are excluded from dedup in v1 (two coincident line labels both pass through). ──
        [Test]
        public void Store_LineLabels_AreNotDeduped()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };
            var line1 = new LabelInstance { Placement = SymbolPlacement.Line, Text = "Main St", MaterialIndex = 0,
                TileKey = SymbolFeatureExtractor.PackTileKey(tile) };
            var line2 = new LabelInstance { Placement = SymbolPlacement.LineCenter, Text = "Main St", MaterialIndex = 0,
                TileKey = SymbolFeatureExtractor.PackTileKey(tile) };

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var key = new SymbolTileLabelStore.Key("src", tile);
            store.CompleteBuild(key, store.BeginBuild(key), new List<LabelInstance> { line1, line2 });
            Assert.AreEqual(2, Collect(store, q).Count, "line labels pass through undeduped (per-anchor identity is a follow-up)");
        }

        // ── (6) quantize ≤ 0 disables dedup — the pre-A-3 pass-through (duplicates NOT collapsed). ──
        [Test]
        public void Store_QuantizeDisabled_EmitsEverything()
        {
            const double q = 50.0;
            double3 anchor = new double3(q * 5.5, 0, q * 5.5);
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var kP = new SymbolTileLabelStore.Key("src", parent);
            var kC = new SymbolTileLabelStore.Key("src", child);
            store.CompleteBuild(kP, store.BeginBuild(kP), new List<LabelInstance> { PointLabel(anchor, 0, "X", parent) });
            store.CompleteBuild(kC, store.BeginBuild(kC), new List<LabelInstance> { PointLabel(anchor, 0, "X", child) });
            Assert.AreEqual(2, Collect(store, 0.0).Count, "quantize<=0 is the pre-A-3 pass-through: both emitted");
        }
    }
}
