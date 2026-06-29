// Unity EditMode only — structural guard for S42 tooth 4: no legacy UnityEngine.Input,
// scroll normalized by WheelNotchUnits, Keyboard.current present, null-guarded.
//
// Mirrors the ShaderStructureTests pattern: reads the source file as text and asserts
// the presence/absence of required strings. This makes tooth 4 a durable, runnable test
// rather than a manual grep.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapController = MapRenderer.Unity.Rendering.Map.Controller;

namespace MapRenderer.Tests
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
                string path   = Path.Combine(root, "Assets", "MapRenderer.Unity", "Rendering", "Map", "Controller.cs");
                return File.ReadAllText(path);
            }
        }

        // ── Tooth 4a — no legacy UnityEngine.Input ────────────────────────────────────────────────

        /// <summary>
        /// S42 tooth 4: MapController.cs must NOT use legacy UnityEngine.Input (the project uses the
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
        /// S42 tooth 4: scroll must be divided by the named normalization constant WheelNotchUnits
        /// so that a wheel notch and a trackpad swipe are comparable. The constant must be greppable.
        /// </summary>
        [Test]
        public void MapController_HasNamedScrollNormalizationConstant()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("WheelNotchUnits"),
                "MapController.cs must define and use 'WheelNotchUnits' as the device-independent " +
                "scroll normalization constant (tooth 4, S42 D4).");
        }

        // ── Tooth 4c — Keyboard.current present and greppable ────────────────────────────────────

        /// <summary>
        /// S42 tooth 4: keyboard zoom (via Keyboard.current) must be present and greppable in
        /// MapController.cs so the feature cannot silently disappear.
        /// </summary>
        [Test]
        public void MapController_HasKeyboardCurrent()
        {
            string src = MapControllerSource;
            Assert.IsTrue(src.Contains("Keyboard.current"),
                "MapController.cs must contain 'Keyboard.current' (keyboard zoom, S42 D4 / tooth 4).");
        }

        // ── Tooth 4d — Mouse.current null-guarded ────────────────────────────────────────────────

        /// <summary>
        /// S42 tooth 4: Mouse.current must be null-guarded so headless/test builds don't NRE.
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
        /// S42 tooth 4: Keyboard.current must be null-guarded separately from Mouse.current
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
    }
}
