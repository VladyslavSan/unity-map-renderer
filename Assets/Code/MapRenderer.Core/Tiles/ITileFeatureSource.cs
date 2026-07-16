using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;

namespace MapRenderer.Core.Tiles
{
    /// <summary>
    /// Epic A / A7 (design §B): the polymorphic decode-provisioning seam a <see cref="ITileFeatureSource"/>
    /// hands back — decode stays LAZY (not performed by <see cref="ITileFeatureSource.GetTile"/> itself), so
    /// the A4/A5b decode-once-shared invariant survives the raise unchanged. <c>SharedTileDecode</c> (Unity)
    /// implements this over a byte-lazy MVT decode; a future in-memory/GeoJSON source would return an EAGER
    /// handle wrapping a pre-sliced <see cref="IDecodedTile"/> (no bytes, no fetch) — same interface, no
    /// change to the coordinator.
    /// </summary>
    public interface IDecodedTileHandle
    {
        IDecodedTile GetOrDecode();
    }

    /// <summary>
    /// Epic A / A7 (design §"Raise the source interface to the decoded-tile level"): the coordinator's
    /// source boundary, raised from byte-centric (<c>IDataSource.FetchAsync → TileResponse</c>) to
    /// decoded-tile-provider (<c>GetTile → IDecodedTileHandle</c>). Byte-fetch, scheduling/caching, and
    /// <c>ITileDecoder</c> resolution are implementation details BELOW this interface — the MVT
    /// implementation (<c>MvtTileFeatureSource</c>, Unity) wraps the unchanged <c>TileScheduler</c>; a future
    /// non-byte source (in-memory features, GeoJSON) implements this directly with no
    /// <c>IDataSource</c>/bytes at all.
    ///
    /// <para>Future polymorphic-output axis (documented, NOT built by A7 — §D scope fence): today every
    /// implementation is vector (returns a handle over <see cref="ITileLayer"/>/<see cref="ITileFeature"/>).
    /// A raster source would return a different decode-result kind (e.g. a texture artifact) at this SAME
    /// seam — <see cref="IDecodedTile"/> is already the documented "universal, polymorphic-by-kind" level
    /// (see its doc in <c>DecodedTile.cs</c>), so no interface change is anticipated, only new implementations.</para>
    /// </summary>
    public interface ITileFeatureSource : System.IDisposable
    {
        /// <summary>Resolves one tile's decode-provisioning handle. Absent (404/204/missing) → a null
        /// handle, matching today's <c>TileResponse.HasData == false</c> — the coordinator renders nothing
        /// for it. A genuine error still throws (unchanged fetch-error contract).</summary>
        UniTask<IDecodedTileHandle> GetTile(TileId id, CancellationToken ct = default);

        /// <summary>Releases interest in a tile (cancel in-flight, evict from any internal cache) — the
        /// raised form of <c>TileScheduler.Release</c>.</summary>
        void Release(TileId id);

        /// <summary>The in-flight request count — the raised form of <c>TileScheduler.InFlightCount</c>.</summary>
        int InFlightCount { get; }
    }
}
