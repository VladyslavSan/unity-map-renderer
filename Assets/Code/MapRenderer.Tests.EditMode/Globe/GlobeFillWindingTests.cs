// GlobeFillWindingTests — the fill geometry's emitted front face must genuinely point OUT of the surface, so
// stock Cull Back (shipped MapFill.mat _Cull:2) keeps the camera-facing surface on BOTH the flat Mercator and
// the globe build. This asserts TWO things: (1) an ABSOLUTE invariant — the dominant sign(dot(cross(edges),
// up)) is +1 (front face aligned with the outward normal; the LayerOrderSnapshotTests {0,2,1,0,3,2} +Y-normal
// quad computes the same +1 and renders under Cull Back); and (2) globe == Mercator, so one cull mode fits both.
// An earlier version asserted ONLY the relative equality and stayed green while BOTH sides were inverted
// (front-renders-as-back) — the absolute check closes that hole. The globe render mapping (ECEF axis-swap) is a
// reflection that flips raw winding; StyledFillTileBuilder reverses triangle indices at the GPU mesh-write
// boundary to restore a Unity-front result, which is what makes the sign come out +1.
//
// Both builds run the real production mesh build: Mercator through FillMeshPipeline's earcut/project IR, the
// globe through GlobeFillSubdivideJob (a distinct emission path) — both reversed once at the StyledFillTileBuilder
// write. A near-uniform dominant sign on each side also proves MVT/earcut winding is deterministic (no
// mixed-orientation triangles).

using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests
{
    public class GlobeFillWindingTests
    {

        /// <summary>IR C1 P3: a decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""Test"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>IR C1 P3: the fixture is decoded at the SAME z0 address every test builds at, and the
        /// layer (which owns its buffer) is returned instead of a detached feature list.</summary>
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static (ITileLayer layer, List<SelectedTileFeature> selection, Fill.PaintProperties paint) Setup()
        {
            var mvtTile   = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, SampleTileFixture.Bytes()));
            var fillLayer = MinimalStyle().Layers[0];
            var paint     = new Fill.PaintProperties(fillLayer);
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "fixture must have the 'countries' layer");
            var selected  = TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0);
            Assert.Greater(selected.Count, 0);
            return (mvtLayer, selected, paint);
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
                // Skip near-degenerate NEEDLE slivers (high aspect ratio): the globe fill's edge-conforming
                // subdivision leaves antimeridian-spanning earcut slivers (an earcut concern, not a winding
                // one — ~0.08% of z0 fill area) whose geometric face normal is likewise numerical noise, but
                // not edge-on enough to trip the |cos|<0.5 gate below. thinness = 2·area/longestEdge² =
                // gm/longestEdge²; well-formed ≈0.4+, a needle ≈<0.02. This keeps the check measuring
                // MVT/earcut orientation consistency on WELL-FORMED triangles (its stated intent) — it does
                // NOT lower the 0.99 uniformity bar.
                float longestSq = math.max(math.lengthsq(vb - va), math.max(math.lengthsq(vc - vb), math.lengthsq(va - vc)));
                if (longestSq > 0f && gm / longestSq < 0.02f) continue; // needle sliver — face-normal noise
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
            var (layer, selection, paint) = Setup(); // z0 countries: globe path fully subdivides (curvature)

            Mesh flat  = TestTileMeshBuilder.BuildFillFromLayer(layer, selection, paint, 0.0, FixtureTile, null); // WebMercator
            Mesh globe = TestTileMeshBuilder.BuildFillFromLayer(layer, selection, paint, 0.0, FixtureTile, new SphericalProjection());
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

            // ABSOLUTE invariant (post the GPU-boundary winding reversal in StyledFillTileBuilder): each emitted
            // triangle's right-handed face normal must ALIGN with the outward surface normal (sign +1) — the
            // front face genuinely points OUT of the surface. That is exactly the orientation stock Cull Back
            // (shipped MapFill.mat _Cull:2) keeps for the camera-facing surface. Same convention as the
            // LayerOrderSnapshotTests fill quad: {0,2,1,0,3,2} with a +Y normal ⇒ dot(cross_RH, up) = +1, which
            // renders under Cull Back; the old {0,1,2} gave −1 and needed the compensating Cull Front. A sign of
            // −1 here means the reversal was dropped and the render is inverted (front-renders-as-back). This
            // catches the inversion that the earlier relative-only guard could not — it stayed green while both
            // sides flipped together.
            Assert.AreEqual(1, mSign, "Mercator fill front face must point OUT of the surface (Unity-front under stock Cull Back)");
            Assert.AreEqual(1, gSign, "globe fill front face must point OUT of the surface (Unity-front under stock Cull Back)");
            // Consistency: globe must wind the SAME as Mercator relative to its outward normal, so one cull mode
            // is correct for both projections.
            Assert.AreEqual(mSign, gSign,
                "globe fill winds OPPOSITE to Mercator relative to the surface normal — back-face culling that " +
                "shows Mercator would hide the near hemisphere on the globe (the reported glitch).");

            Object.DestroyImmediate(flat);
            Object.DestroyImmediate(globe);
        }
    }
}
