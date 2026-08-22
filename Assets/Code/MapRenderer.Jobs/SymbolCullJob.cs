using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// The <c>GatherSymbolPoints</c> Cull pass, ported to Burst — per record, the SAME
    /// dropped → departing → coverage → zoom → horizon → distance → none verdict chain the managed loop ran,
    /// so the perf win is the per-element native READ moving under Burst (no <c>AtomicSafetyHandle</c> per
    /// array access), not a changed decision. <c>.Run()</c>, like <see cref="SymbolProjectionJob"/>: no
    /// Schedule/Complete round-trip, no worker hand-off — the caller blocks here regardless (the Compact pass
    /// reads <see cref="OutTrigger"/> immediately after).
    ///
    /// <para>Note: the Unity <b>batch</b> gate (<c>./Tools/run-tests.sh</c>) runs this job Burst-compiled;
    /// interactive EditMode runs the managed fallback. Under the gate, <c>SymbolCullJobTests</c> is therefore a
    /// real Burst-vs-managed differential, not a managed-only check.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct SymbolCullJob : IJobParallelFor
    {
        // ── Input — per-record flags/fields, index-parallel to the mirror ───────────────────────────────────
        /// <summary>Per-record: 1 = the record's tile was coverage-dropped (hard-skip, no fade). Tested first.</summary>
        [ReadOnly] public NativeArray<byte>   RecordDropped;
        /// <summary>Per-record: 1 = the record is departing (its tile is leaving cover) — the second verdict branch.</summary>
        [ReadOnly] public NativeArray<byte>   RecordDeparting;
        /// <summary>Per-record: 1 = the record is coverage-fading — the third verdict branch.</summary>
        [ReadOnly] public NativeArray<byte>   RecordCoverageFading;
        /// <summary>Per-record representative anchor in render space (pre-RTC) — the point the horizon/distance culls test.</summary>
        [ReadOnly] public NativeArray<double3> RepAnchor;
        /// <summary>Per-record <see cref="LabelRecordKind"/> — selects which detail array (<see cref="Points"/>/<see cref="Curveds"/>) the slot is read from.</summary>
        [ReadOnly] public NativeArray<byte>   Kinds;
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
        [ReadOnly] public double   LabelCullDistance;
        /// <summary>This frame's live slot count — the exclusive upper bound on the <see cref="SlotVisible"/> read (see its note).</summary>
        [ReadOnly] public int      SlotCount;

        // ── Output — one verdict per record, same index ──────────────────────────────────────────────────────
        /// <summary>Per-record cull verdict, written once per index; the Compact pass consumes it in record order.</summary>
        [WriteOnly] public NativeArray<GatherTrigger> OutTrigger;

        public void Execute(int index)
        {
            GatherTrigger t;
            if (RecordDropped[index] != 0) t = GatherTrigger.Dropped; // D1 — never on screen, no fade
            else if (RecordDeparting[index] != 0) t = GatherTrigger.Departing;
            else if (RecordCoverageFading[index] != 0) t = GatherTrigger.Coverage;
            else if (IsOutOfLiveZoom(index)) t = GatherTrigger.Zoom;
            else if (HorizonCull.IsHiddenBeyondHorizon(RepAnchor[index], SceneOriginRender, Rebase,
                                                        CameraRelative, GlobeCentreRelative, GlobeRadiusSq))
                t = GatherTrigger.Horizon;
            else if (LabelFarPlaneCull.IsCulled(RepAnchor[index], SceneOriginRender, Rebase,
                                                 CameraRelative, LabelCullDistance))
                t = GatherTrigger.Distance;
            else t = GatherTrigger.None;
            OutTrigger[index] = t;
        }

        // Record r's owning material slot is out of the live camera zoom's [minzoom, maxzoom) — mirrors
        // LabelPlacementSystem's (now-removed) managed IsOutOfLiveZoom. SlotCount, NOT SlotVisible.Length,
        // bounds the read — the native list is grown-only and may be longer than this frame's slot count.
        private bool IsOutOfLiveZoom(int r)
        {
            if (SlotCount == 0) return false;
            int detail = Detail[r];
            int slot = Kinds[r] == (byte)LabelRecordKind.Point ? Points[detail].Slot : Curveds[detail].Slot;
            return slot >= 0 && slot < SlotCount && !SlotVisible[slot];
        }
    }
}
