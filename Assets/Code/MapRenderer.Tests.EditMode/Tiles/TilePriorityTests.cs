// Engine-free: no UnityEngine references — pure MapRenderer.Core math. Shared verbatim with
// Tools/core-tests (single source of truth — see core-tests.csproj).
//
// T6 (tile-load smoothness plan §3.5): TilePriority.Key/SortByPriority must be monotonic in each
// metric (center strictly before a far corner), deterministic (same input twice → identical order), and
// the sort must actually place the center-most tile first. These are the FAST Core-only teeth; the
// TileManager-level "which tile builds first" (T1) and "strategies diverge under tilt" (T5) teeth live
// in TileManagerLoadPriorityTests (Unity-only — need the real Tick/PumpPending seam).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TilePriorityTests
    {
        private static readonly WebMercatorProjection Proj = new WebMercatorProjection();

        private static TilePriorityContext ContextFor(GeoCoordinate3D lookAt, TilePriorityStrategy strategy,
            double zoom = 10.0, double heading = 0.0, double tilt = 0.0)
        {
            var cam = new CameraProperties(lookAt, zoom, heading, tilt);
            return TilePriorityContext.From(in cam, new double2(1024, 1024), Proj, strategy);
        }

        /// <summary>The tile whose CENTER coincides with lon/lat, so <see cref="TilePriority.Key"/> reads
        /// ~0 for it under either strategy at tilt=0 (camera directly overhead its own look-at).</summary>
        private static GeoCoordinate3D LookAtCenterOf(TileId tile)
        {
            double2 ll = tile.ToLonLat(0.5, 0.5, 1.0);
            return new GeoCoordinate3D { Longitude = ll.x, Latitude = ll.y, Altitude = 0 };
        }

        [Test]
        public void Key_CenterTile_IsSmallerThan_FarCornerTile_BothStrategies()
        {
            var center = new TileId { Z = 8, X = 128, Y = 96 };
            var corner = new TileId { Z = 8, X = center.X + 50, Y = center.Y };
            GeoCoordinate3D lookAt = LookAtCenterOf(center);

            foreach (TilePriorityStrategy strategy in new[]
                     { TilePriorityStrategy.GroundDistanceToLookAt, TilePriorityStrategy.CameraDistance })
            {
                TilePriorityContext ctx = ContextFor(lookAt, strategy);
                double centerKey = TilePriority.Key(in center, in ctx);
                double cornerKey = TilePriority.Key(in corner, in ctx);
                Assert.Less(centerKey, cornerKey,
                    $"strategy={strategy}: the tile AT lookAt must rank strictly before a distant corner tile.");
            }
        }

        [Test]
        public void Key_IsStrictlyMonotonic_AlongAStraightLineOfTiles_BothStrategies()
        {
            var center = new TileId { Z = 9, X = 200, Y = 150 };
            GeoCoordinate3D lookAt = LookAtCenterOf(center);

            foreach (TilePriorityStrategy strategy in new[]
                     { TilePriorityStrategy.GroundDistanceToLookAt, TilePriorityStrategy.CameraDistance })
            {
                TilePriorityContext ctx  = ContextFor(lookAt, strategy);
                double               prev = -1.0;
                for (int dx = 0; dx <= 10; dx++)
                {
                    var    t   = new TileId { Z = center.Z, X = center.X + dx, Y = center.Y };
                    double key = TilePriority.Key(in t, in ctx);
                    Assert.Greater(key, prev,
                        $"strategy={strategy}: key must strictly increase with distance (dx={dx}).");
                    prev = key;
                }
            }
        }

        [Test]
        public void SortByPriority_PutsCenterTileFirst_AndIsDeterministicAcrossRuns()
        {
            var center = new TileId { Z = 7, X = 64, Y = 48 };
            GeoCoordinate3D     lookAt = LookAtCenterOf(center);
            TilePriorityContext ctx    = ContextFor(lookAt, TilePriorityStrategy.GroundDistanceToLookAt);

            List<TileId> Build() => new List<TileId>
            {
                new TileId { Z = 7, X = 64 + 20, Y = 48 },     // far
                new TileId { Z = 7, X = 64,      Y = 48 },     // AT lookAt — must sort to the head
                new TileId { Z = 7, X = 64 - 5,  Y = 48 + 5 }, // mid
                new TileId { Z = 7, X = 64 + 1,  Y = 48 },     // near
            };

            List<TileId> listA = Build();
            var          keysA = new double[1]; // deliberately under-sized — SortByPriority must grow it
            TilePriority.SortByPriority(listA, ref keysA, in ctx);

            List<TileId> listB = Build();
            var          keysB = new double[listB.Count];
            TilePriority.SortByPriority(listB, ref keysB, in ctx);

            Assert.AreEqual(center, listA[0], "the tile at lookAt must sort to the head of the list.");
            CollectionAssert.AreEqual(listA, listB,
                "same input + same context, sorted twice, must produce IDENTICAL order (determinism).");
        }

        [Test]
        public void SortByPriority_TiebreaksEqualKeysByTileId_Deterministically()
        {
            // Two tiles at the SAME zoom, mirrored across lookAt (X-20 vs X+20, same Y) are equidistant
            // under GroundDistanceToLookAt at tilt=0 (a symmetric ground metric) — their keys tie exactly,
            // so the sort must fall back to a deterministic TileId (Z,X,Y) tiebreak rather than leaving
            // the outcome to happenstance input order.
            var center = new TileId { Z = 6, X = 32, Y = 32 };
            GeoCoordinate3D     lookAt = LookAtCenterOf(center);
            TilePriorityContext ctx    = ContextFor(lookAt, TilePriorityStrategy.GroundDistanceToLookAt);

            var left  = new TileId { Z = 6, X = center.X - 4, Y = center.Y };
            var right = new TileId { Z = 6, X = center.X + 4, Y = center.Y };

            var listForward = new List<TileId> { right, left };
            var keysForward = new double[2];
            TilePriority.SortByPriority(listForward, ref keysForward, in ctx);

            var listReversed = new List<TileId> { left, right };
            var keysReversed = new double[2];
            TilePriority.SortByPriority(listReversed, ref keysReversed, in ctx);

            CollectionAssert.AreEqual(listForward, listReversed,
                "an exact key tie must resolve to the SAME order regardless of input order (TileId tiebreak).");
        }
    }
}
