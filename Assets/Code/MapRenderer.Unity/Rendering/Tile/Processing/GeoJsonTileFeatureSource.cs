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
    /// The GeoJSON implementation of <see cref="ITileFeatureSource"/>: tiles sliced locally from a retained
    /// dataset instead of fetched from a server. It owns no fetcher, no scheduler and no cache — there is
    /// nothing to fetch — so the whole of it is "project once, then slice on demand".
    ///
    /// <para><b>The handle is EAGER, exactly as the MVT one is.</b> <see cref="GetTile"/> slices, then hands
    /// back a <see cref="SharedDisposable{T}"/> over the finished tile. The laziness this type used to argue for was
    /// load-bearing under the scoped lease for one reason only: <c>TileManager.Tick</c> calls
    /// <c>GetTile</c> for EVERY cover tile while only some ever opened a scope, so a pre-built tile on the
    /// others would have been freed by nothing. That premise is gone — every drop path is now an OWNER with
    /// a release in it, so a tile handed out to a record that never kicks is freed when the record dies.</para>
    ///
    /// <para><b>The slice stays OFF the main thread under the desktop policy</b>, which laziness used to
    /// arrange for free by deferring it into the kick's pool lambda. It is now arranged deliberately, by
    /// routing through <see cref="TileDecodeDispatch.DecodeAsync"/> — the same dispatch the MVT source uses,
    /// under whichever <see cref="IWorkScheduler"/> this source was constructed with — and pinned by a tooth
    /// rather than by an accident of the design. Under <see cref="ThreadPoolWorkScheduler"/> (desktop/editor)
    /// slicing inline inside <c>Tick</c> would be a per-cover-tile main-thread stall; under
    /// <see cref="InlineWorkScheduler"/> (WebGL) the slice runs synchronously on whatever thread calls
    /// <c>GetTile</c> by design — there is no worker thread to hop to.</para>
    ///
    /// <para><b>Reusing the shared decode dispatch verbatim</b> costs zero lines in the most safety-critical
    /// part of the pipeline (the pool hop's completion invariant, the profiler marker, the lease's refcount
    /// and dispose-outside-the-lock ordering) and leaves every lease tooth unmodified — which is also the
    /// evidence the decode seam was drawn in the right place. The price, stated: a permanently-null
    /// <c>bytes</c> argument, which <see cref="ITileDecoder.Decode"/> documents.</para>
    ///
    /// <para><b>Slice options are a CONSTRUCTOR PARAMETER, never a constant.</b> Extent and buffer set the
    /// positional resolution of everything this source will ever be used to state, so a hardcoded
    /// <c>GeoJsonSliceOptions.Default</c> here would turn a per-source choice into a production constant.</para>
    ///
    /// Internal (not public): constructed only from <c>MapView.BuildSourceSpecs</c> and from the test
    /// assembly via <c>InternalsVisibleTo</c> — the same posture as the MVT source beside it.
    /// </summary>
    internal sealed class GeoJsonTileFeatureSource : ITileFeatureSource
    {
        private readonly GeoJsonProjectedDataset _dataset;
        private readonly GeoJsonSliceOptions     _options;
        private readonly ITileDecoder            _decoder;
        private readonly IWorkScheduler          _scheduler;

        /// <param name="dataset">The parsed dataset. Projected ONCE here — projection is zoom-independent, so
        /// it is the whole of the work that can be shared across every tile this source will serve. Recorded
        /// cost: that happens on the main thread inside <c>SetStyle</c>, O(N) once per style-set; negligible
        /// for a fixture, a hitch for a large dataset.</param>
        /// <param name="options">Slice options — see the type doc: a parameter, deliberately.</param>
        /// <param name="scheduler">The execution policy the slice hop runs under — see
        /// <see cref="TileDecodeDispatch.DecodeAsync"/>.</param>
        internal GeoJsonTileFeatureSource(GeoJsonDataset dataset, in GeoJsonSliceOptions options,
            IWorkScheduler scheduler)
        {
            // Validated HERE, not at the first slice. These options are retained for the source's whole
            // life, so an unusable set (a `default(GeoJsonSliceOptions)`, whose zero extent makes the probe
            // window NaN, the probe answer "keep", and every slice throw; or an unimplemented non-zero
            // SimplifyTolerance) would otherwise surface once per tile, as a faulted GetTile task, far from
            // the wiring site that chose it.
            options.Validate();

            _dataset   = GeoJsonProjectedDataset.Project(dataset);
            _options   = options;
            _decoder   = new GeoJsonTileDecoder(_dataset, options);
            _scheduler = scheduler;
        }

        /// <summary>Slices the tile and hands back a lease over it, or null for a tile this dataset provably
        /// cannot reach.
        ///
        /// <para>The emptiness probe is O(1) and CONSERVATIVE: it intersects the tile's buffered unit-square
        /// window against the dataset's own bounding box, both from the same
        /// <see cref="GeoJsonSliceOptions.UnitSquareWindow"/> arithmetic the slicer's per-feature reject uses,
        /// so it can never reject a tile that has geometry. It cannot be an exact answer without slicing, and
        /// the point of it is to avoid slicing a tile at all just to learn it is empty — not to keep the
        /// slice off the main thread, which the scheduler hop below arranges (a pool hop under
        /// <see cref="ThreadPoolWorkScheduler"/>; inline, on WebGL). A tile
        /// inside the box but between features therefore yields a non-null handle over an
        /// empty tile, which the coordinator already handles (zero layers ⇒ every processor settles at zero
        /// vertices).</para>
        ///
        /// <para>The probe runs on the CALLER's thread — three comparisons — and only the slice runs under the
        /// injected scheduler. A disjoint tile therefore still costs nothing and still never decodes.</para></summary>
        public async UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default)
        {
            // Nothing here is long enough to cancel MID-call, and no production caller threads a token
            // today — `TileManager.Tick` calls `GetTile(id)` (TileManager.cs:1215), the sole call site, for
            // both implementations. So this is CONTRACT CONFORMANCE ahead of the coordinator, not an
            // observed cancellation path: the seam declares a `CancellationToken`, and an implementation
            // that ignored it would go on minting handles the moment one is threaded through a teardown.
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
