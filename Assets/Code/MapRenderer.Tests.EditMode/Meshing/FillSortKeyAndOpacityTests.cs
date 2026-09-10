using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Expressions;
using Color = UnityEngine.Color;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// P3 + P4 — the two build-time fill behaviours that show up in the MESH rather than in a uniform:
    /// <c>fill-sort-key</c> ordering and data-driven <c>fill-opacity</c>.
    ///
    /// <para>Ordering is asserted on the INDEX buffer (draw order) and opacity on the COLOR stream, because
    /// both are per-feature and a uniform cannot express either. The features are synthetic (<see cref="DictionaryFeature"/>) so the sort keys and
    /// attribute values are exact and the expected ordering is unambiguous — a fixture tile's feature order
    /// is an accident of the encoder, which would make an ordering assertion untrustworthy.</para>
    /// </summary>
    [TestFixture]
    public class FillSortKeyAndOpacityTests
    {
        private const double Extent = 4096.0;

        private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

        /// <summary>One axis-aligned square, in MVT command form: MoveTo(1) + LineTo(3) + ClosePath with
        /// zig-zag deltas — the same encoding the decoder emits, so this drives the real geometry path
        /// rather than a bypass.</summary>
        private static uint[] Square(int x, int y, int size) => new[]
        {
            (1u << 3) | 1u, ZigZag(x),     ZigZag(y),     // MoveTo (x, y)
            (3u << 3) | 2u, ZigZag(size),  ZigZag(0),     // LineTo +x
                            ZigZag(0),     ZigZag(size),  // LineTo +y
                            ZigZag(-size), ZigZag(0),     // LineTo -x
            (1u << 3) | 7u,                               // ClosePath (ring closes implicitly)
        };

        private static DictionaryFeature Feature(int x, int y, string name, double sortKey, double opacity = 1.0)
            => new DictionaryFeature(
                new Dictionary<string, Value>
                {
                    ["name"] = Value.String(name),
                    ["sk"]   = Value.Number(sortKey),
                    ["op"]   = Value.Number(opacity),
                },
                TileGeometryType.Polygon,
                geometry: Square(x, y, 100));

        /// <summary>
        /// The per-feature PAINT order, read off the INDEX buffer: each triangle's colour in the order the
        /// GPU will rasterize them, deduped to one entry per contiguous run.
        ///
        /// <para>Deliberately not read off <c>mesh.colors</c>. Vertex order and index order happen to agree
        /// here (<c>FillMeshPipeline</c> concatenates each polygon's indices in ascending polygon order, and
        /// polygon order follows feature order), but the claim under test is about DRAW order — so the
        /// oracle has to be the buffer that determines it. A vertex-order oracle would keep passing if the
        /// pipeline ever grouped or reordered its index emission.</para>
        ///
        /// <para>Draw order is what decides the winner because these fills run <c>ZWrite Off</c> with
        /// <c>LEqual</c> (<c>BaseTweaker.ApplyBaseContract</c>): coincident coplanar triangles never
        /// depth-reject each other, so the last one rasterized is the one on top.</para>
        /// </summary>
        private static List<Color> PaintOrderColorRuns(Mesh mesh)
        {
            Color[] colors    = mesh.colors;
            int[]   triangles = mesh.triangles;
            var     runs      = new List<Color>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Color c = colors[triangles[t]];
                if (runs.Count == 0 || runs[runs.Count - 1] != c) runs.Add(c);
            }
            return runs;
        }

        // ── P3: fill-sort-key orders features within the layer ───────────────────────────────────

        [Test]
        public void SortKey_HigherKeyIsEmittedLast_SoItDrawsOnTop()
        {
            // Declared order is deliberately the REVERSE of the sort order, so passing requires an actual
            // sort — not merely preserving input order.
            var features = new List<IFeature>
            {
                Feature(0, 0, "top",    sortKey: 10.0),
                Feature(0, 0, "middle", sortKey: 5.0),
                Feature(0, 0, "bottom", sortKey: 1.0),
            };

            // Distinct colours per feature via a data-driven expression keyed on `name`, so the rasterization
            // order read off the index buffer identifies which feature paints when.
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""bottom"", ""#ff0000"", ""middle"", ""#00ff00"", ""top"", ""#0000ff"", ""#ffffff""]}"));
            var layout = Fill.LayoutProperties.Parse(JsonParser.Parse(@"{""fill-sort-key"": [""get"", ""sk""]}"));

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null, layout);
            try
            {
                Assert.IsNotNull(mesh, "the stub polygons must produce geometry");
                var runs = PaintOrderColorRuns(mesh);
                Assert.AreEqual(3, runs.Count, "expected one colour run per feature");

                // Ascending sort key ⇒ bottom (1) first, top (10) last. Later == painted on top.
                Assert.Greater(runs[0].r, 0.5f, "lowest sort key (red 'bottom') must be emitted FIRST");
                Assert.Greater(runs[1].g, 0.5f, "middle sort key (green) must be emitted second");
                Assert.Greater(runs[2].b, 0.5f,
                    "highest sort key (blue 'top') must be emitted LAST so it draws on top — if this is red, " +
                    "fill-sort-key is being ignored and declared order survived.");
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SortKey_EqualKeys_PreserveDeclaredOrder()
        {
            // Stability tooth: Array.Sort is an introsort and is NOT stable, so equal keys would otherwise
            // shuffle arbitrarily. The spec's implicit order for ties is the declared one.
            var features = new List<IFeature>
            {
                Feature(0, 0, "first",  sortKey: 4.0),
                Feature(0, 0, "second", sortKey: 4.0),
            };
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""second"", ""#00ff00"", ""#ffffff""]}"));
            var layout = Fill.LayoutProperties.Parse(JsonParser.Parse(@"{""fill-sort-key"": [""get"", ""sk""]}"));

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null, layout);
            try
            {
                var runs = PaintOrderColorRuns(mesh);
                Assert.AreEqual(2, runs.Count);
                Assert.Greater(runs[0].r, 0.5f, "equal sort keys must keep DECLARED order ('first' stays first)");
                Assert.Greater(runs[1].g, 0.5f);
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SortKey_Absent_LeavesDeclaredOrderAndMeshUntouched()
        {
            // The byte-identity guarantee: no sort key ⇒ no reorder, so every pre-existing fill mesh (and
            // every snapshot baked from one) is unaffected by P3.
            var features = new List<IFeature>
            {
                Feature(0, 0, "first",  sortKey: 99.0), // key present in the DATA but not referenced by the style
                Feature(0, 0, "second", sortKey: 1.0),
            };
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""second"", ""#00ff00"", ""#ffffff""]}"));

            Mesh withoutLayout = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 });
            Mesh withDefaultLayout = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null,
                Fill.LayoutProperties.Parse((JsonValue)null));
            try
            {
                var noLayout  = PaintOrderColorRuns(withoutLayout);
                var defLayout = PaintOrderColorRuns(withDefaultLayout);

                Assert.Greater(noLayout[0].r, 0.5f, "declared order must survive when no fill-sort-key is set");
                CollectionAssert.AreEqual(noLayout, defLayout,
                    "a layout object with no fill-sort-key must be indistinguishable from no layout at all");
                Assert.AreEqual(withoutLayout.vertexCount, withDefaultLayout.vertexCount);
            }
            finally
            {
                if (withoutLayout != null)     Object.DestroyImmediate(withoutLayout);
                if (withDefaultLayout != null) Object.DestroyImmediate(withDefaultLayout);
            }
        }

        /// <summary>
        /// IR B5 T5b — <c>fill-sort-key</c> reorders the GEOMETRY and the COLOURS <b>together</b>. Both come
        /// off the same loop over the sorted list, so a build that sorted only one of them would paint each
        /// feature's colour onto its neighbour's polygon.
        ///
        /// <para><b>Why this test exists</b> (RED-verify row D4): every other sort-key case in this file
        /// stacks its features at <c>(0, 0)</c> with an IDENTICAL square, because they assert draw ORDER
        /// under the painter's algorithm and coincident polygons are the point. That makes them structurally
        /// blind to a geometry/colour desync — permuting identical geometry changes nothing. The two cases
        /// with distinct positions declare no sort key at all, where <c>OrderBySortKey</c> returns the same
        /// instance and there is nothing to desync. So this is the only fixture in the repo where the join
        /// is observable: distinct positions AND a live sort key, asserting WHICH polygon each colour lands
        /// on rather than merely that all three colours appear.</para>
        /// </summary>
        [Test]
        public void SortKey_ReordersGeometryAndColoursTogether_SoEachColourKeepsItsOwnPolygon()
        {
            // Declared order is the REVERSE of sort order (so a sort really happens), and the three squares
            // sit at increasing tile-local x (so each colour is identifiable by WHERE it landed).
            var features = new List<IFeature>
            {
                Feature(1000, 1000, "far",  sortKey: 3.0),
                Feature(500,  500,  "mid",  sortKey: 2.0),
                Feature(0,    0,    "near", sortKey: 1.0),
            };
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""near"", ""#ff0000"", ""mid"", ""#00ff00"", ""far"", ""#0000ff"", ""#ffffff""]}"));
            var layout = Fill.LayoutProperties.Parse(JsonParser.Parse(@"{""fill-sort-key"": [""get"", ""sk""]}"));

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null, layout);
            try
            {
                Assert.IsNotNull(mesh, "the three squares must produce geometry");
                Vector3[] verts  = mesh.vertices;
                Color[]   colors = mesh.colors;
                Assert.AreEqual(verts.Length, colors.Length, "precondition: one colour per vertex");

                // Tile-local x grows eastward, and so does world x — so the three colour groups must appear
                // in the same left-to-right order as the squares were authored, whatever the sort did.
                double nearX = MeanXOfColor(verts, colors, c => c.r > 0.5f && c.g < 0.5f && c.b < 0.5f);
                double midX  = MeanXOfColor(verts, colors, c => c.g > 0.5f && c.r < 0.5f && c.b < 0.5f);
                double farX  = MeanXOfColor(verts, colors, c => c.b > 0.5f && c.r < 0.5f && c.g < 0.5f);

                // Non-vacuity: all three colours must actually be present, or the ordering below is vacuous.
                Assert.IsFalse(double.IsNaN(nearX), "the RED ('near') polygon must be in the mesh");
                Assert.IsFalse(double.IsNaN(midX),  "the GREEN ('mid') polygon must be in the mesh");
                Assert.IsFalse(double.IsNaN(farX),  "the BLUE ('far') polygon must be in the mesh");

                Assert.Less(nearX, midX,
                    "the RED colour must land on the square authored at tile x=0 — if geometry and colours " +
                    "were sorted independently, red would sit on the FARTHEST square instead");
                Assert.Less(midX, farX,
                    "…and GREEN on the middle square, BLUE on the farthest: this pins WHICH polygon each " +
                    "colour landed on, not just that all three colours exist");
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        /// <summary>Mean world x of the vertices whose colour matches <paramref name="match"/>, or
        /// <c>NaN</c> when that colour is absent (which the caller asserts against).</summary>
        private static double MeanXOfColor(Vector3[] verts, Color[] colors, System.Func<Color, bool> match)
        {
            double sum = 0.0;
            int    n   = 0;
            for (int i = 0; i < verts.Length; i++)
                if (match(colors[i])) { sum += verts[i].x; n++; }
            return n == 0 ? double.NaN : sum / n;
        }

        /// <summary>
        /// IR B5 T5 — the fill ordinal join survives a geometry-less feature. Before B5 the builder skipped a
        /// Polygon whose command stream was null, so such a feature took no position in the list handed to
        /// the materializer; now it occupies an ordinal (there is no interface member left to test it by, and
        /// the materializer treats a null stream as zero commands). It contributes no ring, so
        /// <c>VertexFeatureIdx</c> never names it — but the per-feature colour list is built in the SAME loop
        /// and must stay index-aligned, or every feature after the gap paints its neighbour's colour.
        /// </summary>
        [Test]
        public void NullGeometryPolygon_TakesAnOrdinal_WithoutShiftingItsNeighboursColours()
        {
            var features = new List<IFeature>
            {
                Feature(0, 0, "first", sortKey: 0.0),
                new DictionaryFeature(
                    new Dictionary<string, Value> { ["name"] = Value.String("gap") },
                    TileGeometryType.Polygon,
                    geometry: null),                     // a Polygon with NO geometry, between the two drawn ones
                Feature(500, 500, "second", sortKey: 0.0),
            };
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""gap"", ""#00ff00"", ""second"", ""#0000ff"", ""#ffffff""]}"));

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 });
            try
            {
                Assert.IsNotNull(mesh, "the two real polygons must still produce geometry");
                var runs = PaintOrderColorRuns(mesh);

                Assert.AreEqual(2, runs.Count,
                    "exactly two colour runs — the geometry-less feature contributes no triangles");
                Assert.Greater(runs[0].r, 0.5f,
                    "the first polygon must still paint RED; green here means the gap feature's colour " +
                    "slid onto its neighbour (the colour list desynced from the feature ordinals)");
                Assert.Greater(runs[1].b, 0.5f,
                    "the polygon AFTER the gap must still paint BLUE — an off-by-one would paint it green");
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        // ── P4: data-driven fill-opacity bakes into the COLOR stream's alpha ─────────────────────

        [Test]
        public void DataDrivenOpacity_BakesDistinctPerFeatureAlpha()
        {
            var features = new List<IFeature>
            {
                Feature(0,   0, "a", sortKey: 0.0, opacity: 0.25),
                Feature(200, 0, "b", sortKey: 0.0, opacity: 0.75),
            };
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(
                @"{""fill-color"": ""#ffffff"", ""fill-opacity"": [""get"", ""op""]}"));

            Assert.IsTrue(paint.Opacity.DependsOnFeature, "precondition: this expression must be data-driven");

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 });
            try
            {
                var alphas = new HashSet<float>();
                foreach (Color c in mesh.colors) alphas.Add(Mathf.Round(c.a * 100f) / 100f);

                // Before P4 a data-driven fill-opacity silently reverted to the default: every vertex would
                // carry alpha 1 and this set would be {1.00}.
                CollectionAssert.AreEquivalent(new[] { 0.25f, 0.75f }, alphas,
                    "each feature's evaluated fill-opacity must be baked into its vertices' alpha. " +
                    "A single value of 1.00 means the data-driven opacity was dropped.");
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ConstantOpacity_IsNotBaked_SoTheUniformStaysTheSingleSource()
        {
            // The double-apply guard's other half: a constant/zoom opacity rides the _Opacity uniform, so it
            // must NOT also appear in vertex alpha or the shader would multiply it in twice.
            var features = new List<IFeature> { Feature(0, 0, "a", sortKey: 0.0) };
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(
                @"{""fill-color"": ""#ffffff"", ""fill-opacity"": 0.5}"));

            Assert.IsFalse(paint.Opacity.DependsOnFeature, "precondition: constant opacity");

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 });
            try
            {
                foreach (Color c in mesh.colors)
                    Assert.AreEqual(1f, c.a, 1e-4,
                        "a CONSTANT fill-opacity must stay on the _Opacity uniform and leave vertex alpha at " +
                        "fill-color's own alpha — baking it too would double-apply it in the fragment.");
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void DataDrivenOpacity_MultipliesWithFillColorAlpha()
        {
            // Both dimensions are real: fill-color may carry its own alpha, and fill-opacity scales it.
            var features = new List<IFeature> { Feature(0, 0, "a", sortKey: 0.0, opacity: 0.5) };
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(
                @"{""fill-color"": ""rgba(255,255,255,0.4)"", ""fill-opacity"": [""get"", ""op""]}"));

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 });
            try
            {
                foreach (Color c in mesh.colors)
                    Assert.AreEqual(0.2f, c.a, 1e-3,
                        "fill-color alpha (0.4) × data-driven fill-opacity (0.5) = 0.2");
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }
    }
}
