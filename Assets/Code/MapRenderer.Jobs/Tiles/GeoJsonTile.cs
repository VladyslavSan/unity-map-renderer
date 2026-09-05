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
    /// tile. Everything a consumer reads off it is the same shape <c>MvtLayer</c> presents, deliberately: a
    /// GeoJSON tile must be indistinguishable downstream from an MVT one.
    ///
    /// <para><b>The parsed feature IS the evaluation surface.</b> <see cref="Features"/> holds the
    /// <see cref="GeoJsonFeature"/>s the slicer carried through by reference, in slice order — not adapters
    /// over them. Filters and expressions therefore read the authored properties directly.</para>
    ///
    /// <para><b>One layer, no name matching.</b> A geojson source has no sub-layers, so a style layer's
    /// <c>source-layer</c> is not used against it (Style Spec: required for vector sources, unused for
    /// geojson) — see <see cref="GeoJsonTile.GetLayer"/>. <see cref="Name"/> is diagnostic only; nothing
    /// resolves against it.</para>
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
        /// through the same <see cref="TileLayerGeometryAdoption"/>.</summary>
        internal void AdoptGeometry(TileGeometryBuffers geometry)
        {
            TileLayerGeometryAdoption.Validate(
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
    /// One tile's worth of a GeoJSON dataset: zero or one <see cref="GeoJsonTileLayer"/>.
    ///
    /// <para><b>Zero layers, never a layer with zero features.</b> An empty slice yields a layer-less tile,
    /// which keeps <c>FeatureCount == Features.Count</c> trivially true and keeps
    /// <see cref="GetLayer"/> honest — "my sole layer" has to actually exist to be returned.</para>
    /// </summary>
    public sealed class GeoJsonTile : IDecodedTile
    {
        private readonly GeoJsonTileLayer _layer;

        internal GeoJsonTile(GeoJsonTileLayer layer) => _layer = layer;

        /// <summary>Returns the tile's sole layer, <b>whatever the name</b> (including null/empty), or null
        /// when the slice was empty.
        ///
        /// <para>This is conformance, not tolerance: the Style Spec says <c>source-layer</c> is required for
        /// vector sources and unused for geojson ones, so a geojson tile has nothing to match a name against.
        /// It is also where the accommodation for an absent <c>source-layer</c> now lives —
        /// <c>SourceLayerResolver</c> no longer short-circuits on one, so each tile answers for its own
        /// format (<c>MvtTile.GetLayer</c> keeps returning null for an empty name).</para>
        ///
        /// <para>Consequence, stated: a style layer naming a bogus <c>source-layer</c> over a geojson source
        /// still resolves. That is the spec's answer; match-or-null would instead give a fixture author a
        /// silent-empty failure mode for a key the spec says is ignored.</para></summary>
        public ITileLayer GetLayer(string name) => _layer;

        public void Dispose() => _layer?.Dispose();
    }

    /// <summary>
    /// The GeoJSON <see cref="ITileDecoder"/>: a closure over a projected dataset and its slice options that
    /// turns a <see cref="TileId"/> into a decoded tile. <b>There are no bytes</b> — <c>Decode</c>'s
    /// <c>bytes</c> parameter is always null here, which <see cref="ITileDecoder.Decode"/> documents.
    ///
    /// <para><b>The slice IS the decode.</b> Sitting behind <see cref="ITileDecoder"/> is what makes a
    /// GeoJSON tile obey exactly the same lifetime as an MVT one: <c>TileDecodeDispatch.DecodeAsync</c> runs
    /// this on the pool at fetch completion, mints one reference-counted handle over the result, and the
    /// native buffers it allocates are freed at the last release. Nothing here needs to know which source
    /// kind it serves.</para>
    ///
    /// <para>Slicing is a pure function of (dataset, tile, options), so two decodes of the same tile produce
    /// identical results — the exact analogue of re-decoding from retained bytes. Under the reference count
    /// that no longer happens in production (a tile decodes once), but the property is what makes a second
    /// <c>GetTile</c> for the same tile well defined.</para>
    ///
    /// <para><b>ONE decoder serves every tile of the source, and the lease's lock is per HANDLE</b>, so two
    /// pool threads can be inside <see cref="Decode"/> at once. That is safe and must stay safe: the dataset
    /// and the options are immutable after construction, every list and array here is allocated per call,
    /// and nothing this reaches holds mutable static state. MVT gets the same property for free by minting a
    /// decoder per encoding; this one has it by construction, which means a future edit that memoized
    /// anything onto a field would break it silently.</para>
    ///
    /// <para><b>THE NAMED FENCE.</b> The paths handed to <see cref="PathGeometryMaterializer"/> are
    /// tile-local <c>double2</c> in <c>[−b, extent+b]</c>, Y-down, quantized to integers — never geodetic,
    /// never projected. Ring assembly's and earcut's thresholds are calibrated to tile-integer magnitude;
    /// degrees are a different scale entirely.</para>
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
