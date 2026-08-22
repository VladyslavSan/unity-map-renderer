// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, never an inline
// `Unity.Mathematics.X`. Unity-side (needs Unity.Collections' NativeArray — Core stays engine-free).

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 1 (design doc §4, §5 B): bakes one tile's build-time
    /// <c>List&lt;LabelInstance&gt;</c> into a fresh native <see cref="SymbolTileLabelBlock"/> — reproducing
    /// EXACTLY the per-label field math the per-frame oracle computes, because both go through the SAME
    /// <see cref="BuildPointInput"/>/<see cref="BuildCurvedInput"/> helpers — the drift-guard the design calls
    /// for. Those helpers live HERE rather than on the oracle: production owns the math, and the oracle
    /// (<c>SymbolLabelBatchBuilder</c>, test assembly) imports it to check assembly and ordering around it.
    ///
    /// <para>Runs on the MAIN thread, once per tile commit (<c>SymbolLabelSubsystem.RunTailAsync</c>) — glyph
    /// quads / curved glyphs are only materialized there (the per-layer shape tail), so there is nothing left
    /// to bake off it. No <c>IProjection</c> parameter (bake-safety): every label in one build's list belongs
    /// to the SAME physical tile (one <c>(source, tile)</c> build), so the caller's single
    /// <paramref name="tileOriginRender"/> already IS the launch-time <c>TileRenderOrigin.Project</c> result
    /// folded into every label's <c>AnchorRender</c> — unlike the oracle's <c>Build</c>, which
    /// walks a multi-tile collected set and so keeps its own per-tile origin cache.</para>
    /// </summary>
    internal static class SymbolTileLabelBlockBaker
    {
        /// <summary>Bake <paramref name="labels"/> (one tile's build output — a RAW list that may contain
        /// <c>null</c> slots for a per-label build failure, see <see cref="SymbolTileLabelBlock"/>'s null-slot
        /// invariant) into a fresh <see cref="SymbolTileLabelBlock"/>. <paramref name="slotCount"/> clamps each
        /// label's material slot (the same <c>ClampSlot</c> the oracle applies).
        ///
        /// <para>(G) Exception-safety: every array is allocated INTO the returned block; on any exception
        /// mid-bake (allocation or fill), the partially-built block is disposed (frees whatever
        /// <see cref="NativeArray{T}.IsCreated"/>) before the exception is rethrown — never a partial-allocation
        /// leak.</para></summary>
        /// <summary>Drift-guard (Phase 1 Stage 1 / design §5 B): the per-label POINT field math used by
        /// <see cref="Bake"/> (the production build-time bake) and by the per-frame oracle
        /// (<c>SymbolLabelBatchBuilder</c>, test assembly) — ONE implementation so the two cannot diverge.
        /// It lives here, on the production side, because production is what must own it: the oracle checks
        /// this math, so the oracle importing it is the direction that keeps the check honest. Resolves everything stable about
        /// <paramref name="label"/> EXCEPT its glyph quads/world anchor (copied by the caller into its own
        /// pool). <paramref name="tileOriginRender"/> is the label's tile's render-space origin — the caller
        /// resolves it (a per-build cache for the oracle; a single value for the single-tile bake).</summary>
        internal static PointStageInput BuildPointInput(LabelInstance label, int slotCount, in double3 tileOriginRender,
            LabelPairRole pairRole)
        {
            float4 color  = LabelPlacementSystem.LinearColor(label);
            // I6: icon FadeId identity now rides label.IconImage (null for text, so a text label's FadeId
            // is unchanged — PointFadeId's guard-skip fold). §10 D9: UNCONDITIONAL on pairRole — a pair's
            // identity IS the owner's existing icon identity; a rider's FadeId is never read by a candidate
            // (LabelStageJob skips staging a Rider record entirely) but is left correctly resolved so
            // the gather Compact pass's per-record fade-alive probe stays well-defined.
            long   fadeId = LabelPlacementSystem.PointFadeId(label.AnchorRender, label.MaterialIndex, label.Text, label.IconImage);

            // Manual per-component narrow (convention — no assumed double3→float3 cast operator; mirrors
            // FloatingOrigin.TileToSceneRebased's identical narrowing).
            float3 anchorLocal = new float3(
                (float)(label.AnchorRender.x - tileOriginRender.x),
                (float)(label.AnchorRender.y - tileOriginRender.y),
                (float)(label.AnchorRender.z - tileOriginRender.z));

            return new PointStageInput
            {
                // dynamic (ScreenPx/Depth/Projected/WasPlacedLastFrame) left default — patched per frame.
                BoundsMin = label.Layout?.BoundsMin ?? float2.zero,
                BoundsMax = label.Layout?.BoundsMax ?? float2.zero,
                TextSizePx = label.TextSizePx, PaddingPx = label.PaddingPx, SortKey = label.SortKey,
                FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                Slot = LabelPlacementSystem.ClampSlot(label.MaterialIndex, slotCount),
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement,
                TranslatePx = label.TranslatePx, TranslateAnchor = label.TranslateAnchor,
                RotationAlignment = label.RotationAlignment, Color = color,
                IconRotateRadians = label.IconRotateRadians,
                FadeId = fadeId,
                // I5a: thread the icon/text discriminator through — NOT yet consumed by the draw side (I5b).
                AtlasKind = label.Kind == LabelKind.Icon ? LabelKind.Icon : LabelKind.Text,
                AnchorLocal = anchorLocal, TileOriginRender = tileOriginRender,
                // §10 D8/D10: RESOLVED role (LabelPairing already decided whether the proposal holds) — the
                // stage job needs nothing else; a paired owner's rider is the next point record.
                PairRole = pairRole,
                // Stage C: read by StagePointPair only — a half whose role did not resolve carries it
                // harmlessly (nothing outside the pair arm looks at it).
                PairOptional = label.PairOptional,
            };
        }

        /// <summary>Drift-guard (Phase 1 Stage 1 / design §5 B): the per-label CURVED field math used by
        /// <see cref="Bake"/> (the production build-time bake) and by the per-frame oracle
        /// (<c>SymbolLabelBatchBuilder</c>, test assembly) — ONE implementation so the two cannot diverge.
        /// It lives here, on the production side, because production is what must own it: the oracle checks
        /// this math, so the oracle importing it is the direction that keeps the check honest. Resolves everything stable about
        /// <paramref name="label"/> EXCEPT its glyphs/anchors/anchor-fade-ids/world path (copied by the caller
        /// into its own pool). <paramref name="tileOriginRender"/> is the label's tile's render-space origin —
        /// the caller resolves it (a per-build cache for the oracle; a single value for the
        /// single-tile bake).</summary>
        internal static CurvedStageInput BuildCurvedInput(LabelInstance label, int slotCount, in double3 tileOriginRender)
            => new CurvedStageInput
            {
                TextSizePx = label.TextSizePx, PaddingPx = label.PaddingPx, SortKey = label.SortKey,
                FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                Slot = LabelPlacementSystem.ClampSlot(label.MaterialIndex, slotCount),
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement,
                TranslatePx = label.TranslatePx, TranslateAnchor = label.TranslateAnchor,
                MaxAngleDeg = label.MaxAngleDeg, KeepUpright = label.KeepUpright,
                Color = LabelPlacementSystem.LinearColor(label), TileOriginRender = tileOriginRender,
                // P-B: the icon/text discriminator, textually identical to BuildPointInput's above — a
                // map-aligned line icon is a one-glyph curved label sampling the SPRITE sheet.
                AtlasKind = label.Kind == LabelKind.Icon ? LabelKind.Icon : LabelKind.Text,
                IconRotateRadians = label.IconRotateRadians,
                // W1: the resolved pitch alignment — StageCurved's world-arc predicate. MetresPerLogicalPixel
                // is deliberately absent: it is this frame's camera ruler, patched per frame by
                // LabelStageJob, not a stable baked field.
                PitchAlignment = label.PitchAlignment,
            };
        internal static SymbolTileLabelBlock Bake(List<LabelInstance> labels, int slotCount, in double3 tileOriginRender)
        {
            var block = new SymbolTileLabelBlock();
            try
            {
                int rawCount = labels?.Count ?? 0;
                CountSizes(labels, rawCount, out int pointCount, out int curvedCount, out int quadCount,
                    out int glyphCount, out int anchorCount, out int anchorFadeCount, out int worldPointCount);

                block.Kinds = new NativeArray<byte>(rawCount, Allocator.Persistent);
                block.Detail = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.WorldStart = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.WorldCount = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.RepAnchor = new NativeArray<double3>(rawCount, Allocator.Persistent);

                block.Points = new NativeArray<PointStageInput>(pointCount, Allocator.Persistent);
                block.PointQuadStart = new NativeArray<int>(pointCount, Allocator.Persistent);
                block.PointQuadCount = new NativeArray<int>(pointCount, Allocator.Persistent);

                block.Curveds = new NativeArray<CurvedStageInput>(curvedCount, Allocator.Persistent);
                block.CurvedGlyphStart = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedGlyphCount = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedAnchorStart = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedAnchorCount = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedAnchorFadeStart = new NativeArray<int>(curvedCount, Allocator.Persistent);

                block.Quads = new NativeArray<SymbolQuad>(quadCount, Allocator.Persistent);
                block.Glyphs = new NativeArray<CurvedGlyph>(glyphCount, Allocator.Persistent);
                block.Anchors = new NativeArray<LineAnchor>(anchorCount, Allocator.Persistent);
                block.WorldPoints = new NativeArray<double3>(worldPointCount, Allocator.Persistent);
                block.WorldUps = new NativeArray<float3>(worldPointCount, Allocator.Persistent);
                block.AnchorFadeIds = new NativeArray<long>(anchorFadeCount, Allocator.Persistent);

                Fill(block, labels, rawCount, slotCount, tileOriginRender);
                block.TileKey = ResolveTileKey(labels, rawCount);
                return block;
            }
            catch
            {
                block.Dispose();
                throw;
            }
        }

        // First pass: exact per-array sizes, so every NativeArray below is allocated ONCE at its final size
        // (Burst-safe fixed arrays — no growth). Mirrors AddPoint/AddCurved's contribution to each pool 1:1.
        private static void CountSizes(List<LabelInstance> labels, int rawCount, out int pointCount, out int curvedCount,
            out int quadCount, out int glyphCount, out int anchorCount, out int anchorFadeCount, out int worldPointCount)
        {
            pointCount = 0; curvedCount = 0; quadCount = 0; glyphCount = 0;
            anchorCount = 0; anchorFadeCount = 0; worldPointCount = 0;
            for (int i = 0; i < rawCount; i++)
            {
                LabelInstance label = labels[i];
                if (label == null) { pointCount++; continue; } // inert placeholder — still consumes a Points slot
                if (label.Placement == SymbolPlacement.Point)
                {
                    pointCount++;
                    quadCount += label.Layout?.Quads?.Count ?? 0;
                    worldPointCount += 1; // point anchor → 1 world point
                }
                else
                {
                    curvedCount++;
                    glyphCount += label.CurvedGlyphs?.Count ?? 0;
                    int anchorLen = math.min(label.LineAnchors?.Length ?? 0, LabelStagingMath.MaxAnchorsPerLine);
                    anchorCount += anchorLen;
                    anchorFadeCount += anchorLen + 1; // + the centred fallback
                    worldPointCount += label.PathRender?.Length ?? 0;
                }
            }
        }

        // Second pass: fill every array, running each pool's write cursor forward — the SAME per-label field
        // math as the oracle's AddPoint/AddCurved (both call BuildPointInput/BuildCurvedInput here), and
        // the SAME Max*/record bookkeeping, just writing into pre-sized NativeArrays instead of growable arrays.
        private static void Fill(SymbolTileLabelBlock block, List<LabelInstance> labels, int rawCount, int slotCount, in double3 tileOriginRender)
        {
            int pointIdx = 0, curvedIdx = 0, quadIdx = 0, glyphIdx = 0, anchorIdx = 0, anchorFadeIdx = 0, worldPointIdx = 0;

            for (int i = 0; i < rawCount; i++)
            {
                LabelInstance label = labels[i];
                if (label == null)
                {
                    // Null-slot invariant (SymbolTileLabelBlock's PIN doc): an inert record — Kind=Point, zero
                    // quad/world contribution, zero Max* share — so localIndex == i for every later label.
                    int slot = pointIdx++;
                    block.Points[slot] = default;
                    block.PointQuadStart[slot] = quadIdx;
                    block.PointQuadCount[slot] = 0;
                    block.Kinds[i] = (byte)LabelRecordKind.Point;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldPointIdx;
                    block.WorldCount[i] = 0;
                    block.RepAnchor[i] = double3.zero;
                    continue;
                }

                if (label.Placement == SymbolPlacement.Point)
                {
                    IReadOnlyList<SymbolQuad> quads = label.Layout?.Quads;
                    int quadCount = quads?.Count ?? 0;
                    int quadStart = quadIdx;
                    for (int q = 0; q < quadCount; q++) block.Quads[quadIdx++] = quads[q];

                    // §10 D10: LabelPairing resolves the PROPOSAL (a rider can go missing to per-label
                    // shaping isolation) against the TILE list — this loop's own `labels` — so a half-built
                    // pair dissolves back into two None-role labels.
                    LabelPairRole pairRole = LabelPairRole.None;
                    if (LabelPairing.TryGetRider(labels, i, out _)) pairRole = LabelPairRole.Owner;
                    else if (LabelPairing.IsRider(labels, i)) pairRole = LabelPairRole.Rider;

                    int slot = pointIdx++;
                    block.Points[slot] = BuildPointInput(label, slotCount, tileOriginRender, pairRole);
                    block.PointQuadStart[slot] = quadStart;
                    block.PointQuadCount[slot] = quadCount;

                    int worldStart = worldPointIdx;
                    block.WorldPoints[worldPointIdx] = label.AnchorRender;
                    block.WorldUps[worldPointIdx] = NarrowUp(label.UpRender);
                    worldPointIdx++;

                    block.Kinds[i] = (byte)LabelRecordKind.Point;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldStart;
                    block.WorldCount[i] = 1;
                    block.RepAnchor[i] = label.AnchorRender;

                    // Mirrors SymbolLabelBatch.AddPoint: one AABB box + its quads, one candidate.
                    // §10 D8: UNCHANGED even for a paired half. A pair still spans TWO records here (icon +
                    // text), each contributing 1 to MaxBoxes/MaxCandidates as before — but at stage time it
                    // collapses to ONE real LabelCandidate with BoxCount/EmitCount up to 2. So MaxBoxes covers
                    // a pair's two boxes EXACTLY, and MaxCandidates (the emit pool's size, LabelPlacementSystem
                    // PreSizeStageOutputs) covers its two emits EXACTLY too, with one candidate slot of slack.
                    // This is the proof PreSizeStageOutputs needs no change for pairing.
                    block.MaxBoxes += 1; block.MaxQuads += quadCount; block.MaxCandidates += 1;
                }
                else
                {
                    IReadOnlyList<CurvedGlyph> glyphs = label.CurvedGlyphs;
                    int glyphCount = glyphs?.Count ?? 0;
                    int glyphStart = glyphIdx;
                    for (int g = 0; g < glyphCount; g++) block.Glyphs[glyphIdx++] = glyphs[g];

                    LineAnchor[] anchors = label.LineAnchors;
                    int anchorLen = math.min(anchors?.Length ?? 0, LabelStagingMath.MaxAnchorsPerLine);
                    int anchorStart = anchorIdx;
                    for (int a = 0; a < anchorLen; a++) block.Anchors[anchorIdx++] = anchors[a];

                    int anchorFadeStart = anchorFadeIdx;
                    for (int a = 0; a < anchorLen; a++)
                        block.AnchorFadeIds[anchorFadeIdx++] =
                            LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, a);
                    block.AnchorFadeIds[anchorFadeIdx++] =
                        LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, -1); // fallback

                    int slot = curvedIdx++;
                    block.Curveds[slot] = BuildCurvedInput(label, slotCount, tileOriginRender);
                    block.CurvedGlyphStart[slot] = glyphStart; block.CurvedGlyphCount[slot] = glyphCount;
                    block.CurvedAnchorStart[slot] = anchorStart; block.CurvedAnchorCount[slot] = anchorLen;
                    block.CurvedAnchorFadeStart[slot] = anchorFadeStart;

                    double3[] path = label.PathRender;
                    double3[] pathUps = label.PathUpRender; // may be null on a legacy/hand-built label — guarded below
                    int pathLen = path?.Length ?? 0;
                    int worldStart = worldPointIdx;
                    for (int v = 0; v < pathLen; v++)
                    {
                        block.WorldPoints[worldPointIdx] = path[v];
                        block.WorldUps[worldPointIdx] = pathUps != null && v < pathUps.Length
                            ? NarrowUp(pathUps[v])
                            : float3.zero; // bug signal, not a supported state — see T-3
                        worldPointIdx++;
                    }
                    double3 rep = pathLen > 0 ? path[pathLen / 2] : label.AnchorRender;

                    block.Kinds[i] = (byte)LabelRecordKind.Curved;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldStart;
                    block.WorldCount[i] = pathLen;
                    block.RepAnchor[i] = rep;

                    // Mirrors SymbolLabelBatch.AddCurved: worst case every anchor + the centred fallback stages.
                    int placements = anchorLen + 1;
                    block.MaxBoxes += placements * glyphCount; block.MaxQuads += placements * glyphCount;
                    block.MaxCandidates += placements;
                }
            }

            // No count fields to publish: CountSizes sized every array to exactly what this loop just wrote,
            // so each array's Length already IS its count (see SymbolTileLabelBlock's header). If the two
            // passes ever disagreed, the write above would have thrown IndexOutOfRange at the divergence
            // rather than silently leaving a count short of Length — which is the point of not carrying one.
        }

        // P2: manual per-component narrow (convention — no assumed double3→float3 cast operator; mirrors
        // BuildPointInput's anchorLocal narrowing above). Up is a DIRECTION (pre-RTC render-space), so unlike
        // AnchorRender it needs no tile-origin subtraction — narrow only. Internal (not private): the test-side
        // parity oracle (SymbolLabelBatchBuilder) reuses it verbatim rather than duplicating the cast, so the
        // two narrowings cannot drift apart.
        internal static float3 NarrowUp(in double3 up) => new float3((float)up.x, (float)up.y, (float)up.z);

        // Every label in one build's list shares the same physical tile (one (source, tile) build) — the
        // first non-null label's TileKey identifies the whole block; an all-null build (every label failed)
        // has no tile identity to report, so it stays 0 (inert — nothing downstream keys off it in Stage 1).
        private static long ResolveTileKey(List<LabelInstance> labels, int rawCount)
        {
            for (int i = 0; i < rawCount; i++)
                if (labels[i] != null) return labels[i].TileKey;
            return 0;
        }
    }
}
