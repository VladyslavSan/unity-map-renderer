// GlobeFillWindingTests — the fill geometry must wind the SAME way relative to its surface normal on the
// globe as it does on the (user-confirmed-correct) flat Mercator build, so that a single Cull mode culls the
// back hemisphere without hiding the near one. The globe render mapping (ECEF axis-swap) is a reflection, and
// the winding-vs-normal orientation is easy to get backwards by hand — so this test does NOT assert an absolute
// sign. It measures Mercator's dominant sign(dot(cross(edges), normal)) — the gold reference — and requires the
// globe's dominant sign to EQUAL it. Handedness-convention-free by construction.
//
// Both builds run the real production mesh build: Mercator through TileMeshPipeline's flat path,
// the globe through the reverseWinding branch + GlobeFillSubdivideJob (a distinct emission path). A near-uniform
// dominant sign on each side also proves MVT/earcut winding is deterministic (no mixed-orientation triangles).

using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests
{
    public class GlobeFillWindingTests
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

        private static (IReadOnlyList<ITileFeature> features, Fill.PaintProperties paint, double extent) Setup()
        {
            var mvtTile   = MvtDecoder.Decode(FixtureBytes());
            var fillLayer = MinimalStyle().Layers[0];
            var paint     = new Fill.PaintProperties(fillLayer);
            var features  = FeatureSelector.SelectFeatures(fillLayer, mvtTile, 0.0);
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "fixture must have the 'countries' layer");
            Assert.Greater(features.Count, 0);
            return (features, paint, mvtLayer.Extent);
        }

        /// <summary>Tally the sign of the angle between each triangle's geometric face normal
        /// (cross(b-a, c-a)) and its outward surface normal; returns (dominantSign, uniformity ∈ [0.5,1]).
        /// Skips edge-on slivers: the 1→4 midpoint subdivision on the globe produces thin triangles whose
        /// face normal is numerical noise (≈⊥ the surface). A well-formed sub-triangle spans ≤3° of curvature,
        /// so its face normal is within a few degrees of the surface normal (cosine≈+1); a genuinely BACK-facing
        /// triangle reads cosine≈−1 — both survive the |cosine|>0.5 gate, only the noise is dropped.</summary>
        private static (int sign, double uniformity, int counted) WindingSign(Mesh mesh)
        {
            Vector3[] v = mesh.vertices;
            Vector3[] n = mesh.normals;
            int[]     t = mesh.triangles;
            Assert.Greater(n.Length, 0, "mesh must carry surface normals");

            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                float3 va = v[t[i]], vb = v[t[i + 1]], vc = v[t[i + 2]]; // Vector3→float3 at the mesh boundary
                float3 g = math.cross(vb - va, vc - va);  // right-handed face normal
                float3 nn = n[t[i]];
                float gm = math.length(g), nm = math.length(nn);
                if (gm <= 0f || nm <= 0f) continue;
                float cos = math.dot(g, nn) / (gm * nm);  // angle between face normal and surface up
                if (math.abs(cos) < 0.5f) continue;       // edge-on sliver (subdivision noise) — skip
                if (cos > 0f) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate triangles to measure");
            int sign = pos >= neg ? 1 : -1;
            double uniformity = (double)math.max(pos, neg) / counted;
            return (sign, uniformity, counted);
        }

        [Test]
        public void GlobeFill_WindsSameAsMercator_RelativeToSurfaceNormal()
        {
            var (features, paint, extent) = Setup();
            var id = new TileId { Z = 0, X = 0, Y = 0 }; // z0 countries: globe path fully subdivides (curvature)

            Mesh flat  = TestTileMeshBuilder.BuildFill(features, paint, 0.0, extent, id, null); // WebMercator
            Mesh globe = TestTileMeshBuilder.BuildFill(features, paint, 0.0, extent, id, new SphericalProjection());
            Assert.IsNotNull(flat,  "Mercator fill must produce geometry");
            Assert.IsNotNull(globe, "globe fill must produce geometry");

            var (mSign, mUnif, mN) = WindingSign(flat);
            var (gSign, gUnif, gN) = WindingSign(globe);

            var w = TestContext.Out;
            w.WriteLine($"Mercator: sign={mSign,2}  uniformity={mUnif:0.0000}  triangles={mN}");
            w.WriteLine($"Globe   : sign={gSign,2}  uniformity={gUnif:0.0000}  triangles={gN}");
            w.Flush();

            // Each build must be near-uniform (MVT/earcut winding is deterministic — no mixed orientation).
            Assert.Greater(mUnif, 0.99, "Mercator fill winding is not uniform (mixed-orientation triangles)");
            Assert.Greater(gUnif, 0.99, "globe fill winding is not uniform (mixed-orientation triangles)");

            // The gold reference: Mercator's front face is correct (maintainer-confirmed). The globe must wind
            // the SAME way relative to its outward normal, so one Cull mode is correct for both projections.
            Assert.AreEqual(mSign, gSign,
                "globe fill winds OPPOSITE to Mercator relative to the surface normal — back-face culling that " +
                "shows Mercator would hide the near hemisphere on the globe (the reported glitch).");

            Object.DestroyImmediate(flat);
            Object.DestroyImmediate(globe);
        }
    }
}
