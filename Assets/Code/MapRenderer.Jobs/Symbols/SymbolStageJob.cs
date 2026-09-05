using System;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// The per-frame symbol STAGING loop run as one Burst <see cref="IJob"/> over native mirrors of
    /// the <c>SymbolBatch</c> SoA. It calls the SAME <see cref="SymbolStagingMath"/> functions the managed path
    /// does — over <c>NativeArray.AsSpan()</c> slices — so it is byte-identical (the full EditMode gate is the
    /// teeth); Burst just SIMD-compiles the transcendental-heavy per-glyph geometry and drops the managed-call
    /// overhead. ONE job, not a fan-out: the candidate ordinal is assigned in record order (each record's
    /// <c>candidateCount</c> depends on all prior), so the loop is inherently serial — like <see cref="SymbolCollisionJob"/>.
    ///
    /// <para>Outputs are pre-sized by the caller to the batch's worst case (<c>MaxBoxes/MaxQuads/MaxCandidates</c>)
    /// and declared as fixed-length <see cref="NativeArray{T}"/>s, which genuinely cannot grow mid-run — this job's
    /// caller doesn't know the exact per-record counts up front either, so it sizes to the worst case instead.
    /// The dynamic per-frame values (projected
    /// screen/depth/valid) arrive as native arrays resolved on the main thread before the job; A-5 incumbency (R2)
    /// is resolved HERE instead, against the caller's <see cref="Placed"/> set — the point arm inline, the curved
    /// arm into the <see cref="AnchorWasPlaced"/> scratch this job fills.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct SymbolStageJob : IJob
    {
        // ── batch mirror (per-symbol records, in collected order) ──
        public NativeArray<SymbolPlacementKind> Kinds;   // point / curved
        public NativeArray<int>  Detail;      // index into Points / Curveds
        public NativeArray<int>  WorldCount;  // path length (curved) — the projected-path slice length
        public int Count;

        // ── point details ──
        public NativeArray<PointStageInput> Points;         // stable fields; dynamic patched per record below
        public NativeArray<int> PointQuadStart, PointQuadCount;

        // ── curved details ──
        public NativeArray<CurvedStageInput> Curveds;
        public NativeArray<int> CurvedGlyphStart, CurvedGlyphCount;
        public NativeArray<int> CurvedAnchorStart, CurvedAnchorCount, CurvedAnchorFadeStart;

        // ── flat pools ──
        public NativeArray<SymbolQuad>  Quads;
        public NativeArray<CurvedGlyph> Glyphs;
        public NativeArray<LineAnchor>  Anchors;
        public NativeArray<long>        AnchorFadeIds;

        // ── per-frame ──
        public NativeArray<int>    PointOffset;      // gather output; -1 = culled → skipped
        public NativeArray<float2> Screen;
        public NativeArray<float>  Depth;
        public NativeArray<byte>   Valid;
        // Stage AC (curved-world): the SAME gathered world polyline Screen was projected FROM — index-aligned
        // 1:1 with Screen/Depth/Valid (SymbolPlacementSystem._symbolPoints). The curved arm slices it at the
        // SAME (off, wc) as the screen path so a glyph's world anchor/tangent sample the identical (segment, t)
        // the screen arc walk resolves. Point arm never reads this.
        public NativeArray<double3> WorldPointsRender;
        // P2: index-parallel to WorldPointsRender (same Slice(off, wc)) — the unit surface normal at each
        // gathered world point, from IProjection.ProjectPoint(...).Up. Patched into PointStageInput.SurfaceUp
        // (point arm) / sliced for StageCurved (curved arm) below. WRITTEN by P2; not yet consumed by
        // SymbolStagingMath's actual placement math — carried onto CandidateEmit/PlacedQuad for P3.
        public NativeArray<float3>  WorldUpsRender;
        // A-5 incumbency per global anchor-fade index — JOB-OWNED scratch: filled here (below) from
        // AnchorFadeIds + Placed, not resolved by the caller. Sized by the caller to _mirrorFadeCount.
        public NativeArray<byte>   AnchorWasPlaced;
        public float   Bearing;
        public double2 Viewport;
        // W1: this frame's world ruler, metres per LOGICAL screen pixel — already recombined by
        // SymbolPlacementSystem.Tick (MetresPerDevicePixel × DevicePixelRatio), so nothing downstream carries
        // a device-px value plus a ratio to be re-multiplied. Patched into each curved record below, the same
        // way the point arm patches ScreenPx/Depth/Projected/SurfaceUp.
        public float   MetresPerLogicalPixel;
        // W3: this frame's view transform (scene origin, rebase, view-projection, logical viewport) — the
        // four values SymbolScreenProjection needs to project an arbitrary render-space point, which is what
        // the map-pitched collision box is built from. Per-frame, like Bearing/Viewport; passed to
        // StageCurved only. The POINT arm never sees it (StagePoint/StagePointPair take no such parameter),
        // and a default-constructed value selects the pre-W3 screen box — see SymbolViewTransform.IsUsable.
        public SymbolViewTransform View;

        // A-5 incumbency: last frame's collision survivors, keyed by fade id. Read from Burst — the reason
        // SymbolPlacementSystem._placedLastFrame is a NativeHashSet at all. NEVER stored across frames by the
        // caller (see SymbolPlacementSystem.RunStageJob).
        public NativeHashSet<long>.ReadOnly Placed;

        // Stage C: last frame's per-half collision verdict for optional pairs, keyed by the pair's FadeId
        // (SymbolPlacementSystem._droppedHalvesLastFrame). Probed ONLY when a pair actually declares an optional
        // half, so a style that sets neither property never touches it — the map is empty in that case anyway.
        public NativeHashMap<long, byte>.ReadOnly DroppedHalves;

        // ── caller-owned scratch (>= max WorldCount) ──
        public NativeArray<float2> PathPoints;
        public NativeArray<float>  CumulativeLengths;

        // ── outputs (pre-sized to the batch worst case) + counts ──
        public NativeArray<SymbolBox>       Boxes;
        public NativeArray<PlacedQuad>     StagedQuads;
        public NativeArray<SymbolCandidate> Candidates;
        public NativeArray<CandidateEmit>  Emit;
        public NativeArray<int>            OutCounts; // [0]=candidateCount [1]=boxCount [2]=quadCount [3]=emitCount

        public void Execute()
        {
            Span<SymbolBox>       boxes = Boxes.AsSpan();
            Span<PlacedQuad>     quads = StagedQuads.AsSpan();
            Span<SymbolCandidate> cands = Candidates.AsSpan();
            Span<CandidateEmit>  emit  = Emit.AsSpan();
            Span<float2>         path  = PathPoints.AsSpan();
            Span<float>          cum   = CumulativeLengths.AsSpan();

            // A-5 (R2): resolve every anchor fade id against the placed-set HERE, in Burst, instead of on the main
            // thread. Whole-range fill (not per-record): the culled records' entries are then defined, and this is
            // byte-identical to the managed loop it replaces.
            for (int i = 0; i < AnchorWasPlaced.Length; i++)
                AnchorWasPlaced[i] = (byte)(Placed.Contains(AnchorFadeIds[i]) ? 1 : 0);

            int candidateCount = 0, boxCount = 0, quadCount = 0, emitCount = 0;
            for (int r = 0; r < Count; r++)
            {
                int off = PointOffset[r];
                if (off < 0) continue; // B-3-distance-culled
                int d = Detail[r];

                if (Kinds[r] == SymbolPlacementKind.Point)
                {
                    PointStageInput s = Points[d];

                    // §10 D8/D10: a resolved RIDER stages nothing on its own — its box/quads/emit are staged
                    // BY its owner (below), so the pair cannot self-block. It still costs its gather/projection
                    // slot (deliberate — no gather change); if its owner culled first, the rider's iteration
                    // simply drops here too, which is the pair's atomic cull (StagePointPair gates once, on
                    // the owner).
                    if (s.PairRole == SymbolPairRole.Rider) continue;

                    s.ScreenPx = Screen[off];
                    s.Depth = Depth[off];
                    s.Projected = Valid[off] != 0;
                    s.SurfaceUp = WorldUpsRender[off]; // P2: per-frame patched, like ScreenPx/Depth/Projected
                    s.WasPlacedLastFrame = Placed.Contains(s.FadeId); // s is the Points[d] copy; FadeId is never patched

                    ReadOnlySpan<SymbolQuad> quadSpan = Quads.AsSpan().Slice(PointQuadStart[d], PointQuadCount[d]);

                    // §10 D8/D10: an owner whose rider is the NEXT point record (the reconciler emits them
                    // adjacently, the gather compacts point records in winner order) stages as ONE pair
                    // candidate. A broken adjacency (rider missing/moved) degrades to a lone badge via the
                    // ordinary StagePoint arm below — never a stranger, never a bare number.
                    if (s.PairRole == SymbolPairRole.Owner && d + 1 < Points.Length && Points[d + 1].PairRole == SymbolPairRole.Rider)
                    {
                        PointStageInput rider = Points[d + 1];
                        ReadOnlySpan<SymbolQuad> riderQuadSpan = Quads.AsSpan().Slice(PointQuadStart[d + 1], PointQuadCount[d + 1]);
                        // Stage C: only an optional pair can have a per-half verdict to carry, so the hash probe
                        // is gated on the flags rather than run for every pair.
                        byte droppedHalvesLastFrame = 0;
                        if (s.PairOptional || rider.PairOptional)
                            DroppedHalves.TryGetValue(s.FadeId, out droppedHalvesLastFrame);
                        candidateCount += SymbolStagingMath.StagePointPair(in s, in rider, quadSpan, riderQuadSpan,
                            Bearing, Viewport, candidateCount,
                            boxes, ref boxCount, quads, ref quadCount, cands, emit, ref emitCount,
                            droppedHalvesLastFrame);
                    }
                    else
                    {
                        candidateCount += SymbolStagingMath.StagePoint(in s, quadSpan, Bearing, Viewport, candidateCount,
                            boxes, ref boxCount, quads, ref quadCount, cands, emit, ref emitCount);
                    }
                }
                else
                {
                    int wc = WorldCount[r];
                    int anchorCount = CurvedAnchorCount[d];
                    int fadeStart   = CurvedAnchorFadeStart[d];
                    CurvedStageInput s = Curveds[d];
                    s.MetresPerLogicalPixel = MetresPerLogicalPixel; // W1: per-frame patch (see the field's doc)

                    ReadOnlySpan<float2>  screen = Screen.AsSpan().Slice(off, wc);
                    ReadOnlySpan<float>   depth  = Depth.AsSpan().Slice(off, wc);
                    ReadOnlySpan<byte>    valid  = Valid.AsSpan().Slice(off, wc);
                    ReadOnlySpan<double3> world  = WorldPointsRender.AsSpan().Slice(off, wc);
                    ReadOnlySpan<float3>  worldUps = WorldUpsRender.AsSpan().Slice(off, wc);
                    ReadOnlySpan<CurvedGlyph> glyphs  = Glyphs.AsSpan().Slice(CurvedGlyphStart[d], CurvedGlyphCount[d]);
                    ReadOnlySpan<LineAnchor>  anchors = Anchors.AsSpan().Slice(CurvedAnchorStart[d], anchorCount);
                    ReadOnlySpan<long> fadeIds   = AnchorFadeIds.AsSpan().Slice(fadeStart, anchorCount + 1);
                    ReadOnlySpan<byte> wasPlaced = AnchorWasPlaced.AsSpan().Slice(fadeStart, anchorCount + 1);

                    candidateCount += SymbolStagingMath.StageCurved(in s, screen, depth, valid, world, worldUps, glyphs, anchors,
                        fadeIds, wasPlaced, path.Slice(0, wc), cum.Slice(0, wc), Bearing, in View, candidateCount,
                        boxes, ref boxCount, quads, ref quadCount, cands, emit, ref emitCount);
                }
            }

            OutCounts[0] = candidateCount;
            OutCounts[1] = boxCount;
            OutCounts[2] = quadCount;
            OutCounts[3] = emitCount;
        }
    }
}
