// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Mvt;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S105 Slice 2 (A3): <see cref="SymbolStyle.SymbolFeatureExtractor.Extract"/> over the committed fixture's
    /// <c>centroids</c> layer (250 Point features with <c>NAME</c>/<c>ABBREV</c>) yields the right count,
    /// the right resolved text for the first feature (<c>"Aruba"</c>), an anchor that is the REAL
    /// tile→geo→project chain (not a stub), and honours the layer filter. Engine-free; both runners.
    /// </summary>
    [TestFixture]
    public class SymbolFeatureExtractorTests
    {
        // Walk-up fixture loader (works in Unity batch mode AND dotnet test) — mirrors MvtPropertyDecodeTests.
        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-tile.bytes not found walking up from cwd/AppContext.");
        }

        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static SymbolStyle.StyleLayer CentroidsLayer(string textField = "{NAME}", string filterJson = null)
            => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                LayoutJson = JsonParser.Parse("{\"text-field\":\"" + textField + "\"}"),
                Filter = filterJson != null ? JsonParser.Parse(filterJson) : null,
            };

        [Test]
        public void Extract_CentroidsLayer_Yields250LabelsWithRealAnchors()
        {
            MvtTile tile = MvtDecoder.Decode(LoadFixture());
            var projection = new WebMercatorProjection();

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(CentroidsLayer(), tile, FixtureTile, 0.0, projection, labels);

            // The fixture has 250 centroids features; one label per point feature with a NON-EMPTY NAME.
            // Two features have an absent/empty NAME → their "{NAME}" resolves empty → skipped (the A2 skip
            // rule proven on real data), so 248 labels. Compute the expectation independently, then pin it.
            MvtLayer centroids = tile.GetLayer("centroids");
            Assert.AreEqual(250, centroids.Features.Count, "fixture pin: 250 centroids features");
            int pointFeaturesWithName = 0;
            foreach (MvtFeature feat in centroids.Features)
            {
                if (feat.GeometryType != MvtGeometryType.Point) continue;
                if (feat.Properties.TryGetValue("NAME", out MapRenderer.Core.Expressions.Value name)
                    && !string.IsNullOrWhiteSpace(name.ToDisplayString()))
                    pointFeaturesWithName++;
            }
            Assert.AreEqual(pointFeaturesWithName, labels.Count,
                "one label per point feature with a resolvable NAME (empty/absent NAME is skipped, not blank)");
            Assert.AreEqual(248, labels.Count, "fixture pin: 248 of 250 centroids resolve a non-empty NAME");

            // First feature resolves to "Aruba".
            Assert.AreEqual("Aruba", labels[0].Text, "feature[0]'s NAME is Aruba");

            // Its anchor is the REAL tile→geo→project chain, not a stubbed origin. Recompute independently
            // from the decoded first point and assert equality; also pin the fixture point (1252,1904).
            List<List<double2>> paths = MvtGeometry.Decode(centroids.Features[0].Geometry);
            double2 firstPoint = paths[0][0];
            Assert.AreEqual(1252.0, firstPoint.x, 1e-6, "fixture pin: feature[0].point.x");
            Assert.AreEqual(1904.0, firstPoint.y, 1e-6, "fixture pin: feature[0].point.y");

            double2 lonLat = FixtureTile.ToLonLat(firstPoint.x, firstPoint.y, centroids.Extent);
            double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            Assert.AreEqual(expected.x, labels[0].AnchorRender.x, 1e-6, "anchor.x must be the real projection (not a stub)");
            Assert.AreEqual(expected.y, labels[0].AnchorRender.y, 1e-6);
            Assert.AreEqual(expected.z, labels[0].AnchorRender.z, 1e-6);

            // Stable per-tile ordinal + spec padding default carried through.
            Assert.AreEqual(0, labels[0].FeatureIndex);
            Assert.AreEqual(2f, labels[0].PaddingPx, 1e-6, "text-padding spec default is 2");
        }

        [Test]
        public void Extract_WithFilter_NarrowsToNamedFeature()
        {
            MvtTile tile = MvtDecoder.Decode(LoadFixture());
            var projection = new WebMercatorProjection();

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(
                CentroidsLayer(filterJson: "[\"==\",[\"get\",\"ABBREV\"],\"Afg.\"]"),
                tile, FixtureTile, 0.0, projection, labels);

            Assert.AreEqual(1, labels.Count, "the ABBREV=='Afg.' filter selects exactly one feature");
            Assert.AreEqual("Afghanistan", labels[0].Text);
        }
    }
}
