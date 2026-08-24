// Epic A / A5b acceptance teeth — the feed swap (TileManager's per-tile kick drives the symbol worker pass,
// retiring the SymbolTileBytesReady push). EditMode half: F-1 (structural, no settle), F-4 (DrainMeshBuilds
// stays symbol-silent), F-6 (a tile condemned before its kick never begins a symbol build — settles
// deterministically via DrainMeshBuilds). The behavioural symbol-KICK teeth (F-2/F-3/F-5/symbol-only) live
// in the PlayMode half — they assert TryBeginBuild fires via the normal PumpPending kick path over the async
// decode, which DrainMeshBuilds is symbol-silent about (F-4) and needs real PlayMode frames to land.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TileSymbolKickTests
    {
        /// <summary>Spy <see cref="ISymbolTileWorkerFactory"/> — records every <c>TryBeginBuild</c> call and
        /// every pass it issues.</summary>
        private sealed class SpySymbolTileWorkerFactory : ISymbolTileWorkerFactory
        {
            public readonly List<(string SourceId, TileId Tile)> BeginBuildCalls = new();
            public readonly List<SpySymbolTileWorkerPass> IssuedPasses = new();
            public Func<string, bool> ParticipatesFor = _ => true;
            public Func<string, TileId, SpySymbolTileWorkerPass> PassFactory;

            public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
            {
                BeginBuildCalls.Add((sourceId, tile));
                if (!ParticipatesFor(sourceId)) return null;
                SpySymbolTileWorkerPass pass = PassFactory != null
                    ? PassFactory(sourceId, tile)
                    : new SpySymbolTileWorkerPass(sourceId, tile);
                IssuedPasses.Add(pass);
                return pass;
            }
        }

        private sealed class SpySymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            public readonly string SourceId;
            public readonly TileId Tile;
            public bool Ran;
            public SharedDisposable<IDecodedTile> ReceivedDecode;
            public Action OnRun;

            public SpySymbolTileWorkerPass(string sourceId, TileId tile) { SourceId = sourceId; Tile = tile; }

            public void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)
            {
                Ran = true;
                ReceivedDecode = decode;
                OnRun?.Invoke();
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument FillAndSymbolStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""s"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16 } }
            ]
        }");

        private static (GameObject go, MapView view) NewView(int zoom, bool preparedCacheEnabled = true)
        {
            var go   = new GameObject("MapView_TileSymbolKick");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = zoom;
            view.Config.TileSelection.MaxZoom = zoom;
            view.Config.PreparedCache.Enabled = preparedCacheEnabled;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            return (go, view);
        }

        private static string SourcePath(params string[] relative)
            => Path.Combine(Application.dataPath, "Code", Path.Combine(relative));

        private static int CountOccurrences(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ── F-1: the feed swap is real (structural) — genuinely RED pre-A5b ──
        [Test]
        public void F1_FeedSwapIsReal_NoResidualPushSymbols()
        {
            string tileManagerSrc = File.ReadAllText(SourcePath("MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs"));
            string subsystemSrc   = File.ReadAllText(SourcePath("MapRenderer.Unity", "Text", "SymbolSubsystem.cs"));

            Assert.AreEqual(0, CountOccurrences(tileManagerSrc, "SymbolTileBytesReady"),
                "TileManager must contain ZERO SymbolTileBytesReady occurrences — the push feed is retired.");
            Assert.IsTrue(tileManagerSrc.Contains("ISymbolTileWorkerFactory SymbolWorkerFactory"),
                "TileManager must hold the factory field SymbolWorkerFactory.");
            Assert.IsTrue(tileManagerSrc.Contains("ISymbolTileWorkerPass symbolPass"),
                "KickMeshBuild's signature must carry an ISymbolTileWorkerPass parameter.");

            Assert.AreEqual(0, CountOccurrences(subsystemSrc, "OnTileBytesReady"),
                "SymbolSubsystem must contain ZERO OnTileBytesReady occurrences — the push entry is retired.");
            Assert.AreEqual(0, CountOccurrences(subsystemSrc, "_buildQueue"),
                "SymbolSubsystem must contain ZERO _buildQueue occurrences — the build-start queue is retired.");
            Assert.IsTrue(typeof(ISymbolTileWorkerFactory).IsAssignableFrom(typeof(SymbolSubsystem)),
                "SymbolSubsystem must implement ISymbolTileWorkerFactory.");
        }

        // ── F-4: DrainMeshBuilds stays symbol-silent (§Q-Drain KEEP) ───────────────────────────────────
        [Test]
        public void F4_DrainMeshBuilds_NeverDrivesSymbolFactory()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(zoom: 0);
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndSymbolStyle());

                // ONE LateUpdate creates the record + starts the fetch. The kick block can NEVER fire on this
                // same call (it requires lt.Decode already set from a PRIOR PumpPending call) — TryBeginBuild
                // is provably unreached so far.
                view.LateUpdate();
                Assert.AreEqual(0, spy.BeginBuildCalls.Count, "sanity: the kick cannot have fired yet.");

                // Settle entirely via DrainMeshBuilds — never through PumpPending's kick block again.
                view.DrainMeshBuilds();

                Assert.IsTrue(view.AllTilesSettled(), "sanity: drain must fully settle the tile.");
                Assert.AreEqual(0, spy.BeginBuildCalls.Count,
                    "F-4 DECISIVE: TryBeginBuild must NEVER fire from a DrainMeshBuilds call site — symbolPass " +
                    "is computed ONLY at the PumpPending kick site (§Q-Drain); drain always passes the default " +
                    "(null), keeping it symbol-silent by construction.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── F-6: a tile condemned before its kick never attempts a symbol build ────────────────────────
        [Test]
        public void F6_DepartedBeforeKick_NeverBeginsSymbolBuild()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_TileSymbolKick_F6");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5; // known 9-tile z5 cover
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 1; // trickle kicks — most tiles stay un-kicked after the observe tick
            view.Config.MaxReleasesPerTick   = 1; // trickle releases — the condemned window stays observable
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillAndSymbolStyle());

                view.LateUpdate();          // Tick 1: cover created (9 tiles), fetches requested
                view.DrainMeshBuilds();     // land the fetch decodes deterministically (symbol-silent, per F-4)
                view.LateUpdate();          // Tick 2: fetches observed → lt.Decode set, 0 kicked
                Assert.AreEqual(0, spy.BeginBuildCalls.Count, "sanity: nothing kicked before the tile has a prior lt.Decode.");

                var oldKeys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(oldKeys);
                Assert.GreaterOrEqual(oldKeys.Count, 6, "need a multi-tile cover for the departure to be non-vacuous.");
                var oldTiles = oldKeys.ConvertAll(k => k.Tile);

                // Pan far away — the NEXT recompute condemns every old tile. At most ONE old tile can race
                // ahead and get kicked before its condemnation registers (matching mesh's own pre-existing
                // race, Stall #2) — the point is that the OTHER old tiles never reach TryBeginBuild.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 150.0, Latitude = 70.0 });
                for (int f = 0; f < 300 && view.ReleaseQueueDepth() > 0; f++) { view.LateUpdate(); view.DrainMeshBuilds(); }
                Assert.AreEqual(0, view.ReleaseQueueDepth(), "sanity: the departing backlog must fully drain.");

                int oldTilesKicked = spy.BeginBuildCalls.FindAll(c => oldTiles.Contains(c.Tile)).Count;
                Assert.LessOrEqual(oldTilesKicked, 1,
                    "F-6 DECISIVE: a tile condemned before its kick must never reach TryBeginBuild — at most the " +
                    "single unavoidable race-window tile (kicked the SAME tick its condemnation registers, " +
                    "before the recompute runs) may appear; a missing _releaseQueued skip would eventually kick " +
                    $"ALL {oldTiles.Count} departed tiles while the release budget trickles them out.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
