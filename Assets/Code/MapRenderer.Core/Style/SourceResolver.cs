namespace MapRenderer.Core.Style
{
    /// <summary>
    /// Resolves a <see cref="SourceDefinition"/>'s tile fields from its TileJSON document. A source declares
    /// its tiles inline (<c>tiles[]</c>) or through a TileJSON <c>url</c>; this step fills
    /// <c>Tiles</c>/<c>MinZoom</c>/<c>MaxZoom</c>/<c>Scheme</c>/<c>Bounds</c> from the parsed
    /// <see cref="TileJson"/>. It does not fetch the TileJSON document; the caller fetches it once, at
    /// SetStyle time. That keeps this type engine-free and fast-core-testable.
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
        /// Fills <paramref name="source"/>'s tile fields from <paramref name="tileJson"/> and returns the same
        /// instance. <b>Inline <c>tiles[]</c> wins:</b> a source that already lists tiles is returned UNCHANGED,
        /// and a caller that checks <see cref="NeedsTileJson"/> never fetches its TileJSON. A null
        /// <paramref name="tileJson"/> (the document failed to load) also leaves the source unchanged.
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
