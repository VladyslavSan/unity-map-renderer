// Unity EditMode only — tests for MapHost.EnsureDirectionalLight: it creates a directional light when
// the scene (visibly) has none, and does NOT add a second when one already exists. The light is now
// UNCONDITIONAL across render modes — unlike the ambient probe (EnvironmentLightingTests), which stays
// unlit-gated. The unlit fill-extrusion twin reads the main light's DIRECTION for a half-Lambert so 3D
// buildings don't render as flat solid blocks (see FillExtrusion_UnlitForwardPass.hlsl); fills and lines
// ignore it. This is the deliberate walk-back of the earlier "unlit creates no light" behaviour.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MapHost = MapRenderer.App.MapHost;

namespace MapRenderer.Tests.MapViews
{
    /// <summary>
    /// Behaviour tests for <see cref="MapHost.EnsureDirectionalLight"/>: unconditional creation when
    /// the scene lacks a directional light, and idempotency when one is already present. The light exists
    /// in BOTH render modes because the unlit fill-extrusion twin consumes its direction — only the ambient
    /// probe (<c>EnvironmentLightingTests</c>) is unlit-gated.
    /// </summary>
    [TestFixture]
    public class DirectionalLightBootstrapTests
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
            GameObject tempGo = null;
            try
            {
                tempGo = new GameObject("TempDirectional");
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
                if (tempGo != null) Object.DestroyImmediate(tempGo);
                RestoreLights(hidden);
            }
        }
    }
}
