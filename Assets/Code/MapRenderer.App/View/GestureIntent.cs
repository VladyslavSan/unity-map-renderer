// Engine-free: no UnityEngine, Mouse, Keyboard, Touch or any device/input type.
// Unity.Mathematics + MapRenderer.Core.* only.

using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.App.View
{
    /// <summary>
    /// The kind of camera-interaction gesture the user wants to perform.
    /// </summary>
    public enum GestureKind
    {
        /// <summary>Zoom by a delta, pinning the ground point under a screen-space anchor.</summary>
        ZoomAtAnchor,

        /// <summary>Pan by placing a previously-grabbed ground point under a cursor position.</summary>
        PanToAnchor,

        /// <summary>Rotate bearing by a signed degree delta (heading-only; tilt unchanged).</summary>
        HeadingBy,

        /// <summary>Pitch the camera by a signed degree delta (tilt-only; heading unchanged).</summary>
        TiltBy,
    }

    /// <summary>
    /// A <b>device-agnostic</b>, allocation-free intent value that describes what the user wants the
    /// camera to do. Sources (mouse/keyboard, touch, test driver) translate device events into
    /// <see cref="GestureIntent"/> values and pass them to
    /// <see cref="ViewInput.Apply(in GestureIntent, in ViewContext)"/>; the mapping lives entirely in
    /// <see cref="ViewInput"/>, not in the source.
    ///
    /// <para><b>Encoding:</b> a discriminated-union-style <c>readonly struct</c> — one
    /// <see cref="Kind"/> discriminant plus the union of all payloads. Use the factory methods
    /// (<see cref="ZoomAt"/>, <see cref="Pan"/>, <see cref="HeadingBy"/>, <see cref="TiltBy"/>)
    /// so the correct fields are always set.</para>
    ///
    /// <para><b>Per-gesture config</b> (zoom clamps, <c>maxPitch</c>) rides on the payload so the
    /// source can hand algorithm knobs to the mapping without polluting the shared
    /// <see cref="ViewContext"/> carrier (which holds only the per-frame camera/viewport/projection).</para>
    ///
    /// <para><b>Sensitivity</b> (px→degrees, scroll→levels) is applied by the source <i>before</i>
    /// building the intent; the seam receives device-independent magnitudes, so different sources
    /// (mouse, touch) can use their own feel without changing the mapping.</para>
    ///
    /// <para>No <c>UnityEngine</c>, <c>Mouse</c>, <c>Keyboard</c>, <c>Touch</c>, <c>Touchscreen</c>,
    /// or <c>Vector2</c> reference appears anywhere in this type.</para>
    /// </summary>
    public readonly struct GestureIntent
    {
        // ── Discriminant ─────────────────────────────────────────────────────────────────────────

        /// <summary>Which kind of gesture this intent represents.</summary>
        public GestureKind Kind { get; init; }

        // ── ZoomAtAnchor payload ──────────────────────────────────────────────────────────────────

        /// <summary>Screen-space anchor pixel (ZoomAtAnchor). Viewport from <see cref="ViewContext.ViewportPx"/>.</summary>
        public double2 AnchorPx { get; init; }

        /// <summary>Zoom-level delta, already scaled by source sensitivity (ZoomAtAnchor).</summary>
        public double ZoomDelta { get; init; }

        /// <summary>Minimum zoom level (ZoomAtAnchor). From the source's serialized MinZoom.</summary>
        public double MinZoom { get; init; }

        /// <summary>Maximum zoom level (ZoomAtAnchor). From the source's serialized MaxZoom.</summary>
        public double MaxZoom { get; init; }

        // ── PanToAnchor payload ───────────────────────────────────────────────────────────────────

        /// <summary>The ground point captured at drag-start (PanToAnchor). Captured by the source.</summary>
        public GeoCoordinate3D GrabbedGround { get; init; }

        /// <summary>Current cursor pixel (PanToAnchor). Viewport from <see cref="ViewContext.ViewportPx"/>.</summary>
        public double2 CursorPx { get; init; }

        // ── HeadingBy payload ─────────────────────────────────────────────────────────────────────

        /// <summary>Bearing delta in degrees, already scaled by source sensitivity (HeadingBy).</summary>
        public double HeadingDeltaDeg { get; init; }

        // ── TiltBy payload ────────────────────────────────────────────────────────────────────────

        /// <summary>Pitch delta in degrees, already scaled by source sensitivity (TiltBy).</summary>
        public double TiltDeltaDeg { get; init; }

        /// <summary>
        /// Maximum pitch in degrees (TiltBy). Clamped range is [0, MaxPitch]. Supplied by the source
        /// from its serialized MaxPitch; kept on the payload so the mapping does not need to know it.
        /// </summary>
        public double MaxPitch { get; init; }

        // ── Factory methods ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a <see cref="GestureKind.ZoomAtAnchor"/> intent: zoom by <paramref name="zoomDelta"/>
        /// levels, keeping the ground point under <paramref name="anchorPx"/> pinned.
        /// </summary>
        public static GestureIntent ZoomAt(double2 anchorPx, double zoomDelta, double minZoom, double maxZoom)
            => new GestureIntent
            {
                Kind      = GestureKind.ZoomAtAnchor,
                AnchorPx  = anchorPx,
                ZoomDelta = zoomDelta,
                MinZoom   = minZoom,
                MaxZoom   = maxZoom,
            };

        /// <summary>
        /// Creates a <see cref="GestureKind.PanToAnchor"/> intent: place <paramref name="grabbedGround"/>
        /// under <paramref name="cursorPx"/>.
        /// </summary>
        public static GestureIntent Pan(GeoCoordinate3D grabbedGround, double2 cursorPx)
            => new GestureIntent
            {
                Kind          = GestureKind.PanToAnchor,
                GrabbedGround = grabbedGround,
                CursorPx      = cursorPx,
            };

        /// <summary>
        /// Creates a <see cref="GestureKind.HeadingBy"/> intent: rotate bearing by
        /// <paramref name="deltaDeg"/> degrees (heading-only; tilt is not touched).
        /// </summary>
        public static GestureIntent HeadingBy(double deltaDeg)
            => new GestureIntent
            {
                Kind            = GestureKind.HeadingBy,
                HeadingDeltaDeg = deltaDeg,
            };

        /// <summary>
        /// Creates a <see cref="GestureKind.TiltBy"/> intent: pitch by <paramref name="deltaDeg"/>
        /// degrees, clamped to [0, <paramref name="maxPitch"/>] (tilt-only; heading is not touched).
        /// </summary>
        public static GestureIntent TiltBy(double deltaDeg, double maxPitch)
            => new GestureIntent
            {
                Kind         = GestureKind.TiltBy,
                TiltDeltaDeg = deltaDeg,
                MaxPitch     = maxPitch,
            };
    }
}
