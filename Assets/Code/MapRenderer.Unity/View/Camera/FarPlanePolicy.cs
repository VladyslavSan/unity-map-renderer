// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View.Camera
{
    /// <summary>
    /// Computes the camera far-clip distance (render metres) from the view. A policy, not a constant, because
    /// "how far to see" is a function of zoom (altitude), tilt, and FOV — and different maps want different
    /// answers (a fixed multiplier vs. a geometry-aware cap). The SAME instance is used by the render camera
    /// (<c>MapCamera.SyncToCamera</c>) and by the tile selector, so the frustum the selector covers is exactly
    /// the one that renders (a mismatch over/under-selects).
    /// </summary>
    public interface IFarPlanePolicy
    {
        /// <summary>Far-clip distance in render metres for a camera at <paramref name="altitude"/> looking with
        /// the given tilt/FOV/aspect.</summary>
        double FarMetres(double altitude, Angle tilt, double fovDegVertical, double aspect);

        /// <summary>Short stable id for logging / the Inspector.</summary>
        string Name { get; }
    }

    /// <summary>far = altitude · <c>Multiplier</c> — the classic fixed cap (e.g. ×4). Tilt/FOV-independent; the
    /// simplest policy, and the one the globe uses (it reaches the sphere's limb at low zoom).</summary>
    public sealed class MultiplierFarPlane : IFarPlanePolicy
    {
        public double Multiplier { get; }
        public MultiplierFarPlane(double multiplier = 4.0) { Multiplier = multiplier; }

        public double FarMetres(double altitude, Angle tilt, double fovDegVertical, double aspect)
            => altitude * Multiplier;

        public string Name => $"x{Multiplier:0.#}";
    }

    /// <summary>Geometry-aware planar far: the slant distance to the farthest viewport-corner ground ray — tight
    /// overhead, growing with pitch, clamped to <c>altitude · CapMultiplier</c> so it can't run to infinity at
    /// the horizon. Tight tile counts on the flat atlas; would clip a curved globe at low zoom, so it's the
    /// planar default.</summary>
    public sealed class GeometryAwareFarPlane : IFarPlanePolicy
    {
        public double CapMultiplier { get; }
        public GeometryAwareFarPlane(double capMultiplier = 4.0) { CapMultiplier = capMultiplier; }

        public double FarMetres(double altitude, Angle tilt, double fovDegVertical, double aspect)
            => CameraPoseMath.FarClip(altitude, tilt, fovDegVertical, aspect, CapMultiplier);

        public string Name => $"geometry≤x{CapMultiplier:0.#}";
    }

    /// <summary>Curvature-correct far for the GLOBE: casts the four frustum-corner rays at the render-space
    /// sphere (radius <c>Radius</c>, centred R below the look-at) and takes the farthest GROUND hit as a
    /// distance along the view axis. Tight at high zoom (the corners hit local ground ≈ overhead altitude),
    /// opening to the limb tangent at low zoom (the corners see past the horizon — no clipping the curved
    /// globe), and clamped to <c>altitude · CapMultiplier</c> so a horizon-grazing tilt can't run to infinity.
    /// Replaces the flat ×4 for the globe, which either wasted tiles (too far near overhead) or clipped the
    /// horizon (too near at low zoom). Heading-invariant, so it needs only tilt/FOV/altitude.</summary>
    public sealed class RaySphereFarPlane : IFarPlanePolicy
    {
        public double Radius { get; }
        public double CapMultiplier { get; }
        public RaySphereFarPlane(double radius, double capMultiplier = 8.0) { Radius = radius; CapMultiplier = capMultiplier; }

        public double FarMetres(double altitude, Angle tilt, double fovDegVertical, double aspect)
        {
            // Pose in the look-at render frame (heading irrelevant to the far magnitude — sphere/FOV symmetric).
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), tilt,
                out double3 pos, out double3 fwd, out double3 up);
            double3 f = math.normalize(fwd);
            double3 r = math.normalize(math.cross(f, up));
            double3 u = math.cross(r, f);
            double tanV = math.tan(Angle.FromDegrees(fovDegVertical * 0.5).Radians);
            double tanH = tanV * aspect;

            double3 centre = new double3(0.0, -Radius, 0.0);
            double3 oc = new double3(pos.x - centre.x, pos.y - centre.y, pos.z - centre.z);
            double cc = math.dot(oc, oc) - Radius * Radius;          // = dc² − R² ≥ 0 (camera outside)
            double limb = math.sqrt(cc > 0.0 ? cc : 0.0);            // slant tangent distance to the limb

            double far = 0.0;
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sx = -1; sx <= 1; sx += 2)
            {
                double3 d = math.normalize(new double3(
                    f.x + sy * tanV * u.x + sx * tanH * r.x,
                    f.y + sy * tanV * u.y + sx * tanH * r.y,
                    f.z + sy * tanV * u.z + sx * tanH * r.z));
                double b = math.dot(d, oc);
                double disc = b * b - cc;
                double fwdDist;
                if (disc >= 0.0)
                {
                    double t = -b - math.sqrt(disc);                 // near ground hit (t > 0 here)
                    fwdDist = t * math.dot(d, f);                    // its distance along the view axis
                }
                else fwdDist = limb;                                 // ray misses ⇒ sees past horizon ⇒ limb
                if (fwdDist > far) far = fwdDist;
            }

            far *= 1.05;                                             // margin so the far ground corner isn't clipped
            double cap = altitude * CapMultiplier;
            return far < cap ? far : cap;
        }

        public string Name => $"ray-sphere≤x{CapMultiplier:0.#}";
    }
}
