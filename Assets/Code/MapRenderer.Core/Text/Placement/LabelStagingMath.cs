// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2/4 — this
// file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to a
// (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See PolylineArcWalker.

using System;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The per-frame label STAGING geometry — projecting/placing one label's collision boxes + drawn quads —
    /// factored out of <c>LabelPlacementSystem</c> as pure, engine-free static functions over BLITTABLE inputs
    /// (no managed <see cref="LabelInstance"/>). This is Lever C step 1: it decouples the transcendental-heavy
    /// staging math from the managed carrier so the identical code can later run in a Burst job over stored SoA
    /// (the byte-parity gate is that <c>LabelPlacementSystem</c> calls THESE — one implementation, no divergence).
    ///
    /// <para>Inputs are the per-label values a producer can compute once and store blittable
    /// (<see cref="PointStageInput"/>/<see cref="CurvedStageInput"/>) plus this frame's projected screen geometry;
    /// the string-derived point fade-id, the <see cref="LinearColor"/> conversion, and the last-frame incumbency
    /// lookup are the caller's job (they need managed/main-thread state) and arrive pre-resolved as plain values.</para>
    ///
    /// <para>Outputs are appended to caller-owned growable pools (<c>ref T[]</c> + a <c>ref int</c> cursor,
    /// geometric growth, never shrinks — no per-frame GC once warm). Lever C step 3 swaps these for
    /// <c>NativeList</c> Adds at the job boundary; the math body is unchanged.</para>
    /// </summary>
    public static class LabelStagingMath
    {
        /// <summary>Defence-in-depth cap on a line's repeat anchors — the staging loop and the batch builder's
        /// per-anchor fade-id pre-resolve MUST use the same value (see SymbolLabelBatchBuilder).</summary>
        public const int MaxAnchorsPerLine = 256;
        private const float MaxProjectedPx  = 1e5f;    // a near-plane blow-up past this skips the label.

        /// <summary>
        /// Stages one POINT label: its whole-label AABB collision box + glyph quads at the projected anchor
        /// (rotated by the #4 bearing under <c>text-rotation-alignment:map</c>). Writes
        /// <c>candidates[ordinal]</c>/<c>emit[ordinal]</c>. Returns 1 if staged, or 0 (appending nothing) when it
        /// has no quads, projected behind the camera, or culls outside the viewport margin.
        /// </summary>
        public static int StagePoint(in PointStageInput s, ReadOnlySpan<SymbolQuad> quads,
            float bearingRadians, double2 viewportLogicalPx, int ordinal,
            Span<LabelBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<LabelCandidate> candidates, Span<CandidateEmit> emit)
        {
            if (quads.Length == 0) return 0;
            if (!s.Projected || !LabelScreenProjection.IsWithinViewportMargin(s.ScreenPx, viewportLogicalPx))
                return 0;

            float2 screenPx = LabelTranslate.ApplyTranslate(s.ScreenPx, s.TranslatePx, s.TranslateAnchor, bearingRadians);
            float rotationRadians = LabelBearing.BillboardRotationRadians(s.RotationAlignment, bearingRadians);

            int boxStart = boxCount;
            boxes[boxCount++] = LabelBox.Build(
                screenPx, s.BoundsMin, s.BoundsMax, s.TextSizePx, s.PaddingPx,
                s.SortKey, s.FeatureIndex, s.TileKey, ordinal, s.AllowOverlap, s.IgnorePlacement);

            int quadStart = quadCount;
            for (int q = 0; q < quads.Length; q++)
                quadsOut[quadCount++] = new PlacedQuad
                {
                    Quad = quads[q], AnchorScreenPx = screenPx, TextSizePx = s.TextSizePx,
                    Depth = s.Depth, Color = s.Color, RotationRadians = rotationRadians,
                };

            candidates[ordinal] = new LabelCandidate
            {
                BoxStart = boxStart, BoxCount = 1,
                SortKey = s.SortKey, FeatureIndex = s.FeatureIndex, TileKey = s.TileKey,
                AllowOverlap = s.AllowOverlap, IgnorePlacement = s.IgnorePlacement, LabelIndex = ordinal,
                FadeId = s.FadeId, WasPlacedLastFrame = s.WasPlacedLastFrame,
            };
            emit[ordinal] = new CandidateEmit { QuadStart = quadStart, QuadCount = quads.Length, Slot = s.Slot, AtlasKind = s.AtlasKind };
            return 1;
        }

        /// <summary>
        /// Stages one CURVED along-line label (#5): validates + walks this frame's projected path, then stages one
        /// all-or-nothing candidate per stable build-time anchor (A-2), each N per-glyph rotated boxes/quads placed
        /// along the arc. Returns the number of anchors staged (0 if the path culls, has zero projected length, or
        /// the label is longer than the whole line). <paramref name="pathScratch"/> and
        /// <paramref name="cumulativeScratch"/> are caller-owned reused buffers sized to at least the path length.
        ///
        /// <para><paramref name="anchorFadeIds"/>/<paramref name="anchorWasPlaced"/> carry the pre-resolved A-4
        /// fade id + A-5 incumbency per anchor — indices <c>[0, anchorCount)</c> for the build-time anchors and the
        /// LAST slot (<c>[anchorCount]</c>) for the centred fallback (anchor index -1). Compute them with
        /// <see cref="LineFadeId"/> against the caller's placed-last-frame set.</para>
        /// </summary>
        public static int StageCurved(in CurvedStageInput s,
            ReadOnlySpan<float2> screenPath, ReadOnlySpan<float> depthPath, ReadOnlySpan<byte> validPath,
            ReadOnlySpan<CurvedGlyph> glyphs, ReadOnlySpan<LineAnchor> anchors,
            ReadOnlySpan<long> anchorFadeIds, ReadOnlySpan<byte> anchorWasPlaced,
            Span<float2> pathScratch, Span<float> cumulativeScratch,
            float bearingRadians, int ordinal,
            Span<LabelBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<LabelCandidate> candidates, Span<CandidateEmit> emit)
        {
            int pathLen = screenPath.Length;
            if (pathLen < 2 || glyphs.Length == 0 || anchors.Length == 0) return 0;

            // Validate + copy this frame's projected path: any vertex behind the camera, or a near-plane blow-up
            // that would explode the arc length, skips the whole label. Representative depth from the mid vertex.
            float pathDepth = 0f;
            for (int v = 0; v < pathLen; v++)
            {
                if (validPath[v] == 0) return 0;
                float2 sp = screenPath[v];
                if (!(math.abs(sp.x) < MaxProjectedPx && math.abs(sp.y) < MaxProjectedPx)) return 0;
                pathScratch[v] = sp;
                if (v == pathLen / 2) pathDepth = depthPath[v];
            }

            float total = PolylineArcMath.BuildCumulative(pathScratch, pathLen, cumulativeScratch);
            if (!(total > 0f)) return 0;

            float scale            = s.TextSizePx / TextQuadLayout.OneEm;
            float labelCenterBaked = (glyphs[0].ArcCenter + glyphs[glyphs.Length - 1].ArcCenter) * 0.5f;
            float labelSpanPx      = (glyphs[glyphs.Length - 1].ArcCenter - glyphs[0].ArcCenter) * scale;
            float halfSpan         = labelSpanPx * 0.5f;
            if (labelSpanPx > total) return 0;

            int cursor = 0; // one resumable arc cursor for the whole label (Lever A), threaded across anchors.
            int staged = 0;
            int anchorCount = math.min(anchors.Length, MaxAnchorsPerLine);
            for (int a = 0; a < anchorCount; a++)
            {
                float centerArc = PolylineArcMath.ArcDistanceAt(cumulativeScratch, pathLen, anchors[a].Segment, anchors[a].T);
                if (centerArc - halfSpan < 0f || centerArc + halfSpan > total) continue; // label spills the ends
                if (StageCurvedAnchor(in s, pathScratch, cumulativeScratch, pathLen, total, glyphs, ref cursor,
                        ordinal + staged, anchorFadeIds[a], anchorWasPlaced[a] != 0,
                        centerArc, labelCenterBaked, scale, pathDepth, bearingRadians,
                        boxes, ref boxCount, quadsOut, ref quadCount, candidates, emit))
                    staged++;
            }

            // No build-time anchor's projected position fit this frame → try one centred label at the arc midpoint.
            if (staged == 0 &&
                StageCurvedAnchor(in s, pathScratch, cumulativeScratch, pathLen, total, glyphs, ref cursor,
                    ordinal, anchorFadeIds[anchorCount], anchorWasPlaced[anchorCount] != 0,
                    total * 0.5f, labelCenterBaked, scale, pathDepth, bearingRadians,
                    boxes, ref boxCount, quadsOut, ref quadCount, candidates, emit))
                staged = 1;

            return staged;
        }

        // Stages ONE curved-label instance centred at `centerArc`. Rolls the box/quad pools back and returns false
        // if any adjacent-glyph line curvature exceeds text-max-angle (the label is dropped at this anchor, #6).
        private static bool StageCurvedAnchor(in CurvedStageInput s,
            ReadOnlySpan<float2> path, ReadOnlySpan<float> cumulative, int pathLen, float total,
            ReadOnlySpan<CurvedGlyph> glyphs, ref int cursor,
            int ordinal, long fadeId, bool wasPlaced,
            float centerArc, float labelCenterBaked, float scale, float pathDepth, float bearingRadians,
            Span<LabelBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<LabelCandidate> candidates, Span<CandidateEmit> emit)
        {
            // keep-upright: a centre tangent pointing leftward reads right-to-left; walk the arc reversed and flip
            // each glyph +pi so it still reads left-to-right (each anchor decides its own flip — a line can bend back).
            PolylineArcMath.At(path, cumulative, pathLen, total, centerArc, ref cursor, out _, out float centerTangent);
            bool  reversed = s.KeepUpright && math.cos(centerTangent) < 0f;
            float dir      = reversed ? -1f : 1f;
            float flip     = reversed ? math.PI : 0f;
            float maxAngleRad = math.radians(s.MaxAngleDeg);

            int boxStart  = boxCount;
            int quadStart = quadCount;
            float prevCenterTangent = 0f;
            for (int g = 0; g < glyphs.Length; g++)
            {
                CurvedGlyph cg = glyphs[g];
                float arc = centerArc + dir * (cg.ArcCenter - labelCenterBaked) * scale;
                PolylineArcMath.At(path, cumulative, pathLen, total, arc, ref cursor, out float2 pt, out float centerTangentAtGlyph);

                // The max-angle gate answers "is the PATH too kinky to place a label here" — a path-curvature
                // property, so it stays on the raw per-glyph SEGMENT tangent (byte-identical to pre-fix
                // behaviour; a Burst-vs-managed atan2 ULP mismatch on the blended angle below would otherwise
                // flip cull decisions right at the threshold, see LabelStageJobTests.BurstStage_MatchesManaged).
                if (g > 0 && math.abs(AngleDelta(centerTangentAtGlyph, prevCenterTangent)) > maxAngleRad)
                {
                    boxCount = boxStart; quadCount = quadStart; // roll back this anchor's partial appends
                    return false;                                // too sharp a bend → drop the label here (#6)
                }
                prevCenterTangent = centerTangentAtGlyph;

                // Orient the rigid glyph quad by the CHORD across its OWN footprint, not the single-point
                // segment tangent: a glyph straddling a polyline VERTEX would otherwise rotate to one
                // segment's raw angle while its neighbour (advance-spaced, not vertex-spaced) rotates to
                // the other, so their inner corners collide on the concave side of the bend. The chord
                // blends the two segment angles in proportion to how much of the footprint sits on each
                // side of the vertex, so consecutive glyphs tile edge-to-edge (MapLibre's fix). This is
                // purely a RENDER-orientation choice — it does not feed the cull gate above.
                float halfWidthPx = (cg.Cell.BottomRight.x - cg.Cell.TopLeft.x) * scale * 0.5f;
                float tangent = centerTangentAtGlyph;
                if (halfWidthPx > 1e-4f)
                {
                    float arcLeft  = math.max(0f, math.min(total, arc - halfWidthPx));
                    float arcRight = math.max(0f, math.min(total, arc + halfWidthPx));
                    PolylineArcMath.At(path, cumulative, pathLen, total, arcLeft,  ref cursor, out float2 pLeft,  out _);
                    PolylineArcMath.At(path, cumulative, pathLen, total, arcRight, ref cursor, out float2 pRight, out _);
                    float2 chord = pRight - pLeft;
                    if (math.lengthsq(chord) > 1e-12f) tangent = (float)math.atan2(chord.y, chord.x);
                }

                pt = LabelTranslate.ApplyTranslate(pt, s.TranslatePx, s.TranslateAnchor, bearingRadians);
                float rotation = tangent + flip; // tangent ONLY — not the label bearing (would double-rotate)

                boxes[boxCount++] = LabelBox.BuildRotatedGlyph(pt, cg.Cell, s.TextSizePx, rotation, s.PaddingPx);
                quadsOut[quadCount++] = new PlacedQuad
                {
                    Quad = cg.Cell, AnchorScreenPx = pt, TextSizePx = s.TextSizePx,
                    Depth = pathDepth, Color = s.Color, RotationRadians = rotation,
                };
            }

            candidates[ordinal] = new LabelCandidate
            {
                BoxStart = boxStart, BoxCount = glyphs.Length,
                SortKey = s.SortKey, FeatureIndex = s.FeatureIndex, TileKey = s.TileKey,
                AllowOverlap = s.AllowOverlap, IgnorePlacement = s.IgnorePlacement, LabelIndex = ordinal,
                FadeId = fadeId, WasPlacedLastFrame = wasPlaced,
            };
            emit[ordinal] = new CandidateEmit { QuadStart = quadStart, QuadCount = glyphs.Length, Slot = s.Slot };
            return true;
        }

        /// <summary>A-4 LINE fade identity: within-tile (tile, feature, anchor-index) FNV-1a-64. Anchor index -1 is
        /// the centred fallback. Pure arithmetic — the caller precomputes these per anchor so the staging math needs
        /// no string or set state.</summary>
        public static long LineFadeId(long tileKey, int featureIndex, int anchorIndex)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV-1a 64
                h = (h ^ (ulong)tileKey) * 1099511628211UL;
                h = (h ^ (ulong)(uint)featureIndex) * 1099511628211UL;
                h = (h ^ (ulong)(uint)anchorIndex) * 1099511628211UL;
                return (long)h;
            }
        }

        // Smallest signed angle a - b, wrapped to (-pi, pi] via atan2 (bounded, no while-loop drift).
        private static float AngleDelta(float a, float b)
        {
            float d = a - b;
            return (float)math.atan2(math.sin(d), math.cos(d));
        }
    }
}
