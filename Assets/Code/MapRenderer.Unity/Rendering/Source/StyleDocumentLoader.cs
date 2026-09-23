using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The <b>document loader</b> — a one-shot read of a <i>style JSON</i> or <i>TileJSON</i>
    /// document (not per-tile bytes; that is the tile-source factory's job). Dispatches on scheme
    /// (<see cref="MapUri"/>): <c>file://</c>/bare path → <see cref="File.ReadAllText"/>; <c>http(s)://</c>
    /// → <see cref="UnityWebRequest"/>. Non-local invariant: <c>MapView.SetStyle</c>'s continuation does
    /// main-thread-only Unity work (Material creation, backend construction), so the <c>file://</c> path
    /// reads synchronously on the CALLING (main) thread, unlike the per-tile <c>FileDataSource</c>. That
    /// needs no PlayerLoop-dependent <c>SwitchToMainThread</c>, so a headless test drives SetStyle to
    /// completion by spinning. The <c>http</c> path needs the PlayerLoop (<c>ToUniTask</c>) and resumes
    /// on the main thread.
    /// </summary>
    internal static class StyleDocumentLoader
    {
        /// <summary>Reads the document at <paramref name="uri"/> as text. Throws on IO/HTTP error — the
        /// caller decides per document whether that failure is fatal or isolated.</summary>
        public static async UniTask<string> LoadTextAsync(string uri, CancellationToken ct = default)
        {
            if (MapUri.IsHttp(uri))
            {
                using var req = UnityWebRequest.Get(uri);
                req.downloadHandler = new DownloadHandlerBuffer();
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
                return req.downloadHandler.text;
            }

            // file:// or bare path → synchronous local file IO on the calling thread (see the class summary).
            // The state machine completes this return inline, since only the http branch awaits.
            string path = MapUri.LocalPath(uri);
            ct.ThrowIfCancellationRequested();
            return File.ReadAllText(path);
        }
    }
}
