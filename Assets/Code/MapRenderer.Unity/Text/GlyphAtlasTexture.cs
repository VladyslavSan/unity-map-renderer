// Non-obvious why: write `int2` via `using Unity.Mathematics;`, never inline `Unity.Mathematics.int2` —
// inside `MapRenderer.Unity.*` the leading `Unity` binds to `MapRenderer.Unity` (CS0234).

using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// The Unity half of the SDF glyph atlas: an R8 <see cref="Texture2DArray"/> with one layer per
    /// <see cref="GlyphAtlas"/> page, each layer sized to <see cref="GlyphAtlas.Size"/>. It is the only
    /// point where the engine-free atlas touches <c>UnityEngine</c>. A one-page atlas gives one layer, and
    /// every glyph samples layer 0. Main-thread only: call <see cref="Upload"/> after any off-thread
    /// fetch/decode work resumes on the main thread.
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
