// Non-obvious why: write `int2` via `using Unity.Mathematics;`, never inline `Unity.Mathematics.int2` —
// inside `MapRenderer.Unity.*` the leading `Unity` binds to `MapRenderer.Unity` (CS0234).

using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// The Unity-side sprite sheet: an immutable <see cref="Texture2D"/> decoded once from the sprite PNG,
    /// paired with its <see cref="SpriteIndex"/>. Main-thread only. Non-local invariant: a top-left coord
    /// <c>(x,y)</c> reads <c>GetPixel(x,y)</c>, as in the glyph atlas, so one shader binds either texture.
    /// The ctor repacks every sprite with a one-texel transparent border; see
    /// docs/labels-and-symbols-design.md § "Sampling the sheet — bilinear + a one-texel padded repack".
    /// </summary>
    public sealed class SpriteSheet : VerifiedDisposable
    {
        /// <summary>Texels of transparent border manufactured around every sprite by the repack.</summary>
        private const int BorderTexels = 1;

        private const int BytesPerTexel = 4;

        private Texture2D _texture;
        private readonly SpriteIndex _index;

        /// <summary>The decoded, row-flipped, padded-repacked sheet texture.</summary>
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
            if (index == null) throw new ArgumentNullException(nameof(index));

            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            // A local the `finally` can see: until `_texture` owns it, a throw would strand it. Nulled on
            // success, so the `finally` and DoDispose never both destroy it.
            Texture2D repacked = null;
            try
            {
                decoded.LoadImage(pngBytes);
                var sourceSize = new int2(decoded.width, decoded.height);

                // GetPixels32, not GetRawTextureData: LoadImage picks its own format from the PNG, so the
                // raw bytes may not be RGBA32.
                byte[] source = PackTopLeftOrigin(decoded.GetPixels32(), sourceSize);

                SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, BorderTexels);
                if (plan.Padding != BorderTexels && index.Count > 0)
                    Debug.LogWarning(
                        $"SpriteSheet: the {sourceSize.x}x{sourceSize.y} sheet could not be repacked with a " +
                        $"{BorderTexels}-texel border; icons will render without the silhouette ramp.");

                var composed = new byte[plan.Size.x * plan.Size.y * BytesPerTexel];
                SpriteSheetComposer.Compose(source, sourceSize.x, sourceSize.y, plan, composed);

                repacked = new Texture2D(plan.Size.x, plan.Size.y, TextureFormat.RGBA32, mipChain: false);
                repacked.SetPixels32(UnpackToUnityPixels(composed, plan.Size));
                repacked.Apply(updateMipmaps: false);

                // Bilinear, no mips: the border above keeps edge taps off neighbours.
                repacked.filterMode = FilterMode.Bilinear;
                repacked.wrapMode = TextureWrapMode.Clamp;

                _texture = repacked;
                repacked = null; // ownership transferred — see the local's declaration above
                _index = plan.Index;
            }
            finally
            {
                decoded.DestroySafely();
                repacked.DestroySafely(); // non-null only on the throw path
            }
        }

        /// <summary>
        /// Reads Unity's bottom-left-origin <c>GetPixels32</c> buffer into a top-left-origin, row-major
        /// RGBA32 byte buffer — the sprite-JSON space <c>SpriteSheetComposer</c> works in. This row reversal
        /// IS the flip the orientation contract calls for: sprite-JSON top-left <c>(x,y)</c> ==
        /// <c>GetPixel(x,y)</c>, the same convention <see cref="GlyphAtlasTexture"/> establishes.
        /// </summary>
        private static byte[] PackTopLeftOrigin(Color32[] pixels, int2 size)
        {
            var bytes = new byte[size.x * size.y * BytesPerTexel];
            for (int row = 0; row < size.y; row++)
            {
                int sourceRowStart = (size.y - 1 - row) * size.x;
                int destination = row * size.x * BytesPerTexel;
                for (int column = 0; column < size.x; column++, destination += BytesPerTexel)
                {
                    Color32 pixel = pixels[sourceRowStart + column];
                    bytes[destination] = pixel.r;
                    bytes[destination + 1] = pixel.g;
                    bytes[destination + 2] = pixel.b;
                    bytes[destination + 3] = pixel.a;
                }
            }
            return bytes;
        }

        /// <summary>
        /// Writes a top-left-origin RGBA32 buffer into the <c>Color32[]</c> <c>SetPixels32</c> expects, in
        /// straight row order. The single flip of the path is in <see cref="PackTopLeftOrigin"/>; a second
        /// reversal here would flip the sheet back.
        /// </summary>
        private static Color32[] UnpackToUnityPixels(byte[] bytes, int2 size)
        {
            var pixels = new Color32[size.x * size.y];
            for (int i = 0, source = 0; i < pixels.Length; i++, source += BytesPerTexel)
            {
                pixels[i] = new Color32(
                    bytes[source], bytes[source + 1], bytes[source + 2], bytes[source + 3]);
            }
            return pixels;
        }

        protected override void DoDispose()
        {
            _texture.DestroySafely();
            _texture = null;
        }
    }
}
