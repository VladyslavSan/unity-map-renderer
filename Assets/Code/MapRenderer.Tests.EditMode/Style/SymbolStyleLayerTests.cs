// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S105 Slice 1 (A1): a <c>symbol</c> layer parses to the typed <see cref="SymbolStyle.StyleLayer"/> (NOT the
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
            Assert.IsInstanceOf<SymbolStyle.StyleLayer>(layer,
                "a 'symbol' layer must parse to the typed Symbol.StyleLayer, not the generic base");
            var sym = (SymbolStyle.StyleLayer)layer;

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
            var sym = (SymbolStyle.StyleLayer)doc.Layers[1];

            // Layout defaults.
            Assert.AreEqual(16f, sym.Layout.TextSize.Evaluate(0.0), 1e-6, "text-size default is 16");
            Assert.AreEqual(2f, sym.Layout.TextPadding.Evaluate(0.0), 1e-6,
                "text-padding default is 2 (the resolver applies the spec default; LabelInstance's carrier default is 0)");
            Assert.IsFalse(sym.Layout.TextAllowOverlap, "text-allow-overlap default is false");
            Assert.IsFalse(sym.Layout.TextIgnorePlacement, "text-ignore-placement default is false");
            Assert.AreEqual(SymbolStyle.PropertyNames.PlacementPoint, sym.Layout.SymbolPlacement, "symbol-placement default is point");

            // Paint defaults.
            Assert.AreEqual(0f, sym.Paint.HaloWidth.Evaluate(0.0), 1e-6, "text-halo-width default is 0");
            Assert.AreEqual(new Color(0, 0, 0, 1), sym.Paint.Color.Evaluate(0.0), "text-color default is opaque black");
            Assert.AreEqual(1f, sym.Paint.Opacity.Evaluate(0.0), 1e-6, "text-opacity default is 1");
            Assert.AreEqual(new float2(0, 0), sym.Paint.Translate, "text-translate default is [0,0]");
            Assert.AreEqual(TextTranslateAnchor.Map, sym.Paint.TranslateAnchor, "text-translate-anchor default is map");
            Assert.IsTrue(sym.Paint.IsInertFallback, "an empty paint sub-tree is an inert fallback");

            // Slice A layout defaults.
            Assert.AreEqual(TextAnchor.Center, sym.Layout.TextAnchor, "text-anchor default is center");
            Assert.AreEqual(TextJustify.Center, sym.Layout.TextJustify,
                "text-justify default is the spec 'center' — NOT the enum zero-value Auto");
            Assert.AreEqual(float2.zero, sym.Layout.TextOffset, "text-offset default is [0,0]");
            Assert.AreEqual(1.2f, sym.Layout.TextLineHeight.Evaluate(0.0), 1e-6, "text-line-height default is 1.2");
            Assert.AreEqual(0f, sym.Layout.TextLetterSpacing.Evaluate(0.0), 1e-6, "text-letter-spacing default is 0");
            Assert.AreEqual(0f, sym.Layout.TextRadialOffset.Evaluate(0.0), 1e-6, "text-radial-offset default is 0");
            Assert.AreEqual(TextTransform.None, sym.Layout.TextTransform, "text-transform default is none");
            Assert.AreEqual(AlignmentMode.Auto, sym.Layout.TextRotationAlignment, "text-rotation-alignment default is auto");
            Assert.AreEqual(AlignmentMode.Auto, sym.Layout.TextPitchAlignment, "text-pitch-alignment default is auto");
        }

        [Test]
        public void SymbolLayer_Alignment_ParsesMapViewportAndDegradesToAuto()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-rotation-alignment':'map','text-pitch-alignment':'viewport' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Map, sym.Layout.TextRotationAlignment);
            Assert.AreEqual(AlignmentMode.Viewport, sym.Layout.TextPitchAlignment);

            var bad = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-rotation-alignment':'sideways' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Auto, bad.Layout.TextRotationAlignment, "an unrecognized alignment degrades to auto");
        }

        [Test]
        public void SymbolLayer_LayoutOptions_ParseEnumsAndScalars()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-anchor':'top-left','text-justify':'right',
                    'text-offset':[1,2],'text-line-height':1.5,'text-letter-spacing':0.1,'text-radial-offset':0.5,
                    'text-transform':'uppercase' } } ] }").Layers[0];

            Assert.AreEqual(TextAnchor.TopLeft, sym.Layout.TextAnchor, "hyphenated 'top-left' → TopLeft");
            Assert.AreEqual(TextJustify.Right, sym.Layout.TextJustify);
            Assert.AreEqual(TextTransform.Uppercase, sym.Layout.TextTransform);
            // text-offset is retained RAW (y-down, un-flipped) at parse; the y-flip happens in the builder.
            Assert.AreEqual(new float2(1f, 2f), sym.Layout.TextOffset);
            Assert.AreEqual(1.5f, sym.Layout.TextLineHeight.Evaluate(0.0), 1e-6);
            Assert.AreEqual(0.1f, sym.Layout.TextLetterSpacing.Evaluate(0.0), 1e-6);
            Assert.AreEqual(0.5f, sym.Layout.TextRadialOffset.Evaluate(0.0), 1e-6);
        }

        [Test]
        public void SymbolLayer_UnrecognizedAnchorJustify_DegradeToSpecDefault()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-anchor':'nonsense','text-justify':'sideways',
                    'text-transform':'italic' } } ] }").Layers[0];

            Assert.AreEqual(TextAnchor.Center, sym.Layout.TextAnchor, "an unrecognized text-anchor degrades to center");
            Assert.AreEqual(TextJustify.Center, sym.Layout.TextJustify, "an unrecognized text-justify degrades to center");
            Assert.AreEqual(TextTransform.None, sym.Layout.TextTransform, "an unrecognized text-transform degrades to none");
        }

        [Test]
        public void SymbolLayer_TextTranslate_ParsesPixelsAndAnchor()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','paint':{
                    'text-translate':[4,6],'text-translate-anchor':'viewport' } } ] }").Layers[0];

            Assert.AreEqual(new float2(4, 6), sym.Paint.Translate, "text-translate parses to [x,y] px (raw y-down)");
            Assert.AreEqual(TextTranslateAnchor.Viewport, sym.Paint.TranslateAnchor);
        }
    }
}
