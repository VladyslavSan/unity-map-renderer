using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.Data
{
    /// <summary>
    /// Fetches tile bytes from the local filesystem.
    /// <para>
    /// Path template tokens: <c>{z}</c>, <c>{x}</c>, <c>{y}</c>.
    /// Default template: <c>{z}/{x}/{y}.mvt</c> (relative to <see cref="RootDirectory"/>).
    /// </para>
    /// <para>
    /// Y convention: XYZ (slippy-map / MapLibre default). TMS Y-flip is NOT applied.
    /// </para>
    /// <para>
    /// A missing file returns <c>HasData=false</c>; an I/O error (permission denied, etc.) throws.
    /// </para>
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

        public async Task<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default)
        {
            string relativePath = _pathTemplate
                .Replace("{z}", coord.Z.ToString())
                .Replace("{x}", coord.X.ToString())
                .Replace("{y}", coord.Y.ToString());

            string fullPath = Path.Combine(_rootDirectory, relativePath);

            if (!File.Exists(fullPath))
                return TileResponse.Absent(TileEncoding.Mvt);

            ct.ThrowIfCancellationRequested();

            // File.ReadAllBytesAsync is available on .NET Standard 2.1+ and net5+.
            byte[] bytes = await ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            return new TileResponse(bytes, TileEncoding.Mvt);
        }

        /// <summary>
        /// Reads all bytes async. Uses File.ReadAllBytesAsync where available; falls back to a
        /// synchronous read wrapped in Task.Run for older runtimes. The Unity asmdef targets
        /// .NET Standard 2.1 which includes File.ReadAllBytesAsync; dotnet test targets net10.
        /// </summary>
        private static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return File.ReadAllBytesAsync(path, ct);
#else
            // Fallback for any profile that doesn't have the async overload.
            return Task.Run(() => File.ReadAllBytes(path), ct);
#endif
        }

        public void Dispose() { /* no managed resources to release */ }
    }
}
