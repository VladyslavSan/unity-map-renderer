using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The MonoBehaviour host for a <see cref="MapView"/> — the Unity-lifecycle shell. It carries the
    /// serialized <see cref="Config"/> (the only place Inspector fields can live), builds and owns the plain
    /// <see cref="MapView"/> once a camera is wired, and forwards the frame/teardown lifecycle. All map logic
    /// lives in <see cref="MapView"/>; this class holds only the MonoBehaviour glue.
    ///
    /// <para>The <see cref="MapView"/> is created on the first <see cref="SetCamera"/> (Bootstrapper.Wire
    /// hands it a <see cref="MapCamera"/> over the main tagged camera) — so it is always born with a real
    /// camera and its own config. Before that, and in the pre-wire <see cref="Update"/> window, the forwards
    /// no-op; that "not wired yet" state is the MonoBehaviour's to hold, not <see cref="MapView"/>'s.</para>
    /// </summary>
    public sealed class MapViewComponent : MonoBehaviour
    {
        [Tooltip("Inspector-tunable map settings (shared by reference with the MapView — live edits apply).")]
        public MapViewConfig Config = new MapViewConfig();

        /// <summary>The map logic. Null until a camera is wired (<see cref="SetCamera"/>). Test surface.</summary>
        internal MapView View { get; private set; }

        // ── Production API (Bootstrapper / controllers) ──────────────────────────────────────────

        /// <summary>Builds the <see cref="MapView"/> over <see cref="Config"/> + this camera. The camera is
        /// construction-only on <see cref="MapView"/>, so a re-injection tears down the old view and builds a
        /// fresh one (in practice this is called once, by Bootstrapper.Wire over the main camera).</summary>
        public void SetCamera(MapCamera camera)
        {
            View?.Teardown();
            View = new MapView(Config, camera);
        }

        public UniTask SetStyle(string styleUri, CancellationToken ct = default)
            => View != null ? View.SetStyle(styleUri, ct) : UniTask.CompletedTask;

        public UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
            => View != null ? View.SetStyle(style, styleId, ct) : UniTask.CompletedTask;

        public MapCamera Camera => View?.Camera;

        // ── Frame / teardown lifecycle (also driven explicitly by tests) ─────────────────────────

        public void Tick()               => View?.Tick();
        public void UpdateFrame(double dt) => View?.UpdateFrame(dt);
        public void Teardown()           => View?.Teardown();

        private void Update()    => View?.UpdateFrame(Time.deltaTime);
        private void OnDestroy() => View?.Teardown();

        // ── Internal test reads (forwarded so MapViewTestExtensions stays unchanged) ─────────────
        internal Tile.TileManager     TileManager => View?.TileManager;
        internal Style.RenderLayerSet Layers      => View?.Layers;
        internal double2              SceneOrigin => View != null ? View.SceneOrigin : default;
        internal string               StyleId     => View?.StyleId;
    }
}
