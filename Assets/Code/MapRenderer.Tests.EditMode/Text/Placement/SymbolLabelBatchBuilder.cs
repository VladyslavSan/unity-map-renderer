// Unity EditMode only — needs UnityEngine.Color for the byte-parity sRGB->linear conversion, and the internal
// production seams it calls (SymbolTileLabelBlockBaker.BuildPointInput/BuildCurvedInput,
// LabelPlacementSystem.LinearColor/RepresentativeAnchor) via InternalsVisibleTo. NOT in core-tests.csproj.
//
// THE PARITY ORACLE. This used to live in MapRenderer.Unity as production code, which it never was: nothing
// in Core/Unity/Jobs called Build. It is the independent second implementation the production path is checked
// against — SymbolGatherParityTests pins the subsystem's plan against it, SymbolPlanMirrorParityTests pins the
// test helper's. Keeping it in the shipped assembly meant every consumer of the renderer carried the checker
// for the renderer.
//
// It is NOT redundant with SymbolTileLabelBlockBakerTests, which is what the proposal's "or delete" clause
// hoped: the baker's own teeth cover ONE tile's bake, while this oracle is walked over a whole collected set,
// so it is what pins cross-tile assembly and ORDERING (a reordered collect, a dedup that ate a label). The
// per-label field math is shared, not duplicated -- both sides call the baker's BuildPointInput/
// BuildCurvedInput -- so this checks the assembly around that math, not the math itself.
//
// Namespace-collision guard (see GlyphAtlasTexture.cs's header): uses Unity.Mathematics types via a TOP-LEVEL
// `using Unity.Mathematics;` + unqualified types, never an inline `Unity.Mathematics.X`.

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests
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
        // Epic A / A1 (design §11 A1 D2 — the BLOCKER fix): a per-Build cache, TileKey → the world-anchored
        // render-space tile origin (TileRenderOrigin.Project). Reused + cleared each Build so the demo hot
        // path (which rebuilds every Tick) allocates nothing in steady state (T4).
        [System.ThreadStatic] private static Dictionary<long, double3> _worldOriginCache;

        // SoA-build breakdown markers (nest under MapRenderer.Symbol.BatchBuild.SoA), splitting the per-frame
        // LabelInstance→SoA conversion into two natures so the profiler shows what actually dominates:
        //   Hash    — fade-id string hash + sRGB→linear colour, the movable-to-build-time conversion (~22%).
        //   Copy    — bulk glyph/quad/anchor appends into the ONE contiguous pool the Burst jobs read; INTRINSIC
        //             per frame (a stale/off-main decision still gathers survivors into one buffer) — measured
        //             the dominant ~70%, which is why the answer is off-main/GPU-projected rendering, not a
        //             build-time precompute. Marker Begin/End is a near-no-op when the profiler is not
        //             recording, so the per-label cost in production is negligible.
        // (the per-unique-tile 4-corner coverage-cull projection this Project marker used to bracket moved to
        // the pre-build Core LabelTileCoverageFilter — see SymbolLabelSubsystem.CurrentBatch.)
        // The two SoA.Hash / SoA.Copy profiler markers that used to bracket the loops below are GONE with the
        // move. A Profiler marker exists to attribute cost in a running map; this code only ever runs inside a
        // test, so the markers named a cost no profile would meet. ProfilerMarkerTests lost their two entries
        // with them.

        /// <summary>Rebuild <paramref name="batch"/> in place from <paramref name="labels"/> (collected order
        /// preserved so the collision ordinal — and thus the mesh — stays byte-identical). <paramref name="slotCount"/>
        /// clamps each label's material slot (mirrors the old per-frame ClampSlot).</summary>
        /// <param name="activeCount">Labels at index &lt; this are ACTIVE; index ≥ this are DEPARTING (a tile leaving
        /// cover, retained for a fade-out — <see cref="Text.SymbolTileLabelStore.CollectInto(System.Collections.Generic.List{LabelInstance}, double, out int)"/>
        /// puts them last). Each such record is flagged so the placement gather fades it out instead of popping.
        /// Default <see cref="int.MaxValue"/> ⇒ every label is active (the demo / test seam, which has no store).</param>
        /// <param name="coverageFadingTiles">Tile keys <see cref="Core.Text.Placement.LabelTileCoverageFilter.FilterActive"/>
        /// classified Fade this call (its coverage crossed below threshold but was on screen) — each such record is
        /// flagged <see cref="SymbolLabelBatch.RecordCoverageFading"/> so the placement gather fades it out instead
        /// of popping (a SEPARATE flag from <paramref name="activeCount"/>'s departing split — see the batch's doc).
        /// Null ⇒ no tile is coverage-fading (the demo / test seam, which never runs the filter).</param>
        public static void Build(SymbolLabelBatch batch, IReadOnlyList<LabelInstance> labels, int slotCount,
            IProjection projection = null, int activeCount = int.MaxValue, HashSet<long> coverageFadingTiles = null)
        {
            batch.Reset();
            if (labels == null) return;

            // Epic A / A1 D2: cache for the world-anchored bake's tile origin. Reused + cleared each Build.
            Dictionary<long, double3> worldOriginByKey = _worldOriginCache ??= new Dictionary<long, double3>();
            worldOriginByKey.Clear();

            for (int i = 0; i < labels.Count; i++)
            {
                LabelInstance label = labels[i];
                if (label == null) continue; // nulls contributed no candidate/world-point in the old loop → omit

                bool departing = i >= activeCount; // collected-order split: departing labels come last (see CollectInto)
                bool coverageFading = coverageFadingTiles?.Contains(label.TileKey) ?? false;
                if (label.Placement == SymbolPlacement.Point)
                {
                    // §10 D10: resolved over the WINNER list (this `labels` — the reconciler keeps owner→rider
                    // adjacent here), matching what the production baker resolves over the TILE list — both
                    // intact, so they agree.
                    LabelPairRole pairRole = LabelPairRole.None;
                    if (LabelPairing.TryGetRider(labels, i, out _)) pairRole = LabelPairRole.Owner;
                    else if (LabelPairing.IsRider(labels, i)) pairRole = LabelPairRole.Rider;
                    AddPoint(batch, label, slotCount, departing, coverageFading, projection, worldOriginByKey, pairRole);
                }
                else
                    AddCurved(batch, label, slotCount, departing, coverageFading, projection, worldOriginByKey);
            }
        }

        /// <summary>
        /// Epic A / A1 (design §11 A1 D2 — the BLOCKER fix): resolves <paramref name="tileKey"/>'s
        /// world-anchored render-space tile origin, ALWAYS valid — <see cref="TileRenderOrigin.Project"/> is
        /// null-safe (<paramref name="projection"/> null ⇒ planar Mercator fallback). Cached per unique tile
        /// key within one <see cref="Build"/>.
        /// </summary>
        private static double3 ResolveTileOrigin(long tileKey, IProjection projection, Dictionary<long, double3> cache)
        {
            if (cache.TryGetValue(tileKey, out double3 origin)) return origin;
            origin = TileRenderOrigin.Project(SymbolFeatureExtractor.UnpackTileKey(tileKey), projection);
            cache[tileKey] = origin;
            return origin;
        }

        private static void AddPoint(SymbolLabelBatch batch, LabelInstance label, int slotCount,
            bool departing, bool coverageFading, IProjection projection, Dictionary<long, double3> worldOriginByKey,
            LabelPairRole pairRole)
        {
            // Copy this label's glyph quads into the flat pool (empty/absent layout → 0 quads, stages nothing later).
            IReadOnlyList<SymbolQuad> quads = label.Layout?.Quads;
            int quadCount = quads?.Count ?? 0;
            int quadStart = batch.QuadCount;
            for (int q = 0; q < quadCount; q++) batch.AddQuad(quads[q]);

            // Epic A / A1 D2: the world-anchored Level-1 RTC bake — AnchorLocal = anchorRender − tileOriginRender,
            // the SAME double3 origin ResolveTileOrigin resolves (null-safe) — so the presenter placement and
            // this bake cancel exactly (§3.4).
            double3 tileOriginRender = ResolveTileOrigin(label.TileKey, projection, worldOriginByKey);
            PointStageInput input = SymbolTileLabelBlockBaker.BuildPointInput(label, slotCount, tileOriginRender, pairRole);
            int detail = batch.AddPoint(input, quadStart, quadCount);

            int worldStart = batch.AddWorldPoint(label.AnchorRender);      // point anchor → 1 world point
            batch.AddWorldUp(SymbolTileLabelBlockBaker.NarrowUp(label.UpRender));
            batch.AddRecord(LabelRecordKind.Point, detail, worldStart, 1, label.AnchorRender, departing, coverageFading);
        }

        private static void AddCurved(SymbolLabelBatch batch, LabelInstance label, int slotCount,
            bool departing, bool coverageFading, IProjection projection, Dictionary<long, double3> worldOriginByKey)
        {
            // Copy glyphs.
            IReadOnlyList<CurvedGlyph> glyphs = label.CurvedGlyphs;
            int glyphCount = glyphs?.Count ?? 0;
            int glyphStart = batch.GlyphCount;
            for (int g = 0; g < glyphCount; g++) batch.AddGlyph(glyphs[g]);

            // Copy anchors (capped at the staging cap, which StageCurved re-applies) + pre-resolve their fade ids
            // (one per anchor + a trailing fallback for the centred label). Incumbency stays per-frame (not stored).
            LineAnchor[] anchors = label.LineAnchors;
            int anchorLen = anchors?.Length ?? 0;
            int anchorCount = math.min(anchorLen, LabelStagingMath.MaxAnchorsPerLine);
            int anchorStart = batch.AnchorCount;
            for (int a = 0; a < anchorCount; a++) batch.AddAnchor(anchors[a]);
            int anchorFadeStart = batch.AnchorFadeCount;
            // label.MaterialIndex = the symbol layer's slot — the SAME per-layer id PointFadeId folds in. Load-bearing:
            // FeatureIndex restarts per layer, so without it two roads in different layers of one tile collide (the
            // stuck-at-partial-opacity fade fight). See LineFadeId's doc.
            // Stage AC (curved-world): the SAME null-safe origin AddPoint's D2 bake uses (ResolveTileOrigin —
            // TileRenderOrigin.Project), so the per-glyph AnchorLocal bake StageCurvedAnchor computes and this
            // tile's render-space origin cancel exactly.
            double3 tileOriginRender = ResolveTileOrigin(label.TileKey, projection, worldOriginByKey);
            for (int a = 0; a < anchorCount; a++)
                batch.AddAnchorFadeId(LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, a));
            batch.AddAnchorFadeId(LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, -1)); // fallback
            CurvedStageInput input = SymbolTileLabelBlockBaker.BuildCurvedInput(label, slotCount, tileOriginRender);
            int detail = batch.AddCurved(input, glyphStart, glyphCount, anchorStart, anchorCount, anchorFadeStart);

            // Path world points, in order (projected + walked per frame). Cull rep = path midpoint, else the anchor
            // (matches LabelPlacementSystem.RepresentativeAnchor for a line with/without a path).
            double3[] path = label.PathRender;
            double3[] pathUps = label.PathUpRender; // may be null on a legacy/hand-built label — guarded below
            int pathLen = path?.Length ?? 0;
            int worldStart = batch.WorldPointCount;
            for (int v = 0; v < pathLen; v++)
            {
                batch.AddWorldPoint(path[v]);
                batch.AddWorldUp(pathUps != null && v < pathUps.Length
                    ? SymbolTileLabelBlockBaker.NarrowUp(pathUps[v])
                    : float3.zero); // bug signal, not a supported state — see T-3
            }
            double3 rep = pathLen > 0 ? path[pathLen / 2] : label.AnchorRender;
            batch.AddRecord(LabelRecordKind.Curved, detail, worldStart, pathLen, rep, departing, coverageFading);
        }
    }
}
