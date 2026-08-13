// Unity EditMode only — drives SymbolFeatureExtractor over the Waist-1 native buffer.
// NOT registered in Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests.Jobs;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// IR C1 P2 — tooth <b>E3</b>: the symbol extractor's ring bucketing re-based from the <b>selected</b>
    /// feature list onto the source layer's own feature ordinals.
    ///
    /// <para><b>Written BEFORE the conversion.</b> It drives the production entry point
    /// (<see cref="SymbolFeatureExtractor.Extract"/>) with the signature P2 does not break, so it measures the
    /// pre-conversion label sequence and re-measures the post-conversion one.</para>
    ///
    /// <para><b>Why a separate fixture from <c>SymbolBufferParityTests</c>.</b> That file's T1 states, in its
    /// own doc, that its arm B <i>transcribes</i> the production bucketing rather than calling it — a defect
    /// injected into <c>Extract</c>'s own <c>ringStart</c>/<c>ringOrder</c> leaves it green. E3's whole claim
    /// is about that bucketing, so it must run production and compare against an independent control, not
    /// against a re-implementation.</para>
    ///
    /// <para><b>The production configuration is the one under test</b> (the standing check). The symbol
    /// fixtures in this repo hand the extractor a layer whose features it selects in full, so ordinal == slot
    /// and the re-base is inert. Here the filter admits a <b>strict subset</b> (ordinals
    /// <c>[0, 2, 3, 5]</c> of six); the unselected features <b>carry paths</b>; a <c>Polygon</c> is
    /// <b>selected but undrawable</b>, sitting between selected features; the selected features are
    /// <b>multi-path</b>, so bucketing is load-bearing; and — the discriminator for the specific off-by-N the
    /// plan names — the <b>highest selected ordinal (5) exceeds the selected count (4)</b>, so a
    /// <c>ringStart</c> sized to the selected count cannot even address the last feature.</para>
    /// </summary>
    [TestFixture]
    public class SymbolSharedBufferTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private const uint Extent = 4096;
        private static readonly TileId Tile = new TileId { Z = 1, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        private const string FilterJson = @"[""!="", ""cls"", ""skip""]";

        /// <summary>
        /// E3 — the labels extracted from the SIX-feature source layer, with only four features selected, are
        /// the same sequence (count, <c>FeatureIndex</c>, text, anchor, placement, kind) as the labels
        /// extracted from the four selected features alone as their own, unfiltered layer.
        ///
        /// <para><b>Catches:</b> sizing the counting sort's <c>ringStart</c> to the <i>selected</i> count
        /// instead of the layer's feature count (an <c>IndexOutOfRangeException</c> here, because ordinal 5
        /// is addressed — and a silent mis-bucket wherever it is not); driving the feature loop over
        /// selected-list positions while <c>RingFeatureIdx</c> names ordinals (paths land on the wrong
        /// features); and dropping or re-ordering the per-tile <c>FeatureIndex</c> counter, which is the
        /// stable S20 placement tiebreak and therefore observable output.</para>
        /// </summary>
        [Test]
        public void SymbolSharedLayerBuffer_BucketsPathsByOrdinal_MatchingTheSelectedOnlyControl()
        {
            IReadOnlyList<IFeature> layerFeatures = SharedLayerFeatures();
            IReadOnlyList<SelectedTileFeature> selection = SelectFromLayer(layerFeatures);

            // ── Non-vacuity #1: strict, non-identity subset whose HIGHEST ordinal is out of range for any
            //    array sized to the selected count. That inequality is what makes the named off-by-N reachable.
            Assert.AreEqual(6, layerFeatures.Count, "precondition: the source layer carries six features");
            Assert.AreEqual(4, selection.Count,
                "precondition: the filter must admit a STRICT SUBSET of the layer");
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 5 }, Ordinals(selection),
                "precondition: the admitted ordinals must NOT be 0..n-1, or slot and ordinal coincide and " +
                "this fixture is blind to the re-base entirely");
            Assert.Greater(selection[selection.Count - 1].Ordinal, selection.Count - 1,
                "precondition: the highest selected ordinal must EXCEED the selected count — otherwise a " +
                "ringStart sized to the selected count still addresses every feature and the off-by-N the " +
                "plan names is unreachable here");

            // ── Non-vacuity #2: the rejected features really do carry paths, and an undrawable kind really
            //    is interleaved among the selected ones.
            Assert.AreEqual(TileGeometryType.Point, layerFeatures[1].GeometryType,
                "precondition: the unselected feature at ordinal 1 must be a drawable KIND carrying paths — " +
                "an unselected Polygon would be dropped by the kind gate anyway");
            Assert.AreEqual(TileGeometryType.Polygon, layerFeatures[2].GeometryType,
                "precondition: a Polygon must be SELECTED and interleaved, so the kind gate stays " +
                "independently load-bearing");

            List<SymbolStyle.SymbolLabel> shared  = Extract(layerFeatures, FilterJson);
            List<SymbolStyle.SymbolLabel> control = Extract(SelectedOnlyLayer(layerFeatures), null);

            // ── Non-vacuity #3: the control is a real, multi-feature, multi-path extraction. A one-label or
            //    single-text control could not tell a permuted attribution from a correct one.
            Assert.AreEqual(5, control.Count,
                "precondition: the control must emit 5 labels — 2 (multi-point 'a') + 1 (line-centre 'b') + " +
                "2 (multi-point 'c'); the selected Polygon emits none");
            Assert.AreEqual(3, DistinctTexts(control),
                "precondition: the control's labels must carry at least three DISTINCT texts, or a " +
                "mis-attributed path is indistinguishable from a correct one");

            Assert.AreEqual(control.Count, shared.Count,
                "the two unselected features' paths and the selected Polygon's rings must contribute EXACTLY " +
                "nothing — a differing label count means the selection gate or the kind gate is missing");

            for (int i = 0; i < control.Count; i++)
            {
                SymbolStyle.SymbolLabel e = control[i];
                SymbolStyle.SymbolLabel a = shared[i];
                Assert.AreEqual(e.Text, a.Text,
                    $"label {i} TEXT — a mismatch is a path bucketed onto the wrong feature");
                Assert.AreEqual(e.FeatureIndex, a.FeatureIndex,
                    $"label {i} FeatureIndex — the per-tile ordinal counter is the stable S20 placement " +
                    "tiebreak, so its sequence is observable output, not an implementation detail");
                Assert.AreEqual(e.Placement, a.Placement, $"label {i} placement");
                Assert.AreEqual(e.Kind, a.Kind, $"label {i} kind");
                Assert.AreEqual(e.AnchorRender.x, a.AnchorRender.x, $"label {i} anchor x");
                Assert.AreEqual(e.AnchorRender.y, a.AnchorRender.y, $"label {i} anchor y");
                Assert.AreEqual(e.AnchorRender.z, a.AnchorRender.z, $"label {i} anchor z");
            }

            // The FeatureIndex sequence is pinned absolutely as well as differentially: a control that had
            // itself drifted would make the comparison above agree on a wrong answer.
            var indices = new List<int>();
            foreach (SymbolStyle.SymbolLabel label in shared) indices.Add(label.FeatureIndex);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, indices,
                "FeatureIndex counts emitted labels 0..n-1 in emission order, per tile — never the source " +
                "layer's feature ordinal, and never restarted per feature");
        }

        // ── Fixture ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The source layer, in decode order.</summary>
        private static IReadOnlyList<IFeature> SharedLayerFeatures() => new List<IFeature>
        {
            /* 0 */ Feature("a",    TileGeometryType.Point,
                        MvtCommandStream.Ring( 600,  600), MvtCommandStream.Ring(1200,  900)),
            /* 1 */ Feature("skip", TileGeometryType.Point,
                        MvtCommandStream.Ring(1800, 1100), MvtCommandStream.Ring(1900, 1200),
                        MvtCommandStream.Ring(2000, 1300)),
            /* 2 */ Feature("a",    TileGeometryType.Polygon,
                        MvtCommandStream.Ring(2400, 1400, 3000, 1400, 3000, 2000, 2400, 2000)),
            /* 3 */ Feature("b",    TileGeometryType.LineString,
                        MvtCommandStream.Ring( 500, 2200, 1300, 2200, 2100, 2600)),
            /* 4 */ Feature("skip", TileGeometryType.Point, MvtCommandStream.Ring(3100, 2800)),
            /* 5 */ Feature("c",    TileGeometryType.Point,
                        MvtCommandStream.Ring( 800, 3200), MvtCommandStream.Ring(1500, 3500)),
        };

        /// <summary>The four selected features alone, as their own layer — the control arm, where ordinal
        /// == slot (the configuration every pre-existing symbol fixture runs in).</summary>
        private static IReadOnlyList<IFeature> SelectedOnlyLayer(IReadOnlyList<IFeature> layerFeatures)
            => new List<IFeature> { layerFeatures[0], layerFeatures[2], layerFeatures[3], layerFeatures[5] };

        private static List<SymbolStyle.SymbolLabel> Extract(IReadOnlyList<IFeature> features, string filterJson)
        {
            var tile = TestDecodedTiles.Of("probe", Tile, features, Extent);
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(
                StyleLayer(filterJson), tile, Tile, Zoom, new WebMercatorProjection(), labels);
            return labels;
        }

        /// <summary>Runs the REAL selection seam, so the ordinals under test are production's.</summary>
        private static IReadOnlyList<SelectedTileFeature> SelectFromLayer(IReadOnlyList<IFeature> features)
        {
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(
                StyleLayer(FilterJson), TestDecodedTiles.Of("probe", Tile, features, Extent).GetLayer("probe"),
                Zoom, selected);
            return selected;
        }

        private static SymbolStyle.StyleLayer StyleLayer(string filterJson) => new SymbolStyle.StyleLayer
        {
            Id          = "p2-symbol",
            LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
            SourceLayer = "probe",
            LayoutJson  = JsonParser.Parse(@"{""text-field"":""{cls}""}"),
            Filter      = filterJson != null ? JsonParser.Parse(filterJson) : null,
        };

        private static IFeature Feature(string cls, TileGeometryType kind, params IReadOnlyList<double2>[] rings)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["cls"] = Value.String(cls) },
                geometryType: kind,
                geometry: MvtCommandStream.Feature(rings));

        private static int[] Ordinals(IReadOnlyList<SelectedTileFeature> selection)
        {
            var ordinals = new int[selection.Count];
            for (int i = 0; i < selection.Count; i++) ordinals[i] = selection[i].Ordinal;
            return ordinals;
        }

        private static int DistinctTexts(IReadOnlyList<SymbolStyle.SymbolLabel> labels)
        {
            var seen = new HashSet<string>();
            foreach (SymbolStyle.SymbolLabel label in labels) seen.Add(label.Text);
            return seen.Count;
        }
    }
}
