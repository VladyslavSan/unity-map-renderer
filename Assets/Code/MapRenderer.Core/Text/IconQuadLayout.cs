// Engine-free: no UnityEngine dependency.

using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// I3 — turns one resolved <see cref="SpriteEntry"/> + <c>icon-*</c> layout properties into a single
    /// label-local, anchor-relative <see cref="SymbolQuad"/> in the sprite sheet's own pixel space. The
    /// icon analogue of <see cref="TextQuadLayout"/> — reuses the SAME anchor hAlign/vAlign factors and the
    /// SAME y-down→y-up single-negation offset convention so icon and text labels agree, but a sprite is
    /// exactly one quad (no glyph run, no wrap, no baseline) so this is a single pure function rather than a
    /// stateful forward pass.
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

            // UVs use the RAW sheet rect — pixelRatio only divides the logical SIZE, never the UV rect.
            float2 uvTopLeft = new float2(entry.X, entry.Y) / sheetSize;
            float2 uvBottomRight = new float2(entry.X + entry.Width, entry.Y + entry.Height) / sheetSize;

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
        /// I5a — wraps a single laid-out icon <see cref="SymbolQuad"/> into the same
        /// <see cref="TextLayoutResult"/> shape the point-text path produces, so <c>StyledSymbolTileBuilder</c>'s
        /// Pass 2 can emit an icon <see cref="Placement.LabelInstance"/> down the SAME point-placement path
        /// (§5.4: ride <c>Kind.Point</c> + the <c>AtlasKind</c> discriminator, no parallel icon path). Bounds are
        /// the quad's own min/max corner per component — a sprite is exactly one quad, so its bbox IS the block
        /// bbox. <see cref="TextLayoutResult.LineCount"/> is 1 (an icon has no line concept, but every consumer
        /// of <c>LineCount</c> treats "&gt;= 1" as the normal case).
        /// </summary>
        public static TextLayoutResult ToLayoutResult(in SymbolQuad iconQuad)
        {
            float2 min = math.min(iconQuad.TopLeft, iconQuad.BottomRight);
            float2 max = math.max(iconQuad.TopLeft, iconQuad.BottomRight);
            return new TextLayoutResult
            {
                Quads = new[] { iconQuad },
                BoundsMin = min,
                BoundsMax = max,
                LineCount = 1,
            };
        }

        /// <summary>Same mapping as <c>TextQuadLayout.ResolveAlignFactors</c>: hAlign Left*=0, Right*=1, else .5;
        /// vAlign Top*=0, Bottom*=1, else .5 — icon and text anchors agree.</summary>
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
