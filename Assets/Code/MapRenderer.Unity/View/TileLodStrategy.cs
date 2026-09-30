// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

using Unity.Mathematics;

namespace MapRenderer.Unity.View
{
    /// <summary>Selectable LOD behaviours (serialized in config / the Inspector; resolved to a strategy). Add a
    /// value here + a case where it's resolved when a new strategy lands.</summary>
    public enum TileLodMode
    {
        /// <summary>Uniform single-zoom cover (<see cref="FlatLodStrategy"/>).</summary>
        Flat = 0,

        /// <summary>Screen-space LOD: near full-detail, far progressively coarser (<see cref="ScreenSpaceLodStrategy"/>).
        /// Default; the better-looking mode under tilt.</summary>
        ScreenSpaceLod = 1,

        /// <summary>Projected-area LOD (<see cref="ProjectedAreaLodStrategy"/>): stops on the tile's true
        /// on-screen size, so it emits far fewer tiles under tilt at a visible cost to detail. Opt-in; trades
        /// quality for frame time, it does not replace <see cref="ScreenSpaceLod"/>.</summary>
        ProjectedArea = 2,
    }

    /// <summary>What the previous selection did with a tile. It lets a strategy hold a decision inside a hysteresis
    /// range around its threshold, so a tile at the threshold does not flip on every small camera move.</summary>
    public enum TileLodHistory
    {
        /// <summary>No history: a first selection, a jump, an empty viewport, or a tile the last cover did not reach.</summary>
        None = 0,

        /// <summary>The last cover emitted this tile as a coarse stop.</summary>
        Stopped = 1,

        /// <summary>The last cover subdivided this tile: it is a strict ancestor of an emitted tile.</summary>
        Refined = 2,
    }

    /// <summary>
    /// Per-tile detail policy for the frustum tile-cover traversal (planar + globe share it). The traversal
    /// handles VISIBILITY (frustum + occlusion) and the near-field detail cap; the strategy decides, for a
    /// visible tile still below the target zoom, whether to STOP here (emit this coarse tile) or SUBDIVIDE
    /// further. Swapping the strategy turns the same traversal into a flat single-zoom cover, a
    /// screen-space LOD cover, etc.
    /// </summary>
    public interface ITileLodStrategy
    {
        /// <summary>True ⇒ emit <c>ctx</c>'s tile as-is (stop descending). False ⇒ subdivide into 4 children.
        /// Only called for VISIBLE tiles whose zoom is still below <see cref="TileLodContext.TargetZoom"/> — the
        /// traversal always emits at the target zoom regardless, so termination doesn't depend on the strategy.
        /// Pure in <c>ctx</c>: the same context gives the same answer. A strategy may read
        /// <see cref="TileLodContext.History"/> to hold a decision.</summary>
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

        /// <summary>What the previous selection did with the tile; empty after a jump.</summary>
        public TileLodHistory History { get; init; }
    }

    /// <summary>Never stops early, so every visible tile is subdivided to the target zoom —
    /// a uniform single-zoom cover. Far tiles are full-resolution (and tiny on screen under tilt).</summary>
    public sealed class FlatLodStrategy : ITileLodStrategy
    {
        public string Name                          => "flat";
        public bool   StopAt(in TileLodContext ctx) => false;
    }

    /// <summary>Screen-space LOD: stop once <c>GroundSize ≤ ScreenRatio·Distance</c>, so near tiles reach the target
    /// zoom and tiles toward the horizon stop coarser. The default: under tilt it grows the tile count (the billboard
    /// approximation ignores foreshortening), but buys more horizon detail than <see cref="ProjectedAreaLodStrategy"/>.
    /// A tile-detail hysteresis <c>b</c> (zoom units) widens the threshold to a range with <c>h = 2^b</c>: a tile the last cover
    /// stopped at keeps stopping up to <c>h</c> times the threshold, and one it subdivided keeps subdividing down to
    /// <c>1/h</c> of it. With <c>b = 0</c> the rule is stateless.
    /// Limitation: the hysteresis makes flips rarer, it does not hide them. A flip still swaps a coarse tile for its
    /// children before they are built, and the fix is a geometry hold in <c>TileManager</c>.</summary>
    public sealed class ScreenSpaceLodStrategy : ITileLodStrategy
    {
        /// <summary>The largest tile-detail hysteresis: the range is twice this wide in zoom units, so it stays within one level.</summary>
        internal const double MaxTileDetailHysteresis = 0.5;

        private readonly double _hysteresisFactor;

        public string Name => "lod-screen";

        /// <param name="tileDetailHysteresis">Tile-detail hysteresis in zoom units, clamped to [0, 0.5]. 0 turns it off.</param>
        public ScreenSpaceLodStrategy(double tileDetailHysteresis = 0.0) => _hysteresisFactor = math.pow(2.0, math.clamp(tileDetailHysteresis, 0.0, MaxTileDetailHysteresis));

        public bool StopAt(in TileLodContext ctx)
        {
            double factor = ctx.History switch
            {
                TileLodHistory.Stopped => _hysteresisFactor,
                TileLodHistory.Refined => 1.0 / _hysteresisFactor,
                _                      => 1.0,
            };
            return ctx.Distance > 0.0 && ctx.GroundSize <= ctx.ScreenRatio * ctx.Distance * factor;
        }
    }

    /// <summary>Projected-area LOD: stop once the tile's TRUE projected on-screen size (foreshortening
    /// included) drops to the target. Opt-in: it emits far fewer tiles under tilt but looks visibly worse.
    /// The aggressiveness parameter scales the threshold (1.0 = the target; higher = coarser). The two rules
    /// differ by a per-tile factor, so no value reproduces <see cref="ScreenSpaceLodStrategy"/>.</summary>
    public sealed class ProjectedAreaLodStrategy : ITileLodStrategy
    {
        private readonly double _aggressiveness;

        public string Name => "lod-area";

        /// <param name="aggressiveness">Threshold scale; 1.0 stops exactly at the target on-screen size.</param>
        public ProjectedAreaLodStrategy(double aggressiveness = 1.0) => _aggressiveness = aggressiveness;

        public bool StopAt(in TileLodContext ctx) => ctx.OnScreenPx <= ctx.TargetOnScreenPx * _aggressiveness;
    }
}
