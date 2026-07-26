// Namespace-collision guard (fcd7145 just fixed this exact trap in a Text test): this file lives in
// MapRenderer.Unity.Text and uses Unity.Mathematics.int2 — a top-level `using Unity.Mathematics;` +
// unqualified `int2` is required. NEVER write the inline-qualified `Unity.Mathematics.int2` here: inside
// a `MapRenderer.Unity.*` namespace, the leading `Unity` segment binds to the CURRENT namespace
// (`MapRenderer.Unity`), not the global `Unity` root, so `Unity.Mathematics.int2` resolves to
// `MapRenderer.Unity.Mathematics.int2` (CS0234: no such namespace). `MapRenderer.Unity.Text` itself does
// not collide with any bare UnityEngine type (UnityEngine.UI.Text is nested under UnityEngine.UI, not a
// bare `Text` this assembly's code ever references unqualified).

using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S18 Unity-side batch: the deferred Unity half of Slice 2's SDF glyph atlas — wraps a
    /// <see cref="Texture2DArray"/> (single-channel <see cref="TextureFormat.R8"/>, one array LAYER per
    /// <see cref="GlyphAtlas"/> PAGE) and uploads each page's <see cref="GlyphAtlas.PagePixels"/> CPU
    /// buffer to its layer via <see cref="Texture2DArray.SetPixelData{T}(T[],int,int)"/> +
    /// <see cref="Texture2DArray.Apply()"/>, every layer sized to the atlas's (per-page) <see cref="GlyphAtlas.Size"/>.
    /// <see cref="GlyphAtlas"/> itself stays engine-free (Core) — this is the ONLY point an atlas touches
    /// <c>UnityEngine</c>.
    ///
    /// <para><b>Stage M — single-page byte-identical.</b> An atlas that never overflows a page
    /// (<see cref="GlyphAtlas.PageCount"/> == 1, the invariant for any map that fits) uploads a
    /// Texture2DArray with exactly ONE layer — layer 0 holds the SAME bytes a pre-Stage-M
    /// <see cref="Texture2D"/> would have, and every <c>SymbolQuad.Page</c>/<c>BillboardVertex.Page</c> is
    /// 0, so the shader's array sample at layer 0 is pixel-identical to a plain 2D sample. Multi-page only
    /// activates once a SECOND page opens.</para>
    ///
    /// Main-thread only (like every <c>Texture2D</c>/<c>Texture2DArray</c> mutation) — <see cref="Upload"/>
    /// must be called from the main thread, after any off-thread fetch/decode work has resumed there
    /// (mirrors every other GPU-resource boundary in this codebase — the mesh/backend disposal contract).
    /// </summary>
    public sealed class GlyphAtlasTexture : VerifiedDisposable
    {
        private Texture2DArray _texture;

        /// <summary>The uploaded texture array, or <c>null</c> before the first successful <see cref="Upload"/>.
        /// <see cref="Texture2DArray.depth"/> is the current page/layer count.</summary>
        public Texture2DArray Texture => _texture;

        /// <summary>
        /// (Re)creates the texture array if <paramref name="atlas"/>'s per-page size or page count has
        /// changed since the last upload, then uploads every page's current
        /// <see cref="GlyphAtlas.PagePixels"/> buffer to its layer. A no-op if the atlas has not packed
        /// anything yet (<c>Size.y == 0</c>) — a <see cref="Texture2DArray"/> requires a positive height.
        /// </summary>
        public void Upload(GlyphAtlas atlas)
        {
            if (atlas == null) throw new ArgumentNullException(nameof(atlas));

            int2 size = atlas.Size;
            if (size.x <= 0 || size.y <= 0) return;

            int pageCount = atlas.PageCount;
            if (_texture == null || _texture.width != size.x || _texture.height != size.y || _texture.depth != pageCount)
            {
                _texture.DestroySafely();
                _texture = new Texture2DArray(size.x, size.y, pageCount, TextureFormat.R8, mipChain: false, linear: true)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            for (int page = 0; page < pageCount; page++)
            {
                _texture.SetPixelData(atlas.PagePixels(page), 0, page);
            }
            _texture.Apply(updateMipmaps: false);
        }

        protected override void DoDispose()
        {
            _texture.DestroySafely();
            _texture = null;
        }
    }
}
