// Test-only image-analysis helpers shared by WorldSymbolAbRenderSnapshotTests (T2) and
// WorldSymbolMotionTests (T3) — kept in its own file (rather than duplicated per test, or bolted onto
// SymbolAtlasOrientationSnapshotTests) so both A0 GPU teeth share ONE ink-analysis implementation without
// touching that existing, frozen test file. Internal, not public (test-code-bloat convention: a test
// helper's footprint stays inside the test assembly).

using Unity.Mathematics;

namespace MapRenderer.Tests
{
    internal static class WorldSymbolInkAnalysis
    {
        /// <summary>Background is white (255); black/dark fill reads well below this on the R channel.</summary>
        public const byte InkThreshold = 200;

        /// <summary>Vertically mirrors an RGBA32 row-major buffer in place (row r ↔ row height-1-r) — the
        /// same off-screen-readback un-mirror <c>SymbolAtlasOrientationSnapshotTests</c> applies (see its
        /// file header for the full rationale).</summary>
        public static void FlipRowsVertically(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            var tmp = new byte[stride];
            for (int r = 0; r < height / 2; r++)
            {
                int top = r * stride;
                int bot = (height - 1 - r) * stride;
                System.Array.Copy(rgba, top, tmp, 0, stride);
                System.Array.Copy(rgba, bot, rgba, top, stride);
                System.Array.Copy(tmp, 0, rgba, bot, stride);
            }
        }

        /// <summary>Scans an RGBA32 buffer (row-major, top-left origin) for "ink" pixels and reports the
        /// bounding box plus the pixel-weighted centroid (row, col).</summary>
        public static void AnalyzeInk(
            byte[] rgba, int width, int height,
            out int minRow, out int maxRow, out int minCol, out int maxCol,
            out float centroidRow, out float centroidCol, out int inkCount)
            => AnalyzeInk(rgba, width, height, 0, height - 1,
                out minRow, out maxRow, out minCol, out maxCol, out centroidRow, out centroidCol, out inkCount);

        /// <summary>Same scan, restricted to the INCLUSIVE row band
        /// <paramref name="rowFrom"/>..<paramref name="rowTo"/> (clamped to the buffer; an inverted band
        /// reports no ink) — for a frame carrying more than one symbol, where a whole-frame scan reports one
        /// merged bounding box that belongs to neither.
        ///
        /// <para>The whole-frame overload above forwards to this one, so there is exactly ONE scanner: a
        /// change to the ink rule cannot apply to one caller and not the other.</para></summary>
        public static void AnalyzeInk(
            byte[] rgba, int width, int height, int rowFrom, int rowTo,
            out int minRow, out int maxRow, out int minCol, out int maxCol,
            out float centroidRow, out float centroidCol, out int inkCount)
        {
            minRow = int.MaxValue; maxRow = int.MinValue;
            minCol = int.MaxValue; maxCol = int.MinValue;
            inkCount = 0;
            double sumRow = 0.0, sumCol = 0.0;

            int firstRow = math.max(0, rowFrom);
            int lastRow  = math.min(height - 1, rowTo);

            for (int row = firstRow; row <= lastRow; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int idx = (row * width + col) * 4;
                    byte r = rgba[idx];
                    if (r >= InkThreshold) continue;

                    inkCount++;
                    sumRow += row;
                    sumCol += col;
                    if (row < minRow) minRow = row;
                    if (row > maxRow) maxRow = row;
                    if (col < minCol) minCol = col;
                    if (col > maxCol) maxCol = col;
                }
            }

            if (inkCount == 0)
            {
                minRow = maxRow = minCol = maxCol = 0;
                centroidRow = centroidCol = 0f;
                return;
            }

            centroidRow = (float)(sumRow / inkCount);
            centroidCol = (float)(sumCol / inkCount);
        }

        /// <summary>Average per-row horizontal ink extent in the top and bottom thirds of
        /// <paramref name="minRow"/>..<paramref name="maxRow"/> — the same "'A' renders upright" check
        /// <c>SymbolAtlasOrientationSnapshotTests</c> makes (bottom third, the crossbar/legs, must be wider
        /// than the top third, the apex).</summary>
        public static void ThirdWidths(
            byte[] rgba, int width, int height, int minRow, int maxRow,
            out float topThirdAvgWidth, out float bottomThirdAvgWidth)
        {
            int span = maxRow - minRow + 1;
            int thirdSize = math.max(1, span / 3);
            int topEnd = minRow + thirdSize;
            int bottomStart = maxRow - thirdSize;

            topThirdAvgWidth = AverageRowWidth(rgba, width, height, minRow, topEnd);
            bottomThirdAvgWidth = AverageRowWidth(rgba, width, height, bottomStart, maxRow);
        }

        private static float AverageRowWidth(byte[] rgba, int width, int height, int startRow, int endRow)
        {
            float sum = 0f;
            int count = 0;
            for (int row = startRow; row <= endRow && row < height; row++)
            {
                int rowMinCol = int.MaxValue, rowMaxCol = int.MinValue;
                for (int col = 0; col < width; col++)
                {
                    int idx = (row * width + col) * 4;
                    if (rgba[idx] >= InkThreshold) continue;
                    if (col < rowMinCol) rowMinCol = col;
                    if (col > rowMaxCol) rowMaxCol = col;
                }
                if (rowMaxCol < rowMinCol) continue;
                sum += rowMaxCol - rowMinCol + 1;
                count++;
            }
            return count == 0 ? 0f : sum / count;
        }

        /// <summary>Fraction of pixels whose R channel differs by more than <paramref name="threshold"/>
        /// between two same-size RGBA32 buffers — T2's "changed-pixel fraction" equivalence metric.</summary>
        public static float ChangedPixelFraction(byte[] a, byte[] b, int width, int height, byte threshold = 24)
        {
            int total = width * height;
            int changed = 0;
            for (int i = 0; i < total; i++)
            {
                int idx = i * 4;
                if (math.abs(a[idx] - b[idx]) > threshold) changed++;
            }
            return (float)changed / total;
        }
    }
}
