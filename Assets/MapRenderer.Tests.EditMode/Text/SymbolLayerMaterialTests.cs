// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolLabelSubsystem. NOT
// registered in core-tests.csproj.

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S105 Slice 4 (A5b): each symbol style layer gets its OWN material — a distinct
    /// <see cref="MapMaterialSet.SymbolText"/> clone (NOT one shared material) — with its
    /// <c>text-halo-*</c> bound by name. This is what makes per-layer halo variation possible (F1).
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
        public void SetStyle_PerLayerMaterials_AreDistinctClonesWithBoundHalo()
        {
            var camGo = new GameObject("SymbolLayerMat_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));

            var set = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.SymbolText = new Material(Shader.Find("Map/SymbolText"));

            StyleDocument style = StyleParser.Parse(TwoSymbolLayers.Replace('\'', '"'));
            var subsystem = new SymbolLabelSubsystem(mapCamera, set);

            try
            {
                subsystem.SetStyle(style);

                Assert.IsTrue(subsystem.HasSymbolLayers);
                Assert.AreEqual(2, subsystem.LayerMaterials.Count, "one material per symbol layer");

                Material m0 = subsystem.LayerMaterials[0];
                Material m1 = subsystem.LayerMaterials[1];
                Assert.IsNotNull(m0);
                Assert.IsNotNull(m1);
                Assert.AreNotSame(m0, m1, "per-layer materials are DISTINCT instances, not one shared material");
                Assert.AreNotSame(set.SymbolText, m0, "a layer material is a CLONE of the SymbolText base, not the base asset");

                // Each layer's text-halo-width is bound onto its own material by name (F1).
                Assert.AreEqual(1f, m0.GetFloat("_HaloWidthPx"), 1e-4f, "layer a's text-halo-width binds to its material");
                Assert.AreEqual(3f, m1.GetFloat("_HaloWidthPx"), 1e-4f, "layer b's text-halo-width binds to its material");
            }
            finally
            {
                subsystem.Dispose();
                Object.DestroyImmediate(set.SymbolText);
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
