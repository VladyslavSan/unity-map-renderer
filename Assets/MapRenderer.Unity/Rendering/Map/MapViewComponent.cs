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
        {
            return View?.SetStyle(styleUri, ct) ?? UniTask.CompletedTask;
        }

        public UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
        {
            return View?.SetStyle(style, styleId, ct) ?? UniTask.CompletedTask;
        }

        public MapCamera Camera => View?.Camera;

        // ── Frame / teardown lifecycle (also driven explicitly by tests) ─────────────────────────

        public void Tick()               => View?.Tick();
        public void UpdateFrame(double dt) => View?.UpdateFrame(dt);
        public void Teardown()           => View?.Teardown();

        private void Update()    => View?.UpdateFrame(Time.deltaTime);

        // Single per-frame camera commit. Input controllers mutate MapCamera.CurrentProperties during their
        // Update; LateUpdate runs after ALL of them, so this propagates the final merged state to the Unity
        // camera exactly once — and before rendering (LateUpdate precedes culling/render). This is the
        // "camera matrix frozen for this frame" point: any future Unity-camera-matrix consumer (symbol
        // screen-space placement) must be sequenced AFTER this call, here — not in Update, not in another
        // component's LateUpdate (Unity does not order those).
        private void LateUpdate()
        {
            var cam = View?.Camera;
            if (cam == null) return;
            // Refresh the DPI ratio from the (live, Inspector-tunable) config before the commit so the camera
            // frames the logical viewport (S92 D1); Config is shared by reference with the Controller, so the
            // render and the interaction seam can't diverge on DPR.
            cam.DevicePixelRatio = Config.DevicePixelRatio;
            cam.SyncToCamera();
        }

        private void OnDestroy() => View?.Teardown();

        // ── Internal test reads (forwarded so MapViewTestExtensions stays unchanged) ─────────────
        internal Tile.TileManager     TileManager => View?.TileManager;
        internal Style.RenderLayerSet Layers      => View?.Layers;
        internal string               StyleId     => View?.StyleId;
    }
}
