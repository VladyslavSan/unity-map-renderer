// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

namespace MapRenderer.Core.View
{
    /// <summary>Selectable LOD behaviours (serialized in config / the Inspector; resolved to a strategy). Add a
    /// value here + a case where it's resolved when a new strategy lands.</summary>
    public enum TileLodMode
    {
        /// <summary>Previous behaviour: uniform single-zoom cover (<see cref="FlatLodStrategy"/>).</summary>
        Flat = 0,

        /// <summary>Screen-space LOD: near full-detail, far progressively coarser (<see cref="ScreenSpaceLodStrategy"/>).</summary>
        ScreenSpaceLod = 1,
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

    /// <summary>Inputs a <see cref="ITileLodStrategy"/> may use to decide stop-vs-subdivide. All render-space
    /// metres; camera-relative (look-at at the render origin).</summary>
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
    /// target zoom (full detail); toward the horizon they stop progressively coarser, so no tile renders tiny
    /// and the tile count stays roughly constant under tilt.
    ///
    /// <para><b>Known drawback — LOD-churn white flash.</b> Because the emitted zoom is distance-driven, panning
    /// toward the view vector continuously pulls far tiles nearer, so each crosses the threshold and its coarse
    /// tile is swapped for four tiles one zoom finer. Under the current instant tile-atomic consume the coarse
    /// tile leaves the cover before its finer replacements have fetched+tessellated, so that patch flashes WHITE
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
}