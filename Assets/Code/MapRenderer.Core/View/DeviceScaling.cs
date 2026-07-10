// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references. The Unity layer reads Screen.dpi and passes it in as a plain double.

using System.Diagnostics;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Device-density → logical-pixel scaling for tile framing/selection.
    ///
    /// <para>The framing viewport is normalised to LOGICAL pixels (<c>logicalPx = physicalPx / dpr</c>) so an
    /// on-screen tile occupies a constant PHYSICAL size across panel densities — the "512 convention" tile is
    /// defined at <see cref="ReferenceDpi"/>; a denser panel yields <c>dpr &gt; 1</c> and the same physical
    /// size. The device-pixel ratio is <c>actualDpi / ReferenceDpi</c>.</para>
    ///
    /// <para>Pure math over an explicit <c>double</c>: the Unity layer reads <c>Screen.dpi</c> and passes it
    /// here. A positive density is a precondition (the caller only derives from a real panel; tests and
    /// headless keep the serialized <c>DevicePixelRatio</c> and never call this).</para>
    /// </summary>
    public static class DeviceScaling
    {
        /// <summary>The golden-standard display density (Android <c>mdpi</c> baseline) at which a selection
        /// tile's on-screen size is defined. <c>dpr = actualDpi / ReferenceDpi</c>, so a 320-dpi panel ⇒
        /// <c>dpr = 2</c>.</summary>
        public const double ReferenceDpi = 160.0;

        /// <summary>The device-pixel ratio for a real display density: <c>screenDpi / ReferenceDpi</c>.
        /// <paramref name="screenDpi"/> must be positive (a measured panel density) — the caller derives this
        /// only when the platform reports one.</summary>
        public static double DevicePixelRatioFromDpi(double screenDpi)
        {
            Debug.Assert(screenDpi > 0.0, "screenDpi must be a positive display density.");
            return screenDpi / ReferenceDpi;
        }
    }
}
