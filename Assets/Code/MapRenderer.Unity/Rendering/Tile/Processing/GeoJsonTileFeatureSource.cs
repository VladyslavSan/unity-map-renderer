using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The GeoJSON <see cref="ITileFeatureSource"/>: tiles sliced locally from a retained dataset, with no
    /// fetcher, scheduler or cache. The handle is eager, as in the MVT source: <see cref="GetTile"/> slices,
    /// then returns a <see cref="SharedDisposable{T}"/>, so a record that never kicks still frees its tile.
    /// Non-obvious why: the slice uses the MVT source's <see cref="TileDecodeDispatch.DecodeAsync"/>. Under
    /// <see cref="ThreadPoolWorkScheduler"/> it hops to the pool, so no slice stalls <c>Tick</c>
    /// (<c>GeoJsonSourceTests.GetTile_SlicesOffTheMainThread</c>); under <see cref="InlineWorkScheduler"/>
    /// (WebGL) it runs inline.
    /// The shared dispatch passes a null <c>bytes</c>, which <see cref="ITileDecoder.Decode"/> documents.
    /// Slice options are a constructor parameter because extent and buffer set each source's resolution.
    /// </summary>
    internal sealed class GeoJsonTileFeatureSource : ITileFeatureSource
    {
        private readonly GeoJsonProjectedDataset _dataset;
        private readonly GeoJsonSliceOptions     _options;
        private readonly ITileDecoder            _decoder;
        private readonly IWorkScheduler          _scheduler;

        /// <param name="dataset">The parsed dataset, projected once here for every tile (projection is
        /// zoom-independent). Limitation: this O(N) step runs on the main thread in <c>SetStyle</c>.</param>
        /// <param name="options">Slice options — a parameter, not a constant (see the type doc).</param>
        /// <param name="scheduler">The execution policy the slice hop runs under — see
        /// <see cref="TileDecodeDispatch.DecodeAsync"/>.</param>
        internal GeoJsonTileFeatureSource(GeoJsonDataset dataset, in GeoJsonSliceOptions options,
            IWorkScheduler scheduler)
        {
            // Validate here, not at the first slice: the options live as long as the source, so a bad set
            // would otherwise fault every GetTile task, far from the wiring site.
            options.Validate();

            _dataset   = GeoJsonProjectedDataset.Project(dataset);
            _options   = options;
            _decoder   = new GeoJsonTileDecoder(_dataset, options);
            _scheduler = scheduler;
        }

        /// <summary>Slices the tile and hands back a lease over it, or null for a tile this dataset provably
        /// cannot reach.
        ///
        /// <para>Non-local invariant: the emptiness probe is O(1) and CONSERVATIVE — it intersects the
        /// tile's buffered unit-square window against the dataset's own bounding box, using the same
        /// <see cref="GeoJsonSliceOptions.UnitSquareWindow"/> arithmetic the slicer's per-feature reject
        /// uses, so it can never reject a tile that has geometry. It exists to avoid slicing a tile only to
        /// learn that it is empty. A tile inside the box but between
        /// features yields a non-null handle over an empty tile, which the coordinator already handles.</para>
        ///
        /// <para>The probe runs on the CALLER's thread — three comparisons — and only the slice runs under
        /// the injected scheduler, so a disjoint tile costs nothing and never decodes.</para></summary>
        public async UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default)
        {
            // No production caller threads a token, so this is CONTRACT CONFORMANCE. An
            // implementation that ignored it would go on minting handles once a token reaches a teardown.
            ct.ThrowIfCancellationRequested();

            _options.UnitSquareWindow(id, out double2 windowMin, out double2 windowMax);

            bool disjoint = _dataset.BboxMax.x < windowMin.x || _dataset.BboxMin.x > windowMax.x ||
                            _dataset.BboxMax.y < windowMin.y || _dataset.BboxMin.y > windowMax.y;

            // The slice is the decode, and it runs on the pool — never inline in the caller's Tick.
            return disjoint ? null : await TileDecodeDispatch.DecodeAsync(id, null, _decoder, _scheduler);
        }

        /// <summary>Nothing to cancel and nothing to evict — the dataset is retained for this source's whole
        /// life, and a tile's native buffers belong to the lease, not to this.</summary>
        public void Release(TileId id) { }

        /// <summary>Zero, and honestly so: this source never has a request in flight.</summary>
        public int InFlightCount => 0;

        /// <summary>Nothing to free. The decoded tiles own the native memory and the lease frees them.</summary>
        public void Dispose() { }
    }
}
