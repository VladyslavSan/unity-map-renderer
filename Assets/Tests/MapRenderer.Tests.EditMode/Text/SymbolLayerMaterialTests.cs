// Unity EditMode only — needs a real Material/Shader + the internal RenderLayerSet. NOT registered in
// core-tests.csproj.

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S105 Slice 4 (A5b) → E2 (D11): each symbol style layer gets its OWN material — a distinct
    /// <see cref="MapMaterialSet.SymbolTextWorld"/> clone (NOT one shared material) — with its
    /// <c>text-halo-*</c> bound by name. This is what makes per-layer halo variation possible (F1).
    /// Ownership migrated from <c>SymbolSubsystem</c> into <see cref="SymbolRenderLayer"/> in E2
    /// (D11: "per-layer materials live on the layer object, one owner") — the tooth is unchanged
    /// (per-layer halo), only the owner under test is: this now builds the render layers directly via
    /// <see cref="RenderLayerSet.Build"/> (the same production path <c>MapView.SetStyle</c> drives) instead
    /// of going through the subsystem. <see cref="IRenderLayer.Material"/> IS
    /// <see cref="SymbolRenderLayer.WorldTextMaterial"/> (§0.2 — the screen material is retired), so the two
    /// used to be asserted separately are now the same object; asserted here as a single identity.
    /// </summary>
    [TestFixture]
    public class SymbolLayerMaterialTests
    {
        private const string TwoSymbolLayers = @"{
            'version': 8,
            'layers': [
                { 'id':'a', 'type':'symbol', 'source':'s', 'source-layer':'la',
                  'layout': { 'text-field':'{NAME}' }, 'paint': { 'text-halo-width': 1 } },
                { 'id':'b', 'type':'symbol', 'source':'s', 'source-layer':'lb',
                  'layout': { 'text-field':'{NAME}' }, 'paint': { 'text-halo-width': 3 } }
            ]
        }";

        [Test]
        public void Build_PerLayerMaterials_AreDistinctClones()
        {
            var set = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.SymbolTextWorld = new Material(Shader.Find("Map/Symbol/TextWorld"));

            StyleDocument style = StyleParser.Parse(TwoSymbolLayers.Replace('\'', '"'));

            try
            {
                using var layers = new RenderLayerSet();
                layers.Build(style, 5.0, set);

                Assert.AreEqual(2, layers.Count, "one render layer per symbol style layer");

                var symbolLayer0 = (SymbolRenderLayer)layers[0];
                var symbolLayer1 = (SymbolRenderLayer)layers[1];
                Material m0 = symbolLayer0.Material;
                Material m1 = symbolLayer1.Material;
                Assert.IsNotNull(m0);
                Assert.IsNotNull(m1);
                Assert.AreSame(m0, symbolLayer0.WorldTextMaterial, "Material IS WorldTextMaterial (§0.2 — the screen material is retired).");
                Assert.AreNotSame(m0, m1, "per-layer materials are DISTINCT instances, not one shared material");
                Assert.AreNotSame(set.SymbolTextWorld, m0, "a layer material is a CLONE of the SymbolTextWorld base, not the base asset");

                // No paint assertion here any more: a symbol layer's materials carry engine plumbing only.
                // Every text-* term, text-halo-* included, is evaluated per feature and rides the vertex
                // stream — SymbolHaloEmitTests reads it where it actually lands, on the emitted mesh.
            }
            finally
            {
                Object.DestroyImmediate(set.SymbolTextWorld);
                Object.DestroyImmediate(set);
            }
        }
    }
}
