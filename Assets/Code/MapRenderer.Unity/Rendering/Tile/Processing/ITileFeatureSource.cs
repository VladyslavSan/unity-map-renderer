using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The coordinator's source boundary, raised from byte-centric (<c>IDataSource.FetchAsync →
    /// TileResponse</c>) to
    /// decoded-tile-provider (<c>GetTile → SharedDisposable&lt;IDecodedTile&gt;</c>). Byte-fetch,
    /// scheduling/caching, and <c>ITileDecoder</c> resolution are implementation details BELOW this
    /// interface — the MVT implementation (<c>MvtTileFeatureSource</c>, Unity) wraps the unchanged
    /// <c>TileScheduler</c>; a future non-byte source (in-memory features, GeoJSON) implements this directly
    /// with no <c>IDataSource</c>/bytes at all.
    ///
    /// <para>Future polymorphic-output axis, documented but not built: today every
    /// implementation is vector (returns a handle over <see cref="ITileLayer"/>/<see cref="IFeature"/>).
    /// A raster source would return a different decode-result kind (e.g. a texture artifact) at this SAME
    /// seam — <see cref="IDecodedTile"/> is already the documented "universal, polymorphic-by-kind" level
    /// (see its doc in <c>DecodedTile.cs</c>), so no interface change is anticipated, only new implementations.</para>
    /// </summary>
    public interface ITileFeatureSource : System.IDisposable
    {
        /// <summary>Resolves one tile's decode-provisioning handle, <b>already decoded</b>. Absent
        /// (404/204/missing, or a tile the dataset provably cannot reach) → <see langword="null"/>, matching
        /// today's <c>TileResponse.HasData == false</c> — the coordinator renders nothing for it.
        ///
        /// <para><b>The handle is a REFERENCE COUNT (<see cref="SharedDisposable{T}"/>), and every holder
        /// owns one — SYMMETRICALLY.</b> A decoded tile owns <c>Allocator.Persistent</c> buffers, so it
        /// needs a definite lifetime; the count is it. The buffers are freed by the LAST
        /// <see cref="SharedDisposable{T}.Release"/>, not by the last reader finishing. The caller receives
        /// ONE reference (the creator's, born with the wrapper) and owns it: every further holder takes its
        /// OWN with <see cref="SharedDisposable{T}.Acquire"/> at the top of its own scope and drops it at the
        /// end — there is no transfer of ownership from one holder to another. Every reference must reach
        /// exactly one release site, or the tile's
        /// <c>Allocator.Persistent</c> buffers leak.</para>
        ///
        /// <para><b>Who holds a reference in production, and where each is released.</b> The
        /// <c>TileManager.LoadedTile</c> record holds the creator's reference from the moment the fetch is
        /// observed until <c>TileManager.RenderTeardownRecord</c> releases it (cover change, eviction,
        /// restyle, teardown) — for the record's WHOLE in-cover lifetime now, kicked or not; there is no
        /// second release site for it. The mesh kick takes its OWN separate reference in
        /// <c>KickMeshBuild</c>'s main-thread prologue (before the pool lambda exists) and releases it from
        /// that lambda's <c>finally</c>. A parked symbol build takes its own via
        /// <see cref="SharedDisposable{T}.Acquire"/> while the kick's reference is still live — which is what
        /// keeps the tile alive across the <c>SetStyle</c>→<c>SpritesSettled</c> window and is why a parked
        /// build no longer re-decodes — and releases it from <c>PumpBuilds</c>' drain or from
        /// <c>SymbolSubsystem.DrainAndDiscardParkedBuilds</c>. A fetch outcome nobody wants is released
        /// by <c>PendingDisposalQueue.DiscardFetchOutcome</c>.</para>
        ///
        /// <para><b>Failures are reported through the returned <see cref="UniTask{T}"/>, never thrown
        /// synchronously</b> — true of both implementations, and what lets the coordinator observe every
        /// outcome at one place. A fetch error and a decode fault both arrive here; a decode fault is a
        /// <c>TileDecodeException</c>, so the two can be told apart and logged apart.</para>
        ///
        /// <para><paramref name="ct"/> is <b>contract-only</b>: no production caller threads a token today
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
