using System.Collections.Generic;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// Parses a MapLibre Style document into the typed <see cref="StyleDocument"/> model. Clean-room:
    /// the schema, field names, and defaults come from the PUBLIC MapLibre Style Spec docs only — not
    /// from MapLibre's source.
    ///
    /// Forward-compat: unknown root, source, layer, and paint/layout keys never throw — they are
    /// preserved on the corresponding <c>Raw</c> JSON. Malformed JSON throws
    /// <see cref="JsonParseException"/> from <see cref="JsonParser"/>; a structurally surprising but
    /// valid-JSON document (e.g. an unexpected type for a field) is tolerated by falling back to a
    /// default rather than throwing.
    ///
    /// <para><b>Exception:</b> a paint/layout key that parses eagerly through the expression engine (UMR-108
    /// — every typed <c>Paint</c>/<c>Layout</c> view parses at construction, not lazily on first access) can
    /// throw <see cref="MapRenderer.Core.Expressions.ExpressionParseException"/> when its VALUE is a malformed
    /// expression (bad interpolate/step stops, wrong arity, unknown operator) — that is not the "unexpected
    /// type for a field" case above, and THIS method does not catch it. <c>MapView.SetStyle(string,
    /// CancellationToken)</c> is the one in-repo call site and guards its own call. A caller that invokes
    /// <see cref="Parse(string, bool)"/>/<see cref="Parse(JsonValue, bool)"/> directly — including
    /// <c>MapView.SetStyle(StyleDocument, string, CancellationToken)</c>, which takes an ALREADY-parsed
    /// document and never reaches that guard — must handle this exception itself.</para>
    /// </summary>
    public static class StyleParser
    {
        // ---- vector source spec defaults (public MapLibre Style Spec "Sources" page) --------------
        public const string DefaultScheme = "xyz";
        public const int DefaultSourceMinZoom = 0;
        public const int DefaultSourceMaxZoom = 22;
        public static readonly double[] DefaultBounds = { -180.0, -85.051129, 180.0, 85.051129 };

        /// <summary>Parse from a JSON string.</summary>
        /// <param name="fillAntialiasDefault">What <c>fill-antialias</c> means for a fill layer that omits
        /// it (<c>MapViewConfig.FillAntialiasing</c>). The Style Spec's own default is <c>true</c>.</param>
        public static StyleDocument Parse(string json, bool fillAntialiasDefault = true)
            => Parse(JsonParser.Parse(json), fillAntialiasDefault);

        /// <summary>Parse from an already-parsed JSON DOM root.</summary>
        public static StyleDocument Parse(JsonValue root, bool fillAntialiasDefault = true)
        {
            var doc = new StyleDocument { Root = root };
            if (root == null || !root.IsObject)
                return doc; // tolerate: empty document rather than throw

            doc.Version = root.GetInt("version", 0);
            doc.Name = root.GetString("name");
            doc.Sprite = root.GetString("sprite");
            doc.Glyphs = root.GetString("glyphs");

            if (root.TryGet("sources", out var sources) && sources.IsObject)
            {
                foreach (var kv in sources.Members)
                    doc.Sources[kv.Key] = ParseSource(kv.Value);
            }

            if (root.TryGet("layers", out var layers) && layers.IsArray)
            {
                foreach (var layerJson in layers.Items)
                    doc.Layers.Add(ParseLayer(layerJson, fillAntialiasDefault));
            }

            return doc;
        }

        private static SourceDefinition ParseSource(JsonValue json)
        {
            var src = new SourceDefinition { Raw = json };
            if (json == null || !json.IsObject)
                return src;

            src.RawType = json.GetString("type");
            src.Type = ParseSourceType(src.RawType);

            src.Url = json.GetString("url");
            src.Tiles = ParseStringArray(json.Get("tiles"));
            // geojson `data`: retained verbatim, because the spec allows an inline object OR a URL string.
            src.Data = json.Get("data");

            // Vector-source defaults. (Other source types carry these keys too; applying the vector
            // defaults is harmless for them and the raw object is always retained for later stages.)
            src.MinZoom = json.GetInt("minzoom", DefaultSourceMinZoom);
            src.MaxZoom = json.GetInt("maxzoom", DefaultSourceMaxZoom);
            src.Scheme = json.GetString("scheme", DefaultScheme);
            src.Bounds = ParseDoubleArray(json.Get("bounds")) ?? (double[])DefaultBounds.Clone();

            return src;
        }

        private static SourceType ParseSourceType(string type)
        {
            switch (type)
            {
                case "vector": return SourceType.Vector;
                case "raster": return SourceType.Raster;
                case "raster-dem": return SourceType.RasterDem;
                case "geojson": return SourceType.GeoJson;
                case "image": return SourceType.Image;
                case "video": return SourceType.Video;
                default: return SourceType.Unknown;
            }
        }

        private static StyleLayer ParseLayer(JsonValue json, bool fillAntialiasDefault)
        {
            if (json == null || !json.IsObject)
                return new StyleLayer { Raw = json };

            string rawType = json.GetString("type");
            StyleLayerType layerType = StyleLayerTypeExtensions.ParseLayerType(rawType);

            JsonValue layoutJson = json.Get("layout");
            JsonValue paintJson  = json.Get("paint");

            // Factory: line/fill/symbol/background/fill-extrusion get their typed subclass, with its typed
            // Paint/Layout parsed here, eagerly; every other type uses the generic base. Raw retains the
            // original object for forward-compat.
            StyleLayer layer;
            switch (layerType)
            {
                case StyleLayerType.Line:
                    layer = new Line.StyleLayer
                    {
                        Paint  = Line.PaintProperties.Parse(paintJson),
                        Layout = Line.LayoutProperties.Parse(layoutJson),
                    };
                    break;
                case StyleLayerType.Fill:
                    layer = new Fill.StyleLayer
                    {
                        Paint  = Fill.PaintProperties.Parse(paintJson, fillAntialiasDefault),
                        Layout = Fill.LayoutProperties.Parse(layoutJson),
                    };
                    break;
                case StyleLayerType.Symbol:
                    layer = new Symbol.StyleLayer
                    {
                        Paint  = Symbol.PaintProperties.Parse(paintJson),
                        Layout = Symbol.LayoutProperties.Parse(layoutJson),
                    };
                    break;
                case StyleLayerType.Background:
                    layer = new Background.StyleLayer
                    {
                        Paint = Background.PaintProperties.Parse(paintJson),
                    };
                    break;
                case StyleLayerType.FillExtrusion:
                    layer = new FillExtrusion.StyleLayer
                    {
                        Paint = FillExtrusion.PaintProperties.Parse(paintJson),
                    };
                    break;
                default:
                    layer = new StyleLayer();
                    break;
            }

            layer.Raw = json;
            layer.Id = json.GetString("id");
            layer.RawType = rawType;
            layer.LayerType = layerType;
            layer.Source = json.GetString("source");
            layer.SourceLayer = json.GetString("source-layer");

            // Layer minzoom/maxzoom have NO spec default (Optional number in [0,24]); absent = null.
            layer.MinZoom = json.GetNullableDouble("minzoom");
            layer.MaxZoom = json.GetNullableDouble("maxzoom");

            // visibility is kind-agnostic, so it belongs on the base beside minzoom, not in a per-kind
            // LayoutProperties. Absent or any value but "none" means visible, the spec default.
            layer.Visible = layoutJson?.GetString("visibility") != "none";

            // Raw sub-tree retained verbatim (null if the key is absent).
            layer.Filter = json.Get("filter");

            return layer;
        }

        // internal (not private): TileJsonParser reuses these so the tiles[]/bounds parse is single-source.
        internal static string[] ParseStringArray(JsonValue arr)
        {
            if (arr == null || !arr.IsArray) return null;
            var items = arr.Items;
            var result = new string[items.Count];
            for (int i = 0; i < items.Count; i++)
                result[i] = items[i].AsString();
            return result;
        }

        internal static double[] ParseDoubleArray(JsonValue arr)
        {
            if (arr == null || !arr.IsArray) return null;
            var items = arr.Items;
            var result = new double[items.Count];
            for (int i = 0; i < items.Count; i++)
                result[i] = items[i].AsDouble();
            return result;
        }
    }
}
