using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using Unity.Mathematics;
using GraphicsDeviceType = UnityEngine.Rendering.GraphicsDeviceType;
using SphericalHarmonicsL2 = UnityEngine.Rendering.SphericalHarmonicsL2;

using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Map;
// Disambiguate from UnityEngine.RenderMode (Canvas) — the map's render mode is the material-set one.
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;

namespace MapRenderer.App
{
    /// <summary>
    /// Map composition root on the scene's map root GameObject. It wires the MapView + camera graph, lighting,
    /// input, and the Map reference of each dev surface in the scene; it never adds or gates a dev surface.
    /// <see cref="Start"/> is the runtime entry and finds the Main Camera via <c>Camera.main</c>.
    /// <see cref="Wire(GameObject, Camera)"/> is the static entry that EditMode tests drive (no file or HTTP
    /// access). Supported layer types: <see cref="MapRenderer.Unity.Rendering.Style.RenderLayerFactory"/>.
    /// </summary>
    [RequireComponent(typeof(MapViewComponent))]
    public sealed class MapHost : MonoBehaviour
    {
        /// <summary>Wires <see cref="VerifiedDisposable.LeakReporter"/> to <see cref="Debug.LogError"/> once
        /// at startup, so a <see cref="VerifiedDisposable"/>-derived instance finalized without Dispose()
        /// (a resource leak) surfaces in the Editor/Player log instead of Core's engine-free no-op default.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void WireLeakReporter() => VerifiedDisposable.LeakReporter = Debug.LogError;

        // A TileJSON url in the style's sources[] keeps the tile path current across OpenFreeMap versions.
        // Default: offline, from StreamingAssets. Live style: https://tiles.openfreemap.org/styles/liberty.
        [Header("Style")]
        [Tooltip("Style document URI — the single source of truth. file://, http(s):// or a bare path " +
                 "resolved under StreamingAssets. Its sources[] declare the tiles (inline or via TileJSON).")]
        public string StyleUri = "Fixtures/liberty.json";

        [Header("Initial Camera")]
        [Tooltip("Initial map center latitude (decimal degrees, WGS-84).")]
        public double InitialLatitude = 52.52; // Berlin

        [Tooltip("Initial map center longitude (decimal degrees, WGS-84).")]
        public double InitialLongitude = 13.405; // Berlin

        [Tooltip("Initial zoom level (0 = world view). z14 is OpenFreeMap's maxzoom — densest data " +
                 "(buildings + full road network). Lower zooms thin out fast (z13 Berlin = 1 building).")]
        public double InitialZoom = 13.0; // the 512 tile convention shifts zoom numbers down by one

        [Tooltip("Vertical field-of-view (degrees) for the perspective camera — the initial camera lens " +
                 "(carried in CameraProperties, pushed to the Unity camera).")]
        public double VerticalFovDeg = 60.0;

        [Header("Session (launch-time constants)")]
        [Tooltip("Cap the frame rate to the display refresh rate (VSync) on Start. Off = render " +
                 "uncapped (1000+ FPS), which needlessly drives the GPU and heats the machine.")]
        public bool CapFrameRateToRefreshRate = true;

        [Tooltip("Render on a 3D globe (SphericalProjection) instead of the flat Web-Mercator plane. " +
                 "Launch-time only — the projection is a session constant. Pan and zoom input cast " +
                 "the pixel ray at the sphere.")]
        public bool UseGlobe = false;

