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
    /// POINT geometry only (S20 is point-placement); one label per point (a MultiPoint feature emits one
    /// label per point). Engine-free / clean-room — the shaping step is Unity-side (Slice 3).
    /// </summary>
    public static class SymbolFeatureExtractor
    {
        /// <summary>
        /// Append every point label of <paramref name="layer"/> over <paramref name="tile"/> to
        /// <paramref name="output"/>. Features whose <c>text-field</c> resolves to null/empty are skipped;
        /// non-point features are ignored. <paramref name="output"/> is caller-owned (cleared? no — appended,
        /// mirroring the tile-accumulation lifecycle in F5).
        /// </summary>
        /// <param name="layer">The symbol style layer (a non-symbol layer is a no-op).</param>
        /// <param name="tile">The decoded tile.</param>
        /// <param name="tileId">The tile's slippy address (drives tile→geo + the <c>TileKey</c> tiebreak).</param>
        /// <param name="zoom">Current zoom, for evaluating zoom-dependent text-size/sort-key/paint.</param>
        /// <param name="projection">Geo → render-space projection.</param>
        /// <param name="output">Caller-owned list the extracted labels are appended to.</param>
        /// <param name="spriteAtlas">
        /// I3 — the sprite sheet <c>icon-image</c> resolves against; <c>null</c> (the default) yields NO icon
        /// labels regardless of the layer's <c>icon-*</c> properties, so every pre-I3 caller (which omits this
        /// argument) is byte-identical to before I3. Point placement only — a line-placement layer never emits
        /// icons even when an atlas is supplied.
        /// </param>
        public static void Extract(
            MapRenderer.Core.Style.StyleLayer layer,
            IDecodedTile tile,
            TileId tileId,
            double zoom,
            IProjection projection,
            List<SymbolLabel> output,
            SpriteAtlasView spriteAtlas = null)
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
            long tileKey = PackTileKey(tileId);
            int ordinal = 0;

            LayoutProperties layout = symbolLayer.Layout;
            PaintProperties paint = symbolLayer.Paint;

            // text-translate is a constant px offset (not feature-dependent) — stamp it onto every label.
            // Stored y-down (as authored); the y-flip happens at placement.
            float2 translatePx = paint.Translate;

            SymbolPlacement placement = layout.SymbolPlacement;
            bool isLine = placement != SymbolPlacement.Point;
            TileGeometryType wantGeometry = isLine ? TileGeometryType.LineString : TileGeometryType.Point;

            for (int f = 0; f < features.Count; f++)
            {
                ITileFeature feature = features[f];
                if (feature.GeometryType != wantGeometry) continue; // point layer skips lines and vice-versa

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

                // I3: icons are point-placement only (isLine skips them entirely) and only resolved when the
                // caller supplied a sprite atlas — a null atlas (every pre-I3 caller) never produces icons.
                bool hasIcon = false;
                SpriteEntry iconEntry = default;
                // I6: hoisted to feature scope (was block-local + discarded) — the resolved sprite name is the
                // icon's cross-tile identity, needed at the icon-emit site below (SymbolLabel.IconImage).
                string iconImage = null;
                if (!isLine && spriteAtlas != null)
                {
                    iconImage = IconImageResolver.Resolve(layout.IconImage, feature);
                    if (iconImage != null)
                        hasIcon = spriteAtlas.Index.TryGetSprite(iconImage, out iconEntry);
                }

                if (text == null && !hasIcon) continue; // neither a text label nor an icon → nothing to emit

                // Per-feature evaluated style (zoom + feature — safe for constant/zoom/data-driven).
                float textSize = layout.TextSize.Evaluate(zoom, feature);
                float padding  = layout.TextPadding.Evaluate(zoom, feature);
                float sortKey  = layout.SymbolSortKey.Evaluate(zoom, feature);
                float spacing  = math.max(1f, layout.SymbolSpacing.Evaluate(zoom, feature)); // px, >= 1 (spec)
                float maxAngle = layout.TextMaxAngle.Evaluate(zoom, feature);                // degrees (#6)
                LabelPaint labelPaint = EvaluatePaint(paint, zoom, feature);

                // I3: the icon quad/paint are feature-constant (icon-size/-padding/-opacity don't vary per
                // point within a MultiPoint feature) — build once here, stamp onto every point label below.
                SymbolQuad iconQuad = default;
                LabelPaint iconPaint = default;
                float iconPadding = 0f;
                if (hasIcon)
                {
                    float iconSize = layout.IconSize.Evaluate(zoom, feature);
                    iconPadding = layout.IconPadding.Evaluate(zoom, feature);
                    float iconOpacity = paint.IconOpacity.Evaluate(zoom, feature);
                    iconQuad = IconQuadLayout.Layout(iconEntry, spriteAtlas.Size, iconSize, layout.IconAnchor, layout.IconOffset);
                    iconPaint = new LabelPaint
                    {
                        TextColor = new float4(1f, 1f, 1f, 1f),
                        Opacity = iconOpacity,
                        HaloColor = new float4(1f, 1f, 1f, 1f),
                        HaloWidthPx = 0f,
                        HaloBlurPx = 0f,
                    };
                }

                List<List<double2>> paths = MvtGeometry.Decode(feature.Geometry);

                if (isLine)
                {
                    // One curved label per line string (#5). Orientation comes from the projected line tangent,
                    // so the point-layout options (anchor/justify/offset) and rotation-alignment don't apply.
                    for (int p = 0; p < paths.Count; p++)
                    {
                        List<double2> path = paths[p];
                        if (path.Count < 2) continue; // need at least one segment to place along
                        // A-2: anchors computed ONCE here in TILE space (zoom-invariant). symbol-spacing is px
                        // at the tile's on-screen size (512 logical px per tile at integer zoom), so px → tile
                        // units is `spacing · extent / TilePixelSize`. Projection-agnostic: uses only the layer
                        // extent + the 512 convention, no projection scale.
                        double spacingTileUnits = spacing * extent / WebMercator.TilePixelSize;

                        // S4: subdivide the tile-local path ONCE so ProjectPath and LineAnchorPlacement.Compute
                        // both index against the SAME finer sequence — never subdivide only one of the two, or
                        // LineAnchor.Segment silently desyncs from PathRender (docs/labels-and-symbols-design.md
                        // §4). On a flat projection (MaxRefineAngleRad == ∞, e.g. Mercator) this bypasses
                        // LineCurvatureSubdivision.Subdivide entirely and passes the ORIGINAL path straight
                        // through — the live Mercator byte-identity guarantee (zero-alloc, unchanged behaviour).
                        double maxRefineAngleRad = projection.MaxRefineAngleRad;
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

                        output.Add(new SymbolLabel
                        {
                            Placement = placement,
                            PathRender = ProjectPath(densePath, tileId, extent, projection),
                            LineAnchors = LineAnchorPlacement.Compute(densePath, spacingTileUnits, placement),
                            Text = text,
                            TextSizePx = textSize,
                            PaddingPx = padding,
                            SortKey = sortKey,
                            SpacingPx = spacing,
                            MaxAngleDeg = maxAngle,
                            KeepUpright = layout.TextKeepUpright,
                            AllowOverlap = layout.TextAllowOverlap,
                            IgnorePlacement = layout.TextIgnorePlacement,
                            FeatureIndex = ordinal++,
                            TileKey = tileKey,
                            Paint = labelPaint,
                            TranslatePx = translatePx,
                            TranslateAnchor = paint.TranslateAnchor,
                        });
                    }
                }
                else
                {
                    // Layout options are per-feature (zoom + feature evaluated), constant across the feature's
                    // points — build once here, stamp onto every point label below.
                    TextLayoutOptions layoutOptions = TextLayoutOptionsBuilder.Build(layout, zoom, feature);
                    for (int p = 0; p < paths.Count; p++)
                    {
                        List<double2> path = paths[p];
                        for (int i = 0; i < path.Count; i++)
                        {
                            double2 tp = path[i];
                            // Single-world clip: a point anchor outside this tile's [0, extent) bounds is a
                            // source world-copy / buffer duplicate (low-zoom tiles carry ±360° label copies).
                            // Drop it — the tile that owns the anchor emits it exactly once. (The mesh path
                            // is clipped by the source; symbols were not, which is why labels repeated ±360°
                            // while fill/line stayed single.)
                            if (tp.x < 0.0 || tp.x >= extent || tp.y < 0.0 || tp.y >= extent) continue;
                            double2 lonLat = tileId.ToLonLat(tp.x, tp.y, extent);
                            double3 anchor = projection.Project(
                                new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });

                            // I3: text and icon are independent labels over the SAME anchor — text first,
                            // then icon, so a feature with both emits two labels in a stable order.
                            if (text != null)
                            {
                                output.Add(new SymbolLabel
                                {
                                    AnchorRender = anchor,
                                    Placement = SymbolPlacement.Point,
                                    Text = text,
                                    TextSizePx = textSize,
                                    PaddingPx = padding,
                                    SortKey = sortKey,
                                    AllowOverlap = layout.TextAllowOverlap,
                                    IgnorePlacement = layout.TextIgnorePlacement,
                                    FeatureIndex = ordinal++,
                                    TileKey = tileKey,
                                    Paint = labelPaint,
                                    LayoutOptions = layoutOptions,
                                    TranslatePx = translatePx,
                                    TranslateAnchor = paint.TranslateAnchor,
                                    RotationAlignment = layout.TextRotationAlignment,
                                });
                            }

                            if (hasIcon)
                            {
                                output.Add(new SymbolLabel
                                {
                                    AnchorRender = anchor,
                                    Placement = SymbolPlacement.Point,
                                    Kind = LabelKind.Icon,
                                    IconQuad = iconQuad,
                                    IconImage = iconImage,
                                    PaddingPx = iconPadding,
                                    SortKey = sortKey,
                                    AllowOverlap = layout.IconAllowOverlap,
                                    IgnorePlacement = layout.IconIgnorePlacement,
                                    RotationAlignment = layout.IconRotationAlignment,
                                    Paint = iconPaint,
                                    FeatureIndex = ordinal++,
                                    TileKey = tileKey,
                                });
                            }
                        }
                    }
                }
            }
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
                TextColor = ToFloat4(textColor),
                Opacity = paint.Opacity.Evaluate(zoom, feature),
                HaloColor = ToFloat4(haloColor),
                HaloWidthPx = paint.HaloWidth.Evaluate(zoom, feature),
                HaloBlurPx = paint.HaloBlur.Evaluate(zoom, feature),
            };
        }

        private static float4 ToFloat4(in Color c) => new float4((float)c.R, (float)c.G, (float)c.B, (float)c.A);

        /// <summary>Packs a tile address into a stable, unique <c>long</c> (z in the high bits, then y, then
        /// x) — an opaque S20 tiebreak key, not a coordinate. Valid for z ≤ 19 (x,y &lt; 2^22).</summary>
        public static long PackTileKey(in TileId tile)
            => ((long)tile.Z << 44) | ((long)(tile.Y & 0x3FFFFF) << 22) | (long)(tile.X & 0x3FFFFF);

        /// <summary>Inverse of <see cref="PackTileKey"/>: unpacks a tile key back to its <see cref="TileId"/>
        /// (z/x/y). Used by the label tile-coverage pre-cull to recover a tile's corners from a batch record.</summary>
        public static TileId UnpackTileKey(long key)
            => new TileId { Z = (int)(key >> 44), Y = (int)((key >> 22) & 0x3FFFFF), X = (int)(key & 0x3FFFFF) };
    }
}
