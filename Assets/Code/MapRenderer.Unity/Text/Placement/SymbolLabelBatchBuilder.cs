// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, never an inline
// `Unity.Mathematics.X`. Unity-side (not Core) because the byte-parity colour conversion needs UnityEngine.Color.

using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Converts the managed <see cref="LabelInstance"/> carriers of one collected label set into the blittable
    /// <see cref="SymbolLabelBatch"/> SoA (Lever C step 2). Run at the aggregation seam
    /// (<see cref="Text.SymbolLabelSubsystem.CurrentBatch"/>) so every frame's gather/project/stage reads the SoA
    /// directly; the conversion itself is allocation-free (reused buffers) but is CPU work on the main thread.
    ///
    /// <para>Resolves here exactly the managed bits the staging math can't: the glyph/quad copies out of the
    /// managed lists, the sRGB→linear vertex colour (<see cref="LabelPlacementSystem.LinearColor"/>, matched to
    /// the old path for byte-parity), the string-hashed point fade-id and the per-anchor line fade-ids. The
    /// per-frame dynamic values (projected screen/depth/valid, last-frame incumbency) are NOT stored — the
    /// consumer patches them each frame.</para>
    /// </summary>
    internal static class SymbolLabelBatchBuilder
    {
        // Reused tile-dedup map (TileKey → unique-tile index), CLEARED not reallocated each Build so the demo
        // hot path (which rebuilds every Tick) allocates nothing in steady state (T4). Build is main-thread only
        // and non-reentrant (demo Tick + the subsystem's CurrentBatch both run on the main thread), so one shared
        // instance is safe.
        [System.ThreadStatic] private static Dictionary<long, int> _tileDedup;

        // Epic A / A1 (design §11 A1 D2 — the BLOCKER fix): a SEPARATE per-Build cache, TileKey → the
        // world-anchored render-space tile origin (TileRenderOrigin.Project), decoupled from the coverage
        // dedup above (whose ResolveTileIndex deliberately returns -1 in the demo/test seam — see its own
        // doc). Reused + cleared each Build (same no-alloc discipline as _tileDedup).
        [System.ThreadStatic] private static Dictionary<long, double3> _worldOriginCache;

        // SoA-build breakdown markers (nest under MapRenderer.Symbol.BatchBuild.SoA), splitting the per-frame
        // LabelInstance→SoA conversion into three natures so the profiler shows what actually dominates:
        //   Project — per-unique-tile 4-corner projection (movable to build-time; measured ~7% of SoA).
        //   Hash    — fade-id string hash + sRGB→linear colour, the movable-to-build-time conversion (~22%).
        //   Copy    — bulk glyph/quad/anchor appends into the ONE contiguous pool the Burst jobs read; INTRINSIC
        //             per frame (a stale/off-main decision still gathers survivors into one buffer) — measured
        //             the dominant ~70%, which is why the answer is off-main/GPU-projected rendering, not a
        //             build-time precompute (that would only retire Hash+Project). Marker Begin/End is a near-
        //             no-op when the profiler is not recording, so the per-label cost in production is negligible.
        private static readonly ProfilerMarker PmSoAProject = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.BatchBuild.SoA.Project");
        private static readonly ProfilerMarker PmSoAHash    = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.BatchBuild.SoA.Hash");
        private static readonly ProfilerMarker PmSoACopy    = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.BatchBuild.SoA.Copy");

        /// <summary>Rebuild <paramref name="batch"/> in place from <paramref name="labels"/> (collected order
        /// preserved so the collision ordinal — and thus the mesh — stays byte-identical). <paramref name="slotCount"/>
        /// clamps each label's material slot (mirrors the old per-frame ClampSlot).</summary>
        /// <param name="activeCount">Labels at index &lt; this are ACTIVE; index ≥ this are DEPARTING (a tile leaving
        /// cover, retained for a fade-out — <see cref="Text.SymbolTileLabelStore.CollectInto(System.Collections.Generic.List{LabelInstance}, double, out int)"/>
        /// puts them last). Each such record is flagged so the placement gather fades it out instead of popping.
        /// Default <see cref="int.MaxValue"/> ⇒ every label is active (the demo / test seam, which has no store).</param>
        public static void Build(SymbolLabelBatch batch, IReadOnlyList<LabelInstance> labels, int slotCount,
            IProjection projection = null, int activeCount = int.MaxValue)
        {
            batch.Reset();
            if (labels == null) return;

            // Dedup tiles by TileKey → the batch's unique-tile index, so each tile's 4 render-space coverage-cull
            // corners are projected + stored ONCE per rebuild (camera-independent), and every record records which
            // tile it belongs to. Reused + cleared (see _tileDedup) so the demo hot path allocates nothing.
            Dictionary<long, int> tileIndexByKey = _tileDedup ??= new Dictionary<long, int>();
            tileIndexByKey.Clear();

            // Epic A / A1 D2: a SEPARATE cache for the world-anchored bake's tile origin — cleared alongside
            // the coverage dedup above but never consulted by it (see ResolveTileOrigin's doc).
            Dictionary<long, double3> worldOriginByKey = _worldOriginCache ??= new Dictionary<long, double3>();
            worldOriginByKey.Clear();

            for (int i = 0; i < labels.Count; i++)
            {
                LabelInstance label = labels[i];
                if (label == null) continue; // nulls contributed no candidate/world-point in the old loop → omit

                int tileIndex = ResolveTileIndex(batch, projection, tileIndexByKey, label.TileKey);
                bool departing = i >= activeCount; // collected-order split: departing labels come last (see CollectInto)
                if (label.Placement == SymbolPlacement.Point)
                    AddPoint(batch, label, slotCount, tileIndex, departing, projection, worldOriginByKey);
                else
                    AddCurved(batch, label, slotCount, tileIndex, departing, projection, worldOriginByKey);
            }
        }

        // Map a label's TileKey to the batch's unique-tile slot, projecting + storing its 4 render-space corners
        // on first sight. Returns -1 when no projection is available (the demo / test seam) — that record then
        // carries no tile and is never coverage-culled (the safe degenerate).
        private static int ResolveTileIndex(SymbolLabelBatch batch, IProjection projection,
            Dictionary<long, int> tileIndexByKey, long tileKey)
        {
            if (projection == null) return -1;
            if (tileIndexByKey.TryGetValue(tileKey, out int idx)) return idx;

            TileId tile = SymbolFeatureExtractor.UnpackTileKey(tileKey);
            // Tile-local corners (extent 1) in ring order TL,TR,BR,BL → geo → render, via the SAME path that built
            // AnchorRender (SymbolFeatureExtractor: ToLonLat → projection.Project). No MercatorBounds/flat-earth
            // shortcut — this stays globe-correct.
            double3 topLeft, topRight, bottomRight, bottomLeft;
            using (PmSoAProject.Auto())
            {
                topLeft     = ProjectCorner(tile, 0.0, 0.0, projection);
                topRight    = ProjectCorner(tile, 1.0, 0.0, projection);
                bottomRight = ProjectCorner(tile, 1.0, 1.0, projection);
                bottomLeft  = ProjectCorner(tile, 0.0, 1.0, projection);
            }
            idx = batch.AddTile(topLeft, topRight, bottomRight, bottomLeft);
            tileIndexByKey[tileKey] = idx;
            return idx;
        }

        private static double3 ProjectCorner(in TileId tile, double px, double py, IProjection projection)
        {
            double2 lonLat = tile.ToLonLat(px, py, 1.0);
            return projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
        }

        /// <summary>
        /// Epic A / A1 (design §11 A1 D2 — the BLOCKER fix): resolves <paramref name="tileKey"/>'s
        /// world-anchored render-space tile origin, ALWAYS valid — <see cref="TileRenderOrigin.Project"/> is
        /// null-safe (<paramref name="projection"/> null ⇒ planar Mercator fallback), so this NEVER returns
        /// the coverage cull's <c>-1</c> degenerate (<see cref="ResolveTileIndex"/> stays untouched, kept
        /// separate on purpose — see that method's doc). Cached per unique tile key within one <see cref="Build"/>.
        /// </summary>
        private static double3 ResolveTileOrigin(long tileKey, IProjection projection, Dictionary<long, double3> cache)
        {
            if (cache.TryGetValue(tileKey, out double3 origin)) return origin;
            origin = TileRenderOrigin.Project(SymbolFeatureExtractor.UnpackTileKey(tileKey), projection);
            cache[tileKey] = origin;
            return origin;
        }

        private static void AddPoint(SymbolLabelBatch batch, LabelInstance label, int slotCount, int tileIndex,
            bool departing, IProjection projection, Dictionary<long, double3> worldOriginByKey)
        {
            // Copy this label's glyph quads into the flat pool (empty/absent layout → 0 quads, stages nothing later).
            IReadOnlyList<SymbolQuad> quads = label.Layout?.Quads;
            int quadCount = quads?.Count ?? 0;
            int quadStart = batch.QuadCount;
            using (PmSoACopy.Auto())
                for (int q = 0; q < quadCount; q++) batch.AddQuad(quads[q]);

            // Hash: the movable-to-build-time conversion (sRGB→linear color + fade-id string hash).
            float4 color;
            long   fadeId;
            using (PmSoAHash.Auto())
            {
                color  = LabelPlacementSystem.LinearColor(label);
                // I6: icon FadeId identity now rides label.IconImage (null for text, so a text label's FadeId
                // is unchanged — PointFadeId's guard-skip fold).
                fadeId = LabelPlacementSystem.PointFadeId(label.AnchorRender, label.MaterialIndex, label.Text, label.IconImage);
            }
            // Epic A / A1 D2: the world-anchored Level-1 RTC bake — AnchorLocal = anchorRender − tileOriginRender,
            // the SAME double3 origin ResolveTileOrigin resolves (null-safe, decoupled from the coverage -1
            // degenerate above) — so the presenter placement and this bake cancel exactly (§3.4).
            double3 tileOriginRender = ResolveTileOrigin(label.TileKey, projection, worldOriginByKey);
            // Manual per-component narrow (convention — no assumed double3→float3 cast operator; mirrors
            // FloatingOrigin.TileToSceneRebased's identical narrowing).
            float3 anchorLocal = new float3(
                (float)(label.AnchorRender.x - tileOriginRender.x),
                (float)(label.AnchorRender.y - tileOriginRender.y),
                (float)(label.AnchorRender.z - tileOriginRender.z));

            var input = new PointStageInput
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
                FadeId = fadeId,
                // I5a: thread the icon/text discriminator through — NOT yet consumed by the draw side (I5b).
                AtlasKind = label.Kind == LabelKind.Icon ? LabelKind.Icon : LabelKind.Text,
                AnchorLocal = anchorLocal, TileOriginRender = tileOriginRender,
            };
            int detail = batch.AddPoint(input, quadStart, quadCount);

            int worldStart = batch.AddWorldPoint(label.AnchorRender);      // point anchor → 1 world point
            batch.AddRecord(SymbolLabelBatch.Kind.Point, detail, worldStart, 1, label.AnchorRender, tileIndex, departing);
        }

        private static void AddCurved(SymbolLabelBatch batch, LabelInstance label, int slotCount, int tileIndex,
            bool departing, IProjection projection, Dictionary<long, double3> worldOriginByKey)
        {
            // Copy glyphs.
            IReadOnlyList<CurvedGlyph> glyphs = label.CurvedGlyphs;
            int glyphCount = glyphs?.Count ?? 0;
            int glyphStart = batch.GlyphCount;
            using (PmSoACopy.Auto())
                for (int g = 0; g < glyphCount; g++) batch.AddGlyph(glyphs[g]);

            // Copy anchors (capped at the staging cap, which StageCurved re-applies) + pre-resolve their fade ids
            // (one per anchor + a trailing fallback for the centred label). Incumbency stays per-frame (not stored).
            LineAnchor[] anchors = label.LineAnchors;
            int anchorLen = anchors?.Length ?? 0;
            int anchorCount = math.min(anchorLen, LabelStagingMath.MaxAnchorsPerLine);
            int anchorStart = batch.AnchorCount;
            using (PmSoACopy.Auto())
                for (int a = 0; a < anchorCount; a++) batch.AddAnchor(anchors[a]);
            int anchorFadeStart = batch.AnchorFadeCount;
            // label.MaterialIndex = the symbol layer's slot — the SAME per-layer id PointFadeId folds in. Load-bearing:
            // FeatureIndex restarts per layer, so without it two roads in different layers of one tile collide (the
            // stuck-at-partial-opacity fade fight). See LineFadeId's doc.
            float4 curvedColor;
            using (PmSoAHash.Auto())
            {
                for (int a = 0; a < anchorCount; a++)
                    batch.AddAnchorFadeId(LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, a));
                batch.AddAnchorFadeId(LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, -1)); // fallback
                curvedColor = LabelPlacementSystem.LinearColor(label);
            }

            // Stage AC (curved-world): the SAME null-safe origin AddPoint's D2 bake uses (ResolveTileOrigin —
            // TileRenderOrigin.Project, never the coverage tileIndex's -1 degenerate), so the per-glyph
            // AnchorLocal bake StageCurvedAnchor computes and this tile's render-space origin cancel exactly.
            double3 tileOriginRender = ResolveTileOrigin(label.TileKey, projection, worldOriginByKey);

            var input = new CurvedStageInput
            {
                TextSizePx = label.TextSizePx, PaddingPx = label.PaddingPx, SortKey = label.SortKey,
                FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                Slot = LabelPlacementSystem.ClampSlot(label.MaterialIndex, slotCount),
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement,
                TranslatePx = label.TranslatePx, TranslateAnchor = label.TranslateAnchor,
                MaxAngleDeg = label.MaxAngleDeg, KeepUpright = label.KeepUpright,
                Color = curvedColor, TileOriginRender = tileOriginRender,
            };
            int detail = batch.AddCurved(input, glyphStart, glyphCount, anchorStart, anchorCount, anchorFadeStart);

            // Path world points, in order (projected + walked per frame). Cull rep = path midpoint, else the anchor
            // (matches LabelPlacementSystem.RepresentativeAnchor for a line with/without a path).
            double3[] path = label.PathRender;
            int pathLen = path?.Length ?? 0;
            int worldStart = batch.WorldPointCount;
            using (PmSoACopy.Auto())
                for (int v = 0; v < pathLen; v++) batch.AddWorldPoint(path[v]);
            double3 rep = pathLen > 0 ? path[pathLen / 2] : label.AnchorRender;
            batch.AddRecord(SymbolLabelBatch.Kind.Curved, detail, worldStart, pathLen, rep, tileIndex, departing);
        }
    }
}
