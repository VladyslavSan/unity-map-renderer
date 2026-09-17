// Pure System.IO — no Unity API, no GPU. The text half of the style-layer zoom-bound read-set fence;
// the COMPILER is the other half (MinZoom/MaxZoom/Hidden are internal to MapRenderer.Core).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MapRenderer.Tests.Structure
{
    [TestFixture]
    public class StyleLayerZoomBoundReaderFenceTests
    {
        private const string Predicate = "IsVisibleAtZoom";

        /// <summary>
        /// Every production site that asks whether a style layer draws at a zoom, with its occurrence
        /// count after comments are stripped. Adding a row is meant to be a deliberate edit — see
        /// <see cref="StyleLayerZoomBounds_HaveOnlyTheNamedReaders"/> for the question a new row must answer.
        /// </summary>
        private static readonly Dictionary<string, int> ExpectedReaders = new Dictionary<string, int>
        {
            ["MapRenderer.Core/Style/StyleLayer.cs"]                      = 1, // the definition
            ["MapRenderer.Unity/Text/Placement/SymbolPlacementSystem.cs"] = 2,
            // Two reads, both off a carrier this arm refreshes. Build's fade seed reads the NEW document's
            // layer during a FULL build, which the in-place restyle arm never runs; AdvanceFade reads
            // StyleLayer off a LIVE render layer, which Restyle moves forward.
            ["MapRenderer.Unity/Rendering/Style/RenderLayerSet.cs"]       = 2,
            ["MapRenderer.Unity/Rendering/Style/FillRenderLayer.cs"]      = 1,
            ["MapRenderer.Unity/Rendering/Style/LineRenderLayer.cs"]      = 1,
            ["MapRenderer.Unity/Rendering/Style/FillExtrusionRenderLayer.cs"] = 1,
            ["MapRenderer.Unity/Rendering/Style/BackgroundRenderLayer.cs"]    = 1,
        };

        private static string CodeDir => Path.Combine(ShaderPropertyParser.RepoRoot, "Assets", "Code");

        /// <summary>
        /// The production assemblies, found by scanning rather than by a hard-coded list of names — a
        /// bounded count in this repo once said seven assemblies when the truth was eight.
        /// </summary>
        private static IEnumerable<string> ProductionAssemblyDirs()
            => Directory.EnumerateDirectories(CodeDir)
                .Where(d => !Path.GetFileName(d).Contains("Tests"))
                .Where(d => Path.GetFileName(d) != "ThirdParty");

        // Comments are stripped before matching: SymbolFeatureExtractor names the predicate in a
        // legitimate pointer comment, which is not a reader and must not red the fence.
        private static string StripComments(string text)
            => Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
                             @"//[^\n]*", string.Empty);

        private static Dictionary<string, int> ActualReaders()
        {
            var found = new Dictionary<string, int>();
            foreach (string dir in ProductionAssemblyDirs())
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                int n = Regex.Matches(StripComments(File.ReadAllText(file, Encoding.UTF8)), Predicate).Count;
                if (n == 0) continue;
                string rel = Path.GetRelativePath(CodeDir, file).Replace('\\', '/');
                found[rel] = n;
            }
            return found;
        }

        /// <summary>
        /// The predicate still exists under the name this fence matches. Without this clause a rename
        /// would make the forbidden-elsewhere half match nothing and pass silently — it would fail OPEN.
        /// </summary>
        [Test]
        public void StyleLayerVisibilityPredicate_StillCarriesTheFencedName()
        {
            string definition = Path.Combine(CodeDir, "MapRenderer.Core/Style/StyleLayer.cs");
            Assert.That(StripComments(File.ReadAllText(definition, Encoding.UTF8)), Does.Contain(Predicate),
                $"StyleLayer no longer declares '{Predicate}'. If it was renamed, rename it in this fence "
                + "too — this assertion exists so a rename cannot leave the fence matching nothing and "
                + "reading green.");
        }

        /// <summary>
        /// No production code outside the named set asks whether a style layer draws at a zoom.
        ///
        /// <para>Limits, stated because a fence that overstates itself is worse than none: this half is
        /// rename-RESISTANT, not rename-proof, and two holes are open. A new reader inside
        /// <c>MapRenderer.Core</c> is not caught — the compiler does not bite in the same assembly and
        /// <c>StyleLayer.cs</c> is already a named row, so a Core member re-exporting a bound leaves both
        /// halves green. Reflection by a computed name reaches the predicate with the token nowhere in
        /// source. Both are accepted and recorded, not closed.</para>
        /// </summary>
        [Test]
        public void StyleLayerZoomBounds_HaveOnlyTheNamedReaders()
        {
            CollectionAssert.AreEquivalent(ExpectedReaders, ActualReaders(),
                "A new reader of a style layer's zoom visibility is not automatically wrong, but it must "
                + "answer one question first: does it reach the layer through a carrier the IN-PLACE "
                + "restyle arm refreshes? MapView's in-place arm skips rebuilding _symbolStyleLayers / "
                + "_symbolRenderLayers and skips SymbolSubsystem.SetStyle, so SymbolSubsystem._allSymbolLayers "
                + "holds the PREVIOUS style's layer objects until the next full build. A bound read off that "
                + "list is stale on every in-place restyle and no behavioural test will notice. Read off a "
                + "live SymbolRenderLayer (which Restyle updates) it is fine. Answer that, then add your row "
                + "here with the answer.");
        }
    }
}
