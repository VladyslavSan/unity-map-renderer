// Unity-side (reads SymbolTileBlock's native columns — Core stays engine-free). Not engine-free itself: it
// reads the block's raw-order NativeArray columns directly, so it is compiled + run only by the Unity EditMode
// runner, not the engine-free core-tests fast loop.

using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>The pure, off-main cross-tile symbol dedup: <see cref="Run"/> reads an immutable
    /// <see cref="SymbolSnapshot"/> (its blocks pinned on the main thread) and fills a reused
    /// <see cref="SymbolReconcileResult"/>, touching no Unity API beyond the block's read-only NativeArray
    /// columns and no store state — so it runs safely on a thread-pool worker.
    ///
    /// <para>Non-reentrant: the reused <see cref="_dedup"/> index assumes the caller runs exactly one
    /// <see cref="Run"/> at a time. The scan order is byte-identical to the store's inline <c>CollectInto</c>, so
    /// the store/oracle suite is this method's oracle; winner identity is <c>(BlockId, LocalIndex)</c>.</para>
    /// </summary>
    internal sealed class SymbolReconciler
    {
        // Reused cross-tile dedup index (non-reentrant): key = the stable (quantized-anchor, layer, text-id,
        // icon-id) identity; value = the winner's zoom/tile-key tiebreak + its (blockId, localIndex). Alloc-free.
        private readonly Dictionary<DedupKey, DedupEntry> _dedup = new Dictionary<DedupKey, DedupEntry>();

        // ── Test seams (reached via InternalsVisibleTo, mirroring the store's / subsystem's internal seams) ──
        /// <summary>The thread <see cref="Run"/> last executed on — a test asserts it is off the main thread.
        /// Test-only.</summary>
        internal int LastRunThreadId;

        /// <summary>When non-null, the next <see cref="Run"/> blocks on this gate ONCE (then clears it), so a
        /// test can PARK the worker in flight (assert one-in-flight / apply-stale / pin lifetime) and release it
        /// deterministically. A teardown while this is held MUST open the gate first, or the drain hangs. Internal
        /// test-only.</summary>
        internal ManualResetEventSlim GateForTest;

        /// <summary>When true, the next <see cref="Run"/> throws once after writing a deliberately misaligned
        /// partial result — the tooth for swap-only-on-success (swapping a non-success result would feed that
        /// partial buffer to <c>SymbolGatherPlan.Build</c> and crash). Test-only.</summary>
        internal bool FaultNextRun;

        /// <summary>Dedup <paramref name="snapshot"/> into <paramref name="result"/>. Clears both first (a reused
        /// result/index must not leak a prior run), then runs the store's plan-aware scan.</summary>
        public void Run(SymbolSnapshot snapshot, SymbolReconcileResult result)
        {
            LastRunThreadId = Thread.CurrentThread.ManagedThreadId;
            ManualResetEventSlim gate = GateForTest;
            if (gate != null) { GateForTest = null; gate.Wait(); }

            result.Clear(); // reused result: clear all lists + ActiveCount before refilling
            _dedup.Clear(); // reused index: a prior run's winners must not survive into this one

            if (FaultNextRun)
            {
                FaultNextRun = false;
                // A misaligned buffer (BlockId longer than LocalIndex): a buggy pickup that swapped this
                // non-success result in would read LocalIndex out of range downstream and crash.
                result.BlockId.Add(0);
                throw new InvalidOperationException("SymbolReconciler injected fault (test) — partial misaligned result");
            }

            // ── ACTIVE scan: curved emit in scan order, points dedup into _dedup (one block per active tile). ──
            int count = snapshot.Count;
            for (int s = 0; s < count; s++)
            {
                TileSlice slice = snapshot.Slices[s];
                if (slice.IsDeparting) continue; // departing tiles are scanned in the SECOND pass, after the winner emit
                SymbolTileBlock block = slice.Block;
                if (block == null) continue;
                int blockId = result.OrderedBlocks.Count;
                result.OrderedBlocks.Add(block);
                int rawCount = block.Kinds.Length;
                for (int i = 0; i < rawCount; i++)
                {
                    if (block.Kinds[i] != SymbolPlacementKind.Point)
                    {
                        // Curved (line) symbols emit DURING the scan, in scan order — the plan entry rides in lockstep.
                        // (A curved symbol is never paired, so it needs no rider check.)
                        result.BlockId.Add(blockId); result.LocalIndex.Add(i); result.IsDeparting.Add(0);
                        continue;
                    }
                    // A rider carries no key of its own — its identity is its owner's; it rides with the winning
                    // owner below (see DedupEntry.HasRider), never looked up here.
                    if (block.PairRoles[i] == SymbolPairRole.Rider) continue;

                    var key = DedupKey.For(block.RepAnchor[i], block.MaterialIndexes[i], block.TextIds[i],
                        block.IconImageIds[i], CrossTileSymbolKey.CanonicalGridMeters);
                    long tileKey = block.TileKey;
                    int z = (int)(tileKey >> 44); // SymbolTileKey.Pack: z in the high bits (finest zoom wins)
                    if (!_dedup.TryGetValue(key, out DedupEntry cur)
                        || z > cur.Z || (z == cur.Z && tileKey < cur.TileKey))
                    {
                        // Resolve the pair's rider from the SAME block, so it swaps atomically with the winner
                        // (never a stale rider against a fresh owner). The bound matches TryGetRider's: an Owner at
                        // the final raw index has no follower.
                        bool hasRider = block.PairRoles[i] == SymbolPairRole.Owner
                            && i + 1 < rawCount && block.PairRoles[i + 1] == SymbolPairRole.Rider;
                        _dedup[key] = new DedupEntry
                        {
                            Z = z, TileKey = tileKey, BlockId = blockId, LocalIndex = i,
                            HasRider = hasRider, RiderLocalIndex = hasRider ? i + 1 : -1,
                        };
                    }
                }
            }
            // Point winners emit AFTER the scan, in _dedup first-insertion order — the second active segment.
            foreach (KeyValuePair<DedupKey, DedupEntry> kv in _dedup)
            {
                result.BlockId.Add(kv.Value.BlockId); result.LocalIndex.Add(kv.Value.LocalIndex);
                result.IsDeparting.Add(0);
                // The winning owner's rider (if any) is emitted immediately after it, same block — so "the rider
                // is the next point symbol" holds for the downstream gather.
                if (kv.Value.HasRider)
                {
                    result.BlockId.Add(kv.Value.BlockId); result.LocalIndex.Add(kv.Value.RiderLocalIndex);
                    result.IsDeparting.Add(0);
                }
            }
            result.ActiveCount = result.BlockId.Count;

            // ── DEPARTING scan: appended after the active split; a point whose identity an active/earlier copy
            //    already claimed is skipped (no double-draw); one block per departing tile continues OrderedBlocks. ──
            for (int s = 0; s < count; s++)
            {
                TileSlice slice = snapshot.Slices[s];
                if (!slice.IsDeparting) continue;
                SymbolTileBlock block = slice.Block;
                if (block == null) continue;
                int blockId = result.OrderedBlocks.Count;
                result.OrderedBlocks.Add(block);
                int rawCount = block.Kinds.Length;
                // Carries the previous symbol's emit decision: a rider is emitted iff its owner (the immediately
                // preceding symbol in this same block) was, so a claim-skipped owner takes its rider with it — no
                // orphan rider. Reset per slice.
                bool previousEmitted = false;
                for (int i = 0; i < rawCount; i++)
                {
                    bool emit;
                    // Read the rider role off PairRoles (raw-order), never off Detail[i]: Detail indexes Points[]
                    // or Curveds[] by Kind, so a curved symbol's Detail would mis-index a point's role. PairRoles
                    // is valid for every Kind, no gate needed.
                    if (block.PairRoles[i] == SymbolPairRole.Rider)
                    {
                        // A rider computes no claim key of its own — it inherits its owner's decision.
                        emit = previousEmitted;
                    }
                    else
                    {
                        emit = true;
                        if (block.Kinds[i] == SymbolPlacementKind.Point)
                        {
                            var key = DedupKey.For(block.RepAnchor[i], block.MaterialIndexes[i], block.TextIds[i],
                                block.IconImageIds[i], CrossTileSymbolKey.CanonicalGridMeters);
                            if (_dedup.ContainsKey(key))
                                emit = false; // active/earlier copy already shows it
                            else
                                _dedup[key] = new DedupEntry
                                    { Z = 0, TileKey = block.TileKey, BlockId = blockId, LocalIndex = i }; // claim
                        }
                    }

                    if (emit)
                    {
                        result.BlockId.Add(blockId); result.LocalIndex.Add(i); result.IsDeparting.Add(1);
                    }
                    previousEmitted = emit;
                }
            }
        }
    }

    // The all-integer cross-tile dedup key — a behaviour-preserving image of CrossTileSymbolKey's EQUALITY
    // partition. TextId/IconImageId are the interned ids (ordinal), so id-equality ⟺ string-equality and the
    // equivalence classes are identical to the string key's. A readonly struct : IEquatable so it does not box in
    // _dedup. Internal top-level (not nested) so this reconciler and the store's plain overloads share one copy.
    internal readonly struct DedupKey : IEquatable<DedupKey>
    {
        public readonly long GridX;
        public readonly long GridZ;
        public readonly long GridY;
        public readonly int LayerId;
        public readonly int TextId;
        public readonly int IconImageId;

        public DedupKey(long gridX, long gridZ, long gridY, int layerId, int textId, int iconImageId)
        {
            GridX = gridX; GridZ = gridZ; GridY = gridY;
            LayerId = layerId; TextId = textId; IconImageId = iconImageId;
        }

        public static DedupKey For(in double3 anchorRender, int layerId, int textId, int iconImageId, double quantizeMeters)
        {
            CrossTileSymbolKey.QuantizeAnchor(anchorRender, quantizeMeters, out long gx, out long gz, out long gy);
            return new DedupKey(gx, gz, gy, layerId, textId, iconImageId);
        }

        public bool Equals(DedupKey o)
            => GridX == o.GridX && GridZ == o.GridZ && GridY == o.GridY
               && LayerId == o.LayerId && TextId == o.TextId && IconImageId == o.IconImageId;

        public override bool Equals(object obj) => obj is DedupKey o && Equals(o);

        public override int GetHashCode()
        {
            // Value is irrelevant to emission order — _dedup is add-only between Clears, so it enumerates in
            // first-insertion order regardless of this hash.
            unchecked
            {
                int h = 17;
                h = h * 31 + GridX.GetHashCode();
                h = h * 31 + GridZ.GetHashCode();
                h = h * 31 + GridY.GetHashCode();
                h = h * 31 + LayerId;
                h = h * 31 + TextId;
                h = h * 31 + IconImageId;
                return h;
            }
        }
    }

    // All fields ride atomically with the winner, so a finest-zoom overwrite swaps them together (never a stale
    // ref against a fresh winner). HasRider=false means "no rider" (RiderLocalIndex is meaningless then; a rider
    // shares its owner's block, so no RiderBlockId). The store's plain overloads leave BlockId/LocalIndex at 0.
    internal struct DedupEntry
    {
        public int Z; public long TileKey; public int BlockId; public int LocalIndex;
        public bool HasRider; public int RiderLocalIndex;
    }
}
