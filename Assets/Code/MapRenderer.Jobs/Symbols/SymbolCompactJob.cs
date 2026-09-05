using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// The <c>GatherSymbolPoints</c> Compact pass, ported to Burst — consumes <see cref="SymbolCullJob"/>'s
    /// per-record <see cref="GatherTrigger"/> verdict IN RECORD ORDER: a Dropped record is hard-skipped with no
    /// fade/count; a triggered record whose fade is still alive KEEPS staging (its FadeIds recorded into
    /// <see cref="ForceFadeOut"/> so the emit loop eases it to 0 — no pop); a triggered fade-dead record is
    /// hard-skipped and tallied into its trigger's <see cref="Counts"/> slot; a kept (<c>None</c>) record is
    /// appended. The running append length (<see cref="OutPoints"/><c>.Length</c>) is the destination offset, so
    /// this pass is inherently serial — <see cref="IJob"/>, not <see cref="IJobParallelFor"/> (unlike
    /// <see cref="SymbolCullJob"/>, whose per-index verdict has no cross-record dependency).
    ///
    /// <para><c>.Run()</c>, like <see cref="SymbolCullJob"/>/<see cref="SymbolProjectionJob"/>: no
    /// Schedule/Complete round-trip, no worker hand-off — the caller blocks here regardless (<c>EmitSymbols</c>
    /// reads <see cref="OutPoints"/>/<see cref="OutUps"/> immediately after).</para>
    ///
    /// <para>Integer branch + verbatim <c>double3</c>/<c>float3</c> copy + a single <c>float</c> compare
    /// (<c>&gt; FadeEpsilon</c>) — no <c>dot</c>, no FMA-reorderable math — so Burst and managed are
    /// bit-identical here; there is no near-threshold ULP hazard to fence a fixture around (contrast
    /// <see cref="SymbolCullJob"/>'s Horizon/Distance double-precision <c>dot</c> math).</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct SymbolCompactJob : IJob
    {
        // ── Input — per-record verdict + fields, index-parallel to the mirror ───────────────────────────────
        /// <summary>Per-record cull verdict from <see cref="SymbolCullJob"/>, consumed in record order.</summary>
        [ReadOnly] public NativeArray<GatherTrigger> Trigger;
        /// <summary>Per-record <see cref="SymbolPlacementKind"/> — selects which detail path (<see cref="PointDetails"/>
        /// / the curved anchor arrays) the fade-alive probe reads.</summary>
        [ReadOnly] public NativeArray<SymbolPlacementKind> Kinds;
        /// <summary>Per-record index into <see cref="PointDetails"/> or the curved anchor arrays (per <see cref="Kinds"/>).</summary>
        [ReadOnly] public NativeArray<int> Detail;
        /// <summary>Point-kind per-record detail; only <c>.FadeId</c> is read here.</summary>
        [ReadOnly] public NativeArray<PointStageInput> PointDetails;
        /// <summary>Curved-kind per-detail start into <see cref="FadeIds"/> for that record's anchor fade ids.</summary>
        [ReadOnly] public NativeArray<int> CurvedAnchorFadeStart;
        /// <summary>Curved-kind per-detail anchor count; the fade-alive probe walks this many ids plus the
        /// trailing centred-fallback id (<c>+ 1</c>).</summary>
        [ReadOnly] public NativeArray<int> CurvedAnchorCount;
        /// <summary>Flat pool of fade identities the curved anchor slices index into.</summary>
        [ReadOnly] public NativeArray<long> FadeIds;

        // ── Input — per-record world-point span (into the flat world-point pool) ────────────────────────────
        /// <summary>Per-record start into <see cref="WorldPoints"/>/<see cref="WorldUps"/>.</summary>
        [ReadOnly] public NativeArray<int> WorldStart;
        /// <summary>Per-record vertex count in the <see cref="WorldStart"/> span.</summary>
        [ReadOnly] public NativeArray<int> WorldCount;
        /// <summary>Flat pool of world-space points a kept record's span is copied from.</summary>
        [ReadOnly] public NativeArray<double3> WorldPoints;
        /// <summary>Flat pool of world-space unit up vectors, index-parallel to <see cref="WorldPoints"/>.</summary>
        [ReadOnly] public NativeArray<float3> WorldUps;

        // ── Input — fade state + frame scalars ───────────────────────────────────────────────────────────────
        /// <summary>Per-fade-identity opacity; a triggered record with any id above <see cref="FadeEpsilon"/>
        /// keeps staging instead of hard-skipping.</summary>
        [ReadOnly] public NativeHashMap<long, float> FadeOpacity;
        /// <summary>Below this opacity a fade identity reads as invisible (matches
        /// <c>SymbolPlacementSystem.FadeEpsilon</c>).</summary>
        public float FadeEpsilon;
        /// <summary>Record count — the loop bound (index-parallel across every input array above).</summary>
        public int Count;

        // ── Output — per-record destination offset + the flat kept-point pools ──────────────────────────────
        /// <summary>Per-record start into <see cref="OutPoints"/>/<see cref="OutUps"/>; <c>-1</c> for a
        /// hard-skipped record (Dropped, or triggered and fade-dead).</summary>
        [WriteOnly] public NativeArray<int> StageOffset;
        /// <summary>Kept records' world points, appended in record order — the running length IS
        /// <see cref="StageOffset"/> at the time of append, so this field is read as well as written.</summary>
        public NativeList<double3> OutPoints;
        /// <summary>Kept records' world up vectors, index-parallel to <see cref="OutPoints"/>.</summary>
        public NativeList<float3> OutUps;
        /// <summary>Fade identities to force-ease toward 0 this frame — every id the fade-alive probe visits,
        /// point or curved, alive or not (see the Curved-path side-effect note on <c>MarkFadeOutIfAlive</c>).</summary>
        public NativeHashSet<long> ForceFadeOut;
        /// <summary>Per-<see cref="GatherTrigger"/> tally, indexed by <c>(int)trigger</c> (one slot per trigger
        /// value — see the <c>Counts[(int)t]++</c> comment in <see cref="Execute"/>); the caller adds slots 1-5
        /// back onto the five <c>Last*CulledCount</c> properties after <c>.Run()</c>.</summary>
        public NativeArray<int> Counts;

        public void Execute()
        {
            for (int r = 0; r < Count; r++)
            {
                GatherTrigger t = Trigger[r];
                if (t == GatherTrigger.Dropped) { StageOffset[r] = -1; continue; } // never on screen, no fade, no count

                if (t != GatherTrigger.None && !MarkFadeOutIfAlive(r))
                {
                    // Enum-indexed, not a switch/default: behaviour-identical to the original switch because only
                    // t ∈ {Departing,Coverage,Zoom,Horizon,Distance} (1-5) ever reach this branch (Dropped
                    // short-circuits above; None is excluded by the `t != None` guard) — slots 0/6 stay unused.
                    Counts[(int)t]++;
                    StageOffset[r] = -1;
                    continue;
                }

                StageOffset[r] = OutPoints.Length;
                int ws = WorldStart[r], wc = WorldCount[r];
                for (int v = 0; v < wc; v++)
                {
                    OutPoints.Add(WorldPoints[ws + v]);
                    OutUps.Add(WorldUps[ws + v]);
                }
            }
        }

        // If record r's fade is still visible (any of its FadeIds has opacity > FadeEpsilon), record those
        // FadeIds in ForceFadeOut so the emit loop eases them toward 0, and return true (the caller then keeps
        // staging it for the fade-out). Returns false when the record has no live fade — never shown, or
        // already faded — so the caller hard-skips it (no pop; nothing was on screen to pop).
        private bool MarkFadeOutIfAlive(int r)
        {
            int detail = Detail[r];
            if (Kinds[r] == SymbolPlacementKind.Point)
                return TryForceFadeOut(PointDetails[detail].FadeId);

            // Curved: one candidate per anchor plus the centred-fallback slot. Force-fade EVERY anchor — not just
            // the ones already visible — so a previously-invisible anchor can't fade IN on a tile we are culling;
            // keep the record staged if ANY anchor is still visible.
            bool alive = false;
            int fadeStart = CurvedAnchorFadeStart[detail];
            int fadeCount = CurvedAnchorCount[detail] + 1; // + trailing centred-fallback fade id
            for (int i = 0; i < fadeCount; i++)
            {
                long fadeId = FadeIds[fadeStart + i];
                ForceFadeOut.Add(fadeId);
                if (FadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > FadeEpsilon) alive = true;
            }
            return alive;
        }

        private bool TryForceFadeOut(long fadeId)
        {
            if (!(FadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > FadeEpsilon)) return false;
            ForceFadeOut.Add(fadeId);
            return true;
        }
    }
}