        private void Start()
        {
            // Log the perf-relevant runtime settings first — Burst/threading state that is otherwise
            // invisible in a player and silently makes the build slow (a switched-off editor Burst toggle).
            RuntimeDiagnostics.Log();

            // 0. Cap the frame rate to the display refresh (VSync) — uncapped rendering serves no
            //    purpose for a map demo and just spins up the GPU/fans. vSyncCount=1 = every v-blank.
            if (CapFrameRateToRefreshRate)
            {
                QualitySettings.vSyncCount  = 1;
                Application.targetFrameRate = (int)math.round(Screen.currentResolution.refreshRateRatio.value);
            }

            // 1. Build the initial camera state.
            var initialView = new CameraProperties(
                new GeoCoordinate3D
                {
                    Latitude = InitialLatitude, Longitude = InitialLongitude, Altitude = 0.0
                }, InitialZoom, 0, 0, VerticalFovDeg);

            // 2. Wire camera + components; SetStyle (step 4) loads the sources. The projection is a launch-time
            //    session constant — globe or planar.
            Wire(gameObject, Camera.main, initialView,
                 UseGlobe ? new SphericalProjection() : null);

            // 2.5. The render mode is a property of the MapMaterialSet; the lighting bootstrap needs it first.
            //      A missing MapViewComponent or MaterialSet defaults to Lit.
            var mapView    = GetComponent<MapViewComponent>();
            var renderMode = mapView != null && mapView.Config.MaterialSet != null
                ? mapView.Config.MaterialSet.RenderMode
                : RenderMode.Lit;

            // 3. Both render modes need the light: URP Lit shades with it, and the unlit fill-extrusion twin
            //    reads its direction for a half-Lambert. Only the ambient probe below is Unlit-gated.
            Light sunLight = EnsureDirectionalLight();

            // 3.5. After EnsureDirectionalLight, so a procedural skybox convolves against the real sun.
            EnsureEnvironmentLighting(renderMode);

            // 3.6. Point the sun writer at the bootstrap light BEFORE SetStyle (step 4), so the first
            //      style load applies its `light` block instead of leaving the bootstrap Euler(60,30,0).
            if (mapView != null) mapView.SetSunLightTarget(sunLight);

            // 3.7. Same ordering reason: the first style load paints its `sky` behind Camera.main.
            if (mapView != null) mapView.SetSkyTarget(Camera.main);

            // 3.8. Same ordering reason: the first style load sets the haze's fog colour.
            if (mapView != null) mapView.EnableHaze();

            // 4. SetStyle is async (style doc + any TileJSON) and fire-and-forget; tiles stream in as it completes.
            if (mapView != null)
            {
                // dpr = Screen.dpi / 160 (the Android mdpi baseline, an open choice: docs/device-pixel-ratio-design.md).
                // Limitation: in Play Mode, Screen.dpi reports the Editor monitor's density (observed, not documented).
                // Tests drive Wire and keep the serialized ratio.
                mapView.Config.DevicePixelRatio = DeviceScaling.DevicePixelRatioFromDpi(Screen.dpi);
            }
            string styleUri = ResolveStyleUri(StyleUri);
            if (mapView != null)
                mapView.SetStyle(styleUri).Forget();

            // 5. The MapCamera built in Wire has already framed the camera. Re-applying through the Controller
            //    (when present) is harmless and gives frame 0 the same path as runtime input.
            var ctrl = GetComponent<Controller>();
            if (ctrl != null && ctrl.Camera != null)
                ctrl.ApplyCameraTransform(initialView);

            // Set on Camera.main, so it applies without a Controller. The sky (step 3.7) replaces this clear; it
            // shows only when the sky shader is missing from the build.
            var mainCam = Camera.main;
            if (mainCam != null && mainCam.backgroundColor == default)
                mainCam.backgroundColor = new Color(0.85f, 0.95f, 1.0f, 1f); // light blue sky

            // 6. Fill the Map reference of each dev surface present in the scene.
            WireDevSurfaces(mapView);

            Debug.Log($"[MapHost] Started. styleUri={styleUri}, zoom={InitialZoom}, " +
                      $"center=({InitialLatitude:F2},{InitialLongitude:F2}).");
        }

        /// <summary>
        /// Point each PRESENT dev surface's <c>Map</c> at <paramref name="mapView"/> (only when it hasn't been
        /// set by hand). MapHost never adds or removes these — a surface is in the scene or it isn't (the user's
        /// choice), and enabling/disabling it is Unity's own component checkbox; this just spares you dragging
        /// the reference. Includes inactive components so one enabled at runtime is already wired.
        /// </summary>
        /// <param name="mapView">The view the surfaces read (this GameObject's <see cref="MapViewComponent"/>).</param>
        private void WireDevSurfaces(MapViewComponent mapView)
        {
            if (mapView == null) return;

            // Each surface exposes a public `Map` field and shares no interface, so three explicit blocks are
            // clearer than reflection. A hand-set Map is kept.
            var menu = Object.FindAnyObjectByType<Menu.MenuOverlay>(FindObjectsInactive.Include);
            if (menu != null && menu.Map == null) menu.Map = mapView;

            var camPanel = Object.FindAnyObjectByType<CameraControlPanel>(FindObjectsInactive.Include);
            if (camPanel != null && camPanel.Map == null) camPanel.Map = mapView;

            var telemetry = Object.FindAnyObjectByType<MapTelemetryPanel>(FindObjectsInactive.Include);
            if (telemetry != null && telemetry.Map == null) telemetry.Map = mapView;
        }

