using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using LineStyleLayer = MapRenderer.Core.Style.Line.StyleLayer;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Rung 2 (EditMode, the source of truth): build the REAL boundary_3 line mesh through the production
    /// path — projection (TileToGeoJob → managed ProjectPoint) + the actual Burst LineRibbonJob + the
    /// Mercator bake — for the three maintainer-reported "line/polygon across the whole screen" tiles, and
    /// scan the resulting Mesh for any triangle whose edge spans more than half a tile.
    ///
    /// The fast Core path already proved decode, filter, and the managed mesh build ORACLE are all clean
    /// on these rings (BoundaryGlitchDump). This test exercises the two stages that path could not — the
    /// Burst job and the projection/bake — so a failure here localizes the glitch to one of those; a pass
    /// pushes it downstream of the mesh (shader extrude / origin / camera).
    /// </summary>
    public class BoundaryGlitchMeshTests
    {
        // fixture, tile z, x, y  (paint/width evaluate at integer tile z, matching the kick path)
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-6-38-19.pbf.bytes",   6,  38,  19)]
        public void Boundary3Mesh_HasNoAcrossTileTriangle(string fixture, int z, int x, int y)
        {
            var id = new TileId { Z = z, X = x, Y = y };

            string styleJson = File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json"));
            StyleDocument style = StyleParser.Parse(styleJson);
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture));
            // IR C1 P3: decoded at the SAME id the build bakes from — the buffer is the only copy now.
            using MvtTile tile = MvtDecoder.Decode(id, bytes);
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            Assert.IsNotNull(mvtLayer, "boundary_3's source-layer must resolve in this fixture");
            var selected = TestTileMeshBuilder.Select(layer, mvtLayer, z);
            Assert.Greater(selected.Count, 0, "expected boundary_3 line features in this tile");

            // Production path: real projection + real Burst LineRibbonJob + Mercator bake, over the layer's
            // own buffer with this style layer's ordinal-bearing selection.
            Mesh mesh = TestTileMeshBuilder.BuildLineFromLayer(
                mvtLayer, selected, layer.Paint, layer.Layout, z, id, new WebMercatorProjection());
            Assert.IsNotNull(mesh, "boundary_3 produced no geometry");

            Vector3[] verts = mesh.vertices;
            int[]     tris  = mesh.triangles;

            double tileWorld = EarthConstants.EquatorialCircumferenceMetres / math.pow(2.0, z);
            double threshold = tileWorld * 0.5;

            var w = TestContext.Out;
            w.WriteLine($"=== {fixture} z{z} verts={verts.Length} tris={tris.Length / 3} " +
                        $"tileWorld={tileWorld:0}m threshold={threshold:0}m ===");

            int longTris = 0;
            double worst = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                double e0 = Vector3.Distance(a, b), e1 = Vector3.Distance(b, c), e2 = Vector3.Distance(c, a);
                double e = math.max(e0, math.max(e1, e2));
                if (e > threshold)
                {
                    longTris++;
                    if (e > worst) worst = e;
                    if (longTris <= 8)
                    {
                        w.WriteLine($"  [ACROSS-TILE] tri#{t / 3} edge={e:0}m");
                        w.WriteLine($"     a=({a.x:0},{a.y:0},{a.z:0}) b=({b.x:0},{b.y:0},{b.z:0}) c=({c.x:0},{c.y:0},{c.z:0})");
                    }
                }
            }
            w.WriteLine($"  => across-tile triangles: {longTris}  worstEdge={worst:0}m");

            // The mesh vertices are the CENTERLINE; width is a shader extrude along the TexCoord0 "across"
            // vector (magnitude = miter factor, which the miter limit should cap near ~2). TexCoord2 carries
            // the per-vertex width scale. A huge value in either => the shader flings that vertex across the
            // screen while the centerline mesh above still looks clean. This is the data-side check the
            // position scan can't make.
            var across = new List<Vector3>();
            mesh.GetUVs(0, across);
            var widthScale = new List<Vector4>(); // TexCoord2 is Float32x1 → x carries widthScale
            mesh.GetUVs(2, widthScale);
            double maxAcross = 0; int maxAcrossVert = -1;
            for (int vi = 0; vi < across.Count; vi++)
            {
                double m = across[vi].magnitude;
                if (m > maxAcross) { maxAcross = m; maxAcrossVert = vi; }
            }
            double maxWidth = 0;
            for (int vi = 0; vi < widthScale.Count; vi++)
                if (widthScale[vi].x > maxWidth) maxWidth = widthScale[vi].x;
            w.WriteLine($"  => maxAcross(miterFactor)={maxAcross:0.00} at vert {maxAcrossVert}  maxWidthScale={maxWidth:0.00}");
            w.Flush();

            Assert.AreEqual(0, longTris,
                $"{fixture}: {longTris} triangle(s) span >half a tile (worst {worst:0}m) — glitch is in the mesh centerline.");
            Assert.Less(maxAcross, 8.0,
                $"{fixture}: across/miter factor {maxAcross:0.0} ≫ miter limit — the shader extrude flings vert {maxAcrossVert} across the screen.");
        }
    }
}
