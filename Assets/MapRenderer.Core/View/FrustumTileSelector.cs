// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — Unity.Mathematics + IProjection/WebMercator/TileId + CameraPoseMath only. No System.Math.

using System; // ArgumentNullException
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// The universal visible-tile selector: a frustum quadtree cover that works for ANY projection. It descends
    /// the tile quadtree from the world tile, keeping every tile whose ground quad meets the camera's actual
    /// pitched/rotated frustum (built from the same <see cref="CameraPoseMath"/> the renderer uses). Projection
    /// specifics are asked OF the projection, not branched on here:
    /// <list type="bullet">
    ///   <item>tile positions come from <see cref="IProjection.Project"/> + <see cref="IProjection.TangentBasisAt"/>
    ///     — planar or spherical, same code;</item>
    ///   <item>a self-occluding surface (the globe) reports its horizon sphere via
    ///     <see cref="IProjection.TryGetHorizonOccluder"/>, and tiles beyond that horizon are culled (back-face);
    ///     a flat atlas reports none.</item>
    /// </list>
    ///
    /// <para>Two injected policies make behaviour selectable/comparable rather than hard-coded:
    /// <see cref="ITileLodStrategy"/> decides stop-vs-subdivide (flat single-zoom vs. screen-space LOD), and
    /// <see cref="IFarPlanePolicy"/> sets how far selection reaches (shared with the render camera so the covered
    /// frustum is exactly the rendered one).</para>
    ///
    /// <para>The planar Web-Mercator world is a FINITE atlas sheet — it does not repeat, so there is no
    /// antimeridian wrap; the globe wraps naturally through its sphere positions. Engine-free, allocation-free in
    /// steady state (reused traversal stack).</para>
    /// </summary>
    public sealed class FrustumTileSelector : IVisibleTileSelector
    {
        private readonly int    _minZoom;
        private readonly int    _maxZoom;
        private readonly int    _selectionZoomOffset;
        private readonly double _onScreenTilePx;
        private readonly ITileLodStrategy _lod;
        private readonly IFarPlanePolicy  _farPolicy;

        private readonly List<TileId> _stack = new List<TileId>(256);

        /// <param name="minZoom">Lower clamp for the near-field selection zoom.</param>
        /// <param name="maxZoom">Upper clamp for the near-field selection zoom.</param>
        /// <param name="onScreenTilePx">Target on-screen tile size (the 512 MapLibre convention). Sets BOTH the
        ///   near-field selection zoom (offset <c>log2(TilePixelSize/onScreenTilePx)</c>) AND the LOD threshold
        ///   (a tile is small enough to stop at when its projected size ≤ this).</param>
        /// <param name="lod">Stop-vs-subdivide policy. Default <see cref="FlatLodStrategy"/> (previous behaviour:
        ///   uniform single-zoom cover).</param>
        /// <param name="farPolicy">Far-plane policy — MUST match the render camera's. Default
        ///   <see cref="GeometryAwareFarPlane"/> (planar); the globe wants <see cref="MultiplierFarPlane"/>.</param>
        public FrustumTileSelector(int minZoom = 0, int maxZoom = 22, int onScreenTilePx = 512,
                                   ITileLodStrategy lod = null, IFarPlanePolicy farPolicy = null)
        {
            _minZoom = minZoom;
            _maxZoom = maxZoom;
            double tilePx     = WebMercator.TilePixelSize;
            double onScreenPx = onScreenTilePx > 0 ? onScreenTilePx : tilePx;
            _selectionZoomOffset = (int)math.round(math.log2(tilePx / onScreenPx));
            _onScreenTilePx      = onScreenPx;
            _lod       = lod       ?? new FlatLodStrategy();
            _farPolicy = farPolicy ?? new GeometryAwareFarPlane();
        }

        /// <inheritdoc/>
        public void SelectVisibleTiles(in ViewContext view, List<TileId> reuseBuffer)
        {
            if (reuseBuffer == null) throw new ArgumentNullException(nameof(reuseBuffer));
            reuseBuffer.Clear();

            IProjection      proj = view.Projection;
            CameraProperties cam  = view.Camera;
            double2          vp   = view.ViewportPx;
            if (vp.x <= 0.0 || vp.y <= 0.0) return;

            int z = cam.IntegerZoom + _selectionZoomOffset;
            if (z < _minZoom) z = _minZoom;
            if (z > _maxZoom) z = _maxZoom;

            // Frustum from the SAME pose math the renderer uses (MapCamera.SyncToCamera), with the shared far.
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, vp.y, cam.VerticalFovDeg);
            if (altitude < 0.1) altitude = 0.1;
            CameraPoseMath.ComputePose(altitude, cam.Heading.Value, cam.Tilt.Value,
                out double3 pos, out double3 fwd, out double3 up);
            double near = CameraPoseMath.NearClip(altitude);
            if (near < 0.1) near = 0.1;
            double aspect = vp.x / vp.y;
            double far    = _farPolicy.FarMetres(altitude, cam.Tilt.Value, cam.VerticalFovDeg, aspect);
            ViewFrustum frustum = ViewFrustum.FromPose(pos, fwd, up, cam.VerticalFovDeg, aspect, near, far);

            // Render-space scene frame (look-at at the origin), matching MapView.BuildSceneFrame.
            var lookAt = new GeoCoordinate
            {
                Latitude  = proj.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };
            double3  origin = proj.Project(lookAt);
            float3x3 basis  = proj.TangentBasisAt(lookAt);

            // Horizon occlusion (globe only): a tile whose bounding sphere is entirely beyond this sphere's
            // limb is hidden. A flat atlas reports none — occ == false, and none of the sphere math runs.
            bool    occ    = proj.TryGetHorizonOccluder(out double3 occCentre, out double occRadius);
            double3 camVec = occ ? new double3(pos.x - occCentre.x, pos.y - occCentre.y, pos.z - occCentre.z) : default;
            double  dc     = occ ? math.length(camVec) : 0.0;
            double  r2     = occ ? occRadius * occRadius : 0.0;

            // LOD screen-size ratio: a tile of groundSize at distance d spans ≤ onScreenTilePx px ⇔
            // groundSize ≤ lodRatio·d. worldSpan is the equatorial circumference (planar & globe alike).
            double tanV      = math.tan(Angle.FromDegrees(cam.VerticalFovDeg * 0.5).Radians);
            double lodRatio  = _onScreenTilePx * 2.0 * tanV / vp.y;
            double worldSpan = 2.0 * WebMercator.WorldExtent;

            _stack.Clear();
            _stack.Add(new TileId { Z = 0, X = 0, Y = 0 });
            while (_stack.Count > 0)
            {
                int    last = _stack.Count - 1;
                TileId t    = _stack[last];
                _stack.RemoveAt(last);

                double nearDist;
                // The whole-world tile is always partially visible, and (on a globe) its bounding sphere
                // under-bounds it — so testing it can wrongly prune everything. Skip the test at z0.
                if (t.Z == 0) nearDist = 0.0;
                else if (!TileVisible(in frustum, proj, origin, basis, occ, occCentre, camVec, dc, r2,
                                      pos, t, t.Z >= z, out nearDist)) continue;

                if (t.Z >= z) { reuseBuffer.Add(t); continue; } // near-field detail cap
                if (t.Z == 0) { PushChildren(t); continue; }    // never LOD-stop the world tile

                var ctx = new TileLodContext
                {
                    TileZoom    = t.Z,
                    TargetZoom  = z,
                    GroundSize  = worldSpan / (1L << t.Z),
                    Distance    = nearDist,
                    ScreenRatio = lodRatio,
                };
                if (_lod.StopAt(in ctx)) { reuseBuffer.Add(t); continue; } // far → coarse
                PushChildren(t);
            }
        }

        private void PushChildren(TileId t)
        {
            int cz = t.Z + 1, cx = t.X * 2, cy = t.Y * 2;
            _stack.Add(new TileId { Z = cz, X = cx,     Y = cy     });
            _stack.Add(new TileId { Z = cz, X = cx + 1, Y = cy     });
            _stack.Add(new TileId { Z = cz, X = cx,     Y = cy + 1 });
            _stack.Add(new TileId { Z = cz, X = cx + 1, Y = cy + 1 });
        }

        /// <summary>True iff tile <paramref name="t"/> meets the frustum (and, on a globe, is not entirely
        /// behind the horizon). Outputs <paramref name="nearDist"/> — the NEAREST render-space distance from the
        /// camera to the tile's bound (its bounding sphere while descending on a globe, else the corner AABB).
        ///
        /// <para>The LOD metric is the nearest point, not the centre, on purpose: a huge coarse tile that merely
        /// grazes the frustum edge has a far centre but a NEAR edge, so a centre metric would emit it coarse even
        /// though its visible sliver wants detail. Keyed on the nearest point it reads as near ⇒ the traversal
        /// subdivides it and culls the off-view part, instead of loading a giant off-screen tile.</para>
        ///
        /// <para>A globe tile is bounded by a SPHERE while descending (conservative — a coarse curved tile that
        /// contains the view isn't wrongly pruned) and by a tight corner AABB at the leaf (no ~0.7-tile
        /// over-cover). A flat atlas tile is always the tight AABB (no bulge, no occlusion).</para></summary>
        private static bool TileVisible(in ViewFrustum frustum, IProjection proj, double3 origin, float3x3 basis,
                                        bool occ, double3 occCentre, double3 camVec, double dc, double r2,
                                        double3 camPos, TileId t, bool leaf, out double nearDist)
        {
            double3 c  = RenderPoint(proj, origin, basis, t, 0.5, 0.5);
            double3 p0 = RenderPoint(proj, origin, basis, t, 0.0, 0.0);
            double3 p1 = RenderPoint(proj, origin, basis, t, 1.0, 0.0);
            double3 p2 = RenderPoint(proj, origin, basis, t, 0.0, 1.0);
            double3 p3 = RenderPoint(proj, origin, basis, t, 1.0, 1.0);

            if (occ)
            {
                double rad = Dist(c, p0);
                double d1 = Dist(c, p1); if (d1 > rad) rad = d1;
                double d2 = Dist(c, p2); if (d2 > rad) rad = d2;
                double d3 = Dist(c, p3); if (d3 > rad) rad = d3;

                // Occlusion: entirely behind the limb iff the near-most point of the bounding sphere still fails.
                double centreDot = (c.x - occCentre.x) * camVec.x + (c.y - occCentre.y) * camVec.y
                                 + (c.z - occCentre.z) * camVec.z;
                if (centreDot + rad * dc < r2) { nearDist = 0.0; return false; }

                if (!leaf)
                {
                    double dCentre = Dist(camPos, c);
                    nearDist = dCentre > rad ? dCentre - rad : 0.0; // nearest point of the bounding sphere
                    return frustum.IntersectsSphere(c, rad);        // descent: conservative sphere
                }
                // leaf: tight AABB below.
            }

            // Corner AABB (thin vertical slab so a flat y≈0 quad isn't degenerate).
            double minX = c.x, minY = c.y, minZ = c.z, maxX = c.x, maxY = c.y, maxZ = c.z;
            Grow(p0, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            Grow(p1, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            Grow(p2, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            Grow(p3, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            double aMinY = minY - 2.0, aMaxY = maxY + 2.0;

            // Nearest distance from the camera to the AABB (0 on an axis the camera is already within).
            double nx = camPos.x < minX ? minX - camPos.x : (camPos.x > maxX ? camPos.x - maxX : 0.0);
            double ny = camPos.y < aMinY ? aMinY - camPos.y : (camPos.y > aMaxY ? camPos.y - aMaxY : 0.0);
            double nz = camPos.z < minZ ? minZ - camPos.z : (camPos.z > maxZ ? camPos.z - maxZ : 0.0);
            nearDist = math.sqrt(nx * nx + ny * ny + nz * nz);
            return frustum.IntersectsAabb(new double3(minX, aMinY, minZ),
                                          new double3(maxX, aMaxY, maxZ));
        }

        private static double3 RenderPoint(IProjection proj, double3 origin, float3x3 basis,
                                           TileId t, double px, double py)
        {
            double2 ll = t.ToLonLat(px, py, 1.0);
            double3 w  = proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
            double rx = w.x - origin.x, ry = w.y - origin.y, rz = w.z - origin.z;
            return new double3(
                basis.c0.x * rx + basis.c0.y * ry + basis.c0.z * rz,
                basis.c1.x * rx + basis.c1.y * ry + basis.c1.z * rz,
                basis.c2.x * rx + basis.c2.y * ry + basis.c2.z * rz);
        }

        private static void Grow(double3 p, ref double minX, ref double minY, ref double minZ,
                                 ref double maxX, ref double maxY, ref double maxZ)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
            if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
        }

        private static double Dist(double3 a, double3 b)
        {
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return math.sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
