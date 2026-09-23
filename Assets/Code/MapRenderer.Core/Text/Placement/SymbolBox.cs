// Engine-free. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 — this file lives in
// MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to a (nonexistent)
// `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See the namespace-collision trap in
// GlyphAtlasTexture.cs.
// BLITTABLE: kept to blittable fields only, because the Burst CollisionJob takes it as a NativeArray<SymbolBox>
// element (mirrors PlacedQuad / LineRibbonVertex).

using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One symbol's screen-space collision record — the axis-aligned bounding box <c>CollisionJob</c> tests
    /// for overlap, plus the greedy placement-order key (<see cref="SortKey"/> + the
    /// <see cref="FeatureIndex"/>/<see cref="TileKey"/> stable tiebreak) and the per-symbol overlap flags.
    /// <see cref="Min"/>/<see cref="Max"/> are in logical screen pixels with <c>text-padding</c> ALREADY
    /// applied (see <see cref="Build"/>); <see cref="SymbolIndex"/> is the back-reference to the source symbol list.
    /// </summary>
    public struct SymbolBox
    {
        /// <summary>Collision AABB minimum corner, logical screen px (padding applied).</summary>
        public float2 Min;

        /// <summary>Collision AABB maximum corner, logical screen px (padding applied).</summary>
        public float2 Max;

        /// <summary>`symbol-sort-key` — greedy placement order. LOWER is placed FIRST (MapLibre priority:
        /// a lower sort key wins a collision against a higher one).</summary>
        public float SortKey;

        /// <summary>Feature index within its tile — the first stable tiebreak when <see cref="SortKey"/>s are equal.</summary>
        public int FeatureIndex;

        /// <summary>Owning tile id (opaque key) — the second stable tiebreak, guaranteeing a total order.</summary>
        public long TileKey;

        /// <summary>Index back into the caller's symbol list — survivor identity after the box array is sorted.</summary>
        public int SymbolIndex;

        /// <summary>`text-allow-overlap` — skip the collision test and always place this symbol.</summary>
        public bool AllowOverlap;

        /// <summary>`text-ignore-placement` — place this symbol but do NOT let it block later ones.</summary>
        public bool IgnorePlacement;

        /// <summary>
        /// Builds the screen-space collision box for a symbol from its projected anchor, the block bbox
        /// (baked-px, anchor-relative — <see cref="TextLayoutBounds.Min"/>/<c>Max</c>), the resolved
        /// <c>text-size</c>, and <c>text-padding</c>. Applies the SAME <c>textSizePx / OneEm</c> scale
        /// <see cref="BillboardMath.BuildQuad"/> uses for the visible quads (so the collision box tracks the
        /// rendered glyphs exactly), then grows it by <paramref name="paddingPx"/> on every edge.
        /// </summary>
        public static SymbolBox Build(
            in float2 anchorScreenPx,
            in float2 boundsMin,
            in float2 boundsMax,
            float textSizePx,
            float paddingPx,
            float sortKey,
            int featureIndex,
            long tileKey,
            int symbolIndex,
            bool allowOverlap,
            bool ignorePlacement)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;
            var pad = new float2(paddingPx, paddingPx);
            return new SymbolBox
            {
                Min = anchorScreenPx + boundsMin * scale - pad,
                Max = anchorScreenPx + boundsMax * scale + pad,
                SortKey = sortKey,
                FeatureIndex = featureIndex,
                TileKey = tileKey,
                SymbolIndex = symbolIndex,
                AllowOverlap = allowOverlap,
                IgnorePlacement = ignorePlacement,
            };
        }

        /// <summary>
        /// The tight axis-aligned bound of ONE curved-symbol glyph — the AABB of the four ROTATED cell corners,
        /// so the collision box tracks the drawn glyph on a sloped line. Mirrors
        /// <see cref="BillboardMath.BuildQuad"/>: same <c>textSizePx / OneEm</c> scale, same CCW rotation about
        /// <paramref name="anchorScreenPx"/>, then grown by <paramref name="paddingPx"/> on every edge. Only
        /// <see cref="Min"/>/<see cref="Max"/> are meaningful — the sort/flag fields live on the owning
        /// <see cref="SymbolCandidate"/>, not the per-glyph box.
        ///
        /// <para><paramref name="cellSkirt"/> (<c>CurvedGlyph.CellSkirt</c>, baked px) is the transparent
        /// border baked into <paramref name="cell"/>, and is REMOVED before the corners are built, so the
        /// collision box bounds the icon's ink rather than its skirt. Text passes <c>0</c>, which makes every
        /// term below an exact <c>x - 0f</c> and the text path byte-identical.</para>
        /// </summary>
        public static SymbolBox BuildRotatedGlyph(
            in float2 anchorScreenPx,
            in SymbolQuad cell,
            float textSizePx,
            float rotationRadians,
            float paddingPx,
            float cellSkirt)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;
            float skirt = cellSkirt * scale;

            float2 tlLocal = cell.TopLeft * scale + new float2(skirt, -skirt);
            float2 brLocal = cell.BottomRight * scale - new float2(skirt, -skirt);
            float2 trLocal = new float2(brLocal.x, tlLocal.y);
            float2 blLocal = new float2(tlLocal.x, brLocal.y);

            math.sincos(rotationRadians, out float sin, out float cos);
            float2 tl = anchorScreenPx + Rotate(tlLocal, sin, cos);
            float2 tr = anchorScreenPx + Rotate(trLocal, sin, cos);
            float2 br = anchorScreenPx + Rotate(brLocal, sin, cos);
            float2 bl = anchorScreenPx + Rotate(blLocal, sin, cos);

            float2 min = math.min(math.min(tl, tr), math.min(br, bl)) - new float2(paddingPx, paddingPx);
            float2 max = math.max(math.max(tl, tr), math.max(br, bl)) + new float2(paddingPx, paddingPx);
            return new SymbolBox { Min = min, Max = max };
        }

        /// <summary>
        /// The map-pitched curved glyph's collision box: the screen AABB of its FOUR PROJECTED WORLD CORNERS. A
        /// map-pitched glyph is DRAWN as a world-metre quad lying in the ground plane at its anchor, so its
        /// screen size foreshortens with depth; <see cref="BuildRotatedGlyph"/> has no depth term at all and
        /// therefore over-reserves, without bound, as the symbol recedes. This builds the same frame the shader
        /// does, displaces the four corners in it, and projects each one.
        ///
        /// <para>Returns <c>false</c> — meaning take <see cref="BuildRotatedGlyph"/>'s screen box, not "fail" —
        /// when the ground frame is degenerate (zero/parallel
        /// <paramref name="surfaceUp"/>/<paramref name="tangentRender"/>) or any corner fails to project:
        /// behind the camera, or projecting past <see cref="SymbolScreenProjection.MaxProjectedPx"/> in a
        /// near-plane blow-up, which would otherwise make this AABB unbounded. <paramref name="box"/> is then
        /// untouched, so a half-built box is not expressible. <b>This is a KNOWING divergence from the
        /// shader</b>, whose own degenerate fallback is a camera-facing METRE frame
        /// (<c>SymbolWorldPitchAlign.hlsl</c>): in that case the box does not track the ink. Reproducing the
        /// camera-facing frame needs the view basis in render space and a fresh handedness derivation, to serve
        /// a state unreachable in production.</para>
        ///
        /// <para><b><paramref name="emScaleMetres"/> must be built as
        /// <c>TextSizePx · CandidateEmit.CornerMetresPerLogicalPixel</c></b> — the RENDERER's own association
        /// (<c>WorldSymbolRenderer</c> passes <c>q.TextSizePx * cornerScale</c> into
        /// <see cref="BillboardMath.BuildWorldQuad"/>). <c>SymbolStagingMath</c>'s already-computed
        /// <c>arcScale</c> is the mathematically equal but differently-associated
        /// <c>TextSizePx / OneEm · metresPerLogicalPixel</c>; substituting it would make the box agree with the
        /// quad only to a ULP instead of to the last bit, a silent loosening of the tests that compare them.</para>
        ///
        /// <para><b>The ŷ sense.</b> The shader displaces by
        /// <c>off.y · SYMBOL_WORLD_MAP_Y_SIGN · _ProjectionParams.x · cross(up, x̂)</c> with
        /// <c>off.y = −c_y</c> (the corner Y negation), so at the measured <c>_ProjectionParams.x == −1</c> the
        /// world displacement for a y-UP corner <c>c_y</c> is <c>c_y · cross(x̂, up)</c> — which is what this
        /// uses. <b>The CPU must not reproduce <c>_ProjectionParams.x</c>:</b> it cancels out of the DISPLAY
        /// sense of an ordinary projection (there is no flipped target here) but not out of the shader's WORLD
        /// displacement, so the shader's runtime read is load-bearing and this side picks the
        /// geometrically-correct sense. If the shader is ever wrong at <c>+1</c>, the box is right and the ink
        /// is wrong — a shader defect, not a box defect. <b>Only ONE test pins this sense</b>,
        /// <c>MapPitchedWorldArcStagingTests.MapPitched_ProjectedBox_PutsAPositiveCellYAboveTheAnchor</c>: the
        /// box is an AABB, and for a cell that is y-symmetric about its anchor a ŷ flip merely PERMUTES the
        /// corner set, which an AABB is invariant under, so that test needs an off-centre cell. It also
        /// re-derives the sense rather than importing it, so a SHARED convention error stays unobserved on the
        /// CPU side.</para>
        ///
        /// <para><paramref name="cellSkirt"/> is removed BEFORE the corners are built, as
        /// <see cref="BuildRotatedGlyph"/> does — the box bounds the icon's ink, not its transparent border.
        /// <paramref name="paddingPx"/> stays a SCREEN-pixel grow of the final AABB: <c>text-padding</c> is a
        /// screen-space property, and growing it in metres would make it depth-dependent.</para>
        ///
        /// <para>Only <see cref="Min"/>/<see cref="Max"/> are written — the sort/flag fields live on the
        /// owning <see cref="SymbolCandidate"/>, as with <see cref="BuildRotatedGlyph"/>.</para>
        /// </summary>
        /// <param name="cell">The glyph's baked-px cell, skirt included.</param>
        /// <param name="cellSkirt">The transparent border baked into <paramref name="cell"/>, baked px.</param>
        /// <param name="emScaleMetres">What ONE em is in WORLD METRES — see the note above on its association.</param>
        /// <param name="rotationRadians">The constant extra rotation the renderer applies to the corners
        /// (<c>CandidateEmit.ExtraRotationRadians</c>, i.e. <c>icon-rotate</c>). The along-line TANGENT is not
        /// included: it is already carried by <paramref name="tangentRender"/>, which builds the frame.</param>
        /// <param name="translateDeltaMetres">The renderer's <c>text-translate</c> delta in the corners' own
        /// unit (<c>CandidateEmit.TranslateDeltaPx · CornerMetresPerLogicalPixel</c>), y-UP.</param>
        /// <param name="anchorRender">The glyph's world anchor, render space (pre-RTC).</param>
        /// <param name="tangentRender">The glyph's unit world tangent, render space — already keep-upright
        /// negated by the caller, which is how the box inherits keep-upright for free.</param>
        /// <param name="surfaceUp">The unit surface normal at the anchor, render-space direction.</param>
        /// <param name="view">This frame's view transform. The caller must have checked
        /// <see cref="SymbolViewTransform.IsUsable"/>.</param>
        internal static bool TryBuildProjectedWorldGlyph(
            in SymbolQuad cell,
            float cellSkirt,
            float emScaleMetres,
            float rotationRadians,
            in float2 translateDeltaMetres,
            in double3 anchorRender,
            in double3 tangentRender,
            in float3 surfaceUp,
            in SymbolViewTransform view,
            float paddingPx,
            out SymbolBox box)
        {
            box = default;

            // The ground frame, mirroring SymbolWorldGroundFrame's guards in the SAME order with the SAME constants.
            // Guard BEFORE any normalize, both operands: a zero Up is what many test fixtures and
            // SymbolTileBlockBaker (null PathUpRender) write; a zero tangent is what StageCurvedAnchor produces on
            // a degenerate world chord.
            double3 up = new double3(surfaceUp.x, surfaceUp.y, surfaceUp.z);
            if (math.dot(up, up) < 0.5 || math.dot(tangentRender, tangentRender) < 0.5) return false;

            // A road pointing along the surface normal has no in-surface direction to be tangent to.
            double axial = math.dot(tangentRender, up);
            if (math.abs(axial) > 1.0 - 1e-3) return false;

            // Gram-Schmidt — keeps x̂ IN the surface. INERT on every Mercator fixture (up is (0,1,0) and every baked
            // road tangent is horizontal ⇒ axial == 0 exactly), like the shader's own copy of this projection.
            // Neither copy is exercised without a spherical curved-map fixture.
            double3 xh = math.normalize(tangentRender - up * axial);
            double3 yh = math.cross(xh, up); // see the ŷ-sense paragraph above

            // The skirt comes off FIRST, in BAKED units, so the one scale below is applied once and a skirted cell
            // is bit-identical to the same cell pre-shrunk by it. Text carries a 0 skirt, which makes both terms an
            // exact `x ± 0f`. Only the two corners are carried: the atlas UVs play no part in a collision box.
            var content = new SymbolQuad
            {
                TopLeft     = cell.TopLeft     + new float2(cellSkirt, -cellSkirt),
                BottomRight = cell.BottomRight - new float2(cellSkirt, -cellSkirt),
            };

            // The SAME four rotated y-UP corners BillboardMath.BuildWorldQuad emits — one expression, shared,
            // rather than two hand-maintained copies — plus the renderer's own translate, in the same frame.
            BillboardMath.QuadCornersLocal(in content, emScaleMetres, rotationRadians,
                out float2 tl, out float2 tr, out float2 br, out float2 bl);
            tl += translateDeltaMetres;
            tr += translateDeltaMetres;
            br += translateDeltaMetres;
            bl += translateDeltaMetres;

            if (!TryProjectCorner(tl, xh, yh, anchorRender, in view, out float2 pTl) ||
                !TryProjectCorner(tr, xh, yh, anchorRender, in view, out float2 pTr) ||
                !TryProjectCorner(br, xh, yh, anchorRender, in view, out float2 pBr) ||
                !TryProjectCorner(bl, xh, yh, anchorRender, in view, out float2 pBl))
                return false;

            float2 min = math.min(math.min(pTl, pTr), math.min(pBr, pBl)) - new float2(paddingPx, paddingPx);
            float2 max = math.max(math.max(pTl, pTr), math.max(pBr, pBl)) + new float2(paddingPx, paddingPx);
            box = new SymbolBox { Min = min, Max = max };
            return true;
        }

        // Displaces ONE y-UP corner in the ground frame at the anchor and projects it. The accumulation is in
        // double3 (render space is full-scale — the float narrow happens inside TryProjectPoint, after the
        // scene-origin subtract, as it does for every other projected symbol point).
        private static bool TryProjectCorner(
            in float2 cornerLocal, in double3 xh, in double3 yh, in double3 anchorRender,
            in SymbolViewTransform view, out float2 screenPx)
        {
            double3 cornerRender = anchorRender + xh * cornerLocal.x + yh * cornerLocal.y;
            if (!SymbolScreenProjection.TryProjectPoint(cornerRender, view.SceneOriginRender, view.ViewProj,
                    view.ViewportLogicalPx, view.Rebase, out screenPx, out _))
                return false;

            // Non-obvious why: a corner just IN FRONT of the camera plane has a tiny positive clip.w, which
            // survives the behind-camera test above and divides into an arbitrarily large (but finite, so no NaN
            // guard sees it) screen coordinate. Unbounded here means an unbounded collision AABB, unlike the
            // cell-bounded screen box. The same threshold bounds a path VERTEX one level up
            // (SymbolStagingMath.StageCurved); rejecting the corner takes the bounded screen-box fallback.
            return math.abs(screenPx.x) < SymbolScreenProjection.MaxProjectedPx
                && math.abs(screenPx.y) < SymbolScreenProjection.MaxProjectedPx;
        }

        // CCW rotation in a y-up frame — the SAME formula BillboardMath.Rotate uses (identity at angle 0), so
        // the collision box corners coincide with the drawn quad corners.
        private static float2 Rotate(in float2 p, float sin, float cos)
            => new float2(cos * p.x - sin * p.y, sin * p.x + cos * p.y);
    }
}
