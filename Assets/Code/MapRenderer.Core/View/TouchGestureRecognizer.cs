// Engine-free: no UnityEngine, no device/input types.
// Unity.Mathematics + MapRenderer.Core.* only. No System.Math.

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Injected configuration for <see cref="TouchGestureRecognizer"/>. All tunables live here
    /// (serialized on the Unity adapter) so tests can pin them and feel can be adjusted without
    /// touching recognition logic.
    /// </summary>
    public readonly struct TouchGestureConfig
    {
        /// <summary>log2(d/d0) multiplier → zoom-level delta.</summary>
        public double ZoomSensitivity { get; init; }

        /// <summary>Degrees of bearing change per degree of inter-finger-angle change.</summary>
        public double BearingSensitivity { get; init; }

        /// <summary>Degrees of tilt change per LOGICAL pixel of vertical centroid movement (S92 touch-DPI
        /// closure: positions are fed in logical px, so this is a density-independent physical rate).</summary>
        public double PitchSensitivity { get; init; }

        /// <summary>Minimum zoom level clamp (passed through to <see cref="GestureIntent.ZoomAt"/>).</summary>
        public double MinZoom { get; init; }

        /// <summary>Maximum zoom level clamp (passed through to <see cref="GestureIntent.ZoomAt"/>).</summary>
        public double MaxZoom { get; init; }

        /// <summary>Maximum tilt in degrees (passed through to <see cref="GestureIntent.TiltBy"/>).</summary>
        public double MaxPitch { get; init; }

        /// <summary>
        /// Minimum inter-finger distance change (LOGICAL px) to classify as a pinch. A constant physical
        /// size — positions are fed in logical px, so no per-density scaling is applied here.
        /// </summary>
        public double PinchDistanceThresholdPx { get; init; }

        /// <summary>
        /// Minimum inter-finger angle change (degrees) to classify as a twist.
        /// </summary>
        public double TwistAngleThresholdDeg { get; init; }

        /// <summary>
        /// Minimum vertical centroid displacement (LOGICAL px) to classify as a parallel drag (tilt).
        /// A constant physical size — positions are fed in logical px, so no per-density scaling here.
        /// </summary>
        public double TiltCentroidThresholdPx { get; init; }
    }

    /// <summary>
    /// S74: Stateful, engine-free gesture recognizer — the touch source's brain (S73 D2/D3).
    ///
    /// <para>Converts per-frame <see cref="TouchSample"/> lists into <see cref="GestureIntent"/>
    /// values via the S73 seam (<see cref="ViewInput.Apply"/>). Owns cross-frame disambiguation state
    /// (one-finger grabbed-ground latch, two-finger family lock). No <c>UnityEngine</c>,
    /// no <c>System.Math</c>, no LINQ, no per-call heap allocation in steady state.</para>
    ///
    /// <para><b>Family-lock rule (D3):</b> on the first decisive two-finger motion, one of two
    /// mutually-exclusive families is latched and held for the rest of the touch-event chain:
    /// <list type="bullet">
    ///   <item><b>TILT</b> — only <see cref="GestureKind.TiltBy"/> is emitted; zoom/heading never.</item>
    ///   <item><b>ZOOM+ROTATE</b> — <see cref="GestureKind.ZoomAtAnchor"/> and
    ///     <see cref="GestureKind.HeadingBy"/> may compose; <see cref="GestureKind.TiltBy"/> never.</item>
    /// </list>
    /// The latch resets only when the two-finger chain ends (finger count drops below 2).</para>
    ///
    /// <para><b>Purity:</b> this type and <see cref="TouchSample"/> contain zero
    /// <c>UnityEngine</c>, <c>Touchscreen</c>, <c>EnhancedTouch</c>, <c>InputSystem</c>, or
    /// <c>Vector2</c> references. All math is <c>Unity.Mathematics math.*</c>; no <c>System.Math</c>.</para>
    /// </summary>
    public sealed class TouchGestureRecognizer
    {
        // ── Configuration (injected; immutable after construction) ────────────────────────────────
        private readonly TouchGestureConfig _cfg;

        // ── One-finger pan state ──────────────────────────────────────────────────────────────────
        private GeoCoordinate3D _grabbedGround;
        private bool            _panActive;
        private int             _panFingerId;

        // ── Two-finger family-lock state ──────────────────────────────────────────────────────────
        private enum TwoFingerFamily { None, Tilt, ZoomRotate }

        private TwoFingerFamily _family;
        private bool            _twoFingerActive;
        private double          _prevDistance;
        private double          _prevAngleDeg;
        private double2         _prevCentroid;

        // ── Construction ──────────────────────────────────────────────────────────────────────────

        public TouchGestureRecognizer(in TouchGestureConfig cfg) => _cfg = cfg;

        // ── Per-frame entry point ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Classify <paramref name="samples"/> for this frame and append resolved intents to
        /// <paramref name="results"/> (cleared first). The caller feeds each intent through
        /// <see cref="ViewInput.Apply"/> and merges into one <see cref="CameraPropertiesUpdate"/>.
        /// Allocation-free in steady state (reuse-buffer convention).
        /// </summary>
        public void Recognize(
            List<TouchSample> samples,
            in ViewContext    view,
            List<GestureIntent> results)
        {
            results.Clear();

            // Count active (non-Ended) fingers this frame.
            int effectiveCount = 0;
            TouchSample f0 = default, f1 = default; // two lowest-id active fingers
            bool        haveF0 = false, haveF1 = false;

            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s.Phase == TouchPhase.Ended) continue;

                effectiveCount++;
                if (!haveF0) { f0 = s; haveF0 = true; }
                else if (!haveF1) { f1 = s; haveF1 = true; }
                // third+ fingers ignored
            }

            if (effectiveCount == 0)
            {
                // All fingers lifted — reset everything.
                _panActive       = false;
                _twoFingerActive = false;
                _family          = TwoFingerFamily.None;
                return;
            }

            if (effectiveCount == 1)
            {
                // ── One-finger pan ─────────────────────────────────────────────────────────────
                // Clear two-finger state whenever we drop to one finger.
                _twoFingerActive = false;
                _family          = TwoFingerFamily.None;

                TouchSample finger = haveF0 ? f0 : f1;

                if (!_panActive || _panFingerId != finger.FingerId)
                {
                    // Capture grabbed-ground on first frame of this drag (or new finger).
                    // Copy to a local so we can pass `in` (CS8156 prevents `in view.Camera`
                    // when `view` is itself an `in` parameter).
                    CameraProperties camSnap = view.Camera;
                    _grabbedGround = view.Projection.ScreenToGround(
                        finger.PositionPx, view.ViewportPx, in camSnap);
                    _panActive   = true;
                    _panFingerId = finger.FingerId;
                }

                results.Add(GestureIntent.Pan(_grabbedGround, finger.PositionPx));
                return;
            }

            // effectiveCount >= 2 — two-finger path.
            _panActive = false;

            // Pick two fingers with the lowest FingerId for stable ordering.
            // f0 and f1 are already ordered by the loop above (first two non-Ended samples).
            // Sort by FingerId.
            if (f0.FingerId > f1.FingerId)
            {
                var tmp = f0; f0 = f1; f1 = tmp;
            }

            double2 p0 = f0.PositionPx;
            double2 p1 = f1.PositionPx;

            double dx       = p1.x - p0.x;
            double dy       = p1.y - p0.y;
            double dist     = math.sqrt(dx * dx + dy * dy);
            double angleRad = math.atan2(dy, dx);
            double angleDeg = angleRad * (180.0 / math.PI_DBL);
            double2 centroid = new double2((p0.x + p1.x) * 0.5, (p0.y + p1.y) * 0.5);

            if (!_twoFingerActive)
            {
                // Seed frame: store initial values, emit nothing (no prior frame for deltas).
                _prevDistance    = dist;
                _prevAngleDeg    = angleDeg;
                _prevCentroid    = centroid;
                _twoFingerActive = true;
                _family          = TwoFingerFamily.None;
                return;
            }

            // Compute frame-to-frame deltas.
            double dd  = dist - _prevDistance;
            double dth = WrapPm180(angleDeg - _prevAngleDeg);   // (−180, 180]
            double2 dc = centroid - _prevCentroid;

            // Thresholds in LOGICAL px (S92 touch-DPI closure). The recognizer is fed logical positions
            // (TouchController divides the raw touch + viewport by DevicePixelRatio, matching the render and
            // the mouse seam), so a threshold is already a constant physical size — density normalization
            // happens ONCE, at the position basis, not again here.
            double pinchPx   = _cfg.PinchDistanceThresholdPx;
            double tiltPx    = _cfg.TiltCentroidThresholdPx;
            double twistDeg  = _cfg.TwistAngleThresholdDeg;

            // ── Family selection (once per chain) ─────────────────────────────────────────────
            if (_family == TwoFingerFamily.None)
            {
                bool pinchOrTwist = math.abs(dd) > pinchPx || math.abs(dth) > twistDeg;
                bool tiltDominant = math.abs(dc.y) > tiltPx
                                 && math.abs(dd) < pinchPx
                                 && math.abs(dth) < twistDeg;

                if (tiltDominant)
                {
                    _family = TwoFingerFamily.Tilt;
                }
                else if (pinchOrTwist)
                {
                    _family = TwoFingerFamily.ZoomRotate;
                }
                else
                {
                    // Sub-threshold — emit nothing; update prev and return.
                    UpdatePrev(dist, angleDeg, centroid);
                    return;
                }
            }

            // ── Emit by latched family (NO re-evaluation, NO flip) ────────────────────────────
            if (_family == TwoFingerFamily.Tilt)
            {
                // Emit TiltBy unconditionally every frame (even zero centroid shift) —
                // so B-CLASSIFY row 1 "keeps emitting ONLY TiltBy" holds on injected pinch frames.
                // Sign: -dc.y (drag-up → pitch decreases; drag-down → pitch increases) matches Controller.
                double tiltDeltaDeg = -dc.y * _cfg.PitchSensitivity;
                results.Add(GestureIntent.TiltBy(tiltDeltaDeg, _cfg.MaxPitch));
            }
            else // ZoomRotate
            {
                // ZoomAtAnchor: only when distance changed meaningfully (avoids log2(1)==0 noise).
                if (dd != 0.0 && _prevDistance > 0.0)
                {
                    double2 anchor    = centroid;
                    double  zoomDelta = math.log2(dist / _prevDistance) * _cfg.ZoomSensitivity;
                    results.Add(GestureIntent.ZoomAt(anchor, zoomDelta, _cfg.MinZoom, _cfg.MaxZoom));
                }

                // HeadingBy: only when angle changed.
                if (dth != 0.0)
                {
                    double headingDelta = dth * _cfg.BearingSensitivity;
                    results.Add(GestureIntent.HeadingBy(headingDelta));
                }
                // Note: TiltBy is NEVER emitted in ZoomRotate (family lock).
            }

            UpdatePrev(dist, angleDeg, centroid);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        private void UpdatePrev(double dist, double angleDeg, double2 centroid)
        {
            _prevDistance = dist;
            _prevAngleDeg = angleDeg;
            _prevCentroid = centroid;
        }

        /// <summary>
        /// Wraps a degree delta to (−180, 180] so inter-finger angle changes don't jump at ±180.
        /// Uses <c>math.floor</c> — no <c>System.Math</c>, no <c>% 360</c> on headings.
        /// </summary>
        private static double WrapPm180(double x)
            => x - 360.0 * math.floor((x + 180.0) / 360.0);
    }
}
