using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Style;
using SkyPropertyId = MapRenderer.Unity.Rendering.ShaderProperties.Sky.PropertyId;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// Paints the style's root <c>sky</c> as a vertical gradient behind the map: a runtime <c>Map/Sky</c>
    /// material on a camera <see cref="Skybox"/> component. The blend spans the visible sky strip: from where
    /// the rendered map ends (<see cref="MapEdgeElevation"/>) to the top of the screen (<see cref="TopElevation"/>),
    /// both pushed every frame. It never writes <see cref="RenderSettings"/>, so
    /// ambient and reflections are unchanged. A runtime override replaces the style colours until
    /// <see cref="ResetToStyle"/> or the next <see cref="ApplyStyle"/>. A restyle eases the colours over the
    /// style transition; <see cref="Advance"/> moves it.
    /// </summary>
    internal sealed class SkyGradient : IDisposable
    {
        /// <summary>The shader the runtime material uses; listed in Always Included Shaders.</summary>
        internal const string ShaderName = "Map/Sky";

        /// <summary>The spec-default <c>sky-horizon-blend</c>: the blend spans this fraction of the visible sky
        /// strip. Fixed, because that key is not parsed.</summary>
        internal const float SkyHorizonBlend = 0.8f;

        private readonly Camera           _camera;
        private readonly Skybox           _skybox;
        private readonly bool             _addedSkybox;
        private readonly Material         _previousSkyboxMaterial;
        private readonly CameraClearFlags _previousClearFlags;
        private StyleSky                  _style;
        private double                    _lastZoom;
        private StyleEase                 _ease;
        private (Color sky, Color horizon) _from, _to;

        /// <summary>Points <paramref name="camera"/> at a new sky material. With no camera, or no
        /// <c>Map/Sky</c> shader in the build, it tracks colours only and the camera keeps its clear.</summary>
        public SkyGradient(Camera camera)
        {
            _camera = camera;
            if (camera == null) return;

            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SkyGradient] Shader '{ShaderName}' is not in this build; the camera keeps its clear colour.");
                return;
            }

            Material = new Material(shader) { name = "MapSky (runtime)" };
            Material.SetFloat(SkyPropertyId.SkyHorizonBlend, SkyHorizonBlend);

            _skybox      = camera.GetComponent<Skybox>();
            _addedSkybox = _skybox == null;
            if (_addedSkybox) _skybox = camera.gameObject.AddComponent<Skybox>();
            _previousSkyboxMaterial = _skybox.material;
            _skybox.material        = Material;

            _previousClearFlags = camera.clearFlags;
            camera.clearFlags   = CameraClearFlags.Skybox;
        }

        /// <summary>The runtime sky material; null without a camera or shader.</summary>
        internal Material Material { get; }

        /// <summary>True while a runtime override is showing instead of the style's own colours.</summary>
        public bool IsOverridden { get; private set; }

        /// <summary>The sky colour last written (style- or override-derived).</summary>
        public Color SkyColor { get; private set; }

        /// <summary>The horizon colour last written (style- or override-derived).</summary>
        public Color HorizonColor { get; private set; }

        /// <summary>True while a restyle ease is still moving the colours.</summary>
        public bool IsTransitioning => _ease.IsActive;

        /// <summary>Applies <paramref name="style"/>'s sky colours at <paramref name="zoom"/> and clears any
        /// runtime override. A style with no <c>sky</c> block gets the spec defaults. The first style, and an
        /// instant transition, snap.</summary>
        public void ApplyStyle(StyleSky style, double zoom, in StyleTransition transition = default,
                               double nowSeconds = 0.0)
        {
            bool first   = _style == null;
            _style       = style;
            _lastZoom    = zoom;
            IsOverridden = false;
            _from = (SkyColor, HorizonColor);
            _to   = (ZoomStyleApplier.ToUnityColor(style.SkyColor.Evaluate(zoom)),
                     ZoomStyleApplier.ToUnityColor(style.HorizonColor.Evaluate(zoom)));
            _ease.Arm(first ? default : transition, nowSeconds);
            if (!_ease.IsActive) Write(_to.sky, _to.horizon);
        }

        /// <summary>Writes this frame's eased colours. A no-op when no ease is running.</summary>
        public void Advance(double nowSeconds)
        {
            if (!_ease.IsActive) return;
            float weight = _ease.Step(nowSeconds);
            if (!_ease.IsActive) { Write(_to.sky, _to.horizon); return; }
            Write(StyleEase.Mix(_from.sky, _to.sky, weight), StyleEase.Mix(_from.horizon, _to.horizon, weight));
        }

        /// <summary>Overrides both colours on top of the last applied style, until
        /// <see cref="ResetToStyle"/> or the next <see cref="ApplyStyle"/>.</summary>
        public void SetOverride(Color skyColor, Color horizonColor)
        {
            _ease.Stop();
            IsOverridden = true;
            Write(skyColor, horizonColor);
        }

        /// <summary>Clears a runtime override and re-applies the last style sky. A no-op before the first
        /// <see cref="ApplyStyle"/>.</summary>
        public void ResetToStyle()
        {
            if (_style != null) ApplyStyle(_style, _lastZoom, StyleTransition.Instant);
        }

        /// <summary>Pushes this frame's <see cref="MapEdgeElevation"/> and <see cref="TopElevation"/> from the
        /// committed camera. Call after <see cref="MapCamera.SyncToCamera"/>.</summary>
        public void UpdateMapEdge(MapCamera camera)
        {
            if (Material == null) return;
            double3 position = camera.CameraRelativePosition;
            Angle edge = MapEdgeElevation(position, camera.CurrentFarMetres, camera.Projection);
            Angle top  = TopElevation(position, camera.CurrentProperties.VerticalFovDeg);
            Material.SetFloat(SkyPropertyId.MapEdgeElevation, (float)edge.Radians);
            Material.SetFloat(SkyPropertyId.SkyTopElevation, (float)top.Radians);
        }

        /// <summary>Elevation of the view ray through the top-centre of the screen: the view pitch plus half the
        /// vertical field of view, at most 90°. The view pitch is the forward ray's elevation (tilt − 90°), not
        /// the map tilt.</summary>
        /// <param name="cameraPosition">The camera relative to the look-at, which sits at the origin.</param>
        internal static Angle TopElevation(double3 cameraPosition, double verticalFovDeg)
        {
            double pitch = -math.asin(cameraPosition.y / math.length(cameraPosition));
            return Angle.FromRadians(math.min(pitch + 0.5 * math.radians(verticalFovDeg), 0.5 * math.PI_DBL));
        }

        /// <summary>
        /// Elevation of the view ray from the camera to where the rendered map ends, in the look-at frame: the
        /// ground point at view depth <paramref name="farMetres"/> in the screen's centre column (the view's
        /// vertical plane), or on the globe the planet's limb when that is nearer. Zero or negative. The far cut
        /// is taken on the tangent plane.
        /// </summary>
        /// <param name="cameraPosition">The camera relative to the look-at, which sits at the origin.</param>
        internal static Angle MapEdgeElevation(double3 cameraPosition, double farMetres, IProjection projection)
        {
            double height   = cameraPosition.y;
            double reach    = math.length(cameraPosition.xz); // horizontal distance to the look-at
            double distance = math.length(cameraPosition);
            // The view looks at the origin, so the forward pitch below horizontal has sin = height/distance.
            double edge = -math.atan2(height * reach / distance, farMetres - height * height / distance);

            if (projection != null && projection.TryGetHorizonOccluder(out double3 centre, out double radius))
            {
                double3 toCentre = centre - cameraPosition;
                double  centreElevation = math.atan2(toCentre.y, reach);
                double  limbHalfAngle   = math.asin(math.min(1.0, radius / math.length(toCentre)));
                edge = math.min(edge, centreElevation + limbHalfAngle);
            }
            return Angle.FromRadians(math.clamp(edge, -0.5 * math.PI_DBL, 0.0));
        }

        /// <summary>Restores the camera's clear and skybox, and destroys the runtime material.</summary>
        public void Dispose()
        {
            if (Material == null) return;
            if (_camera != null) _camera.clearFlags = _previousClearFlags;
            if (_addedSkybox) _skybox.DestroySafely();
            else if (_skybox != null) _skybox.material = _previousSkyboxMaterial;
            Material.DestroySafely();
        }

        private void Write(Color skyColor, Color horizonColor)
        {
            SkyColor = skyColor; HorizonColor = horizonColor;
            if (Material == null) return;
            Material.SetColor(SkyPropertyId.SkyColor, skyColor);
            Material.SetColor(SkyPropertyId.HorizonColor, horizonColor);
        }
    }
}
