// Unity EditMode only — SymbolTileLabelStore / SymbolGatherPlan / SymbolTileLabelBlock touch
// Unity.Collections. NOT registered in core-tests.csproj.
//
// Lets a render/snapshot fixture drive the PRODUCTION Tick(in SceneFrame, SymbolGatherPlan, ...) overload
// from the same plain List<LabelInstance> it already builds, instead of the demo Tick(..., labels, ...)
// overload. Motivation: the label render fixtures were exercising a path production does not run — the
// two converge on the same native mirror, but only the plan side is what ships.
//
// This is the store-and-bake half of what SymbolLabelSubsystem does per frame (bake a block per tile,
// collect winners, fill the plan). It deliberately does NOT own a camera, RenderTexture, atlas or
// material: each fixture's baseline was captured against its own, and swapping those in would move every
// baseline for reasons unrelated to which Tick overload ran.
//
// Equivalence is not assumed — SymbolPlanMirrorParityTests pins that a plan built here fills the native
// mirror field-for-field identically to the demo batch path over the same labels.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Builds a production <see cref="SymbolGatherPlan"/> from a flat label list, grouping by
    /// <see cref="LabelInstance.TileKey"/> exactly as a real per-tile symbol build would.
    /// Dispose releases the store's baked blocks and the plan's native lists.
    /// </summary>
    internal sealed class TestSymbolPlan : IDisposable
    {
        private readonly SymbolTileLabelStore _store = new SymbolTileLabelStore(cacheCap: 64);
        private readonly SymbolGatherPlan _plan = new SymbolGatherPlan();
        private readonly IProjection _projection;
        private int _version;

        public TestSymbolPlan(IProjection projection) => _projection = projection;

        /// <summary>
        /// Re-seed from <paramref name="labels"/> and return the refilled plan. Every label is classified
        /// Keep — the tile-coverage cull is a separate concern with its own fixtures, and a Drop here would
        /// silently remove a label the caller expects to render.
        /// </summary>
        /// <param name="slotCount">Render-layer slot count; must cover every label's
        /// <see cref="LabelInstance.MaterialIndex"/>. Fixtures with no layer list pass 1 (slot 0).</param>
        /// <param name="coverageFadingTiles">Tile keys the coverage cull classified <c>Fade</c> — still
        /// resident and still drawn, easing out rather than popping. This is how the production path
        /// expresses what the retired batch builder took as its <c>coverageFadingTiles</c> set.</param>
        /// <param name="droppedTiles">Tile keys classified <c>Drop</c> — D1 keeps the record resident in
        /// the plan and MASKS it downstream, so it is counted by <see cref="CollectedCount"/> but not
        /// staged.</param>
        /// <param name="departingTiles">Tile keys whose records are marked DEPARTING (the tile has left
        /// cover and is fading out rather than popping). Production fills this from the store's
        /// per-record <c>IsDeparting</c> flag; stamping it here is the plan-path equivalent of the
        /// retired batch builder's <c>activeCount</c> knob, and reaches the same
        /// <see cref="SymbolGatherPlan.Departing"/> field downstream reads.</param>
        public SymbolGatherPlan Build(IReadOnlyList<LabelInstance> labels, int slotCount = 1,
            HashSet<long> coverageFadingTiles = null, HashSet<long> droppedTiles = null,
            HashSet<long> departingTiles = null)
        {
            _store.Clear();

            // One block per distinct tile, baked against that tile's render origin — the same grouping the
            // production per-tile build produces, and what makes AnchorLocal a real RTC offset rather than 0.
            //
            // NULL entries are kept, not skipped. A null is a real shape here: production tile label lists
            // carry them as gaps (a feature slot that produced no label), the baker is null-slot-safe, and
            // the reconciler skips them at collect. Dropping them would silently close the gap and destroy
            // the index-mapping-across-gaps property LabelProjectionJobTests exists to pin. A null carries no
            // TileKey, so it joins the group of the label before it — leading nulls join the first group
            // created, which keeps them inside a block rather than discarding them.
            var byTile = new Dictionary<long, List<LabelInstance>>();
            var order = new List<long>();
            var leadingNulls = 0;
            long currentTileKey = 0;
            bool haveCurrent = false;

            for (int i = 0; i < (labels?.Count ?? 0); i++)
            {
                LabelInstance label = labels[i];
                if (label == null)
                {
                    if (!haveCurrent) { leadingNulls++; continue; }
                    byTile[currentTileKey].Add(null);
                    continue;
                }

                long tileKey = label.TileKey;
                if (!byTile.TryGetValue(tileKey, out List<LabelInstance> bucket))
                {
                    bucket = new List<LabelInstance>();
                    byTile.Add(tileKey, bucket);
                    order.Add(tileKey);
                    for (int n = 0; n < leadingNulls; n++) bucket.Add(null);
                    leadingNulls = 0;
                }
                bucket.Add(label);
                currentTileKey = tileKey;
                haveCurrent = true;
            }

            foreach (long tileKey in order)
            {
                TileId tile = LabelTileKey.Unpack(tileKey);
                var key = new SymbolTileLabelStore.Key("s", tile);
                int gen = _store.BeginBuild(key);
                SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(
                    byTile[tileKey], slotCount, TileRenderOrigin.Project(tile, _projection));
                if (!_store.CompleteBuild(key, gen, byTile[tileKey], block))
                    throw new InvalidOperationException($"store rejected the build for tile {tile.Z}/{tile.X}/{tile.Y}");
            }

            var collected = new List<LabelInstance>();
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            // The param only GATES dedup on/off — the plan-aware overload dropped its no-dedup branch, so the
            // reconciler always dedups on the FIXED CrossTileLabelKey.CanonicalGridMeters (4 m) grid whatever is
            // passed. Passing the canonical value says so honestly; passing 0 would read as "dedup off" and be
            // wrong. Consequence for a caller: two point labels within 4 m that share (layer, text, icon) MERGE.
            // CollectedCount is the guard — a fixture asserting it against its input count sees the merge as a
            // failed precondition rather than as silently missing ink.
            _store.CollectInto(collected, blockId, localIndex, isDeparting,
                CrossTileLabelKey.CanonicalGridMeters, out _);

            var decisions = new List<byte>(collected.Count);
            for (int i = 0; i < collected.Count; i++)
            {
                long tk = collected[i].TileKey;
                decisions.Add(droppedTiles != null && droppedTiles.Contains(tk) ? LabelTileCoverageFilter.Drop
                    : coverageFadingTiles != null && coverageFadingTiles.Contains(tk) ? LabelTileCoverageFilter.Fade
                    : LabelTileCoverageFilter.Keep);
                if (departingTiles != null && departingTiles.Contains(tk)) isDeparting[i] = 1;
            }

            // The plan object is reused across calls, so the version must move or GatherIntoMirror's memo
            // would serve the previous frame's mirror.
            _plan.Build(blockId, localIndex, collected, isDeparting, decisions, _store.OrderedBlocks, ++_version);
            return _plan;
        }

        /// <summary>How many labels the collect actually yielded — a fixture asserts this against its input
        /// count so a silently-dropped label shows up as a precondition failure, not as missing ink.</summary>
        public int CollectedCount => _plan.WinnerCount;

        public void Dispose()
        {
            _plan.Dispose();
            _store.Clear();
        }
    }
}
