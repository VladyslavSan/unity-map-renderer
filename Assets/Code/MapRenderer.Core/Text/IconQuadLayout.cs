// Engine-free: no UnityEngine dependency.

using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Turns one resolved <see cref="SpriteEntry"/> + <c>icon-*</c> layout properties into one symbol-local,
    /// anchor-relative <see cref="SymbolQuad"/> in the sheet's pixel space. The icon analogue of
    /// <see cref="TextQuadLayout"/>: the same hAlign factor and y-down→y-up offset negation, but one quad.
    /// Vertically, a sprite's box IS its ink, so <c>vAlign = 0.5</c> already centres it; text needs an
    /// optical-centre formula instead (<c>docs/road-shields-design.md</c>).
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

            // The SKIRT grows the quad by the border's drawn size, symmetrically, AFTER align/offset placed the
            // CONTENT box, so every icon-anchor stays exact and the collision box (inset by SkirtPx) stays on the ink.
            minX -= skirtPx; maxX += skirtPx;
            minY -= skirtPx; maxY += skirtPx;

            // Non-local invariant: UVs span the PADDED rect with no inset, and the quad grows by the same border,
            // so uvWidth * sheetWidth / quadWidth == PixelRatio / iconSize for border and content alike; either
            // half alone shrinks or grows the ink (IconQuadLayoutTests, SymbolIconResamplingTests).
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
                Page = 0, // The sprite sheet is single-page, so every icon is on page 0.
            };
        }

        /// <summary>
        /// The drawn width per side, in baked px, of the transparent border around <paramref name="entry"/>'s
        /// content, scaled like the content (pixel ratio, <c>icon-size</c>); 0 without a border.
        /// Non-local invariant: <see cref="Layout"/> grows the quad by it and <c>CurvedGlyph.CellSkirt</c> insets
        /// by it, so both use this one formula. The <c>pixelRatio</c> guard (as in <c>FillPattern.TryResolve</c>)
        /// stops a parsed <c>"pixelRatio": 0</c> from making the skirt NaN.
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
