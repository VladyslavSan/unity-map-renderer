// Unity EditMode only — render tests requiring a GPU context (VisualScene/SnapshotRenderer). Degrade to
// Inconclusive when the context is unavailable in batch mode, per the other snapshot fixtures.
// NOT included in Tools/core-tests/core-tests.csproj.
//
// The product-observing tooth for the symbol text-color carrier split: every other tooth in this stage
// (SymbolTextColorCarrierTests) shares a CPU model of the fragment (`vertex × uniform`) with the code it
// checks — a model that would agree with a plausible wrong port just as readily as with the real one. This
// reads the rendered pixel itself, through the full production path (VisualScene → real MapView →
// SymbolRenderLayer's per-layer material → the real shader).
//
// Two arms differing ONLY in `text-color` — grey #808080 and white #ffffff — same glyph, same anchor. The
// ratio grey/white must land at linear(0.5019) ≈ 0.2158: the authored multiplier, applied ONCE. Landing
// on its square (≈0.0466) means both carriers hold the colour; landing on 1.0 means the vertex carries
// white and the uniform never reached the fragment.
//
// This tooth is an INVARIANT, not a regression check: it must pass against the UNMODIFIED tree too (the
// colour then rides the vertex stream alone, with no uniform in the picture at all) and pass again once
// the two-carrier split lands. A RED baseline means the harness resolved the wrong material — see the
// stage plan's escalation note before touching this file.

#if UNITY_EDITOR
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    internal class SymbolTextColorRenderTests
    {
        private static readonly TileId Tile = new TileId { Z = 6, X = 40, Y = 25 };
        private const string FontName    = "Fixture Text Color Font";
        private const string GlyphText   = "I"; // a simple, near-solid vertical stroke in a sans font
        private const double TextSizePx  = 220.0;
        private const int    SizePx      = 256;

        // Half-size of the box sampled for the LINEAR colour average — small enough to sit well inside the
        // glyph's solid interior (mirrors PaintColorRenderTests' "sample box comfortably inside" pattern),
        // once centred on the measured ink centroid rather than an assumed screen position.
        private const int SampleHalf = 3;

        private static byte[] _glyphBytes;

        private static (double lon, double lat) TileCenter()
        {
            double2 c = Tile.ToLonLat(0.5, 0.5, 1.0);
            return (c.x, c.y);
        }

        private static VisualScene BuildScene(string hexColor)
        {
            (double lon, double lat) = TileCenter();
            return VisualScene.New()
                .Source("points", GeoJson.Points((lon, lat, GlyphText)))
                .Layer(VisualLayer.SymbolText("labels").Source("points").TextField("name")
                    .TextSize(TextSizePx).TextFont(FontName).TextColor(hexColor))
                .Glyphs(FontName, LoadGlyphBytes())
                .Camera(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0.0 }, zoom: Tile.Z)
                .ExpectSymbolQuads(1);
        }

        /// <summary>Mean LINEAR RGB of the inclusive-exclusive box, decoded per-pixel from the sRGB-encoded
        /// readback (mirrors <c>PaintColorRenderTests.SampleLinear</c> — averaging encoded bytes first
        /// would be a different, wrong quantity).</summary>
        private static double3 SampleLinearBox(VisualFrame frame, int x0, int y0, int x1, int y1)
        {
            double3 sum = double3.zero;
            int n = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int b = (y * frame.Width + x) * 4;
                Color lin = new Color(frame.RawPixels[b] / 255f, frame.RawPixels[b + 1] / 255f,
                                      frame.RawPixels[b + 2] / 255f, 1f).linear;
                sum += new double3(lin.r, lin.g, lin.b);
                n++;
            }
            return sum / n;
        }

        [Test]
        public void ConstantTextColor_RenderedPixel_RidesTheUniformOnceNotSquared()
        {
            using var whiteScene = BuildScene("#ffffff");
            VisualFrame whiteFrame = whiteScene.Render(SizePx);
            if (whiteFrame.NoGpuContext)
            {
                Assert.Inconclusive("No GPU context (the white reference arm rendered blank).");
                return;
            }

            // Locate the glyph from the white arm's own render — no assumed screen position. The box then
            // reused for the grey arm too, since both arms share IDENTICAL geometry (colour is the only
            // difference), so the same rectangle samples the same glyph pixels in both.
            whiteFrame.InkStatsIn(0, 0, whiteFrame.Width, whiteFrame.Height, out double2 centroid, out int inkCount);
            TestContext.WriteLine($"[SymbolTextColorRender] white ink centroid={centroid} count={inkCount}");
            Assert.Greater(inkCount, 0, "the white arm must render some ink to locate the glyph from.");

            int cx = (int)math.round(centroid.x), cy = (int)math.round(centroid.y);
            int x0 = cx - SampleHalf, x1 = cx + SampleHalf + 1;
            int y0 = cy - SampleHalf, y1 = cy + SampleHalf + 1;

            double3 whiteSample = SampleLinearBox(whiteFrame, x0, y0, x1, y1);

            using var greyScene = BuildScene("#808080");
            VisualFrame greyFrame = greyScene.Render(SizePx);
            Assert.IsFalse(greyFrame.NoGpuContext, "the grey arm rendered blank while the white arm did not.");
            double3 greySample = SampleLinearBox(greyFrame, x0, y0, x1, y1);

            double3 ratio = greySample / whiteSample;
            const double expected = 0.2158; // linear(0x80/255)
            const double squared  = expected * expected;
            TestContext.WriteLine($"[SymbolTextColorRender] white={whiteSample} grey={greySample} ratio={ratio} " +
                                   $"expected~={expected} squared~={squared}");

            // The ratio construction cancels any factor common to BOTH arms — a shader edit that scales
            // every text pixel would leave the ratio (and this tooth) green over a real defect. Assert the
            // white arm's absolute intensity first, so a bad denominator (partial coverage, not colour)
            // reports as a bad denominator rather than a confusing ratio.
            for (int c = 0; c < 3; c++)
                Assert.That(whiteSample[c], Is.EqualTo(1.0).Within(0.02),
                    $"the white reference arm must render at full linear intensity. whiteSample={whiteSample}. " +
                    "A value below 1 means either the sample box missed the glyph's solid interior (the ratio's " +
                    "denominator is then partial coverage, not colour) or something scales ALL text — which the " +
                    "grey/white ratio cancels and cannot see.");

            for (int c = 0; c < 3; c++)
                Assert.That(ratio[c], Is.EqualTo(expected).Within(0.03),
                    $"channel {c}: a CONSTANT text-color must reach the fragment ONCE. ratio={ratio} " +
                    $"expected~={expected} squared~={squared}. Landing on the square means both the " +
                    "_TextColor uniform and the vertex COLOR stream carry the colour. Landing on 1.0 means " +
                    "the uniform never reached the fragment (vertex white, uniform unread or absent).");
        }

        // ── Glyph fixture loading (mirrors GeoJsonPointSymbolFixtureTests.LoadGlyphBytes) ────────────────

        private static byte[] LoadGlyphBytes()
            => _glyphBytes ??= LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");

        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { System.IO.Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new System.IO.DirectoryInfo(start);
                while (dir != null)
                {
                    string p = System.IO.Path.Combine(dir.FullName, System.IO.Path.Combine(relative));
                    if (System.IO.File.Exists(p)) return System.IO.File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new System.IO.FileNotFoundException(
                $"could not locate fixture file under any ancestor of the working directory: {System.IO.Path.Combine(relative)}");
        }
    }
}
#endif // UNITY_EDITOR
