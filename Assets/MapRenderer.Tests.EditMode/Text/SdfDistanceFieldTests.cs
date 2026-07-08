// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S18 Slice 2 — T3: the decoded glyph-PBF bitmap is a REAL signed distance field, not a coverage
    /// bitmap masquerading as one, against the same committed fixture
    /// (<c>Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes</c>) Slice 1's decode tests use.
    ///
    /// Convention confirmed: the MapLibre/Mapbox glyph-PBF SDF encodes the glyph OUTLINE at
    /// <see cref="IsoLevel"/> ≈ 0.75 × 255 (≈191) — not the generic-SDF 0.5 — leaving more of the byte
    /// range for outward distance (halo) than inward (fill); this is the (public, non-source) "Drawing
    /// Text with Signed Distance Fields in Mapbox GL" convention, cross-checked directly against the
    /// committed fixture's real 'A' bitmap below (deep-outside padding corners decode to flat 0, values
    /// climb smoothly toward the interior — never the reverse — confirming the sign and rough placement
    /// of the cutoff without needing to re-derive it from the pixels, which would be circular).
    ///
    /// Two checks, each run against BOTH the real fixture (must pass) and a synthetic coverage-style
    /// (flat 0 / flat 255, single-pixel jump) array:
    ///  1. Graded-edge structural guard — THE decisive tooth. A real SDF has many distinct mid-range
    ///     byte values forming a multi-pixel transition band; a coverage bitmap has none (it is exactly
    ///     {0, 255}), so it fails <c>distinctMidBandValues &gt; 10</c> outright (0 is never &gt; 10).
    ///  2. Scale-invariance — a real SDF's iso-level crossing, reconstructed from a coarse (every-2nd-
    ///     sample) set, agrees with the full-resolution reconstruction to a fraction of a pixel (positive
    ///     property, checked on real data only). Separately (different, coarser step — a hard edge has
    ///     no gradient for step=2 to expose, see the step-2-vs-step-4 note on the teeth test below), a
    ///     coverage bitmap's reconstructed position visibly SHIFTS as the sampling grid coarsens — the
    ///     scale-dependence a real SDF does not have. This second check demonstrates the failure mode
    ///     qualitatively; it is not a like-for-like re-run of check 2's real-data assertion.
    /// </summary>
    [TestFixture]
    public class SdfDistanceFieldTests
    {
        // 0.75 * 255 = 191.25 -> 191. The MapLibre/Mapbox SDF convention: the outline sits at ~0.75 of
        // the byte range (not the generic-SDF 0.5), asymmetrically favoring outward (halo) distance.
        private const int IsoLevel = 191;

        // A texel counts as "graded" (neither deep-outside nor deep-inside) if it falls strictly within
        // this band — loose on purpose: the point is "not flat 0/255", not pinning an exact width.
        private const int MidBandLo = 32;
        private const int MidBandHi = 223;

        // Same threshold used both to accept the real SDF's coarse/fine agreement and to reject the
        // synthetic coverage bitmap's coarse/fine divergence (see the two ScaleInvariance_* tests).
        private const double ScaleInvarianceTolerancePixels = 0.5;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static SdfGlyph LoadUppercaseA()
            => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0].Glyphs[65u];

        // =========================================================================================
        // 1. Graded-edge structural guard (real fixture): deep-outside padding corner is flat 0; the
        //    bitmap carries many distinct graded mid-range values, not a hard 0/255 split.
        // =========================================================================================
        [Test]
        public void StructuralGuard_GlyphEdgeTexelsAreGradedNotFlat()
        {
            SdfGlyph a = LoadUppercaseA();

            Assert.AreEqual(0, a.Bitmap[0], "a deep-padding corner texel must be flat 0 (far outside the spread radius)");

            int distinctMidBandValues = CountDistinctValuesInBand(a.Bitmap, MidBandLo, MidBandHi);
            Assert.Greater(distinctMidBandValues, 10,
                "a real SDF must carry many graded mid-range values around the glyph edge, not a hard 0/255 step");
        }

        // =========================================================================================
        // 1b. Teeth: the SAME check applied to a synthetic coverage-style (flat 0 / flat 255) array
        //     finds ZERO graded values — proving the guard above actually discriminates.
        // =========================================================================================
        [Test]
        public void StructuralGuard_Teeth_SyntheticCoverageBitmapHasNoGradedBand()
        {
            byte[] coverageBitmap = BuildSyntheticHardEdge(length: 21, jumpAtIndex: 10);

            Assert.AreEqual(0, CountDistinctValuesInBand(coverageBitmap, MidBandLo, MidBandHi),
                "a plain coverage bitmap is exactly {0,255} everywhere -- it must show ZERO graded mid-band values");
        }

        // =========================================================================================
        // 2. Scale-invariance (real fixture): row 3 of 'A' (0-based; the apex, empirically verified
        //    against the committed fixture to cross the iso-level cleanly) thresholded at IsoLevel from
        //    a coarse (every-2nd-sample) reconstruction agrees with the full-resolution reconstruction,
        //    within a fraction of a pixel -- the entire point of an SDF: crisp at any effective size.
        // =========================================================================================
        [Test]
        public void ScaleInvariance_CoarseAndFineSamplingAgreeOnIsoCrossing()
        {
            SdfGlyph a = LoadUppercaseA();
            int cellWidth = a.Width + 2 * GlyphSdf.Buffer;
            const int row = 3;
            var rowBytes = new byte[cellWidth];
            Array.Copy(a.Bitmap, row * cellWidth, rowBytes, 0, cellWidth);

            Assert.IsTrue(TryFindRisingCrossing(rowBytes, step: 1, IsoLevel, out double fineCrossing),
                "full-resolution sampling must find a rising iso crossing on this scanline");
            Assert.IsTrue(TryFindRisingCrossing(rowBytes, step: 2, IsoLevel, out double coarseCrossing),
                "half-resolution (every-2nd-sample) reconstruction must find the same rising crossing");

            Assert.LessOrEqual(Math.Abs(fineCrossing - coarseCrossing), ScaleInvarianceTolerancePixels,
                "the recovered iso-level edge position must be (near-)identical whether reconstructed from " +
                "a coarse or a fine sample set -- that is what lets one SDF texture stay crisp at any size");
        }

        // =========================================================================================
        // 2b. A coverage bitmap's reconstructed iso crossing SHIFTS as the sampling grid coarsens --
        //     the scale-dependence a real SDF does not have. Uses a coarser step (4, vs 2 for the real
        //     scanline above) deliberately: a hard, zero-width transition carries no gradient at all
        //     between its two flat plateaus, so a coarse-enough sample set slides the interpolated
        //     crossing toward whichever bracket it lands in; step=2 on THIS array only drifts ~0.25px
        //     (an alignment coincidence, not evidence the check is toothless -- the graded-value guard
        //     above is the decisive tooth). This is a qualitative demonstration of the failure mode, not
        //     a like-for-like re-run of the real-data assertion (which uses step=2 and passes it).
        // =========================================================================================
        [Test]
        public void ScaleInvariance_Teeth_SyntheticCoverageBitmapDiverges()
        {
            byte[] coverageBitmap = BuildSyntheticHardEdge(length: 24, jumpAtIndex: 12);

            Assert.IsTrue(TryFindRisingCrossing(coverageBitmap, step: 1, IsoLevel, out double fineCrossing));
            Assert.IsTrue(TryFindRisingCrossing(coverageBitmap, step: 4, IsoLevel, out double coarseCrossing));

            Assert.Greater(Math.Abs(fineCrossing - coarseCrossing), ScaleInvarianceTolerancePixels,
                "a coverage bitmap's coarse/fine iso crossings shift with sampling coarseness -- the " +
                "scale-dependence a real SDF (see the check above) does not have");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Flat 0 for [0, jumpAtIndex), flat 255 for [jumpAtIndex, length) -- a plain coverage mask.</summary>
        private static byte[] BuildSyntheticHardEdge(int length, int jumpAtIndex)
        {
            var data = new byte[length];
            for (int i = jumpAtIndex; i < length; i++) data[i] = 255;
            return data;
        }

        private static int CountDistinctValuesInBand(byte[] data, int lo, int hi)
        {
            var seen = new HashSet<byte>();
            foreach (byte v in data)
            {
                if (v >= lo && v <= hi) seen.Add(v);
            }
            return seen.Count;
        }

        /// <summary>
        /// Finds the first index i (in units of the ORIGINAL/native array, i.e. already scaled by
        /// <paramref name="step"/>) such that sample[i] &lt; iso &lt;= sample[i+step], subsampling
        /// <paramref name="row"/> every <paramref name="step"/> native indices, and linearly
        /// interpolates the sub-pixel crossing position within that bracket (native index units).
        /// </summary>
        private static bool TryFindRisingCrossing(byte[] row, int step, int iso, out double nativeCrossing)
        {
            int sampleCount = (row.Length - 1) / step + 1;
            for (int i = 0; i + 1 < sampleCount; i++)
            {
                int nativeA = i * step;
                int nativeB = (i + 1) * step;
                byte a = row[nativeA];
                byte b = row[nativeB];
                if (a < iso && b >= iso)
                {
                    double t = (iso - a) / (double)(b - a);
                    nativeCrossing = nativeA + t * step;
                    return true;
                }
            }
            nativeCrossing = 0;
            return false;
        }
    }
}
