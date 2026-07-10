using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// A parsed TileJSON document (the TileJSON spec — https://github.com/mapbox/tilejson-spec). In the
    /// MapLibre model a source declares its tiles <b>either</b> inline (<c>"tiles": [...]</c>) <b>or</b>
    /// indirectly via a TileJSON <c>url</c> pointing at one of these documents. This is the typed view
    /// of the fields the tile pipeline needs (<c>tiles</c>, <c>minzoom</c>, <c>maxzoom</c>, <c>scheme</c>,
    /// <c>bounds</c>); every other TileJSON field is retained verbatim on <see cref="Raw"/> for later
    /// stages (attribution, vector_layers, center, …).
    ///
    /// The <c>SourceDefinition</c> resolution that fills a source from this document is
    /// <see cref="SourceResolver"/>. <b>Fetching</b> the document (file://, http) is deliberately out of
    /// scope here (S83b) — this is the engine-free parse step.
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
    /// Parses a TileJSON document into the typed <see cref="TileJson"/> model. Tolerant +
    /// forward-compatible, exactly like <see cref="StyleParser"/>: unknown fields never throw, a field
    /// with a surprising-but-valid type falls back to its spec default, and malformed JSON surfaces
    /// <see cref="JsonParseException"/> from <see cref="JsonParser"/>.
    ///
    /// Spec defaults are shared with <see cref="StyleParser"/> (<c>DefaultScheme</c>,
    /// <c>DefaultSourceMinZoom</c>/<c>MaxZoom</c>, <c>DefaultBounds</c>) so a TileJSON omitting a field
    /// resolves to the same default the style-spec source would — single source of those constants.
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
