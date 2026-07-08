// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;
using Sym = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S105 Slice 1 (A1): a <c>symbol</c> layer parses to the typed <see cref="Sym.StyleLayer"/> (NOT the
    /// generic base) with its <c>text-*</c>/<c>symbol-*</c> paint+layout values — and an EMPTY symbol layer
    /// yields every MapLibre spec DEFAULT. Engine-free; runs in both runners.
    /// </summary>
    [TestFixture]
    public class SymbolStyleLayerTests
    {
        // Author JSON with single quotes for readability, then swap to real quotes.
        private static StyleDocument Parse(string json) => StyleParser.Parse(json.Replace('\'', '"'));

        private const string StyleJson = @"{
            'version': 8,
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'src', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':24, 'symbol-sort-key':3,
                              'text-allow-overlap':true, 'text-padding':5 },
                  'paint':  { 'text-color':'#ff0000', 'text-halo-color':'#ffffff', 'text-halo-width':1.5 } },
                { 'id':'bare', 'type':'symbol', 'source':'src', 'source-layer':'centroids' }
            ]
        }";

        [Test]
        public void SymbolLayer_ParsesToTypedSubclass_WithExpectedValues()
        {
            StyleDocument doc = Parse(StyleJson);
            Assert.AreEqual(2, doc.Layers.Count);

            StyleLayer layer = doc.Layers[0];
            Assert.IsInstanceOf<Sym.StyleLayer>(layer,
                "a 'symbol' layer must parse to the typed Symbol.StyleLayer, not the generic base");
            var sym = (Sym.StyleLayer)layer;

            // text-field kept as raw JSON (resolved per-feature, not a scalar).
            Assert.IsNotNull(sym.Layout.TextField, "text-field must be retained (raw) for per-feature resolution");
            Assert.AreEqual("{NAME}", sym.Layout.TextField.AsString(null));

            // Layout scalars.
            Assert.AreEqual(24f, sym.Layout.TextSize.Evaluate(0.0), 1e-6);
            Assert.AreEqual(3f, sym.Layout.SymbolSortKey.Evaluate(0.0), 1e-6);
            Assert.AreEqual(5f, sym.Layout.TextPadding.Evaluate(0.0), 1e-6);
            Assert.IsTrue(sym.Layout.TextAllowOverlap, "text-allow-overlap:true must parse to true");

            // Paint.
            Assert.AreEqual(new Color(1, 0, 0, 1), sym.Paint.Color.Evaluate(0.0), "text-color #ff0000 → opaque red");
            Assert.AreEqual(new Color(1, 1, 1, 1), sym.Paint.HaloColor.Evaluate(0.0), "text-halo-color #ffffff → opaque white");
            Assert.AreEqual(1.5f, sym.Paint.HaloWidth.Evaluate(0.0), 1e-6);
        }

        [Test]
        public void SymbolLayer_EmptyLayoutAndPaint_YieldsSpecDefaults()
        {
            StyleDocument doc = Parse(StyleJson);
            var sym = (Sym.StyleLayer)doc.Layers[1];

            // Layout defaults.
            Assert.AreEqual(16f, sym.Layout.TextSize.Evaluate(0.0), 1e-6, "text-size default is 16");
            Assert.AreEqual(2f, sym.Layout.TextPadding.Evaluate(0.0), 1e-6,
                "text-padding default is 2 (the resolver applies the spec default; LabelInstance's carrier default is 0)");
            Assert.IsFalse(sym.Layout.TextAllowOverlap, "text-allow-overlap default is false");
            Assert.IsFalse(sym.Layout.TextIgnorePlacement, "text-ignore-placement default is false");
            Assert.AreEqual(Sym.PropertyNames.PlacementPoint, sym.Layout.SymbolPlacement, "symbol-placement default is point");

            // Paint defaults.
            Assert.AreEqual(0f, sym.Paint.HaloWidth.Evaluate(0.0), 1e-6, "text-halo-width default is 0");
            Assert.AreEqual(new Color(0, 0, 0, 1), sym.Paint.Color.Evaluate(0.0), "text-color default is opaque black");
            Assert.AreEqual(1f, sym.Paint.Opacity.Evaluate(0.0), 1e-6, "text-opacity default is 1");
            Assert.IsTrue(sym.Paint.IsInertFallback, "an empty paint sub-tree is an inert fallback");
        }
    }
}
