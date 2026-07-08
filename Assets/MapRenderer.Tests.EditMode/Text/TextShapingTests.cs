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
    /// S18 Slice 3 — T1: THE decisive Model-A/Option-Y shaping test. Shapes Arabic
    /// <c>"&#x0645;&#x0631;&#x062D;&#x0628;&#x0627;"</c> ("marhaba" / hello — meem, reh, hah, beh, alef)
    /// with <see cref="CodepointTextShaper"/> and asserts the exact ordered
    /// <c>(AtlasCodepoint, Cluster)</c> sequence against a golden pinned by an INDEPENDENT hand
    /// derivation from the public Unicode joining tables — NOT by calling
    /// <see cref="ArabicJoining"/>/<see cref="CodepointTextShaper"/> and copying their output (that
    /// would be tautological; see the "anti-tautology" note in the class body below).
    ///
    /// ── Golden derivation (letter-by-letter, from ArabicShaping.txt Joining_Type values) ──────────
    /// Logical (reading) order: index 0 = meem (first-typed), index 4 = alef (last-typed).
    ///   i=0 MEEM  (U+0645, Dual_Joining):   no previous char -> cannot receive a join.
    ///                                       next = REH (Right_Joining, CAN receive a join) -> sends one.
    ///                                       => INITIAL form -> U+FEE3.
    ///   i=1 REH   (U+0631, Right_Joining):  prev = MEEM (Dual_Joining, CAN send) -> receives one.
    ///                                       Right_Joining letters can NEVER send a join forward
    ///                                       (that is the defining property of the 6 "non-connecting"
    ///                                       Arabic letters: alef/dal/dhal/reh/zain/waw), regardless
    ///                                       of what follows.
    ///                                       => FINAL form -> U+FEAE. (The word visually breaks here —
    ///                                          reh never connects to the following hah.)
    ///   i=2 HAH   (U+062D, Dual_Joining):   prev = REH (Right_Joining -> CANNOT send) -> no join in.
    ///                                       next = BEH (Dual_Joining, CAN receive) -> sends one.
    ///                                       => INITIAL form -> U+FEA3. (Starts a new connected cluster.)
    ///   i=3 BEH   (U+0628, Dual_Joining):   prev = HAH (Dual_Joining, CAN send) -> receives one.
    ///                                       next = ALEF (Right_Joining, CAN receive) -> sends one.
    ///                                       => MEDIAL form -> U+FE92.
    ///   i=4 ALEF  (U+0627, Right_Joining):  prev = BEH (Dual_Joining, CAN send) -> receives one.
    ///                                       no next char -> cannot send (moot: alef never sends anyway).
    ///                                       => FINAL form -> U+FE8E.
    /// So the LOGICAL-order shaped sequence is: [FEE3(c0), FEAE(c1), FEA3(c2), FE92(c3), FE8E(c4)].
    /// Single-run RTL (decision 8) reverses this to VISUAL order:
    ///                                        [FE8E(c4), FE92(c3), FEA3(c2), FEAE(c1), FEE3(c0)].
    /// This matches the standard "Arabic Presentation Forms-B" block layout (each dual-joining letter's
    /// 4 forms are consecutive codepoints in isolated/final/initial/medial order; right-joining-only
    /// letters have only 2). Independently cross-checked: every one of these 5 codepoints was confirmed
    /// present (with a real, positive PBF <c>advance</c>) by decoding the actual committed
    /// <c>65024-65279.pbf.bytes</c> fixture before this test was written (not derived FROM the test).
    ///
    /// ── Anti-tautology ──────────────────────────────────────────────────────────────────────────────
    /// The golden array below is a literal, hand-written from the derivation above — it is never
    /// computed by calling any production shaping code. A codepoint-passthrough (no joining/bidi) run
    /// is asserted to diverge from it (teeth, below). A separate assert (the R3 de-risk tooth) confirms
    /// every golden codepoint actually exists in the decoded presentation-form fixtures — a DIFFERENT
    /// failure mode than shape correctness (a wrong mapping could coincidentally land on an existing
    /// but WRONG codepoint, e.g. FEE1 "meem isolated" also exists in the fixture; only the hand
    /// derivation above guards against that).
    /// </summary>
    [TestFixture]
    public class TextShapingTests
    {
        // "marhaba" (Arabic for "hello") as explicit codepoint escapes -- avoids embedding a raw RTL
        // string in this (LTR) source file. meem (U+0645), reh (U+0631), hah (U+062D), beh (U+0628),
        // alef (U+0627), in that logical (typed) order.
        private const string Marhaba = "\u0645\u0631\u062D\u0628\u0627";

        // lam (U+0644) + alef (U+0627) -- exercises the mandatory lam-alef ligature (U+FEFB/FEFC)
        // separately from T1.
        private const string LamAlef = "\u0644\u0627";

        // The T1 golden: (AtlasCodepoint, Cluster) in VISUAL (post-bidi) order. See the class doc above
        // for the letter-by-letter derivation. Hand-written literal -- never computed from production code.
        private static readonly (uint codepoint, int cluster)[] MarhabaGoldenVisualOrder =
        {
            (0xFE8Eu, 4), // ALEF final
            (0xFE92u, 3), // BEH medial
            (0xFEA3u, 2), // HAH initial
            (0xFEAEu, 1), // REH final
            (0xFEE3u, 0), // MEEM initial
        };

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

        /// <summary>
        /// T1's <see cref="IGlyphMetricsProvider"/>, backed by the UNION of both decoded presentation-
        /// form fixture ranges (64256-64511 + 65024-65279) — so advances come from real committed data,
        /// and a joining bug that lands on a codepoint absent from both ranges fails loudly (advance 0,
        /// caught by the "positive advance" assert) rather than silently.
        /// </summary>
        private sealed class FixtureGlyphMetricsProvider : IGlyphMetricsProvider
        {
            private readonly Dictionary<uint, float> _advances;

            public FixtureGlyphMetricsProvider(IReadOnlyDictionary<uint, float> advances)
            {
                _advances = new Dictionary<uint, float>(advances);
            }

            public bool TryGetAdvance(uint codepoint, out float advance)
                => _advances.TryGetValue(codepoint, out advance);
        }

        private static (FixtureGlyphMetricsProvider metrics, HashSet<uint> allPresentationFormCodepoints) LoadPresentationFormFixtures()
        {
            FontStackGlyphs presentationFormsA = GlyphPbfDecoder.Decode(LoadFixture("64256-64511.pbf.bytes")).Stacks[0];
            FontStackGlyphs presentationFormsB = GlyphPbfDecoder.Decode(LoadFixture("65024-65279.pbf.bytes")).Stacks[0];

            var advances = new Dictionary<uint, float>();
            var codepoints = new HashSet<uint>();
            foreach (var kv in presentationFormsA.Glyphs) { advances[kv.Key] = kv.Value.Advance; codepoints.Add(kv.Key); }
            foreach (var kv in presentationFormsB.Glyphs) { advances[kv.Key] = kv.Value.Advance; codepoints.Add(kv.Key); }

            return (new FixtureGlyphMetricsProvider(advances), codepoints);
        }

        private static ShapedRun ShapeMarhaba(IGlyphMetricsProvider metrics)
        {
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest
            {
                Text = Marhaba,
                FontStack = new FontStack { Names = new[] { "Noto Sans Regular" } },
                Metrics = metrics,
            };
            return shaper.Shape(in request);
        }

        // =========================================================================================
        // T1 — THE decisive test: exact golden (AtlasCodepoint, Cluster) sequence in visual order.
        // =========================================================================================
        [Test]
        public void Shape_Marhaba_ProducesGoldenVisualOrderRun()
        {
            var (metrics, _) = LoadPresentationFormFixtures();
            ShapedRun run = ShapeMarhaba(metrics);

            Assert.AreEqual(TextDirection.RightToLeft, run.Direction, "pure Arabic text must resolve to RTL");
            Assert.AreEqual(MarhabaGoldenVisualOrder.Length, run.Glyphs.Count);

            for (int i = 0; i < MarhabaGoldenVisualOrder.Length; i++)
            {
                PositionedGlyph glyph = run.Glyphs[i];
                (uint codepoint, int cluster) expected = MarhabaGoldenVisualOrder[i];
                Assert.AreEqual(expected.codepoint, glyph.AtlasCodepoint,
                    $"visual position {i}: expected atlas codepoint U+{expected.codepoint:X4}, got U+{glyph.AtlasCodepoint:X4}");
                Assert.AreEqual(expected.cluster, glyph.Cluster, $"visual position {i}: cluster mismatch");
                Assert.Greater(glyph.XAdvance, 0f, $"visual position {i}: advance must be present and positive (real fixture data)");
            }
        }

        // =========================================================================================
        // Teeth: a codepoint-passthrough shortcut (no joining/bidi — nominal forms, logical order)
        // yields a DIFFERENT codepoint sequence AND order than the golden.
        // =========================================================================================
        [Test]
        public void Shape_Teeth_NominalLogicalOrderPassthroughDivergesFromGolden()
        {
            // The naive "codepoint == glyph id" shortcut MapLibre GL JS itself uses for non-complex
            // scripts: base codepoints, unjoined, in logical (typed) order -- no joining, no bidi.
            (uint codepoint, int cluster)[] passthrough =
            {
                (0x0645u, 0), // meem, nominal/base form
                (0x0631u, 1), // reh
                (0x062Du, 2), // hah
                (0x0628u, 3), // beh
                (0x0627u, 4), // alef
            };

            Assert.AreEqual(MarhabaGoldenVisualOrder.Length, passthrough.Length,
                "sanity: same glyph count -- divergence must be in codepoints/order, not count");

            bool anyDivergence = false;
            for (int i = 0; i < MarhabaGoldenVisualOrder.Length; i++)
            {
                if (passthrough[i].codepoint != MarhabaGoldenVisualOrder[i].codepoint
                    || passthrough[i].cluster != MarhabaGoldenVisualOrder[i].cluster)
                {
                    anyDivergence = true;
                    break;
                }
            }
            Assert.IsTrue(anyDivergence,
                "a codepoint-passthrough (no joining, no bidi) sequence must diverge from the golden " +
                "element-by-element -- if it didn't, the golden would be tautologically trivial");

            // The actual shaper must NOT equal the naive passthrough (this is what makes T1 decisive).
            var (metrics, _) = LoadPresentationFormFixtures();
            ShapedRun run = ShapeMarhaba(metrics);
            bool actualMatchesPassthrough = true;
            for (int i = 0; i < passthrough.Length; i++)
            {
                if (run.Glyphs[i].AtlasCodepoint != passthrough[i].codepoint || run.Glyphs[i].Cluster != passthrough[i].cluster)
                {
                    actualMatchesPassthrough = false;
                    break;
                }
            }
            Assert.IsFalse(actualMatchesPassthrough, "the real shaper output must NOT equal the naive passthrough");
        }

        // =========================================================================================
        // §6.1 R3 de-risk tooth: every shaped presentation-form AtlasCodepoint must actually exist in
        // the decoded committed presentation-form fixtures (64256-64511 UNION 65024-65279). Catches a
        // wrong joining mapping landing on a non-existent atlas cell -- a DIFFERENT failure mode than
        // T1's shape-correctness assert (see the class-doc anti-tautology note).
        // =========================================================================================
        [Test]
        public void Shape_Marhaba_AllAtlasCodepointsExistInPresentationFormFixtures()
        {
            var (metrics, presentCodepoints) = LoadPresentationFormFixtures();
            ShapedRun run = ShapeMarhaba(metrics);

            Assert.Greater(presentCodepoints.Count, 0, "fixture precondition: presentation-form fixtures must decode glyphs");
            foreach (PositionedGlyph glyph in run.Glyphs)
            {
                Assert.IsTrue(presentCodepoints.Contains(glyph.AtlasCodepoint),
                    $"shaped atlas codepoint U+{glyph.AtlasCodepoint:X4} does not exist in the decoded " +
                    "presentation-form fixtures (64256-64511 ∪ 65024-65279) -- the joining mapping is wrong");
            }
        }

        // =========================================================================================
        // Supplementary (non-T1): mandatory lam-alef ligature. "لا" (LAM+ALEF, no preceding letter)
        // must collapse to the single isolated ligature glyph U+FEFB, cluster 0 (the LAM's offset).
        // Independently confirmed present in the committed 65024-65279 fixture (see the class-doc
        // note) -- NOT independently golden-pinned like T1 (no external tool cross-check), so this is
        // a lower-confidence supplementary tooth, not a second decisive test.
        // =========================================================================================
        [Test]
        public void Shape_LamAlef_CollapsesToSingleIsolatedLigatureGlyph()
        {
            var (metrics, presentCodepoints) = LoadPresentationFormFixtures();
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = LamAlef, FontStack = null, Metrics = metrics };

            ShapedRun run = shaper.Shape(in request);

            Assert.AreEqual(1, run.Glyphs.Count, "LAM+ALEF must collapse into exactly one glyph");
            PositionedGlyph glyph = run.Glyphs[0];
            Assert.AreEqual(0xFEFBu, glyph.AtlasCodepoint, "isolated lam-alef ligature (nothing precedes it)");
            Assert.AreEqual(0, glyph.Cluster, "ligature reports the LAM's (first) source char offset");
            Assert.IsTrue(presentCodepoints.Contains(glyph.AtlasCodepoint), "ligature codepoint must exist in the fixtures");
        }

        // =========================================================================================
        // Decision 8: mixed strong-direction (RTL + LTR) input is a documented throw, not silently
        // wrong output -- full UAX #9 bidi is a deferred follow-up.
        // =========================================================================================
        [Test]
        public void Shape_MixedDirectionText_Throws()
        {
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest
            {
                Text = "abc" + Marhaba, // Latin + Arabic in the same run
                FontStack = null,
                Metrics = new FixtureGlyphMetricsProvider(new Dictionary<uint, float>()),
            };

            Assert.Throws<NotSupportedException>(() => shaper.Shape(in request));
        }

        // =========================================================================================
        // Sanity: pure Latin text takes the LTR passthrough path -- logical order == visual order,
        // codepoints unchanged (no Arabic joining applied to non-Arabic text).
        // =========================================================================================
        [Test]
        public void Shape_LatinText_PassesThroughInLogicalOrder()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            var advances = new Dictionary<uint, float>();
            foreach (var kv in latin.Glyphs) advances[kv.Key] = kv.Value.Advance;
            var metrics = new FixtureGlyphMetricsProvider(advances);

            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = "Ab", FontStack = null, Metrics = metrics };
            ShapedRun run = shaper.Shape(in request);

            Assert.AreEqual(TextDirection.LeftToRight, run.Direction);
            Assert.AreEqual(2, run.Glyphs.Count);
            Assert.AreEqual((uint)'A', run.Glyphs[0].AtlasCodepoint);
            Assert.AreEqual(0, run.Glyphs[0].Cluster);
            Assert.AreEqual((uint)'b', run.Glyphs[1].AtlasCodepoint);
            Assert.AreEqual(1, run.Glyphs[1].Cluster);
            Assert.Greater(run.Glyphs[0].XAdvance, 0f);
            Assert.Greater(run.Glyphs[1].XAdvance, 0f);
        }
    }
}
