using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Style;
using MapRenderer.Unity.Rendering.Layers;
using MapRenderer.Unity.Interpolation;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// Applies the style's root <c>light</c> to a scene directional <see cref="Light"/>: rotation from
    /// <c>light.position</c> (azimuth/polar, anchor always <c>"map"</c>), color and intensity. A runtime
    /// override (<see cref="SetOverride"/>) replaces the style values on the same light until
    /// <see cref="ResetToStyle"/> runs or the next <see cref="ApplyStyle"/> (a restyle always clears it).
    /// A restyle eases from the current values over the style transition; <see cref="Update"/> moves it.
    /// </summary>
    internal sealed class SunLight
    {
        /// <summary>One interpolation endpoint: the light's azimuth, polar angle, color and intensity.</summary>
        private readonly struct LightState : IInterpolatable<LightState>
        {
            public Angle Azimuth   { get; init; }
            public Angle Polar     { get; init; }
            public Color Color     { get; init; }
            public float Intensity { get; init; }

            public LightState Interpolate(in LightState to, float weight) => new LightState
            {
                Azimuth   = Angle.LerpShortest(Azimuth, to.Azimuth, weight),
                Polar     = Angle.FromDegrees(math.lerp(Polar.Degrees, to.Polar.Degrees, weight)),
                Color     = ColorMix.Lerp(Color, to.Color, weight),
                Intensity = math.lerp(Intensity, to.Intensity, weight),
            };
        }

        private readonly Light        _light;
        private StyleLight            _style;
        private double                _lastZoom;
        private Smoothstep<LightState> _interpolation;

        /// <summary>The default light: white, <see cref="DefaultIntensity"/>, azimuth/polar zero. A light with
        /// no style yet reads this from the properties; <paramref name="light"/> itself is not written until
        /// the first <see cref="ApplyStyle"/> or <see cref="SetOverride"/>.</summary>
        public SunLight(Light light)
        {
            _light = light;
            _interpolation.Set(new LightState { Color = UnityEngine.Color.white, Intensity = DefaultIntensity });
        }

        /// <summary>True while a runtime override is showing instead of the style's own values.</summary>
        public bool IsOverridden { get; private set; }

        /// <summary>The azimuth last written to the light (style- or override-derived).</summary>
        public Angle Azimuth => _interpolation.Current.Azimuth;

        /// <summary>The polar angle (from zenith) last written to the light.</summary>
        public Angle Polar => _interpolation.Current.Polar;

        /// <summary>The color last written to the light.</summary>
        public Color Color => _interpolation.Current.Color;

        /// <summary>Unity <c>Light.intensity</c> at <c>light-intensity</c>'s own spec default (0.5) — see
        /// <see cref="IntensityToUnity"/>. A light with no style yet reads this.</summary>
        internal const float DefaultIntensity = 1f;

        /// <summary>The intensity last written to the light.</summary>
        public float Intensity => _interpolation.Current.Intensity;

        /// <summary>True while a restyle interpolation is still moving the light.</summary>
        public bool IsTransitioning => _interpolation.IsActive;

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
            var target = new LightState
            {
                Azimuth   = position.Azimuthal,
                Polar     = position.Polar,
                Color     = ZoomStyleApplier.ToUnityColor(style.Color.Evaluate(zoom)),
                Intensity = IntensityToUnity(style.Intensity.Evaluate(zoom)),
            };
            double delaySeconds    = first ? 0.0 : transition.DelaySeconds;
            double durationSeconds = first ? 0.0 : transition.DurationSeconds;
            _interpolation.Start(target, delaySeconds, durationSeconds, nowSeconds);
            if (!_interpolation.IsActive) WriteToLight(_interpolation.Current);
        }

        /// <summary>Writes this frame's interpolated light. A no-op when no interpolation is running.</summary>
        public void Update(double nowSeconds)
        {
            if (_interpolation.Update(nowSeconds)) WriteToLight(_interpolation.Current);
        }

        /// <summary>Overrides azimuth, polar angle, color and intensity on the live light, on top of the
        /// last applied style, until <see cref="ResetToStyle"/> or the next <see cref="ApplyStyle"/>.</summary>
        /// <param name="intensity">Unity light intensity units (not the style's [0,1] scale) — the
        /// caller already resolved it, the same way <see cref="IntensityToUnity"/> resolves a style's.</param>
        public void SetOverride(Angle azimuth, Angle polar, Color color, float intensity)
        {
            IsOverridden = true;
            _interpolation.Set(new LightState { Azimuth = azimuth, Polar = polar, Color = color, Intensity = intensity });
            WriteToLight(_interpolation.Current);
        }

        /// <summary>Clears a runtime override and re-applies the last style light. A no-op before the
        /// first <see cref="ApplyStyle"/>.</summary>
        public void ResetToStyle()
        {
            if (_style != null) ApplyStyle(_style, _lastZoom, StyleTransition.Instant);
        }

        private void WriteToLight(LightState state)
        {
            if (_light == null) return;
            _light.transform.rotation = Rotation(state.Azimuth, state.Polar);
            _light.color              = state.Color;
            _light.intensity          = state.Intensity;
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
