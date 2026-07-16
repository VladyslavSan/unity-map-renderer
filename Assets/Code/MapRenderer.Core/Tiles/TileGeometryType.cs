namespace MapRenderer.Core.Tiles
{
    /// <summary>Geometry type (maps the MVT Feature.type field values per the MVT spec).</summary>
    public enum TileGeometryType
    {
        Unknown = 0,
        Point = 1,
        LineString = 2,
        Polygon = 3
    }
}
