using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View.Camera;

namespace MapRenderer.App.View.Camera
{
    /// <summary>
    /// The pure, engine-free two-way reconcile between an Editor authoring surface's
    /// Zoom / Tilt / Heading sliders and the live Core <see cref="CameraProperties"/>.
    ///
    /// <para>Engine-free (no <c>UnityEngine</c>) so the whole binding runs headless in
    /// <c>Tools/core-tests</c> — the MonoBehaviour shell (<c>CameraControlPanel</c>) is a thin shuttle
    /// that owns no logic. <see cref="Reconcile"/> is the entire mechanism.</para>
    ///
    /// <para><b>The feedback guard:</b> per field, the rule compares the slider field to a
    /// <b>baseline</b> (the last value the panel synced) — <b>never</b> to the live camera. If the field
    /// differs from its baseline, the user dragged that slider ⇒ emit only that field into the patch. If
    /// the field equals its baseline, the user is idle ⇒ pull that field from the camera. Because the idle
    /// branch never emits a patch, a camera that moves on its own (scroll-zoom, <c>flyTo</c>) produces no
    /// patch and the sliders simply follow it — no fight, no oscillation. Comparing the field to the live
    /// camera instead would misread any non-panel camera motion as a user edit and revert it every frame.</para>
    ///
    /// <para><b>Per-field isolation:</b> only the dragged field is written; a concurrent camera change to a
    /// different field (scroll-zoom changing Zoom while the user drags Heading) is not stomped.</para>
    ///
    /// <para><b>Zoom is canonical end-to-end.</b> All three quantities are compared and stored in their
    /// native units (zoom level, degrees, degrees) — there is no metres↔zoom conversion and the binding
    /// needs no viewport/FOV framing input. Tilt/Heading round-trip through <see cref="ConstrainedAngle"/>
    /// so the slider snaps to the model's clamped/wrapped value; Zoom passes through (its range is bounded
    /// by the panel's slider, not by a model constraint).</para>
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
