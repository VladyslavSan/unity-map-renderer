// Render-state-and-capture layer. Owns QualitySettings/RenderSettings save-apply-restore (per test)
// and a Capture() facility; never a camera, a MapView, or a scene.

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Saves <c>QualitySettings</c>/<c>RenderSettings.ambient*</c> before EACH test and restores the
    /// entry values after EACH test. Provides <see cref="Capture"/> — a render-and-readback a test
    /// calls on demand, from a camera the TEST supplies.
    /// </summary>
    public abstract class VisualTestFixture : BaseTestFixture
    {
        /// <summary>The render globals a fixture wants applied for its tests. A <c>null</c> member is
        /// left untouched — the fixture still saves and restores it either way.</summary>
        public struct RenderState
        {
            public int?         QualityLevel { get; set; }
            public AmbientMode? AmbientMode  { get; set; }
            public Color?       AmbientLight { get; set; }
            public bool?        Fog          { get; set; }
        }

        /// <summary>Render globals this fixture applies. Default applies nothing.</summary>
        protected virtual RenderState State => default;

        private RenderState _saved;

        // Sealed: no subclass overrides OnSetUp/OnTearDown any more, State is the only extension
        // point, so sealing makes a mistaken override a compile error instead of a silent no-op.
        protected sealed override void OnSetUp()
        {
            base.OnSetUp();
            _saved = Current();
            Apply(State);
        }

        protected sealed override void OnTearDown()
        {
            try
            {
                Apply(_saved);
            }
            finally
            {
                base.OnTearDown();
            }
        }

        /// <summary>The render globals as they stand right now — every member set, so
        /// <see cref="Apply"/>ing the result restores everything.</summary>
        private static RenderState Current() => new RenderState
        {
            QualityLevel = QualitySettings.GetQualityLevel(),
            AmbientMode  = RenderSettings.ambientMode,
            AmbientLight = RenderSettings.ambientLight,
            Fog          = RenderSettings.fog,
        };

        /// <summary>Applies every member of <paramref name="state"/> that is set; a <c>null</c>
        /// member is left alone. Quality FIRST: <c>SetQualityLevel</c> can itself touch
        /// <c>RenderSettings</c>, so an explicit assignment must come after it or risk being
        /// clobbered.</summary>
        private static void Apply(RenderState state)
        {
            if (state.QualityLevel.HasValue) QualitySettings.SetQualityLevel(state.QualityLevel.Value, false);
            if (state.AmbientMode.HasValue)  RenderSettings.ambientMode  = state.AmbientMode.Value;
            if (state.AmbientLight.HasValue) RenderSettings.ambientLight = state.AmbientLight.Value;
            if (state.Fog.HasValue)          RenderSettings.fog          = state.Fog.Value;
        }

        /// <summary>
        /// Renders <paramref name="camera"/> to an off-screen target and reads it back. Callable more
        /// than once per test; each call is independent and disposes its own render target. Returns
        /// the frame and asserts nothing — a later layer (coverage, a golden-image diff) consumes it.
        /// </summary>
        /// <param name="camera">The camera to render — owned by the caller, not this fixture.</param>
        /// <param name="px">Square render-target size.</param>
        /// <returns>The captured frame.</returns>
        private protected VisualFrame Capture(Camera camera, int px = 512)
        {
            using var snapshot = new SnapshotRenderer(px, px);
            snapshot.Render(camera);

            Color bg = camera.backgroundColor;
            return new VisualFrame(
                snapshot.Pixels, px, px,
                new Color32(ToByteChannel(bg.r), ToByteChannel(bg.g), ToByteChannel(bg.b), 255),
                parsedStyle: null, mapView: null, camera);
        }

        // Round-half-up, not Color32's implicit truncating conversion, so the background byte matches what
        // the camera's float background colour names.
        private static byte ToByteChannel(float c) => (byte)(Mathf.Clamp01(c) * 255f + 0.5f);

        /// <summary>What a top-down snapshot camera should frame. World units, not device pixels —
        /// <c>ViewSize</c> is the visible (width, height), not Unity's half-height orthographicSize.
        /// </summary>
        public struct CameraSettings
        {
            public float2 ViewSize   { get; set; }
            public Color  Background { get; set; }

            /// <summary>World distance from the subject. <c>null</c> uses
            /// <see cref="StandardDistance"/>. NOT scaffolding despite the orthographic projection:
            /// a lit shader reads the camera's world position (<c>_WorldSpaceCameraPos</c>), so
            /// moving this changes shading even though it changes nothing else on screen.</summary>
            public float? Distance { get; set; }
        }

        /// <summary>The distance 15 of 16 measured snapshot rigs already use.</summary>
        public const float StandardDistance = 200f;

        /// <summary>
        /// Builds a disabled, top-down orthographic camera framing <see cref="CameraSettings.ViewSize"/>
        /// world units at <see cref="CameraSettings.Distance"/>, solid-cleared to
        /// <see cref="CameraSettings.Background"/>. Far-clip IS scaffolding (be able to see the
        /// geometry) derived from distance; distance itself is not — see its member doc. The caller
        /// owns and destroys the returned GameObject; this fixture never keeps a camera of its own.
        /// </summary>
        /// <param name="settings">What to frame, from how far, and what background to clear to.</param>
        /// <returns>The camera's GameObject and the disabled <see cref="Camera"/> on it.</returns>
        protected (GameObject go, Camera camera) BuildCamera(CameraSettings settings)
        {
            float distance = settings.Distance ?? StandardDistance;

            var go     = new GameObject("VisualTestFixture_Camera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, distance, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = settings.ViewSize.y * 0.5f;
            camera.aspect             = settings.ViewSize.x / settings.ViewSize.y;
            camera.farClipPlane       = distance * 5f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = settings.Background;
            camera.enabled            = false;
            return (go, camera);
        }
    }
}
