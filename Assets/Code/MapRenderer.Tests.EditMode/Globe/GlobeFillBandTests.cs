// GlobeFillBandTests.cs — the boundary band through curvature subdivision.
//
// The band node runs UPSTREAM of GlobeFillSubdivideJob, so every band quad is subdivided along with the
// interior it borders. That only works because a band quad is DEGENERATE in tile space: its outer vertices
// sit on their inner twin's coordinate, so the quad's long edges have the same two endpoints as the
// interior boundary edge, compute the identical subdivision mark, and split at the identical midpoint —
// conforming, with no connectivity and no T-junction. Its zero-length radial edges subtend no angle and are
// never marked. So does the diagonal, the two triangles' shared edge: same tile-space endpoints, same mark,
// same midpoint — and that midpoint's side is 0.5, the only place a lerped coverage coordinate appears. That is the claim these tests measure, on the job, before any wiring or rendering.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using UnityEngine;
using MapRenderer.Jobs.Fill;

namespace MapRenderer.Tests.Globe
{
    [TestFixture]
    public class GlobeFillBandTests
    {
        private const double Extent = 4096.0;

        /// <summary>A whole-world tile, so the shared edge subtends far more than the ~3 degree split
        /// threshold and the subdivider genuinely recurses. A tile that never splits would make every
        /// assertion below vacuous.</summary>
        private static readonly TileId WholeWorld = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>The shared edge runs along <c>x = 0</c>, i.e. in LATITUDE, from +85 degrees to -85. The
        /// tile's <c>y = 0</c> edge looks like the obvious choice and is useless: on a z0 tile it runs from
        /// longitude -180 to +180, which is the SAME meridian, so both endpoints share a surface up, the
        /// edge subtends zero angle and is never marked. Measured, not assumed — the first form of this
        /// fixture used that edge and its own non-vacuity precondition caught it.</summary>
        private const double SharedEdgeX = 0.0;

        /// <summary>The outward miter every band outer vertex in these fixtures carries — unit length, so a
        /// vertex's expected <c>|band.xy|</c> is numerically its own <c>side</c>.</summary>
        private static readonly float3 OuterBand = new float3(0f, -1f, 1f);

        /// <summary>One subdivision run's output, disposed by the caller.</summary>
        private readonly struct Run : System.IDisposable
        {
            /// <summary>The subdivided vertices, in traversal order.</summary>
            public readonly NativeList<GlobeFillVertex> Vertices;

            /// <param name="vertices">The subdivided vertices.</param>
            /// <param name="indices">The sequential index list, held only so it can be disposed.</param>
            public Run(NativeList<GlobeFillVertex> vertices, NativeList<int> indices)
            {
                Vertices = vertices;
                _indices = indices;
            }

            private readonly NativeList<int> _indices;

            public void Dispose() { Vertices.Dispose(); _indices.Dispose(); }
        }

        /// <summary>Subdivides one hand-built vertex/triangle set through the production dispatcher.</summary>
        /// <param name="tileVerts">Tile-space vertices.</param>
        /// <param name="bands">The band attribute of each vertex, same length as <paramref name="tileVerts"/>.</param>
        /// <param name="triangles">Triangle indices into <paramref name="tileVerts"/>.</param>
        /// <returns>The completed run.</returns>
        private static Run Subdivide(double2[] tileVerts, float3[] bands, int[] triangles)
        {
            var verts = new NativeList<double2>(tileVerts.Length, Allocator.Persistent);
            var band  = new NativeList<float3>(bands.Length, Allocator.Persistent);
            var tris  = new NativeList<int>(triangles.Length, Allocator.Persistent);
            var feat  = new NativeList<int>(tileVerts.Length, Allocator.Persistent);
            foreach (double2 v in tileVerts) verts.Add(v);
            foreach (float3 b in bands) band.Add(b);
            foreach (int t in triangles) tris.Add(t);
            for (int i = 0; i < tileVerts.Length; i++) feat.Add(0);

            var outVerts = new NativeList<GlobeFillVertex>(256, Allocator.Persistent);
            var outIndices = new NativeList<int>(256, Allocator.Persistent);
            JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                new SphericalProjection(), verts, tris, feat, band,
                WholeWorld, Extent, double3.zero,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad,
                GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices,
                GlobeFillSubdivideDispatch.DefaultMaxTotalVertices,
                outVerts, outIndices, default);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            verts.Dispose(); band.Dispose(); tris.Dispose(); feat.Dispose();
            return new Run(outVerts, outIndices);
        }

