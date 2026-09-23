using MapRenderer.Core.Data;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The <b>tile loader</b> seam — builds the per-tile <see cref="IDataSource"/> for a resolved
    /// tile URL template, dispatching on scheme (<see cref="MapUri"/>). Non-local invariant: a
    /// <c>file://</c> chain uses only <see cref="FileDataSource"/> (plain file IO, engine-free, zero
    /// network) and never constructs a <see cref="UnityWebRequestDataSource"/> — a cheap structural guard
    /// the offline tooth leans on. Distinct from <see cref="StyleDocumentLoader"/> (one-shot style/TileJSON
    /// read): merging the two would muddy the offline guarantee.
    /// </summary>
    internal static class TileDataSourceFactory
    {
        /// <summary>
        /// Creates the data source for a tile-URL <paramref name="tileTemplate"/> (with <c>{z}/{x}/{y}</c>
        /// tokens). <c>file://</c> → <see cref="FileDataSource"/> (the local path is the template; tokens are
        /// substituted by <see cref="FileDataSource"/> itself, so the root is empty and
        /// <c>Path.Combine("", path)</c> returns the path unchanged). <c>http(s)://</c> →
        /// <see cref="UnityWebRequestDataSource"/>.
        /// </summary>
        public static IDataSource Create(string tileTemplate)
        {
            if (MapUri.IsFile(tileTemplate))
                return new FileDataSource(string.Empty, MapUri.LocalPath(tileTemplate));

            return new UnityWebRequestDataSource(tileTemplate);
        }
    }
}
