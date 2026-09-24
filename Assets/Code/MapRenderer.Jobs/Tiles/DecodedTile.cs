using System;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Mvt;

using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Tiles
{
    /// <summary>One decoded tile layer: name, extent, its features — and <b>its geometry</b>.
    /// The layer OWNS its coordinates: the decoder, which holds the tile address, mints
    /// <see cref="Geometry"/> eagerly, so the buffer's <c>Tile</c>/<c>Extent</c> cannot disagree with the
    /// layer.
    /// </summary>
    public interface ITileLayer
    {
        string Name { get; }
        uint   Extent { get; }
        IReadOnlyList<IFeature> Features { get; }

        /// <summary>This layer's decoded rings, in tile-local coordinates. <b>BORROWED</b> — owned by the
        /// decoded tile, freed when the tile is disposed. Never dispose it, never mutate it, and never retain
        /// it past the decode scope that produced the tile. <c>default</c> (<c>IsCreated == false</c>) for a
        /// layer with no features.</summary>
        TileGeometryBuffers Geometry { get; }
    }

    /// <summary>
    /// A decoded tile: layers looked up by name (<see cref="MvtTile"/>, <see cref="GeoJsonTile"/>).
    /// Non-local invariant: the tile holds <c>Allocator.Persistent</c> geometry, so its single owner is the
    /// <c>SharedDisposable{IDecodedTile}</c> that wraps it; consumers read layers inside a held reference and
    /// <b>never</b> dispose the tile themselves.
    /// </summary>
    public interface IDecodedTile : IDisposable
    {
        ITileLayer GetLayer(string name);
    }
}
