namespace MapRenderer.Core.Style
{
    /// <summary>
    /// Resolves a <see cref="SourceDefinition"/>'s tile fields from its TileJSON document. In the
    /// MapLibre model a source declares its tiles either inline (<c>tiles[]</c>) or indirectly via a
    /// TileJSON <c>url</c>; this is the "indirect → resolved" step that fills
    /// <c>Tiles</c>/<c>MinZoom</c>/<c>MaxZoom</c>/<c>Scheme</c>/<c>Bounds</c> from the parsed
    /// <see cref="TileJson"/>. Engine-free; pure parse-and-fill.
    ///
    /// <b>Fetching</b> the TileJSON document (file://, http) and deciding <i>when</i> to do it (once at
    /// SetStyle time) is the integration concern of S83b — deliberately not here, which is what keeps
    /// this unit engine-free and fast-core-testable.
    /// </summary>
    public static class SourceResolver
    {
        /// <summary>
        /// True when <paramref name="source"/> must have its tiles resolved from a TileJSON document:
        /// it carries a <c>url</c> and has no inline <c>tiles[]</c>. A source with inline tiles
        /// short-circuits (the caller need not fetch its TileJSON at all). The fetch-side counterpart
        /// of the <see cref="Resolve"/> short-circuit.
        /// </summary>
        public static bool NeedsTileJson(SourceDefinition source)
            => source != null
               && (source.Tiles == null || source.Tiles.Length == 0)
               && !string.IsNullOrEmpty(source.Url);

        /// <summary>
        /// Fills <paramref name="source"/>'s <c>Tiles</c>/<c>MinZoom</c>/<c>MaxZoom</c>/<c>Scheme</c>/
        /// <c>Bounds</c> from <paramref name="tileJson"/> and returns the same instance.
        ///
        /// <b>Inline <c>tiles[]</c> wins:</b> a source that already lists tiles is returned UNCHANGED —
        /// the TileJSON is never applied (and a well-behaved caller, via <see cref="NeedsTileJson"/>,
        /// will not even have fetched it). A null <paramref name="tileJson"/> (e.g. the document failed
        /// to load) also leaves the source unchanged.
        /// </summary>
        public static SourceDefinition Resolve(SourceDefinition source, TileJson tileJson)
        {
            if (source == null) return null;

            // Inline tiles short-circuit: never overwrite or re-derive an explicitly-listed tiles[].
            if (source.Tiles != null && source.Tiles.Length > 0) return source;
            if (tileJson == null) return source;

            source.Tiles = tileJson.Tiles;
            source.MinZoom = tileJson.MinZoom;
            source.MaxZoom = tileJson.MaxZoom;
            source.Scheme = tileJson.Scheme;
            source.Bounds = tileJson.Bounds;
            return source;
        }
    }
}
