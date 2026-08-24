// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S19 Slice 1 — T1 (THE decisive per-glyph quad golden: buffer + UV + pen-advance + anchor,
    /// element-by-element) and T8 (structural: no text-size parameter; <see cref="SymbolQuad"/> is
    /// blittable).
    /// </summary>
    [TestFixture]
    public class TextQuadLayoutTests
    {
        private const float Tolerance = 1e-3f;

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

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(TextDirection direction, params (uint codepoint, float advance)[] glyphs)
        {
            var list = new List<PositionedGlyph>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
            {
                list.Add(new PositionedGlyph { AtlasCodepoint = glyphs[i].codepoint, XAdvance = glyphs[i].advance, Cluster = i });
            }
            return new ShapedRun { Glyphs = list, Direction = direction };
        }

        // =========================================================================================
        // T1 — THE decisive test: per-glyph quad (buffer + UV + pen-advance + anchor), golden
        // hand-computed from entryA/entryLowerA (INPUT fields fetched from the atlas, not builder
        // output) — non-tautological.
        // =========================================================================================
        [Test]
        public void Layout_SingleLineCenterAnchor_MatchesHandComputedGolden()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);

            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, resultQuads);

            Assert.AreEqual(2, resultQuads.Count, "two visible glyphs, no whitespace -- exactly two quads");
            Assert.AreEqual(1, result.LineCount);

            // ---- Hand-computed golden, per the corrected TOP-anchored layout math ----
            // The glyph 'Top' metric is top-referenced, so the cell's TOP edge is at
            // (baselineY + Top + Buffer) and the cell grows DOWNWARD by CellSize.y; left edge at
            // (penX + Left - Buffer). (Convention correctness is a visual/Bucket-B check; this golden
            // guards the mechanical formula — buffer, CellSize, pen-advance — against a shallow impl.)
            float leftXA = 0f + entryA.Left - GlyphSdf.Buffer;
            float cellTopYA = 0f + entryA.Top + GlyphSdf.Buffer;
            float penXAfterA = entryA.Advance; // letter-spacing = 0 (default options)

            float leftXLowerA = penXAfterA + entryLowerA.Left - GlyphSdf.Buffer;
            float cellTopYLowerA = 0f + entryLowerA.Top + GlyphSdf.Buffer;
            float lineWidth = penXAfterA + entryLowerA.Advance; // == blockWidth (single line)

            // Center anchor: hAlign = .5; justify auto -> Center (factor .5), which cancels for a
            // single line (lineWidth == blockWidth) -- see TextQuadLayout's class doc. The y term is
            // the hand-derived optical-centre shift (§11 D12), NOT read back from the production
            // constants it exists to check: GlyphSdf.BaselineBelowReferencePx (26) minus half a
            // cap height (0.5 * (17/24)em * 24 = 8.5) = 17.5.
            float2 anchorShift = new float2(-0.5f * lineWidth, 17.5f);

            // TopLeft = (leftX, cellTopY); BottomRight = (leftX + CellSize.x, cellTopY - CellSize.y).
            float2 expectedTopLeftA = new float2(leftXA, cellTopYA) + anchorShift;
            float2 expectedBottomRightA = new float2(leftXA + entryA.CellSize.x, cellTopYA - entryA.CellSize.y) + anchorShift;
            float2 expectedTopLeftLowerA = new float2(leftXLowerA, cellTopYLowerA) + anchorShift;
            float2 expectedBottomRightLowerA = new float2(leftXLowerA + entryLowerA.CellSize.x, cellTopYLowerA - entryLowerA.CellSize.y) + anchorShift;

            SymbolQuad quadA = resultQuads[0];
            SymbolQuad quadLowerA = resultQuads[1];

            Assert.AreEqual(expectedTopLeftA.x, quadA.TopLeft.x, Tolerance, "'A' TopLeft.x");
            Assert.AreEqual(expectedTopLeftA.y, quadA.TopLeft.y, Tolerance, "'A' TopLeft.y");
            Assert.AreEqual(expectedBottomRightA.x, quadA.BottomRight.x, Tolerance, "'A' BottomRight.x");
            Assert.AreEqual(expectedBottomRightA.y, quadA.BottomRight.y, Tolerance, "'A' BottomRight.y");

            Assert.AreEqual(expectedTopLeftLowerA.x, quadLowerA.TopLeft.x, Tolerance, "'a' TopLeft.x");
            Assert.AreEqual(expectedTopLeftLowerA.y, quadLowerA.TopLeft.y, Tolerance, "'a' TopLeft.y");
            Assert.AreEqual(expectedBottomRightLowerA.x, quadLowerA.BottomRight.x, Tolerance, "'a' BottomRight.x");
            Assert.AreEqual(expectedBottomRightLowerA.y, quadLowerA.BottomRight.y, Tolerance, "'a' BottomRight.y");

            // UV: read directly off the fetched atlas entries (input, not the layout's own output).
            float2 atlasSize = atlas.Size;
            float2 atlasOriginA = entryA.AtlasOrigin;
            float2 atlasOriginLowerA = entryLowerA.AtlasOrigin;
            float2 expectedUvMinA = atlasOriginA / atlasSize;
            float2 expectedUvMaxA = (atlasOriginA + (float2)entryA.CellSize) / atlasSize;
            float2 expectedUvMinLowerA = atlasOriginLowerA / atlasSize;
            float2 expectedUvMaxLowerA = (atlasOriginLowerA + (float2)entryLowerA.CellSize) / atlasSize;

            Assert.AreEqual(expectedUvMinA.x, quadA.UvTopLeft.x, Tolerance);
            Assert.AreEqual(expectedUvMinA.y, quadA.UvTopLeft.y, Tolerance);
            Assert.AreEqual(expectedUvMaxA.x, quadA.UvBottomRight.x, Tolerance);
            Assert.AreEqual(expectedUvMaxA.y, quadA.UvBottomRight.y, Tolerance);

            Assert.AreEqual(expectedUvMinLowerA.x, quadLowerA.UvTopLeft.x, Tolerance);
            Assert.AreEqual(expectedUvMinLowerA.y, quadLowerA.UvTopLeft.y, Tolerance);
            Assert.AreEqual(expectedUvMaxLowerA.x, quadLowerA.UvBottomRight.x, Tolerance);
            Assert.AreEqual(expectedUvMaxLowerA.y, quadLowerA.UvBottomRight.y, Tolerance);
        }

        // =========================================================================================
        // T1 teeth: a quad sized to bare Width×Height (ignoring the 3px SDF buffer), positioned
        // without the buffer offset, or a pen that does not accumulate glyph-to-glyph, would all
        // yield DIFFERENT numbers than the golden above.
        // =========================================================================================
        [Test]
        public void Layout_Teeth_RequiresBufferedCellSizeAndAccumulatedPen()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, resultQuads);
            SymbolQuad quadA = resultQuads[0];
            SymbolQuad quadLowerA = resultQuads[1];

            // Tooth 1: quad size must be the buffered CellSize, not the bare glyph Width/Height
            // (CellSize - 2*Buffer).
            float actualWidthA = quadA.BottomRight.x - quadA.TopLeft.x;
            float actualHeightA = quadA.TopLeft.y - quadA.BottomRight.y;
            int bareWidthA = entryA.CellSize.x - 2 * GlyphSdf.Buffer;
            int bareHeightA = entryA.CellSize.y - 2 * GlyphSdf.Buffer;
            Assert.AreEqual(entryA.CellSize.x, actualWidthA, Tolerance, "quad width must be the buffered CellSize");
            Assert.AreEqual(entryA.CellSize.y, actualHeightA, Tolerance, "quad height must be the buffered CellSize");
            Assert.AreNotEqual((float)bareWidthA, actualWidthA, "a Width-only (no-buffer) quad would be narrower than the golden");
            Assert.AreNotEqual((float)bareHeightA, actualHeightA, "a Height-only (no-buffer) quad would be shorter than the golden");

            // Tooth 2: UV rect size must use CellSize, not bare Width/Height.
            float2 atlasSize = atlas.Size;
            float2 actualUvSize = quadA.UvBottomRight - quadA.UvTopLeft;
            float2 bareUvSize = new float2(bareWidthA, bareHeightA) / atlasSize;
            Assert.AreNotEqual(bareUvSize.x, actualUvSize.x, "a Width-only UV rect would not match the golden");
            Assert.AreNotEqual(bareUvSize.y, actualUvSize.y, "a Height-only UV rect would not match the golden");

            // Tooth 3: pen accumulates glyph-to-glyph. The anchor shift is a single additive constant
            // shared by both quads on the same line, so it cancels out of this relative delta --
            // isolating pen accumulation from anchor math entirely.
            float actualDeltaX = quadLowerA.TopLeft.x - quadA.TopLeft.x;
            float expectedDeltaXAccumulated = (entryA.Advance + entryLowerA.Left - GlyphSdf.Buffer) - (entryA.Left - GlyphSdf.Buffer);
            float wrongDeltaXNonAccumulated = entryLowerA.Left - entryA.Left; // if 'a' were placed at penX=0 too
            Assert.AreEqual(expectedDeltaXAccumulated, actualDeltaX, Tolerance);
            Assert.AreNotEqual(wrongDeltaXNonAccumulated, actualDeltaX, "a non-accumulating pen would place 'a' at the wrong x");
        }

        // =========================================================================================
        // T8a — structural: the layout API takes no text-size/size parameter.
        // =========================================================================================
        [Test]
        public void Layout_StructuralGuard_HasNoSizeParameter()
        {
            MethodInfo[] layoutMethods = typeof(TextQuadLayout).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Layout")
                .ToArray();

            Assert.AreEqual(1, layoutMethods.Length,
                "expected exactly the one no-alloc Layout overload (the allocating overload was retired with the managed layout-result carrier)");

            foreach (MethodInfo method in layoutMethods)
            {
                foreach (ParameterInfo p in method.GetParameters())
                {
                    Assert.IsFalse(p.Name.ToLowerInvariant().Contains("size"),
                        $"{method}: parameter '{p.Name}' must not be a size/text-size parameter (S19 is size-independent)");
                    Assert.IsFalse(p.ParameterType.Name.ToLowerInvariant().Contains("size"),
                        $"{method}: parameter type '{p.ParameterType.Name}' must not be a size-carrying type");
                }
            }
        }

        // =========================================================================================
        // T8b — structural: SymbolQuad is blittable (no managed field breaks the `unmanaged` constraint).
        // =========================================================================================
        [Test]
        public void SymbolQuad_IsBlittable()
        {
            AssertUnmanaged<SymbolQuad>();
        }

        private static void AssertUnmanaged<T>() where T : unmanaged
        {
        }

        // =========================================================================================
        // Options-construction safety net (see TextLayoutOptions class doc: this project's C# 9
        // LangVersion cannot use a hand-written parameterless struct constructor, C# 10+ only).
        // =========================================================================================
        [Test]
        public void Default_MatchesStyleSpecDefaults()
        {
            TextLayoutOptions options = TextLayoutOptions.Default;

            Assert.AreEqual(TextAnchor.Center, options.Anchor);
            Assert.AreEqual(0f, options.Offset.x);
            Assert.AreEqual(0f, options.Offset.y);
            Assert.AreEqual(0f, options.RadialOffset);
            Assert.AreEqual(TextJustify.Auto, options.Justify);
            Assert.AreEqual(10f, options.MaxWidthEm);
            Assert.AreEqual(1.2f, options.LineHeightEm);
            Assert.AreEqual(0f, options.LetterSpacingEm);
        }

        [Test]
        public void ZeroValuedOptions_FallsBackToSaneDefaults_ForMaxWidthAndLineHeight()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance));

            TextLayoutOptions zeroOptions = default;
            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in zeroOptions, resultQuads);

            Assert.AreEqual(1, resultQuads.Count);
            Assert.AreEqual(1, result.LineCount);
            float2 size = result.Max - result.Min;
            Assert.Greater(size.x, 0f, "a zero-valued MaxWidthEm must fall back, not collapse the layout");
            Assert.Greater(size.y, 0f, "a zero-valued LineHeightEm must fall back, not collapse the layout");
        }
    }
}
