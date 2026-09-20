using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Owns the cover-recompute staleness gate: tracks whether the camera/viewport moved since the
    /// last <see cref="Commit"/>, so a still camera skips the cover descent.</summary>
    internal sealed class CoverKeyGate
    {
        private double _lon;
        private double _lat;
        private double _zoom;
        private double _heading;
        private double _tilt;
        private double _viewportX;
        private double _viewportY;
        private bool   _initialised;

        /// <summary>Starts dirty so the first <see cref="MarkStaleIfMoved"/> always recomputes;
        /// <see cref="Commit"/> must run only after that recompute, never before.</summary>
        private bool _dirty = true;

        /// <summary>Whether the cover needs recomputing since the last <see cref="Commit"/>.</summary>
        public bool IsDirty => _dirty;

        /// <summary>The zoom recorded at the last <see cref="Commit"/>.</summary>
        public double LastZoom => _zoom;

        /// <summary>Forces the next <see cref="MarkStaleIfMoved"/> to report dirty, regardless of camera movement.</summary>
        public void Invalidate()
        {
            _dirty       = true;
            _initialised = false;
        }

        /// <summary>Marks the gate dirty if any framing input differs from the last <see cref="Commit"/>.
        /// Compares by EXACT <c>!=</c>, not a tolerance — a sub-tile camera nudge must still trip this, so
        /// the cover recompute keeps tracking it (pinned by <c>TileManagerLoadPriorityTests</c> and
        /// <c>TileLoadMeasurementTests</c>).</summary>
        public void MarkStaleIfMoved(in CameraProperties cam, in TileManager.TileSelectionConfig cfg)
        {
            if (!_initialised                     ||
                cam.LookAt.Longitude    != _lon       ||
                cam.LookAt.Latitude     != _lat       ||
                cam.Zoom                != _zoom      ||
                cam.Heading.Degrees     != _heading   ||
                cam.Tilt.Degrees        != _tilt      ||
                cfg.FramingViewportPx.x != _viewportX ||
                cfg.FramingViewportPx.y != _viewportY)
            {
                _dirty = true;
            }
        }

        /// <summary>Records the current framing as the last-known key and clears dirty. Must run only after
        /// the recompute this tick's dirty flag triggered.</summary>
        public void Commit(in CameraProperties cam, in TileManager.TileSelectionConfig cfg)
        {
            _lon         = cam.LookAt.Longitude;
            _lat         = cam.LookAt.Latitude;
            _zoom        = cam.Zoom;
            _heading     = cam.Heading.Degrees;
            _tilt        = cam.Tilt.Degrees;
            _viewportX   = cfg.FramingViewportPx.x;
            _viewportY   = cfg.FramingViewportPx.y;
            _initialised = true;

            _dirty = false;
        }
    }
}
