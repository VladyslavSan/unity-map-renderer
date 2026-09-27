// Unity EditMode only — structural guard: reads TouchController.cs as text and asserts the
// presence/absence of required strings, like MapControllerInputTests.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using MapRenderer.App;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class TouchInputStackTests
    {
        private static string TouchControllerSource
        {
            get
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                // Move-proof: find the source by filename anywhere under Assets/Code (survives assembly moves).
                string[] hits = Directory.GetFiles(Path.Combine(root, "Assets", "Code"), "TouchController.cs", SearchOption.AllDirectories);
                Assert.That(hits, Has.Length.EqualTo(1), "expected exactly one TouchController.cs under Assets/Code");
                return File.ReadAllText(hits[0]);
            }
        }

        // ── T-TOUCHSTACK-A/C: required source idioms, one row per idiom ──────────────────────────

        /// <summary>
        /// Each row names a token (or a set of acceptable alternatives) TouchController.cs must contain:
        /// the EnhancedTouch stack (T-TOUCHSTACK-A) and delegation to the seam dispatch / recognizer
        /// (T-TOUCHSTACK-C).
        /// </summary>
        private static IEnumerable<TestCaseData> RequiredIdiomCases()
        {
            // A bare string[] argument would be unpacked by NUnit as the argument LIST (its single-array-
            // parameter gotcha); the (object) cast forces it to stay one argument.
            yield return new TestCaseData((object)new[] { "EnhancedTouchSupport.Enable(" })
                .SetName("TouchControllerSource_ContainsRequiredIdiom(EnhancedTouchSupportEnable)");
            yield return new TestCaseData((object)new[] { "EnhancedTouch" })
                .SetName("TouchControllerSource_ContainsRequiredIdiom(EnhancedTouchNamespace)");
            yield return new TestCaseData((object)new[] { "Touch.activeTouches" })
                .SetName("TouchControllerSource_ContainsRequiredIdiom(TouchActiveTouches)");
            yield return new TestCaseData((object)new[] { "ViewInput.Apply(" })
                .SetName("TouchControllerSource_ContainsRequiredIdiom(DelegatesToViewInputApply)");
            yield return new TestCaseData((object)new[] { "Recognize(", "TouchGestureRecognizer" })
                .SetName("TouchControllerSource_ContainsRequiredIdiom(DelegatesToRecognizer)");
        }

        [Test]
        [TestCaseSource(nameof(RequiredIdiomCases))]
        public void TouchControllerSource_ContainsRequiredIdiom(string[] acceptableTokens)
        {
            string src = TouchControllerSource;
            Assert.IsTrue(acceptableTokens.Any(src.Contains),
                $"TouchController.cs must contain at least one of: {string.Join(" | ", acceptableTokens)} (T-TOUCHSTACK).");
        }

        // ── T-TOUCHSTACK-B/D — forbidden source idioms, one row per idiom ────────────────────────

        /// <summary>
        /// Each row names a token TouchController.cs must NOT contain: legacy UnityEngine.Input touch API
        /// (T-TOUCHSTACK-B), re-implemented camera/gesture/disambiguation math that belongs in
        /// Core/ConstrainedAngle/TouchGestureRecognizer instead (T-TOUCHSTACK-D), and the vestigial
        /// DpiScale knob (density is normalized once, at the position basis).
        /// </summary>
        private static IEnumerable<TestCaseData> ForbiddenIdiomCases()
        {
            yield return new TestCaseData("Input.touches")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_Touches)");
            yield return new TestCaseData("Input.GetTouch")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_GetTouch)");
            yield return new TestCaseData("Input.touchCount")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_TouchCount)");
            yield return new TestCaseData("UnityEngine.Input.")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(LegacyInput_Prefix)");
            yield return new TestCaseData("% 360")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(ManualAngleWrap)");
            yield return new TestCaseData("Mathf.Clamp")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(MathfClampOnAngles)");
            yield return new TestCaseData("WebMercator.")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(DirectWebMercatorCall)");
            yield return new TestCaseData("math.atan2")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(DisambiguationAtan2)");
            yield return new TestCaseData("math.log2")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(PinchZoomLog2)");
            yield return new TestCaseData("DpiScale")
                .SetName("TouchControllerSource_DoesNotContainForbiddenIdiom(VestigialDpiScale)");
        }

        [Test]
        [TestCaseSource(nameof(ForbiddenIdiomCases))]
        public void TouchControllerSource_DoesNotContainForbiddenIdiom(string forbiddenToken)
        {
            Assert.IsFalse(TouchControllerSource.Contains(forbiddenToken),
                $"TouchController.cs must not contain '{forbiddenToken}' — camera/gesture math, legacy device " +
                "reads, and double density-normalization live elsewhere (T-TOUCHSTACK).");
        }

        // ── Touch-DPI closure — the seam runs in LOGICAL px ──────────────────────────────────────

        /// <summary>
        /// The touch seam must be DPI-normalized like the mouse seam: <see cref="TouchController"/> converts the
        /// viewport and finger positions with <c>DeviceScaling.DeviceToLogicalPx</c>, so anchors share the render
        /// camera's logical basis. Headless runs have DPR=1 and no synthetic touches, so the guard is structural.
        /// Its mouse twin lives in <c>MapControllerInputTests</c>.
        /// </summary>
        [Test]
        public void TouchController_ConvertsSeamThroughTheDeviceToLogicalConversion()
        {
            string src = TouchControllerSource;
            Assert.IsTrue(src.Contains("DevicePixelRatio"),
                "TouchController.cs must read Config.DevicePixelRatio to run the interaction seam in logical px " +
                "(the touch-DPI closure — else retina pan/pinch drifts off the fingers).");
            // BOTH the viewport AND the finger positions must be converted; normalizing only one still
            // drifts the anchor.
            int conversions = DeviceScalingSeam.ConversionCallCount(src);
            Assert.GreaterOrEqual(conversions, 2,
                $"TouchController.cs must convert BOTH the viewport AND the finger positions — found " +
                $"{conversions} DeviceToLogicalPx call(s), expected ≥2 (touch-DPI closure / T3-5).");
        }
    }

    /// <summary>
    /// Shared by the two T3-5 teeth — the touch seam's (above) and the mouse seam's twin in
    /// <see cref="MapControllerInputTests"/>. One helper so the two can only be weakened together.
    /// </summary>
    internal static class DeviceScalingSeam
    {
        /// <summary>
        /// Counts CALL sites of the device→logical conversion in <paramref name="source"/>, ignoring
        /// <c>//</c> comment lines. Both defences are load-bearing: each seam's own comments name the
        /// conversion, so a naive substring count would read 2 for a half-fix that converted the viewport and
        /// left the cursor/finger positions raw — exactly the partial fix these teeth exist to catch.
        /// </summary>
        internal static int ConversionCallCount(string source)
        {
            int count = 0;
            foreach (string line in source.Split('\n'))
            {
                if (line.TrimStart().StartsWith("//")) continue;
                count += System.Text.RegularExpressions.Regex.Matches(line, @"DeviceToLogicalPx\(").Count;
            }
            return count;
        }
    }
}
