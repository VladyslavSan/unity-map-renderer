// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// The response from a sprite-sheet fetch (<see cref="ISpriteSource.FetchAsync"/>) — the index JSON
    /// text plus the raw sheet PNG bytes, fetched together since a sheet is a single keyless pair (unlike
    /// glyph ranges, which are fontstack+range-keyed). Mirrors <see cref="GlyphRangeResponse"/>'s
    /// absent-vs-error shape: <see cref="HasData"/> is false when the sheet is explicitly absent (HTTP
    /// 204/404, missing file) — a defined "not found," never a throw — as opposed to a thrown error
    /// (network failure, HTTP 5xx, IO error).
    /// </summary>
    public readonly struct SpriteResponse
    {
        /// <summary>The sprite index JSON document's raw text (e.g. <c>sprite.json</c>).</summary>
        public string Json { get; init; }

        /// <summary>The sprite sheet image's raw PNG bytes (e.g. <c>sprite.png</c>).</summary>
        public byte[] Png { get; init; }

        /// <summary>True when both <see cref="Json"/> and <see cref="Png"/> were fetched.</summary>
        public bool HasData { get; init; }

        /// <summary>Creates an absent-sheet response (HTTP 204/404 or missing file).</summary>
        public static SpriteResponse Absent() => default;
    }
}
