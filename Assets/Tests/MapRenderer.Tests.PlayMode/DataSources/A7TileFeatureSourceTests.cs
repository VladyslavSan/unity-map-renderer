// A BYTELESS source (no IDataSource, no bytes, no FetchAsync) flows through the UNCHANGED per-layer
// fan-out. PlayMode: drives the real cover→build→settle loop over real frames (yield, never
// Thread.Sleep). The pure async-Task GetTile teeth live in the EditMode half
// (MapRenderer.Tests.DataSources.A7TileFeatureSourceTests).

using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using CoreMapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.PlayMode.DataSources
{
    [TestFixture]
    public class A7TileFeatureSourceTests : BaseTestFixture
    {
        /// <summary>A <see cref="ITileFeatureSource"/> with NO IDataSource, no bytes, no fetch — every
        /// <see cref="GetTile"/> builds a tile and hands back a fresh <see cref="SharedDisposable{T}"/> over
        /// it. The falsifier: a coordinator still routing through the byte-centric boundary cannot consume
        /// this — there is no IDataSource anywhere to wrap, so it would not compile.</summary>
        private sealed class FakeTileFeatureSource : ITileFeatureSource
        {
            private readonly System.Func<IDecodedTile> _tileFactory;
            public int GetTileCalls { get; private set; }
            public FakeTileFeatureSource(System.Func<IDecodedTile> tileFactory) => _tileFactory = tileFactory;

            public UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default)
            {
                GetTileCalls++;
                return UniTask.FromResult(new SharedDisposable<IDecodedTile>(_tileFactory()));
            }

            public void Release(TileId id) { }
            public int InFlightCount => 0;
            public void Dispose() { }
        }

        private const string FixtureSourceLayerName = "a7-fixture-layer";

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Mirrors <c>MapViewTestExtensions.LoadTestStyle</c>'s body, but wires an
        /// <see cref="ITileFeatureSource"/> DIRECTLY — no IDataSource to wrap.</summary>
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

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        // ── A bytes-less ITileFeatureSource flows through the UNCHANGED per-layer fan-out ─────────────────
        [UnityTest]
        public IEnumerator ByteLessSource_FlowsThroughTheUnchangedFanOut_ProducesTheFullExtentQuad()
        {
            // The fixture tile: one layer, one feature — the full-extent-ring command stream, carried by
            // DictionaryFeature. Built per GetTile call, OWNED BY THE LEASE that wraps it.
            IDecodedTile MakeFixtureTile()
            {
                var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false, geometry: FullExtentRingCommandStream.Commands);
                var layer = new InMemoryTileLayer(
                    FixtureSourceLayerName, new TileId { Z = 0, X = 0, Y = 0 }, new IFeature[] { feature },
                    (uint)BackgroundQuad.Extent);
                return new InMemoryDecodedTile(layer);
            }

            var fake = new FakeTileFeatureSource(MakeFixtureTile);

            var style = StyleParser.Parse($@"{{
                ""version"": 8,
                ""layers"": [
                    {{ ""id"": ""fixture-fill"", ""type"": ""fill"", ""source"": ""s"",
                       ""source-layer"": ""{FixtureSourceLayerName}"",
                       ""paint"": {{ ""fill-color"": ""#ffffff"" }} }}
                ]
            }}");

            var go   = Track(new GameObject("A7ByteLessSource"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            try
            {
                LoadTestStyleWithFeatureSource(view, fake, Cam(0, 0, 0.0), style);
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "sanity: the tile must settle.");
                Assert.Greater(fake.GetTileCalls, 0, "sanity: the byteless source must have been consulted.");

                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "F-2 DECISIVE: a coordinator still routing through the byte-centric " +
                    "boundary cannot consume a source with no IDataSource/bytes/FetchAsync at all — the fill " +
                    "mesh must exist, proving the raise is real.");
                Assert.AreEqual(1, meshes.Length);
                Assert.IsNotNull(meshes[0]);
                // 4 interior + 8 band: a real fill layer carries the outward boundary band, two vertices
                // per ring vertex appended after the interior quad.
                Assert.AreEqual(12, meshes[0].vertexCount,
                    "the byteless source's feature must flow through StyledFillTileBuilder unchanged and " +
                    "produce the flat 4-vertex quad (Mercator, no subdivision) plus its 8-vertex boundary " +
                    "band — the same oracle A6NonMvtDecoderTests asserts one level down.");
            }
            finally { view.Teardown(); }
        }
    }
}
