// Unity EditMode only — uses RenderLayerSet, RenderLayerFactory, MaterialFactory (per-layer Materials).
// NOT included in Tools/core-tests.
//
// E1 (design docs/render-layer-unification.md §8, tooth §6.6): D7 global numbering supersedes Stage A's
// "non-renderable takes no slot" contract — background/symbol now take slots too. These teeth fail if the
// old type-bucketing (fills then lines) or a second ordering creeps back, if a genuinely unpainted type
// (raster/unknown) takes a slot, or if a symbol/background slot's queue math desyncs the tile-mesh layers
// shifted above it.
//
// E3 flip: background is now material-bearing too (a real quad + fill-base clone) — the LAST
// null-material slot from E1/E2 is gone. Build_Materials_* and Build_QueueShift assert slot 0 like every
// other slot; Factory_* now Dispose() the whole layer (background owns a GO + Mesh besides its material).

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Tests.Visual;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// E1 — the render-layer unification round-2 structural teeth: one ordered <see cref="RenderLayerSet"/>
    /// over EVERY painted layer kind (D7), fill/line/symbol/background interleaved by declared order,
    /// <c>index == DrawIndex == draw order == material index</c>, and <see cref="RenderLayerFactory"/> as
    /// the sole dispatch point (only genuinely unpainted types take no slot).
    /// </summary>
    [TestFixture]
    public class RenderLayerSetTests
    {
        // background + fill + symbol + line + fill: one style exercising all four painted kinds, in an
        // order that decisively separates "declared order" from "type-bucketed order" for BOTH the
        // tile-mesh pair (fill, then symbol in between, then line, then fill again) and the queue shift
        // (each tile-mesh layer's queue must shift by the count of non-tile-mesh layers preceding it).
        private const string InterleavedStyleJson = @"{
    ""version"": 8,
    ""name"": ""Interleaved"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""bg"",      ""type"": ""background"", ""paint"": { ""background-color"": [""rgba"",10,10,10,1] } },
        { ""id"": ""fill-a"",  ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""symbol-b"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""b"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""line-c"",  ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""fill-d"",  ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        // A second, minimal style for the factory dispatch test only — a raster layer alongside one fill,
        // kept separate from InterleavedStyleJson so the Build_* teeth's index math stays exactly 5-wide.
        private const string RasterAndFillStyleJson = @"{
    ""version"": 8,
    ""name"": ""RasterAndFill"",
    ""sources"": {
        ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] },
        ""r"": { ""type"": ""raster"", ""tiles"": [""https://x/{z}/{x}/{y}.png""] }
    },
    ""layers"": [
        { ""id"": ""raster-r"", ""type"": ""raster"", ""source"": ""r"" },
        { ""id"": ""fill-a"",   ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } }
    ]
}";

        private static RenderLayerSet Build(string json)
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        [Test]
        public void Build_AllFourKinds_TakeSlotsInDeclaredOrder()
        {
            using var set = Build(InterleavedStyleJson);

            // D7: EVERY painted layer takes a slot now — background and symbol included. Five declared
            // layers → five slots, in declared order (not bucketed by type).
            Assert.AreEqual(5, set.Count, "background/symbol/fill/line all take slots under D7 global numbering.");

            Assert.IsInstanceOf<BackgroundRenderLayer>(set[0], "index 0 = declared background");
            Assert.IsInstanceOf<Fill.StyleLayer>(set[1].StyleLayer, "index 1 = declared fill-a");
            Assert.IsInstanceOf<SymbolRenderLayer>(set[2], "index 2 = declared symbol-b (BETWEEN fill and line)");
            Assert.IsInstanceOf<Line.StyleLayer>(set[3].StyleLayer, "index 3 = declared line-c");
            Assert.IsInstanceOf<Fill.StyleLayer>(set[4].StyleLayer, "index 4 = declared fill-d");

            for (int i = 0; i < set.Count; i++)
                Assert.AreEqual(i, set[i].DrawIndex, $"slot {i}'s DrawIndex must equal its declared-order index.");
        }

        [Test]
        public void Build_Axes_MatchTheDesignsPinning()
        {
            using var set = Build(InterleavedStyleJson);

            // Epic A / A2: background is now a source-less per-covered-tile TileMesh layer — ViewGeometry
            // is REMOVED (design §B "The RenderLayerBuild.ViewGeometry enum decision").
            Assert.AreEqual(RenderLayerBuild.TileMesh, set[0].Build, "background is TileMesh (A2).");
            Assert.AreEqual(RenderLayerBuild.TileMesh,     set[1].Build, "fill is TileMesh.");
            Assert.AreEqual(RenderLayerBuild.FramePlaced,  set[2].Build, "symbol is FramePlaced.");
            Assert.AreEqual(RenderLayerBuild.TileMesh,     set[3].Build, "line is TileMesh.");
            Assert.AreEqual(RenderLayerBuild.TileMesh,     set[4].Build, "fill is TileMesh.");

            // Only the TileMesh layers implement the mesh-build capability — the tile produce loop's filter
            // (TileManager.ComputeDenseLayerIds) relies on exactly this.
            Assert.IsNotInstanceOf<ITileMeshRenderLayer>(set[0], "background is not a tile-mesh layer.");
            Assert.IsInstanceOf<ITileMeshRenderLayer>(set[1], "fill IS a tile-mesh layer.");
            Assert.IsNotInstanceOf<ITileMeshRenderLayer>(set[2], "symbol is not a tile-mesh layer.");
            Assert.IsInstanceOf<ITileMeshRenderLayer>(set[3], "line IS a tile-mesh layer.");
            Assert.IsInstanceOf<ITileMeshRenderLayer>(set[4], "fill IS a tile-mesh layer.");
        }

        [Test]
        public void Build_QueueShift_TileMeshLayersShiftByPrecedingNonTileMeshCount()
        {
            using var set = Build(InterleavedStyleJson);

            // D7: renderQueue = TransparentQueue + DrawIndex, and DrawIndex == the slot's own declared-order
            // index — uniformly for EVERY slot, material-bearing or not (RenderLayerSet.Build increments
            // drawIndex once per slot regardless; the QUEUE WRITE is skipped only when that slot's own base
            // material is unconfigured). E3 made background material-bearing too, so slot 0 (bg) now carries
            // its own queue same as every other slot — the last E1/E2 null-material slot is gone.
            Assert.AreEqual(LayerDrawOrder.TransparentQueue + 0, set[0].Material.renderQueue,
                "bg — slot 0, material-bearing as of E3 so it gets a queue like every other slot.");
            Assert.AreEqual(LayerDrawOrder.TransparentQueue + 1, set[1].Material.renderQueue, "fill-a — slot 1.");
            Assert.AreEqual(LayerDrawOrder.TransparentQueue + 2, set[2].Material.renderQueue,
                "symbol-b — slot 2, material-bearing (E2, D11) so it gets a queue like every other slot.");
            Assert.AreEqual(LayerDrawOrder.TransparentQueue + 3, set[3].Material.renderQueue, "line-c — slot 3.");
            Assert.AreEqual(LayerDrawOrder.TransparentQueue + 4, set[4].Material.renderQueue, "fill-d — slot 4.");

            // Monotonic over EVERY material-bearing slot (background, fill, symbol, line, fill), in declared order.
            Assert.Less(set[0].Material.renderQueue, set[1].Material.renderQueue);
            Assert.Less(set[1].Material.renderQueue, set[2].Material.renderQueue);
            Assert.Less(set[2].Material.renderQueue, set[3].Material.renderQueue);
            Assert.Less(set[3].Material.renderQueue, set[4].Material.renderQueue);
        }

        [Test]
        public void Build_Materials_EverySlotMaterialBearing_AllDistinct()
        {
            using var set = Build(InterleavedStyleJson);

            // E3: background is now material-bearing too (a fill-base clone, MaterialFactory
            // .CreateBackgroundMaterial) — no slot is null when the material set is configured.
            Assert.IsNotNull(set[0].Material, "bg now owns a material (E3) — BackgroundRenderLayer.Create clones MapMaterialSet.FillMaterial.");
            Assert.IsNotNull(set[1].Material);
            Assert.IsNotNull(set[2].Material, "symbol-b now owns a material (E2, D11) — SymbolRenderLayer.Create clones MapMaterialSet.SymbolText.");
            Assert.IsNotNull(set[3].Material);
            Assert.IsNotNull(set[4].Material);
            Assert.AreNotSame(set[0].Material, set[1].Material, "bg and fill-a must be distinct instances.");
            Assert.AreNotSame(set[0].Material, set[2].Material, "bg and symbol-b must be distinct instances.");
            Assert.AreNotSame(set[0].Material, set[4].Material, "bg and fill-d must be distinct instances (both fill-base clones).");
            Assert.AreNotSame(set[1].Material, set[2].Material, "fill-a and symbol-b must be distinct instances.");
            Assert.AreNotSame(set[1].Material, set[3].Material, "fill-a and line-c must be distinct instances.");
            Assert.AreNotSame(set[1].Material, set[4].Material, "fill-a and fill-d must be distinct instances.");
            Assert.AreNotSame(set[2].Material, set[3].Material, "symbol-b and line-c must be distinct instances.");
            Assert.AreNotSame(set[3].Material, set[4].Material, "line-c and fill-d must be distinct instances.");
        }

        [Test]
        public void Build_LayerGameObjects_ParentedUnderVisibleRoot()
        {
            using var set = Build(InterleavedStyleJson);

            // The shared "Map Render Layers" root exists and is a visible-but-not-serialised runtime
            // artifact (DontSave carries no HideInHierarchy bit, unlike the old HideAndDontSave).
            Assert.IsNotNull(set.Root, "RenderLayerSet exposes a shared root after Build.");
            Assert.AreEqual(HideFlags.DontSave, set.Root.gameObject.hideFlags,
                "the root is not serialised into a scene/build, but IS visible/inspectable in the Hierarchy.");

            // Epic A / A2: background no longer owns a scene GameObject (its geometry is per-tile, produced
            // by TileBackgroundLayerProcessor and owned by the backend) — only symbol presenters still take
            // the shared-root parenting path (lazily, on first Present), so there is nothing eager left to
            // assert here for background; this test now only pins the shared root's own visibility contract.
        }

        [Test]
        public void Factory_IsTheSoleDispatchPoint_UnpaintedTypesYieldNoRenderLayer()
        {
            // Extensibility contract: RenderLayerFactory maps a StyleLayer subtype → IRenderLayer. Genuinely
            // unpainted/unsupported-for-now types (raster) map to null (no slot); background/symbol now DO
            // produce a render layer (D7) — the axis-bearing placeholders this stage adds.
            StyleDocument style = StyleParser.Parse(RasterAndFillStyleJson);
            var settings = MapMaterialSetTestUtil.Load();

            int drawIndex = 0;
            foreach (var sl in style.Layers)
            {
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, 0.0, drawIndex);
                if (sl.LayerType == StyleLayerType.Raster)
                {
                    Assert.IsNull(layer, $"'{sl.Id}' (raster) is genuinely unpainted — no slot.");
                    continue;
                }

                Assert.IsNotNull(layer, $"'{sl.Id}' is a renderable type and must produce an IRenderLayer.");
                Assert.AreSame(sl, layer.StyleLayer, "the render layer must reference its source StyleLayer.");
                Assert.AreEqual(drawIndex, layer.DrawIndex, "the factory must set DrawIndex from its parameter.");
                layer.Dispose(); // no set owns it here — free it (correct for every kind; background also owns a GO+Mesh, E3)
                drawIndex++;
            }
        }

        [Test]
        public void Factory_Create_ReturnsTheAxisBearingPlaceholders_ForSymbolAndBackground()
        {
            StyleDocument style = StyleParser.Parse(InterleavedStyleJson);
            var settings = MapMaterialSetTestUtil.Load();

            int drawIndex = 0;
            foreach (var sl in style.Layers)
            {
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, 0.0, drawIndex);
                switch (sl)
                {
                    case Symbol.StyleLayer:
                        Assert.IsInstanceOf<SymbolRenderLayer>(layer, $"'{sl.Id}' must dispatch to SymbolRenderLayer.");
                        break;
                    default:
                        if (sl.LayerType == StyleLayerType.Background)
                            Assert.IsInstanceOf<BackgroundRenderLayer>(layer, $"'{sl.Id}' must dispatch to BackgroundRenderLayer.");
                        break;
                }
                layer?.Dispose(); // no set owns it here — free it (background also owns a GO+Mesh, E3)
                drawIndex++;
            }
        }
    }
}
