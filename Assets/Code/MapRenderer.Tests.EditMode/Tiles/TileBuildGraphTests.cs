// job-scheduling-design.md §8 stage 2 teeth (b) and (f) — TileBuildGraph's own allocation and
// read-before-complete contracts, exercised directly (below the full TileManager stack).

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

namespace MapRenderer.Tests.Tiles
{
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
        /// doc for that trap). <see cref="ILayerMeshBuild"/> exposes neither (test-code-bloat rule; R1's own
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

                // job-scheduling-design.md §8 stage 3 (2.4): ONE SLOT PER REQUEST, in request order — not
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
        // ── Write-step PARITY: the graph's stream write against frozen goldens, byte for byte (R4) ──────
        //
        // The write step is this stage's only new geometry-PRODUCING code, and until this tooth existed
        // nothing read a single byte it wrote. The snapshot suites observe it only through colour-dominance
        // samples, and the background renders with the fill LIT shader — so a wrong Normal, a flipped
        // tangent w, or a wrong PatternCoord is invisible to them. This compares all four vertex streams,
        // the index buffer and the bounds.
        //
        // R4 (plan review, CLOSED): this used to compare live against StyledFillTileBuilder.WriteGeometry —
        // the oracle every other parity tooth in this epic used. Group B rewrites WriteGeometry to itself
        // route through FillMeshGraph.Schedule + ScheduleStreamWrite, the SAME path ScheduleWrite takes, so
        // the live comparison would become self-referential (green forever, proves nothing). Six per-stream
        // SHA-256 digests, captured from the INDEPENDENT WriteGeometry arm before Group B rewrote it (commit
        // f5e13c19, stage 4 Group A — see docs/stage4-groupb-goldens-capture-f5e13c19.txt's SITE4 lines), replace it as a
        // frozen regression pin — same reasoning as R6/B.7, same per-stream granularity as N1.
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
        /// stage 3's Group 0.5 changed the OWNER: <see cref="TileBuildGraph"/> now caches the array itself and
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

        // ── Tooth (c) — job-scheduling-design.md §8 stage 3 §5(c): the decode reference is released ONLY
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
        /// on <see cref="SpinUntilGateJob"/> (job-scheduling-design.md E2 option iii) — never disposed while
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
        /// <para><b>RED (job-scheduling-design.md §5(c)):</b> moving <c>TileBuildGraph.Dispose</c>'s
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
                // Clean-up (job-scheduling-design.md §5(c)): release the delay, Complete() the handle, then
                // dispose the raw arrays BY HAND — the geometry's own IsCreated already flipped false before
                // the safety system's throw above, so it needs no further disposal (and a bare
                // geometry.Dispose() here would itself throw). graph.Dispose() is deliberately NOT called:
                // it would try to release decode a SECOND time (a double-release the safety system would
                // also catch). The accepted cost of a positive control that demonstrates the unsafe path:
                // (a) the build's own rent DOES count now — FillLayerBuild.Rent is unconditional (§2.2 of the
                // per-layer build-object stage), unlike the retired LayerRequest.Create's object-initializer
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
        // review caught (job-scheduling-design.md §8 stage 5); it is now a tooth instead of a review finding.

        /// <summary>Disposes a genuinely in-flight <see cref="FillExtrusionLayerBuild"/> directly (not
        /// through a <see cref="TileBuildGraph"/>) — held on <see cref="SpinUntilGateJob"/>
        /// (job-scheduling-design.md E2 option iii), released immediately before <c>Dispose()</c> so
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
}
