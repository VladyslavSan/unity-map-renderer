// Engine-free (like SymbolTileLabelStore + SymbolLabelSnapshot, co-located here): references only System,
// Unity.Mathematics (double3 — engine-free), and MapRenderer.Core types, NO `using UnityEngine`. Compiled by
// BOTH the Unity runner and Tools/core-tests (its <Compile Include> lives in Tools/core-tests/core-tests.csproj)
// so the off-main dedup — the part with the subtle winner-order + shrink races — gets the fast headless tests.
// Do NOT add a UnityEngine reference.

using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Stage 4b (labels-async-reconcile): the PURE, off-main cross-tile label dedup. <see cref="Run"/> reads an
    /// IMMUTABLE <see cref="SymbolLabelSnapshot"/> (captured on the main thread, its blocks pinned) and fills a
    /// reused <see cref="SymbolLabelReconcileResult"/> — the ~11 ms per-event dedup lifted off the render thread
    /// (design §2/§4). It touches NO Unity API, NO native array, and NO store state (only the snapshot's managed
    /// refs), so it is safe on a thread-pool worker.
    ///
    /// <para><b>One in flight ⇒ non-reentrant.</b> The reused <see cref="_dedup"/> index is owned here; the
    /// coalescing state machine (<c>SymbolLabelSubsystem</c>) guarantees exactly one <see cref="Run"/> executes
    /// at a time, so the reused index is never touched concurrently.</para>
    ///
    /// <para><b>Byte-identical.</b> The scan order (active tiles first — curved emitted in scan order, points
    /// deduped into <see cref="_dedup"/>; then the winner emit in first-insertion order; then departing with the
    /// active-set claim-skip) reproduces the store's inline plan-aware <c>CollectInto</c> exactly, so the existing
    /// store/oracle test suite is a byte-identical oracle for this extraction.</para>
    /// </summary>
    internal sealed class SymbolLabelReconciler
    {
        // Reused cross-tile dedup index (one-in-flight ⇒ non-reentrant) — keyed by the stable
        // (quantized-anchor, layer, text-id, icon-id) identity, value = the winning label + its tile zoom/key
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
        /// buffer — <c>Output</c> longer than <c>BlockId</c>), so a wrong "swap on non-success" would feed that
        /// partial buffer to <c>SymbolGatherPlan.Build</c> and crash — the exact hazard swap-only-on-success
        /// guards. Internal test-only.</summary>
        internal bool FaultNextRun;

        /// <summary>Dedup <paramref name="snapshot"/> into <paramref name="result"/> — the off-main body. Clears
        /// the result AND the reused index FIRST (#3b/#3c: a reused result/index must not leak a prior run's
        /// records — the SHRINK correctness core), then transcribes the store's plan-aware scan.</summary>
        public void Run(SymbolLabelSnapshot snapshot, SymbolLabelReconcileResult result)
        {
            LastRunThreadId = Thread.CurrentThread.ManagedThreadId;
            ManualResetEventSlim gate = GateForTest;
            if (gate != null) { GateForTest = null; gate.Wait(); }

            result.Clear(); // #3b — reused result: clear all lists + ActiveCount before refilling
            _dedup.Clear(); // #3c — reused index: a prior run's winners must not survive into this one

            if (FaultNextRun)
            {
                FaultNextRun = false;
                // Partially populate → a MISALIGNED buffer (Output.Count = 1, BlockId.Count = 0). If a buggy
                // pickup swapped this un-Succeeded result into the front, SymbolGatherPlan.Build would read
                // BlockId[0] out of range and crash — so swap-only-on-success is what protects the consumer.
                result.Output.Add(null);
                throw new InvalidOperationException("SymbolLabelReconciler injected fault (test) — partial misaligned result");
            }

            // ── ACTIVE scan: curved emit in scan order, points dedup into _dedup (one block per active tile). ──
            int count = snapshot.Count;
            for (int s = 0; s < count; s++)
            {
                TileSlice slice = snapshot.Slices[s];
                if (slice.IsDeparting) continue; // departing tiles are scanned in the SECOND pass, after the winner emit
                List<LabelInstance> labels = slice.Labels;
                if (labels == null) continue;
                int blockId = result.OrderedBlocks.Count;
                result.OrderedBlocks.Add(slice.Block);
                for (int i = 0; i < labels.Count; i++)
                {
                    LabelInstance label = labels[i];
                    if (label == null) continue;
                    if (label.Placement != SymbolPlacement.Point)
                    {
                        // Curved (line) labels emit DURING the scan, in scan order — the plan entry rides in lockstep.
                        // (A curved label is never paired — §10's fence — so it needs no rider check.)
                        result.Output.Add(label); result.BlockId.Add(blockId); result.LocalIndex.Add(i); result.IsDeparting.Add(0);
                        continue;
                    }
                    // §10 D9: a resolved RIDER gets no DedupKey of its own — its identity IS its owner's. It
                    // rides along with its winning owner below (DedupEntry.RiderLabel), so it is never looked
                    // up here independently.
                    if (LabelPairing.IsRider(labels, i)) continue;

                    var key = DedupKey.For(label.AnchorRender, label.MaterialIndex, slice.TextIds[i], slice.IconImageIds[i], CrossTileLabelKey.CanonicalGridMeters);
                    int z = (int)(label.TileKey >> 44); // PackTileKey: z in the high bits (finest zoom wins)
                    if (!_dedup.TryGetValue(key, out DedupEntry cur)
                        || z > cur.Z || (z == cur.Z && label.TileKey < cur.TileKey))
                    {
                        // §10 D9: resolve the pair's rider (if any) HERE, from the SAME tile list `label` came
                        // from, so it swaps ATOMICALLY with the winner — never a stale rider against a fresh
                        // owner. A rider has no key of its own; it is carried purely as the owner's passenger.
                        LabelInstance riderLabel = null;
                        int riderLocalIndex = -1;
                        if (LabelPairing.TryGetRider(labels, i, out int ri)) { riderLabel = labels[ri]; riderLocalIndex = ri; }
                        _dedup[key] = new DedupEntry
                        {
                            Label = label, Z = z, TileKey = label.TileKey, BlockId = blockId, LocalIndex = i,
                            RiderLabel = riderLabel, RiderLocalIndex = riderLocalIndex,
                        };
                    }
                }
            }
            // Point winners emit AFTER the scan, in _dedup first-insertion order — the second active segment.
            foreach (KeyValuePair<DedupKey, DedupEntry> kv in _dedup)
            {
                result.Output.Add(kv.Value.Label); result.BlockId.Add(kv.Value.BlockId); result.LocalIndex.Add(kv.Value.LocalIndex);
                result.IsDeparting.Add(0);
                // §10 D9: the winning owner's rider (if any) rides immediately after it — same block, same
                // IsDeparting — so "the rider is the next point record" holds downstream (SymbolGatherJob
                // compacts point records in winner order, LabelStageJob reads Points[d+1]).
                if (kv.Value.RiderLabel != null)
                {
                    result.Output.Add(kv.Value.RiderLabel); result.BlockId.Add(kv.Value.BlockId); result.LocalIndex.Add(kv.Value.RiderLocalIndex);
                    result.IsDeparting.Add(0);
                }
            }
            result.ActiveCount = result.Output.Count;

            // ── DEPARTING scan: appended after the active split; a point whose identity an active/earlier copy
            //    already claimed is skipped (no double-draw); one block per departing tile continues OrderedBlocks. ──
            for (int s = 0; s < count; s++)
            {
                TileSlice slice = snapshot.Slices[s];
                if (!slice.IsDeparting) continue;
                List<LabelInstance> labels = slice.Labels;
                if (labels == null) continue;
                int blockId = result.OrderedBlocks.Count;
                result.OrderedBlocks.Add(slice.Block);
                // §10 D9: carries the PREVIOUS iteration's emit decision — a rider is emitted iff its owner
                // (always the immediately preceding record in this SAME tile list) was. A claim-skipped owner
                // therefore takes its rider with it, so no orphan rider ever reaches the plan (P13). Reset per
                // slice: a rider's owner always lives in the SAME departing tile's list.
                bool previousEmitted = false;
                for (int i = 0; i < labels.Count; i++)
                {
                    LabelInstance label = labels[i];
                    if (label == null) { previousEmitted = false; continue; }

                    bool emit;
                    if (LabelPairing.IsRider(labels, i))
                    {
                        // A rider computes no claim key of its own — it inherits its owner's decision.
                        emit = previousEmitted;
                    }
                    else
                    {
                        emit = true;
                        if (label.Placement == SymbolPlacement.Point)
                        {
                            var key = DedupKey.For(label.AnchorRender, label.MaterialIndex, slice.TextIds[i], slice.IconImageIds[i], CrossTileLabelKey.CanonicalGridMeters);
                            if (_dedup.ContainsKey(key))
                                emit = false; // active/earlier copy already shows it
                            else
                                _dedup[key] = new DedupEntry { Label = label, Z = 0, TileKey = label.TileKey, BlockId = blockId, LocalIndex = i }; // claim
                        }
                    }

                    if (emit)
                    {
                        result.Output.Add(label); result.BlockId.Add(blockId); result.LocalIndex.Add(i); result.IsDeparting.Add(1);
                    }
                    previousEmitted = emit;
                }
            }
        }
    }

    // Stage 2 (labels-async-reconcile): the all-integer cross-tile dedup key — a behaviour-preserving
    // representation of CrossTileLabelKey's EQUALITY partition (the fade path keeps CrossTileLabelKey + its string
    // hash verbatim — the two identities are decoupled by design). TextId/IconImageId are the interned ids of the
    // label's Text/IconImage (via LabelTextIntern, ordinal): id-equality ⟺ ordinal-string-equality, so the
    // equivalence classes are IDENTICAL to the string key's. A `readonly struct : IEquatable<DedupKey>` so it does
    // NOT box in the reused _dedup dictionary (the zero-alloc property). Grid math is the SHARED
    // CrossTileLabelKey.QuantizeAnchor (bit-identical grids).
    //
    // Stage 4b: moved OUT of SymbolTileLabelStore (was a private nested type) to an `internal` top-level type here,
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
            CrossTileLabelKey.QuantizeAnchor(anchorRender, quantizeMeters, out long gx, out long gz, out long gy);
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

    // Stage-2 (symbol-label native gather): BlockId/LocalIndex ride ATOMICALLY with the winner so a finest-zoom
    // overwrite swaps all fields together (never a stale block ref against a fresh label). Moved here alongside
    // DedupKey (Stage 4b) — the store's legacy plain overloads leave BlockId/LocalIndex at 0 (never read there).
    // §10 D9: RiderLabel/RiderLocalIndex ride ATOMICALLY too — a null RiderLabel means "no rider", never a
    // sentinel LocalIndex alone (RiderLocalIndex is meaningless without RiderLabel; the pair is resolved
    // together at write time from the SAME tile list `Label` came from).
    internal struct DedupEntry
    {
        public LabelInstance Label; public int Z; public long TileKey; public int BlockId; public int LocalIndex;
        public LabelInstance RiderLabel; public int RiderLocalIndex;
    }
}
