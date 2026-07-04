using System;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// S82: an opaque, equatable cache-key token identifying the active style — one component of
    /// <see cref="PreparedTileCache"/>'s composite key (alongside <c>TileId</c> and layerId). MapView supplies
    /// it (wrapping its own <c>StyleId</c>); the cache never interprets the string, only compares it — so
    /// prepared meshes under two different styles coexist (keying by styleId is COEXISTENCE, not a flush —
    /// see the S82 stage's Decision 2).
    ///
    /// A constant <see cref="Default"/> until S83's <c>SetStyle</c> supplies a real per-style id (S82 Risk 2);
    /// every tile-layer cached before then shares one token, which is correct (there is effectively one style).
    ///
    /// Value-equality + a stable hash so a composite Dictionary key built from it is zero-boxing (mirrors
    /// <c>TileManager.LoadedKey</c>/<c>SourceKey</c>). <c>null</c> normalises to <see cref="string.Empty"/> so
    /// the token is always a valid, comparable key.
    /// </summary>
    internal readonly struct StyleToken : IEquatable<StyleToken>
    {
        public readonly string Id;

        public StyleToken(string id) { Id = id ?? string.Empty; }

        /// <summary>The pre-S83 constant token — every tile-layer cached before a real per-style id exists
        /// shares this one (correct: there is effectively one active style).</summary>
        public static readonly StyleToken Default = new StyleToken(string.Empty);

        public bool Equals(StyleToken other) => Id == other.Id;
        public override bool Equals(object obj) => obj is StyleToken other && Equals(other);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id;
    }
}
