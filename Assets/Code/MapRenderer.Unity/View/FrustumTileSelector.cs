// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — Unity.Mathematics + IProjection/WebMercator/TileId + CameraPoseMath only. No System.Math.

using System; // ArgumentNullException
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View.Cameras;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// The visible-tile selector for any projection: it descends the quadtree from the world tile and keeps every
    /// tile whose ground quad meets the camera frustum (from <see cref="CameraPoseMath"/>). Positions come from
    /// <see cref="IProjection.Project"/>/<see cref="IProjection.TangentBasisAt"/>, and a globe culls tiles past
    /// <see cref="IProjection.TryGetHorizonOccluder"/>. <see cref="ITileLodStrategy"/> decides stop-vs-subdivide;
    /// <see cref="IFarPlanePolicy"/> is shared with the render camera. The planar world does not wrap.
    /// Non-local invariant: one instance is one camera's history. The target level holds inside a hysteresis of
    /// the continuous level, and each tile's previous decision reaches the strategy. A jump starts without history.
    /// </summary>
    public sealed class FrustumTileSelector : IVisibleTileSelector
    {
        private readonly int    _minZoom;
        private readonly int    _maxZoom;
        private readonly int    _selectionZoomOffset;
        private readonly double _onScreenTilePx;
        private readonly double _zoomLevelHysteresis;
        private readonly double _zoomLevelPreload;
        private readonly ITileLodStrategy _lod;
        private readonly IFarPlanePolicy  _farPolicy;

        private readonly List<TileId> _stack = new List<TileId>(256);
        private readonly HashSet<TileId> _planned = new HashSet<TileId>(256); // dedupes one preload or keep list

        // The previous selection, read as TileLodContext.History. Cleared by a jump, not by a level change.
        private readonly HashSet<TileId> _prevEmitted = new HashSet<TileId>(256);
        private readonly HashSet<TileId> _prevRefined = new HashSet<TileId>(256); // strict ancestors of emitted

        // Largest zoom-level hysteresis: the holding window is 1 + 2h wide and stays below two levels.
        internal const double MaxZoomLevelHysteresis = 0.5;

        // The held target level in use, or -1 before the first selection and after an empty viewport.
        private int    _level = -1;
        private double _continuous;

        /// <param name="minZoom">Lower clamp for the near-field selection zoom.</param>
        /// <param name="maxZoom">Upper clamp for the near-field selection zoom.</param>
        /// <param name="onScreenTilePx">Target on-screen tile size (512 convention). Sets both the zoom offset
        ///   <c>log2(TilePixelSize/onScreenTilePx)</c> and the LOD stop threshold (projected size ≤ this).</param>
        /// <param name="lod">Stop-vs-subdivide policy. Default <see cref="FlatLodStrategy"/> (uniform zoom).</param>
        /// <param name="farPolicy">Far-plane policy — MUST match the render camera's. Default
        ///   <see cref="GeometryAwareFarPlane"/> (planar); the globe wants <see cref="MultiplierFarPlane"/>.</param>
        /// <param name="zoomLevelHysteresis">Zoom units the target level holds past each integer, clamped to [0, 0.5].
        ///   0 follows the camera's integer zoom exactly. It also widens the keep set past the preload lead.</param>
        /// <param name="zoomLevelPreload">Zoom units before a level switch at which the preload set starts,
        /// clamped to [-1, 1]. Negative starts after the whole level.</param>
        public FrustumTileSelector(int minZoom = 0, int maxZoom = 22, int onScreenTilePx = 512,
                                   ITileLodStrategy lod = null, IFarPlanePolicy farPolicy = null,
                                   double zoomLevelHysteresis = 0.0, double zoomLevelPreload = 0.0)
        {
            _zoomLevelPreload = math.clamp(zoomLevelPreload, -1.0, 1.0);
            _zoomLevelHysteresis = math.clamp(zoomLevelHysteresis, 0.0, MaxZoomLevelHysteresis);
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
        public void SelectVisibleTiles(in ViewContext view, TileSelection selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            selection.Clear();

            IProjection      proj = view.Projection;
            CameraProperties cam  = view.Camera;
            double2          vp   = view.ViewportPx;
            if (vp.x <= 0.0 || vp.y <= 0.0) { ClearHistory(); return; }

            double continuous = cam.Zoom + _selectionZoomOffset;
            int    z          = ChooseLevel(in cam, proj, continuous);

            // Render-space scene frame (look-at at the origin), matching MapView.BuildSceneFrame.
            var lookAt = new GeoCoordinate
            {
                Latitude  = proj.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };

            // Frustum from the SAME pose math the renderer uses (MapCamera.SyncToCamera), with the shared far.
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, vp.y, cam.VerticalFovDeg);
            if (altitude < 0.1) altitude = 0.1;
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value,
                out double3 pos, out double3 fwd, out double3 up);
            double near = CameraPoseMath.NearClip(altitude);
            if (near < 0.1) near = 0.1;
            double aspect = vp.x / vp.y;
            double far    = _farPolicy.FarMetres(altitude, cam.Tilt.Value, cam.VerticalFovDeg, aspect);

            // Horizon occlusion (globe only): a tile whose bounding sphere is entirely beyond this sphere's
            // limb is hidden. A flat atlas reports none — occ == false, and none of the sphere math runs.
            bool    occ    = proj.TryGetHorizonOccluder(out double3 occCentre, out double occRadius);
            double3 camVec = occ ? new double3(pos.x - occCentre.x, pos.y - occCentre.y, pos.z - occCentre.z) : default;

            // LOD screen-size ratio: a tile of groundSize at distance d spans ≤ onScreenTilePx px ⇔
            // groundSize ≤ lodRatio·d. worldSpan is the equatorial circumference (planar & globe alike).
            double tanV = math.tan(Angle.FromDegrees(cam.VerticalFovDeg * 0.5).Radians);

            var frame = new TraversalFrame(
                projection: proj,
                frustum:    ViewFrustum.FromPose(pos, fwd, up, cam.VerticalFovDeg, aspect, near, far),
                origin:     proj.Project(lookAt),
                basis:      proj.TangentBasisAt(lookAt),
                occluded:   occ,
                occCentre:  occCentre,
                camVec:     camVec,
                camDist:    occ ? math.length(camVec) : 0.0,
                occRadius2: occ ? occRadius * occRadius : 0.0,
                camPos:     pos,
                // Pinhole pixel basis, for measuring a tile's actual projected on-screen size (the area-rule input).
                pixels:     new PixelBasis(pos, fwd, up, vp.y / (2.0 * tanV), near),
                lodRatio:   _onScreenTilePx * 2.0 * tanV / vp.y,
                worldSpan:  2.0 * WebMercator.WorldExtent);

            Traverse(in frame, z, 1.0, selection.Cover);
            // The shifted passes read the history of the PREVIOUS cover, so they run before it is remembered.
            PlanAhead(in frame, z, continuous, _zoomLevelPreload, selection.Preload);
            // Keep holds Preload by construction. With no hysteresis its lead equals the preload lead, so it needs no pass.
            if (_zoomLevelHysteresis > 0.0)
                PlanAhead(in frame, z, continuous, _zoomLevelPreload + _zoomLevelHysteresis, selection.Keep);
            // An index loop, not AddRange: a BCL may copy an ICollection source through a temporary array.
            for (int i = 0; i < selection.Preload.Count; i++)
                selection.Keep.Add(selection.Preload[i]);

            RememberCover(selection.Cover);
            DedupeOutsideCover(selection.Preload);
            DedupeOutsideCover(selection.Keep);
            _level      = z;
            _continuous = continuous;
        }

        /// <summary>Appends to <paramref name="into"/> the tiles the traversal would emit if the camera were
        /// <paramref name="lead"/> zoom units closer, and as many units farther. The level target moves one
        /// level once the zoom is within the lead of the switch. The LOD thresholds scale by the lead, and not
        /// at all for a negative lead (no preload).</summary>
        private void PlanAhead(in TraversalFrame frame, int level, double continuous, double lead, List<TileId> into)
        {
            double scale = math.pow(2.0, math.max(lead, 0.0));
            int closer = continuous >= level + 1 - lead && level + 1 <= _maxZoom ? level + 1 : level;
            int farther = continuous < level + lead && level - 1 >= _minZoom ? level - 1 : level;
            Traverse(in frame, closer, 1.0 / scale, into);
            Traverse(in frame, farther, scale, into);
        }

        /// <summary>Removes duplicates, and the tiles of the cover just remembered, from
        /// <paramref name="tiles"/>, keeping order.</summary>
        private void DedupeOutsideCover(List<TileId> tiles)
        {
            _planned.Clear();
            int kept = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                TileId tile = tiles[i];
                if (_prevEmitted.Contains(tile) || !_planned.Add(tile)) continue;
                tiles[kept++] = tile;
            }

            tiles.RemoveRange(kept, tiles.Count - kept);
        }

        /// <summary>The descent from the world tile: appends to <paramref name="output"/> every visible tile at
        /// or past the target zoom <paramref name="level"/>, or earlier where the LOD strategy stops.
        /// <paramref name="ratioScale"/> scales the LOD screen ratio and the target on-screen size, so every
        /// strategy sees the same shift.</summary>
        private void Traverse(in TraversalFrame frame, int level, double ratioScale, List<TileId> output)
        {
            double lodRatio = frame.LodRatio * ratioScale;
            _stack.Clear();
            _stack.Add(new TileId { Z = 0, X = 0, Y = 0 });
            while (_stack.Count > 0)
            {
                int    last = _stack.Count - 1;
                TileId t    = _stack[last];
                _stack.RemoveAt(last);

                double nearDist;
                double onScreenPx;
                // The whole-world tile is always partially visible, and (on a globe) its bounding sphere
                // under-bounds it — so testing it can wrongly prune everything. Skip the test at z0.
                if (t.Z == 0) { nearDist = 0.0; onScreenPx = 0.0; }
                else if (!TileVisible(in frame, t, t.Z >= level, out nearDist, out onScreenPx)) continue;

                if (t.Z >= level) { output.Add(t); continue; } // near-field detail cap
                if (t.Z == 0) { PushChildren(t); continue; }   // never LOD-stop the world tile

                var ctx = new TileLodContext
                {
                    TileZoom         = t.Z,
                    TargetZoom       = level,
                    GroundSize       = frame.WorldSpan / (1L << t.Z),
                    Distance         = nearDist,
                    ScreenRatio      = lodRatio,
                    OnScreenPx       = onScreenPx,
                    TargetOnScreenPx = _onScreenTilePx * ratioScale,
                    History          = HistoryOf(t),
                };
                if (_lod.StopAt(in ctx)) { output.Add(t); continue; } // far → coarse
                PushChildren(t);
            }
        }

        /// <summary>The per-call camera quantities every traversal pass reads.</summary>
        private readonly struct TraversalFrame
        {
            public readonly IProjection Projection;
            public readonly ViewFrustum Frustum;
            public readonly double3     Origin;
            public readonly float3x3    Basis;
            public readonly bool        Occluded;
            public readonly double3     OccCentre;
            public readonly double3     CamVec;
            public readonly double      CamDist;
            public readonly double      OccRadius2;
            public readonly double3     CamPos;
            public readonly PixelBasis  Pixels;
            public readonly double      LodRatio;
            public readonly double      WorldSpan;

            public TraversalFrame(IProjection projection, ViewFrustum frustum, double3 origin, float3x3 basis,
                                  bool occluded, double3 occCentre, double3 camVec, double camDist,
                                  double occRadius2, double3 camPos, PixelBasis pixels, double lodRatio,
                                  double worldSpan)
            {
                Projection = projection;
                Frustum    = frustum;
                Origin     = origin;
                Basis      = basis;
                Occluded   = occluded;
                OccCentre  = occCentre;
                CamVec     = camVec;
                CamDist    = camDist;
                OccRadius2 = occRadius2;
                CamPos     = camPos;
                Pixels     = pixels;
                LodRatio   = lodRatio;
                WorldSpan  = worldSpan;
            }
        }

        /// <summary>The target level: the previous one while <c>L - m &lt;= continuous &lt; L + 1 + m</c>, else the
        /// floor of the camera zoom. A jump (two or more levels, or a look-at on a tile the last cover did not
        /// reach) clears the history first. An empty history reads as a jump.</summary>
        private int ChooseLevel(in CameraProperties cam, IProjection proj, double continuous)
        {
            int fresh = math.clamp(cam.IntegerZoom + _selectionZoomOffset, _minZoom, _maxZoom);
            int held  = _level;
            if (held < 0 || math.abs(fresh - held) >= 2 || !LookAtWasDrawn(in cam, proj, held))
            {
                ClearHistory();
                return fresh;
            }

            bool sticky = continuous >= held - _zoomLevelHysteresis && continuous < held + 1 + _zoomLevelHysteresis;
            return sticky ? held : fresh;
        }

        /// <summary>True iff the tile under the look-at at <paramref name="level"/> was in the last cover, was
        /// subdivided by it, or lies below a tile it emitted.</summary>
        private bool LookAtWasDrawn(in CameraProperties cam, IProjection proj, int level)
        {
            double latitude = proj.ClampValidLatitude(cam.LookAt.Latitude);
            if (math.abs(latitude) > WebMercator.MaxLatitude) return true; // no Mercator tile there, so never a jump
            double2 unit = WebMercatorTiling.UnitSquareFromLonLat(new GeoCoordinate
            {
                Latitude  = latitude,
                Longitude = cam.LookAt.Longitude,
            });
            int last = (1 << level) - 1;
            var tile = new TileId
            {
                Z = level,
                X = math.clamp((int)(unit.x * (1 << level)), 0, last),
                Y = math.clamp((int)(unit.y * (1 << level)), 0, last),
            };
            if (_prevEmitted.Contains(tile) || _prevRefined.Contains(tile)) return true;
            foreach (TileId above in TileAncestry.Ancestors(tile))
                if (_prevEmitted.Contains(above)) return true;

            return false;
        }

        private TileLodHistory HistoryOf(TileId t)
            => _prevEmitted.Contains(t) ? TileLodHistory.Stopped
             : _prevRefined.Contains(t) ? TileLodHistory.Refined
             : TileLodHistory.None;

        private void ClearHistory()
        {
            _prevEmitted.Clear();
            _prevRefined.Clear();
            _level = -1;
        }

        /// <summary>Stores <paramref name="cover"/> and its strict ancestors as the next call's history. The ancestor
        /// walk stops at the first ancestor already stored, so it is bounded by the tree depth per new branch.</summary>
        private void RememberCover(List<TileId> cover)
        {
            _prevEmitted.Clear();
            _prevRefined.Clear();
            for (int i = 0; i < cover.Count; i++)
            {
                TileId t = cover[i];
                _prevEmitted.Add(t);
                foreach (TileId above in TileAncestry.Ancestors(t))
                    if (!_prevRefined.Add(above)) break; // its own ancestors are already in
            }
        }

        private void PushChildren(TileId t)
        {
            for (int child = 0; child < TileAncestry.ChildCount; child++)
                _stack.Add(TileAncestry.Child(t, child));
        }

        /// <summary>True iff tile <paramref name="t"/> meets the frustum (and, on a globe, is not entirely
        /// behind the horizon). Outputs <paramref name="nearDist"/> — the NEAREST render-space distance from the
        /// camera to the tile's bound. Non-obvious why: the distance uses the nearest point, because a coarse
        /// tile grazing the frustum has a far centre but a near edge, so the traversal subdivides it instead of
        /// loading it coarse. A globe tile uses a bounding sphere while descending (so a curved tile is not
        /// wrongly pruned) and the tight corner AABB at the leaf; a flat tile always uses the AABB.</summary>
        /// <param name="onScreenPx">The tile's true projected on-screen size, in pixels.</param>
        private static bool TileVisible(in TraversalFrame frame, TileId t, bool leaf,
                                        out double nearDist, out double onScreenPx)
        {
            double3 c  = RenderPoint(frame.Projection, frame.Origin, frame.Basis, t, 0.5, 0.5);
            double3 p0 = RenderPoint(frame.Projection, frame.Origin, frame.Basis, t, 0.0, 0.0);
            double3 p1 = RenderPoint(frame.Projection, frame.Origin, frame.Basis, t, 1.0, 0.0);
            double3 p2 = RenderPoint(frame.Projection, frame.Origin, frame.Basis, t, 0.0, 1.0);
            double3 p3 = RenderPoint(frame.Projection, frame.Origin, frame.Basis, t, 1.0, 1.0);

            onScreenPx = OnScreenSize(in frame.Pixels, c, p0, p1, p2, p3);

            if (frame.Occluded)
            {
                double rad = Dist(c, p0);
                double d1 = Dist(c, p1); if (d1 > rad) rad = d1;
                double d2 = Dist(c, p2); if (d2 > rad) rad = d2;
                double d3 = Dist(c, p3); if (d3 > rad) rad = d3;

                // Occlusion: entirely behind the limb iff the near-most point of the bounding sphere still fails.
                double centreDot = (c.x - frame.OccCentre.x) * frame.CamVec.x
                                 + (c.y - frame.OccCentre.y) * frame.CamVec.y
                                 + (c.z - frame.OccCentre.z) * frame.CamVec.z;
                if (centreDot + rad * frame.CamDist < frame.OccRadius2) { nearDist = 0.0; return false; }

                if (!leaf)
                {
                    double dCentre = Dist(frame.CamPos, c);
                    nearDist = dCentre > rad ? dCentre - rad : 0.0; // nearest point of the bounding sphere
                    return frame.Frustum.IntersectsSphere(c, rad);  // descent: conservative sphere
                }
                // leaf: tight AABB below.
            }

            // Corner AABB (thin vertical slab so a flat y≈0 quad isn't degenerate).
            double minX = c.x;
            double minY = c.y;
            double minZ = c.z;
            double maxX = c.x;
            double maxY = c.y;
            double maxZ = c.z;
            Grow(p0, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            Grow(p1, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            Grow(p2, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            Grow(p3, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            double aMinY = minY - 2.0;
            double aMaxY = maxY + 2.0;

            // Nearest distance from the camera to the AABB (0 on an axis the camera is already within).
            double3 camPos = frame.CamPos;
            double nx = camPos.x < minX ? minX - camPos.x : (camPos.x > maxX ? camPos.x - maxX : 0.0);
            double ny = camPos.y < aMinY ? aMinY - camPos.y : (camPos.y > aMaxY ? camPos.y - aMaxY : 0.0);
            double nz = camPos.z < minZ ? minZ - camPos.z : (camPos.z > maxZ ? camPos.z - maxZ : 0.0);
            nearDist = math.sqrt(nx * nx + ny * ny + nz * nz);
            return frame.Frustum.IntersectsAabb(new double3(minX, aMinY, minZ),
                                                new double3(maxX, aMaxY, maxZ));
        }

        private static double3 RenderPoint(IProjection proj, double3 origin, float3x3 basis,
                                           TileId t, double px, double py)
        {
            double2 ll = t.ToLonLat(px, py, 1.0);
            double3 w  = proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
            double rx = w.x - origin.x;
            double ry = w.y - origin.y;
            double rz = w.z - origin.z;
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
            double dx = a.x - b.x;
            double dy = a.y - b.y;
            double dz = a.z - b.z;
            return math.sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>The tile's projected on-screen size: the square root of the screen-space area of the
        /// centre fan <c>c,p0,p1,p3,p2</c> (boundary order), summing each triangle's area <b>after</b> taking
        /// its absolute value — a signed sum lets a folded (near-clamped) fan cancel and under-report.</summary>
        private static double OnScreenSize(in PixelBasis camera, double3 c, double3 p0, double3 p1, double3 p2, double3 p3)
        {
            double2 pxC  = camera.ToPixels(c);
            double2 pxP0 = camera.ToPixels(p0);
            double2 pxP1 = camera.ToPixels(p1);
            double2 pxP2 = camera.ToPixels(p2);
            double2 pxP3 = camera.ToPixels(p3);

            double area = TriangleArea(pxC, pxP0, pxP1) + TriangleArea(pxC, pxP1, pxP3)
                        + TriangleArea(pxC, pxP3, pxP2) + TriangleArea(pxC, pxP2, pxP0);
            return math.sqrt(area);
        }

        private static double TriangleArea(double2 a, double2 b, double2 c)
            => 0.5 * math.abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        /// <summary>The camera's pinhole pixel basis: forward/right/up axes plus the pixel scale, for turning a
        /// render-space point into screen pixel coordinates (origin at frame centre). Depth is clamped to the
        /// near plane so a point straddling the camera doesn't sign-flip into a near-zero or negative depth.</summary>
        private readonly struct PixelBasis
        {
            private readonly double3 _pos;
            private readonly double3 _forward;
            private readonly double3 _right;
            private readonly double3 _up;
            private readonly double _scale;
            private readonly double _near;

            public PixelBasis(double3 pos, double3 forward, double3 up, double scale, double near)
            {
                _pos     = pos;
                _forward = math.normalize(forward);
                _right   = math.normalize(math.cross(_forward, up));
                _up      = math.cross(_right, _forward);
                _scale   = scale;
                _near    = near;
            }

            public double2 ToPixels(double3 point)
            {
                double3 toPoint = point - _pos;
                double  depth   = math.dot(toPoint, _forward);
                if (depth < _near) depth = _near;
                return new double2(_scale * math.dot(toPoint, _right) / depth,
                                   _scale * math.dot(toPoint, _up)    / depth);
            }
        }
    }
}
