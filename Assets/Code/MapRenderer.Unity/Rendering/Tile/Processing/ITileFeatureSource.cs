using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The coordinator's source boundary: a decoded-tile provider
    /// (<c>GetTile → SharedDisposable&lt;IDecodedTile&gt;</c>), not a byte fetch. Byte-fetch, scheduling/caching
    /// and <c>ITileDecoder</c> resolution sit below it: the MVT source wraps <c>TileScheduler</c>, and
    /// <c>GeoJsonTileFeatureSource</c> has no bytes at all.
    /// <see cref="IDecodedTile"/> is polymorphic by kind, so a raster source needs only a new implementation.
    /// </summary>
    public interface ITileFeatureSource : System.IDisposable
    {
        /// <summary>Resolves one tile's decode-provisioning handle, <b>already decoded</b>. Absent
        /// (404/204/missing, or a tile the dataset provably cannot reach) → <see langword="null"/>, matching
        /// <c>TileResponse.HasData == false</c>; the coordinator renders nothing for it.
        ///
        /// <para>Non-local invariant: the handle is a reference count (<see cref="SharedDisposable{T}"/>), and
        /// every reference must reach exactly one release site, or the tile's <c>Allocator.Persistent</c>
        /// buffers leak. The LAST <see cref="SharedDisposable{T}.Release"/> frees them. The caller owns the one
        /// reference it receives. Every further holder takes its OWN with <see cref="SharedDisposable{T}.Acquire"/>
        /// and releases it once; only the mesh kick's reference travels with its build output.</para>
        ///
        /// <para>Production holders and their release sites. The <c>TileManager.LoadedTile</c> record holds the
        /// creator's reference for its whole in-cover lifetime, kicked or not; only
        /// <c>TileManager.RenderTeardownRecord</c> releases it. The mesh kick acquires its own in
        /// <c>KickMeshBuild</c>'s main-thread prologue. A synchronous hand-off throw releases it in the outer
        /// <c>catch</c>, and a fault in the pool lambda releases it in the lambda's <c>catch</c>. On success it
        /// moves to <c>TilePrologueOutput.Decode</c>: <c>TileBuildGraph.Dispose</c> releases it after
        /// <c>ScheduleMeasureFromDecode</c> (whose <c>catch</c> releases it on a scheduling throw), or
        /// <c>TilePrologueOutput.Dispose</c> releases it from a pen drain. A parked symbol build acquires its own
        /// while the kick's is live, so the tile stays alive across <c>SetStyle</c>→<c>SpritesSettled</c> with
        /// no re-decode; <c>PumpBuilds</c>' drain or <c>SymbolSubsystem.DrainAndDiscardParkedBuilds</c> releases
        /// it. <c>PendingDisposalQueue.DiscardFetchOutcome</c> releases a fetch outcome nobody wants.</para>
        ///
        /// <para>Both implementations report failures through the returned <see cref="UniTask{T}"/>, never by a
        /// synchronous throw, so the coordinator observes every outcome in one place. A decode fault is a
        /// <c>TileDecodeException</c>, so it logs apart from a fetch error.</para>
        ///
        /// <para><paramref name="ct"/> is contract-only: <c>TileManager.Tick</c>, the sole call site, passes
        /// none. An HTTP abort flows through <see cref="Release"/> into the scheduler's per-tile token. An
        /// implementation still honours <paramref name="ct"/>, so a caller that passes one cannot mint handles
        /// through a teardown.</para></summary>
        UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default);

        /// <summary>Releases interest in a tile (cancel in-flight, evict from any internal cache) — the
        /// raised form of <c>TileScheduler.Release</c>.</summary>
        void Release(TileId id);

        /// <summary>The in-flight request count — the raised form of <c>TileScheduler.InFlightCount</c>.</summary>
        int InFlightCount { get; }
    }
}
