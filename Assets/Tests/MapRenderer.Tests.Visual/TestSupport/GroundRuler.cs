// Unity EditMode only — derives screen-space rulers by projecting world points through a LIVE Camera.
// NOT registered in Tools/core-tests/core-tests.csproj.

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The tilt fixture's measurement oracle. Pure — no render. Every quantity is obtained by projecting KNOWN
    /// world points through the LIVE <see cref="Camera"/>, never by re-deriving a projection matrix (applying
    /// <see cref="GroundRowSolver"/>'s principle forward).
    ///
    /// <para>Non-obvious why: this is an oracle, not the mechanism under test. The mechanism under test is how
    /// a styled or offset quantity becomes a WORLD displacement (the shader's extrusion, a future
    /// tangent-plane symbol offset); the projection itself is shared machinery, correct because the whole
    /// renderer works. This class computes
    /// the intended displacement on the CPU and projects it with the same matrix the render uses, so a
    /// discrepancy isolates the displacement, not the projection.</para>
    /// </summary>
    internal static class GroundRuler
    {
        /// <summary>Projects a world point to screen px via the live camera. Asserts the point projects in
        /// FRONT of the camera — a point behind the camera projects to a mirrored, meaningless screen
        /// position (<see cref="GroundRowSolver"/> documents exactly this hazard for its bisection bracket),
        /// and a silently mirrored oracle would make every ratio wrong in a plausible-looking way.</summary>
        public static double2 ProjectPx(Camera camera, double3 worldPoint)
        {
            var screen = camera.WorldToScreenPoint(
                new Vector3((float)worldPoint.x, (float)worldPoint.y, (float)worldPoint.z));
            Assert.That(screen.z, Is.GreaterThan(0f),
                $"GroundRuler.ProjectPx: world point {worldPoint} projects BEHIND the camera (z={screen.z:F1}) " +
                "— the screen position is mirrored and this oracle reading would be meaningless.");
            return new double2(screen.x, screen.y);
        }

        /// <summary>Screen distance, in px, between the projections of two world points.</summary>
        public static double ScreenSpanPx(Camera camera, double3 worldFrom, double3 worldTo)
            => math.length(ProjectPx(camera, worldTo) - ProjectPx(camera, worldFrom));

        /// <summary>The normalised screen-space direction a world line through <paramref name="worldFrom"/>
        /// and <paramref name="worldTo"/> projects to — the direction <see cref="PixelCoverage.CoverageProfileAlongRay"/>
        /// marches.</summary>
        public static double2 ScreenDirection(Camera camera, double3 worldFrom, double3 worldTo)
            => math.normalize(ProjectPx(camera, worldTo) - ProjectPx(camera, worldFrom));

        /// <summary>Screen span, in px, of a world segment of length <paramref name="lengthMetres"/> centred on
        /// <paramref name="centreWorld"/>, along ground direction <paramref name="groundDirXZ"/> (world XZ;
        /// need not be pre-normalised).
        ///
        /// <para>Non-obvious why: symmetric about the centre — a one-sided probe travels to a different depth
        /// and picks up its own foreshortening, the very defect these fixtures measure (see
        /// <c>LineProbeSymmetrySnapshotTests</c>). Do not simplify this to a one-sided probe.</para>
        /// </summary>
        public static double GroundSegmentSpanPx(
            Camera camera, double3 centreWorld, double2 groundDirXZ, double lengthMetres)
        {
            double2 dir   = math.normalize(groundDirXZ);
            double3 delta = new double3(dir.x, 0.0, dir.y) * (lengthMetres * 0.5);
            return ScreenSpanPx(camera, centreWorld - delta, centreWorld + delta);
        }

        /// <summary>Converts a STYLED px width to a world-metre width via the frame constant
        /// (<c>docs/line-rendering-design.md</c>'s conversion, given one site here). The tilt teeth measure in
        /// world metres and do not use it; it exists for the consumers whose styled sizes are in px.</summary>
        public static double StyledPixelsToWorldMetres(double styledPx, double metresPerDevicePixel)
            => styledPx * metresPerDevicePixel;

        /// <summary>The closed-form screen span, AT THE LOOK-AT ONLY, of a world segment of
        /// <paramref name="lengthMetres"/> along the tilt axis: <c>lengthMetres / mpp · cos(tilt)</c>. Used
        /// ONLY to cross-check the projective ruler (<see cref="GroundSegmentSpanPx"/>). Limitation: <c>mpp</c>
        /// is fixed at the look-at depth, so this form is wrong elsewhere by the depth ratio, and a look-at
        /// fixture cannot see it; use <see cref="ClosedFormPerpendicularSpanPx"/> away from the look-at.</summary>
        public static double ClosedFormAcrossAzimuthSpanPx(
            double lengthMetres, double metresPerDevicePixel, Angle tilt)
            => lengthMetres / metresPerDevicePixel * tilt.Cos;

        /// <summary>The VIEW depth of <paramref name="worldPoint"/> — its distance along the camera's forward
        /// axis, in world units. This is <c>WorldToScreenPoint(p).z</c>, which <see cref="ProjectPx"/> already
        /// computes for its in-front assert and then discards.
        ///
        /// <para>Non-local invariant: this is NOT NDC depth (<c>clip.z/clip.w</c>, what
        /// <c>SymbolProjectionJob</c> carries purely for sorting, is non-linear and near/far-dependent) —
        /// every perspective law in this family, including <see cref="ClosedFormPerpendicularSpanPx"/>, is
        /// stated in terms of THIS depth. Cross-checked against the view matrix
        /// (<c>-(worldToCameraMatrix · p).z</c>, negated because Unity's right-handed view space looks down
        /// −z) so a disagreement between the two ways Unity can be asked the same question fails loudly here
        /// rather than propagating into a ratio.</para></summary>
        public static double ViewDepthMetres(Camera camera, double3 worldPoint)
        {
            var p = new Vector3((float)worldPoint.x, (float)worldPoint.y, (float)worldPoint.z);
            var screen = camera.WorldToScreenPoint(p);
            Vector3 view = camera.worldToCameraMatrix.MultiplyPoint(p);
            Assert.That(screen.z, Is.GreaterThan(0f),
                $"GroundRuler.ViewDepthMetres: world point {worldPoint} is BEHIND the camera " +
                $"(WorldToScreenPoint.z={screen.z:F1}, -(worldToCamera·p).z={-view.z:F1}) — no depth-scaled " +
                "reading taken here would be meaningful.");
            Assert.That(screen.z, Is.EqualTo(-view.z).Within(0.5).Percent,
                $"GroundRuler.ViewDepthMetres: WorldToScreenPoint.z ({screen.z:R}) and the view matrix " +
                $"({-view.z:R}) disagree about the view depth of {worldPoint} — one of them is not the " +
                "quantity this oracle thinks it is.");
            return screen.z;
        }

        /// <summary>The closed-form screen span, AT ANY DEPTH, of a world displacement of
        /// <paramref name="lengthMetres"/> PERPENDICULAR to the camera's view axis, at view depth
        /// <paramref name="viewDepthMetres"/>: <c>L·|P11|·H / (2w)</c>. The depth-GENERAL sibling of
        /// <see cref="ClosedFormAcrossAzimuthSpanPx"/>, built from <c>projectionMatrix.m11</c>, pixel height
        /// and a measured depth. Limitation: exact only perpendicular to the view axis (one shared <c>w</c>),
        /// a small-span approximation otherwise; <c>|P11|·H == |P00|·W</c>, so it holds horizontally too.</summary>
        public static double ClosedFormPerpendicularSpanPx(
            double lengthMetres, double viewDepthMetres, double absP11, double viewportHeightPx)
            => lengthMetres * absP11 * viewportHeightPx / (2.0 * viewDepthMetres);
    }
}
