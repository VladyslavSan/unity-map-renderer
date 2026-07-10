// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The response from a single glyph-PBF range fetch (<see cref="IGlyphSource.FetchAsync"/>). Mirrors
    /// <see cref="Data.TileResponse"/>'s absent-vs-error shape: <see cref="HasData"/> is false when the
    /// range is explicitly absent (HTTP 204/404, missing file) — a defined "not found," never a throw —
    /// as opposed to a thrown error (network failure, HTTP 5xx, IO error).
    /// </summary>
    public readonly struct GlyphRangeResponse
    {
        public readonly byte[] Bytes;
        public readonly bool HasData;

        public GlyphRangeResponse(byte[] bytes)
        {
            Bytes = bytes;
            HasData = true;
        }

        /// <summary>Creates an absent-range response (HTTP 204/404 or missing file).</summary>
        public static GlyphRangeResponse Absent() => default;
    }
}
