// Non-obvious why: this file reads Unity.Collections through TileGeometryBuffers, so it lives in
// MapRenderer.Unity and must not be added to Tools/core-tests.

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Extracts pre-shaping <see cref="SymbolFeature"/>s from a decoded tile for one symbol style layer, one
    /// per anchor. Accepts Point and LineString geometry, never Polygon; a map-resolved line icon emits one
    /// curved symbol per path. Non-local invariant: the layer's <see cref="TileGeometryBuffers"/> is BORROWED
    /// from the decoded tile and never disposed here, and it must stay unfiltered upstream — a 1-point path is
    /// a real symbol, so a shared short-ring filter would delete every Point-feature symbol.
    /// </summary>
    /// <remarks>Symbol has no area concept: it never schedules <c>FillMeshGraph</c> or its ring/earcut jobs,
    /// and never applies an area test to a path (a Point path has no area; a straight road has zero).</remarks>
    public static class SymbolFeatureExtractor
    {
        /// <summary>
        /// Append every extracted symbol of <paramref name="layer"/> over <paramref name="tile"/> to
        /// <paramref name="output"/>. Features whose <c>text-field</c> resolves to null/empty AND whose
        /// <c>icon-image</c> resolves to nothing are skipped; Polygon features are always ignored (LineString
        /// IS accepted — see the class doc). <paramref name="output"/> is appended to, never cleared.
        /// </summary>
        /// <param name="layer">The symbol style layer (a non-symbol layer is a no-op).</param>
        /// <param name="tile">The decoded tile.</param>
        /// <param name="tileId">IGNORED — do not add a reader. The address comes from the layer's own
        /// <c>ITileLayer.Geometry.Tile</c>; a second copy here could drift.</param>
        /// <param name="zoom">Build zoom for every zoom-dependent property, including <c>symbol-placement</c>,
        /// which stays frozen for the tile's lifetime.</param>
        /// <param name="projection">Geo → render-space projection.</param>
        /// <param name="output">Caller-owned list the extracted symbols are appended to.</param>
        /// <param name="spriteAtlas">The sheet <c>icon-image</c> resolves against, at every placement. <c>null</c>
        /// (the default) yields no icon symbols.</param>
        public static void Extract(
            MapRenderer.Core.Style.StyleLayer layer,
            IDecodedTile                      tile,
            TileId                            tileId,
            double                            zoom,
            IProjection                       projection,
            List<SymbolFeature>                 output,
            SpriteAtlasView                   spriteAtlas = null)
        {
            if (!(layer is MapRenderer.Core.Style.Symbol.StyleLayer symbolLayer) || tile == null || projection == null || output == null)
                return;

            // Layer minzoom/maxzoom is not gated here: overzoomed tiles are reused, not rebuilt, so the gate
            // runs at display time on the live camera zoom.

            // Fully qualified: a `using MapRenderer.Core.Style;` would make StyleLayer ambiguous.
            ITileLayer tileLayer = MapRenderer.Jobs.Tiles.SourceLayerResolver.ResolveTileLayer(layer, tile);
            if (tileLayer == null) return;

            // The buffer is the one authority for address and extent. The IsCreated guard makes reading
            // them safe: a `default` buffer carries neither, and has no rings to emit anyway.
            TileGeometryBuffers geometry = tileLayer.Geometry;
            if (!geometry.IsCreated) return;
            TileId tileAddress = geometry.Tile;
            double extent      = geometry.Extent;

            // The ordinal overload over the already-resolved layer: RingFeatureIdx names those ordinals, and
            // the selector appends in Features order, so selected order is decode order.
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(layer, tileLayer, zoom, selected);
            if (selected.Count == 0) return;
            long                        tileKey  = SymbolTileKey.Pack(tileAddress);
            int                         ordinal  = 0;

            LayoutProperties layout = symbolLayer.Layout;
            PaintProperties  paint  = symbolLayer.Paint;

            // text-translate is a constant px offset (not feature-dependent) — stamp it onto every symbol.
            // Stored y-down (as authored); the y-flip happens at placement.
            float2 translatePx = paint.Translate;

            // Limitation: symbol-placement is evaluated once, at build zoom, never per frame. TryEvaluate
            // degrades to Point on a malformed or data-driven expression instead of throwing.
            SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(zoom, null, out SymbolPlacement evaluatedPlacement)
                ? evaluatedPlacement : SymbolPlacement.Point;
            bool isLine = placement != SymbolPlacement.Point;

            // Once per layer: alignment is not data-driven. Under LINE placement, a symbol that does not
            // resolve to Map is an upright block at each along-line anchor, not curved.
            AlignmentMode textAlign    = AlignmentResolution.Resolve(layout.TextRotationAlignment, placement);
            AlignmentMode iconAlign    = AlignmentResolution.Resolve(layout.IconRotationAlignment, placement);
            // Pitch is stamped RESOLVED, unlike rotation: the curved staging arm reads it and has no
            // placement to resolve pitch `auto` against.
            AlignmentMode textPitch    = AlignmentResolution.ResolvePitch(
                layout.TextPitchAlignment, layout.TextRotationAlignment, placement);
            AlignmentMode iconPitch    = AlignmentResolution.ResolvePitch(
                layout.IconPitchAlignment, layout.IconRotationAlignment, placement);
            bool          textAtAnchors = isLine && textAlign != AlignmentMode.Map;
            bool          iconAtAnchors = isLine && iconAlign != AlignmentMode.Map;
            // The third icon mode. A MAP-resolved line icon rides the along-line anchors AND rotates to
            // the local line tangent — emitted as a one-glyph curved symbol (see EmitAlongLineIcon).
            bool          iconAlongLine = isLine && iconAlign == AlignmentMode.Map;

            // Per-feature arrays are sized to the whole LAYER (the buffer's own feature column) and indexed by
            // `SelectedTileFeature.Ordinal`, because RingFeatureIdx names layer ordinals, not selected indices.
            int layerFeatureCount = geometry.FeatureCount;

            // Non-local invariant: a stable counting sort by RingFeatureIdx keeps each feature's paths in decode
            // order, which the FeatureIndex tiebreak exposes. Persistent, not TempJob: an off-main extract can
            // outlive TempJob's 4-frame limit. The 2-arg ctor zeroes memory, which the `++` accumulation needs.
            // `cursor` stays separate because the feature loop reads ringStart again.
            using var ringStart = new NativeArray<int>(layerFeatureCount + 1, Allocator.Persistent);
            using var ringOrder = new NativeArray<int>(geometry.RingCount, Allocator.Persistent);
            using var cursor    = new NativeArray<int>(layerFeatureCount + 1, Allocator.Persistent);
            // A `using`-declared local rejects an index write (CS1654), so writes go through a
            // GetSubArray(0, Length) view over the same memory.
            NativeArray<int> ringStartWritable = ringStart.GetSubArray(0, ringStart.Length);
            NativeArray<int> ringOrderWritable = ringOrder.GetSubArray(0, ringOrder.Length);
            NativeArray<int> cursorWritable    = cursor.GetSubArray(0, cursor.Length);
            for (int r = 0; r < geometry.RingCount; r++) ringStartWritable[geometry.RingFeatureIdx[r] + 1]++;
            for (int i = 0; i < layerFeatureCount; i++) ringStartWritable[i + 1] += ringStartWritable[i];
            NativeArray<int>.Copy(ringStartWritable, cursorWritable);
            for (int r = 0; r < geometry.RingCount; r++) ringOrderWritable[cursorWritable[geometry.RingFeatureIdx[r]]++] = r;

            // Walks the selection in decode order and addresses the bucketed rings by each entry's layer
            // ordinal, so the emit sequence and `ordinal` follow decode order.
            for (int si = 0; si < selected.Count; si++)
            {
                int      f       = selected[si].Ordinal;
                IFeature feature = selected[si].Feature;
                // Point placement also accepts a LineString (one anchor at mid arc-length, below). Polygon is
                // never accepted.
                if (isLine
                        ? feature.GeometryType != TileGeometryType.LineString
                        : (feature.GeometryType != TileGeometryType.Point &&
                           feature.GeometryType != TileGeometryType.LineString))
                    continue;

                // Text and icon are independent: a feature is skipped only when neither resolves.
                string text = TextFieldResolver.Resolve(layout.TextField, feature);
                if (text != null)
                {
                    // text-transform: case-fold the resolved symbol before it is shaped downstream.
                    text = layout.TextTransform.Apply(text);
                }

                // Icons resolve at every placement, but only when the caller supplied a sprite atlas.
                bool        hasIcon   = false;
                SpriteEntry iconEntry = default;
                // At feature scope because the resolved sprite name is the icon's cross-tile identity,
                // needed at the icon-emit site below (SymbolFeature.IconImage).
                string iconImage = null;
                // No map-aligned fence here: `iconAlongLine` routes to EmitAlongLineIcon, which rotates the
                // icon to the projected tangent.
                if ((!isLine || iconAtAnchors || iconAlongLine) && spriteAtlas != null)
                {
                    iconImage = IconImageResolver.Resolve(layout.IconImage, feature);
                    if (iconImage != null)
                        hasIcon = spriteAtlas.Index.TryGetSprite(iconImage, out iconEntry);
                }

                if (text == null && !hasIcon) continue; // neither a text symbol nor an icon → nothing to emit

                // Pairing predicate: the feature resolved both a text and an icon — no anchor, offset or
                // optional conjuncts. See docs/labels-and-symbols-design.md § "Non-centred icon+text pairing (P-A)".
                bool pairedInstance = hasIcon && text != null;

                // Per-feature evaluated style (zoom + feature — safe for constant/zoom/data-driven).
                float      textSize   = layout.TextSize.Evaluate(zoom, feature);
                float      padding    = layout.TextPadding.Evaluate(zoom, feature);
                float      sortKey    = layout.SymbolSortKey.Evaluate(zoom, feature);
                float      spacing    = math.max(1f, layout.SymbolSpacing.Evaluate(zoom, feature)); // px, >= 1 (spec)
                float      maxAngle   = layout.TextMaxAngle.Evaluate(zoom, feature);                // degrees
                SymbolPaint symbolPaint = EvaluatePaint(paint, zoom, feature);

                // The icon quad/paint are feature-constant (icon-size/-padding/-opacity don't vary per
                // point within a MultiPoint feature) — build once here, stamp onto every point symbol below.
                SymbolQuad iconQuad    = default;
                SymbolPaint iconPaint   = default;
                float      iconPadding = 0f;
                float      iconRotateRadians = 0f;
                float      iconSkirtPx = 0f;
                if (hasIcon)
                {
                    float iconSize = layout.IconSize.Evaluate(zoom, feature);
                    iconPadding = layout.IconPadding.Evaluate(zoom, feature);
                    // Degrees to radians only; the clockwise-positive sense is kept. The single sign flip is
                    // in SymbolBearing.IconRotationRadians, below both icon emit shapes.
                    iconRotateRadians = math.radians(layout.IconRotate.Evaluate(zoom, feature));
                    float iconOpacity = paint.IconOpacity.Evaluate(zoom, feature);
                    iconQuad = IconQuadLayout.Layout(iconEntry, spriteAtlas.Size, iconSize, layout.IconAnchor,
                        layout.IconOffset);
                    // The border baked into iconQuad, carried alongside it: every consumer that needs the
                    // CONTENT box back (collision, placement) subtracts exactly this.
                    iconSkirtPx = IconQuadLayout.SkirtPx(iconEntry, iconSize);
                    iconPaint = new SymbolPaint
                    {
                        TextColor   = new float4(1f, 1f, 1f, 1f),
                        Opacity     = iconOpacity,
                        HaloColor   = new float4(1f, 1f, 1f, 1f),
                        HaloWidthPx = 0f,
                        HaloBlurPx  = 0f,
                    };
                }

                // This feature's decoded paths, as a span of the bucketed ring order (decode order preserved).
                int pathCount = ringStart[f + 1] - ringStart[f];

                // Built only when a point-style text symbol can emit; the curved branch never reads it. Each
                // branch builds its own AnchorEmitContext because the line branch suppresses Text/HasIcon.
                TextLayoutOptions layoutOptions = !isLine || textAtAnchors
                    ? TextLayoutOptionsBuilder.Build(layout, zoom, feature)
                    : default;

                if (isLine)
                {
                    // Per-feature values, read once per path below. Only a map-resolved line icon builds it.
                    AlongLineIconContext alongLineIconCtx = iconAlongLine && hasIcon
                        ? new AlongLineIconContext
                        {
                            IconQuad = iconQuad,
                            IconSkirtPx = iconSkirtPx,
                            IconImage = iconImage,
                            PaddingPx = iconPadding,
                            Paint = iconPaint,
                            AllowOverlap = layout.IconAllowOverlap,
                            IgnorePlacement = layout.IconIgnorePlacement,
                            RotationAlignment = layout.IconRotationAlignment,
                            PitchAlignment = iconPitch, // RESOLVED, unlike RotationAlignment above
                            SortKey = sortKey,
                            SpacingPx = spacing,
                            MaxAngleDeg = maxAngle,
                            IconRotateRadians = iconRotateRadians,
                        }
                        : default;

                    for (int p = 0; p < pathCount; p++)
                    {
                        IReadOnlyList<double2> path = CopyRing(geometry, ringOrder[ringStart[f] + p]);
                        if (path.Count < 2) continue; // need at least one segment to place along
                        // Anchors live in tile space (zoom-invariant): px → tile units is
                        // `spacing · extent / TilePixelSize`, with no projection scale.
                        double spacingTileUnits = spacing * extent / WebMercator.TilePixelSize;

                        // Subdivide once, so anchors and PathRender index one sequence (else LineAnchor.Segment
                        // desyncs). A flat projection (MaxRefineAngleRad == ∞) passes the path through.
                        double                 maxRefineAngleRad = projection.MaxRefineAngleRad;
                        IReadOnlyList<double2> densePath;
                        if (double.IsPositiveInfinity(maxRefineAngleRad))
                        {
                            densePath = path;
                        }
                        else
                        {
                            // The per-vertex surface normal drives the split metric (the arc a segment
                            // subtends).
                            var ups = new double3[path.Count];
                            for (int i = 0; i < path.Count; i++)
                            {
                                double2 lonLat = tileAddress.ToLonLat(path[i].x, path[i].y, extent);
                                ups[i] = projection.ProjectPoint(
                                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x }).Up;
                            }

                            densePath = LineCurvatureSubdivision.Subdivide(path, ups, maxRefineAngleRad);
                        }

                        // Anchors computed once, shared by BOTH sub-branches below.
                        LineAnchor[] anchors = LineAnchorPlacement.Compute(densePath, spacingTileUnits, placement);

                        // The point path's single-world clip, over the shared anchors: only the owning tile
                        // emits an anchor in the buffer strip.
                        anchors = KeepAnchorsInsideTile(anchors, densePath, extent);
                        if (anchors.Length == 0) continue; // every anchor belongs to a neighbour — nothing here

                        // Curved text — only when this symbol is NOT upright-at-anchors (map-aligned, or
                        // line-center's textAlign resolves Map).
                        if (text != null && !textAtAnchors)
                        {
                            double3[] textPathRender = ProjectPath(densePath, tileAddress, extent, projection, out double3[] textPathUps);
                            output.Add(new SymbolFeature
                            {
                                Placement       = placement,
                                PathRender      = textPathRender,
                                PathUpRender    = textPathUps,
                                LineAnchors     = anchors,
                                Text            = text,
                                TextSizePx      = textSize,
                                PaddingPx       = padding,
                                SortKey         = sortKey,
                                SpacingPx       = spacing,
                                MaxAngleDeg     = maxAngle,
                                KeepUpright     = layout.TextKeepUpright,
                                AllowOverlap    = layout.TextAllowOverlap,
                                IgnorePlacement = layout.TextIgnorePlacement,
                                FeatureIndex    = ordinal++,
                                TileKey         = tileKey,
                                Paint           = symbolPaint,
                                TranslatePx     = translatePx,
                                TranslateAnchor = paint.TranslateAnchor,
                                PitchAlignment  = textPitch, // RESOLVED — selects the world-metre arc walk
                            });
                        }

                        // A map-resolved line icon: a one-glyph curved symbol on the same anchors. Its own
                        // ProjectPath call keeps each symbol the sole owner of its path.
                        if (iconAlongLine && hasIcon)
                        {
                            double3[] iconPathRender = ProjectPath(densePath, tileAddress, extent, projection, out double3[] iconPathUps);
                            EmitAlongLineIcon(placement, iconPathRender, iconPathUps,
                                anchors, in alongLineIconCtx, tileKey, ref ordinal, output);
                        }

                        // Upright-at-anchors: POINT symbols at each along-line anchor. A side that did not
                        // resolve to viewport is suppressed; it keeps its own emit path.
                        if (textAtAnchors || iconAtAnchors)
                        {
                            // Built here, not hoisted: the side that did NOT resolve to viewport is
                            // suppressed by the map-aligned fence.
                            var anchorCtx = new AnchorEmitContext
                            {
                                Text = textAtAnchors ? text : null,
                                TextSizePx = textSize,
                                PaddingPx = padding,
                                LayoutOptions = layoutOptions,
                                Paint = symbolPaint,
                                TextAllowOverlap = layout.TextAllowOverlap,
                                TextIgnorePlacement = layout.TextIgnorePlacement,
                                TextRotationAlignment = layout.TextRotationAlignment,
                                HasIcon = iconAtAnchors && hasIcon,
                                IconQuad = iconQuad,
                                IconSkirtPx = iconSkirtPx,
                                IconImage = iconImage,
                                IconPaddingPx = iconPadding,
                                IconPaint = iconPaint,
                                IconAllowOverlap = layout.IconAllowOverlap,
                                IconIgnorePlacement = layout.IconIgnorePlacement,
                                IconRotationAlignment = layout.IconRotationAlignment,
                                IconRotateRadians = iconRotateRadians,
                                IconOptional = layout.IconOptional,
                                TextOptional = layout.TextOptional,
                                SortKey = sortKey,
                                TranslatePx = translatePx,
                                TranslateAnchor = paint.TranslateAnchor,
                                // Re-gated on both suppressions, so EmitAtAnchor never pairs a half that this
                                // branch dropped (a Rider with no Owner, or an Owner with no Rider).
                                PairedInstance = pairedInstance && iconAtAnchors && textAtAnchors,
                            };
                            for (int a = 0; a < anchors.Length; a++)
                            {
                                LineAnchor lineAnchor = anchors[a];
                                double2 tp = math.lerp(densePath[lineAnchor.Segment], densePath[lineAnchor.Segment + 1], lineAnchor.T);
                                EmitAtAnchor(tp, tileAddress, extent, projection, in anchorCtx, tileKey, ref ordinal, output);
                            }
                        }
                    }
                }
                else
                {
                    // Built ONCE per feature, read once per anchor by EmitAtAnchor. Built inside this
                    // branch because only the point branch reads it.
                    var ctx = new AnchorEmitContext
                    {
                        Text = text,
                        TextSizePx = textSize,
                        PaddingPx = padding,
                        LayoutOptions = layoutOptions,
                        Paint = symbolPaint,
                        TextAllowOverlap = layout.TextAllowOverlap,
                        TextIgnorePlacement = layout.TextIgnorePlacement,
                        TextRotationAlignment = layout.TextRotationAlignment,
                        HasIcon = hasIcon,
                        IconQuad = iconQuad,
                        IconSkirtPx = iconSkirtPx,
                        IconImage = iconImage,
                        IconPaddingPx = iconPadding,
                        IconPaint = iconPaint,
                        IconAllowOverlap = layout.IconAllowOverlap,
                        IconIgnorePlacement = layout.IconIgnorePlacement,
                        IconRotationAlignment = layout.IconRotationAlignment,
                        IconRotateRadians = iconRotateRadians,
                        IconOptional = layout.IconOptional,
                        TextOptional = layout.TextOptional,
                        SortKey = sortKey,
                        TranslatePx = translatePx,
                        TranslateAnchor = paint.TranslateAnchor,
                        PairedInstance = pairedInstance,
                    };
                    for (int p = 0; p < pathCount; p++)
                    {
                        IReadOnlyList<double2> path = CopyRing(geometry, ringOrder[ringStart[f] + p]);
                        // A Point anchors at every vertex; a LineString anchors once, at mid arc-length on the
                        // unclipped path. EmitAtAnchor then clips the resolved point to [0, extent).
                        IReadOnlyList<double2> anchorPoints;
                        if (feature.GeometryType == TileGeometryType.LineString)
                        {
                            LineAnchor[] midArc = LineAnchorPlacement.Compute(path, 0.0, SymbolPlacement.LineCenter);
                            if (midArc.Length == 0) continue; // degenerate (< 2 points / zero-length) — no anchor
                            LineAnchor a = midArc[0];
                            anchorPoints = new[] { math.lerp(path[a.Segment], path[a.Segment + 1], a.T) };
                        }
                        else
                        {
                            anchorPoints = path;
                        }

                        for (int i = 0; i < anchorPoints.Count; i++)
                            EmitAtAnchor(anchorPoints[i], tileAddress, extent, projection, in ctx, tileKey, ref ordinal, output);
                    }
                }
            }

            // `geometry` is borrowed and never disposed here (a double free); only the `using var` sort
            // buffers are. Pinned by SymbolExtractMintsNoBufferAndDisposesNone_ItBorrows.
        }

        /// <summary>Copies ring <paramref name="r"/>'s tile-local span (never geodetic, never projected) out of
        /// the shared buffer into a managed array, the shape every downstream path consumer reads.
        /// <paramref name="geometry"/> is by value, not <c>in</c>: <see cref="TileGeometryBuffers"/> is not a
        /// <c>readonly struct</c>, so <c>in</c> would force a defensive copy per field read.</summary>
        private static double2[] CopyRing(TileGeometryBuffers geometry, int r)
        {
            int start = geometry.RingOffsets[r];
            int n     = geometry.RingOffsets[r + 1] - start;
            var ring  = new double2[n];
            for (int k = 0; k < n; k++) ring[k] = geometry.Vertices[start + k];
            return ring;
        }

        /// <summary>The per-FEATURE values every anchor of that feature stamps onto its
        /// symbols — evaluated once in <see cref="Extract"/>'s feature loop, read once per anchor by
        /// <see cref="EmitAtAnchor"/>. readonly struct + <c>in</c> per docs/conventions-short.md (bigger than
        /// ~16 bytes, read-only at the call site).</summary>
        private readonly struct AnchorEmitContext
        {
            // text side (Text == null ⇒ emit no text symbol)
            public string             Text { get; init; }
            public float              TextSizePx { get; init; }
            public float              PaddingPx { get; init; }
            public TextLayoutOptions  LayoutOptions { get; init; }
            public SymbolPaint         Paint { get; init; }
            public bool               TextAllowOverlap { get; init; }
            public bool               TextIgnorePlacement { get; init; }
            public AlignmentMode      TextRotationAlignment { get; init; }
            // icon side (HasIcon == false ⇒ emit no icon symbol)
            public bool               HasIcon { get; init; }
            public SymbolQuad         IconQuad { get; init; }
            /// <summary>Baked-px transparent border inside <see cref="IconQuad"/>, per side.</summary>
            public float              IconSkirtPx { get; init; }
            public string             IconImage { get; init; }
            public float              IconPaddingPx { get; init; }
            public SymbolPaint         IconPaint { get; init; }
            public bool               IconAllowOverlap { get; init; }
            public bool               IconIgnorePlacement { get; init; }
            public AlignmentMode      IconRotationAlignment { get; init; }
            /// <summary><c>icon-rotate</c> in radians — stamped on the ICON half only; text is never
            /// rotated by it.</summary>
            public float              IconRotateRadians { get; init; }
            /// <summary><c>icon-optional</c> — stamped on the ICON half's
            /// <see cref="SymbolFeature.PairOptional"/>: the icon is the droppable one, so its text partner can
            /// place without it.</summary>
            public bool               IconOptional { get; init; }
            /// <summary><c>text-optional</c> — stamped on the TEXT half's
            /// <see cref="SymbolFeature.PairOptional"/>: the text is the droppable one, so its icon partner can
            /// place without it.</summary>
            public bool               TextOptional { get; init; }
            // shared
            public float              SortKey { get; init; }
            public float2             TranslatePx { get; init; }
            public TextTranslateAnchor TranslateAnchor { get; init; }
            /// <summary>True when both a text and an icon are emitted at this anchor as ONE placement instance:
            /// the icon is the <see cref="SymbolPairRole.Owner"/> (emitted first), the text the
            /// <see cref="SymbolPairRole.Rider"/>, sharing a <c>PairId</c>. <see cref="Placement.SymbolPairing"/>
            /// decides whether the pair holds. Otherwise text emits before icon, both
            /// <see cref="SymbolPairRole.None"/>.</summary>
            public bool               PairedInstance { get; init; }
        }

        /// <summary>The per-FEATURE icon values every along-line icon symbol of that feature stamps, read once
        /// per decoded path by <see cref="EmitAlongLineIcon"/>. Separate from <see cref="AnchorEmitContext"/>
        /// because it describes a different emit shape: a single curved glyph, not a point block's
        /// text+icon halves.</summary>
        private readonly struct AlongLineIconContext
        {
            public SymbolQuad    IconQuad { get; init; }
            /// <summary>Baked-px transparent border inside <see cref="IconQuad"/>, per side.</summary>
            public float         IconSkirtPx { get; init; }
            public string        IconImage { get; init; }
            public float         PaddingPx { get; init; }
            public SymbolPaint    Paint { get; init; }
            public bool          AllowOverlap { get; init; }
            public bool          IgnorePlacement { get; init; }
            public AlignmentMode RotationAlignment { get; init; }
            /// <summary>The RESOLVED <c>icon-pitch-alignment</c>, unlike
            /// <see cref="RotationAlignment"/> beside it (recorded as authored). Consumed: it selects the
            /// world-metre arc walk in the curved staging arm.</summary>
            public AlignmentMode PitchAlignment { get; init; }
            public float         SortKey { get; init; }
            public float         SpacingPx { get; init; }
            public float         MaxAngleDeg { get; init; }
            /// <summary><c>icon-rotate</c> in radians, composed on top of the along-line tangent.</summary>
            public float         IconRotateRadians { get; init; }
        }

        /// <summary>The along-line twin of <see cref="EmitAtAnchor"/>'s <c>[0, extent)</c> clip: keeps, in arc
        /// order, the anchors whose resolved tile-space point lies inside this tile. It filters anchors, never
        /// the path, because the arc walk needs vertices beyond them. Non-local invariant: the half-open,
        /// zero-margin bound makes adjacent tiles' anchors a partition; see
        /// docs/labels-and-symbols-design.md § "Known limits (accepted)".</summary>
        private static LineAnchor[] KeepAnchorsInsideTile(
            LineAnchor[] anchors, IReadOnlyList<double2> tilePath, double extent)
        {
            int kept = 0;
            for (int i = 0; i < anchors.Length; i++)
                if (IsAnchorInsideTile(anchors[i], tilePath, extent)) kept++;

            // The overwhelmingly common case: hand the input array straight back, so "unchanged" is literally
            // unchanged (zero alloc, same instance).
            if (kept == anchors.Length) return anchors;
            if (kept == 0) return System.Array.Empty<LineAnchor>();

            var inside = new LineAnchor[kept];
            int w = 0;
            for (int i = 0; i < anchors.Length; i++)
                if (IsAnchorInsideTile(anchors[i], tilePath, extent)) inside[w++] = anchors[i];
            return inside;
        }

        // The exact complement of EmitAtAnchor's early-return test — the same four comparisons in the same
        // order, so the line branch's clip and the point branch's cannot drift into an overlap.
        private static bool IsAnchorInsideTile(
            LineAnchor anchor, IReadOnlyList<double2> tilePath, double extent)
        {
            double2 p = math.lerp(tilePath[anchor.Segment], tilePath[anchor.Segment + 1], anchor.T);
            return p.x >= 0.0 && p.x < extent && p.y >= 0.0 && p.y < extent;
        }

        /// <summary>Emits ONE curved symbol for a decoded path whose single glyph is the icon quad, riding
        /// <paramref name="anchors"/> and rotated per frame to the line tangent. <c>KeepUpright</c> is false:
        /// a flipped one-way arrow points the wrong way. It is never paired: only
        /// <see cref="EmitAtAnchor"/> proposes a pair.</summary>
        private static void EmitAlongLineIcon(
            SymbolPlacement placement, double3[] pathRender, double3[] pathUpRender, LineAnchor[] anchors,
            in AlongLineIconContext ctx, long tileKey, ref int ordinal, List<SymbolFeature> output)
        {
            output.Add(new SymbolFeature
            {
                Placement         = placement,
                Kind              = SymbolKind.Icon,
                PathRender        = pathRender,
                PathUpRender      = pathUpRender,
                LineAnchors       = anchors,
                IconQuad          = ctx.IconQuad,
                IconSkirtPx       = ctx.IconSkirtPx,
                IconImage         = ctx.IconImage,
                PaddingPx         = ctx.PaddingPx,
                SortKey           = ctx.SortKey,
                SpacingPx         = ctx.SpacingPx,
                // Structurally inert at one glyph (the max-angle gate is `g > 0`-guarded), carried rather
                // than replaced by a sentinel so the field means the same thing on every curved symbol.
                MaxAngleDeg       = ctx.MaxAngleDeg,
                KeepUpright       = false,
                AllowOverlap      = ctx.AllowOverlap,
                IgnorePlacement   = ctx.IgnorePlacement,
                // Recorded, not consumed: the curved path orients by the line tangent, and the builder does
                // not forward this.
                RotationAlignment = ctx.RotationAlignment,
                // The pitch twin IS forwarded and IS read — it selects StageCurved's world arc walk.
                PitchAlignment    = ctx.PitchAlignment,
                IconRotateRadians = ctx.IconRotateRadians,
                Paint             = ctx.Paint,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
            });
        }

        /// <summary>Resolves one tile-space anchor point to a symbol anchor and emits its text/icon
        /// symbols per <paramref name="ctx"/> — the single emit site shared by point-placement anchors AND
        /// line-placement upright-at-anchor symbols. Applies the single-world <c>[0, extent)</c> clip —
        /// an anchor outside the tile is a source world-copy/buffer duplicate, dropped so the owning tile
        /// emits it exactly once.</summary>
        private static void EmitAtAnchor(
            double2 tilePoint, TileId tileId, double extent, IProjection projection,
            in AnchorEmitContext ctx, long tileKey, ref int ordinal, List<SymbolFeature> output)
        {
            if (tilePoint.x < 0.0 || tilePoint.x >= extent || tilePoint.y < 0.0 || tilePoint.y >= extent) return;
            double2 lonLat = tileId.ToLonLat(tilePoint.x, tilePoint.y, extent);
            ProjectedPoint pp = projection.ProjectPoint(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            double3 anchor = pp.World;

            if (ctx.PairedInstance)
            {
                // PairId is the icon owner's FeatureIndex, taken before `ordinal` advances. Every caller
                // re-gates PairedInstance on its suppressions, so both emitters below run.
                int pairId = ordinal;
                if (ctx.HasIcon) EmitIcon(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.Owner, pairId);
                if (ctx.Text != null) EmitText(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.Rider, pairId);
            }
            else
            {
                if (ctx.Text != null) EmitText(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.None, 0);
                if (ctx.HasIcon) EmitIcon(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.None, 0);
            }
        }

        private static void EmitText(
            double3 anchor, double3 up, long tileKey, ref int ordinal, List<SymbolFeature> output, in AnchorEmitContext ctx,
            SymbolPairRole pairRole, int pairId)
        {
            output.Add(new SymbolFeature
            {
                AnchorRender      = anchor,
                UpRender          = up,
                Placement         = SymbolPlacement.Point,
                Text              = ctx.Text,
                TextSizePx        = ctx.TextSizePx,
                PaddingPx         = ctx.PaddingPx,
                SortKey           = ctx.SortKey,
                AllowOverlap      = ctx.TextAllowOverlap,
                IgnorePlacement   = ctx.TextIgnorePlacement,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
                Paint             = ctx.Paint,
                LayoutOptions     = ctx.LayoutOptions,
                TranslatePx       = ctx.TranslatePx,
                TranslateAnchor   = ctx.TranslateAnchor,
                RotationAlignment = ctx.TextRotationAlignment,
                PairRole          = pairRole,
                PairId            = pairId,
                PairOptional      = ctx.TextOptional,
            });
        }

        private static void EmitIcon(
            double3 anchor, double3 up, long tileKey, ref int ordinal, List<SymbolFeature> output, in AnchorEmitContext ctx,
            SymbolPairRole pairRole, int pairId)
        {
            output.Add(new SymbolFeature
            {
                AnchorRender      = anchor,
                UpRender          = up,
                Placement         = SymbolPlacement.Point,
                Kind              = SymbolKind.Icon,
                IconQuad          = ctx.IconQuad,
                IconSkirtPx       = ctx.IconSkirtPx,
                IconImage         = ctx.IconImage,
                PaddingPx         = ctx.IconPaddingPx,
                SortKey           = ctx.SortKey,
                AllowOverlap      = ctx.IconAllowOverlap,
                IgnorePlacement   = ctx.IconIgnorePlacement,
                RotationAlignment = ctx.IconRotationAlignment,
                IconRotateRadians = ctx.IconRotateRadians,
                Paint             = ctx.IconPaint,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
                PairRole          = pairRole,
                PairId            = pairId,
                PairOptional      = ctx.IconOptional,
            });
        }

        // Project a tile-local line string to render-space (PRE-RTC) vertices, plus the index-parallel
        // unit surface normal at each vertex.
        private static double3[] ProjectPath(
            IReadOnlyList<double2> path, TileId tileId, double extent, IProjection projection, out double3[] ups)
        {
            var pts = new double3[path.Count];
            ups = new double3[path.Count];
            for (int i = 0; i < path.Count; i++)
            {
                double2 lonLat = tileId.ToLonLat(path[i].x, path[i].y, extent);
                ProjectedPoint pp = projection.ProjectPoint(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                pts[i] = pp.World;
                ups[i] = pp.Up;
            }

            return pts;
        }

        private static SymbolPaint EvaluatePaint(PaintProperties paint, double zoom, IFeature feature)
        {
            return new SymbolPaint
            {
                TextColor   = StreamRgba(paint.Color, zoom, feature),
                Opacity     = paint.Opacity.Evaluate(zoom, feature),
                HaloColor   = StreamRgba(paint.HaloColor, zoom, feature),
                HaloWidthPx = paint.HaloWidth.Evaluate(zoom, feature),
                HaloBlurPx  = paint.HaloBlur.Evaluate(zoom, feature),
            };
        }

        /// <summary>Evaluates one colour for the vertex COLOR stream — the complement of
        /// <c>SymbolRenderLayer.BindColorTint</c>: a CONSTANT rides the uniform, so the stream stays white
        /// and the shader's uniform x vertex product is the colour ONCE. Alpha is untouched either way, it
        /// has no uniform carrier.</summary>
        private static float4 StreamRgba(StyleProperty<Color> color, double zoom, IFeature feature)
        {
            float4 rgba = ToFloat4(color.Evaluate(zoom, feature));
            return SymbolTextColorCarrier.RidesUniform(color) ? new float4(1f, 1f, 1f, rgba.w) : rgba;
        }

        private static float4 ToFloat4(in Color c)
        {
            return new float4((float)c.R, (float)c.G, (float)c.B, (float)c.A);
        }
    }
}