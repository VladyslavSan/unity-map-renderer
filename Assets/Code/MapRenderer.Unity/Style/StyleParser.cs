using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Unity.Style
{
    /// <summary>
    /// Parses a MapLibre Style document into the typed <see cref="StyleDocument"/> model; the schema and
    /// defaults come from the public Style Spec docs. Unknown keys survive on the matching <c>Raw</c> JSON,
    /// and an unexpected field type falls back to a default. Malformed JSON throws
    /// <see cref="JsonParseException"/>. Non-local invariant: a malformed expression costs only its own
    /// property, which takes its default and adds one <see cref="StyleDocument.Errors"/> entry.
    /// </summary>
    public static class StyleParser
    {
        // ---- vector source spec defaults (public MapLibre Style Spec "Sources" page) --------------
        public const string DefaultScheme = "xyz";
        public const int DefaultSourceMinZoom = 0;
        public const int DefaultSourceMaxZoom = 22;
        public const int DefaultGeoJsonSourceMaxZoom = 18;
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
            doc.Sprites.AddRange(ParseSprites(root));
            doc.Glyphs = root.GetString("glyphs");

            if (root.TryGet("sources", out var sources) && sources.IsObject)
            {
                foreach (var kv in sources.Members)
                    doc.Sources[kv.Key] = ParseSource(kv.Value);
            }

            if (root.TryGet("layers", out var layers) && layers.IsArray)
            {
                foreach (var layerJson in layers.Items)
                {
                    StyleLayer layer = ParseLayer(layerJson, fillAntialiasDefault, doc.Errors);
                    doc.Layers.Add(layer);
                    WarnOnCompositePaint(layer, doc.Warnings);
                }
            }

            doc.Light = StyleLight.Parse(root.Get("light"));
            doc.Sky = StyleSky.Parse(root.Get("sky"));
            doc.Errors.AddRange(doc.Light.Errors);
            doc.Errors.AddRange(doc.Sky.Errors);

            return doc;
        }

        /// <summary>
        /// Parses root <c>sprite</c>: a string counts as one entry with id <c>"default"</c>; an array
        /// keeps every well-formed <c>{id, url}</c> entry in declared order. Any other shape, or an array
        /// entry missing <c>id</c>/<c>url</c>, is dropped rather than thrown (the same forward-compat
        /// posture <see cref="ParseSource"/> takes).
        /// </summary>
        private static List<SpriteReference> ParseSprites(JsonValue root)
        {
            var result = new List<SpriteReference>();
            JsonValue sprite = root.Get("sprite");
            if (sprite == null)
                return result;

            if (sprite.Kind == JsonKind.String)
            {
                result.Add(new SpriteReference { Id = "default", Url = sprite.AsString() });
                return result;
            }

            if (sprite.Kind == JsonKind.Array)
            {
                foreach (JsonValue item in sprite.Items)
                {
                    string id = item.GetString("id");
                    string url = item.GetString("url");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url))
                        continue;
                    result.Add(new SpriteReference { Id = id, Url = url });
                }
            }

            return result;
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
            // geojson `data`: an inline object is parsed once here; a URL string is fetched later by
            // MapView.BuildSourceSpecs.
            src.Data = SourcePayload.Parse(json.Get("data"));
            // `buffer` (geojson only, per the spec) is an AUTHORED value only — a non-geojson source, an
            // absent key, or a non-number stays null, so MapView keeps GeoJsonSliceOptions.DefaultBufferAtReferenceExtent.
            double? rawBuffer = src.Type == SourceType.GeoJson ? json.GetNullableDouble("buffer") : null;
            src.Buffer = rawBuffer.HasValue ? math.clamp(rawBuffer.Value, 0.0, 512.0) : (double?)null;

            // Vector-source defaults. (Other source types carry these keys too; applying the vector
            // defaults is harmless for them and the raw object is always retained for later stages.)
            src.MinZoom = json.GetInt("minzoom", DefaultSourceMinZoom);
            src.MaxZoom = json.GetInt("maxzoom",
                src.Type == SourceType.GeoJson ? DefaultGeoJsonSourceMaxZoom : DefaultSourceMaxZoom);
            src.Scheme = json.GetString("scheme", DefaultScheme);
            src.Bounds = ParseBounds(json, out src.BoundsMalformed);

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

        /// <summary>Adds each error of one layer carrier to <paramref name="errors"/>, prefixed with the layer id.</summary>
        private static void ReportErrors(List<string> errors, string layerId, IReadOnlyList<string> layerErrors)
        {
            for (int i = 0; i < layerErrors.Count; i++) errors.Add($"layer '{layerId}' {layerErrors[i]}");
        }

        /// <summary>The renderer does not support a composite (zoom AND feature) paint value.
        /// This records one warning per such paint key, whether or not the layer type models that key.</summary>
        private static void WarnOnCompositePaint(StyleLayer layer, List<string> warnings)
        {
            JsonValue paintJson = layer.Raw != null && layer.Raw.IsObject ? layer.Raw.Get("paint") : null;
            if (paintJson == null || !paintJson.IsObject) return;
            foreach (KeyValuePair<string, JsonValue> member in paintJson.Members)
            {
                ExpressionKind kind;
                try { kind = ExpressionParser.Parse(member.Value).Kind; }
                catch (ExpressionParseException) { continue; } // a value that fails to parse is skipped
                if (kind == ExpressionKind.Composite)
                    warnings.Add($"layer '{layer.Id}' paint '{member.Key}' combines zoom and feature data, which is not supported.");
            }
        }

        private static StyleLayer ParseLayer(JsonValue json, bool fillAntialiasDefault, List<string> errors)
        {
            if (json == null || !json.IsObject)
                return new StyleLayer { Raw = json };

            string rawType = json.GetString("type");
            StyleLayerType layerType = StyleLayerTypeExtensions.ParseLayerType(rawType);

            JsonValue layoutJson = json.Get("layout");
            JsonValue paintJson  = json.Get("paint");

            // Factory: line/fill/symbol/background/fill-extrusion get their typed subclass with Paint/Layout
            // parsed eagerly; every other type uses the generic base.
            string id = json.GetString("id");
            StyleLayer layer;
            switch (layerType)
            {
                case StyleLayerType.Line:
                {
                    var paint = Line.PaintProperties.Parse(paintJson);
                    layer = new Line.StyleLayer { Paint = paint, Layout = Line.LayoutProperties.Parse(layoutJson) };
                    ReportErrors(errors, id, paint.Errors);
                    break;
                }
                case StyleLayerType.Fill:
                {
                    var paint  = Fill.PaintProperties.Parse(paintJson, fillAntialiasDefault);
                    var layout = Fill.LayoutProperties.Parse(layoutJson);
                    layer = new Fill.StyleLayer { Paint = paint, Layout = layout };
                    ReportErrors(errors, id, paint.Errors);
                    ReportErrors(errors, id, layout.Errors);
                    break;
                }
                case StyleLayerType.Symbol:
                {
                    var paint  = Symbol.PaintProperties.Parse(paintJson);
                    var layout = Symbol.LayoutProperties.Parse(layoutJson);
                    layer = new Symbol.StyleLayer { Paint = paint, Layout = layout };
                    ReportErrors(errors, id, paint.Errors);
                    ReportErrors(errors, id, layout.Errors);
                    break;
                }
                case StyleLayerType.Background:
                {
                    var paint = Background.PaintProperties.Parse(paintJson);
                    layer = new Background.StyleLayer { Paint = paint };
                    ReportErrors(errors, id, paint.Errors);
                    break;
                }
                case StyleLayerType.FillExtrusion:
                {
                    var paint = FillExtrusion.PaintProperties.Parse(paintJson);
                    layer = new FillExtrusion.StyleLayer { Paint = paint };
                    ReportErrors(errors, id, paint.Errors);
                    break;
                }
                default:
                    layer = new StyleLayer();
                    break;
            }

            layer.Raw = json;
            layer.Id = id;
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

            // Parses to null for an absent key; never throws (see LayerFilter.Parse).
            layer.Filter = LayerFilter.Parse(json.Get("filter"));

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

        /// <summary>Parses and validates <paramref name="parent"/>'s <c>bounds</c> key as
        /// <c>[west, south, east, north]</c>. <paramref name="malformed"/> is true only for a PRESENT key
        /// that fails the shape (4 numbers) or range (<c>south &lt;= north</c>, longitudes in [-180, 180])
        /// check; both an absent key and a malformed one return <see cref="DefaultBounds"/>. internal:
        /// <c>TileJsonParser</c> reuses it for the same check.</summary>
        internal static double[] ParseBounds(JsonValue parent, out bool malformed)
        {
            malformed = false;
            if (parent == null || !parent.TryGet("bounds", out JsonValue arr))
                return (double[])DefaultBounds.Clone(); // absent — not malformed

            if (!arr.IsArray || arr.Items.Count != 4)
            {
                malformed = true;
                return (double[])DefaultBounds.Clone();
            }

            IReadOnlyList<JsonValue> items = arr.Items;
            var parsed = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (items[i].Kind != JsonKind.Number)
                {
                    malformed = true;
                    return (double[])DefaultBounds.Clone();
                }
                parsed[i] = items[i].AsDouble();
            }

            double west = parsed[0];
            double south = parsed[1];
            double east = parsed[2];
            double north = parsed[3];
            if (south > north || west < -180.0 || west > 180.0 || east < -180.0 || east > 180.0)
            {
                malformed = true;
                return (double[])DefaultBounds.Clone();
            }

            return parsed;
        }
    }
}
