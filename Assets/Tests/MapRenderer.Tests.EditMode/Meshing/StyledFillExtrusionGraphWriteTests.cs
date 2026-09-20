// job-scheduling-design.md §8 stage 4, tooth (a) / B.7 — the extrusion write graph's stream output.
//
// Group B rewrites StyledFillExtrusionTileBuilder.WriteMeshData to itself route through
// FillMeshGraph.Schedule + ScheduleStreamWrite — the SAME path ScheduleWrite (the graph arm) takes. Comparing
// the two live, as this tooth used to, would therefore be a SELF-REFERENTIAL oracle (green forever, proves
// nothing): both arms would literally run the identical code. Six per-stream SHA-256 digests, captured from
// the INDEPENDENT managed WriteMeshData arm BEFORE Group B rewrote it (commit f5e13c19, stage 4 Group A —
// see docs/stage4-groupb-goldens-capture-f5e13c19.txt's SITE3 lines), replace the live comparison as a frozen regression pin
// — same reasoning as R6, same per-stream granularity as N1 (a red names WHICH stream moved).
//
// REVISION (2026-09-04, wall-job stage): Stream0/Stream2 are NO LONGER a single whole-stream hash. The
// wall-job stage moved wall projection off the MANAGED IProjection.ProjectPoint onto the Burst
// ProjectPointsJob<TProj> chain — job-scheduling-design.md §8 stage 5's opening invariant block measured that boundary
// BIT-EXACT for linear quantities, but diverging by a few ULP for anything downstream of a transcendental
// (sin/cos on the globe, atan/sinh for latitude). A single hash cannot express "the roof prefix is
// bit-exact, the wall tail is within a measured tolerance" — a hash is opaque, so ANY byte moving anywhere
// in the stream moves the whole digest, whether the mover is a real regression or one ULP of Burst-vs-
// managed rounding. Stream0 (Position+Normal) and Stream2 (Tangent) — the two streams whose wall half is
// downstream of the projection boundary — are now compared PER-VERTEX against
// Assets/Fixtures/extrusion-graphwrite-golden-{WebMercator,Spherical}.json: the ROOF PREFIX (indices
// [0, roofVertexCount)) bit-exact, since the roof path is completely untouched by this stage; the WALL TAIL
// within a per-component ULP bound MEASURED on this fixture (not assumed from §8 stage 5's opening invariant
// block, whose table bounds the raw double-precision projection output at a DIFFERENT origin/magnitude —
// Normal/Tangent here are NORMALIZED
// DIFFERENCES of two nearby float32 positions, a quantity where cancellation is not safely inferred from
// the source table). Those two golden JSON files were captured by re-running the RETIRED managed WriteWalls
// loop (verbatim, from commit 74a9f604 — the tree just before this stage's production code existed) over
// the SAME roof measure this test already computes; the reconstruction was validated by reproducing this
// file's own frozen whole-stream digests (below) exactly, on all six streams, before any golden byte was
// written. That validation ran once, in a throwaway harness now deleted — GoldenJson_ReproducesFrozenDigests
// below is the ASSERTED, permanent form of the same check: it re-hashes the golden JSON's own per-vertex
// bytes and asserts they still reproduce the frozen Stream0/Stream2 digests, so the whole-stream pin outlives
// the split and a golden byte changing anywhere fails loudly instead of silently drifting from its own proof.
//
// Stream1 (ExtrudeUpAndT+Bake), Stream3 (colour), Indices and Bounds are UNCHANGED — still single whole-
// stream hashes against the frozen constants below. All four are still satisfiable exactly, but NOT because
// they sit outside the projection boundary this stage moved — Extrude's sec-φ factor multiplies a Burst
// TileToGeoJob-derived latitude by a Burst ProjectPointsJob-derived Up, so it IS downstream of it. They stay
// exact for the SAME structural reason Position does: every value here (Extrude's Up component, the AABB
// min/max) lands at tile-local RTC magnitude, where the origin-relative double divergence this stage
// measured is six orders of magnitude below a float32 ULP (docs/job-scheduling-design.md §8 stage 5's
// opening invariant block) — the narrowing erases it. Colour and Indices are structurally independent of the
// projection boundary outright (colour is a straight feature-property copy; indices are ring/topology
// arithmetic). Keeping all four as hard hashes is what still catches an ordering/topology/colour/index
// regression — the split narrows what floats, it does not widen what is allowed to.
//
// Fixture: a plain square feature plus a courtyard feature (a square with a square hole) sharing one
// ModerateTile — the courtyard exercises earcut's hole-bridge path (RingAssemblyJob's shoelace-sign
// classification), which the plain square alone never enters. fill-extrusion-height is DATA-DRIVEN on BOTH
// features (["get","h"]) so BakedBaseHeight.y is observed on the courtyard too, not only on the square.
//
// R3 (plan review): the naive bound "vertex count > N" does NOT discriminate a mis-wound hole ring (which
// silently reads as a SECOND EXTERIOR) from a real courtyard — both produce roughly the same total. The
// graph-side precondition below (Counts[0].HoleCount >= 1) names the branch directly instead of bounding a
// total.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class StyledFillExtrusionGraphWriteTests
    {
        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));

        private static void AppendMoveTo(List<uint> cmds, ref int cx, ref int cy, int x, int y)
        {
            cmds.Add((1u << 3) | 1u);
            cmds.Add(ZigZag(x - cx)); cmds.Add(ZigZag(y - cy));
            cx = x; cy = y;
        }

        private static void AppendLineTo(List<uint> cmds, ref int cx, ref int cy, params (int x, int y)[] pts)
        {
            cmds.Add(((uint)pts.Length << 3) | 2u);
            foreach (var p in pts)
            {
                cmds.Add(ZigZag(p.x - cx)); cmds.Add(ZigZag(p.y - cy));
                cx = p.x; cy = p.y;
            }
        }

        // CW on screen (Y-down) = MVT exterior (RingAssemblyJob.cs:179's own convention) — same corner
        // order StyledFillExtrusionMeshTests' SquareRing uses.
        private static uint[] SquareRing(int x0, int y0, int size)
        {
            var cmds = new List<uint>();
            int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            return cmds.ToArray();
        }

        // Exterior ring identical in shape to SquareRing; the hole ring is wound OPPOSITE (reversed corner
        // order) — RingAssemblyJob classifies a ring by comparing its own shoelace sign against the first
        // (exterior) ring's, so a matching sign would read as a SECOND EXTERIOR, not a hole.
        private static uint[] SquareWithHoleRing(int x0, int y0, int size, int holeX0, int holeY0, int holeSize)
        {
            var cmds = new List<uint>();
            int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            AppendMoveTo(cmds, ref cx, ref cy, holeX0, holeY0);
            AppendLineTo(cmds, ref cx, ref cy, (holeX0, holeY0 + holeSize), (holeX0 + holeSize, holeY0 + holeSize), (holeX0 + holeSize, holeY0));
            return cmds.ToArray();
        }

        private static IFeature SquareFeature(int x0, int y0, int size, IReadOnlyDictionary<string, Value> props)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon, geometry: SquareRing(x0, y0, size));

        private static IFeature CourtyardFeature(
            int x0, int y0, int size, int holeInset, int holeSize, IReadOnlyDictionary<string, Value> props)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon,
                geometry: SquareWithHoleRing(x0, y0, size, x0 + holeInset, y0 + holeInset, holeSize));

        private const double Extent = 4096.0;
        private static readonly TileId ModerateTile = new TileId { Z = 10, X = 300, Y = 380 }; // mid-latitude, shared with StyledFillExtrusionMeshTests

        private static IReadOnlyList<IFeature> Fixture()
        {
            var squareProps    = new Dictionary<string, Value> { ["h"] = Value.Number(30.0) };
            var courtyardProps = new Dictionary<string, Value> { ["h"] = Value.Number(50.0) }; // h on BOTH features
            return new[]
            {
                SquareFeature(1000, 1000, 500, squareProps),
                CourtyardFeature(2000, 1000, 600, holeInset: 150, holeSize: 300, courtyardProps),
            };
        }

        private static FillExtrusion.PaintProperties Paint()
            => TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":[\"get\",\"h\"]}");

        /// <summary>
        /// Fixture-authoring sanity, independent of anything the graph or the managed pipeline computes:
        /// <c>Materialize</c> must have decoded exactly THREE rings (the square's one exterior + the
        /// courtyard's exterior and hole) — a two-ring command stream the decoder mis-parses (dropped
        /// MoveTo, wrong repeat count) fails loudly here rather than silently becoming "two exteriors" three
        /// tests down.
        /// </summary>
        [Test]
        public void Fixture_MaterializesToExactlyThreeRings()
        {
            var features = Fixture();
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);
            try
            {
                Assert.AreEqual(3, geometry.RingCount,
                    "the square (1 exterior) + the courtyard (1 exterior + 1 hole) must decode to 3 rings.");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>
        /// The ASSERTED, permanent form of the reconstruction-validation the wall-job stage ran once in a
        /// throwaway harness (now deleted): re-hashes each golden JSON's own per-vertex Position+Normal
        /// (Stream0) and Tangent (Stream2) bytes, interleaved in the SAME order
        /// <see cref="ScheduleWrite_MatchesSplitGoldens_StreamForStream"/> hashes them, and asserts the
        /// result still reproduces the corresponding <see cref="FrozenGoldensFlat"/>/
        /// <see cref="FrozenGoldensSpherical"/> Stream0/Stream2 digest. Makes the whole-stream pin outlive the
        /// split: a golden byte changing for any reason fails this test loudly, rather than silently drifting
        /// from the proof that justified trusting it in the first place.
        /// </summary>
        [Test]
        public void GoldenJson_ReproducesFrozenDigests(
            [Values(false, true)] bool spherical)
        {
            string label = spherical ? "Spherical" : "WebMercator";
            (uint[] posHex, uint[] normHex, uint[] tanHex, int roofVertexCount, int totalVertexCount) =
                LoadGraphWriteGolden(label);
            Assert.AreEqual(posHex.Length, normHex.Length,
                $"[spherical={spherical}] positionHex/normalHex vertex counts disagree — golden is internally inconsistent.");
            int n = totalVertexCount;
            Assert.AreEqual(n * 3, posHex.Length, $"[spherical={spherical}] positionHex length != 3 * totalVertexCount.");
            Assert.AreEqual(n * 4, tanHex.Length, $"[spherical={spherical}] tangentHex length != 4 * totalVertexCount.");

            var s0 = new List<byte>();
            var s2 = new List<byte>();
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < 3; c++) s0.AddRange(BitConverter.GetBytes(math.asfloat(posHex[i * 3 + c])));
                for (int c = 0; c < 3; c++) s0.AddRange(BitConverter.GetBytes(math.asfloat(normHex[i * 3 + c])));
                for (int c = 0; c < 4; c++) s2.AddRange(BitConverter.GetBytes(math.asfloat(tanHex[i * 4 + c])));
            }

            Dictionary<string, string> frozen = ParseGolden(spherical ? FrozenGoldensSpherical : FrozenGoldensFlat);
            Assert.AreEqual(frozen["Stream0"], Sha256(s0),
                $"[spherical={spherical}] extrusion-graphwrite-golden-{label}.json's Position+Normal bytes no " +
                "longer reproduce the frozen Stream0 digest — the golden changed since its own reconstruction " +
                "was validated, and this is what would have caught it.");
            Assert.AreEqual(frozen["Stream2"], Sha256(s2),
                $"[spherical={spherical}] extrusion-graphwrite-golden-{label}.json's Tangent bytes no longer " +
                "reproduce the frozen Stream2 digest — the golden changed since its own reconstruction was " +
                "validated, and this is what would have caught it.");
        }

        // Frozen goldens (B.7): captured from the managed StyledFillExtrusionTileBuilder.WriteMeshData arm —
        // stream0 (PositionNormal), stream1 (ExtrudeAndBake), stream2 (tangent), stream3 (colour), indices,
        // bounds — on commit f5e13c19 (stage 4 Group A), before Group B rewrote WriteMeshData to itself route
        // through the graph. See docs/stage4-groupb-goldens-capture-f5e13c19.txt's SITE3 lines.
        // Stream0/Stream2 in these strings are used ONLY as the reconstruction-validation reference (see the
        // file header) — the live per-vertex comparison reads Assets/Fixtures/extrusion-graphwrite-golden-
        // *.json instead. Stream1/Stream3/Indices/Bounds are still compared against these strings directly.
        //
        // Stream3 (colour) was re-captured 2026-09-07 (UMR-96) and is the ONLY digest here that has moved
        // since f5e13c19. This fixture styles a CONSTANT fill-extrusion-color, which is no longer baked into
        // the COLOR stream — it rides the _BaseColor uniform and the vertex carries the white identity. The
        // digest therefore pins the OPPOSITE fact it used to (see its assertion below). Stream0/1/2, Indices
        // and Bounds are untouched, which is what confines that change to the colour path.
        //
        // vertex sharing (Spherical only — WebMercator never enters the subdivider):
        // GlobeFillSubdivideJob shares the roof's shared vertices, so its storage layout shrank
        // (roofVertexCount 30→12, totalVertexCount 78→60 — see extrusion-graphwrite-golden-Spherical.json)
        // and Stream0/1/2/3/Indices moved with it (Bounds did not — a min/max reduction is invariant to
        // which duplicate of a shared coordinate survives sharing, confirmed unmoved by the same capture).
        // Trustworthiness of these new numbers rests on RoofDeindexedStream_MatchesFrozenDigest_Spherical
        // (below), not on this capture alone — see that test's doc.
        private const string FrozenGoldensFlat =
            "Stream0=/BSPNaJt/vKUH5jnoGe18XG++7De4CQ5CaAkGo5/8cA= Stream1=5yoGCdWnImZda9zBFM2lMZ4cZpoio+676rYaTQc6Tj4= " +
            "Stream2=RZIb0svnW8ClUJy19C/CWEwcYrSRiGcuVrCU8pAINPM= Stream3=mTU6yDV37mWN86QXk22ebkLiBzEWq1ZAV/SCMCedDyQ= " +
            "Indices=UDPCPGXJU3OsWoNjbMg2T3AYq8M+lCd4AJOThHpv+uk= Bounds=U2c5vKyQgtfxmbjk+DwMuZ6m8SWFVpA+FZdtEiShrRs=";
        private const string FrozenGoldensSpherical =
            "Stream0=9sMgYPrYFR94lXLoVJDxijo6Bs3AYfbsdqlK3MWQ+R4= Stream1=ucir7tIjYxm9UiBL4GkqayK2I7hiB5hw4DCCGZfnQoI= " +
            "Stream2=eXX7a9tjYp/WC1KRsmKGg+/hW+jTh+hfTF93KqI353A= Stream3=jujmGM1QiI66NmIvsgG6bNN2zuISOIHEKZU+Q6CN02U= " +
            "Indices=nKqdVPkofLl5PE9XkvXfc3w1AAP8eddsf9IYEORH8Rc= Bounds=gokodWKrmk2WSnB0etMqXgtn1CTMRDmjLL82v96b+oE=";

        // Measured 2026-09-04 (wall-job stage): per-component max ULP delta between the retired managed
        // WriteWalls loop and the new Burst WallQuadJob chain, on THIS fixture's wall tail, plus a stated +2
        // margin for hardware/Burst-version headroom. job-scheduling-design.md §8 stage 5's opening invariant block bounds the
        // RAW double-precision projection output; Normal/Tangent here are NORMALIZED DIFFERENCES of two
        // nearby float32 positions — a different quantity, not safely inferred from that table, so measured
        // fresh rather than assumed. A regression that moves bytes (wrong index, wrong argument order, wrong
        // latitude) moves them far past these bounds; only genuine Burst-vs-managed rounding sits under them.
        // Position has no bound array: it is asserted bit-exact (a literal 0) everywhere, roof and wall tail,
        // for the STRUCTURAL reason above the assertion call sites — a divergence provably cannot reach it,
        // so a tolerance here would trade a proven guarantee for an unneeded one.
        private static readonly int[] WallNormalMaxUlpFlat        = { 2, 2, 2 };
        private static readonly int[] WallTangentMaxUlpFlat       = { 2, 2, 2, 2 };
        private static readonly int[] WallNormalMaxUlpSpherical   = { 3, 3, 3 };
        private static readonly int[] WallTangentMaxUlpSpherical  = { 4, 2, 3, 2 };

        /// <summary>
        /// (a) Extrusion stream parity — job-scheduling-design.md §8 stage 4 §5(a) / B.7, split per the
        /// wall-job stage (2026-09-04, see the file header). Stream1/Stream3/Indices/Bounds still match the
        /// frozen whole-stream hashes exactly; Stream0/Stream2 compare per-vertex against the golden JSON —
        /// bit-exact on the roof prefix, within the measured ULP bound on the wall tail.
        ///
        /// RED — two EXECUTED against this exact instrument (2026-09-04), one per split arm, confirmed RED
        /// with the injection and GREEN with it reverted:
        /// <list type="bullet">
        /// <item><b>Roof-prefix arm.</b> In <c>FillExtrusionStreamWriteJob.Execute</c>
        /// (<c>StyledFillExtrusionTileBuilder.WriteJob.cs</c>), forced <c>s2[i] = new Vector4(1f, 0f, 0f, 1f)</c>
        /// unconditionally instead of the per-vertex east — inert on the flat arm (its east already IS the
        /// constant <c>(1,0,0)</c>) and RED on the spherical arm with
        /// <c>"[spherical=True] Stream2.Tangent.x[0] (ROOF) diverges from the bit-exact golden — actual=0x3F800000
        /// (1) golden=0x3F769FC6 (0.963375449)"</c> — the bit-exact roof-prefix assertion, exactly the arm this
        /// injection targets.</item>
        /// <item><b>Wall-tail arm.</b> In <c>WallQuadJob.Execute</c> (<c>StyledFillExtrusionTileBuilder.WallJob.cs</c>),
        /// swapped <c>posA</c> for its edge partner's projected position (<c>World[idxB]</c> instead of
        /// <c>World[idxA]</c>) — RED on both projections (Position is bit-exact everywhere, so a shift shows
        /// immediately), e.g. <c>"[spherical=False] Stream0.Position.x[14] (WALL) diverges from the bit-exact
        /// golden — actual=0x465FEFC5 (14331.9424) golden=0x46154A84 (9554.629)"</c> — index 14 is
        /// <c>roofVertexCount</c> on the flat fixture, i.e. the first wall vertex, confirming the wall-tail
        /// assertion (not the roof one) is what fired. <b>The roof-prefix arm is RED-verified on the
        /// SPHERICAL projection only</b> — the injection is inert on flat (its east already IS the
        /// constant <c>(1,0,0)</c>, so the corruption writes the same bytes the golden expects); the flat
        /// roof prefix has no executed RED of its own, only the shared assertion machinery the spherical
        /// run already exercised.</item>
        /// </list>
        /// Both injections were reverted immediately after RED-verifying — checked by re-running this test
        /// class clean (3/3 green) and independently by grepping for the INJECTED expressions themselves
        /// (never the bare <c>RED-VERIFY</c> token — this paragraph's own prose contains it, which would make
        /// a bare token grep against it self-defeating): <c>grep -n "s2\[i\] = new Vector4(1f, 0f, 0f, 1f);"
        /// StyledFillExtrusionTileBuilder.WriteJob.cs</c> and <c>grep -n "posA = (float3)World\[idxB\];"
        /// StyledFillExtrusionTileBuilder.WallJob.cs</c> (the LEGITIMATE <c>posB = (float3)World[idxB];</c> a
        /// line below does not match — the injected pattern names <c>posA</c>, not <c>posB</c>, specifically),
        /// both returning empty. Both injections were re-run once more, one at a time against a freshly
        /// confirmed-clean baseline (no injection ⇒ 3/3 green, verified immediately before each), to remove
        /// any doubt about which message belonged to which arm.
        ///
        /// <para><b>A real defect the first roof-prefix RED-verify surfaced, in this test itself, not
        /// production:</b> <c>AssertStreamComponent</c> for Normal/Tangent originally passed the WALL-TAIL ULP
        /// bound unconditionally, never gated to bit-exact on the roof prefix — so a roof regression smaller
        /// than the wall-tail margin (2-4 ULP) would have passed silently. The first roof-prefix RED still
        /// caught the (enormous, 614458-ULP) injected error, but through the loose bound, not the bit-exact
        /// roof pin the doc above claims — a red for the wrong reason looks identical to a red for the right
        /// one. Fixed by gating Normal/Tangent's bound to <c>roof ? 0 : measuredBound</c>, same as Position;
        /// re-verified after the fix that the roof-prefix RED now fires the bit-exact assertion, not the
        /// bounded one.</para>
        /// </summary>
        // vertex sharing: the permanent de-indexed tooth item 6 asks for, standing in
        // for the deleted managed WriteWalls oracle. What it certifies: walking the INDEX buffer and
        // expanding each index to its full vertex tuple (Position/Normal/Tangent/ExtrudeAndBake/Colour) is
        // representation-independent of how much storage GlobeFillSubdivideJob's sharing changes — i.e. sharing
        // changes the roof's storage LAYOUT (fewer unique vertices, so ScheduleWrite_MatchesSplitGoldens_
        // StreamForStream's storage-order golden legitimately moves), never its de-indexed CONTENT (this
        // digest does not). Empirically verified once, ordinal-zero: stashed GlobeFillSubdivider.cs (only),
        // captured this exact digest on the pre-sharing tree, popped the stash, captured it again — Stream0,
        // Stream1, Stream2, Stream3, Bounds, roofIndexCount and streamLength were BYTE-IDENTICAL; only the
        // raw Indices= hash (literal index integers, not part of this claim — they shift because the roof's
        // vertex count the wall half rebases against shrank) differed, as expected. That equality is what
        // lets ScheduleWrite_MatchesSplitGoldens_StreamForStream's freshly re-captured storage-order golden
        // inherit the retired managed oracle's provenance instead of resting on this stage's own say-so.
        [Test]
        public void RoofDeindexedStream_MatchesFrozenDigest_Spherical()
        {
            IProjection projection = new SphericalProjection();
            IReadOnlyList<IFeature> features = Fixture();
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            FillExtrusion.PaintProperties paint = Paint();
            double3 renderOrigin = TileRenderOrigin.Project(ModerateTile, projection);
            TileBufferClip clip = TileBufferClip.KeepTileUnits(0.0);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);

            MeshWriteOutput graphWrite = default;
            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, paint, 0.0, renderOrigin, out colors, out bake, projection, clip);
                ringVisitOrder = input.RingVisitOrder;

                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                graphWrite = StyledFillExtrusionTileBuilder.ScheduleWrite(
                    ext.Roof, colors, bake, ext.Walls, projection, ModerateTile, Extent);
                graphWrite.Handle.Complete();

                Mesh.MeshData b = graphWrite.Mda[0];
                var b0 = b.GetVertexData<StyledFillExtrusionTileBuilder.PositionNormal>(0);
                var b1 = b.GetVertexData<StyledFillExtrusionTileBuilder.ExtrudeAndBake>(1);
                var b2 = b.GetVertexData<Vector4>(2);
                var b3 = b.GetVertexData<Vector4>(3);
                NativeArray<int> bi = b.GetIndexData<int>();
                int roofIndexCount = ext.Roof.TriangleIndices.Length;

                var s0 = new List<byte>(); var s1 = new List<byte>(); var s2 = new List<byte>(); var s3 = new List<byte>();
                for (int i = 0; i < bi.Length; i++)
                {
                    int vi = bi[i];
                    Vector3 p = b0[vi].Position, n = b0[vi].Normal;
                    Vector4 tan = b2[vi];
                    Vector4 eut = b1[vi].ExtrudeUpAndT; Vector2 bbh = b1[vi].BakedBaseHeight;
                    s0.AddRange(BitConverter.GetBytes(p.x)); s0.AddRange(BitConverter.GetBytes(p.y)); s0.AddRange(BitConverter.GetBytes(p.z));
                    s0.AddRange(BitConverter.GetBytes(n.x)); s0.AddRange(BitConverter.GetBytes(n.y)); s0.AddRange(BitConverter.GetBytes(n.z));
                    s1.AddRange(BitConverter.GetBytes(eut.x)); s1.AddRange(BitConverter.GetBytes(eut.y));
                    s1.AddRange(BitConverter.GetBytes(eut.z)); s1.AddRange(BitConverter.GetBytes(eut.w));
                    s1.AddRange(BitConverter.GetBytes(bbh.x)); s1.AddRange(BitConverter.GetBytes(bbh.y));
                    s2.AddRange(BitConverter.GetBytes(tan.x)); s2.AddRange(BitConverter.GetBytes(tan.y)); s2.AddRange(BitConverter.GetBytes(tan.z)); s2.AddRange(BitConverter.GetBytes(tan.w));
                    Vector4 col = b3[vi];
                    s3.AddRange(BitConverter.GetBytes(col.x)); s3.AddRange(BitConverter.GetBytes(col.y));
                    s3.AddRange(BitConverter.GetBytes(col.z)); s3.AddRange(BitConverter.GetBytes(col.w));
                }

                float3x2 gb = graphWrite.Bounds[0];
                float3 boundsCenter = (gb.c0 + gb.c1) * 0.5f;
                float3 boundsSize = gb.c1 - gb.c0;
                var boundsBytes = new List<byte>();
                boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.x)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.y)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.z));
                boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.x)); boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.y)); boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.z));

                Assert.AreEqual(FrozenDeindexedRoofIndexCount, roofIndexCount,
                    "roof/wall split (emitted index count) moved — a topology change, not a sharing-representation question.");
                Assert.AreEqual(FrozenDeindexedStreamLength, bi.Length,
                    "de-indexed triangle-stream length moved — a topology change, not a sharing-representation question.");
                Assert.AreEqual(FrozenDeindexedStream0, Sha256(s0), "de-indexed Position+Normal diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedStream1, Sha256(s1), "de-indexed ExtrudeUpAndT+Bake diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedStream2, Sha256(s2), "de-indexed Tangent diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedStream3, Sha256(s3), "de-indexed colour diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedBounds, Sha256(boundsBytes), "de-indexed Bounds diverges — a real regression.");
            }
            finally
            {
                graphWrite.Dispose();
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        // Frozen de-indexed digests (vertex sharing) — captured once, verified identical
        // pre- and post-sharing by the ordinal-zero stash protocol described on the test above.
        private const int FrozenDeindexedRoofIndexCount = 30;
        private const int FrozenDeindexedStreamLength = 102;
        private const string FrozenDeindexedStream0 = "7ICFr669P/x2IHqVJ5T023SWd1v90P8WMj2DItamSOk=";
        private const string FrozenDeindexedStream1 = "ezdw3k30Z4SXZi+Cl7BMLml1g6AA0p9C9HOBhMBo/GU=";
        private const string FrozenDeindexedStream2 = "F9ebvG8+LKB7EXhnPGhjYThVKcO6hZr4LI+BoI4jiuA=";
        private const string FrozenDeindexedStream3 = "v1RlA1kuuUf4NeACVRLaRb4cxUkaXgeCYNDTMeI6/1I=";
        private const string FrozenDeindexedBounds = "gokodWKrmk2WSnB0etMqXgtn1CTMRDmjLL82v96b+oE=";

        [Test]
        public void ScheduleWrite_MatchesSplitGoldens_StreamForStream(
            [Values(false, true)] bool spherical)
        {
            IProjection projection = spherical ? (IProjection)new SphericalProjection() : new WebMercatorProjection();
            IReadOnlyList<IFeature> features = Fixture();
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            FillExtrusion.PaintProperties paint = Paint();
            double3 renderOrigin = TileRenderOrigin.Project(ModerateTile, projection);
            TileBufferClip clip = TileBufferClip.KeepTileUnits(0.0);

            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);

            MeshWriteOutput graphWrite = default;
            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake   = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, paint, 0.0, renderOrigin,
                    out colors, out bake, projection, clip);
                ringVisitOrder = input.RingVisitOrder;
                Assert.IsTrue(input.RingVisitOrder.IsCreated, "precondition: the fixture must select real work.");

                // One graph, one ownership story (review NIT 4) — FillExtrusionMeshGraph.Schedule already
                // composes the roof via FillMeshGraph.Schedule internally; a second, separate
                // FillMeshGraph.Schedule(input) call here would schedule the roof TWICE over the same input.
                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                Assert.AreEqual(FillGraphCounts.Ok, ext.Roof.Error.Value, "precondition: the measure must not fault.");
                Assert.GreaterOrEqual(ext.Roof.Counts[0].HoleCount, 1,
                    "precondition (R3): the hole-bridge path must have been entered — a mis-wound inner " +
                    "ring reads as a second exterior and this stays 0, which is exactly the bug this fixture exists to catch.");

                graphWrite = StyledFillExtrusionTileBuilder.ScheduleWrite(
                    ext.Roof, colors, bake, ext.Walls, projection, ModerateTile, Extent);
                graphWrite.Handle.Complete();
                Assert.Greater(graphWrite.VertexCount, 0,
                    "precondition: the graph must have produced real geometry — an empty mesh would pass while proving nothing.");

                Mesh.MeshData b = graphWrite.Mda[0];
                var b0 = b.GetVertexData<StyledFillExtrusionTileBuilder.PositionNormal>(0);
                var b1 = b.GetVertexData<StyledFillExtrusionTileBuilder.ExtrudeAndBake>(1);
                var b2 = b.GetVertexData<Vector4>(2);
                var b3 = b.GetVertexData<Vector4>(3);
                NativeArray<int> bi = b.GetIndexData<int>();

                // vertex sharing: STORAGE order, deliberately — this whole-stream golden
                // pins what the write step actually PUT in the buffer (the same reason TileBuildGraphTests
                // stays storage-order). The de-indexed representation lives separately, as its own permanent
                // tooth (RoofDeindexedStream_MatchesFrozenDigest_Spherical, below) — that is what certifies
                // these freshly-captured storage-order numbers are representation-equivalent to what the
                // (now-retired) managed oracle originally validated, without conflating two different
                // questions ("what got written" vs "is it still the same geometry") in one digest.
                var s1 = new List<byte>(); var s3 = new List<byte>();
                bool anyBaked = false;
                bool anySecPhi = false;
                bool everyRoofUnit = true;
                for (int i = 0; i < graphWrite.VertexCount; i++)
                {
                    Vector4 eut = b1[i].ExtrudeUpAndT; Vector2 bbh = b1[i].BakedBaseHeight;
                    s1.AddRange(BitConverter.GetBytes(eut.x)); s1.AddRange(BitConverter.GetBytes(eut.y));
                    s1.AddRange(BitConverter.GetBytes(eut.z)); s1.AddRange(BitConverter.GetBytes(eut.w));
                    s1.AddRange(BitConverter.GetBytes(bbh.x)); s1.AddRange(BitConverter.GetBytes(bbh.y));
                    Vector4 col = b3[i];
                    s3.AddRange(BitConverter.GetBytes(col.x)); s3.AddRange(BitConverter.GetBytes(col.y));
                    s3.AddRange(BitConverter.GetBytes(col.z)); s3.AddRange(BitConverter.GetBytes(col.w));

                    if (bbh.y != 0f) anyBaked = true;
                    float mag = new Vector3(eut.x, eut.y, eut.z).magnitude;
                    if (!spherical && mag > 1.05f) anySecPhi = true;
                    if (spherical && (mag < 1f - 1e-5f || mag > 1f + 1e-5f)) everyRoofUnit = false;
                }
                Assert.IsTrue(anyBaked, "precondition: the data-driven height bake must have been entered (BakedBaseHeight.y != 0 somewhere).");
                if (!spherical)
                    Assert.IsTrue(anySecPhi, "precondition: the flat arm's sec(φ) factor must actually be applied somewhere (ModerateTile is mid-latitude).");
                else
                    Assert.IsTrue(everyRoofUnit, "precondition: the globe arm's extrude-up must be un-scaled unit-up everywhere (Globe was derived, not defaulted).");

                var idxBytes = new List<byte>();
                for (int i = 0; i < bi.Length; i++) idxBytes.AddRange(BitConverter.GetBytes(bi[i]));

                // Center/size (not raw min/max c0/c1) — matches the captured oracle's Bounds struct, which
                // stores (min+max)*0.5 / max-min, a different float bit pattern than the raw endpoints.
                float3x2 gb = graphWrite.Bounds[0];
                float3 boundsCenter = (gb.c0 + gb.c1) * 0.5f;
                float3 boundsSize   = gb.c1 - gb.c0;
                var boundsBytes = new List<byte>();
                boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.x)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.y)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.z));
                boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.x));   boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.y));   boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.z));

                // Stream1/Stream3/Indices/Bounds — UNCHANGED, still a whole-stream hash against the frozen
                // constant (the split narrows only Stream0/Stream2, below).
                Dictionary<string, string> frozen = ParseGolden(spherical ? FrozenGoldensSpherical : FrozenGoldensFlat);
                Assert.AreEqual(frozen["Stream1"], Sha256(s1), $"[spherical={spherical}] Stream1 (ExtrudeUpAndT+Bake) diverges — a real regression, not a re-bake candidate.");
                // NOT "never a re-bake candidate" — that claim was written when a constant colour WAS baked
                // into this stream, and UMR-96 retired that. The digest now encodes a white vertex, so what a
                // divergence means has flipped: reading a STYLED colour here means the constant-colour bake
                // came back and the layer renders colour-squared. Any OTHER divergence is still a regression.
                Assert.AreEqual(frozen["Stream3"], Sha256(s3), $"[spherical={spherical}] Stream3 (colour) diverges. This fixture's fill-extrusion-color is CONSTANT, so the stream must carry the WHITE identity and the colour must ride _BaseColor; a styled colour here means the vertex bake was re-introduced (colour-squared). Re-bake ONLY on a deliberate, stated change to which carrier holds a constant colour.");
                Assert.AreEqual(frozen["Indices"], Sha256(idxBytes), $"[spherical={spherical}] Indices diverge — a real regression, not a re-bake candidate.");
                Assert.AreEqual(frozen["Bounds"], Sha256(boundsBytes), $"[spherical={spherical}] Bounds diverge — a real regression, not a re-bake candidate.");

                // Stream0 (Position+Normal) / Stream2 (Tangent) — split: bit-exact on the roof prefix, within
                // the measured ULP bound on the wall tail (see the file header + the bound constants above).
                string label = spherical ? "Spherical" : "WebMercator";
                (uint[] posHex, uint[] normHex, uint[] tanHex, int roofVertexCount, int totalVertexCount) =
                    LoadGraphWriteGolden(label);
                Assert.AreEqual(totalVertexCount, graphWrite.VertexCount,
                    $"[spherical={spherical}] golden vertex count ({totalVertexCount}) no longer matches the " +
                    $"build's ({graphWrite.VertexCount}) — a topology change, not a float-noise question.");

                int[] normBound = spherical ? WallNormalMaxUlpSpherical : WallNormalMaxUlpFlat;
                int[] tanBound = spherical ? WallTangentMaxUlpSpherical : WallTangentMaxUlpFlat;

                for (int i = 0; i < graphWrite.VertexCount; i++)
                {
                    bool roof = i < roofVertexCount;
                    Vector3 p = b0[i].Position, n = b0[i].Normal;
                    Vector4 tan = b2[i];

                    // Position: BIT-EXACT everywhere, roof AND wall tail — STRUCTURALLY, not by luck of this
                    // fixture (docs/job-scheduling-design.md §8 stage 5's opening invariant block): the
                    // origin-relative double divergence this stage measured is ~9.3e-10 absolute at tile-local
                    // magnitude, six orders below a float32 ULP there (~9.8e-4) — the (float3) narrowing
                    // erases it for any tile-local geometry, not just this one. A red here is a formula error,
                    // never drift; bounding it would be a WEAKER tooth than the code earns.
                    AssertStreamComponent(roof, p.x, posHex[i * 3 + 0], 0, spherical, i, "Stream0.Position.x");
                    AssertStreamComponent(roof, p.y, posHex[i * 3 + 1], 0, spherical, i, "Stream0.Position.y");
                    AssertStreamComponent(roof, p.z, posHex[i * 3 + 2], 0, spherical, i, "Stream0.Position.z");
                    // Normal/Tangent: bit-exact on the roof prefix too (roof is 100% untouched by this stage —
                    // a bound there would let a real roof regression under the wall-tail's own ULP margin pass
                    // silently), the measured bound applies ONLY past roofVertexCount.
                    AssertStreamComponent(roof, n.x, normHex[i * 3 + 0], roof ? 0 : normBound[0], spherical, i, "Stream0.Normal.x");
                    AssertStreamComponent(roof, n.y, normHex[i * 3 + 1], roof ? 0 : normBound[1], spherical, i, "Stream0.Normal.y");
                    AssertStreamComponent(roof, n.z, normHex[i * 3 + 2], roof ? 0 : normBound[2], spherical, i, "Stream0.Normal.z");
                    AssertStreamComponent(roof, tan.x, tanHex[i * 4 + 0], roof ? 0 : tanBound[0], spherical, i, "Stream2.Tangent.x");
                    AssertStreamComponent(roof, tan.y, tanHex[i * 4 + 1], roof ? 0 : tanBound[1], spherical, i, "Stream2.Tangent.y");
                    AssertStreamComponent(roof, tan.z, tanHex[i * 4 + 2], roof ? 0 : tanBound[2], spherical, i, "Stream2.Tangent.z");
                    AssertStreamComponent(roof, tan.w, tanHex[i * 4 + 3], roof ? 0 : tanBound[3], spherical, i, "Stream2.Tangent.w");
                }
            }
            finally
            {
                graphWrite.Dispose();
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                // Caller-owned (FillMeshPipeline.LayerInput.RingVisitOrder's own doc — FillMeshGraph.Schedule
                // only reads it) — pre-existing leak, found chasing a Persistent-allocation leak the wall-job
                // stage's gate surfaced; unrelated to WriteWalls, confirmed by reading FillMeshGraph.cs (it
                // never disposes input.RingVisitOrder, only WriteMeshData's caller-side `using var` did).
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        private static string Sha256(List<byte> bytes)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return System.Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static Dictionary<string, string> ParseGolden(string golden)
        {
            var result = new Dictionary<string, string>();
            foreach (string part in golden.Split(' '))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) result[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return result;
        }

        /// <summary>Loads a per-vertex Stream0/Stream2 golden written by the reconstruction capture harness
        /// (deleted after use — see the file header) — hex IEEE-754 bit patterns, comma-separated, read back
        /// via <see cref="math.asfloat(uint)"/>.
        ///
        /// <para>vertex sharing (the Spherical file only — WebMercator never enters the
        /// subdivider, unaffected): re-captured in STORAGE order (unchanged framing) once sharing shrank the
        /// roof's real unique vertex count — <c>roofVertexCount</c>/<c>totalVertexCount</c> are the new,
        /// smaller counts. Trustworthiness of the new numbers rests on
        /// <see cref="RoofDeindexedStream_MatchesFrozenDigest_Spherical"/>, a separate permanent tooth that
        /// proves the roof's DE-INDEXED triangle-stream content is bit-identical to the pre-sharing tree —
        /// i.e. sharing only changed storage layout, never geometry — which is what lets this storage-order
        /// capture inherit the retired managed oracle's provenance instead of being trusted on its own
        /// say-so.</para></summary>
        private static (uint[] posHex, uint[] normHex, uint[] tanHex, int roofVertexCount, int totalVertexCount)
            LoadGraphWriteGolden(string projectionLabel)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", $"extrusion-graphwrite-golden-{projectionLabel}.json");
            Assert.IsTrue(File.Exists(path), $"golden missing: {path}");
            JsonValue root = JsonParser.Parse(File.ReadAllText(path));
            int roofVertexCount = root.Get("roofVertexCount").AsInt();
            int totalVertexCount = root.Get("totalVertexCount").AsInt();
            uint[] posHex = ParseHexArray(root.Get("positionHex").AsString(null));
            uint[] normHex = ParseHexArray(root.Get("normalHex").AsString(null));
            uint[] tanHex = ParseHexArray(root.Get("tangentHex").AsString(null));
            return (posHex, normHex, tanHex, roofVertexCount, totalVertexCount);
        }

        private static uint[] ParseHexArray(string csv)
        {
            string[] parts = csv.Split(',');
            var result = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = System.Convert.ToUInt32(parts[i], 16);
            return result;
        }

        /// <summary>The roof prefix is bit-exact (the roof path is untouched by this stage); the wall tail is
        /// within <paramref name="maxUlp"/> — the total-order IEEE-754 ULP-distance mapping (Bruce Dawson's
        /// AlmostEqualUlps idiom), never a raw bit-pattern subtraction, which is wrong across zero/sign.
        /// <paramref name="roof"/> is the TRUE <c>i &lt; roofVertexCount</c> flag (labels the failure
        /// correctly) — comparison strictness is driven by <paramref name="maxUlp"/> alone, so a bit-exact
        /// bound (e.g. Position, everywhere) still reports "(WALL)" honestly on a wall-vertex mismatch.</summary>
        private static void AssertStreamComponent(
            bool roof, float actual, uint goldenHex, int maxUlp, bool spherical, int index, string label)
        {
            uint actualHex = math.asuint(actual);
            string where = roof ? "(ROOF)" : "(WALL)";
            if (maxUlp == 0)
            {
                Assert.AreEqual(goldenHex, actualHex,
                    $"[spherical={spherical}] {label}[{index}] {where} diverges from the bit-exact golden — " +
                    $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}). " +
                    (roof ? "The roof path is untouched by this stage — this is a real regression."
                          : "This field is bit-exact everywhere (measured 0 ULP) — this is a real regression."));
                return;
            }

            ulong delta = UlpDistance(actualHex, goldenHex);
            Assert.LessOrEqual(delta, (ulong)maxUlp,
                $"[spherical={spherical}] {label}[{index}] {where} exceeds its measured {maxUlp}-ULP bound — " +
                $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}) delta={delta} ULP.");
        }

        private static ulong ToUlpOrder(uint bits) => (bits & 0x80000000U) != 0 ? ~bits : (bits | 0x80000000U);

        private static ulong UlpDistance(uint a, uint b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }
    }
}
