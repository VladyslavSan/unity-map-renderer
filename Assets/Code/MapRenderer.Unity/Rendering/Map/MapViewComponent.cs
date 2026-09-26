using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The MonoBehaviour host for a <see cref="MapView"/>. It carries the serialized <see cref="Config"/>
    /// (the only place Inspector fields can live), builds the <see cref="MapView"/> on the first
    /// <see cref="SetCamera"/>, and forwards the frame/teardown lifecycle. All map logic lives in
    /// <see cref="MapView"/>. Before a camera is wired the forwards no-op; that "not wired yet" state belongs
    /// to this class, so a <see cref="MapView"/> always has a real camera.
    /// </summary>
    public sealed partial class MapViewComponent : MonoBehaviour
    {
        [Tooltip("Inspector-tunable map settings (shared by reference with the MapView — live edits apply).")]
        public MapViewConfig Config = new MapViewConfig();

        /// <summary>The map logic. Null until a camera is wired (<see cref="SetCamera"/>). Test surface.</summary>
        internal MapView View { get; private set; }

        // Profiler-counter hooks, implemented in MapViewComponent.ProfilerCounters.cs inside `#if ENABLE_PROFILER`.
        // A release player has no implementing part, so the compiler erases the calls (docs/telemetry-design.md).
        partial void AttachTelemetryCounters();
        partial void ReleaseTelemetryCounters();
        partial void MirrorTelemetryCounters();

        // ── Production API (host / controllers) ──────────────────────────────────────────

        /// <summary>Builds the <see cref="MapView"/> over <see cref="Config"/> + this camera. The camera is
        /// construction-only on <see cref="MapView"/>, so a re-injection tears down the old view and builds a
        /// fresh one (in practice this is called once, at runtime wiring over the main camera).</summary>
        public void SetCamera(MapCamera camera)
        {
            ReleaseTelemetryCounters();
            View?.Teardown();
            View = new MapView(Config, camera);
            AttachTelemetryCounters();
        }

        public UniTask SetStyle(string styleUri, CancellationToken ct = default)
        {
            return View?.SetStyle(styleUri, ct) ?? UniTask.CompletedTask;
        }

        public UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
        {
            return View?.SetStyle(style, styleId, ct) ?? UniTask.CompletedTask;
        }

        public MapCamera Camera => View?.Camera;

        // ── Sun light (App wiring + the debug-menu Lighting page) ────────────────────────────────
        /// <summary>Points the sun light writer at the scene's directional light. Called once by
        /// <c>MapHost</c> after it finds/creates that light, before the first <see cref="SetStyle(string,CancellationToken)"/>.</summary>
        internal void SetSunLightTarget(Light light) => View?.SetSunLightTarget(light);

        /// <summary>The live sun light state (read by the Lighting menu page to seed its sliders).</summary>
        internal SunLight SunLight => View?.SunLight;

        /// <summary>Overrides the sun's azimuth, polar angle, color and intensity on top of the style,
        /// until <see cref="ResetSunToStyle"/> or the next style change.</summary>
        internal void SetSunOverride(Angle azimuth, Angle polar, Color color, float intensity)
            => View?.SunLight?.SetOverride(azimuth, polar, color, intensity);

        /// <summary>Clears a runtime sun override and re-applies the current style's light.</summary>
        internal void ResetSunToStyle() => View?.SunLight?.ResetToStyle();

        // ── Sky (App wiring + the debug-menu Lighting page) ──────────────────────────────────────
        /// <summary>Paints the style's sky behind <paramref name="camera"/>. Called once by <c>MapHost</c>,
        /// before the first <see cref="SetStyle(string,CancellationToken)"/>.</summary>
        internal void SetSkyTarget(UnityEngine.Camera camera) => View?.SetSkyTarget(camera);

        /// <summary>The live sky state (read by the Lighting menu page to seed its controls).</summary>
        internal SkyGradient SkyGradient => View?.SkyGradient;

        /// <summary>Overrides the sky and horizon colours on top of the style, until
        /// <see cref="ResetSkyToStyle"/> or the next style change.</summary>
        internal void SetSkyOverride(Color skyColor, Color horizonColor)
            => View?.SkyGradient?.SetOverride(skyColor, horizonColor);

        /// <summary>Clears a runtime sky override and re-applies the current style's sky.</summary>
        internal void ResetSkyToStyle() => View?.SkyGradient?.ResetToStyle();

        // ── Haze (App wiring + the debug-menu Lighting page) ─────────────────────────────────────
        /// <summary>Starts writing the style's distance haze. Called once by <c>MapHost</c>, before the first
        /// <see cref="SetStyle(string,CancellationToken)"/>.</summary>
        internal void EnableHaze() => View?.EnableHaze();

        /// <summary>The live haze state (read by the Lighting menu page to seed its controls).</summary>
        internal DistanceHaze DistanceHaze => View?.DistanceHaze;

        /// <summary>Overrides the haze switch and fog colour on top of the style, until
        /// <see cref="ResetHazeToStyle"/> or the next style change.</summary>
        internal void SetHazeOverride(bool enabled, Color fogColor)
            => View?.DistanceHaze?.SetOverride(enabled, fogColor);

        /// <summary>Clears a runtime haze override and re-applies the current style's fog colour.</summary>
        internal void ResetHazeToStyle() => View?.DistanceHaze?.ResetToStyle();

        // ── Frame / teardown lifecycle (also driven explicitly by tests) ─────────────────────────

        public void Teardown()
        {
            ReleaseTelemetryCounters();
            View?.Teardown();
        }

        // LateUpdate, not Update: it runs after the input controller's Update mutates the camera, so the frame
        // sees this frame's input. The ordered pipeline lives in MapView.LateUpdate.
        public void LateUpdate()
        {
            View?.LateUpdate();

            // AFTER the frame, not before: the counter consumer PULLS each provider's levels, so it has to run
            // once every provider has refreshed its own. Reading first would mirror last frame's numbers.
            MirrorTelemetryCounters();
        }

        private void OnDestroy() => Teardown();

        // ── Internal test reads (forwarded so MapViewTestExtensions stays unchanged) ─────────────
        internal Tile.TileManager     TileManager => View?.TileManager;
        internal Style.RenderLayerSet Layers      => View?.Layers;
        internal string               StyleId     => View?.StyleId;
    }
}