// Engine-free: no UnityEngine dependency. Mirrors StyledLineTileBuilder's select -> decode -> project
// pattern (docs/mesh-pipeline.md), but emits pre-shaping SymbolLabels instead of a Mesh.

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

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
        /// <param name="tile">The decoded MVT tile.</param>
        /// <param name="tileId">The tile's slippy address (drives tile→geo + the <c>TileKey</c> tiebreak).</param>
        /// <param name="zoom">Current zoom, for evaluating zoom-dependent text-size/sort-key/paint.</param>
        /// <param name="projection">Geo → render-space projection.</param>
        /// <param name="output">Caller-owned list the extracted labels are appended to.</param>
        public static void Extract(
            MapRenderer.Core.Style.StyleLayer layer,
            MvtTile tile,
            TileId tileId,
            double zoom,
            IProjection projection,
            List<SymbolLabel> output)
        {
            if (!(layer is StyleLayer symbolLayer) || tile == null || projection == null || output == null)
                return;

            MvtLayer mvtLayer = SourceLayerResolver.ResolveMvtLayer(layer, tile);
            if (mvtLayer == null) return;
            double extent = mvtLayer.Extent;

            IReadOnlyList<MvtFeature> features = FeatureSelector.SelectFeatures(layer, tile, zoom);
            long tileKey = PackTileKey(tileId);
            int ordinal = 0;

            LayoutProperties layout = symbolLayer.Layout;
            PaintProperties paint = symbolLayer.Paint;

            // text-translate is a constant px offset (not feature-dependent) — stamp it onto every label.
            // Stored y-down (as authored); the y-flip happens at placement.
            float2 translatePx = paint.Translate;

            for (int f = 0; f < features.Count; f++)
            {
                MvtFeature feature = features[f];
                if (feature.GeometryType != MvtGeometryType.Point) continue;

                var adapted = new MvtFeatureAdapter(feature);
                string text = TextFieldResolver.Resolve(layout.TextField, adapted);
                if (text == null) continue; // absent/empty text-field → no label
                // text-transform (Slice B): case-fold the resolved label before it is shaped downstream.
                text = layout.TextTransform.Apply(text);

                // Per-feature evaluated style (zoom + feature — safe for constant/zoom/data-driven).
                float textSize = layout.TextSize.Evaluate(zoom, adapted);
                float padding  = layout.TextPadding.Evaluate(zoom, adapted);
                float sortKey  = layout.SymbolSortKey.Evaluate(zoom, adapted);
                LabelPaint labelPaint = EvaluatePaint(paint, zoom, adapted);
                // Layout options are per-feature (zoom + feature evaluated), constant across the feature's
                // points — build once here, stamp onto every point label below.
                TextLayoutOptions layoutOptions = TextLayoutOptionsBuilder.Build(layout, zoom, adapted);

                List<List<double2>> paths = MvtGeometry.Decode(feature.Geometry);
                for (int p = 0; p < paths.Count; p++)
                {
                    List<double2> path = paths[p];
                    for (int i = 0; i < path.Count; i++)
                    {
                        double2 tp = path[i];
                        double2 lonLat = tileId.ToLonLat(tp.x, tp.y, extent);
                        double3 anchor = projection.Project(
                            new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });

                        output.Add(new SymbolLabel
                        {
                            AnchorRender = anchor,
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
                }
            }
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
    }
}
