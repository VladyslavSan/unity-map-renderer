// FillBandAttributeTests.cs — the fill boundary band's plumbing, checked where a rendered test cannot.
//
// WHY THESE ARE FILE-READING TESTS AND NOT RENDERS. A TEXCOORDn semantic binds to the MESH's
// VertexAttributeDescriptor index, globally — not to a pass's own Attributes struct — and an UNBOUND
// semantic silently zero-fills rather than failing to compile.
//
// The unqualified form of that claim — "no render can tell the six passes apart" — was TRUE ONLY WHILE
// every vertex carried side = 0, i.e. before the band node existed: back then a pass that forgot the
// attribute rendered exactly like one that has it, because the zero-fill WAS the correct value. Now that
// real band geometry ships, a missing attribute in one of the two FORWARD passes is render-visible. What
// stays invisible, and is what still justifies reading declarations rather than pixels, is the four
// DEPTH-writing passes: their band fragments are clipped, so a missing attribute there changes no pixel
// and no depth sample either way, and the only instrument that can see it is one that reads the file.
//
// The same holds for the depth clip itself. A band fragment at coverage 0 under a pass's hardcoded
// ZWrite On writes depth and casts a shadow, so a missing clip fattens the shadow silhouette — and now
// that band fragments exist, that is a live defect rather than a latent one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Meshing;

namespace MapRenderer.Tests.Structure
{
    [TestFixture]
    public class FillBandAttributeTests
    {
        /// <summary>The two fill entry points. Every pass body below is reached through one of them.</summary>
        private static readonly string[] FillShaders = { "Fill.shader", "FillUnlit.shader" };

        /// <summary>The band attribute's mesh slot. TexCoord1/TexCoord2 would silently feed the forward and
        /// GBuffer passes' lightmap UVs instead.</summary>
        private const string BandSemantic = "TEXCOORD3";

        /// <summary>One ShaderLab <c>Pass</c> block, reduced to what these teeth ask about.</summary>
        private readonly struct FillPass
        {
            /// <summary>The entry point that declares this pass, for failure messages.</summary>
            public readonly string Shader;

            /// <summary>File name of the pass body the block includes, e.g. <c>Fill_DepthOnlyPass.hlsl</c>.</summary>
            public readonly string BodyFile;

            /// <summary>True when the block hardcodes <c>ZWrite On</c> — the discriminator for "this pass
            /// writes depth", which is what makes clipping the band mandatory rather than optional.</summary>
            public readonly bool WritesDepth;

            /// <param name="shader">The declaring entry point.</param>
            /// <param name="bodyFile">The included pass-body file name.</param>
            /// <param name="writesDepth">Whether the block hardcodes <c>ZWrite On</c>.</param>
            public FillPass(string shader, string bodyFile, bool writesDepth)
            {
                Shader      = shader;
                BodyFile    = bodyFile;
                WritesDepth = writesDepth;
            }
        }

        /// <summary>
        /// Every <c>Pass</c> block of both fill entry points, paired with the pass body it includes —
        /// derived from the shaders themselves rather than listed here, so a seventh pass joins these
        /// teeth by existing. The count is asserted at each call site: a regex that stopped matching
        /// would otherwise turn every tooth below into a vacuous loop over nothing.
        /// </summary>
        private static List<FillPass> EnumerateFillPasses()
        {
            var passes = new List<FillPass>();
            foreach (string shaderName in FillShaders)
            {
                string text = File.ReadAllText(ShaderPropertyParser.MapShaderPath(shaderName));
                // Blocks start at a `Pass` line; the last one runs to end of file. Splitting on the keyword
                // is enough because a pass body's own braces never contain another `Pass` line.
                string[] blocks = Regex.Split(text, @"^\s*Pass\s*$", RegexOptions.Multiline);
                for (int i = 1; i < blocks.Length; i++)   // [0] is everything before the first Pass
                {
                    Match include = Regex.Match(blocks[i], "#include\\s+\"(?:\\.\\./)?([A-Za-z0-9_]+Pass\\.hlsl)\"");
                    if (!include.Success) continue;
                    bool zWriteOn = Regex.IsMatch(blocks[i], @"^\s*ZWrite\s+On\s*$", RegexOptions.Multiline);
                    passes.Add(new FillPass(shaderName, include.Groups[1].Value, zWriteOn));
                }
            }
            return passes;
        }

        /// <summary>Reads a pass body by file name, with comments stripped so a mention in prose can never
        /// satisfy a check about code.</summary>
        /// <param name="bodyFile">Pass-body file name, as included by the shader.</param>
        private static string ReadPassBody(string bodyFile)
            => ShaderPropertyParser.StripHlslComments(
                   File.ReadAllText(ShaderPropertyParser.MapShaderPath(bodyFile)));

