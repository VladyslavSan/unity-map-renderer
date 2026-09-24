// Non-local invariant: Tools/core-tests/core-tests.csproj compiles this file, so it must not reference
// UnityEngine or UnityWebRequestGlyphSource; the caller injects the concrete IGlyphSource.

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text;
using MapRenderer.Core.Lifetime;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Ties <see cref="IGlyphSource"/> + <see cref="GlyphPbfDecoder"/> +
    /// <see cref="GlyphCache"/> + <see cref="GlyphAtlas"/> + <see cref="FontStackResolver"/> into the
    /// fetch/decode/cache/atlas pipeline a <c>text-font</c> stack needs. It fetches per font NAME, not per
    /// joined <see cref="FontStack.RequestToken"/>, so <see cref="FontStackResolver"/> can fall back in
    /// stack order. Non-local invariant: the decode/cache/atlas steps run on the context the fetch resumes
    /// on, so the <see cref="IGlyphSource"/> must resume on the main thread (the
    /// <c>UnityWebRequest</c> source does).
    /// </summary>
    public sealed class GlyphManager : VerifiedDisposable
    {
        private readonly IGlyphSource _source;
        private readonly GlyphCache _cache;
        private readonly GlyphAtlas _atlas;

        private static readonly IReadOnlyDictionary<uint, SdfGlyph> EmptyGlyphs = new Dictionary<uint, SdfGlyph>();

        public GlyphManager(IGlyphSource source, GlyphAtlas atlas = null, GlyphCache cache = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _atlas = atlas ?? new GlyphAtlas();
            _cache = cache ?? new GlyphCache();
        }

        /// <summary>The shared SDF glyph atlas every ensured range's glyphs are appended to.</summary>
        public GlyphAtlas Atlas => _atlas;

        /// <summary>The keep-all-per-session decoded-range cache backing <see cref="CreateResolver"/>.</summary>
        public GlyphCache Cache => _cache;

        /// <summary>Builds a fallback-aware resolver over this manager's cache for the given font stack.
        /// It is handed the ATLAS as well, so the font ids it stamps onto shaped glyphs come from the same
        /// table <see cref="AppendToAtlas"/> keys the entries by.</summary>
        public FontStackResolver CreateResolver(FontStack fontStack)
            => new FontStackResolver(fontStack, _cache, _atlas);

        /// <summary>
        /// Ensures every font in <paramref name="fontStack"/>'s <see cref="FontStack.Names"/> has the
        /// 256-codepoint range covering <paramref name="codepoint"/> fetched, decoded, cached, and
        /// appended to <see cref="Atlas"/> — fetching each name in stack order (fallback fonts still get
        /// fetched even though only the winning one matters, since which font "wins" isn't known until
        /// <see cref="FontStackResolver.Resolve"/> runs against the populated cache).
        /// </summary>
        public async UniTask EnsureFontStackRangeAsync(FontStack fontStack, uint codepoint, CancellationToken ct = default)
        {
            if (fontStack?.Names == null) return;
            int rangeStart = FontStackResolver.ComputeRangeStart(codepoint);
            for (int i = 0; i < fontStack.Names.Count; i++)
            {
                string fontName = fontStack.Names[i];
                if (fontName == null) continue;
                await EnsureFontRangeAsync(fontName, rangeStart, ct);
            }
        }

        /// <summary>
        /// Ensures ONE font's 256-codepoint range is fetched/decoded/cached/appended. A no-op (no fetch)
        /// if that <c>(fontName, rangeStart)</c> pair is already cached — keep-all-per-session caching
        /// means every range is fetched at most once for the process's lifetime, including a
        /// range the source reported absent (cached as an empty range so a permanently-missing range
        /// doesn't get refetched every time it's requested).
        /// </summary>
        public async UniTask EnsureFontRangeAsync(string fontName, int rangeStart, CancellationToken ct = default)
        {
            if (_cache.TryGet(fontName, rangeStart, out _)) return;

            GlyphRangeResponse response = await _source.FetchAsync(fontName, rangeStart, ct);
            FontStackGlyphs glyphs = DecodeOrEmpty(fontName, rangeStart, response);
            _cache.Store(fontName, rangeStart, glyphs);
            AppendToAtlas(fontName, glyphs);
        }

        private static FontStackGlyphs DecodeOrEmpty(string fontName, int rangeStart, GlyphRangeResponse response)
        {
            if (response.HasData)
            {
                GlyphPbfRange decoded = GlyphPbfDecoder.Decode(response.Bytes);
                if (decoded.Stacks != null && decoded.Stacks.Count > 0)
                {
                    return decoded.Stacks[0];
                }
            }

            // Absent/empty is a defined outcome, never a throw — cache it so a permanently-missing
            // range (404/204, or a decoded PBF with zero stacks) is not refetched on every request.
            return new FontStackGlyphs
            {
                Name = fontName,
                RangeStart = rangeStart,
                RangeEnd = rangeStart + 255,
                Glyphs = EmptyGlyphs,
            };
        }

        /// <param name="fontName">The REQUESTED name, not the PBF-embedded <see cref="FontStackGlyphs.Name"/>:
        /// the cache and <see cref="FontStackResolver"/> key on the requested name.</param>
        private void AppendToAtlas(string fontName, FontStackGlyphs glyphs)
        {
            if (glyphs.Glyphs == null) return;
            // The skip below is per font id; a skip by codepoint alone drops this face's glyph when
            // another face already has that codepoint.
            int fontId = _atlas.FontId(fontName);
            foreach (var kv in glyphs.Glyphs)
            {
                if (!_atlas.TryGetEntry(fontId, kv.Key, out _))
                {
                    _atlas.Append(kv.Value, fontId);
                }
            }
        }

        protected override void DoDispose() => _source?.Dispose();
    }
}
