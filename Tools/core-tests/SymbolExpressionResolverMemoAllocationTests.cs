// C2 — TextFieldResolver/IconImageResolver used to re-run ExpressionParser.Parse on the SAME expression
// JSON node on every call: once per selected feature, per symbol layer, per tile
// (SymbolFeatureExtractor.Extract's selection loop). Both resolvers now memoize the parsed Expression per
// JSON node via a ConditionalWeakTable, mirroring FeatureSelector.FilterFor's compiled-filter memo. This
// tooth pins the fix with a real byte-delta meter: `GC.GetAllocatedBytesForCurrentThread()` is DEAD in
// Unity's EditMode Mono runner (reads 0 for everything there) but real under `dotnet test
// Tools/core-tests`'s CoreCLR runner — so this tooth lives ONLY here, not mirrored into the EditMode
// assembly (a copy there would be vacuous, not merely redundant).

using System;
using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Resolving MANY additional features against the SAME expression-form <c>text-field</c>/
    /// <c>icon-image</c> node must allocate roughly the same per-call bytes as the token-string form (which
    /// never touches <see cref="MapRenderer.Core.Expressions.ExpressionParser"/> at all) — not a growing
    /// multiple of it, which is what a per-call re-parse would produce.
    /// </summary>
    [TestFixture]
    public class SymbolExpressionResolverMemoAllocationTests
    {
        private static JsonValue Field(string json) => JsonParser.Parse(json.Replace('\'', '"'));

        private static IFeature Feature(params (string key, string val)[] props)
        {
            var dict = new Dictionary<string, Value>();
            foreach (var (key, val) in props) dict[key] = Value.String(val);
            return new DictionaryFeature(dict, TileGeometryType.Point);
        }

        // Large enough that a per-call parse (which allocates a parser + scope + dict + node objects, on
        // the order of a few hundred bytes for this fixture) dominates any residual per-call floor.
        private const int Iterations = 1000;

        [Test]
        public void TextFieldResolver_ExpressionForm_MemoizesParseAcrossFeatures()
        {
            JsonValue expr = Field("['coalesce',['get','name:en'],['get','NAME']]");
            IFeature aruba = Feature(("NAME", "Aruba"));
            Assert.AreEqual("Aruba", SymbolStyle.TextFieldResolver.Resolve(expr, aruba), "fixture sanity");

            JsonValue token = Field("'{NAME}'");
            Assert.AreEqual("Aruba", SymbolStyle.TextFieldResolver.Resolve(token, aruba), "fixture sanity");

            double perCallToken = MeanAllocatedBytes(() => SymbolStyle.TextFieldResolver.Resolve(token, aruba));
            double perCallExpression = MeanAllocatedBytes(() => SymbolStyle.TextFieldResolver.Resolve(expr, aruba));

            Assert.Less(perCallExpression, perCallToken + 96,
                $"a memoized expression-form text-field must cost about the same per additional feature as " +
                $"the never-parsed token form (token={perCallToken:F1} B/call, expression={perCallExpression:F1} " +
                "B/call) — a per-call re-parse would scale with the expression tree's node count instead.");
        }

        [Test]
        public void IconImageResolver_ExpressionForm_MemoizesParseAcrossFeatures()
        {
            JsonValue expr = Field("['coalesce',['get','icon:2'],['get','icon']]");
            IFeature feature = Feature(("icon", "airport"));
            Assert.AreEqual("airport", SymbolStyle.IconImageResolver.Resolve(expr, feature), "fixture sanity");

            JsonValue token = Field("'{icon}'");
            Assert.AreEqual("airport", SymbolStyle.IconImageResolver.Resolve(token, feature), "fixture sanity");

            double perCallToken = MeanAllocatedBytes(() => SymbolStyle.IconImageResolver.Resolve(token, feature));
            double perCallExpression = MeanAllocatedBytes(() => SymbolStyle.IconImageResolver.Resolve(expr, feature));

            Assert.Less(perCallExpression, perCallToken + 96,
                $"a memoized expression-form icon-image must cost about the same per additional feature as " +
                $"the never-parsed token form (token={perCallToken:F1} B/call, expression={perCallExpression:F1} " +
                "B/call) — a per-call re-parse would scale with the expression tree's node count instead.");
        }

        // Mean managed bytes allocated per call to `action`, over Iterations calls. Warms the exact
        // delegate first so the measured window excludes one-time JIT.
        private static double MeanAllocatedBytes(Action action)
        {
            for (int w = 0; w < 10; w++) action();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++) action();
            long after = GC.GetAllocatedBytesForCurrentThread();
            return (after - before) / (double)Iterations;
        }
    }
}
