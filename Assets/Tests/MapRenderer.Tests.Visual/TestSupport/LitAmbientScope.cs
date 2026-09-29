// Unity EditMode only (not in Tools/core-tests) — pins the lighting a lit render depends on.

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Pins the process-global lighting the lit map shaders read: quality level 0, a flat 0.9 ambient and one
    /// directional light. Dispose restores what it replaced.
    ///
    /// <para>Non-obvious why: the lights in the scene differ between runs. The batch runner's default directional
    /// light exists when a test runs alone and is gone after <c>SceneIntegrityTests</c>. A lit render shaded
    /// differently in each case, which moved a tilted cap's reach by over a pixel at its near end.</para>
    /// </summary>
    internal sealed class LitAmbientScope : IDisposable
    {
        private readonly int         _quality;
        private readonly AmbientMode _ambientMode;
        private readonly Color       _ambientLight;

        /// <summary>The directional light this scope created.</summary>
        public GameObject LightGameObject { get; }

        /// <param name="lightRotation">Direction of the light; the default is a 60° pitch turned 30° in yaw.</param>
        public LitAmbientScope(Quaternion? lightRotation = null)
        {
            _quality      = QualitySettings.GetQualityLevel();
            _ambientMode  = RenderSettings.ambientMode;
            _ambientLight = RenderSettings.ambientLight;

            QualitySettings.SetQualityLevel(0, false);
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

            LightGameObject = new GameObject("LitAmbientScope_DirLight");
            LightGameObject.transform.rotation = lightRotation ?? Quaternion.Euler(60f, 30f, 0f);
            var light = LightGameObject.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;
        }

        public void Dispose()
        {
            if (LightGameObject != null) UnityEngine.Object.DestroyImmediate(LightGameObject);
            QualitySettings.SetQualityLevel(_quality, false);
            RenderSettings.ambientMode  = _ambientMode;
            RenderSettings.ambientLight = _ambientLight;
        }
    }
}
