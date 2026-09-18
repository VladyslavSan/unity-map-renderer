// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using Background = MapRenderer.Core.Style.Background;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// UMR-108 acceptance: the eight style property types (<c>Fill</c>/<c>Line</c>/<c>Symbol</c>/
    /// <c>Background</c>/<c>FillExtrusion</c> × <c>Paint</c>/<c>Layout</c>, where each exists) parse eagerly
    /// via a static <c>Parse</c> factory — not lazily from a retained raw JSON field.
    ///
    /// Tooth 1: <see cref="StyleParser"/> threads its <c>fillAntialiasDefault</c> parameter into the parse,
    /// rather than a hardcoded constant — pins behaviour preservation (passes against the pre-change code
    /// too; it is tooth 2 below that observes the laziness actually being removed).
    /// Tooth 2: reflection over the compiled types — the laziness cannot be reconstructed (see each clause).
    /// Tooth 4: <c>Parse(null)</c> is tolerated (returns all spec defaults, does not throw) by all eight
    /// types — the idiom ~60 test call sites and every production caller with an absent paint/layout block
    /// depend on.
    /// </summary>
    [TestFixture]
    public class StyleLayerEagerParseTests
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        // ── Tooth 1 — behaviour preservation through StyleParser (passes today too) ────────────────

        /// <summary>Builds a minimal style with exactly one fill layer, whose <c>paint</c> block is empty
        /// unless <paramref name="antialias"/> is supplied.</summary>
        /// <param name="antialias">When set, the layer's <c>fill-antialias</c> value; when null, the key
        /// is omitted entirely.</param>
        /// <returns>The style JSON string.</returns>
        private static string MinimalFillStyle(bool? antialias)
        {
            string paint = antialias.HasValue ? $"{{\"fill-antialias\":{(antialias.Value ? "true" : "false")}}}" : "{}";
            return "{\"version\":8,\"sources\":{\"s\":{\"type\":\"vector\",\"tiles\":[\"https://example.com/{z}/{x}/{y}.pbf\"]}}," +
                   "\"layers\":[{\"id\":\"f\",\"type\":\"fill\",\"source\":\"s\",\"source-layer\":\"l\",\"paint\":" + paint + "}]}";
        }

        [Test]
        public void StyleParser_FillAntialiasOmitted_HostDefaultFalse_ReachesTheParse()
        {
            var doc = StyleParser.Parse(MinimalFillStyle(antialias: null), fillAntialiasDefault: false);
            var fill = (Fill.StyleLayer)doc.Layers[0];
            Assert.IsFalse(fill.Paint.Antialias.Evaluate(0.0),
                "an omitted fill-antialias must fall back to the HOST default, not a hardcoded true.");
        }

        [Test]
        public void StyleParser_FillAntialiasOmitted_HostDefaultTrue_ReachesTheParse()
        {
            var doc = StyleParser.Parse(MinimalFillStyle(antialias: null), fillAntialiasDefault: true);
            var fill = (Fill.StyleLayer)doc.Layers[0];
            Assert.IsTrue(fill.Paint.Antialias.Evaluate(0.0),
                "the host default must be READ, not just present as an unused parameter.");
        }

        [Test]
        public void StyleParser_FillAntialiasExplicit_WinsOverTheHostDefault()
        {
            var doc = StyleParser.Parse(MinimalFillStyle(antialias: true), fillAntialiasDefault: false);
            var fill = (Fill.StyleLayer)doc.Layers[0];
            Assert.IsTrue(fill.Paint.Antialias.Evaluate(0.0),
                "an explicit layer value must win over the host default, whichever it is.");
        }

        // ── Tooth 2 — the laziness cannot come back (reflection, never text) ───────────────────────

        /// <summary>True when <paramref name="property"/>'s setter carries the <c>IsExternalInit</c>
        /// required custom modifier — the compiler's marker for an <c>init</c> accessor. Tolerates a
        /// missing setter (<c>SetMethod == null</c>) by returning false rather than throwing.
        ///
        /// <para>Compared by <c>FullName</c>, never <c>== typeof(IsExternalInit)</c>: Core carries its own
        /// <c>internal static class IsExternalInit</c> polyfill (<c>Core/IsExternalInit.cs</c>), duplicated
        /// per assembly, and this TEST assembly has none of its own — a type-identity comparison would read
        /// false even for a genuinely init-only property, making the tooth a permanent false RED.</para>
        /// </summary>
        private static bool IsInitOnly(PropertyInfo property)
        {
            if (property.SetMethod == null) return false;
            return property.SetMethod.ReturnParameter.GetRequiredCustomModifiers()
                .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }

        /// <summary>Every typed <c>StyleLayer</c> subclass's <c>Paint</c>/<c>Layout</c> properties (only
        /// the ones the type actually declares — <c>Background</c>/<c>FillExtrusion</c> have no Layout).</summary>
        private static readonly (Type Layer, string Property)[] TypedLayerViews =
        {
            (typeof(Fill.StyleLayer), "Paint"),
            (typeof(Fill.StyleLayer), "Layout"),
            (typeof(Line.StyleLayer), "Paint"),
            (typeof(Line.StyleLayer), "Layout"),
            (typeof(SymbolStyle.StyleLayer), "Paint"),
            (typeof(SymbolStyle.StyleLayer), "Layout"),
            (typeof(Background.StyleLayer), "Paint"),
            (typeof(FillExtrusion.StyleLayer), "Paint"),
        };

        /// <summary>Clause A — non-vacuity + init-only: every typed layer's Paint/Layout property exists
        /// and its setter carries the <c>init</c> modreq. The discriminator is the required custom modifier,
        /// not merely <c>SetMethod != null</c> — <c>{ get => _paint ??= …; set => _paint = value; }</c> would
        /// pass a bare non-null check while restoring the laziness in full.</summary>
        [Test]
        public void TypedLayerViews_ArePresentAndInitOnly()
        {
            foreach ((Type layer, string propertyName) in TypedLayerViews)
            {
                PropertyInfo property = layer.GetProperty(propertyName, PublicInstance);
                Assert.IsNotNull(property, $"{layer.Name}.{propertyName} must exist.");
                Assert.IsTrue(IsInitOnly(property),
                    $"{layer.Name}.{propertyName} must be init-only (carry the IsExternalInit modreq).");
            }
        }

        /// <summary>Clause B — no public writable property re-creating the deleted
        /// <c>Fill.StyleLayer.AntialiasDefault</c> laziness knob.</summary>
        [Test]
        public void FillStyleLayer_AntialiasDefault_IsGone()
        {
            PropertyInfo property = typeof(Fill.StyleLayer).GetProperty("AntialiasDefault", PublicInstance);
            Assert.IsNull(property,
                "Fill.StyleLayer must not carry a public writable AntialiasDefault (or anything reviving it).");
        }

        /// <summary>All eight property types, for clause C.</summary>
        private static readonly Type[] PropertyTypes =
        {
            typeof(Fill.PaintProperties), typeof(Fill.LayoutProperties),
            typeof(Line.PaintProperties), typeof(Line.LayoutProperties),
            typeof(SymbolStyle.PaintProperties), typeof(SymbolStyle.LayoutProperties),
            typeof(Background.PaintProperties),
            typeof(FillExtrusion.PaintProperties),
        };

        /// <summary>Clause C — no public constructor on any of the eight property types; <c>Parse</c> is
        /// the only way in.</summary>
        [Test]
        public void PropertyTypes_HaveNoPublicConstructor()
        {
            foreach (Type type in PropertyTypes)
                Assert.AreEqual(0, type.GetConstructors().Length,
                    $"{type.FullName} must have zero public constructors — Parse is the only way in.");
        }

        // ── Tooth 4 — Parse(null) is tolerated by all eight types ───────────────────────────────────

        /// <summary><c>Parse(null)</c> returning all-spec-defaults (never throwing) is the idiom ~60 test
        /// call sites and every production caller with an absent paint/layout block rely on. One straight-line
        /// test covering all eight types, not eight separate ones.</summary>
        [Test]
        public void ParseNull_ReturnsNonNull_ForAllEightTypes()
        {
            Assert.IsNotNull(Fill.PaintProperties.Parse(null));
            Assert.IsNotNull(Fill.LayoutProperties.Parse(null));
            Assert.IsNotNull(Line.PaintProperties.Parse(null));
            Assert.IsNotNull(Line.LayoutProperties.Parse(null));
            Assert.IsNotNull(SymbolStyle.PaintProperties.Parse(null));
            Assert.IsNotNull(SymbolStyle.LayoutProperties.Parse(null));
            Assert.IsNotNull(Background.PaintProperties.Parse(null));
            Assert.IsNotNull(FillExtrusion.PaintProperties.Parse(null));
        }

        // ── layout.visibility parses into the one draw-gate predicate ──────────────────────────

        /// <summary>
        /// <c>StyleParser.ParseLayer</c> sets <c>Visible</c> to <c>false</c> only for
        /// <c>visibility: "none"</c>. Any other value — including an absent layout, an absent key, and a
        /// garbage string — is <c>true</c>, which the spec's default gives for free.
        /// </summary>
        /// <param name="layoutJson">The layer's <c>layout</c> block, or <c>null</c> to omit it entirely.</param>
        [TestCase("{\"visibility\":\"none\"}",    false, TestName = "Visibility_None_Hides")]
        [TestCase("{\"visibility\":\"visible\"}", true,  TestName = "Visibility_Visible_Draws")]
        [TestCase("{}",                            true,  TestName = "Visibility_AbsentKey_Draws")]
        [TestCase("{\"visibility\":\"NONE\"}",    true,  TestName = "Visibility_WrongCase_Draws")]
        [TestCase(null,                            true,  TestName = "Visibility_NoLayoutBlock_Draws")]
        public void ParseLayer_Visibility_SetsVisible(string layoutJson, bool expected)
        {
            string layout = layoutJson == null ? "" : $"\"layout\": {layoutJson},";
            StyleDocument doc = StyleParser.Parse($@"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [ {{ ""id"": ""f0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""f0"", {layout}
        ""paint"": {{ ""fill-color"": [""rgba"",102,153,204,1] }} }} ]
}}");
            Assert.AreEqual(expected, doc.Layers[0].Visible,
                $"layout {layoutJson ?? "(absent)"} must parse to Visible={expected}. Only the exact "
                + "string \"none\" hides a layer; every other value is the spec default \"visible\".");
        }

        /// <summary>
        /// A hidden layer is invisible even at a zoom strictly INSIDE its declared range — the row an
        /// implementation that ANDs the flag in the wrong place fails.
        /// </summary>
        [Test]
        public void HiddenLayer_IsNotVisible_EvenInsideItsZoomRange()
        {
            var layer = new StyleLayer { Id = "x", MinZoom = 5.0, MaxZoom = 20.0 };
            Assert.IsTrue(layer.IsVisibleAtZoom(10.0), "precondition: z10 is inside [5,20), so it draws.");

            layer.Visible = false;
            Assert.IsFalse(layer.IsVisibleAtZoom(10.0),
                "visibility:none must hide the layer at EVERY zoom, including one inside its declared "
                + "range. A flag consulted only outside the bounds passes the out-of-range rows and fails "
                + "exactly here.");
        }
    }
}
