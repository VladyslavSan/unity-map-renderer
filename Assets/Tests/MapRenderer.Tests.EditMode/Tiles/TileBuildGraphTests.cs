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
//   NonMvtDecoderFanOutTests          — Load-bearing acceptance: proves a NON-MvtDecoder ITileDecoder flows through the unchanged fill fan-out (RunWorkerPass -> TileMeshLayerProcessor -> StyledFillTileBuilder.WriteMeshData) and produces real geometry.

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

        /// <summary>One layer's fixture: the rented build plus two caller-owned locals a test reads
        /// independently of the build — the geometry, which the build BORROWS and never frees, and the visit
        /// order, whose length a precondition reads before the build's <c>Dispose</c>.
        /// <see cref="ILayerMeshBuild"/> exposes neither (test-code-bloat rule), so the test keeps its own
        /// reference.</summary>
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
        /// <para>Non-obvious why: unlike <see cref="EmptyLayerFixture"/>, whose zero-length visit order
        /// fast-outs to <c>IsCreated == false</c>, this shape reaches <c>CompleteMeasureAndScheduleWrite</c>'s
        /// zero-vertex guard, which no other layer observes. Production reaches it when a layer's features all
        /// clip or degenerate away.</para></summary>
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

        /// <summary>A layer whose <see cref="FillMeshPipeline.LayerInput.RingVisitOrder"/> is zero-length,
        /// which hits <c>FillMeshGraph.Schedule</c>'s "nothing to draw" fast-out (<see cref="FillGraphOutput"/>'s
        /// type doc). Rented through <see cref="FillLayerBuild.Rent"/>: an IsCreated-but-zero-length array
        /// also passes a render layer's emptiness gate (it tests only <c>IsCreated</c>), which matches what
        /// production hands the graph for an empty source layer.</summary>
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

        // ── (b) exact-size allocation, multi-layer form ────────────────────────────────────────────────
        // One Mesh.MeshDataArray per non-empty layer, none for an empty one; counts are deltas on a static.

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

            // The degenerate layer makes the zero-vertex guard reachable: a non-empty visit order over a
            // two-vertex ring, read from the fixture's own locals, not from anything the graph computes.
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
            // Each layer owns its own geometry, so the graph's ownedGeometry slot is `default`; the test frees
            // each BORROWED geometry in `finally`, after the graph.
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

                // ONE SLOT PER REQUEST, in request order; a null slot is an empty or faulted layer. Compacting
                // would renumber the slots the pump joins against.
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
        /// — the engine's container safety system, not application code, must refuse this at the
        /// measure→write transition. The message is asserted by its parts, not verbatim, because its wording
        /// belongs to the engine.</summary>
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
                // Assert the missing Complete() and the job that still writes: a verbatim match breaks on an
                // engine rewording, and a non-empty check passes any stub that throws the right type.
                StringAssert.Contains("Complete()", ex.Message,
                    "the safety system must say a Complete() is missing — that is the whole claim of this " +
                    "tooth: the output is unreadable until the caller completes the handle.");
                // FillBandJob is the last writer of TileVertices (it appends the band to AggregateJob's columns).
                // If the graph reshapes, re-capture the job name; never weaken this to a type-only check.
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
        // Non-obvious why: the snapshot suites see only colour dominance, so a wrong Normal, tangent w or
        // PatternCoord is invisible to them. A live comparison against StyledFillTileBuilder.WriteGeometry
        // shares ScheduleWrite's own path and proves nothing, so frozen per-stream SHA-256 digests pin all four
        // vertex streams, the indices and the bounds.
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

                // Non-obvious why: the goldens digest the INTERIOR only, because FillBandJob appends the band to
                // the same mesh; this keeps the goldens byte-identical and shows the band perturbs nothing interior.
                // The flat arm's band is a vertex SUFFIX (BandVertexCount). The curved arm interleaves band and
                // interior, so a triangle is interior iff all three vertices carry side 0 — exact, because `side`
                // is affine and a sub-triangle with all three vertices on its zero line has zero area. The digest
                // renumbers interior vertices by rank (rank == index on the flat arm). Pick the filter by ARM,
                // not data: the curved filter drops unreferenced vertices and would move a flat golden.
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
                    // Hash only the UV half of stream 1 (it also carries the band attribute), so the golden
                    // stays the pattern coordinates' own digest.
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
        // Non-obvious why: ScheduleStreamWrite sizes the Mesh.MeshData from the COMPLETED output's list lengths,
        // so the band counts. An interior-only size makes the write job run past the mesh end, silently in a
        // release player, where NativeList's indexer bounds check is compiled out.
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
                    // Zero here is NOT "no band": subdivision interleaves band and interior, so GlobeFillScatterJob
                    // clears the suffix counts; the per-vertex attribute below carries band-ness.
                    Assert.AreEqual(0, bandVertices,
                        "the curved arm reports no band SUFFIX — subdivision leaves none to report");
                    Assert.AreEqual(0, bandIndices, "and no band index suffix, for the same reason");
                }
                else
                {
                    // One 3-vertex ring ⇒ 6 band vertices, and 6 band indices per edge that KEEPS its quad. The
                    // (0,0)->(10,0) edge has both endpoints on the y = 0 window line, so it keeps no quad.
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

                // The band column's path into stream 1, which the frozen PatternUv digest does not cover.
                // Assertions, not a digest, so a failure names what broke.
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
                // Non-vacuity, and on the curved arm the ONLY proof the band reached the mesh. The flat arm
                // also checks WHERE: the band is the suffix, so the last vertex is a band vertex.
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
        // _writeScheduled, _payloadsTaken and _disposed make a protocol violation throw; counts are deltas.

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

        /// <summary><see cref="TileBuildGraph"/> caches the payload array and hands the SAME instance back on
        /// every call, so a repeat call is a legitimate resume of a budget-bound partial consume, not a
        /// protocol violation. Pins: same instance, no second allocation, no throw.</summary>
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

        /// <summary><b>A caller that loses its first reference can take the payloads again.</b> A consume site
        /// that holds the array in a LOCAL loses it on a throw before the store (a backend <c>AddTileLayer</c>,
        /// a mesh apply). A permanently set <c>_payloadsTaken</c> would make every later retry throw and leak
        /// the array. The test drops its first reference, then calls again.</summary>
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
        /// <see cref="TileBuildGraph.Complete"/>. The decode is a disposal-counting tile the test holds no
        /// other reference to; the graph is held in flight on <see cref="SpinUntilGateJob"/>, so the decode
        /// must stay alive while the gate holds and be disposed exactly once by <c>graph.Dispose()</c>.</summary>
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
                // graph.Dispose() swept the build's columns and released decode, which disposed the layer's
                // geometry through CountingDecodedTile → RawGeometryDecodedTile → RawGeometryTileLayer.
            }
        }

        /// <summary>Positive control for the tooth above: releasing the decode reference directly, not through
        /// <see cref="TileBuildGraph.Dispose"/>, while the measure job still reads its geometry
        /// <c>[ReadOnly]</c>, must throw from Unity's <c>NativeContainer</c> safety system. It shows what every
        /// caller would hit if <c>TileBuildGraph.Dispose</c> released <c>_decode</c> before
        /// <c>_handle.Complete()</c>.</summary>
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

                // The test holds the creator reference, the same one ScheduleMeasureFromDecode took, so this
                // release bypasses TileBuildGraph.Dispose() and is one the graph never authorized.
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
                // Limitation: graph.Dispose() is not called, because it would release decode a second time, and
                // the geometry is already disposed (a bare geometry.Dispose() here would throw). So
                // LayerMeshBuildCounters.DebugLiveBuilds and TileBuildGraph.DebugLiveCount stay one high for the
                // rest of the process (harmless: every tooth reads its counters as a delta), and the measure
                // graph's Allocator.Persistent output buffers leak for the rest of the run.
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

        // ── per-layer build object: the ownership contract, terminal-before-request-columns arm ──────────
        // Dispose() completes _ext BEFORE freeing the request columns that in-flight wall jobs hold [ReadOnly].

        /// <summary>Disposes an in-flight <see cref="FillExtrusionLayerBuild"/> directly, held on
        /// <see cref="SpinUntilGateJob"/> and released just before <c>Dispose()</c>. Must NOT throw: the
        /// correct order completes the wall/roof terminal first.
        /// <para><b>RED:</b> in <c>FillExtrusionLayerBuild.Dispose()</c>, move the request-column disposes
        /// above <c>_ext.Dispose();</c> — the safety system throws.</para></summary>
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
    /// Pins that <see cref="TileLayerProcessorRunner.RunWorkerPass"/> shares one <see cref="IDecodedTile"/>
    /// across every processor in dense order, and keeps the fault policy (abort-on-first-fault,
    /// settle-every-processor). The decode happens BEFORE the lease exists (at the source's <c>GetTile</c>),
    /// so these fixtures decode, then wrap, and release in a <c>finally</c>, as the kick lambda does.
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
        /// SHARED log, and counts Release() calls for exactly-once settlement.
        /// <see cref="TryTakeGraphRequest"/> hands back <see cref="Build"/>, a stub that owns nothing and is
        /// never rented from <c>LayerMeshBuildPool</c>; the dense-slot contract is observed as reference
        /// IDENTITY (<c>Assert.AreSame(processor.Build, output.Layers[i])</c>).</summary>
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
                // NOT counted as a Features read: the buffer is built here, once, exactly as the
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
        /// One source-layer is materialized <b>once per worker pass</b>, however many style layers name it.
        /// The layer already holds its buffer, so <c>Features</c> is read once per style layer (by
        /// <c>FeatureSelector</c>) and zero times for geometry: the count is exactly N. A consumer that
        /// re-derived geometry from the feature list would push it above N.
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
                // The LEASE owns the decoded tile, which owns this source layer, so this one release disposes
                // it. Disposing `sourceLayer` directly would free the lender's buffers under a live owner.
                handle.Release();
            }
        }

        // ── Primary semantic tooth ────────────────────────────────────────────────────────────────────

        /// <summary>The slot-join half, observed as reference IDENTITY: <c>output.Layers[i]</c> IS the
        /// processor's own build. The four-member <see cref="ILayerMeshBuild"/> interface carries no
        /// <c>MaterialIndex</c>, so this is the dense-slot contract job-scheduling-design.md specifies,
        /// observed on the array production uses.</summary>
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
            Assert.AreSame(p0.Build, output.Layers[0], "dense-slot identity (job-scheduling-design.md § \"Ownership — the graph as a value\")");
            Assert.AreSame(p1.Build, output.Layers[1], "dense-slot identity (job-scheduling-design.md § \"Ownership — the graph as a value\")");
            Assert.AreSame(p2.Build, output.Layers[2], "dense-slot identity (job-scheduling-design.md § \"Ownership — the graph as a value\")");
        }

        // ── Fault-parity: the fan-out read faults ─────────────────────────────────────────────────────
        // Limitation: no test drives a fault at the fan-out read. SharedDisposable<T>.Value does not throw after
        // the last Release() (a DEBUG assertion only), so it hands back the disposed instance and nothing faults.

        // ── Fault-parity: a processor throws ──────────────────────────────────────────────────────────

        /// <summary>A processor fault stops the later processors, but every processor still returns to
        /// <see cref="TileMeshLayerProcessorPool"/> exactly once and no graph request is left un-taken.</summary>
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
        // RunWorkerPass' catch settles every processor, and its warning must NAME THE TILE to be actionable.

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

        // The released-read fault cannot occur (see the fan-out read note above), so the processor-throw
        // test above is the only fault that pins "the warning names the tile".

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
        /// on the graph-arm fault site.</summary>
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

        /// <summary>A fault inside a per-layer body can strand a graph build's <c>Allocator.Persistent</c>
        /// columns; <see cref="Meshing.LayerMeshBuildCounters.DebugLiveBuilds"/> observes that. The thrower is
        /// SECOND, after a real request-producing layer, so "returns to baseline" is not trivially true.
        /// <b>RED:</b> drop <c>LayerMeshBuildCounters.RecordDisposed()</c> in <c>FillLayerBuild.Dispose()</c>
        /// (a no-op <c>Reset</c> backstop cannot fire: the thrower never sets <c>_build</c>).</summary>
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
        /// reference, then hands every rented instance back and returns whether it was found. The pool is a
        /// process-global <c>ConcurrentBag</c> with no ordering guarantee, so checking only the next
        /// <c>Rent()</c> would be flaky.</summary>
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
            // Catches gross scale/parse bugs (e.g. raw 0..4096 coords left unprojected). Limitation: the z0
            // tile bbox is symmetric about the origin, so this does NOT catch a Y-flip; a non-z0 fixture would.
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
        /// (c) With <see cref="InlineWorkScheduler"/> on a LINE-ONLY style, the prologue builds only the
        /// request, and the pump schedules the line graph on the next tick. The monotonic
        /// <see cref="LineGraphOutput.DebugBuffersAllocated"/> has NOT advanced when the prologue hands over,
        /// so an inline schedule-and-complete cannot pass; <c>GraphMeasureInFlight</c> is then held open by
        /// <see cref="TileManager.GraphDepsForTest"/>.
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
        /// real vertices. Tooth (c) observes where the graph runs; this one observes that the line layer's
        /// graph path allocates nothing at kick.
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

        /// <summary>Restyling away a source must not leave its tiles behind. Removing the MIDDLE source (b,
        /// slot 1) moves the survivor (c) from slot 2 to slot 1, so a stale entry keyed to the OLD slot would
        /// resolve against the wrong pipeline. Pins that <c>_loaded</c> empties on restyle and no settled tile
        /// reports the removed source. <see cref="Rebuild_CallerFactoryObservesLoadedClearedFirst"/> pins the
        /// clear-before-rebuild ORDER.</summary>
        [Test]
        public void RemovedSource_TilesDoNotSurviveARestyle()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("SlotInvariant"));
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
            var go   = Track(new GameObject("RebuildReentrancy"));
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
    // NonMvtDecoderFanOutTests — a non-MvtDecoder ITileDecoder flows through the unchanged fill fan-out
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class NonMvtDecoderFanOutTests : BaseTestFixture
    {
        // Malformed as MVT (a truncated TileLayers field, which MvtDecoder.Decode throws on), so a pass that
        // bypassed the injected ITileDecoder would fault instead of producing the quad below.
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
            // One layer, one feature: the full-extent-ring command stream, which TileBackgroundQuadProjectionTests
            // asserts decodes to the tile's 4 corners.
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

            // ScheduleMeasureFromDecode owns `decode` from here; graph.Dispose() releases it. Upload the payload
            // BEFORE graph.Dispose(), which sweeps every payload the caller has not consumed.
            TileBuildGraph graph = TileBuildGraph.ScheduleMeasureFromDecode(output.Layers, decode);
            Mesh mesh;
            try
            {
                graph.CompleteMeasureAndScheduleWrite(out _);
                MeshDataPayload[] payloads = graph.CompleteWriteAndTakePayloads();

                Assert.AreEqual(1, payloads.Length);
                Assert.IsNotNull(payloads[0], "the worker pass must settle a payload even under the fake decoder.");
                // 4 interior + 8 band: a real fill layer, unlike BackgroundQuad, appends two band vertices per
                // ring vertex after the interior quad.
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