        /// <summary>The interior triangle alone — the reference arm.</summary>
        /// <returns>Its subdivision.</returns>
        private static Run InteriorOnly() => Subdivide(
            new[] { new double2(0, 0), new double2(0, Extent), new double2(Extent, Extent) },
            new[] { float3.zero, float3.zero, float3.zero },
            new[] { 0, 1, 2 });

        /// <summary>The same interior triangle plus one band quad on its <c>x = 0</c> edge, in the layout
        /// <c>FillBandJob</c> emits: inner and outer at the SAME tile coordinate, inner carrying
        /// <c>(0,0,0)</c>, and the two triangles <c>(inner_i, outer_i, outer_j)</c> /
        /// <c>(inner_i, outer_j, inner_j)</c>.</summary>
        /// <returns>Its subdivision.</returns>
        private static Run InteriorPlusBand() => Subdivide(
            new[]
            {
                new double2(0, 0), new double2(0, Extent), new double2(Extent, Extent), // interior
                new double2(0, 0), new double2(0, 0),                                   // band inner/outer @ P0
                new double2(0, Extent), new double2(0, Extent),                         // band inner/outer @ P1
            },
            new[] { float3.zero, float3.zero, float3.zero, float3.zero, OuterBand, float3.zero, OuterBand },
            new[] { 0, 1, 2, /* quad */ 3, 4, 6, 3, 6, 5 });

        // ── The refusal gate: the interior is untouched, and the band conforms to it ────────────────────

        /// <summary>
        /// Subdividing the interior triangle produces bit-identical output whether or not band quads are in
        /// the same input — the band appends, it never perturbs. And the band's own share of the output
        /// splits the shared edge at exactly the same tile coordinates the interior does, which is the
        /// conformance claim that lets the band ride through subdivision at all.
        ///
        /// <para>RED-verify: reverse the band quad's two triangles into non-degenerate tile positions (give
        /// the outer vertices a real tile offset) and the shared-edge sets diverge; the split points stop
        /// agreeing because the marks stop being computed from the same endpoints.</para>
        /// </summary>
        [Test]
        public void ABandQuadSplitsTheSharedEdgeExactlyWhereTheInteriorDoes()
        {
            using Run reference = InteriorOnly();
            using Run banded = InteriorPlusBand();

            Assert.Greater(reference.Vertices.Length, 3,
                "precondition: the whole-world tile must actually subdivide, or every assertion here is " +
                "vacuous.");
            Assert.Greater(banded.Vertices.Length, reference.Vertices.Length,
                "the band quad must contribute vertices of its own.");

            // The subdivider drains its stack per source triangle, so the interior triangle's whole subtree
            // is a PREFIX of the banded run's output.
            for (int i = 0; i < reference.Vertices.Length; i++)
            {
                GlobeFillVertex a = reference.Vertices[i], b = banded.Vertices[i];
                Assert.AreEqual(a.Tile.x, b.Tile.x, 0.0, $"interior vertex {i}: Tile.x must be bit-identical");
                Assert.AreEqual(a.Tile.y, b.Tile.y, 0.0, $"interior vertex {i}: Tile.y must be bit-identical");
                Assert.AreEqual(a.World.x, b.World.x, 0.0, $"interior vertex {i}: World.x must be bit-identical");
                Assert.AreEqual(0.0, b.Band.z, 0.0,
                    $"interior vertex {i} carries a band coordinate. The interior is never displaced.");
            }

            // Conformance: the split points along the shared x = 0 edge, taken from the interior's share and
            // from the band's share, must be the same set.
            SortedSet<double> interiorSplits = SharedEdgeSplits(reference.Vertices, 0, reference.Vertices.Length);
            SortedSet<double> bandSplits = SharedEdgeSplits(banded.Vertices, reference.Vertices.Length, banded.Vertices.Length);

            Assert.Greater(interiorSplits.Count, 2,
                "precondition: the shared edge must genuinely split, or the sets below agree trivially.");
            Assert.That(bandSplits, Is.EquivalentTo(interiorSplits),
                "the band quad's split of the shared edge does not match the interior's. A mark is a " +
                "function of an edge's two endpoints alone, and a band quad's long edge has the same two " +
                "endpoints as the interior edge it abuts — so any divergence here is a T-junction, a visible " +
                "crack between the fill and its own band at low zoom.");
        }

