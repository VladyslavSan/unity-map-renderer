using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;

namespace MapRenderer.Core.Data
{
    /// <summary>
    /// Fetches tile bytes from the local filesystem. Path template tokens: <c>{z}</c>, <c>{x}</c>, <c>{y}</c>
    /// (default <c>{z}/{x}/{y}.mvt</c>, relative to <see cref="RootDirectory"/>). Y convention: XYZ, with no
    /// TMS Y-flip. Missing file: <c>HasData=false</c>; I/O error: throws. Non-obvious why: FetchAsync
    /// switches to the ThreadPool and never back, as a PlayerLoop continuation never runs under synchronous
    /// polling; <c>RunOnThreadPool(configureAwait: false)</c> is absent from the <c>dotnet test</c> build.
    /// On WebGL the read runs inline (docs/web-target.md).
    /// </summary>
    public sealed class FileDataSource : IDataSource
    {
        private readonly string _rootDirectory;
        private readonly string _pathTemplate;

        public TileEncoding Encoding => TileEncoding.Mvt;

        public string RootDirectory => _rootDirectory;

        /// <summary>
        /// Constructs with a root directory and an optional path template.
        /// </summary>
        /// <param name="rootDirectory">Base directory; tile paths are resolved relative to it.</param>
        /// <param name="pathTemplate">
        /// Optional path template using <c>{z}</c>, <c>{x}</c>, <c>{y}</c> tokens.
        /// Defaults to <c>{z}/{x}/{y}.mvt</c>.
        /// </param>
        public FileDataSource(string rootDirectory, string pathTemplate = "{z}/{x}/{y}.mvt")
        {
            _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
            _pathTemplate  = pathTemplate  ?? throw new ArgumentNullException(nameof(pathTemplate));
        }

        public async UniTask<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default)
        {
            string relativePath = _pathTemplate
                .Replace("{z}", coord.Z.ToString())
                .Replace("{x}", coord.X.ToString())
                .Replace("{y}", coord.Y.ToString());

            string fullPath = Path.Combine(_rootDirectory, relativePath);

            if (!File.Exists(fullPath))
                return TileResponse.Absent(TileEncoding.Mvt);

            ct.ThrowIfCancellationRequested();

            // Never switch back (see the class doc). Cancellation is checked only before the read starts;
            // a read in progress runs to completion.
#if !UNITY_WEBGL || UNITY_EDITOR
            await UniTask.SwitchToThreadPool();
#endif
            ct.ThrowIfCancellationRequested();
            byte[] data = File.ReadAllBytes(fullPath);
            return new TileResponse(data, TileEncoding.Mvt);
        }

        public void Dispose() { /* no managed resources to release */ }
    }
}