        // ── The semantic, and the hook that carries it ──────────────────────────────────────────

        /// <summary>
        /// Every fill pass declares the band attribute at the mesh's own slot and threads it through the
        /// vertex hook. This is the tooth a render cannot be: at <c>side = 0</c> a zero-filled unbound
        /// semantic and a correctly bound one produce identical pixels.
        /// </summary>
        [Test]
        public void EveryFillPass_DeclaresTheBandAttribute_AndThreadsItThroughTheHook()
        {
            List<FillPass> passes = EnumerateFillPasses();
            Assert.That(passes.Count, Is.EqualTo(8),
                "Expected 8 fill pass blocks across Fill.shader + FillUnlit.shader; found " +
                $"{passes.Count} [{string.Join(", ", passes.Select(p => p.Shader + ":" + p.BodyFile))}]. " +
                "A block regex that drifted would make every assertion below vacuous.");

            foreach (string bodyFile in passes.Select(p => p.BodyFile).Distinct())
            {
                var semantics = ShaderPropertyParser.ParseStructSemantics(
                    ShaderPropertyParser.MapShaderPath(bodyFile), "Attributes");

                Assert.That(semantics, Does.Contain(BandSemantic),
                    $"{bodyFile}'s Attributes struct does not declare {BandSemantic}. An unbound semantic " +
                    "zero-fills SILENTLY — the compiler, every uniform-path test and every debug view read " +
                    "this as correct, which is why it is checked here and not by rendering.");

                Assert.That(ReadPassBody(bodyFile), Does.Match(@"MapVertexModify\([^;]*input\.band"),
                    $"{bodyFile} declares the band attribute but never hands it to MapVertexModify, so the " +
                    "fragment stage reads an uninitialised side.");
            }
        }

        // ── Shade it or clip it — never neither ─────────────────────────────────────────────────

        /// <summary>
        /// A fill pass either shades the band (multiplying its coverage into alpha) or clips it away —
        /// and which one is decided by whether the pass writes depth, not by a list kept here. A pass
        /// hardcoding <c>ZWrite On</c> must clip: a coverage-0 band fragment still writes depth under it,
        /// which would fatten the depth prepass and the shadow silhouette by the band's whole width.
        /// </summary>
        [Test]
        public void EveryFillPass_EitherShadesTheBandOrClipsIt_ByWhetherItWritesDepth()
        {
            List<FillPass> passes = EnumerateFillPasses();
            Assert.That(passes.Count, Is.EqualTo(8), "precondition: all 8 fill pass blocks must be found.");
            Assert.That(passes.Count(p => p.WritesDepth), Is.EqualTo(6),
                "precondition: the 6 depth-writing pass blocks (ShadowCaster/GBuffer/DepthOnly/DepthNormals " +
                "across both entry points) must be detected by their hardcoded ZWrite On.");

            foreach (FillPass pass in passes)
            {
                string body     = ReadPassBody(pass.BodyFile);
                bool shades     = body.IndexOf("MapFillBandCoverage(input.side)", StringComparison.Ordinal) >= 0;
                // The whole ternary, not just its opening: `clip(input.side > 0 ? 1 : -1)` is the
                // inversion that discards the fill INTERIOR and keeps only the band, and a match on
                // the condition alone reads it as correct.
                bool clips      = Regex.IsMatch(body,
                    @"clip\(\s*input\.side\s*>\s*0\.0\s*\?\s*-1\.0\s*:\s*1\.0\s*\)");
                string where    = $"{pass.Shader} -> {pass.BodyFile}";

                if (pass.WritesDepth)
                {
                    Assert.That(clips, Is.True,
                        $"{where} hardcodes ZWrite On but does not clip the band. A coverage-0 band fragment " +
                        "writes depth and casts a shadow, so the silhouette would grow by the band's width.");
                    Assert.That(shades, Is.False,
                        $"{where} is a depth-writing pass and must not shade the band.");
                }
                else
                {
                    Assert.That(shades, Is.True,
                        $"{where} shades pixels but never multiplies MapFillBandCoverage into alpha — the " +
                        "boundary would stay hard.");
                }
            }
        }

