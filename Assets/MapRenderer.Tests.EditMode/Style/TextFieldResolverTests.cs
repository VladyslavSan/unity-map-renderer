// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Mvt;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S105 Slice 1 (A2): <see cref="SymbolStyle.TextFieldResolver.Resolve"/> — token sugar (<c>{prop}</c>) AND
    /// expression form (<c>["get",…]</c>/<c>["coalesce",…]</c>) resolve to the exact label string; a missing
    /// property SKIPS the feature (returns null), never a blank label. Engine-free; runs in both runners.
    /// </summary>
    [TestFixture]
    public class TextFieldResolverTests
    {
        // Author field JSON with single quotes, then swap to real quotes.
        private static JsonValue Field(string json) => JsonParser.Parse(json.Replace('\'', '"'));

        private static IFeature Feature(params (string key, string val)[] props)
        {
            var dict = new Dictionary<string, Value>();
            foreach (var (key, val) in props) dict[key] = Value.String(val);
            return new DictionaryFeature(dict, MvtGeometryType.Point);
        }

        private static readonly IFeature Aruba = Feature(("NAME", "Aruba"));
        private static readonly IFeature Afghanistan = Feature(("NAME", "Afghanistan"), ("ABBREV", "Afg."));

        [Test]
        public void Token_SingleProperty_Resolves()
        {
            Assert.AreEqual("Aruba", SymbolStyle.TextFieldResolver.Resolve(Field("'{NAME}'"), Aruba));
        }

        [Test]
        public void Expression_Get_Resolves()
        {
            Assert.AreEqual("Aruba", SymbolStyle.TextFieldResolver.Resolve(Field("['get','NAME']"), Aruba));
        }

        [Test]
        public void Token_MultiTokenWithLiterals_Resolves()
        {
            Assert.AreEqual("Afghanistan (Afg.)",
                SymbolStyle.TextFieldResolver.Resolve(Field("'{NAME} ({ABBREV})'"), Afghanistan));
        }

        [Test]
        public void Expression_CoalesceFallback_Resolves()
        {
            // name:en absent → coalesce falls back to NAME.
            Assert.AreEqual("Aruba",
                SymbolStyle.TextFieldResolver.Resolve(Field("['coalesce',['get','name:en'],['get','NAME']]"), Aruba));
        }

        [Test]
        public void MissingProperty_Token_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.TextFieldResolver.Resolve(Field("'{missing}'"), Aruba),
                "an unknown token resolving to empty text must SKIP the feature (null), not emit a blank label");
        }

        [Test]
        public void MissingProperty_Expression_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.TextFieldResolver.Resolve(Field("['get','missing']"), Aruba),
                "a get on a missing property must SKIP the feature (null), not emit a blank label");
        }

        [Test]
        public void LiteralNoTokens_PassesThrough()
        {
            Assert.AreEqual("Airport", SymbolStyle.TextFieldResolver.Resolve(Field("'Airport'"), Aruba));
        }

        [Test]
        public void NullField_Skips()
        {
            Assert.IsNull(SymbolStyle.TextFieldResolver.Resolve(null, Aruba));
        }
    }
}
