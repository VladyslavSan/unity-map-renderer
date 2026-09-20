// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// I3: <see cref="SymbolStyle.IconImageResolver.Resolve"/> — token sugar (<c>{prop}</c>) AND expression
    /// form (<c>["get",…]</c>/<c>["coalesce",…]</c>) resolve to the exact sprite name; a missing property
    /// SKIPS the icon (returns null), never an empty name. Mirrors <c>TextFieldResolverTests</c> for the
    /// icon-image analogue. Engine-free; runs in both runners.
    /// </summary>
    [TestFixture]
    public class IconImageResolverTests
    {
        // Author field JSON with single quotes, then swap to real quotes.
        private static JsonValue Field(string json) => JsonParser.Parse(json.Replace('\'', '"'));

        private static IFeature Feature(params (string key, string val)[] props)
        {
            var dict = new Dictionary<string, Value>();
            foreach (var (key, val) in props) dict[key] = Value.String(val);
            return new DictionaryFeature(dict, TileGeometryType.Point);
        }

        private static readonly IFeature Marker = Feature(("icon", "marker"));
        private static readonly IFeature Star = Feature(("icon", "star"), ("kind", "poi"));

        [Test]
        public void Token_SingleProperty_Resolves()
        {
            Assert.AreEqual("marker", SymbolStyle.IconImageResolver.Resolve(Field("'{icon}'"), Marker));
        }

        [Test]
        public void Expression_Get_Resolves()
        {
            Assert.AreEqual("marker", SymbolStyle.IconImageResolver.Resolve(Field("['get','icon']"), Marker));
        }

        [Test]
        public void Expression_CoalesceFallback_Resolves()
        {
            // icon:2x absent → coalesce falls back to icon.
            Assert.AreEqual("star",
                SymbolStyle.IconImageResolver.Resolve(Field("['coalesce',['get','icon:2x'],['get','icon']]"), Star));
        }

        [Test]
        public void UnknownToken_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(Field("'{missing}'"), Marker),
                "an unknown token resolving to empty text must SKIP the icon (null), not emit an empty name");
        }

        [Test]
        public void MissingProperty_Expression_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(Field("['get','missing']"), Marker),
                "a get on a missing property must SKIP the icon (null), not emit an empty name");
        }

        [Test]
        public void LiteralNoTokens_PassesThrough()
        {
            Assert.AreEqual("pin", SymbolStyle.IconImageResolver.Resolve(Field("'pin'"), Marker));
        }

        [Test]
        public void AbsentField_Skips()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(null, Marker));
        }

        [Test]
        public void EmptyLiteral_Skips()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(Field("''"), Marker),
                "an empty/whitespace resolution must skip, mirroring TextFieldResolver");
        }
    }
}
