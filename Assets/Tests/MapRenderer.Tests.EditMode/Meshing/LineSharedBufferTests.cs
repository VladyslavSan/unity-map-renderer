// Unity EditMode only — Mesh/MeshData + the Burst line ribbon job. NOT registered in Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests.Jobs;
using Color = UnityEngine.Color;                  // aliased: MapRenderer.Core.Expressions has its own Color
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// IR C1 P2 — teeth <b>E1</b> and <b>E2</b>: line reads a buffer it <b>shares</b> with the whole source
    /// layer, and joins its per-feature colour/width columns by the source-layer <b>ordinal</b> rather than by
    /// its own selected-list position.
    ///
    /// <para><b>Written BEFORE the conversion.</b> Both tests drive the production line path through
    /// <see cref="TestTileMeshBuilder.BuildLineFromLayer"/>, whose signature does not change when
    /// <c>StyledLineTileBuilder.WriteMeshData</c> moves onto the borrowed buffer — so the reference these
    /// teeth measure is the PRE-conversion output, and a post-conversion pass is byte-identity, not
    /// ratification.</para>
    ///
    /// <para><b>The production configuration is the one under test</b> (the standing check). Every existing
    /// line instrument in the repo — <c>StyledLineBufferParityTests</c>, <c>LineRibbonJobTests</c>, every line
    /// pixel suite — hands the builder a feature list that <i>is</i> the whole layer, so slot and ordinal
    /// coincide and the re-base this phase performs is inert. This fixture is the only one where they differ:
    /// the style layer's filter admits a <b>strict subset</b> (ordinals <c>[0, 3, 4, 5]</c> of six), the
    /// unselected features <b>have rings</b> — including one that is a <c>LineString</c>, so a missing
    /// selection gate produces line geometry rather than nothing — and a <b>Polygon is selected</b> and
    /// interleaved among the selected LineStrings, so the kind gate stays load-bearing and independently
    /// falsifiable.</para>
    /// </summary>
    [TestFixture]
    public class LineSharedBufferTests
    {

        /// <summary>IR C1 P3: the synthetic layers built above own <c>Allocator.Persistent</c> buffers now.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        // Data-driven on `cls`, so the colour and width columns are DISCRIMINATING: a column read at the
        // wrong index lands a visibly different value, which a constant paint could never show.
        private const string PaintJson =
            @"{""line-color"": [""match"", [""get"", ""cls""],
                 ""a"", ""#ff0000"", ""b"", ""#00ff00"", ""c"", ""#0000ff"", ""#ffffff""],
               ""line-width"": [""match"", [""get"", ""cls""],
                 ""a"", 2, ""b"", 4, ""c"", 6, 1]}";

        // Excludes cls == "skip". Two of the six layer features carry it, so the selection is a STRICT
        // subset and its ordinals are NOT 0..n-1 — the discriminator the whole fixture rests on.
        private const string FilterJson = @"[""!="", ""cls"", ""skip""]";

        // ── E1 — per-feature attribution over a shared layer buffer ────────────────────────────────

        /// <summary>
        /// E1 — the mesh built from the SIX-feature source layer, with only four features selected, is
        /// element-wise identical (positions, colours, width scales, indices) to the mesh built from the three
        /// drawable features alone with an identity selection.
        ///
        /// <para><b>Catches:</b> indexing <c>featColors</c>/<c>featWidths</c> by the selected-list slot (or by
        /// ring-order position) instead of by the layer ordinal — the exact re-base P2 performs. Under the
        /// slot form, ordinal 3's colour is read out of slot 3 (which holds feature <c>c</c>'s value) and
        /// ordinal 5 reads past the selected features entirely; the geometry stays right and only the colours
        /// and widths permute, which no vertex-count assertion can see.</para>
        ///
        /// <para>Also catches a missing selection gate (the unselected <c>LineString</c> at ordinal 2 would
        /// add a ribbon) and a missing kind gate (the selected <c>Polygon</c> at ordinal 4 would add two).</para>
        /// </summary>
        [Test]
        public void LineSharedLayerBuffer_AttributesColourAndWidthByOrdinal_NotBySelectedSlot()
        {
            IReadOnlyList<IFeature> layerFeatures = SharedLayerFeatures();
            IReadOnlyList<SelectedTileFeature> selection = SelectFromLayer(layerFeatures);

            // ── Non-vacuity #1: the selection really is a strict, NON-IDENTITY subset. Without this the
            //    slot-vs-ordinal mixup is inert and every equality below is true for the wrong reason.
            Assert.AreEqual(6, layerFeatures.Count, "precondition: the source layer carries six features");
            Assert.AreEqual(4, selection.Count,
                "precondition: the style layer's filter must admit a STRICT SUBSET of the layer");
            CollectionAssert.AreEqual(new[] { 0, 3, 4, 5 }, Ordinals(selection),
                "precondition: the admitted ordinals must NOT be 0..n-1 — an identity selection makes slot " +
                "and ordinal coincide and this whole fixture blind");

            // ── Non-vacuity #2: the features the filter rejected carry real rings, one of them a LineString,
            //    so a missing selection gate is observable as EXTRA line geometry rather than as nothing.
            Assert.AreEqual(TileGeometryType.LineString, layerFeatures[2].GeometryType,
                "precondition: the unselected feature at ordinal 2 must be a LineString — an unselected " +
                "POLYGON would be filtered out by the kind gate anyway, leaving the selection gate unobserved");
            Assert.AreEqual(TileGeometryType.Polygon, layerFeatures[4].GeometryType,
                "precondition: a POLYGON must be among the SELECTED features, interleaved between selected " +
                "LineStrings — that is what keeps the kind gate load-bearing here");

            Mesh control = null, shared = null;
            try
            {
                control = BuildControl();
                shared  = BuildShared(layerFeatures, selection);

                Assert.IsNotNull(control, "non-vacuity: the drawable-features-only control must build a mesh");
                Assert.IsNotNull(shared,  "the shared-layer build must produce the same mesh, not nothing");
                Assert.Greater(control.vertexCount, 0, "non-vacuity: the control carries vertices");

                Vector3[] controlPos = control.vertices;
                Vector3[] sharedPos  = shared.vertices;
                Color[]   controlCol = control.colors;
                Color[]   sharedCol  = shared.colors;
                var controlWidth = new List<Vector4>(); control.GetUVs(2, controlWidth); // TexCoord2.x = widthScale
                var sharedWidth  = new List<Vector4>(); shared.GetUVs(2, sharedWidth);

                // ── Non-vacuity #3: the columns under test really do vary across the fixture. A single
                //    colour (or a single width) would make a permuted read indistinguishable from a correct one.
                Assert.GreaterOrEqual(DistinctCount(controlCol), 3,
                    "precondition: the control must carry at least three DISTINCT colours — a constant " +
                    "colour column cannot detect a permuted read");
                Assert.GreaterOrEqual(DistinctWidths(controlWidth), 3,
                    "precondition: the control must carry at least three DISTINCT width scales, for the " +
                    "same reason");

                Assert.AreEqual(control.vertexCount, shared.vertexCount,
                    "the four unselected/undrawable rings in the shared layer must contribute EXACTLY " +
                    "nothing — a differing count means the selection gate or the kind gate is missing");
                CollectionAssert.AreEqual(control.triangles, shared.triangles,
                    "…including the index buffer, element for element: ring emission order is draw order");

                for (int i = 0; i < controlPos.Length; i++)
                {
                    Assert.AreEqual(controlPos[i], sharedPos[i], $"vertex {i} position");
                    Assert.AreEqual(controlCol[i], sharedCol[i],
                        $"vertex {i} COLOUR — a mismatch here is the slot-vs-ordinal mis-join: the geometry " +
                        "is right and the per-feature column landed on the wrong feature");
                    Assert.AreEqual(controlWidth[i].x, sharedWidth[i].x,
                        $"vertex {i} WIDTH SCALE — the second per-feature column, joined through the same index");
                }
            }
            finally
            {
                if (control != null) UnityEngine.Object.DestroyImmediate(control);
                if (shared  != null) UnityEngine.Object.DestroyImmediate(shared);
            }
        }

        // ── E2 — ring emission order ───────────────────────────────────────────────────────────────

        /// <summary>
        /// E2 — the rings the shared build emits are, in order, exactly the selected LineString features'
        /// rings in <b>decode order</b>: feature <c>a</c>'s ring, then <c>b</c>'s, then <c>c</c>'s two, and
        /// nothing else.
        ///
        /// <para>The plan's ordering argument — <c>FeatureSelector</c> appends in <c>Features</c> order, so
        /// selected order IS decode order, so iterating the layer buffer with a selection gate reproduces it —
        /// <b>is an argument, not a measurement.</b> This is the measurement, and it pins WHICH ring is where
        /// rather than only how many there are: every ring in the fixture is authored HORIZONTAL, at a
        /// distinct tile <c>y</c>, so each ring collapses to exactly one render-space <c>z</c> and the
        /// vertex stream's <c>z</c>-run sequence names the rings in emission order. The expected sequence is
        /// built from three <b>independent single-feature builds</b>, not from the shared build itself.</para>
        ///
        /// <para><b>Catches:</b> iterating the layer's rings without the selection gate (the unselected
        /// LineString at ordinal 2 is authored at <c>y = 1000</c>, i.e. BETWEEN two selected rings, so it
        /// inserts a fifth run in the middle rather than at an end); a per-feature gather that visits the
        /// selected list in a different order; a polygon ring reaching the ribbon (its rings are not
        /// horizontal, so they shatter the run structure).</para>
        /// </summary>
        [Test]
        public void LineRingOrder_OverTheSharedBuffer_IsDecodeOrderOfTheSelectedLineRings()
        {
            IReadOnlyList<IFeature> layerFeatures = SharedLayerFeatures();
            IReadOnlyList<SelectedTileFeature> selection = SelectFromLayer(layerFeatures);

            Mesh shared = null, aOnly = null, bOnly = null, cOnly = null;
            try
            {
                shared = BuildShared(layerFeatures, selection);
                aOnly  = BuildAlone(layerFeatures[0]);
                bOnly  = BuildAlone(layerFeatures[3]);
                cOnly  = BuildAlone(layerFeatures[5]);

                Assert.IsNotNull(shared, "the shared-layer build must produce a mesh");

                List<float> expected = new List<float>();
                expected.AddRange(RingZSequence(aOnly));
                expected.AddRange(RingZSequence(bOnly));
                expected.AddRange(RingZSequence(cOnly));

                // ── Non-vacuity: the oracle really does name four distinct rings, and the ORDER of the
                //    expected sequence is information (a constant sequence would pin nothing).
                Assert.AreEqual(4, expected.Count,
                    "precondition: the three drawable features contribute 1 + 1 + 2 = 4 rings");
                CollectionAssert.AllItemsAreUnique(expected,
                    "precondition: each ring must occupy its OWN render-space z, or the run sequence cannot " +
                    "distinguish a permutation from the correct order");

                List<float> actual = RingZSequence(shared);

                Assert.AreEqual(4, actual.Count,
                    "the shared build must emit exactly four rings. A fifth means the unselected LineString " +
                    "at ordinal 2 (authored at y = 1000, between two selected rings) reached the ribbon — " +
                    "i.e. the selection gate is missing; more than five means a polygon ring did.");
                CollectionAssert.AreEqual(expected, actual,
                    "the emitted rings must appear in DECODE order — feature a, feature b, then feature c's " +
                    "two rings in their own decode order. The expected sequence comes from three independent " +
                    "single-feature builds, so this pins WHICH ring is where, not merely how many.");
            }
            finally
            {
                if (shared != null) UnityEngine.Object.DestroyImmediate(shared);
                if (aOnly  != null) UnityEngine.Object.DestroyImmediate(aOnly);
                if (bOnly  != null) UnityEngine.Object.DestroyImmediate(bOnly);
                if (cOnly  != null) UnityEngine.Object.DestroyImmediate(cOnly);
            }
        }

        // ── Fixture ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The source layer, in decode order. Every LineString ring is HORIZONTAL and at its own tile
        /// <c>y</c> — E2's run oracle depends on it; the polygons are deliberately NOT horizontal.
        /// </summary>
        private static IReadOnlyList<IFeature> SharedLayerFeatures() => new List<IFeature>
        {
            /* 0 */ LineFeature("a",    MvtCommandStream.Ring( 400,  500, 1200,  500, 2000,  500)),
            /* 1 */ PolygonFeature("skip", MvtCommandStream.Ring( 600,  900, 1400,  900, 1400, 1700,  600, 1700)),
            /* 2 */ LineFeature("skip", MvtCommandStream.Ring( 500, 1000, 1500, 1000)),
            /* 3 */ LineFeature("b",    MvtCommandStream.Ring( 300, 1500, 1100, 1500, 2100, 1500)),
            /* 4 */ PolygonFeature("a", MvtCommandStream.Ring(2500, 1800, 3300, 1800, 3300, 2600, 2500, 2600)),
            /* 5 */ LineFeature("c",    MvtCommandStream.Ring( 400, 2000, 1600, 2000),
                                        MvtCommandStream.Ring( 700, 2500, 2400, 2500)),
        };

        /// <summary>The three drawable features alone, as their OWN layer with an identity selection — the
        /// control arm. Ordinal == slot here, which is precisely the configuration every pre-existing line
        /// instrument runs in.</summary>
        private static Mesh BuildControl()
        {
            IReadOnlyList<IFeature> layer = SharedLayerFeatures();
            var control = new List<IFeature> { layer[0], layer[3], layer[5] };
            return TestTileMeshBuilder.BuildLineFromLayer(
                control, TestTileMeshBuilder.Selection(control), Paint(), Layout(), Zoom, Extent, Tile,
                double2.zero);
        }

        private static Mesh BuildShared(
            IReadOnlyList<IFeature> layerFeatures, IReadOnlyList<SelectedTileFeature> selection)
            => TestTileMeshBuilder.BuildLineFromLayer(
                layerFeatures, selection, Paint(), Layout(), Zoom, Extent, Tile, double2.zero);

        private static Mesh BuildAlone(IFeature feature)
        {
            var one = new List<IFeature> { feature };
            return TestTileMeshBuilder.BuildLineFromLayer(
                one, TestTileMeshBuilder.Selection(one), Paint(), Layout(), Zoom, Extent, Tile, double2.zero);
        }

        /// <summary>Runs the REAL selection seam, so the ordinals under test are the ones production
        /// computes rather than hand-written constants.</summary>
        private static IReadOnlyList<SelectedTileFeature> SelectFromLayer(IReadOnlyList<IFeature> features)
        {
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(
                StyleLayerWithFilter(), TestDecodedTiles.Of("p2", Tile, features, (uint)Extent).GetLayer("p2"),
                Zoom, selected);
            return selected;
        }

        private static Line.StyleLayer StyleLayerWithFilter() => new Line.StyleLayer
        {
            Id          = "p2-line",
            LayerType   = StyleLayerType.Line,
            SourceLayer = "p2",
            Paint       = TestStyle.LinePaint(PaintJson),
            Layout      = TestStyle.LineLayout(),
            Filter      = JsonParser.Parse(FilterJson),
        };

        private static Line.PaintProperties  Paint()  => StyleLayerWithFilter().Paint;
        private static Line.LayoutProperties Layout() => StyleLayerWithFilter().Layout;

        private static IFeature LineFeature(string cls, params IReadOnlyList<double2>[] rings)
            => Feature(cls, TileGeometryType.LineString, rings);

        private static IFeature PolygonFeature(string cls, params IReadOnlyList<double2>[] rings)
            => Feature(cls, TileGeometryType.Polygon, rings);

        private static IFeature Feature(string cls, TileGeometryType kind, IReadOnlyList<double2>[] rings)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["cls"] = Value.String(cls) },
                geometryType: kind,
                geometry: MvtCommandStream.Feature(rings));

        // ── Readers ────────────────────────────────────────────────────────────────────────────────

        private static int[] Ordinals(IReadOnlyList<SelectedTileFeature> selection)
        {
            var ordinals = new int[selection.Count];
            for (int i = 0; i < selection.Count; i++) ordinals[i] = selection[i].Ordinal;
            return ordinals;
        }

        /// <summary>The vertex stream's render-space <c>z</c>, collapsed to one entry per consecutive run.
        /// Every fixture ring is horizontal in tile space, so one run == one emitted ring, in emission
        /// order.</summary>
        private static List<float> RingZSequence(Mesh mesh)
        {
            var runs = new List<float>();
            if (mesh == null) return runs;
            Vector3[] positions = mesh.vertices;
            for (int i = 0; i < positions.Length; i++)
                if (i == 0 || positions[i].z != positions[i - 1].z)
                    runs.Add(positions[i].z);
            return runs;
        }

        private static int DistinctCount(Color[] colors)
        {
            var seen = new HashSet<Color>();
            foreach (Color c in colors) seen.Add(c);
            return seen.Count;
        }

        private static int DistinctWidths(List<Vector4> widths)
        {
            var seen = new HashSet<float>();
            foreach (Vector4 w in widths) seen.Add(w.x);
            return seen.Count;
        }
    }
}
