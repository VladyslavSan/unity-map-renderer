namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// S83b: tiny scheme classifier for map URIs (style docs, TileJSON docs, and tile-URL templates). The
    /// two loader seams (document loader, tile-source factory) dispatch on these so the offline story is
    /// explicit: a <c>file://</c> URI is local file IO, never a network call.
    ///
    /// <para>The local-path extraction is a deliberate <b>textual</b> strip of the <c>file://</c> prefix
    /// rather than <c>new System.Uri(...).LocalPath</c>: a tile-URL <i>template</i> carries <c>{z}/{x}/{y}</c>
    /// tokens that are not valid URI characters, so <c>System.Uri</c> parsing is unreliable for them. POSIX
    /// (macOS/Linux — this project's targets): <c>file:///abs/path</c> → <c>/abs/path</c>.</para>
    /// </summary>
    internal static class MapUri
    {
        private const string FileScheme = "file://";

        /// <summary>True for a <c>file://</c> URI, or a bare path with no scheme (treated as a local file).</summary>
        public static bool IsFile(string uri)
            => !string.IsNullOrEmpty(uri) && (uri.StartsWith(FileScheme) || !HasScheme(uri));

        /// <summary>True for an <c>http://</c> or <c>https://</c> URI.</summary>
        public static bool IsHttp(string uri)
            => !string.IsNullOrEmpty(uri) && (uri.StartsWith("http://") || uri.StartsWith("https://"));

        /// <summary>The local filesystem path for a <c>file://</c> URI (or a bare path, returned as-is).</summary>
        public static string LocalPath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;
            return uri.StartsWith(FileScheme) ? uri.Substring(FileScheme.Length) : uri;
        }

        // A scheme is "<letters>://" at the start. Bare paths (absolute or relative) have none.
        private static bool HasScheme(string uri)
        {
            int sep = uri.IndexOf("://", System.StringComparison.Ordinal);
            if (sep <= 0) return false;
            for (int i = 0; i < sep; i++)
            {
                char c = uri[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
            }
            return true;
        }
    }
}
