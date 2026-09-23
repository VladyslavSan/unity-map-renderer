// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references. The Unity layer reads Screen.dpi and passes it in as a plain double.

using System.Diagnostics;
using Unity.Mathematics;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Which pixel space a px-valued style property's CONSUMER measures against — the target of
    /// <see cref="DeviceScaling.LogicalToDevicePx"/>. A MapLibre style's <c>px</c> values are LOGICAL (CSS)
    /// pixels: a <c>line-width: 2</c> road is 2 logical px (<c>2 × dpr</c> physical px) on every panel —
    /// the member says what the consumer on the far side expects. <see cref="Logical"/>: the consumer
    /// already divides by the device-pixel ratio (symbol shaders' <c>_ScreenParamsLogical</c>, or CPU
    /// framing) — the IDENTITY, no conversion owed. <see cref="Device"/>: the consumer measures against
    /// the PHYSICAL framebuffer (line/fill shaders' <c>MapPixelsToWorld</c>, the SDF text shader's
    /// <c>fwidth</c> scale) — the logical value must be multiplied by the ratio.
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
    /// in. Both fall back to a ratio of 1 through the same private guard, so paint, camera framing,
    /// tile-cover framing and interaction seams cannot disagree about an unconfigured ratio.
    ///
    /// <para>The framing viewport is normalised to LOGICAL pixels (<c>logicalPx = physicalPx / dpr</c>) so an
    /// on-screen tile occupies a constant PHYSICAL size across panel densities — the "512 convention" tile is
    /// defined at <see cref="ReferenceDpi"/>. The device-pixel ratio is <c>actualDpi / ReferenceDpi</c>.</para>
    ///
    /// <para>Pure math over an explicit <c>double</c>: the Unity layer reads <c>Screen.dpi</c> and passes it
    /// here. A positive density is a precondition — tests and headless keep the serialized
    /// <c>DevicePixelRatio</c> and never call this.</para>
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
        /// <c>logicalPx = devicePx / dpr</c>. Every site taking a PHYSICAL measurement into the map's
        /// logical-pixel basis routes through it — the camera's framing viewport, the tile selector's
        /// framing viewport, and the mouse/touch interaction seams. Its input is a MEASUREMENT (a
        /// framebuffer size, a cursor coordinate), not a style value, so it takes no
        /// <see cref="PixelSpace"/> — a measurement has exactly one meaningful target (logical), while a
        /// style <c>px</c> value's target depends on which consumer reads it. Do not "unify" the two
        /// signatures. Division, never multiplication by a reciprocal: at a non-dyadic ratio <c>v * (1/d)</c>
        /// differs from <c>v / d</c> in the last bit for a third of all values.
        /// </summary>
        public static double DeviceToLogicalPx(double devicePx, double devicePixelRatio)
            => devicePx / SafeRatio(devicePixelRatio);

        /// <summary>Component-wise <see cref="DeviceToLogicalPx(double,double)"/> — the form the viewport and
        /// cursor/touch sites need (they all convert a <c>double2</c>).</summary>
        public static double2 DeviceToLogicalPx(double2 devicePx, double devicePixelRatio)
            => devicePx / SafeRatio(devicePixelRatio);

        /// <summary>
        /// Restate a RATE expressed per LOGICAL pixel as the same rate per DEVICE pixel:
        /// <c>perDevicePx = perLogicalPx / dpr</c>. The dash parameterisation's ruler
        /// (<c>CameraPoseMath.MetersPerPixel(zoom)</c>, metres per logical px) meets a <c>_Width</c> that
        /// reached the shader in DEVICE px, so the two must share a basis. NOT
        /// <see cref="DeviceToLogicalPx(double,double)"/>, whose input is a physical MEASUREMENT — this
        /// input (and output) is a per-pixel RATE; the arithmetic coincides, the meaning does not. Routed
        /// through <see cref="SafeRatio"/> because a raw <c>/ devicePixelRatio</c> sends the ruler to
        /// <c>+∞</c> at dpr 0 and propagates NaN into every dashed layer.
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

        /// <summary>The ratio actually applied: one outside the plausible band is unusable, so it degrades
        /// to 1. A non-positive or infinite ratio blanks or mirrors the paint and sends the camera framing
        /// to infinity; a merely implausible one (0.1, 100) rescales the whole map. This is the single home
        /// of that fallback for both directions
        /// (<c>DevicePixelRatioFramingTests.RatioFallback_HasExactlyOneHome_InDeviceScaling</c>). Non-local
        /// invariant: a FALLBACK, not a clamp — a clamp would invent a plausible-looking value (a map drawn
        /// at the floor still looks like a map, invisible to inspection), where the fallback yields the
        /// neutral default every test runs at. <c>NaN</c> and both infinities fail both comparisons and are
        /// rejected like any other implausible value — a DECISION, not an accident. The band
        /// (<see cref="MinPlausibleRatio"/>…<see cref="MaxPlausibleRatio"/>, inclusive) sits far from real
        /// hardware on both ends; see each bound's own doc for why.</summary>
        private static double SafeRatio(double devicePixelRatio)
            => devicePixelRatio >= MinPlausibleRatio && devicePixelRatio <= MaxPlausibleRatio
                ? devicePixelRatio
                : 1.0;
    }
}