        /// <summary>
        /// Coverage is folded in AFTER the fill-pattern branch. That branch REPLACES alpha rather than
        /// modulating it, so coverage applied earlier is discarded on a patterned fill and its boundary
        /// silently stays hard — a defect no unpatterned fixture can see.
        /// </summary>
        [Test]
        public void ForwardPasses_ApplyBandCoverage_AfterThePatternBranch()
        {
            List<FillPass> forward = EnumerateFillPasses().Where(p => !p.WritesDepth).ToList();
            Assert.That(forward.Count, Is.EqualTo(2),
                $"Expected the 2 shading fill passes (Fill.shader ForwardLit + FillUnlit.shader Unlit); " +
                $"found {forward.Count}. Without this the loop below iterates nothing and passes vacuously, " +
                "which is the one failure a green result cannot distinguish itself from.");

            foreach (FillPass pass in forward)
            {
                string body = ReadPassBody(pass.BodyFile);
                int patternReplace = body.LastIndexOf("patternTexel.a * _Opacity", StringComparison.Ordinal);
                int coverage       = body.IndexOf("MapFillBandCoverage(input.side)", StringComparison.Ordinal);

                Assert.That(patternReplace, Is.GreaterThanOrEqualTo(0),
                    $"{pass.BodyFile} no longer carries the fill-pattern alpha replacement this tooth orders " +
                    "against — the ordering rule it encodes may no longer apply, or the search string drifted.");
                Assert.That(coverage, Is.GreaterThan(patternReplace),
                    $"{pass.BodyFile} multiplies the band coverage into alpha BEFORE the fill-pattern branch " +
                    "replaces it, so a patterned fill loses its boundary antialiasing silently.");
            }
        }

        // ── The formula is the shipped one ──────────────────────────────────────────────────────

        /// <summary>
        /// The fill's coverage is the line path's shipped outer-edge ramp, not a re-derivation, and both
        /// files are read here so it stays that way. The gradient in particular must remain EUCLIDEAN:
        /// <c>fwidth</c> is the Manhattan length, over-reads by up to 1.41x on a diagonal silhouette, and
        /// agrees exactly with the Euclidean form on every axis-aligned fixture — so no axis-aligned tooth
        /// anywhere can catch that substitution and this one must.
        ///
        /// <para>The line file writes <c>absSide</c> where the fill writes <c>side</c>: the line's band
        /// straddles its centre so its coordinate is signed, while the fill's band lies wholly outside the
        /// boundary and never goes negative. That single substitution is applied here and is the only
        /// licensed difference between the two expressions.</para>
        /// </summary>
        [Test]
        public void FillBandCoverage_IsVerbatimTheShippedLineCoverageExpression()
        {
            string line = File.ReadAllText(ShaderPropertyParser.MapShaderPath("Line_VertexExtrude.hlsl"));
            string fill = File.ReadAllText(ShaderPropertyParser.MapShaderPath("Fill_BandCoverage.hlsl"));

            const string gradient = "max(length(float2(ddx(side), ddy(side))), 1e-6)";
            const string coverage = "saturate((1.0 - absSide) / sideGrad)";

            Assert.That(line, Does.Contain(gradient),
                "Line_VertexExtrude.hlsl no longer computes the Euclidean side gradient this way; the fill " +
                "band is now reusing an expression the line path has stopped using.");
            Assert.That(line, Does.Contain(coverage),
                "Line_VertexExtrude.hlsl no longer computes the outer-edge coverage this way.");

            Assert.That(fill, Does.Contain(gradient),
                "Fill_BandCoverage.hlsl must carry the shipped Euclidean gradient verbatim — an fwidth here " +
                "renders a 45-degree silhouette WRONG while every axis-aligned fixture stays green — and " +
                "wrong in the unobvious direction: coverage is (1 - side) DIVIDED by the gradient, so an " +
                "over-read gradient makes coverage fall faster. Measured, the 45-degree ramp narrows to " +
                "0.80 px and the coverage at the boundary itself drops below 1.");
            Assert.That(fill, Does.Contain(coverage.Replace("absSide", "side")),
                "Fill_BandCoverage.hlsl must carry the shipped coverage expression, with the line's signed " +
                "absSide as the fill's unsigned side and nothing else changed.");
        }

        // ── The displacement and the coverage coordinate never multiply ─────────────────────────

