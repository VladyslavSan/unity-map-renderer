using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// The source-type discriminator (Style Spec <c>sources[].type</c>). The types that have no loader
    /// yet are still modeled here so the discriminator is complete.
    /// </summary>
    public enum SourceType
    {
        Unknown = 0,
        Vector,
        Raster,
        RasterDem,
        GeoJson,
        Image,
        Video
    }

    /// <summary>
    /// A style source definition (Style Spec <c>sources[]</c>). Vector fields are typed; the whole object
    /// stays in <see cref="Raw"/>. <see cref="StyleParser"/> applies the Style Spec "Sources" defaults for an
    /// absent key: <c>scheme</c> = "xyz", <c>minzoom</c> = 0, <c>maxzoom</c> = 22,
    /// <c>bounds</c> = [-180, -85.051129, 180, 85.051129].
    /// </summary>
    public sealed class SourceDefinition
    {
        /// <summary>The recognized source type; <see cref="SourceType.Unknown"/> for forward-compat.</summary>
        public SourceType Type;

        /// <summary>The raw <c>type</c> string as written in the JSON (preserved even when Unknown).</summary>
        public string RawType;

        /// <summary>TileJSON URL (Style Spec <c>url</c>), or null.</summary>
        public string Url;

        /// <summary>Explicit tile URL templates (Style Spec <c>tiles</c>), or null. Never empty-vs-null ambiguous.</summary>
        public string[] Tiles;

        /// <summary>Source <c>minzoom</c>. Defaults to 0 for vector when absent.</summary>
        public int MinZoom;

        /// <summary>Source <c>maxzoom</c>. Defaults to 22 for vector when absent.</summary>
        public int MaxZoom;

        /// <summary>
        /// Tile coordinate scheme: "xyz" (default) or "tms". Influences the y direction of tile
        /// coordinates.
        /// </summary>
        public string Scheme;

        /// <summary>
        /// [west, south, east, north] bounds in lon/lat. Defaults to
        /// [-180, -85.051129, 180, 85.051129] for vector when absent.
        /// </summary>
        public double[] Bounds;

        /// <summary>
        /// Style Spec <c>data</c> — <b>either</b> an inline GeoJSON object <b>or</b> a URL string, which is
        /// why it is a <see cref="JsonValue"/> and not a typed model: the key carries two shapes and this
        /// type sits outside every decoder folder, so a format-named type in its signature would be a fence
        /// violation as well as a lie about half the values. Null when the key is absent.
        /// </summary>
        public JsonValue Data;

        /// <summary>The full original source JSON object (preserves any unknown/forward-compat keys).</summary>
        public JsonValue Raw;
    }
}
