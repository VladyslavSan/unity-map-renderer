// Unity EditMode only — T-TOUCHSTACK structural guard.
// Mirrors MapControllerInputTests: reads TouchController.cs as text and asserts
// the presence/absence of required strings, so structural correctness is a runnable test.

using System.IO;
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

        // ── T-TOUCHSTACK-A: uses EnhancedTouch + Enable ──────────────────────────────────────────

        /// <summary>
        /// T-TOUCHSTACK: TouchController.cs must use EnhancedTouchSupport.Enable() — without it,
        /// Touch.activeTouches is always empty and touch silently does nothing.
        /// </summary>
        [Test]
        public void TouchController_Uses_EnhancedTouchSupport_Enable()
        {
            string src = TouchControllerSource;
            Assert.IsTrue(src.Contains("EnhancedTouchSupport.Enable("),
                "TouchController.cs must call EnhancedTouchSupport.Enable() in OnEnable " +
                "(without it Touch.activeTouches is always empty — T-TOUCHSTACK).");
        }

        /// <summary>
        /// T-TOUCHSTACK: EnhancedTouch namespace must be used (not legacy touch).
        /// </summary>
        [Test]
        public void TouchController_Uses_EnhancedTouch_Namespace()
        {
            Assert.IsTrue(TouchControllerSource.Contains("EnhancedTouch"),
                "TouchController.cs must reference the EnhancedTouch namespace (T-TOUCHSTACK).");
        }

        /// <summary>
        /// T-TOUCHSTACK: Touch.activeTouches must be present and greppable.
        /// </summary>
        [Test]
        public void TouchController_Uses_Touch_activeTouches()
        {
            Assert.IsTrue(TouchControllerSource.Contains("Touch.activeTouches"),
                "TouchController.cs must read Touch.activeTouches (T-TOUCHSTACK).");
        }

        // ── T-TOUCHSTACK-B: no legacy UnityEngine.Input ──────────────────────────────────────────

        /// <summary>
        /// T-TOUCHSTACK: TouchController.cs must NOT use legacy UnityEngine.Input touch API.
        /// The project uses the new Input System exclusively (activeInputHandler = 1).
        /// </summary>
        [Test]
        public void TouchController_DoesNotUse_LegacyInput_touches()
        {
            Assert.IsFalse(TouchControllerSource.Contains("Input.touches"),
                "TouchController.cs must not use 'Input.touches' (legacy UnityEngine.Input — T-TOUCHSTACK).");
        }

        [Test]
        public void TouchController_DoesNotUse_LegacyInput_GetTouch()
        {
            Assert.IsFalse(TouchControllerSource.Contains("Input.GetTouch"),
                "TouchController.cs must not use 'Input.GetTouch' (legacy UnityEngine.Input — T-TOUCHSTACK).");
        }

        [Test]
        public void TouchController_DoesNotUse_LegacyInput_touchCount()
        {
            Assert.IsFalse(TouchControllerSource.Contains("Input.touchCount"),
                "TouchController.cs must not use 'Input.touchCount' (legacy UnityEngine.Input — T-TOUCHSTACK).");
        }

        [Test]
        public void TouchController_DoesNotUse_LegacyUnityEngineInput_Prefix()
        {
            Assert.IsFalse(TouchControllerSource.Contains("UnityEngine.Input."),
                "TouchController.cs must not use 'UnityEngine.Input.' (legacy API — T-TOUCHSTACK).");
        }

        // ── T-TOUCHSTACK-C: delegates to recognizer + ViewInput.Apply (no re-impl) ────────────────

        /// <summary>
        /// T-TOUCHSTACK: TouchController.cs must delegate to ViewInput.Apply (the seam dispatch)
        /// — it must not re-implement camera/gesture math.
        /// </summary>
        [Test]
        public void TouchController_DelegatesTo_ViewInputApply()
        {
            Assert.IsTrue(TouchControllerSource.Contains("ViewInput.Apply("),
                "TouchController.cs must delegate to 'ViewInput.Apply(' — the seam dispatch (T-TOUCHSTACK).");
        }

        /// <summary>
        /// T-TOUCHSTACK: TouchController.cs must delegate to TouchGestureRecognizer.Recognize.
        /// </summary>
        [Test]
        public void TouchController_DelegatesTo_Recognizer_Recognize()
        {
            string src = TouchControllerSource;
            Assert.IsTrue(src.Contains("Recognize(") || src.Contains("TouchGestureRecognizer"),
                "TouchController.cs must reference TouchGestureRecognizer/Recognize (T-TOUCHSTACK).");
        }

        // ── T-TOUCHSTACK-D: no re-implemented camera/gesture math ────────────────────────────────

        [Test]
        public void TouchController_NoAngleWrapMath()
        {
            Assert.IsFalse(TouchControllerSource.Contains("% 360"),
                "TouchController.cs must not contain '% 360' — angle wrap lives in ConstrainedAngle (T-TOUCHSTACK).");
        }

        [Test]
        public void TouchController_NoMathfClamp_OnAngles()
        {
            Assert.IsFalse(TouchControllerSource.Contains("Mathf.Clamp"),
                "TouchController.cs must not contain 'Mathf.Clamp' — clamping lives in ConstrainedAngle (T-TOUCHSTACK).");
        }

        [Test]
        public void TouchController_NoWebMercatorDirectCall()
        {
            Assert.IsFalse(TouchControllerSource.Contains("WebMercator."),
                "TouchController.cs must not call WebMercator.* directly — projection lives in Core (T-TOUCHSTACK).");
        }

        [Test]
        public void TouchController_NoDisambiguationMathInBinding()
        {
            string src = TouchControllerSource;
            Assert.IsFalse(src.Contains("math.atan2"),
                "TouchController.cs must not contain 'math.atan2' — disambiguation lives in TouchGestureRecognizer (T-TOUCHSTACK).");
            Assert.IsFalse(src.Contains("math.log2"),
                "TouchController.cs must not contain 'math.log2' — pinch-to-zoom lives in TouchGestureRecognizer (T-TOUCHSTACK).");
        }

        // ── Touch-DPI closure — the seam runs in LOGICAL px ──────────────────────────────────────

        /// <summary>
        /// The touch interaction seam must be DPI-normalized like the
        /// mouse seam — <see cref="TouchController"/> converts the viewport + finger positions with
        /// <c>DeviceScaling.DeviceToLogicalPx</c> so anchors share the render camera's logical basis (no
        /// retina pan/pinch drift). This is the one regression point the headless gate can't exercise (DPR=1,
        /// no synthetic touches), so guard it structurally. Its twin for the mouse seam lives in
        /// <c>MapControllerInputTests</c>.
        /// </summary>
        [Test]
        public void TouchController_ConvertsSeamThroughTheDeviceToLogicalConversion()
        {
            string src = TouchControllerSource;
            Assert.IsTrue(src.Contains("DevicePixelRatio"),
                "TouchController.cs must read Config.DevicePixelRatio to run the interaction seam in logical px " +
                "(the touch-DPI closure — else retina pan/pinch drifts off the fingers).");
            // BOTH the viewport AND the finger positions must be converted. A partial fix that normalizes only
            // one still drifts the anchor — and this structural guard is the only durable protection (the
            // functional path is DPR=1 / no-synthetic-touch and can't be exercised headless).
            int conversions = DeviceScalingSeam.ConversionCallCount(src);
            Assert.GreaterOrEqual(conversions, 2,
                $"TouchController.cs must convert BOTH the viewport AND the finger positions — found " +
                $"{conversions} DeviceToLogicalPx call(s), expected ≥2 (touch-DPI closure / T3-5).");
        }


        /// <summary>
        /// Density normalization lives ONCE, at the position basis — NOT re-applied in the recognizer.
        /// The vestigial (and mis-scaled) <c>DpiScale</c> knob is gone; its presence would signal a
        /// double-normalization regression.
        /// </summary>
        [Test]
        public void TouchController_NoDpiScale_DensityNormalizedOnceAtThePositionBasis()
        {
            Assert.IsFalse(TouchControllerSource.Contains("DpiScale"),
                "TouchController.cs must not set DpiScale — density is normalized once by the ÷DevicePixelRatio " +
                "seam; a DpiScale multiply would double-apply it (the touch-DPI closure).");
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
