// GlobeFillTangentTests (S91-C / C-2) — the fill Tangent stream must carry the projection's per-vertex SURFACE
// EAST, not a constant +X. Unity's Lit shader builds the TBN frame from Tangent for normal/bump maps and
// fill-pattern texturing; on the curved globe a constant tangent lights every fill wrong. Verified: globe
// tangents vary, are unit-length, are perpendicular to the geodetic normal, carry w=+1; Mercator stays the
// constant (1,0,0,1) (byte-identical to the old FlatTangent).

using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests
{
    public class GlobeFillTangentTests
    {
        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""Test"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        private static (IReadOnlyList<MvtFeature> features, Fill.PaintProperties paint, double extent) Setup()
        {
            var mvtTile   = MvtDecoder.Decode(FixtureBytes());
            var fillLayer = MinimalStyle().Layers[0];
            var paint     = new Fill.PaintProperties(fillLayer);
            var features  = FeatureSelector.SelectFeatures(fillLayer, mvtTile, 0.0);
            var mvtLayer  = SourceLayerResolver.ResolveMvtLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "fixture must have the 'countries' layer");
            Assert.Greater(features.Count, 0);
            return (features, paint, mvtLayer.Extent);
        }

        [Test]
        public void GlobeFill_Tangent_IsPerVertexEast_OrthonormalToNormal()
        {
            var (features, paint, extent) = Setup();
            var id = new TileId { Z = 0, X = 0, Y = 0 };

            Mesh mesh = TestTileMeshBuilder.BuildFill(features, paint, 0.0, extent, id, new SphericalProjection());
            Assert.IsNotNull(mesh, "globe fill must produce geometry");
            Vector4[] tan = mesh.tangents;
            Vector3[] nrm = mesh.normals;
            Assert.Greater(tan.Length, 0);
            Assert.AreEqual(nrm.Length, tan.Length);

            var first = new float3(tan[0].x, tan[0].y, tan[0].z);
            bool varies = false;
            int stride = math.max(1, tan.Length / 3000); // sample — the subdivided globe mesh can be large
            for (int i = 0; i < tan.Length; i += stride)
            {
                var t = new float3(tan[i].x, tan[i].y, tan[i].z);
                var n = new float3(nrm[i].x, nrm[i].y, nrm[i].z);
                Assert.AreEqual(1f, math.length(t), 1e-3f, $"tangent {i} must be unit-length");
                Assert.AreEqual(0f, math.dot(t, n),  2e-3f, $"tangent {i} must be ⊥ the surface normal");
                Assert.AreEqual(1f, tan[i].w, "bitangent sign must be +1");
                if (math.length(t - first) > 1e-2f) varies = true;
            }
            // The globe's east rotates over the sphere — a constant +X (the bug) would make this false.
            Assert.IsTrue(varies, "globe fill tangents must VARY per vertex (not a constant tangent)");
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void GlobeFill_IsSubdivided_ForCurvature_MercatorIsNot()
        {
            var (features, paint, extent) = Setup();
            var id = new TileId { Z = 0, X = 0, Y = 0 }; // z0 tile spans the globe → heavy chording without C-3

            Mesh flat  = TestTileMeshBuilder.BuildFill(features, paint, 0.0, extent, id, null);
            Mesh globe = TestTileMeshBuilder.BuildFill(features, paint, 0.0, extent, id, new SphericalProjection());
            Assert.IsNotNull(flat); Assert.IsNotNull(globe);

            // C-3 refines flat earcut triangles onto the sphere → strictly more vertices than the flat build,
            // while Mercator stays exactly the un-subdivided earcut output.
            Assert.Greater(globe.vertexCount, flat.vertexCount * 2,
                "globe fill must subdivide for curvature (many more verts than the flat Mercator build)");
            Object.DestroyImmediate(flat);
            Object.DestroyImmediate(globe);
        }

        [Test]
        public void MercatorFill_Tangent_StaysConstantPlusX()
        {
            var (features, paint, extent) = Setup();
            var id = new TileId { Z = 0, X = 0, Y = 0 };

            Mesh mesh = TestTileMeshBuilder.BuildFill(features, paint, 0.0, extent, id, null); // null ⇒ WebMercator
            Assert.IsNotNull(mesh);
            Vector4[] tan = mesh.tangents;
            Assert.Greater(tan.Length, 0);
            foreach (var t in tan)
                Assert.AreEqual(new Vector4(1f, 0f, 0f, 1f), t, "Mercator fill tangent must stay constant +X (byte-identical)");
            Object.DestroyImmediate(mesh);
        }
    }
}
