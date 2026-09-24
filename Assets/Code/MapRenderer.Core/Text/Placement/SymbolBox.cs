// TOP-LEVEL `using Unity.Mathematics;` (see the namespace-collision trap in GlyphAtlasTexture.cs).
// BLITTABLE: the Burst CollisionJob takes it as a NativeArray<SymbolBox> element.

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
        /// The AABB of ONE curved-symbol glyph's four ROTATED cell corners, so the box tracks the glyph on a
        /// sloped line: <see cref="BillboardMath.BuildQuad"/>'s scale and CCW rotation about
        /// <paramref name="anchorScreenPx"/>, grown by <paramref name="paddingPx"/>. <paramref name="cellSkirt"/>
        /// is removed first, so the box bounds icon ink; text passes 0 and stays byte-identical. Only
        /// <see cref="Min"/>/<see cref="Max"/> are meaningful.
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
        /// The map-pitched curved glyph's collision box: the screen AABB of its four projected world corners in the
        /// shader's ground frame, because <see cref="BuildRotatedGlyph"/> has no depth term and over-reserves as
        /// the symbol recedes. Returns <c>false</c> (use <see cref="BuildRotatedGlyph"/>) for a degenerate frame or
        /// a corner that fails to project or passes <see cref="SymbolScreenProjection.MaxProjectedPx"/>;
        /// <paramref name="box"/> is then untouched. Limitation: it does not reproduce the shader's camera-facing
        /// degenerate fallback, a state unreachable in production. Non-local invariant: the ŷ sense is
        /// <c>c_y · cross(x̂, up)</c>, the shader's result at <c>_ProjectionParams.x == −1</c>, and only
        /// <c>MapPitchedWorldArcStagingTests.MapPitched_ProjectedBox_PutsAPositiveCellYAboveTheAnchor</c> pins it.
        /// The skirt is removed first; <paramref name="paddingPx"/> grows the final AABB in screen px.
        /// </summary>
        /// <param name="cell">The glyph's baked-px cell, skirt included.</param>
        /// <param name="cellSkirt">The transparent border baked into <paramref name="cell"/>, baked px.</param>
        /// <param name="emScaleMetres">One em in WORLD METRES, built as <c>TextSizePx · CornerMetresPerLogicalPixel</c>
        /// like the renderer, so the box matches the drawn quad to the bit.</param>
        /// <param name="rotationRadians">The renderer's constant corner rotation (<c>icon-rotate</c>); the
        /// tangent is already in <paramref name="tangentRender"/>.</param>
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

            // SymbolWorldGroundFrame's guards, same order and constants, before any normalize: fixtures and the
            // baker write a zero Up, and a degenerate world chord gives a zero tangent.
            double3 up = new double3(surfaceUp.x, surfaceUp.y, surfaceUp.z);
            if (math.dot(up, up) < 0.5 || math.dot(tangentRender, tangentRender) < 0.5) return false;

            // A road pointing along the surface normal has no in-surface direction to be tangent to.
            double axial = math.dot(tangentRender, up);
            if (math.abs(axial) > 1.0 - 1e-3) return false;

            // Gram-Schmidt keeps x̂ in the surface. Inert on Mercator (axial == 0); only a spherical curved-map
            // fixture would exercise it, here or in the shader.
            double3 xh = math.normalize(tangentRender - up * axial);
            double3 yh = math.cross(xh, up); // see the ŷ-sense paragraph above

            // The skirt comes off first, in baked units, so a skirted cell is bit-identical to the pre-shrunk
            // one. Only the two corners are carried; UVs play no part in a collision box.
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

        // Displaces ONE y-UP corner in the ground frame at the anchor and projects it, in double3; the float
        // narrow happens inside TryProjectPoint, after the scene-origin subtract.
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
