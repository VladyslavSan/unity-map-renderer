// Unity EditMode only — structural guard over MapController's source text: no legacy UnityEngine.Input,
// scroll normalized by WheelNotchUnits, Keyboard.current present, null-guarded.

using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // ── Tooth 4a-4e, T-ADAPTER — required source idioms, one row per idiom ───────────────────

        /// <summary>
        /// Each row names a token (or a set of acceptable alternative tokens) that MapController.cs must
        /// contain — the new Input System device reads (tooth 4), their null guards (tooth 4d/4e), the
        /// scroll normalization constant, and the device-agnostic seam dispatch (T-ADAPTER).
        /// </summary>
        private static IEnumerable<TestCaseData> RequiredIdiomCases()
        {
            // A bare string[] argument would be unpacked by NUnit as the argument LIST (its single-array-
            // parameter gotcha); the (object) cast forces it to stay one argument.
            yield return new TestCaseData((object)new[] { "WheelNotchUnits" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(ScrollNormalizationConstant)");
            yield return new TestCaseData((object)new[] { "Keyboard.current" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(KeyboardCurrent)");
            yield return new TestCaseData((object)new[] { "ViewInput.Apply(" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(RoutesThroughViewInputApply)");
            yield return new TestCaseData((object)new[] { "Mouse.current" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(MouseCurrent)");
            yield return new TestCaseData((object)new[] { "mouse != null", "mouse == null" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(MouseCurrentNullGuard)");
            yield return new TestCaseData((object)new[] { "kb != null", "kb == null" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(KeyboardCurrentNullGuard)");
            // Shift→tilt (MapLibre parity) cannot silently regress to fused right-drag.
            yield return new TestCaseData((object)new[] { "leftShiftKey" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(ShiftKeyBinding)");
            yield return new TestCaseData((object)new[] { "TiltBy" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(TiltIntent)");
            // Ctrl→heading (MapLibre parity) cannot silently regress.
            yield return new TestCaseData((object)new[] { "leftCtrlKey" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(CtrlKeyBinding)");
            yield return new TestCaseData((object)new[] { "HeadingBy" })
                .SetName("MapControllerSource_ContainsRequiredIdiom(HeadingIntent)");
        }

        [Test]
        [TestCaseSource(nameof(RequiredIdiomCases))]
        public void MapControllerSource_ContainsRequiredIdiom(string[] acceptableTokens)
        {
            string src = MapControllerSource;
            Assert.IsTrue(acceptableTokens.Any(src.Contains),
                $"MapController.cs must contain at least one of: {string.Join(" | ", acceptableTokens)}.");
        }

        // ── Tooth 4a, T-ADAPTER — forbidden source idioms, one row per idiom ─────────────────────

        /// <summary>
        /// Each row names a token MapController.cs must NOT contain: legacy UnityEngine.Input (the project
        /// uses the new Input System exclusively), the retired fused ApplyTilt helper, and re-implemented
        /// camera math that belongs in Core/ConstrainedAngle instead of the binding (T-ADAPTER).
        /// </summary>
        private static IEnumerable<TestCaseData> ForbiddenIdiomCases()
        {
            yield return new TestCaseData("UnityEngine.Input.")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_Prefix)");
            yield return new TestCaseData("Input.GetAxis")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_GetAxis)");
            yield return new TestCaseData("Input.GetButton")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_GetButton)");
            yield return new TestCaseData("Input.GetKey")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_GetKey)");
            yield return new TestCaseData("Input.mouseScrollDelta")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_MouseScrollDelta)");
            yield return new TestCaseData("ApplyTilt")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(FusedApplyTilt)");
            yield return new TestCaseData("% 360")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(ManualAngleWrap)");
            yield return new TestCaseData("Mathf.Clamp")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(MathfClampOnAngles)");
            yield return new TestCaseData("WebMercator.")
                .SetName("MapControllerSource_DoesNotContainForbiddenIdiom(DirectWebMercatorCall)");
        }

        [Test]
        [TestCaseSource(nameof(ForbiddenIdiomCases))]
        public void MapControllerSource_DoesNotContainForbiddenIdiom(string forbiddenToken)
        {
            Assert.IsFalse(MapControllerSource.Contains(forbiddenToken),
                $"MapController.cs must not contain '{forbiddenToken}' — camera math and legacy device " +
                "reads live elsewhere (T-ADAPTER); use the new Input System and ConstrainedAngle/Core instead.");
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
