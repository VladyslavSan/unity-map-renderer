// UMR-139 — the prepared mesh cache and the symbol store are two halves of ONE tile's render product, and
// nothing crossed them before this fixture. Unity EditMode only (real MapView/materials/glyph atlas), NOT
// in Tools/core-tests.
//
// EditMode, not PlayMode: the "EditMode settle is symbol-silent" rule is about DrainMeshBuilds (see
// TileSymbolKickTests' header). AwaitInFlightMeshBuilds does let a symbol build land between LateUpdates —
// VisualScene renders real labels through the same MapView path in EditMode.

using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Text;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class PreparedCacheSymbolCoverageTests
    {
        private const string SourceId = "maplibre";
        private const string FontName = "LatinFont";

        // Same z=4 tile the rest of the prepared-cache teeth track — well inside a tile, no boundary edge case.
        private static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        private static readonly SymbolTileStore.Key TrackedKey =
            new SymbolTileStore.Key(SourceId, TrackedTile);

        /// <summary>Fill + symbol over the same source — the shape every real style has, and the one the
        /// existing prepared-cache restyle teeth deliberately lack.</summary>
        private static StyleDocument FillAndLabelStyleA() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""maplibre"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16, ""text-font"": [""LatinFont""] } }
            ]
        }");

        /// <summary>A different style — different layer set, so the content token differs and the restyle
        /// takes the full-rebuild arm (which is what Clears the symbol store).</summary>
        private static StyleDocument OtherStyleB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } }
            ]
        }");

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask"/> (mirrors
        /// <c>PreparedCacheTests.SpinToCompleted</c>).</summary>
        private static void SpinToCompleted(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }

        /// <summary>Settles the cover with <c>AwaitInFlightMeshBuilds</c>, not <c>DrainMeshBuilds</c>: the
        /// symbol pass rides the mesh kick task, so only the awaiting form lets a label build land.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 2000)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                view.AwaitInFlightMeshBuilds();
            }
        }

        private static SymbolTileStore Store(MapView view) => view.View.SymbolSubsystem.Store();

        /// <summary>Pumps until the tracked tile has a committed symbol block, and reports whether it got one.
        /// Bounded — a fixture that never commits must fail loud, not spin.</summary>
        private static bool PumpUntilLabelsCommitted(MapView view, int maxTicks = 600)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                if (Store(view).DebugBlockFor(TrackedKey) != null) return true;
                view.LateUpdate();
                view.AwaitInFlightMeshBuilds();
            }
            return Store(view).DebugBlockFor(TrackedKey) != null;
        }

        /// <summary>A view on the real <c>MapView.SetStyle</c> path with a fixture glyph source, so the
        /// symbol pipeline is LIVE (no network) and a label build can actually commit.</summary>
        private static MapView NewLabelledRestyleView(byte[] bytes, out GameObject go)
        {
            go = new GameObject("MapView_UMR139_SymbolCoverage");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(bytes);

            byte[] latinGlyphs = File.ReadAllBytes(
                Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes"));
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = latinGlyphs };
            view.View.SymbolSubsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);

            // Seed the camera BEFORE any SetStyle: SetStyle builds the render layers at the CURRENT zoom.
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();
            return view;
        }

        private static void PanOutOfCover(MapView view)
            => view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });

        private static void PanBackIntoCover(MapView view)
            => view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });

        /// <summary>UMR-139: a prepared-cache hit must never re-show a tile with its labels missing. A
        /// full-rebuild restyle Clears the symbol store while the mesh cache keeps its entries, so the
        /// pan-back tile came back as geometry only — and a hit is never pumped again, so the loss was
        /// permanent.</summary>
        [Test]
        public void StyleRoundTrip_AfterPanOut_PanBackKeepsItsLabels()
        {
            var view = NewLabelledRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(FillAndLabelStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");
                Assert.IsTrue(PumpUntilLabelsCommitted(view),
                    "drive precondition: the fixture must actually commit labels under A — without this the " +
                    "whole tooth is vacuous (it would 'pass' against a symbol pipeline that never runs).");

                PanOutOfCover(view);
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must leave cover.");
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");

                SpinToCompleted(view.SetStyle(OtherStyleB(), "B"));
                PumpUntilSettled(view);
                SpinToCompleted(view.SetStyle(FillAndLabelStyleA(), "A"));
                Assert.IsNull(Store(view).DebugBlockFor(TrackedKey),
                    "drive precondition: the restyle must have dropped the warm symbol block — that asymmetry " +
                    "against the surviving mesh cache entry IS the trigger under test.");

                PanBackIntoCover(view);
                // Bounded, not one tick: a refused hit re-shows via fetch+build, which needs several.
                for (int f = 0; f < 600 && !view.TryGetBuiltTile(TrackedTile); f++)
                {
                    view.LateUpdate();
                    view.AwaitInFlightMeshBuilds();
                }
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must re-show at all.");

                Assert.IsTrue(PumpUntilLabelsCommitted(view),
                    "DECISIVE: the re-shown tile must end with symbol coverage. Serving it from the prepared " +
                    "mesh cache marks it Built+FetchCompleted, so PumpPending skips it forever and the symbol " +
                    "kick never fires — geometry returns and every label is gone, permanently.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>The non-degeneracy companion: within ONE style the warm symbol store makes a pan-back a
        /// FULL hit, so tightening the hit predicate must not cost it. Refusing every symbol-bearing hit
        /// passes the tooth above and fails this one.</summary>
        [Test]
        public void PanBackWithinOneStyle_StillServesAFullCacheHit()
        {
            var view = NewLabelledRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(FillAndLabelStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");
                Assert.IsTrue(PumpUntilLabelsCommitted(view), "drive precondition: labels must commit under A.");

                PanOutOfCover(view);
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must leave cover.");
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");
                Assert.IsNotNull(Store(view).DebugBlockFor(TrackedKey),
                    "drive precondition: no restyle happened, so the symbol block must still be warm.");

                int hitsBefore = view.PreparedCacheHits();
                PanBackIntoCover(view);
                view.LateUpdate();

                Assert.Greater(view.PreparedCacheHits(), hitsBefore,
                    "a within-style pan-back must still register a cache hit — the symbol store kept this " +
                    "tile's block warm, so nothing is lost by serving it.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(), "a cache hit must not start a build.");
                Assert.IsNotNull(Store(view).DebugBlockFor(TrackedKey),
                    "the hit must restore the warm block, not drop it.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
