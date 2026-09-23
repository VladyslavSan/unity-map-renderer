using System;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Mvt;

using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Tiles
{
    /// <summary>One decoded tile layer: name, extent, its features — and <b>its geometry</b>.
    ///
    /// <para>The element type of <see cref="Features"/> is <see cref="IFeature"/>, the evaluation surface
    /// itself.</para>
    ///
    /// <para><b>The layer OWNS its coordinates.</b> <see cref="Geometry"/> is a member of the layer, minted
    /// eagerly by the decoder, which was itself handed the tile address — so the buffer's
    /// <c>Tile</c>/<c>Extent</c> and the layer they describe cannot disagree.</para>
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
    /// A decoded tile: layers looked up by name. This is the *universal*, polymorphic-by-kind decode
    /// surface — vector sources resolve to <see cref="ITileLayer"/>/<see cref="IFeature"/>, and a non-vector
    /// kind (raster → texture, terrain → heightfield) would be an additional decode result at this same
    /// level, not an addition to the vector-feature shape above. Implemented by <see cref="MvtTile"/> and
    /// <see cref="GeoJsonTile"/>.
    ///
    /// <para><b>Why <see cref="IDisposable"/>.</b> A decoded tile holds <c>Allocator.Persistent</c> native
    /// memory (its layers' <see cref="ITileLayer.Geometry"/>), so it has a definite lifetime and a single
    /// owner. That owner is the <c>SharedDisposable{IDecodedTile}</c> that wraps it: consumers read layers
    /// inside a held reference and <b>never</b> dispose the tile themselves.</para>
    /// </summary>
    public interface IDecodedTile : IDisposable
    {
        ITileLayer GetLayer(string name);
    }
}
