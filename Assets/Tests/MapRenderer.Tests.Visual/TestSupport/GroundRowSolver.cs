// Unity EditMode only — solves world-z from a screen row against a LIVE camera.
// NOT registered in Tools/core-tests/core-tests.csproj.

using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>Screen-row → world-z inversion for fixtures that measure a ground-plane ribbon on the
    /// world line <c>(0, 0, z)</c>. Shared by <see cref="LineDashSnapshotTests"/> and
    /// <see cref="LineProbeSymmetrySnapshotTests"/> so both invert with one implementation.</summary>
    internal static class GroundRowSolver
    {
        /// <summary>Solves, by bisection on the live camera, which point on the world line (0, 0, z) projects onto
        /// <paramref name="targetScreenY"/>. Bisection rather than a closed form because it must not duplicate
        /// Unity's live camera matrices and conventions — the projection is knowable, but a second implementation
        /// of it inside the test is the thing most likely to be wrong.
        ///
        /// <para>UNITS: <paramref name="targetScreenY"/> is a Unity SCREEN-Y, the space Camera.WorldToScreenPoint
        /// returns — NOT a pixel index. Both spaces have a bottom-left origin and grow upward, so there is no flip;
        /// they differ by exactly half a pixel, because pixel index j's CENTRE is at screen-y j + 0.5. Callers
        /// measuring on SnapshotRenderer.Pixels (row 0 = bottom scanline) must add that 0.5 themselves.
        /// Half a pixel is not a rounding detail here: at the T-S1 pose the two band edges sit at local scales of
        /// 763 and 347 metres per screen pixel, so a uniform half-row error fabricates a 1.7 % asymmetry — the same
        /// size as the effect S111 removes, and in the same direction.</para>
        ///
        /// <para>The bracket is a PAIR, not a symmetric ±: a target far from the look-at needs a wide bracket on one
        /// side while the other must stay in front of the camera. At the S111 pose the camera plane crosses the
        /// ground at z ≈ −165 501 m, and WorldToScreenPoint returns a mirrored, non-monotone y beyond it, which
        /// would void the bisection silently.</para></summary>
        internal static double SolveWorldZForRow(
            Camera camera, double targetScreenY, double loMetres, double hiMetres)
        {
            double RowAt(double z) => camera.WorldToScreenPoint(new Vector3(0f, 0f, (float)z)).y;

            double lo = loMetres, hi = hiMetres;
            Assert.That(RowAt(lo), Is.LessThan(targetScreenY), "probe bracket: target row below the low bound.");
            Assert.That(RowAt(hi), Is.GreaterThan(targetScreenY), "probe bracket: target row above the high bound.");
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (RowAt(mid) < targetScreenY) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }
    }
}
