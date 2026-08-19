// Unity EditMode only — tests Bootstrapper.EnsureEnvironmentLighting() (fix for pure-black
// fill-extrusion walls: RenderSettings.ambientProbe was never generated for the demo scenes, so any
// face the directional light misses got zero indirect fill). Verifies the guard fires exactly when the
// probe is degenerate and never clobbers an already-populated (e.g. host-baked) probe.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Bootstrapper = MapRenderer.Unity.Rendering.Map.Bootstrapper;

namespace MapRenderer.Tests.MapViews
{
    /// <summary>
    /// Guard-behaviour tests for <see cref="Bootstrapper.EnsureEnvironmentLighting"/>.
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

                Bootstrapper.EnsureEnvironmentLighting();

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

                Bootstrapper.EnsureEnvironmentLighting();

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
