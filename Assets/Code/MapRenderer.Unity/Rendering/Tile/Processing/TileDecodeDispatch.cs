using Cysharp.Threading.Tasks;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The one place a tile is decoded and the one place a <see cref="SharedDisposable{T}"/> over it is
    /// minted. Every
    /// <see cref="ITileFeatureSource"/> implementation calls it from inside its <c>GetTile</c> task, so
    /// "decode at fetch completion, off the main thread, exactly once" is a property of this helper rather
    /// than of each source remembering to arrange it.
    ///
    /// <para><b>Why shared and not one hop per source.</b> Two contracts would otherwise be duplicated. The
    /// first is <c>configureAwait: false</c>: <c>TileManager.DrainMeshBuilds</c> and <c>DoDispose</c> spin
    /// on <c>req.Status.IsCompleted()</c> from the main thread <b>without pumping the PlayerLoop</b>, so a
    /// fetch task that completes via a PlayerLoop-posted continuation deadlocks that spin (§G-1). The second
    /// is the profiler marker name, which must stay stable across the move so live profiles taken before it
    /// still line up. One helper means a future third source cannot get either wrong.</para>
    /// </summary>
    internal static class TileDecodeDispatch
    {
        /// <summary>Profiler marker name constants (SSOT) for the tile decode — referenced by the
        /// <see cref="ProfilerMarker"/> field below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // Moved here from SharedTileDecode (itself moved from TileManager) — the NAME is unchanged both
            // times, so live profiles taken before either move still line up.
            internal const string TileDecode = "MapRenderer.Tile.Decode";
        }

        private static readonly ProfilerMarker PmTileDecode =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.TileDecode);

        /// <summary>Decodes one tile on the thread pool and hands back a <see cref="SharedDisposable{T}"/>
        /// holding the caller's ONE reference. The caller owns it: it must reach exactly one release site.
        ///
        /// <para>A decoder throw becomes a <see cref="TileDecodeException"/> and faults the returned task —
        /// nothing is minted, so a failed decode can never leak a reference. IR C1 P3: the tile address
        /// goes IN here, at the only mint site, and is never supplied again.</para></summary>
        /// <param name="bytes">The encoded payload, or <see langword="null"/> for a source whose decoder
        /// carries its own payload (the GeoJSON dataset) — <see cref="ITileDecoder.Decode"/> documents it.</param>
        internal static async UniTask<SharedDisposable<IDecodedTile>> DecodeAsync(TileId id, byte[] bytes, ITileDecoder decoder)
        {
            return await UniTask.RunOnThreadPool(() =>
            {
                IDecodedTile tile;
                try
                {
                    using (PmTileDecode.Auto())
                        tile = decoder.Decode(id, bytes);
                }
                catch (System.Exception ex)
                {
                    throw new TileDecodeException(id, ex);
                }
                return new SharedDisposable<IDecodedTile>(tile);
            }, configureAwait: false);
        }
    }
}
