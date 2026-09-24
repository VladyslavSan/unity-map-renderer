// Unity EditMode only — structural guard over MapController's source text: no legacy UnityEngine.Input,
// scroll normalized by WheelNotchUnits, Keyboard.current present, null-guarded.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapController = MapRenderer.App.Controller;

namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class MapControllerInputTests
    {
        private static string MapControllerSource
        {
            get
            {
                // Resolve path relative to the Unity project root (Application.dataPath ends at "Assets").
                string root   = Path.GetDirectoryName(Application.dataPath);
                // Move-proof: find the source by filename anywhere under Assets/Code (survives assembly moves).
                string[] hits = Directory.GetFiles(Path.Combine(root, "Assets", "Code"), "Controller.cs", SearchOption.AllDirectories);
                Assert.That(hits, Has.Length.EqualTo(1), "expected exactly one Controller.cs under Assets/Code");
                return File.ReadAllText(hits[0]);
            }
        }

        // ── Tooth 4a — no legacy UnityEngine.Input ────────────────────────────────────────────────

        /// <summary>
        /// MapController.cs must NOT use legacy UnityEngine.Input (the project uses the
        /// new Input System exclusively; activeInputHandler = 1 / new only).
        /// </summary>
        [Test]
        public void MapController_DoesNotUse_LegacyUnityEngineInput()
        {
            string src = MapControllerSource;
            bool hasLegacy = src.Contains("UnityEngine.Input.")
                          || src.Contains("Input.GetAxis")
                          || src.Contains("Input.GetButton")
                          || src.Contains("Input.GetKey")
                          || src.Contains("Input.mouseScrollDelta");

            Assert.IsFalse(hasLegacy,
                "MapController.cs must not use legacy UnityEngine.Input. " +
                "Use Mouse.current / Keyboard.current (new Input System) instead.");
        }

        // ── Tooth 4b — scroll normalized by WheelNotchUnits ──────────────────────────────────────

        /// <summary>
        /// Scroll must be divided by the named normalization constant WheelNotchUnits
        /// so that a wheel notch and a trackpad swipe are comparable. The constant must be greppable.
        /// </summary>
        [Test]
        public void MapController_HasNamedScrollNormalizationConstant()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("WheelNotchUnits"),
                "MapController.cs must define and use 'WheelNotchUnits' as the device-independent " +
                "scroll normalization constant (tooth 4).");
        }

        // ── Tooth 4c — Keyboard.current present and greppable ────────────────────────────────────

        /// <summary>
        /// Keyboard zoom (via Keyboard.current) must be present and greppable in
        /// MapController.cs so the feature cannot silently disappear.
        /// </summary>
        [Test]
        public void MapController_HasKeyboardCurrent()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("Keyboard.current"),
                "MapController.cs must contain 'Keyboard.current' (keyboard zoom, tooth 4).");
        }

        // ── Tooth 4d — Mouse.current null-guarded ────────────────────────────────────────────────

        /// <summary>
        /// Mouse.current must be null-guarded so headless/test builds don't NRE.
        /// The canonical guard pattern is assigning to a local and checking it.
        /// </summary>
        [Test]
        public void MapController_NullGuards_MouseCurrent()
        {
            string src = MapControllerSource;
            // The guard appears as: var mouse = Mouse.current; ... if (mouse != null)
            Assert.IsTrue(src.Contains("Mouse.current"),
                "MapController.cs must read Mouse.current (needed to null-guard it).");
            // A null check on the mouse variable must be present.
            Assert.IsTrue(src.Contains("mouse != null") || src.Contains("mouse == null"),
                "Mouse.current result must be null-checked before use (tooth 4 null-guard requirement).");
        }

        // ── Tooth 4e — Keyboard.current null-guarded separately ──────────────────────────────────

        /// <summary>
        /// Keyboard.current must be null-guarded separately from Mouse.current
        /// (each device can be absent independently in headless / test environments).
        /// </summary>
        [Test]
        public void MapController_NullGuards_KeyboardCurrent()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("kb != null") || src.Contains("kb == null"),
                "Keyboard.current result must be null-checked (tooth 4 null-guard requirement). " +
                "Pattern: var kb = Keyboard.current; if (kb != null) { ... }");
        }

        // ── T-ADAPTER teeth — desktop backend is a thin adapter over the seam ────────────────────

        /// <summary>
        /// T-ADAPTER: Controller.cs must route gestures through <c>ViewInput.Apply(</c> —
        /// the device-agnostic seam dispatch — rather than calling camera math directly.
        /// </summary>
        [Test]
        public void MapController_RoutesThrough_ViewInputApply()
        {
            Assert.IsTrue(MapControllerSource.Contains("ViewInput.Apply("),
                "Controller.cs must contain 'ViewInput.Apply(' — all gestures route through the " +
                "device-agnostic seam dispatch (T-ADAPTER).");
        }

        /// <summary>
        /// T-ADAPTER: the fused <c>ApplyTilt</c> call (that set both Heading and Tilt) must be
        /// gone from Controller.cs. The split HeadingBy / TiltBy intents replace it.
        /// </summary>
        [Test]
        public void MapController_NoFusedApplyTilt()
        {
            Assert.IsFalse(MapControllerSource.Contains("ApplyTilt"),
                "Controller.cs must NOT contain 'ApplyTilt' — tilt and heading are separate intents, not one fused helper. " +
                "Use GestureIntent.TiltBy + GestureIntent.HeadingBy via ViewInput.Apply instead.");
        }

        /// <summary>
        /// T-ADAPTER: shift key binding must be present and greppable — proves shift→tilt
        /// (MapLibre parity) cannot silently regress to fused right-drag.
        /// </summary>
        [Test]
        public void MapController_ShiftDrivesTilt()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("leftShiftKey"),
                "Controller.cs must reference 'leftShiftKey' (shift → TiltBy, T-ADAPTER).");
            Assert.IsTrue(src.Contains("TiltBy"),
                "Controller.cs must contain 'TiltBy' (the tilt intent, T-ADAPTER).");
        }

        /// <summary>
        /// T-ADAPTER: ctrl key binding must be present and greppable — proves ctrl→heading
        /// (MapLibre parity) cannot silently regress.
        /// </summary>
        [Test]
        public void MapController_CtrlDrivesHeading()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("leftCtrlKey"),
                "Controller.cs must reference 'leftCtrlKey' (ctrl → HeadingBy, T-ADAPTER).");
            Assert.IsTrue(src.Contains("HeadingBy"),
                "Controller.cs must contain 'HeadingBy' (the heading intent, T-ADAPTER).");
        }

        /// <summary>
        /// T-ADAPTER: Controller.cs must not re-implement camera math — no Mercator arithmetic,
        /// no manual degree wrapping, no Mathf.Clamp on angles. These live in Core / ConstrainedAngle,
        /// invoked via ViewInput.Apply, not duplicated in the binding.
        /// </summary>
        [Test]
        public void MapController_NoCameraMathInBinding()
        {
            string src = MapControllerSource;
            Assert.IsFalse(src.Contains("% 360"),
                "Controller.cs must not contain '% 360' — angle wrap lives in ConstrainedAngle (T-ADAPTER).");
            Assert.IsFalse(src.Contains("Mathf.Clamp"),
                "Controller.cs must not contain 'Mathf.Clamp' on tilt/heading — clamping lives in ConstrainedAngle (T-ADAPTER).");
            Assert.IsFalse(src.Contains("WebMercator."),
                "Controller.cs must not call WebMercator.* directly — projection math lives in Core (T-ADAPTER).");
        }

        // ── T3-5 — the mouse seam runs in LOGICAL px ─────────────────────────────────────────────

        /// <summary>
        /// The mouse twin of <c>TouchInputStackTests</c>' touch-seam test: <see cref="MapController"/> must
        /// convert BOTH the viewport AND the cursor through <c>DeviceScaling.DeviceToLogicalPx</c>, so gesture
        /// anchors and <c>ScreenToGround</c> share the render camera's logical basis. Limitation: it is
        /// structural because headless runs have dpr 1 and no synthetic mouse.
        /// </summary>
        [Test]
        public void MapController_ConvertsSeamThroughTheDeviceToLogicalConversion()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("DevicePixelRatio"),
                "Controller.cs must read Config.DevicePixelRatio to run the interaction seam in logical px " +
                "(else retina anchored pan and zoom-to-cursor drift off the pointer).");

            int conversions = DeviceScalingSeam.ConversionCallCount(src);
            Assert.GreaterOrEqual(conversions, 2,
                $"Controller.cs must convert BOTH the viewport AND the cursor — found {conversions} " +
                "DeviceToLogicalPx call(s), expected ≥2. Converting only one still drifts the anchor " +
                "(T3-5).");
        }
    }
}
