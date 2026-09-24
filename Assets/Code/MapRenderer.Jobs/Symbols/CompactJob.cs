using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// The <c>GatherSymbolPoints</c> Compact pass, a serial Burst <see cref="IJob"/>. IN RECORD ORDER, a Dropped
    /// record is skipped; a triggered record with a live fade KEEPS staging (its FadeIds go into
    /// <see cref="ForceFadeOut"/>); a fade-dead one is skipped and tallied in <see cref="Counts"/>; a kept record
    /// is appended at <see cref="OutPoints"/>' length. It has no FMA math, so Burst and managed are bit-identical.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct CompactJob : IJob
    {
        // ── Input — per-record verdict + fields, index-parallel to the mirror ───────────────────────────────
        /// <summary>Per-record cull verdict from <see cref="CullJob"/>, consumed in record order.</summary>
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
                    // Only t ∈ {Departing,Coverage,Zoom,Horizon,Distance} (1-5) reach this branch (Dropped exits
                    // above; the guard excludes None), so slots 0/6 stay unused.
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

        // True if any of record r's FadeIds is above FadeEpsilon; those ids go into ForceFadeOut to ease to 0.
        // False when nothing is on screen, so the caller hard-skips the record without a pop.
        private bool MarkFadeOutIfAlive(int r)
        {
            int detail = Detail[r];
            if (Kinds[r] == SymbolPlacementKind.Point)
                return TryForceFadeOut(PointDetails[detail].FadeId);

            // Curved: force-fade EVERY anchor plus the centred fallback, so an invisible anchor cannot fade IN on
            // a culled tile; keep the record staged if ANY anchor is still visible.
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
