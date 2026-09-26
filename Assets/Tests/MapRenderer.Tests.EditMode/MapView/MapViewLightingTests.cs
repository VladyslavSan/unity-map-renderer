// MapHost's directional light and environment-probe bootstrap, plus the SunLight writer it feeds. Its own
// file because UnityEngine.Rendering and MapRenderer.Core.Geo both define CameraProperties, and the other
// MapView files use the Core one — this file imports Core.Geo (for Angle) but never names CameraProperties
// unqualified, so the two usings coexist.
//
// Contents:
//   DirectionalLightBootstrapTests  — MapHost.EnsureDirectionalLight adds a light only when the scene has none.
//   EnvironmentLightingTests        — MapHost.EnsureEnvironmentLighting fires only when the ambient probe is degenerate,
//                                     and IsUsableAmbientProbe rejects a corrupt probe.
//   SunLightDefaultIdentityTests    — a style with no `light` block reproduces today's bootstrap light exactly.
//   SunLightOverrideTests           — a runtime override wins over the style, and a restyle clears it.
//   SkyGradientStyleTests           — spec defaults vs a specified sky; override/reset; lighting untouched.
//   SkyMapEdgeTests                 — the visible sky strip: where the map ends (far cut or limb) and the top ray.
//   DistanceHazeRangeTests          — the fog range: no haze top-down, full haze at the far cut, follows the far.
//   DistanceHazeStyleTests          — spec default vs a specified fog-color; override/reset; fog restored on dispose.
//   LightingRestyleEaseTests        — a restyle eases sun, sky and fog over the style transition; MapView drives it.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapHost = MapRenderer.App.MapHost;
using UnityEngine.Rendering;
using Unity.Mathematics;
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Core.Json;
using SkyPropertyId = MapRenderer.Unity.Rendering.ShaderProperties.Sky.PropertyId;
using StyleTransition = MapRenderer.Unity.Rendering.Style.StyleTransition;


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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SunLightDefaultIdentityTests / SunLightOverrideTests — the style `light` → Light writer
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SunLightDefaultIdentityTests : BaseTestFixture
    {
        [Test]
        public void ApplyStyle_NoLightBlock_MatchesBootstrapLightExactly()
        {
            var light = Track(new GameObject("TestSun")).AddComponent<Light>();
            var sun = new SunLight(light);

            sun.ApplyStyle(StyleLight.Parse(null), zoom: 0.0);

            // Forward of Euler(60, 30, 0) — today's bootstrap light (MapHost.EnsureDirectionalLight).
            Vector3 forward = light.transform.forward;
            Assert.AreEqual(0.25,       forward.x, 1e-6, "forward.x");
            Assert.AreEqual(-0.8660254, forward.y, 1e-6, "forward.y");
            Assert.AreEqual(0.4330127,  forward.z, 1e-6, "forward.z");

            Assert.AreEqual(Color.white, light.color, "no `light` block ⇒ white, like today's bootstrap.");
            Assert.AreEqual(1.0f, light.intensity, 1e-6f,
                "the spec default intensity 0.5 must resolve to exactly today's Unity intensity 1.0.");
        }

        [Test]
        public void IntensityToUnity_SpecDefault_ResolvesToOne()
        {
            Assert.AreEqual(1.0f, SunLight.IntensityToUnity(0.5f), 1e-6f);
        }

        [Test]
        public void Rotation_DefaultPosition_EqualsBootstrapEuler()
        {
            Quaternion expected = Quaternion.Euler(60f, 30f, 0f);
            Quaternion actual   = SunLight.Rotation(Angle.FromDegrees(210.0), Angle.FromDegrees(30.0));

            Assert.AreEqual(expected.x, actual.x, 1e-6f);
            Assert.AreEqual(expected.y, actual.y, 1e-6f);
            Assert.AreEqual(expected.z, actual.z, 1e-6f);
            Assert.AreEqual(expected.w, actual.w, 1e-6f);
        }

        [Test]
        public void Rotation_NonDefaultPosition_MatchesExpectedForward()
        {
            // Azimuth 90°, polar 60° (elevation 30°): forward = (cos(30)·sin(-90), -sin(30), cos(30)·cos(-90)).
            Quaternion rotation = SunLight.Rotation(Angle.FromDegrees(90.0), Angle.FromDegrees(60.0));
            Vector3 forward = rotation * Vector3.forward;

            Assert.AreEqual(-0.8660254, forward.x, 1e-6, "forward.x");
            Assert.AreEqual(-0.5,       forward.y, 1e-6, "forward.y");
            Assert.AreEqual(0.0,        forward.z, 1e-6, "forward.z");
        }
    }

    [TestFixture]
    public class SunLightOverrideTests : BaseTestFixture
    {
        [Test]
        public void SetOverride_WinsOverStyle_UntilResetOrRestyle()
        {
            var light = Track(new GameObject("TestSun")).AddComponent<Light>();
            var sun = new SunLight(light);
            sun.ApplyStyle(StyleLight.Parse(null), zoom: 0.0);

            sun.SetOverride(Angle.FromDegrees(90.0), Angle.FromDegrees(60.0), Color.red, 3.0f);

            Assert.IsTrue(sun.IsOverridden);
            Vector3 forward = light.transform.forward;
            Assert.AreEqual(-0.8660254, forward.x, 1e-6, "forward.x");
            Assert.AreEqual(-0.5,       forward.y, 1e-6, "forward.y");
            Assert.AreEqual(0.0,        forward.z, 1e-6, "forward.z");
            Assert.AreEqual(Color.red, light.color);
            Assert.AreEqual(3.0f, light.intensity, 1e-6f);
        }

        [Test]
        public void ApplyStyle_Restyle_ClearsOverride()
        {
            var light = Track(new GameObject("TestSun")).AddComponent<Light>();
            var sun = new SunLight(light);
            sun.ApplyStyle(StyleLight.Parse(null), zoom: 0.0);
            sun.SetOverride(Angle.FromDegrees(90.0), Angle.FromDegrees(60.0), Color.red, 3.0f);
            Assert.IsTrue(sun.IsOverridden, "precondition: override active.");

            sun.ApplyStyle(StyleLight.Parse(null), zoom: 0.0);

            Assert.IsFalse(sun.IsOverridden, "a restyle (ApplyStyle) must clear a runtime override.");
            Vector3 forward = light.transform.forward;
            Assert.AreEqual(0.25,       forward.x, 1e-6, "forward.x reverts to the style's own direction.");
            Assert.AreEqual(-0.8660254, forward.y, 1e-6, "forward.y reverts to the style's own direction.");
            Assert.AreEqual(0.4330127,  forward.z, 1e-6, "forward.z reverts to the style's own direction.");
            Assert.AreEqual(Color.white, light.color, "cleared override ⇒ the style's own color.");
            Assert.AreEqual(1.0f, light.intensity, 1e-6f, "cleared override ⇒ the style's own intensity.");
        }

        [Test]
        public void ResetToStyle_ReappliesLastStyle()
        {
            var light = Track(new GameObject("TestSun")).AddComponent<Light>();
            var sun = new SunLight(light);
            sun.ApplyStyle(StyleLight.Parse(null), zoom: 0.0);
            sun.SetOverride(Angle.FromDegrees(90.0), Angle.FromDegrees(60.0), Color.red, 3.0f);

            sun.ResetToStyle();

            Assert.IsFalse(sun.IsOverridden);
            Vector3 forward = light.transform.forward;
            Assert.AreEqual(0.25,       forward.x, 1e-6, "forward.x reverts to the style's own direction.");
            Assert.AreEqual(-0.8660254, forward.y, 1e-6, "forward.y reverts to the style's own direction.");
            Assert.AreEqual(0.4330127,  forward.z, 1e-6, "forward.z reverts to the style's own direction.");
            Assert.AreEqual(Color.white, light.color);
            Assert.AreEqual(1.0f, light.intensity, 1e-6f);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SkyGradientStyleTests — the style `sky` → skybox gradient writer
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SkyGradientStyleTests : BaseTestFixture
    {
        private static StyleSky ParseSky(string json) => StyleSky.Parse(JsonParser.Parse(json).Get("sky"));

        private Camera NewCamera()
        {
            var camera = Track(new GameObject("TestSkyCamera")).AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            return camera;
        }

        [Test]
        public void ApplyStyle_NoSkyBlock_UsesSpecDefaults()
        {
            using var sky = new SkyGradient(NewCamera());

            sky.ApplyStyle(StyleSky.Parse(null), zoom: 0.0);

            Assert.AreEqual(0x88 / 255f, sky.SkyColor.r, 1e-6f, "no `sky` block ⇒ spec default sky-color #88C6FC.");
            Assert.AreEqual(0xC6 / 255f, sky.SkyColor.g, 1e-6f, "no `sky` block ⇒ spec default sky-color #88C6FC.");
            Assert.AreEqual(0xFC / 255f, sky.SkyColor.b, 1e-6f, "no `sky` block ⇒ spec default sky-color #88C6FC.");
            Assert.AreEqual(Color.white, sky.HorizonColor, "no `sky` block ⇒ spec default horizon-color white.");
            Assert.IsNotNull(sky.Material, "Map/Sky must resolve in the Editor.");
            Assert.AreEqual(Color.white, sky.Material.GetColor(SkyPropertyId.HorizonColor));
        }

        [Test]
        public void ApplyStyle_SpecifiedSky_WritesItsColors()
        {
            using var sky = new SkyGradient(NewCamera());

            sky.ApplyStyle(ParseSky("{\"sky\":{\"sky-color\":\"#ff0000\",\"horizon-color\":\"#00ff00\"}}"), zoom: 0.0);

            Assert.AreEqual(Color.red, sky.SkyColor);
            Assert.AreEqual(Color.green, sky.HorizonColor);
            Assert.AreEqual(Color.red, sky.Material.GetColor(SkyPropertyId.SkyColor));
            Assert.AreEqual(Color.green, sky.Material.GetColor(SkyPropertyId.HorizonColor));
        }

        [Test]
        public void SetOverride_WinsOverStyle_UntilRestyle()
        {
            using var sky = new SkyGradient(NewCamera());
            sky.ApplyStyle(StyleSky.Parse(null), zoom: 0.0);

            sky.SetOverride(Color.red, Color.blue);

            Assert.IsTrue(sky.IsOverridden);
            Assert.AreEqual(Color.red, sky.Material.GetColor(SkyPropertyId.SkyColor));
            Assert.AreEqual(Color.blue, sky.Material.GetColor(SkyPropertyId.HorizonColor));

            sky.ApplyStyle(ParseSky("{\"sky\":{\"horizon-color\":\"#00ff00\"}}"), zoom: 0.0);

            Assert.IsFalse(sky.IsOverridden, "a restyle (ApplyStyle) must clear a runtime override.");
            Assert.AreEqual(Color.green, sky.HorizonColor, "cleared override ⇒ the new style's own colour.");
        }

        [Test]
        public void ResetToStyle_ReappliesLastStyle()
        {
            using var sky = new SkyGradient(NewCamera());
            sky.ApplyStyle(ParseSky("{\"sky\":{\"sky-color\":\"#ff0000\"}}"), zoom: 0.0);
            sky.SetOverride(Color.blue, Color.blue);

            sky.ResetToStyle();

            Assert.IsFalse(sky.IsOverridden);
            Assert.AreEqual(Color.red, sky.SkyColor);
            Assert.AreEqual(Color.white, sky.HorizonColor);
            Assert.AreEqual(Color.red, sky.Material.GetColor(SkyPropertyId.SkyColor));
        }

        [Test]
        public void Wiring_UsesCameraSkybox_AndLeavesSceneLightingUntouched()
        {
            Material skyboxBefore     = RenderSettings.skybox;
            var probeBefore           = RenderSettings.ambientProbe;
            var reflectionModeBefore  = RenderSettings.defaultReflectionMode;
            Texture reflectionBefore  = RenderSettings.customReflectionTexture;
            Camera camera = NewCamera();

            var sky = new SkyGradient(camera);
            Material material = sky.Material;
            try
            {
                sky.ApplyStyle(StyleSky.Parse(null), zoom: 0.0);
                sky.SetOverride(Color.black, Color.black);
                sky.ResetToStyle();

                Assert.AreEqual(CameraClearFlags.Skybox, camera.clearFlags);
                Assert.AreSame(material, camera.GetComponent<Skybox>().material,
                    "the sky rides the camera's own Skybox component.");
                Assert.AreSame(skyboxBefore, RenderSettings.skybox,
                    "RenderSettings.skybox feeds the ambient convolution and reflections; it must not change.");
                Assert.AreEqual(probeBefore, RenderSettings.ambientProbe);
                Assert.AreEqual(reflectionModeBefore, RenderSettings.defaultReflectionMode);
                Assert.AreSame(reflectionBefore, RenderSettings.customReflectionTexture);
            }
            finally { sky.Dispose(); }

            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags, "Dispose restores the clear.");
            Assert.IsFalse(camera.TryGetComponent<Skybox>(out _), "Dispose removes the Skybox component it added.");
            Assert.IsTrue(material == null, "Dispose destroys the runtime material.");
        }
    }

    [TestFixture]
    public class SkyMapEdgeTests
    {
        private const double Distance = 1000.0;

        // The camera orbit position for a tilt, heading 0, looking at the origin.
        private static double3 Orbit(double tiltDeg, double distance)
        {
            Angle tilt = Angle.FromDegrees(tiltDeg);
            return new double3(0.0, distance * tilt.Cos, -distance * tilt.Sin);
        }

        [Test]
        public void Planar_TopDown_EdgeIsTheHorizon()
        {
            Angle edge = SkyGradient.MapEdgeElevation(Orbit(0.0, Distance), 4.0 * Distance, new WebMercatorProjection());

            Assert.AreEqual(0.0, edge.Degrees, 1e-9, "top-down, the whole plane is nearer than the far plane.");
        }

        [Test]
        public void Planar_Tilt60_EdgeIsTheFarCutOnTheGround()
        {
            Angle edge = SkyGradient.MapEdgeElevation(Orbit(60.0, Distance), 4.0 * Distance, new WebMercatorProjection());

            // Pitch 30° below horizontal, height d/2: the ground at view depth 4d is 3.75d/cos30° away horizontally.
            Assert.AreEqual(-math.degrees(math.atan(0.5 * 0.8660254037844386 / 3.75)), edge.Degrees, 1e-9);
        }

        [Test]
        public void Globe_HighAndOverhead_EdgeIsTheLimb()
        {
            const double height = 2.0e6;
            Angle edge = SkyGradient.MapEdgeElevation(new double3(0.0, height, 0.0), 1.0e9, new SphericalProjection());

            double expected = -math.degrees(math.acos(SphericalProjection.Radius / (SphericalProjection.Radius + height)));
            Assert.AreEqual(expected, edge.Degrees, 1e-6, "the limb is nearer than a far cut 1e9 m away.");
        }

        [TestCase(60.0, 60.0, 0.0)]
        [TestCase(0.0, 60.0, -60.0)]
        [TestCase(80.0, 60.0, 20.0)]
        public void TopElevation_IsPitchPlusHalfFov(double tiltDeg, double fovDeg, double expectedDeg)
        {
            Angle top = SkyGradient.TopElevation(Orbit(tiltDeg, Distance), fovDeg);

            Assert.AreEqual(expectedDeg, top.Degrees, 1e-9);
        }

        [Test]
        public void Globe_LowAndTilted_EdgeIsTheFarCut()
        {
            double3 camera = Orbit(60.0, Distance);
            Angle globe  = SkyGradient.MapEdgeElevation(camera, 4.0 * Distance, new SphericalProjection());
            Angle planar = SkyGradient.MapEdgeElevation(camera, 4.0 * Distance, new WebMercatorProjection());

            Assert.AreEqual(planar.Degrees, globe.Degrees, 1e-9, "a far cut 4 km away is nearer than the limb.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DistanceHazeRangeTests — the fog range as a pure function of the committed camera
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class DistanceHazeRangeTests
    {
        private const double Altitude = 1000.0;
        private const double FovDeg   = 60.0;
        private const double Near     = Altitude * 0.01;

        private static readonly double[] SupportedAspects = { 21.0 / 9.0, 16.0 / 9.0, 4.0 / 3.0, 1.0, 9.0 / 16.0 };

        private static double3 Orbit(double tiltDeg)
        {
            Angle tilt = Angle.FromDegrees(tiltDeg);
            return new double3(0.0, Altitude * tilt.Cos, -Altitude * tilt.Sin);
        }

        private static double Far(double tiltDeg, double aspect, double cap = 4.0)
            => CameraPoseMath.FarClip(Altitude, Angle.FromDegrees(tiltDeg), FovDeg, aspect, cap);

        [Test]
        public void TopDown_HasNoHaze([ValueSource(nameof(SupportedAspects))] double aspect)
        {
            double far = Far(0.0, aspect);
            DistanceHaze.HazeRange range = DistanceHaze.Range(Orbit(0.0), far, Near, FovDeg);

            Assert.IsFalse(range.On, $"top-down at aspect {aspect:F3}: the far cut is off screen, so no haze.");
            Assert.That(range.Start + Near, Is.GreaterThanOrEqualTo(DistanceHaze.DeepestGroundDepth(Orbit(0.0), far, FovDeg)));
        }

        [Test]
        public void Tilt60_FullHazeAtTheFarCut_ClearAtTheLookAt([ValueSource(nameof(SupportedAspects))] double aspect)
        {
            double far = Far(60.0, aspect);
            DistanceHaze.HazeRange range = DistanceHaze.Range(Orbit(60.0), far, Near, FovDeg);

            Assert.IsTrue(range.On);
            Assert.That(range.End, Is.LessThanOrEqualTo(far - Near), "the lit passes measure from the near plane.");
            Assert.That(range.Start, Is.GreaterThanOrEqualTo(Altitude - Near),
                "the look-at (screen centre) and everything nearer is clear air.");
        }

        [Test]
        public void Tilt60_FollowsTheCommittedFar_WhenTheCapChanges()
        {
            double far4 = Far(60.0, 16.0 / 9.0, cap: 4.0);
            double far8 = Far(60.0, 16.0 / 9.0, cap: 8.0);
            Assert.That(far8, Is.GreaterThan(far4 * 1.5), "precondition: the cap binds at tilt 60.");

            DistanceHaze.HazeRange range4 = DistanceHaze.Range(Orbit(60.0), far4, Near, FovDeg);
            DistanceHaze.HazeRange range8 = DistanceHaze.Range(Orbit(60.0), far8, Near, FovDeg);

            Assert.AreEqual(far4 - Near, range4.End, 1e-9);
            Assert.AreEqual(far8 - Near, range8.End, 1e-9);
            Assert.That(range8.Start, Is.GreaterThan(range4.Start), "the haze band moves out with the far plane.");
        }

        [Test]
        public void Range_HasNoStepInTilt([ValueSource(nameof(SupportedAspects))] double aspect)
        {
            // Stops at 85°: above it the corner-ray far plane itself moves several percent per step.
            // A step in the start (not a ramp) would move it by about 0.37 far at once.
            double previousStart = DistanceHaze.Range(Orbit(0.0), Far(0.0, aspect), Near, FovDeg).Start;
            for (int step = 1; step <= 850; step++)
            {
                double tiltDeg = step * 0.1;
                double far = Far(tiltDeg, aspect);
                double start = DistanceHaze.Range(Orbit(tiltDeg), far, Near, FovDeg).Start;
                Assert.That(math.abs(start - previousStart) / far, Is.LessThanOrEqualTo(0.03),
                    $"fog start steps between tilt {tiltDeg - 0.1:F1}° and {tiltDeg:F1}°.");
                previousStart = start;
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DistanceHazeStyleTests — the style `fog-color` → RenderSettings fog writer
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class DistanceHazeStyleTests : BaseTestFixture
    {
        private static StyleSky ParseSky(string json) => StyleSky.Parse(JsonParser.Parse(json).Get("sky"));

        private MapCamera NewCamera(double tiltDeg)
        {
            var camera = Track(new GameObject("TestHazeCamera")).AddComponent<Camera>();
            camera.aspect = 1f;
            var mapCamera = new MapCamera(camera, new MapRenderer.Core.Geo.CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 }, 15.0, 0.0, tiltDeg, 60.0));
            mapCamera.SyncToCamera();
            return mapCamera;
        }

        [Test]
        public void NoSkyBlock_HazesWithSpecDefaultWhite_AtTilt60()
        {
            using var haze = new DistanceHaze();

            haze.ApplyStyle(StyleSky.Parse(null), zoom: 15.0);
            haze.UpdateRange(NewCamera(60.0));

            Assert.IsTrue(RenderSettings.fog, "spec defaults: a style with no `sky` block still hazes.");
            Assert.AreEqual(Color.white, RenderSettings.fogColor, "no `sky` block ⇒ spec default fog-color white.");
            Assert.AreEqual(DistanceHaze.HazeFogMode, RenderSettings.fogMode);
        }

        [Test]
        public void SpecifiedFogColor_IsWritten()
        {
            using var haze = new DistanceHaze();

            haze.ApplyStyle(ParseSky("{\"sky\":{\"fog-color\":\"#ff0000\"}}"), zoom: 15.0);
            haze.UpdateRange(NewCamera(60.0));

            Assert.AreEqual(Color.red, haze.FogColor);
            Assert.AreEqual(Color.red, RenderSettings.fogColor);
        }

        [Test]
        public void TopDown_WritesFogOff()
        {
            using var haze = new DistanceHaze();

            haze.ApplyStyle(StyleSky.Parse(null), zoom: 15.0);
            haze.UpdateRange(NewCamera(0.0));

            Assert.IsFalse(RenderSettings.fog, "top-down the far cut is off screen, so fog stays off.");
        }

        [Test]
        public void SetOverride_WinsOverStyle_UntilRestyleOrReset()
        {
            using var haze = new DistanceHaze();
            MapCamera camera = NewCamera(60.0);
            haze.ApplyStyle(StyleSky.Parse(null), zoom: 15.0);

            haze.SetOverride(false, Color.blue);
            haze.UpdateRange(camera);
            Assert.IsTrue(haze.IsOverridden);
            Assert.IsFalse(RenderSettings.fog, "the override switched the haze off.");

            haze.ResetToStyle();
            haze.UpdateRange(camera);
            Assert.IsFalse(haze.IsOverridden);
            Assert.IsTrue(RenderSettings.fog, "reset re-applies the style, which hazes.");
            Assert.AreEqual(Color.white, RenderSettings.fogColor);

            haze.SetOverride(true, Color.blue);
            haze.ApplyStyle(ParseSky("{\"sky\":{\"fog-color\":\"#00ff00\"}}"), zoom: 15.0);
            haze.UpdateRange(camera);
            Assert.IsFalse(haze.IsOverridden, "a restyle (ApplyStyle) must clear a runtime override.");
            Assert.AreEqual(Color.green, RenderSettings.fogColor);
        }

        [Test]
        public void Dispose_RestoresTheSceneFog()
        {
            bool fog = RenderSettings.fog;
            Color color = RenderSettings.fogColor;
            var haze = new DistanceHaze();
            try
            {
                haze.ApplyStyle(ParseSky("{\"sky\":{\"fog-color\":\"#ff0000\"}}"), zoom: 15.0);
                haze.UpdateRange(NewCamera(60.0));
                Assert.IsTrue(RenderSettings.fog, "precondition: the haze wrote fog.");
            }
            finally { haze.Dispose(); }

            Assert.AreEqual(fog, RenderSettings.fog);
            Assert.AreEqual(color, RenderSettings.fogColor);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LightingRestyleEaseTests — a restyle eases sun, sky and fog over the style transition
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SunLight"/>, <see cref="SkyGradient"/> and <see cref="DistanceHaze"/> ease a restyle over the
    /// same <see cref="StyleTransition"/> and clock as the layer paint. Each writer runs with no scene target,
    /// so it tracks values only. The last test drives the ease through <see cref="MapView.LateUpdate"/>.
    /// </summary>
    [TestFixture]
    public class LightingRestyleEaseTests : BaseTestFixture
    {
        private const string Day =
            "{\"light\":{\"position\":[1.5,90,30],\"color\":\"#ffffff\",\"intensity\":0.5}," +
            "\"sky\":{\"sky-color\":\"#88bbff\",\"horizon-color\":\"#ffffff\",\"fog-color\":\"#ffffff\"}}";
        private const string Night =
            "{\"light\":{\"position\":[1.5,150,60],\"color\":\"#203060\",\"intensity\":0.1}," +
            "\"sky\":{\"sky-color\":\"#070a14\",\"horizon-color\":\"#171b26\",\"fog-color\":\"#171b26\"}}";
        private const string Red =
            "{\"light\":{\"position\":[1.5,30,45],\"color\":\"#ff0000\",\"intensity\":0.3}," +
            "\"sky\":{\"sky-color\":\"#ff0000\",\"horizon-color\":\"#00ff00\",\"fog-color\":\"#0000ff\"}}";

        private static readonly StyleTransition Eased = StyleTransition.Default;
        private static readonly double Mid = StyleTransition.Default.DurationSeconds / 2.0;

        private Writers _writers;

        [SetUp]
        public void CreateWriters() => _writers = new Writers();

        [TearDown]
        public void DisposeWriters() => _writers.Dispose();

        private void Apply(string json, in StyleTransition transition, double nowSeconds)
            => _writers.Apply(json, transition, nowSeconds);

        private void Advance(double nowSeconds) => _writers.Advance(nowSeconds);

        private Written Now() => _writers.Snapshot();

        /// <summary>The values a fresh writer set writes on a snap to <paramref name="json"/>.</summary>
        private static Written Snapped(string json)
        {
            using var reference = new Writers();
            reference.Apply(json, StyleTransition.Instant, 0.0);
            return reference.Snapshot();
        }

        /// <summary>The three writers with no scene target, so each tracks its values only.</summary>
        private sealed class Writers : System.IDisposable
        {
            public SunLight     Sun  { get; } = new SunLight(null);
            public SkyGradient  Sky  { get; } = new SkyGradient(null);
            public DistanceHaze Haze { get; } = new DistanceHaze();

            public void Apply(string json, in StyleTransition transition, double nowSeconds)
            {
                JsonValue root = JsonParser.Parse(json);
                Sun.ApplyStyle(StyleLight.Parse(root.Get("light")), 0.0, transition, nowSeconds);
                StyleSky sky = StyleSky.Parse(root.Get("sky"));
                Sky.ApplyStyle(sky, 0.0, transition, nowSeconds);
                Haze.ApplyStyle(sky, 0.0, transition, nowSeconds);
            }

            public void Advance(double nowSeconds)
            {
                Sun.Advance(nowSeconds);
                Sky.Advance(nowSeconds);
                Haze.Advance(nowSeconds);
            }

            public Written Snapshot() => new Written(Sun.Azimuth, Sun.Polar, Sun.Color, Sun.Intensity,
                                                     Sky.SkyColor, Sky.HorizonColor, Haze.FogColor);

            public void Dispose()
            {
                Sky.Dispose();
                Haze.Dispose();
            }
        }

        /// <summary>Every value the three writers last wrote. Struct equality compares each field exactly.</summary>
        private readonly struct Written
        {
            public readonly Angle Azimuth, Polar;
            public readonly Color SunColor, Sky, Horizon, Fog;
            public readonly float Intensity;

            public Written(Angle azimuth, Angle polar, Color sunColor, float intensity, Color sky, Color horizon,
                           Color fog)
            {
                Azimuth = azimuth; Polar = polar; SunColor = sunColor; Intensity = intensity;
                Sky = sky; Horizon = horizon; Fog = fog;
            }

            public override string ToString()
                => $"az {Azimuth} polar {Polar} sun {SunColor} x{Intensity} sky {Sky} horizon {Horizon} fog {Fog}";
        }

        private static Vector3 Direction(Written w) => SunLight.Rotation(w.Azimuth, w.Polar) * Vector3.forward;

        private static void AssertStrictlyBetween(Color from, Color to, Color value, string what)
        {
            int moving = 0;
            for (int c = 0; c < 3; c++)
            {
                if (from[c] == to[c]) continue;
                moving++;
                Assert.Greater(value[c], math.min(from[c], to[c]), $"{what}[{c}] must sit strictly between old and new.");
                Assert.Less(value[c], math.max(from[c], to[c]), $"{what}[{c}] must sit strictly between old and new.");
            }
            Assert.Greater(moving, 0, $"fixture: {what} must differ between the two styles.");
        }

        [Test]
        public void MidTransition_EveryValue_SitsStrictlyBetweenOldAndNew()
        {
            Apply(Day, Eased, 0.0);
            Written day = Now();
            Assert.AreEqual(Snapped(Day), day, "the first style must snap, even with an eased transition.");
            Apply(Night, Eased, 0.0);
            Written night = Snapped(Night);

            Advance(Mid);

            Written mid = Now();
            AssertStrictlyBetween(day.SunColor, night.SunColor, mid.SunColor, "sun colour");
            AssertStrictlyBetween(day.Sky, night.Sky, mid.Sky, "sky-color");
            AssertStrictlyBetween(day.Horizon, night.Horizon, mid.Horizon, "horizon-color");
            AssertStrictlyBetween(day.Fog, night.Fog, mid.Fog, "fog-color");
            Assert.Less(mid.Intensity, day.Intensity, "intensity must have left the old value.");
            Assert.Greater(mid.Intensity, night.Intensity, "intensity must not have reached the new value.");

            Vector3 dayDirection = Direction(day);
            Vector3 nightDirection = Direction(night);
            Vector3 midDirection = Direction(mid);
            float span = Vector3.Angle(dayDirection, nightDirection);
            Assert.Greater(Vector3.Angle(dayDirection, midDirection), 1f, "the sun must have left the old direction.");
            Assert.Greater(Vector3.Angle(nightDirection, midDirection), 1f, "the sun must not have reached the new direction.");
            Assert.Less(Vector3.Angle(dayDirection, midDirection), span, "the sun must turn toward the new direction.");
            Assert.Less(Vector3.Angle(nightDirection, midDirection), span, "the sun must turn toward the new direction.");

            Assert.That(() => Advance(Mid * 1.5), Is.Not.AllocatingGCMemory(),
                "a mid-transition frame must not allocate.");
        }

        [Test]
        public void TransitionEnd_EqualsTheTargetExactly()
        {
            Apply(Day, Eased, 0.0);
            Apply(Night, Eased, 0.0);

            Advance(Mid);
            Assert.AreNotEqual(Snapped(Night), Now(), "fixture: the mid frame must not be the target yet.");
            Advance(Eased.DurationSeconds);

            Assert.AreEqual(Snapped(Night), Now(), "the last frame must write the target itself, not a lerp near it.");
            Assert.IsFalse(_writers.Sun.IsTransitioning || _writers.Sky.IsTransitioning || _writers.Haze.IsTransitioning,
                "the ease must end at its duration.");
        }

        [Test]
        public void InstantTransition_Snaps_AndStopsARunningEase()
        {
            Apply(Day, Eased, 0.0);
            Apply(Night, Eased, 0.0);
            Advance(Mid);

            Apply(Red, StyleTransition.Instant, Mid);

            Assert.AreEqual(Snapped(Red), Now(), "an instant transition must snap in the same call.");
            Advance(Mid * 1.5);
            Advance(Eased.DurationSeconds * 2.0);
            Assert.AreEqual(Snapped(Red), Now(), "the ease that was running must not resume after an instant snap.");
        }

        [Test]
        public void RestyleMidTransition_ContinuesFromTheCurrentValue()
        {
            Apply(Day, Eased, 0.0);
            Apply(Night, Eased, 0.0);
            Advance(Mid);
            Written mid = Now();
            Assert.AreNotEqual(Snapped(Night), mid, "fixture: the first ease must still be running.");

            Apply(Day, Eased, Mid);
            Advance(Mid);

            Assert.AreEqual(mid, Now(),
                "the second ease must start from the value on screen, not from the old target (night).");
            Advance(Mid + Eased.DurationSeconds);
            Assert.AreEqual(Snapped(Day), Now(), "the second ease must end on its own target.");
        }

        [Test]
        public void OverrideDuringTransition_WinsImmediately_AndStays()
        {
            Apply(Day, Eased, 0.0);
            Apply(Night, Eased, 0.0);
            Advance(Mid);

            _writers.Sun.SetOverride(Angle.FromDegrees(10.0), Angle.FromDegrees(20.0), Color.green, 3f);
            _writers.Sky.SetOverride(Color.red, Color.blue);
            _writers.Haze.SetOverride(true, Color.yellow);
            Written overridden = Now();
            Assert.AreEqual(new Written(Angle.FromDegrees(10.0), Angle.FromDegrees(20.0), Color.green, 3f,
                                        Color.red, Color.blue, Color.yellow), overridden, "an override must show in the same call.");

            Advance(Mid * 1.5);
            Advance(Eased.DurationSeconds * 2.0);
            Assert.AreEqual(overridden, Now(), "the ease must not overwrite an override.");
        }

        /// <summary>The ease runs through <see cref="MapView.SetStyle(StyleDocument,string,System.Threading.CancellationToken)"/>
        /// and <see cref="MapView.LateUpdate"/> on the view's own transition clock.</summary>
        [Test]
        public void MapView_Restyle_EasesTheSkyAndFog_ThroughLateUpdate()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                double now = 0.0;
                view.View.NowSecondsOverride = () => now;
                view.View.StyleTransition = Eased;
                view.View.SetSkyTarget(null);
                view.View.EnableHaze();
                RestyleHarness.SpinToCompleted(view.SetStyle(MapStyle(Day), "day"));
                view.LateUpdate();
                Color daySky = view.View.SkyGradient.SkyColor, dayFog = view.View.DistanceHaze.FogColor;

                RestyleHarness.SpinToCompleted(view.SetStyle(MapStyle(Night), "night"));
                now = Mid;
                view.LateUpdate();
                AssertStrictlyBetween(daySky, Snapped(Night).Sky, view.View.SkyGradient.SkyColor, "sky-color");
                AssertStrictlyBetween(dayFog, Snapped(Night).Fog, view.View.DistanceHaze.FogColor, "fog-color");

                now = Eased.DurationSeconds;
                view.LateUpdate();
                Assert.AreEqual(Snapped(Night).Sky, view.View.SkyGradient.SkyColor, "the view must settle the sky.");
                Assert.AreEqual(Snapped(Night).Fog, view.View.DistanceHaze.FogColor, "the view must settle the fog.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>A one-fill style over the sample tile with <paramref name="lightAndSky"/>'s root keys.</summary>
        private static StyleDocument MapStyle(string lightAndSky) => StyleParser.Parse(
            "{\"version\":8,\"name\":\"T\"," + lightAndSky.Substring(1, lightAndSky.Length - 2) + "," +
            "\"sources\":{\"s\":{\"type\":\"vector\",\"tiles\":[\"https://example.invalid/{z}/{x}/{y}.pbf\"]}}," +
            "\"layers\":[{\"id\":\"fill0\",\"type\":\"fill\",\"source\":\"s\",\"source-layer\":\"countries\"}]}");
    }
}
