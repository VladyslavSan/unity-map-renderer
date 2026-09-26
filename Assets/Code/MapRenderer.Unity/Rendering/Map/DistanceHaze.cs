using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// Fades distant map ground toward the style's <c>fog-color</c> with URP linear fog, so the far cut
    /// dissolves into the sky. The fog range follows the committed far plane every frame
    /// (<see cref="UpdateRange"/>), and there is no haze while the far cut is off screen. This is the only
    /// writer of the <see cref="RenderSettings"/> fog fields; <see cref="Dispose"/> restores them. A restyle
    /// eases the fog colour over the style transition; <see cref="Advance"/> moves it.
    /// </summary>
    internal sealed class DistanceHaze : IDisposable
    {
        /// <summary>The fog mode the renderer uses. The build keeps only this mode's shader variants.</summary>
        internal const FogMode HazeFogMode = FogMode.Linear;

        /// <summary>Below this ground ratio (deepest on-screen ground depth / far) there is no haze. It is above
        /// the top-down ratio of every aspect at the 60° FOV (at most cos 30° / 1.1 ≈ 0.79).</summary>
        internal const double HazeGroundRatio = 0.8;

        /// <summary>At full haze the fog starts this fraction of the way from the look-at to the far cut.</summary>
        internal const double HazeStartFraction = 0.5;

        private readonly (bool fog, FogMode mode, Color color, float start, float end) _saved;
        private StyleSky _style;
        private double   _lastZoom;
        private StyleEase _ease;
        private Color    _fromColor, _toColor;

        /// <summary>Saves the current <see cref="RenderSettings"/> fog, for <see cref="Dispose"/>.</summary>
        public DistanceHaze()
        {
            _saved = (RenderSettings.fog, RenderSettings.fogMode, RenderSettings.fogColor,
                      RenderSettings.fogStartDistance, RenderSettings.fogEndDistance);
        }

        /// <summary>True while a runtime override is showing instead of the style's own values.</summary>
        public bool IsOverridden { get; private set; }

        /// <summary>False when the haze is switched off; the style always switches it on.</summary>
        public bool Enabled { get; private set; } = true;

        /// <summary>The fog colour last written (style- or override-derived).</summary>
        public Color FogColor { get; private set; } = Color.white;

        /// <summary>True while a restyle ease is still moving the fog colour.</summary>
        public bool IsTransitioning => _ease.IsActive;

        /// <summary>Applies <paramref name="style"/>'s <c>fog-color</c> at <paramref name="zoom"/>, switches the
        /// haze on and clears any runtime override. A style with no <c>sky</c> block gets the spec default white.
        /// The first style, and an instant transition, snap. The switch turns on at once, so the colour eases
        /// visibly instead of popping on at the end.</summary>
        public void ApplyStyle(StyleSky style, double zoom, in StyleTransition transition = default,
                               double nowSeconds = 0.0)
        {
            bool first   = _style == null;
            _style       = style;
            _lastZoom    = zoom;
            IsOverridden = false;
            Enabled      = true;
            _fromColor   = FogColor;
            _toColor     = ZoomStyleApplier.ToUnityColor(style.FogColor.Evaluate(zoom));
            _ease.Arm(first ? default : transition, nowSeconds);
            if (!_ease.IsActive) FogColor = _toColor;
        }

        /// <summary>Moves this frame's eased fog colour. Call before <see cref="UpdateRange"/>, which writes it.
        /// A no-op when no ease is running.</summary>
        public void Advance(double nowSeconds)
        {
            if (!_ease.IsActive) return;
            float weight = _ease.Step(nowSeconds);
            if (!_ease.IsActive) { FogColor = _toColor; return; }
            FogColor = StyleEase.Mix(_fromColor, _toColor, weight);
        }

        /// <summary>Overrides the switch and the colour on top of the last applied style, until
        /// <see cref="ResetToStyle"/> or the next <see cref="ApplyStyle"/>.</summary>
        public void SetOverride(bool enabled, Color fogColor)
        {
            _ease.Stop();
            IsOverridden = true;
            Enabled      = enabled;
            FogColor     = fogColor;
        }

        /// <summary>Clears a runtime override and re-applies the last style. A no-op before the first
        /// <see cref="ApplyStyle"/>.</summary>
        public void ResetToStyle()
        {
            if (_style != null) ApplyStyle(_style, _lastZoom, StyleTransition.Instant);
        }

        /// <summary>Writes this frame's fog from the committed camera. Call after
        /// <see cref="MapCamera.SyncToCamera"/>.</summary>
        public void UpdateRange(MapCamera camera)
        {
            HazeRange range = Range(camera.CameraRelativePosition, camera.CurrentFarMetres,
                                    camera.Camera.nearClipPlane, camera.CurrentProperties.VerticalFovDeg);
            bool on = Enabled && range.On;
            RenderSettings.fog = on;
            if (!on) return;
            RenderSettings.fogMode          = HazeFogMode;
            RenderSettings.fogColor         = FogColor;
            RenderSettings.fogStartDistance = (float)range.Start;
            RenderSettings.fogEndDistance   = (float)range.End;
        }

        /// <summary>
        /// The fog range for a camera. The ramp weight grows from 0 to 1 as the ground ratio goes from
        /// <see cref="HazeGroundRatio"/> to 1 (the far cut on screen). The start moves from the far plane to
        /// <see cref="HazeStartFraction"/> of the way from the look-at; the end is the far plane. Distances
        /// count from the near plane, because the lit passes measure fog depth from there.
        /// </summary>
        /// <param name="cameraPosition">The camera relative to the look-at, which sits at the origin.</param>
        internal static HazeRange Range(double3 cameraPosition, double farMetres, double nearMetres,
                                        double verticalFovDeg)
        {
            double groundRatio = DeepestGroundDepth(cameraPosition, farMetres, verticalFovDeg) / farMetres;
            double weight = math.saturate((groundRatio - HazeGroundRatio) / (1.0 - HazeGroundRatio));
            double fullStart = math.lerp(math.length(cameraPosition), farMetres, HazeStartFraction);
            return new HazeRange
            {
                On    = weight > 0.0,
                Start = math.lerp(farMetres, fullStart, weight) - nearMetres,
                End   = farMetres - nearMetres,
            };
        }

        /// <summary>View depth of the deepest on-screen ground point: the top screen row on the ground
        /// plane, or <paramref name="farMetres"/> when that row is at or above the horizon. Limitation: on the
        /// globe the tangent plane stands in for the sphere.</summary>
        internal static double DeepestGroundDepth(double3 cameraPosition, double farMetres, double verticalFovDeg)
        {
            double top = SkyGradient.TopElevation(cameraPosition, verticalFovDeg).Radians;
            if (top >= 0.0) return farMetres;
            double slant = cameraPosition.y / math.sin(-top);
            return math.min(farMetres, slant * math.cos(0.5 * math.radians(verticalFovDeg)));
        }

        /// <summary>Restores the <see cref="RenderSettings"/> fog saved at construction.</summary>
        public void Dispose()
        {
            (RenderSettings.fog, RenderSettings.fogMode, RenderSettings.fogColor,
             RenderSettings.fogStartDistance, RenderSettings.fogEndDistance) = _saved;
        }

        /// <summary>A linear fog range in metres from the near plane. <see cref="On"/> is false when there is
        /// no haze; <see cref="Start"/> and <see cref="End"/> are then equal and must not reach URP.</summary>
        internal readonly struct HazeRange
        {
            public bool   On    { get; init; }
            public double Start { get; init; }
            public double End   { get; init; }
        }
    }
}
