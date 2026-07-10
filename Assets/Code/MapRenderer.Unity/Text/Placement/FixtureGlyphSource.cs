using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Text;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// S20 Slice 1 demo-only <see cref="IGlyphSource"/>: serves the committed fixture glyph-PBF ranges
    /// under <c>Assets/Fixtures/glyphs/&lt;fontStack&gt;/&lt;rangeStart&gt;-&lt;rangeStart+255&gt;.pbf.bytes</c>
    /// (the same fixture <c>GlyphAtlasTextureTests</c>/<c>GlyphPbfDecodeTests</c> load), so
    /// <c>SyntheticLabelSource</c>'s eyeball demo renders REAL SDF glyphs with no network dependency. NOT
    /// a production data source — production (S105) wires the real <c>UnityWebRequestGlyphSource</c>
    /// against a style's <c>glyphs</c> URL template.
    /// </summary>
    public sealed class FixtureGlyphSource : IGlyphSource
    {
        public UniTask<GlyphRangeResponse> FetchAsync(string fontStack, int rangeStart, CancellationToken ct = default)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "glyphs", fontStack ?? string.Empty,
                $"{rangeStart}-{rangeStart + 255}.pbf.bytes");

            if (!File.Exists(path))
            {
                return UniTask.FromResult(GlyphRangeResponse.Absent());
            }

            byte[] bytes = File.ReadAllBytes(path);
            return UniTask.FromResult(new GlyphRangeResponse(bytes));
        }

        public void Dispose()
        {
        }
    }
}
