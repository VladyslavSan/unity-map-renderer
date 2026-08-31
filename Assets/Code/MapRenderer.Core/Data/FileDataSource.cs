using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;

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
    ///
    /// S51: FetchAsync returns UniTask&lt;TileResponse&gt; (was Task). On desktop/editor it switches to the
    /// ThreadPool via UniTask.SwitchToThreadPool(), then does the synchronous File.ReadAllBytes work,
    /// then returns WITHOUT switching back to the main thread (no UniTask.SwitchToMainThread).
    ///
    /// This "switch-then-work-no-return" pattern is equivalent to UniTask.RunOnThreadPool with
    /// configureAwait: false. configureAwait: false (no return) is REQUIRED: the alternative
    /// (UniTask.Run / RunOnThreadPool with the default configureAwait: true) posts the final
    /// continuation via UniTask.Yield() to the Unity PlayerLoop, which never advances when
    /// polled synchronously from test helpers or DrainMeshBuilds. Without the PlayerLoop pump,
    /// the task never reaches Succeeded — GetResult() throws "Not yet completed" and DrainMeshBuilds
    /// never observes completion. Completing on the ThreadPool (no return) makes IsCompleted true
    /// immediately.
    ///
    /// <b>WebGL player exception:</b> the managed ThreadPool has no workers there (see
    /// docs/threading-on-web.md) — a queued SwitchToThreadPool continuation would never run, hanging
    /// this fetch forever exactly like <see cref="MapRenderer.Core.Data.TileScheduler"/>'s own
    /// now-removed hop did. The switch is therefore guarded to desktop/editor only
    /// (<c>#if !UNITY_WEBGL || UNITY_EDITOR</c>); on a WebGL player the read runs synchronously,
    /// inline on the calling thread — a real but accepted cost (the alternative is a permanent
    /// silent hang), the same trade-off the mesh path already accepts on WebGL.
    ///
    /// UniTask.SwitchToThreadPool() is available in both the NetCore NuGet build (headless dotnet test)
    /// and the vendored Unity build. UniTask.RunOnThreadPool is NOT available in the NetCore build,
    /// so this pattern is the portable equivalent. <c>UNITY_WEBGL</c> is undefined under
    /// <c>dotnet test</c>, so the fast test project keeps the desktop behaviour, unchanged.
    /// No PlayerLoop-dependent APIs are used here.
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

            // Switch to the ThreadPool, do the synchronous I/O work, then return WITHOUT switching
            // back to the main thread. This "switch-work-no-return" pattern is equivalent to
            // UniTask.RunOnThreadPool(configureAwait: false) and is the only portable pattern that
            // works in BOTH the NetCore NuGet build (dotnet test, no RunOnThreadPool method) and
            // the vendored Unity build. The result UniTask completes on the ThreadPool on desktop/editor,
            // or inline on WebGL (see the class doc); either way IsCompleted is true immediately after
            // return, with no PlayerLoop dependency.
            //
            // Guarded to desktop/editor: the managed ThreadPool has no workers on a WebGL player (see
            // docs/threading-on-web.md), so this switch would hang forever there. On WebGL the read
            // below runs synchronously, inline on the calling thread instead — see the class doc.
            //
            // CancellationToken is checked before the read starts; mid-read cancellation is not
            // guaranteed (acceptable, consistent with the old netstandard2.1 path).
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
