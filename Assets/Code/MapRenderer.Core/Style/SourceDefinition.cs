using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// The source-type discriminator (Style Spec <c>sources[].type</c>). Vector is implemented first
    /// (S08); the others are modeled here so the discriminator exists, but their loading is later
    /// stages (S21 geojson, S22 raster, S24 raster-dem, S36 image/video).
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
    /// A style source definition (Style Spec <c>sources[]</c>). Vector fields are typed first; the
    /// rest of the object is retained as <see cref="Raw"/> for later source stages.
    ///
    /// Spec defaults (vector source, from the public MapLibre Style Spec "Sources" page):
    /// <c>scheme</c> = "xyz", <c>minzoom</c> = 0, <c>maxzoom</c> = 22,
    /// <c>bounds</c> = [-180, -85.051129, 180, 85.051129]. These are applied by
    /// <see cref="StyleParser"/> when the corresponding key is absent.
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

        /// <summary>The full original source JSON object (preserves any unknown/forward-compat keys).</summary>
        public JsonValue Raw;
    }
}
