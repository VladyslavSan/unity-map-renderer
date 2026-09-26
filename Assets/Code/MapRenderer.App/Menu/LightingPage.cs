using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App.Menu
{
    /// <summary>
    /// Lighting page: runtime overrides for the sun, the sky and the haze, on top of the live style's
    /// <c>light</c> and <c>sky</c> blocks (<see cref="MapViewComponent.SetSunOverride"/>,
    /// <see cref="MapViewComponent.SetSkyOverride"/>, <see cref="MapViewComponent.SetHazeOverride"/>). Controls
    /// seed from the live state on the first draw, on every restyle, and again when a restyle's ease ends.
    /// "Reset to style" clears all three overrides and reverts to the style's own values.
    /// </summary>
    internal sealed class LightingPage : IMenuPage
    {
        // The StyleId the sliders were last seeded from; null before the first draw. Re-seeding on a
        // StyleId change catches both the first open and a restyle happening while this page is open.
        private string _seededStyleId;

        // True when the last seed ran while a restyle was still easing, so the controls hold a mid-ease value.
        private bool _seededMidTransition;

        private float _azimuthDeg;
        private float _elevationDeg;
        private float _intensity;
        private float _h, _s, _v;

        // The values last pushed through SetSunOverride — compared against the live fields each draw so
        // Apply runs only when a control actually moved, not on every Layout/Repaint pass (which would
        // fight Reset and re-assert a stale override under a live restyle).
        private float _appliedAzimuthDeg, _appliedElevationDeg, _appliedIntensity;
        private float _appliedH, _appliedS, _appliedV;

        // Sky colours as HSV, with their own applied snapshot: a sky change must not re-push the sun.
        private float _skyH, _skyS, _skyV;
        private float _horizonH, _horizonS, _horizonV;
        private float _appliedSkyH, _appliedSkyS, _appliedSkyV;
        private float _appliedHorizonH, _appliedHorizonS, _appliedHorizonV;

        // Haze switch and fog colour, with their own applied snapshot.
        private bool  _hazeOn, _appliedHazeOn;
        private float _fogH, _fogS, _fogV;
        private float _appliedFogH, _appliedFogS, _appliedFogV;

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

            GUILayout.Label(map.SunLight != null && map.SunLight.IsOverridden ? "Sun (overridden)" : "Sun");
            GUILayout.Label($"Azimuth: {_azimuthDeg:F0}°");
            _azimuthDeg = GUILayout.HorizontalSlider(_azimuthDeg, 0f, 360f);
            GUILayout.Label($"Elevation: {_elevationDeg:F0}°");
            _elevationDeg = GUILayout.HorizontalSlider(_elevationDeg, -90f, 90f);
            GUILayout.Label($"Intensity: {_intensity:F2}");
            _intensity = GUILayout.HorizontalSlider(_intensity, 0f, 4f);

            GUILayout.Space(6f);
            DrawColorControl("Color", ref _h, ref _s, ref _v);

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
            GUILayout.Label(map.SkyGradient != null && map.SkyGradient.IsOverridden ? "Sky (overridden)" : "Sky");
            DrawColorControl("Sky", ref _skyH, ref _skyS, ref _skyV);
            DrawColorControl("Horizon", ref _horizonH, ref _horizonS, ref _horizonV);

            GUILayout.Space(10f);
            GUILayout.Label(map.DistanceHaze != null && map.DistanceHaze.IsOverridden ? "Haze (overridden)" : "Haze");
            _hazeOn = GUILayout.Toggle(_hazeOn, "Haze on");
            DrawColorControl("Fog", ref _fogH, ref _fogS, ref _fogV);

            GUILayout.Space(6f);
            if (GUILayout.Button("Reset to style"))
            {
                map.ResetSunToStyle();
                map.ResetSkyToStyle();
                map.ResetHazeToStyle();
                Seed(map); // controls match the now-live style; nothing pending to Apply below
                return;
            }

            if (HasPendingChange())
            {
                map.SetSunOverride(Angle.FromDegrees(_azimuthDeg), Angle.FromDegrees(90.0 - _elevationDeg),
                    Color.HSVToRGB(_h, _s, _v), _intensity);
                MarkApplied();
            }

            if (HasPendingSkyChange())
            {
                map.SetSkyOverride(Color.HSVToRGB(_skyH, _skyS, _skyV),
                                   Color.HSVToRGB(_horizonH, _horizonS, _horizonV));
                MarkSkyApplied();
            }

            if (HasPendingHazeChange())
            {
                map.SetHazeOverride(_hazeOn, Color.HSVToRGB(_fogH, _fogS, _fogV));
                MarkHazeApplied();
            }
        }

        // Reads the light's, sky's and haze's CURRENT state into the controls, so a first open — or a restyle that lands
        // while this page is open — shows what's actually lit rather than a stale or default guess.
        private void Seed(MapViewComponent map)
        {
            _seededStyleId       = map.StyleId;
            _seededMidTransition = IsTransitioning(map);
            SkyGradient sky = map.SkyGradient;
            if (sky != null)
            {
                Color.RGBToHSV(sky.SkyColor, out _skyH, out _skyS, out _skyV);
                Color.RGBToHSV(sky.HorizonColor, out _horizonH, out _horizonS, out _horizonV);
                MarkSkyApplied();
            }

            DistanceHaze haze = map.DistanceHaze;
            if (haze != null)
            {
                _hazeOn = haze.Enabled;
                Color.RGBToHSV(haze.FogColor, out _fogH, out _fogS, out _fogV);
                MarkHazeApplied();
            }

            SunLight sun = map.SunLight;
            if (sun == null) return;

            _azimuthDeg   = (float)sun.Azimuth.NormalizedDegrees().Degrees;
            _elevationDeg = (float)(90.0 - sun.Polar.Degrees);
            _intensity    = sun.Intensity;
            Color.RGBToHSV(sun.Color, out _h, out _s, out _v);
            MarkApplied();
        }

        /// <summary>True while a restyle still eases the sun, the sky or the haze.</summary>
        private static bool IsTransitioning(MapViewComponent map)
            => map.SunLight?.IsTransitioning == true || map.SkyGradient?.IsTransitioning == true
            || map.DistanceHaze?.IsTransitioning == true;

        private bool HasPendingChange()
            => _azimuthDeg != _appliedAzimuthDeg || _elevationDeg != _appliedElevationDeg
            || _intensity != _appliedIntensity || _h != _appliedH || _s != _appliedS || _v != _appliedV;

        private void MarkApplied()
        {
            _appliedAzimuthDeg = _azimuthDeg; _appliedElevationDeg = _elevationDeg; _appliedIntensity = _intensity;
            _appliedH = _h; _appliedS = _s; _appliedV = _v;
        }

        private bool HasPendingSkyChange()
            => _skyH != _appliedSkyH || _skyS != _appliedSkyS || _skyV != _appliedSkyV
            || _horizonH != _appliedHorizonH || _horizonS != _appliedHorizonS || _horizonV != _appliedHorizonV;

        private void MarkSkyApplied()
        {
            _appliedSkyH = _skyH; _appliedSkyS = _skyS; _appliedSkyV = _skyV;
            _appliedHorizonH = _horizonH; _appliedHorizonS = _horizonS; _appliedHorizonV = _horizonV;
        }

        private bool HasPendingHazeChange()
            => _hazeOn != _appliedHazeOn || _fogH != _appliedFogH || _fogS != _appliedFogS || _fogV != _appliedFogV;

        private void MarkHazeApplied()
        {
            _appliedHazeOn = _hazeOn;
            _appliedFogH = _fogH; _appliedFogS = _fogS; _appliedFogV = _fogV;
        }

        // Small, reusable H/S/V control — a swatch plus three sliders.
        private static void DrawColorControl(string label, ref float h, ref float s, ref float v)
        {
            Color color = Color.HSVToRGB(h, s, v);
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(56f));
                var prevColor = GUI.color;
                GUI.color = color;
                GUILayout.Box(string.Empty, GUILayout.Width(24f), GUILayout.Height(16f));
                GUI.color = prevColor;
            }
            GUILayout.Label($"H: {h:F2}");
            h = GUILayout.HorizontalSlider(h, 0f, 1f);
            GUILayout.Label($"S: {s:F2}");
            s = GUILayout.HorizontalSlider(s, 0f, 1f);
            GUILayout.Label($"V: {v:F2}");
            v = GUILayout.HorizontalSlider(v, 0f, 1f);
        }

        private void ApplyPreset(float elevation, float h, float s, float v, float intensity)
        {
            _elevationDeg = elevation;
            _h = h; _s = s; _v = v;
            _intensity = intensity;
        }
    }
}
