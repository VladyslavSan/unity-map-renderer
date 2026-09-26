using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// Applies the style's root <c>light</c> to a scene directional <see cref="Light"/>: rotation from
    /// <c>light.position</c> (azimuth/polar, anchor always <c>"map"</c>), color and intensity. A runtime
    /// override (<see cref="SetOverride"/>) replaces the style values on the same light until
    /// <see cref="ResetToStyle"/> runs or the next <see cref="ApplyStyle"/> (a restyle always clears it).
    /// A restyle eases from the current values over the style transition; <see cref="Advance"/> moves it.
    /// </summary>
    internal sealed class SunLight
    {
        private readonly Light _light;
        private StyleLight     _style;
        private double         _lastZoom;
        private StyleEase      _ease;
        private (Angle azimuth, Angle polar, Color color, float intensity) _from, _to;

        public SunLight(Light light) => _light = light;

        /// <summary>True while a runtime override is showing instead of the style's own values.</summary>
        public bool IsOverridden { get; private set; }

        /// <summary>The azimuth last written to the light (style- or override-derived).</summary>
        public Angle Azimuth { get; private set; }

        /// <summary>The polar angle (from zenith) last written to the light.</summary>
        public Angle Polar { get; private set; }

        /// <summary>The color last written to the light.</summary>
        public Color Color { get; private set; } = UnityEngine.Color.white;

        /// <summary>The intensity last written to the light.</summary>
        public float Intensity { get; private set; } = 1f;

        /// <summary>True while a restyle ease is still moving the light.</summary>
        public bool IsTransitioning => _ease.IsActive;

        /// <summary>
        /// Applies <paramref name="style"/>'s light at <paramref name="zoom"/> and clears any runtime
        /// override. A style with no <c>light</c> block reproduces today's default light exactly: azimuth
        /// 210°, polar 30°, white, intensity 1.0. The first style, and an instant transition, snap.
        /// </summary>
        public void ApplyStyle(StyleLight style, double zoom, in StyleTransition transition = default,
                               double nowSeconds = 0.0)
        {
            bool first    = _style == null;
            _style        = style;
            _lastZoom     = zoom;
            IsOverridden  = false;
            LightPosition position = style.Position.Evaluate(zoom);
            _from = (Azimuth, Polar, Color, Intensity);
            _to   = (position.Azimuthal, position.Polar,
                     ZoomStyleApplier.ToUnityColor(style.Color.Evaluate(zoom)),
                     IntensityToUnity(style.Intensity.Evaluate(zoom)));
            _ease.Arm(first ? default : transition, nowSeconds);
            if (!_ease.IsActive) Write(_to.azimuth, _to.polar, _to.color, _to.intensity);
        }

        /// <summary>Writes this frame's eased light. A no-op when no ease is running.</summary>
        public void Advance(double nowSeconds)
        {
            if (!_ease.IsActive) return;
            float weight = _ease.Step(nowSeconds);
            if (!_ease.IsActive) { Write(_to.azimuth, _to.polar, _to.color, _to.intensity); return; }
            Write(Angle.LerpShortest(_from.azimuth, _to.azimuth, weight),
                  Angle.FromDegrees(math.lerp(_from.polar.Degrees, _to.polar.Degrees, weight)),
                  StyleEase.Mix(_from.color, _to.color, weight),
                  math.lerp(_from.intensity, _to.intensity, weight));
        }

        /// <summary>Overrides azimuth, polar angle, color and intensity on the live light, on top of the
        /// last applied style, until <see cref="ResetToStyle"/> or the next <see cref="ApplyStyle"/>.</summary>
        /// <param name="intensity">Unity light intensity units (not the style's [0,1] scale) — the
        /// caller already resolved it, the same way <see cref="IntensityToUnity"/> resolves a style's.</param>
        public void SetOverride(Angle azimuth, Angle polar, Color color, float intensity)
        {
            _ease.Stop();
            IsOverridden = true;
            Write(azimuth, polar, color, intensity);
        }

        /// <summary>Clears a runtime override and re-applies the last style light. A no-op before the
        /// first <see cref="ApplyStyle"/>.</summary>
        public void ResetToStyle()
        {
            if (_style != null) ApplyStyle(_style, _lastZoom, StyleTransition.Instant);
        }

        private void Write(Angle azimuth, Angle polar, Color color, float intensity)
        {
            Azimuth = azimuth; Polar = polar; Color = color; Intensity = intensity;
            if (_light == null) return;
            _light.transform.rotation = Rotation(azimuth, polar);
            _light.color              = color;
            _light.intensity          = intensity;
        }

        /// <summary>
        /// Euler rotation for a light position's azimuth (clockwise from north) and polar angle (from
        /// zenith), anchor <c>"map"</c>. At the style's own defaults (azimuth 210°, polar 30°) this is
        /// exactly <c>Euler(60, 30, 0)</c> — today's bootstrap light.
        /// </summary>
        internal static Quaternion Rotation(Angle azimuth, Angle polar)
            => Quaternion.Euler((float)(90.0 - polar.Degrees), (float)(azimuth.Degrees - 180.0), 0f);

        /// <summary>Maps style <c>light-intensity</c> [0,1] to the Unity light intensity: linear, so the
        /// spec default 0.5 resolves to exactly today's intensity 1.0.</summary>
        internal static float IntensityToUnity(float styleIntensity) => styleIntensity * 2f;
    }
}
