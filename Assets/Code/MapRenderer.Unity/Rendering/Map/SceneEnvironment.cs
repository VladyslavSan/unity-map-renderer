using System;
using UnityEngine;
using MapRenderer.Unity.Style;
using MapRenderer.Unity.Rendering.Layers;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// Owns the sun, sky and haze writers and their per-frame order. The sun is required at construction, so
    /// the sky and the haze can always read its intensity (<see cref="AmbientBrightnessResponse"/>) once
    /// either opts in.
    /// </summary>
    internal sealed class SceneEnvironment : IDisposable
    {
        /// <summary>Wires the sun writer to <paramref name="sun"/> at once. Sky and haze start disabled.</summary>
        public SceneEnvironment(Light sun) => Sun = new SunLight(sun);

        /// <summary>The sun light writer, wired at construction.</summary>
        public SunLight Sun { get; }

        /// <summary>The sky gradient writer, wired by <see cref="EnableSky"/>. Null until then.</summary>
        public SkyGradient Sky { get; private set; }

        /// <summary>The distance haze writer, created by <see cref="EnableHaze"/>. Null until then.</summary>
        public DistanceHaze Haze { get; private set; }

        /// <summary>Paints the style's sky behind <paramref name="camera"/> (<c>MapHost</c> passes
        /// <c>Camera.main</c>, which can be null). A null camera tracks colours only. Disposes any previous
        /// sky.</summary>
        public void EnableSky(Camera camera)
        {
            Sky?.Dispose();
            Sky = new SkyGradient(camera);
        }

        /// <summary>Starts writing the style's haze to the process-global <c>RenderSettings</c> fog every
        /// frame. Opt-in, so an environment that never enables it leaves the fog alone.</summary>
        public void EnableHaze()
        {
            Haze?.Dispose();
            Haze = new DistanceHaze();
        }

        /// <summary>Applies <paramref name="light"/>/<paramref name="sky"/> to every wired writer, clearing
        /// any runtime override. Re-applied on every restyle, in-place or full rebuild.</summary>
        public void ApplyStyle(StyleLight light, StyleSky sky, double zoom, in StyleTransition transition,
                               double nowSeconds)
        {
            Sun.ApplyStyle(light, zoom, transition, nowSeconds);
            Sky?.ApplyStyle(sky, zoom, transition, nowSeconds);
            Haze?.ApplyStyle(sky, zoom, transition, nowSeconds);
        }

        /// <summary>Moves the sun first, then the sky and the haze at the sun's own intensity, so both read
        /// this frame's eased value. Call once per frame, after <see cref="MapCamera.SyncToCamera"/>.</summary>
        public void Update(double nowSeconds, MapCamera camera)
        {
            Sun.Update(nowSeconds);
            Sky?.Update(nowSeconds, camera, Sun.Intensity);
            Haze?.Update(nowSeconds, camera, Sun.Intensity);
        }

        /// <summary>Disposes the sky and the haze (the sun holds no disposable resource).</summary>
        public void Dispose()
        {
            Sky?.Dispose();
            Haze?.Dispose();
        }
    }
}
