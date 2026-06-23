using System.IO;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
// S51: HttpDataSource removed from Core; HTTP moved to Unity layer as UnityWebRequestDataSource.

namespace MapRenderer.Unity
{
    /// <summary>
    /// S41 map-root bootstrap and wire-up hub. Lives on the <b>MapRoot</b> GameObject, which is the
    /// scene owner of the map subsystem (<see cref="MapView"/> + <see cref="MapController"/>).
    ///
    /// <para>The Main Camera is a separate plain camera GameObject (tagged <c>MainCamera</c>); this
    /// component finds it at startup via <c>Camera.main</c> and wires it into <see cref="MapController"/>.</para>
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
    ///   <see cref="TileUrlTemplate"/>   — HTTP URL template with {z}/{x}/{y} tokens.
    ///   <see cref="StyleAssetPath"/>    — path to the style JSON, relative to Assets/StreamingAssets/
    ///                                     (ships in builds; Editor falls back to Assets/).
    ///   <see cref="InitialLatitude"/>   — initial map center latitude.
    ///   <see cref="InitialLongitude"/>  — initial map center longitude.
    ///   <see cref="InitialZoom"/>       — initial zoom level.
    ///
    /// Rendered layer types: fill, line (with casing), and background. Symbol (labels/icons), raster,
    /// and fill-extrusion layers present in a style are silently skipped until those features land.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    [RequireComponent(typeof(MapView))]
    [RequireComponent(typeof(MapController))]
    public sealed class MapRoot : MonoBehaviour
    {
        // Demo data source: the free, no-key OpenFreeMap OSM tiles (OpenMapTiles schema) rendered with
        // the OpenFreeMap "liberty" style (Assets/StreamingAssets/Fixtures/liberty.json — under
        // StreamingAssets so it ships in standalone builds). The maplibre demotiles only
        // carry a single country-fill layer up to ~z5; OpenFreeMap goes to z14 with water/roads/landuse/
        // buildings. We render liberty's fill + line layers (water, landuse, the full road hierarchy with
        // casings, boundaries); symbol (labels), raster (hillshade) and fill-extrusion layers are skipped
        // until those features land. A simpler hand-authored alternative lives at Fixtures/openfreemap-
        // style.json.
        //
        // NOTE: the "20260614_080001_pt" segment is a DATED tile-set version that OpenFreeMap rotates
        // periodically. If tiles start 404ing, fetch the current path from the TileJSON:
        //   curl -s https://tiles.openfreemap.org/planet | jq -r '.tiles[0]'
        // (The robust fix — recommended before relying on this in CI/demos — is to read that TileJSON at
        // startup rather than hardcode the version segment.)
        [Tooltip("MVT tile URL template. Tokens: {z} {x} {y}. " +
                 "OpenFreeMap free OSM tiles (OpenMapTiles schema). Versioned path — see code note.")]
        public string TileUrlTemplate =
            "https://tiles.openfreemap.org/planet/20260614_080001_pt/{z}/{x}/{y}.pbf";

        [Tooltip("Path to the style document JSON, relative to Assets/StreamingAssets/ (so it ships in builds; " +
                 "Editor falls back to Assets/). OpenFreeMap 'liberty' style " +
                 "(water/landuse/roads-with-casing/boundaries; labels not yet rendered).")]
        public string StyleAssetPath = "Fixtures/liberty.json";

        [Tooltip("Initial map center latitude (decimal degrees, WGS-84).")]
        public double InitialLatitude = 52.52;   // Berlin

        [Tooltip("Initial map center longitude (decimal degrees, WGS-84).")]
        public double InitialLongitude = 13.405;  // Berlin

        [Tooltip("Initial zoom level (0 = world view). z14 is OpenFreeMap's maxzoom — densest data " +
                 "(buildings + full road network). Lower zooms thin out fast (z13 Berlin = 1 building).")]
        public double InitialZoom = 14.0;

