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
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// I4 — the Unity-side sprite sheet: decodes a MapLibre sprite PNG into a single <see cref="Texture2D"/>
    /// and pairs it with the already-parsed <see cref="SpriteIndex"/>. Immutable (a sheet is a single
    /// pre-baked image, unlike <see cref="GlyphAtlasTexture"/>'s grow-and-reupload atlas) — built once from
    /// a fetched <see cref="SpriteResponse"/> and disposed as a unit.
    ///
    /// <para>
    /// <b>Orientation contract:</b> the glyph atlas uploads a top-left-origin CPU buffer via
    /// <c>LoadRawTextureData</c>, so a top-left coord <c>(px,py)</c> samples at <c>GetPixel(px,py)</c> with
    /// no flip (see <see cref="GlyphAtlasTexture"/>). <see cref="Texture2D.LoadImage"/> does the OPPOSITE —
    /// it decodes a PNG's top row to <c>GetPixel</c> row <c>height-1</c> (Unity's bottom-left-origin
    /// <c>GetPixel</c> convention). To make this sheet obey the SAME contract as the glyph atlas — so the
    /// icon-quad-layout / SDF-glyph shader path (I5) can bind either texture unchanged — this constructor
    /// flips the decoded image's rows vertically once, so the sprite JSON's top-left-origin
    /// <c>(x,y)</c> rect also equals <c>GetPixel(x,y)</c> here. This renders icons UPRIGHT (matching text on
    /// screen), pinned end-to-end by <c>SymbolIconRenderSnapshotTests</c>.
    /// </para>
    ///
    /// Main-thread-only (like every <c>Texture2D</c> mutation) — must be constructed and disposed from the
    /// main thread, after any off-thread fetch work has resumed there (mirrors every other GPU-resource
    /// boundary in this codebase — the mesh/backend disposal contract).
    /// </summary>
    public sealed class SpriteSheet : VerifiedDisposable
    {
        private Texture2D _texture;
        private readonly SpriteIndex _index;

        /// <summary>The decoded, row-flipped sheet texture.</summary>
        public Texture2D Texture => _texture;

        /// <summary>The read-only <see cref="SpriteAtlasView"/> icon-quad-layout consumers bind against.</summary>
        public SpriteAtlasView View => new SpriteAtlasView
        {
            Index = _index,
            Size = new int2(_texture.width, _texture.height),
        };

        public SpriteSheet(byte[] pngBytes, SpriteIndex index)
        {
            if (pngBytes == null) throw new ArgumentNullException(nameof(pngBytes));
            _index = index ?? throw new ArgumentNullException(nameof(index));

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            tex.LoadImage(pngBytes);

            FlipRowsInPlace(tex);

            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            _texture = tex;
        }

        /// <summary>
        /// Swaps row <c>y</c> with row <c>height-1-y</c> in place, undoing <c>LoadImage</c>'s
        /// bottom-left-origin decode so the sheet's <c>GetPixel</c> convention matches the glyph atlas's
        /// (see the class doc's orientation contract). Kept readable (headless <c>GetPixels32</c>/
        /// <c>SetPixels32</c>) — this runs once per sheet, not per frame. On-screen correctness (icons render
        /// upright, matching text) is pinned end-to-end by <c>SymbolIconRenderSnapshotTests</c>.
        /// </summary>
        private static void FlipRowsInPlace(Texture2D tex)
        {
            int width = tex.width;
            int height = tex.height;
            Color32[] pixels = tex.GetPixels32();
            var flipped = new Color32[pixels.Length];

            for (int y = 0; y < height; y++)
            {
                int srcRowStart = y * width;
                int dstRowStart = (height - 1 - y) * width;
                Array.Copy(pixels, srcRowStart, flipped, dstRowStart, width);
            }

            tex.SetPixels32(flipped);
            tex.Apply(updateMipmaps: false);
        }

        protected override void DoDispose()
        {
            _texture.DestroySafely();
            _texture = null;
        }
    }
}
