// Reads SymbolTileBlock's raw-order NativeArray columns, so it is not engine-free: only the Unity EditMode
// runner compiles and runs it, not the core-tests fast loop.

using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>The pure, off-main cross-tile symbol dedup: <see cref="Run"/> reads an immutable
    /// <see cref="SymbolSnapshot"/> (blocks pinned on the main thread) and fills a reused
    /// <see cref="SymbolReconcileResult"/>. It touches no store state and no Unity API beyond the blocks'
    /// read-only columns, so it runs on a thread-pool worker. It is non-reentrant. Its scan order matches the
    /// store's <c>CollectInto</c>, whose suite is its oracle. Winner identity is <c>(BlockId, LocalIndex)</c>.
    /// </summary>
    internal sealed class SymbolReconciler
    {
        // Reused cross-tile dedup index (non-reentrant): key = the stable (quantized-anchor, layer, text-id,
        // icon-id) identity; value = the winner's zoom/tile-key tiebreak + its (blockId, localIndex). Alloc-free.
        private readonly Dictionary<DedupKey, DedupEntry> _dedup = new Dictionary<DedupKey, DedupEntry>();

        // Reused cross-zoom index of the FINAL active point winners, keyed by identity without the anchor:
        // value = head of a chain through _activePoints. The departing pass reads it. Alloc-free once warm.
        private readonly Dictionary<SymbolIdentity, int> _activeByIdentity = new Dictionary<SymbolIdentity, int>();
        private readonly List<ActivePointEntry> _activePoints = new List<ActivePointEntry>();

        /// <summary>How far, in coarser-zoom tile units at extent 4096, a departing point may sit from an active
        /// winner of the same identity and still count as the same symbol.</summary>
        private const double CrossZoomMatchUnits = 2.0;

        /// <summary>Cap on the identity chain a departing point walks; past it there is no match.</summary>
        private const int MaxIdentityChainWalk = 256;

        /// <summary>The match radius in metres for a tile pair whose coarser zoom is <paramref name="coarserZoom"/>.</summary>
        private static double CrossZoomMatchRadius(int coarserZoom)
            => CrossZoomMatchUnits * 2.0 * WebMercator.WorldExtent / (math.pow(2.0, coarserZoom) * TileBufferClip.ReferenceExtent);

        // ── Test seams (reached via InternalsVisibleTo, mirroring the store's / subsystem's internal seams) ──
        /// <summary>The thread <see cref="Run"/> last executed on — a test asserts it is off the main thread.
        /// Test-only.</summary>
        internal int LastRunThreadId;

        /// <summary>When non-null, the next <see cref="Run"/> blocks on this gate once and clears it, so a test
        /// can park the worker in flight. Test-only. A teardown while it is held must open the gate first, or the
        /// drain hangs. Non-local invariant: arming it under an inline <c>WorkScheduler</c> deadlocks the calling
        /// thread; <c>SymbolSubsystem.WorkScheduler</c>'s setter rejects Inline while armed, but nothing guards
        /// arming it while already Inline.</summary>
        internal ManualResetEventSlim GateForTest;

        /// <summary>When true, the next <see cref="Run"/> throws once after writing a misaligned
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
            _activeByIdentity.Clear();
            _activePoints.Clear();

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
                        // Resolve the rider from the same block, so it swaps atomically with the winner. As in
                        // TryGetRider, an Owner at the final raw index has no follower.
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
                IndexActiveWinner(result.OrderedBlocks[kv.Value.BlockId], kv.Value);
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
                // A rider emits iff its owner (the preceding symbol in this block) did, so a claim-skipped owner
                // takes its rider with it. Reset per slice.
                bool previousEmitted = false;
                for (int i = 0; i < rawCount; i++)
                {
                    bool emit;
                    // Read the role off raw-order PairRoles, valid for every Kind. Detail[i] indexes Points[] or
                    // Curveds[] by Kind, so a curved symbol's Detail would mis-index a point's role.
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
                            else if (TryMatchActiveWinner(block, i, out long activeFadeId))
                            {
                                // The same symbol baked in another zoom: the active copy shows it, and its fade
                                // takes over this copy's opacity. It claims nothing, so no active winner moves.
                                emit = false;
                                result.FadeAliases.Add(new FadeAlias { From = FadeIdOf(block, i), To = activeFadeId });
                            }
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

        // The baked fade id of the point at raw index i of block (PointFadeId, fixed at bake time).
        private static long FadeIdOf(SymbolTileBlock block, int i) => block.Points[block.Detail[i]].FadeId;

        // Pushes a FINAL active winner onto its identity's chain. Indexing here, not in the scan, keeps the
        // finest-zoom overwrite out of the index.
        private void IndexActiveWinner(SymbolTileBlock block, in DedupEntry winner)
        {
            int i = winner.LocalIndex;
            var identity = new SymbolIdentity(block.MaterialIndexes[i], block.TextIds[i], block.IconImageIds[i]);
            _activeByIdentity.TryGetValue(identity, out int head);
            _activePoints.Add(new ActivePointEntry
            {
                Anchor = block.RepAnchor[i], Z = winner.Z, FadeId = FadeIdOf(block, i),
                Next = _activeByIdentity.ContainsKey(identity) ? head : -1,
            });
            _activeByIdentity[identity] = _activePoints.Count - 1;
        }

        // Whether the departing point at raw index i has an active winner of the same identity, baked at another
        // zoom, within the cross-zoom radius. Non-local invariant: this reads only; a match never displaces or reorders an active
        // winner, because nearness is not transitive and so cannot join the active pass.
        private bool TryMatchActiveWinner(SymbolTileBlock block, int i, out long activeFadeId)
        {
            activeFadeId = 0;
            var identity = new SymbolIdentity(block.MaterialIndexes[i], block.TextIds[i], block.IconImageIds[i]);
            if (!_activeByIdentity.TryGetValue(identity, out int index)) return false;

            double3 anchor = block.RepAnchor[i];
            int departingZoom = (int)(block.TileKey >> 44);
            for (int walked = 0; index >= 0 && walked < MaxIdentityChainWalk; walked++)
            {
                ActivePointEntry entry = _activePoints[index];
                // Same-zoom copies share one quantization, so the exact 4 m cell already decides them.
                if (entry.Z != departingZoom)
                {
                    double radius = CrossZoomMatchRadius(math.min(entry.Z, departingZoom));
                    if (math.distancesq(entry.Anchor, anchor) <= radius * radius)
                    {
                        activeFadeId = entry.FadeId;
                        return true;
                    }
                }

                index = entry.Next;
            }

            return false;
        }
    }

    // A point winner's identity WITHOUT its anchor: the cross-zoom match compares anchors by distance instead.
    // Ids are ordinal interned ids, so id-equality is string-equality. IEquatable, so it does not box.
    internal readonly struct SymbolIdentity : IEquatable<SymbolIdentity>
    {
        public readonly int LayerId;
        public readonly int TextId;
        public readonly int IconImageId;

        public SymbolIdentity(int layerId, int textId, int iconImageId)
        {
            LayerId = layerId; TextId = textId; IconImageId = iconImageId;
        }

        public bool Equals(SymbolIdentity o) => LayerId == o.LayerId && TextId == o.TextId && IconImageId == o.IconImageId;
        public override bool Equals(object obj) => obj is SymbolIdentity o && Equals(o);
        public override int GetHashCode() => unchecked((LayerId * 31 + TextId) * 31 + IconImageId);
    }

    // One final active point winner in the cross-zoom index. Next chains entries of one identity (-1 ends it).
    internal struct ActivePointEntry
    {
        public double3 Anchor; public int Z; public long FadeId; public int Next;
    }

    // The all-integer cross-tile dedup key, with the same equality partition as CrossTileSymbolKey: the text and
    // icon ids are ordinal interned ids, so id-equality ⟺ string-equality. IEquatable, so it does not box.
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

    // All fields swap together on a finest-zoom overwrite. RiderLocalIndex is meaningful only when HasRider; a
    // rider shares its owner's block. The store's plain overloads leave BlockId/LocalIndex at 0.
    internal struct DedupEntry
    {
        public int Z; public long TileKey; public int BlockId; public int LocalIndex;
        public bool HasRider; public int RiderLocalIndex;
    }
}
