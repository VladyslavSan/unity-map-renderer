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
    /// The Unity-side sprite sheet: decodes a MapLibre sprite PNG into a single <see cref="Texture2D"/>
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
    /// icon-quad-layout / SDF-glyph shader path can bind either texture unchanged — this constructor
    /// reads the decode into a top-left-origin buffer and writes the repacked result back row-reversed, so
    /// the sprite JSON's top-left-origin <c>(x,y)</c> rect also equals <c>GetPixel(x,y)</c> here. This
    /// renders icons UPRIGHT (matching text on screen), pinned end-to-end by
    /// <c>SymbolIconRenderSnapshotTests</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Padded repack:</b> the decoded sheet is never bound as-is. Published sheets are full-bleed and
    /// abutting (228 of 264 sprites in the shipped style's sheet have ink on the rect edge), so a sprite's
    /// silhouette IS the drawn quad's polygon edge — and with MSAA off that edge gets one binary coverage
    /// sample per pixel and flips whole pixels in and out as the quad slides sub-pixel. This constructor
    /// therefore repacks every sprite into its own cell with a one-texel transparent border
    /// (<c>SpriteSheetPadder</c> plans the rects, <c>SpriteSheetComposer</c> writes the pixels) and hands the
    /// derived index on, so the silhouette becomes a texture ALPHA edge that bilinear filtering ramps across.
    /// The sheet grows ~20 %, once, at style load.
    /// </para>
    ///
    /// Main-thread-only (like every <c>Texture2D</c> mutation) — must be constructed and disposed from the
    /// main thread, after any off-thread fetch work has resumed there (mirrors every other GPU-resource
    /// boundary in this codebase — the mesh/backend disposal contract).
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
            // Held in a local the `finally` can see: between its creation and the `_texture` assignment
            // below, the repacked texture has NO owner, so a throw in there would strand it (the ctor's
            // caller never gets an instance to Dispose). Nulled on success — the field owns it from that
            // point, and DestroySafely is null-tolerant, so the two paths cannot double-destroy.
            Texture2D repacked = null;
            try
            {
                decoded.LoadImage(pngBytes);
                var sourceSize = new int2(decoded.width, decoded.height);

                // GetPixels32 — NOT GetRawTextureData. LoadImage picks its own format from the PNG and does
                // not have to honour the ctor's RGBA32, so the raw bytes may not be RGBA32 at all;
                // GetPixels32 is the normalising read path.
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

                // BILINEAR, not Point. An icon's magnification is `icon-size * dpr / pixelRatio`; the sheet is
                // always fetched @1x and dpr is Screen.dpi/160, so it is essentially never an integer — and
                // nearest-neighbour is exact ONLY at integer magnification. Off it, each texel covers N or N+1
                // device pixels depending on the quad's sub-pixel phase, so panning re-quantises an icon's
                // interior every frame: the icon's pixels visibly warp. Pinned by
                // SymbolIconResamplingTests.IconInterior_TracksSubPixelPhaseSmoothly.
                //
                // What makes bilinear safe here is the ONE-TEXEL TRANSPARENT BORDER the repack above laid
                // around every sprite — an edge tap now reaches into that border rather than into the sprite
                // packed next door, and the border is simultaneously what turns the silhouette into an alpha
                // edge the filter can antialias (SymbolIconResamplingTests' bleed and silhouette teeth).
                // There is no UV inset any more: IconQuadLayout draws the padded rect edge-to-edge and grows
                // the quad by the border, so the icon's ink keeps its nominal size.
                //
                // Still no mip chain (see the ctor above): mips on a PACKED atlas average neighbouring sprites
                // together at every level >= 1, which is a worse artifact than the minification aliasing they fix.
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
        /// Writes a top-left-origin RGBA32 buffer into the <c>Color32[]</c> <c>SetPixels32</c> expects.
        /// Deliberately NOT a row reversal: <c>SetPixels32</c> indexes <c>[y * width + x]</c> with <c>y</c>
        /// being the <c>GetPixel</c> y, so a straight row-order copy is exactly what makes
        /// <c>GetPixel(x,y)</c> read the top-left-origin <c>(x,y)</c> — the orientation contract. The single
        /// flip of the whole path lives in <see cref="PackTopLeftOrigin"/>, which is where
        /// <c>LoadImage</c>'s upside-down decode is undone; reversing here as well would flip the sheet back.
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