        /// <summary>Distinct tile-y coordinates of every vertex on the shared <c>x = 0</c> edge, over one
        /// slice of the output.</summary>
        /// <param name="verts">The subdivided vertices.</param>
        /// <param name="from">First index of the slice.</param>
        /// <param name="to">One past the last index of the slice.</param>
        /// <returns>The distinct y coordinates found.</returns>
        private static SortedSet<double> SharedEdgeSplits(NativeList<GlobeFillVertex> verts, int from, int to)
        {
            var ys = new SortedSet<double>();
            for (int i = from; i < to; i++)
                if (verts[i].Tile.x == SharedEdgeX) ys.Add(verts[i].Tile.y);
            return ys;
        }

        // ── The band does not perturb the interior's subdivision ───────────────────────────────────────

        /// <summary>Builds the z0 countries fixture on the sphere, with or without the boundary band, and
        /// returns the vertices of its INTERIOR triangles only, in triangle order.
        ///
        /// <para><b>The interior/band discriminator is per-TRIANGLE, and it is exact.</b> A band leaf
        /// triangle always retains at least one vertex with <c>side &gt; 0</c>: <c>side</c> is affine over a
        /// source triangle, so its zero set is a LINE (the band's inner edge), and a sub-triangle with all
        /// three vertices on a line would be degenerate — subdivision produces none. So "all three vertices
        /// carry side 0" identifies interior triangles with no false positives.</para></summary>
        /// <param name="suppressBand">True to build with no band geometry at all.</param>
        /// <param name="interior">Vertices of the interior triangles, in triangle order.</param>
        /// <param name="totalVertices">The whole mesh's vertex count, band included.</param>
        /// <param name="bounds">The built mesh's bounds.</param>
        private static void BuildGlobeFixture(
            bool suppressBand, out Vector3[] interior, out int totalVertices, out Bounds bounds)
        {
            var (go, material) = FillSceneHelper.BuildFillGo(
                projection: new SphericalProjection(), suppressBoundaryBand: suppressBand);
            try
            {
                Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                var band = new List<Vector3>();
                mesh.GetUVs(3, band);

                var kept = new List<Vector3>(vertices.Length);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (band[a].z != 0f || band[b].z != 0f || band[c].z != 0f) continue;
                    kept.Add(vertices[a]); kept.Add(vertices[b]); kept.Add(vertices[c]);
                }

                interior = kept.ToArray();
                totalVertices = mesh.vertexCount;
                bounds = mesh.bounds;
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The band changes nothing about the interior, on the real z0 countries fixture: the interior
        /// triangles' vertex stream is IDENTICAL, element for element, with and without the band.
        ///
        /// <para><b>Why this compares interior-to-interior and not stream-to-stream.</b> Subdivision
        /// re-emits every vertex in traversal order and <c>FillBandJob</c> interleaves each feature's band
        /// triangles after that feature's own interior triangles, so the two whole streams are interleaved,
        /// not nested. An in-order positional match over the raw streams would be a SUBSEQUENCE test, and a
        /// band vertex sits on a ring vertex — a position the interior also carries — so it could advance
        /// the match pointer and pass for the wrong reason. Filtering to interior triangles first (see
        /// <see cref="BuildGlobeFixture"/> for why that discriminator is exact) makes this a plain equality.
        /// Do not restore the subsequence form.</para>
        ///
        /// <para><b>Why this is stronger than a budget check.</b> The subdivider's only cross-triangle
        /// coupling is its budget test, so this property is what a correct budget split BUYS; asserting it
        /// directly holds no matter where any ceiling later sits. It is also the property the rendered
        /// six-pixel failure violated: a shared budget let band vertices starve the interior, triangles past
        /// the cutoff emitted flat, and a thin feature moved off the pixels it had covered.</para>
        ///
        /// <para>RED-verify: point <c>GlobeFillSubdivideJob</c>'s <c>overBudget</c> back at
        /// <c>OutVerts.Length &gt;= InteriorBudget</c> (the shared budget this replaced) and it fails,
        /// naming the first interior vertex that moved.</para>
        /// </summary>
        [Test]
        public void TheBandLeavesTheCurvedArmsInteriorAndBoundsAlone()
        {
            BuildGlobeFixture(true, out Vector3[] hard, out int hardTotal, out Bounds hardBounds);
            BuildGlobeFixture(false, out Vector3[] banded, out int bandedTotal, out Bounds bandedBounds);

            TestContext.WriteLine(
                $"[curved arm] band-free total={hardTotal} banded total={bandedTotal} " +
                $"interior band-free={hard.Length} interior banded={banded.Length} " +
                $"interior budget={GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices} " +
                $"total ceiling={GlobeFillSubdivideDispatch.DefaultMaxTotalVertices}");

            Assert.Greater(bandedTotal, hardTotal,
                "precondition: the banded build must actually carry band geometry.");
            Assert.Greater(banded.Length, 0, "precondition: the filter must find interior triangles.");
            Assert.Less(bandedTotal, GlobeFillSubdivideDispatch.DefaultMaxTotalVertices,
                $"the banded curved build ({bandedTotal}) reached the TOTAL allocation ceiling. That ceiling " +
                "is a backstop, not a working limit; hitting it on the shipped fixture means it is sized " +
                "wrong or the arm's cost has changed.");

            // The band's one-pixel displacement happens in the VERTEX SHADER, so a band vertex sits on its
            // ring vertex CPU-side and the mesh bounds cannot legitimately grow. If they do, the displacement
            // has moved out of the shader and the mechanism has changed underneath us — and any fixture that
            // fits a camera per mesh is then measuring the transform rather than the band.
            Assert.AreEqual(hardBounds, bandedBounds,
                $"the two builds' bounds differ (hard={hardBounds}, banded={bandedBounds}).");

            Assert.AreEqual(hard.Length, banded.Length,
                $"the interior emitted a different number of vertices with the band present " +
                $"({hard.Length} without, {banded.Length} with). Band geometry must never reach geometry " +
                "that is not its own — the subdivider's budget test is the only path by which it can.");
            for (int i = 0; i < hard.Length; i++)
                if (hard[i] != banded[i])
                    Assert.Fail(
                        $"interior vertex {i} moved when the band was added: {hard[i]} -> {banded[i]}. " +
                        "The band starved the interior's subdivision budget and triangles past the cutoff " +
                        "emitted flat.");
        }

        /// <summary>
        /// The curved arm's INTERIOR keeps real headroom under its own subdivision budget on the shipped
        /// fixture. Past that budget the subdivider forces triangles to emit flat regardless of their marks,
        /// so exhausting it is a silent quality regression — the globe's fills facet — with no error and no
        /// failing test anywhere else.
        ///
        /// <para><b>The fence is 95%, and here is the whole trade.</b> Measured when it was written:
        /// 164 535 of 200 000, or 82.3%. It is not set at the measurement, because a fence at the
        /// measurement is one no regression fails. It is not set at 100% either, because past the budget the
        /// subdivider emits flat regardless of marks and the globe facets SILENTLY — the fence has to fire
        /// while there is still something to do about it.
        /// <b>What it will NOT catch: interior growth under ~25 500 vertices passes silently.</b> That is the
        /// cost of 95% over a tighter 90%, stated so the number does not read as arbitrary and so the next
        /// reader moves it deliberately or not at all.</para>
        ///
        /// <para>Getting 164 535 down is <b>UMR-103</b>'s job, not this fence's — this one only stops it
        /// getting worse. The claim used to live as prose in <c>DefaultMaxInteriorVertices</c>' own doc
        /// ("never reached in production"), where it was already 18% from false and nothing was measuring
        /// it.</para>
        /// </summary>
        [Test]
        public void TheCurvedArmsInteriorKeepsHeadroomUnderItsBudget()
        {
            BuildGlobeFixture(true, out Vector3[] hard, out _, out _);

            const double fence = 0.95;
            int ceiling = (int)(GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices * fence);
            TestContext.WriteLine($"[curved arm] band-free interior={hard.Length} fence={ceiling}");
            Assert.Less(hard.Length, ceiling,
                $"the curved arm's band-free interior is {hard.Length} vertices, past {fence:P0} of its " +
                $"{GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices} subdivision budget — it measured " +
                "164535 (82.3%) when this fence was written, so it has grown since. Past the budget " +
                "itself the subdivider emits flat and the globe's fills facet, silently. Raising the budget " +
                "to clear this fence is only correct if the extra peak allocation is affordable — it is not " +
                "a way to make the fence pass. Bringing the count itself down is tracked as UMR-103.");
        }

        // ── The attribute survives the lerp, and stays a displacement times a coverage coordinate ──────

        /// <summary>
        /// Across every subdivided band vertex the two halves of the attribute stay consistent:
        /// <c>|band.xy| == side</c> (these fixtures use a unit miter), <c>side</c> stays inside
        /// <c>[0,1]</c>, and at least one vertex lands STRICTLY between the two — the midpoint of a split
        /// inner→outer edge, which is what proves the subdivider interpolates the attribute rather than
        /// dropping or duplicating it.
        ///
        /// <para>The three assertions are not redundant. Dropping the band from the midpoint construction
        /// entirely leaves every vertex at <c>side</c> 0 or 1 with <c>|xy|</c> to match, so the ratio
        /// assertion stays GREEN and only the strictly-between one reds. Interpolating only one half reds
        /// the ratio.</para>
        ///
        /// <para>RED-verify: make the midpoint take one endpoint's band instead of their average (only the
        /// strictly-between assertion reds), then make it average only <c>xy</c> and take <c>z</c> from an
        /// endpoint (the ratio assertion reds).</para>
        /// </summary>
        [Test]
        public void SubdividingABandEdgeInterpolatesBothHalvesOfTheAttribute()
        {
            using Run banded = InteriorPlusBand();

            int strictlyBetween = 0;
            for (int i = 0; i < banded.Vertices.Length; i++)
            {
                float3 band = banded.Vertices[i].Band;
                Assert.IsFalse(math.any(math.isnan(band)), $"vertex {i}: the band attribute must never be NaN.");
                Assert.That(band.z, Is.InRange(0.0, 1.0),
                    $"vertex {i}: side must stay inside [0,1] — the band is one pixel wide and that is not " +
                    "a knob a midpoint may widen.");
                Assert.AreEqual(band.z, math.length(band.xy), 1e-12,
                    $"vertex {i}: |band.xy| must track side. The displacement and the coverage coordinate " +
                    "are interpolated together; a midpoint that lerps one and not the other puts the ramp " +
                    "somewhere other than where its geometry is.");
                if (band.z > 1e-9 && band.z < 1.0 - 1e-9) strictlyBetween++;
            }

            Assert.Greater(strictlyBetween, 0,
                "no subdivided vertex carries a side strictly between 0 and 1, so the subdivider is not " +
                "interpolating the band at all — it is copying an endpoint's value. The band's inner→outer " +
                "edges DO get split on this fixture; a run with only 0s and 1s means the attribute was " +
                "dropped from the midpoint construction.");
        }
    }
}
