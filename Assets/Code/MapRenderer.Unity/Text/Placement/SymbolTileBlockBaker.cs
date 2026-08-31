// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, never an inline
// `Unity.Mathematics.X`. Unity-side (needs Unity.Collections' NativeArray — Core stays engine-free).

using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Symbol-symbol perf Phase 1 / Stage 1 (design doc §4, §5 B): bakes one tile's build-time
    /// <see cref="SymbolTileBuffer"/> into a fresh native <see cref="SymbolTileBlock"/>. The per-symbol
    /// field math lives in the <see cref="BuildPointInput"/>/<see cref="BuildCurvedInput"/> helpers, shared by
    /// this bake and by the baker's golden test — the test states its expected per-symbol values by calling the
    /// SAME helpers rather than re-deriving them, a drift-guard so the test's expectation cannot diverge from
    /// what the bake produces.
    ///
    /// <para>Runs on the MAIN thread, once per tile commit (<c>SymbolSubsystem.RunTailAsync</c>) — glyph
    /// quads / curved glyphs are only materialized there (the per-layer shape tail), so there is nothing left
    /// to bake off it. No <c>IProjection</c> parameter (bake-safety): every symbol in one build's buffer
    /// belongs to the SAME physical tile (one <c>(source, tile)</c> build), so the caller's single
    /// <paramref name="tileOriginRender"/> already IS the launch-time <c>TileRenderOrigin.Project</c> result
    /// folded into every symbol's <c>AnchorRender</c> — a single origin value suffices because one build is
    /// exactly one tile (no multi-tile set to keep a per-tile origin cache for).</para>
    /// </summary>
    internal static class SymbolTileBlockBaker
    {
        /// <summary>Bake <paramref name="buffer"/> (one tile's build output — <see cref="SymbolTileBuffer.Symbols"/>
        /// is a dense list, one symbol per successfully-shaped symbol) into a fresh
        /// <see cref="SymbolTileBlock"/>. <paramref name="slotCount"/> clamps each symbol's material slot
        /// (via <c>ClampSlot</c>, to the material-array bounds).
        ///
        /// <para>(G) Exception-safety: every array is allocated INTO the returned block; on any exception
        /// mid-bake (allocation or fill), the partially-built block is disposed (frees whatever
        /// <see cref="NativeArray{T}.IsCreated"/>) before the exception is rethrown — never a partial-allocation
        /// leak.</para></summary>
        /// <summary>Drift-guard (design §5 B): the per-symbol POINT field math used by <see cref="Bake"/> (the
        /// production build-time bake) and by the baker's golden test (which states its expected per-symbol
        /// values by calling this SAME helper rather than re-deriving them) — ONE implementation, so the test's
        /// expectation cannot diverge from what the bake produces. Resolves everything stable about
        /// <paramref name="symbol"/> EXCEPT its glyph quads/world anchor (copied by the caller into its own
        /// pool). <paramref name="tileOriginRender"/> is the symbol's tile's render-space origin — a single value
        /// for the single-tile bake.</summary>
        internal static PointStageInput BuildPointInput(in ShapedSymbol symbol, int slotCount, in double3 tileOriginRender,
            SymbolPairRole pairRole)
        {
            float4 color  = SymbolPlacementSystem.LinearColor(symbol.Paint);
            // I6: icon FadeId identity now rides symbol.IconImage (null for text, so a text symbol's FadeId
            // is unchanged — PointFadeId's guard-skip fold). §10 D9: UNCONDITIONAL on pairRole — a pair's
            // identity IS the owner's existing icon identity; a rider's FadeId is never read by a candidate
            // (SymbolStageJob skips staging a Rider symbol entirely) but is left correctly resolved so
            // the gather Compact pass's per-symbol fade-alive probe stays well-defined.
            long   fadeId = SymbolPlacementSystem.PointFadeId(symbol.AnchorRender, symbol.MaterialIndex, symbol.Text, symbol.IconImage);

            // Manual per-component narrow (convention — no assumed double3→float3 cast operator; mirrors
            // FloatingOrigin.TileToSceneRebased's identical narrowing).
            float3 anchorLocal = new float3(
                (float)(symbol.AnchorRender.x - tileOriginRender.x),
                (float)(symbol.AnchorRender.y - tileOriginRender.y),
                (float)(symbol.AnchorRender.z - tileOriginRender.z));

            return new PointStageInput
            {
                // dynamic (ScreenPx/Depth/Projected/WasPlacedLastFrame) left default — patched per frame.
                BoundsMin = symbol.BoundsMin,
                BoundsMax = symbol.BoundsMax,
                TextSizePx = symbol.TextSizePx, PaddingPx = symbol.PaddingPx, SortKey = symbol.SortKey,
                FeatureIndex = symbol.FeatureIndex, TileKey = symbol.TileKey,
                Slot = SymbolPlacementSystem.ClampSlot(symbol.MaterialIndex, slotCount),
                AllowOverlap = symbol.AllowOverlap, IgnorePlacement = symbol.IgnorePlacement,
                TranslatePx = symbol.TranslatePx, TranslateAnchor = symbol.TranslateAnchor,
                RotationAlignment = symbol.RotationAlignment, Color = color,
                IconRotateRadians = symbol.IconRotateRadians,
                FadeId = fadeId,
                // I5a: thread the icon/text discriminator through — NOT yet consumed by the draw side (I5b).
                AtlasKind = symbol.Kind == SymbolKind.Icon ? SymbolKind.Icon : SymbolKind.Text,
                AnchorLocal = anchorLocal, TileOriginRender = tileOriginRender,
                // §10 D8/D10: RESOLVED role (SymbolPairing already decided whether the proposal holds) — the
                // stage job needs nothing else; a paired owner's rider is the next point symbol.
                PairRole = pairRole,
                // Stage C: read by StagePointPair only — a half whose role did not resolve carries it
                // harmlessly (nothing outside the pair arm looks at it).
                PairOptional = symbol.PairOptional,
            };
        }

        /// <summary>Drift-guard (design §5 B): the per-symbol CURVED field math used by <see cref="Bake"/> (the
        /// production build-time bake) and by the baker's golden test (which states its expected per-symbol
        /// values by calling this SAME helper rather than re-deriving them) — ONE implementation, so the test's
        /// expectation cannot diverge from what the bake produces. Resolves everything stable about
        /// <paramref name="symbol"/> EXCEPT its glyphs/anchors/anchor-fade-ids/world path (copied by the caller
        /// into its own pool). <paramref name="tileOriginRender"/> is the symbol's tile's render-space origin —
        /// a single value for the single-tile bake.</summary>
        internal static CurvedStageInput BuildCurvedInput(in ShapedSymbol symbol, int slotCount, in double3 tileOriginRender)
            => new CurvedStageInput
            {
                TextSizePx = symbol.TextSizePx, PaddingPx = symbol.PaddingPx, SortKey = symbol.SortKey,
                FeatureIndex = symbol.FeatureIndex, TileKey = symbol.TileKey,
                Slot = SymbolPlacementSystem.ClampSlot(symbol.MaterialIndex, slotCount),
                AllowOverlap = symbol.AllowOverlap, IgnorePlacement = symbol.IgnorePlacement,
                TranslatePx = symbol.TranslatePx, TranslateAnchor = symbol.TranslateAnchor,
                MaxAngleDeg = symbol.MaxAngleDeg, KeepUpright = symbol.KeepUpright,
                Color = SymbolPlacementSystem.LinearColor(symbol.Paint), TileOriginRender = tileOriginRender,
                // P-B: the icon/text discriminator, textually identical to BuildPointInput's above — a
                // map-aligned line icon is a one-glyph curved symbol sampling the SPRITE sheet.
                AtlasKind = symbol.Kind == SymbolKind.Icon ? SymbolKind.Icon : SymbolKind.Text,
                IconRotateRadians = symbol.IconRotateRadians,
                // W1: the resolved pitch alignment — StageCurved's world-arc predicate. MetresPerLogicalPixel
                // is deliberately absent: it is this frame's camera ruler, patched per frame by
                // SymbolStageJob, not a stable baked field.
                PitchAlignment = symbol.PitchAlignment,
            };
        internal static SymbolTileBlock Bake(SymbolTileBuffer buffer, int slotCount, in double3 tileOriginRender,
            SymbolStringTable stringTable)
        {
            var block = new SymbolTileBlock();
            try
            {
                int rawCount = buffer?.Symbols?.Count ?? 0;
                CountSizes(buffer, rawCount, out int pointCount, out int curvedCount, out int quadCount,
                    out int glyphCount, out int anchorCount, out int anchorFadeCount, out int worldPointCount);

                block.Kinds = new NativeArray<SymbolPlacementKind>(rawCount, Allocator.Persistent);
                block.Detail = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.WorldStart = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.WorldCount = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.RepAnchor = new NativeArray<double3>(rawCount, Allocator.Persistent);
                block.MaterialIndexes = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.PairRoles = new NativeArray<SymbolPairRole>(rawCount, Allocator.Persistent);
                block.TextIds = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.IconImageIds = new NativeArray<int>(rawCount, Allocator.Persistent);

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

                Fill(block, buffer, rawCount, slotCount, tileOriginRender, stringTable);
                block.TileKey = ResolveTileKey(buffer, rawCount);
                return block;
            }
            catch
            {
                block.Dispose();
                throw;
            }
        }

        // First pass: exact per-array sizes, so every NativeArray below is allocated ONCE at its final size
        // (Burst-safe fixed arrays — no growth). Mirrors Shape's contribution to each buffer pool 1:1
        // (each symbol's *Count field IS that contribution — no re-derivation needed here).
        private static void CountSizes(SymbolTileBuffer buffer, int rawCount, out int pointCount, out int curvedCount,
            out int quadCount, out int glyphCount, out int anchorCount, out int anchorFadeCount, out int worldPointCount)
        {
            pointCount = 0; curvedCount = 0; quadCount = 0; glyphCount = 0;
            anchorCount = 0; anchorFadeCount = 0; worldPointCount = 0;
            for (int i = 0; i < rawCount; i++)
            {
                ShapedSymbol symbol = buffer.Symbols[i];
                if (symbol.Placement == SymbolPlacement.Point)
                {
                    pointCount++;
                    quadCount += symbol.QuadCount;
                    worldPointCount += 1; // point anchor → 1 world point
                }
                else
                {
                    curvedCount++;
                    glyphCount += symbol.GlyphCount;
                    int anchorLen = math.min(symbol.AnchorCount, SymbolStagingMath.MaxAnchorsPerLine);
                    anchorCount += anchorLen;
                    anchorFadeCount += anchorLen + 1; // + the centred fallback
                    worldPointCount += symbol.PathCount;
                }
            }
        }

        // Second pass: fill every array, running each pool's write cursor forward — the per-symbol field math via
        // BuildPointInput/BuildCurvedInput (the same helpers the golden test states its expectations through)
        // plus the Max*/symbol bookkeeping, writing into pre-sized NativeArrays instead of growable arrays.
        private static void Fill(SymbolTileBlock block, SymbolTileBuffer buffer, int rawCount, int slotCount,
            in double3 tileOriginRender, SymbolStringTable stringTable)
        {
            int pointIdx = 0, curvedIdx = 0, quadIdx = 0, glyphIdx = 0, anchorIdx = 0, anchorFadeIdx = 0, worldPointIdx = 0;

            for (int i = 0; i < rawCount; i++)
            {
                ShapedSymbol symbol = buffer.Symbols[i];
                // Native columns set for EVERY slot in the common prologue. MaterialIndexes mirrors the field;
                // PairRoles is written per-branch below (it needs the resolved role).
                block.MaterialIndexes[i] = symbol.MaterialIndex;
                // Intern INTO the store's own table (main thread — same as CompleteBuild) so the block's ids are
                // byte-identical to the entry's parallel arrays: Intern is idempotent, so a later CompleteBuild
                // pass over the same symbols reuses these exact ids. Intern(null) == 0 handles a null text.
                block.TextIds[i] = stringTable.Intern(symbol.Text);
                block.IconImageIds[i] = stringTable.Intern(symbol.IconImage);

                if (symbol.Placement == SymbolPlacement.Point)
                {
                    int quadStart = quadIdx;
                    for (int q = 0; q < symbol.QuadCount; q++) block.Quads[quadIdx++] = buffer.Quads[symbol.QuadStart + q];

                    // §10 D10: SymbolPairing resolves the PROPOSAL (a rider can go missing to per-symbol
                    // shaping isolation) against the TILE list — this loop's own `buffer.Symbols` — so a
                    // half-built pair dissolves back into two None-role symbols.
                    SymbolPairRole pairRole = SymbolPairRole.None;
                    if (SymbolPairing.TryGetRider(buffer.Symbols, i, out _)) pairRole = SymbolPairRole.Owner;
                    else if (SymbolPairing.IsRider(buffer.Symbols, i)) pairRole = SymbolPairRole.Rider;
                    // Bake the RESOLVED role into a raw-order column so the reconciler reads it instead of
                    // re-running SymbolPairing over the tile list (identical — same resolver, same list).
                    block.PairRoles[i] = pairRole;

                    int slot = pointIdx++;
                    block.Points[slot] = BuildPointInput(in symbol, slotCount, tileOriginRender, pairRole);
                    block.PointQuadStart[slot] = quadStart;
                    block.PointQuadCount[slot] = symbol.QuadCount;

                    int worldStart = worldPointIdx;
                    block.WorldPoints[worldPointIdx] = symbol.AnchorRender;
                    block.WorldUps[worldPointIdx] = NarrowUp(symbol.UpRender);
                    worldPointIdx++;

                    block.Kinds[i] = SymbolPlacementKind.Point;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldStart;
                    block.WorldCount[i] = 1;
                    block.RepAnchor[i] = symbol.AnchorRender;

                    // Mirrors SymbolBatch.AddPoint: one AABB box + its quads, one candidate.
                    // §10 D8: UNCHANGED even for a paired half. A pair still spans TWO symbols here (icon +
                    // text), each contributing 1 to MaxBoxes/MaxCandidates as before — but at stage time it
                    // collapses to ONE real SymbolCandidate with BoxCount/EmitCount up to 2. So MaxBoxes covers
                    // a pair's two boxes EXACTLY, and MaxCandidates (the emit pool's size, SymbolPlacementSystem
                    // PreSizeStageOutputs) covers its two emits EXACTLY too, with one candidate slot of slack.
                    // This is the proof PreSizeStageOutputs needs no change for pairing.
                    block.MaxBoxes += 1; block.MaxQuads += symbol.QuadCount; block.MaxCandidates += 1;
                }
                else
                {
                    int glyphStart = glyphIdx;
                    for (int g = 0; g < symbol.GlyphCount; g++) block.Glyphs[glyphIdx++] = buffer.Glyphs[symbol.GlyphStart + g];

                    int anchorLen = math.min(symbol.AnchorCount, SymbolStagingMath.MaxAnchorsPerLine);
                    int anchorStart = anchorIdx;
                    for (int a = 0; a < anchorLen; a++) block.Anchors[anchorIdx++] = buffer.Anchors[symbol.AnchorStart + a];

                    int anchorFadeStart = anchorFadeIdx;
                    for (int a = 0; a < anchorLen; a++)
                        block.AnchorFadeIds[anchorFadeIdx++] =
                            SymbolStagingMath.LineFadeId(symbol.TileKey, symbol.MaterialIndex, symbol.FeatureIndex, a);
                    block.AnchorFadeIds[anchorFadeIdx++] =
                        SymbolStagingMath.LineFadeId(symbol.TileKey, symbol.MaterialIndex, symbol.FeatureIndex, -1); // fallback

                    int slot = curvedIdx++;
                    block.Curveds[slot] = BuildCurvedInput(in symbol, slotCount, tileOriginRender);
                    block.CurvedGlyphStart[slot] = glyphStart; block.CurvedGlyphCount[slot] = symbol.GlyphCount;
                    block.CurvedAnchorStart[slot] = anchorStart; block.CurvedAnchorCount[slot] = anchorLen;
                    block.CurvedAnchorFadeStart[slot] = anchorFadeStart;

                    int pathLen = symbol.PathCount;
                    int worldStart = worldPointIdx;
                    for (int v = 0; v < pathLen; v++)
                    {
                        block.WorldPoints[worldPointIdx] = buffer.Path[symbol.PathStart + v];
                        block.WorldUps[worldPointIdx] = NarrowUp(buffer.PathUp[symbol.PathStart + v]);
                        worldPointIdx++;
                    }
                    double3 rep = pathLen > 0 ? buffer.Path[symbol.PathStart + pathLen / 2] : symbol.AnchorRender;

                    block.Kinds[i] = SymbolPlacementKind.Curved;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldStart;
                    block.WorldCount[i] = pathLen;
                    block.RepAnchor[i] = rep;
                    block.PairRoles[i] = SymbolPairRole.None; // §10 fence: a curved symbol is never a pair half

                    // Mirrors SymbolBatch.AddCurved: worst case every anchor + the centred fallback stages.
                    int placements = anchorLen + 1;
                    block.MaxBoxes += placements * symbol.GlyphCount; block.MaxQuads += placements * symbol.GlyphCount;
                    block.MaxCandidates += placements;
                }
            }

            // No count fields to publish: CountSizes sized every array to exactly what this loop just wrote,
            // so each array's Length already IS its count (see SymbolTileBlock's header). If the two
            // passes ever disagreed, the write above would have thrown IndexOutOfRange at the divergence
            // rather than silently leaving a count short of Length — which is the point of not carrying one.
        }

        // P2: manual per-component narrow (convention — no assumed double3→float3 cast operator; mirrors
        // BuildPointInput's anchorLocal narrowing above). Up is a DIRECTION (pre-RTC render-space), so unlike
        // AnchorRender it needs no tile-origin subtraction — narrow only. Internal (not private): the baker's
        // golden test reuses it verbatim to state its expected world-ups rather than duplicating the cast, so
        // the two narrowings cannot drift apart.
        internal static float3 NarrowUp(in double3 up) => new float3((float)up.x, (float)up.y, (float)up.z);

        // Every symbol in one build's list shares the same physical tile (one (source, tile) build) — the
        // first symbol's TileKey identifies the whole block; an empty build (every symbol failed and was
        // skipped) has no tile identity to report, so it stays 0 (nothing downstream keys off it in Stage 1).
        private static long ResolveTileKey(SymbolTileBuffer buffer, int rawCount)
            => rawCount > 0 ? buffer.Symbols[0].TileKey : 0;
    }
}
