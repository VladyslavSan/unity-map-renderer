// Unit-level teeth for CoverKeyGate (UMR-112 §4.3 T1/T2). No MapView/Tick — the gate is exercised
// directly, since its whole point is to be independently testable off TileManager.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Tile;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class CoverKeyGateTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom, double heading = 0.0,
            double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, heading, tilt);

        private static TileManager.TileSelectionConfig Cfg(double viewportX, double viewportY)
            => new TileManager.TileSelectionConfig { FramingViewportPx = new double2(viewportX, viewportY) };

        // ── T2a ──────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void FreshGate_IsDirty()
        {
            var gate = new CoverKeyGate();

            // Checked BEFORE any MarkStaleIfMoved call: its own `!_initialised` clause would also force
            // dirty true and mask a dropped `_dirty = true` initializer if this ran second.
            Assert.IsTrue(gate.IsDirty,
                "a newly constructed gate must read dirty immediately — this pins the HEAD `_dirty = true` " +
                "field initializer directly, not via MarkStaleIfMoved's own fallback.");

            var cam = Cam(10, 20, 5.0);
            var cfg = Cfg(800, 600);
            gate.MarkStaleIfMoved(in cam, in cfg);

            Assert.IsTrue(gate.IsDirty,
                "a newly constructed, never-committed, never-invalidated gate must be dirty as soon as " +
                "MarkStaleIfMoved runs — this pins the HEAD `_dirty = true` initializer together with the " +
                "`!_initialised` clause, so the first Tick always recomputes.");
        }

        // ── T2b ──────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void StaysDirty_UntilCommitted()
        {
            var gate = new CoverKeyGate();
            var cam  = Cam(10, 20, 5.0, heading: 30, tilt: 15);
            var cfg  = Cfg(800, 600);

            gate.Commit(in cam, in cfg);
            Assert.IsFalse(gate.IsDirty, "precondition: Commit must clear dirty.");

            gate.Invalidate();
            Assert.IsTrue(gate.IsDirty, "Invalidate must mark the gate dirty immediately, before any " +
                "MarkStaleIfMoved call observes it.");

            gate.MarkStaleIfMoved(in cam, in cfg); // same camera/viewport as the Commit — no movement
            Assert.IsTrue(gate.IsDirty,
                "a gate that treats Invalidate as 'reset the key and recompute from it' would read clean " +
                "here, because the camera genuinely has not moved. Invalidate must force dirty regardless.");
        }

        // ── T1 ───────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void DoesNotRecompute_WhenNothingMoved()
        {
            var gate = new CoverKeyGate();
            var cam  = Cam(10, 20, 5.0, heading: 30, tilt: 15);
            var cfg  = Cfg(800, 600);
            gate.Commit(in cam, in cfg);

            gate.MarkStaleIfMoved(in cam, in cfg);

            Assert.IsFalse(gate.IsDirty, "an identical camera and viewport must not mark the gate dirty.");
        }

        /// <summary>Perturbs exactly ONE of the seven framing inputs and asserts the gate goes dirty. A
        /// gate that forgot one comparison passes a single-field test and fails this one, run over all
        /// seven — see §4.3 T1.</summary>
        [TestCase(0, TestName = "MarksDirty_WhenLongitudeMoves")]
        [TestCase(1, TestName = "MarksDirty_WhenLatitudeMoves")]
        [TestCase(2, TestName = "MarksDirty_WhenZoomMoves")]
        [TestCase(3, TestName = "MarksDirty_WhenHeadingMoves")]
        [TestCase(4, TestName = "MarksDirty_WhenTiltMoves")]
        [TestCase(5, TestName = "MarksDirty_WhenViewportWidthMoves")]
        [TestCase(6, TestName = "MarksDirty_WhenViewportHeightMoves")]
        public void MarksDirty_WhenExactlyOneFieldMoves(int fieldIndex)
        {
            var gate = new CoverKeyGate();
            var cam  = Cam(10, 20, 5.0, heading: 30, tilt: 15);
            var cfg  = Cfg(800, 600);
            gate.Commit(in cam, in cfg);

            CameraProperties                   movedCam = cam;
            TileManager.TileSelectionConfig    movedCfg = cfg;
            switch (fieldIndex)
            {
                case 0: movedCam = Cam(11, 20, 5.0, heading: 30, tilt: 15); break;
                case 1: movedCam = Cam(10, 21, 5.0, heading: 30, tilt: 15); break;
                case 2: movedCam = Cam(10, 20, 6.0, heading: 30, tilt: 15); break;
                case 3: movedCam = Cam(10, 20, 5.0, heading: 31, tilt: 15); break;
                case 4: movedCam = Cam(10, 20, 5.0, heading: 30, tilt: 16); break;
                case 5: movedCfg = Cfg(801, 600); break;
                case 6: movedCfg = Cfg(800, 601); break;
            }

            gate.MarkStaleIfMoved(in movedCam, in movedCfg);

            Assert.IsTrue(gate.IsDirty, $"field index {fieldIndex} moved from the committed key — the gate must go dirty.");
        }
    }
}
