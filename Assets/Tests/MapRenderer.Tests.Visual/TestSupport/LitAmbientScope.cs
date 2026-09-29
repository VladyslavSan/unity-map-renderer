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
    /// <para>Non-obvious why: without it a lit render reads whatever ambient probe, ambient mode and lights the
    /// earlier tests left behind. That shifted a tilted cap's reach by over a pixel at its near end, which sees a
    /// different view angle than the plateau it is measured against.</para>
    /// </summary>
    internal sealed class LitAmbientScope : IDisposable
    {
        private readonly int         _quality;
        private readonly AmbientMode _ambientMode;
        private readonly Color       _ambientLight;

        /// <summary>The directional light this scope created.</summary>
        public GameObject LightGameObject { get; }

        public LitAmbientScope()
        {
            _quality      = QualitySettings.GetQualityLevel();
            _ambientMode  = RenderSettings.ambientMode;
            _ambientLight = RenderSettings.ambientLight;

            QualitySettings.SetQualityLevel(0, false);
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

            LightGameObject = new GameObject("LitAmbientScope_DirLight");
            LightGameObject.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
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
