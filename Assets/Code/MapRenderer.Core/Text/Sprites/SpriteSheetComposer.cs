// Engine-free: no UnityEngine dependency. RGBA32 bytes are not a Unity type, which is the whole point —
// the load-bearing pixel rule below is checked byte-for-byte on the fast dotnet-test loop instead of behind
// a GPU readback.

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// Executes a <see cref="SpritePadPlan"/> over pixels: copies each sprite's content block to its new
    /// home and manufactures the transparent border around it.
    ///
    /// <para><b>The border rule.</b> Every border texel is <b>alpha 0 with the RGB of the nearest content
    /// texel</b> (the content rect's edge pixel; a corner texel therefore takes the diagonal corner pixel).
    /// The RGB replication is not cosmetic and not optional: bilinear filtering interpolates RGB and alpha
    /// <i>independently</i>, so a mid-ramp texel whose RGB was zeroed contributes black to the colour while
    /// still contributing coverage — a dark fringe all the way around every icon. Replicating makes the ramp
    /// colour→same colour and alpha 1→0, which is exactly a premultiplied-looking edge without the
    /// premultiply.</para>
    ///
    /// <para><b>Coordinate space</b> is the sprite JSON's: top-left origin, row-major, 4 bytes per texel in
    /// R,G,B,A order. Both buffers are in it; the row flip that reconciles this with Unity's bottom-left
    /// <c>GetPixel</c> convention lives in <c>SpriteSheet</c>'s pack/unpack helpers, OUTSIDE
    /// this type, so the plan's rects mean exactly one thing here.</para>
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
        /// A blit that reaches outside either sheet is a planner bug, not malformed content — the planner
        /// rejects out-of-bounds source rects and the packer guarantees the destination cell fits. Fail loudly
        /// rather than corrupt a neighbouring sprite's row.
        ///
        /// <para>Every reach test widens to <c>long</c>, for the same reason
        /// <c>SpriteSheetPadder.IsPackable</c> does: in 32-bit signed arithmetic a rect at
        /// <c>x: int.MaxValue</c> wraps NEGATIVE and passes the bound, and the guard would then wave through
        /// exactly the blit it exists to reject.</para>
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
