// Unity EditMode only — reads a rendered frame produced by OffLookAtLabelScene. NOT registered in
// Tools/core-tests/core-tests.csproj.
//
// The ink-run segmenter shared by the map-pitch epic's ink teeth. W2 wrote these bodies inside
// MapPitchedGlyphSizeTests; W3 needs the SAME segmentation for its containment tooth, and a second copy
// would have been the third ink probe across these suites (P3b's recorded nit N3 predicted exactly that
// drift). This file is a PURE MOVE of those bodies — no logic change.
//
// W2's DELIBERATE, WRITTEN decision is preserved and NOT reversed. Its note read:
//
//     "Lives in the test assembly rather than in WorldSymbolInkAnalysis so the shared analyser every
//      pre-existing snapshot tooth depends on is untouched by this stage."
//
// That reason is still valid — WorldSymbolInkAnalysis is depended on by frozen snapshot teeth, and moving
// these helpers into it would put new code under all of them. W3's addendum is only that a SECOND consumer
// justifies lifting them out of one test fixture into a test-assembly-internal helper class, which changes
// neither the analyser nor what any existing tooth reads.

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Tests.Text.Placement; // WorldSymbolInkAnalysis

namespace MapRenderer.Tests
{
    /// <summary>Ink-run segmentation over an <see cref="OffLookAtLabelScene"/> frame (row 0 = TOP scanline,
    /// the convention <c>OffLookAtLabelScene.InkPixels</c> and <c>RenderIsolated</c> both return).</summary>
    internal static class OffLookAtInkRuns
    {
        /// <summary>
        /// Contiguous inked spans along COLUMNS (a screen-HORIZONTAL label), where a column counts as inked if
        /// any pixel of it inside the inclusive row band is below
        /// <c>WorldSymbolInkAnalysis.InkThreshold</c>. Returned as inclusive <c>(start, end)</c> column pairs.
        /// </summary>
        public static (int start, int end)[] AlongColumns(byte[] rgba, int size, int rowFrom, int rowTo)
        {
            var inked = new bool[size];
            int first = math.max(0, rowFrom), last = math.min(size - 1, rowTo);
            for (int col = 0; col < size; col++)
                for (int row = first; row <= last; row++)
                    if (rgba[(row * size + col) * 4] < WorldSymbolInkAnalysis.InkThreshold) { inked[col] = true; break; }
            return RunsOf(inked);
        }

        /// <summary>The ROW analogue, for a screen-VERTICAL (receding) label: contiguous inked spans along
        /// rows, where a row counts as inked if any pixel of it inside the inclusive COLUMN band is ink.</summary>
        public static (int start, int end)[] AlongRows(byte[] rgba, int size, int colFrom, int colTo)
        {
            var inked = new bool[size];
            int first = math.max(0, colFrom), last = math.min(size - 1, colTo);
            for (int row = 0; row < size; row++)
                for (int col = first; col <= last; col++)
                    if (rgba[(row * size + col) * 4] < WorldSymbolInkAnalysis.InkThreshold) { inked[row] = true; break; }
            return RunsOf(inked);
        }

        /// <summary>A receding label's ink runs: render it ISOLATED (the construction-time ink pass draws only
        /// the cross-azimuth pair — see <c>OffLookAtLabelScene.RenderIsolated</c> for why) and segment along
        /// ROWS inside its own column band.</summary>
        public static (int start, int end)[] Receding(OffLookAtLabelScene f, OffLookAtLabelId id)
        {
            byte[] pixels = f.RenderIsolated(id);
            f.ColumnBandFor(id, out int colFrom, out int colTo);
            return AlongRows(pixels, f.Config.SizePx, colFrom, colTo);
        }

        /// <summary>Asserts — never assumes — that this label really runs screen-VERTICALLY, which is what
        /// makes a row segmentation inside a column band measure the thing the tooth thinks it does. If the
        /// pose ever stops making the receding roads axis-aligned, this fails loudly instead of quietly
        /// measuring the wrong axis.</summary>
        public static void AssertRunsVertically(OffLookAtLabelScene f, OffLookAtLabelId id,
            GlyphMeasurement[] glyphs)
        {
            double2 spread = glyphs[glyphs.Length - 1].ScreenPx - glyphs[0].ScreenPx;
            // Name the calling tooth: two suites share this helper now, so "precondition failed" alone no
            // longer says whose.
            Assert.That(math.abs(spread.y) > 3.0 * math.abs(spread.x), Is.True,
                $"{TestContext.CurrentContext.Test.Name} precondition ({id}): a receding label must run " +
                $"screen-VERTICALLY — its glyph run spans Δ=({spread.x:F1}, {spread.y:F1}) px.");
        }

        public static (int start, int end)[] RunsOf(bool[] inked)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int i = 0; i < inked.Length; i++)
            {
                if (inked[i] && start < 0) start = i;
                else if (!inked[i] && start >= 0) { runs.Add((start, i - 1)); start = -1; }
            }
            if (start >= 0) runs.Add((start, inked.Length - 1));
            return runs.ToArray();
        }
    }
}
#endif
