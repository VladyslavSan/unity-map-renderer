using System;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// An opaque, equatable cache-key token identifying the active style — one component of
    /// <see cref="PreparedTileCache"/>'s composite key (alongside <c>TileId</c> and layerId). MapView
    /// supplies it; the cache never interprets the string, only compares it — so prepared meshes under two
    /// different tokens coexist: keying by content is COEXISTENCE, not a flush.
    ///
    /// The id is a digest of <c>styleId</c> PLUS the style document's CONTENT PLUS the layer
    /// numbering <c>RenderLayerSet.Build</c> actually produced (<see cref="Map.MapView.SetStyle"/>,
    /// <c>JsonCanonical.CacheKey</c>) — the numbering component matters because a <c>MapMaterialSet</c> field
    /// Build reads is live-mutable and not part of the style document, so it can shift dense layer ids under
    /// unchanged content. <c>JsonCanonical.CacheKey</c> is a 64-bit FNV-1a hash, not a cryptographic one — two
    /// runs differing in ANY of these digest unequal for any pair this session will actually produce, which
    /// is what lets the cache skip a separate purge condition; it is not a guarantee against a hash collision.
    ///
    /// Value-equality + a stable hash so a composite Dictionary key built from it is zero-boxing (mirrors
    /// <c>TileManager.LoadedKey</c>/<c>SourceKey</c>). <c>null</c> normalises to <see cref="string.Empty"/> so
    /// the token is always a valid, comparable key.
    /// </summary>
    internal readonly struct StyleToken : IEquatable<StyleToken>
    {
        public readonly string Id;

        public StyleToken(string id) { Id = id ?? string.Empty; }

        /// <summary>The constant token used before <see cref="Map.MapView.SetStyle"/> has supplied a real
        /// content-derived token — every tile-layer cached under it shares this one token.</summary>
        public static readonly StyleToken Default = new StyleToken(string.Empty);

        public bool Equals(StyleToken other) => Id == other.Id;
        public override bool Equals(object obj) => obj is StyleToken other && Equals(other);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id;
    }
}
