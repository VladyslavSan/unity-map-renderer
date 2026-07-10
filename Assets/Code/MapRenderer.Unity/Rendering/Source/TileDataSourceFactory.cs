using MapRenderer.Core.Data;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// S83b: the <b>tile loader</b> seam — builds the per-tile <see cref="IDataSource"/> for a resolved tile
    /// URL template, dispatching on scheme (<see cref="MapUri"/>). This is the seam that makes the offline
    /// story real and checkable:
    /// <list type="bullet">
    ///   <item><c>file://</c> / bare path → <see cref="FileDataSource"/> (plain file IO, engine-free, zero
    ///     network). A <c>file://</c> chain therefore NEVER constructs a
    ///     <see cref="UnityWebRequestDataSource"/> — a cheap structural guard the offline tooth leans on.</item>
    ///   <item><c>http(s)://</c> → <see cref="UnityWebRequestDataSource"/>.</item>
    /// </list>
    /// Distinct from the <see cref="StyleDocumentLoader"/> (one-shot style/TileJSON read): merging the two
    /// would muddy the offline guarantee.
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
