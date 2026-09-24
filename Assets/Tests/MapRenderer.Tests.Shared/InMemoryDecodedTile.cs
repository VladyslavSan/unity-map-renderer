// Unity EditMode only — TileGeometryBuffers is NativeArray-backed. NOT registered in core-tests.csproj.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A synthetic <see cref="ITileLayer"/> shaped like a decoded one: it <b>owns</b> its
    /// <see cref="TileGeometryBuffers"/>, materialized ONCE at construction through the real
    /// <see cref="MvtGeometryMaterializer"/>. Non-obvious why: <c>MvtDecoder.Decode</c> mints one buffer
    /// before any read, and a lazy property would let <c>SourceLayerBufferSharingTests</c> pass for a
    /// reason production lacks. Dispose it via <see cref="InMemoryDecodedTile"/>; the buffer is Persistent.
    /// </summary>
    public sealed class InMemoryTileLayer : ITileLayer, IDisposable
    {
        public string                  Name     { get; }
        public uint                    Extent   { get; }
        public IReadOnlyList<IFeature> Features { get; }
        public TileGeometryBuffers     Geometry { get; private set; }

        public InMemoryTileLayer(string name, TileId tile, IReadOnlyList<IFeature> features, uint extent = 4096)
        {
            Name     = name;
            Extent   = extent;
            Features = features ?? Array.Empty<IFeature>();

            var kinds    = new List<TileGeometryType>(Features.Count);
            var commands = new List<uint[]>(Features.Count);
            for (int i = 0; i < Features.Count; i++)
            {
                kinds.Add(Features[i]?.GeometryType ?? TileGeometryType.Unknown);
                commands.Add((Features[i] as ITileCommandStreamFeature)?.Geometry);
            }

            Geometry = MvtGeometryMaterializerTestFactory.Materialize(tile, extent, kinds, commands);
        }

        public void Dispose()
        {
            TileGeometryBuffers geometry = Geometry;
            geometry.Dispose();
            Geometry = default;
        }
    }

    /// <summary>
    /// A synthetic <see cref="IDecodedTile"/> over <see cref="InMemoryTileLayer"/>s, mirroring
    /// <c>MvtTile</c>: it owns its layers and frees their geometry on <see cref="Dispose"/>.
    /// </summary>
    public sealed class InMemoryDecodedTile : IDecodedTile
    {
        private readonly List<InMemoryTileLayer> _layers;

        public InMemoryDecodedTile(params InMemoryTileLayer[] layers)
            => _layers = new List<InMemoryTileLayer>(layers ?? Array.Empty<InMemoryTileLayer>());

        public ITileLayer GetLayer(string name)
        {
            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i].Name == name) return _layers[i];
            return null;
        }

        public void Dispose()
        {
            for (int i = 0; i < _layers.Count; i++) _layers[i].Dispose();
        }
    }
}
