using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Mvt;

namespace MapRenderer.Core.Tiles
{
    /// <summary>
    /// Epic A / A6 (design §B-1): the *vector*-feature abstraction — geometry plus evaluated properties.
    /// <see cref="MvtTile"/>/<see cref="MvtLayer"/>/<see cref="MvtFeature"/> implement these directly
    /// (zero-copy) — no new carrier types, no re-materialization. A non-MVT vector decoder (a future
    /// GeoJSON/MLT source) implements the same three interfaces over its own in-memory representation and
    /// flows through the unchanged fill/line/symbol fan-out. The level that generalizes beyond vector data
    /// (raster, terrain, …) is <see cref="IDecodedTile"/> below, not this interface — see its doc.
    /// </summary>
    public interface ITileFeature : IFeature
    {
        /// <summary>Tile-space geometry command stream (decode with <see cref="MvtGeometry.Decode"/>). This
        /// is **MVT's** MoveTo/LineTo/ClosePath wire encoding — not a format-neutral shape. Other vector
        /// formats (GeoJSON, MLT) would have to transcode their own geometry INTO this command stream to
        /// implement <see cref="ITileFeature"/> today; they do not emit it natively. The planned
        /// neutralization (a format-neutral geometry buffer replacing this <c>uint[]</c>) is tracked in
        /// <c>docs/per-layer-tile-processing-design.md</c> §"Round-5 update" (canonical-IR direction).</summary>
        uint[] Geometry { get; }
    }

    /// <summary>One decoded tile layer: name, extent, and its features. See <see cref="ITileFeature"/>.</summary>
    public interface ITileLayer
    {
        string Name { get; }
        uint   Extent { get; }
        IReadOnlyList<ITileFeature> Features { get; }
    }

    /// <summary>
    /// A decoded tile: layers looked up by name. This is the *universal*, polymorphic-by-kind decode
    /// surface — vector sources resolve to <see cref="ITileLayer"/>/<see cref="ITileFeature"/> today, and
    /// future non-vector kinds (raster → texture, terrain → heightfield) are additional decode results at
    /// this same level, not additions to the vector-feature shape above. Implemented by <see cref="MvtTile"/>
    /// (and, later, any other <see cref="ITileDecoder"/> output).
    /// </summary>
    public interface IDecodedTile
    {
        ITileLayer GetLayer(string name);
    }

    /// <summary>
    /// A minimal, engine-free <see cref="ITileFeature"/> carrier for synthesized (source-less) features —
    /// production user: <c>TileBackgroundLayerProcessor</c>'s full-tile-extent background quad (design
    /// §B-4). No id, no properties (background paint is constant, never data-driven).
    /// </summary>
    public sealed class InMemoryTileFeature : ITileFeature
    {
        public TileGeometryType GeometryType { get; init; }
        public uint[]           Geometry     { get; init; }

        public bool   HasId => false;
        public Value  Id    => Value.Null;

        public bool TryGetProperty(string name, out Value value)
        {
            value = Value.Null;
            return false;
        }

        public IReadOnlyDictionary<string, Value> Properties { get; } = new Dictionary<string, Value>();
    }
}
