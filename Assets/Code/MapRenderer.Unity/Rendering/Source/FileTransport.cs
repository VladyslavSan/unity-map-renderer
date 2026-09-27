using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The local-filesystem transport. Missing file: returns <c>null</c> (absent); I/O error: throws.
    /// Two threading policies live here: <see cref="FetchOffMainAsync"/> (tiles) hops off main, because
    /// it is a bulk read with nowhere that needs it back on the calling thread; <see cref="ReadText"/>
    /// (the style document) stays inline, because <c>SetStyle</c>'s continuation creates Materials and a
    /// headless test drives it to completion by spinning, with no PlayerLoop to hop back through.
    /// </summary>
    internal static class FileTransport
    {
        /// <summary>Fetches raw bytes from the local path <paramref name="uri"/>. Returns <c>null</c> when
        /// the file does not exist.</summary>
        public static async UniTask<byte[]> FetchOffMainAsync(string uri, CancellationToken ct)
        {
            string path = MapUri.LocalPath(uri);

            if (!File.Exists(path))
                return null;

            ct.ThrowIfCancellationRequested();

            // The return hop is never taken because it needs the PlayerLoop, which never runs under
            // synchronous polling. On WebGL the read runs inline.
#if !UNITY_WEBGL || UNITY_EDITOR
            await UniTask.SwitchToThreadPool();
#endif
            ct.ThrowIfCancellationRequested();
            return File.ReadAllBytes(path);
        }

        /// <summary>Reads the local path <paramref name="uri"/> as text, inline on the calling thread.
        /// Returns <c>null</c> when the file does not exist.</summary>
        public static string ReadText(string uri)
        {
            string path = MapUri.LocalPath(uri);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
