using System.Collections.Generic;
using System.IO;
using System;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Regression (fast, headless) for the near-180° miter-hairpin blow-up: real admin-boundary tiles from
    /// the liberty style contained line features whose projected geometry doubles back on itself, and the
    /// miter-vs-bevel gate compared the SIGNED miter factor (1/dot) to the limit. Near a hairpin dot goes
    /// small-NEGATIVE, so a huge negative factor slipped past the gate and the extrusion normal exploded
    /// (a "line across the whole screen"). Fixed by comparing |1/dot| in LineTessellator.NeedsBevel (and its
    /// mirror in the Burst LineTessellationJob). This runs the managed tessellator on the same projected
    /// points production feeds it and asserts the miter factor stays capped.
    ///
    /// The full production path (the actual Burst job + bake) is guarded by the EditMode
    /// BoundaryGlitchMeshTests; this is the seconds-fast oracle-side guard.
    /// </summary>
    public class LineMiterHairpinTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "Assets", "StreamingAssets", "Fixtures", "liberty.json")))
                    return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new FileNotFoundException("repo root (Assets/StreamingAssets/Fixtures/liberty.json) not found");
        }

        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-6-38-19.pbf.bytes",   6,  38,  19)]
        public void Boundary3_ProjectedMiterFactor_StaysCapped(string fixture, int z, int x, int y)
        {
            string root = RepoRoot();
            StyleDocument style = StyleParser.Parse(File.ReadAllText(
                Path.Combine(root, "Assets", "StreamingAssets", "Fixtures", "liberty.json")));
            StyleLayer layer = null;
            foreach (var l in style.Layers) if (l.Id == "boundary_3") { layer = l; break; }
            Assert.IsNotNull(layer, "boundary_3 not found in liberty.json");

            MvtTile tile = MvtDecoder.Decode(File.ReadAllBytes(Path.Combine(root, "Assets", "Fixtures", fixture)));
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            double extent = mvtLayer?.Extent ?? 4096.0;
            var id = new TileId { Z = z, X = x, Y = y };
            var proj = new WebMercatorProjection();
            IReadOnlyList<ITileFeature> features = FeatureSelector.SelectFeatures(layer, tile, z);
            Assert.Greater(features.Count, 0, "expected boundary_3 features in this tile");

            const double miterLimit = 2.0;
            double maxFactor = 0;
            for (int fi = 0; fi < features.Count; fi++)
            {
                foreach (var ring in MvtGeometry.Decode(features[fi].Geometry))
                {
                    if (ring == null || ring.Count < 2) continue;
                    var projected = new List<double2>(ring.Count);
                    foreach (var p in ring)
                    {
                        double2 ll = id.ToLonLat(p.x, p.y, extent);
                        double3 wpos = proj.ProjectPoint(
                            new GeoCoordinate { Latitude = ll.y, Longitude = ll.x }).World;
                        projected.Add(new double2(wpos.x, wpos.z));
                    }
                    LineTessellator.Result res = LineTessellator.Triangulate(
                        projected, JoinType.Miter, CapType.Butt, miterLimit, 4);
                    for (int vi = 0; vi < res.Vertices.Length; vi++)
                    {
                        double2 nrm = res.Vertices[vi].Normal;
                        double m = System.Math.Sqrt(nrm.x * nrm.x + nrm.y * nrm.y);
                        if (m > maxFactor) maxFactor = m;
                    }
                }
            }

            // The extrusion-normal magnitude is the miter factor; a miter join must never exceed the limit
            // (bevel takes over past it). Pre-fix this reached 49,000+ on the oracle and 10^11 in the mesh.
            Assert.LessOrEqual(maxFactor, miterLimit + 0.01,
                $"{fixture}: miter factor {maxFactor:0.###} exceeds the limit — the hairpin bevel gate leaked.");
        }
    }
}
