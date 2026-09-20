// S76 BRG line-prop readback tests — THE CPU-BUFFER CI GATE (GPU-independent, always green headless).
//
// Directly constructs a BrgTileRenderer from a line-only RenderLayerSet (no MapView) and asserts that:
//   1. FloatsPerInstance == 82 and MetadataEntryCount == 33 (exact plan-count tooth).
//   2. _Width packed value == mat.GetFloat(_Width id) == 40 (non-NaN, the direct pack proof).
//   3. _Opacity SoA float offset == 46 (byte-identical-wire spot check: fill wire layout unchanged).
//
// Falsifiability: the buggy build (no _Width plan entry) makes GetInstancePropValue return NaN instead
// of the material value → assertion != mat.GetFloat(id) FAILS. The non-NaN check names the failure
// explicitly rather than silently passing through 0.
//
// Pattern: mirrors BrgTileRendererEvictionTests (direct BrgTileRenderer construction, no MapView).

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S76 CPU-buffer readback acceptance tests: line props must be packed at the correct SoA slots.
    /// All assertions are GPU-independent (read the CPU float[] buffer via <see cref="BrgTileRenderer.GetInstancePropValue"/>).
    /// </summary>
    [TestFixture]
    public class BrgLinePropReadbackTests
    {
        // Line-only style: one line layer with line-width=40 (literal, so the styler evaluates it as 40
        // at any zoom). This sets _Width=40 on the material after RenderLayerSet.Build.
        private const string LineStyleJson = @"{
    ""version"": 8,
    ""name"": ""LinePropReadbackTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""test-line"", ""type"": ""line"",
          ""source"": ""s"", ""source-layer"": ""geolines"",
          ""paint"": { ""line-color"": [""rgba"", 80, 200, 80, 1], ""line-width"": 40 } }
    ]
}";

        /// <summary>
        /// Headless CI gate: after Rebuild with a line material that has _Width=40,
        /// the CPU buffer must contain 40.0 at the correct SoA slot for _Width.
        /// </summary>
        [Test]
        public void BrgLinePropReadback_PackedValuesMatchMaterial()
        {
            var style  = StyleParser.Parse(LineStyleJson);
            var set    = new RenderLayerSet();
            set.Build(style, 0.0, MapMaterialSetTestUtil.Load());

            // Line-only style: one render layer (a line) → _layerMaterials[0] = layer[0].
            Assert.That(set.Count, Is.EqualTo(1), "Line-only style must have exactly 1 render layer.");
            Assert.IsInstanceOf<MapRenderer.Core.Style.Line.StyleLayer>(set[0].StyleLayer,
                "The single render layer must be a line.");

            Material mat = set[0].Material;
            Assert.IsNotNull(mat, "Line material must be non-null after RenderLayerSet.Build.");

            // Pre-check: styler must have applied line-width=40 to the material.
            int widthId       = ShaderProperties.Line.PropertyId.Width;
            int opacityId     = ShaderProperties.PropertyId.Opacity;

            float matWidth      = mat.HasProperty(widthId)      ? mat.GetFloat(widthId)      : float.NaN;
            float matOpacity    = mat.HasProperty(opacityId)    ? mat.GetFloat(opacityId)    : float.NaN;

            Assert.That(matWidth, Is.EqualTo(40f).Within(1e-3f),
                "Pre-check: mat._Width must be 40 after RenderLayerSet.Build with line-width:40. " +
                "If NaN, the Line shader does not declare _Width in Properties{}.");

            var brg  = new BrgTileRenderer(new[] { set[0].Material });
            var mesh = new Mesh();

            try
            {
                // ── Exact plan-count tooth ────────────────────────────────────────────────────
                Assert.That(brg.FloatsPerInstance(), Is.EqualTo(82),
                    "BrgTileRenderer.FloatsPerInstance must be 82 (MapInstanceData: 24 transform + 58 material floats). " +
                    "An incompletely-generated plan (e.g. missing line props) produces a smaller value.");

                Assert.That(brg.MetadataEntryCount(), Is.EqualTo(33),
                    "BrgTileRenderer.MetadataEntryCount must be 33 (2 transforms + 31 material props). " +
                    "Missing entries mean the BRG batch omits those props from the GPU instancing table.");

                // ── Register and Rebuild ──────────────────────────────────────────────────────
                // materialIndex=0: FillCount=0 → lines[0] is at index 0.
                int h = brg.AddTileLayer(mesh, double3.zero, 0, new TileId { Z = 0, X = 0, Y = 0 });
                brg.Rebuild(SceneFrame.Mercator(double2.zero));

                // ── _Width readback (the direct line-prop pack proof) ─────────────────────────
                float packedWidth = brg.GetInstancePropValue(h, widthId);

                Assert.That(packedWidth, Is.Not.NaN,
                    "GetInstancePropValue(_Width) returned NaN — no plan entry for _Width. " +
                    "This is the original bug: _Width has no SoA slot → reads garbage (byte 0 = transform). " +
                    "S76: MapInstanceData must declare _Width as a field so InstancePropPlan creates its entry.");

                Assert.That(packedWidth, Is.EqualTo(matWidth).Within(1e-3f),
                    $"Packed _Width ({packedWidth}) must equal mat.GetFloat(_Width) ({matWidth}). " +
                    "If they differ, the pack gate (HasProperty + GetFloat) is broken for _Width.");

                Assert.That(packedWidth, Is.EqualTo(40f).Within(1e-3f),
                    $"Packed _Width must be 40.0 (the style's line-width). Got {packedWidth}.");

                // ── _Opacity byte-identical-wire spot check ───────────────────────────────────
                // _Opacity was at SoA float offset 46 before S76 and must remain there so the fill
                // wire layout is byte-identical (no offset shift for existing 19 props).
                int opacitySoaOffset = brg.GetPropSoaOffset(opacityId);
                Assert.That(opacitySoaOffset, Is.EqualTo(46),
                    $"_Opacity SoA float offset must be 46 (unchanged from pre-S76). " +
                    $"Got {opacitySoaOffset}. If shifted, the fill wire layout changed and existing " +
                    "fill-rendered tiles would misread per-instance properties.");
            }
            finally
            {
                brg.Dispose();
                Object.DestroyImmediate(mesh);
                set.Dispose();
            }
        }
    }
}
#endif
