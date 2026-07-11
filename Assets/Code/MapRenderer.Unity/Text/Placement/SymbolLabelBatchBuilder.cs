// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, never an inline
// `Unity.Mathematics.X`. Unity-side (not Core) because the byte-parity colour conversion needs UnityEngine.Color.

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Converts the managed <see cref="LabelInstance"/> carriers of one collected label set into the blittable
    /// <see cref="SymbolLabelBatch"/> SoA (Lever C step 2). Run ONCE per collected-set change (keyed on
    /// <c>SymbolTileLabelStore.Version</c>) at the aggregation seam — off the per-frame path — so every frame's
    /// gather/project/stage reads the SoA with no managed iteration and no per-frame conversion.
    ///
    /// <para>Resolves here exactly the managed bits the staging math can't: the glyph/quad copies out of the
    /// managed lists, the sRGB→linear vertex colour (<see cref="LabelPlacementSystem.LinearColor"/>, matched to
    /// the old path for byte-parity), the string-hashed point fade-id and the per-anchor line fade-ids. The
    /// per-frame dynamic values (projected screen/depth/valid, last-frame incumbency) are NOT stored — the
    /// consumer patches them each frame.</para>
    /// </summary>
    internal static class SymbolLabelBatchBuilder
    {
        /// <summary>Rebuild <paramref name="batch"/> in place from <paramref name="labels"/> (collected order
        /// preserved so the collision ordinal — and thus the mesh — stays byte-identical). <paramref name="slotCount"/>
        /// clamps each label's material slot (mirrors the old per-frame ClampSlot).</summary>
        public static void Build(SymbolLabelBatch batch, IReadOnlyList<LabelInstance> labels, int slotCount)
        {
            batch.Reset();
            if (labels == null) return;

            for (int i = 0; i < labels.Count; i++)
            {
                LabelInstance label = labels[i];
                if (label == null) continue; // nulls contributed no candidate/world-point in the old loop → omit

                if (label.Placement == SymbolPlacement.Point) AddPoint(batch, label, slotCount);
                else                                          AddCurved(batch, label, slotCount);
            }
        }

        private static void AddPoint(SymbolLabelBatch batch, LabelInstance label, int slotCount)
        {
            // Copy this label's glyph quads into the flat pool (empty/absent layout → 0 quads, stages nothing later).
            IReadOnlyList<SymbolQuad> quads = label.Layout?.Quads;
            int quadCount = quads?.Count ?? 0;
            int quadStart = batch.QuadCount;
            for (int q = 0; q < quadCount; q++) batch.AddQuad(quads[q]);

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
                RotationAlignment = label.RotationAlignment, Color = LabelPlacementSystem.LinearColor(label),
                FadeId = LabelPlacementSystem.PointFadeId(label.AnchorRender, label.MaterialIndex, label.Text),
            };
            int detail = batch.AddPoint(input, quadStart, quadCount);

            int worldStart = batch.AddWorldPoint(label.AnchorRender);      // point anchor → 1 world point
            batch.AddRecord(SymbolLabelBatch.Kind.Point, detail, worldStart, 1, label.AnchorRender);
        }

        private static void AddCurved(SymbolLabelBatch batch, LabelInstance label, int slotCount)
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
            for (int a = 0; a < anchorCount; a++)
                batch.AddAnchorFadeId(LabelStagingMath.LineFadeId(label.TileKey, label.FeatureIndex, a));
            batch.AddAnchorFadeId(LabelStagingMath.LineFadeId(label.TileKey, label.FeatureIndex, -1)); // fallback

            var input = new CurvedStageInput
            {
                TextSizePx = label.TextSizePx, PaddingPx = label.PaddingPx, SortKey = label.SortKey,
                FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                Slot = LabelPlacementSystem.ClampSlot(label.MaterialIndex, slotCount),
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement,
                TranslatePx = label.TranslatePx, TranslateAnchor = label.TranslateAnchor,
                MaxAngleDeg = label.MaxAngleDeg, KeepUpright = label.KeepUpright,
                Color = LabelPlacementSystem.LinearColor(label),
            };
            int detail = batch.AddCurved(input, glyphStart, glyphCount, anchorStart, anchorCount, anchorFadeStart);

            // Path world points, in order (projected + walked per frame). Cull rep = path midpoint, else the anchor
            // (matches LabelPlacementSystem.RepresentativeAnchor for a line with/without a path).
            double3[] path = label.PathRender;
            int pathLen = path?.Length ?? 0;
            int worldStart = batch.WorldPointCount;
            for (int v = 0; v < pathLen; v++) batch.AddWorldPoint(path[v]);
            double3 rep = pathLen > 0 ? path[pathLen / 2] : label.AnchorRender;
            batch.AddRecord(SymbolLabelBatch.Kind.Curved, detail, worldStart, pathLen, rep);
        }
    }
}
