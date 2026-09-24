// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references. The Unity layer reads Screen.dpi and passes it in as a plain double.

using System.Diagnostics;
using Unity.Mathematics;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Which pixel space a px-valued style property's CONSUMER measures against, the target of
    /// <see cref="DeviceScaling.LogicalToDevicePx"/>. Style <c>px</c> values are LOGICAL (CSS) pixels.
    /// <see cref="Logical"/>: the consumer already divides by the ratio (symbol shaders'
    /// <c>_ScreenParamsLogical</c>, CPU framing). <see cref="Device"/>: the consumer measures the physical
    /// framebuffer (line/fill <c>MapPixelsToWorld</c>, the SDF text <c>fwidth</c> scale).
    /// </summary>
    public enum PixelSpace
    {
        /// <summary>The consumer already works in logical px — factor 1.</summary>
        Logical,

        /// <summary>The consumer measures against the physical framebuffer — factor <c>dpr</c>.</summary>
        Device,
    }

    /// <summary>
    /// Device-density ↔ logical-pixel scaling: <see cref="LogicalToDevicePx"/> takes a style's px value out
    /// to its consumer's space, <see cref="DeviceToLogicalPx(double,double)"/> takes a physical measurement
    /// in. Both fall back to a ratio of 1 through one private guard, so no two consumers disagree. The
    /// framing viewport is in LOGICAL pixels, so a tile keeps a constant physical size across densities
    /// (the "512 convention" tile is defined at <see cref="ReferenceDpi"/>).
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

        /// <summary>
        /// The ONE px→consumer-space conversion for the style's px-valued properties.
        /// <paramref name="logicalPx"/> is a logical (CSS) pixel value straight off the style;
        /// <paramref name="target"/> names which space the consumer measures in (see <see cref="PixelSpace"/>).
        /// <see cref="PixelSpace.Logical"/> returns <paramref name="logicalPx"/> unchanged;
        /// <see cref="PixelSpace.Device"/> multiplies by the ratio. Non-local invariant: a
        /// <paramref name="devicePixelRatio"/> outside the plausible band falls back to 1 via
        /// <see cref="SafeRatio"/> — the same fallback <see cref="DeviceToLogicalPx(double,double)"/> and the
        /// camera framing take, so an unconfigured ratio cannot scale the paint and the camera differently.
        /// </summary>
        public static double LogicalToDevicePx(double logicalPx, PixelSpace target, double devicePixelRatio)
        {
            if (target == PixelSpace.Logical) return logicalPx;
            return logicalPx * SafeRatio(devicePixelRatio);
        }

        /// <summary>
        /// The ONE device→logical conversion, the inverse of <see cref="LogicalToDevicePx"/>:
        /// <c>logicalPx = devicePx / dpr</c>, for every physical measurement (framing viewports, cursor/touch).
        /// A measurement has one target (logical), so it takes no <see cref="PixelSpace"/>. Non-obvious why:
        /// it divides, since at a non-dyadic ratio <c>v * (1/d)</c> differs from <c>v / d</c> in the last bit.
        /// </summary>
        public static double DeviceToLogicalPx(double devicePx, double devicePixelRatio)
            => devicePx / SafeRatio(devicePixelRatio);

        /// <summary>Component-wise <see cref="DeviceToLogicalPx(double,double)"/> — the form the viewport and
        /// cursor/touch sites need (they all convert a <c>double2</c>).</summary>
        public static double2 DeviceToLogicalPx(double2 devicePx, double devicePixelRatio)
            => devicePx / SafeRatio(devicePixelRatio);

        /// <summary>
        /// Restates a RATE per logical pixel as the same rate per device pixel:
        /// <c>perDevicePx = perLogicalPx / dpr</c>. The dash ruler (<c>CameraPoseMath.MetersPerPixel</c>)
        /// meets a <c>_Width</c> in device px, so the two must share a basis. The arithmetic matches
        /// <see cref="DeviceToLogicalPx(double,double)"/>, but the input is a rate, not a measurement.
        /// <see cref="SafeRatio"/> keeps dpr 0 from sending the ruler to <c>+∞</c>.
        /// </summary>
        public static double PerLogicalPxToPerDevicePx(double perLogicalPx, double devicePixelRatio)
            => perLogicalPx / SafeRatio(devicePixelRatio);

        /// <summary>The floor of the plausible band — 40 dpi. Far below anything that ships:
        /// Android's sparsest bucket (<c>ldpi</c>, 120 dpi) is 0.75 and a ~100-dpi desktop panel is 0.625.
        /// It is NOT 1, because sub-1 ratios are legitimate — a floor of 1 would silently rebase the map on
        /// every low-density device rather than reject a bad reading.</summary>
        private const double MinPlausibleRatio = 0.25;

        /// <summary>The ceiling of the plausible band — 1280 dpi. Shipping panels top out near 4 (Android's
        /// densest bucket, <c>xxxhdpi</c> at 640 dpi, is exactly 4.0), so this keeps a full doubling of
        /// headroom above real hardware.</summary>
        private const double MaxPlausibleRatio = 8.0;

        /// <summary>The ratio applied: one outside the inclusive band <see cref="MinPlausibleRatio"/> to
        /// <see cref="MaxPlausibleRatio"/> (NaN and infinities included) falls back to 1. This is the one
        /// home of that fallback
        /// (<c>DevicePixelRatioFramingTests.RatioFallback_HasExactlyOneHome_InDeviceScaling</c>).
        /// Non-obvious why: a clamp would draw a plausible-looking wrong map; the fallback gives the
        /// neutral default every test runs at.</summary>
        private static double SafeRatio(double devicePixelRatio)
            => devicePixelRatio >= MinPlausibleRatio && devicePixelRatio <= MaxPlausibleRatio
                ? devicePixelRatio
                : 1.0;
    }
}
