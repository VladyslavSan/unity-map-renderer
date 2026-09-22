// Engine-free by design (mirrors TileScheduler's decoupling from UnityWebRequestDataSource): this
// file references only System/Cysharp.Threading.Tasks/
// MapRenderer.Core.Text — NO UnityEngine using. It physically lives under MapRenderer.Unity (the
// atlas/texture wiring it feeds is Unity-only) but stays compilable standalone so its logic runs in
// BOTH the Unity EditMode runner and the fast Tools/core-tests project (the matching
// <Compile Include> lives in Tools/core-tests/core-tests.csproj). Do NOT add a UnityEngine reference
// here, and do NOT reference UnityWebRequestGlyphSource directly (that WOULD drag UnityEngine.Networking
// into this file's compile unit) — the caller builds the concrete IGlyphSource from
// StyleDocument.Glyphs and injects it (mirrors TileDataSourceFactory feeding TileScheduler).

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
    /// fetch/decode/cache/atlas pipeline a <c>text-font</c> stack needs.
    ///
    /// <para>
    /// <b>Fetch model:</b>
    /// fetches happen PER INDIVIDUAL FONT NAME in a <see cref="FontStack"/>'s <see cref="FontStack.Names"/>
    /// — not the joined <see cref="FontStack.RequestToken"/>. For each font name: <see cref="IGlyphSource.FetchAsync"/>
    /// → <see cref="GlyphPbfDecoder.Decode"/> → store under <c>(fontName, rangeStart)</c> in
    /// <see cref="GlyphCache"/> → append every decoded glyph to the shared <see cref="Atlas"/>. A
    /// <see cref="FontStackResolver"/> (constructed by <see cref="CreateResolver"/>) then resolves a
    /// codepoint against the per-font-name cache entries in stack order, giving correct fallback.
    /// A single-entry stack (e.g. the demotiles composite "Noto Sans Regular") is simply the degenerate
    /// one-name case of this same loop.
    /// </para>
    ///
    /// <para>
    /// The <see cref="IGlyphSource"/> passed to the constructor is normally built from the style's root
    /// <see cref="MapRenderer.Core.Style.StyleDocument.Glyphs"/> URL template — e.g.
    /// <c>new GlyphManager(new UnityWebRequestGlyphSource(style.Glyphs))</c> — the caller's job, mirroring
    /// how <c>TileDataSourceFactory</c> builds a tile <c>IDataSource</c> outside <c>TileScheduler</c>. That
    /// keeps this class decoupled from any concrete transport and fully testable with an in-memory fake.
    /// </para>
    ///
    /// <para>
    /// Main-thread-only for atlas mutation: <see cref="EnsureFontRangeAsync"/>'s only <c>await</c> is the
    /// fetch itself; the decode/cache/atlas-append steps that follow run synchronously on whatever
    /// context the fetch resumed on (Unity's <c>UnityWebRequest</c>-backed source resumes on the main
    /// thread via its PlayerLoop-bound <c>.ToUniTask()</c>, exactly like <c>UnityWebRequestDataSource</c>).
    /// </para>
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

        /// <param name="fontName">The REQUESTED name, not <see cref="FontStackGlyphs.Name"/>. The two
        /// differ: the decoder fills Name from the name embedded IN the PBF, while the cache and
        /// <see cref="FontStackResolver"/> both key on what the style asked for. Interning the embedded one
        /// files every glyph under an id no lookup ever asks for, and the map renders no text at all.</param>
        private void AppendToAtlas(string fontName, FontStackGlyphs glyphs)
        {
            if (glyphs.Glyphs == null) return;
            // The id is per FONT, so it is taken once per decoded range, not per glyph. Without it the
            // "already present" skip below reads as "some other face already has this codepoint" and drops
            // this face's glyph — which is how a whole map ends up rendering in one arbitrary face.
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
