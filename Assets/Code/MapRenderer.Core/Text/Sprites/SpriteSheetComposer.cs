// Engine-free RGBA32 bytes, so the pixel rule below is checked byte-for-byte on the dotnet-test loop instead
// of behind a GPU readback.

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// Executes a <see cref="SpritePadPlan"/> over pixels: copies each sprite's content block and makes a
    /// transparent border whose texels have alpha 0 and the RGB of the nearest content texel. Non-obvious why:
    /// bilinear filtering interpolates RGB and alpha independently, so a zeroed RGB would leave a dark fringe
    /// around every icon. Buffers are top-left-origin, row-major RGBA32; <c>SpriteSheet</c>'s pack/unpack
    /// helpers do the row flip to Unity's bottom-left convention.
    /// </summary>
    public static class SpriteSheetComposer
    {
        /// <summary>Bytes per RGBA32 texel.</summary>
        private const int BytesPerTexel = 4;

        /// <summary>
        /// Composes the repacked sheet described by <paramref name="plan"/> into <paramref name="dst"/>
        /// (which must hold <c>plan.Size.x * plan.Size.y</c> RGBA32 texels). Everything not covered by a
        /// sprite's content or border is left fully transparent black.
        /// </summary>
        public static void Compose(byte[] src, int srcWidth, int srcHeight, SpritePadPlan plan, byte[] dst)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (srcWidth < 0 || srcHeight < 0) throw new ArgumentOutOfRangeException(nameof(srcWidth));
            if (src.Length < srcWidth * srcHeight * BytesPerTexel)
                throw new ArgumentException("source buffer is smaller than its declared size", nameof(src));

            int dstWidth = plan.Size.x;
            int dstHeight = plan.Size.y;
            if (dst.Length < dstWidth * dstHeight * BytesPerTexel)
                throw new ArgumentException("destination buffer is smaller than the plan's sheet", nameof(dst));

            Array.Clear(dst, 0, dstWidth * dstHeight * BytesPerTexel);
            if (plan.Blits == null)
                return;

            foreach (SpriteBlit blit in plan.Blits)
            {
                if (blit.Width <= 0 || blit.Height <= 0)
                    continue;

                RequireInBounds(blit, plan.Padding, srcWidth, srcHeight, dstWidth, dstHeight);
                CopyContent(src, srcWidth, dst, dstWidth, blit);
                FillBorder(src, srcWidth, dst, dstWidth, blit, plan.Padding);
            }
        }

        /// <summary>Copies the content block row by row — byte-identical, no resampling, ever.</summary>
        private static void CopyContent(byte[] src, int srcWidth, byte[] dst, int dstWidth, in SpriteBlit blit)
        {
            int rowBytes = blit.Width * BytesPerTexel;
            for (int row = 0; row < blit.Height; row++)
            {
                int srcOffset = ((blit.SrcY + row) * srcWidth + blit.SrcX) * BytesPerTexel;
                int dstOffset = ((blit.DstY + row) * dstWidth + blit.DstX) * BytesPerTexel;
                Buffer.BlockCopy(src, srcOffset, dst, dstOffset, rowBytes);
            }
        }

        /// <summary>
        /// Writes the <paramref name="padding"/>-texel band around the content: alpha 0, RGB clamped-sampled
        /// from the content. Clamping is what makes edges and corners one rule rather than eight cases — an
        /// edge texel clamps to the content pixel directly beside it, a corner texel to the diagonal one.
        /// </summary>
        private static void FillBorder(
            byte[] src, int srcWidth, byte[] dst, int dstWidth, in SpriteBlit blit, int padding)
        {
            if (padding <= 0)
                return;

            for (int dy = -padding; dy < blit.Height + padding; dy++)
            {
                for (int dx = -padding; dx < blit.Width + padding; dx++)
                {
                    bool insideContent = dx >= 0 && dx < blit.Width && dy >= 0 && dy < blit.Height;
                    if (insideContent)
                        continue;

                    int nearestX = math.clamp(dx, 0, blit.Width - 1);
                    int nearestY = math.clamp(dy, 0, blit.Height - 1);
                    int srcOffset = ((blit.SrcY + nearestY) * srcWidth + blit.SrcX + nearestX) * BytesPerTexel;
                    int dstOffset = ((blit.DstY + dy) * dstWidth + blit.DstX + dx) * BytesPerTexel;

                    dst[dstOffset] = src[srcOffset];
                    dst[dstOffset + 1] = src[srcOffset + 1];
                    dst[dstOffset + 2] = src[srcOffset + 2];
                    dst[dstOffset + 3] = 0; // the ramp bilinear will interpolate the silhouette across
                }
            }
        }

        /// <summary>
        /// A blit that reaches outside either sheet is a planner bug, so throw rather than corrupt a
        /// neighbouring sprite's row. Every reach test widens to <c>long</c>, as
        /// <c>SpriteSheetPadder.IsPackable</c> does, because <c>x: int.MaxValue</c> wraps negative in 32 bits.
        /// </summary>
        private static void RequireInBounds(
            in SpriteBlit blit, int padding, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
        {
            bool srcOk = blit.SrcX >= 0 && blit.SrcY >= 0
                         && (long)blit.SrcX + blit.Width <= srcWidth
                         && (long)blit.SrcY + blit.Height <= srcHeight;
            bool dstOk = (long)blit.DstX - padding >= 0 && (long)blit.DstY - padding >= 0
                         && (long)blit.DstX + blit.Width + padding <= dstWidth
                         && (long)blit.DstY + blit.Height + padding <= dstHeight;
            if (!srcOk || !dstOk)
                throw new ArgumentException(
                    $"blit src({blit.SrcX},{blit.SrcY},{blit.Width},{blit.Height}) → " +
                    $"dst({blit.DstX},{blit.DstY}) with {padding}px border does not fit " +
                    $"src {srcWidth}x{srcHeight} / dst {dstWidth}x{dstHeight}");
        }
    }
}
