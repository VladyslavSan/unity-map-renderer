using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
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