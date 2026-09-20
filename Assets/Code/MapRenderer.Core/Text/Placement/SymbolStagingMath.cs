// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2/4 — this
// file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to a
// (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See the sibling
// SymbolScreenProjection header comment for the namespace-collision trap.

using System;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The per-frame symbol STAGING geometry — projecting/placing one symbol's collision boxes + drawn quads —
    /// as pure, engine-free static functions over BLITTABLE inputs (no managed per-symbol carrier).
    ///
    /// <para>Inputs are the per-symbol values a producer can compute once and store blittable
    /// (<see cref="PointStageInput"/>/<see cref="CurvedStageInput"/>) plus this frame's projected screen geometry;
    /// the string-derived point fade-id, the <see cref="LinearColor"/> conversion, and the last-frame incumbency
    /// lookup are the caller's job (they need managed/main-thread state) and arrive pre-resolved as plain values.</para>
    ///
    /// <para>Outputs are appended to caller-owned growable pools (<c>ref T[]</c> + a <c>ref int</c> cursor,
    /// geometric growth, never shrinks — no per-frame GC once warm).</para>
    /// </summary>
    public static class SymbolStagingMath
    {
        /// <summary>Defence-in-depth cap on a line's repeat anchors — the build-time bake and the per-frame
        /// staging loop both clamp to this same value, so their anchor counts agree.</summary>
        public const int MaxAnchorsPerLine = 256;

        // Blocker B1 (salvaged from reverted 6e39e282): a NON-FINITE symbol-sort-key breaks
        // ComparePlacementOrder's totality — with a NaN key BOTH `a < b` and `a > b` are false, so the compare
        // FALLS THROUGH the sort-key branch (never returning 0 for the pair) and the resulting order becomes
        // INTRANSITIVE (a 3-cycle whose sort-key/feature comparisons disagree), making the unstable heapsort's
        // survivor set seed-/mirror-order dependent. `symbol-sort-key` projects raw expression output to float
        // with no finite check, and `/` returns raw IEEE, so a style like ["/", 0, 0] yields NaN. Normalizing at
        // THIS candidate-build choke (the single point where the raw baked value becomes a sort key) restores a
        // strict total order. float.MaxValue sorts LAST (lowest priority) — a broken authoring value sorts no
        // earlier than a legitimately-authored float.MaxValue key (a tie there falls to feature/tile/fade order).
        // No-op on every shipped scene (defaults to 0; real styles use finite exprs), so
        // this is byte-identical on every baked snapshot. finite ⇔ |k| ≤ MaxValue (NaN and ±Inf both fail the
        // compare). Expressed via math.abs (not math.isfinite) so it also compiles under the Tools/core-tests
        // Unity.Mathematics shim (has math.abs, not math.isfinite) — this file is compiled by BOTH runners.
        internal static float SanitizeSortKey(float k) => math.abs(k) <= float.MaxValue ? k : float.MaxValue;

        /// <summary>
        /// Stages one POINT symbol: its whole-symbol AABB collision box + glyph quads at the projected anchor
        /// (rotated by the #4 bearing under <c>text-rotation-alignment:map</c>). Writes
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
        /// Road-shields §10 D8: stages a centred icon+text PAIR as ONE candidate spanning both halves' boxes —
        /// the existing all-or-nothing multi-box machinery (<c>CollisionJob</c>) curved symbols already run
        /// on, so the pair cannot self-block. The projection/viewport gate runs ONCE,
        /// on <paramref name="owner"/> — the halves share an anchor by construction (both emitted from the
        /// extractor's same <c>EmitAtAnchor</c> anchor), which is what makes the pair atomic at the cull too.
        /// <paramref name="rider"/>'s box/quads/emit are appended only when <paramref name="riderQuads"/> is
        /// non-empty (a text that laid out no glyphs degrades to a lone badge, never a dangling box). Writes
        /// <c>candidates[ordinal]</c> and ONE OR TWO <c>CandidateEmit</c>s starting at <c>emitCount</c>. Returns 1
        /// if staged, or 0 when the owner itself has no quads or culls.
        ///
        /// <para>Stage C (<c>icon-optional</c>/<c>text-optional</c>): each half's
        /// <see cref="PointStageInput.PairOptional"/> becomes its bit in the candidate's
        /// <see cref="SymbolCandidate.OptionalBoxMask"/> (bit 0 = owner, bit 1 = rider), so collision may drop
        /// that half alone instead of the whole pair. <paramref name="droppedHalvesLastFrame"/> carries the
        /// PREVIOUS frame's per-half verdict for this pair's <c>FadeId</c> (R3's one-frame verdict latency —
        /// the emit loop runs before collision), and is masked down to the halves that are actually optional
        /// and actually staged. Both default to 0, which is the exact pre-Stage-C behaviour.</para>
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
            // Both halves are appended at the OWNER's raw projected anchor — AppendPointHalf applies each
            // half's OWN translate on top of it, so a text-translate (icon has none) still resolves correctly.
            // P2: SurfaceUp is likewise carried from the OWNER for both halves — a pair shares one anchor.
            AppendPointHalf(in owner, ownerQuads, owner.ScreenPx, owner.SurfaceUp, bearingRadians, ordinal,
                boxes, ref boxCount, quadsOut, ref quadCount, emit, ref emitCount);

            int boxCountForCandidate = 1;
            // Stage C: bit 0 addresses the owner's box/emit, bit 1 the rider's — the rider's bit is set only
            // when it actually staged one (an empty rider appends no box, so a bit would address the NEXT
            // candidate's).
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
                // §10 D8: the pair ignores collision only if BOTH halves do, and blocks unless BOTH decline to.
                AllowOverlap = owner.AllowOverlap && rider.AllowOverlap,
                IgnorePlacement = owner.IgnorePlacement && rider.IgnorePlacement,
                SymbolIndex = ordinal,
                FadeId = owner.FadeId, WasPlacedLastFrame = owner.WasPlacedLastFrame,
            };
            return 1;
        }

        // Appends ONE half of a point symbol (a lone symbol, or one side of a §10 D8 pair): its collision box,
        // glyph quads, and its own CandidateEmit — applying THIS half's own translate/rotation. `screenPx` is
        // the shared, UN-translated projected anchor (StagePoint's own s.ScreenPx, or a pair's owner.ScreenPx
        // for both halves — the gate/projection already ran once on the owner). Factored out of the pre-§10
        // StagePoint body so a lone symbol and a pair's two halves cannot drift: ONE implementation appends a
        // box + quads + emit, whether called once (StagePoint) or twice (StagePointPair).
        private static void AppendPointHalf(in PointStageInput s, ReadOnlySpan<SymbolQuad> quads,
            float2 screenPx, float3 surfaceUp, float bearingRadians, int candidateOrdinal,
            Span<SymbolBox> boxes, ref int boxCount, Span<PlacedQuad> quadsOut, ref int quadCount,
            Span<CandidateEmit> emit, ref int emitCount)
        {
            float2 translatedScreenPx = SymbolTranslate.ApplyTranslate(screenPx, s.TranslatePx, s.TranslateAnchor, bearingRadians);
            // P-B: icon-rotate is a CONSTANT angular offset composed on top of whatever the alignment
            // produced — viewport ⇒ icon-rotate alone, map ⇒ bearing + icon-rotate. 2D rotations commute, so
            // one addition here is the whole composition. 0 for every text symbol (exact `x + 0f`).
            // SymbolBearing.IconRotationRadians converts MapLibre's clockwise-positive sense into this frame's
            // counter-clockwise-positive one — the ONE negation, shared with the along-line path below.
            float rotationRadians = SymbolBearing.BillboardRotationRadians(s.RotationAlignment, bearingRadians)
                                    + SymbolBearing.IconRotationRadians(s.IconRotateRadians);
            float sortKey = SanitizeSortKey(s.SortKey); // B1: finite-SortKey invariant (comparator totality)

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

            // Epic A / A1 (design §11 A1 D2/D6): carry the world-anchored draw payload alongside the
            // (unchanged) screen box/quad above — IsWorld=true marks this emit for WorldSymbolRenderer's emit
            // branch (D2/D6), never a curved one (StageCurvedAnchor leaves these fields default).
            // TranslateDeltaPx is the SAME translate already folded into translatedScreenPx above, expressed
            // as a delta from the untranslated anchor (D4) — the world path adds it to Offset instead of
            // the (unavailable, un-projected) anchor.
            emit[emitCount++] = new CandidateEmit
            {
                QuadStart = quadStart, QuadCount = quads.Length, Slot = s.Slot, AtlasKind = s.AtlasKind,
                AnchorLocal = s.AnchorLocal, TileOriginRender = s.TileOriginRender, TileKey = s.TileKey,
                TranslateDeltaPx = translatedScreenPx - screenPx, IsWorld = true,
                SurfaceUp = surfaceUp,
                // text-halo-*: carried per emit, not per quad — the halo is a second copy of THIS label's
                // whole glyph run (WorldSymbolRenderer.Emit), so one set of values covers all of it.
                HaloColor = s.HaloColor, HaloWidthPx = s.HaloWidthPx, HaloBlurPx = s.HaloBlurPx,
            };
        }

        /// <summary>
        /// Stages one CURVED along-line symbol (#5): validates + walks this frame's projected path, then stages one
        /// all-or-nothing candidate per stable build-time anchor (A-2), each N per-glyph rotated boxes/quads placed
        /// along the arc. Returns the number of anchors staged (0 if the path culls, has zero projected length, or
        /// the symbol is longer than the whole line). <paramref name="pathPoints"/> and
        /// <paramref name="cumulativeLengths"/> are caller-owned reused buffers sized to at least the path length.
        ///
        /// <para><paramref name="anchorFadeIds"/>/<paramref name="anchorWasPlaced"/> carry the pre-resolved A-4
        /// fade id + A-5 incumbency per anchor — indices <c>[0, anchorCount)</c> for the build-time anchors and the
        /// LAST slot (<c>[anchorCount]</c>) for the centred fallback (anchor index -1). Compute them with
        /// <see cref="LineFadeId"/> against the caller's placed-last-frame set.</para>
        ///
        /// <para><b>W1 — TWO ARC RULERS, ONE SCALE.</b> The predicate is
        /// <c>s.PitchAlignment == AlignmentMode.Map &amp;&amp; s.MetresPerLogicalPixel &gt; 0</c>. Under it the
        /// arc walk runs in WORLD METRES: the cumulative table is built from <paramref name="worldPath"/>
        /// (<see cref="PolylineArcMath.BuildCumulativeWorld"/>) instead of the projected screen path, and the
        /// glyph advance is converted metres-per-logical-px once. Otherwise the pre-W1 screen-px walk runs
        /// unchanged. This is the settled model — a map-pitched <c>text-size</c> is X px TOP-DOWN, i.e. a
        /// world size fixed once, with the perspective divide doing the rest, so letters AND letter spacing
        /// foreshorten together (the same principle as <c>line-width</c>).</para>
        ///
        /// <para><b>The single scale carries the unit, so a PARTIAL conversion is not expressible.</b>
        /// <c>arcScale</c> is the ONE factor turning a baked em coordinate into an arc distance, and it has
        /// exactly three consumers — the symbol span (and therefore the spill gate), the per-glyph
        /// <c>arc</c>, and the chord probe's half-width — all three of which are arc quantities. So redefining
        /// <c>arcScale</c> by the branch converts every arc quantity at once; there is no fourth site to
        /// forget.</para>
        ///
        /// <para><b>W2 — the SAME conjunct now selects TWO things, and they are one value by construction.</b>
        /// Before W2 <c>arcScale</c> had no render-size consumer: the DRAWN cell size came from
        /// <see cref="CurvedStageInput.TextSizePx"/> straight through <see cref="PlacedQuad.TextSizePx"/>, in
        /// logical px, so a map-pitched symbol's spacing was a world length while its glyphs were a screen
        /// size — the two rulers diverged with depth and the letters piled up as the symbol receded. W2 closes
        /// that by writing the SAME <c>MetresPerLogicalPixel</c> onto
        /// <see cref="CandidateEmit.CornerMetresPerLogicalPixel"/>, which the renderer multiplies the corner
        /// offsets by. Spacing and size then share one constant, so their RATIO is the purely typographic
        /// <c>ΔArcCenter / cellWidthBaked</c> — every scale cancels, at every depth. This method's only W2
        /// edit is to carry that already-computed value onto the emit; no arithmetic in the walk changed.</para>
        ///
        /// <para><b>Under the world walk the SCREEN point is the approximate side, deliberately.</b>
        /// <c>AtWithSegment</c> resolves <c>(seg, t)</c> against the world table and then reads the screen
        /// point as <c>lerp(path[seg], path[seg+1], t)</c> — an AFFINE interpolation at a WORLD parameter,
        /// which is not perspective-correct. Clip space is linear in the WORLD parameter, so
        /// <c>C(t) = (1−t)·C₀ + t·C₁</c> and <c>w(t) = (1−t)·w₀ + t·w₁</c>; dividing through, the exact SCREEN
        /// parameter is
        /// <code>
        ///     t' = t·w₁ / ((1−t)·w₀ + t·w₁)          [ ≡ (t/w₀) / ((1−t)/w₁ + t/w₀) ]
        /// </code>
        /// <b>Mind the direction.</b> This is the WORLD→SCREEN map; its inverse (the familiar
        /// screen→attribute form, with <c>w₀</c> and <c>w₁</c> transposed) is a different function and is NOT
        /// what this site needs. Sanity check: on a receding segment the far half is compressed on screen, so
        /// the world midpoint must land PAST the screen midpoint, toward the far endpoint —
        /// <c>t' = w₁/(w₀+w₁) &gt; ½</c> whenever <c>w₁ &gt; w₀</c> (2/3 for a 2:1 segment). The transposed
        /// form moves it the wrong way, toward the camera. That is the right side to carry the error: the WORLD
        /// anchor (<see cref="PolylineArcMath.SampleWorld"/>) is exact and is what the renderer actually
        /// draws. <b>W3 CHANGED WHAT THIS SCREEN POINT FEEDS — the old "only the collision box and the dead
        /// screen quad path" is no longer true.</b> Since W3 the map-pitched collision box is built from the
        /// EXACT projection of <c>SampleWorld</c>'s world point (see the W3 paragraph below), so on the map
        /// arm this affine point feeds only the dead screen quad path, the FALLBACK box on a degenerate
        /// ground frame or an unprojectable corner (unreachable at any shipped pose — F-W3-1), and
        /// <see cref="PlacedQuad.RotationRadians"/>, which <c>WorldSymbolRenderer</c> discards for
        /// <c>AlongLine</c> in favour of <c>CandidateEmit.ExtraRotationRadians</c>. <b>The limitation is
        /// therefore cosmetic on the map arm:</b> implementing <c>t'</c> would change no live output there.
        /// It still governs the non-map arm, where the screen walk is the walk.
        /// <b>The exact form is also not implementable from today's inputs:</b>
        /// <paramref name="depthPath"/> is NDC depth (<c>clip.z / clip.w</c>), and clip <c>w</c> is not
        /// recoverable from it without the projection's <c>m22</c>/<c>m23</c>, which this math never
        /// receives. (W3 plumbs the view MATRIX instead, which yields <c>w</c> at any point — so the input is
        /// now available should a later stage want it.) Observed by
        /// <c>MapPitchedWorldArcStagingTests</c> W1-T6, which goes RED the moment anyone implements
        /// <c>t'</c>; if a later stage implements it anyway, delete this paragraph along with that tooth.</para>
        ///
        /// <para><b>W3 — the map-pitched COLLISION BOX is the screen AABB of the glyph's four PROJECTED WORLD
        /// CORNERS.</b> Two gates select it, both already in this method: <c>cornerMetresPerLogicalPixel &gt; 0</c>
        /// (the same value W2 writes onto the emit, so a metre-sized quad with a pixel-sized box is not
        /// expressible) and <see cref="SymbolViewTransform.IsUsable"/> on <paramref name="view"/> (a
        /// default-constructed transform means "no camera was supplied" and keeps the pre-W3 screen box
        /// byte-identical — which is what every hand-built staging fixture gets). Since W2 the glyph is DRAWN
        /// as a world-metre quad in the ground plane, so its screen size foreshortens; the screen box sized
        /// from <c>TextSizePx</c> had no depth term and over-reserved without bound as the symbol receded
        /// (~150× the ink area at 8× the look-at depth). The box is built by
        /// <see cref="SymbolBox.TryBuildProjectedWorldGlyph"/>, which falls back to the screen box on a
        /// degenerate ground frame or an unprojectable corner. Observed by W3-T1…T9.</para>
        ///
        /// <para><b>The max-angle gate stays on the SCREEN tangent</b> (see <c>StageCurvedAnchor</c>): one
        /// stage, one concern, and the existing Burst-vs-managed atan2 ULP argument for using the raw segment
        /// tangent is unaffected by which table resolved the position. Whether a ground-welded symbol should
        /// instead gate on world curvature is a separate, later question. Observed by W1-T7.</para>
        ///
        /// <para><b>The <c>MetresPerLogicalPixel &gt; 0</c> conjunct is a degradation guard.</b> Without it a
        /// map-pitched symbol whose per-frame ruler was never patched would get <c>arcScale == 0</c>, every
        /// glyph would land on <c>centerArc</c>, and the symbol would silently collapse to a point. Degrading
        /// to the existing screen walk is the same failure-mode judgement the epic's earlier map-pitch guards
        /// took (a staging pass has no channel to report); unlike those, it is pinned by a tooth (W1-T9) so
        /// it cannot rot into an unobserved path.</para>
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

            // W1: the ONE branch. `worldArc` picks the ruler; `arcScale` carries its unit into every arc
            // quantity below (see this method's doc). Under `worldArc` everything named "arc" — the
            // cumulative table, `total`, `symbolSpanArc`, `halfSpan`, `centerArc`, each glyph's `arc`, and
            // StageCurvedAnchor's `halfWidthArc` — is METRES; otherwise all of them are screen px, exactly as
            // before. Nothing is half-converted: the two substitutions here are the whole change.
            bool worldArc = s.PitchAlignment == AlignmentMode.Map && s.MetresPerLogicalPixel > 0f;

            // W2: the SAME predicate, one evaluation, also selects the CORNER unit. 0 ⇒ the emit's corner
            // offsets stay logical px; > 0 ⇒ they are metres and this is the factor. StageCurvedAnchor only
            // carries it — it must never re-evaluate the predicate (see CandidateEmit's doc).
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
                // W1: with the world table this is the anchor's WORLD arc distance. A (seg, t) anchor is
                // build-time tile-space topology, so resolving it on the world path is strictly more faithful
                // than resolving it on the per-frame projected one — the two agree only at constant view
                // depth, which is why a map-pitched curved anchor used to drift with the pose.
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

        // Stage AC (curved-world): resolves `arc` to its screen point/tangent AND (seg,t) in one pass — the
        // SAME formula PolylineArcMath.At uses internally (byte-identical to At's own output), but also
        // surfaces the segment index/parametric t so the caller can sample the WORLD polyline at the
        // IDENTICAL position — no second, independently-diverging walk.
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

        // Stages ONE curved-symbol instance centred at `centerArc`. Rolls the box/quad pools back and returns false
        // if any adjacent-glyph line curvature exceeds text-max-angle (the symbol is dropped at this anchor, #6).
        //
        // W1: `centerArc`, `total`, `cumulative` and `arcScale` are all in ONE arc unit — screen px under the
        // pre-W1 walk, world METRES under map pitch alignment (StageCurved's doc has the branch). This method
        // never re-derives which; it just uses them consistently. `path` stays the SCREEN polyline in both
        // cases: AtWithSegment resolves (seg, t) from `cumulative`/`total` and reads the point/tangent from
        // `path`, so passing the world table with the screen path is exactly right — resolve the position in
        // metres, report where that lands on screen.
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
            float sortKey = SanitizeSortKey(s.SortKey); // B1: finite-SortKey invariant (comparator totality)

            // W3 — hoisted out of the emit below (a pure movement: every input is symbol-level, so both were
            // already loop-invariant), because the per-glyph box branch needs them INSIDE the glyph loop. The
            // box must bound what the renderer draws, and WorldSymbolRenderer rotates the corners by
            // ExtraRotationRadians and adds TranslateDeltaPx · cornerScale — so a box omitting them does not
            // bound the ink. See the emit at the bottom of this method for what each one is.
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

                // The max-angle gate answers "is the PATH too kinky to place a symbol here" — a path-curvature
                // property, so it stays on the raw per-glyph SEGMENT tangent (byte-identical to pre-fix
                // behaviour; a Burst-vs-managed atan2 ULP mismatch on the blended angle below would otherwise
                // flip cull decisions right at the threshold, see SymbolStageJobTests.BurstStage_MatchesManaged).
                if (g > 0 && math.abs(AngleDelta(centerTangentAtGlyph, prevCenterTangent)) > maxAngleRad)
                {
                    boxCount = boxStart; quadCount = quadStart; // roll back this anchor's partial appends
                    return false;                                // too sharp a bend → drop the symbol here (#6)
                }
                prevCenterTangent = centerTangentAtGlyph;

                // Stage AC: the world anchor at the SAME (seg,t) the screen walk above just resolved — the
                // Level-1 RTC bake (worldPoint − TileOriginRender). segDirWorld is the raw-segment fallback
                // direction (SampleWorld's zero-length-segment skip), used below when the chord is degenerate.
                PolylineArcMath.SampleWorld(worldPath, pathLen, segArc, tArc, out double3 worldPt, out double3 segDirWorld);
                // P2: the up analogue, sampled at the IDENTICAL (segArc, tArc) — never re-sampled at the
                // chord probe's (segLeft,…)/(segRight,…) below; the anchor is the sample point.
                float3 surfaceUp = PolylineArcMath.SampleUp(worldUpPath, segArc, tArc);

                // Orient the rigid glyph quad by the CHORD across its OWN footprint, not the single-point
                // segment tangent: a glyph straddling a polyline VERTEX would otherwise rotate to one
                // segment's raw angle while its neighbour (advance-spaced, not vertex-spaced) rotates to
                // the other, so their inner corners collide on the concave side of the bend. The chord
                // blends the two segment angles in proportion to how much of the footprint sits on each
                // side of the vertex, so consecutive glyphs tile edge-to-edge (MapLibre's fix). This is
                // purely a RENDER-orientation choice — it does not feed the cull gate above.
                // The CONTENT width: an icon cell carries a transparent border (CellSkirt) that is drawn but
                // is not ink, and the chord probe must straddle the ink's footprint, not the skirt's. Text
                // has CellSkirt == 0, so this is an exact `x - 0f` there.
                // W1 — THE TRAP THIS NAME EXISTS TO CLOSE. The half-width is an ARC quantity and must be
                // scaled by `arcScale`, i.e. it is METRES whenever the walk is. Leaving it on the px scale
                // while `arc` is in metres makes the probe span ~10 units of a ~10⁴-metre advance: the chord
                // degenerates below the 1e-12 guards below, the code silently falls back to the RAW segment
                // direction, and the vertex-straddling fix above is undone. On a STRAIGHT path that is a
                // no-op, which is why no straight-road fixture can see it — the observing tooth is
                // MapPitchedWorldArcStagingTests' W1-T10, on a bent path.
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

                    // World chord — the SAME arcLeft/arcRight arc distances (screen px or metres, per the
                    // walk in force — W1) and their just-resolved (seg,t), sampled on the WORLD polyline
                    // instead of the screen one.
                    PolylineArcMath.SampleWorld(worldPath, pathLen, segLeft,  tLeft,  out double3 worldLeft,  out _);
                    PolylineArcMath.SampleWorld(worldPath, pathLen, segRight, tRight, out double3 worldRight, out _);
                    double3 worldChord = worldRight - worldLeft;
                    if (math.lengthsq(worldChord) > 1e-12) chordDirWorld = worldChord;
                }

                // Unit-normalize (direction only feeds the shader's atan2, D-E — magnitude never matters) and
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

                // W3 — the map-pitched box is the screen AABB of the FOUR PROJECTED WORLD CORNERS, so it
                // tracks the ink at every depth. Before W3 it was a screen box sized from TextSizePx with no
                // depth term at all, while the glyph has been DRAWN as a world-metre quad since W2 — so it
                // over-reserved, always and only, without bound as the symbol receded. Same predicate the
                // corner UNIT already rides on (cornerMetresPerLogicalPixel), so a metre-sized quad with a
                // pixel-sized box is not expressible. A degenerate ground frame or a corner behind the camera
                // falls back to the pre-W3 screen box (F-W3-1). The scale is the RENDERER's own association,
                // TextSizePx · cornerMetres — NOT the equal-but-differently-associated `arcScale` above.
                // (`glyphBox` is declared up front rather than as an `out var`: the two gates short-circuit,
                // so the compiler cannot prove it assigned at the ternary below.)
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
            // Epic AC (curved-world): flip curved onto the SAME world-anchored draw sink point/icon already
            // use (D2/D6's IsWorld branch) — AlongLine additionally tells WorldSymbolRenderer.Emit to read each
            // quad's OWN AnchorLocal/Tangent (a curved candidate has no single per-candidate anchor).
            // text-translate: WorldSymbolRenderer.Emit adds emit.TranslateDeltaPx to every world corner (both
            // point and curved go through BuildWorldQuad). The delta is position-independent (ApplyTranslate
            // depends only on translatePx/anchor/bearing — NOT the input point), so it is one value per symbol:
            // ApplyTranslate(0, …) = the delta itself. Curved's per-glyph screen `pt` above is translated for
            // the dead screen/A-B path; this carries the SAME translate into the live world path.
            // (W3 hoisted `translateDeltaPx` above the glyph loop — same value, same expression.)
            emit[emitCount++] = new CandidateEmit
            {
                QuadStart = quadStart, QuadCount = glyphs.Length, Slot = s.Slot, AtlasKind = s.AtlasKind,
                TileKey = s.TileKey, TileOriginRender = s.TileOriginRender, TranslateDeltaPx = translateDeltaPx,
                IsWorld = true, AlongLine = true,
                // text-halo-*: carried per emit, not per quad — the halo is a second copy of THIS label's
                // whole glyph run (WorldSymbolRenderer.Emit), so one set of values covers all of it.
                HaloColor = s.HaloColor, HaloWidthPx = s.HaloWidthPx, HaloBlurPx = s.HaloBlurPx,

                // W2: the corner unit, decided ONCE by StageCurved's `worldArc` and only carried here. It is
                // the same MetresPerLogicalPixel factor `arcScale` above already spaces these glyphs with, so
                // a map-pitched symbol's spacing and its drawn cell size come from one constant.
                CornerMetresPerLogicalPixel = cornerMetresPerLogicalPixel,
                // P-B: the along-line path's icon-rotate term. The renderer forces this candidate's per-quad
                // rotation to 0 (the shader supplies the tangent instead), so the constant rides here — see
                // CandidateEmit.ExtraRotationRadians for the sign contract. The sense conversion is the SAME
                // SymbolBearing.IconRotationRadians the point path applies (one negation, not one per path);
                // the shader's tangent rotation acts on the already-converted offsets and cannot change it.
                // Curved TEXT leaves this 0, so the renderer passes exactly the 0f it used to hardcode.
                // (W3 hoisted the call above the glyph loop — same value, same expression.)
                ExtraRotationRadians = extraRotationRadians,
            };
            return true;
        }

        /// <summary>A-4 LINE fade identity: (tile, LAYER, feature, anchor-index) FNV-1a-64. Anchor index -1 is the
        /// centred fallback. Pure arithmetic — the caller precomputes these per anchor so the staging math needs no
        /// string or set state.
        /// <para><b><paramref name="layerId"/> is load-bearing, not decorative.</b> <c>SymbolFeatureExtractor</c>
        /// runs once PER symbol layer and restarts its <c>FeatureIndex</c> ordinal at 0 each time, so
        /// <c>(tileKey, featureIndex)</c> is NOT unique across layers of one tile — two different roads in two
        /// different line-symbol layers share it. Without the layer dimension their fade ids collide, and because a
        /// fade id must be unique per live candidate (each frame's <see cref="SymbolPlacementSystem"/> does one
        /// read-modify-write of the opacity per id), the collision makes two candidates FIGHT over one opacity and
        /// stick at a partial value forever. <see cref="SymbolPlacementSystem.PointFadeId"/> already folds in its
        /// layer id for the same reason; this is the line analogue.</para></summary>
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
