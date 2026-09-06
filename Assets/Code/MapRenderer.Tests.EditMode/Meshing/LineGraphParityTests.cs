// job-scheduling-design.md §8 stage 5 (line graph) — tooth (a): ribbon parity, per ring, over the corpus,
// against a FROZEN capture of the managed StyledLineTileBuilder.WriteMeshData arm taken at ORDINAL ZERO
// (A0.4, commit fcf79f75, before any production edit — provenance recorded in the stage's own devloop log,
// stage-line-graph-goldens-capture-fcf79f75.txt).
// Not a live comparison against WriteMeshData: Group B repoints that method at this same graph, which would
// make a live comparison self-referential.
//
// Step 1 (this file's non-negotiable gate): the per-ring SUBDIVIDED POINT COUNT, exact, element for element
// — a quantiser (LineCurvatureSubdivision.SegmentSteps), so a 1-ULP input difference across a ceil boundary
// can shift every downstream vertex. Compared BEFORE any vertex.
// Step 2: Indices and Stream3 (colour+widthScale) as frozen whole-stream SHA-256 digests (structurally
// independent of the projection). Stream0/1/2 (position, normal, across, side, distanceAlong) compared
// per-vertex, per COMPONENT CLASS — Position/Normal/Side bit-exact, Across/DistanceAlong each bound on
// its own measured mechanism — never one blanket ceiling; see each Assert call's own comment below for
// which hazard applies to which class and why.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using LineStyleLayer = MapRenderer.Core.Style.Line.StyleLayer;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Projection;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;
using MapRenderer.Unity.Rendering.Meshing;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class LineGraphParityTests
    {
        [TestCase("boundary-6-34-21.pbf.bytes", "z6", 6, 34, 21, "WebMercator")]
        [TestCase("boundary-6-34-21.pbf.bytes", "z6", 6, 34, 21, "Spherical")]
        [TestCase("boundary-9-274-168.pbf.bytes", "z9", 9, 274, 168, "WebMercator")]
        [TestCase("boundary-9-274-168.pbf.bytes", "z9", 9, 274, 168, "Spherical")]
        public void LineMeshGraph_MatchesTheCapturedManagedOracle_StreamForStream(
            string fixture, string tag, int z, int x, int y, string label)
        {
            var id = new TileId { Z = z, X = x, Y = y };
            IProjection projection = label == "Spherical" ? (IProjection)new SphericalProjection() : new WebMercatorProjection();

            StyleDocument style = StyleParser.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json")));
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            Assert.IsNotNull(mvtLayer, "boundary_3's source-layer must resolve in this fixture");
            var selected = TestTileMeshBuilder.Select(layer, mvtLayer, z);
            Assert.Greater(selected.Count, 0, "expected boundary_3 line features in this tile");

            double3 origin = TileRenderOrigin.Project(id, projection);
            LayerInput input = StyledLineTileBuilder.BuildLayerInput(
                selected, mvtLayer.Geometry, layer.Paint, layer.Layout, z, origin,
                out NativeArray<Vector4> featureColors, out NativeArray<float> featureWidths, projection);
            Assert.IsTrue(input.FeatureSelected.IsCreated, "precondition: real line geometry must be selected");

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value, "precondition: the measure must not fault");
                Assert.Greater(output.Vertices.Length, 0, "precondition: the graph must have produced real geometry");

                JsonValue golden = LoadGolden(tag, label);
                int goldenTotal = golden.GetInt("totalVertexCount");
                int goldenIndexCount = golden.GetInt("indexCount");

                // ── Step 1: the per-ring subdivided point count, exact — gates everything after it. ────
                List<int> graphRingCounts = ComputeGraphRingSubdivideCounts(mvtLayer.Geometry, input, projection);
                List<int> goldenRingCounts = new List<int>();
                foreach (JsonValue v in golden.Get("ringSubdividedPointCounts").Items) goldenRingCounts.Add(v.AsInt());

                Assert.AreEqual(goldenRingCounts.Count, graphRingCounts.Count,
                    $"[{tag}/{label}] ring count disagrees with the captured oracle — a topology change, not a float question.");
                for (int r = 0; r < goldenRingCounts.Count; r++)
                    Assert.AreEqual(goldenRingCounts[r], graphRingCounts[r],
                        $"[{tag}/{label}] ring {r}'s SUBDIVIDED POINT COUNT diverges from the captured oracle " +
                        $"(golden={goldenRingCounts[r]}, graph={graphRingCounts[r]}) — a quantisation mismatch " +
                        "compares different geometry, not different rounding; stop here, do not compare vertices.");

                Assert.AreEqual(goldenTotal, output.Vertices.Length,
                    $"[{tag}/{label}] total vertex count disagrees with the captured oracle.");
                Assert.AreEqual(goldenIndexCount, output.Indices.Length,
                    $"[{tag}/{label}] index count disagrees with the captured oracle.");

                // ── Step 2a: Indices and Stream3 — frozen whole-stream digests, bit-exact. ───────────────
                var idxBytes = new List<byte>();
                for (int i = 0; i < output.Indices.Length; i++) idxBytes.AddRange(BitConverter.GetBytes(output.Indices[i]));
                var stream3Bytes = new List<byte>();
                for (int i = 0; i < output.Vertices.Length; i++)
                {
                    int f = output.VertexFeatureIdx[i];
                    Vector4 c = featureColors[f];
                    float widthScale = output.Vertices[i].WidthScale * featureWidths[f];
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.x));
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.y));
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.z));
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.w));
                    stream3Bytes.AddRange(BitConverter.GetBytes(widthScale));
                }
                Assert.AreEqual(golden.GetString("indicesDigest"), Sha256(idxBytes),
                    $"[{tag}/{label}] Indices diverge from the captured oracle — a real regression, not a re-bake candidate.");
                // Re-captured 2026-09-07 (UMR-96): these tiles style a CONSTANT line-color, which is no
                // longer baked here — it rides _BaseColor and the vertex carries white. Unlike the Indices
                // digest above, this one is therefore NOT "never a re-bake candidate": it is a live oracle
                // for the opposite fact, and reading a styled colour means the bake came back.
                Assert.AreEqual(golden.GetString("stream3Digest"), Sha256(stream3Bytes),
                    $"[{tag}/{label}] Stream3 (colour+widthScale) diverges from the captured oracle. This layer's line-color is CONSTANT, so the colour components must be the WHITE identity; a styled colour here means the constant-colour vertex bake was re-introduced and the layer renders colour-squared.");

                // ── Step 2b: Stream0/1/2, per-vertex, per-COMPONENT-CLASS, each bound on its own mechanism.
                // No single ceiling: the five classes below are not one phenomenon with a spread — each has
                // a different (or absent) numerical hazard, established by reading RibbonJob/
                // ProjectPointsJob, not by curve-fitting the observed numbers. See each Assert call's own
                // comment for the mechanism — Position/Normal/Side are bit-exact (no hazard), Across/
                // DistanceAlong each get an explicit measured-plus-margin ceiling.
                uint[] posHex = ParseHexArray(golden.GetString("positionHex"));
                uint[] normHex = ParseHexArray(golden.GetString("normalHex"));
                uint[] acrossHex = ParseHexArray(golden.GetString("acrossHex"));
                uint[] sideDistHex = ParseHexArray(golden.GetString("sideDistHex"));

                // Asserted BEFORE indexing below — a truncated/malformed golden must fail here, with a
                // message naming which array and by how much, not as an opaque IndexOutOfRangeException
                // from inside the per-vertex loop.
                int n = output.Vertices.Length;
                Assert.AreEqual(n * 3, posHex.Length, $"[{tag}/{label}] positionHex length != 3 * vertexCount.");
                Assert.AreEqual(n * 3, normHex.Length, $"[{tag}/{label}] normalHex length != 3 * vertexCount.");
                Assert.AreEqual(n * 3, acrossHex.Length, $"[{tag}/{label}] acrossHex length != 3 * vertexCount.");
                Assert.AreEqual(n * 2, sideDistHex.Length, $"[{tag}/{label}] sideDistHex length != 2 * vertexCount.");

                var posMax = new MaxUlp(); var normMax = new MaxUlp(); var acrossMax = new MaxUlp();
                var sideMax = new MaxUlp(); var distMax = new MaxUlp();
                for (int i = 0; i < output.Vertices.Length; i++)
                {
                    LineRibbonVertex rv = output.Vertices[i];
                    float3 pos = (float3)rv.Position, up = (float3)rv.Up, across = (float3)rv.Across;
                    posMax.Observe(Ulp(pos.x, posHex[i * 3 + 0]), i); posMax.Observe(Ulp(pos.y, posHex[i * 3 + 1]), i); posMax.Observe(Ulp(pos.z, posHex[i * 3 + 2]), i);
                    normMax.Observe(Ulp(up.x, normHex[i * 3 + 0]), i); normMax.Observe(Ulp(up.y, normHex[i * 3 + 1]), i); normMax.Observe(Ulp(up.z, normHex[i * 3 + 2]), i);
                    acrossMax.Observe(Ulp(across.x, acrossHex[i * 3 + 0]), i); acrossMax.Observe(Ulp(across.y, acrossHex[i * 3 + 1]), i); acrossMax.Observe(Ulp(across.z, acrossHex[i * 3 + 2]), i);
                    sideMax.Observe(Ulp(rv.Side, sideDistHex[i * 2 + 0]), i);
                    distMax.Observe(Ulp((float)rv.DistanceAlong, sideDistHex[i * 2 + 1]), i);
                }
                TestContext.Out.WriteLine(
                    $"[{tag}/{label}] max ULP by class: Position={posMax} Normal={normMax} Across={acrossMax} " +
                    $"Side={sideMax} DistanceAlong={distMax}");

                // Normal (Up): NO computed hazard — ProjectPointsJob writes it as a straight trig
                // evaluation/copy (ProjectPointsJob.cs:58, Normals[index] = pp.Up), never a difference of
                // two comparable-magnitude quantities, so there is nothing to AMPLIFY a rounding difference.
                // Reconciling with job-scheduling-design.md §8 stage 5's invariant block, which says Up's
                // raw DOUBLE-precision divergence (2-3 ULP, Spherical) "reaches the narrowed float3
                // un-amplified and un-erased": that line is about the SUBTRACTION mechanism specifically —
                // Up is never origin-subtracted, so it gets neither Position's cancellation penalty NOR its
                // deliberate erasure-by-narrowing. It does NOT claim Up's divergence survives AS float32
                // ULP: a few parts in 2^52 (double ULP at unit magnitude) is itself ~8 orders of magnitude
                // below ONE float32 ULP at that same magnitude (~2^-23) — small enough to vanish under
                // ordinary float32 rounding even with no special-cased erasure mechanism protecting it.
                // MEASURED bit-exact (0 ULP) on all four cases of this corpus confirms exactly that. The
                // bound is bit-exact, not "0 because that's what was observed": a component with no computed
                // hazard degrading at all is itself the signal something changed.
                Assert.AreEqual(0u, normMax.Delta,
                    $"[{tag}/{label}] Normal (Up) exceeds 0 ULP at vertex {normMax.Index} — this component " +
                    "has NO computed hazard (a straight copy/trig evaluation), so ANY divergence is a real " +
                    "regression, not noise.");

                // Side: EVERY assignment site in RibbonJob is a bare literal (+1f/-1f/0f — MakeVertex's
                // callers, RibbonJob.cs). Two literals of the same value are the SAME bit pattern in
                // both the managed and Burst arms; there is no floating computation to diverge. Bit-exact.
                Assert.AreEqual(0u, sideMax.Delta,
                    $"[{tag}/{label}] Side exceeds 0 ULP at vertex {sideMax.Index} — every Side value is a " +
                    "bare literal in production; ANY divergence here is a branch/topology bug, never rounding.");

                // Across carries TWO hazards, not one:
                //   (1) near-singular miter division (1/cosHalf, RibbonJob.TryJoinBisector/
                //       ComputeMiterNormals/NeedsBevel) amplifies ordinary input ULP by a factor that blows
                //       up as a join angle approaches a hairpin — this is the fixture-dependent one,
                //       confirmed by the measurement itself: 1/10/81/1000 ULP across the four cases, a 1000x
                //       spread consistent with "how close the sharpest join on this corpus's fixture came to
                //       a hairpin", not a fixed noise floor.
                //   (2) `across = math.normalize(math.cross(along, up))` (RibbonJob.cs:249) — Burst's
                //       relaxed-math `normalize` does not guarantee managed IEEE rounding on every input,
                //       the SAME mechanism the wall-job stage measured diverging ~1-2 ULP at three call
                //       sites, data-dependently (job-scheduling-design.md §8 stage 5's invariant block).
                //       Contributes a small, roughly-constant few-ULP floor UNDER the miter amplification,
                //       not the 1000x spread itself.
                // AcrossUlpCeiling (4000) is 4x the largest observed (1000, z6/WebMercator) — margin for a
                // sharper join existing than this corpus happens to sample, while still failing hard against
                // a real regression (which, per the near-zero-escape-hatch doc above, showed as BILLIONS of
                // ULP before that hatch existed — six more orders of magnitude past this ceiling).
                Assert.LessOrEqual(acrossMax.Delta, AcrossUlpCeiling,
                    $"[{tag}/{label}] Across exceeds its {AcrossUlpCeiling}-ULP ceiling at vertex {acrossMax.Index} " +
                    "— near-singular miter division and normalize's own relaxed-math divergence are the ONLY " +
                    "hazards this component has; a delta orders of magnitude past this is a different bug, " +
                    "not a sharper join.");

                // DistanceAlong: a per-ring running SUM (RibbonJob.cs cumDist accumulation) is the
                // hazard for this quantity in THEORY — error would grow with the number of terms summed
                // (up to ~660 on this corpus), not with tile/origin magnitude. MEASURED bit-exact (0 ULP) on
                // all four cases: bit-exact is the honest bound, not a margin over a nonzero observation.
                // RECORDED LIMITATION: accumulation drift is a real mechanism this corpus does not exercise
                // — no ring here pushes the running sum far enough for double-vs-Burst rounding to surface
                // in float32. A longer ring than ~660 points could legitimately need slack here; if this
                // assertion ever reds on a larger fixture, that is the mechanism to re-derive a bound from,
                // not evidence this bit-exact bound was wrong to start with.
                Assert.AreEqual(0u, distMax.Delta,
                    $"[{tag}/{label}] DistanceAlong exceeds 0 ULP at vertex {distMax.Index}.");

                // Position: origin-relative cancellation (ProjectPointsJob.cs:57, WorldPositions[index] =
                // pp.World - OriginWorld — a planetary-magnitude, ~6.378e6 m, subtraction) is the textbook
                // hazard for this quantity, and the SAME mechanism the wall-job stage measured for its own
                // Position/Tangent split (job-scheduling-design.md §8 stage 5's invariant block). There it
                // narrows to float32 STRUCTURALLY, not by luck of one fixture: the origin-relative double
                // error at tile-local magnitude (~1e4) is six orders of magnitude below a float32 ULP there,
                // for any tile-local geometry (RTC's whole purpose). MEASURED bit-exact (0 ULP) here too,
                // confirming the same structural erasure applies to line's ribbon Position — bit-exact, not
                // a margin over an observed nonzero.
                Assert.AreEqual(0u, posMax.Delta,
                    $"[{tag}/{label}] Position exceeds 0 ULP at vertex {posMax.Index} — origin-relative " +
                    "cancellation narrows to float32 STRUCTURALLY at tile-local magnitude (see comment above), " +
                    "so ANY divergence here is a formula error, not drift.");
            }
            finally
            {
                output.Dispose();
                if (featureColors.IsCreated) featureColors.Dispose();
                if (featureWidths.IsCreated) featureWidths.Dispose();
            }
        }

        /// <summary>Reproduces <see cref="LineMeshGraph.ScheduleTyped{TProj}"/>'s first half (gather → tile→geo
        /// → project → subdivide) using the SAME production internal jobs, run to completion synchronously, to
        /// read <c>OutRingSubOffsets</c> — a value <see cref="LineGraphOutput"/> does not expose (it is
        /// consumed entirely inside the graph). This is a real exercise of the production ring-gather/subdivide
        /// jobs, not a re-derivation of their math — including the projection step: <c>SrcUp</c> is filled by
        /// <see cref="ProjectionDispatch.Schedule"/> (the real Burst <c>ProjectPointsJob&lt;TProj&gt;</c>), never
        /// the managed <c>IProjection.ProjectPoint</c>, because <c>SrcUp</c> is exactly what
        /// <c>LineCurvatureSubdivision.SegmentSteps</c> quantises on — a managed-arm substitute here could
        /// silently disagree with what the real graph actually computes.</summary>
        private static List<int> ComputeGraphRingSubdivideCounts(
            TileGeometryBuffers geometry, LayerInput input, IProjection projection)
        {
            using var srcTile = new NativeList<double2>(Allocator.Persistent);
            using var ringSrcOffsets = new NativeList<int>(Allocator.Persistent);
            using var ringFeature = new NativeList<int>(Allocator.Persistent);
            using var srcGeo = new NativeList<GeoCoordinate>(Allocator.Persistent);
            using var srcWorld = new NativeList<double3>(Allocator.Persistent);
            using var srcUp = new NativeList<double3>(Allocator.Persistent);

            new RingGatherJob
            {
                Vertices = geometry.Vertices, RingOffsets = geometry.RingOffsets, RingFeatureIdx = geometry.RingFeatureIdx,
                FeatureGeometryType = geometry.FeatureGeometryType, RingCount = geometry.RingCount,
                FeatureSelected = input.FeatureSelected,
                OutSrcTile = srcTile, OutRingSrcOffsets = ringSrcOffsets, OutRingFeature = ringFeature,
                OutSrcGeo = srcGeo, OutSrcWorld = srcWorld, OutSrcUp = srcUp,
            }.Run();

            new TileToGeoJob
            {
                Tile = geometry.Tile, Extent = geometry.Extent,
                TileCoords = srcTile.AsArray(), OutGeo = srcGeo.AsArray(),
            }.Run(srcTile.Length);

            // The REAL Burst-compiled projection kernel (ProjectionDispatch.Schedule → ProjectPointsJob<TProj>),
            // NOT the managed projection.ProjectPoint(...).Up loop this replaced. This matters because
            // SrcUp is exactly SegmentSteps' input, and the managed-vs-Burst boundary is the one place a
            // few ULP of divergence can flip a ceil() and shift the whole quantised count Step 1 gates on —
            // a gate built from the managed arm could agree with the golden while the real graph's counts
            // differ, or red on a divergence the graph never had. `originWorld` is irrelevant here (only
            // affects the discarded World output, never Up) — see RingGatherJob's own doc on why that
            // column is a deliberate dead output. Same Burst kernel, bit-identical, scheduled+completed
            // inline instead of run — the wall-job-graph stage retired the synchronous Run/RunTyped entry
            // points (ProjectionDispatch's own class doc).
            ProjectionDispatch.Schedule(
                projection, double3.zero, srcGeo, srcWorld, srcUp, default).Complete();

            using var subTile = new NativeList<double2>(Allocator.Persistent);
            using var ringSubOffsets = new NativeList<int>(Allocator.Persistent);
            using var subGeo = new NativeList<GeoCoordinate>(Allocator.Persistent);
            using var subWorld = new NativeList<double3>(Allocator.Persistent);
            using var subUp = new NativeList<double3>(Allocator.Persistent);

            new SubdivideJob
            {
                SrcTile = srcTile, RingSrcOffsets = ringSrcOffsets, SrcUp = srcUp,
                MaxRefineAngleRad = projection.MaxRefineAngleRad,
                OutSubTile = subTile, OutRingSubOffsets = ringSubOffsets,
                OutSubGeo = subGeo, OutSubWorld = subWorld, OutSubUp = subUp,
            }.Run();

            var counts = new List<int>();
            for (int r = 0; r < ringSubOffsets.Length - 1; r++)
                counts.Add(ringSubOffsets[r + 1] - ringSubOffsets[r]);
            return counts;
        }

        // Per-class ceiling — argued from the mechanism named at its Assert call site above, then MEASURED
        // on this corpus (all four TestCases) with a stated margin, never derived from the number alone.
        // Recorded per-class rather than one blanket bound (the earlier revision's self-fulfilling
        // 1500-ULP-for-everything, which was 1500x slack on Side/Normal/Position/DistanceAlong — every one
        // of which measures BIT-EXACT here and is asserted so directly at its call site, no constant needed).
        // Across is the ONE class with a margin, because it is the only one with a hazard this corpus
        // actually exercises (near-singular miter division AND relaxed-math normalize divergence — see its
        // own Assert comment): 4x the largest observed (1000, z6/WebMercator).
        private const ulong AcrossUlpCeiling = 4000;

        /// <summary>Total-order IEEE-754 ULP-distance between <paramref name="actual"/> (the domain is
        /// float32, the value AS STORED in the stream — matches production) and a golden hex bit pattern.
        /// Never a raw bit-pattern subtraction, which is wrong across zero/sign.
        ///
        /// <para><b>Near-zero escape hatch, measured, not guessed.</b> A raw ULP distance is the wrong
        /// instrument near zero: float32's exponent shrinks toward the subnormal range there, so two values
        /// a physically negligible ~1e-12 apart (both, in practice, "this axis is orthogonal to the
        /// extrusion direction") can be a BILLION ULP apart. Observed on this exact corpus: every case that
        /// needed this escape hatch was an <c>Across</c> component with BOTH values under 1e-11 in
        /// magnitude — cancellation noise on an axis a nearly axis-aligned segment's extrusion direction has
        /// none of, not a real divergence. <see cref="NearZeroAbs"/> (1e-6) is five orders of magnitude
        /// above what was actually observed, so it cannot swallow a component that should be non-zero at any
        /// physically meaningful scale for this stream (ribbon geometry in tile-local metres).</para></summary>
        private const float NearZeroAbs = 1e-6f;

        private static ulong Ulp(float actual, uint goldenHex)
        {
            float golden = math.asfloat(goldenHex);
            if (math.abs(actual) < NearZeroAbs && math.abs(golden) < NearZeroAbs)
                return 0; // both negligible — see the near-zero escape hatch doc above.
            return UlpDistance(math.asuint(actual), goldenHex);
        }

        private static ulong ToUlpOrder(uint bits) => (bits & 0x80000000U) != 0 ? ~bits : (bits | 0x80000000U);
        private static ulong UlpDistance(uint a, uint b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }

        /// <summary>The running max ULP delta for one component class, plus WHICH vertex it came from — a
        /// max with no index is useless for tracking down a real regression.</summary>
        private struct MaxUlp
        {
            public ulong Delta;
            public int Index;
            public void Observe(ulong delta, int index) { if (delta > Delta) { Delta = delta; Index = index; } }
            public override string ToString() => $"{Delta}@{Index}";
        }

        private static JsonValue LoadGolden(string tag, string label)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", $"line-graphwrite-golden-{tag}-{label}.json");
            Assert.IsTrue(File.Exists(path), $"golden missing: {path}");
            return JsonParser.Parse(File.ReadAllText(path));
        }

        private static uint[] ParseHexArray(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return Array.Empty<uint>();
            string[] parts = csv.Split(',');
            var result = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = Convert.ToUInt32(parts[i], 16);
            return result;
        }

        private static string Sha256(List<byte> bytes)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }
}
