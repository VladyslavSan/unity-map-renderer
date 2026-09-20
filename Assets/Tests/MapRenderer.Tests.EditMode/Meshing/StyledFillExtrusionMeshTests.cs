// S23 I2b — StyledFillExtrusionTileBuilder mesh teeth. Headless (no render), Unity EditMode only (uses
// UnityEngine.Mesh / NativeArray — not in Tools/core-tests).
//
// Covers the I2b invariant/teeth from the dev brief:
//   T1 (RED-verify) — within each wall quad, the floor (t=0) and roof (t=1) verts COINCIDE in footprint
//                      position (differ only by t + identical baked extrude-up). Roof-cap vs wall-t=1
//                      verts at the same ring point are NOT asserted equal (different lighting normals).
//   T2 — height maps to H·sec(φ) along the per-vertex baked extrude-up; fixture discriminates on BOTH
//        non-unity sec φ (high latitude) AND latitude SPAN (two builds sharing one TileId at different Y).
//   T5 — constant/zoom height rides the uniform (bake stream stays 0); data-driven height bakes per-vertex
//        (uniform untouched); a height-VALUE change alone does not change vertex/index counts (extends T1).
//   T6 — wall + roof front-faces point OUT under Cull Back, on both Mercator and the globe (ABSOLUTE check,
//        not merely relative — mirrors GlobeFillWindingTests).
//
// Wall quad layout is a WHITE-BOX assumption these teeth rely on (StyledFillExtrusionTileBuilder.WriteWalls
// appends exactly 4 vertices per boundary edge, AFTER all roof vertices, in floorA/floorB/roofB/roofA order):
// a single hole-less convex N-gon footprint therefore yields (roofVertexCount) + 4*N total vertices, with
// the last 4*N being the wall vertices in per-edge groups of 4. If that emission order ever changes, these
// teeth's grouping must change with it — the coincidence/count assertions are what actually pins behaviour,
// not the grouping trick itself.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Expressions;
namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class StyledFillExtrusionMeshTests
    {
        // ── Fixture geometry: a single convex square ring (exterior only, no holes) ──────────────
        // MVT command stream: MoveTo(1) to the first corner, LineTo(3) for the rest, implicitly closed.
        // Winding (CW/CCW in tile-local Y-down space) is NOT asserted here — T6 measures the ACTUAL
        // rendered front-face sign, it does not assume one.

        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));

        private static uint[] SquareRing(int x0, int y0, int size)
        {
            return new uint[]
            {
                (1u << 3) | 1u, ZigZag(x0), ZigZag(y0),                       // MoveTo count=1 → (x0,y0)
                (3u << 3) | 2u,                                               // LineTo count=3
                ZigZag(size), ZigZag(0),                                     // → (x0+size, y0)
                ZigZag(0),    ZigZag(size),                                  // → (x0+size, y0+size)
                ZigZag(-size), ZigZag(0),                                    // → (x0, y0+size)
            };
        }

        private static IFeature SquareFeature(int x0, int y0, int size, IReadOnlyDictionary<string, Value> props = null)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon,
                geometry: SquareRing(x0, y0, size));

        private const double Extent = 4096.0;
        private static readonly TileId ModerateTile = new TileId { Z = 10, X = 300, Y = 380 }; // mid-latitude, arbitrary

        // ── T1 (RED-verify) + T5 (topology invariant): a single square footprint ─────────────────

        [Test]
        public void Wall_FloorAndRoof_CoincideInFootprintPosition()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50}"); // constant — no bake

            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile);
            Assert.IsNotNull(mesh, "a single square footprint must produce geometry.");

            Vector3[] positions = mesh.vertices;
            var extrudeUpAndT = new List<Vector4>();
            mesh.GetUVs(3, extrudeUpAndT);
            Assert.AreEqual(positions.Length, extrudeUpAndT.Count, "TEXCOORD3 (extrudeUpAndT) must be populated for every vertex.");

            // A hole-less convex quad triangulates without bridge splits: roof = 4 verts, walls = 4 edges *
            // 4 verts = 16 — appended in that order (see the class doc's white-box note).
            Assert.AreEqual(20, positions.Length,
                "expected roof(4) + walls(4 edges * 4 verts) = 20 vertices for a hole-less square footprint.");

            int wallStart = positions.Length - 16;
            for (int edge = 0; edge < 4; edge++)
            {
                int b = wallStart + edge * 4; // floorA, floorB, roofB, roofA
                Vector3 floorA = positions[b + 0], floorB = positions[b + 1];
                Vector3 roofB  = positions[b + 2], roofA  = positions[b + 3];
                float tFloorA = extrudeUpAndT[b + 0].w, tFloorB = extrudeUpAndT[b + 1].w;
                float tRoofB  = extrudeUpAndT[b + 2].w, tRoofA  = extrudeUpAndT[b + 3].w;

                Assert.AreEqual(0f, tFloorA, 1e-6f); Assert.AreEqual(0f, tFloorB, 1e-6f);
                Assert.AreEqual(1f, tRoofB,  1e-6f); Assert.AreEqual(1f, tRoofA,  1e-6f);

                // T1: floor/roof AT THE SAME RING POINT share the identical footprint position — the mesh
                // is height-agnostic; only t (+ the extrude-up magnitude, checked below) differs.
                Assert.AreEqual(floorA, roofA,
                    $"edge {edge}: floorA and roofA must coincide in position (height-agnostic footprint).");
                Assert.AreEqual(floorB, roofB,
                    $"edge {edge}: floorB and roofB must coincide in position (height-agnostic footprint).");

                Vector4 upA0 = extrudeUpAndT[b + 0], upA1 = extrudeUpAndT[b + 3];
                Vector4 upB0 = extrudeUpAndT[b + 1], upB1 = extrudeUpAndT[b + 2];
                Assert.AreEqual(new Vector3(upA0.x, upA0.y, upA0.z), new Vector3(upA1.x, upA1.y, upA1.z),
                    $"edge {edge}: extrude-up must be identical for the floor/roof pair at A.");
                Assert.AreEqual(new Vector3(upB0.x, upB0.y, upB0.z), new Vector3(upB1.x, upB1.y, upB1.z),
                    $"edge {edge}: extrude-up must be identical for the floor/roof pair at B.");
            }

            // RED-VERIFIED (2026-09-04): baking `extrudeUp * 10` into `Position` for t>0.5 in AddWallVertex
            // failed "edge 0: floorA and roofA must coincide in position (height-agnostic footprint)." —
            // T1's own message, confirming the tooth can fail.
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void HeightUniformChange_DoesNotChangeVertexOrIndexCount()
        {
            var feature = SquareFeature(1000, 1000, 500);

            Mesh meshLow  = TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":1}"),  0.0, Extent, ModerateTile);
            Mesh meshHigh = TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":999}"), 0.0, Extent, ModerateTile);
            Assert.IsNotNull(meshLow); Assert.IsNotNull(meshHigh);

            Assert.AreEqual(meshLow.vertexCount, meshHigh.vertexCount,
                "changing _ExtrusionHeight's VALUE must never change the vertex count — extrusion is a VS op.");
            Assert.AreEqual(meshLow.triangles.Length, meshHigh.triangles.Length,
                "changing _ExtrusionHeight's VALUE must never change the index count.");

            Object.DestroyImmediate(meshLow);
            Object.DestroyImmediate(meshHigh);
        }

        // ── T5: uniform vs bake ───────────────────────────────────────────────────────────────────

        [Test]
        public void ConstantHeight_BakeStreamStaysZero()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50,\"fill-extrusion-base\":5}");

            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile);
            Assert.IsNotNull(mesh);

            var bakedBaseHeight = new List<Vector2>();
            mesh.GetUVs(4, bakedBaseHeight);
            Assert.AreEqual(mesh.vertexCount, bakedBaseHeight.Count);
            foreach (var bake in bakedBaseHeight)
                Assert.AreEqual(Vector2.zero, bake,
                    "constant/zoom height+base must NOT be baked per-vertex — the uniform carries them.");

            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void DataDrivenHeight_BakesEvaluatedValuePerVertex()
        {
            var props  = new Dictionary<string, Value> { ["h"] = Value.Number(42.0) };
            var feature = SquareFeature(1000, 1000, 500, props);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":[\"get\",\"h\"]}");
            Assert.IsTrue(paint.Height.DependsOnFeature, "fixture sanity: [\"get\",\"h\"] must classify as Feature-kind.");

            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile);
            Assert.IsNotNull(mesh);

            var bakedBaseHeight = new List<Vector2>();
            mesh.GetUVs(4, bakedBaseHeight);
            Assert.AreEqual(mesh.vertexCount, bakedBaseHeight.Count);
            foreach (var bake in bakedBaseHeight)
                Assert.AreEqual(42.0, bake.y, 1e-4,
                    "data-driven fill-extrusion-height must bake the EVALUATED value per vertex.");

            Object.DestroyImmediate(mesh);
        }

        // ── T2: height factor tracks per-vertex sec(φ) — two discriminators ────────────────────────
        // z=0 (one tile spans the whole globe): a footprint near the north edge sits at high latitude
        // (sec φ ≫ 1); one straddling the equator sits at φ≈0 (sec φ = 1, the value a MISSING factor would
        // also produce — the equator is not a discriminating point on its own). Both builds share the SAME
        // TileId, so a per-TILE-constant implementation (wrong: D3 pins PER-VERTEX) would answer identically
        // for both footprints; only a genuinely per-vertex sec φ tracks the different Y.

        [Test]
        public void ExtrudeUpMagnitude_TracksPerVertexLatitude_NotPerTileConstant()
        {
            var z0 = new TileId { Z = 0, X = 0, Y = 0 };
            var paint = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":10}");

            // High-latitude footprint (~φ=80°, sec≈5.76) — see TileToGeoJob.GeoAt: v≈0.1146 ⇒ y≈469 at extent 4096.
            var highLatFeature = SquareFeature(1800, 400, 100);
            // Equatorial footprint (φ≈0°, sec≈1) — v=0.5 ⇒ y=2048.
            var equatorFeature = SquareFeature(1800, 2000, 100);

            Mesh highLatMesh = TestTileMeshBuilder.BuildFillExtrusion(new[] { highLatFeature }, paint, 0.0, Extent, z0);
            Mesh equatorMesh = TestTileMeshBuilder.BuildFillExtrusion(new[] { equatorFeature }, paint, 0.0, Extent, z0);
            Assert.IsNotNull(highLatMesh); Assert.IsNotNull(equatorMesh);

            double highLatFactor = MaxExtrudeUpMagnitude(highLatMesh);
            double equatorFactor = MaxExtrudeUpMagnitude(equatorMesh);

            var w = TestContext.Out;
            w.WriteLine($"high-latitude extrudeUp magnitude (expect ~5.76): {highLatFactor:0.0000}");
            w.WriteLine($"equatorial extrudeUp magnitude    (expect ~1.00): {equatorFactor:0.0000}");
            w.Flush();

            Assert.That(equatorFactor, Is.EqualTo(1.0).Within(0.05),
                "at the equator sec(φ)=1 — the extrude-up must be un-scaled unit-up.");
            Assert.That(highLatFactor, Is.GreaterThan(3.0),
                "at φ≈80° sec(φ)≈5.76 — a missing/per-tile-constant factor would read ≈1.0 here too.");

            Object.DestroyImmediate(highLatMesh);
            Object.DestroyImmediate(equatorMesh);
        }

        private static double MaxExtrudeUpMagnitude(Mesh mesh)
        {
            var extrudeUpAndT = new List<Vector4>();
            mesh.GetUVs(3, extrudeUpAndT);
            double max = 0.0;
            foreach (var v in extrudeUpAndT)
                max = math.max(max, (double)new Vector3(v.x, v.y, v.z).magnitude);
            return max;
        }

        // ── T6: winding — front faces point OUT, under both Mercator and the globe ─────────────────
        // Mirrors GlobeFillWindingTests.WindingSign: tally sign(dot(cross(edges), lighting-normal)) across
        // every triangle. An ABSOLUTE +1 check (not merely globe==Mercator) — see that file's comment for
        // why a relative-only check can pass while both sides are inverted.

        /// <summary>
        /// Tallies sign(dot(face-normal, lighting-normal)) over triangles whose FIRST vertex index falls in
        /// <c>[minVertex, maxVertexExclusive)</c> — lets the caller isolate roof triangles (vertex indices
        /// 0..3 for the square fixture: earcut always emits the roof first) from wall triangles (4..19),
        /// so a failure names WHICH side is inverted rather than an ambiguous combined tally.
        /// </summary>
        private static (int sign, double uniformity, int counted) WindingSign(Mesh mesh, int minVertex, int maxVertexExclusive)
        {
            // Height is extruded in the VERTEX SHADER (D1: mesh built once), so a wall quad is degenerate in
            // stored object space — floor(t=0) and roof(t=1) coincide in Position. Measuring winding off raw
            // mesh.vertices gives a zero-area cross for every wall triangle (counted=0). Reconstruct the
            // post-shader position (footprint + extrudeUp·elevation) first. Winding sign is invariant to a
            // positive height scale, so unit elevation (t itself) suffices; the roof, lifted uniformly, keeps
            // its horizontal winding, and the walls gain their vertical extent.
            Vector3[] raw = mesh.vertices;
            Vector3[] n = mesh.normals;
            int[] t = mesh.triangles;
            var uv3 = new List<Vector4>();
            mesh.GetUVs(3, uv3); // TEXCOORD3 = (extrudeUp.xyz, t)
            var v = new Vector3[raw.Length];
            for (int j = 0; j < raw.Length; j++)
            {
                Vector4 e = uv3[j];
                v[j] = raw[j] + new Vector3(e.x, e.y, e.z) * e.w;
            }
            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                if (t[i] < minVertex || t[i] >= maxVertexExclusive) continue;
                float3 va = v[t[i]], vb = v[t[i + 1]], vc = v[t[i + 2]];
                float3 g = math.cross(vb - va, vc - va);
                float3 nn = n[t[i]];
                float gm = math.length(g), nm = math.length(nn);
                if (gm <= 1e-12f || nm <= 1e-12f) continue;
                float cos = math.dot(g, nn) / (gm * nm);
                if (math.abs(cos) < 0.3f) continue; // near edge-on — skip as noise
                if (cos > 0f) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate triangles to measure winding on.");
            int sign = pos >= neg ? 1 : -1;
            return (sign, (double)math.max(pos, neg) / counted, counted);
        }

        private static void AssertRoofAndWallWindOut(Mesh mesh, string label)
        {
            // Wall vertex count is fixture-solid regardless of projection: walls never subdivide (the
            // builder's "Globe wall chording" note), so exactly 4 edges * 4 verts = 16 wall vertices,
            // appended after the roof. The roof itself MAY subdivide on the globe (more than 4 vertices) —
            // computing the split point from the total (rather than hardcoding roof=4) stays correct either way.
            const int wallVertexCount = 16;
            int roofVertexCount = mesh.vertexCount - wallVertexCount;
            Assert.Greater(roofVertexCount, 0, "fixture-shape assumption violated: fewer than 16 wall vertices.");

            // Independent, handedness-free check that the wall LIGHTING normal ('outward') actually points
            // away from the building interior. Without this, the sign test below is relative-wearing-absolute:
            // 'outward' is DERIVED from edgeDir, so dot(face-normal, outward)==+1 can pass with BOTH the
            // normal and the geometry inverted (the exact hazard the roof avoids because +up is unambiguous).
            // The convex square's footprint centroid is knowable, so we ground the direction on the fixture.
            AssertWallNormalsPointOutward(mesh, roofVertexCount, label);

            var (roofSign, roofUnif, roofN) = WindingSign(mesh, 0, roofVertexCount);
            var (wallSign, wallUnif, wallN) = WindingSign(mesh, roofVertexCount, mesh.vertexCount);

            TestContext.Out.WriteLine($"{label} roof: sign={roofSign} uniformity={roofUnif:0.0000} triangles={roofN}");
            TestContext.Out.WriteLine($"{label} wall: sign={wallSign} uniformity={wallUnif:0.0000} triangles={wallN}");

            Assert.Greater(roofUnif, 0.9, $"{label}: roof winding is not uniform.");
            Assert.Greater(wallUnif, 0.9, $"{label}: wall winding is not uniform.");

            Assert.AreEqual(1, roofSign,
                $"{label}: roof front face must point OUT (Unity-front under stock Cull Back). The roof " +
                "reuses StyledFillTileBuilder's already-calibrated 2↔3 index swap, so this failing would be " +
                "surprising — check FillExtrusionStreamWriteJob (StyledFillExtrusionTileBuilder.WriteJob.cs) first.");
            Assert.AreEqual(1, wallSign,
                $"{label}: wall front face must point OUT (Unity-front under stock Cull Back). With the outward " +
                "normal independently confirmed above, this is an absolute winding check: REMEDY is to reverse " +
                "the wall TRIANGLE index order in StyledFillExtrusionTileBuilder.WriteWalls (the two Add-index " +
                "triples). NOTE the A/B vertex swap is a no-op here — it negates the face normal and 'outward' " +
                "together, leaving cos invariant.");
        }

        /// <summary>
        /// Asserts each wall quad's stored lighting normal points AWAY from the footprint centroid — a
        /// fixture-grounded, handedness-free proof that 'outward' is genuinely outward, so the sign tooth
        /// downstream is a real absolute check rather than a self-referential one.
        /// </summary>
        private static void AssertWallNormalsPointOutward(Mesh mesh, int roofVertexCount, string label)
        {
            Vector3[] p = mesh.vertices;
            Vector3[] n = mesh.normals;

            // Footprint centroid: mean of the roof-cap vertices (the exterior ring of the convex square).
            Vector3 centroid = Vector3.zero;
            for (int j = 0; j < roofVertexCount; j++) centroid += p[j];
            centroid /= roofVertexCount;

            // Walls are groups of 4: floorA(+0), floorB(+1), roofB(+2), roofA(+3); all 4 share one 'outward'.
            for (int b = roofVertexCount; b + 3 < mesh.vertexCount; b += 4)
            {
                Vector3 outward = n[b];
                Vector3 edgeMid = (p[b + 0] + p[b + 1]) * 0.5f; // floorA, floorB — the wall's base edge
                float projected = Vector3.Dot(outward, edgeMid - centroid);
                Assert.Greater(projected, 0f,
                    $"{label}: wall quad at vertex {b} has an INWARD lighting normal " +
                    $"(dot(outward, mid-centroid)={projected:0.000}). Fix 'outward' in WriteWalls (negate the " +
                    "cross, or swap its operands) — do NOT touch the triangle order for this failure.");
            }
        }

        [Test]
        public void Winding_FrontFacesPointOut_Mercator()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50}");
            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile);
            Assert.IsNotNull(mesh);
            Assert.AreEqual(20, mesh.vertexCount, "fixture-shape assumption: roof(4)+walls(16)=20 for a hole-less square.");

            AssertRoofAndWallWindOut(mesh, "Mercator");

            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void Winding_FrontFacesPointOut_Globe()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50}");
            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(
                new[] { feature }, paint, 0.0, Extent, ModerateTile, new SphericalProjection());
            Assert.IsNotNull(mesh);
            // No exact vertexCount assertion here (unlike the Mercator test): the globe roof MAY subdivide
            // (GlobeFillSubdivideDispatch), so its vertex count is not pinned to 4 — only the 16 wall
            // vertices are fixture-solid (see AssertRoofAndWallWindOut).

            AssertRoofAndWallWindOut(mesh, "Globe");

            Object.DestroyImmediate(mesh);
        }
    }
}
