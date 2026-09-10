// Unity EditMode only — uses RenderLayerSet, RenderLayerFactory, MaterialFactory (per-layer Materials).
// NOT included in Tools/core-tests.
//
// E1 (the render-layer model): D7 global numbering supersedes Stage A's
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

namespace MapRenderer.Tests.Rendering
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

            // Pin WHICH sub-slot each kind's Material occupies — independent of the queue-value assertions
            // below, so a wrongly-defaulted MaterialSubSlot can't hide behind a coincidentally-matching queue.
            Assert.AreEqual(LayerSubSlot.Base,  set[0].MaterialSubSlot, "background — Base.");
            Assert.AreEqual(LayerSubSlot.Base,  set[1].MaterialSubSlot, "fill — Base.");
            Assert.AreEqual(LayerSubSlot.Above, set[2].MaterialSubSlot, "symbol — Above (its Material is WorldTextMaterial).");
            Assert.AreEqual(LayerSubSlot.Base,  set[3].MaterialSubSlot, "line — Base.");
            Assert.AreEqual(LayerSubSlot.Base,  set[4].MaterialSubSlot, "fill — Base.");

            // G7/D7 (Stage 2): renderQueue = LayerDrawOrder.QueueFor(DrawIndex, MaterialSubSlot) — DrawIndex
            // == the slot's own declared-order index, uniformly for EVERY slot (RenderLayerSet.Build
            // increments drawIndex once per slot regardless; the QUEUE WRITE is skipped only when that
            // slot's own base material is unconfigured); MaterialSubSlot is Base for background/fill/line and
            // Above for symbol-b's Material (its WorldTextMaterial), since a symbol layer's text must draw
            // over its own icon. E3 made background material-bearing too, so slot 0 (bg) now carries its own
            // queue same as every other slot — the last E1/E2 null-material slot is gone.
            Assert.AreEqual(LayerDrawOrder.QueueFor(0, set[0].MaterialSubSlot), set[0].Material.renderQueue,
                "bg — slot 0, Base, material-bearing as of E3 so it gets a queue like every other slot.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(1, set[1].MaterialSubSlot), set[1].Material.renderQueue, "fill-a — slot 1, Base.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(2, set[2].MaterialSubSlot), set[2].Material.renderQueue,
                "symbol-b — slot 2, Above (its Material IS WorldTextMaterial), material-bearing (E2, D11) so it gets a queue like every other slot.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(3, set[3].MaterialSubSlot), set[3].Material.renderQueue, "line-c — slot 3, Base.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(4, set[4].MaterialSubSlot), set[4].Material.renderQueue, "fill-d — slot 4, Base.");

            // Monotonic over EVERY material-bearing slot (background, fill, symbol, line, fill), in declared order.
            Assert.Less(set[0].Material.renderQueue, set[1].Material.renderQueue);
            Assert.Less(set[1].Material.renderQueue, set[2].Material.renderQueue);
            // U3: symbol-b's TEXT queue (slot 2, Above — its own layer's higher sub-slot) must still be
            // strictly below slot 3's Base — the next layer's band must not be reachable from inside this one.
            Assert.Less(set[2].Material.renderQueue, set[3].Material.renderQueue,
                "symbol-b's text (Above sub-slot) must stay strictly below line-c's Base sub-slot — a symbol " +
                "layer's own band must never escape into the next layer's.");
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
            Assert.IsNotNull(set[2].Material, "symbol-b now owns a material (E2, D11) — SymbolRenderLayer.Create clones MapMaterialSet.SymbolTextWorld.");
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

        // Commit-2 deletion (§0.1): the world icon material's renderQueue used to be set SOLELY by
        // WorldSymbolRenderer.ResolveMaterial's per-frame sync (deleted this commit) — this style, with two
        // symbol layers at distinct draw indices, pins that BOTH world materials now carry the correct
        // Build-time queue with NO Tick at all.
        private const string TwoSymbolLayersStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""symbol-a"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""la"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""symbol-b"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""lb"", ""layout"": { ""text-field"": ""{NAME}"" } }
    ]
}";

        // U1 — G7/D7 contract correction, NOT a re-bake. The version this replaces asserted BOTH materials
        // equal LayerDrawOrder.QueueFor(i) — i.e. it PINNED the G7 bug (icon and text sharing one queue,
        // coplanar + ZWrite off ⇒ no tiebreak ⇒ the badge can paint over its own number). The "Build-time,
        // no Tick" intent survives verbatim; what changes is the asserted VALUE — icon at its own layer's
        // Base sub-slot, text at Above, and (the strict inequality below) icon < text ASSERTED EXPLICITLY,
        // not merely implied by two equalities. A re-bake could restate equality at new numbers and still
        // pass with the icon on top of the text; this cannot.
        [Test]
        public void Build_SymbolLayers_WorldTextAndIconQueues_SetAtBuildTime_NoTickNeeded()
        {
            var settings = MapMaterialSetTestUtil.Load();
            Assert.IsNotNull(settings.SymbolIconWorld,
                "this tooth needs SymbolIconWorld assigned on the production set to observe the icon queue write.");

            using var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(TwoSymbolLayersStyleJson), 0.0, settings);

            Assert.AreEqual(2, set.Count, "one render layer per declared symbol layer.");
            for (int i = 0; i < set.Count; i++)
            {
                var symbolLayer = (SymbolRenderLayer)set[i];
                int iconQueue = symbolLayer.WorldIconMaterial.renderQueue;
                int textQueue = symbolLayer.WorldTextMaterial.renderQueue;
                int bandBase  = LayerDrawOrder.QueueFor(i, LayerSubSlot.Base);
                int bandTop   = bandBase + LayerDrawOrder.SubSlotsPerLayer - 1;

                Assert.AreEqual(bandBase, iconQueue,
                    $"slot {i}'s WorldIconMaterial queue must be set directly by SymbolRenderLayer.Create (§0.1), " +
                    "with NO Tick, at its own layer's Base sub-slot.");
                Assert.AreEqual(LayerDrawOrder.QueueFor(i, LayerSubSlot.Above), textQueue,
                    $"slot {i}'s WorldTextMaterial queue must be set by RenderLayerSet.Build (via Material, §0.2), " +
                    "with NO Tick, at its own layer's Above sub-slot.");

                // The tooth a re-bake cannot fake: icon strictly below its own layer's text (G7/D7).
                Assert.Less(iconQueue, textQueue,
                    $"slot {i}: the icon must draw strictly BEFORE (below) its own text — otherwise the badge " +
                    "can paint over the number it frames, the exact bug this stage fixes.");
                Assert.That(iconQueue, Is.InRange(bandBase, bandTop), $"slot {i}'s icon queue must lie inside its own layer's band.");
                Assert.That(textQueue, Is.InRange(bandBase, bandTop), $"slot {i}'s text queue must lie inside its own layer's band.");
            }
        }

        // U2 — cross-layer: every sub-slot of layer 0's band must sit strictly below every sub-slot of
        // layer 1's band. This is the tooth a "text = queue + 1" naive fix fails: under SubSlotsPerLayer = 1
        // (keeping Above = 1), layer 0's text and layer 1's icon collide at the same value.
        [Test]
        public void Build_SymbolLayers_Layer0Band_IsStrictlyBelow_Layer1Band()
        {
            var settings = MapMaterialSetTestUtil.Load();
            using var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(TwoSymbolLayersStyleJson), 0.0, settings);

            var layer0 = (SymbolRenderLayer)set[0];
            var layer1 = (SymbolRenderLayer)set[1];

            Assert.Less(layer0.WorldTextMaterial.renderQueue, layer1.WorldIconMaterial.renderQueue,
                "layer 0's text (its own band's Above sub-slot — the highest queue it owns) must be strictly " +
                "below layer 1's icon (its own band's Base sub-slot — the lowest queue it owns): a symbol " +
                "layer's text must never escape into the next layer's band.");
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
            // by BackgroundQuad and owned by the backend) — only symbol presenters still take
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
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, 0.0, drawIndex, out _);
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
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, 0.0, drawIndex, out _);
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
