// Epic A / A2 acceptance — plan §F teeth 3 (globe curvature — the projection payoff, DECISION 3) and 8
// (synthetic ring encoding). Drives the REAL processor path (AllocateForKick → ProcessOnWorker → Complete →
// Upload), not StyledFillTileBuilder directly (HIGH d) — this proves TileBackgroundLayerProcessor actually
// forwards ctx.Tile/TileOriginRender/Projection and settles+uploads.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Background = MapRenderer.Core.Style.Background;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
                                                        // 'Color' ambiguous with UnityEngine's

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TileBackgroundQuadProjectionTests
    {
        // z3/4/3: a COARSE tile (45° span — well above the 3° subdivision threshold, so the globe path must
        // subdivide) that stays comfortably within GlobeFillSubdivideDispatch's DefaultMaxDepth (5) — UNLIKE
        // z0 (~170° span: the WHOLE tile-extent quad, not a small clipped feature), whose worst-case edges
        // hit the recursion-depth cap before reaching the 3° angular threshold (verified: measured ~7.4° at
        // z0, not <=3°). A real MVT feature can be z0-sized because it's usually much smaller than the full
        // tile; this quad IS the full tile, so it needs a less extreme zoom to fit the depth budget.
        private static readonly TileId CoarseTile = new TileId { Z = 3, X = 4, Y = 3 };

        private static BackgroundRenderLayer NewBackgroundLayer()
        {
            var style   = StyleParser.Parse(@"{ ""version"": 8, ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#ffffff"" } } ] }");
            var bgLayer = (Background.StyleLayer)style.Layers[0];
            return BackgroundRenderLayer.Create(bgLayer, MapMaterialSetTestUtil.Load(), initialZoom: 0.0, drawIndex: 0);
        }

        /// <summary>Drives the real processor path for <paramref name="projection"/> and returns the
        /// uploaded mesh (caller destroys it) plus the origin used to bake it (positions are ORIGIN-RELATIVE
        /// — MED 5 — so a caller reconstructing absolute positions must add this back).</summary>
        private static (Mesh mesh, double3 origin) BuildViaProcessor(IProjection projection)
        {
            double3 origin = TileRenderOrigin.Project(CoarseTile, projection);
            var context = new TileLayerProcessContext
            {
                Tile = CoarseTile, Zoom = CoarseTile.Z, TileOriginRender = origin, Projection = projection,
            };

            using var layer = NewBackgroundLayer();
            TileBackgroundLayerProcessor processor = TileBackgroundLayerProcessor.AllocateForKick(layer, materialIndex: 0);
            processor.ProcessOnWorker(null, in context); // source-less: no decoded tile at all
            IRenderLayerPayload payload = processor.Complete();
            Mesh mesh = payload.Upload();
            payload.Dispose(); // no-op after Upload — belt-and-braces, mirrors production consume
            return (mesh, origin);
        }

        /// <summary>
        /// B5 T3 — the background quad is the SAME geometry after the corners stopped being a hand-authored
        /// MVT command stream. A <b>differential against the retired encoding</b>: materialize
        /// <see cref="FullExtentRingCommandStream"/> (the frozen record of what production used to hold)
        /// through the MVT producer, materialize the live processor's corners through the path producer, and
        /// compare the two <c>TileGeometryBuffers</c> element-wise.
        ///
        /// <para>Why this shape and not "assert four corners": a corner-value assertion cannot see ring
        /// COUNT, ring OFFSETS, the trailing SENTINEL or the kind COLUMN — the four things a hand-written
        /// flatten gets wrong. And an oracle written from the new implementation could only restate it; this
        /// one is the thing being retired, so it cannot be satisfied by transcribing the replacement.</para>
        /// </summary>
        [Test]
        public void SyntheticRing_MaterializesIdenticallyToTheRetiredCommandStream()
        {
            double extent = TileBackgroundLayerProcessor.Extent;
            Assert.AreEqual(FullExtentRingCommandStream.Extent, extent,
                "precondition: the retired stream was authored at the extent the processor still uses");

            // The legacy arm: the exact bytes production hand-authored before B5.
            var legacyFeature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                Geometry     = FullExtentRingCommandStream.Commands,
            };
            TileGeometryBuffers legacy = new MvtGeometryMaterializer(
                CoarseTile, extent,
                new[] { legacyFeature.GeometryType },
                new[] { legacyFeature.Geometry }).Materialize();

            // The live arm: PRODUCTION's own corner data and kind column, through production's own path
            // producer — the same relationship the retired version of this test had to the command stream
            // it decoded. A test-owned copy of the corners would compare the fixture with itself.
            TileGeometryBuffers current = new PathGeometryMaterializer(
                CoarseTile, extent,
                TileBackgroundLayerProcessor.FullExtentRingKinds,
                TileBackgroundLayerProcessor.FullExtentRingPaths).Materialize();

            try
            {
                // Non-vacuity: both arms really produced a ring, so an all-default comparison cannot pass.
                Assert.IsTrue(legacy.IsCreated, "precondition: the legacy command stream materialized");
                Assert.IsTrue(current.IsCreated, "precondition: the live background geometry materialized");
                Assert.AreEqual(1, legacy.RingCount, "precondition: the legacy stream is exactly one ring");
                Assert.AreEqual(4, legacy.VertexCount, "precondition: …of exactly four vertices");

                Assert.AreEqual(legacy.RingCount, current.RingCount,
                    "ring COUNT must match — a flatten that emitted two rings, or none, shows only here");
                Assert.AreEqual(legacy.VertexCount, current.VertexCount, "vertex COUNT must match");
                Assert.AreEqual(legacy.FeatureCount, current.FeatureCount, "feature COUNT must match");
                Assert.AreEqual(legacy.Extent, current.Extent, "both must describe the same extent");

                for (int f = 0; f < legacy.FeatureCount; f++)
                    Assert.AreEqual(legacy.FeatureGeometryType[f], current.FeatureGeometryType[f],
                        $"FeatureGeometryType[{f}] — the kind column drives every consumer's ring gate");

                // RingCount + 1 offsets: the trailing SENTINEL is included deliberately. Nothing else in this
                // fixture would notice its absence, and every downstream span read depends on it.
                for (int r = 0; r <= legacy.RingCount; r++)
                    Assert.AreEqual(legacy.RingOffsets[r], current.RingOffsets[r],
                        $"RingOffsets[{r}] (index {legacy.RingCount} is the trailing sentinel)");

                for (int r = 0; r < legacy.RingCount; r++)
                    Assert.AreEqual(legacy.RingFeatureIdx[r], current.RingFeatureIdx[r],
                        $"RingFeatureIdx[{r}] — the join a consumer colours through");

                for (int v = 0; v < legacy.VertexCount; v++)
                {
                    Assert.AreEqual(legacy.Vertices[v].x, current.Vertices[v].x, 1e-12,
                        $"Vertices[{v}].x — corner ORDER is load-bearing, not just membership");
                    Assert.AreEqual(legacy.Vertices[v].y, current.Vertices[v].y, 1e-12,
                        $"Vertices[{v}].y");
                }
            }
            finally
            {
                legacy.Dispose();
                current.Dispose();
            }
        }


        [Test]
        public void BackgroundQuad_FlatOnMercator_NoSubdivision()
        {
            var (mesh, origin) = BuildViaProcessor(new WebMercatorProjection());
            try
            {
                Assert.IsNotNull(mesh, "Mercator background must produce geometry.");
                var verts = new List<Vector3>();
                mesh.GetVertices(verts);

                Assert.AreEqual(4, verts.Count,
                    "Mercator (MaxRefineAngleRad == +∞) must write the flat 4-corner quad — no subdivision.");
                foreach (var v in verts)
                    Assert.AreEqual(0f, v.y, 1e-3f, "Mercator stored positions must be coplanar y≈0 (origin-relative).");

                // B5 T4 colour clause: every background vertex is exactly opaque white. Before B5 this came
                // from evaluating a constant {"fill-color":"#ffffff"} paint through the fill builder; B5
                // passes the literal that expression produced. Nothing else watched that value, so a wrong
                // literal (or a stray alpha) would have rendered a tinted/translucent background silently.
                var colors = new List<Color>();
                mesh.GetColors(colors);
                Assert.AreEqual(verts.Count, colors.Count,
                    "every background vertex must carry a colour — the fill stream is not optional here");
                foreach (Color c in colors)
                    Assert.AreEqual(new Color(1f, 1f, 1f, 1f), c,
                        "background vertex colour must be exactly opaque white (linear == sRGB for white); " +
                        "the material uniform supplies the actual background colour.");

                AssertBoundsMatchesVertexEnvelope(mesh, verts);
                // RTC contract: stored (origin-relative) positions stay bounded to roughly the TILE's own
                // extent — never require ≈ R (the globe radius) or ≈ the world's total circumference. A tile
                // at low zoom is itself physically large (this z3 tile spans ~5000 km), so the bound is
                // relative to the tile's own size, not an absolute small constant.
                double tileSizeWorld = WebMercator.WorldExtent * 2.0 / math.pow(2.0, CoarseTile.Z);
                foreach (var v in verts)
                    Assert.Less(((Vector3)v).magnitude, (float)(tileSizeWorld * 2.0),
                        "Mercator stored positions must be origin-relative (bounded to ~the tile's own extent), " +
                        "not world/circumference-scale (a missing origin subtraction would show ~40,075,017 m).");
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        /// <summary>DECISION 3 (round-3 fix for HIGH d): reads the uploaded INDEX buffer and proves rendered
        /// subdivision TOPOLOGY, not just vertex existence — (a) every emitted vertex is triangle-referenced
        /// (no dangling verts from an "append midpoints without reindexing" bug), (b) all 4 projected tile
        /// corners are present (defeats a wrong TileId), (c) every indexed triangle edge subtends ≤ the
        /// subdivision angular threshold on the sphere (defeats a hardcoded-flat/unsplit-quad impl whose
        /// chord edges exceed it) — plus the <see cref="Mesh.bounds"/> envelope check.</summary>
        [Test]
        public void BackgroundQuad_CurvesOnSphere_SubdividedTopology()
        {
            var projection = new SphericalProjection();
            var (mesh, origin) = BuildViaProcessor(projection);
            try
            {
                Assert.IsNotNull(mesh, "globe background must produce geometry.");
                var verts = new List<Vector3>();
                mesh.GetVertices(verts);
                var tris = new List<int>();
                mesh.GetTriangles(tris, 0);

                Assert.Greater(verts.Count, 4,
                    "the globe patch must subdivide beyond the flat 4-corner quad (GlobeFillSubdivideJob emits " +
                    "fresh verts per source triangle).");
                Assert.Greater(tris.Count, 0, "the uploaded mesh must carry a non-empty index buffer.");
                Assert.AreEqual(0, tris.Count % 3, "index buffer must be a whole number of triangles.");

                // (a) every emitted vertex is referenced by SOME triangle — defeats "append midpoints
                // without rewiring indices" (dangling verts that inflate the count but aren't rendered).
                var referenced = new bool[verts.Count];
                foreach (int idx in tris) referenced[idx] = true;
                Assert.IsTrue(referenced.All(r => r),
                    "every uploaded vertex must be referenced by the index buffer — a dangling (unreferenced) " +
                    "vertex means it was appended without rewiring triangle indices.");

                // Absolute (world/ECEF) positions — MED 5: stored positions are ORIGIN-RELATIVE, so
                // reconstruct absolute BEFORE any sphere-space (radius/angle) test.
                var absolute = verts.Select(v => new double3(v.x, v.y, v.z) + origin).ToArray();
                foreach (var a in absolute)
                    Assert.That(math.length(a), Is.EqualTo(SphericalProjection.Radius).Within(SphericalProjection.Radius * 1e-6),
                        "every absolute vertex must lie ON the sphere of SphericalProjection.Radius.");

                // (b) all 4 projected tile corners for CoarseTile are present among the vertices.
                double2[] cornersTileLocal =
                {
                    new double2(0, 0), new double2(TileBackgroundLayerProcessor.Extent, 0),
                    new double2(TileBackgroundLayerProcessor.Extent, TileBackgroundLayerProcessor.Extent),
                    new double2(0, TileBackgroundLayerProcessor.Extent),
                };
                foreach (double2 tc in cornersTileLocal)
                {
                    double2 lonLat = CoarseTile.ToLonLat(tc.x, tc.y, TileBackgroundLayerProcessor.Extent);
                    double3 expectedAbs = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                    bool found = absolute.Any(a => math.distance(a, expectedAbs) < SphericalProjection.Radius * 1e-4);
                    Assert.IsTrue(found, $"projected tile corner {tc} (lon/lat {lonLat}) must be present among the " +
                                          "uploaded vertices — a wrong TileId would project different corners.");
                }

                // (c) every indexed triangle edge subtends ≤ the subdivision angular threshold on the sphere
                // (the real proof of subdivision — a mere vertex-count>4 doesn't prove split FACES; a
                // hardcoded-flat or unsplit-quad impl's chord edges would exceed this).
                double maxAllowedRad = GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad * 1.10; // 10% float slack
                int edgesChecked = 0;
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    AssertEdgeWithinThreshold(absolute[i0], absolute[i1], maxAllowedRad, ref edgesChecked);
                    AssertEdgeWithinThreshold(absolute[i1], absolute[i2], maxAllowedRad, ref edgesChecked);
                    AssertEdgeWithinThreshold(absolute[i2], absolute[i0], maxAllowedRad, ref edgesChecked);
                }
                Assert.Greater(edgesChecked, 0, "must have checked at least one triangle edge.");

                AssertBoundsMatchesVertexEnvelope(mesh, verts);
            }
            finally { if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        private static void AssertEdgeWithinThreshold(double3 a, double3 b, double maxAllowedRad, ref int edgesChecked)
        {
            double cosAngle = math.dot(a, b) / (math.length(a) * math.length(b));
            cosAngle = math.clamp(cosAngle, -1.0, 1.0);
            double angle = math.acos(cosAngle);
            Assert.LessOrEqual(angle, maxAllowedRad,
                "every indexed triangle edge must subtend <= the subdivision angular threshold on the sphere " +
                "— the faces must hug the sphere, not chord straight through it.");
            edgesChecked++;
        }

        /// <summary>Both projections: the uploaded <see cref="Mesh.bounds"/> equals the origin-relative
        /// vertex envelope and encapsulates every stored vertex.</summary>
        private static void AssertBoundsMatchesVertexEnvelope(Mesh mesh, List<Vector3> verts)
        {
            Vector3 min = verts[0], max = verts[0];
            foreach (var v in verts) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            Vector3 expectedCenter = (min + max) * 0.5f;
            Vector3 expectedSize   = max - min;

            Assert.That(mesh.bounds.center.x, Is.EqualTo(expectedCenter.x).Within(1e-2f));
            Assert.That(mesh.bounds.center.y, Is.EqualTo(expectedCenter.y).Within(1e-2f));
            Assert.That(mesh.bounds.center.z, Is.EqualTo(expectedCenter.z).Within(1e-2f));
            Assert.That(mesh.bounds.size.x, Is.EqualTo(expectedSize.x).Within(1e-2f));
            Assert.That(mesh.bounds.size.y, Is.EqualTo(expectedSize.y).Within(1e-2f));
            Assert.That(mesh.bounds.size.z, Is.EqualTo(expectedSize.z).Within(1e-2f));

            Bounds expanded = mesh.bounds;
            expanded.Expand(1e-2f);
            foreach (var v in verts)
                Assert.IsTrue(expanded.Contains(v), $"mesh.bounds must encapsulate every stored vertex ({v}).");
        }

        /// <summary>
        /// The kind column and the path list are two independent lists joined BY POSITION, so a length
        /// mismatch would mis-classify every ring rather than fail. <c>PathGeometryMaterializer</c> validates
        /// that <b>before</b> it allocates — which is what makes the throw safe: after <c>Allocate</c> four
        /// <c>Allocator.Persistent</c> arrays exist and a throw would strand them on the one exit path no
        /// caller can dispose.
        /// <para>Structural rather than behavioural on purpose: production always passes matched lists, so
        /// this path is unreachable in production and no behavioural test can reach it. That is precisely why
        /// it needs a tooth — the sibling <c>MvtGeometryMaterializer</c> holds its own unreachable throw path
        /// to the same standard, "by reading the code, not by arguing reachability".</para>
        /// </summary>
        [Test]
        public void PathMaterializer_KindColumnShorterThanPaths_ThrowsBeforeAllocating()
        {
            var paths = new List<IReadOnlyList<IReadOnlyList<double2>>>
            {
                new[] { new[] { new double2(0, 0), new double2(1, 0), new double2(1, 1) } },
                new[] { new[] { new double2(2, 2), new double2(3, 2), new double2(3, 3) } },
            };
            // One kind for two features — the desync.
            var kinds = new List<TileGeometryType> { TileGeometryType.Polygon };

            // Non-vacuity: the matched pair really does materialize, so the throw below is attributable to the
            // mismatch and not to some other defect in the fixture.
            var matchedKinds = new List<TileGeometryType>
                { TileGeometryType.Polygon, TileGeometryType.Polygon };
            TileGeometryBuffers ok = new PathGeometryMaterializer(
                CoarseTile, 4096.0, matchedKinds, paths).Materialize();
            try
            {
                Assert.IsTrue(ok.IsCreated, "precondition: matched kinds/paths must materialize");
                Assert.AreEqual(2, ok.RingCount, "precondition: both features' rings are present");
            }
            finally { ok.Dispose(); }

            // Fully qualified: `using System;` would make `Object` ambiguous with UnityEngine.Object at this
            // file's existing call sites.
            var ex = Assert.Throws<System.ArgumentException>(
                () => new PathGeometryMaterializer(CoarseTile, 4096.0, kinds, paths).Materialize(),
                "a kind column shorter than the path list must be rejected, not silently mis-joined");
            Assert.That(ex.Message, Does.Contain("one entry per feature"),
                "the message must name the contract that was violated, so a wiring error is diagnosable");
        }
    }
}