        /// <summary>
        /// Resolves <paramref name="styleUri"/> to a loadable URI. A leading <c>file://</c>, <c>http://</c> or
        /// <c>https://</c> scheme is used verbatim. A bare relative path resolves under
        /// <c>Application.streamingAssetsPath</c> as a <c>file://</c> URI, so the shipped style loads offline.
        /// <c>internal</c>: <see cref="Menu.StylesPage"/> shares it, so a runtime style switch and the startup
        /// load resolve a bare path to the same URI.
        /// </summary>
        internal static string ResolveStyleUri(string styleUri)
        {
            if (string.IsNullOrEmpty(styleUri)) styleUri = "Fixtures/liberty.json";
            if (styleUri.StartsWith("file://") || styleUri.StartsWith("http://") || styleUri.StartsWith("https://"))
                return styleUri;
            // Bare path → under StreamingAssets, as a file:// URI.
            return "file://" + Path.Combine(Application.streamingAssetsPath, styleUri);
        }

        // ── Static wire-up entry (testable without Play mode) ─────────────────────────────────────

        /// <summary>
        /// Wires <see cref="Controller"/> and <see cref="MapViewComponent"/> on <paramref name="root"/> and builds
        /// the MapView over <paramref name="camera"/> from <paramref name="initialView"/>. A null camera logs a
        /// warning and builds no MapView. Data loads separately via
        /// <see cref="MapViewComponent.SetStyle(string,System.Threading.CancellationToken)"/>. The single wiring
        /// entry: <see cref="Start"/> and the EditMode wiring tests both call it.
        /// </summary>
        /// <param name="root">The map-root GameObject — must carry MapView + Controller.</param>
        /// <param name="camera">The camera to drive; typically <c>Camera.main</c>.</param>
        /// <param name="initialView">Initial camera state (the MapCamera is built from it).</param>
        public static void Wire(
            GameObject       root,
            Camera           camera,
            CameraProperties initialView = default,
            IProjection      projection  = null) // null ⇒ WebMercator (planar); SphericalProjection ⇒ globe
        {
            if (root == null)
            {
                Debug.LogWarning("[MapHost.Wire] root is null — wire-up skipped.");
                return;
            }

            if (camera == null)
            {
                Debug.LogWarning("[MapHost.Wire] No camera provided (Camera.main is null). " +
                                 "Controller will not drive any camera. Wire-up skipped for camera.");
                // We still continue to initialise MapView and set Map on the controller.
            }

            var mapView = root.GetComponent<MapViewComponent>();
            if (mapView == null)
            {
                Debug.LogWarning("[MapHost.Wire] No MapViewComponent found on root — wire-up skipped.");
                return;
            }

            // The input Controller is optional: a script-driven stress scene (TileLoadStressDriver) omits it,
            // and the map still wires and renders.
            var ctrl = root.GetComponent<Controller>();

            // ── Wire the MapCamera onto MapView (the essential graph, controller-independent) ────────────
            // MapView owns the one MapCamera. SetCamera builds the MapView, empty until SetStyle loads data.
            if (camera != null)
            {
                // Seed the DPR at construction so the ctor's frame-0 SyncToCamera frames the logical viewport;
                // LateUpdate keeps it live afterwards.
                float altitudeMultiplier = ctrl != null ? ctrl.AltitudeMultiplier : 1f;
                var mapCamera = new MapCamera(camera, initialView, altitudeMultiplier, projection,
                                              mapView.Config.DevicePixelRatio);
                mapView.SetCamera(mapCamera);
            }

            // ── Input wiring — only when an input Controller is present ──────────────────────────────────
            // A stress/headless scene omits the Controller; skip the desktop + touch input seams entirely.
            if (ctrl != null)
            {
                ctrl.Camera = camera; // may be null — guarded in Controller.Update
                ctrl.Map    = mapView;

                // Touch shares the desktop controller's write seam. Adding it at runtime needs no scene edit, and
                // the scene validator flags only missing scripts, not runtime-added ones.
                var touch = root.GetComponent<TouchController>() ?? root.AddComponent<TouchController>();
                touch.camera = camera;
                touch.Map    = mapView;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures <see cref="RenderSettings.ambientProbe"/> is non-degenerate: without it, URP Lit renders black
        /// every face whose <c>N·L</c> ≤ 0, such as walls facing away from the sun. It recomputes once at startup,
        /// only when the probe's DC term is near zero, so a host scene's own probe stays untouched. On WebGPU, or
        /// when the recompute is unusable, it sets a flat probe of the ambient colour. A no-op under
        /// <see cref="RenderMode.Unlit"/>: the unlit shader twins have no indirect term.
        /// </summary>
        /// <param name="mode">The active render mode (<see cref="MapMaterialSet.RenderMode"/>); only
        /// <see cref="RenderMode.Lit"/> runs the probe check.</param>
        internal static void EnsureEnvironmentLighting(RenderMode mode)
        {
            if (mode != RenderMode.Lit) return;

            var probe = RenderSettings.ambientProbe;
            double dcTerm = math.abs(probe[0, 0]) + math.abs(probe[1, 0]) + math.abs(probe[2, 0]);
            if (dcTerm > 1e-6) return; // already has a real probe — don't clobber a host's baked lighting

            // WebGPU has no texture readback, so the sky convolution returns garbage there.
            bool trustConvolution = SystemInfo.graphicsDeviceType != GraphicsDeviceType.WebGPU;
            if (trustConvolution) DynamicGI.UpdateEnvironment();
            RenderSettings.ambientProbe = ResolveAmbientProbe(RenderSettings.ambientProbe,
                RenderSettings.ambientLight * RenderSettings.ambientIntensity, trustConvolution);
        }

        /// <summary>
        /// Returns <paramref name="convolved"/> when it is trusted and usable. Otherwise returns a flat probe of
        /// <paramref name="ambient"/>, with each channel raised to at least <see cref="MinFallbackAmbient"/>.
        /// </summary>
        /// <param name="convolved">The probe <c>DynamicGI.UpdateEnvironment</c> produced.</param>
        /// <param name="trustConvolution">False when the device cannot run the convolution.</param>
        internal static SphericalHarmonicsL2 ResolveAmbientProbe(SphericalHarmonicsL2 convolved, Color ambient,
                                                                 bool trustConvolution)
        {
            if (trustConvolution && IsUsableAmbientProbe(convolved)) return convolved;

            var flat = new SphericalHarmonicsL2();
            flat.AddAmbientLight(new Color(math.max(ambient.r, MinFallbackAmbient),
                                           math.max(ambient.g, MinFallbackAmbient),
                                           math.max(ambient.b, MinFallbackAmbient)));
            return flat;
        }

        /// <summary>Bound far above the coefficients of a real sky; readback garbage exceeds it by decades.</summary>
        private const float MaxProbeCoefficient = 1e4f;

        /// <summary>Darkest channel value of the fallback probe. A flat 0.4 renders correctly on web.</summary>
        private const float MinFallbackAmbient = 0.4f;

        /// <summary>
        /// True when every coefficient is finite and within <see cref="MaxProbeCoefficient"/>, and the DC term
        /// is non-negative in every channel and non-zero in at least one.
        /// </summary>
        internal static bool IsUsableAmbientProbe(SphericalHarmonicsL2 probe)
        {
            float dcTerm = 0f;
            for (int channel = 0; channel < 3; channel++)
            {
                for (int coefficient = 0; coefficient < 9; coefficient++)
                {
                    float value = probe[channel, coefficient];
                    if (!(math.abs(value) <= MaxProbeCoefficient)) return false; // also rejects NaN and ±Inf
                }
                if (probe[channel, 0] < 0f) return false;
                dcTerm += probe[channel, 0];
            }
            return dcTerm > 1e-6f;
        }

        /// <summary>
        /// Ensures at least one directional light is present in the scene, creating one if none exists.
        /// Runs in BOTH render modes — unlike the ambient probe (<see cref="EnsureEnvironmentLighting"/>),
        /// which stays Unlit-gated, the light itself is unconditional: URP Lit needs it for full lighting,
        /// and the unlit fill-extrusion twin reads its DIRECTION for a cheap half-Lambert so 3D buildings
        /// don't render as flat solid blocks. Fills and lines ignore it (no face normal to react to).
        /// </summary>
        /// <remarks>Test seam: <c>internal</c> (not <c>private</c>) so the bootstrap tooth
        /// (<c>InternalsVisibleTo</c>) can call it directly without going through the MonoBehaviour
        /// <see cref="Start"/> lifecycle. See <c>FillExtrusion_UnlitForwardPass.hlsl</c> for the shading
        /// side that consumes this light.</remarks>
        internal static Light EnsureDirectionalLight()
        {
            var existing = Object.FindAnyObjectByType<Light>();
            if (existing != null && existing.type == LightType.Directional)
                return existing;

            var lightGo = new GameObject("MapDirectionalLight");
            var light   = lightGo.AddComponent<Light>();
            light.type                 = LightType.Directional;
            light.intensity            = 1.0f;
            // Explicit: buildings cast shadows, and a light without them hides every backend's shadow declaration.
            // Only the bootstrap light gets it; a host-supplied light keeps the host's own shadow setting.
            light.shadows              = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            Debug.Log("[MapHost] Created directional light (none found in scene).");
            return light;
        }
    }
}