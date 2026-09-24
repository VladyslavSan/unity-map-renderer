using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// A parsed TileJSON document (the TileJSON spec — https://github.com/mapbox/tilejson-spec), which a source
    /// references through its <c>url</c> instead of inline <c>tiles</c>. It types the fields the tile pipeline
    /// needs (<c>tiles</c>, <c>minzoom</c>, <c>maxzoom</c>, <c>scheme</c>, <c>bounds</c>) and keeps every other
    /// field on <see cref="Raw"/>. <see cref="SourceResolver"/> fills a source from it; fetching is elsewhere.
    /// </summary>
    public sealed class TileJson
    {
        /// <summary>Tile URL templates (TileJSON <c>tiles</c>), or null. Never empty-vs-null ambiguous.</summary>
        public string[] Tiles;

        /// <summary>Minimum zoom (TileJSON <c>minzoom</c>). Defaults to the spec default when absent.</summary>
        public int MinZoom;

        /// <summary>Maximum zoom (TileJSON <c>maxzoom</c>). Defaults to the spec default when absent.</summary>
        public int MaxZoom;

        /// <summary>Tile scheme ("xyz" default, or "tms"). Defaults to the spec default when absent.</summary>
        public string Scheme;

        /// <summary>[west, south, east, north] bounds in lon/lat; spec default when absent.</summary>
        public double[] Bounds;

        /// <summary>The full original TileJSON object (preserves any unknown/forward-compat keys).</summary>
        public JsonValue Raw;
    }

    /// <summary>
    /// Parses a TileJSON document into the typed <see cref="TileJson"/> model, tolerant like
    /// <see cref="StyleParser"/>: unknown fields never throw, a surprising type falls back to the spec default,
    /// and malformed JSON throws <see cref="JsonParseException"/>. It shares <see cref="StyleParser"/>'s source
    /// defaults, so an omitted field resolves as it would on a style-spec source.
    /// </summary>
    public static class TileJsonParser
    {
        /// <summary>Parse from a JSON string.</summary>
        public static TileJson Parse(string json)
            => Parse(JsonParser.Parse(json));

        /// <summary>Parse from an already-parsed JSON DOM root.</summary>
        public static TileJson Parse(JsonValue root)
        {
            var tj = new TileJson { Raw = root };
            if (root == null || !root.IsObject)
            {
                // Tolerate a non-object document: resolve every field to its spec default rather than throw.
                tj.MinZoom = StyleParser.DefaultSourceMinZoom;
                tj.MaxZoom = StyleParser.DefaultSourceMaxZoom;
                tj.Scheme = StyleParser.DefaultScheme;
                tj.Bounds = (double[])StyleParser.DefaultBounds.Clone();
                return tj;
            }

            tj.Tiles = StyleParser.ParseStringArray(root.Get("tiles"));
            tj.MinZoom = root.GetInt("minzoom", StyleParser.DefaultSourceMinZoom);
            tj.MaxZoom = root.GetInt("maxzoom", StyleParser.DefaultSourceMaxZoom);
            tj.Scheme = root.GetString("scheme", StyleParser.DefaultScheme);
            tj.Bounds = StyleParser.ParseDoubleArray(root.Get("bounds")) ?? (double[])StyleParser.DefaultBounds.Clone();

            return tj;
        }
    }
}
