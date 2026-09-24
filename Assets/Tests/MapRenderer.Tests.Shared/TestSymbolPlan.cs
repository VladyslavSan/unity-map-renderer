// Lets a render/snapshot fixture drive the PRODUCTION Tick(in SceneFrame, SymbolGatherPlan, ...) overload from
// its SymbolTileBuffer: the store-and-bake half of SymbolSubsystem's per-frame work.
//
// Non-obvious why: it owns no camera, RenderTexture, atlas or material, because each fixture captured its
// baseline against its own. SymbolGatherParityTests pins the gather against an independent oracle.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolTileBuffer.CopySymbolInto

namespace MapRenderer.Tests
{
    /// <summary>
    /// Builds a production <see cref="SymbolGatherPlan"/> from a flat <see cref="SymbolTileBuffer"/> build
    /// buffer, grouping by <see cref="ShapedSymbol.TileKey"/> exactly as a real per-tile symbol build would.
    /// Dispose releases the store's baked blocks and the plan's native lists.
    /// </summary>
    internal sealed class TestSymbolPlan : IDisposable
    {
        private readonly SymbolTileStore _store = new SymbolTileStore(cacheCap: 64);
        private readonly SymbolGatherPlan _plan = new SymbolGatherPlan();
        private readonly IProjection _projection;
        private int _version;

        public TestSymbolPlan(IProjection projection) => _projection = projection;

        /// <summary>
        /// Re-seed from <paramref name="buffer"/> and return the refilled plan. Every record is classified
        /// Keep — the tile-coverage cull is a separate concern with its own fixtures, and a Drop here would
        /// silently remove a symbol the caller expects to render.
        /// </summary>
        /// <param name="buffer">The build buffer, in emission order — one record per shaped symbol (dense).</param>
        /// <param name="slotCount">Render-layer slot count; must cover every record's
        /// <see cref="ShapedSymbol.MaterialIndex"/>. Fixtures with no layer list pass 1 (slot 0).</param>
        /// <param name="coverageFadingTiles">Tile keys the coverage cull classified <c>Fade</c> — still
        /// resident and still drawn, easing out rather than popping.</param>
        /// <param name="droppedTiles">Tile keys classified <c>Drop</c>: the record stays in the plan, MASKED, so
        /// <see cref="CollectedCount"/> counts it but nothing stages it.</param>
        /// <param name="departingTiles">Tile keys whose records are DEPARTING (left cover, fading out). It
        /// feeds the <see cref="SymbolGatherPlan.Departing"/> field that production's IsDeparting fills.</param>
        public SymbolGatherPlan Build(SymbolTileBuffer buffer, int slotCount = 1,
            HashSet<long> coverageFadingTiles = null, HashSet<long> droppedTiles = null,
            HashSet<long> departingTiles = null)
        {
            _store.Clear();

            // One block per distinct tile, baked against that tile's render origin — the same grouping the
            // production per-tile build produces, and what makes AnchorLocal a real RTC offset rather than 0.
            var byTile = new Dictionary<long, List<int>>();
            var order = new List<long>();

            int symbolCount = buffer?.Symbols.Count ?? 0;
            for (int i = 0; i < symbolCount; i++)
            {
                long tileKey = buffer.Symbols[i].TileKey;
                if (!byTile.TryGetValue(tileKey, out List<int> bucket))
                {
                    bucket = new List<int>();
                    byTile.Add(tileKey, bucket);
                    order.Add(tileKey);
                }
                bucket.Add(i);
            }

            foreach (long tileKey in order)
            {
                TileId tile = SymbolTileKey.Unpack(tileKey);
                var key = new SymbolTileStore.Key("s", tile);
                int gen = _store.BeginBuild(key);

                // Each record's pooled spans address the SOURCE buffer by (start,count), so the per-tile
                // sub-buffer COPIES them and re-offsets the spans.
                var sub = new SymbolTileBuffer();
                foreach (int index in byTile[tileKey]) TestSymbolTileBuffer.CopySymbolInto(sub, buffer, index);

                SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                    sub, slotCount, TileRenderOrigin.Project(tile, _projection));
                if (!_store.CompleteBuild(key, gen, block))
                    throw new InvalidOperationException($"store rejected the build for tile {tile.Z}/{tile.X}/{tile.Y}");
            }

            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            // Non-obvious why: the reconciler always dedups on the FIXED CanonicalGridMeters grid, whatever is
            // passed, so 0 would misread as "dedup off". Two point symbols within 4 m that share (layer, text,
            // icon) MERGE; a fixture that asserts CollectedCount against its input sees that as a failed
            // precondition.
            _store.CollectInto(blockId, localIndex, isDeparting, CrossTileSymbolKey.CanonicalGridMeters, out _);

            var decisions = new List<byte>(blockId.Count);
            for (int i = 0; i < blockId.Count; i++)
            {
                long tk = _store.OrderedBlocks[blockId[i]].TileKey;
                decisions.Add(droppedTiles != null && droppedTiles.Contains(tk) ? SymbolTileCoverageFilter.Drop
                    : coverageFadingTiles != null && coverageFadingTiles.Contains(tk) ? SymbolTileCoverageFilter.Fade
                    : SymbolTileCoverageFilter.Keep);
                if (departingTiles != null && departingTiles.Contains(tk)) isDeparting[i] = 1;
            }

            // The plan object is reused across calls, so the version must move or GatherIntoMirror's memo
            // would serve the previous frame's mirror.
            _plan.Build(blockId, localIndex, isDeparting, decisions, _store.OrderedBlocks, ++_version);
            return _plan;
        }

        /// <summary>How many symbols the collect actually yielded — a fixture asserts this against its input
        /// count so a silently-dropped symbol shows up as a precondition failure, not as missing ink.</summary>
        public int CollectedCount => _plan.WinnerCount;

        public void Dispose()
        {
            _plan.Dispose();
            _store.Clear();
        }
    }
}
