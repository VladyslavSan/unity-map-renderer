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
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S18 Unity-side batch: the deferred Unity half of Slice 2's SDF glyph atlas — wraps a single
    /// <see cref="Texture2D"/> (single-channel <see cref="TextureFormat.R8"/>) and uploads the Core
    /// <see cref="GlyphAtlas.Pixels"/> CPU buffer to it via <see cref="Texture2D.LoadRawTextureData(byte[])"/>
    /// + <see cref="Texture2D.Apply()"/>, sized to <see cref="GlyphAtlas.Size"/>. <see cref="GlyphAtlas"/>
    /// itself stays engine-free (Core) — this is the ONLY point an atlas touches <c>UnityEngine</c>.
    ///
    /// Main-thread only (like every <c>Texture2D</c> mutation) — <see cref="Upload"/> must be called from
    /// the main thread, after any off-thread fetch/decode work has resumed there (mirrors every other
    /// GPU-resource boundary in this codebase — the mesh/backend disposal contract).
    /// </summary>
    public sealed class GlyphAtlasTexture : IDisposable
    {
        private Texture2D _texture;

        /// <summary>The uploaded texture, or <c>null</c> before the first successful <see cref="Upload"/>.</summary>
        public Texture2D Texture => _texture;

        /// <summary>
        /// (Re)creates the texture if <paramref name="atlas"/>'s size has changed since the last upload,
        /// then uploads its current <see cref="GlyphAtlas.Pixels"/> buffer. A no-op if the atlas has not
        /// packed anything yet (<c>Size.y == 0</c>) — <see cref="Texture2D"/> requires a positive height.
        /// </summary>
        public void Upload(GlyphAtlas atlas)
        {
            if (atlas == null) throw new ArgumentNullException(nameof(atlas));

            int2 size = atlas.Size;
            if (size.x <= 0 || size.y <= 0) return;

            if (_texture == null || _texture.width != size.x || _texture.height != size.y)
            {
                _texture.DestroySafely();
                _texture = new Texture2D(size.x, size.y, TextureFormat.R8, mipChain: false, linear: true)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            _texture.LoadRawTextureData(atlas.Pixels);
            _texture.Apply(updateMipmaps: false);
        }

        public void Dispose()
        {
            _texture.DestroySafely();
            _texture = null;
        }
    }
}
