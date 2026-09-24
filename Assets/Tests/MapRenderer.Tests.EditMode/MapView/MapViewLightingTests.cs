// MapHost's directional light and environment-probe bootstrap. Its own file because UnityEngine.Rendering
// and MapRenderer.Core.Geo both define CameraProperties, and the other MapView files use the Core one.
//
// Contents:
//   DirectionalLightBootstrapTests  — MapHost.EnsureDirectionalLight adds a light only when the scene has none.
//   EnvironmentLightingTests        — MapHost.EnsureEnvironmentLighting fires only when the ambient probe is degenerate,
//                                     and IsUsableAmbientProbe rejects a corrupt probe.

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

        // Hides (does not destroy) every active light for the test, so a stray scene light can't satisfy the
        // method's guard; the caller restores them.
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
                Assert.IsTrue(MapHost.IsUsableAmbientProbe(probe), "The populated ambient probe is usable.");
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
            // Under Unlit, the same degenerate probe stays untouched: Unlit geometry has no indirect term, so
            // a DynamicGI.UpdateEnvironment call would be dead work.
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

        [Test]
        public void IsUsableAmbientProbe_FlatAmbientProbe_IsAccepted()
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(0.4f, 0.5f, 0.6f, 1f));
            Assert.IsTrue(MapHost.IsUsableAmbientProbe(probe), "A flat ambient probe is usable.");
        }

        [TestCase(1.678e34f, TestName = "IsUsableAmbientProbe_WebGpuReadbackGarbage_IsRejected")]
        [TestCase(float.NaN, TestName = "IsUsableAmbientProbe_NaNCoefficient_IsRejected")]
        [TestCase(float.PositiveInfinity, TestName = "IsUsableAmbientProbe_InfiniteCoefficient_IsRejected")]
        [TestCase(-0.5f, TestName = "IsUsableAmbientProbe_NegativeDcTerm_IsRejected")]
        public void IsUsableAmbientProbe_CorruptDcTerm_IsRejected(float corruptValue)
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(0.4f, 0.5f, 0.6f, 1f));
            probe[0, 0] = corruptValue;
            Assert.IsFalse(MapHost.IsUsableAmbientProbe(probe), $"A probe with DC term {corruptValue} is unusable.");
        }

        [Test]
        public void IsUsableAmbientProbe_GarbageHigherOrderCoefficient_IsRejected()
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(0.4f, 0.5f, 0.6f, 1f));
            probe[2, 7] = -1.275e33f;
            Assert.IsFalse(MapHost.IsUsableAmbientProbe(probe), "A huge higher-order coefficient is unusable.");
        }

        [Test]
        public void IsUsableAmbientProbe_ZeroProbe_IsRejected()
        {
            Assert.IsFalse(MapHost.IsUsableAmbientProbe(default), "An all-zero probe is unusable.");
        }

        private static SphericalHarmonicsL2 FlatProbe(Color color)
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(color);
            return probe;
        }

        [Test]
        public void ResolveAmbientProbe_TrustedUsableProbe_IsKept()
        {
            var convolved = FlatProbe(new Color(0.1f, 0.2f, 0.3f));

            var probe = MapHost.ResolveAmbientProbe(convolved, Color.black, trustConvolution: true);

            Assert.AreEqual(convolved, probe, "A trusted, usable convolved probe is kept.");
        }

        [Test]
        public void ResolveAmbientProbe_GarbageProbe_FallsBackToFlatAmbient()
        {
            var garbage = FlatProbe(new Color(0.1f, 0.2f, 0.3f));
            garbage[0, 0] = 1.678e34f;
            var ambient = new Color(0.6f, 0.7f, 0.8f);

            var probe = MapHost.ResolveAmbientProbe(garbage, ambient, trustConvolution: true);

            Assert.AreEqual(FlatProbe(ambient), probe, "A garbage probe falls back to a flat ambient probe.");
        }

        [Test]
        public void ResolveAmbientProbe_UntrustedConvolution_FallsBackEvenWhenPlausible()
        {
            var plausible = FlatProbe(new Color(0.1f, 0.2f, 0.3f));
            var ambient = new Color(0.6f, 0.7f, 0.8f);

            var probe = MapHost.ResolveAmbientProbe(plausible, ambient, trustConvolution: false);

            Assert.AreEqual(FlatProbe(ambient), probe,
                "An untrusted convolution (WebGPU) falls back to a flat ambient probe, however plausible it looks.");
        }

        [Test]
        public void ResolveAmbientProbe_DarkAmbient_IsRaisedToFallbackFloor()
        {
            var probe = MapHost.ResolveAmbientProbe(default, new Color(0.212f, 0.5f, 0f), trustConvolution: true);

            Assert.AreEqual(FlatProbe(new Color(0.4f, 0.5f, 0.4f)), probe,
                "Each fallback channel is raised to at least 0.4, and a brighter channel is kept.");
        }
    }
}
