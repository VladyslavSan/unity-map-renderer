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
                              'symbol-spacing':180, 'text-max-angle':30, 'text-keep-upright':false,
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
            Assert.AreEqual(180f, sym.Layout.SymbolSpacing.Evaluate(0.0), 1e-6, "symbol-spacing parses");
            Assert.AreEqual(30f, sym.Layout.TextMaxAngle.Evaluate(0.0), 1e-6, "text-max-angle parses");
            Assert.IsFalse(sym.Layout.TextKeepUpright, "text-keep-upright:false parses");
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
            Assert.AreEqual(SymbolPlacement.Point, sym.Layout.SymbolPlacement.Evaluate(0.0), "symbol-placement default is point");
            Assert.AreEqual(250f, sym.Layout.SymbolSpacing.Evaluate(0.0), 1e-6, "symbol-spacing default is 250");
            Assert.AreEqual(45f, sym.Layout.TextMaxAngle.Evaluate(0.0), 1e-6, "text-max-angle default is 45");
            Assert.IsTrue(sym.Layout.TextKeepUpright, "text-keep-upright default is true");

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

            // icon-* layout defaults.
            Assert.IsNull(sym.Layout.IconImage, "icon-image default is absent (null)");
            Assert.AreEqual(1f, sym.Layout.IconSize.Evaluate(0.0), 1e-6, "icon-size default is 1");
            Assert.AreEqual(2f, sym.Layout.IconPadding.Evaluate(0.0), 1e-6, "icon-padding default is 2");
            Assert.AreEqual(TextAnchor.Center, sym.Layout.IconAnchor, "icon-anchor default is center");
            Assert.AreEqual(AlignmentMode.Auto, sym.Layout.IconRotationAlignment, "icon-rotation-alignment default is auto");
            Assert.IsFalse(sym.Layout.IconAllowOverlap, "icon-allow-overlap default is false");
            Assert.IsFalse(sym.Layout.IconIgnorePlacement, "icon-ignore-placement default is false");
            Assert.AreEqual(float2.zero, sym.Layout.IconOffset, "icon-offset default is [0,0]");

            // icon-opacity paint default.
            Assert.AreEqual(1f, sym.Paint.IconOpacity.Evaluate(0.0), 1e-6, "icon-opacity default is 1");
        }

        [Test]
        public void SymbolLayer_IconProperties_Parse()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-image':['get','icon'],'icon-size':2,'icon-offset':[3,4],'icon-anchor':'top-left',
                    'icon-rotation-alignment':'map','icon-allow-overlap':true,'icon-ignore-placement':true,
                    'icon-padding':5 },
                  'paint':{ 'icon-opacity':0.5 } } ] }").Layers[0];

            Assert.IsNotNull(sym.Layout.IconImage, "icon-image must be retained (raw) for per-feature resolution");
            Assert.IsTrue(sym.Layout.IconImage.IsArray, "icon-image data-driven expression survives as a raw array (resolution is I3)");
            Assert.AreEqual(2f, sym.Layout.IconSize.Evaluate(0.0), 1e-6);
            Assert.AreEqual(5f, sym.Layout.IconPadding.Evaluate(0.0), 1e-6);
            Assert.AreEqual(new float2(3, 4), sym.Layout.IconOffset);
            Assert.AreEqual(TextAnchor.TopLeft, sym.Layout.IconAnchor, "hyphenated 'top-left' → TopLeft");
            Assert.AreEqual(AlignmentMode.Map, sym.Layout.IconRotationAlignment);
            Assert.IsTrue(sym.Layout.IconAllowOverlap, "icon-allow-overlap:true must parse to true");
            Assert.IsTrue(sym.Layout.IconIgnorePlacement, "icon-ignore-placement:true must parse to true");

            Assert.AreEqual(0.5f, sym.Paint.IconOpacity.Evaluate(0.0), 1e-6);
            Assert.IsFalse(sym.Paint.IsInertFallback, "an icon-opacity-only paint sub-tree is NOT an inert fallback");
        }

        // ── C1 (stage C) — icon-optional / text-optional parse as plain layout booleans ────────────────────
        [Test]
        public void SymbolLayer_IconAndTextOptional_ParseWithSpecDefaultFalse()
        {
            var absent = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{ 'text-field':'{NAME}' } } ] }").Layers[0];
            Assert.IsFalse(absent.Layout.IconOptional, "icon-optional default is false (spec)");
            Assert.IsFalse(absent.Layout.TextOptional, "text-optional default is false (spec)");

            var set = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','icon-optional':true,'text-optional':true } } ] }").Layers[0];
            Assert.IsTrue(set.Layout.IconOptional, "icon-optional:true must parse to true");
            Assert.IsTrue(set.Layout.TextOptional, "text-optional:true must parse to true");

            // Independent, not one flag read twice: liberty's airport sets only text-optional, and its four
            // label_* layers set only icon-optional.
            var textOnly = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-optional':true } } ] }").Layers[0];
            Assert.IsFalse(textOnly.Layout.IconOptional, "text-optional must not set icon-optional");
            Assert.IsTrue(textOnly.Layout.TextOptional);

            // Malformed (a string, not a bool) degrades to the spec default rather than throwing.
            var malformed = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','icon-optional':'yes','text-optional':7 } } ] }").Layers[0];
            Assert.IsFalse(malformed.Layout.IconOptional, "a malformed icon-optional degrades to false");
            Assert.IsFalse(malformed.Layout.TextOptional, "a malformed text-optional degrades to false");
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

        // ── B1 (P-B): icon-rotate parses as a zoom-capable float, spec default 0. ──
        [Test]
        public void SymbolLayer_IconRotate_ParsesConstantZoomAndDefault()
        {
            var bare = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c' } ] }").Layers[0];
            Assert.AreEqual(0f, bare.Layout.IconRotate.Evaluate(0.0), 1e-6, "icon-rotate default is 0 (spec)");

            var constant = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-rotate':180 } } ] }").Layers[0];
            Assert.AreEqual(180f, constant.Layout.IconRotate.Evaluate(0.0), 1e-6,
                "a constant icon-rotate parses in DEGREES (the conversion is the extractor's)");
            Assert.AreEqual(180f, constant.Layout.IconRotate.Evaluate(18.0), 1e-6, "a constant is zoom-invariant");

            // Zoom-capable: an interpolate expression must be honoured, not collapsed to the default.
            var zoomed = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-rotate':['interpolate',['linear'],['zoom'],10,0,20,90] } } ] }").Layers[0];
            Assert.AreEqual(0f, zoomed.Layout.IconRotate.Evaluate(10.0), 1e-6, "z10 -> 0");
            Assert.AreEqual(45f, zoomed.Layout.IconRotate.Evaluate(15.0), 1e-4, "z15 -> the linear midpoint");
            Assert.AreEqual(90f, zoomed.Layout.IconRotate.Evaluate(20.0), 1e-6, "z20 -> 90");
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
