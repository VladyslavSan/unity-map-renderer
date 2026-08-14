// Unity EditMode only — Stage G-VR, the golden reference-image regression layer.
// NOT registered in Tools/core-tests/core-tests.csproj (engine-bound: Texture2D encode/decode).
//
// A CHANGE DETECTOR layered ALONGSIDE the visual kit's existing analytic teeth (SnapshotCoverage / InkStatsIn
// / the oracle in GeoJsonPointLabelFixtureTests) — it does not replace them. A golden can only say "different
// from the last bake" and will happily lock in a WRONG image; the analytic assertions stay the correctness
// oracle. Two traps guarded here (both from the plan, both load-bearing — see docs/lessons-learned.md):
//   1. Vacuous all-black golden: NoGpuContext refuses to bake and never compares to a pass; a nonzero-ink
//      precondition (frame-wide, via VisualFrame.InkStatsIn — NOT SnapshotCoverage.IsBlank, which reads TRUE
//      on a sparse label frame too, see GeoJsonPointLabelFixtureTests.Neg_) runs before every compare.
//   2. Orientation-blind compare: RawPixels is bottom-left; a Texture2D.LoadImage'd PNG must be read back the
//      SAME way. RED-verified empirically (T-RED-flip) rather than assumed — see the dev report.

#if UNITY_EDITOR
using System;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The golden-image comparer: <see cref="Assert"/> is the entry point a fixture calls after
    /// <see cref="VisualScene.Render"/>. References live at
    /// <c>Assets/Fixtures/visual-references~/&lt;referenceName&gt;.png</c> — the trailing <c>~</c> makes Unity
    /// ignore the folder (no importer pass, no <c>.meta</c>, no lossy Texture2D copy, never in a build), which
    /// is correct because the reference is read by file path (<see cref="ReferencePath"/> + raw bytes), never
    /// through the AssetDatabase. Failure artifacts (actual / expected / heat-mapped diff) are written to
    /// <c>Logs/snapshots/</c> via <see cref="SnapshotRenderer.WritePngFromRgba32"/>.
    /// </summary>
    internal static class GoldenImage
    {
        /// <summary>Per-pixel max-channel byte delta above which a pixel counts as "differing". Tuned on this
        /// host — widen only with a recorded reason (plan §Edit 2).</summary>
        private const int MaxChannelDelta = 4;

        /// <summary>Fraction of differing pixels above which the frame fails the golden. Tuned on this host —
        /// widen only with a recorded reason (plan §Edit 2).</summary>
        private const double MaxDifferingFraction = 0.002;

        /// <summary>Env var gating the bake path (plan §Edit 2 step 3) — set to <c>"1"</c> to (re)write the
        /// reference PNG for every golden this run touches. Baking NEVER counts as a pass.</summary>
        private const string BakeEnvVar = "MAPRENDERER_BAKE_GOLDENS";

        /// <summary>
        /// Compares <paramref name="frame"/> against the baked reference <c>&lt;referenceName&gt;.png</c>.
        /// Never silently passes on a black or reference-less frame — see the class summary's two traps.
        /// </summary>
        /// <param name="frame">The rendered frame to check.</param>
        /// <param name="referenceName">Reference file stem, no extension (e.g. "gv0-fill").</param>
        public static void Assert(VisualFrame frame, string referenceName)
        {
            if (frame.NoGpuContext)
            {
                NUnit.Framework.Assert.Inconclusive(
                    $"golden '{referenceName}': NoGpuContext — batch EditMode has no GPU context here, cannot compare.");
                return;
            }

            // Nonzero-ink precondition — frame-wide, the SAME background predicate SnapshotCoverage uses
            // (InkStatsIn delegates to SnapshotCoverage.Tolerance). Deliberately NOT frame.Coverage().IsBlank:
            // IsBlank fires at >=97% background, which reads TRUE on a legitimate sparse label frame (measured
            // 0.27% filled in GeoJsonPointLabelFixtureTests) and would resolve Inconclusive forever.
            frame.InkStatsIn(0, 0, frame.Width, frame.Height, out _, out int totalInk);
            if (totalInk == 0)
            {
                NUnit.Framework.Assert.Inconclusive(
                    $"golden '{referenceName}': frame is background-only — nothing to compare.");
                return;
            }

            string referencePath = ReferencePath(referenceName);

            if (Environment.GetEnvironmentVariable(BakeEnvVar) == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
                byte[] png = SnapshotRenderer.EncodeRgba32ToPng(frame.RawPixels, frame.Width, frame.Height);
                File.WriteAllBytes(referencePath, png);
                NUnit.Framework.Assert.Inconclusive(
                    $"golden '{referenceName}': baked {referencePath} — re-run without {BakeEnvVar} to verify.");
                return;
            }

            if (!File.Exists(referencePath))
            {
                NUnit.Framework.Assert.Fail(
                    $"golden '{referenceName}': no reference at {referencePath}; bake via {BakeEnvVar}=1.");
                return;
            }

            byte[] refPixels = LoadReferencePixels(referencePath, out int refWidth, out int refHeight);

            if (refWidth != frame.Width || refHeight != frame.Height)
            {
                NUnit.Framework.Assert.Fail(
                    $"golden '{referenceName}': reference size {refWidth}x{refHeight} != frame size " +
                    $"{frame.Width}x{frame.Height} — a resized render target is a real change, not jitter.");
                return;
            }
            if (refPixels.Length != frame.RawPixels.Length)
            {
                NUnit.Framework.Assert.Fail(
                    $"golden '{referenceName}': reference buffer length {refPixels.Length} != frame buffer " +
                    $"length {frame.RawPixels.Length} despite matching dimensions — decode format mismatch.");
                return;
            }

            Compare(frame, refPixels, referenceName, referencePath);
        }

        /// <summary>Absolute path to the committed reference PNG for <paramref name="referenceName"/>.</summary>
        private static string ReferencePath(string referenceName)
            => Path.Combine(Application.dataPath, "Fixtures", "visual-references~", $"{referenceName}.png");

        /// <summary>
        /// Reads a reference PNG back to a flat RGBA32 buffer, bottom-left origin — the SAME convention as
        /// <see cref="VisualFrame.RawPixels"/>. Uses <c>File.ReadAllBytes</c> + <c>Texture2D.LoadImage</c>
        /// (bypasses AssetDatabase/importer pixel mutation) and <c>GetPixels32()</c> rather than
        /// <c>GetRawTextureData()</c> — an opaque PNG commonly decodes to RGB24 (3 bytes/px), and
        /// <c>GetRawTextureData</c> would silently misalign against a 4-byte stride; <c>GetPixels32</c> is
        /// format-independent and shares the same bottom-left row order.
        /// </summary>
        /// <param name="path">Absolute path to the reference PNG.</param>
        /// <param name="width">The decoded texture's width.</param>
        /// <param name="height">The decoded texture's height.</param>
        private static byte[] LoadReferencePixels(string path, out int width, out int height)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                tex.LoadImage(bytes); // resizes tex to the PNG's own dimensions.
                width  = tex.width;
                height = tex.height;

                Color32[] colors = tex.GetPixels32();
                var pixels = new byte[colors.Length * 4];
                for (int i = 0; i < colors.Length; i++)
                {
                    int b = i * 4;
                    pixels[b]     = colors[i].r;
                    pixels[b + 1] = colors[i].g;
                    pixels[b + 2] = colors[i].b;
                    pixels[b + 3] = colors[i].a;
                }
                return pixels;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Per-pixel max-channel-delta compare. On a pass, does nothing. On a fail, writes
        /// <c>&lt;name&gt;.actual.png</c>, <c>&lt;name&gt;.expected.png</c> and a heat-mapped
        /// <c>&lt;name&gt;.diff.png</c> to <c>Logs/snapshots/</c>, then fails loud with the differing-pixel
        /// bounding box and all three artifact paths.
        /// </summary>
        /// <param name="frame">The rendered frame (the "actual" side).</param>
        /// <param name="refPixels">The reference RGBA32 buffer, same layout as <see cref="VisualFrame.RawPixels"/>.</param>
        /// <param name="referenceName">Reference file stem, used to name artifacts.</param>
        /// <param name="referencePath">Absolute path of the reference PNG, copied to <c>&lt;name&gt;.expected.png</c> on fail.</param>
        private static void Compare(VisualFrame frame, byte[] refPixels, string referenceName, string referencePath)
        {
            byte[] actual = frame.RawPixels;
            int width = frame.Width, height = frame.Height;
            int totalPx = width * height;

            int differing = 0;
            int minX = width, minY = height, maxX = -1, maxY = -1;
            var diff = new byte[actual.Length];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int b = (y * width + x) * 4;
                    int dr = math.abs(actual[b]     - refPixels[b]);
                    int dg = math.abs(actual[b + 1] - refPixels[b + 1]);
                    int db = math.abs(actual[b + 2] - refPixels[b + 2]);
                    int da = math.abs(actual[b + 3] - refPixels[b + 3]);
                    int delta = math.max(math.max(dr, dg), math.max(db, da));

                    if (delta > MaxChannelDelta)
                    {
                        differing++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;

                        // Heat-mapped, NOT a raw subtract: saturated yellow->red by magnitude.
                        byte heat = (byte)math.clamp(delta * 2, 0, 255);
                        diff[b]     = 255;
                        diff[b + 1] = (byte)(255 - heat);
                        diff[b + 2] = 0;
                    }
                    else
                    {
                        // Dimmed grayscale of the actual pixel — visually "this matched", not flat black.
                        int gray = (actual[b] + actual[b + 1] + actual[b + 2]) / 3 / 4;
                        diff[b] = diff[b + 1] = diff[b + 2] = (byte)gray;
                    }
                    diff[b + 3] = 255;
                }
            }

            double differingFraction = (double)differing / totalPx;
            if (differingFraction <= MaxDifferingFraction) return; // pass — golden matches within jitter tolerance

            string actualPath = SnapshotRenderer.WritePngFromRgba32(actual, width, height, $"{referenceName}.actual.png");
            string diffPath   = SnapshotRenderer.WritePngFromRgba32(diff, width, height, $"{referenceName}.diff.png");
            string expectedPath = Path.Combine(SnapshotRenderer.GetSnapshotsDir(), $"{referenceName}.expected.png");
            Directory.CreateDirectory(SnapshotRenderer.GetSnapshotsDir());
            File.Copy(referencePath, expectedPath, overwrite: true);

            NUnit.Framework.Assert.Fail(
                $"golden '{referenceName}': {differing} differing px ({differingFraction:P3} > {MaxDifferingFraction:P3}), " +
                $"bbox=[{minX},{minY}]-[{maxX},{maxY}]. actual={actualPath} expected={expectedPath} diff={diffPath}");
        }
    }
}
#endif // UNITY_EDITOR
