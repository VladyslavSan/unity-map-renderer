using System;
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Tiles;

using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The second production <see cref="ITileLayer"/> — one GeoJSON dataset's geometry as it falls inside one
    /// tile, in <c>MvtLayer</c>'s shape, so a GeoJSON tile is indistinguishable downstream from an MVT one.
    /// <see cref="Features"/> holds the sliced <see cref="GeoJsonFeature"/>s themselves, not adapters, so
    /// filters read the authored properties directly. A geojson source has one layer and ignores
    /// <c>source-layer</c> (<see cref="GeoJsonTile.GetLayer"/>), so <see cref="Name"/> is diagnostic only.
    /// </summary>
    public sealed class GeoJsonTileLayer : ITileLayer, IDisposable
    {
        /// <summary>The name this layer reports. Diagnostic only — <see cref="GeoJsonTile.GetLayer"/> does
        /// not match on it, because a geojson source has nothing to disambiguate.</summary>
        public const string WellKnownName = "geojson";

        internal GeoJsonTileLayer(uint extent, List<IFeature> features)
        {
            Extent   = extent;
            Features = features;
        }

        public string Name => WellKnownName;

        public uint Extent { get; }

        /// <summary>The sliced features, in slice order. <b>Not the dataset's features</b> — a feature the
        /// tile clipped away is absent, so this list is what <c>SelectedTileFeature.Ordinal</c> counts
        /// positions in, and what <see cref="Geometry"/>'s per-feature column is in lockstep with.</summary>
        public IReadOnlyList<IFeature> Features { get; }

        /// <summary>This layer's rings. <b>BORROWED</b> by every consumer, exactly as <c>MvtLayer</c>'s is —
        /// owned here, freed by <see cref="Dispose"/>, which the tile drives.</summary>
        public TileGeometryBuffers Geometry { get; private set; }

        private bool _geometryAdopted;

        /// <summary>Set-once, lockstep-checked adoption — the same two guards <c>MvtLayer</c> applies,
        /// through the same <see cref="LayerGeometryAdoption"/>.</summary>
        internal void AdoptGeometry(TileGeometryBuffers geometry)
        {
            LayerGeometryAdoption.Validate(
                $"GeoJsonTileLayer '{Name}'", _geometryAdopted, geometry, Features.Count);

            _geometryAdopted = true;
            Geometry         = geometry;
        }

        /// <summary>Frees this layer's buffer. Idempotent — and the write-back is load-bearing for exactly
        /// the reason it is on <c>MvtLayer.Dispose</c>: a property getter hands out a COPY, so
        /// <c>Geometry.Dispose()</c> alone would free the arrays and leave this layer still claiming to own
        /// them, making the next (documented-idempotent) call a real double free.</summary>
        public void Dispose()
        {
            TileGeometryBuffers geometry = Geometry;
            geometry.Dispose();
            Geometry = geometry;
        }
    }

    /// <summary>
    /// One tile's worth of a GeoJSON dataset: zero or one <see cref="GeoJsonTileLayer"/>. An empty slice
    /// yields a layer-less tile, never a layer with zero features, so <see cref="GetLayer"/> returns a real
    /// layer or null.
    /// </summary>
    public sealed class GeoJsonTile : IDecodedTile
    {
        private readonly GeoJsonTileLayer _layer;

        internal GeoJsonTile(GeoJsonTileLayer layer) => _layer = layer;

        /// <summary>Returns the tile's sole layer, <b>whatever the name</b> (including null/empty), or null
        /// when the slice was empty. Non-obvious why: the Style Spec leaves <c>source-layer</c> unused for
        /// geojson sources, so a bogus name still resolves. <c>SourceLayerResolver</c> passes an absent name
        /// through, so each tile answers for its own format (<c>MvtTile.GetLayer</c> returns null).</summary>
        public ITileLayer GetLayer(string name) => _layer;

        public void Dispose() => _layer?.Dispose();
    }

    /// <summary>
    /// The GeoJSON <see cref="ITileDecoder"/>: slices a projected dataset into one tile. Behind the interface
    /// its tile has an MVT tile's lifetime; <c>bytes</c> is always null. Non-local invariant: one decoder
    /// serves every tile, so two threads can run <see cref="Decode"/> at once; a field memo breaks this.
    /// Paths to <see cref="PathGeometryMaterializer"/> are tile-local integers in <c>[−b, extent+b]</c>, Y-down,
    /// never degrees (docs/tile-geometry-ir-design.md § "Invariants the mechanism must hold").
    /// </summary>
    public sealed class GeoJsonTileDecoder : ITileDecoder
    {
        private readonly GeoJsonProjectedDataset _dataset;
        private readonly GeoJsonSliceOptions     _options;

        public GeoJsonTileDecoder(GeoJsonProjectedDataset dataset, in GeoJsonSliceOptions options)
        {
            _dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
            _options = options;
        }

        /// <param name="bytes">Ignored — always null on this path. See <see cref="ITileDecoder.Decode"/>.</param>
        public IDecodedTile Decode(TileId id, byte[] bytes)
        {
            TileSlice slice = GeoJsonTileSlicer.Slice(_dataset, id, _options);
            IReadOnlyList<SlicedFeature> sliced = slice.Features;

            if (sliced.Count == 0) return new GeoJsonTile(null);

            var features = new List<IFeature>(sliced.Count);
            var kinds    = new TileGeometryType[sliced.Count];
            var paths    = new IReadOnlyList<IReadOnlyList<double2>>[sliced.Count];

            for (int f = 0; f < sliced.Count; f++)
            {
                GeoJsonFeature source = sliced[f].Source;
                features.Add(source);
                // Kind comes from the source's own declaration, never inferred from the coordinates
                // (ITileGeometryMaterializer's contract, "Kind, not shape").
                kinds[f] = source.GeometryType;
                paths[f] = sliced[f].Paths;
            }

            var layer = new GeoJsonTileLayer((uint)slice.Extent, features);
            layer.AdoptGeometry(
                new PathGeometryMaterializer(slice.Tile, slice.Extent, kinds, paths).Materialize());
            return new GeoJsonTile(layer);
        }
    }
}
