// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

namespace MapRenderer.Core.View
{
    /// <summary>Selectable LOD behaviours (serialized in config / the Inspector; resolved to a strategy). Add a
    /// value here + a case where it's resolved when a new strategy lands.</summary>
    public enum TileLodMode
    {
        /// <summary>Previous behaviour: uniform single-zoom cover (<see cref="FlatLodStrategy"/>).</summary>
        Flat = 0,

        /// <summary>Screen-space LOD: near full-detail, far progressively coarser (<see cref="ScreenSpaceLodStrategy"/>).
        /// Default; the better-looking mode under tilt.</summary>
        ScreenSpaceLod = 1,

        /// <summary>Projected-area LOD (<see cref="ProjectedAreaLodStrategy"/>): stops on the tile's true
        /// on-screen size, so it emits far fewer tiles under tilt at a visible cost to detail. Opt-in; trades
        /// quality for frame time, it does not replace <see cref="ScreenSpaceLod"/>.</summary>
        ProjectedArea = 2,
    }

    /// <summary>
    /// Per-tile detail policy for the frustum tile-cover traversal (planar + globe share it). The traversal
    /// handles VISIBILITY (frustum + occlusion) and the near-field detail cap; the strategy decides, for a
    /// visible tile still below the target zoom, whether to <b>stop here</b> (emit this coarse tile) or
    /// <b>subdivide</b> further. Swapping the strategy turns the same traversal into a flat single-zoom cover, a
    /// screen-space LOD cover, etc. — so behaviours coexist and can be selected/compared at runtime rather than
    /// one being hard-coded.
    /// </summary>
    public interface ITileLodStrategy
    {
        /// <summary>True ⇒ emit <c>ctx</c>'s tile as-is (stop descending). False ⇒ subdivide into 4 children.
        /// Only called for VISIBLE tiles whose zoom is still below <see cref="TileLodContext.TargetZoom"/> — the
        /// traversal always emits at the target zoom regardless, so termination doesn't depend on the strategy.</summary>
        bool StopAt(in TileLodContext ctx);

        /// <summary>Short stable id for logging / the Inspector (e.g. "flat", "lod-screen").</summary>
        string Name { get; }
    }

    /// <summary>Inputs a <see cref="ITileLodStrategy"/> may use to decide stop-vs-subdivide: either the
    /// distance-rule's render-space metres (camera-relative, look-at at the render origin), or the area-rule's
    /// screen pixels — both sets are populated for every candidate; a strategy reads the fields it needs.</summary>
    public readonly struct TileLodContext
    {
        /// <summary>The candidate tile's zoom (always &lt; <see cref="TargetZoom"/> when the strategy is called).</summary>
        public int TileZoom { get; init; }

        /// <summary>The near-field selection zoom the traversal caps at (camera zoom + on-screen-size offset).</summary>
        public int TargetZoom { get; init; }

        /// <summary>The tile's ground size in render metres (<c>2·WorldExtent / 2^TileZoom</c>).</summary>
        public double GroundSize { get; init; }

        /// <summary>Distance from the camera to the tile's NEAREST point (its bound), render metres — NOT the
        /// centre. A tile's on-screen size is set by its nearest, largest-projecting part, so keying LOD on the
        /// nearest point stops a coarse tile grazing the frustum edge (far centre, near edge) from being emitted
        /// coarse when its visible sliver actually wants detail.</summary>
        public double Distance { get; init; }

        /// <summary>Screen-size ratio <c>onScreenTilePx·2·tan(fov/2)/viewportHeightPx</c>: a tile of
        /// <see cref="GroundSize"/> at <see cref="Distance"/> spans <c>GroundSize/Distance/ratio · onScreenTilePx</c>
        /// pixels, so it is ≤ the target on-screen size exactly when <c>GroundSize ≤ ratio·Distance</c>.</summary>
        public double ScreenRatio { get; init; }

        /// <summary>The tile's true projected on-screen size, in pixels (square root of its screen-space quad
        /// area). Models foreshortening; <see cref="Distance"/>/<see cref="GroundSize"/> approximate it as a
        /// billboard instead.</summary>
        public double OnScreenPx { get; init; }

        /// <summary>The selector's target on-screen tile size, in pixels (the 512 MapLibre convention).</summary>
        public double TargetOnScreenPx { get; init; }
    }

