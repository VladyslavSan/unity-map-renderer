using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// S83b: the <b>document loader</b> — a one-shot read of a <i>style JSON</i> or a <i>TileJSON</i>
    /// document (NOT per-tile bytes; that is the tile-source factory's job). Dispatches on scheme
    /// (<see cref="MapUri"/>):
    /// <list type="bullet">
    ///   <item><c>file://</c> / bare path → <see cref="File.ReadAllText"/> on the ThreadPool — true
    ///     zero-network IO a headless test can assert.</item>
    ///   <item><c>http(s)://</c> → <see cref="UnityWebRequest"/>.</item>
    /// </list>
    ///
    /// <para>The <c>file://</c> path reads <b>synchronously on the calling thread</b> (no thread hop).
    /// This is deliberate and the opposite of <c>FileDataSource</c> (which threadpools because it runs
    /// per-tile in the live loop): the document load is a <i>one-shot</i> style/TileJSON read at SetStyle
    /// time, and <c>MapView.SetStyle</c>'s continuation then does <b>main-thread-only</b> Unity work
    /// (Material creation, backend construction). Staying on the calling (main) thread keeps that work on
    /// the main thread with NO PlayerLoop-dependent <c>SwitchToMainThread</c> — so a headless test can drive
    /// SetStyle to completion by spinning, and a <c>file://</c> chain never touches the network. (The
    /// <c>http</c> path DOES need the PlayerLoop via <c>ToUniTask</c>, and resumes on the main thread — fine
    /// for runtime; the offline tests use <c>file://</c> throughout.)</para>
    /// </summary>
    internal static class StyleDocumentLoader
    {
        /// <summary>Reads the document at <paramref name="uri"/> as text. Throws on IO/HTTP error (the
        /// caller decides per-document whether that is fatal or isolated — S83b decision 4 failure isolation).</summary>
        public static async UniTask<string> LoadTextAsync(string uri, CancellationToken ct = default)
        {
            if (MapUri.IsHttp(uri))
            {
                using var req = UnityWebRequest.Get(uri);
                req.downloadHandler = new DownloadHandlerBuffer();
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
                return req.downloadHandler.text;
            }

            // file:// or bare path → synchronous local file IO on the calling thread (see class remarks:
            // keeps SetStyle's Unity work on the main thread, no PlayerLoop dependency). The `async`/`await`
            // in the http branch above makes the state machine complete this return inline.
            string path = MapUri.LocalPath(uri);
            ct.ThrowIfCancellationRequested();
            return File.ReadAllText(path);
        }
    }
}
