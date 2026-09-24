// Unity EditMode only — the golden reference-image regression layer.
// NOT registered in Tools/core-tests/core-tests.csproj (engine-bound: Texture2D encode/decode).
//
// Non-obvious why: a golden is a CHANGE DETECTOR beside the kit's analytic teeth, not a replacement. It only
// says "different from the last bake" and can lock in a WRONG image. It guards two traps (docs/lessons-learned.md):
//   1. Vacuous all-black golden: a frame-wide nonzero-ink precondition runs before every compare.
//   2. Orientation-blind compare: the frame is bottom-left, and a loaded PNG must be read back the same way.

#if UNITY_EDITOR
using System;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The golden-image comparer: a fixture calls <see cref="Assert"/> after <see cref="VisualScene.Render"/>.
    /// References live at <c>Assets/Fixtures/visual-references~/&lt;referenceName&gt;.png</c>; the <c>~</c>
    /// makes Unity skip the folder, and the reference is read as raw bytes from the path
    /// <see cref="ReferencePath"/> returns.
    /// Failure artifacts (actual, expected, heat-mapped diff) go to <c>Logs/snapshots/</c>.
    /// </summary>
    internal static class GoldenImage
    {
        /// <summary>Per-pixel max-channel byte delta above which a pixel counts as "differing". Tuned on this
        /// host — widen only with a recorded reason.</summary>
        private const int MaxChannelDelta = 4;

        /// <summary>Fraction of differing pixels above which the frame fails the golden. Tuned on this host —
        /// widen only with a recorded reason.</summary>
        private const double MaxDifferingFraction = 0.002;

        /// <summary>Env var gating the bake path — set to <c>"1"</c> to (re)write the
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
            // Nonzero-ink precondition, frame-wide. Not Coverage().IsBlank: it fires at >=97% background, which
            // is TRUE on a legitimate sparse symbol frame.
            frame.InkStatsIn(0, 0, frame.Width, frame.Height, out _, out int totalInk);
            if (totalInk == 0)
            {
                NUnit.Framework.Assert.Fail(
                    $"golden '{referenceName}': frame is background-only — nothing to compare.");
            }

            string referencePath = ReferencePath(referenceName);

            if (Environment.GetEnvironmentVariable(BakeEnvVar) == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
                byte[] png = SnapshotRenderer.EncodeRgba32ToPng(frame.Pixels);
                File.WriteAllBytes(referencePath, png);
                NUnit.Framework.Assert.Inconclusive(
                    $"golden '{referenceName}': baked {referencePath} — re-run without {BakeEnvVar} to verify.");
            }

            if (!File.Exists(referencePath))
            {
                NUnit.Framework.Assert.Fail(
                    $"golden '{referenceName}': no reference at {referencePath}; bake via {BakeEnvVar}=1.");
                return;
            }

            Frame reference = LoadReferencePixels(referencePath);

            if (reference.Width != frame.Width || reference.Height != frame.Height)
            {
                NUnit.Framework.Assert.Fail(
                    $"golden '{referenceName}': reference size {reference.Width}x{reference.Height} != frame size " +
                    $"{frame.Width}x{frame.Height} — a resized render target is a real change, not jitter.");
                return;
            }
            if (reference.Pixels.Length != frame.Pixels.Pixels.Length)
            {
                NUnit.Framework.Assert.Fail(
                    $"golden '{referenceName}': reference pixel count {reference.Pixels.Length} != frame pixel " +
                    $"count {frame.Pixels.Pixels.Length} despite matching dimensions — decode format mismatch.");
                return;
            }

            Compare(frame, reference, referenceName, referencePath);
        }

        /// <summary>Absolute path to the committed reference PNG for <paramref name="referenceName"/>.</summary>
        private static string ReferencePath(string referenceName)
            => Path.Combine(Application.dataPath, "Fixtures", "visual-references~", $"{referenceName}.png");

        /// <summary>
        /// Reads a reference PNG back to a <see cref="Frame"/>, bottom-left origin — the SAME convention as
        /// <see cref="VisualFrame.Pixels"/>. Uses <c>File.ReadAllBytes</c> + <c>Texture2D.LoadImage</c>
        /// (bypasses AssetDatabase/importer pixel mutation) and <c>GetPixels32()</c> — format-independent (an
        /// opaque PNG commonly decodes to RGB24, which <c>GetRawTextureData</c> would misalign against a
        /// 4-byte stride) and shares the same bottom-left row order.
        /// </summary>
        /// <param name="path">Absolute path to the reference PNG.</param>
        private static Frame LoadReferencePixels(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var bag = new ObjectDisposalBag();
            var tex = bag.Track(new Texture2D(2, 2, TextureFormat.RGBA32, false));
            tex.LoadImage(bytes); // resizes tex to the PNG's own dimensions.
            return new Frame(tex.GetPixels32(), tex.width, tex.height);
        }

        /// <summary>
        /// Per-pixel max-channel-delta compare. On a pass, does nothing. On a fail, writes
        /// <c>&lt;name&gt;.actual.png</c>, <c>&lt;name&gt;.expected.png</c> and a heat-mapped
        /// <c>&lt;name&gt;.diff.png</c> to <c>Logs/snapshots/</c>, then fails loud with the differing-pixel
        /// bounding box and all three artifact paths.
        /// </summary>
        /// <param name="frame">The rendered frame (the "actual" side).</param>
        /// <param name="reference">The reference frame, same layout as <see cref="VisualFrame.Pixels"/>.</param>
        /// <param name="referenceName">Reference file stem that names artifacts.</param>
        /// <param name="referencePath">Absolute path of the reference PNG, copied to <c>&lt;name&gt;.expected.png</c> on fail.</param>
        private static void Compare(VisualFrame frame, Frame reference, string referenceName, string referencePath)
        {
            Color32[] actual = frame.Pixels.Pixels;
            Color32[] refPixels = reference.Pixels;
            int width = frame.Width, height = frame.Height;
            int totalPx = width * height;

            int differing = 0;
            int minX = width, minY = height, maxX = -1, maxY = -1;
            var diff = new Color32[actual.Length];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    Color32 a = actual[i], r = refPixels[i];
                    int dr = math.abs(a.r - r.r);
                    int dg = math.abs(a.g - r.g);
                    int db = math.abs(a.b - r.b);
                    int da = math.abs(a.a - r.a);
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
                        diff[i] = new Color32(255, (byte)(255 - heat), 0, 255);
                    }
                    else
                    {
                        // Dimmed grayscale of the actual pixel — visually "this matched", not flat black.
                        byte gray = (byte)((a.r + a.g + a.b) / 3 / 4);
                        diff[i] = new Color32(gray, gray, gray, 255);
                    }
                }
            }

            double differingFraction = (double)differing / totalPx;
            if (differingFraction <= MaxDifferingFraction) return; // pass — golden matches within jitter tolerance

            string actualPath = SnapshotRenderer.WritePngFromRgba32(frame.Pixels, $"{referenceName}.actual.png");
            string diffPath   = SnapshotRenderer.WritePngFromRgba32(new Frame(diff, width, height), $"{referenceName}.diff.png");
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
