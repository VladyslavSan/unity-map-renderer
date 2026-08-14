// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the S72 two-way slider reconcile.

using NUnit.Framework;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class CameraSliderBindingTests
    {
        private static CameraProperties Cam(double zoom, double heading = 0.0, double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0 }, zoom, heading, tilt);

        // Idle fields/baseline that match the given camera exactly (same units everywhere now).
        private static SliderValues Idle(CameraProperties c)
            => new SliderValues { Zoom = c.Zoom, Tilt = c.Tilt.Degrees, Heading = c.Heading.Degrees };

        // ── B1 — oscillation stability under feedback ────────────────────────────────────────────────
        [Test]
        public void B1_NoOscillation_WhenCameraSteady()
        {
            CameraProperties camera = Cam(zoom: 8.0, heading: 30.0, tilt: 20.0);
            SliderValues fields   = Idle(camera);
            SliderValues baseline = Idle(camera);

            for (int i = 0; i < 100; i++)
            {
                ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in camera);

                Assert.IsFalse(r.HasPatch, $"iteration {i}: a steady camera must emit no patch");
                Assert.That(r.Display.Zoom,    Is.EqualTo(8.0).Within(1e-4),  $"iteration {i}: Zoom drifted");
                Assert.That(r.Display.Tilt,    Is.EqualTo(20.0).Within(1e-3), $"iteration {i}: Tilt drifted");
                Assert.That(r.Display.Heading, Is.EqualTo(30.0).Within(1e-3), $"iteration {i}: Heading drifted");

                // Feed the loop back (the live two-way binding).
                fields   = r.Display;
                baseline = r.Baseline;
            }
        }

        // ── B2 — THE decisive test: does NOT fight a self-moving camera ──────────────────────────────
        [Test]
        public void B2_DoesNotFightSelfMovingCamera()
        {
            CameraProperties before = Cam(zoom: 8.0, heading: 30.0, tilt: 20.0);
            SliderValues fields   = Idle(before);   // user idle
            SliderValues baseline = Idle(before);

            // The camera moves on its own (scroll-zoom + heading change), the panel having done nothing.
            CameraProperties after = Cam(zoom: 12.0, heading: 75.0, tilt: 20.0);

            ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in after);

            Assert.IsFalse(r.HasPatch, "a field-vs-camera guard would emit a stale revert-patch here");
            Assert.That(r.Display.Zoom,    Is.EqualTo(12.0).Within(1e-9), "Zoom must follow the camera");
            Assert.That(r.Display.Heading, Is.EqualTo(75.0).Within(1e-9), "Heading must follow the camera");
            Assert.That(r.Display.Tilt,    Is.EqualTo(20.0).Within(1e-9), "Tilt unchanged → follows the camera");
        }

        // ── B3 — slider→camera survives ConstrainedAngle Wrap exactly ────────────────────────────────
        [Test]
        public void B3_HeadingEdit_WrapsExactly()
        {
            CameraProperties camera = Cam(zoom: 5.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = Idle(camera);

            SliderValues over = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = 370.0 };
            ReconcileResult r1 = CameraSliderBinding.Reconcile(in over, in baseline, in camera);
            Assert.IsTrue(r1.Patch.Heading.HasValue);
            Assert.That(r1.Patch.Heading.Value, Is.EqualTo(370.0), "raw value is passed to the patch");
            Assert.That(r1.Display.Heading,     Is.EqualTo(10.0),  "370 wraps to 10 exactly");

            SliderValues under = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = -10.0 };
            ReconcileResult r2 = CameraSliderBinding.Reconcile(in under, in baseline, in camera);
            Assert.That(r2.Display.Heading, Is.EqualTo(350.0), "-10 wraps to 350 exactly");
        }

        // ── B4 — slider→camera survives ConstrainedAngle Clamp ───────────────────────────────────────
        [Test]
        public void B4_TiltEdit_ClampsExactly()
        {
            CameraProperties camera = Cam(zoom: 5.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = Idle(camera);

            SliderValues over = new SliderValues { Zoom = 5.0, Tilt = 95.0, Heading = 0.0 };
            ReconcileResult r1 = CameraSliderBinding.Reconcile(in over, in baseline, in camera);
            Assert.That(r1.Display.Tilt, Is.EqualTo(90.0), "95 clamps to 90");

            SliderValues under = new SliderValues { Zoom = 5.0, Tilt = -5.0, Heading = 0.0 };
            ReconcileResult r2 = CameraSliderBinding.Reconcile(in under, in baseline, in camera);
            Assert.That(r2.Display.Tilt, Is.EqualTo(0.0), "-5 clamps to 0");
        }

        // ── B5 — per-field isolation (no stomp) ──────────────────────────────────────────────────────
        [Test]
        public void B5_PerFieldIsolation_OnlyEditedFieldEmitted()
        {
            // Baseline says zoom 5; the live camera is at zoom 10 (a concurrent camera change). The Zoom
            // field still matches its baseline (idle), so Zoom must NOT be emitted despite the camera diff.
            CameraProperties camera = Cam(zoom: 10.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = 0.0 };
            SliderValues fields   = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = 90.0 };

            ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in camera);

            Assert.IsTrue(r.Patch.Heading.HasValue, "the edited Heading must be emitted");
            Assert.IsFalse(r.Patch.Zoom.HasValue, "the idle Zoom must NOT be emitted (no stomp)");
            Assert.IsFalse(r.Patch.Tilt.HasValue, "the idle Tilt must NOT be emitted");
        }

        // ── B6 — a Zoom edit is emitted as canonical zoom ────────────────────────────────────────────
        [Test]
        public void B6_ZoomEdit_EmittedAsCanonicalZoom()
        {
            CameraProperties camera = Cam(zoom: 8.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = Idle(camera);

            SliderValues fields = new SliderValues { Zoom = 11.5, Tilt = 0.0, Heading = 0.0 };
            ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in camera);

            Assert.IsTrue(r.Patch.Zoom.HasValue, "an edited Zoom must be emitted");
            Assert.That(r.Patch.Zoom.Value, Is.EqualTo(11.5).Within(1e-9), "Zoom is passed through as canonical zoom");
            Assert.That(r.Display.Zoom, Is.EqualTo(11.5).Within(1e-9));
        }
    }
}
