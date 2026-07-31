// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references. The Unity layer reads Screen.dpi and passes it in as a plain double.

using System.Diagnostics;
using Unity.Mathematics;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Which pixel space a px-valued style property's CONSUMER measures against — the target of
    /// <see cref="DeviceScaling.LogicalToDevicePx"/>.
    ///
    /// <para>A MapLibre style's <c>px</c> values are LOGICAL (CSS) pixels: a <c>line-width: 2</c> road is
    /// 2 logical px, i.e. <c>2 × dpr</c> physical px, on every panel. That is the basis every value
    /// entering the conversion is in; the member says what the consumer on the far side expects.</para>
    ///
    /// <para><see cref="Logical"/> — the consumer already divides by the device-pixel ratio (the symbol
    /// shaders' <c>_ScreenParamsLogical</c>, or the CPU's <c>WebMercator.TilePixelSize</c> / camera-altitude
    /// framing). No conversion is owed: <see cref="Logical"/> is the IDENTITY by construction.</para>
    ///
    /// <para><see cref="Device"/> — the consumer measures against the PHYSICAL framebuffer (the line/fill
    /// shaders' <c>MapPixelsToWorld</c>, which spans against <c>_ScreenParams</c>; the SDF text shader's
    /// <c>fwidth</c>-derived screen scale). The logical value must be multiplied by the ratio to land at the
    /// size the style asked for.</para>
    /// </summary>
    public enum PixelSpace
    {
        /// <summary>The consumer already works in logical px — factor 1.</summary>
        Logical,

        /// <summary>The consumer measures against the physical framebuffer — factor <c>dpr</c>.</summary>
        Device,
    }

    /// <summary>
    /// Device-density ↔ logical-pixel scaling: <see cref="LogicalToDevicePx"/> takes a style's px value out to
    /// the space its consumer measures in, <see cref="DeviceToLogicalPx(double,double)"/> takes a physical
    /// measurement in. Both fall back to a ratio of 1 through the same private guard, so the paint basis, the
    /// camera framing, the tile-cover framing and the interaction seams cannot disagree about an unconfigured
    /// ratio.
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

        /// <summary>
        /// The ONE px→consumer-space conversion for the style's px-valued properties (S107 Stage 1).
        /// <paramref name="logicalPx"/> is a logical (CSS) pixel value straight off the style;
        /// <paramref name="target"/> names which space the consumer measures in (see <see cref="PixelSpace"/>).
        ///
        /// <para><see cref="PixelSpace.Logical"/> returns <paramref name="logicalPx"/> unchanged — the
        /// identity, by construction. <see cref="PixelSpace.Device"/> multiplies by the ratio.</para>
        ///
        /// <para>A <paramref name="devicePixelRatio"/> outside the plausible band falls back to 1 via
        /// <see cref="SafeRatio"/> — the same fallback <see cref="DeviceToLogicalPx(double,double)"/> and the
        /// camera framing take, so an unconfigured ratio cannot scale the paint and the camera differently.
        /// Since S108 that is structural (one guard, one home), not a claim two call sites keep in step.</para>
        /// </summary>
        public static double LogicalToDevicePx(double logicalPx, PixelSpace target, double devicePixelRatio)
        {
            if (target == PixelSpace.Logical) return logicalPx;
            return logicalPx * SafeRatio(devicePixelRatio);
        }

        /// <summary>
        /// The ONE device→logical conversion (S108 Stage 3), the inverse direction of
        /// <see cref="LogicalToDevicePx"/>: <c>logicalPx = devicePx / dpr</c>. Every site that takes a
        /// PHYSICAL measurement into the map's logical-pixel basis routes through it — the camera's framing
        /// viewport, the tile selector's framing viewport, and the mouse/touch interaction seams.
        ///
        /// <para>Its input is a <b>measurement</b> — a framebuffer size, a cursor or finger coordinate — not a
        /// style value, which is why it takes no <see cref="PixelSpace"/>. A measurement in physical pixels has
        /// exactly one meaningful target space (logical), so the parameter would be a constant at every call
        /// site; a style <c>px</c> value is the asymmetric case, because its target depends on which consumer
        /// reads it (see <see cref="PixelSpace"/>). Do not "unify" the two signatures.</para>
        ///
        /// <para>Division, never multiplication by a reciprocal: at a non-dyadic ratio <c>v * (1/d)</c>
        /// differs from <c>v / d</c> in the last bit for a third of all values.</para>
        /// </summary>
        public static double DeviceToLogicalPx(double devicePx, double devicePixelRatio)
            => devicePx / SafeRatio(devicePixelRatio);

        /// <summary>Component-wise <see cref="DeviceToLogicalPx(double,double)"/> — the form the viewport and
        /// cursor/touch sites need (they all convert a <c>double2</c>).</summary>
        public static double2 DeviceToLogicalPx(double2 devicePx, double devicePixelRatio)
            => devicePx / SafeRatio(devicePixelRatio);

        /// <summary>The floor of the plausible band — 40 dpi. Deliberately far below anything that ships:
        /// Android's sparsest bucket (<c>ldpi</c>, 120 dpi) is 0.75 and a ~100-dpi desktop panel is 0.625.
        /// It is NOT 1, because sub-1 ratios are legitimate — a floor of 1 would silently rebase the map on
        /// every low-density device rather than reject a bad reading.</summary>
        private const double MinPlausibleRatio = 0.25;

        /// <summary>The ceiling of the plausible band — 1280 dpi. Shipping panels top out near 4 (Android's
        /// densest bucket, <c>xxxhdpi</c> at 640 dpi, is exactly 4.0), so this keeps a full doubling of
        /// headroom above real hardware.</summary>
        private const double MaxPlausibleRatio = 8.0;

        /// <summary>The ratio actually applied: one outside the plausible band is unusable, so it degrades to
        /// 1. How badly it fails varies with how far out it is — a non-positive or infinite ratio blanks or
        /// mirrors the paint and sends the camera framing to infinity, while a merely implausible one (0.1,
        /// 100) rescales the whole map by that factor. Neither is a value any display reports. The single home of that
        /// fallback for both directions — pinned by <c>DevicePixelRatioFramingTests.RatioFallback_HasExactlyOneHome_InDeviceScaling</c>,
        /// which sweeps production sources and requires this file to be the only match.
        ///
        /// <para>The band is <see cref="MinPlausibleRatio"/>…<see cref="MaxPlausibleRatio"/>, inclusive at
        /// both ends; each bound's own doc says why it sits where it does. Both are placed far from real
        /// hardware on purpose, so nothing a panel legitimately reports lands near a bound and anything that
        /// does is garbage.</para>
        ///
        /// <para>A fallback, deliberately NOT a clamp. A clamp invents a plausible-looking value — a map
        /// drawn at the floor still looks like a map, so the substitution is invisible to inspection — where
        /// the fallback yields the documented neutral default, which is the known baseline every test runs
        /// at. It also keeps ONE behaviour for "unusable" instead of splitting the guard's semantics at the
        /// bounds.</para>
        ///
        /// <para><c>NaN</c> and both infinities fail the band by DECISION, not by accident (S108 §6.1
        /// finding 6): they fail both comparisons, so a not-a-number ratio is simply "not plausible" like any
        /// other rejected value. That also closes a live hole — <c>+∞</c> satisfied the old positivity test
        /// and PROPAGATED, taking the paint to <c>+∞</c> and the logical viewport to 0.</para></summary>
        private static double SafeRatio(double devicePixelRatio)
            => devicePixelRatio >= MinPlausibleRatio && devicePixelRatio <= MaxPlausibleRatio
                ? devicePixelRatio
                : 1.0;
    }
}
