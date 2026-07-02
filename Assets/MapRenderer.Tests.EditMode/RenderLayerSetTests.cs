// Unity EditMode only — uses RenderLayerSet, RenderLayerFactory, MaterialFactory (per-layer Materials).
// NOT included in Tools/core-tests.
//
// S89 Stage A acceptance: the fill-vs-line split is gone. Fill and line are two IRenderLayer
// implementations in ONE ordered list where index == draw order == material index, dispatched by the
// single RenderLayerFactory registry. These teeth fail if the old type-bucketing (fills then lines) or a
// second ordering (materialIndex = FillCount + li) creeps back, or if a non-renderable layer takes a slot.

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Tests.Visual;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S89 Stage A — the render-layer unification's structural teeth: one ordered <see cref="RenderLayerSet"/>,
    /// fill/line interleaved by declared order (not bucketed by type), a single monotonic draw ordering, and
    /// <see cref="RenderLayerFactory"/> as the sole dispatch point (non-renderable types take no slot).
    /// </summary>
    [TestFixture]
    public class RenderLayerSetTests
    {
        // background (not renderable) + fill + line + fill: an interleaved style with a non-render layer.
        // If the old type-bucketing survived, the line would sort after both fills (wrong). The background
        // must take NO slot.
        private const string InterleavedStyleJson = @"{
    ""version"": 8,
    ""name"": ""Interleaved"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""bg"",     ""type"": ""background"", ""paint"": { ""background-color"": [""rgba"",10,10,10,1] } },
        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""line-b"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""fill-c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        private static RenderLayerSet Build(string json)
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        [Test]
        public void Build_OneOrderedList_FillAndLineInterleavedByDeclaredOrder_BackgroundTakesNoSlot()
        {
            using var set = Build(InterleavedStyleJson);

            // Background is not a render layer → no slot. Three renderable layers remain, in declared order.
            Assert.AreEqual(3, set.Count, "background must take no slot; fill+line+fill remain.");

            // DECISIVE: the list is interleaved by DECLARED order (fill, line, fill) — NOT bucketed by type
            // (which would be fill, fill, line). One list, two IRenderLayer kinds, no _fills/_lines split.
            Assert.IsInstanceOf<Fill.StyleLayer>(set[0].StyleLayer, "index 0 = declared fill-a");
            Assert.IsInstanceOf<Line.StyleLayer>(set[1].StyleLayer, "index 1 = declared line-b (BETWEEN the fills)");
            Assert.IsInstanceOf<Fill.StyleLayer>(set[2].StyleLayer, "index 2 = declared fill-c");
            Assert.AreEqual("fill-a", set[0].StyleLayer.Id);
            Assert.AreEqual("line-b", set[1].StyleLayer.Id);
            Assert.AreEqual("fill-c", set[2].StyleLayer.Id);
        }

        [Test]
        public void Build_OneOrdering_RenderQueueStrictlyIncreasesWithDeclaredIndex()
        {
            using var set = Build(InterleavedStyleJson);

            // index == draw order: renderQueue is a single monotonic sequence across ALL layer kinds.
            int q0 = set[0].Material.renderQueue;
            int q1 = set[1].Material.renderQueue;
            int q2 = set[2].Material.renderQueue;

            Assert.Less(q0, q1, "declared-earlier fill must draw under the interleaved line.");
            Assert.Less(q1, q2, "the interleaved line must draw under the later fill (painter's order).");
            Assert.GreaterOrEqual(q0, LayerDrawOrder.TransparentBandStart, "all layers share the transparent band.");
        }

        [Test]
        public void Build_EachLayer_HasItsOwnDistinctMaterialInstance()
        {
            using var set = Build(InterleavedStyleJson);
            for (int i = 0; i < set.Count; i++)
                for (int j = i + 1; j < set.Count; j++)
                    Assert.AreNotSame(set[i].Material, set[j].Material,
                        $"layer {i} and {j} must own distinct Material instances (never shared).");
        }

        [Test]
        public void Factory_IsTheSoleDispatchPoint_NonRenderableTypeYieldsNoRenderLayer()
        {
            // Extensibility contract: RenderLayerFactory maps a StyleLayer subtype → IRenderLayer. Adding a
            // static layer type is one arm here + one IRenderLayer class — the set/backends/consume loop are
            // untouched. A non-renderable type (background) maps to null (no slot), which is why Build above
            // yields 3, not 4.
            StyleDocument style = StyleParser.Parse(InterleavedStyleJson);
            var settings = MapMaterialSetTestUtil.Load();

            foreach (var sl in style.Layers)
            {
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, 0.0);
                if (sl is Fill.StyleLayer || sl is Line.StyleLayer)
                {
                    Assert.IsNotNull(layer, $"'{sl.Id}' is a renderable type and must produce an IRenderLayer.");
                    Assert.AreSame(sl, layer.StyleLayer, "the render layer must reference its source StyleLayer.");
                    RenderLayerSet.DestroyMaterialInstance(layer.Material); // no set owns it here — free it
                }
                else
                {
                    Assert.IsNull(layer, $"'{sl.Id}' ({sl.GetType().Name}) is not a static render layer — no slot.");
                }
            }
        }
    }
}
