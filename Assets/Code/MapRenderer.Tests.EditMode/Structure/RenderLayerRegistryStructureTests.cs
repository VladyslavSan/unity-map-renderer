// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// E1 D10 (design <c>docs/render-layer-unification.md</c> §2, tooth §6.7): <c>RenderLayerFactory</c> is
    /// the ONE registry mapping a <see cref="MapRenderer.Core.Style.StyleLayer"/> subtype to its runtime
    /// render layer. This is a grep guard, modeled on
    /// <see cref="MapRenderer.Tests.Text.Placement.LabelPlacementStructureTests"/>'s <c>AddTileLayer</c>
    /// guard: neither <c>MapView</c> nor <c>SymbolLabelSubsystem</c> may re-dispatch on a
    /// <c>Fill</c>/<c>Line</c>/<c>Symbol</c> <c>StyleLayer</c> subtype — they derive "which sources to
    /// fetch" / "which layers are mine" from the already-built <c>RenderLayerSet</c> instead (§1.6's three
    /// scattered switches, now killed to one).
    /// </summary>
    [TestFixture]
    public class RenderLayerRegistryStructureTests
    {
        // Matches a StyleLayer-subtype type-check ("is Fill.StyleLayer", "is MapRenderer.Core.Style.Symbol.StyleLayer",
        // "is not Line.StyleLayer") — a re-dispatch outside the one registry. Deliberately narrow (the CALL
        // form only) so prose in a doc comment discussing the constraint is not itself flagged.
        private static readonly Regex Offender =
            new Regex(@"is\s+(\w+\.)*\s*(Fill|Line|Symbol)\.StyleLayer", RegexOptions.Compiled);

        [Test]
        public void MapView_NeverTypeSwitchesOnAStyleLayerSubtype()
        {
            AssertNoOffendingPattern(Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Map", "MapView.cs"));
        }

        [Test]
        public void SymbolLabelSubsystem_NeverTypeSwitchesOnAStyleLayerSubtype()
        {
            AssertNoOffendingPattern(Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolLabelSubsystem.cs"));
        }

        private static void AssertNoOffendingPattern(string file)
        {
            Assert.IsTrue(File.Exists(file), $"expected source file to exist at {file}");
            string text = File.ReadAllText(file);
            var offenders = Offender.Matches(text);

            Assert.AreEqual(0, offenders.Count,
                $"'{Path.GetFileName(file)}' must derive its layer-kind decisions from the built " +
                "RenderLayerSet, not re-dispatch on a Fill/Line/Symbol.StyleLayer subtype " +
                "(RenderLayerFactory is the sole registry, D10). Offending text: " +
                (offenders.Count > 0 ? offenders[0].Value : string.Empty));
        }
    }
}
