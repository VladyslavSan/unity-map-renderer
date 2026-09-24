using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// The <c>GatherSymbolPoints</c> Cull pass in Burst. Per record, it writes the first verdict of the chain
    /// dropped → departing → coverage → zoom → horizon → distance → none into <see cref="OutTrigger"/>.
    /// Limitation: a Burst compile failure falls back to managed IL silently (<c>FillGraphBurstProbeTests</c>),
    /// so <c>SymbolCullJobTests</c> is a Burst-vs-managed differential only under the batch gate
    /// (<c>./Tools/run-tests.sh</c>), not in the interactive Test Runner.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct CullJob : IJobParallelFor
    {
        // ── Input — per-record flags/fields, index-parallel to the mirror ───────────────────────────────────
        /// <summary>Per-record: 1 = the record's tile was coverage-dropped (hard-skip, no fade). Tested first.</summary>
        [ReadOnly] public NativeArray<byte>   SymbolDropped;
        /// <summary>Per-record: 1 = the record is departing (its tile is leaving cover) — the second verdict branch.</summary>
        [ReadOnly] public NativeArray<byte>   SymbolDeparting;
        /// <summary>Per-record: 1 = the record is coverage-fading — the third verdict branch.</summary>
        [ReadOnly] public NativeArray<byte>   SymbolCoverageFading;
        /// <summary>Per-record representative anchor in render space (pre-RTC) — the point the horizon/distance culls test.</summary>
        [ReadOnly] public NativeArray<double3> RepAnchor;
        /// <summary>Per-record <see cref="SymbolPlacementKind"/> — selects which detail array (<see cref="Points"/>/<see cref="Curveds"/>) the slot is read from.</summary>
        [ReadOnly] public NativeArray<SymbolPlacementKind> Kinds;
        /// <summary>Per-record index into <see cref="Points"/> or <see cref="Curveds"/> (per <see cref="Kinds"/>).</summary>
        [ReadOnly] public NativeArray<int>    Detail;
        /// <summary>Point-kind per-record detail; only <c>.Slot</c> (the material slot) is read here.</summary>
        [ReadOnly] public NativeArray<PointStageInput>  Points;
        /// <summary>Curved-kind per-record detail; only <c>.Slot</c> (the material slot) is read here.</summary>
        [ReadOnly] public NativeArray<CurvedStageInput> Curveds;

        // ── Input — per-slot zoom-visibility lookup (index == material slot) ────────────────────────────────
        /// <summary>Per-slot visible-at-live-zoom lookup (index == material slot). Grow-only (never shrinks) — it may be
        /// LONGER than this frame's <see cref="SlotCount"/>, so reads bound by <see cref="SlotCount"/>, never <c>.Length</c>.</summary>
        [ReadOnly] public NativeArray<bool> SlotVisible;

        // ── Input — per-frame scalars (the Horizon/Distance cull params + the zoom-gate bound) ────────────────
        /// <summary>The per-frame floating-origin the camera orbits (<c>SceneFrame.SceneOriginRender</c>).</summary>
        [ReadOnly] public double3  SceneOriginRender;
        /// <summary>The per-frame render→look-at-ENU rotation (<c>SceneFrame.Rebase</c>, identity on Mercator).</summary>
        [ReadOnly] public float3x3 Rebase;
        /// <summary>The camera position, render-space RTC-relative — the horizon/distance culls measure from here.</summary>
        [ReadOnly] public double3  CameraRelative;
        /// <summary>The globe centre, render-space RTC-relative — the horizon-plane origin (unused when <see cref="GlobeRadiusSq"/> &lt; 0).</summary>
        [ReadOnly] public double3  GlobeCentreRelative;
        /// <summary>Globe radius squared; &lt; 0 makes the horizon cull a no-op (planar projection).</summary>
        [ReadOnly] public double   GlobeRadiusSq;
        /// <summary>The far-distance cull threshold (render-space units); a record beyond it is <see cref="GatherTrigger.Distance"/>.</summary>
        [ReadOnly] public double   SymbolCullDistance;
        /// <summary>This frame's live slot count — the exclusive upper bound on the <see cref="SlotVisible"/> read (see its note).</summary>
        [ReadOnly] public int      SlotCount;

        // ── Output — one verdict per record, same index ──────────────────────────────────────────────────────
        /// <summary>Per-record cull verdict, written once per index; the Compact pass consumes it in record order.</summary>
        [WriteOnly] public NativeArray<GatherTrigger> OutTrigger;

        public void Execute(int index)
        {
            GatherTrigger t;
            if (SymbolDropped[index] != 0) t = GatherTrigger.Dropped; // never on screen, no fade
            else if (SymbolDeparting[index] != 0) t = GatherTrigger.Departing;
            else if (SymbolCoverageFading[index] != 0) t = GatherTrigger.Coverage;
            else if (IsOutOfLiveZoom(index)) t = GatherTrigger.Zoom;
            else if (HorizonCull.IsHiddenBeyondHorizon(RepAnchor[index], SceneOriginRender, Rebase,
                                                        CameraRelative, GlobeCentreRelative, GlobeRadiusSq))
                t = GatherTrigger.Horizon;
            else if (SymbolFarPlaneCull.IsCulled(RepAnchor[index], SceneOriginRender, Rebase,
                                                 CameraRelative, SymbolCullDistance))
                t = GatherTrigger.Distance;
            else t = GatherTrigger.None;
            OutTrigger[index] = t;
        }

        // Record r's material slot is out of the live camera zoom's [minzoom, maxzoom). SlotCount, NOT
        // SlotVisible.Length, bounds the read, because the grow-only list may be longer than this frame's slots.
        private bool IsOutOfLiveZoom(int r)
        {
            if (SlotCount == 0) return false;
            int detail = Detail[r];
            int slot = Kinds[r] == SymbolPlacementKind.Point ? Points[detail].Slot : Curveds[detail].Slot;
            return slot >= 0 && slot < SlotCount && !SlotVisible[slot];
        }
    }
}
