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
            var features = new List<ITileFeature>
            {
                Feature(0, 0, "top",    sortKey: 10.0),
                Feature(0, 0, "middle", sortKey: 5.0),
                Feature(0, 0, "bottom", sortKey: 1.0),
            };

            // Distinct colours per feature via a data-driven expression keyed on `name`, so the rasterization
            // order read off the index buffer identifies which feature paints when.
            var paint = new Fill.PaintProperties(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""bottom"", ""#ff0000"", ""middle"", ""#00ff00"", ""top"", ""#0000ff"", ""#ffffff""]}"));
            var layout = new Fill.LayoutProperties(JsonParser.Parse(@"{""fill-sort-key"": [""get"", ""sk""]}"));

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
            var features = new List<ITileFeature>
            {
                Feature(0, 0, "first",  sortKey: 4.0),
                Feature(0, 0, "second", sortKey: 4.0),
            };
            var paint = new Fill.PaintProperties(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""second"", ""#00ff00"", ""#ffffff""]}"));
            var layout = new Fill.LayoutProperties(JsonParser.Parse(@"{""fill-sort-key"": [""get"", ""sk""]}"));

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
            var features = new List<ITileFeature>
            {
                Feature(0, 0, "first",  sortKey: 99.0), // key present in the DATA but not referenced by the style
                Feature(0, 0, "second", sortKey: 1.0),
            };
            var paint = new Fill.PaintProperties(JsonParser.Parse(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""second"", ""#00ff00"", ""#ffffff""]}"));

            Mesh withoutLayout = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 });
            Mesh withDefaultLayout = TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null,
                new Fill.LayoutProperties((JsonValue)null));
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

        // ── P4: data-driven fill-opacity bakes into the COLOR stream's alpha ─────────────────────

        [Test]
        public void DataDrivenOpacity_BakesDistinctPerFeatureAlpha()
        {
            var features = new List<ITileFeature>
            {
                Feature(0,   0, "a", sortKey: 0.0, opacity: 0.25),
                Feature(200, 0, "b", sortKey: 0.0, opacity: 0.75),
            };
            var paint = new Fill.PaintProperties(JsonParser.Parse(
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
            var features = new List<ITileFeature> { Feature(0, 0, "a", sortKey: 0.0) };
            var paint = new Fill.PaintProperties(JsonParser.Parse(
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
            var features = new List<ITileFeature> { Feature(0, 0, "a", sortKey: 0.0, opacity: 0.5) };
            var paint = new Fill.PaintProperties(JsonParser.Parse(
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
