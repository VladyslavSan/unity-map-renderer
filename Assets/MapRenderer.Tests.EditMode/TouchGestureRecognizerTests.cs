// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference.
// Tests B-CLASSIFY / B-CLAMP / B-PANPIN / T-FAKESOURCE for the S74 touch gesture recognizer.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class TouchGestureRecognizerTests
    {
        // ── Shared test helpers ───────────────────────────────────────────────────────────────────

        private static readonly IProjection Proj = new WebMercatorProjection();
        private static readonly double2     Vp   = new double2(1920.0, 1080.0);
        private static readonly double2     Centre = new double2(960.0, 540.0);

        private static CameraProperties Cam(double lon = 0, double lat = 0, double zoom = 6,
                                            double heading = 0, double tilt = 0)
            => new CameraProperties(
                new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, heading, tilt);

        private static ViewContext MakeView(CameraProperties cam)
            => new ViewContext { Camera = cam, ViewportPx = Vp, Projection = Proj };

        // Config with explicit far-from-boundary thresholds (DpiScale=1 for headless).
        private static TouchGestureConfig Cfg(double maxPitch = 60)
            => new TouchGestureConfig
            {
                DpiScale                   = 1.0,
                ZoomSensitivity            = 1.0,
                BearingSensitivity         = 1.0,
                PitchSensitivity           = 1.0,
                MinZoom                    = 0.0,
                MaxZoom                    = 22.0,
                MaxPitch                   = maxPitch,
                PinchDistanceThresholdPx   = 8.0,
                TwistAngleThresholdDeg     = 4.0,
                TiltCentroidThresholdPx    = 8.0,
            };

        private static TouchSample Sample(int id, double x, double y, TouchPhase phase)
            => new TouchSample { FingerId = id, PositionPx = new double2(x, y), Phase = phase };

        // Run one frame through the recognizer and return the result list.
        private static List<GestureIntent> Step(TouchGestureRecognizer rec, ViewContext view,
                                                List<TouchSample> samples)
        {
            var results = new List<GestureIntent>();
            rec.Recognize(samples, in view, results);
            return results;
        }

        private static int CountKind(List<GestureIntent> list, GestureKind kind)
        {
            int n = 0;
            foreach (var gi in list)
                if (gi.Kind == kind) n++;
            return n;
        }

        // ── Pan baseline ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void Pan_SingleFinger_EmitsPanToAnchor_OnlyPan()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam());

            // Frame 1: Began at centre.
            var frame1 = Step(rec, view, new List<TouchSample> { Sample(0, Centre.x, Centre.y, TouchPhase.Began) });
            // Expect exactly one PanToAnchor (grabbed-ground captured).
            Assert.AreEqual(1, CountKind(frame1, GestureKind.PanToAnchor), "Began frame: one PanToAnchor");
            Assert.AreEqual(0, CountKind(frame1, GestureKind.ZoomAtAnchor), "Began: no Zoom");
            Assert.AreEqual(0, CountKind(frame1, GestureKind.HeadingBy),    "Began: no Heading");
            Assert.AreEqual(0, CountKind(frame1, GestureKind.TiltBy),       "Began: no Tilt");

            // Frame 2: Moved +50px right.
            var frame2 = Step(rec, view, new List<TouchSample>
            { Sample(0, Centre.x + 50, Centre.y, TouchPhase.Moved) });
            Assert.AreEqual(1, CountKind(frame2, GestureKind.PanToAnchor), "Moved frame: one PanToAnchor");
            Assert.AreEqual(0, CountKind(frame2, GestureKind.ZoomAtAnchor), "Moved: no Zoom");
            Assert.AreEqual(0, CountKind(frame2, GestureKind.HeadingBy),    "Moved: no Heading");
            Assert.AreEqual(0, CountKind(frame2, GestureKind.TiltBy),       "Moved: no Tilt");
        }

        // ── Row 4 — idle/sub-threshold two fingers → empty set ────────────────────────────────────

        [Test]
        public void Row4_TwoFingers_Idle_EmitsNothing()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam());

            // Seed frame (two fingers Began, no prior → empty).
            double2 f0 = new double2(400, 500);
            double2 f1 = new double2(600, 500);
            var seed = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });
            Assert.AreEqual(0, seed.Count, "Seed frame (no prior): empty");

            // Tiny jitter < threshold — still empty.
            var jitter = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x + 1, f0.y + 1, TouchPhase.Moved),
                Sample(1, f1.x - 1, f1.y - 1, TouchPhase.Moved),
            });
            Assert.AreEqual(0, jitter.Count, "Sub-threshold jitter: empty (Row 4)");

            // More sub-threshold noise.
            var noise = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x + 2, f0.y + 2, TouchPhase.Moved),
                Sample(1, f1.x - 2, f1.y - 2, TouchPhase.Moved),
            });
            Assert.AreEqual(0, noise.Count, "Further sub-threshold: empty (Row 4)");
        }

        // ── Row 1 — TILT family stays TILT even when pinch/twist appears ─────────────────────────

        [Test]
        public void Row1_TiltFamilyLatch_IgnoresSubsequentPinchTwist()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam());

            // Two fingers, starting position with large vertical centroid separation.
            double2 f0 = new double2(800, 400);
            double2 f1 = new double2(1000, 400); // horizontal spread (low pinch threat)

            // Seed frame.
            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });

            // Frame 2: big parallel vertical drag (centroid.y −40px) → should latch TILT.
            // Both fingers move UP by 40px (drag-up direction), distance stays roughly the same.
            var tiltFrame = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y + 40, TouchPhase.Moved),   // up 40px
                Sample(1, f1.x, f1.y + 40, TouchPhase.Moved),   // up 40px
            });
            Assert.AreEqual(1, CountKind(tiltFrame, GestureKind.TiltBy),
                "Frame 2: latches TILT, emits TiltBy");
            Assert.AreEqual(0, CountKind(tiltFrame, GestureKind.ZoomAtAnchor),
                "Frame 2: no ZoomAtAnchor");
            Assert.AreEqual(0, CountKind(tiltFrame, GestureKind.HeadingBy),
                "Frame 2: no HeadingBy");

            // Frames 3–5: inject strong pinch + twist while finger count stays 2.
            // Distance grows by 80px (well above pinchThreshold=8), angle rotates.
            // A stateless dominant-component classifier would flip to ZoomRotate here — the latch forbids it.
            for (int i = 0; i < 3; i++)
            {
                double spread = 80.0 + i * 20;
                var pinchFrame = Step(rec, view, new List<TouchSample>
                {
                    Sample(0, 800 - spread, 400 + 40, TouchPhase.Moved),
                    Sample(1, 1000 + spread + 30 * i, 400 + 40 + 15 * i, TouchPhase.Moved),
                });
                Assert.AreEqual(1, CountKind(pinchFrame, GestureKind.TiltBy),
                    $"Row 1 pinch-inject frame {i + 3}: MUST still emit TiltBy (family lock)");
                Assert.AreEqual(0, CountKind(pinchFrame, GestureKind.ZoomAtAnchor),
                    $"Row 1 pinch-inject frame {i + 3}: must NOT emit ZoomAtAnchor (Row 1)");
                Assert.AreEqual(0, CountKind(pinchFrame, GestureKind.HeadingBy),
                    $"Row 1 pinch-inject frame {i + 3}: must NOT emit HeadingBy (Row 1)");
            }
        }

        // ── Row 2 — ZOOM+ROTATE family never emits TiltBy ────────────────────────────────────────

        [Test]
        public void Row2_ZoomRotateFamilyLatch_NeverEmitsTiltBy()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam());

            double2 f0 = new double2(800, 540);
            double2 f1 = new double2(1000, 540);

            // Seed.
            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });

            // Frame 2: clear pinch (distance grows by 80px >> threshold 8) → latches ZoomRotate.
            var zoomFrame = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x - 40, f0.y, TouchPhase.Moved),
                Sample(1, f1.x + 40, f1.y, TouchPhase.Moved),
            });
            Assert.AreEqual(0, CountKind(zoomFrame, GestureKind.TiltBy),
                "Row 2: pinch frame must NOT emit TiltBy");
            Assert.Greater(CountKind(zoomFrame, GestureKind.ZoomAtAnchor) +
                           CountKind(zoomFrame, GestureKind.HeadingBy), 0,
                "Row 2: pinch frame must emit Zoom or Heading");

            // Frames 3–5: strong parallel vertical centroid drift — TiltBy must NEVER appear.
            double cx0 = f0.x - 40, cx1 = f1.x + 40;
            double cy = f0.y;
            for (int i = 0; i < 3; i++)
            {
                cy += 40.0; // centroid drifts upward 40px per frame
                var driftFrame = Step(rec, view, new List<TouchSample>
                {
                    Sample(0, cx0, cy, TouchPhase.Moved),
                    Sample(1, cx1, cy, TouchPhase.Moved),
                });
                Assert.AreEqual(0, CountKind(driftFrame, GestureKind.TiltBy),
                    $"Row 2 drift frame {i + 3}: TiltBy must NEVER appear (ZoomRotate lock)");
            }
        }

        // ── Row 3 — composition within ZOOM+ROTATE ────────────────────────────────────────────────

        [Test]
        public void Row3_ZoomRotate_SimultaneousSpreadAndArc_BothEmitted()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam());

            double2 f0 = new double2(800, 540);
            double2 f1 = new double2(1000, 540);

            // Seed.
            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });

            // Frame 2: spread (pinch) + arc (twist) simultaneously → latches ZoomRotate.
            // distance grows 50px, angle rotates ~15 deg.
            var composedFrame = Step(rec, view, new List<TouchSample>
            {
                Sample(0, 750, 490, TouchPhase.Moved),   // moved left and down
                Sample(1, 1060, 590, TouchPhase.Moved),  // moved right and up — also rotates
            });
            Assert.AreEqual(0, CountKind(composedFrame, GestureKind.TiltBy),
                "Row 3: no TiltBy in ZoomRotate composition");
            Assert.AreEqual(1, CountKind(composedFrame, GestureKind.ZoomAtAnchor),
                "Row 3: ZoomAtAnchor present (spread)");
            Assert.AreEqual(1, CountKind(composedFrame, GestureKind.HeadingBy),
                "Row 3: HeadingBy present (arc)");
        }

        // ── Row 5 — family resets when chain ends; re-latches fresh ──────────────────────────────

        [Test]
        public void Row5_FamilyResetsOnChainBreak_RelatchesFresh()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam());

            double2 f0 = new double2(800, 400);
            double2 f1 = new double2(1000, 400);

            // Establish TILT latch.
            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });
            // Frame 2: big parallel vertical drag → latch TILT.
            var tiltFrame = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y + 40, TouchPhase.Moved),
                Sample(1, f1.x, f1.y + 40, TouchPhase.Moved),
            });
            Assert.AreEqual(1, CountKind(tiltFrame, GestureKind.TiltBy), "Prerequisite: TILT latched");

            // One finger lifts → chain ends.
            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y + 40, TouchPhase.Ended),
                Sample(1, f1.x, f1.y + 40, TouchPhase.Moved),
            });

            // Now two fingers re-touch with a clear pinch → should latch ZoomRotate fresh.
            double2 nf0 = new double2(900, 540);
            double2 nf1 = new double2(1100, 540);

            // Seed new chain.
            Step(rec, view, new List<TouchSample>
            {
                Sample(2, nf0.x, nf0.y, TouchPhase.Began),
                Sample(3, nf1.x, nf1.y, TouchPhase.Began),
            });

            // Clear pinch: spread 80px >> threshold 8 → ZoomRotate.
            var newFrame = Step(rec, view, new List<TouchSample>
            {
                Sample(2, nf0.x - 40, nf0.y, TouchPhase.Moved),
                Sample(3, nf1.x + 40, nf1.y, TouchPhase.Moved),
            });
            Assert.AreEqual(0, CountKind(newFrame, GestureKind.TiltBy),
                "Row 5: new ZoomRotate chain must NOT emit TiltBy (latch reset)");
            Assert.Greater(CountKind(newFrame, GestureKind.ZoomAtAnchor), 0,
                "Row 5: new ZoomRotate chain emits ZoomAtAnchor");
        }

        // ── B-CLAMP — recognizer intents survive ConstrainedAngle clamp/wrap ─────────────────────

        [Test]
        public void BClamp_TiltBy_HugeDownwardDrag_ClampedTo60()
        {
            // Huge downward centroid drag (dc.y < 0) from tilt=0 → TiltBy huge positive →
            // ViewInput.Apply clamps to MaxPitch=60.
            var rec  = new TouchGestureRecognizer(Cfg(maxPitch: 60));
            var view = MakeView(Cam(tilt: 0));

            double2 f0 = new double2(800, 540);
            double2 f1 = new double2(1000, 540);

            // Seed.
            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });

            // Huge downward drag (−500px centroid) → latch Tilt, huge +tilt delta.
            var bigTilt = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y - 500, TouchPhase.Moved),  // DOWN (centroid.y decreases)
                Sample(1, f1.x, f1.y - 500, TouchPhase.Moved),
            });

            Assert.AreEqual(1, CountKind(bigTilt, GestureKind.TiltBy), "Should emit TiltBy");
            GestureIntent intent = bigTilt[0];
            var patch = ViewInput.Apply(intent, view);
            Assert.AreEqual(60.0, patch.Tilt.Value, 1e-9,
                "B-CLAMP: huge tilt delta clamps to MaxPitch=60.0");
            Assert.IsNull(patch.Heading, "B-CLAMP: TiltBy leaves Heading null");
        }

        [Test]
        public void BClamp_TiltBy_HugeUpwardDrag_FlooredAtZero()
        {
            // Huge upward drag from a tilted camera → TiltBy huge negative → clamped to 0.
            var rec  = new TouchGestureRecognizer(Cfg(maxPitch: 60));
            var view = MakeView(Cam(tilt: 30));

            double2 f0 = new double2(800, 540);
            double2 f1 = new double2(1000, 540);

            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });

            // Huge upward drag (+500px centroid) → latch Tilt, huge −tilt delta.
            var upTilt = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y + 500, TouchPhase.Moved),  // UP
                Sample(1, f1.x, f1.y + 500, TouchPhase.Moved),
            });

            Assert.AreEqual(1, CountKind(upTilt, GestureKind.TiltBy), "Should emit TiltBy");
            GestureIntent intent = upTilt[0];
            var patch = ViewInput.Apply(intent, view);
            Assert.AreEqual(0.0, patch.Tilt.Value, 1e-9,
                "B-CLAMP: upward drag from tilt=30 floors at 0.0");
            Assert.IsNull(patch.Heading, "B-CLAMP: TiltBy leaves Heading null");
        }

        [Test]
        public void BClamp_HeadingBy_WrapsForward_350Plus20Gives10()
        {
            // ZoomRotate: twist produces HeadingBy +20 from heading=350 → wraps to 10.
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam(heading: 350.0));

            double2 f0 = new double2(800, 540);
            double2 f1 = new double2(1000, 540);

            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });

            // Rotate f1 CCW so inter-finger angle increases by ~20 degrees.
            // Initial angle: atan2(0, 200)*180/PI = 0 degrees.
            // Target angle: 20 degrees — so f1 moves to (cos20*200, sin20*200) from f0.
            double rad20 = 20.0 * math.PI_DBL / 180.0;
            double nx    = f0.x + 200.0 * math.cos(rad20);
            double ny    = f0.y + 200.0 * math.sin(rad20);

            var twistFrame = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Moved),  // f0 stays
                Sample(1, nx, ny, TouchPhase.Moved),
            });

            // Should have latched ZoomRotate via twist (angle delta ~20 >> threshold 4).
            var headingIntents = new List<GestureIntent>();
            foreach (var gi in twistFrame)
                if (gi.Kind == GestureKind.HeadingBy) headingIntents.Add(gi);

            Assert.Greater(headingIntents.Count, 0, "B-CLAMP: twist should produce HeadingBy");
            Assert.AreEqual(0, CountKind(twistFrame, GestureKind.TiltBy),
                "B-CLAMP: ZoomRotate must not emit TiltBy");

            GestureIntent headingIntent = headingIntents[0];
            var patch = ViewInput.Apply(headingIntent, view);
            Assert.AreEqual(10.0, patch.Heading.Value, 1e-9,
                "B-CLAMP: 350 + 20 wraps to 10.0 (S68 Wrap)");
            Assert.IsNull(patch.Tilt, "B-CLAMP: HeadingBy leaves Tilt null");
        }

        [Test]
        public void BClamp_ZoomAtAnchor_LeavesHeadingTiltNull()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam(zoom: 5));

            double2 f0 = new double2(800, 540);
            double2 f1 = new double2(1000, 540);

            Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x, f0.y, TouchPhase.Began),
                Sample(1, f1.x, f1.y, TouchPhase.Began),
            });

            // Clear horizontal pinch.
            var pinchFrame = Step(rec, view, new List<TouchSample>
            {
                Sample(0, f0.x - 50, f0.y, TouchPhase.Moved),
                Sample(1, f1.x + 50, f1.y, TouchPhase.Moved),
            });

            Assert.Greater(CountKind(pinchFrame, GestureKind.ZoomAtAnchor), 0,
                "B-CLAMP: pinch emits ZoomAtAnchor");

            GestureIntent zoomIntent = default;
            foreach (var gi in pinchFrame)
                if (gi.Kind == GestureKind.ZoomAtAnchor) { zoomIntent = gi; break; }

            var patch = ViewInput.Apply(zoomIntent, view);
            Assert.IsNull(patch.Heading, "B-CLAMP: ZoomAtAnchor leaves Heading null");
            Assert.IsNull(patch.Tilt,    "B-CLAMP: ZoomAtAnchor leaves Tilt null");
            Assert.IsNotNull(patch.Zoom, "B-CLAMP: ZoomAtAnchor sets Zoom");
        }

        [Test]
        public void BClamp_PanToAnchor_LeavesZoomHeadingTiltNull()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var view = MakeView(Cam());

            // Single-finger pan.
            Step(rec, view, new List<TouchSample> { Sample(0, Centre.x, Centre.y, TouchPhase.Began) });
            var frame2 = Step(rec, view, new List<TouchSample>
            { Sample(0, Centre.x + 50, Centre.y, TouchPhase.Moved) });

            Assert.AreEqual(1, CountKind(frame2, GestureKind.PanToAnchor), "PanToAnchor present");
            GestureIntent panIntent = default;
            foreach (var gi in frame2)
                if (gi.Kind == GestureKind.PanToAnchor) { panIntent = gi; break; }

            var patch = ViewInput.Apply(panIntent, view);
            Assert.IsNull(patch.Zoom,    "B-CLAMP: PanToAnchor leaves Zoom null");
            Assert.IsNull(patch.Heading, "B-CLAMP: PanToAnchor leaves Heading null");
            Assert.IsNull(patch.Tilt,    "B-CLAMP: PanToAnchor leaves Tilt null");
        }

        // ── B-PANPIN — grabbed ground stays under finger within 0.5px ────────────────────────────

        [Test]
        public void BPanPin_GrabbedGroundStaysUnderFinger_Within0p5px()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var cam  = Cam(lon: 10, lat: 45, zoom: 6);
            var view = MakeView(cam);

            // Frame 1: Began at Centre — grabs ground under Centre.
            Step(rec, view, new List<TouchSample> { Sample(0, Centre.x, Centre.y, TouchPhase.Began) });

            // Frame 2: Moved to off-centre cursor.
            double2 cursor = Centre + new double2(80.0, 35.0);
            var frame2 = Step(rec, view, new List<TouchSample>
            { Sample(0, cursor.x, cursor.y, TouchPhase.Moved) });

            Assert.AreEqual(1, CountKind(frame2, GestureKind.PanToAnchor), "B-PANPIN: PanToAnchor present");

            GestureIntent panIntent = default;
            foreach (var gi in frame2)
                if (gi.Kind == GestureKind.PanToAnchor) { panIntent = gi; break; }

            // Apply and check grabbed ground appears under cursor within 0.5px.
            var patch   = ViewInput.Apply(panIntent, view);
            var newCam  = patch.ApplyTo(cam);
            double2 reprojected = Proj.GroundToScreen(panIntent.GrabbedGround, Vp, newCam);
            double2 diff        = reprojected - cursor;
            double  dist        = math.sqrt(diff.x * diff.x + diff.y * diff.y);
            Assert.LessOrEqual(dist, 0.5,
                "B-PANPIN: grabbed ground must stay under finger within 0.5px");
        }

        [Test]
        public void BPanPin_DragDown_ShiftsLookAtConsistentDirection()
        {
            var rec  = new TouchGestureRecognizer(Cfg());
            var cam  = Cam(lon: 0, lat: 0, zoom: 6);
            var view = MakeView(cam);

            Step(rec, view, new List<TouchSample> { Sample(0, Centre.x, Centre.y, TouchPhase.Began) });

            // Drag DOWN (y decreases — finger moves toward bottom of screen).
            var frame2 = Step(rec, view, new List<TouchSample>
            { Sample(0, Centre.x, Centre.y - 50, TouchPhase.Moved) });

            GestureIntent panIntent = default;
            foreach (var gi in frame2)
                if (gi.Kind == GestureKind.PanToAnchor) { panIntent = gi; break; }

            var patch = ViewInput.Apply(panIntent, view);
            // Drag down → content below scrolls up → center should move NORTH (lat increases).
            Assert.IsNotNull(patch.Latitude, "B-PANPIN: drag-down sets Latitude");
            Assert.Greater(patch.Latitude.Value, cam.LookAt.Latitude,
                "B-PANPIN: drag-down shifts look-at northward (consistent direction)");
        }

        // ── T-FAKESOURCE — recognizer + seam driven headless (zero device dependency) ─────────────

        [Test]
        public void FakeSource_TouchRecognizer_DrivesSeam_NoDeviceDependency()
        {
            // This entire test: hand-built TouchSample sequences → recognizer → GestureIntent →
            // ViewInput.Apply. Zero Unity/device imports in this file.

            var rec  = new TouchGestureRecognizer(Cfg());
            var cam  = Cam(lon: 0, lat: 0, zoom: 8, heading: 45, tilt: 20);
            var view = MakeView(cam);

            // Pan: one finger.
            var panResults = new List<GestureIntent>();
            rec.Recognize(new List<TouchSample> { Sample(0, Centre.x, Centre.y, TouchPhase.Began) },
                          in view, panResults);
            Assert.AreEqual(1, panResults.Count, "T-FAKESOURCE: Began emits one intent");
            rec.Recognize(new List<TouchSample> { Sample(0, Centre.x + 30, Centre.y, TouchPhase.Moved) },
                          in view, panResults);
            Assert.AreEqual(1, panResults.Count,             "T-FAKESOURCE: Moved emits one intent");
            Assert.AreEqual(GestureKind.PanToAnchor, panResults[0].Kind,
                            "T-FAKESOURCE: one-finger produces PanToAnchor");

            // Lift finger.
            rec.Recognize(new List<TouchSample> { Sample(0, Centre.x + 30, Centre.y, TouchPhase.Ended) },
                          in view, panResults);

            // Two fingers, tilt.
            var rec2  = new TouchGestureRecognizer(Cfg());
            var tiltR = new List<GestureIntent>();
            rec2.Recognize(new List<TouchSample>
            {
                Sample(0, 800, 540, TouchPhase.Began),
                Sample(1, 1000, 540, TouchPhase.Began),
            }, in view, tiltR);
            Assert.AreEqual(0, tiltR.Count, "T-FAKESOURCE: seed frame empty");

            rec2.Recognize(new List<TouchSample>
            {
                Sample(0, 800, 580, TouchPhase.Moved),   // both up 40px → tilt dominant
                Sample(1, 1000, 580, TouchPhase.Moved),
            }, in view, tiltR);
            Assert.AreEqual(1, CountKind(tiltR, GestureKind.TiltBy),
                "T-FAKESOURCE: parallel drag → TiltBy through seam");

            var tiltPatch = ViewInput.Apply(tiltR[0], view);
            Assert.IsNotNull(tiltPatch.Tilt,    "T-FAKESOURCE: TiltBy sets Tilt");
            Assert.IsNull   (tiltPatch.Heading, "T-FAKESOURCE: TiltBy leaves Heading null");
        }
    }
}
