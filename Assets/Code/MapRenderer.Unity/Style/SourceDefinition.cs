using MapRenderer.Core.Json;

namespace MapRenderer.Unity.Style
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
    /// absent key: <c>scheme</c> = "xyz", <c>minzoom</c> = 0, <c>maxzoom</c> = 22 (18 for geojson),
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

        /// <summary>Source <c>maxzoom</c>. Defaults to 22 for vector when absent, 18 for geojson.</summary>
        public int MaxZoom;

        /// <summary>
        /// Tile coordinate scheme: "xyz" (default) or "tms". Influences the y direction of tile
        /// coordinates.
        /// </summary>
        public string Scheme;

        /// <summary>
        /// [west, south, east, north] bounds in lon/lat. Defaults to
        /// [-180, -85.051129, 180, 85.051129] for vector when absent OR when <see cref="BoundsMalformed"/>.
        /// </summary>
        public double[] Bounds;

        /// <summary>True when a present <c>bounds</c> key failed validation (wrong shape, a non-number
        /// element, <c>south &gt; north</c>, or a longitude outside [-180, 180]) — never true when the key
        /// is simply absent. The typed alternative to re-reading <see cref="Raw"/> at the call site: a
        /// consumer that must apply no bounds gate on malformed input checks this flag instead.</summary>
        public bool BoundsMalformed;

        /// <summary>
        /// Style Spec <c>data</c> (geojson): a URL to fetch, or an inline dataset parsed once at style
        /// load. Null when the key is absent.
        /// </summary>
        public SourcePayload Data;

        /// <summary>Style Spec <c>buffer</c> (geojson only), clamped to [0, 512] (512 = one tile width).
        /// Null for a non-geojson source, an absent key, or a non-number — an AUTHORED value only:
        /// <c>MapViewSourceSpecs.Build</c> then keeps <c>GeoJsonSliceOptions.DefaultBufferAtReferenceExtent</c>
        /// rather than the spec's 128 default.</summary>
        public double? Buffer;

        /// <summary>The full original source JSON object (preserves any unknown/forward-compat keys).</summary>
        public JsonValue Raw;
    }
}
