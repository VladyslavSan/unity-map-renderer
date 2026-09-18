// Unity EditMode only (UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() — the only trustworthy
// allocation meter on Unity Mono, see MapRenderer.Tests.EditMode/Meshing/LineBuildAllocTests.cs). NOT
// registered in core-tests.csproj (MeshDataPayload/Mesh.MeshDataArray are Unity types).
//
// perf/gc-elimination, meshing follow-ups Stage A: TileMeshLayerProcessor and MeshDataPayload — the
// per-dense-layer kick-time objects — used to be `new`d fresh on every AllocateForKick/Complete() call
// (~208 alloc events / ~16.5 KB per tile-build on a liberty-shaped style). They are now rented from
// TileMeshLayerProcessorPool/MeshDataPayloadPool (ConcurrentBag-backed, mirroring TileBuildBuffersPool —
// both objects cross the main/worker thread boundary between allocation and release, which is what rules
// out UnityEngine.Pool here).

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Acceptance teeth for meshing follow-ups Stage A: pooling <see cref="TileMeshLayerProcessor"/> and
    /// <see cref="MeshDataPayload"/>. See <see cref="TileLayerProcessorRunnerTests"/>'s
    /// <c>FaultingGraphRequest_StillReturnsItsProcessor_AndLeaksNoRequest</c> for the
    /// sibling degenerate-path pool-return tooth (extended there, next to the existing fault-settlement
    /// coverage it builds on).
    /// </summary>
    [TestFixture]
    public class TileMeshLayerProcessorPoolingTests
    {
        private static readonly TileId Tile = new TileId { Z = 4, X = 2, Y = 3 };
        private const double Zoom = 4.0;

        private static MvtLayer MakeSourceLayer(string name, int count)
        {
            var layer = new MvtLayer { Name = name, Extent = 4096, Version = 2 };
            for (int i = 0; i < count; i++)
                layer.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Point });
            return layer;
        }

        private static StyleLayer SelectAllLayer(string id, string sourceLayer) => new StyleLayer
        {
            Id          = id,
            Source      = "s",
            SourceLayer = sourceLayer,
        };

        /// <summary>Mirrors <c>TileMeshLayerProcessorSelectionAllocTests.UnreachedRenderLayer</c>: every
        /// fixture layer here leaves <c>Geometry.IsCreated</c> false, so <c>BuildGraphRequest</c> is never
        /// reached and <c>ProcessOnWorker</c> completes with no request.</summary>
        private sealed class UnreachedRenderLayer : ITileMeshRenderLayer
        {
            public UnreachedRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

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
                Assert.Fail("BuildGraphRequest must not be reached — this fixture's layers carry no adopted " +
                            "geometry (IsCreated == false), by design.");
                return null;
            }
        }

        // ── Tooth 1: zero-alloc, warmed repeat kick+release cycle ────────────────────────────────────

        /// <summary>
        /// The tooth: a warmed repeat of <see cref="TileMeshLayerProcessor.AllocateForKick"/> →
        /// <see cref="TileMeshLayerProcessor.ProcessOnWorker"/> → <see cref="TileMeshLayerProcessor.Release"/>,
        /// over two style layers, must allocate zero managed bytes once the kick-time wrapper is rented from
        /// its pool instead of `new`d. RED-verified by reverting <c>AllocateForKick</c> back to
        /// `new TileMeshLayerProcessor(...)` — must fail.
        ///
        /// <para>job-scheduling-design.md §8 stage 5 Group B: this tooth's cycle used to run through
        /// <c>Complete()</c> → <see cref="MeshDataPayload.Upload"/>/<see cref="MeshDataPayload.Dispose"/> —
        /// the seam-arm's kick-time <c>MeshDataPayload</c> pooling, which retired with the last seam-arm
        /// kind: after B.3 nothing is allocated at kick, so there is no payload for THIS cycle to Upload or
        /// Dispose any more. What survives, unaffected by B.1-B.5, is <see cref="TileMeshLayerProcessor"/>'s
        /// OWN object pooling (<see cref="TileMeshLayerProcessorPool"/>) — the property this narrowed tooth
        /// still observes.</para>
        /// </summary>
        [Test]
        public void KickProcessRelease_OverTwoLayers_WarmedRepeat_AllocatesNoGCMemory()
        {
            MvtLayer roads  = MakeSourceLayer("roads", 30);
            MvtLayer places = MakeSourceLayer("places", 45);
            var tile = new MvtTile();
            tile.Layers.Add(roads);
            tile.Layers.Add(places);

            var roadsRenderLayer  = new UnreachedRenderLayer(SelectAllLayer("roads-all", "roads"));
            var placesRenderLayer = new UnreachedRenderLayer(SelectAllLayer("places-all", "places"));

            // ONE buffers instance reused across the whole test, exactly as ONE TileBuildBuffers is rented
            // for the whole duration of a real worker pass (see TileMeshLayerProcessorSelectionAllocTests).
            var buffers = new TileBuildBuffers();
            var context = new TileLayerProcessContext
            {
                Tile             = Tile,
                Zoom             = Zoom,
                TileOriginRender = double3.zero,
                Projection       = null, // never read — BuildGraphRequest is unreachable in this fixture
                Buffers          = buffers,
            };

            try
            {
                void RunCycle()
                {
                    TileMeshLayerProcessor roadsProcessor  = TileMeshLayerProcessor.AllocateForKick(roadsRenderLayer,  materialIndex: 0);
                    TileMeshLayerProcessor placesProcessor = TileMeshLayerProcessor.AllocateForKick(placesRenderLayer, materialIndex: 1);

                    roadsProcessor.ProcessOnWorker(tile, in context);
                    placesProcessor.ProcessOnWorker(tile, in context);

                    roadsProcessor.Release();
                    placesProcessor.Release();
                }

                // Warm the EXACT delegate the constraint measures below: JIT + any one-time pool/grow costs
                // happen here, outside the measured region.
                for (int i = 0; i < 8; i++) RunCycle();

                Assert.That(RunCycle, Is.Not.AllocatingGCMemory(),
                    "a warmed repeat of AllocateForKick + ProcessOnWorker + Release must not allocate managed " +
                    "memory once TileMeshLayerProcessor is pool-rented instead of constructed fresh per layer " +
                    "per tile-build.");
            }
            finally
            {
                tile.Dispose();
            }
        }

        // ── Tooth 2: cross-build isolation — Upload() must not itself return to the pool ────────────

        /// <summary>
        /// The regression tooth for Correction 2 (meshing-follow-up plan, Stage A): <see cref="MeshDataPayload.Dispose"/>,
        /// not <see cref="MeshDataPayload.Upload"/>, is the sole pool-return point. <c>TileManager.ConsumeMeshBuild</c>
        /// calls <c>payload.Upload(); payload.Dispose();</c> back-to-back on the same reference — if
        /// <c>Upload()</c>'s success path also returned <c>this</c> to the pool, a concurrent build's
        /// <c>Rent()</c> could receive and <c>Reset()</c> the SAME instance in the gap before the original
        /// caller's own following <c>Dispose()</c> runs, corrupting cross-build state (the double-free /
        /// wrong-native-array risk Correction 2 describes). This test proves the window doesn't exist: the
        /// instance a concurrent renter observes immediately after a successful <c>Upload()</c> is never the
        /// one just uploaded.
        ///
        /// <para>RED-verified by temporarily adding <c>MeshDataPayloadPool.Return(this);</c> to <c>Upload</c>'s
        /// success path (the exact bug Correction 2 forbids) — the very next <c>Rent()</c> then returns the
        /// same instance, failing this assertion.</para>
        /// </summary>
        [Test]
        public void Upload_DoesNotReturnThePayloadToThePool_OnlyDisposeDoes()
        {
            // job-scheduling-design.md §8 stage 5 Group B (§3.6): this tooth's subject is MeshDataPayload's
            // OWN pool contract — it never needed the runner, only a vehicle to obtain a real,
            // vertex-bearing payload. Post-migration (no more seam-arm processor to route one through), get
            // one directly: allocate a tracked writable MeshDataArray, write real geometry into it with the
            // still-kept (E2 ruling) StyledFillTileBuilder.WriteMeshData, then wrap it exactly the way the
            // (retired) seam-arm settlement used to — MeshDataPayloadPool.Rent() + Reset(...).
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                Geometry     = FullExtentRingCommandStream.Commands,
            };
            var tileId = new TileId { Z = 0, X = 0, Y = 0 };
            const string sourceLayerName = "isolation-fixture-layer";
            var layer = new InMemoryTileLayer(
                sourceLayerName, tileId, new IFeature[] { feature }, (uint)BackgroundQuad.Extent);
            using var fixtureTile = new InMemoryDecodedTile(layer);
            TileGeometryBuffers geometry = layer.Geometry;

            var paint = Fill.PaintProperties.Parse(JsonParser.Parse("{\"fill-color\":\"#ffffff\"}"));
            var projection = new WebMercatorProjection();
            double3 renderOrigin = TileRenderOrigin.Project(tileId, projection);
            var selected = new List<SelectedTileFeature> { new SelectedTileFeature { Feature = feature, Ordinal = 0 } };

            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            SyncMeshWrite.Fill(mda[0], selected, geometry, paint, zoom: 0.0, renderOrigin,
                out int vertexCount, out Bounds writeBounds, projection);

            MeshDataPayload payload = MeshDataPayloadPool.Rent();
            payload.Reset(mda, vertexCount, writeBounds, "isolation-fill", materialIndex: 0);

            Assert.Greater(payload.VertexCount, 0, "precondition: the fixture must produce real geometry");

            Mesh mesh = payload.Upload();
            try
            {
                Assert.IsNotNull(mesh, "precondition: a real vertex-bearing payload must upload successfully");

                // The probe: if Upload() incorrectly returned `payload` to the pool, this Rent() would very
                // likely hand it straight back out (ConcurrentBag's uncontended same-thread behaviour is
                // effectively LIFO) — the exact alias a concurrent build's Rent()+Reset() would corrupt.
                MeshDataPayload other = MeshDataPayloadPool.Rent();
                try
                {
                    Assert.AreNotSame(payload, other,
                        "Upload() must not return the payload to the pool — only Dispose() may, per Correction 2 " +
                        "(meshing-follow-up plan, Stage A): returning it from Upload() opens a window where a " +
                        "concurrent build's Rent()+Reset() races the original caller's own still-pending Dispose().");
                }
                finally
                {
                    // `other` may be a freshly-minted stub (never Reset()) — return it as-is, never touch its
                    // fields/Dispose it, exactly like MeshDataPayloadPool.Return's contract expects.
                    MeshDataPayloadPool.Return(other);
                }
            }
            finally
            {
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                payload.Dispose(); // the correct, sole return point — see the method's own doc comment
            }
        }
    }
}
