using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The local-filesystem transport. Missing file: returns <c>null</c> (absent); I/O error: throws.
    /// Non-obvious why: <see cref="FetchOffMainAsync"/> switches to the ThreadPool and never back, as a
    /// PlayerLoop continuation never runs under synchronous polling. On WebGL the read runs inline
    /// (docs/web-target.md).
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

            // Never switch back (see the class doc). Cancellation is checked only before the read starts;
            // a read in progress runs to completion.
#if !UNITY_WEBGL || UNITY_EDITOR
            await UniTask.SwitchToThreadPool();
#endif
            ct.ThrowIfCancellationRequested();
            return File.ReadAllBytes(path);
        }
    }
}
