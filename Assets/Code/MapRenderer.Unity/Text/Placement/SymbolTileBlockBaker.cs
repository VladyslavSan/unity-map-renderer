// Namespace-collision guard (see GlyphAtlasTexture.cs's header): TOP-LEVEL `using Unity.Mathematics;` and
// unqualified types, never an inline `Unity.Mathematics.X`.

using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Bakes one tile's build-time <see cref="SymbolTileBuffer"/> into a fresh native <see cref="SymbolTileBlock"/>.
    /// It runs on the main thread once per tile commit (<c>SymbolSubsystem.RunTailAsync</c>), where the glyph quads
    /// exist. Non-obvious why: it takes no <c>IProjection</c>, because one build is one physical tile, so one
    /// <c>tileOriginRender</c> serves every symbol.
    /// </summary>
    internal static class SymbolTileBlockBaker
    {
        /// <summary>The per-symbol point field math of <see cref="Bake"/>. The golden test calls this same helper
        /// for its expected values, so the two cannot drift. It resolves everything stable about
        /// <paramref name="symbol"/> except its glyph quads and world anchor, which the caller copies.</summary>
        internal static PointStageInput BuildPointInput(in ShapedSymbol symbol, int slotCount, in double3 tileOriginRender,
            SymbolPairRole pairRole)
        {
            float4 color  = SymbolPlacementSystem.LinearColor(symbol.Paint);
            float4 halo   = SymbolPlacementSystem.LinearHaloColor(symbol.Paint);
            // Icon FadeId identity rides symbol.IconImageId (0 for text). It ignores pairRole: a rider's FadeId
            // stays resolved so the gather Compact pass's fade-alive probe stays well-defined.
            long   fadeId = SymbolPlacementSystem.PointFadeId(symbol.AnchorRender, symbol.MaterialIndex, symbol.TextId, symbol.IconImageId);

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
                HaloColor = halo, HaloWidthPx = symbol.Paint.HaloWidthPx, HaloBlurPx = symbol.Paint.HaloBlurPx,
                IconRotateRadians = symbol.IconRotateRadians,
                FadeId = fadeId,
                // Thread the icon/text discriminator through.
                AtlasKind = symbol.Kind == SymbolKind.Icon ? SymbolKind.Icon : SymbolKind.Text,
                AnchorLocal = anchorLocal, TileOriginRender = tileOriginRender,
                // RESOLVED role (SymbolPairing already decided whether the proposal holds) — the stage job
                // needs nothing else; a paired owner's rider is the next point symbol.
                PairRole = pairRole,
                // Read by StagePointPair only — a half whose role did not resolve carries it harmlessly
                // (nothing outside the pair arm looks at it).
                PairOptional = symbol.PairOptional,
            };
        }

        /// <summary>The per-symbol curved field math of <see cref="Bake"/>. The golden test calls this same helper
        /// for its expected values, so the two cannot drift. It resolves everything stable about
        /// <paramref name="symbol"/> except its glyphs, anchors, anchor fade ids and world path.</summary>
        internal static CurvedStageInput BuildCurvedInput(in ShapedSymbol symbol, int slotCount, in double3 tileOriginRender)
            => new CurvedStageInput
            {
                TextSizePx = symbol.TextSizePx, PaddingPx = symbol.PaddingPx, SortKey = symbol.SortKey,
                FeatureIndex = symbol.FeatureIndex, TileKey = symbol.TileKey,
                Slot = SymbolPlacementSystem.ClampSlot(symbol.MaterialIndex, slotCount),
                AllowOverlap = symbol.AllowOverlap, IgnorePlacement = symbol.IgnorePlacement,
                TranslatePx = symbol.TranslatePx, TranslateAnchor = symbol.TranslateAnchor,
                MaxAngleDeg = symbol.MaxAngleDeg, KeepUpright = symbol.KeepUpright,
                Color = SymbolPlacementSystem.LinearColor(symbol.Paint),
                HaloColor = SymbolPlacementSystem.LinearHaloColor(symbol.Paint),
                HaloWidthPx = symbol.Paint.HaloWidthPx, HaloBlurPx = symbol.Paint.HaloBlurPx,
                TileOriginRender = tileOriginRender,
                // The icon/text discriminator, textually identical to BuildPointInput's above — a
                // map-aligned line icon is a one-glyph curved symbol sampling the SPRITE sheet.
                AtlasKind = symbol.Kind == SymbolKind.Icon ? SymbolKind.Icon : SymbolKind.Text,
                IconRotateRadians = symbol.IconRotateRadians,
                // StageCurved's world-arc predicate. MetresPerLogicalPixel is absent: StageJob patches it
                // per frame from the camera.
                PitchAlignment = symbol.PitchAlignment,
            };

        /// <summary>Bakes <paramref name="buffer"/> into a fresh <see cref="SymbolTileBlock"/>.
        /// <paramref name="slotCount"/> clamps each symbol's material slot. On any exception mid-bake, it disposes
        /// the partial block before it rethrows, so no allocation leaks.</summary>
        internal static SymbolTileBlock Bake(SymbolTileBuffer buffer, int slotCount, in double3 tileOriginRender)
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

                Fill(block, buffer, rawCount, slotCount, tileOriginRender);
                block.TileKey = ResolveTileKey(buffer, rawCount);
                return block;
            }
            catch
            {
                block.Dispose();
                throw;
            }
        }

        // First pass: exact per-array sizes, so every NativeArray is allocated once at its final size. Each
        // symbol's *Count field is its contribution to that pool.
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

        // Second pass: fill every pre-sized array, running each pool's write cursor forward, plus the Max*
        // bookkeeping.
        private static void Fill(SymbolTileBlock block, SymbolTileBuffer buffer, int rawCount, int slotCount,
            in double3 tileOriginRender)
        {
            int pointIdx = 0, curvedIdx = 0, quadIdx = 0, glyphIdx = 0, anchorIdx = 0, anchorFadeIdx = 0, worldPointIdx = 0;

            for (int i = 0; i < rawCount; i++)
            {
                ShapedSymbol symbol = buffer.Symbols[i];
                // Native columns set for EVERY slot in the common prologue. MaterialIndexes mirrors the field;
                // PairRoles is written per-branch below (it needs the resolved role).
                block.MaterialIndexes[i] = symbol.MaterialIndex;
                // a straight copy — StyledSymbolTileBuilder.Shape already interned Text/IconImage into
                // TextId/IconImageId when it emitted this symbol, so the block's columns just mirror the raw slot.
                block.TextIds[i] = symbol.TextId;
                block.IconImageIds[i] = symbol.IconImageId;

                if (symbol.Placement == SymbolPlacement.Point)
                {
                    int quadStart = quadIdx;
                    for (int q = 0; q < symbol.QuadCount; q++) block.Quads[quadIdx++] = buffer.Quads[symbol.QuadStart + q];

                    // SymbolPairing resolves the proposal against the tile list, so a pair whose rider failed
                    // to shape dissolves back into None-role symbols.
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

                    // Mirrors SymbolBatch.AddPoint: one box, its quads, one candidate, also for a pair half. The
                    // two halves reserve the two boxes and two emits that the pair stages as one candidate.
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
                    block.PairRoles[i] = SymbolPairRole.None; // fence: a curved symbol is never a pair half

                    // Mirrors SymbolBatch.AddCurved: worst case every anchor + the centred fallback stages.
                    int placements = anchorLen + 1;
                    block.MaxBoxes += placements * symbol.GlyphCount; block.MaxQuads += placements * symbol.GlyphCount;
                    block.MaxCandidates += placements;
                }
            }

            // No count fields to publish: each array's Length is its count. A disagreement between the two
            // passes throws IndexOutOfRange at the divergence.
        }

        // Up is a direction, so unlike AnchorRender it needs no tile-origin subtraction. Internal so the golden
        // test states its expected world-ups through the same cast.
        internal static float3 NarrowUp(in double3 up) => new float3((float)up.x, (float)up.y, (float)up.z);

        // One build is one physical tile, so the first symbol's TileKey identifies the block. An empty build
        // has no tile identity and reports 0.
        private static long ResolveTileKey(SymbolTileBuffer buffer, int rawCount)
            => rawCount > 0 ? buffer.Symbols[0].TileKey : 0;
    }
}
