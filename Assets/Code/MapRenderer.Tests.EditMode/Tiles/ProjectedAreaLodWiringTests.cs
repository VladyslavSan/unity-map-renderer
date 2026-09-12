// Unity EditMode only — needs MapView (MapRenderer.Unity), so it is NOT part of the engine-free
// ProjectedAreaLodTests.cs / Tools/core-tests. UMR-125: the aggressiveness knob must actually reach the
// strategy through MapView.EnsureSelector, not just through a direct ProjectedAreaLodStrategy constructor
// call — a defaulted ctor argument is exactly the kind of thing that silently never gets plumbed.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using SelectorInputs = MapRenderer.Unity.Rendering.Map.MapView.SelectorInputs;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// T-AGGR-WIRED: <see cref="MapView"/> must build a <see cref="ProjectedAreaLodStrategy"/> carrying the
    /// Inspector's aggressiveness value, and rebuild the selector when that value changes.
    /// </summary>
    public class ProjectedAreaLodWiringTests
    {
        // Same fixture pose as TiltCoverGrowthTests/ProjectedAreaLodTests (globe z13 1600x900 tilt 60) —
        // aggressiveness 2.0 there measures 22, aggressiveness 1.0 measures 39.
        private static readonly double2 Viewport = new double2(1600.0, 900.0);

        private static CameraProperties FixtureCam()
            => new CameraProperties(
                new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52, Altitude = 0.0 }, 13.0, 0.0, 60.0);

        /// <summary>
        /// T-AGGR-WIRED. Config selects <see cref="TileLodMode.ProjectedArea"/> at aggressiveness 2.0; the
        /// selector <see cref="MapView"/> built must read back the 2.0 cover (22), not the ctor default's 39.
        /// RED recipe: drop the aggressiveness argument at the <c>MapView</c> call site so the ctor default
        /// applies — the cover reads 39 instead. Also covers the <c>_selectorInputs</c> rebuild key: if the
        /// knob is missing from it, a second selection at a changed value returns the stale cover.
        /// </summary>
        [Test]
        public void ProjectedAreaAggressiveness_ReachesTheSelector_ThroughMapView()
        {
            var go   = new GameObject("MapView_UMR125_AggrWired");
            var view = go.AddComponent<MapView>();
            try
            {
                view.WithTestCamera(projection: new SphericalProjection());
                view.Config.TileSelection.LodMode                     = TileLodMode.ProjectedArea;
                view.Config.TileSelection.GlobeFarPlaneCap            = 8.0;
                view.Config.TileSelection.ProjectedAreaAggressiveness = 2.0;

                view.LateUpdate(); // EnsureSelector() must build ProjectedAreaLodStrategy(2.0)

                var probe = new ViewContext
                {
                    Camera     = FixtureCam(),
                    ViewportPx = Viewport,
                    Projection = new SphericalProjection(),
                };
                var cover = new List<TileId>();
                view.View.TileManager.Selector.SelectVisibleTiles(in probe, cover);

                Assert.AreEqual(22, cover.Count,
                    "aggressiveness 2.0 must reach the strategy through MapView's wiring (measured cover 22); "
                  + "39 means the ctor default (1.0) silently applied instead.");

                // Changing the knob alone (nothing else) must rebuild the selector — the _selectorInputs key.
                view.Config.TileSelection.ProjectedAreaAggressiveness = 1.0;
                view.LateUpdate();
                view.View.TileManager.Selector.SelectVisibleTiles(in probe, cover);
                Assert.AreEqual(39, cover.Count,
                    "changing ProjectedAreaAggressiveness alone must rebuild the selector; a stale cover here "
                  + "means the knob is missing from the _selectorInputs rebuild key.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// UMR-125: <see cref="SelectorInputs.Equals(SelectorInputs)"/> is hand-written field-by-field (not
        /// a tuple — see its summary for why), which means a NINTH field added later can be silently left
        /// out of the comparison. Changing each field ALONE from a baseline must flip <c>Equals</c> to
        /// false — a field missing from the comparison passes vacuously here instead.
        /// </summary>
        [Test]
        public void SelectorInputsEquals_DistinguishesEveryField()
        {
            var baseline = new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0, maxZoom: 14,
                onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0);
            Assert.IsTrue(baseline.Equals(baseline), "sanity: an instance must equal itself");

            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: true, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "Globe");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.ScreenSpaceLod,
                minZoom: 0, maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "Lod");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 1,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "MinZoom");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 15, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "MaxZoom");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 256, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "OnScreenPx");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 5.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "MercFarCap");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 9.0, areaAggressiveness: 1.0)), "GlobeFarCap");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 2.0)), "AreaAggressiveness");
        }
    }
}
