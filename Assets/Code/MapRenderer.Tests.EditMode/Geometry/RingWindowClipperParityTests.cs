// Unity-only by design: this test drives the Burst job over native containers, which the engine-free
// Tools/core-tests project cannot compile. NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Geometry
{
    /// <summary>
    /// T8 — <see cref="RingWindowClipper"/> (managed, <c>MapRenderer.Core</c>) and <see cref="RingClipJob"/>
    /// (Burst) must produce BIT-IDENTICAL vertex sequences.
    ///
    /// <para>The GeoJSON slicer cannot use the job — it must stay engine-free so the whole stack runs in the
    /// ~0.1 s <c>dotnet test</c> loop, which is the point of a source that exists to serve fixtures. That
    /// leaves a second copy of an in-repo algorithm, which this repo normally treats as a smell. This test is
    /// what converts the copy from an UNVERIFIED duplicate into a CHECKED equivalence, and it is also the
    /// retirement path: when the geometry-IR epic lands, the job can delegate to the managed clipper and this
    /// stays green.</para>
    /// </summary>
    [TestFixture]
    public class RingWindowClipperParityTests
    {
        /// <summary>
        /// Both windows the pair is actually driven with: the bare tile <c>[0, 4096]</c>, and the
        /// negative-min BUFFERED window <c>[−64, 4160]</c> that <c>GeoJsonSliceOptions.Window</c> and
        /// <c>TileBufferClip.TryWindow</c> both produce at the standard margin. A window whose minimum is
        /// below zero is the one the slicer uses in production, and comparing only against the bare tile
        /// would leave it unchecked.
        /// </summary>
        private static readonly double2[][] Windows =
        {
            new[] { new double2(0.0, 0.0),     new double2(4096.0, 4096.0) },
            new[] { new double2(-64.0, -64.0), new double2(4160.0, 4160.0) }
        };

        [Test]
        public void T8_ManagedClipperMatchesTheBurstJob_BitForBit()
        {
            foreach (double2[] window in Windows)
                AssertParityOverCorpus(window[0], window[1]);
        }

        private static void AssertParityOverCorpus(double2 clipMin, double2 clipMax)
        {
            List<List<double2>> corpus = BuildCorpus(clipMin, clipMax);
            Assert.That(corpus.Count, Is.GreaterThan(10), "the corpus must be non-trivial");

            Dictionary<int, List<double2>> fromJob = RunJob(corpus, clipMin, clipMax);

            int actuallyClipped = 0;
            string where = $"window [{clipMin.x}, {clipMax.x}]";

            for (int i = 0; i < corpus.Count; i++)
            {
                List<double2> managed = RingWindowClipper.Clip(corpus[i], clipMin, clipMax);

                if (!fromJob.TryGetValue(i, out List<double2> burst))
                {
                    Assert.That(managed, Is.Null,
                        $"{where}, ring {i}: the job dropped it, so the managed clipper must too");
                    continue;
                }

                Assert.That(managed, Is.Not.Null,
                    $"{where}, ring {i}: the job kept it, so the managed clipper must too");
                Assert.That(managed.Count, Is.EqualTo(burst.Count),
                    $"{where}, ring {i}: vertex count differs");

                for (int v = 0; v < burst.Count; v++)
                {
                    Assert.That(managed[v].x, Is.EqualTo(burst[v].x),
                        $"{where}, ring {i}, vertex {v}: x differs bit-for-bit");
                    Assert.That(managed[v].y, Is.EqualTo(burst[v].y),
                        $"{where}, ring {i}, vertex {v}: y differs bit-for-bit");
                }

                if (!SameSequence(managed, corpus[i])) actuallyClipped++;
            }

            // Non-vacuity: without this the test could be comparing two verbatim bbox-fast-path copies and
            // proving nothing whatsoever about the clipping ARITHMETIC. It has to hold PER WINDOW — a corpus
            // that only clips against one of them leaves the other vacuous.
            Assert.That(actuallyClipped, Is.GreaterThanOrEqualTo(5),
                $"{where}: at least five corpus rings must have taken the real Sutherland–Hodgman path");
        }

        private static bool SameSequence(List<double2> a, List<double2> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].x != b[i].x || a[i].y != b[i].y) return false;
            return true;
        }

        /// <summary>Runs the Burst job over the whole corpus at once, keyed by input ring index (the job
        /// carries that through as its per-ring "feature" index).</summary>
        private static Dictionary<int, List<double2>> RunJob(
            List<List<double2>> corpus, double2 clipMin, double2 clipMax)
        {
            int total = 0, longest = 0;
            foreach (List<double2> ring in corpus)
            {
                total += ring.Count;
                longest = math.max(longest, ring.Count);
            }

            var vertices   = new NativeArray<double2>(total, Allocator.Persistent);
            var offsets    = new NativeArray<int>(corpus.Count + 1, Allocator.Persistent);
            var featureIdx = new NativeArray<int>(corpus.Count, Allocator.Persistent);
            var scratchA   = new NativeArray<double2>(longest * RingClipJob.ScratchLengthMultiplier, Allocator.Persistent);
            var scratchB   = new NativeArray<double2>(longest * RingClipJob.ScratchLengthMultiplier, Allocator.Persistent);
            var outVerts   = new NativeList<double2>(total * 4, Allocator.Persistent);
            var outOffsets = new NativeList<int>(corpus.Count + 1, Allocator.Persistent);
            var outFeature = new NativeList<int>(corpus.Count, Allocator.Persistent);

            try
            {
                int cursor = 0;
                for (int i = 0; i < corpus.Count; i++)
                {
                    offsets[i]    = cursor;
                    featureIdx[i] = i;
                    foreach (double2 v in corpus[i]) vertices[cursor++] = v;
                }
                offsets[corpus.Count] = cursor;

                // IR B7: the clip walks a caller-supplied visit order. "Every ring, in decode order" is the
                // identity order, which is what this parity fixture always meant by RingCount.
                var visitOrder = new NativeArray<int>(corpus.Count, Allocator.Persistent);
                for (int i = 0; i < corpus.Count; i++) visitOrder[i] = i;

                new RingClipJob
                {
                    Vertices          = vertices,
                    RingOffsets       = offsets,
                    RingFeatureIdx    = featureIdx,
                    RingVisitOrder    = visitOrder,
                    ClipMin           = clipMin,
                    ClipMax           = clipMax,
                    ScratchA          = scratchA,
                    ScratchB          = scratchB,
                    OutVertices       = outVerts,
                    OutRingOffsets    = outOffsets,
                    OutRingFeatureIdx = outFeature
                }.Run();
                visitOrder.Dispose();

                var result = new Dictionary<int, List<double2>>();
                for (int r = 0; r < outFeature.Length; r++)
                {
                    var ring = new List<double2>();
                    for (int v = outOffsets[r]; v < outOffsets[r + 1]; v++) ring.Add(outVerts[v]);
                    result[outFeature[r]] = ring;
                }
                return result;
            }
            finally
            {
                vertices.Dispose();
                offsets.Dispose();
                featureIdx.Dispose();
                scratchA.Dispose();
                scratchB.Dispose();
                outVerts.Dispose();
                outOffsets.Dispose();
                outFeature.Dispose();
            }
        }

        /// <summary>
        /// The five named cases the tooth requires — wholly inside, straddling one edge, crossing a corner,
        /// wholly outside, and vertices exactly ON the boundary — plus the sub-triangle rings both
        /// implementations special-case, plus a seeded pseudo-random tail whose rings are sized and placed to
        /// land in all of those regimes.
        ///
        /// <para>The absolute cases below are authored against <c>[0, 4096]</c>; driven against the buffered
        /// window they land in different (still useful) regimes, so the boundary-exact cases are rebuilt from
        /// <paramref name="min"/>/<paramref name="max"/> and stay boundary-exact either way.</para>
        /// </summary>
        private static List<List<double2>> BuildCorpus(double2 min, double2 max)
        {
            var corpus = new List<List<double2>>
            {
                Ring(100, 100, 3000, 100, 3000, 2000, 100, 2000),              // wholly inside
                Ring(3000, 1000, 5000, 1000, 5000, 2000, 3000, 2000),          // straddles one edge
                Ring(3500, 3500, 5000, 3500, 5000, 5000, 3500, 5000),          // crosses a corner
                Ring(5000, 5000, 6000, 5000, 6000, 6000, 5000, 6000),          // wholly outside
                Ring(0, 0, 4096, 0, 4096, 4096, 0, 4096),                      // exactly the window
                Ring(-64, -64, 4160, -64, 4160, 4160, -64, 4160),              // encloses the window
                Ring(0, 1000, 2000, 1000, 2000, 2000, 0, 2000),                // one edge ON the boundary
                Ring(-500, 2000, 2000, -500, 4600, 2000, 2000, 4600),          // diamond over all four edges
                Ring(-100, 2000, 200, 2000, 200, 2100),                        // a triangle across an edge
            };

            // A concave subject that passes AROUND a corner — the classic Sutherland–Hodgman degenerate,
            // where the two arms are joined by a zero-width channel along the boundary rather than split.
            corpus.Add(Ring(3500, 1000, 5000, 1000, 5000, 3000, 3500, 3000, 3500, 2500, 4500, 2500,
                            4500, 1500, 3500, 1500));

            // Boundary-exact against whichever window is driving.
            corpus.Add(Ring(min.x, min.y, max.x, min.y, max.x, max.y, min.x, max.y));        // exactly it
            corpus.Add(Ring(min.x, 1000, 2000, 1000, 2000, 2000, min.x, 2000));              // edge on min.x
            corpus.Add(Ring(3000, min.y, max.x + 500, min.y, max.x + 500, 2000, 3000, 2000)); // exits max.x

            // Sub-triangle rings. BOTH implementations run the bbox fast path BEFORE the < 3 drop, so one
            // already inside is copied verbatim while one that is not is dropped — two branches the corpus
            // never reached while every ring in it had three or more vertices.
            corpus.Add(Ring(500, 500, 1500, 900));            // 2 vertices, wholly inside  ⇒ verbatim
            corpus.Add(Ring(500, 500, max.x + 500, 900));     // 2 vertices, straddling     ⇒ dropped
            corpus.Add(Ring(700, 700));                       // 1 vertex,  wholly inside   ⇒ verbatim
            corpus.Add(Ring(max.x + 700, 700));               // 1 vertex,  outside         ⇒ dropped

            // System.Random explicitly: Unity.Mathematics also has a Random, and `using` both makes the
            // bare name ambiguous.
            var random = new System.Random(20260807);
            for (int i = 0; i < 40; i++)
            {
                double cx = random.NextDouble() * 6000.0 - 1000.0;
                double cy = random.NextDouble() * 6000.0 - 1000.0;
                int    n  = 3 + random.Next(6);
                double r  = 200.0 + random.NextDouble() * 2500.0;

                var ring = new List<double2>(n);
                for (int k = 0; k < n; k++)
                {
                    double angle  = 2.0 * math.PI_DBL * k / n;
                    double radius = r * (0.5 + random.NextDouble());
                    // Snap a share of the vertices onto the window edges: an exact-boundary vertex is where
                    // an inclusive test and a rounding step can disagree, so it must be in the corpus.
                    double x = cx + radius * math.cos(angle);
                    double y = cy + radius * math.sin(angle);
                    if (random.Next(6) == 0) x = SnapToNearerEdge(x, min.x, max.x);
                    if (random.Next(6) == 0) y = SnapToNearerEdge(y, min.y, max.y);
                    ring.Add(new double2(x, y));
                }
                corpus.Add(ring);
            }

            return corpus;
        }

        /// <summary>Pulls a coordinate onto whichever window edge it is nearer — the exact-boundary case,
        /// expressed against the window actually driving rather than a hard-coded 4096.</summary>
        private static double SnapToNearerEdge(double c, double lo, double hi)
            => math.abs(c - lo) <= math.abs(c - hi) ? lo : hi;

        private static List<double2> Ring(params double[] xy)
        {
            var ring = new List<double2>(xy.Length / 2);
            for (int i = 0; i < xy.Length; i += 2) ring.Add(new double2(xy[i], xy[i + 1]));
            return ring;
        }
    }
}
