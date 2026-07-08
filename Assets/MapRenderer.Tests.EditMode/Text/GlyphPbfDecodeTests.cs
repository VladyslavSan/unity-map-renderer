// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S18 Slice 1 — T2: glyph-PBF decode against a real fixture (demotiles "Noto Sans Regular" 0-255
    /// range, committed as <c>Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes</c>).
    ///
    /// Golden values below were pinned by decoding the committed fixture with an independent (Python,
    /// non-Unity) generic protobuf walker — not by round-tripping through the decoder under test.
    /// </summary>
    [TestFixture]
    public class GlyphPbfDecodeTests
    {
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

        private static GlyphPbfRange DecodeLatinRange() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes"));

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // =========================================================================================
        // 0. Fontstack / range envelope decodes.
        // =========================================================================================
        [Test]
        public void Decode_ProducesOneFontStack_WithParsedRange()
        {
            GlyphPbfRange result = DecodeLatinRange();

            Assert.AreEqual(1, result.Stacks.Count, "the 0-255.pbf fixture carries one fontstack");
            FontStackGlyphs stack = result.Stacks[0];
            StringAssert.StartsWith("Noto Sans Regular", stack.Name);
            Assert.AreEqual(0, stack.RangeStart, "range string '0-255' parses to start=0");
            Assert.AreEqual(255, stack.RangeEnd, "range string '0-255' parses to end=255");
            Assert.AreEqual(223, stack.Glyphs.Count, "223 glyph records present in the 0-255 range fixture");
        }

        // =========================================================================================
        // 1. Exact metrics for known codepoints ('A', 'a') — both carry a bitmap, non-negative Left.
        // =========================================================================================
        [Test]
        public void Decode_ExactMetrics_UppercaseA()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(65u, out SdfGlyph a), "codepoint 65 ('A') present");

            Assert.AreEqual(65u, a.Codepoint);
            Assert.AreEqual(15, a.Width);
            Assert.AreEqual(17, a.Height);
            Assert.AreEqual(0, a.Left);
            Assert.AreEqual(-9, a.Top);
            Assert.AreEqual(15, a.Advance);
            Assert.IsTrue(a.HasBitmap);
        }

        [Test]
        public void Decode_ExactMetrics_LowercaseA()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(97u, out SdfGlyph a), "codepoint 97 ('a') present");

            Assert.AreEqual(97u, a.Codepoint);
            Assert.AreEqual(11, a.Width);
            Assert.AreEqual(13, a.Height);
            Assert.AreEqual(1, a.Left);
            Assert.AreEqual(-13, a.Top);
            Assert.AreEqual(13, a.Advance);
            Assert.IsTrue(a.HasBitmap);
        }

        // =========================================================================================
        // 2. Space (codepoint 32) has no bitmap in the PBF — decoder must tolerate the absent
        //    `bitmap` field (whitespace/zero-advance glyphs) rather than throwing or fabricating one.
        // =========================================================================================
        [Test]
        public void Decode_Space_HasNoBitmap()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(32u, out SdfGlyph space), "codepoint 32 (space) present");

            Assert.AreEqual(0, space.Width);
            Assert.AreEqual(0, space.Height);
            Assert.AreEqual(-26, space.Top);
            Assert.AreEqual(6, space.Advance);
            Assert.IsNull(space.Bitmap, "space carries no bitmap field in the PBF");
            Assert.IsFalse(space.HasBitmap);
        }

        // =========================================================================================
        // 3. Zigzag-vs-plain-varint discriminator: 'J' (codepoint 74) has a NEGATIVE Left (-2).
        //    The wire byte for this field is the zigzag encoding of -2 (raw varint value 3); reading
        //    it as a plain (non-zigzag) varint would yield 3, not -2 — a different, wrong value.
        //    'A'/'a' above (Left = 0 / 1) do not discriminate this bug since small non-negative zigzag
        //    values coincide with small positive raw varints.
        // =========================================================================================
        [Test]
        public void Decode_NegativeLeftAndTop_RequireZigzagDecode()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(74u, out SdfGlyph j), "codepoint 74 ('J') present");

            Assert.AreEqual(6, j.Width);
            Assert.AreEqual(22, j.Height);
            Assert.AreEqual(-2, j.Left, "zigzag-decoded Left must be -2; plain-varint decode would yield 3");
            Assert.AreEqual(-9, j.Top);
            Assert.AreEqual(6, j.Advance);
            Assert.IsTrue(j.HasBitmap);
        }

        // =========================================================================================
        // 4. Structural guard: decoded bitmap length == (Width + 2*Buffer) * (Height + 2*Buffer).
        //    A "width = bitmap.Length / height" shortcut that ignores the buffer fails this.
        // =========================================================================================
        [Test]
        public void Decode_BitmapLength_MatchesBufferedCellDimensions()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];

            foreach (uint codepoint in new uint[] { 65u, 97u, 74u })
            {
                SdfGlyph g = stack.Glyphs[codepoint];
                int expected = (g.Width + 2 * GlyphSdf.Buffer) * (g.Height + 2 * GlyphSdf.Buffer);
                Assert.AreEqual(expected, g.Bitmap.Length,
                    $"codepoint {codepoint}: bitmap length must equal (width+2*buffer)*(height+2*buffer)");
                Assert.AreEqual(new int2(g.Width + 6, g.Height + 6), g.CellSize);
            }
        }

        // =========================================================================================
        // 5. Exact byte-match (via SHA-256 golden hash) of 'A''s decoded SDF bitmap — pinned against
        //    the committed fixture with an independent, non-Unity protobuf walker.
        // =========================================================================================
        [Test]
        public void Decode_UppercaseA_BitmapMatchesGoldenHash()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            SdfGlyph a = stack.Glyphs[65u];

            Assert.AreEqual(483, a.Bitmap.Length);
            Assert.AreEqual(
                "06abcc9e85c98ceefdc40b0081d8b6770a1b08037abc03655cb81b6e96569c32",
                Sha256Hex(a.Bitmap),
                "decoded 'A' bitmap bytes must match the golden hash exactly (row-major, buffer-padded)");
        }
    }
}
