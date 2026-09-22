// Tiles/TileBuildGraphTests.cs — TileBuildGraph's own allocation/read-before-complete contracts, the layer-processor runner, decode validation, line-graph kick, source-registry restyle, and the non-MVT decoder seam.
//
// TileBuildGraph and its layer-processor-runner client first, then the three independent decode/kick/restyle fixtures, then the non-MVT decoder acceptance fixture.
//
// Contents:
//   TileBuildGraphTests               — job-scheduling-design.md teeth (b) and (f) — TileBuildGraph's own allocation and read-before-complete contracts, exercised directly (below the full TileManager stack).
//   TileLayerProcessorRunnerTests     — RunWorkerPass decodes the fetched bytes once and shares that one IDecodedTile across every processor in dense order; the fault policy survives the move.
//   DecodeTests                       — Headless validation of the decode + coordinate path against the real fixture tile.
//   LineGraphKickTests                — Style: geolines-stroke@0 only, on the geolines source-layer (LineString geometry — the SAME fixture/ layer StyledLineBufferParityTests and ThrottleTests.FillAndLineStyle already use), so a mixed style's fill layer cannot satisfy either tooth's assertions…
//   SourceRegistrySlotInvariantTests  — Unity EditMode only — drives the real MapView/TileManager restyle path.
//   A6NonMvtDecoderTests              — Load-bearing acceptance: proves a NON-MvtDecoder ITileDecoder flows through the unchanged fill fan-out (RunWorkerPass -> TileMeshLayerProcessor -> StyledFillTileBuilder.WriteMeshData) and produces real geometry.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine.TestTools;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;
using System.Threading;
using MapRenderer.Jobs.Lines;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Unity.Rendering.Tile;
using Fill = MapRenderer.Core.Style.Fill;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Tiles
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // TileBuildGraphTests — own allocation and read-before-complete contracts
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileBuildGraphTests
    {
        private static TileGeometryBuffers SingleTrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingOffsets[0] = 0;
            g.Vertices[0] = new double2(0, 0);
            g.Vertices[1] = new double2(10, 0);
            g.Vertices[2] = new double2(10, 10);
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[1] = 3;
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        /// <summary>One layer's fixture: the rented build plus the two owned-by-the-caller locals a test
        /// needs to read independently of anything the build/graph computes (the geometry, BORROWED by the
        /// build and never freed by it, and the visit order, whose LENGTH a precondition reads before the
        /// build's own <c>Dispose</c> makes it meaningless — see <c>LayerRequest.HasWork</c>'s retired own
        /// doc for that trap). <see cref="ILayerMeshBuild"/> exposes neither (test-code-bloat rule; the interface's own
        /// note on why <c>MaterialIndex</c> did not survive onto the interface applies here too), so a test
        /// that needs them keeps its own reference instead of reaching back through the build.</summary>
        private readonly struct LayerFixture
        {
            public readonly ILayerMeshBuild Build;
            public readonly TileGeometryBuffers Geometry;
            public readonly NativeArray<int> VisitOrder;

            public LayerFixture(ILayerMeshBuild build, TileGeometryBuffers geometry, NativeArray<int> visitOrder)
            {
                Build = build; Geometry = geometry; VisitOrder = visitOrder;
            }
        }

        /// <summary>A ring of TWO vertices — <c>RingAssemblyJob</c> drops it as degenerate (<c>rLen &lt; 3</c>),
        /// so the graph runs to completion and returns a CREATED output holding zero vertices.
        ///
        /// <para>This is a different emptiness from <see cref="EmptyLayerFixture"/>, and the distinction is
        /// the point. A zero-length visit order never reaches the graph at all — <c>FillMeshGraph.Schedule</c>
        /// fast-outs and hands back <c>IsCreated == false</c>. A degenerate ring DOES build a graph, so the
        /// only thing standing between it and a needlessly allocated <c>MeshDataArray</c> is
        /// <c>CompleteMeasureAndScheduleWrite</c>'s zero-vertex guard. Without a layer of this shape that
        /// guard has no observing tooth: deleting it changes nothing any test can see, because the
        /// <c>!IsCreated</c> check above it already absorbs the only empty layer the fixture produces.
        /// Production reaches this shape whenever a layer's features all clip or degenerate away.</para></summary>
        private static LayerFixture DegenerateLayerFixture(TileId tile, string name, int materialIndex)
        {
            var geometry = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 2);
            geometry.FeatureGeometryType[0] = TileGeometryType.Polygon;
            geometry.RingOffsets[0] = 0;
            geometry.Vertices[0] = new double2(0, 0);
            geometry.Vertices[1] = new double2(10, 0);
            geometry.RingFeatureIdx[0] = 0;
            geometry.RingOffsets[1] = 2;
            geometry.RingCount = 1;
            geometry.VertexCount = 2;

            var visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent) { [0] = Vector4.one };
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(),
                Clip = TileBufferClip.KeepTileUnits(0.0),
            };
            return new LayerFixture(FillLayerBuild.Rent(input, featureColors, materialIndex, name), geometry, visitOrder);
        }

        private static LayerFixture RealLayerFixture(TileId tile, string name, int materialIndex)
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(tile);
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent) { [0] = Vector4.one };
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                Clip = TileBufferClip.KeepTileUnits(0.0), // matches production (KickSourcelessBackground)
            };
            return new LayerFixture(FillLayerBuild.Rent(input, featureColors, materialIndex, name), geometry, visitOrder);
        }

        /// <summary>A layer whose <see cref="FillMeshPipeline.LayerInput.RingVisitOrder"/> is genuinely
        /// zero-length — <c>FillMeshGraph.Schedule</c>'s own "nothing to draw" fast-out (<see cref="FillGraphOutput"/>'s
        /// type doc), not a hand-stubbed struct standing in for one. Rented directly through
        /// <see cref="FillLayerBuild.Rent"/> rather than through a render layer's own emptiness gate — an
        /// IsCreated-but-zero-length array passes that gate too (it only tests <c>IsCreated</c>), exactly
        /// matching what production hands the graph for an empty source layer.</summary>
        private static LayerFixture EmptyLayerFixture(TileId tile, string name, int materialIndex)
        {
            TileGeometryBuffers geometry =
                TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 0, maxRings: 0, maxVertices: 0);
            var visitOrder = new NativeArray<int>(0, Allocator.Persistent);
            var featureColors = new NativeArray<Vector4>(0, Allocator.Persistent);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                Clip = TileBufferClip.KeepTileUnits(0.0), // matches production (KickSourcelessBackground)
            };
            return new LayerFixture(FillLayerBuild.Rent(input, featureColors, materialIndex, name), geometry, visitOrder);
        }

        // ── (b) exact-size allocation, corrected multi-layer form ──────────────────────────────────────
        //
        // The earlier three-CHUNK arm is struck (chunking is gone). This is three LAYERS instead — two with
        // real geometry, one genuinely empty — asserting CompleteMeasureAndScheduleWrite allocates exactly
        // one Mesh.MeshDataArray per non-empty layer, never one for the empty layer. Fixture composition (3
        // layers, 1 empty) is asserted as a PRECONDITION, read independently of anything
        // CompleteMeasureAndScheduleWrite itself computes (raw RingVisitOrder.Length, not FillGraphOutput
        // state) — so a fixture that silently stopped producing an empty layer could not make this pass
        // vacuously. Every count below is a BEFORE/AFTER delta on MeshDataPayload.DebugLiveAllocCount, a
        // process-wide static shared across the whole batch run — never an absolute reading.

        [Test]
        public void CompleteMeasureAndScheduleWrite_AllocatesOneArrayPerNonEmptyLayer_NoneForTheEmptyOne()
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            var fixtures = new[]
            {
                RealLayerFixture(tile, "layer-a", materialIndex: 0),
                EmptyLayerFixture(tile, "layer-empty", materialIndex: 1),
                RealLayerFixture(tile, "layer-b", materialIndex: 2),
                DegenerateLayerFixture(tile, "layer-degenerate", materialIndex: 3),
            };
            var layers = new ILayerMeshBuild[fixtures.Length];
            for (int i = 0; i < fixtures.Length; i++) layers[i] = fixtures[i].Build;

            Assert.AreEqual(4, layers.Length, "precondition: the fixture must supply exactly four layers.");
            int emptyByInput = 0, nonEmptyByInput = 0;
            foreach (LayerFixture f in fixtures)
            {
                if (f.VisitOrder.Length == 0) emptyByInput++;
                else nonEmptyByInput++;
            }
            Assert.AreEqual(1, emptyByInput,
                "precondition: exactly one layer's RingVisitOrder must be genuinely zero-length (the empty layer).");
            Assert.AreEqual(3, nonEmptyByInput,
                "precondition: the other three layers must carry ring data the graph will actually run over.");

            // The degenerate layer is what makes the zero-vertex guard REACHABLE, and it must be empty by a
            // different mechanism than the zero-length one: its visit order is non-empty (so the graph runs
            // and returns a CREATED output) while its only ring has two vertices, which RingAssemblyJob drops
            // as degenerate — leaving a created output holding nothing. Asserted from the fixture's OWN
            // locals, independently of anything CompleteMeasureAndScheduleWrite computes.
            LayerFixture degenerate = fixtures[3];
            Assert.Greater(degenerate.VisitOrder.Length, 0,
                "precondition: the degenerate layer must reach the graph at all — a zero-length visit order " +
                "would fast-out to IsCreated == false and exercise the wrong guard.");
            int ringStart = degenerate.Geometry.RingOffsets[0];
            int ringEnd   = degenerate.Geometry.RingOffsets[1];
            Assert.Less(ringEnd - ringStart, 3,
                "precondition: the degenerate layer's ring must have fewer than three vertices, so the graph " +
                "produces a created-but-empty output rather than geometry.");

            long baseline = MeshDataPayload.DebugLiveAllocCount;
            // Unlike production's shared-quad background tile, these three layers each mint their OWN
            // geometry (a deliberately different fixture per layer) — none of them is the graph's single
            // per-tile ownedGeometry slot, so that argument is `default` (nothing shared to own) and each
            // layer's own Geometry is disposed by this test in `finally`, after the graph itself (BORROWED —
            // never freed by the build).
            TileBuildGraph graph = TileBuildGraph.ScheduleMeasure(layers, default(TileGeometryBuffers));
            try
            {
                graph.Complete();
                graph.CompleteMeasureAndScheduleWrite(out int meshDataArraysAllocated);

                Assert.AreEqual(2, meshDataArraysAllocated,
                    "exactly the two non-empty layers must produce a Mesh.MeshDataArray — the empty layer's " +
                    "FillGraphOutput.IsCreated is false (FillMeshGraph.Schedule's own empty-input fast-out) " +
                    "and must settle as zero-vertex with no array allocated at all.");

                long afterAllocate = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(baseline + 2, afterAllocate,
                    "the DELTA in live MeshDataPayload allocations must be exactly the two arrays this call " +
                    "allocated — an absolute reading is meaningless against a counter shared by the whole batch run.");

                // ONE SLOT PER REQUEST, in request order — not
                // one per produced mesh. A null slot means an empty or faulted fill layer; compacting would
                // silently renumber the slots a caller (the pump) joins against.
                MeshDataPayload[] payloads = graph.CompleteWriteAndTakePayloads();
                Assert.AreEqual(4, payloads.Length,
                    "one slot per REQUEST (4), not one per produced mesh (2) — the empty and degenerate " +
                    "layers still take a slot, left null.");
                Assert.IsNotNull(payloads[0], "layer-a (real) must have produced a payload.");
                Assert.IsNull(payloads[1], "layer-empty must settle as a null slot, not a payload.");
                Assert.IsNotNull(payloads[2], "layer-b (real) must have produced a payload.");
                Assert.IsNull(payloads[3], "layer-degenerate must settle as a null slot, not a payload.");
                foreach (MeshDataPayload p in payloads) p?.Dispose();

                long afterDispose = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(baseline, afterDispose,
                    "every allocated array must be released once its payload is disposed — back to baseline.");
            }
            finally
            {
                graph.Dispose(); // completes every job first — safe to free the raw geometries after this
                foreach (LayerFixture f in fixtures)
                    f.Geometry.Dispose();
            }
        }

        /// <summary><b>RED injection:</b> drop the <c>if (output.TileVertices.Length == 0 || …) continue;</c>
        /// guard in <c>TileBuildGraph.CompleteMeasureAndScheduleWrite</c> — the empty layer gets scheduled
        /// for write too, and <c>meshDataArraysAllocated</c> reads 3.</summary>

        // ── (f) Complete-before-read positive control ──────────────────────────────────────────────────

        /// <summary>Reads a job-scheduled <see cref="FillGraphOutput"/> field before <c>Handle.Complete()</c>
        /// — the engine's own container safety system, not application code, must refuse this at the
        /// measure→write transition. Executed once to observe the actual message; the exact wording is an
        /// engine internal, not this repo's contract to pin, so it is recorded here as a comment rather than
        /// asserted verbatim. Observed (Unity 6000.x job safety system):
        /// <c>"The NativeContainer has been declared as \[WriteOnly\] in the job, but you are reading from it."</c>
        /// (Unity's message for a <c>WriteOnly</c>-declared list read externally before the job completes;
        /// class name and field vary by container type, so only the shape — <see cref="InvalidOperationException"/>
        /// with a non-empty message — is asserted.)</summary>
        [Test]
        public void ReadingOutputBeforeComplete_Throws_AtTheMeasureToWriteTransition()
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                Clip = TileBufferClip.KeepTileUnits(0.0), // matches production (KickSourcelessBackground)
            };

            FillGraphOutput output = FillMeshGraph.Schedule(input);
            try
            {
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                {
                    int _ = output.TileVertices.Length;
                });
                // Observed 2026-09-03 (captured from a filtered run, not predicted):
                //   "The previously scheduled job AggregateJob writes to the
                //    NativeList`1[Unity.Mathematics.double2] AggregateJob.TileVertices. You must call
                //    JobHandle.Complete() on the job AggregateJob, before you can read from the
                //    NativeList`1[Unity.Mathematics.double2] safely."
                // Asserted by its load-bearing PARTS, not verbatim: the exact wording is Unity's and would
                // make this test fail on an engine upgrade that reworded it, which is not the defect this
                // tooth exists to catch. What must hold is that the safety system named the missing
                // Complete() and the job that still owns the write — an IsNotEmpty check would pass for a
                // stub that threw the right TYPE with any text at all.
                StringAssert.Contains("Complete()", ex.Message,
                    "the safety system must say a Complete() is missing — that is the whole claim of this " +
                    "tooth: the output is unreadable until the caller completes the handle.");
                // Re-captured when FillBandJob became the flat arm's last writer of TileVertices (it appends
                // the boundary band to AggregateJob's own columns). Re-capture on the same terms if the graph
                // reshapes again — never weaken the assertion to a type-only check.
                StringAssert.Contains("FillBandJob", ex.Message,
                    "it must name the job still writing TileVertices. If a future graph reshape makes some " +
                    "other node the last writer, this fails loudly and the recorded string above is stale — " +
                    "which is the signal to re-capture it, not to weaken the assertion.");
            }
            finally
            {
                output.Handle.Complete();
                output.Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }
        // ── Write-step PARITY: the graph's stream write against frozen goldens, byte for byte ──────
        //
        // The write step is this stage's only new geometry-PRODUCING code, and until this tooth existed
        // nothing read a single byte it wrote. The snapshot suites observe it only through colour-dominance
        // samples, and the background renders with the fill LIT shader — so a wrong Normal, a flipped
        // tangent w, or a wrong PatternCoord is invisible to them. This compares all four vertex streams,
        // the index buffer and the bounds.
        //
        // Comparing live against StyledFillTileBuilder.WriteGeometry would be self-referential: WriteGeometry
        // routes through FillMeshGraph.Schedule + ScheduleStreamWrite, the SAME path ScheduleWrite takes, so
        // the comparison would be green forever and prove nothing. Six per-stream SHA-256 digests, captured
        // from an INDEPENDENT WriteGeometry arm before that rewrite, are the frozen regression pin instead,
        // at the same per-stream granularity.
        //
        // RED: flip `w` to -1f in the tangent write, drop the Normal assignment, or un-reverse the index
        // 2nd/3rd swap — each reddens a named stream below.
        private const string FrozenGoldensFlat =
            "Stream0=DP8+lbaDnqnXo32FewHCvd56e5FR2172oWesZmac3I0= Stream1=ycx4JigRdmfDwxwHaeX4v7o3nJZToxUkEf1l5+erqP0= " +
            "Stream2=1RQ/sTquk3Cj4XlWFjd4hAT1L/dHby/JBckr7B8o5EA= Stream3=BqV3NujaZBIyhp8mGaJomHgLaTtc8qtEO5G+atpnKDA= " +
            "Indices=vj5j3bGOJy3YqLoQJ3LmWF5nLYcjDgBIY19HQFkmEJ8= Bounds=Cdbs9HaYIDzj0sqYENY9VmlRQjWy7EScMyRVMFObTr8=";
        private const string FrozenGoldensSpherical =
            "Stream0=nb1J+NsjJ4eZoKrXrFnxHV5mLD2q8dHfOYrelF0pm1Y= Stream1=ycx4JigRdmfDwxwHaeX4v7o3nJZToxUkEf1l5+erqP0= " +
            "Stream2=2JNye5+EaLy+vNY2MijWmE9aiunN9dm7SOJ5WPxbMvc= Stream3=BqV3NujaZBIyhp8mGaJomHgLaTtc8qtEO5G+atpnKDA= " +
            "Indices=vj5j3bGOJy3YqLoQJ3LmWF5nLYcjDgBIY19HQFkmEJ8= Bounds=Wu+l0hNvh4iI1J63SzYxcI29CAwAKW1iUgUmW8k63kc=";

        [Test]
        public void ScheduleWrite_MatchesFrozenGoldens_StreamForStream(
            [Values(false, true)] bool spherical)
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            IProjection projection = spherical
                ? (IProjection)new SphericalProjection()
                : new WebMercatorProjection();

            TileGeometryBuffers geometry = SingleTrianglePolygon(tile);
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent) { [0] = new Vector4(0.25f, 0.5f, 0.75f, 1f) };

            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = projection, Clip = TileBufferClip.KeepTileUnits(0.0),
            };

            FillGraphOutput output = default;
            MeshWriteOutput graphWrite = default;
            try
            {
                output = FillMeshGraph.Schedule(input);
                output.Handle.Complete();
                graphWrite = StyledFillTileBuilder.ScheduleWrite(output, featureColors, tile, geometry.Extent);
                graphWrite.Handle.Complete();

                Assert.Greater(graphWrite.VertexCount, 0,
                    "precondition: the graph must have produced real geometry — an empty mesh would pass while proving nothing.");

                Mesh.MeshData b = graphWrite.Mda[0];
                var b0 = b.GetVertexData<StyledFillTileBuilder.FillPositionNormal>(0);
                var b1 = b.GetVertexData<StyledFillTileBuilder.FillPatternUvBand>(1);
                var b2 = b.GetVertexData<Vector4>(2);
                var b3 = b.GetVertexData<Vector4>(3);
                NativeArray<int> bi = b.GetIndexData<int>();

                // The INTERIOR only — FillBandJob appends the outward boundary band to the same mesh, and
                // these goldens are the digest of what the independent WriteGeometry arm produced. See
                // FillMeshGraphParityTests' twin note: narrowing keeps the frozen constants byte-identical and
                // makes the claim stronger (the band perturbed nothing interior), where a re-bake would retire
                // a long-lived pin for a number nobody can review.
                //
                // TWO WAYS TO SAY "INTERIOR", because the two arms have different structure. On the flat arm
                // the band is a vertex SUFFIX and BandVertexCount measures it. On the curved arm subdivision
                // re-emits every vertex in traversal order, so band and interior interleave, no suffix
                // survives, and GlobeFillScatterJob clears the count rather than ship one that lies. The
                // discriminator there is the per-vertex attribute: a triangle is interior iff all three of
                // its vertices carry side 0. That is EXACT, not a tolerance — `side` is affine over a source
                // triangle, so its zero set is a line, and a sub-triangle with all three vertices on one line
                // has zero area and is never emitted.
                //
                // The digest is then taken over the interior vertices in ascending index order, with indices
                // renumbered to their rank among them. On the flat arm rank == index (the interior is the
                // prefix) and the triangle filter is the same "all three interior" test the index-range
                // comparison already was, so this is byte-identical to the form that captured the goldens.
                // Selected by the ARM, never by the data: a flat build that emitted no band would take the
                // curved filter, which drops index-unreferenced vertices from the digest and so moves a
                // frozen golden for a reason nobody would think to look for.
                int bandVertexSuffix = output.Counts[0].BandVertexCount;
                var isInterior = new bool[graphWrite.VertexCount];
                if (!spherical)
                {
                    for (int i = 0; i < graphWrite.VertexCount - bandVertexSuffix; i++) isInterior[i] = true;
                }
                else
                {
                    for (int i = 0; i + 2 < bi.Length; i += 3)
                    {
                        int ta = bi[i], tb = bi[i + 1], tc = bi[i + 2];
                        if (b1[ta].Band.z != 0f || b1[tb].Band.z != 0f || b1[tc].Band.z != 0f) continue;
                        isInterior[ta] = true; isInterior[tb] = true; isInterior[tc] = true;
                    }
                }

                var interiorRank = new int[graphWrite.VertexCount];
                var interiorIndices = new List<int>(graphWrite.VertexCount);
                for (int i = 0; i < graphWrite.VertexCount; i++)
                {
                    interiorRank[i] = interiorIndices.Count;
                    if (isInterior[i]) interiorIndices.Add(i);
                }
                Assert.Greater(interiorIndices.Count, 0,
                    "precondition: the interior must be non-empty, or every digest below is vacuous.");
                Assert.Less(interiorIndices.Count, graphWrite.VertexCount,
                    "precondition: the band must actually be present and excluded, or this tooth is hashing " +
                    "the whole mesh and calling it the interior.");

                var s0 = new List<byte>(); var s1 = new List<byte>(); var s2 = new List<byte>(); var s3 = new List<byte>();
                foreach (int i in interiorIndices)
                {
                    Vector3 p = b0[i].Position, n = b0[i].Normal;
                    s0.AddRange(BitConverter.GetBytes(p.x)); s0.AddRange(BitConverter.GetBytes(p.y)); s0.AddRange(BitConverter.GetBytes(p.z));
                    s0.AddRange(BitConverter.GetBytes(n.x)); s0.AddRange(BitConverter.GetBytes(n.y)); s0.AddRange(BitConverter.GetBytes(n.z));
                    // Only the UV half is hashed, deliberately: stream 1 gained the band attribute
                    // alongside it, and the frozen golden must stay the pattern coordinates' own digest so
                    // this tooth still says whether THEY changed.
                    Vector2 pc = b1[i].PatternUv;
                    s1.AddRange(BitConverter.GetBytes(pc.x)); s1.AddRange(BitConverter.GetBytes(pc.y));
                    Vector4 tan = b2[i];
                    s2.AddRange(BitConverter.GetBytes(tan.x)); s2.AddRange(BitConverter.GetBytes(tan.y));
                    s2.AddRange(BitConverter.GetBytes(tan.z)); s2.AddRange(BitConverter.GetBytes(tan.w));
                    Vector4 col = b3[i];
                    s3.AddRange(BitConverter.GetBytes(col.x)); s3.AddRange(BitConverter.GetBytes(col.y));
                    s3.AddRange(BitConverter.GetBytes(col.z)); s3.AddRange(BitConverter.GetBytes(col.w));
                }

                // Band triangles are interleaved after their own feature's interior triangles, so the filter
                // is on vertex index, not on position.
                var idxBytes = new List<byte>();
                for (int i = 0; i + 2 < bi.Length; i += 3)
                {
                    if (!isInterior[bi[i]] || !isInterior[bi[i + 1]] || !isInterior[bi[i + 2]]) continue;
                    idxBytes.AddRange(BitConverter.GetBytes(interiorRank[bi[i]]));
                    idxBytes.AddRange(BitConverter.GetBytes(interiorRank[bi[i + 1]]));
                    idxBytes.AddRange(BitConverter.GetBytes(interiorRank[bi[i + 2]]));
                }

                // Center/size (not raw min/max c0/c1) — matches the captured oracle's Bounds struct, which
                // stores (min+max)*0.5 / max-min, a different float bit pattern than the raw endpoints.
                float3x2 gb = graphWrite.Bounds[0];
                float3 boundsCenter = (gb.c0 + gb.c1) * 0.5f;
                float3 boundsSize   = gb.c1 - gb.c0;
                var boundsBytes = new List<byte>();
                boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.x)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.y)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.z));
                boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.x));   boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.y));   boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.z));

                string result =
                    $"Stream0={Sha256(s0)} Stream1={Sha256(s1)} Stream2={Sha256(s2)} Stream3={Sha256(s3)} " +
                    $"Indices={Sha256(idxBytes)} Bounds={Sha256(boundsBytes)}";
                string expected = spherical ? FrozenGoldensSpherical : FrozenGoldensFlat;
                Assert.AreEqual(expected, result,
                    $"[spherical={spherical}] the fill write graph's output no longer matches the frozen " +
                    "managed-WriteGeometry goldens — a real regression, not a re-bake candidate.");
            }
            finally
            {
                graphWrite.Dispose();
                output.Dispose();
                featureColors.Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── The built mesh is sized for interior + band ────────────────────────────────────────────────
        //
        // ScheduleStreamWrite sizes the Mesh.MeshData off the COMPLETED graph output's list lengths, not off
        // a kick-time bound, which is what makes the band's appended vertices counted automatically. If that
        // ever regresses to an interior-only total, SetVertexBufferParams undersizes and the write job runs
        // past the end of the mesh — silently in a release player, where NativeList's indexer bounds check is
        // compiled out. This is the standing check for that.
        //
        // RED: size the MeshData from (output.TileVertices.Length - Counts[0].BandVertexCount).
        [Test]
        public void ScheduleWrite_SizesTheMeshForTheBandAsWellAsTheInterior(
            [Values(false, true)] bool spherical)
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            IProjection projection = spherical
                ? (IProjection)new SphericalProjection()
                : new WebMercatorProjection();

            TileGeometryBuffers geometry = SingleTrianglePolygon(tile);
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent) { [0] = new Vector4(0.25f, 0.5f, 0.75f, 1f) };

            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = projection, Clip = TileBufferClip.KeepTileUnits(0.0),
            };

            FillGraphOutput output = default;
            MeshWriteOutput graphWrite = default;
            try
            {
                output = FillMeshGraph.Schedule(input);
                output.Handle.Complete();
                graphWrite = StyledFillTileBuilder.ScheduleWrite(output, featureColors, tile, geometry.Extent);
                graphWrite.Handle.Complete();

                int bandVertices = output.Counts[0].BandVertexCount;
                int bandIndices  = output.Counts[0].BandIndexCount;

                if (spherical)
                {
                    // The two scalars are ZERO on this arm and that is NOT "no band". They count a
                    // contiguous suffix, which subdivision destroys: GlobeFillSubdivideJob re-emits every
                    // vertex in traversal order, so band and interior interleave and GlobeFillScatterJob
                    // clears them rather than ship a suffix count that lies. Band-ness here is the
                    // per-vertex attribute, asserted directly below.
                    Assert.AreEqual(0, bandVertices,
                        "the curved arm reports no band SUFFIX — subdivision leaves none to report");
                    Assert.AreEqual(0, bandIndices, "and no band index suffix, for the same reason");
                }
                else
                {
                    // One ring of 3 vertices ⇒ 6 band vertices, and 6 band indices per edge that KEEPS its
                    // quad. This fixture runs at KeepTileUnits(0.0), so its window is [0,0]..[4096,4096] and
                    // the ring's (0,0)->(10,0) edge lies wholly on the y = 0 window line: the band stops
                    // there, as it does at every tile seam. (The ring is small enough to take RingClipJob's
                    // wholly-inside fast path, so that edge was never actually cut — the predicate is "both
                    // endpoints on one window line", which is deliberately the wider of the two readings.)
                    Assert.AreEqual(6, bandVertices,
                        "two band vertices per ring vertex, over the fixture's one 3-vertex ring — suppression " +
                        "drops a QUAD, never a vertex pair, so this count does not move with it");
                    Assert.AreEqual(12, bandIndices,
                        "six band indices for each of the two edges that keep their quad; the third runs along " +
                        "the window line");
                }

                Assert.AreEqual(output.TileVertices.Length, graphWrite.VertexCount,
                    "the mesh must be sized for every vertex the graph produced, band included — sizing it " +
                    "from the interior total writes past the end of the vertex buffer.");

                Mesh.MeshData md = graphWrite.Mda[0];
                Assert.AreEqual(output.TriangleIndices.Length, md.GetIndexData<int>().Length,
                    "and for every index, band triangles included.");

                // The write-job plumbing between the graph's band column and stream 1 — the gap the frozen
                // PatternUv digest deliberately does not cover. Two assertions instead of a digest: they name
                // what broke, which a hash never does.
                var stream1 = md.GetVertexData<StyledFillTileBuilder.FillPatternUvBand>(1);
                Assert.AreEqual(Vector3.zero, stream1[0].Band,
                    "an interior vertex's band bytes must be exactly zero in the MESH, not merely in the column " +
                    "— a Mesh.MeshData vertex buffer is not guaranteed zero-initialised.");
                for (int i = 0; i < output.VertexBand.Length; i++)
                {
                    float3 expected = output.VertexBand[i];
                    Assert.AreEqual(new Vector3(expected.x, expected.y, expected.z), stream1[i].Band,
                        $"vertex {i}'s band attribute did not reach stream 1 — the shader reads THIS, not the column.");
                }
                // Non-vacuity, and on the curved arm it is also the ONLY statement that the band reached the
                // mesh at all — there is no suffix count to read it off. The flat arm can say WHERE (the
                // band is the suffix, so the last vertex is a band vertex); the curved arm can only say
                // THAT, because subdivision interleaves them.
                int nonZeroBand = 0;
                for (int i = 0; i < graphWrite.VertexCount; i++)
                    if (stream1[i].Band != Vector3.zero) nonZeroBand++;
                Assert.Greater(nonZeroBand, 0,
                    "no vertex carries a non-zero band attribute, so the whole band is inert and every " +
                    "assertion above passes over zeros.");
                if (!spherical)
                    Assert.AreNotEqual(Vector3.zero, stream1[graphWrite.VertexCount - 1].Band,
                        "on the flat arm the band is the vertex suffix, so the LAST vertex must be one of " +
                        "its outer vertices.");
            }
            finally
            {
                graphWrite.Dispose();
                output.Dispose();
                featureColors.Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        private static string Sha256(List<byte> bytes)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return System.Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        // ── The three protocol guards, observed ────────────────────────────────────────────────────────
        //
        // _writeScheduled, _payloadsTaken and _disposed each exist to make a protocol violation loud rather
        // than silently wrong, and until these teeth existed all three could be deleted with the gate still
        // green. That is not academic: the missing DrainMeshBuilds guard found in review was a REAL second
        // take, and _payloadsTaken is the only reason it surfaced as an exception instead of a tile settling
        // with zero meshes registered.
        //
        // Deltas, never absolutes — the live counters are process-wide statics and an EditMode batch run is
        // one process.

        private static TileBuildGraph OneRealLayerGraph(out TileGeometryBuffers[] geometries)
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            LayerFixture fixture = RealLayerFixture(tile, "layer-a", materialIndex: 0);
            geometries = new[] { fixture.Geometry };
            TileBuildGraph graph = TileBuildGraph.ScheduleMeasure(new[] { fixture.Build }, default(TileGeometryBuffers));
            graph.Complete();
            return graph;
        }

        /// <summary>Frees ONLY what the graph does not own: each layer's own geometry, which is
        /// BORROWED (the graph disposes a build's own request columns itself, in its own <c>Dispose</c>, and
        /// never the geometry). Disposing it as well throws <c>ObjectDisposedException</c> — which is the
        /// ownership contract working, and how the first draft of these teeth failed.</summary>
        private static void DisposeGeometries(TileGeometryBuffers[] geometries)
        {
            foreach (TileGeometryBuffers g in geometries)
                g.Dispose();
        }

        [Test]
        public void CompleteMeasureAndScheduleWrite_CalledTwice_Throws()
        {
            TileBuildGraph graph = OneRealLayerGraph(out var geometries);
            try
            {
                graph.CompleteMeasureAndScheduleWrite(out _);
                Assert.Throws<InvalidOperationException>(() => graph.CompleteMeasureAndScheduleWrite(out _),
                    "a second write-schedule must throw. Without the guard it silently allocates a SECOND " +
                    "MeshDataArray per layer and overwrites the first, which leaks it — visible only as a " +
                    "counter drift nobody reads.");
            }
            finally { graph.Dispose(); DisposeGeometries(geometries); }
        }

        /// <summary>Used to pin a throw on a second take — DrainMeshBuilds/PumpPending held their own copy of
        /// the returned array in <c>LoadedTile.Payloads</c>, and a caller that took twice (a resumed budget-
        /// bound partial) would silently allocate + leak a second array without this guard. job-scheduling
        /// <see cref="TileBuildGraph"/> caches the array itself and
        /// hands the SAME instance back on every call, so <c>LoadedTile.Payloads</c> is gone and there is no
        /// second array to leak — a repeat call is a legitimate resume, not a protocol violation, and this
        /// tooth now pins THAT: same instance, not a second allocation, not a throw.</summary>
        [Test]
        public void CompleteWriteAndTakePayloads_CalledTwice_ReturnsTheSameArrayInstance()
        {
            TileBuildGraph graph = OneRealLayerGraph(out var geometries);
            try
            {
                graph.CompleteMeasureAndScheduleWrite(out _);
                graph.Complete();
                MeshDataPayload[] first  = graph.CompleteWriteAndTakePayloads();
                MeshDataPayload[] second = graph.CompleteWriteAndTakePayloads();
                Assert.AreSame(first, second,
                    "a repeat call must return the SAME array instance the graph cached on the first call — " +
                    "not a fresh allocation, and not a throw. A caller that resumes a budget-bound partial " +
                    "consume across Ticks (or that retries after its own consume loop threw) relies on this: " +
                    "getting back a different array would desync from whichever slots an earlier call already " +
                    "nulled, and getting a throw would leave the tile permanently stuck (the defect this " +
                    "replaced — see CompleteWriteAndTakePayloads_RecoversAfterTheCallerLosesItsFirstReference, " +
                    "below).");
            }
            finally { graph.Dispose(); DisposeGeometries(geometries); } // graph.Dispose() sweeps the cached array itself
        }

        /// <summary><b>The actual defect this stage's Group 0.5 fixed, reproduced.</b> Both real
        /// <c>TileManager</c> consume sites used to take the array into a LOCAL (<c>LoadedTile.Payloads</c> on
        /// the pump's local copy) and only store it back to <c>_loaded</c> afterward — so a throw between the
        /// take and the store (a backend <c>AddTileLayer</c>, a mesh apply) lost the local while
        /// <c>_payloadsTaken</c> stayed permanently set, and every later Tick's retry hit the old
        /// <c>InvalidOperationException</c> guard forever, leaking the taken array (nothing else referenced
        /// it). Reproduced here at the level the fix actually lives — the caller drops its first reference
        /// exactly as an interrupted store would, then "retries on a later Tick" by calling again.</summary>
        [Test]
        public void CompleteWriteAndTakePayloads_RecoversAfterTheCallerLosesItsFirstReference()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;
            TileBuildGraph graph = OneRealLayerGraph(out var geometries);
            try
            {
                graph.CompleteMeasureAndScheduleWrite(out _);
                graph.Complete();

                // "The caller's local copy is lost before it can be stored" — call once and let the result
                // fall out of scope unused, exactly as a throw between take-and-store would.
                _ = graph.CompleteWriteAndTakePayloads();
                Assert.Greater(MeshDataPayload.DebugLiveAllocCount, baseline,
                    "precondition: the first take must have produced a real, still-live array — otherwise " +
                    "the retry below proves nothing about recovery.");

                // "Retries on a later Tick" — must NOT throw, and must get back a real, usable array rather
                // than an empty stand-in (the recovery, not just a non-throwing no-op).
                MeshDataPayload[] retried = null;
                Assert.DoesNotThrow(() => retried = graph.CompleteWriteAndTakePayloads(),
                    "a caller that lost its first reference must recover on a later call, not be stuck " +
                    "throwing InvalidOperationException forever (the defect this replaced).");
                Assert.AreEqual(1, retried.Length, "the recovered array must still hold the real layer's payload.");

                foreach (var p in retried) p.Dispose();
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "disposing the RECOVERED array must free the SAME underlying allocation the lost first " +
                    "call produced — proving the fix does not leak the caller's dropped reference.");
            }
            finally { graph.Dispose(); DisposeGeometries(geometries); }
        }

        [Test]
        public void Dispose_CalledTwice_DecrementsTheLiveCountExactlyOnce()
        {
            long baseline = TileBuildGraph.DebugLiveCount;
            long negativesBefore = TileBuildGraph.DebugNegativeObservations;
            TileBuildGraph graph = OneRealLayerGraph(out var geometries);
            Assert.AreEqual(baseline + 1, TileBuildGraph.DebugLiveCount,
                "precondition: scheduling must have raised the live count, or the assertions below are vacuous.");

            graph.Dispose();
            graph.Dispose(); // the redundant sweep TileManager.DisposeWholePayloads' idiom really performs

            Assert.AreEqual(baseline, TileBuildGraph.DebugLiveCount,
                "two Dispose() calls must decrement exactly once — an unguarded second decrement drives the " +
                "count NEGATIVE, and a later real leak would then bring it back through zero, so the pen and " +
                "teardown teeth would read green on a leak.");
            Assert.AreEqual(negativesBefore, TileBuildGraph.DebugNegativeObservations,
                "and it must never have been observed below zero along the way.");

            DisposeGeometries(geometries);
        }

        // ── Tooth (c): the decode reference is released ONLY
        // by TileBuildGraph.Dispose(), after Complete() ──────────────────────────────────────────────────

        /// <summary>A minimal <see cref="ITileLayer"/> wrapping a raw <see cref="TileGeometryBuffers"/>
        /// directly — tooth (c)'s own fixture. Avoids <c>InMemoryTileLayer</c>'s MVT command-stream
        /// re-materialization: the geometry only needs to exist and be disposed on cue by the DECODED
        /// TILE, not decode anything.</summary>
        private sealed class RawGeometryTileLayer : ITileLayer
        {
            public string Name { get; }
            public uint Extent => 4096;
            public IReadOnlyList<IFeature> Features => Array.Empty<IFeature>();
            public TileGeometryBuffers Geometry { get; private set; }

            public RawGeometryTileLayer(string name, TileGeometryBuffers geometry)
            {
                Name = name;
                Geometry = geometry;
            }

            public void Dispose()
            {
                Geometry.Dispose();
                Geometry = default;
            }
        }

        private sealed class RawGeometryDecodedTile : IDecodedTile
        {
            private readonly RawGeometryTileLayer _layer;
            public RawGeometryDecodedTile(RawGeometryTileLayer layer) => _layer = layer;
            public ITileLayer GetLayer(string name) => name == _layer.Name ? _layer : null;
            public void Dispose() => _layer.Dispose();
        }

        /// <summary>Tooth (c)'s own disposal-counting instrument — counts <see cref="Dispose"/> calls so
        /// the test can assert "disposed exactly once" directly, without relying on GC/finalizer timing.</summary>
        private sealed class CountingDecodedTile : IDecodedTile
        {
            private readonly IDecodedTile _inner;
            public int DisposeCount;
            public CountingDecodedTile(IDecodedTile inner) => _inner = inner;
            public ITileLayer GetLayer(string name) => _inner.GetLayer(name);
            public void Dispose() { DisposeCount++; _inner.Dispose(); }
        }

        /// <summary>The decode reference is released ONLY by <see cref="TileBuildGraph.Dispose"/>, after
        /// <see cref="TileBuildGraph.Complete"/> — never earlier. A <see cref="SharedDisposable{T}"/> over a
        /// disposal-counting <see cref="IDecodedTile"/>; the build borrows its layer's <c>Geometry</c>
        /// (<c>Clip</c> set as production passes it — NIT 7); <c>ScheduleMeasureFromDecode(builds, decode,
        /// delayHandle)</c>, this test holding no OTHER reference to <c>decode</c>. Held genuinely in-flight
        /// on <see cref="SpinUntilGateJob"/> (job-scheduling-design.md) — never disposed while
        /// the delay holds; release the gate; <c>graph.Dispose()</c> → disposed exactly once.</summary>
        [Test]
        public void DecodeReference_ReleasedOnlyByGraphDispose_AfterComplete()
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            const string layerName = "layer-a";

            TileGeometryBuffers geometry = SingleTrianglePolygon(tile);
            var rawLayer = new RawGeometryTileLayer(layerName, geometry);
            var counting = new CountingDecodedTile(new RawGeometryDecodedTile(rawLayer));
            var decode = new SharedDisposable<IDecodedTile>(counting);

            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent) { [0] = Vector4.one };
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(),
                Clip = TileBufferClip.KeepTileUnits(0.0), // matches production (KickSourcelessBackground)
            };
            var builds = new ILayerMeshBuild[] { FillLayerBuild.Rent(input, featureColors, materialIndex: 0, payloadName: layerName) };

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;
            TileBuildGraph graph = null;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();

                graph = TileBuildGraph.ScheduleMeasureFromDecode(builds, decode, delayHandle);

                DelayGateJobInstrument.WaitForStart(started);
                Assert.AreEqual(0, counting.DisposeCount,
                    "precondition: the decode must not be disposed while the delay job holds its measure " +
                    "graph — the assertion below would be vacuous if it already was.");

                gate[0] = 1; // release — the measure job (and everything depending on it) proceeds
                graph.Dispose(); // Complete()s the handle first, then releases the decode reference — LAST

                Assert.AreEqual(1, counting.DisposeCount,
                    "the decode reference must be disposed EXACTLY ONCE, by TileBuildGraph.Dispose(), after " +
                    "Complete() — never earlier (the measure job still held it), never twice.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                graph?.Dispose(); // idempotent — a no-op if the happy path already ran it
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
                // Nothing else to free by hand: on the happy path graph.Dispose() already swept the
                // build's own columns and released decode, which disposed the
                // layer's geometry through CountingDecodedTile → RawGeometryDecodedTile → RawGeometryTileLayer.
            }
        }

        /// <summary>Positive control for the tooth above: releasing the decode reference OURSELVES — not
        /// through <see cref="TileBuildGraph.Dispose"/> — while the delay job still holds the measure step
        /// frees the geometry's NativeArrays out from under a job that still declares them <c>[ReadOnly]</c>.
        /// Unity's own <c>NativeContainer</c> safety system must catch this as a hard fault: confirms the
        /// "graph, not the caller, owns this reference" contract is load-bearing, not merely documented.
        /// <para><b>RED:</b> moving <c>TileBuildGraph.Dispose</c>'s
        /// <c>_decode?.Release()</c> above <c>_handle.Complete()</c> would make THIS release (already
        /// early, by construction) into the NORMAL shape every pen path takes — this test's own throw is
        /// the demonstration of what that reordering would inflict on every caller, not just this one.</para></summary>
        [Test]
        public void DecodeReference_ReleasedEarlyByCaller_ThrowsFromTheSafetySystem()
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            const string layerName = "layer-a";

            TileGeometryBuffers geometry = SingleTrianglePolygon(tile);
            var rawLayer = new RawGeometryTileLayer(layerName, geometry);
            var decode = new SharedDisposable<IDecodedTile>(new RawGeometryDecodedTile(rawLayer));

            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent) { [0] = Vector4.one };
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(),
                Clip = TileBufferClip.KeepTileUnits(0.0),
            };
            var builds = new ILayerMeshBuild[] { FillLayerBuild.Rent(input, featureColors, materialIndex: 0, payloadName: layerName) };

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;
            TileBuildGraph graph = null;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();

                graph = TileBuildGraph.ScheduleMeasureFromDecode(builds, decode, delayHandle);
                DelayGateJobInstrument.WaitForStart(started);

                // The positive control: release OURSELVES, bypassing TileBuildGraph.Dispose() — the
                // reference this test holds is the wrapper's creator reference, the same one
                // ScheduleMeasureFromDecode took, so this is an EXTRA release the graph never authorized.
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => decode.Release(),
                    "releasing the decode reference while the graph's measure job still holds it as a " +
                    "[ReadOnly] input must throw from Unity's own NativeContainer safety system — the " +
                    "ownership contract is load-bearing, not merely documented.");
                Assert.IsNotEmpty(ex.Message,
                    "the safety system's message is engine-internal wording, not this repo's contract to " +
                    "pin (Unity 6000.x observed: a WriteOnly/ReadOnly container disposed while a scheduled " +
                    "job still references it) — only its non-empty presence is asserted.");
            }
            finally
            {
                // Clean-up: release the delay, Complete() the handle, then
                // dispose the raw arrays BY HAND — the geometry's own IsCreated already flipped false before
                // the safety system's throw above, so it needs no further disposal (and a bare
                // geometry.Dispose() here would itself throw). graph.Dispose() is deliberately NOT called:
                // it would try to release decode a SECOND time (a double-release the safety system would
                // also catch). The accepted cost of a positive control that demonstrates the unsafe path:
                // (a) the build's own rent DOES count — FillLayerBuild.Rent is unconditional, unlike the
                // retired LayerRequest.Create's object-initializer
                // bypass — so LayerMeshBuildCounters.DebugLiveBuilds stays elevated by one for the rest of this batch
                // process, alongside TileBuildGraph.DebugLiveCount; harmless, every tooth in this suite reads
                // its own counters as a DELTA against a baseline captured in its own body, never an absolute.
                // (b) a REAL leak, not just a
                // stale counter — the measure graph's own Allocator.Persistent output buffers are never
                // freed, because the only path that frees them is graph.Dispose(), which this test cannot
                // safely call. Every other tooth in this suite reads its own counters as a DELTA, so the
                // stale DebugLiveCount is harmless to them; the buffer leak is real but scoped to this one
                // process's remaining test run.
                gate[0] = 1;
                delayHandle.Complete();
                graph?.Complete();
                visitOrder.Dispose();
                featureColors.Dispose();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── T4 (per-layer build-object stage): the ownership contract, terminal-before-request-columns arm ──
        //
        // The extrusion build is the one case where a REORDER inside Dispose(), not a deletion, is the
        // falsifiable defect: FillExtrusionLayerBuild.Dispose() must complete the extrusion's own terminal
        // (_ext.Dispose(), which is FillExtrusionGraphOutput.Dispose()'s Handle.Complete() → Roof.Dispose()
        // → Walls.Dispose()) BEFORE freeing the request columns (FeatureColors/FeatureBake/Input.RingVisitOrder)
        // those in-flight wall jobs still hold [ReadOnly]. This is the exact defect the last stage's plan
        // review caught; it is now a tooth instead of a review finding.

        /// <summary>Disposes a genuinely in-flight <see cref="FillExtrusionLayerBuild"/> directly (not
        /// through a <see cref="TileBuildGraph"/>) — held on <see cref="SpinUntilGateJob"/>
        /// (job-scheduling-design.md), released immediately before <c>Dispose()</c> so
        /// <c>Handle.Complete()</c> unblocks fast rather than spinning the whole bound. Must NOT throw: the
        /// correct order completes the wall/roof terminal first.
        ///
        /// <para><b>RED (falsifiable by a REORDER, not a deletion):</b> in
        /// <c>FillExtrusionLayerBuild.Dispose()</c>, move <c>_input.RingVisitOrder.Dispose(); _featureColors.Dispose();
        /// _featureBake.Dispose();</c> ABOVE <c>_ext.Dispose();</c> — the wall chain's jobs still declare
        /// those three <c>[ReadOnly]</c>, so freeing them before <c>_ext.Dispose()</c>'s own
        /// <c>Handle.Complete()</c> throws from Unity's own <c>NativeContainer</c> safety system. Executed
        /// and reverted.</para></summary>
        [Test]
        public void FillExtrusionLayerBuild_Dispose_CompletesItsOwnTerminal_BeforeFreeingRequestColumns()
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            TileGeometryBuffers geometry = SingleTrianglePolygon(tile);
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent) { [0] = Vector4.one };
            var featureBake = new NativeArray<Vector2>(1, Allocator.Persistent) { [0] = new Vector2(0f, 50f) };
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(),
                Clip = TileBufferClip.KeepTileUnits(0.0),
            };

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;
            ILayerMeshBuild build = null;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();

                build = FillExtrusionLayerBuild.Rent(input, featureColors, featureBake, materialIndex: 0, payloadName: "ext");
                build.ScheduleMeasure(delayHandle);

                DelayGateJobInstrument.WaitForStart(started);

                gate[0] = 1; // release — Handle.Complete() inside Dispose() unblocks immediately
                Assert.DoesNotThrow(() => build.Dispose(),
                    "Dispose() must complete the extrusion's own terminal handle before freeing the request " +
                    "columns wall jobs still hold [ReadOnly] — a reorder throws from the safety system.");
                build = null; // disposed — the finally below must not double-dispose it
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                build?.Dispose();
                geometry.Dispose(); // BORROWED by the build — never freed by its own Dispose()
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileLayerProcessorRunnerTests — decodes fetched bytes once; the old fault policy is now inert
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves
    /// <see cref="TileLayerProcessorRunner.RunWorkerPass"/> decodes the fetched bytes exactly once and
    /// shares that same <see cref="IDecodedTile"/> reference across every processor in dense order, and that
    /// the fault policy (abort-on-first-fault, settle-every-processor) survives the move unchanged.
    /// The decoder is injected, not hardcoded, and the decode happens BEFORE the lease exists (at the
    /// source's <c>GetTile</c>), so these fixtures mint one the same way production does — decode, then wrap
    /// — and release in a <c>finally</c>, mirroring the kick lambda.
    /// </summary>
    [TestFixture]
    public class TileLayerProcessorRunnerTests
    {
        /// <summary>The ONE address these teeth use — the decode's id and the context's tile are
        /// the same thing now, so a fixture that let them drift would be building the mispairing C1 removes.</summary>
        private static readonly TileId ContextTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = ContextTile,
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>Records ProcessOnWorker invocations (order + the observed decoded-tile reference) into a
        /// SHARED log so a test can assert cross-processor invocation order/identity, and counts
        /// Release() calls so a test can assert exactly-once settlement.
        ///
        /// <para><see cref="TryTakeGraphRequest"/> hands
        /// back <see cref="Build"/>, a stub <see cref="ILayerMeshBuild"/> that owns nothing (the
        /// per-layer build-object stage): the interface's four members drop <c>MaterialIndex</c>, so the
        /// dense-slot contract the runner's settle loop actually produces (job-scheduling-design.md) is
        /// now observed as reference IDENTITY (<c>Assert.AreSame(processor.Build, output.Layers[i])</c>),
        /// not a carried field. This fake owns no native columns and is never rented from
        /// <c>LayerMeshBuildPool</c>, so it must never be <c>Dispose</c>d as though it did — unlike every
        /// other <see cref="ILayerMeshBuild"/> in the tree.</para></summary>
        private sealed class RecordingProcessor : ITileMeshLayerProcessor
        {
            /// <summary>A stub <see cref="ILayerMeshBuild"/> owning nothing — see this outer type's own doc.</summary>
            private sealed class StubBuild : ILayerMeshBuild
            {
                public JobHandle ScheduleMeasure(JobHandle deps) => default;
                public bool TryScheduleWrite(out JobHandle writeHandle) { writeHandle = default; return false; }
                public MeshDataPayload TakePayload() => null;
                public void Dispose() { }
            }

            private readonly int _order;
            private readonly List<(int order, IDecodedTile tile)> _log;
            private readonly bool _throwOnProcess;

            public int ReleaseCallCount { get; private set; }

            public LayerPhase Phase { get; }

            /// <summary>The stub <see cref="TryTakeGraphRequest"/> hands back — captured so a test can assert
            /// dense-slot IDENTITY against <c>output.Layers[i]</c> directly, by reference.</summary>
            public ILayerMeshBuild Build { get; } = new StubBuild();

            public RecordingProcessor(int order, List<(int order, IDecodedTile tile)> log,
                LayerPhase phase = LayerPhase.WorkerOnly, bool throwOnProcess = false)
            {
                _order          = order;
                _log            = log;
                Phase           = phase;
                _throwOnProcess = throwOnProcess;
            }

            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
            {
                if (_throwOnProcess)
                    throw new InvalidOperationException("RecordingProcessor deliberate fault (test)");
                _log.Add((_order, tile));
            }

            public bool TryTakeGraphRequest(out ILayerMeshBuild build)
            {
                build = Build;
                return true;
            }

            public void Release() => ReleaseCallCount++;
        }

        /// <summary>A tile layer that counts how many times its <c>Features</c> list is read, and — like a
        /// decoded layer — OWNS its geometry, materialized once at construction. The counter
        /// is how "the geometry read costs no Features walk" becomes observable without putting a test-only
        /// member on a production class.</summary>
        private sealed class CountingTileLayer : ITileLayer, IDisposable
        {
            private readonly IReadOnlyList<IFeature> _features;
            public int FeaturesReadCount { get; private set; }

            public CountingTileLayer(string name, IReadOnlyList<IFeature> features, TileId tile)
            {
                Name = name;
                _features = features;
                var kinds    = new List<TileGeometryType>();
                var commands = new List<uint[]>();
                for (int i = 0; i < features.Count; i++)
                {
                    kinds.Add(features[i].GeometryType);
                    commands.Add((features[i] as ITileCommandStreamFeature)?.Geometry);
                }
                // Deliberately NOT counted as a Features read: the buffer is built here, once, exactly as the
                // decoder builds a real layer's — so any read the runner performs is the runner's own.
                Geometry = MvtGeometryMaterializerTestFactory.Materialize(tile, Extent, kinds, commands);
            }

            public string Name   { get; }
            public uint   Extent => 4096;
            public TileGeometryBuffers Geometry { get; private set; }

            public IReadOnlyList<IFeature> Features
            {
                get { FeaturesReadCount++; return _features; }
            }

            public void Dispose() { TileGeometryBuffers g = Geometry; g.Dispose(); Geometry = default; }
        }

        private sealed class OneLayerDecodedTile : IDecodedTile
        {
            private readonly ITileLayer _layer;
            public OneLayerDecodedTile(ITileLayer layer) => _layer = layer;
            public ITileLayer GetLayer(string name) => name == _layer.Name ? _layer : null;
            public void Dispose() => (_layer as IDisposable)?.Dispose();
        }

        /// <summary>A render layer that records that it was reached and builds no graph request — the store
        /// call in <see cref="TileMeshLayerProcessor.ProcessOnWorker"/> happens BEFORE this, so a no-op body
        /// still exercises the memo. <c>WriteIntoCallCount</c> is now <see cref="BuildGraphRequestCallCount"/>,
        /// returning <c>default</c> (<c>HasWork == false</c>, nothing to dispose).</summary>
        private sealed class NoGeometryTileMeshRenderLayer : ITileMeshRenderLayer
        {
            public int BuildGraphRequestCallCount { get; private set; }

            public NoGeometryTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public StyleLayer       StyleLayer      { get; }
            public RenderLayerBuild Build           => RenderLayerBuild.TileMesh;
            public DrawPersistence  Persistence     => DrawPersistence.Persistent;
            public int              DrawIndex       => 0;
            public LayerSubSlot     MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material         Material        => null;
            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public int TransitioningCount => 0;
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
            {
                BuildGraphRequestCallCount++;
                Assert.IsTrue(geometry.IsCreated, "the processor must hand BuildGraphRequest a live shared buffer");
                return null;
            }
        }

        /// <summary>
        /// T4a — one source-layer is materialized <b>once per worker pass</b>, however many style
        /// layers name it. This is the tooth that would have caught the shape the shared buffer fixes: three fill
        /// layers over one source-layer used to decode its geometry three times.
        ///
        /// <para><b>How to read the number.</b> The layer already holds its buffer, so obtaining geometry
        /// reads <c>Features</c> <b>zero</b> times and the count is exactly N. The assertion is therefore
        /// "<b>exactly</b> the layer count, not one more" — and it is discriminating in that direction: any
        /// consumer that re-derived geometry from the feature list (a re-materializing property, a
        /// resurrected per-pass store) would push it above N.</para>
        /// </summary>
        [Test]
        public void RunWorkerPass_ObtainsGeometryWithoutReReadingTheFeatureList()
        {
            const string SourceLayerName = "shared-source";
            const int    LayerCount      = 3;

            // One 300-unit square, well inside the tile: real rings, so the store really materializes.
            var feature = new DictionaryFeature(
                properties: null,
                geometryType: TileGeometryType.Polygon,
                hasId: false,
                geometry: new uint[]
                {
                    (1u << 3) | 1u, 200u, 200u,       // MoveTo (100, 100)
                    (3u << 3) | 2u, 600u, 0u,         // LineTo +300, 0
                                    0u,   600u,       // LineTo 0, +300
                                    599u, 0u,         // LineTo -300, 0
                    (1u << 3) | 7u,                   // ClosePath
                });
            var sourceLayer = new CountingTileLayer(SourceLayerName, new IFeature[] { feature }, ContextTile);
            // Was a FixedDecodeHandle test double — "a handle over an ALREADY-BUILT tile". That is what a
            // lease now is, so the double is gone and this uses the production type.
            var handle      = new SharedDisposable<IDecodedTile>(new OneLayerDecodedTile(sourceLayer));

            var renderLayers = new NoGeometryTileMeshRenderLayer[LayerCount];
            var processors   = new ITileMeshLayerProcessor[LayerCount];
            for (int i = 0; i < LayerCount; i++)
            {
                renderLayers[i] = new NoGeometryTileMeshRenderLayer(new StyleLayer
                {
                    Id = $"fill-{i}", Source = "src", SourceLayer = SourceLayerName,
                });
                processors[i] = TileMeshLayerProcessor.AllocateForKick(renderLayers[i], materialIndex: i);
            }

            var context = MakeContext();
            TilePrologueOutput output = TileLayerProcessorRunner.RunWorkerPass(handle, in context, processors);

            try
            {
                // Non-vacuity: all three layers really ran and really received geometry. Without this, a pass
                // that faulted on the first processor would report a low count and pass.
                for (int i = 0; i < LayerCount; i++)
                    Assert.AreEqual(1, renderLayers[i].BuildGraphRequestCallCount,
                        $"precondition: layer {i} must have been reached with a live buffer");

                Assert.AreEqual(LayerCount, sourceLayer.FeaturesReadCount,
                    $"the source layer's Features must be read exactly {LayerCount} times — once per style " +
                    "layer by FeatureSelector (each has its own filter) and NOT AT ALL to obtain geometry, " +
                    "which the layer already owns. A consumer that re-derived the buffer from the " +
                    $"feature list would read it {LayerCount * 2} times and decode the same geometry once per " +
                    "layer — the 108-materializations-per-tile shape this tooth exists to catch.");
            }
            finally
            {
                for (int i = 0; i < output.Layers.Length; i++) output.Layers[i]?.Dispose();
                // The LEASE owns the decoded tile, and the decoded tile owns this source layer — so the one
                // release below is what disposes it. Disposing `sourceLayer` here directly and leaving the
                // lease live was a borrower freeing its lender's buffers and then a live owner sitting
                // around an already-disposed tile: two ownership violations for one missing line.
                handle.Release();
            }
        }

        // ── Primary semantic tooth ────────────────────────────────────────────────────────────────────

        /// <summary>The slot-join half that used to read <c>payloads[i].MaterialIndex</c> off a
        /// <c>FakePayload</c> the runner wrapped, then <c>output.Layers[i].MaterialIndex</c> directly, is now
        /// observed as reference IDENTITY (<c>output.Layers[i]</c> IS the processor's own build) — the
        /// four-member <see cref="ILayerMeshBuild"/> interface drops <c>MaterialIndex</c> entirely, so
        /// this is the dense-slot contract job-scheduling-design.md specifies, observed on the
        /// array production uses.</summary>
        [Test]
        public void RunWorkerPass_InvokesEveryProcessorOnceInDenseOrder_WithTheSameDecodedTile()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, log);
            var p1 = new RecordingProcessor(1, log);
            var p2 = new RecordingProcessor(2, log);
            var processors = new ITileMeshLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(3, log.Count, "every processor must be invoked exactly once");
            Assert.AreEqual(0, log[0].order, "dense order 0 first");
            Assert.AreEqual(1, log[1].order, "dense order 1 second");
            Assert.AreEqual(2, log[2].order, "dense order 2 third");

            Assert.IsNotNull(log[0].tile, "the decoded tile must be non-null");
            Assert.AreSame(log[0].tile, log[1].tile, "every processor must observe the SAME decoded tile reference");
            Assert.AreSame(log[0].tile, log[2].tile, "every processor must observe the SAME decoded tile reference");

            Assert.AreEqual(3, output.Layers.Length, "one request slot per input slot");
            Assert.AreSame(p0.Build, output.Layers[0], "dense-slot identity (job-scheduling-design.md §3.1)");
            Assert.AreSame(p1.Build, output.Layers[1], "dense-slot identity (job-scheduling-design.md §3.1)");
            Assert.AreSame(p2.Build, output.Layers[2], "dense-slot identity (job-scheduling-design.md §3.1)");
        }

        // ── Fault-parity: the fan-out read faults ─────────────────────────────────────────────────────
        //
        // RunWorkerPass_WhenTheDecodedTileReadFaults_InvokesNoProcessors_ButCompletesEveryPayload is
        // RETIRED here, not "made to pass". It drove the "a fault AT THE FAN-OUT READ invokes no
        // processors" half of the
        // fault policy through DecodedTileLease's release-then-read ObjectDisposedException — thrown from
        // `decode.Tile` BEFORE the processor loop even starts. SharedDisposable<T> is undefended by design
        // (no throw after the last Release()), so `decode.Value` after release just hands back the
        // (disposed) instance and the loop proceeds to RecordingProcessor — a fake that never reads native
        // memory — which then runs to completion instead of faulting: the anti-vacuity assertion this tooth
        // opened with can no longer be satisfied, and the read-fault site it existed to drive is gone.

        // ── Fault-parity: a processor throws ──────────────────────────────────────────────────────────

        /// <summary>Mechanically preserved (<c>ReleaseCallCount</c>/<c>output.Layers.Length</c>), but
        /// its STATED REASON is rewritten — the old text said "no stranded array"; after B.3 no array is
        /// allocated at kick, so nothing can be stranded. What is actually at stake now is that every
        /// processor is returned to <see cref="TileMeshLayerProcessorPool"/> exactly once and no graph
        /// request is left un-taken.</summary>
        [Test]
        public void RunWorkerPass_WhenAProcessorThrows_StopsLaterProcessors_ButReleasesEveryProcessor()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, log);
            var p1 = new RecordingProcessor(1, log, throwOnProcess: true);
            var p2 = new RecordingProcessor(2, log);
            var processors = new ITileMeshLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(1, log.Count, "only the processor BEFORE the fault runs");
            Assert.AreEqual(0, log[0].order);

            Assert.AreEqual(1, p0.ReleaseCallCount, "p0 (ran normally) still settles exactly once");
            Assert.AreEqual(1, p1.ReleaseCallCount, "p1 (threw) still settles exactly once");
            Assert.AreEqual(1, p2.ReleaseCallCount, "p2 (never invoked) still settles exactly once — every processor returns to its pool");

            Assert.AreEqual(3, output.Layers.Length);
        }

        // ── The fault is VISIBLE, not just survivable ─────────────────────────────────────────────────
        //
        // RunWorkerPass' catch settles every processor as zero-vertex and carries on — correct, and
        // deliberately unchanged. What it must not do is stay SILENT: unrelated faults land in that one
        // catch and produce the identical invisible outcome, and one of them is the ObjectDisposedException
        // the lease raises when its tile is read after the last reference went — which the lease chose
        // precisely so a use-after-free would be loud. The symbol cadence already logs; these pin that the
        // mesh cadence, with 100+ layers behind it, does too — and that the log NAMES THE TILE, which is
        // the only thing that makes the warning actionable when many tiles are in flight.

        /// <summary>A distinctive address, so "the warning names the tile" cannot be satisfied by a zero
        /// that could have come from anywhere. Decode id and context tile stay the same value (this file's
        /// convention — the second address copy is gone).</summary>
        private static readonly TileId NamedTile = new TileId { Z = 9, X = 274, Y = 168 };

        private static TileLayerProcessContext MakeNamedContext() => new TileLayerProcessContext
        {
            Tile             = NamedTile,
            Zoom             = 9.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        [Test]
        public void RunWorkerPass_WhenAProcessorThrows_LogsAWarningNamingTheTile()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"9/274/168"));

            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, log, throwOnProcess: true);
            var processors = new ITileMeshLayerProcessor[] { p0 };
            var context = MakeNamedContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(NamedTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            // Settle-everything behaviour is UNCHANGED by the log — asserted here so a future "simplify the
            // catch" cannot trade the fault policy for the diagnostic.
            Assert.AreEqual(1, p0.ReleaseCallCount, "the faulting processor still settles exactly once");
            Assert.AreEqual(1, output.Layers.Length);
            Assert.AreSame(p0.Build, output.Layers[0], "the faulting processor's slot still carries its own build");
        }

        // RunWorkerPass_ReadingAReleasedLease_LogsAWarningNamingTheTile is
        // RETIRED alongside its sibling above, for the identical reason — it drove the SAME
        // release-then-read fault, over a NAMED tile, to pin that the runner's warning names the tile even
        // on this fault (not just a processor throw). With the read no longer able to fault, there is
        // nothing left for that log-message assertion to observe; RunWorkerPass_WhenAProcessorThrows_
        // LogsAWarningNamingTheTile (above) still pins the "log names the tile" property on the fault that
        // DOES still reach this runner.

        // ── The runner does not choreograph WorkerThenMain ────────────────────────────────────────────

        [Test]
        public void RunWorkerPass_WorkerThenMainProcessor_IsNotInvoked_AndStillSettles()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, log, phase: LayerPhase.WorkerThenMain);
            var processors = new ITileMeshLayerProcessor[] { p0 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(0, log.Count, "the runner must never run a WorkerThenMain processor's worker step");
            Assert.AreEqual(1, p0.ReleaseCallCount, "the reserved-phase processor must still settle (no stranded array)");
            Assert.AreEqual(1, output.Layers.Length);
            Assert.AreSame(p0.Build, output.Layers[0]);
        }

        // ── TileMeshLayerProcessor settlement (real adapter, not a recording fake) ────────────────────

        /// <summary>A minimal graph-arm layer whose <see cref="BuildGraphRequest"/> rents a REAL
        /// <see cref="FillLayerBuild"/> (<c>FillLayerBuild.Rent</c> — increments
        /// <see cref="LayerMeshBuildCounters.DebugLiveBuilds"/>) — the non-vacuity witness required: a build
        /// that can actually make the counter rise, so "returns to baseline" is a real property rather than
        /// trivially true on an empty ledger.</summary>
        private sealed class CountedGraphInputRenderLayer : ITileMeshRenderLayer
        {
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material Material => null;
            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public int TransitioningCount => 0;
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public CountedGraphInputRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
                => FillLayerBuild.Rent(
                    new FillMeshPipeline.LayerInput
                    {
                        Geometry     = geometry,
                        RingVisitOrder = new NativeArray<int>(1, Allocator.Persistent),
                        OriginRender = context.TileOriginRender,
                        Projection   = context.Projection,
                    },
                    new NativeArray<Vector4>(1, Allocator.Persistent), materialIndex, payloadName);
        }

        /// <summary>A minimal graph-arm layer whose <see cref="BuildGraphRequest"/> always throws — proves
        /// <see cref="TileMeshLayerProcessor"/>'s settlement path on a real (not recording-fake) processor,
        /// on the graph-arm fault site that replaces the retired seam-arm <c>WriteInto</c> fault.</summary>
        private sealed class ThrowingGraphInputRenderLayer : ITileMeshRenderLayer
        {
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material Material => null;
            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public int TransitioningCount => 0;
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public ThrowingGraphInputRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
                => throw new InvalidOperationException("ThrowingGraphInputRenderLayer deliberate fault (test)");
        }

        /// <summary>Replaces
        /// <c>TileMeshLayerProcessor_FaultingWrite_ReturnsEmptyPayload_AndReleasesTrackedMeshData</c>. Its
        /// (a)-(d) assertions (a tracked <c>MeshDataArray</c>, a zero-vertex settle, the material index
        /// surviving the fault, no native leak on that array) lose their subject after B.3 deletes the
        /// kick-time allocation — a legitimate retirement, but the HAZARD CLASS this test guarded does not
        /// cease to exist, it RELOCATES: a fault inside a per-layer body now strands the graph build's own
        /// <c>Allocator.Persistent</c> columns instead, parked in <c>TileMeshLayerProcessor._build</c> —
        /// a field whose own doc calls its <c>Reset</c> dispose a "never-fired backstop". This test observes
        /// THAT relocated hazard directly via <see cref="Meshing.LayerMeshBuildCounters.DebugLiveBuilds"/>, the real
        /// native-leak guard now.
        ///
        /// <para>Two layers, the thrower SECOND, the first a real request-producing graph layer (required
        /// shape) — otherwise "returns to baseline" would be trivially true on an empty ledger. The
        /// non-vacuity witness (<c>DebugLiveBuilds &gt; baseline</c>, asserted below before disposal) proves
        /// the counted layer's request really was counted before this test's own cleanup frees it.</para>
        ///
        /// <para><b>RED:</b> the mandated injection — a no-op <c>Reset</c> backstop
        /// (<c>if (_build != null) _build.Dispose();</c> in <c>TileMeshLayerProcessor.Reset</c>) — CANNOT fire
        /// against this fixture: <see cref="ThrowingGraphInputRenderLayer.BuildGraphRequest"/> throws BEFORE
        /// building a request, so <c>_build</c> is never set and that backstop never
        /// runs for either processor. Working RED: drop the <c>LayerMeshBuildCounters.RecordDisposed()</c> call in
        /// <c>FillLayerBuild.Dispose()</c> (the disposal this test's own cleanup loop drives) —
        /// reds this test's own <c>Assert.AreEqual(baseline, LayerMeshBuildCounters.DebugLiveBuilds, …)</c> with
        /// <c>Expected: 0, But was: 1</c>. Executed and reverted.</para>
        /// </summary>
        [Test]
        public void FaultingGraphRequest_StillReturnsItsProcessor_AndLeaksNoRequest()
        {
            var countedLayer  = new CountedGraphInputRenderLayer(new StyleLayer { Id = "counted-test-layer", SourceLayer = "countries" });
            var throwingLayer = new ThrowingGraphInputRenderLayer(new StyleLayer { Id = "throwing-test-layer", SourceLayer = "countries" });

            var p0 = TileMeshLayerProcessor.AllocateForKick(countedLayer, materialIndex: 11);
            var p1 = TileMeshLayerProcessor.AllocateForKick(throwingLayer, materialIndex: 12);
            var processors = new ITileMeshLayerProcessor[] { p0, p1 };

            long baseline         = LayerMeshBuildCounters.DebugLiveBuilds;
            long negativeBaseline = TileBuildGraph.DebugNegativeObservations;

            var context = MakeContext();
            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(2, output.Layers.Length);

            // Non-vacuity witness (guarding against a trivially-passing empty ledger): the counted
            // layer's factory-built request really is live before this test disposes it below.
            Assert.Greater(LayerMeshBuildCounters.DebugLiveBuilds, baseline,
                "the layer BEFORE the fault must have produced a real, counted graph build");

            try
            {
                Assert.IsTrue(ProbePoolContainsAndRestore(TileMeshLayerProcessorPool.Rent, TileMeshLayerProcessorPool.Return, p0),
                    "the layer BEFORE the fault must still be returned to TileMeshLayerProcessorPool by Release()");
                Assert.IsTrue(ProbePoolContainsAndRestore(TileMeshLayerProcessorPool.Rent, TileMeshLayerProcessorPool.Return, p1),
                    "the FAULTING layer's own processor must still be returned to TileMeshLayerProcessorPool by Release()");
            }
            finally
            {
                for (int i = 0; i < output.Layers.Length; i++) output.Layers[i]?.Dispose();
            }

            Assert.AreEqual(baseline, LayerMeshBuildCounters.DebugLiveBuilds,
                "every counted build must be freed once disposed — no native leak on the fault path");
            Assert.AreEqual(negativeBaseline, TileBuildGraph.DebugNegativeObservations,
                "no TileBuildGraph was ever double-disposed — this test never touches one, so the counter " +
                "must stay exactly where it started");
        }

        /// <summary>Rents up to <paramref name="maxProbe"/> times looking for <paramref name="target"/> by
        /// reference identity, then hands every rented instance back (restoring pool state) before
        /// returning whether it was found. A bounded, order-agnostic way to observe "was this instance
        /// returned to its pool" against a process-global <c>ConcurrentBag</c> pool shared with every other
        /// test in the run — <c>Rent</c>/<c>Return</c> give no ordering guarantee, so asserting identity on
        /// the very next <c>Rent()</c> alone would be flaky.</summary>
        private static bool ProbePoolContainsAndRestore<T>(Func<T> rent, Action<T> giveBack, T target, int maxProbe = 32)
            where T : class
        {
            var pulled = new List<T>();
            bool found = false;
            for (int i = 0; i < maxProbe; i++)
            {
                T candidate = rent();
                pulled.Add(candidate);
                if (ReferenceEquals(candidate, target)) { found = true; break; }
            }
            foreach (T item in pulled) giveBack(item);
            return found;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DecodeTests — decode + coordinate path against the real fixture tile
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Headless validation of the decode + coordinate path against the real fixture tile.
    /// No rendering / GUI required: Window → General → Test Runner → EditMode → Run All.
    /// </summary>
    public class DecodeTests
    {
        /// <summary>The address the committed fixture is decoded at. The decode stamps it into
        /// every layer's buffer, so it must be the same one the projection assertions use below.</summary>
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static byte[] LoadFixture()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }

        [Test]
        public void Decodes_expected_layers_and_counts()
        {
            using var tile = MvtDecoder.Decode(FixtureTile, LoadFixture());

            Assert.IsNotNull(tile.GetLayer("countries"), "countries layer present");
            Assert.IsNotNull(tile.GetLayer("geolines"), "geolines layer present");
            Assert.IsNotNull(tile.GetLayer("centroids"), "centroids layer present");

            Assert.AreEqual(239, tile.GetLayer("countries").Features.Count, "country feature count");
            Assert.AreEqual(6, tile.GetLayer("geolines").Features.Count, "geoline feature count");
            Assert.AreEqual(4096u, tile.GetLayer("countries").Extent, "default extent");
        }

        /// <summary>A decoded feature no longer carries a command stream — geometry belongs to the
        /// LAYER. The "every country is a polygon WITH geometry" claim is therefore split across the two
        /// things that now hold the halves: the feature's declared kind, and the layer's own buffer.</summary>
        [Test]
        public void Country_features_are_polygons_and_the_layer_carries_their_geometry()
        {
            using var tile = MvtDecoder.Decode(FixtureTile, LoadFixture());
            var layer = tile.GetLayer("countries");
            foreach (var f in layer.Features)
                Assert.AreEqual(TileGeometryType.Polygon, f.GeometryType);

            Assert.IsTrue(layer.Geometry.IsCreated, "the layer must own a materialized buffer");
            Assert.AreEqual(layer.Features.Count, layer.Geometry.FeatureCount,
                "the buffer's per-feature kind column must span EVERY feature of the layer — a buffer sized " +
                "to some subset is the mis-bucketing hazard the ordinal join depends on not having");
            Assert.Greater(layer.Geometry.RingCount, 0, "…and it must actually hold rings");
        }

        [Test]
        public void Geometry_decodes_into_nonempty_rings()
        {
            // Arm A: the independent fixture reader + the managed reference decoder (the decoded
            // feature has no stream to read, and reading the layer's buffer would make this self-referential).
            var layer = MvtFixtureStreams.ReadLayer(LoadFixture(), "countries");
            int totalRings = 0;
            for (int fi = 0; fi < layer.Commands.Count; fi++)
            {
                var rings = MvtGeometry.Decode(layer.Commands[fi]);
                foreach (var ring in rings)
                    Assert.GreaterOrEqual(ring.Count, 3, "a polygon ring needs >= 3 points");
                totalRings += rings.Count;
            }
            Assert.Greater(totalRings, 0, "expected at least one decoded ring");
        }

        [Test]
        public void Projected_vertices_fall_within_tile_world_bounds()
        {
            // Catches gross scale/parse bugs (e.g. forgetting tile→Mercator, leaving raw 0..4096 coords).
            // NOTE: at z0 the tile bbox is symmetric about the origin, so this does NOT catch a Y-flip;
            // a non-z0 fixture would. Y-orientation is validated visually in Batch 2.
            var layer = MvtFixtureStreams.ReadLayer(LoadFixture(), "countries");
            var t = FixtureTile;
            var (min, max) = t.MercatorBounds();

            double marginX = (max.x - min.x) * 0.05;
            double marginY = (max.y - min.y) * 0.05;
            int checkd = 0;

            foreach (uint[] commands in layer.Commands)
            foreach (var ring in MvtGeometry.Decode(commands))
            foreach (var p in ring)
            {
                double2 m = t.ToMercator(p.x, p.y, layer.Extent);
                Assert.That(m.x, Is.GreaterThanOrEqualTo(min.x - marginX).And.LessThanOrEqualTo(max.x + marginX));
                Assert.That(m.y, Is.GreaterThanOrEqualTo(min.y - marginY).And.LessThanOrEqualTo(max.y + marginY));
                checkd++;
            }
            Assert.Greater(checkd, 0);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineGraphKickTests — the full TileManager/MapView pump over a LineString-only style
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LineGraphKickTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument LineOnlyStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                    ""source-layer"": ""geolines"",
                    ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 }
                }
            ]
        }");

        // ── Tooth (c): the graph is scheduled by the pump, not executed inside the prologue ─────────

        /// <summary>
        /// (c) With <see cref="InlineWorkScheduler"/> injected on a LINE-ONLY style, the prologue's
        /// <c>WorkHandle</c> completes with the request's native columns and NO ribbon geometry, and the
        /// tile is at <c>BuildStep == Measure</c> after the kick Tick. Two assertions — the timing one
        /// alone does not back the title: (1) <see cref="MapViewTestExtensions.CaptureTelemetry"/>'s
        /// <c>GraphMeasureInFlight</c> moves only AFTER the hand-off tick, held open by
        /// <see cref="TileManager.GraphDepsForTest"/>; (2) <see cref="LineGraphOutput.DebugBuffersAllocated"/>
        /// — a MONOTONIC counter, never walked back — has NOT advanced at the moment the prologue hands
        /// over, so a prologue that scheduled-AND-completed the graph inline (returning a merely-BALANCED
        /// live count) cannot pass this by accident.
        /// </summary>
        [Test]
        public void LineLayer_GraphScheduledByThePump_NotExecutedInsideThePrologueBody()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("LineGraphKick_C"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0; // z0: exactly one covered tile
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                long buffersBaseline = LineGraphOutput.DebugBuffersAllocated;

                int caller = System.Environment.CurrentManagedThreadId;
                var spy = new RecordingWorkScheduler(new InlineWorkScheduler());
                view.TileManager.WorkScheduler = spy;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: LineOnlyStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0, "drive precondition: the tile's build must have been started " +
                    "(kickTick starts at -1, so this is load-bearing, not trivially true — it is what stops a " +
                    "never-kicked run from passing the assertions below).");
                Assert.GreaterOrEqual(spy.ScheduleCount, 1, "the prologue kick must go THROUGH the injected scheduler.");
                Assert.GreaterOrEqual(spy.BodyThreadIds.Count, 1, "the body must actually have run at least once.");
                foreach (int tid in spy.BodyThreadIds)
                    Assert.AreEqual(caller, tid, "under Inline the prologue body runs on the CALLING thread.");

                Assert.AreEqual(buffersBaseline, LineGraphOutput.DebugBuffersAllocated,
                    "the prologue body itself must not have scheduled the line measure graph — it only " +
                    "builds the request (BuildGraphRequest); LineMeshGraph.Schedule is the PUMP's job, next tick.");

                // Next tick: prologue-complete hands off to ScheduleMeasureFromDecode — the line graph is
                // genuinely scheduled now, held open by the still-gated delay job.
                view.LateUpdate();
                Assert.Greater(LineGraphOutput.DebugBuffersAllocated, buffersBaseline,
                    "a real line measure graph must have been scheduled by now — the geometry left the prologue body.");
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "the tile must be observed in its MEASURE step — deterministic under the still-held delay job.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must not read settled while its measure step is genuinely held incomplete.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── Tooth (f): a line layer allocates no Mesh.MeshDataArray at kick ──────────────────────────

        /// <summary>
        /// (f) On a LINE-ONLY style, <see cref="MapViewTestExtensions.MeshDataArraysAllocatedLastKick"/> is
        /// 0 at the kick Tick and non-zero once the write step runs, and the tile still produces a mesh with
        /// real vertices. Complement of tooth (c): (c) observes inline execution on the GRAPH path; this one
        /// observes the ABSENCE of any kick-time allocation. There is no second, synchronous-mesh-write
        /// path for a line
        /// layer to fall back onto; this pins that the graph-arm path it actually takes allocates nothing at
        /// kick either.
        /// </summary>
        [Test]
        public void LineLayer_AllocatesNoMeshDataArrayAtKick_OnlyAtWrite()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("LineGraphKick_F"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            try
            {
                long payloadBaseline = MeshDataPayload.DebugLiveAllocCount;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: LineOnlyStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0, "drive precondition: the tile's build must have been started " +
                    "(kickTick starts at -1, so this is load-bearing, not trivially true — it is what stops a " +
                    "never-kicked run from passing the assertions below).");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "NO MeshDataArray at kick for a line-only style — line is graph-arm now, exactly like fill.");
                Assert.AreEqual(0, view.MeshDataArraysAllocatedLastKick(),
                    "the kick Tick itself allocates nothing — only a later write-kick does.");

                int writeTick = -1;
                for (; tick < 3000 && writeTick < 0; tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.MeshDataArraysAllocatedLastKick() > 0) writeTick = tick;
                }
                Assert.GreaterOrEqual(writeTick, 0, "the write step must eventually allocate.");
                Assert.AreEqual(1, view.MeshDataArraysAllocatedLastKick(),
                    "exactly one MeshDataArray on the write-kick tick — the one non-empty line layer.");

                for (; tick < 3000 && !view.AllTilesSettled(); tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.IsTrue(view.AllTilesSettled(), "the tile must eventually settle.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from a " +
                    "build that never happened.");
            }
            finally
            {
                // Unconditional — an assertion failure above must not leak the view or leave its process-wide
                // counters (MeshDataPayload.DebugLiveAllocCount et al.) elevated for whatever test runs next.
                view.Teardown();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SourceRegistrySlotInvariantTests — drives the real MapView/TileManager restyle path
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SourceRegistrySlotInvariantTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, 0.0, 0.0);

        /// <summary>Deterministic settle (mirrors <c>Tiles/PreparedCacheTests.PumpUntilSettled</c>):
        /// <c>DrainMeshBuilds</c> spins each tick's kicked builds to completion so the next tick consumes
        /// them. The <c>LoadedTileCount() &gt; 0</c> guard is load-bearing — <c>AllTilesSettled()</c> is
        /// vacuously true on an empty cover, before anything has ever been admitted.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 200)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        private static StyleDocument ThreeSourceStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""T7ThreeSources"",
            ""sources"": {
                ""a"": { ""type"": ""vector"", ""tiles"": [""https://example.com/a/{z}/{x}/{y}.pbf""] },
                ""b"": { ""type"": ""vector"", ""tiles"": [""https://example.com/b/{z}/{x}/{y}.pbf""] },
                ""c"": { ""type"": ""vector"", ""tiles"": [""https://example.com/c/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""a-fill"", ""type"": ""fill"", ""source"": ""a"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",200,50,50,1]} },
                { ""id"": ""b-fill"", ""type"": ""fill"", ""source"": ""b"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,200,50,1]} },
                { ""id"": ""c-fill"", ""type"": ""fill"", ""source"": ""c"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,50,200,1]} }
            ]
        }");

        // Removes "b" — the MIDDLE slot — so "c" shifts from slot 2 to slot 1.
        private static StyleDocument TwoSourceStyle_BRemoved() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""T7TwoSources"",
            ""sources"": {
                ""a"": { ""type"": ""vector"", ""tiles"": [""https://example.com/a/{z}/{x}/{y}.pbf""] },
                ""c"": { ""type"": ""vector"", ""tiles"": [""https://example.com/c/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""a-fill"", ""type"": ""fill"", ""source"": ""a"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",200,50,50,1]} },
                { ""id"": ""c-fill"", ""type"": ""fill"", ""source"": ""c"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,50,200,1]} }
            ]
        }");

        /// <summary>Restyling away a source must not leave its tiles behind. Removing the
        /// MIDDLE source (b, slot 1) reassigns the survivor (c) from slot 2 to slot 1 — a stale entry keyed
        /// to the OLD slot would resolve against the wrong (or an out-of-range) pipeline. Pins that
        /// <c>_loaded</c> empties immediately on restyle and that the restyled cover settles with no tile
        /// reporting the removed source. The ORDER this depends on is pinned by
        /// <see cref="Rebuild_CallerFactoryObservesLoadedClearedFirst"/> instead — that test reaches the
        /// window directly through <c>Rebuild</c>'s own caller-supplied-code hook, so it, not this
        /// end-to-end settle, is the one that RED-verifies an ordering inversion.</summary>
        [Test]
        public void RemovedSource_TilesDoNotSurviveARestyle()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("T7_SlotInvariant"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick     = 64;
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: ThreeSourceStyle(),
                    decodeScheduler: new InlineWorkScheduler());
                Assert.AreEqual(3, view.WiredFeatureSourceCount(), "precondition: three real sources wired.");

                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "precondition: the initial three-source cover must settle.");
                Assert.Greater(view.LoadedTileCount(), 0, "sanity: something must actually be loaded.");

                // Restyle, removing the MIDDLE source.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: TwoSourceStyle_BRemoved(),
                    decodeScheduler: new InlineWorkScheduler());

                Assert.AreEqual(2, view.WiredFeatureSourceCount(), "the registry must now hold exactly the two surviving sources.");
                Assert.AreEqual(0, view.LoadedTileCount(), "SetSources step 1 must clear _loaded immediately — before any tick.");

                // Drive several ticks against the NEW registry — an ordering bug throws (out-of-range slot)
                // or silently aliases a stale entry to the wrong pipeline.
                PumpUntilSettled(view);
                Assert.IsTrue(view.LoadedTileCount() > 0 && view.AllTilesSettled(),
                    "the restyled two-source cover must settle without throwing.");

                var loaded = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(loaded);
                Assert.Greater(loaded.Count, 0, "sanity: the restyled cover must have loaded something.");
                foreach (var key in loaded)
                    Assert.AreNotEqual("b", key.SourceId,
                        "no loaded record may report the REMOVED source 'b' — a stale slot-keyed entry " +
                        "would alias the wrong pipeline after the restyle re-slotted the survivors.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary><c>SourceRegistry.Rebuild</c> is not a black box mid-call — it invokes a
        /// caller-supplied <c>SourceSpec.CreateSource</c> factory for every new pipeline while the registry
        /// is still rebuilding. Wires a factory that reads the loaded-tile count from inside that call and
        /// pins <c>TileManager.SetSources</c>' load-bearing order: it clears <c>_loaded</c> before calling
        /// <c>Rebuild</c>, so the factory observes zero, never the pre-restyle count.</summary>
        [Test]
        public void Rebuild_CallerFactoryObservesLoadedClearedFirst()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("T7_RebuildReentrancy"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick     = 64;
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: ThreeSourceStyle(),
                    decodeScheduler: new InlineWorkScheduler());
                PumpUntilSettled(view);
                Assert.Greater(view.LoadedTileCount(), 0, "precondition: something must be loaded before the restyle.");

                int observedDuringRebuild = -1;
                var specs = new List<TileManager.SourceSpec>
                {
                    new TileManager.SourceSpec("only-new", default, 0, int.MaxValue, () =>
                    {
                        observedDuringRebuild = view.LoadedTileCount();
                        return new MvtTileFeatureSource(src, new InlineWorkScheduler());
                    }),
                };

                view.TileManager.SetSources(specs, view.Config.Backend);

                Assert.AreEqual(0, observedDuringRebuild,
                    "a CreateSource factory invoked from mid-Rebuild must see _loaded already cleared — " +
                    "SetSources must clear slot-keyed state BEFORE rebuilding the registry.");
            }
            finally
            {
                view.Teardown();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // A6NonMvtDecoderTests — a non-MvtDecoder ITileDecoder flows through the unchanged fill fan-out
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class A6NonMvtDecoderTests : BaseTestFixture
    {
        // Deliberately malformed as MVT (a truncated length-delimited TileLayers field — MvtDecoder.Decode
        // throws decoding it — same fixture used by SharedTileDecodeTests/TileLayerProcessorRunnerTests).
        // The falsifier: if RunWorkerPass ignored the injected ITileDecoder and called MvtDecoder.Decode on
        // these bytes directly, the pass would fault instead of producing the expected quad below.
        private static readonly byte[] MalformedMvtBytes = { 0x1A, 0x64 };

        private const string FixtureSourceLayerName = "non-mvt-fixture-layer";

        /// <summary>Ignores the bytes entirely and returns the fixed fixture tile — the injection point
        /// F-3 proves is actually consumed (not bypassed in favour of a hardcoded MVT decode).</summary>
        private sealed class FakeTileDecoder : ITileDecoder
        {
            private readonly IDecodedTile _tile;
            public FakeTileDecoder(IDecodedTile tile) => _tile = tile;
            public IDecodedTile Decode(TileId id, byte[] bytes) => _tile;
        }

        /// <summary>Mirrors <see cref="FillRenderLayer.BuildGraphRequest"/>'s forward without needing a real
        /// Unity <see cref="Material"/> (this test drives the fan-out into the job graph, not material
        /// binding).</summary>
        private sealed class FakeFillTileMeshRenderLayer : ITileMeshRenderLayer
        {
            private readonly Fill.PaintProperties _paint;
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base; // mirrors FillRenderLayer
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material Material => null;

            public FakeFillTileMeshRenderLayer(StyleLayer styleLayer, Fill.PaintProperties paint)
            {
                StyleLayer = styleLayer;
                _paint = paint;
            }

            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public int TransitioningCount => 0;
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
            {
                FillMeshPipeline.LayerInput input = StyledFillTileBuilder.BuildLayerInput(
                    selected, geometry, _paint, context.Zoom, context.TileOriginRender, out var colors,
                    context.Projection, layout: null, context.BufferClip, context.Buffers);
                if (!input.RingVisitOrder.IsCreated) return null;
                return FillLayerBuild.Rent(input, colors, materialIndex, payloadName);
            }
        }

        [Test]
        public void NonMvtDecoder_FlowsThroughTheUnchangedFanOut_ProducesTheFullExtentQuad()
        {
            // The fixture tile: one layer (named to match the style layer's source-layer), one feature —
            // the full-extent-ring command stream (the SAME oracle TileBackgroundQuadProjectionTests
            // asserts decodes to the tile's 4 corners), carried by DictionaryFeature.
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false, geometry: FullExtentRingCommandStream.Commands);
            var tileId = new TileId { Z = 0, X = 0, Y = 0 };
            // A decoded layer OWNS its geometry, so the fixture layer materializes at construction
            // exactly as MvtDecoder does — the shared InMemoryTileLayer/InMemoryDecodedTile pair.
            var layer = new InMemoryTileLayer(
                FixtureSourceLayerName, tileId, new IFeature[] { feature },
                (uint)BackgroundQuad.Extent);
            using var fixtureTile = new InMemoryDecodedTile(layer);
            var fakeDecoder = new FakeTileDecoder(fixtureTile);

            var styleLayer = new StyleLayer { Id = "fixture-fill", SourceLayer = FixtureSourceLayerName };
            var paint = TestStyle.FillPaint("{\"fill-color\":\"#ffffff\"}");
            var fillLayer = new FakeFillTileMeshRenderLayer(styleLayer, paint);

            var projection = new WebMercatorProjection();
            var context = new TileLayerProcessContext
            {
                Tile = tileId, Zoom = 0.0,
                TileOriginRender = TileRenderOrigin.Project(tileId, projection),
                Projection = projection,
            };

            var processor = TileMeshLayerProcessor.AllocateForKick(fillLayer, materialIndex: 0);
            var decode = new SharedDisposable<IDecodedTile>(fakeDecoder.Decode(tileId, MalformedMvtBytes));

            TilePrologueOutput output = TileLayerProcessorRunner.RunWorkerPass(
                decode, in context, new ITileMeshLayerProcessor[] { processor });
            Assert.AreEqual(1, output.Layers.Length);

            // The graph is the only mesher — drive it
            // synchronously, the way TileManager.KickMeshBuild's pump does. ScheduleMeasureFromDecode takes
            // ownership of `decode` from here — released exactly once, by graph.Dispose() below. The
            // payload must be read/uploaded BEFORE graph.Dispose() runs: Dispose() sweeps whatever
            // CompleteWriteAndTakePayloads handed out that the caller never consumed (TileBuildGraph's own
            // doc), so disposing first would silently zero-vertex the very payload this test asserts on.
            TileBuildGraph graph = TileBuildGraph.ScheduleMeasureFromDecode(output.Layers, decode);
            Mesh mesh;
            try
            {
                graph.CompleteMeasureAndScheduleWrite(out _);
                MeshDataPayload[] payloads = graph.CompleteWriteAndTakePayloads();

                Assert.AreEqual(1, payloads.Length);
                Assert.IsNotNull(payloads[0], "the worker pass must settle a payload even under the fake decoder.");
                // 4 interior + 8 band. Unlike BackgroundQuad's synthesized full-tile quad, this is a real
                // fill layer, so it carries the outward boundary band: two vertices per ring vertex appended
                // after the interior quad.
                Assert.AreEqual(12, payloads[0].VertexCount,
                    "the injected non-MvtDecoder decoder's feature must flow through StyledFillTileBuilder " +
                    "unchanged and produce the flat 4-vertex quad (Mercator, no subdivision) plus its " +
                    "8-vertex boundary band. Zero or a fault here means the fan-out ignored the injected " +
                    "decoder.");

                mesh = Track(payloads[0].Upload());
            }
            finally { graph.Dispose(); }

            Assert.IsNotNull(mesh, "a non-zero-vertex payload must upload a real mesh.");
            Assert.AreEqual(12, mesh.vertexCount);
        }
    }
}
