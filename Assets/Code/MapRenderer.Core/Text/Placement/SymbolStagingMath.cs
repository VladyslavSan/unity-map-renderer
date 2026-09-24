// TOP-LEVEL `using Unity.Mathematics;`: inside this namespace an inline `Unity.Mathematics.float2` binds
// to a nonexistent nested namespace (CS0234; see SymbolScreenProjection).

using System;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The per-frame symbol STAGING geometry — projecting/placing one symbol's collision boxes + drawn quads —
    /// as pure, engine-free static functions over BLITTABLE inputs (<see cref="PointStageInput"/>/
    /// <see cref="CurvedStageInput"/>) plus this frame's projected geometry. Values that need managed state
    /// (point fade id, <see cref="LinearColor"/>, last-frame incumbency) arrive pre-resolved. Outputs append to
    /// caller-owned pools that grow and never shrink, so a warm frame allocates nothing.
    /// </summary>
    public static class SymbolStagingMath
    {
        /// <summary>Defence-in-depth cap on a line's repeat anchors — the build-time bake and the per-frame
        /// staging loop both clamp to this same value, so their anchor counts agree.</summary>
        public const int MaxAnchorsPerLine = 256;

        // Non-obvious why: a NaN or ±Inf sort key (e.g. ["/", 0, 0]) makes ComparePlacementOrder intransitive,
        // so it becomes float.MaxValue (lowest priority) here, where the baked value becomes a sort key.
        // math.abs, not math.isfinite, because the Tools/core-tests shim has only math.abs.
        internal static float SanitizeSortKey(float k) => math.abs(k) <= float.MaxValue ? k : float.MaxValue;

        /// <summary>
        /// Stages one POINT symbol: its whole-symbol AABB collision box + glyph quads at the projected anchor
        /// (rotated by the map bearing under <c>text-rotation-alignment:map</c>). Writes
        /// <c>candidates[ordinal]</c> and ONE <c>CandidateEmit</c> at <c>emitCount</c>. Returns 1 if staged, or 0
        /// (appending nothing) when it has no quads, projected behind the camera, or culls outside the viewport
        /// margin.
        /// </summary>
        public static int StagePoint(in PointStageInput s, ReadOnlySpan<SymbolQuad> quads,
            float bearingRadians, double2 viewportLogicalPx, int ordinal,
            Span<SymbolBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<SymbolCandidate> candidates, Span<CandidateEmit> emit, ref int emitCount)
        {
            if (quads.Length == 0) return 0;
            if (!s.Projected || !SymbolScreenProjection.IsWithinViewportMargin(s.ScreenPx, viewportLogicalPx))
                return 0;

            int boxStart  = boxCount;
            int emitStart = emitCount;
            AppendPointHalf(in s, quads, s.ScreenPx, s.SurfaceUp, bearingRadians, ordinal,
                boxes, ref boxCount, quadsOut, ref quadCount, emit, ref emitCount);

            candidates[ordinal] = new SymbolCandidate
            {
                BoxStart = boxStart, BoxCount = 1,
                EmitStart = emitStart, EmitCount = 1,
                SortKey = SanitizeSortKey(s.SortKey), FeatureIndex = s.FeatureIndex, TileKey = s.TileKey,
                AllowOverlap = s.AllowOverlap, IgnorePlacement = s.IgnorePlacement, SymbolIndex = ordinal,
                FadeId = s.FadeId, WasPlacedLastFrame = s.WasPlacedLastFrame,
            };
            return 1;
        }

        /// <summary>
        /// Stages a centred icon+text PAIR as ONE multi-box candidate (as curved symbols use), so the pair cannot
        /// self-block. The cull runs once, on <paramref name="owner"/>, because the halves share one anchor. The
        /// rider is appended only when <paramref name="riderQuads"/> is non-empty. Writes <c>candidates[ordinal]</c>
        /// and one or two <c>CandidateEmit</c>s; returns 1 if staged, 0 if the owner has no quads or culls.
        /// Non-local invariant: each half's <see cref="PointStageInput.PairOptional"/> sets its bit in
        /// <see cref="SymbolCandidate.OptionalBoxMask"/> (bit 0 = owner, bit 1 = rider), so collision can drop that
        /// half alone; <paramref name="droppedHalvesLastFrame"/> is the previous frame's verdict, because emit runs
        /// before collision.
        /// </summary>
        public static int StagePointPair(in PointStageInput owner, in PointStageInput rider,
            ReadOnlySpan<SymbolQuad> ownerQuads, ReadOnlySpan<SymbolQuad> riderQuads,
            float bearingRadians, double2 viewportLogicalPx, int ordinal,
            Span<SymbolBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<SymbolCandidate> candidates, Span<CandidateEmit> emit, ref int emitCount,
            byte droppedHalvesLastFrame = 0)
        {
            if (ownerQuads.Length == 0) return 0;
            if (!owner.Projected || !SymbolScreenProjection.IsWithinViewportMargin(owner.ScreenPx, viewportLogicalPx))
                return 0;

            int boxStart  = boxCount;
            int emitStart = emitCount;
            // Both halves use the OWNER's raw anchor and SurfaceUp; AppendPointHalf adds each half's own
            // translate, so a text-translate still resolves.
            AppendPointHalf(in owner, ownerQuads, owner.ScreenPx, owner.SurfaceUp, bearingRadians, ordinal,
                boxes, ref boxCount, quadsOut, ref quadCount, emit, ref emitCount);

            int boxCountForCandidate = 1;
            // Bit 0 = owner, bit 1 = rider, set only when the rider staged a box; otherwise the bit would
            // address the NEXT candidate's box.
            byte optionalMask = owner.PairOptional ? (byte)0b01 : (byte)0;
            if (riderQuads.Length > 0)
            {
                AppendPointHalf(in rider, riderQuads, owner.ScreenPx, owner.SurfaceUp, bearingRadians, ordinal,
                    boxes, ref boxCount, quadsOut, ref quadCount, emit, ref emitCount);
                boxCountForCandidate = 2;
                if (rider.PairOptional) optionalMask |= 0b10;
            }

            candidates[ordinal] = new SymbolCandidate
            {
                BoxStart = boxStart, BoxCount = boxCountForCandidate,
                OptionalBoxMask = optionalMask,
                // A carry from a frame whose optional set differed (a re-staged pair, a rider that laid out no
                // quads this time) must not gate an emit that is no longer droppable — hence the mask.
                DroppedBoxMask = (byte)(droppedHalvesLastFrame & optionalMask),
                EmitStart = emitStart, EmitCount = emitCount - emitStart,
                SortKey = SanitizeSortKey(owner.SortKey), FeatureIndex = owner.FeatureIndex, TileKey = owner.TileKey,
                // The pair ignores collision only if BOTH halves do, and blocks unless BOTH decline to.
                AllowOverlap = owner.AllowOverlap && rider.AllowOverlap,
                IgnorePlacement = owner.IgnorePlacement && rider.IgnorePlacement,
                SymbolIndex = ordinal,
                FadeId = owner.FadeId, WasPlacedLastFrame = owner.WasPlacedLastFrame,
            };
            return 1;
        }

        // Appends ONE half of a point symbol (box, quads, CandidateEmit) with this half's own translate and
        // rotation at the shared un-translated `screenPx`; lone symbols and pair halves share it, so cannot drift.
        private static void AppendPointHalf(in PointStageInput s, ReadOnlySpan<SymbolQuad> quads,
            float2 screenPx, float3 surfaceUp, float bearingRadians, int candidateOrdinal,
            Span<SymbolBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<CandidateEmit> emit, ref int emitCount)
        {
            float2 translatedScreenPx = SymbolTranslate.ApplyTranslate(screenPx, s.TranslatePx, s.TranslateAnchor, bearingRadians);
            // icon-rotate is a constant offset on top of the alignment; 2D rotations commute, so one addition
            // composes them. IconRotationRadians is the one sense negation, shared with the along-line path.
            float rotationRadians = SymbolBearing.BillboardRotationRadians(s.RotationAlignment, bearingRadians)
                                    + SymbolBearing.IconRotationRadians(s.IconRotateRadians);
            float sortKey = SanitizeSortKey(s.SortKey); // finite-SortKey invariant (comparator totality)

            boxes[boxCount++] = SymbolBox.Build(
                translatedScreenPx, s.BoundsMin, s.BoundsMax, s.TextSizePx, s.PaddingPx,
                sortKey, s.FeatureIndex, s.TileKey, candidateOrdinal, s.AllowOverlap, s.IgnorePlacement);

            int quadStart = quadCount;
            for (int q = 0; q < quads.Length; q++)
                quadsOut[quadCount++] = new PlacedQuad
                {
                    Quad = quads[q], AnchorScreenPx = translatedScreenPx, TextSizePx = s.TextSizePx,
                    Depth = s.Depth, Color = s.Color, RotationRadians = rotationRadians,
                };

            // IsWorld marks this emit for WorldSymbolRenderer's point branch. TranslateDeltaPx is the translate
            // folded into translatedScreenPx above, as a delta the world path adds to Offset.
            emit[emitCount++] = new CandidateEmit
            {
                QuadStart = quadStart, QuadCount = quads.Length, Slot = s.Slot, AtlasKind = s.AtlasKind,
                AnchorLocal = s.AnchorLocal, TileOriginRender = s.TileOriginRender, TileKey = s.TileKey,
                TranslateDeltaPx = translatedScreenPx - screenPx, IsWorld = true,
                SurfaceUp = surfaceUp,
                // text-halo-*: carried per emit, not per quad — the halo is a second copy of THIS symbol's
                // whole glyph run (WorldSymbolRenderer.Emit), so one set of values covers all of it.
                HaloColor = s.HaloColor, HaloWidthPx = s.HaloWidthPx, HaloBlurPx = s.HaloBlurPx,
            };
        }

        /// <summary>
        /// Stages one CURVED along-line symbol: one all-or-nothing candidate per build-time anchor, with N rotated
        /// per-glyph boxes/quads each. Returns the anchors staged (0 if the path culls, has zero length, or is
        /// shorter than the symbol). <paramref name="anchorFadeIds"/>/<paramref name="anchorWasPlaced"/> hold each
        /// anchor's fade id and incumbency, the last slot for the centred fallback (<see cref="LineFadeId"/>).
        /// Non-local invariant: the caller sizes <paramref name="pathPoints"/> and <paramref name="cumulativeLengths"/>
        /// to at least <paramref name="screenPath"/>'s length, because every vertex is written with no Burst bounds
        /// check. Under <c>s.PitchAlignment == Map &amp;&amp; s.MetresPerLogicalPixel &gt; 0</c> the arc walk runs
        /// in WORLD METRES (docs/labels-and-symbols-design.md § "3. Curved along-line text"); one <c>arcScale</c> converts
        /// every arc quantity; the same value on <see cref="CandidateEmit.CornerMetresPerLogicalPixel"/> sizes the
        /// glyphs, so spacing and size foreshorten together. The collision box is then the projected world corners
        /// (<see cref="SymbolBox.TryBuildProjectedWorldGlyph"/>) when <paramref name="view"/> is usable. An unpatched
        /// ruler (0) keeps the screen walk rather than collapsing the symbol to a point.
        /// Limitation: under the world walk the screen point is an affine lerp at the world parameter, not
        /// perspective-correct; on the map arm it feeds only fallbacks. The exact screen parameter is
        /// <c>t' = t·w₁/((1−t)·w₀+t·w₁)</c>, not its transpose (which moves toward the camera); the view
        /// matrix gives clip w.
        /// </summary>
        public static int StageCurved(in CurvedStageInput s,
            ReadOnlySpan<float2> screenPath, ReadOnlySpan<float> depthPath, ReadOnlySpan<byte> validPath,
            ReadOnlySpan<double3> worldPath, ReadOnlySpan<float3> worldUpPath,
            ReadOnlySpan<CurvedGlyph> glyphs, ReadOnlySpan<LineAnchor> anchors,
            ReadOnlySpan<long> anchorFadeIds, ReadOnlySpan<byte> anchorWasPlaced,
            Span<float2> pathPoints, Span<float> cumulativeLengths,
            float bearingRadians, in SymbolViewTransform view, int ordinal,
            Span<SymbolBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<SymbolCandidate> candidates, Span<CandidateEmit> emit, ref int emitCount)
        {
            int pathLen = screenPath.Length;
            if (pathLen < 2 || glyphs.Length == 0 || anchors.Length == 0) return 0;

            // Validate + copy this frame's projected path: any vertex behind the camera, or a near-plane blow-up
            // that would explode the arc length, skips the whole symbol. Representative depth from the mid vertex.
            float pathDepth = 0f;
            for (int v = 0; v < pathLen; v++)
            {
                if (validPath[v] == 0) return 0;
                float2 sp = screenPath[v];
                if (!(math.abs(sp.x) < SymbolScreenProjection.MaxProjectedPx && math.abs(sp.y) < SymbolScreenProjection.MaxProjectedPx)) return 0;
                pathPoints[v] = sp;
                if (v == pathLen / 2) pathDepth = depthPath[v];
            }

            // The ONE branch: under `worldArc` every "arc" quantity below (and StageCurvedAnchor's) is METRES,
            // otherwise screen px; `arcScale` carries the unit, so nothing is half-converted.
            bool worldArc = s.PitchAlignment == AlignmentMode.Map && s.MetresPerLogicalPixel > 0f;

            // The same evaluation selects the CORNER unit (0 = logical px, > 0 = metres per px).
            // StageCurvedAnchor only carries it and never re-evaluates the predicate.
            float cornerMetresPerLogicalPixel = worldArc ? s.MetresPerLogicalPixel : 0f;

            float total = worldArc
                ? PolylineArcMath.BuildCumulativeWorld(worldPath, pathLen, cumulativeLengths)   // metres
                : PolylineArcMath.BuildCumulative(pathPoints, pathLen, cumulativeLengths);     // screen px
            if (!(total > 0f)) return 0;

            float arcScale         = s.TextSizePx / TextQuadLayout.OneEm            // baked em -> arc units
                                     * (worldArc ? s.MetresPerLogicalPixel : 1f);
            float symbolCenterBaked = (glyphs[0].ArcCenter + glyphs[glyphs.Length - 1].ArcCenter) * 0.5f;
            float symbolSpanArc     = (glyphs[glyphs.Length - 1].ArcCenter - glyphs[0].ArcCenter) * arcScale;
            float halfSpan         = symbolSpanArc * 0.5f;
            if (symbolSpanArc > total) return 0;

            int cursor = 0; // one resumable arc cursor for the whole symbol (Lever A), threaded across anchors.
            int staged = 0;
            int anchorCount = math.min(anchors.Length, MaxAnchorsPerLine);
            for (int a = 0; a < anchorCount; a++)
            {
                // With the world table this is the anchor's WORLD arc distance; resolved on the projected path,
                // a map-pitched anchor would drift with the pose, since the two agree only at constant depth.
                float centerArc = PolylineArcMath.ArcDistanceAt(cumulativeLengths, pathLen, anchors[a].Segment, anchors[a].T);
                if (centerArc - halfSpan < 0f || centerArc + halfSpan > total) continue; // symbol spills the ends
                if (StageCurvedAnchor(in s, pathPoints, cumulativeLengths, pathLen, total, worldPath, worldUpPath, glyphs, ref cursor,
                        ordinal + staged, anchorFadeIds[a], anchorWasPlaced[a] != 0,
                        centerArc, symbolCenterBaked, arcScale, cornerMetresPerLogicalPixel, pathDepth, bearingRadians,
                        in view,
                        boxes, ref boxCount, quadsOut, ref quadCount, candidates, emit, ref emitCount))
                    staged++;
            }

            // No build-time anchor's projected position fit this frame → try one centred symbol at the arc midpoint.
            if (staged == 0 &&
                StageCurvedAnchor(in s, pathPoints, cumulativeLengths, pathLen, total, worldPath, worldUpPath, glyphs, ref cursor,
                    ordinal, anchorFadeIds[anchorCount], anchorWasPlaced[anchorCount] != 0,
                    total * 0.5f, symbolCenterBaked, arcScale, cornerMetresPerLogicalPixel, pathDepth, bearingRadians,
                    in view,
                    boxes, ref boxCount, quadsOut, ref quadCount, candidates, emit, ref emitCount))
                staged = 1;

            return staged;
        }

        // PolylineArcMath.At's formula (byte-identical output), also returning (seg, t) so the caller samples
        // the WORLD polyline at the identical position without a second, diverging walk.
        private static void AtWithSegment(ReadOnlySpan<float2> path, ReadOnlySpan<float> cumulative, int pathLen,
            float total, float arc, ref int cursor,
            out float2 point, out float tangentRadians, out int seg, out float t)
        {
            if (arc <= 0f)
            {
                seg = 0; t = 0f;
                point = path[0];
                tangentRadians = PolylineArcMath.SegmentTangent(path, pathLen, 0);
                return;
            }
            if (arc >= total)
            {
                seg = pathLen - 2; t = 1f;
                point = path[pathLen - 1];
                tangentRadians = PolylineArcMath.SegmentTangent(path, pathLen, pathLen - 2);
                return;
            }
            PolylineArcMath.SegmentAt(cumulative, pathLen, total, arc, ref cursor, out seg, out t);
            point = math.lerp(path[seg], path[seg + 1], t);
            tangentRadians = PolylineArcMath.SegmentTangent(path, pathLen, seg);
        }

        // Stages ONE curved symbol at `centerArc`; rolls back and returns false past text-max-angle. The arc
        // inputs share StageCurved's unit, while `path` stays the SCREEN polyline that (seg, t) lands on.
        private static bool StageCurvedAnchor(in CurvedStageInput s,
            ReadOnlySpan<float2> path, ReadOnlySpan<float> cumulative, int pathLen, float total,
            ReadOnlySpan<double3> worldPath, ReadOnlySpan<float3> worldUpPath,
            ReadOnlySpan<CurvedGlyph> glyphs, ref int cursor,
            int ordinal, long fadeId, bool wasPlaced,
            float centerArc, float symbolCenterBaked, float arcScale, float cornerMetresPerLogicalPixel,
            float pathDepth, float bearingRadians, in SymbolViewTransform view,
            Span<SymbolBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<SymbolCandidate> candidates, Span<CandidateEmit> emit, ref int emitCount)
        {
            // keep-upright: a centre tangent pointing leftward reads right-to-left; walk the arc reversed and flip
            // each glyph +pi so it still reads left-to-right (each anchor decides its own flip — a line can bend back).
            PolylineArcMath.At(path, cumulative, pathLen, total, centerArc, ref cursor, out _, out float centerTangent);
            bool  reversed = s.KeepUpright && math.cos(centerTangent) < 0f;
            float dir      = reversed ? -1f : 1f;
            float flip     = reversed ? math.PI : 0f;
            float maxAngleRad = math.radians(s.MaxAngleDeg);
            float sortKey = SanitizeSortKey(s.SortKey); // finite-SortKey invariant (comparator totality)

            // Hoisted for the per-glyph box: WorldSymbolRenderer rotates corners by ExtraRotationRadians and
            // adds TranslateDeltaPx · cornerScale, so a box without them does not bound the ink.
            float2 translateDeltaPx = SymbolTranslate.ApplyTranslate(
                float2.zero, s.TranslatePx, s.TranslateAnchor, bearingRadians);
            float extraRotationRadians = SymbolBearing.IconRotationRadians(s.IconRotateRadians);

            int boxStart  = boxCount;
            int quadStart = quadCount;
            float prevCenterTangent = 0f;
            for (int g = 0; g < glyphs.Length; g++)
            {
                CurvedGlyph cg = glyphs[g];
                float arc = centerArc + dir * (cg.ArcCenter - symbolCenterBaked) * arcScale;
                AtWithSegment(path, cumulative, pathLen, total, arc, ref cursor,
                    out float2 pt, out float centerTangentAtGlyph, out int segArc, out float tArc);

                // The max-angle gate uses the raw SEGMENT tangent: an atan2 ULP mismatch on the blended angle
                // flips Burst vs managed at the threshold (SymbolStageJobTests.BurstStage_MatchesManaged_CurvedBends).
                if (g > 0 && math.abs(AngleDelta(centerTangentAtGlyph, prevCenterTangent)) > maxAngleRad)
                {
                    boxCount = boxStart; quadCount = quadStart; // roll back this anchor's partial appends
                    return false;                                // too sharp a bend → drop the symbol here
                }
                prevCenterTangent = centerTangentAtGlyph;

                // The world anchor (RTC: worldPoint − TileOriginRender) at the screen walk's (seg,t); segDirWorld
                // is the fallback direction for a degenerate chord below.
                PolylineArcMath.SampleWorld(worldPath, pathLen, segArc, tArc, out double3 worldPt, out double3 segDirWorld);
                // The up analogue, sampled at the IDENTICAL (segArc, tArc) — never re-sampled at the
                // chord probe's (segLeft,…)/(segRight,…) below; the anchor is the sample point.
                float3 surfaceUp = PolylineArcMath.SampleUp(worldUpPath, segArc, tArc);

                // Non-obvious why: orient the glyph by the chord across its own ink footprint (skirt removed), not
                // the segment tangent, so glyphs straddling a vertex tile edge-to-edge; render-only, not the cull.
                // halfWidthArc is an ARC quantity scaled by `arcScale`: on the px scale under a metre walk the
                // chord degenerates and silently falls back to the raw segment (only a bent path shows it).
                float halfWidthArc =
                    (cg.Cell.BottomRight.x - cg.Cell.TopLeft.x - 2f * cg.CellSkirt) * arcScale * 0.5f;
                float tangent = centerTangentAtGlyph;
                double3 chordDirWorld = segDirWorld; // fallback: raw segment direction (degenerate/no-chord case)
                if (halfWidthArc > 1e-4f)
                {
                    float arcLeft  = math.max(0f, math.min(total, arc - halfWidthArc));
                    float arcRight = math.max(0f, math.min(total, arc + halfWidthArc));
                    AtWithSegment(path, cumulative, pathLen, total, arcLeft,  ref cursor,
                        out float2 pLeft,  out _, out int segLeft,  out float tLeft);
                    AtWithSegment(path, cumulative, pathLen, total, arcRight, ref cursor,
                        out float2 pRight, out _, out int segRight, out float tRight);
                    float2 chord = pRight - pLeft;
                    if (math.lengthsq(chord) > 1e-12f) tangent = (float)math.atan2(chord.y, chord.x);

                    // World chord: the same arcLeft/arcRight (seg,t), sampled on the WORLD polyline instead of
                    // the screen one.
                    PolylineArcMath.SampleWorld(worldPath, pathLen, segLeft,  tLeft,  out double3 worldLeft,  out _);
                    PolylineArcMath.SampleWorld(worldPath, pathLen, segRight, tRight, out double3 worldRight, out _);
                    double3 worldChord = worldRight - worldLeft;
                    if (math.lengthsq(worldChord) > 1e-12) chordDirWorld = worldChord;
                }

                // Unit-normalize (direction only feeds the shader's atan2 — magnitude never matters) and
                // bake the SAME keep-upright negation the screen `flip` above already decided.
                double3 unitTangent = math.lengthsq(chordDirWorld) > 1e-18 ? math.normalize(chordDirWorld) : double3.zero;
                if (reversed) unitTangent = -unitTangent;
                float3 tangentLocal = new float3((float)unitTangent.x, (float)unitTangent.y, (float)unitTangent.z);
                // Level-1 RTC bake (manual per-component narrow — no assumed double3→float3 cast operator,
                // mirrors the parity oracle's AddPoint identical narrowing).
                float3 anchorLocal = new float3(
                    (float)(worldPt.x - s.TileOriginRender.x),
                    (float)(worldPt.y - s.TileOriginRender.y),
                    (float)(worldPt.z - s.TileOriginRender.z));

                pt = SymbolTranslate.ApplyTranslate(pt, s.TranslatePx, s.TranslateAnchor, bearingRadians);
                float rotation = tangent + flip; // tangent ONLY — not the symbol bearing (would double-rotate)

                // Non-obvious why: a map-pitched glyph gets the projected-world-corner box (a screen box would
                // over-reserve as it recedes), gated like the corner unit; scale is TextSizePx · cornerMetres, not
                // `arcScale`, to match the renderer to the bit. `glyphBox` is pre-declared for definite assignment.
                SymbolBox glyphBox = default;
                bool projected = cornerMetresPerLogicalPixel > 0f && view.IsUsable &&
                    SymbolBox.TryBuildProjectedWorldGlyph(
                        cg.Cell, cg.CellSkirt,
                        s.TextSizePx * cornerMetresPerLogicalPixel,
                        extraRotationRadians,
                        translateDeltaPx * cornerMetresPerLogicalPixel,
                        worldPt, unitTangent, surfaceUp,
                        in view, s.PaddingPx, out glyphBox);
                boxes[boxCount++] = projected
                    ? glyphBox
                    : SymbolBox.BuildRotatedGlyph(pt, cg.Cell, s.TextSizePx, rotation, s.PaddingPx, cg.CellSkirt);
                quadsOut[quadCount++] = new PlacedQuad
                {
                    Quad = cg.Cell, AnchorScreenPx = pt, TextSizePx = s.TextSizePx,
                    Depth = pathDepth, Color = s.Color, RotationRadians = rotation,
                    AnchorLocal = anchorLocal, Tangent = tangentLocal,
                    SurfaceUp = surfaceUp,
                };
            }

            int emitStart = emitCount;
            candidates[ordinal] = new SymbolCandidate
            {
                BoxStart = boxStart, BoxCount = glyphs.Length,
                EmitStart = emitStart, EmitCount = 1,
                SortKey = sortKey, FeatureIndex = s.FeatureIndex, TileKey = s.TileKey,
                AllowOverlap = s.AllowOverlap, IgnorePlacement = s.IgnorePlacement, SymbolIndex = ordinal,
                FadeId = fadeId, WasPlacedLastFrame = wasPlaced,
            };
            // The IsWorld sink point symbols use; AlongLine makes WorldSymbolRenderer.Emit read each quad's own
            // AnchorLocal/Tangent. The translate delta is position-independent, so one value serves the symbol.
            emit[emitCount++] = new CandidateEmit
            {
                QuadStart = quadStart, QuadCount = glyphs.Length, Slot = s.Slot, AtlasKind = s.AtlasKind,
                TileKey = s.TileKey, TileOriginRender = s.TileOriginRender, TranslateDeltaPx = translateDeltaPx,
                IsWorld = true, AlongLine = true,
                // text-halo-*: carried per emit, not per quad — the halo is a second copy of THIS symbol's whole
                // glyph run (WorldSymbolRenderer.Emit), so one set of values covers all of it.
                HaloColor = s.HaloColor, HaloWidthPx = s.HaloWidthPx, HaloBlurPx = s.HaloBlurPx,

                // Decided once by StageCurved's `worldArc`: the factor `arcScale` spaces glyphs with, so
                // spacing and drawn cell size share one constant.
                CornerMetresPerLogicalPixel = cornerMetresPerLogicalPixel,
                // icon-rotate rides here because the renderer forces per-quad rotation to 0 on this path (see
                // CandidateEmit.ExtraRotationRadians); curved text leaves it 0.
                ExtraRotationRadians = extraRotationRadians,
            };
            return true;
        }

        /// <summary>LINE fade identity: (tile, LAYER, feature, anchor-index) FNV-1a-64; anchor index -1 is the
        /// centred fallback. Non-local invariant: <paramref name="layerId"/> is required, because the extractor
        /// restarts <c>FeatureIndex</c> per layer, and two candidates sharing a fade id fight over one opacity in
        /// <see cref="SymbolPlacementSystem"/>. <see cref="SymbolPlacementSystem.PointFadeId"/> does the same.</summary>
        public static long LineFadeId(long tileKey, int layerId, int featureIndex, int anchorIndex)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV-1a 64
                h = (h ^ (ulong)tileKey) * 1099511628211UL;
                h = (h ^ (ulong)(uint)layerId) * 1099511628211UL;
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
