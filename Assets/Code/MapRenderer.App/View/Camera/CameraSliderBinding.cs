using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View.Camera;

namespace MapRenderer.App.View.Camera
{
    /// <summary>
    /// The pure, engine-free two-way reconcile between an Editor authoring surface's Zoom / Tilt / Heading
    /// sliders and the live Core <see cref="CameraProperties"/>. Non-obvious why: each field compares to a
    /// <b>baseline</b> (the last synced value), never to the live camera. A changed field is a user drag and
    /// emits only that field; an unchanged one follows the camera, so camera self-motion never reads as an edit.
    /// Tilt/Heading round-trip through <see cref="ConstrainedAngle"/>; Zoom passes through in zoom levels.
    /// </summary>
    public static class CameraSliderBinding
    {
        /// <summary>
        /// Idle-vs-edited threshold for Zoom, in zoom levels. Comfortably coarser than the
        /// <c>double→float</c> serialization granularity (~1e-6 at these magnitudes) and finer than any
        /// human drag.
        /// </summary>
        public const double ZoomEpsilon = 1e-4;

        /// <summary>
        /// Idle-vs-edited threshold for Tilt / Heading, in degrees. Above <c>float</c> granularity at
        /// <c>[0,360)</c> (~4e-5°) and below any human drag.
        /// </summary>
        public const double AngleEpsilonDeg = 1e-3;

        /// <summary>
        /// Reconciles the slider <paramref name="fields"/> against the <paramref name="baseline"/> and the
        /// live <paramref name="camera"/>, returning the patch to apply (if any) and the values to write
        /// back into the fields + baseline. Zoom in zoom levels, Tilt/Heading in degrees.
        /// </summary>
        /// <param name="fields">Current slider field values the Inspector shows.</param>
        /// <param name="baseline">Last-synced values. Use the <see cref="ReconcileResult.Baseline"/> from
        /// the previous call.</param>
        /// <param name="camera">The live camera — the single source of truth.</param>
        public static ReconcileResult Reconcile(
            in SliderValues     fields,
            in SliderValues     baseline,
            in CameraProperties camera)
        {
            CameraPropertiesUpdate patch = default;

            // ── Zoom — field-vs-baseline in zoom levels ───────────────────────────────────────────
            double displayZoom, baselineZoom;
            if (math.abs(fields.Zoom - baseline.Zoom) > ZoomEpsilon)
            {
                patch.Zoom  = fields.Zoom;   // canonical zoom — ApplyTo takes it directly
                displayZoom = fields.Zoom;
                baselineZoom = fields.Zoom;
            }
            else
            {
                displayZoom  = camera.Zoom;
                baselineZoom = camera.Zoom;
            }

            // ── Tilt — field-vs-baseline in degrees; constraint round-trips through ConstrainedAngle ──
            double displayTilt, baselineTilt;
            if (math.abs(fields.Tilt - baseline.Tilt) > AngleEpsilonDeg)
            {
                patch.Tilt   = fields.Tilt;                            // raw; ApplyTo re-applies the [0,90] Clamp
                displayTilt  = ConstrainedAngle.Tilt(fields.Tilt).Degrees; // snap the slider to the stored value
                baselineTilt = displayTilt;
            }
            else
            {
                displayTilt  = camera.Tilt.Degrees;
                baselineTilt = camera.Tilt.Degrees;
            }

            // ── Heading — field-vs-baseline in degrees; constraint round-trips through ConstrainedAngle ──
            double displayHeading, baselineHeading;
            if (math.abs(fields.Heading - baseline.Heading) > AngleEpsilonDeg)
            {
                patch.Heading   = fields.Heading;                               // raw; ApplyTo re-applies [0,360) Wrap
                displayHeading  = ConstrainedAngle.Heading(fields.Heading).Degrees; // snap (370 → 10, −10 → 350)
                baselineHeading = displayHeading;
            }
            else
            {
                displayHeading  = camera.Heading.Degrees;
                baselineHeading = camera.Heading.Degrees;
            }

            return new ReconcileResult
            {
                Patch    = patch,
                HasPatch = !patch.IsEmpty,
                Display  = new SliderValues
                {
                    Zoom = displayZoom, Tilt = displayTilt, Heading = displayHeading,
                },
                Baseline = new SliderValues
                {
                    Zoom = baselineZoom, Tilt = baselineTilt, Heading = baselineHeading,
                },
            };
        }
    }

    /// <summary>
    /// A camera authoring triple — zoom level, tilt degrees, heading degrees. Plain doubles,
    /// object-initializer construction. Same units everywhere: fields, baseline and display.
    /// </summary>
    public readonly struct SliderValues
    {
        /// <summary>Fractional zoom level (canonical).</summary>
        public double Zoom { get; init; }

        /// <summary>Tilt in degrees.</summary>
        public double Tilt { get; init; }

        /// <summary>Heading in degrees.</summary>
        public double Heading { get; init; }
    }

    /// <summary>
    /// The result of one <see cref="CameraSliderBinding.Reconcile"/> pass.
    /// </summary>
    public readonly struct ReconcileResult
    {
        /// <summary>The per-field patch to apply (only edited fields are non-null).</summary>
        public CameraPropertiesUpdate Patch { get; init; }

        /// <summary>True when <see cref="Patch"/> carries at least one field (apply only then).</summary>
        public bool HasPatch { get; init; }

        /// <summary>Values to write back into the serialized slider fields.</summary>
        public SliderValues Display { get; init; }

        /// <summary>Values to store as the next baseline.</summary>
        public SliderValues Baseline { get; init; }
    }
}
