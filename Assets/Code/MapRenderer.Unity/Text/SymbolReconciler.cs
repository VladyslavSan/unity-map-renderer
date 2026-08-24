// Unity-side (reads SymbolTileBlock's native columns — Core stays engine-free). No longer engine-free
// itself: the reader cutover (symbols-async-reconcile stage 4.2) reads the block's raw-order NativeArray
// columns directly instead of walking a managed per-symbol carrier list, so this file left the core-tests.csproj
// fast loop (still compiled + run by the Unity EditMode runner).

using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Stage 4b (symbols-async-reconcile): the PURE, off-main cross-tile symbol dedup. <see cref="Run"/> reads an
    /// IMMUTABLE <see cref="SymbolSnapshot"/> (captured on the main thread, its blocks pinned) and fills a
    /// reused <see cref="SymbolReconcileResult"/> — the ~11 ms per-event dedup lifted off the render thread
    /// (design §2/§4). It touches NO Unity API beyond reading a <see cref="SymbolTileBlock"/>'s already-baked
    /// NativeArray columns (read-only, off-main-safe) and NO store state (only the snapshot's refs), so it is
    /// safe on a thread-pool worker.
    ///
    /// <para><b>One in flight ⇒ non-reentrant.</b> The reused <see cref="_dedup"/> index is owned here; the
    /// caller's coalescing state machine guarantees exactly one <see cref="Run"/> executes
    /// at a time, so the reused index is never touched concurrently.</para>
    ///
    /// <para><b>Byte-identical.</b> The scan order (active tiles first — curved emitted in scan order, points
    /// deduped into <see cref="_dedup"/>; then the winner emit in first-insertion order; then departing with the
    /// active-set claim-skip) reproduces the store's inline plan-aware <c>CollectInto</c> exactly, so the existing
    /// store/oracle test suite is a byte-identical oracle for this extraction.</para>
    ///
    /// <para><b>Column reads.</b> Every per-symbol read is off the block's raw-order columns:
    /// <see cref="SymbolTileBlock.Kinds"/> replaces the placement
    /// check, <see cref="SymbolTileBlock.PairRoles"/> replaces re-running <c>SymbolPairing</c> over the tile
    /// list (byte-identical because both are the SAME resolution over the SAME immutable list — see the design
    /// doc's pairing-correctness note), and <see cref="SymbolTileBlock.RepAnchor"/>/
    /// <see cref="SymbolTileBlock.MaterialIndexes"/>/<see cref="SymbolTileBlock.TextIds"/>/
    /// <see cref="SymbolTileBlock.IconImageIds"/> feed <see cref="DedupKey.For"/> directly. Winner identity
    /// is <c>(BlockId, LocalIndex)</c>.</para>
    /// </summary>
    internal sealed class SymbolReconciler
    {
        // Reused cross-tile dedup index (one-in-flight ⇒ non-reentrant) — keyed by the stable
        // (quantized-anchor, layer, text-id, icon-id) identity, value = the winning symbol's tile zoom/key
        // for the finest-zoom-wins tiebreak + its (blockId, localIndex). Reused so a warm reconcile is alloc-free.
        private readonly Dictionary<DedupKey, DedupEntry> _dedup = new Dictionary<DedupKey, DedupEntry>();

        // ── Test seams (reached via InternalsVisibleTo, mirroring the store's / subsystem's internal seams) ──
        /// <summary>The managed thread id the last <see cref="Run"/> executed on — set at the top of Run. An
        /// EditMode test asserts it is NOT the main thread (the off-main tooth). Internal test-only telemetry.</summary>
        internal int LastRunThreadId;

        /// <summary>When non-null, the next <see cref="Run"/> blocks on this gate ONCE (then clears it), so a
        /// test can PARK the worker in flight (assert one-in-flight / apply-stale / pin lifetime) and release it
        /// deterministically. A teardown while this is held MUST open the gate first, or the drain hangs (SPEC A
        /// test note). Internal test-only.</summary>
        internal ManualResetEventSlim GateForTest;

        /// <summary>When true, the next <see cref="Run"/> throws once (then clears the flag) — the fault-policy
        /// tooth (SPEC B): the worker faults AFTER partially populating the result (a deliberately MISALIGNED
        /// buffer — <c>BlockId</c> longer than <c>LocalIndex</c>), so a wrong "swap on non-success" would feed
        /// that partial buffer to <c>SymbolGatherPlan.Build</c> and crash — the exact hazard swap-only-on-success
        /// guards. Internal test-only.</summary>
        internal bool FaultNextRun;

        /// <summary>Dedup <paramref name="snapshot"/> into <paramref name="result"/> — the off-main body. Clears
        /// the result AND the reused index FIRST (#3b/#3c: a reused result/index must not leak a prior run's
        /// symbols — the SHRINK correctness core), then transcribes the store's plan-aware scan.</summary>
        public void Run(SymbolSnapshot snapshot, SymbolReconcileResult result)
        {
            LastRunThreadId = Thread.CurrentThread.ManagedThreadId;
            ManualResetEventSlim gate = GateForTest;
            if (gate != null) { GateForTest = null; gate.Wait(); }

            result.Clear(); // #3b — reused result: clear all lists + ActiveCount before refilling
            _dedup.Clear(); // #3c — reused index: a prior run's winners must not survive into this one

            if (FaultNextRun)
            {
                FaultNextRun = false;
                // Partially populate → a MISALIGNED buffer (BlockId.Count = 1, LocalIndex.Count = 0). If a buggy
                // pickup swapped this un-Succeeded result into the front, SymbolGatherPlan.Build would read
                // LocalIndex[0] out of range and crash — so swap-only-on-success is what protects the consumer.
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
                        // (A curved symbol is never paired — §10's fence — so it needs no rider check.)
                        result.BlockId.Add(blockId); result.LocalIndex.Add(i); result.IsDeparting.Add(0);
                        continue;
                    }
                    // §10 D9: a resolved RIDER gets no DedupKey of its own — its identity IS its owner's. It
                    // rides along with its winning owner below (DedupEntry.HasRider/RiderLocalIndex), so it is
                    // never looked up here independently.
                    if (block.PairRoles[i] == SymbolPairRole.Rider) continue;

                    var key = DedupKey.For(block.RepAnchor[i], block.MaterialIndexes[i], block.TextIds[i],
                        block.IconImageIds[i], CrossTileSymbolKey.CanonicalGridMeters);
                    long tileKey = block.TileKey;
                    int z = (int)(tileKey >> 44); // SymbolTileKey.Pack: z in the high bits (finest zoom wins)
                    if (!_dedup.TryGetValue(key, out DedupEntry cur)
                        || z > cur.Z || (z == cur.Z && tileKey < cur.TileKey))
                    {
                        // §10 D9: resolve the pair's rider (if any) HERE, from the SAME block `i` came from, so
                        // it swaps ATOMICALLY with the winner — never a stale rider against a fresh owner. Gated
                        // on the EXACT bound TryGetRider checked (i+1 < rawCount AND the follower's role) — an
                        // Owner at the final raw index has no follower, so it must resolve to "no rider", not a
                        // LocalIndex one past the block's arrays.
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
                // §10 D9: the winning owner's rider (if any) rides immediately after it — same block, same
                // IsDeparting — so "the rider is the next point symbol" holds downstream (SymbolGatherJob
                // compacts point symbols in winner order, SymbolStageJob reads Points[d+1]).
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
                // §10 D9: carries the PREVIOUS iteration's emit decision — a rider is emitted iff its owner
                // (always the immediately preceding symbol in this SAME tile's block) was. A claim-skipped owner
                // therefore takes its rider with it, so no orphan rider ever reaches the plan (P13). Reset per
                // slice: a rider's owner always lives in the SAME departing tile's block.
                bool previousEmitted = false;
                for (int i = 0; i < rawCount; i++)
                {
                    bool emit;
                    // §2 hazard: detect a rider via the block's OWN PairRoles column (raw-order), never via
                    // Detail[i] — Detail indexes Points[] for a Point symbol and Curveds[] for a Curved one, so
                    // reading a curved symbol's pairing off Points[Detail[i]] would be a valid-but-meaningless
                    // read of an unrelated point's role. PairRoles[i] is None for every curved slot, so
                    // this single raw-order read is correct for every Kind without a gate.
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

    // Stage 2 (symbols-async-reconcile): the all-integer cross-tile dedup key — a behaviour-preserving
    // representation of CrossTileSymbolKey's EQUALITY partition (the fade path keeps CrossTileSymbolKey + its string
    // hash verbatim — the two identities are decoupled by design). TextId/IconImageId are the interned ids of the
    // symbol's Text/IconImage (via SymbolStringTable, ordinal): id-equality ⟺ ordinal-string-equality, so the
    // equivalence classes are IDENTICAL to the string key's. A `readonly struct : IEquatable<DedupKey>` so it does
    // NOT box in the reused _dedup dictionary (the zero-alloc property). Grid math is the SHARED
    // CrossTileSymbolKey.QuantizeAnchor (bit-identical grids).
    //
    // Stage 4b: moved OUT of SymbolTileStore (was a private nested type) to an `internal` top-level type here,
    // so BOTH the off-main reconciler (this file) and the store's legacy plain CollectInto overloads (same
    // namespace) share ONE definition — the relocation is textual only, GetHashCode/Equals are unchanged, so the
    // dedup partition (and hence every existing byte-identity oracle) is untouched.
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
            // Value is IRRELEVANT to emission order — _dedup is add-only between Clears, so it enumerates in
            // first-insertion order, independent of this hash (see the plan's emission-ORDER proof).
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

    // Stage-2 (symbol-symbol native gather): BlockId/LocalIndex ride ATOMICALLY with the winner so a finest-zoom
    // overwrite swaps all fields together (never a stale block ref against a fresh winner). Moved here alongside
    // DedupKey (Stage 4b) — the store's legacy plain overloads leave BlockId/LocalIndex at 0 (never read there).
    // Reader cutover (4.2): all-integer — a rider shares its owner's block, so no RiderBlockId is needed.
    // §10 D9: HasRider/RiderLocalIndex ride ATOMICALLY too — HasRider=false means "no rider", never a sentinel
    // LocalIndex alone (RiderLocalIndex is meaningless when HasRider is false; the pair is resolved together at
    // write time from the SAME block `LocalIndex` came from).
    internal struct DedupEntry
    {
        public int Z; public long TileKey; public int BlockId; public int LocalIndex;
        public bool HasRider; public int RiderLocalIndex;
    }
}
