using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Unity.View.Camera;

using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App
{
    /// <summary>
    /// A debug harness that flies the camera to <b>stress the tile-load pipeline</b>: it orbits the look-at
    /// around a fixed city centre while sweeping the zoom (<see cref="ZoomAt"/>, <see cref="LookAtAt"/>), so
    /// the tile select → fetch → build → consume → evict churn is reproducible. It replaces the input
    /// <see cref="Controller"/> in the stress scene. It does no logging, because <c>Debug.Log</c> allocates
    /// and perturbs the measurement; read the <see cref="MapTelemetryPanel"/> on the same object instead.
    /// </summary>
    public sealed class TileLoadStressDriver : MonoBehaviour
    {
        [Tooltip("The MapView whose camera this driver flies. Leave empty — it is found automatically on " +
                 "Start (FindAnyObjectByType), so this component works on any GameObject in a wired scene.")]
        public MapViewComponent Map;

        [Header("Master switch")]
        [Tooltip("Off ⇒ the driver idles and the camera is left untouched.")]
        public bool SweepEnabled = true;

        [Header("Zoom sweep (triangle wave)")]
        [Range(0f, 22f)]
        [Tooltip("The zoomed-OUT end of the sweep (fewer, larger tiles).")]
        public float MinZoom = 10f;

        [Range(0f, 22f)]
        [Tooltip("The zoomed-IN end of the sweep (dense tiles; OpenFreeMap 'liberty' maxzoom is ~14, where " +
                 "the load cost and the residual stall live). A wide MinZoom↔MaxZoom span crosses the most " +
                 "tile-zoom boundaries ⇒ the harshest load/evict/rebuild churn.")]
        public float MaxZoom = 14f;

        [Tooltip("Seconds for ONE full zoom sweep Min → Max → Min (one triangle period). Shorter = harsher.")]
        public float ZoomPeriodSeconds = 8f;

        [Header("Pan orbit (around a fixed city centre)")]
        [Tooltip("Orbit-centre latitude (WGS-84 degrees). Default: Berlin.")]
        public double CenterLatitude = 52.52;

        [Tooltip("Orbit-centre longitude (WGS-84 degrees). Default: Berlin.")]
        public double CenterLongitude = 13.405;

        [Tooltip("Orbit radius in degrees — how far the look-at circles from the centre. ~0.05° ≈ 5.5 km " +
                 "N-S; at street zoom that sweeps across many tile columns each lap. 0 ⇒ no pan (zoom only).")]
        public double PanRadiusDegrees = 0.05;

        [Tooltip("Seconds for ONE full orbit around the centre. 0 ⇒ no pan (zoom only).")]
        public float PanPeriodSeconds = 12f;

        // Motion clock (seconds since enable).
        private float _elapsed;

        private void Start()
        {
            // Self-wiring safety net: an empty serialized reference still resolves, so the component needs no
            // Inspector dragging (and a hand-authored scene needn't get the object reference exactly right).
            if (Map == null) Map = FindAnyObjectByType<MapViewComponent>();
        }

        private void Update() => Tick(Time.deltaTime);

        /// <summary>
        /// Advance the motion clock by <paramref name="dt"/> seconds and push the resulting zoom + look-at
        /// onto the live camera. <c>internal</c> so an EditMode test drives it deterministically (the
        /// MonoBehaviour game loop does not run under the EditMode test runner).
        /// </summary>
        internal void Tick(float dt)
        {
            if (!SweepEnabled || Map == null || Map.Camera == null) return;

            _elapsed += dt;

            double zoom          = ZoomAt(_elapsed, MinZoom, MaxZoom, ZoomPeriodSeconds);
            var (latitude, longitude) = LookAtAt(
                _elapsed, CenterLatitude, CenterLongitude, PanRadiusDegrees, PanPeriodSeconds);

            // One patch through the same seam the interactive controller uses: absolute zoom + look-at.
            Map.Camera.Apply(new CameraPropertiesUpdate { Zoom = zoom, Latitude = latitude, Longitude = longitude });
        }

        /// <summary>
        /// Pure triangle wave: maps <paramref name="elapsedSeconds"/> to a zoom in
        /// <c>[<paramref name="min"/>, <paramref name="max"/>]</c>, sweeping min → max → min over one
        /// <paramref name="periodSeconds"/>. Deterministic and engine-free. A non-positive period pins to
        /// <paramref name="min"/>; a reversed range is swapped.
        /// </summary>
        public static double ZoomAt(double elapsedSeconds, double min, double max, double periodSeconds)
        {
            if (periodSeconds <= 0.0) return min;
            if (max < min) (min, max) = (max, min);

            double phase = math.frac(elapsedSeconds / periodSeconds); // [0,1)
            double tri   = phase < 0.5 ? phase * 2.0 : 2.0 - phase * 2.0; // 0 → 1 → 0 (min → max → min)
            return min + (max - min) * tri;
        }

        /// <summary>
        /// Pure circular orbit: maps <paramref name="elapsedSeconds"/> to a look-at (latitude, longitude)
        /// circling the centre at <paramref name="radiusDegrees"/>, one full lap per
        /// <paramref name="periodSeconds"/>. Longitude is scaled by <c>1/cos(latitude)</c> so the orbit stays
        /// visually circular as longitude lines compress away from the equator. Deterministic and engine-free.
        /// A non-positive period or radius pins to the centre (no pan).
        /// </summary>
        public static (double latitude, double longitude) LookAtAt(
            double elapsedSeconds, double centerLatitude, double centerLongitude,
            double radiusDegrees, double periodSeconds)
        {
            if (periodSeconds <= 0.0 || radiusDegrees <= 0.0)
                return (centerLatitude, centerLongitude);

            double theta = 2.0 * math.PI_DBL * math.frac(elapsedSeconds / periodSeconds);
            double latitude  = centerLatitude + radiusDegrees * math.sin(theta);
            double lonScale  = math.max(math.cos(math.radians(centerLatitude)), 1e-3); // guard the pole limit
            double longitude = centerLongitude + radiusDegrees * math.cos(theta) / lonScale;
            return (latitude, longitude);
        }
    }
}
