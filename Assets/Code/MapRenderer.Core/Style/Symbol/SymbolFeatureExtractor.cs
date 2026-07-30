// Engine-free: no UnityEngine dependency. Mirrors StyledLineTileBuilder's select -> decode -> project
// pattern, but emits pre-shaping SymbolLabels instead of a Mesh.

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// S105 Slice 2 (A3) — extracts <see cref="SymbolLabel"/>s from a decoded MVT tile for one symbol style
    /// layer. Reuses the existing seams: <see cref="FeatureSelector.SelectFeatures"/> (source-layer resolve
    /// + filter), <see cref="MvtGeometry.Decode"/> (command stream → tile-local points), and
    /// <see cref="TileId.ToLonLat"/> → <see cref="IProjection.Project"/> (tile → geo → render space, PRE-RTC).
    /// Point AND LineString geometry (road-shields D2/D4: a LineString anchors at its mid arc-length under
    /// point placement, and at along-line anchors under a viewport-resolved line placement — see
    /// <see cref="AlignmentResolution"/>); Polygon is never accepted. One label per anchor (a MultiPoint
    /// feature emits one label per point; a viewport-resolved line emits one label per along-line anchor).
    /// Engine-free / clean-room — the shaping step is Unity-side (Slice 3).
    /// </summary>
    public static class SymbolFeatureExtractor
    {
        /// <summary>
        /// Append every extracted label of <paramref name="layer"/> over <paramref name="tile"/> to
        /// <paramref name="output"/>. Features whose <c>text-field</c> resolves to null/empty AND whose
        /// <c>icon-image</c> resolves to nothing are skipped; Polygon features are always ignored (road-shields
        /// D2's fence — LineString is now accepted, see the class doc). <paramref name="output"/> is
        /// caller-owned (cleared? no — appended, mirroring the tile-accumulation lifecycle in F5).
        /// </summary>
        /// <param name="layer">The symbol style layer (a non-symbol layer is a no-op).</param>
        /// <param name="tile">The decoded tile.</param>
        /// <param name="tileId">The tile's slippy address (drives tile→geo + the <c>TileKey</c> tiebreak).</param>
        /// <param name="zoom">Current zoom, for evaluating zoom-dependent text-size/sort-key/paint AND the
        /// build-zoom-evaluated <c>symbol-placement</c> (road-shields D1 — frozen for this tile's lifetime,
        /// never re-evaluated per frame).</param>
        /// <param name="projection">Geo → render-space projection.</param>
        /// <param name="output">Caller-owned list the extracted labels are appended to.</param>
        /// <param name="spriteAtlas">
        /// I3 — the sprite sheet <c>icon-image</c> resolves against; <c>null</c> (the default) yields NO icon
        /// labels regardless of the layer's <c>icon-*</c> properties, so every pre-I3 caller (which omits this
        /// argument) is byte-identical to before I3. Icons resolve under point placement, AND under line
        /// placement when <c>icon-rotation-alignment</c> resolves to <c>viewport</c> (road-shields D4 — the
        /// upright-at-anchor case); a MAP-aligned line icon (e.g. <c>road_one_way_arrow*</c>) still never
        /// emits even when an atlas is supplied (the surviving fence, pinned by T8).
        /// </param>
        public static void Extract(
            MapRenderer.Core.Style.StyleLayer layer,
            IDecodedTile                      tile,
            TileId                            tileId,
            double                            zoom,
            IProjection                       projection,
            List<SymbolLabel>                 output,
            SpriteAtlasView                   spriteAtlas = null)
        {
            if (!(layer is StyleLayer symbolLayer) || tile == null || projection == null || output == null)
                return;

            // NOTE: layer minzoom/maxzoom is deliberately NOT gated here. Tile DATA tops out at a max source zoom
            // (e.g. z14 for OpenFreeMap), so at higher camera zooms those tiles are OVERZOOMED (reused, not
            // rebuilt) — gating at build time would freeze layer visibility at the build zoom and hide layers
            // (poi_r1/r7/r20 @ minzoom 15/16/17) that MapLibre reveals as you zoom past the data level. The gate
            // therefore lives at DISPLAY time against the LIVE camera zoom (see StyleLayer.IsVisibleAtZoom, applied
            // per-frame in LabelPlacementSystem) so overzoomed data still turns layers on/off correctly.

            ITileLayer tileLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            if (tileLayer == null) return;
            double extent = tileLayer.Extent;

            IReadOnlyList<ITileFeature> features = FeatureSelector.SelectFeatures(layer, tile, zoom);
            long                        tileKey  = PackTileKey(tileId);
            int                         ordinal  = 0;

            LayoutProperties layout = symbolLayer.Layout;
            PaintProperties  paint  = symbolLayer.Paint;

            // text-translate is a constant px offset (not feature-dependent) — stamp it onto every label.
            // Stored y-down (as authored); the y-flip happens at placement.
            float2 translatePx = paint.Translate;

            // D1 (road-shields): symbol-placement is now expression-capable but evaluated ONCE here, at the
            // tile's build zoom — never per frame, never re-evaluated as the camera crosses a step boundary
            // (the accepted D1 known limit). TryEvaluate degrades to Point on a malformed/data-driven
            // expression rather than throwing (symbol-placement is never data-driven in a real style).
            SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(zoom, null, out SymbolPlacement evaluatedPlacement)
                ? evaluatedPlacement : SymbolPlacement.Point;
            bool isLine = placement != SymbolPlacement.Point;

            // D3/D4 (road-shields): resolve rotation-alignment against placement ONCE per layer (both are
            // plain parsed enums, not feature-dependent — MapLibre's alignment keys are never data-driven).
            // D4's reframe of G3/G4: under LINE placement, a label whose alignment resolves AWAY from Map is
            // NOT curved — MapLibre lays it out as an ordinary upright (viewport) block at each along-line
            // anchor, exactly the road-shield look. Map-aligned line labels (the pre-shields behaviour) are
            // untouched — this only lifts the icon fence / switches emit shape for the viewport-resolved case.
            AlignmentMode textAlign    = AlignmentResolution.Resolve(layout.TextRotationAlignment, placement);
            AlignmentMode iconAlign    = AlignmentResolution.Resolve(layout.IconRotationAlignment, placement);
            bool          textAtAnchors = isLine && textAlign != AlignmentMode.Map;
            bool          iconAtAnchors = isLine && iconAlign != AlignmentMode.Map;

            for (int f = 0; f < features.Count; f++)
            {
                ITileFeature feature = features[f];
                // D2 (road-shields): point placement now ALSO accepts a LineString feature (one anchor at
                // its mid arc-length, below) — the shields' "point" step branch runs over LineString road
                // geometry. Polygon stays unaccepted at every placement (the D2 fence).
                if (isLine
                        ? feature.GeometryType != TileGeometryType.LineString
                        : (feature.GeometryType != TileGeometryType.Point &&
                           feature.GeometryType != TileGeometryType.LineString))
                    continue;

                // A6: the feature IS an IFeature (the neutral carrier implements it directly) — no adapter alloc.
                // I3: text and icon are INDEPENDENT — a feature may resolve either, both, or neither. Only
                // when NEITHER resolves is the feature skipped (the pre-I3 "text==null -> skip" rule is the
                // isLine-or-null-atlas special case of this, so line/text-only-atlas behaviour is unchanged).
                string text = TextFieldResolver.Resolve(layout.TextField, feature);
                if (text != null)
                {
                    // text-transform (Slice B): case-fold the resolved label before it is shaped downstream.
                    text = layout.TextTransform.Apply(text);
                }

                // I3/D4: icons resolve under point placement OR a viewport-resolved line placement
                // (iconAtAnchors, below) — a map-aligned line stays fenced. Only resolved when the caller
                // supplied a sprite atlas — a null atlas (every pre-I3 caller) never produces icons.
                bool        hasIcon   = false;
                SpriteEntry iconEntry = default;
                // I6: hoisted to feature scope (was block-local + discarded) — the resolved sprite name is the
                // icon's cross-tile identity, needed at the icon-emit site below (SymbolLabel.IconImage).
                string iconImage = null;
                // G3+G4 (D4): the icon fence lifts for the viewport-resolved line case (upright-at-anchor
                // icons are the existing point-icon path at a different anchor) — the map-aligned line case
                // (road_one_way_arrow*) stays fenced; see the D4 doc above.
                if ((!isLine || iconAtAnchors) && spriteAtlas != null)
                {
                    iconImage = IconImageResolver.Resolve(layout.IconImage, feature);
                    if (iconImage != null)
                        hasIcon = spriteAtlas.Index.TryGetSprite(iconImage, out iconEntry);
                }

                if (text == null && !hasIcon) continue; // neither a text label nor an icon → nothing to emit

                // §10 D8/D9 (road-shields, road-shields-design.md): a centred icon+text pair (both at the
                // feature's anchor, no offset) is the PAIRING predicate — the two halves are stamped ONE
                // instance downstream (LabelPairing / StagePointPair), not the old D5 icon-owns-collision
                // approximation (the forced overlap flags below were D5's mechanism; D5 is retired — see §10).
                bool centredPair = hasIcon && text != null
                    && layout.TextAnchor == TextAnchor.Center
                    && layout.TextOffset.Equals(float2.zero)
                    && layout.TextRadialOffset.Evaluate(zoom, feature) == 0f
                    && layout.IconAnchor == TextAnchor.Center
                    && layout.IconOffset.Equals(float2.zero);

                // Per-feature evaluated style (zoom + feature — safe for constant/zoom/data-driven).
                float      textSize   = layout.TextSize.Evaluate(zoom, feature);
                float      padding    = layout.TextPadding.Evaluate(zoom, feature);
                float      sortKey    = layout.SymbolSortKey.Evaluate(zoom, feature);
                float      spacing    = math.max(1f, layout.SymbolSpacing.Evaluate(zoom, feature)); // px, >= 1 (spec)
                float      maxAngle   = layout.TextMaxAngle.Evaluate(zoom, feature);                // degrees (#6)
                LabelPaint labelPaint = EvaluatePaint(paint, zoom, feature);

                // I3: the icon quad/paint are feature-constant (icon-size/-padding/-opacity don't vary per
                // point within a MultiPoint feature) — build once here, stamp onto every point label below.
                SymbolQuad iconQuad    = default;
                LabelPaint iconPaint   = default;
                float      iconPadding = 0f;
                if (hasIcon)
                {
                    float iconSize = layout.IconSize.Evaluate(zoom, feature);
                    iconPadding = layout.IconPadding.Evaluate(zoom, feature);
                    float iconOpacity = paint.IconOpacity.Evaluate(zoom, feature);
                    iconQuad = IconQuadLayout.Layout(iconEntry, spriteAtlas.Size, iconSize, layout.IconAnchor,
                        layout.IconOffset);
                    iconPaint = new LabelPaint
                    {
                        TextColor   = new float4(1f, 1f, 1f, 1f),
                        Opacity     = iconOpacity,
                        HaloColor   = new float4(1f, 1f, 1f, 1f),
                        HaloWidthPx = 0f,
                        HaloBlurPx  = 0f,
                    };
                }

                List<List<double2>> paths = MvtGeometry.Decode(feature.Geometry);

                // D4/NIT: LayoutOptions is only built when a point-style text label can actually be emitted
                // (point placement, or a viewport-resolved line — the curved branch never uses it).
                // AnchorEmitContext itself is built INSIDE each branch below (review NIT 4) rather than once
                // here — the two branches' contexts differ in two fields (Text/HasIcon suppression under the
                // line branch's map-aligned fence) and building them separately removes both the line
                // branch's dead construction (a curved-only feature never reads a context at all) and the
                // silent-divergence hazard of two hand-maintained initializers that must agree.
                TextLayoutOptions layoutOptions = !isLine || textAtAnchors
                    ? TextLayoutOptionsBuilder.Build(layout, zoom, feature)
                    : default;

                if (isLine)
                {
                    for (int p = 0; p < paths.Count; p++)
                    {
                        List<double2> path = paths[p];
                        if (path.Count < 2) continue; // need at least one segment to place along
                        // A-2: anchors computed ONCE here in TILE space (zoom-invariant). symbol-spacing is px
                        // at the tile's on-screen size (512 logical px per tile at integer zoom), so px → tile
                        // units is `spacing · extent / TilePixelSize`. Projection-agnostic: uses only the layer
                        // extent + the 512 convention, no projection scale.
                        double spacingTileUnits = spacing * extent / WebMercator.TilePixelSize;

                        // S4: subdivide the tile-local path ONCE so ProjectPath/anchor-resolve and
                        // LineAnchorPlacement.Compute both index against the SAME finer sequence — never
                        // subdivide only one of the two, or LineAnchor.Segment silently desyncs from
                        // PathRender (docs/labels-and-symbols-design.md §4). On a flat projection
                        // (MaxRefineAngleRad == ∞, e.g. Mercator) this bypasses LineCurvatureSubdivision.Subdivide
                        // entirely and passes the ORIGINAL path straight through — the live Mercator
                        // byte-identity guarantee (zero-alloc, unchanged behaviour).
                        double                 maxRefineAngleRad = projection.MaxRefineAngleRad;
                        IReadOnlyList<double2> densePath;
                        if (double.IsPositiveInfinity(maxRefineAngleRad))
                        {
                            densePath = path;
                        }
                        else
                        {
                            // Per-vertex surface normal drives the split metric (the projected arc a segment
                            // subtends); re-derives geo per vertex, mirroring the mesh path's own re-projection
                            // of the subdivided points.
                            var ups = new double3[path.Count];
                            for (int i = 0; i < path.Count; i++)
                            {
                                double2 lonLat = tileId.ToLonLat(path[i].x, path[i].y, extent);
                                ups[i] = projection.ProjectPoint(
                                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x }).Up;
                            }

                            densePath = LineCurvatureSubdivision.Subdivide(path, ups, maxRefineAngleRad);
                        }

                        // D4/A-2: anchors computed once, shared by BOTH sub-branches below (today computed
                        // inline with identical arguments — byte-identical).
                        LineAnchor[] anchors = LineAnchorPlacement.Compute(densePath, spacingTileUnits, placement);

                        // Curved text — the pre-shields path, unchanged, but only when this label is NOT
                        // upright-at-anchors (map-aligned, or line-center's textAlign resolves Map by D3).
                        if (text != null && !textAtAnchors)
                        {
                            output.Add(new SymbolLabel
                            {
                                Placement       = placement,
                                PathRender      = ProjectPath(densePath, tileId, extent, projection),
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
                                Paint           = labelPaint,
                                TranslatePx     = translatePx,
                                TranslateAnchor = paint.TranslateAnchor,
                            });
                        }

                        // D4: upright-at-anchors — text and/or icon emitted as ordinary POINT labels at each
                        // along-line anchor (the road-shield look). Suppress whichever side didn't resolve to
                        // viewport (Map-aligned text/icon on the SAME feature keeps its OWN emit path/fence).
                        if (textAtAnchors || iconAtAnchors)
                        {
                            // Built here (not hoisted — NIT 4): the side that did NOT resolve to viewport is
                            // suppressed (the map-aligned fence, D4).
                            var anchorCtx = new AnchorEmitContext
                            {
                                Text = textAtAnchors ? text : null,
                                TextSizePx = textSize,
                                PaddingPx = padding,
                                LayoutOptions = layoutOptions,
                                Paint = labelPaint,
                                TextAllowOverlap = layout.TextAllowOverlap,
                                TextIgnorePlacement = layout.TextIgnorePlacement,
                                TextRotationAlignment = layout.TextRotationAlignment,
                                HasIcon = iconAtAnchors && hasIcon,
                                IconQuad = iconQuad,
                                IconImage = iconImage,
                                IconPaddingPx = iconPadding,
                                IconPaint = iconPaint,
                                IconAllowOverlap = layout.IconAllowOverlap,
                                IconIgnorePlacement = layout.IconIgnorePlacement,
                                IconRotationAlignment = layout.IconRotationAlignment,
                                SortKey = sortKey,
                                TranslatePx = translatePx,
                                TranslateAnchor = paint.TranslateAnchor,
                                CentredPair = centredPair,
                            };
                            for (int a = 0; a < anchors.Length; a++)
                            {
                                LineAnchor lineAnchor = anchors[a];
                                double2 tp = math.lerp(densePath[lineAnchor.Segment], densePath[lineAnchor.Segment + 1], lineAnchor.T);
                                EmitAtAnchor(tp, tileId, extent, projection, in anchorCtx, tileKey, ref ordinal, output);
                            }
                        }
                    }
                }
                else
                {
                    // Built ONCE per feature, read once per anchor by EmitAtAnchor (NIT 4 — was hoisted above
                    // both branches; moved here since only the point branch reads it).
                    var ctx = new AnchorEmitContext
                    {
                        Text = text,
                        TextSizePx = textSize,
                        PaddingPx = padding,
                        LayoutOptions = layoutOptions,
                        Paint = labelPaint,
                        TextAllowOverlap = layout.TextAllowOverlap,
                        TextIgnorePlacement = layout.TextIgnorePlacement,
                        TextRotationAlignment = layout.TextRotationAlignment,
                        HasIcon = hasIcon,
                        IconQuad = iconQuad,
                        IconImage = iconImage,
                        IconPaddingPx = iconPadding,
                        IconPaint = iconPaint,
                        IconAllowOverlap = layout.IconAllowOverlap,
                        IconIgnorePlacement = layout.IconIgnorePlacement,
                        IconRotationAlignment = layout.IconRotationAlignment,
                        SortKey = sortKey,
                        TranslatePx = translatePx,
                        TranslateAnchor = paint.TranslateAnchor,
                        CentredPair = centredPair,
                    };
                    for (int p = 0; p < paths.Count; p++)
                    {
                        List<double2> path = paths[p];
                        // D2 (road-shields): a Point feature anchors at every vertex (unchanged); a LineString
                        // feature under POINT placement anchors ONCE, at the path's mid arc-length — reusing
                        // LineAnchorPlacement.Compute(_, _, LineCenter), the same "middle of this tile-space
                        // path" topology the LINE branch above already computes. Anchors are resolved on the
                        // BUFFERED DECODED path (unclipped — the same input the curved branch uses); the
                        // existing [0, extent) single-world clip is then applied to the RESOLVED anchor point,
                        // unchanged (docs/road-shields-design.md §3 D2 — the clip contract).
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
                            EmitAtAnchor(anchorPoints[i], tileId, extent, projection, in ctx, tileKey, ref ordinal, output);
                    }
                }
            }
        }

        /// <summary>D4 (road-shields): the per-FEATURE values every anchor of that feature stamps onto its
        /// labels — evaluated once in <see cref="Extract"/>'s feature loop, read once per anchor by
        /// <see cref="EmitAtAnchor"/>. readonly struct + <c>in</c> per docs/conventions-short.md (bigger than
        /// ~16 bytes, read-only at the call site).</summary>
        private readonly struct AnchorEmitContext
        {
            // text side (Text == null ⇒ emit no text label)
            public string             Text { get; init; }
            public float              TextSizePx { get; init; }
            public float              PaddingPx { get; init; }
            public TextLayoutOptions  LayoutOptions { get; init; }
            public LabelPaint         Paint { get; init; }
            public bool               TextAllowOverlap { get; init; }
            public bool               TextIgnorePlacement { get; init; }
            public AlignmentMode      TextRotationAlignment { get; init; }
            // icon side (HasIcon == false ⇒ emit no icon label)
            public bool               HasIcon { get; init; }
            public SymbolQuad         IconQuad { get; init; }
            public string             IconImage { get; init; }
            public float              IconPaddingPx { get; init; }
            public LabelPaint         IconPaint { get; init; }
            public bool               IconAllowOverlap { get; init; }
            public bool               IconIgnorePlacement { get; init; }
            public AlignmentMode      IconRotationAlignment { get; init; }
            // shared
            public float              SortKey { get; init; }
            public float2             TranslatePx { get; init; }
            public TextTranslateAnchor TranslateAnchor { get; init; }
            /// <summary>§10 D8/D10: true when this feature's text+icon are a centred pair — the two halves
            /// are ONE placement instance. The icon is stamped <see cref="LabelPairRole.Owner"/> (emitted
            /// first) and the text <see cref="LabelPairRole.Rider"/>, sharing a <c>PairId</c>; whether the
            /// proposed pair actually holds is decided downstream by <see cref="Placement.LabelPairing"/>.
            /// Otherwise the pre-pairing order (text then icon) is unchanged and both halves carry
            /// <see cref="LabelPairRole.None"/>.</summary>
            public bool               CentredPair { get; init; }
        }

        /// <summary>D2+D4: resolves one tile-space anchor point to a label anchor and emits its text/icon
        /// labels per <paramref name="ctx"/> — the single emit site shared by point-placement anchors AND
        /// line-placement upright-at-anchor labels. Applies the single-world <c>[0, extent)</c> clip (D2) —
        /// an anchor outside the tile is a source world-copy/buffer duplicate, dropped so the owning tile
        /// emits it exactly once.</summary>
        private static void EmitAtAnchor(
            double2 tilePoint, TileId tileId, double extent, IProjection projection,
            in AnchorEmitContext ctx, long tileKey, ref int ordinal, List<SymbolLabel> output)
        {
            if (tilePoint.x < 0.0 || tilePoint.x >= extent || tilePoint.y < 0.0 || tilePoint.y >= extent) return;
            double2 lonLat = tileId.ToLonLat(tilePoint.x, tilePoint.y, extent);
            double3 anchor = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });

            if (ctx.CentredPair)
            {
                // §10 D10: the pair's PairId is the OWNER's (icon's) FeatureIndex, captured before either
                // emitter advances `ordinal`. CentredPair implies both HasIcon and Text != null (the
                // predicate that computed it), so both emitters below always run together here.
                int pairId = ordinal;
                if (ctx.HasIcon) EmitIconLabel(anchor, tileKey, ref ordinal, output, in ctx, LabelPairRole.Owner, pairId);
                if (ctx.Text != null) EmitTextLabel(anchor, tileKey, ref ordinal, output, in ctx, LabelPairRole.Rider, pairId);
            }
            else
            {
                if (ctx.Text != null) EmitTextLabel(anchor, tileKey, ref ordinal, output, in ctx, LabelPairRole.None, 0);
                if (ctx.HasIcon) EmitIconLabel(anchor, tileKey, ref ordinal, output, in ctx, LabelPairRole.None, 0);
            }
        }

        private static void EmitTextLabel(
            double3 anchor, long tileKey, ref int ordinal, List<SymbolLabel> output, in AnchorEmitContext ctx,
            LabelPairRole pairRole, int pairId)
        {
            output.Add(new SymbolLabel
            {
                AnchorRender      = anchor,
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
            });
        }

        private static void EmitIconLabel(
            double3 anchor, long tileKey, ref int ordinal, List<SymbolLabel> output, in AnchorEmitContext ctx,
            LabelPairRole pairRole, int pairId)
        {
            output.Add(new SymbolLabel
            {
                AnchorRender      = anchor,
                Placement         = SymbolPlacement.Point,
                Kind              = LabelKind.Icon,
                IconQuad          = ctx.IconQuad,
                IconImage         = ctx.IconImage,
                PaddingPx         = ctx.IconPaddingPx,
                SortKey           = ctx.SortKey,
                AllowOverlap      = ctx.IconAllowOverlap,
                IgnorePlacement   = ctx.IconIgnorePlacement,
                RotationAlignment = ctx.IconRotationAlignment,
                Paint             = ctx.IconPaint,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
                PairRole          = pairRole,
                PairId            = pairId,
            });
        }

        // Project a tile-local line string to render-space (PRE-RTC) vertices.
        private static double3[] ProjectPath(
            IReadOnlyList<double2> path, TileId tileId, double extent, IProjection projection)
        {
            var pts = new double3[path.Count];
            for (int i = 0; i < path.Count; i++)
            {
                double2 lonLat = tileId.ToLonLat(path[i].x, path[i].y, extent);
                pts[i] = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            }

            return pts;
        }

        private static LabelPaint EvaluatePaint(PaintProperties paint, double zoom, IFeature feature)
        {
            Color textColor = paint.Color.Evaluate(zoom, feature);
            Color haloColor = paint.HaloColor.Evaluate(zoom, feature);
            return new LabelPaint
            {
                TextColor   = ToFloat4(textColor),
                Opacity     = paint.Opacity.Evaluate(zoom, feature),
                HaloColor   = ToFloat4(haloColor),
                HaloWidthPx = paint.HaloWidth.Evaluate(zoom, feature),
                HaloBlurPx  = paint.HaloBlur.Evaluate(zoom, feature),
            };
        }

        private static float4 ToFloat4(in Color c)
        {
            return new float4((float)c.R, (float)c.G, (float)c.B, (float)c.A);
        }

        /// <summary>Packs a tile address into a stable, unique <c>long</c> (z in the high bits, then y, then
        /// x) — an opaque S20 tiebreak key, not a coordinate. Valid for z ≤ 19 (x,y &lt; 2^22).</summary>
        public static long PackTileKey(in TileId tile)
        {
            return ((long)tile.Z << 44) | ((long)(tile.Y & 0x3FFFFF) << 22) | (long)(tile.X & 0x3FFFFF);
        }

        /// <summary>Inverse of <see cref="PackTileKey"/>: unpacks a tile key back to its <see cref="TileId"/>
        /// (z/x/y). Used by the label tile-coverage pre-cull to recover a tile's corners from a batch record.</summary>
        public static TileId UnpackTileKey(long key)
        {
            return new TileId { Z = (int)(key >> 44), Y = (int)((key >> 22) & 0x3FFFFF), X = (int)(key & 0x3FFFFF) };
        }
    }
}