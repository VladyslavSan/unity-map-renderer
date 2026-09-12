// Unit-level teeth for TilePrioritySorter (UMR-112 §4.3 T4/T5). No MapView/Tick — the sorter is
// exercised directly, since its whole point is to be independently testable off TileManager.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Unity.Rendering.Tile;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TilePrioritySorterTests
    {
        /// <summary>A projection whose <see cref="Project"/> always returns the origin, so every tile's
        /// <see cref="TilePriority.Key"/> is identically 0 — the tiebreak becomes the WHOLE sort decision.
        /// Only the geometry entry point is real; every other member is unused by <c>TilePriority.Key</c>
        /// and throws.</summary>
        private readonly struct ZeroProjection : IProjection
        {
            public ProjectedPoint ProjectPoint(in GeoCoordinate geo) => new ProjectedPoint { World = default, Up = default };
            public double3 Project(in GeoCoordinate geo) => double3.zero;
            public double3 UpAt(in GeoCoordinate geo) => new double3(0, 1, 0);
            public float3x3 TangentBasisAt(in GeoCoordinate geo) => float3x3.identity;
            public double MetersPerUnit => 1.0;
            public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
            {
                renderCentre = default; radius = 0.0; return false;
            }
            public double MaxRefineAngleRad => double.PositiveInfinity;
            public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in MapRenderer.Core.View.Camera.CameraProperties camera)
                => throw new NotSupportedException("ZeroProjection is a priority-math-only test double.");
            public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in MapRenderer.Core.View.Camera.CameraProperties camera)
                => throw new NotSupportedException("ZeroProjection is a priority-math-only test double.");
            public double ClampValidLatitude(double latitudeDegrees) => latitudeDegrees;
            public bool IsFinitePlanarWorld => true;
            public GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in MapRenderer.Core.View.Camera.CameraProperties camera)
                => throw new NotSupportedException("ZeroProjection is a priority-math-only test double.");
        }

        private static TilePriorityContext ZeroContext()
            => new TilePriorityContext(new ZeroProjection(), double3.zero, float3x3.identity, double3.zero,
                TilePriorityStrategy.GroundDistanceToLookAt);

        private static TileManager.LoadedKey Key(int z, int x, int y, int slot)
            => new TileManager.LoadedKey(new TileId { Z = z, X = x, Y = y }, slot);

        private static readonly TilePriorityContext Zero = ZeroContext();

        // ── T4 ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every entry has an identical (zero) priority key, so the sort result is decided
        /// ENTIRELY by the (Z, X, Y, Slot) tiebreak — the reason <see cref="TilePrioritySorter"/>'s private
        /// <c>IsAfter</c> exists. A sorter that drops the <c>Slot</c> tiebreak produces a nondeterministic
        /// paint order no count-based test observes.</summary>
        [Test]
        public void SortsStably_OnEqualKeys()
        {
            var list = new List<TileManager.LoadedKey>
            {
                Key(5, 3, 2, 1),
                Key(5, 1, 5, 0),
                Key(3, 9, 9, 9),
                Key(5, 1, 5, 1),
                Key(5, 1, 2, 0),
            };

            new TilePrioritySorter().Sort(list, in Zero);

            var expected = new List<TileManager.LoadedKey>
            {
                Key(3, 9, 9, 9),
                Key(5, 1, 2, 0),
                Key(5, 1, 5, 0),
                Key(5, 1, 5, 1),
                Key(5, 3, 2, 1),
            };

            CollectionAssert.AreEqual(expected, list,
                "with every priority key tied at 0, the sort must order strictly by (Z, X, Y) then Slot.");
        }

        // ── T5 ───────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>_keys</c> exists solely to avoid a per-sort allocation. Warms the scratch buffer to
        /// steady size with one discarded call (a list past the initial 64-capacity forces the growth
        /// path), then meters a second call — a sorter that allocates a fresh array per call is
        /// functionally correct and destroys the reason the field exists.</summary>
        [Test]
        public void Sort_DoesNotAllocate()
        {
            var list = new List<TileManager.LoadedKey>(100);
            for (int i = 0; i < 100; i++)
                list.Add(Key(5, i, 100 - i, i & 3));

            var sorter = new TilePrioritySorter();
            sorter.Sort(list, in Zero); // warm-up — grows the scratch buffer past 64

            Assert.That(() => sorter.Sort(list, in Zero), Is.Not.AllocatingGCMemory(),
                "TilePrioritySorter.Sort must not allocate once its scratch buffer has grown to steady size.");
        }
    }
}
