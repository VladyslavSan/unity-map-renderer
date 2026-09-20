// Unity EditMode only — derives screen-space rulers by projecting world points through a LIVE Camera.
// NOT registered in Tools/core-tests/core-tests.csproj.

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The tilt fixture's measurement oracle. Pure — no render. Every quantity here is obtained by
    /// projecting KNOWN world points through the LIVE <see cref="Camera"/>, never by re-deriving a
    /// projection matrix — <see cref="GroundRowSolver"/>'s stated principle ("a second implementation of the
    /// projection inside the test is the thing most likely to be wrong"), applied forward.
    ///
    /// <para>WHY THIS IS AN INDEPENDENT ORACLE AND NOT THE MECHANISM UNDER TEST. The mechanism under test is
    /// how a styled or offset quantity becomes a WORLD displacement (the shader's extrusion, a future
    /// tangent-plane symbol offset) — the projection itself is shared machinery, correct by the fact that the
    /// whole renderer works. This class computes the intended world displacement on the CPU and projects it;
    /// the render computes the displacement in the shader and projects it with the same matrix. A discrepancy
    /// therefore isolates the displacement, not the projection.</para>
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
        /// <paramref name="centreWorld"/>, running along ground direction <paramref name="groundDirXZ"/> (world
        /// XZ; need not be pre-normalised).
        ///
        /// <para>SYMMETRIC ABOUT THE CENTRE ON PURPOSE: a one-sided probe travels to a different depth and
        /// picks up its own foreshortening — that IS the S111 defect
        /// (<c>LineProbeSymmetrySnapshotTests.cs:5–10</c>). Do not "simplify" this to a one-sided probe.</para>
        /// </summary>
        public static double GroundSegmentSpanPx(
            Camera camera, double3 centreWorld, double2 groundDirXZ, double lengthMetres)
        {
            double2 dir   = math.normalize(groundDirXZ);
            double3 delta = new double3(dir.x, 0.0, dir.y) * (lengthMetres * 0.5);
            return ScreenSpanPx(camera, centreWorld - delta, centreWorld + delta);
        }

        /// <summary>Converts a STYLED px width to a world-metre width via the frame constant
        /// (<c>docs/line-rendering-design.md</c> §1's conversion, given one site here). Unused by T's own
        /// teeth (T2/T3 use world metres deliberately, per §3.2) — exists for the P3 symbol-offset consumer
        /// and the caps/joins stage, whose styled sizes are in px.</summary>
        public static double StyledPixelsToWorldMetres(double styledPx, double metresPerDevicePixel)
            => styledPx * metresPerDevicePixel;

        /// <summary>The closed-form screen span, AT THE LOOK-AT ONLY, of a world segment of
        /// <paramref name="lengthMetres"/> lying along the tilt axis: <c>lengthMetres / mpp · cos(tilt)</c> —
        /// §1.2's table row, written out once. Used ONLY by T1 to cross-check the projective ruler
        /// (<see cref="GroundSegmentSpanPx"/>) at the one point a closed form is known; every consumer uses
        /// the projective ruler, which is valid off-centre and at other depths where this closed form is
        /// not.
        ///
        /// <para><b>THE BLIND SPOT, NAMED.</b> <c>mpp</c> is a per-FRAME constant — the ruler at the look-at
        /// depth — so this form is silently wrong at any other depth, by exactly the depth ratio. Every
        /// fixture in the pitch-alignment epic anchored its symbol at the look-at, where that error is
        /// identically zero, and therefore could not observe the quantity the epic is about. The
        /// depth-GENERAL sibling is <see cref="ClosedFormPerpendicularSpanPx"/>; prefer it whenever the
        /// measurand is not at the look-at.</para></summary>
        public static double ClosedFormAcrossAzimuthSpanPx(
            double lengthMetres, double metresPerDevicePixel, Angle tilt)
            => lengthMetres / metresPerDevicePixel * tilt.Cos;

        /// <summary>The VIEW depth of <paramref name="worldPoint"/> — its distance along the camera's forward
        /// axis, in world units (metres here). This is <c>WorldToScreenPoint(p).z</c>, which
        /// <see cref="ProjectPx"/> already computes for its in-front assert and then throws away.
        ///
        /// <para><b>NOT NDC depth.</b> <c>clip.z/clip.w</c> (what <c>SymbolProjectionJob</c> carries, purely
        /// for sorting) is a non-linear, near/far-plane-dependent quantity and is NOT this. Every perspective
        /// law in this family — the <c>1/w</c> divide, <see cref="ClosedFormPerpendicularSpanPx"/> — is stated
        /// in terms of THIS depth.</para>
        ///
        /// <para>Cross-checked against the view matrix (<c>-(worldToCameraMatrix · p).z</c>, Unity's
        /// right-handed view space looks down −z) so a disagreement between the two ways Unity can be asked
        /// the same question fails loudly here rather than propagating into a ratio.</para></summary>
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
        /// <paramref name="lengthMetres"/> lying PERPENDICULAR to the camera's view axis, at view depth
        /// <paramref name="viewDepthMetres"/>: <c>L·|P11|·H / (2w)</c>.
        ///
        /// <para>The depth-GENERAL sibling of <see cref="ClosedFormAcrossAzimuthSpanPx"/> (which is
        /// look-at-only — see the blind-spot note there). Its terms are the raw projection
        /// (<c>camera.projectionMatrix.m11</c>, <c>camera.pixelHeight</c>) and a measured view depth; it
        /// carries no per-frame ruler, so it stays correct as a symbol recedes.</para>
        ///
        /// <para>VALIDITY: exact for a displacement perpendicular to the view axis (both endpoints then share
        /// one <c>w</c>, so the perspective divide is a single scale factor), and a small-span approximation
        /// otherwise. <c>|P11|·H</c> is the vertical form; it equals <c>|P00|·W</c> identically
        /// (<c>P00 = P11/aspect</c>, <c>aspect = W/H</c>), so it is also the correct factor for a HORIZONTAL
        /// perpendicular displacement at any aspect. Cross-checked against the live projection at two depths
        /// by the off-look-at fixture's oracle-acceptance tooth.</para></summary>
        public static double ClosedFormPerpendicularSpanPx(
            double lengthMetres, double viewDepthMetres, double absP11, double viewportHeightPx)
            => lengthMetres * absP11 * viewportHeightPx / (2.0 * viewDepthMetres);
    }
}
