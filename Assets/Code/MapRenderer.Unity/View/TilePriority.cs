// Engine-free: no UnityEngine references — Unity.Mathematics + IProjection/TileId/CameraPoseMath only.
// No System.Math. Compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Selects which render-space distance metric <see cref="TilePriority"/> uses to rank tiles for
    /// loading. Both diverge under tilt: (a) favours what's actually centred on screen; (b) favours
    /// whatever the camera happens to be closest to (the near/bottom edge under a pitched view).
    /// </summary>
    public enum TilePriorityStrategy
    {
        /// <summary>[Default] Ground (horizontal-only) distance from a tile's centre to the camera's
        /// LookAt point — the render-space origin, which is also the viewport centre at every
        /// tilt/heading (<see cref="CameraPoseMath.ComputeRelativePose"/> orbits the look-at). A faithful
        /// "what's in the middle of the screen" metric.</summary>
        GroundDistanceToLookAt = 0,

        /// <summary>Full 3D render-space distance from a tile's centre to the camera position. Under
        /// tilt this favours the near/bottom edge of the frustum over the visual centre.</summary>
        CameraDistance = 1,
    }

    /// <summary>
    /// Render-space inputs shared by every tile's priority computation within one Tick — computed once
    /// (the SAME <see cref="CameraPoseMath"/> pose math the render camera and <see cref="FrustumTileSelector"/>
    /// use) and reused per tile, so <see cref="TilePriority.Key"/> stays a pure, allocation-free function of
    /// <c>(tile, context)</c> rather than re-deriving the camera pose on every call.
    /// </summary>
    public readonly struct TilePriorityContext
    {
        /// <summary>The active pixel↔ground projection (planar or spherical).</summary>
        public readonly IProjection Projection;

        /// <summary>Render-space projection of the camera's LookAt point — the scene-frame origin (matches
        /// <see cref="FrustumTileSelector"/>'s "look-at at the origin" render frame).</summary>
        public readonly double3 Origin;

        /// <summary>The render-space ENU tangent basis at LookAt (columns c0=east, c1=up, c2=north).</summary>
        public readonly float3x3 Basis;

        /// <summary>Render-space camera position, relative to <see cref="Origin"/> (LookAt at the origin).</summary>
        public readonly double3 CameraPos;

        /// <summary>Which metric <see cref="TilePriority.Key"/> computes.</summary>
        public readonly TilePriorityStrategy Strategy;

        public TilePriorityContext(IProjection projection, double3 origin, float3x3 basis, double3 cameraPos,
            TilePriorityStrategy strategy)
        {
            Projection = projection;
            Origin     = origin;
            Basis      = basis;
            CameraPos  = cameraPos;
            Strategy   = strategy;
        }

        /// <summary>
        /// Builds the context for one Tick from the camera + viewport — the exact render-space frame
        /// <see cref="FrustumTileSelector.SelectVisibleTiles"/> builds (look-at clamped to the projection's
        /// valid latitude, then projected; pose from <see cref="CameraPoseMath.ComputeRelativePose"/> at the
        /// same altitude/near derivation), so priority ranking and cover selection never disagree about
        /// where "the camera" is.
        /// </summary>
        /// <param name="cam">The current camera state.</param>
        /// <param name="viewportPx">The framing viewport in pixels (drives altitude-from-zoom).</param>
        /// <param name="projection">The active pixel↔ground projection.</param>
        /// <param name="strategy">Which metric to rank by.</param>
        public static TilePriorityContext From(in CameraProperties cam, double2 viewportPx,
            IProjection projection, TilePriorityStrategy strategy)
        {
            var lookAt = new GeoCoordinate
            {
                Latitude  = projection.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };

            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, viewportPx.y, cam.VerticalFovDeg);
            if (altitude < 0.1) altitude = 0.1;
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value,
                out double3 pos, out _, out _);

            double3  origin = projection.Project(lookAt);
            float3x3 basis  = projection.TangentBasisAt(lookAt);

            return new TilePriorityContext(projection, origin, basis, pos, strategy);
        }
    }

    /// <summary>
    /// Pure, allocation-free tile-load priority math — decides which not-yet-loaded tile to admit/build/
    /// consume first, so a fetch/mesh-build/consume burst reaches the screen centre before the edges
    /// ("center-first tile loading"). No cover/selection/LOD concern here (that stays
    /// <see cref="FrustumTileSelector"/>'s job); this only orders an ALREADY-selected set.
    /// </summary>
    public static class TilePriority
    {
        /// <summary>The priority key for <paramref name="tile"/> under <paramref name="ctx"/> — SMALLER is
        /// higher priority (closer to the ranking metric's zero). Deterministic and side-effect-free: same
        /// tile + same context ⇒ same key, always.</summary>
        public static double Key(in TileId tile, in TilePriorityContext ctx)
        {
            double3 c = RenderCenter(tile, ctx.Projection, ctx.Origin, ctx.Basis);

            if (ctx.Strategy == TilePriorityStrategy.CameraDistance)
            {
                double dx = c.x - ctx.CameraPos.x, dy = c.y - ctx.CameraPos.y, dz = c.z - ctx.CameraPos.z;
                return math.sqrt(dx * dx + dy * dy + dz * dz);
            }

            // GroundDistanceToLookAt: horizontal-only (east/north; c1/"up" dropped) — the render origin
            // IS the LookAt point, so this is exactly the tile's ground distance to screen-centre. On the
            // planar Mercator the up component is always ~0 anyway; dropping it explicitly is what makes
            // the metric a genuine GROUND distance on the globe too (curvature bulge excluded).
            return math.sqrt(c.x * c.x + c.z * c.z);
        }

        /// <summary>The tile's centre (px=0.5, py=0.5), projected and expressed in <paramref name="basis"/>
        /// relative to <paramref name="origin"/> — the same construction
        /// <see cref="FrustumTileSelector"/>'s <c>RenderPoint</c> uses for a tile corner, at the centre
        /// instead (priority ranks by "where the middle of the tile is", not its nearest edge — unlike the
        /// selector's near-distance LOD metric, which reads the nearest point).</summary>
        private static double3 RenderCenter(TileId t, IProjection proj, double3 origin, float3x3 basis)
        {
            double2 ll = t.ToLonLat(0.5, 0.5, 1.0);
            double3 w  = proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
            double  rx = w.x - origin.x, ry = w.y - origin.y, rz = w.z - origin.z;
            return new double3(
                basis.c0.x * rx + basis.c0.y * ry + basis.c0.z * rz,
                basis.c1.x * rx + basis.c1.y * ry + basis.c1.z * rz,
                basis.c2.x * rx + basis.c2.y * ry + basis.c2.z * rz);
        }

        /// <summary>
        /// Stable ascending in-place sort of <paramref name="tiles"/> by <see cref="Key"/> — insertion sort
        /// (the cover/desired set is dozens of entries, so O(n²) beats allocating a
        /// <see cref="System.Comparison{T}"/> closure or boxing through <c>List&lt;T&gt;.Sort</c>'s
        /// <c>IComparer</c> path). <paramref name="sortKeys"/> is caller-owned scratch, resized (never
        /// implicitly shrunk) to fit — reused across calls so steady-state sorting allocates nothing. Ties
        /// break on <see cref="TileId"/> (Z, then X, then Y) for a deterministic order independent of the
        /// input list's own ordering.
        /// </summary>
        /// <param name="tiles">The list to sort in place.</param>
        /// <param name="sortKeys">Caller-owned scratch array, grown (never shrunk) to at least
        /// <c>tiles.Count</c> by this call if it starts smaller.</param>
        /// <param name="ctx">The shared render-space priority context for this Tick.</param>
        public static void SortByPriority(List<TileId> tiles, ref double[] sortKeys, in TilePriorityContext ctx)
        {
            int n = tiles.Count;
            if (sortKeys.Length < n)
                sortKeys = new double[math.max(n, sortKeys.Length * 2)];

            double[] keys = sortKeys;
            for (int i = 0; i < n; i++) keys[i] = Key(tiles[i], in ctx);

            for (int i = 1; i < n; i++)
            {
                double k    = keys[i];
                TileId item = tiles[i];
                int    j    = i - 1;
                while (j >= 0 && IsAfter(keys[j], tiles[j], k, item))
                {
                    keys[j + 1]  = keys[j];
                    tiles[j + 1] = tiles[j];
                    j--;
                }

                keys[j + 1]  = k;
                tiles[j + 1] = item;
            }
        }

        /// <summary>True iff (keyA, a) sorts strictly AFTER (keyB, b) — smaller key first, TileId
        /// (Z, X, Y) tiebreak on an exact key match.</summary>
        private static bool IsAfter(double keyA, TileId a, double keyB, TileId b)
        {
            if (keyA != keyB) return keyA > keyB;
            if (a.Z != b.Z) return a.Z > b.Z;
            if (a.X != b.X) return a.X > b.X;
            return a.Y > b.Y;
        }
    }
}
