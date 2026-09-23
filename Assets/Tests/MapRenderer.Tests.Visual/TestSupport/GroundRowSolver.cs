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
        /// <paramref name="targetScreenY"/>. Bisection avoids a second implementation of Unity's live camera
        /// matrices, the thing most likely to be wrong.
        ///
        /// <para>Non-obvious why: <paramref name="targetScreenY"/> is a Unity SCREEN-Y, not a pixel index
        /// (both spaces share a bottom-left origin, so there is no flip) — the two differ by half a pixel,
        /// since pixel index j's CENTRE sits at screen-y j + 0.5; a caller measuring on
        /// <c>SnapshotRenderer.Pixels</c> must add that 0.5. At the tilted pose this is not a rounding
        /// detail: the two band edges sit at 763 and 347 m/px, so a half-row error fabricates a 1.7%
        /// asymmetry, the same size as the foreshortening defect these fixtures measure.</para>
        ///
        /// <para>Limitation no test can observe: the bracket is a PAIR, not a symmetric ±, because a target
        /// far from the look-at needs a wide bracket on one side while the other must stay in front of the
        /// camera, and because <c>WorldToScreenPoint</c> returns a mirrored, non-monotone y once past the
        /// camera plane (z ≈ −165 501 m at the tilted pose), which would void the bisection silently if the
        /// bracket crossed it.</para></summary>
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
