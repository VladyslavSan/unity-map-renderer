using Cysharp.Threading.Tasks;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The one place a tile is decoded and the one place a <see cref="SharedDisposable{T}"/> over it is
    /// minted. Every
    /// <see cref="ITileFeatureSource"/> implementation calls it from inside its <c>GetTile</c> task, so
    /// "decode at fetch completion, under the caller's chosen <see cref="IWorkScheduler"/> policy, exactly
    /// once" is a property of this helper rather than of each source remembering to arrange it.
    ///
    /// <para><b>Why shared and not one hop per source.</b> Two contracts would otherwise be duplicated. The
    /// first is completion staying OFF the PlayerLoop: a scheduler's <c>UniTaskCompletionSource</c> completes
    /// (<c>TrySet*</c>) and runs its continuation INLINE on whichever thread the policy dispatched the body
    /// to — never marshalled to the PlayerLoop — so <c>TileManager.DrainMeshBuilds</c> and <c>DoDispose</c>,
    /// which spin on <c>req.Status.IsCompleted()</c> from the main thread <b>without pumping the
    /// PlayerLoop</b>, never deadlock on it. WHICH thread that is is the scheduler's call:
    /// <see cref="ThreadPoolWorkScheduler"/> (desktop) reproduces yesterday's off-main decode;
    /// <see cref="InlineWorkScheduler"/> (WebGL) runs it on the calling thread by design — the off-PlayerLoop
    /// property holds either way. The second contract is the profiler marker name, which must stay stable
    /// across the move so live profiles taken before it still line up. One helper means a future third source
    /// cannot get either wrong.</para>
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

        /// <summary>Decodes one tile under <paramref name="scheduler"/>'s policy and hands back a
        /// <see cref="SharedDisposable{T}"/> holding the caller's ONE reference. The caller owns it: it must
        /// reach exactly one release site.
        ///
        /// <para>A decoder throw becomes a <see cref="TileDecodeException"/> and faults the returned task —
        /// nothing is minted, so a failed decode can never leak a reference. The tile address
        /// goes IN here, at the only mint site, and is never supplied again.</para></summary>
        /// <param name="bytes">The encoded payload, or <see langword="null"/> for a source whose decoder
        /// carries its own payload (the GeoJSON dataset) — <see cref="ITileDecoder.Decode"/> documents it.</param>
        /// <param name="scheduler">The execution policy — <see cref="ThreadPoolWorkScheduler"/> reproduces
        /// today's off-main decode; <see cref="InlineWorkScheduler"/> is the WebGL-correct alternative.</param>
        internal static UniTask<SharedDisposable<IDecodedTile>> DecodeAsync(
            TileId id, byte[] bytes, ITileDecoder decoder, IWorkScheduler scheduler)
        {
            return scheduler.Schedule(_ =>
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
            }).ToUniTask();
        }
    }
}
