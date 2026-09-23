using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The coordinator's source boundary, raised from byte-centric (<c>IDataSource.FetchAsync →
    /// TileResponse</c>) to decoded-tile-provider (<c>GetTile → SharedDisposable&lt;IDecodedTile&gt;</c>).
    /// Byte-fetch, scheduling/caching, and <c>ITileDecoder</c> resolution are implementation details BELOW
    /// this interface — the MVT implementation wraps <c>TileScheduler</c>; a non-byte source
    /// (<c>GeoJsonTileFeatureSource</c>) implements this directly with no <c>IDataSource</c>/bytes at all. A
    /// polymorphic-output axis is documented but not built: <see cref="IDecodedTile"/> is already
    /// the "universal, polymorphic-by-kind" level (see its doc in <c>DecodedTile.cs</c>), so a raster
    /// source needs only a new implementation, not an interface change.
    /// </summary>
    public interface ITileFeatureSource : System.IDisposable
    {
        /// <summary>Resolves one tile's decode-provisioning handle, <b>already decoded</b>. Absent
        /// (404/204/missing, or a tile the dataset provably cannot reach) → <see langword="null"/>, matching
        /// <c>TileResponse.HasData == false</c> — the coordinator renders nothing for it.
        ///
        /// <para>Non-local invariant, kept in full: a partial statement of this reference-counting protocol
        /// is worse than none, since the next holder added here needs the whole picture to place its
        /// release correctly.</para>
        ///
        /// <para><b>The handle is a REFERENCE COUNT (<see cref="SharedDisposable{T}"/>), and every holder
        /// owns one — SYMMETRICALLY.</b> A decoded tile owns <c>Allocator.Persistent</c> buffers, so it
        /// needs a definite lifetime; the count is it. The buffers are freed by the LAST
        /// <see cref="SharedDisposable{T}.Release"/>, not by the last reader finishing. The caller receives
        /// ONE reference (the creator's, born with the wrapper) and owns it: every further holder takes its
        /// OWN with <see cref="SharedDisposable{T}.Acquire"/> and drops it exactly once. No holder takes over
        /// another holder's reference; only the mesh kick's own reference travels with its build output (see
        /// below). Every reference must reach
        /// exactly one release site, or the tile's
        /// <c>Allocator.Persistent</c> buffers leak.</para>
        ///
        /// <para><b>Who holds a reference in production, and where each is released.</b> The
        /// <c>TileManager.LoadedTile</c> record holds the creator's reference from the moment the fetch is
        /// observed until <c>TileManager.RenderTeardownRecord</c> releases it (cover change, eviction,
        /// restyle, teardown) — for the record's WHOLE in-cover lifetime, kicked or not; there is no
        /// second release site for it. The mesh kick takes its OWN separate reference in
        /// <c>KickMeshBuild</c>'s main-thread prologue (before the pool lambda exists). A synchronous
        /// hand-off throw releases it in <c>KickMeshBuild</c>'s outer <c>catch</c>; a fault inside the lambda
        /// releases it in the lambda's <c>catch</c>. On success it moves to <c>TilePrologueOutput.Decode</c>:
        /// <c>TileBuildGraph.Dispose</c> releases it once the output is handed to
        /// <c>ScheduleMeasureFromDecode</c> (that call's own <c>catch</c> releases it if scheduling throws),
        /// and <c>TilePrologueOutput.Dispose</c> releases it from a pen
        /// drain when the output never gets that far. A parked symbol build takes its own via
        /// <see cref="SharedDisposable{T}.Acquire"/> while the kick's reference is still live — which is what
        /// keeps the tile alive across the <c>SetStyle</c>→<c>SpritesSettled</c> window and is why a parked
        /// build does not need to re-decode — and releases it from <c>PumpBuilds</c>' drain or from
        /// <c>SymbolSubsystem.DrainAndDiscardParkedBuilds</c>. A fetch outcome nobody wants is released
        /// by <c>PendingDisposalQueue.DiscardFetchOutcome</c>.</para>
        ///
        /// <para><b>Failures are reported through the returned <see cref="UniTask{T}"/>, never thrown
        /// synchronously</b> — true of both implementations, and what lets the coordinator observe every
        /// outcome at one place. A fetch error and a decode fault both arrive here; a decode fault is a
        /// <c>TileDecodeException</c>, so the two can be told apart and logged apart.</para>
        ///
        /// <para><paramref name="ct"/> is <b>contract-only</b>: no production caller threads a token
        /// (<c>TileManager.Tick</c> calls <c>GetTile(id)</c>, the sole call site, for both implementations).
        /// The cancellation that matters — aborting an in-flight HTTP request — flows through
        /// <see cref="Release"/> instead, into the scheduler's own per-tile token. An implementation must
        /// still honour the parameter, so that threading one later cannot silently go on minting handles
        /// through a teardown.</para></summary>
        UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default);

        /// <summary>Releases interest in a tile (cancel in-flight, evict from any internal cache) — the
        /// raised form of <c>TileScheduler.Release</c>.</summary>
        void Release(TileId id);

        /// <summary>The in-flight request count — the raised form of <c>TileScheduler.InFlightCount</c>.</summary>
        int InFlightCount { get; }
    }
}
