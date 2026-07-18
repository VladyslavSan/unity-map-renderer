// GlobeLineWindingTests — the line RIBBON's emitted front face must point OUT of the surface (sign +1) on BOTH
// the globe and the flat Mercator build, so stock Cull Back (shipped MapLine.mat _Cull:2) keeps the near-side
// ribbon and culls the far side without hiding the near one. Asserts the absolute +1 invariant AND globe ==
// Mercator (one cull mode fits both). Lines are extruded in the vertex shader (pos + across·side·width), so —
// unlike fills — the rendered winding is NOT visible in the centerline mesh positions; this test RECONSTRUCTS
// the extruded ribbon the way the shader does, then measures sign(dot(faceNormal, surfaceNormal)) per triangle.
//
// Winding is derived BY CONSTRUCTION (S100): the ribbon `across = cross(along, up)` is tied to the SAME `up`
// the centerline was projected with (one frame), so the globe ribbon winds the same way as the flat Mercator
// build with no per-projection flip. This test pins that — calibrate to Mercator, require the globe to match —
// and is the regression tripwire against reintroducing a handedness/curvature branch (see also the decisive
// RightHandedSphereProjectionWindingTests, where a right-handed CURVED projection must ALSO match Mercator).
//
// Robustness: the reconstruction need not be shader-exact. Any consistent error (wrong width, wrong side
// convention) flips Mercator AND globe equally, so the sign-equality comparison survives.

using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Style;
using MapRenderer.Core.Filters;
using LineStyleLayer = MapRenderer.Core.Style.Line.StyleLayer;

namespace MapRenderer.Tests
{
    public class GlobeLineWindingTests
    {
        // z6 and z9 boundary_3 fixtures — winding is independent of curvature subdivision, so either zoom works.
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void GlobeLine_WindsSameAsMercator_RelativeToSurfaceNormal(string fixture, int z, int x, int y)
        {
            var id = new TileId { Z = z, X = x, Y = y };

            StyleDocument style = StyleParser.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json")));
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            MvtTile tile = MvtDecoder.Decode(File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            double extent = mvtLayer?.Extent ?? 4096.0;
            IReadOnlyList<ITileFeature> features = FeatureSelector.SelectFeatures(layer, tile, z);
            Assert.Greater(features.Count, 0, "expected boundary_3 line features in this tile");

            Mesh flat  = TestTileMeshBuilder.BuildLine(features, layer.Paint, layer.Layout, z, extent, id, new WebMercatorProjection());
            Mesh globe = TestTileMeshBuilder.BuildLine(features, layer.Paint, layer.Layout, z, extent, id, new SphericalProjection());
            Assert.IsNotNull(flat,  "Mercator line must produce geometry");
            Assert.IsNotNull(globe, "globe line must produce geometry");

            var (mSign, mUnif, mN) = RibbonWindingSign(flat);
            var (gSign, gUnif, gN) = RibbonWindingSign(globe);

            var w = TestContext.Out;
            w.WriteLine($"[{fixture}] Mercator: sign={mSign,2} uniformity={mUnif:0.0000} tris={mN}");
            w.WriteLine($"[{fixture}] Globe   : sign={gSign,2} uniformity={gUnif:0.0000} tris={gN}");
            w.Flush();

            Assert.Greater(mUnif, 0.99, "Mercator ribbon winding is not uniform");
            Assert.Greater(gUnif, 0.99, "globe ribbon winding is not uniform");
            // ABSOLUTE invariant (post the GPU-boundary winding reversal in StyledLineTileBuilder): the extruded
            // ribbon's front face points OUT of the surface (sign +1), the orientation stock Cull Back (shipped
            // MapLine.mat _Cull:2) keeps for the camera-facing side. Sign −1 means the reversal was dropped and
            // the ribbon renders inverted. Closes the hole the relative-only check left (both sides could flip
            // together and stay green).
            Assert.AreEqual(1, mSign, $"{fixture}: Mercator ribbon front face must point OUT (Unity-front under stock Cull Back)");
            Assert.AreEqual(1, gSign, $"{fixture}: globe ribbon front face must point OUT (Unity-front under stock Cull Back)");
            Assert.AreEqual(mSign, gSign,
                $"{fixture}: globe line ribbon winds OPPOSITE to Mercator relative to the surface normal — " +
                "back-face culling that shows Mercator would hide the near hemisphere on the globe.");

            Object.DestroyImmediate(flat);
            Object.DestroyImmediate(globe);
        }

        /// <summary>Reconstruct the shader-extruded ribbon (pos + across·W — the shader bakes the per-side sign
        /// INTO `across`/extrudeN, so there is NO ·side here; `side` is AA-only) and tally the sign of the angle
        /// between each triangle's face normal and its surface normal. W is adaptive PER TRIANGLE — a small
        /// fraction of the local centerline edge — so the ribbon stays locally thin and never folds at a sharp
        /// turn (a fixed absolute W folds, and folds the globe's subdivided-shorter segments differently). This
        /// is index-order-sensitive (the reversed winding is exactly what the flip fixes). Join/cap fans, whose
        /// three verts share one centerline point (edge≈0), are skipped — their winding is ambiguous.</summary>
        internal static (int sign, double uniformity, int counted) RibbonWindingSign(Mesh mesh)
        {
            Vector3[] p   = mesh.vertices;
            Vector3[] nrm = mesh.normals;
            var across = new List<Vector3>(); mesh.GetUVs(0, across); // TexCoord0 = across (per-side sign baked in)
            int[] t = mesh.triangles;
            Assert.AreEqual(p.Length, across.Count, "across stream must be present");

            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                float3 pa = p[t[i]], pb = p[t[i + 1]], pc = p[t[i + 2]]; // Vector3→float3 at the mesh boundary
                // Local scale = longest centerline edge; w = 5% of it keeps the extruded ribbon thin (no fold).
                float d = math.max(math.distance(pa, pb), math.max(math.distance(pb, pc), math.distance(pc, pa)));
                if (d < 1e-4f) continue; // join/cap fan collapsed to one centerline point — ambiguous, skip
                float w = 0.05f * d;
                float3 ea = pa + (float3)across[t[i]]     * w;
                float3 eb = pb + (float3)across[t[i + 1]] * w;
                float3 ec = pc + (float3)across[t[i + 2]] * w;
                float3 g   = math.cross(eb - ea, ec - ea);      // extruded face normal
                float3 n   = nrm[t[i]];
                float gm = math.length(g), nm = math.length(n);
                if (gm <= 0f || nm <= 0f) continue;
                float cos = math.dot(g, n) / (gm * nm);
                if (math.abs(cos) < 0.5f) continue; // edge-on sliver
                if (cos > 0f) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate ribbon triangles to measure");
            int sign = pos >= neg ? 1 : -1;
            double uniformity = (double)math.max(pos, neg) / counted;
            return (sign, uniformity, counted);
        }
    }
}
