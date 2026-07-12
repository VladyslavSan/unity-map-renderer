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
    /// <see cref="MapMaterialSet.SymbolText"/> clone (NOT one shared material) — with its <c>text-halo-*</c>
    /// bound by name. This is what makes per-layer halo variation possible (F1). Ownership migrated from
    /// <c>SymbolLabelSubsystem</c> into <see cref="SymbolRenderLayer"/> in E2 (D11: "per-layer materials
    /// live on the layer object, one owner") — the tooth is unchanged (per-layer halo), only the owner
    /// under test is: this now builds the render layers directly via <see cref="RenderLayerSet.Build"/>
    /// (the same production path <c>MapView.SetStyle</c> drives) instead of going through the subsystem.
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
        public void Build_PerLayerMaterials_AreDistinctClonesWithBoundHalo()
        {
            var set = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.SymbolText = new Material(Shader.Find("Map/Symbol/Text"));

            StyleDocument style = StyleParser.Parse(TwoSymbolLayers.Replace('\'', '"'));

            try
            {
                using var layers = new RenderLayerSet();
                layers.Build(style, 5.0, set);

                Assert.AreEqual(2, layers.Count, "one render layer per symbol style layer");

                Material m0 = layers[0].Material;
                Material m1 = layers[1].Material;
                Assert.IsNotNull(m0);
                Assert.IsNotNull(m1);
                Assert.AreNotSame(m0, m1, "per-layer materials are DISTINCT instances, not one shared material");
                Assert.AreNotSame(set.SymbolText, m0, "a layer material is a CLONE of the SymbolText base, not the base asset");

                // Each layer's text-halo-width is bound onto its own material by name (F1) — now owned by
                // SymbolRenderLayer (D11), not the subsystem.
                Assert.AreEqual(1f, m0.GetFloat("_HaloWidthPx"), 1e-4f, "layer a's text-halo-width binds to its material");
                Assert.AreEqual(3f, m1.GetFloat("_HaloWidthPx"), 1e-4f, "layer b's text-halo-width binds to its material");
            }
            finally
            {
                Object.DestroyImmediate(set.SymbolText);
                Object.DestroyImmediate(set);
            }
        }
    }
}