        private void Start()
        {
            // 1. Load and parse the style document.
            StyleDocument style = LoadStyle();

            // 2. Create the tile data source (owned — MapView will dispose on OnDestroy).
            // S51: HttpDataSource removed from Core; UnityWebRequestDataSource is the production HTTP source.
            var source = new UnityWebRequestDataSource(TileUrlTemplate);

            // 3. Build the initial camera state.
            var initialView = new CameraProperties(
                new LookAtPoint(InitialLongitude, InitialLatitude, 0), InitialZoom, 0, 0);

            // 4. Wire: sets MapController.Camera, MapController.Map, calls MapView.Initialise,
            //    and wires the S45 CameraSystem + MapCamera.
            Wire(gameObject, Camera.main, source, initialView, ownsSource: true, style: style);

            // 5. Ensure a directional light exists in the scene (for URP Lit fill shader).
            EnsureDirectionalLight();

            // 6. Apply initial camera framing (perspective, altitude-from-zoom, overhead at pitch=0).
            //    Delegates to ApplyCameraTransform so frame-0 framing matches the runtime path and
            //    InitialZoom is respected (zoom 2 → continent scale, zoom 16 → street scale).
            var ctrl = GetComponent<MapController>();
            if (ctrl != null && ctrl.Camera != null)
            {
                ctrl.ApplyCameraTransform(initialView);
                if (ctrl.Camera.backgroundColor == default)
                    ctrl.Camera.backgroundColor = new Color(0.85f, 0.95f, 1.0f, 1f); // light blue sky
            }

            var mapView = GetComponent<MapView>();
            Debug.Log($"[MapRoot] Started. URL={TileUrlTemplate}, zoom={InitialZoom}, " +
                      $"center=({InitialLatitude:F2},{InitialLongitude:F2}), " +
                      $"style layers={mapView.Layers.FillCount} fill layers.");
        }

        // ── Static wire-up entry (testable without Play mode) ─────────────────────────────────────

