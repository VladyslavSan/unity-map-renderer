// UMR-151 T6/T7: the real-SetStyle drive scaffold, lifted from PreparedCacheTests (Tiles/) so a second
// fixture can drive a settled cover through MapView.SetStyle without pasting it. EditMode-only by
// construction: Object.Destroy is immediate here, so CountMeshObjects and every fake-null read below are
// honest; PlayMode defers destruction to end-of-frame and breaks both. The same holds for every fixture
// that drives through this scaffold.

using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile; // UniTaskParkExtensions.WaitOffPlayerLoop

namespace MapRenderer.Tests
{
    /// <summary>Drive scaffold for tests that need a settled real cover behind
    /// <see cref="MapViewComponent.SetStyle"/> — view wiring, a blocking task wait, and the deterministic
    /// EditMode pump. Copied from <c>MapRenderer.Tests.Tiles.PreparedCacheTests</c>, which keeps its own
    /// private originals.</summary>
    internal static class RestyleHarness
    {
        /// <summary>z=4 tile containing (lon=10, lat=10) — well inside a tile, away from any boundary
        /// floating-point edge case.</summary>
        internal static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        /// <summary>Camera properties at a longitude/latitude/zoom, north-up and untilted.</summary>
        internal static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Settles the cover without Thread.Sleep: each tick kicks builds, then
        /// <c>DrainMeshBuilds</c> spins the kicked ThreadPool builds to completion, so the next tick
        /// consumes them.</summary>
        internal static void PumpUntilSettled(MapViewComponent view, int maxTicks = 2000)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        /// <summary>Process-wide live <see cref="Mesh"/> count — a DELTA instrument, never an absolute.
        /// Capture before the fixture builds anything and compare after teardown.</summary>
        internal static int CountMeshObjects() => Resources.FindObjectsOfTypeAll<Mesh>().Length;

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask"/>; re-throws whatever the
        /// task faulted with, so an aborted <c>SetStyle</c> surfaces to <c>Assert.Throws</c>.</summary>
        internal static void SpinToCompleted(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }

        /// <summary>Wires a view for the real <see cref="MapViewComponent.SetStyle"/> path (never
        /// LoadTestStyle) — a tiles[]-only vector source needs no TileJSON fetch, so SetStyle never awaits
        /// network. Leaves <c>Config.Backend</c> at its default, so <c>EntitiesRenderer</c> is non-null.</summary>
        internal static MapViewComponent NewRestyleView(byte[] bytes, out GameObject go)
        {
            go = new GameObject("MapView_CommitAtomicity");
            var view = go.AddComponent<MapViewComponent>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(bytes);
            // Seed the camera BEFORE any SetStyle: SetStyle builds the render layers at the CURRENT zoom.
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();
            return view;
        }
    }
}
