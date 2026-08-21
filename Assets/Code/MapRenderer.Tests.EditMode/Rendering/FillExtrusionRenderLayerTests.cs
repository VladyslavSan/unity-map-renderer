// Unity EditMode only — uses RenderLayerFactory, MaterialFactory (per-layer Materials). NOT included in
// Tools/core-tests.
//
// S23 I1 — T4: a style with a fill-extrusion layer (a) TryGetFetchSource returns true with its source, and
// (b) RenderLayerFactory.Create returns a non-null IRenderLayer taking a slot. RED-verified against the
// pre-I1 code (both returned false/null) — see the dev report for the RED-verify evidence.
//
// S23 I2b: the render layer now clones its OWN Map/FillExtrusion base material
// (MapMaterialSet.FillExtrusionMaterial), not the flat FILL base I1 reused. This suite builds its own
// synthetic settings via Shader.Find (mirroring MaterialTweakerTests' NewFill()/NewLine() precedent) so it
// stays independent of the committed asset. As of I3 the committed production (Lit) MapMaterialSet DOES carry a
// FillExtrusionMaterial (the depth-writing render-state wiring it exercises is guarded by
// MaterialTweakerTests.CreateFillExtrusionMaterial_AppliesElevatedContract_NotFlatPainter).

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Tests.Rendering
{
    [TestFixture]
    public class FillExtrusionRenderLayerTests
    {
        // Mirrors MaterialTweakerTests.NewFill()/NewLine() — a synthetic single-shader material, not a
        // committed .mat asset (see the class doc for why the production asset can't back this suite yet).
        private static MapMaterialSet SettingsWithFillExtrusionMaterial()
        {
            var settings = ScriptableObject.CreateInstance<MapMaterialSet>();
            settings.FillExtrusionMaterial = new Material(Shader.Find("Map/FillExtrusion"));
            return settings;
        }

        private const string FillExtrusionStyleJson = @"{
    ""version"": 8,
    ""name"": ""FillExtrusion"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""buildings-3d"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""buildings"",
          ""paint"": { ""fill-extrusion-color"": [""rgba"",120,120,120,1], ""fill-extrusion-height"": 30 } }
    ]
}";

        [Test]
        public void TryGetFetchSource_FillExtrusionLayer_ReturnsTrueWithItsSource()
        {
            StyleDocument style = StyleParser.Parse(FillExtrusionStyleJson);
            Assert.AreEqual(1, style.Layers.Count);
            StyleLayer layer = style.Layers[0];
            Assert.AreEqual(StyleLayerType.FillExtrusion, layer.LayerType);

            bool result = RenderLayerFactory.TryGetFetchSource(layer, out string sourceId);

            Assert.IsTrue(result, "a fill-extrusion layer with a declared source must be an MVT-fetching kind.");
            Assert.AreEqual("s", sourceId, "the fetched source id must be the layer's declared source.");
        }

        [Test]
        public void Create_FillExtrusionLayer_ReturnsNonNullRenderLayer_TakingItsSlot()
        {
            StyleDocument style = StyleParser.Parse(FillExtrusionStyleJson);
            var settings = SettingsWithFillExtrusionMaterial();
            StyleLayer layer = style.Layers[0];

            const int drawIndex = 3;
            IRenderLayer created = RenderLayerFactory.Create(layer, settings, 0.0, drawIndex);
            try
            {
                Assert.IsNotNull(created, "a fill-extrusion layer with a configured material set must produce a render layer.");
                Assert.IsInstanceOf<FillExtrusionRenderLayer>(created, "'buildings-3d' must dispatch to FillExtrusionRenderLayer.");
                Assert.AreSame(layer, created.StyleLayer, "the render layer must reference its source StyleLayer.");
                Assert.AreEqual(drawIndex, created.DrawIndex, "the factory must set DrawIndex from its parameter.");
                Assert.IsInstanceOf<FillExtrusion.StyleLayer>(created.StyleLayer);
                Assert.IsInstanceOf<ITileMeshRenderLayer>(created, "fill-extrusion is a TileMesh layer.");
            }
            finally
            {
                created?.Dispose();
            }
        }
    }
}