        /// <summary>
        /// Wires the map subsystem on <paramref name="root"/>: finds <see cref="MapController"/> and
        /// <see cref="MapView"/> on <paramref name="root"/>, sets <c>MapController.Camera</c> and
        /// <c>MapController.Map</c>, and calls <see cref="MapView.Initialise"/> with <paramref name="source"/>
        /// and <paramref name="initialView"/>.
        ///
        /// <para>If <paramref name="camera"/> is null the method logs a warning and returns without
        /// NRE (missing camera is handled gracefully).</para>
        ///
        /// <para>This is the single wiring graph entry point. Both the runtime <see cref="Start"/>
        /// and EditMode wiring tests call this method.</para>
        /// </summary>
        /// <param name="root">The MapRoot GameObject (must carry MapView + MapController).</param>
        /// <param name="camera">The camera to drive; typically <c>Camera.main</c>.</param>
        /// <param name="source">The tile data source (HTTP, file, in-memory…). May be null in tests.</param>
        /// <param name="initialView">Initial camera state.</param>
        /// <param name="ownsSource">If true, MapView will dispose the source on OnDestroy.</param>
        /// <param name="style">Optional StyleDocument; null renders nothing.</param>
        public static void Wire(
            GameObject       root,
            Camera           camera,
            IDataSource      source      = null,
            CameraProperties initialView = default,
            bool             ownsSource  = false,
            StyleDocument    style       = null)
        {
            if (root == null)
            {
                Debug.LogWarning("[MapRoot.Wire] root is null — wire-up skipped.");
                return;
            }

            if (camera == null)
            {
                Debug.LogWarning("[MapRoot.Wire] No camera provided (Camera.main is null). " +
                                 "MapController will not drive any camera. Wire-up skipped for camera.");
                // We still continue to initialise MapView and set Map on the controller.
            }

            var mapView = root.GetComponent<MapView>();
            if (mapView == null)
            {
                Debug.LogWarning("[MapRoot.Wire] No MapView found on root — wire-up skipped.");
                return;
            }

            var ctrl = root.GetComponent<MapController>();
            if (ctrl == null)
            {
                Debug.LogWarning("[MapRoot.Wire] No MapController found on root — wire-up skipped.");
                return;
            }

            // Set the camera reference on the controller (may be null — guarded in Update).
            ctrl.Camera = camera;

            // ── S45/S50: Wire CameraSystem + MapCamera onto MapView (D4) ────────────────────────
            // MapView owns the (single) CameraSystem. Wire it BEFORE Initialise so the layer-record
            // build reads this fully-framed system (not a default one). When camera is null, MapCamera
            // sync is a no-op (guarded inside MapCamera.Sync).
            var cameraSystem = new CameraSystem(
                initialView,
                referenceViewportHeightPx: ctrl.ReferenceViewportHeightPx,
                verticalFovDeg:             ctrl.VerticalFovDeg);

            var mapCamera = camera != null
                ? new MapCamera(camera, ctrl.ReferenceViewportHeightPx, ctrl.VerticalFovDeg)
                : new MapCamera(null,   ctrl.ReferenceViewportHeightPx, ctrl.VerticalFovDeg);
            mapCamera.AltitudeMultiplier = ctrl.AltitudeMultiplier;

            mapView.SetCamera(mapCamera, cameraSystem);

            // Initialise MapView so the scheduler and layer records are built (keeps the wired camera).
            if (source != null)
                mapView.Initialise(source, initialView, ownsSource: ownsSource, style: style);

            // Wire the controller to the view.
            ctrl.Map = mapView;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads and parses the style document from <see cref="StyleAssetPath"/>.
        ///
        /// <para>Resolved against <c>Application.streamingAssetsPath</c> first: files under
        /// <c>Assets/StreamingAssets/</c> are the only loose files Unity copies verbatim into a built
        /// player, so this is what makes the style available at runtime in a standalone build. A raw file
        /// under <c>Assets/</c> read via <c>Application.dataPath</c> exists ONLY in the Editor (where
        /// dataPath = the project's <c>Assets</c> folder); in a build dataPath points inside the app bundle
        /// and the file is absent — which is why an earlier dataPath-based load rendered an empty map.</para>
        ///
        /// <para>An Editor/back-compat fallback to <c>Application.dataPath</c> is kept so any legacy path
        /// still resolves. Note: <c>File.ReadAllText</c> on streamingAssetsPath works on standalone
        /// (macOS/Windows/Linux) and in the Editor; Android/WebGL would need a UnityWebRequest read.</para>
        ///
        /// <para>Falls back to an empty StyleDocument if the file is not found (so the demo still launches).</para>
        /// </summary>
        private StyleDocument LoadStyle()
        {
            // StreamingAssets ships into the built player; the Editor-only dataPath/Assets copy does not.
            string fullPath = Path.Combine(Application.streamingAssetsPath, StyleAssetPath);
            if (!File.Exists(fullPath))
            {
                string editorPath = Path.Combine(Application.dataPath, StyleAssetPath);
                if (File.Exists(editorPath))
                    fullPath = editorPath;
            }

            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"[MapRoot] Style not found at {fullPath} " +
                                 $"(StreamingAssets: {Path.Combine(Application.streamingAssetsPath, StyleAssetPath)}). " +
                                 "MapView will render no fills until a style is loaded. " +
                                 "In a build, the style must live under Assets/StreamingAssets/.");
                return new StyleDocument();
            }

            try
            {
                string json = File.ReadAllText(fullPath);
                StyleDocument doc = StyleParser.Parse(json);
                Debug.Log($"[MapRoot] Loaded style: {doc.Name ?? "(unnamed)"}, " +
                          $"{doc.Layers.Count} layers.");
                return doc;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MapRoot] Failed to parse style at {fullPath}: {ex.Message}");
                return new StyleDocument();
            }
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
            light.type      = LightType.Directional;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            Debug.Log("[MapRoot] Created directional light (none found in scene).");
        }
    }
}
