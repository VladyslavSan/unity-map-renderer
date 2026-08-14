// Epic A / A6 acceptance — plan §F-3 (load-bearing): proves a NON-MvtDecoder ITileDecoder flows through the
// unchanged fill fan-out (RunWorkerPass -> TileMeshLayerProcessor -> StyledFillTileBuilder.WriteMeshData) and
// produces real geometry. A rename (a fan-out that still hardcodes MvtDecoder.Decode internally) cannot pass
// this test — the falsifier bytes below are malformed MVT that MvtDecoder.Decode throws on, so a production
// path that ignored the injected decoder and decoded the bytes itself would fault instead of producing the
// expected 4-vertex quad.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class A6NonMvtDecoderTests
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

        /// <summary>Mirrors <see cref="FillRenderLayer.WriteInto"/>'s forward to
        /// <see cref="StyledFillTileBuilder.WriteMeshData"/> without needing a real Unity <see cref="Material"/>
        /// (this test drives the WriteInto-path fan-out, not material binding).</summary>
        private sealed class FakeFillTileMeshRenderLayer : ITileMeshRenderLayer
        {
            private readonly Fill.PaintProperties _paint;
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base; // mirrors FillRenderLayer (G7/D7)
            public Material Material => null;

            public FakeFillTileMeshRenderLayer(StyleLayer styleLayer, Fill.PaintProperties paint)
            {
                StyleLayer = styleLayer;
                _paint = paint;
            }

            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public void WriteInto(
                Mesh.MeshData md, IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                double zoom, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
                out int vertexCount, out Bounds bounds)
                => StyledFillTileBuilder.WriteMeshData(
                    md, selected, geometry, _paint, zoom, tileOriginRender, out vertexCount, out bounds,
                    projection, layout: null, clip: clip);
        }

        [Test]
        public void NonMvtDecoder_FlowsThroughTheUnchangedFanOut_ProducesTheFullExtentQuad()
        {
            // The fixture tile: one layer (named to match the style layer's source-layer), one feature —
            // the A2 full-extent-ring command stream (the SAME oracle TileBackgroundQuadProjectionTests
            // asserts decodes to the tile's 4 corners), carried by the production InMemoryTileFeature.
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                Geometry     = FullExtentRingCommandStream.Commands,
            };
            var tileId = new TileId { Z = 0, X = 0, Y = 0 };
            // IR C1 P3: a decoded layer OWNS its geometry, so the fixture layer materializes at construction
            // exactly as MvtDecoder does — the shared InMemoryTileLayer/InMemoryDecodedTile pair.
            var layer = new InMemoryTileLayer(
                FixtureSourceLayerName, tileId, new IFeature[] { feature },
                (uint)TileBackgroundLayerProcessor.Extent);
            using var fixtureTile = new InMemoryDecodedTile(layer);
            var fakeDecoder = new FakeTileDecoder(fixtureTile);

            var styleLayer = new StyleLayer { Id = "fixture-fill", SourceLayer = FixtureSourceLayerName };
            var paint = new Fill.PaintProperties(JsonParser.Parse("{\"fill-color\":\"#ffffff\"}"));
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

            IRenderLayerPayload[] payloads;
            try
            {
                payloads = TileLayerProcessorRunner.RunWorkerPass(
                    decode, in context, new ITileMeshLayerProcessor[] { processor });
            }
            finally { decode.Release(); }

            Assert.AreEqual(1, payloads.Length);
            Assert.IsNotNull(payloads[0], "the worker pass must settle a payload even under the fake decoder.");
            Assert.AreEqual(4, payloads[0].VertexCount,
                "the injected non-MvtDecoder decoder's feature must flow through StyledFillTileBuilder " +
                "unchanged and produce the flat 4-vertex quad (Mercator, no subdivision) — the same oracle " +
                "TileBackgroundQuadProjectionTests.BackgroundQuad_FlatOnMercator_NoSubdivision asserts. Zero " +
                "or a fault here means the fan-out ignored the injected decoder.");

            Mesh mesh = payloads[0].Upload();
            try
            {
                Assert.IsNotNull(mesh, "a non-zero-vertex payload must upload a real mesh.");
                Assert.AreEqual(4, mesh.vertexCount);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }
    }
}
