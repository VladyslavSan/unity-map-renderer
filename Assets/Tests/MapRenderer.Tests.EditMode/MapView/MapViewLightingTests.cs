// MapView/MapViewLightingTests.cs — MapHost's directional light and environment-probe bootstrap (EditMode).
//
// Split from MapView/MapViewTests.cs by a using collision, not by size: EnvironmentLightingTests
// imports UnityEngine.Rendering (CameraProperties), and the other four MapView files use bare
// CameraProperties from MapRenderer.Core.Geo — the two must never share a file.
//
// Contents:
//   DirectionalLightBootstrapTests  — MapHost.EnsureDirectionalLight adds a light only when the scene has none.
//   EnvironmentLightingTests        — MapHost.EnsureEnvironmentLighting fires only when the ambient probe is degenerate.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MapHost = MapRenderer.App.MapHost;
using UnityEngine.Rendering;
using Unity.Mathematics;
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;


namespace MapRenderer.Tests.MapViews
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // DirectionalLightBootstrapTests — MapHost.EnsureDirectionalLight — adds a light only when the scene has none
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Behaviour tests for <see cref="MapHost.EnsureDirectionalLight"/>: unconditional creation when
    /// the scene lacks a directional light, and idempotency when one is already present. The light exists
    /// in BOTH render modes because the unlit fill-extrusion twin consumes its direction — only the ambient
    /// probe (<c>EnvironmentLightingTests</c>) is unlit-gated.
    /// </summary>
    [TestFixture]
    public class DirectionalLightBootstrapTests : BaseTestFixture
    {
        // Active directional lights only (FindObjectsByType excludes inactive) — the quantity the method's
        // own FindAnyObjectByType guard and these assertions both reason about.
        private static List<Light> ActiveDirectionalLights()
        {
            var found = new List<Light>();
            foreach (var light in Object.FindObjectsByType<Light>())
                if (light.type == LightType.Directional) found.Add(light);
            return found;
        }

        // Every currently-active light of ANY type — hidden (not destroyed) for the duration of a test so a
        // stray scene light can't satisfy the method's guard, then restored. Destroys anything the test
        // itself introduced.
        private static List<Light> HideAllActiveLights()
        {
            var hidden = new List<Light>(Object.FindObjectsByType<Light>());
            foreach (var light in hidden) light.gameObject.SetActive(false);
            return hidden;
        }

        private static void RestoreLights(List<Light> hidden)
        {
            foreach (var light in Object.FindObjectsByType<Light>())
                if (!hidden.Contains(light)) Object.DestroyImmediate(light.gameObject);
            foreach (var light in hidden) if (light != null) light.gameObject.SetActive(true);
        }

        [Test]
        public void EnsureDirectionalLight_CreatesLightWhenNoneExists()
        {
            var hidden = HideAllActiveLights();
            try
            {
                MapHost.EnsureDirectionalLight();

                Assert.AreEqual(1, ActiveDirectionalLights().Count,
                    "EnsureDirectionalLight must create exactly one directional light when the scene " +
                    "(visibly) has none — unconditional across render modes; the unlit fill-extrusion twin " +
                    "reads its direction for face shading.");
            }
            finally { RestoreLights(hidden); }
        }

        [Test]
        public void EnsureDirectionalLight_DoesNotCreateSecond_WhenDirectionalLightExists()
        {
            var hidden = HideAllActiveLights();
            try
            {
                var tempGo = Track(new GameObject("TempDirectional"));
                tempGo.AddComponent<Light>().type = LightType.Directional;
                Assert.AreEqual(1, ActiveDirectionalLights().Count,
                    "precondition: exactly one directional light is visible before the call.");

                MapHost.EnsureDirectionalLight();

                Assert.AreEqual(1, ActiveDirectionalLights().Count,
                    "EnsureDirectionalLight must NOT create a second directional light when one already " +
                    "exists — it is idempotent (guards on FindAnyObjectByType<Light>).");
            }
            finally
            {
                RestoreLights(hidden);
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // EnvironmentLightingTests — MapHost.EnsureEnvironmentLighting — fires only when the ambient probe is degenerate
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Guard-behaviour tests for <see cref="MapHost.EnsureEnvironmentLighting"/>.
    /// </summary>
    [TestFixture]
    public class EnvironmentLightingTests
    {
        [Test]
        public void EnsureEnvironmentLighting_DegenerateProbe_PopulatesIt()
        {
            var prevMode  = RenderSettings.ambientMode;
            var prevLight = RenderSettings.ambientLight;
            var prevProbe = RenderSettings.ambientProbe;

            try
            {
                // Force the exact degenerate state the bug report measured: a Skybox-mode scene whose
                // environment lighting was never generated (probe DC/L0 term == 0 in every channel).
                RenderSettings.ambientMode  = AmbientMode.Skybox;
                RenderSettings.ambientProbe = default; // all 27 SH coefficients zero

                TestContext.WriteLine($"skybox={RenderSettings.skybox} mode={RenderSettings.ambientMode} " +
                    $"dcBefore={math.abs(RenderSettings.ambientProbe[0, 0]) + math.abs(RenderSettings.ambientProbe[1, 0]) + math.abs(RenderSettings.ambientProbe[2, 0])}");

                MapHost.EnsureEnvironmentLighting(RenderMode.Lit);

                var probe = RenderSettings.ambientProbe;
                float dcTerm = math.abs(probe[0, 0]) + math.abs(probe[1, 0]) + math.abs(probe[2, 0]);
                TestContext.WriteLine($"dcAfter={dcTerm}");
                Assert.Greater(dcTerm, 0f,
                    "EnsureEnvironmentLighting must populate a non-zero ambient probe when the scene's " +
                    "environment lighting was never generated — otherwise faces the sun misses stay black.");
            }
            finally
            {
                RenderSettings.ambientMode  = prevMode;
                RenderSettings.ambientLight = prevLight;
                RenderSettings.ambientProbe = prevProbe;
            }
        }

        [Test]
        public void EnsureEnvironmentLighting_UnlitMode_LeavesDegenerateProbeUntouched()
        {
            // S4 (unlit epic): the mode gate — under Unlit, the SAME degenerate-probe scenario that Lit
            // populates above must be left untouched (no DynamicGI.UpdateEnvironment call). Unlit map
            // geometry has no indirect-lighting term to fill, so generating a probe for it is dead work.
            var prevMode  = RenderSettings.ambientMode;
            var prevLight = RenderSettings.ambientLight;
            var prevProbe = RenderSettings.ambientProbe;

            try
            {
                RenderSettings.ambientMode  = AmbientMode.Skybox;
                RenderSettings.ambientProbe = default; // all 27 SH coefficients zero — same as the Lit case

                // Precondition, asserted BEFORE the call under test: if the probe doesn't round-trip to
                // exactly zero here, a failure below is a harness problem, not a gate failure.
                var before = RenderSettings.ambientProbe;
                float dcBefore = math.abs(before[0, 0]) + math.abs(before[1, 0]) + math.abs(before[2, 0]);
                Assert.AreEqual(0f, dcBefore, "precondition: the probe must round-trip to exactly zero.");

                MapHost.EnsureEnvironmentLighting(RenderMode.Unlit);

                var probe = RenderSettings.ambientProbe;
                float dcTerm = math.abs(probe[0, 0]) + math.abs(probe[1, 0]) + math.abs(probe[2, 0]);
                Assert.AreEqual(0f, dcTerm,
                    "EnsureEnvironmentLighting(RenderMode.Unlit) must NOT populate the ambient probe — " +
                    "unlit map geometry has no indirect term to fill.");
            }
            finally
            {
                RenderSettings.ambientMode  = prevMode;
                RenderSettings.ambientLight = prevLight;
                RenderSettings.ambientProbe = prevProbe;
            }
        }

        [Test]
        public void EnsureEnvironmentLighting_NonDegenerateProbe_LeavesItUnchanged()
        {
            var prevMode  = RenderSettings.ambientMode;
            var prevLight = RenderSettings.ambientLight;
            var prevProbe = RenderSettings.ambientProbe;

            try
            {
                // Simulate a host scene that already has real (e.g. baked) environment lighting.
                var baked = new SphericalHarmonicsL2();
                baked.AddAmbientLight(new Color(0.4f, 0.5f, 0.6f, 1f));
                RenderSettings.ambientMode  = AmbientMode.Skybox;
                RenderSettings.ambientProbe = baked;

                MapHost.EnsureEnvironmentLighting(RenderMode.Lit);

                var probe = RenderSettings.ambientProbe;
                for (int channel = 0; channel < 3; channel++)
                for (int coeff = 0; coeff < 9; coeff++)
                    Assert.AreEqual(baked[channel, coeff], probe[channel, coeff], 1e-6f,
                        "EnsureEnvironmentLighting must not touch an already-populated ambient probe " +
                        "(protects a host application's own baked/generated lighting).");
            }
            finally
            {
                RenderSettings.ambientMode  = prevMode;
                RenderSettings.ambientLight = prevLight;
                RenderSettings.ambientProbe = prevProbe;
            }
        }
    }
}
