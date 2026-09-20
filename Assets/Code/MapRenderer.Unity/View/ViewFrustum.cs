// Engine-free: Unity.Mathematics only (no UnityEngine, no System.Math). Compiled by both the Unity EditMode
// runner and the fast dotnet core-tests project.

using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// A 6-plane perspective view frustum in render space, built from the SAME pose math the renderer uses
    /// (<see cref="CameraPoseMath"/>). Used by the tilt-aware visible-tile selector to test a tile's ground
    /// quad against the actual pitched/rotated frustum — which the old overhead corner-unprojection could not
    /// (it ignored tilt entirely, under-covering the far field toward the horizon).
    ///
    /// <para><b>Plane convention:</b> each plane is <c>(n, d)</c> with a unit <b>inward</b> normal <c>n</c>;
    /// a point <c>x</c> is inside the half-space iff <c>dot(n, x) + d ≥ 0</c>. An AABB is <i>outside</i> the
    /// frustum iff it lies entirely behind any one plane — the standard conservative frustum-vs-AABB test,
    /// which (unlike a corner-in-frustum test) is correct when a coarse tile <i>contains</i> the frustum.</para>
    ///
    /// <para><b>Handedness-safe:</b> the four side-plane normals are built as rotations of the forward axis in
    /// the forward-up and forward-right planes (no reliance on the sign of <c>cross(f, up)</c> — left/right are
    /// symmetric, so the plane SET is identical either way).</para>
    /// </summary>
    public readonly struct ViewFrustum
    {
        // Six planes: inside ⟺ dot(n_i, x) + d_i ≥ 0. Normals point inward, unit length.
        private readonly double3 _n0, _n1, _n2, _n3, _n4, _n5;
        private readonly double  _d0, _d1, _d2, _d3, _d4, _d5;

        // The frustum's own axis-aligned bounds (min/max over its 8 corners). Used as a reverse separating-axis
        // pre-cull: a box beyond the frustum's spatial extent is rejected even when it passes all 6 planes (the
        // classic conservative false positive — a big box diagonally past a corner isn't fully behind any one
        // plane). Both bounds are supersets, so the pre-cull never drops a genuinely-visible tile.
        private readonly double3 _amin, _amax;

        private ViewFrustum(
            double3 n0, double d0, double3 n1, double d1, double3 n2, double d2,
            double3 n3, double d3, double3 n4, double d4, double3 n5, double d5,
            double3 amin, double3 amax)
        {
            _n0 = n0; _d0 = d0; _n1 = n1; _d1 = d1; _n2 = n2; _d2 = d2;
            _n3 = n3; _d3 = d3; _n4 = n4; _d4 = d4; _n5 = n5; _d5 = d5;
            _amin = amin; _amax = amax;
        }

        /// <summary>
        /// Builds the frustum from a render-space camera pose (look-at at the origin, as
        /// <see cref="CameraPoseMath.ComputeRelativePose"/> emits) plus the perspective parameters. <paramref name="fwd"/>
        /// points toward the look-at, <paramref name="up"/> is the camera up. FOV is the <b>vertical</b> field of
        /// view in degrees; horizontal is derived from <paramref name="aspect"/> (width/height), matching a
        /// Unity camera with a vertical FOV axis.
        /// </summary>
        public static ViewFrustum FromPose(double3 pos, double3 fwd, double3 up,
                                           double fovDegVertical, double aspect, double near, double far)
        {
            double3 f = math.normalize(fwd);
            double3 r = math.normalize(math.cross(f, up)); // right (sign irrelevant — see class doc)
            double3 u = math.cross(r, f);                  // true up (already unit)

            double halfV = Angle.FromDegrees(fovDegVertical * 0.5).Radians;
            double sinV = math.sin(halfV), cosV = math.cos(halfV);
            double halfH = math.atan(math.tan(halfV) * aspect);
            double sinH = math.sin(halfH), cosH = math.cos(halfH);

            // Near/far: normals ±f, through the clip points along the view axis.
            double3 nNear = f;                     double3 pNear = pos + near * f;
            double3 nFar  = new double3(-f.x, -f.y, -f.z); double3 pFar = pos + far * f;

            // Side planes through the camera position; inward normals tilt from ±u/±r toward +f by the half-FOV.
            double3 nTop    = f * sinV - u * cosV; // top boundary tilts up ⇒ inward normal points down-inward
            double3 nBottom = f * sinV + u * cosV;
            double3 nRight  = f * sinH - r * cosH;
            double3 nLeft   = f * sinH + r * cosH;

            // The eight frustum corners (near/far × top/bottom × right/left) → the frustum's own AABB, for the
            // reverse pre-cull. tanH = tan(halfH) = tan(halfV)·aspect (halfH = atan(tan(halfV)·aspect)).
            double tanV = sinV / cosV, tanH = math.tan(halfH);
            double3 nc = pNear, fc = pFar;
            double nh = near * tanV, nw = near * tanH, fh = far * tanV, fw = far * tanH;
            double3 amin = new double3(double.MaxValue, double.MaxValue, double.MaxValue);
            double3 amax = new double3(double.MinValue, double.MinValue, double.MinValue);
            for (int sf = 0; sf < 2; sf++)          // near / far
            for (int sv = -1; sv <= 1; sv += 2)     // top / bottom
            for (int sh = -1; sh <= 1; sh += 2)     // right / left
            {
                double3 c = (sf == 0 ? nc : fc)
                          + (sf == 0 ? nh : fh) * sv * u
                          + (sf == 0 ? nw : fw) * sh * r;
                amin = math.min(amin, c);
                amax = math.max(amax, c);
            }

            return new ViewFrustum(
                nNear,   -math.dot(nNear,   pNear),
                nFar,    -math.dot(nFar,    pFar),
                nTop,    -math.dot(nTop,    pos),
                nBottom, -math.dot(nBottom, pos),
                nRight,  -math.dot(nRight,  pos),
                nLeft,   -math.dot(nLeft,   pos),
                amin, amax);
        }

        /// <summary>
        /// True if the axis-aligned box <c>[min, max]</c> intersects (or is contained by) the frustum.
        /// Conservative: returns <c>false</c> only when the box is entirely behind some plane (never a false
        /// negative), so it is safe for a tile-cover — no visible tile is dropped.
        /// </summary>
        public bool IntersectsAabb(double3 min, double3 max)
            // Reverse pre-cull first: if the box lies wholly outside the frustum's own AABB it can't intersect
            // the frustum, regardless of the plane tests — this is what rejects a box diagonally past a corner.
            => !(max.x < _amin.x || min.x > _amax.x
              || max.y < _amin.y || min.y > _amax.y
              || max.z < _amin.z || min.z > _amax.z)
            && !OutsideOfPlane(_n0, _d0, min, max)
            && !OutsideOfPlane(_n1, _d1, min, max)
            && !OutsideOfPlane(_n2, _d2, min, max)
            && !OutsideOfPlane(_n3, _d3, min, max)
            && !OutsideOfPlane(_n4, _d4, min, max)
            && !OutsideOfPlane(_n5, _d5, min, max);

        // The box is behind (outside) the plane iff its "positive vertex" — the corner farthest along n — is
        // still behind the plane.
        private static bool OutsideOfPlane(double3 n, double d, double3 min, double3 max)
        {
            double px = n.x >= 0.0 ? max.x : min.x;
            double py = n.y >= 0.0 ? max.y : min.y;
            double pz = n.z >= 0.0 ? max.z : min.z;
            return (n.x * px + n.y * py + n.z * pz) + d < 0.0;
        }

        /// <summary>
        /// True if the sphere <c>(centre, radius)</c> intersects (or is inside) the frustum. Conservative:
        /// <c>false</c> only when the centre is farther than <paramref name="radius"/> behind some plane, so it
        /// never drops a visible volume. Used by the globe cover, where a curved tile is bounded by a sphere —
        /// an AABB of the tile's corners misses the surface bulge toward the camera and wrongly culls coarse
        /// tiles that <i>contain</i> the visible region.
        /// </summary>
        public bool IntersectsSphere(double3 centre, double radius)
            // Reverse pre-cull: the sphere is outside if its centre is farther than radius beyond the frustum's
            // AABB on any axis (a superset test — never drops a sphere that truly meets the frustum).
            => !(centre.x + radius < _amin.x || centre.x - radius > _amax.x
              || centre.y + radius < _amin.y || centre.y - radius > _amax.y
              || centre.z + radius < _amin.z || centre.z - radius > _amax.z)
            && InFrontOf(_n0, _d0, centre, radius) && InFrontOf(_n1, _d1, centre, radius)
            && InFrontOf(_n2, _d2, centre, radius) && InFrontOf(_n3, _d3, centre, radius)
            && InFrontOf(_n4, _d4, centre, radius) && InFrontOf(_n5, _d5, centre, radius);

        private static bool InFrontOf(double3 n, double d, double3 c, double radius)
            => (n.x * c.x + n.y * c.y + n.z * c.z) + d >= -radius;
    }
}