    /// <summary>Previous behaviour: never stop early, so every visible tile is subdivided to the target zoom —
    /// a uniform single-zoom cover. Far tiles are full-resolution (and tiny on screen under tilt).</summary>
    public sealed class FlatLodStrategy : ITileLodStrategy
    {
        public string Name                          => "flat";
        public bool   StopAt(in TileLodContext ctx) => false;
    }

    /// <summary>Screen-space (distance-driven) LOD: stop once the tile's projected size drops to the target
    /// on-screen tile size — i.e. when <c>GroundSize ≤ ScreenRatio·Distance</c>. Near the camera tiles reach the
    /// target zoom (full detail); toward the horizon they stop progressively coarser. This is the DEFAULT and
    /// the better-looking mode under tilt: the tile count still grows under tilt (globe z13 measured 24 → 112,
    /// tilt 0 to 60) because the billboard approximation ignores foreshortening, but that growth buys more
    /// detail toward the horizon than the coarser, cheaper <see cref="ProjectedAreaLodStrategy"/> alternative.
    ///
    /// <para><b>Known drawback — LOD-churn white flash.</b> Because the emitted zoom is distance-driven, panning
    /// toward the view vector continuously pulls far tiles nearer, so each crosses the threshold and its coarse
    /// tile is swapped for four tiles one zoom finer. Under the current instant tile-atomic consume the coarse
    /// tile leaves the cover before its finer replacements have fetched+built, so that patch flashes WHITE
    /// for a frame or two. It is inherent to any screen-space LOD without tile RETENTION — the fix is in the
    /// tile lifecycle (TileManager), not here: keep a parent tile drawable until its finer children are ready
    /// (retain-until-replaced), or cross-fade. <see cref="FlatLodStrategy"/> (uniform zoom) doesn't churn, so it
    /// doesn't flash.</para></summary>
    public sealed class ScreenSpaceLodStrategy : ITileLodStrategy
    {
        public string Name => "lod-screen";

        public bool StopAt(in TileLodContext ctx)
        {
            return ctx.Distance > 0.0 && ctx.GroundSize <= ctx.ScreenRatio * ctx.Distance;
        }
    }

    /// <summary>Projected-area LOD: stop once the tile's TRUE projected on-screen size (foreshortening
    /// included) drops to the target. Opt-in, not a fix for <see cref="ScreenSpaceLodStrategy"/> — the two
    /// trade different things: this rule emits far fewer tiles under tilt (globe z13 measured 24 → 39, tilt 0
    /// to 60, vs the default's 24 → 112) but "looks quite worse" under tilt (maintainer's own viewing), so it
    /// costs visual quality to buy frame time.
    ///
    /// <para>The aggressiveness ctor parameter scales the whole threshold (1.0 = stop exactly at the target;
    /// higher = coarser, fewer tiles). It moves the curve, not its shape: the two rules differ by a per-tile
    /// geometric factor, not a constant, so no value of it reproduces <see cref="ScreenSpaceLodStrategy"/>.</para></summary>
    public sealed class ProjectedAreaLodStrategy : ITileLodStrategy
    {
        private readonly double _aggressiveness;

        public string Name => "lod-area";

        /// <param name="aggressiveness">Threshold scale; 1.0 stops exactly at the target on-screen size.</param>
        public ProjectedAreaLodStrategy(double aggressiveness = 1.0) => _aggressiveness = aggressiveness;

        public bool StopAt(in TileLodContext ctx) => ctx.OnScreenPx <= ctx.TargetOnScreenPx * _aggressiveness;
    }
}
