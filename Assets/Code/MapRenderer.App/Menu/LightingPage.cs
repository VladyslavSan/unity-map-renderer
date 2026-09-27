using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App.Menu
{
    /// <summary>
    /// Lighting page: runtime overrides for the sun, the sky and the haze, on top of the live style's
    /// <c>light</c> and <c>sky</c> blocks (<see cref="SunLight.SetOverride"/>, <see cref="SkyGradient.SetOverride"/>,
    /// <see cref="DistanceHaze.SetOverride"/>, reached through <see cref="MapViewComponent.Environment"/>).
    /// Controls seed from the live state on the first draw, on every restyle, and again when a restyle's ease
    /// ends. "Reset to style" clears all three overrides and reverts to the style's own values.
    /// </summary>
    internal sealed class LightingPage : IMenuPage
    {
        // One colour control's state, as HSV.
        private struct Hsv
        {
            public float H;
            public float S;
            public float V;
        }

        // The StyleId the sliders were last seeded from; null before the first draw. Re-seeding on a
        // StyleId change catches both the first open and a restyle happening while this page is open.
        private string _seededStyleId;

        // True when the last seed ran while a restyle was still easing, so the controls hold a mid-ease value.
        private bool _seededMidTransition;

        private float _azimuthDeg;
        private float _elevationDeg;
        private float _intensity;
        private Hsv _sun;

        // The values last pushed through SunLight.SetOverride — compared against the live fields each draw so
        // Apply runs only when a control actually moved, not on every Layout/Repaint pass (which would
        // fight Reset and re-assert a stale override under a live restyle).
        private float _appliedAzimuthDeg;
        private float _appliedElevationDeg;
        private float _appliedIntensity;
        private Hsv _appliedSun;

        // Sky colours as HSV, with their own applied snapshot: a sky change must not re-push the sun.
        private Hsv _sky;
        private Hsv _horizon;
        private Hsv _appliedSky;
        private Hsv _appliedHorizon;

        // Haze switch and fog colour, with their own applied snapshot.
        private bool _hazeOn;
        private bool _appliedHazeOn;
        private Hsv _fog;
        private Hsv _appliedFog;

        /// <inheritdoc/>
        public string Title => "Lighting";

        /// <inheritdoc/>
        public void Draw(MenuOverlay menu)
        {
            MapViewComponent map = menu.MapComponent;
            if (map == null)
            {
                GUILayout.Label("No live MapView (enter Play mode).");
                return;
            }

            if (map.StyleId != _seededStyleId || (_seededMidTransition && !IsTransitioning(map))) Seed(map);

            SceneEnvironment env = map.Environment;
            GUILayout.Label(env != null && env.Sun.IsOverridden ? "Sun (overridden)" : "Sun");
            GUILayout.Label($"Azimuth: {_azimuthDeg:F0}°");
            _azimuthDeg = GUILayout.HorizontalSlider(_azimuthDeg, 0f, 360f);
            GUILayout.Label($"Elevation: {_elevationDeg:F0}°");
            _elevationDeg = GUILayout.HorizontalSlider(_elevationDeg, -90f, 90f);
            GUILayout.Label($"Intensity: {_intensity:F2}");
            _intensity = GUILayout.HorizontalSlider(_intensity, 0f, 4f);

            GUILayout.Space(6f);
            DrawColorControl("Color", ref _sun);

            GUILayout.Space(6f);
            GUILayout.Label("Presets");
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Noon"))        ApplyPreset(elevation: 75f, h: 0.14f, s: 0.05f, v: 1.0f, intensity: 1.2f);
                if (GUILayout.Button("Golden hour"))  ApplyPreset(elevation: 8f,  h: 0.08f, s: 0.65f, v: 1.0f, intensity: 0.9f);
                if (GUILayout.Button("Dusk"))         ApplyPreset(elevation: 2f,  h: 0.03f, s: 0.8f,  v: 0.9f, intensity: 0.4f);
                if (GUILayout.Button("Night"))        ApplyPreset(elevation: -20f, h: 0.62f, s: 0.5f, v: 0.5f, intensity: 0.05f);
            }

            GUILayout.Space(10f);
            GUILayout.Label(env?.Sky != null && env.Sky.IsOverridden ? "Sky (overridden)" : "Sky");
            DrawColorControl("Sky", ref _sky);
            DrawColorControl("Horizon", ref _horizon);

            GUILayout.Space(10f);
            GUILayout.Label(env?.Haze != null && env.Haze.IsOverridden ? "Haze (overridden)" : "Haze");
            _hazeOn = GUILayout.Toggle(_hazeOn, "Haze on");
            DrawColorControl("Fog", ref _fog);

            GUILayout.Space(6f);
            if (GUILayout.Button("Reset to style"))
            {
                env?.Sun.ResetToStyle();
                env?.Sky?.ResetToStyle();
                env?.Haze?.ResetToStyle();
                Seed(map); // controls match the now-live style; nothing pending to Apply below
                return;
            }

            if (HasPendingChange())
            {
                env?.Sun.SetOverride(Angle.FromDegrees(_azimuthDeg), Angle.FromDegrees(90.0 - _elevationDeg),
                    Color.HSVToRGB(_sun.H, _sun.S, _sun.V), _intensity);
                MarkApplied();
            }

            if (HasPendingSkyChange())
            {
                env?.Sky?.SetOverride(Color.HSVToRGB(_sky.H, _sky.S, _sky.V),
                                      Color.HSVToRGB(_horizon.H, _horizon.S, _horizon.V));
                MarkSkyApplied();
            }

            if (HasPendingHazeChange())
            {
                env?.Haze?.SetOverride(_hazeOn, Color.HSVToRGB(_fog.H, _fog.S, _fog.V));
                MarkHazeApplied();
            }
        }

        // Reads the light's, sky's and haze's CURRENT state into the controls, so a first open — or a restyle that lands
        // while this page is open — shows what's actually lit rather than a stale or default guess.
        private void Seed(MapViewComponent map)
        {
            _seededStyleId       = map.StyleId;
            _seededMidTransition = IsTransitioning(map);
            SceneEnvironment env = map.Environment;

            SkyGradient sky = env?.Sky;
            if (sky != null)
            {
                Color.RGBToHSV(sky.SkyColor, out _sky.H, out _sky.S, out _sky.V);
                Color.RGBToHSV(sky.HorizonColor, out _horizon.H, out _horizon.S, out _horizon.V);
                MarkSkyApplied();
            }

            DistanceHaze haze = env?.Haze;
            if (haze != null)
            {
                _hazeOn = haze.Enabled;
                Color.RGBToHSV(haze.FogColor, out _fog.H, out _fog.S, out _fog.V);
                MarkHazeApplied();
            }

            SunLight sun = env?.Sun;
            if (sun == null) return;

            _azimuthDeg   = (float)sun.Azimuth.NormalizedDegrees().Degrees;
            _elevationDeg = (float)(90.0 - sun.Polar.Degrees);
            _intensity    = sun.Intensity;
            Color.RGBToHSV(sun.Color, out _sun.H, out _sun.S, out _sun.V);
            MarkApplied();
        }

        /// <summary>True while a restyle still eases the sun, the sky or the haze.</summary>
        private static bool IsTransitioning(MapViewComponent map)
        {
            SceneEnvironment env = map.Environment;
            return env != null && (env.Sun.IsTransitioning || env.Sky?.IsTransitioning == true
                                 || env.Haze?.IsTransitioning == true);
        }

        private bool HasPendingChange()
            => _azimuthDeg != _appliedAzimuthDeg || _elevationDeg != _appliedElevationDeg
            || _intensity != _appliedIntensity
            || _sun.H != _appliedSun.H || _sun.S != _appliedSun.S || _sun.V != _appliedSun.V;

        private void MarkApplied()
        {
            _appliedAzimuthDeg = _azimuthDeg; _appliedElevationDeg = _elevationDeg; _appliedIntensity = _intensity;
            _appliedSun = _sun;
        }

        private bool HasPendingSkyChange()
            => _sky.H != _appliedSky.H || _sky.S != _appliedSky.S || _sky.V != _appliedSky.V
            || _horizon.H != _appliedHorizon.H || _horizon.S != _appliedHorizon.S || _horizon.V != _appliedHorizon.V;

        private void MarkSkyApplied()
        {
            _appliedSky = _sky;
            _appliedHorizon = _horizon;
        }

        private bool HasPendingHazeChange()
            => _hazeOn != _appliedHazeOn
            || _fog.H != _appliedFog.H || _fog.S != _appliedFog.S || _fog.V != _appliedFog.V;

        private void MarkHazeApplied()
        {
            _appliedHazeOn = _hazeOn;
            _appliedFog = _fog;
        }

        // Small, reusable H/S/V control — a swatch plus three sliders.
        private static void DrawColorControl(string label, ref Hsv hsv)
        {
            Color color = Color.HSVToRGB(hsv.H, hsv.S, hsv.V);
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(56f));
                var prevColor = GUI.color;
                GUI.color = color;
                GUILayout.Box(string.Empty, GUILayout.Width(24f), GUILayout.Height(16f));
                GUI.color = prevColor;
            }
            GUILayout.Label($"H: {hsv.H:F2}");
            hsv.H = GUILayout.HorizontalSlider(hsv.H, 0f, 1f);
            GUILayout.Label($"S: {hsv.S:F2}");
            hsv.S = GUILayout.HorizontalSlider(hsv.S, 0f, 1f);
            GUILayout.Label($"V: {hsv.V:F2}");
            hsv.V = GUILayout.HorizontalSlider(hsv.V, 0f, 1f);
        }

        private void ApplyPreset(float elevation, float h, float s, float v, float intensity)
        {
            _elevationDeg = elevation;
            _sun.H = h; _sun.S = s; _sun.V = v;
            _intensity = intensity;
        }
    }
}
