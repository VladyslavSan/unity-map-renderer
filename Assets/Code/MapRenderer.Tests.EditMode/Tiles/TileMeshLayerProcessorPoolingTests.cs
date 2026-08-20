// Unity EditMode only (UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() — the only trustworthy
// allocation meter on Unity Mono, see MapRenderer.Tests.EditMode/Meshing/LineBuildAllocTests.cs). NOT
// registered in core-tests.csproj (MeshDataPayload/Mesh.MeshDataArray are Unity types).
//
// perf/gc-elimination, meshing follow-ups Stage A: TileMeshLayerProcessor and MeshDataPayload — the
// per-dense-layer kick-time objects — used to be `new`d fresh on every AllocateForKick/Complete() call
// (~208 alloc events / ~16.5 KB per tile-build on a liberty-shaped style). They are now rented from
// TileMeshLayerProcessorPool/MeshDataPayloadPool (ConcurrentBag-backed, mirroring TileBuildScratchPool —
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
using MapRenderer.Jobs;
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
    /// <c>TileMeshLayerProcessor_FaultingWrite_ReturnsEmptyPayload_AndReleasesTrackedMeshData</c> for the
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
        /// fixture layer here leaves <c>Geometry.IsCreated</c> false, so <c>WriteInto</c> is never reached
        /// and <c>ProcessOnWorker</c> completes zero-vertex. That is deliberate, not incidental — it keeps
        /// the zero-alloc tooth below isolated to the kick/consume OBJECT pooling this stage adds, rather
        /// than mixing in <c>Upload()</c>'s unavoidable-and-out-of-scope <c>new Mesh{...}</c> allocation on
        /// a real (non-zero-vertex) payload, which happens on every call regardless of pooling.</summary>
        private sealed class UnreachedRenderLayer : ITileMeshRenderLayer
        {
            public UnreachedRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public StyleLayer       StyleLayer      { get; }
            public RenderLayerBuild Build           => RenderLayerBuild.TileMesh;
            public DrawPersistence  Persistence     => DrawPersistence.Persistent;
            public int              DrawIndex       => 0;
            public LayerSubSlot     MaterialSubSlot => LayerSubSlot.Base;
            public Material         Material        => null;
            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public void WriteInto(
                Mesh.MeshData md, IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                double zoom, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
                TileBuildScratch scratch, out int vertexCount, out Bounds bounds)
            {
                vertexCount = 0;
                bounds      = default;
                Assert.Fail("WriteInto must not be reached — this fixture's layers carry no adopted " +
                            "geometry (IsCreated == false), by design.");
            }
        }

        /// <summary>Mirrors <c>A6NonMvtDecoderTests.FakeFillTileMeshRenderLayer</c> — a real, vertex-producing
        /// forward to <see cref="StyledFillTileBuilder.WriteMeshData"/>, used by the cross-build isolation
        /// tooth below, which needs a genuinely-uploadable (VertexCount &gt; 0) payload to exercise
        /// <see cref="MeshDataPayload.Upload"/>'s success path.</summary>
        private sealed class ProducingFillRenderLayer : ITileMeshRenderLayer
        {
            private readonly Fill.PaintProperties _paint;
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base;
            public Material Material => null;
            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public ProducingFillRenderLayer(StyleLayer styleLayer, Fill.PaintProperties paint)
            {
                StyleLayer = styleLayer;
                _paint = paint;
            }

            public void WriteInto(
                Mesh.MeshData md, IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                double zoom, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
                TileBuildScratch scratch, out int vertexCount, out Bounds bounds)
                => StyledFillTileBuilder.WriteMeshData(
                    md, selected, geometry, _paint, zoom, tileOriginRender, out vertexCount, out bounds,
                    projection, layout: null, clip: clip, scratch: scratch);
        }

        // ── Tooth 1: zero-alloc, warmed repeat kick+consume cycle ────────────────────────────────────

        /// <summary>
        /// The tooth: a warmed repeat of <see cref="TileMeshLayerProcessor.AllocateForKick"/> →
        /// <see cref="TileMeshLayerProcessor.ProcessOnWorker"/> → <see cref="TileMeshLayerProcessor.Complete"/>
        /// → <see cref="MeshDataPayload.Upload"/>/<see cref="MeshDataPayload.Dispose"/>, over two style
        /// layers, must allocate zero managed bytes once both kick-time wrapper types are rented from their
        /// pools instead of `new`d. RED-verified by reverting <c>AllocateForKick</c>/<c>Complete</c> back to
        /// `new TileMeshLayerProcessor(...)`/`new MeshDataPayload(...)` — must fail.
        /// </summary>
        [Test]
        public void KickCompleteConsumeCycle_OverTwoLayers_WarmedRepeat_AllocatesNoGCMemory()
        {
            MvtLayer roads  = MakeSourceLayer("roads", 30);
            MvtLayer places = MakeSourceLayer("places", 45);
            var tile = new MvtTile();
            tile.Layers.Add(roads);
            tile.Layers.Add(places);

            var roadsRenderLayer  = new UnreachedRenderLayer(SelectAllLayer("roads-all", "roads"));
            var placesRenderLayer = new UnreachedRenderLayer(SelectAllLayer("places-all", "places"));

            // ONE scratch instance reused across the whole test, exactly as ONE TileBuildScratch is rented
            // for the whole duration of a real worker pass (see TileMeshLayerProcessorSelectionAllocTests).
            var scratch = new TileBuildScratch();
            var context = new TileLayerProcessContext
            {
                Tile             = Tile,
                Zoom             = Zoom,
                TileOriginRender = double3.zero,
                Projection       = null, // never read — WriteInto is unreachable in this fixture
                Scratch          = scratch,
            };

            try
            {
                void RunCycle()
                {
                    TileMeshLayerProcessor roadsProcessor  = TileMeshLayerProcessor.AllocateForKick(roadsRenderLayer,  materialIndex: 0);
                    TileMeshLayerProcessor placesProcessor = TileMeshLayerProcessor.AllocateForKick(placesRenderLayer, materialIndex: 1);

                    roadsProcessor.ProcessOnWorker(tile, in context);
                    placesProcessor.ProcessOnWorker(tile, in context);

                    IRenderLayerPayload roadsPayload  = roadsProcessor.Complete();
                    IRenderLayerPayload placesPayload = placesProcessor.Complete();

                    // VertexCount == 0 on both (see UnreachedRenderLayer) ⇒ Upload() takes its early-return,
                    // no-`new Mesh` branch — genuinely exercised, genuinely zero-alloc, isolating this tooth
                    // from Upload()'s unrelated, unavoidable, out-of-scope Mesh-object allocation.
                    roadsPayload.Upload();
                    placesPayload.Upload();
                    roadsPayload.Dispose();
                    placesPayload.Dispose();
                }

                // Precondition, unmeasured: confirm the zero-vertex/null-mesh assumption the measured region
                // above relies on actually holds for this fixture, before trusting an alloc-free reading.
                TileMeshLayerProcessor precheckProcessor = TileMeshLayerProcessor.AllocateForKick(roadsRenderLayer, materialIndex: 0);
                precheckProcessor.ProcessOnWorker(tile, in context);
                IRenderLayerPayload precheckPayload = precheckProcessor.Complete();
                Assert.AreEqual(0, precheckPayload.VertexCount,
                    "precondition: this fixture must produce zero-vertex payloads (Upload() must be a genuine no-op)");
                Assert.IsNull(precheckPayload.Upload(),
                    "precondition: a zero-vertex payload's Upload() must return null without allocating a Mesh");
                precheckPayload.Dispose();

                // Warm the EXACT delegate the constraint measures below: JIT + any one-time pool/grow costs
                // happen here, outside the measured region.
                for (int i = 0; i < 8; i++) RunCycle();

                Assert.That(RunCycle, Is.Not.AllocatingGCMemory(),
                    "a warmed repeat of AllocateForKick + ProcessOnWorker + Complete + Upload/Dispose must not " +
                    "allocate managed memory once TileMeshLayerProcessor/MeshDataPayload are pool-rented " +
                    "instead of constructed fresh per layer per tile-build.");
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
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                Geometry     = FullExtentRingCommandStream.Commands,
            };
            var tileId = new TileId { Z = 0, X = 0, Y = 0 };
            const string sourceLayerName = "isolation-fixture-layer";
            var layer = new InMemoryTileLayer(
                sourceLayerName, tileId, new IFeature[] { feature }, (uint)TileBackgroundLayerProcessor.Extent);
            using var fixtureTile = new InMemoryDecodedTile(layer);

            var styleLayer = new StyleLayer { Id = "isolation-fill", SourceLayer = sourceLayerName };
            var paint = new Fill.PaintProperties(JsonParser.Parse("{\"fill-color\":\"#ffffff\"}"));
            var fillLayer = new ProducingFillRenderLayer(styleLayer, paint);

            var projection = new WebMercatorProjection();
            var context = new TileLayerProcessContext
            {
                Tile             = tileId,
                Zoom             = 0.0,
                TileOriginRender = TileRenderOrigin.Project(tileId, projection),
                Projection       = projection,
            };

            var processor = TileMeshLayerProcessor.AllocateForKick(fillLayer, materialIndex: 0);
            var decode = new SharedDisposable<IDecodedTile>(fixtureTile);

            IRenderLayerPayload[] payloads;
            try
            {
                payloads = TileLayerProcessorRunner.RunWorkerPass(
                    decode, in context, new ITileMeshLayerProcessor[] { processor });
            }
            finally { decode.Release(); }

            var payload = (MeshDataPayload)payloads[0];
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
