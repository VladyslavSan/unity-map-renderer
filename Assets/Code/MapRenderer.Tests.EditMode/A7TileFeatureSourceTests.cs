// Epic A / A7 acceptance: the raised source interface
// (ITileFeatureSource.GetTile -> IDecodedTileHandle). F-2 proves a BYTELESS source (no IDataSource, no
// bytes, no FetchAsync) flows through the UNCHANGED per-layer fan-out — the raise is real, not a rename.
// F-4 proves the lazy-handle decision (§B): GetTile mints a handle without decoding; a malformed-MVT fetch
// completes cleanly and only faults on the first GetOrDecode() call.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using CoreMapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class A7TileFeatureSourceTests
    {
        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>An EAGER <see cref="IDecodedTileHandle"/> wrapping a pre-built <see cref="IDecodedTile"/>
        /// — no bytes, no lazy decode. Proves the handle interface is genuinely polymorphic (§B point 2): the
        /// MVT source's handle (<see cref="SharedTileDecode"/>) is byte-lazy, this one is eager, and both
        /// satisfy the SAME <see cref="IDecodedTileHandle"/> contract the runner reads through.</summary>
        private sealed class EagerHandle : IDecodedTileHandle
        {
            private readonly IDecodedTile _tile;
            public EagerHandle(IDecodedTile tile) => _tile = tile;
            public IDecodedTile GetOrDecode() => _tile;
        }

        /// <summary>A <see cref="ITileFeatureSource"/> with NO <see cref="MapRenderer.Core.Data.IDataSource"/>,
        /// no bytes, no fetch at all — every <see cref="GetTile"/> call resolves immediately (synchronously
        /// completed <see cref="UniTask{T}"/>) to the SAME eager handle. The F-2 falsifier: a coordinator
        /// still routing through the byte-centric boundary cannot consume this (compile-impossible, since
        /// there is no <c>IDataSource</c> anywhere to wrap).</summary>
        private sealed class FakeTileFeatureSource : ITileFeatureSource
        {
            private readonly IDecodedTileHandle _handle;
            public int GetTileCalls { get; private set; }
            public FakeTileFeatureSource(IDecodedTileHandle handle) => _handle = handle;

            public UniTask<IDecodedTileHandle> GetTile(TileId id, CancellationToken ct = default)
            {
                GetTileCalls++;
                return UniTask.FromResult(_handle);
            }

            public void Release(TileId id) { }
            public int InFlightCount => 0;
            public void Dispose() { }
        }

        /// <summary>Minimal engine-free <see cref="IDecodedTile"/>/<see cref="ITileLayer"/> pair — mirrors
        /// <c>A6NonMvtDecoderTests.FixtureDecodedTile</c>/<c>FixtureTileLayer</c> (test-only, per design §B-4;
        /// duplicated locally rather than shared since both are private test fixtures, not a production
        /// type).</summary>
        private sealed class FixtureDecodedTile : IDecodedTile
        {
            private readonly ITileLayer _layer;
            public FixtureDecodedTile(ITileLayer layer) => _layer = layer;
            public ITileLayer GetLayer(string name) => name == _layer.Name ? _layer : null;
        }

        private sealed class FixtureTileLayer : ITileLayer
        {
            public string Name { get; set; }
            public uint Extent { get; set; }
            public IReadOnlyList<ITileFeature> Features { get; set; }
        }

        private const string FixtureSourceLayerName = "a7-fixture-layer";

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Mirrors <c>MapViewTestExtensions.LoadTestStyle</c>'s body, but wires an
        /// <see cref="ITileFeatureSource"/> DIRECTLY — no <c>IDataSource</c> to wrap. Kept local (not added to
        /// the shared helper) since it exists only to prove the raise accepts a byteless source; every other
        /// test in the suite still drives the byte-centric <c>LoadTestStyle</c> overload unchanged.</summary>
        private static void LoadTestStyleWithFeatureSource(
            MapView view, ITileFeatureSource source, CameraProperties initialView, StyleDocument style)
        {
            CoreMapView mv = view.View;
            mv.Camera.SetProperties(initialView);
            mv.Camera.SyncToCamera();
            mv.Layers.Build(style, mv.Camera.CurrentProperties.Zoom, view.Config.MaterialSet);

            var specs = new List<TileManager.SourceSpec>
            {
                new TileManager.SourceSpec("s", default, 0, int.MaxValue, () => source),
            };
            mv.TileManager.SetSources(specs, view.Config.Backend);
        }

        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                Thread.Sleep(1);
            }
        }

        // ── F-2: a bytes-less ITileFeatureSource flows through the UNCHANGED per-layer fan-out ────────────

        [Test]
        public void ByteLessSource_FlowsThroughTheUnchangedFanOut_ProducesTheFullExtentQuad()
        {
            // The fixture tile: one layer, one feature — the A2 full-extent-ring command stream (the SAME
            // oracle A6NonMvtDecoderTests/TileBackgroundQuadProjectionTests assert decodes to the tile's 4
            // corners), carried by the production InMemoryTileFeature.
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                Geometry     = TileBackgroundLayerProcessor.FullExtentRingGeometry,
            };
            var layer = new FixtureTileLayer
            {
                Name     = FixtureSourceLayerName,
                Extent   = (uint)TileBackgroundLayerProcessor.Extent,
                Features = new ITileFeature[] { feature },
            };
            var fixtureTile = new FixtureDecodedTile(layer);
            var fake = new FakeTileFeatureSource(new EagerHandle(fixtureTile));

            var style = StyleParser.Parse($@"{{
                ""version"": 8,
                ""layers"": [
                    {{ ""id"": ""fixture-fill"", ""type"": ""fill"", ""source"": ""s"",
                       ""source-layer"": ""{FixtureSourceLayerName}"",
                       ""paint"": {{ ""fill-color"": ""#ffffff"" }} }}
                ]
            }}");

            var go   = new GameObject("A7ByteLessSource");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            try
            {
                LoadTestStyleWithFeatureSource(view, fake, Cam(0, 0, 0.0), style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "sanity: the tile must settle.");
                Assert.Greater(fake.GetTileCalls, 0, "sanity: the byteless source must have been consulted.");

                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "F-2 DECISIVE: a coordinator still routing through the byte-centric " +
                    "boundary cannot consume a source with no IDataSource/bytes/FetchAsync at all — the fill " +
                    "mesh must exist, proving the raise is real.");
                Assert.AreEqual(1, meshes.Length);
                Assert.IsNotNull(meshes[0]);
                Assert.AreEqual(4, meshes[0].vertexCount,
                    "the byteless source's feature must flow through StyledFillTileBuilder unchanged and " +
                    "produce the flat 4-vertex quad (Mercator, no subdivision) — the same oracle " +
                    "A6NonMvtDecoderTests asserts one level down (the runner, not the coordinator).");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── F-4: GetTile mints a LAZY handle — it must not decode eagerly (the §B decisive tooth) ─────────

        // Deliberately malformed as MVT (a truncated length-delimited TileLayers field — MvtDecoder.Decode
        // throws decoding it — same fixture shape used by SharedTileDecodeTests/A6NonMvtDecoderTests).
        private static readonly byte[] MalformedMvtBytes = { 0x1A, 0x64 };

        [Test]
        public async Task GetTile_MintsALazyHandle_FaultSurfacesOnlyAtFirstGetOrDecode()
        {
            // An eager `GetTile -> IDecodedTile` implementation would decode AT FETCH TIME — with these
            // malformed bytes, that decode throws, so GetTile itself would fault. The lazy-handle contract
            // (§B decision) defers the fault to the first GetOrDecode() call instead — the decisive falsifier
            // for the eager-vs-lazy fork a spy call-count could only observe indirectly (MvtTileFeatureSource
            // resolves its ITileDecoder internally via TileDecoders.ForEncoding — there is no decoder
            // injection seam to spy on without adding a test-only production hook).
            //
            // Narrower guarantee than the planned spy-count: this observes WHERE the fault surfaces, not
            // WHEN decode work runs on the happy path. It catches the natural eager rewrite (decode inside
            // GetTile → GetTile throws) but not a contrived eager impl that re-wraps the fault one layer up;
            // that shape is unnatural and the production code (MvtTileFeatureSource.GetTile never calls
            // GetOrDecode) is verified lazy by reading, not only by this tooth.
            var byteSource = TestDataSource.FromBytes(MalformedMvtBytes);
            using var source = new MvtTileFeatureSource(byteSource);

            IDecodedTileHandle handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 });

            Assert.IsNotNull(handle,
                "F-4 DECISIVE: GetTile must complete and mint a handle WITHOUT decoding — a fault surfacing " +
                "here (instead of at GetOrDecode) would mean the source decoded eagerly at fetch time.");

            Assert.Throws<System.InvalidOperationException>(() => handle.GetOrDecode(),
                "the malformed bytes must only fault on the first GetOrDecode() call — proving the decode " +
                "was deferred to here, matching SharedTileDecode's unchanged lazy-decode contract.");
        }

        [Test]
        public async Task GetTile_AbsentTile_ReturnsNullHandle()
        {
            // Byte-equivalent to today's TileResponse.HasData == false branch (§G risk 4).
            var byteSource = TestDataSource.Absent();
            using var source = new MvtTileFeatureSource(byteSource);

            IDecodedTileHandle handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 });

            Assert.IsNull(handle, "an absent tile (HasData == false) must map to a null handle — the " +
                "coordinator's null-for-absent contract (Epic A / A7 §G-4).");
        }
    }
}
