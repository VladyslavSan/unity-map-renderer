using System;
using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Reads what the Entities backend draws after each Update and asserts the tile hold's invariants: a previously drawn area that is still in
    /// the view stays drawn (closed view), no slot draws a tile with its ancestor (nested-free), and one Update makes at most one show and one hide call
    /// (batched visibility).
    /// </summary>
    internal sealed class DrawnTileWatcher
    {
        private readonly MapViewComponent _view;
        private readonly int _backgroundSlot;
        private readonly int _fillSlot;
        private readonly HashSet<TileId> _shownBackground = new();
        private readonly HashSet<TileId> _shownFill       = new();
        private readonly HashSet<TileId> _previousShown   = new();
        private readonly List<TileId>    _cover           = new();
        private readonly List<TileId>    _scratch         = new();

        public int  Ticks;
        public bool SawDeeperThanCover;

        /// <param name="backgroundSlot">The layer slot of the background, which has one quad per tile and so measures coverage.</param>
        /// <param name="fillSlot">A second slot, checked for nesting only.</param>
        public DrawnTileWatcher(MapViewComponent view, int backgroundSlot, int fillSlot)
        {
            _view           = view;
            _backgroundSlot = backgroundSlot;
            _fillSlot       = fillSlot;
        }

        /// <summary>The background tiles drawn after the last <see cref="Check"/>.</summary>
        public HashSet<TileId> ShownBackground => _shownBackground;

        /// <summary>The cover: the tiles with a record in it, and the ones still waiting to be admitted.</summary>
        public List<TileId> Cover
        {
            get
            {
                _cover.Clear();
                _view.TileManager.CollectLoadedTileIds(_scratch);
                _cover.AddRange(_scratch);
                _view.CollectDesiredTileIds(_scratch);
                _cover.AddRange(_scratch);
                return _cover;
            }
        }

        /// <summary>Call once after every Update.</summary>
        public void Check()
        {
            Ticks++;
            var renderer = _view.EntitiesRenderer();
            renderer.DrawnTilesAtSlot(_backgroundSlot, _shownBackground);
            renderer.DrawnTilesAtSlot(_fillSlot, _shownFill);
            List<TileId> cover = Cover;

            AssertNoNesting(_shownBackground, "background");
            AssertNoNesting(_shownFill, "fill");
            Assert.LessOrEqual(_view.VisibilityBatchesLastTick(), 2, "Batched visibility: one show call and one hide call per Update, however many tiles swap.");

            foreach (TileId was in _previousShown) AssertStillShown(was, cover);

            foreach (TileId shown in _shownBackground)
                if (!cover.Contains(shown)) SawDeeperThanCover = true;

            _previousShown.Clear();
            foreach (TileId t in _shownBackground) _previousShown.Add(t);
        }

        /// <summary>The drawn background tiles are exactly the cover tiles.</summary>
        public void AssertShowsExactlyTheCover()
        {
            var cover = new HashSet<TileId>(Cover);
            Assert.IsTrue(cover.SetEquals(_shownBackground),
                $"a settled view draws the cover and nothing else. {Describe(Cover)} coverTiles=[{string.Join(" ", cover)}]");
        }

        /// <summary>A one-line summary of the drawn tiles and the hold counters, for a failure message.</summary>
        public string Describe(List<TileId> cover)
        {
            TileTelemetrySnapshot t = _view.TileManager.Telemetry;
            var text = new System.Text.StringBuilder(
                $"shown={_shownBackground.Count} cover={cover.Count} held={t.HeldTileCount} bridge={t.BridgeTileCount} releaseQueue={_view.ReleaseQueueDepth()} previous=[");
            foreach (TileId tile in _previousShown) text.Append(tile).Append(' ');
            text.Append("] now=[");
            foreach (TileId tile in _shownBackground) text.Append(tile).Append(' ');
            return text.Append(']').ToString();
        }

        private void AssertStillShown(TileId was, List<TileId> cover)
        {
            bool related = false;
            foreach (TileId c in cover)
            {
                if (!c.Equals(was) && !TileAncestry.IsStrictAncestor(c, was)) continue;
                related = true;
                Assert.IsTrue(AreaShown(was, _shownBackground),
                    $"Closed view broke at Update {Ticks}: tile {was} was shown and its area is still in the view, under {c}. {Describe(cover)}");
            }

            if (related) return;
            foreach (TileId c in cover)
                if (TileAncestry.IsStrictAncestor(was, c))
                    Assert.IsTrue(AreaShown(c, _shownBackground),
                        $"Closed view broke at Update {Ticks}: cover tile {c} lay inside shown tile {was} and is not drawn now. {Describe(cover)}");
        }

        private static void AssertNoNesting(HashSet<TileId> shown, string slot)
        {
            foreach (TileId a in shown)
            foreach (TileId b in shown)
                Assert.IsFalse(TileAncestry.IsStrictAncestor(a, b), $"Nested-free broke: {slot} slot draws {a} together with its descendant {b}.");
        }

        /// <summary>True iff <paramref name="tile"/>'s whole area is drawn: the tile, an ancestor, or descendants that add up to it.</summary>
        private static bool AreaShown(TileId tile, HashSet<TileId> shown)
        {
            if (shown.Contains(tile)) return true;
            for (TileId up = tile; up.Z > 0;)
            {
                up = TileAncestry.Parent(up);
                if (shown.Contains(up)) return true;
            }

            double area = 0.0;
            foreach (TileId d in shown)
                if (TileAncestry.IsStrictAncestor(tile, d)) area += Math.Pow(0.25, d.Z - tile.Z);
            return area >= 1.0 - 1e-9;
        }
    }
}
