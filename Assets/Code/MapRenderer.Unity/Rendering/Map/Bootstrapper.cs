using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using Unity.Mathematics;

// S51: HttpDataSource removed from Core; HTTP moved to Unity layer as UnityWebRequestDataSource.

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S41 map bootstrap and wire-up hub (<c>Map.Bootstrapper</c>). Lives on the <b>MapRoot</b>
    /// GameObject, which is the scene owner of the map subsystem (<see cref="View"/> +
    /// <see cref="Controller"/>); this component assembles and starts that subsystem.
    ///
    /// <para>The Main Camera is a separate plain camera GameObject (tagged <c>MainCamera</c>); this
    /// component finds it at startup via <c>Camera.main</c> and wires it into <see cref="Controller"/>.</para>
    ///
    /// <para>Two wiring entry points are provided:</para>
    /// <list type="bullet">
    ///   <item><see cref="Start"/> — the runtime entry (MonoBehaviour lifecycle; reads inspector fields,
    ///   builds the data source + style, calls <see cref="Wire"/>).</item>
    ///   <item><see cref="Wire(GameObject, Camera)"/> — static, testable entry that performs the component
    ///   graph wiring without touching the file system or HTTP. This is what EditMode tests drive.</item>
    /// </list>
    ///
    /// <para>If the Main Camera is absent, <see cref="Wire"/> logs a warning and returns without NRE
    /// (the map won't pan/zoom but also won't crash).</para>
    ///
    /// Inspector fields:
    ///   <see cref="StyleUri"/>          — the style document URI (the single source of truth — S83b);
    ///                                     its sources[] declare the tiles (inline or via TileJSON).
    ///   <see cref="InitialLatitude"/>   — initial map center latitude.
    ///   <see cref="InitialLongitude"/>  — initial map center longitude.
    ///   <see cref="InitialZoom"/>       — initial zoom level.
    ///
    /// Rendered layer types: fill, line (with casing), symbol (text), and background. Raster and
    /// fill-extrusion layers present in a style are silently skipped until those features land.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    [RequireComponent(typeof(MapViewComponent))]
    public sealed class Bootstrapper : MonoBehaviour
    {
        /// <summary>Wires <see cref="VerifiedDisposable.LeakReporter"/> to <see cref="Debug.LogError"/> once
        /// at startup, so a <see cref="VerifiedDisposable"/>-derived instance finalized without Dispose()
        /// (a resource leak) surfaces in the Editor/Player log instead of Core's engine-free no-op default.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void WireLeakReporter() => VerifiedDisposable.LeakReporter = Debug.LogError;

        // S83b: the STYLE is the single source of truth. One URI points at the style document; its
        // sources[] declare every data source (a vector source carries either inline tiles[] or a TileJSON
        // url, resolved at SetStyle time — S83a). The dated/hardcoded tile-URL template is gone: the
        // openmaptiles source's TileJSON (https://tiles.openfreemap.org/planet) supplies the current tile
        // path, so it no longer rots when OpenFreeMap rotates the version segment.
        //
        // Default: the shipped OpenFreeMap "liberty" style under StreamingAssets (ships in standalone
        // builds), loaded via file:// for a fully-offline default. A leading scheme (file://, http://,
        // https://) is used verbatim; a bare relative path is resolved under StreamingAssets as file://.
        // To use the live OpenFreeMap style instead, set this to
        // "https://tiles.openfreemap.org/styles/liberty".
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
        public double InitialZoom = 13.0; // S93: 512 convention shifts zoom numbers −1 (old 14 → 13 = same view)

        [Tooltip("Vertical field-of-view (degrees) for the perspective camera — the initial camera lens " +
                 "(carried in CameraProperties, pushed to the Unity camera).")]
        public double VerticalFovDeg = 60.0;

        [Header("Session (launch-time constants)")]
        [Tooltip("Cap the frame rate to the display refresh rate (VSync) on Start. Off = render " +
                 "uncapped (1000+ FPS), which needlessly drives the GPU and heats the machine.")]
        public bool CapFrameRateToRefreshRate = true;

        [Tooltip("Render on a 3D globe (SphericalProjection) instead of the flat Web-Mercator plane. " +
                 "Launch-time only — the projection is a session constant (S91-C). First cut: a spherical-cap " +
                 "tile cover at low-to-mid zoom; true pan/zoom input on the globe is a later stage.")]
        public bool UseGlobe = false;

        private void Start()
        {
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

            // 2. Wire camera + components only — the style drives the sources. Wire builds the MapView over
            //    the main camera; SetStyle (step 4) loads the multi-source pipeline. The projection is the
            //    launch-time session constant (S91-C) — globe or planar.
            Wire(gameObject, Camera.main, initialView,
                 UseGlobe ? new SphericalProjection() : null);

            // 3. Ensure a directional light exists in the scene (for URP Lit fill shader).
            EnsureDirectionalLight();

            // 3.5. Ensure the scene has a real ambient probe — a scene whose environment lighting was
            //      never generated leaves fully-shadowed faces (e.g. fill-extrusion walls facing away
            //      from the sun) pure black, since URP Lit's indirect term is the only fill they get.
            //      After EnsureDirectionalLight so a procedural skybox convolves against the real sun.
            EnsureEnvironmentLighting();

            // 4. Resolve the style URI and load it. SetStyle is async (fetches the style doc + any
            //    TileJSON); fire-and-forget — tiles stream in as it completes. The dated hardcoded tile
            //    path is gone: the openmaptiles source's TileJSON supplies the current path (S83a).
            var mapView = GetComponent<MapViewComponent>();
            if (mapView != null)
            {
                // Device-independent on-screen tile size: derive the device-pixel ratio from the reported
                // panel density (dpr = Screen.dpi / 160, the mdpi golden standard) so a selection tile is a
                // constant physical size across densities. Two caveats, both real: Start runs in PLAY MODE
                // as well, where Screen.dpi has been OBSERVED to report the density of whichever monitor the
                // Editor window sits on — so play-testing a mobile build takes this machine's density, not
                // the device's — and that is an observation rather than a contract, because Screen.dpi's
                // Editor behaviour is undocumented (Unity documents the divergence for Screen.width/height
                // and says nothing for dpi). Whether the mdpi derivation is right at all
                // is Stage 4b's open question: mdpi is an Android convention, and no runtime API exposes the
                // platform's own scale factor to managed user code (docs/device-pixel-ratio-design.md §4b).
                // Tests drive Wire (not Start) and keep the serialized ratio.
                mapView.Config.DevicePixelRatio = DeviceScaling.DevicePixelRatioFromDpi(Screen.dpi);
            }
            string styleUri = ResolveStyleUri(StyleUri);
            if (mapView != null)
                mapView.SetStyle(styleUri).Forget();

            // 5. Apply initial camera framing (perspective, altitude-from-zoom, overhead at pitch=0).
            //    Delegates to ApplyCameraTransform so frame-0 framing matches the runtime path and
            //    InitialZoom is respected (zoom 2 → continent scale, zoom 16 → street scale).
            // Frame-0 belt-and-suspenders: the MapCamera built in Wire already framed the camera; re-applying
            // via the Controller (when present) is harmless and keeps the interactive path identical.
            var ctrl = GetComponent<Controller>();
            if (ctrl != null && ctrl.Camera != null)
                ctrl.ApplyCameraTransform(initialView);

            // Sky background on the main camera directly, so it applies with OR without an input Controller
            // (the stress scene omits the Controller). Camera.main is the same camera Wire framed.
            // E3: this clear is the camera background ABOVE THE HORIZON under tilt (a style's tile cover
            // never reaches past the far plane there) and the no-style default; a style's declared
            // `background` layer now paints the GROUND via BackgroundRenderLayer (a real quad, §3.6) —
            // this hardcoded colour does not compete with it and is unchanged byte-for-byte.
            var mainCam = Camera.main;
            if (mainCam != null && mainCam.backgroundColor == default)
                mainCam.backgroundColor = new Color(0.85f, 0.95f, 1.0f, 1f); // light blue sky

            Debug.Log($"[Bootstrapper] Started. styleUri={styleUri}, zoom={InitialZoom}, " +
                      $"center=({InitialLatitude:F2},{InitialLongitude:F2}).");
        }

        /// <summary>
        /// S83b: resolves <paramref name="styleUri"/> to a loadable URI. A leading scheme
        /// (<c>file://</c>, <c>http://</c>, <c>https://</c>) is used verbatim; a bare relative path is
        /// resolved under <c>Application.streamingAssetsPath</c> as a <c>file://</c> URI (so the shipped
        /// liberty.json loads fully offline in a standalone build).
        /// </summary>
        private static string ResolveStyleUri(string styleUri)
        {
            if (string.IsNullOrEmpty(styleUri)) styleUri = "Fixtures/liberty.json";
            if (styleUri.StartsWith("file://") || styleUri.StartsWith("http://") || styleUri.StartsWith("https://"))
                return styleUri;
            // Bare path → under StreamingAssets, as a file:// URI.
            return "file://" + Path.Combine(Application.streamingAssetsPath, styleUri);
        }

        // ── Static wire-up entry (testable without Play mode) ─────────────────────────────────────

        /// <summary>
        /// Wires the map subsystem on <paramref name="root"/>: finds the <see cref="Controller"/> and
        /// <see cref="MapViewComponent"/>, sets <c>Controller.Camera</c>/<c>Controller.Map</c>, and builds
        /// the MapView over the camera (from <paramref name="initialView"/>). Data is loaded separately via
        /// <see cref="MapViewComponent.SetStyle(string,System.Threading.CancellationToken)"/>.
        ///
        /// <para>If <paramref name="camera"/> is null the method logs a warning and returns without
        /// NRE (missing camera is handled gracefully).</para>
        ///
        /// <para>This is the single wiring graph entry point. Both the runtime <see cref="Start"/>
        /// and EditMode wiring tests call this method.</para>
        /// </summary>
        /// <param name="root">The MapRoot GameObject (must carry MapView + Controller).</param>
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
                Debug.LogWarning("[Bootstrapper.Wire] root is null — wire-up skipped.");
                return;
            }

            if (camera == null)
            {
                Debug.LogWarning("[Bootstrapper.Wire] No camera provided (Camera.main is null). " +
                                 "Controller will not drive any camera. Wire-up skipped for camera.");
                // We still continue to initialise MapView and set Map on the controller.
            }

            var mapView = root.GetComponent<MapViewComponent>();
            if (mapView == null)
            {
                Debug.LogWarning("[Bootstrapper.Wire] No MapViewComponent found on root — wire-up skipped.");
                return;
            }

            // The input Controller is OPTIONAL: the essential graph is the MapCamera onto MapView. A scene
            // may omit it — e.g. an automated stress scene driven entirely by a script (TileLoadStressDriver)
            // with no user to feed mouse/keyboard/touch — and the map still wires and renders.
            var ctrl = root.GetComponent<Controller>();

            // ── Wire the MapCamera onto MapView (the essential graph, controller-independent) ────────────
            // MapView owns the (single) MapCamera; SetCamera builds the MapView (valid from that point — an
            // empty map until SetStyle loads data). MapCamera wraps a real Unity camera, so with no camera
            // we skip: no MapView is built (the scene always has a main camera in practice).
            if (camera != null)
            {
                // FOV + viewport come from initialView / the camera; the altitude multiplier (from the
                // Controller when present, else 1) and the DPI ratio are side config. Seed DPR at construction
                // (S92 D1) so the ctor's frame-0 SyncToCamera frames the logical viewport too — LateUpdate
                // keeps it live thereafter.
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

                // S74: wire the touch source alongside the desktop controller (same write seam).
                // GetComponent-or-AddComponent so no committed scene edit is required; the scene validator
                // only flags missing scripts, not runtime-added ones.
                var touch = root.GetComponent<TouchController>() ?? root.AddComponent<TouchController>();
                touch.camera = camera;
                touch.Map    = mapView;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures <see cref="RenderSettings.ambientProbe"/> is non-degenerate, so lit map geometry gets
        /// indirect fill wherever the directional light's <c>N·L</c> is ≤ 0 (otherwise those faces —
        /// e.g. fill-extrusion walls facing away from the sun — render pure black; URP Lit has no other
        /// source of light there). Guarded: only recomputes environment lighting when the probe's DC/L0
        /// term is (near-)zero, i.e. environment lighting was never generated for this scene — a host
        /// scene that already baked or generated its own probe is left untouched.
        ///
        /// <para>Called once, at startup (from <see cref="Start"/>, after the directional light is ensured
        /// so a procedural skybox convolves against the real sun). Nothing in this project changes the
        /// skybox or ambient settings after startup, so a single call suffices; a future time-of-day or
        /// style-driven-sky feature that mutates <see cref="RenderSettings"/> at runtime would own
        /// re-calling this.</para>
        /// </summary>
        internal static void EnsureEnvironmentLighting()
        {
            var probe = RenderSettings.ambientProbe;
            double dcTerm = math.abs(probe[0, 0]) + math.abs(probe[1, 0]) + math.abs(probe[2, 0]);
            if (dcTerm > 1e-6) return; // already has a real probe — don't clobber a host's baked lighting

            DynamicGI.UpdateEnvironment();
        }

        /// <summary>
        /// Ensures at least one directional light is present in the scene so the URP Lit fill shader
        /// produces visible output (not all-black). Creates one if none exists.
        /// </summary>
        private static void EnsureDirectionalLight()
        {
            var existing = Object.FindAnyObjectByType<Light>();
            if (existing != null && existing.type == LightType.Directional)
                return;

            var lightGo = new GameObject("MapDirectionalLight");
            var light   = lightGo.AddComponent<Light>();
            light.type                 = LightType.Directional;
            light.intensity            = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            Debug.Log("[Bootstrapper] Created directional light (none found in scene).");
        }
    }
}