        /// <summary>
        /// <c>Fill_VertexModify.hlsl</c>'s band displacement must NOT be scaled by <c>band.z</c>.
        ///
        /// <para>The attribute's two halves are independent: <c>band.xy</c> IS the displacement in device
        /// pixels (miter factor in its magnitude) and <c>band.z</c> is only the coverage coordinate. A
        /// product of the two is indistinguishable from the correct expression on the FLAT arm, where the
        /// band job emits <c>z</c> in <c>{0,1}</c> and <c>xy == 0</c> whenever <c>z == 0</c> — so every flat
        /// tooth, every golden and both frozen digests stay green either way. On the CURVED arm
        /// <c>GlobeFillSubdivideJob</c> splits a band edge at its midpoint and lerps BOTH halves, so the
        /// product is quadratic in the split parameter: the strip pinches to a quarter pixel at every
        /// midpoint and compounds with depth, exactly where the shipped globe scene lives.</para>
        ///
        /// <para>A forbidden-token check, so it fails CLOSED: it cannot pass by matching nothing. RED-verify
        /// by restoring the <c>* band.z</c> factor on the <c>positionOS +=</c> line.</para>
        /// </summary>
        [Test]
        public void BandDisplacement_IsNotScaledByTheCoverageCoordinate()
        {
            string code = ShaderPropertyParser.StripHlslComments(
                File.ReadAllText(ShaderPropertyParser.MapShaderPath("Fill_VertexModify.hlsl"), Encoding.UTF8));

            int anchor = code.IndexOf("MapPixelsToWorld(bandCenterWS", StringComparison.Ordinal);
            Assert.That(anchor, Is.GreaterThanOrEqualTo(0),
                "Fill_VertexModify.hlsl no longer measures the band displacement through " +
                "MapPixelsToWorld(bandCenterWS, ...) — this tooth reads that statement and can no longer " +
                "find it, so it is measuring nothing. Re-point it at the displacement site.");
            // The WHOLE statement, not the tail after the anchor: a `* band.z` written BEFORE the
            // MapPixelsToWorld call is the form a restore would most naturally take, and a window that
            // starts at the anchor cannot see it. No earlier ';' gives -1 → start 0, so the window only
            // ever widens — this never fails open.
            int start = code.LastIndexOf(';', anchor) + 1;
            int end   = code.IndexOf(';', anchor);
            Assert.That(end, Is.GreaterThan(start), "the displacement statement must be terminated.");
            string statement = code.Substring(start, end - start);

            Assert.That(Regex.IsMatch(statement, @"\bband\s*\.\s*z\b"), Is.False,
                "Fill_VertexModify.hlsl scales the band displacement by band.z. The displacement is band.xy " +
                "alone; band.z is only the coverage coordinate. Multiplying them is exact on the flat arm " +
                "(z is 0 or 1 there) and quadratic on the globe, where subdivision lerps both halves across " +
                $"a split band edge — a quarter-pixel pinch at every midpoint. Statement: {statement}");
        }

        // ── The mesh actually carries it ────────────────────────────────────────────────────────

        /// <summary>
        /// The built fill mesh declares the band attribute on stream 1 with the stride that implies. The
        /// shader-side teeth above are all satisfied by declarations; this is the half that says the mesh
        /// supplies what they declare — the two ends of exactly the binding that zero-fills in silence.
        /// </summary>
        [Test]
        public void BuiltFillMesh_CarriesTheBandAttribute_OnTheSharedUvStream()
        {
            // FIRST, before the mesh is built: a struct that has grown makes GetVertexData<T>(1) throw
            // inside the builder, and a harness exception is not this tooth's message.
            Assert.That(UnsafeUtility.SizeOf<StyledFillTileBuilder.FillPatternUvBand>(), Is.EqualTo(20),
                "FillPatternUvBand must stay 20 B to match stream 1's descriptors. A field added here and " +
                "not to FillVertexDescriptors makes GetVertexData<FillPatternUvBand>(1) return a SHORT " +
                "array that the write job walks off the end of — bounds-checked in the Editor, silent in " +
                "a release player.");

            var (fillGo, material) = FillSceneHelper.BuildFillGo();
            try
            {
                Mesh mesh = fillGo.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh, Is.Not.Null, "precondition: the fixture layer must build a real mesh.");

                VertexAttributeDescriptor band = mesh.GetVertexAttributes()
                    .FirstOrDefault(a => a.attribute == VertexAttribute.TexCoord3);

                Assert.That(band.attribute, Is.EqualTo(VertexAttribute.TexCoord3),
                    "The fill mesh declares no TexCoord3 — the shaders' band attribute would zero-fill.");
                Assert.That(band.dimension, Is.EqualTo(3), "The band attribute is (dirEast, dirNorth, side).");
                Assert.That(band.format, Is.EqualTo(VertexAttributeFormat.Float32),
                    "The band attribute must be Float32 — the shaders read it as a float3.");
                Assert.That(band.stream, Is.EqualTo(1),
                    "The band attribute shares stream 1 with TexCoord0: the fill mesh already uses all four " +
                    "streams Unity allows, so it could not have one of its own.");
                Assert.That(mesh.GetVertexBufferStride(1), Is.EqualTo(20),
                    "Stream 1 is TexCoord0 (8 B) + TexCoord3 (12 B). A stride that is not 20 means the " +
                    "descriptors and the struct the write job casts the stream to have diverged.");
            }
            finally
            {
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (fillGo != null) UnityEngine.Object.DestroyImmediate(fillGo);
            }
        }
    }
}
