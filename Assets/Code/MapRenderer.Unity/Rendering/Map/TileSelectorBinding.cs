using System;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.View.Cameras;
using MapRenderer.Unity.View;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>Keeps the tile manager's visible-tile selector and per-frame selection inputs in step with the configured
    /// <see cref="MapViewConfig"/> and the camera.</summary>
    internal sealed class TileSelectorBinding
    {
        private readonly MapViewConfig     _config;
        private readonly MapCamera         _camera;
        private readonly Tile.TileManager  _tileManager;

        internal TileSelectorBinding(MapViewConfig config, MapCamera camera, Tile.TileManager tileManager)
        {
            _config      = config;
            _camera      = camera;
            _tileManager = tileManager;
        }

        /// <summary>The selector's rebuild-detection inputs, hand-rolled rather than a tuple — DO NOT
        /// "tidy" this back into one. A <c>System.ValueTuple</c> past 7 elements was measured allocating
        /// on Unity's Mono every tick: the 8th+ field wraps in a nested <c>ValueTuple</c> (the compiler's
        /// <c>TRest</c>), and STORING or comparing that shape allocated. Internal (not private) only so
        /// <c>ProjectedAreaLodWiringTests.SelectorInputsEquals_DistinguishesEveryField</c> can reach it.</summary>
        internal readonly struct SelectorInputs : IEquatable<SelectorInputs>
        {
            public readonly bool        Globe;
            public readonly TileLodMode Lod;
            public readonly int         MinZoom;
            public readonly int         MaxZoom;
            public readonly int         OnScreenPx;
            public readonly double      MercFarCap;
            public readonly double      GlobeFarCap;
            public readonly double      AreaAggressiveness;
            public readonly double      ZoomLevelHysteresis;
            public readonly double      TileDetailHysteresis;
            public readonly double      ZoomLevelPreload;

            public SelectorInputs(bool globe, TileLodMode lod, int minZoom, int maxZoom, int onScreenPx,
                                  double mercFarCap, double globeFarCap, double areaAggressiveness,
                                  double zoomLevelHysteresis, double tileDetailHysteresis, double zoomLevelPreload)
            {
                Globe = globe; Lod = lod; MinZoom = minZoom; MaxZoom = maxZoom; OnScreenPx = onScreenPx;
                MercFarCap = mercFarCap; GlobeFarCap = globeFarCap; AreaAggressiveness = areaAggressiveness;
                ZoomLevelHysteresis = zoomLevelHysteresis; TileDetailHysteresis = tileDetailHysteresis;
                ZoomLevelPreload = zoomLevelPreload;
            }

            /// <summary>Field-by-field only — no <see cref="EqualityComparer{T}"/>, no boxing, no
            /// <c>System.ValueTuple</c> machinery, so this stays allocation-free on the per-tick path.</summary>
            public bool Equals(SelectorInputs other)
                => Globe == other.Globe && Lod == other.Lod && MinZoom == other.MinZoom
                && MaxZoom == other.MaxZoom && OnScreenPx == other.OnScreenPx && MercFarCap == other.MercFarCap
                && GlobeFarCap == other.GlobeFarCap && AreaAggressiveness == other.AreaAggressiveness
                && ZoomLevelHysteresis == other.ZoomLevelHysteresis && TileDetailHysteresis == other.TileDetailHysteresis
                && ZoomLevelPreload == other.ZoomLevelPreload;

            public override bool Equals(object obj) => obj is SelectorInputs other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(HashCode.Combine(Globe, Lod, MinZoom, MaxZoom, OnScreenPx, MercFarCap, GlobeFarCap,
                                                     AreaAggressiveness), ZoomLevelHysteresis, TileDetailHysteresis, ZoomLevelPreload);
        }

        // Rebuilds the selector only when a selection input (or the projection) changes.
        private bool           _hasSelectorInputs;
        private SelectorInputs _selectorInputs;

        /// <summary>Builds the selector when it is missing or an input changed, and gives the camera the matching far plane.</summary>
        internal void Ensure()
        {
            var  tileSelection = _config.TileSelection;
            bool globe         = _camera.Projection is SphericalProjection;
            var key = new SelectorInputs(
                globe: globe, lod: tileSelection.LodMode, minZoom: tileSelection.MinZoom,
                maxZoom: tileSelection.MaxZoom, onScreenPx: tileSelection.OnScreenTilePx,
                mercFarCap: tileSelection.MercatorFarPlaneCap, globeFarCap: tileSelection.GlobeFarPlaneCap,
                areaAggressiveness: tileSelection.ProjectedAreaAggressiveness,
                zoomLevelHysteresis: tileSelection.ZoomLevelHysteresis, tileDetailHysteresis: tileSelection.TileDetailHysteresis,
                zoomLevelPreload: tileSelection.ZoomLevelPreload);
            if (_tileManager.Selector != null && _hasSelectorInputs && key.Equals(_selectorInputs)) return;
            _selectorInputs    = key;
            _hasSelectorInputs = true;

            // One FrustumTileSelector for every projection; the far-plane policy is ray-sphere for the globe and
            // geometry-aware for the flat map. The camera gets the same far, so it renders the selected frustum.
            ITileLodStrategy lod = tileSelection.LodMode switch
            {
                TileLodMode.ScreenSpaceLod => new ScreenSpaceLodStrategy(tileSelection.TileDetailHysteresis),
                TileLodMode.ProjectedArea  => new ProjectedAreaLodStrategy(tileSelection.ProjectedAreaAggressiveness),
                _                          => new FlatLodStrategy(),
            };
            IFarPlanePolicy far = _camera.Projection.TryGetHorizonOccluder(out _, out double occRadius)
                ? new RaySphereFarPlane(occRadius, tileSelection.GlobeFarPlaneCap)
                : new GeometryAwareFarPlane(tileSelection.MercatorFarPlaneCap);

            _camera.FarPlanePolicy = far;
            _tileManager.Selector = new FrustumTileSelector(
                tileSelection.MinZoom, tileSelection.MaxZoom, tileSelection.OnScreenTilePx, lod, far,
                tileSelection.ZoomLevelHysteresis, tileSelection.ZoomLevelPreload);
        }

        /// <summary>
        /// The per-frame view inputs the selector consumes. The framing viewport is
        /// <see cref="MapCamera.ViewportLogicalPx"/>, the same quantity the camera altitude frames from, so an
        /// on-screen tile keeps its physical size across panel densities.
        /// </summary>
        internal Tile.TileManager.TileSelectionConfig BuildConfig()
            => new Tile.TileManager.TileSelectionConfig
            {
                FramingViewportPx    = _camera.ViewportLogicalPx,
                Projection           = _camera.Projection,
                MaxConsumesPerTick   = _config.MaxConsumesPerTick,
                MaxMeshBuildsPerTick = _config.MaxMeshBuildsPerTick,
                MaxVerticesPerTick   = _config.MaxVerticesPerTick,
                MaxReleasesPerTick   = _config.MaxReleasesPerTick,
                MaxConcurrentTileLoads = _config.MaxConcurrentTileLoads,
                PriorityStrategy       = _config.PriorityStrategy,
                // Negative skips the clip stage; zero cuts at the tile boundary. The decode lives in the value
                // type so the parity oracles build their reference arm under the same window.
                BufferClip           = TileBufferClip.FromInspectorUnits(_config.FillTileBufferClip),
                MaxConcurrentPrepareLoads = _config.TileSelection.MaxConcurrentPrepareLoads,
                FetchRetrySeconds         = _config.TileSelection.FetchRetrySeconds,
            };
    }
}
