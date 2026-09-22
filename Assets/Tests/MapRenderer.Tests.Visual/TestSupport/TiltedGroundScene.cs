// Unity EditMode only — real MapCamera + Camera/RenderTexture, the lit-ambient recipe every tilted line
// snapshot fixture uses.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// Stage T — the SHARED tilt-measurement harness. Extracted from LineProbeSymmetrySnapshotTests.BuildScene /
// SetupLitAmbient / RestoreAmbient / AssertGpuContext (S111), which already built exactly this camera-and-
// chrome recipe. CONTENT-AGNOSTIC on purpose: this class knows nothing about lines, styles, materials or
// symbols — the consumer attaches its own GameObject(s) — which is what lets it serve both the line/join
// consumer (T2/T3) and the symbol consumer (T4) from one harness.

#if UNITY_EDITOR
using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Configuration for <see cref="TiltedGroundScene"/>. A plain data carrier: object-initializer
    /// construction with field-initializer defaults (<c>docs/conventions-short.md</c>, "Data carriers:
    /// object-initializer construction"). Defaults are EXACTLY <c>LineProbeSymmetrySnapshotTests.BuildScene</c>'s
    /// values, so repointing that fixture onto this harness (Step 4) is a no-op wherever a default applies.
    /// </summary>
    internal sealed class TiltedGroundSceneConfig
    {
        // Plain { get; set; }, not init: MapRenderer.Tests.EditMode has no IsExternalInit polyfill of
        // its own (only Core and Unity define init-only members), so a test-owned carrier stays mutable
        // rather than adding a polyfill file for one fixture class — same call as
        // A6NonMvtDecoderTests.FixtureTileLayer and SymbolIconRenderSnapshotTests.SymbolInk.
        /// <summary>The whole point of the harness; 0 is the inert control every T tooth's RED-verification
        /// forces it to.</summary>
        public double TiltDegrees { get; set; } = 55.0;

        /// <summary>Heading 0 makes a world ±Z step a pure screen-y step. T2 relies on this AND asserts it
        /// (the single-row precondition in <c>LineProbeSymmetrySnapshotTests.WithRenderedRoad</c> /
        /// <c>TiltFixtureSelfTests.MeasureRoadBand</c>); T3 relies on it too but does NOT assert it — it
        /// measures along whatever screen direction the projection returns, so a heading ≠ 0 would silently
        /// change what it measures rather than fail loudly.</summary>
        public double HeadingDegrees { get; set; } = 0.0;

        public double Zoom { get; set; } = 8.0;

        public GeoCoordinate3D LookAt { get; set; } =
            new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 };

        public int SizePx { get; set; } = 512;

        public double DevicePixelRatio { get; set; } = 1.0;

        /// <summary>The line arms render on the shared dark-slate background; the symbol arm (T4) overrides
        /// this to white, because <c>WorldSymbolInkAnalysis.InkThreshold</c> reads dark ink on white.</summary>
        public Color BackgroundColor { get; set; } = new Color(0.05f, 0.05f, 0.08f, 1f);

        /// <summary>The line arms need the lit recipe (real PBR, a live viewDirectionWS); the symbol arm does
        /// not and turns this off.</summary>
        public bool LitAmbient { get; set; } = true;

        public float AltitudeMultiplier { get; set; } = 1f;

        /// <summary><c>null</c> ⇒ <see cref="MapCamera"/> supplies <see cref="WebMercatorProjection"/>, so
        /// P3/P4 can pass <c>SphericalProjection</c> later. NO T tooth exercises this — stated as a non-claim
        /// in the T design (§6).</summary>
        public IProjection Projection { get; set; } = null;
    }

    /// <summary>
    /// The shared tilt-measurement scene: a real <see cref="MapCamera"/> orbiting a look-at at a configurable
    /// tilt, an off-screen <see cref="RenderTexture"/>, and (optionally) the lit-ambient recipe. Owns the
    /// GPU-context guard and the identity-rebase <see cref="SceneFrame"/> convenience. Knows nothing about
    /// what gets rendered — the consumer builds and attaches its own content, then calls <see cref="Render"/>.
    /// </summary>
    internal sealed class TiltedGroundScene : IDisposable
    {
        public readonly TiltedGroundSceneConfig Config;
        public readonly GameObject   CameraGameObject;
        public readonly Camera       UnityCamera;
        public readonly RenderTexture ViewportRenderTexture;
        public readonly MapCamera    MapCam;

        /// <summary>Null when <see cref="TiltedGroundSceneConfig.LitAmbient"/> is false.</summary>
        public readonly GameObject LightGameObject;

        private readonly (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) _savedAmbient;

        /// <summary>World metres per device pixel at the look-at — the frame's ruler. §3.2: every world size
        /// a T fixture wants must derive from this, never a bare metre literal (a metre literal is sub-pixel
        /// at this pose — one device px is hundreds of metres at zoom 8 / lat 30).</summary>
        public double MetresPerDevicePixel => MapCam.MetresPerDevicePixel;

        private TiltedGroundScene(
            TiltedGroundSceneConfig config, GameObject cameraGameObject, Camera unityCamera,
            RenderTexture viewportRenderTexture, MapCamera mapCam, GameObject lightGameObject,
            (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) savedAmbient)
        {
            Config                = config;
            CameraGameObject      = cameraGameObject;
            UnityCamera           = unityCamera;
            ViewportRenderTexture = viewportRenderTexture;
            MapCam                = mapCam;
            LightGameObject       = lightGameObject;
            _savedAmbient         = savedAmbient;
        }

        /// <summary>Builds the scene: ambient/light (if <see cref="TiltedGroundSceneConfig.LitAmbient"/>) →
        /// camera + off-screen RT → <see cref="MapCamera"/>, in that order — verbatim from
        /// <c>LineProbeSymmetrySnapshotTests.SetupLitAmbient</c> / <c>BuildScene</c> (S111).
        ///
        /// <para><b>Safe by construction against a partial failure.</b> The ambient/light block mutates
        /// PROCESS-GLOBAL state that only <see cref="Dispose"/> restores — but nothing owns that restore
        /// until this method RETURNS. If <c>new RenderTexture</c> / <c>new CameraProperties</c> /
        /// <c>new MapCamera</c> below were to throw, the light and the ambient override would otherwise leak
        /// with no owner, corrupting every lit render for the rest of the batch process (the stale-shader-
        /// global genre <c>docs/line-rendering-design.md</c> §1.1 already recorded once). The <c>try</c>/
        /// <c>catch</c> below is this constructor being its own <c>Dispose</c> for the window before one
        /// exists.</para></summary>
        public static TiltedGroundScene Create(TiltedGroundSceneConfig config)
        {
            (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) savedAmbient = default;
            GameObject lightGo = null;

            if (config.LitAmbient)
            {
                savedAmbient = (
                    QualitySettings.GetQualityLevel(),
                    RenderSettings.ambientMode,
                    RenderSettings.ambientLight);
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

                lightGo = new GameObject("TiltedGroundScene_DirLight");
                lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                var light = lightGo.AddComponent<Light>();
                light.type      = LightType.Directional;
                light.intensity = 1f;
            }

            GameObject camGo = null;
            RenderTexture rt = null;
            try
            {
                camGo = new GameObject("TiltedGroundScene_Camera");
                var uCam = camGo.AddComponent<Camera>();

                rt = new RenderTexture(config.SizePx, config.SizePx, 24, RenderTextureFormat.ARGB32);
                uCam.targetTexture   = rt;
                uCam.clearFlags      = CameraClearFlags.SolidColor;
                uCam.backgroundColor = config.BackgroundColor;
                uCam.enabled         = false;

                var props = new CameraProperties(
                    config.LookAt, zoom: config.Zoom, heading: config.HeadingDegrees, tilt: config.TiltDegrees);
                var mapCam = new MapCamera(
                    uCam, props, config.AltitudeMultiplier, config.Projection, config.DevicePixelRatio);

                return new TiltedGroundScene(config, camGo, uCam, rt, mapCam, lightGo, savedAmbient);
            }
            catch
            {
                // Nothing has an owner yet (the constructor hasn't returned), so THIS is the cleanup —
                // restore the exact process-global state Dispose() would, and destroy whatever partially
                // built.
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (lightGo != null) UnityEngine.Object.DestroyImmediate(lightGo);
                if (config.LitAmbient)
                {
                    QualitySettings.SetQualityLevel(savedAmbient.quality, false);
                    RenderSettings.ambientMode  = savedAmbient.mode;
                    RenderSettings.ambientLight = savedAmbient.light;
                }
                throw;
            }
        }

        /// <summary>Renders <paramref name="snapshot"/>'s off-screen target off this scene's camera.
        ///
        /// <para>Re-syncs the <see cref="MapCamera"/> FIRST — not belt-and-braces: <c>SyncToCamera</c> is
        /// idempotent (<c>MapCamera.cs:181</c>) and its LAST act pushes <c>_MapFrameMetersPerDevicePixel</c>,
        /// which is PROCESS state (<c>docs/line-rendering-design.md</c> §1.1 records a fixture reading a ruler
        /// three zoom levels stale, left behind by an earlier fixture in the same batch). A scene that renders
        /// always pushes its own ruler immediately before rendering.</para></summary>
        public void Render(SnapshotRenderer snapshot)
        {
            MapCam.SyncToCamera();
            snapshot.Render(UnityCamera);
        }

        /// <summary>The identity-rebase <see cref="SceneFrame"/> for this scene's look-at: <c>SceneOriginRender
        /// = projection.Project(LookAt)</c>, <c>Rebase = float3x3.identity</c>.
        ///
        /// <para><b>THE NAME CARRIES THE CAVEAT.</b> Identity rebase is correct for Web-Mercator and is NOT
        /// spherical staging — it is the P2 lesson, made unusable-by-accident rather than merely commented.
        /// A spherical consumer (a future tooth passing <see cref="TiltedGroundSceneConfig.Projection"/> =
        /// <c>SphericalProjection</c>) must build its own frame; this method must not be renamed or wrapped
        /// in a way that hides that limitation.</para></summary>
        public SceneFrame BuildIdentityRebaseSceneFrame()
            => new SceneFrame
            {
                SceneOriginRender = MapCam.Projection.Project(Config.LookAt.Surface),
                Rebase            = float3x3.identity,
            };

        public void Dispose()
        {
            if (LightGameObject != null) UnityEngine.Object.DestroyImmediate(LightGameObject);
            if (Config.LitAmbient)
            {
                QualitySettings.SetQualityLevel(_savedAmbient.quality, false);
                RenderSettings.ambientMode  = _savedAmbient.mode;
                RenderSettings.ambientLight = _savedAmbient.light;
            }
            if (CameraGameObject != null) UnityEngine.Object.DestroyImmediate(CameraGameObject);
            if (ViewportRenderTexture != null)
            {
                ViewportRenderTexture.Release();
                UnityEngine.Object.DestroyImmediate(ViewportRenderTexture);
            }
        }
    }
}
#endif // UNITY_EDITOR
