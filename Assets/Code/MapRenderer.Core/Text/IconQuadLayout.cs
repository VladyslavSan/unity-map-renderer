// Engine-free: no UnityEngine dependency.

using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Turns one resolved <see cref="SpriteEntry"/> + <c>icon-*</c> layout properties into a single
    /// symbol-local, anchor-relative <see cref="SymbolQuad"/> in the sprite sheet's own pixel space. The
    /// icon analogue of <see cref="TextQuadLayout"/> — reuses the SAME horizontal hAlign factor and the
    /// SAME y-down→y-up single-negation offset convention, but a sprite is exactly one quad (no glyph run,
    /// no wrap, no baseline) so this is a single pure function rather than a stateful forward pass.
    /// <b>The vertical half differs</b> from <see cref="TextQuadLayout"/>: a sprite has no baseline or
    /// ascent slack, so its box IS its ink — centring the box (<c>vAlign = 0.5</c>) already centres the
    /// ink, which is what <see cref="TextQuadLayout"/>'s centre case reproduces for text via an
    /// optical-centre formula (<c>docs/road-shields-design.md</c>). Do not "restore consistency" by
    /// giving this a three-valued vertical anchor to match — it would re-break this.
    /// </summary>
    public static class IconQuadLayout
    {
        /// <summary>
        /// Lay out <paramref name="entry"/> as a <see cref="SymbolQuad"/>. <paramref name="sheetSize"/> is the
        /// sprite sheet's pixel dimensions (the UV-normalization denominator — <see cref="SpriteAtlasView.Size"/>).
        /// <paramref name="iconSize"/> is the evaluated <c>icon-size</c> scale factor, <paramref name="iconAnchor"/>
        /// is <c>icon-anchor</c>, and <paramref name="iconOffset"/> is the raw (as-authored, y-DOWN) <c>icon-offset</c>
        /// — each component is multiplied by <paramref name="iconSize"/> per spec.
        /// </summary>
        public static SymbolQuad Layout(in SpriteEntry entry, int2 sheetSize, float iconSize, TextAnchor iconAnchor, float2 iconOffset)
        {
            // Logical size divides out the sheet's device pixel ratio (an @2x sprite's pixels cover half the
            // logical size); the drawn size then applies icon-size on top.
            float logicalWidth = entry.Width / entry.PixelRatio;
            float logicalHeight = entry.Height / entry.PixelRatio;
            float drawnWidth = logicalWidth * iconSize;
            float drawnHeight = logicalHeight * iconSize;

            float skirtPx = SkirtPx(entry, iconSize);

            (float hAlign, float vAlign) = ResolveAlignFactors(iconAnchor);

            float minX = -hAlign * drawnWidth;
            float maxX = (1f - hAlign) * drawnWidth;
            float maxY = vAlign * drawnHeight;
            float minY = -(1f - vAlign) * drawnHeight;

            // icon-offset is y-DOWN as authored (mirrors TextLayoutOptionsBuilder's single negation of
            // text-offset) and each component scales by icon-size per spec.
            float2 offsetPx = new float2(iconOffset.x * iconSize, -iconOffset.y * iconSize);
            minX += offsetPx.x;
            maxX += offsetPx.x;
            minY += offsetPx.y;
            maxY += offsetPx.y;

            // The SKIRT: the quad grows by the drawn size of the sprite's transparent border, symmetrically,
            // AFTER the align/offset block above has placed the CONTENT box. Adding it symmetrically is what
            // keeps every icon-anchor exact — `icon-anchor: left` still puts the CONTENT's left edge on the
            // anchor — and what keeps the collision box (the caller-side bounds formula, which insets by the
            // same skirt — see SkirtPx) on the ink rather than on the border.
            minX -= skirtPx; maxX += skirtPx;
            minY -= skirtPx; maxY += skirtPx;

            // UVs span the sprite's PADDED rect — its content plus the transparent border SpriteSheet's
            // repack laid around it — with NO inset. pixelRatio only divides the logical SIZE, never the UV
            // rect.
            //
            // The border is the whole fix. A published sheet is full-bleed and abutting (the shipped style's
            // sheet: 228 of 264 sprites with ink on the rect edge, 371 abutting pairs), so before the repack
            // a sprite's silhouette WAS the quad's polygon edge — and with MSAA off that edge gets one binary
            // coverage sample per pixel, so it flipped whole pixels in and out as the quad slid sub-pixel.
            // Drawing the border makes the silhouette a texture ALPHA edge with a one-texel ramp, which
            // bilinear filtering antialiases. Drawing it also keeps the sampler off the neighbouring sprite,
            // which is what the retired half-texel inset used to do — at the cost of never drawing the
            // sprite's outer half-texel, so its content rendered W/(W-1) too LARGE (+4.8 % at a 22px rect,
            // +14.3 % at an 8px one). That magnification is now gone: icon ink draws at its nominal size.
            // Measured against what SHIPPED, that is a shrink of 1 - (W-1)/W == 1/W — 4.5 % at 22px, 12.5 %
            // at 8px. (Do not restate the magnification figures as the shrink: they are the same ratio read
            // from opposite ends, and they differ.)
            //
            // The identity that holds both halves together, for every sprite and every icon-size:
            //     uvWidth * sheetWidth / quadWidth == PixelRatio / iconSize
            // i.e. texels-per-drawn-pixel is the SAME for the border and for the content. Pad the atlas but
            // leave the quad nominal and the skirt is never rasterized AND the padded rect is squeezed into
            // the nominal quad, so the ink SHRINKS; grow the quad but leave the UV rect on the content and
            // the content is stretched across the padded quad, so the ink GROWS by (W+2P)/W. Pinned by
            // IconQuadLayoutTests and by SymbolIconResamplingTests' silhouette + nominal-ink teeth.
            float2 uvTopLeft = new float2(entry.X - entry.Padding, entry.Y - entry.Padding) / sheetSize;
            float2 uvBottomRight =
                new float2(entry.X + entry.Width + entry.Padding, entry.Y + entry.Height + entry.Padding) / sheetSize;

            return new SymbolQuad
            {
                TopLeft = new float2(minX, maxY),
                BottomRight = new float2(maxX, minY),
                UvTopLeft = uvTopLeft,
                UvBottomRight = uvBottomRight,
                LineIndex = 0,
                Page = 0, // Stage M: the sprite sheet stays single-page — icons never multi-page.
            };
        }

        /// <summary>
        /// The drawn width, in symbol-local (baked) px, of the transparent border around
        /// <paramref name="entry"/>'s content — per side. Divides out the sheet's device pixel ratio and
        /// applies <c>icon-size</c>, exactly as the content's own logical size does, so the border and the
        /// content are drawn at the same texels-per-pixel.
        ///
        /// <para>ONE formula with TWO entry points that must never drift: <see cref="Layout"/> calls it to
        /// grow the quad, and every consumer that needs the CONTENT box back out of a laid-out quad (the
        /// caller-side bounds formula, <c>CurvedGlyph.CellSkirt</c>) calls it to inset by the same amount.
        /// <c>0</c> whenever the sprite reports no border.</para>
        ///
        /// <para>The <c>pixelRatio</c> divisor is guarded exactly as <c>FillPattern.TryResolve</c> guards its
        /// own: <see cref="SpriteIndex"/> defaults the field to 1, but a malformed sheet declaring an explicit
        /// <c>"pixelRatio": 0</c> parses straight through. At <c>Padding == 0</c> that would make the skirt
        /// <c>0/0f</c> — <b>NaN</b>, not the infinity the surrounding size maths produces — and a NaN carried
        /// into <c>SymbolFeature.IconSkirtPx</c> and out into a collision box compares false against everything,
        /// which is a different (and quieter) failure than an infinite box.</para>
        /// </summary>
        public static float SkirtPx(in SpriteEntry entry, float iconSize)
            => entry.Padding / (entry.PixelRatio > 0f ? entry.PixelRatio : 1f) * iconSize;

        /// <summary>hAlign: Left*=0, Right*=1, else .5. vAlign: Top*=0, Bottom*=1, else .5 — a bare float,
        /// unlike <c>TextQuadLayout.ResolveAlignFactors</c>'s three-valued vertical, for the reason in this
        /// class's summary.</summary>
        private static (float hAlign, float vAlign) ResolveAlignFactors(TextAnchor anchor)
        {
            float hAlign = anchor switch
            {
                TextAnchor.Left or TextAnchor.TopLeft or TextAnchor.BottomLeft => 0f,
                TextAnchor.Right or TextAnchor.TopRight or TextAnchor.BottomRight => 1f,
                _ => 0.5f,
            };
            float vAlign = anchor switch
            {
                TextAnchor.Top or TextAnchor.TopLeft or TextAnchor.TopRight => 0f,
                TextAnchor.Bottom or TextAnchor.BottomLeft or TextAnchor.BottomRight => 1f,
                _ => 0.5f,
            };
            return (hAlign, vAlign);
        }
    }
}
