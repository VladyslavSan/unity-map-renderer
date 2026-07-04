using UnityEngine;
using MapRenderer.Core.View;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S85: a dev/debug readout surface for <see cref="MapView.CaptureTelemetry"/> — mirrors
    /// <see cref="CameraControlPanel"/>'s shape (serialized read-only display fields, overwritten every
    /// frame). Reports only; never a control knob (decision 8) — no field here is ever read back into the
    /// map.
    ///
    /// <para><b>Play-mode only:</b> <see cref="MapView"/> is constructed only on the runtime
    /// <c>Bootstrapper.Wire</c>/<c>Start</c> path, so in edit mode <see cref="MapViewComponent.Camera"/> is
    /// null. <see cref="Update"/> null-guards <see cref="Map"/>/<see cref="MapViewComponent.Camera"/> and
    /// no-ops cleanly when unwired (the same guard <see cref="CameraControlPanel"/> uses).</para>
    /// </summary>
    public sealed class MapTelemetryPanel : MonoBehaviour
    {
        [Tooltip("The MapView whose live telemetry this panel reads (set in the Inspector).")]
        public MapViewComponent Map;

        [Header("Telemetry (live — overwritten each frame)")]
        [Tooltip("Size of the selected cover (the frustum cover, no pad ring). THE headline number.")]
        public int VisibleTileCount;

        [Tooltip("Near-field grid width — distinct tile X among the cover's tiles at CoverMaxZoom.")]
        public int CoverColumns;

        [Tooltip("Near-field grid height — distinct tile Y among the cover's tiles at CoverMaxZoom.")]
        public int CoverRows;

        [Tooltip("The finest / near-field target zoom the traversal caps at.")]
        public int SelectionZoom;

        [Tooltip("True when the cover spans more than one zoom level (routine under ScreenSpaceLod).")]
        public bool IsMixedZoom;

        [Tooltip("The cover's coarsest (far) zoom level.")]
        public int CoverMinZoom;

        [Tooltip("The cover's finest (near) zoom level.")]
        public int CoverMaxZoom;

        [Tooltip("The camera's fractional (MapLibre) zoom.")]
        public double FractionalZoom;

        [Tooltip("Number of currently loaded-or-loading (tile, source) records.")]
        public int LoadedTileCount;

        [Tooltip("Loaded records not yet Built — the load-progress lag.")]
        public int PendingTileCount;

        [Tooltip("Loaded records whose tessellation is complete but consume is budget-deferred.")]
        public int ConsumeBacklog;

        [Tooltip("In-flight network fetches, summed across every source pipeline.")]
        public int InFlightFetches;

        [Tooltip("Lifetime count of tiles released while their tessellation was still in-flight.")]
        public int ReleasedMidFlightCount;

        [Tooltip("Lifetime count of tiles released while their fetch was still in-flight.")]
        public int ReleasedMidFetchCount;

        [Tooltip("Lifetime count of genuine (non-cancellation) fetch errors.")]
        public int FetchErrorCount;

        private void Update() => Tick();

        /// <summary>
        /// One readout refresh. <c>internal</c> so an EditMode test can drive it deterministically (the
        /// MonoBehaviour game loop does not run under the EditMode test runner). Production calls it from
        /// <see cref="Update"/>; it is not part of the public surface.
        /// </summary>
        internal void Tick()
        {
            // Play-mode only: null-guard until the bootstrapper wires the camera (edit mode has no MapCamera).
            if (Map == null || Map.Camera == null) return;

            TileTelemetrySnapshot snap = Map.View.CaptureTelemetry();

            VisibleTileCount       = snap.VisibleTileCount;
            CoverColumns           = snap.CoverColumns;
            CoverRows              = snap.CoverRows;
            SelectionZoom          = snap.SelectionZoom;
            IsMixedZoom            = snap.IsMixedZoom;
            CoverMinZoom           = snap.CoverMinZoom;
            CoverMaxZoom           = snap.CoverMaxZoom;
            FractionalZoom         = snap.FractionalZoom;
            LoadedTileCount        = snap.LoadedTileCount;
            PendingTileCount       = snap.PendingTileCount;
            ConsumeBacklog         = snap.ConsumeBacklog;
            InFlightFetches        = snap.InFlightFetches;
            ReleasedMidFlightCount = snap.ReleasedMidFlightCount;
            ReleasedMidFetchCount  = snap.ReleasedMidFetchCount;
            FetchErrorCount        = snap.FetchErrorCount;
        }
    }
}
