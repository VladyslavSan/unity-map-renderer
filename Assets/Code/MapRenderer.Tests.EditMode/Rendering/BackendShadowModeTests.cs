// Unity EditMode only — constructs the three real tile-render backends. NOT included in Tools/core-tests.
//
// Two INDEPENDENT teeth for "buildings cast shadows, ground receives them", deliberately not collapsed into
// one parity test:
//
//   TRANSPORT  — each backend is handed a shadow list ([On, Off, On] over three FILL layers) that no backend
//                could reconstruct from the layer kinds, and must report exactly it back. Three backends
//                agreeing while all reading one list would be near-vacuous: a wrong list reds nothing. A list
//                that contradicts the kinds kills "ignores the list", "re-derives from the material" and
//                "hard-codes a constant" in one assertion.
//   DERIVATION — TileManager.LayerShadowModes yields On at exactly the fill-extrusion slot. Pure; touches no
//                backend, so a backend defect cannot red it and a wrong declaration cannot hide behind one.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests.Rendering
{
    /// <summary>
    /// UMR-92: the per-layer <see cref="IRenderLayer.CastShadows"/> declaration reaches the GPU through all
    /// three backends, and only <c>fill-extrusion</c> declares <see cref="ShadowCastingMode.On"/>.
    /// </summary>
    [TestFixture]
    public class BackendShadowModeTests
    {
        // Three FILL layers — every one of them would derive Off from its kind. The shadow list below says
        // otherwise, which is the whole point (see the file header).
        private const string ThreeFillsStyleJson = @"{
    ""version"": 8,
    ""name"": ""BackendShadowModeTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"",
          ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""fill1"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"",
          ""paint"": { ""fill-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""fill2"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"",
          ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        // background, fill, line, fill-extrusion, symbol — one of every painted kind, in declared order, so
        // the derivation tooth sees the whole axis and not just the interesting slot.
        private const string AllKindsStyleJson = @"{
    ""version"": 8,
    ""name"": ""AllKindsShadowTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""bg"", ""type"": ""background"" },
        { ""id"": ""land"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"",
          ""paint"": { ""fill-color"": [""rgba"",255,255,255,1] } },
        { ""id"": ""road"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"",
          ""paint"": { ""line-color"": [""rgba"",0,0,0,1], ""line-width"": 2 } },
        { ""id"": ""buildings-3d"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""c"",
          ""paint"": { ""fill-extrusion-color"": [""rgba"",120,120,120,1], ""fill-extrusion-height"": 30 } },
        { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""d"",
          ""layout"": { ""text-field"": ""{NAME}"" } }
    ]
}";

        // The list no backend can re-derive: three fills, alternating cast modes.
        private static readonly ShadowCastingMode[] Declared =
        {
            ShadowCastingMode.On,
            ShadowCastingMode.Off,
            ShadowCastingMode.On,
        };

        private static readonly string[] DeclaredNames = { "fill0", "fill1", "fill2" };

        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        private static RenderLayerSet ThreeFillLayerSet()
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(ThreeFillsStyleJson), 0.0, MapMaterialSetTestUtil.Load());
            Assert.AreEqual(3, set.Count, "fixture: the transport style must produce exactly three slots.");
            return set;
        }

        private static List<Material> MaterialsOf(RenderLayerSet set)
        {
            var mats = new List<Material>(set.Count);
            for (int i = 0; i < set.Count; i++) mats.Add(set[i].Material);
            return mats;
        }

        // ── Tooth 1 — TRANSPORT ───────────────────────────────────────────────────────────────────

        [Test]
        public void GameObjectBackend_TransportsTheDeclaredShadowModes_PerLayer()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            GameObjectTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new GameObjectTileRenderer(MaterialsOf(set), DeclaredNames, Declared);
                double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
                    meshes.Add(mesh);
                    r.AddTileLayer(mesh, origin, i, Tile);
                }

                for (int i = 0; i < 3; i++)
                {
                    // By NAME, not GetChild(i): nothing contracts child ORDER, and the pool reparents
                    // released nodes, so an index assumption could go green for the wrong reason.
                    Transform child = r.Container(Tile).Find(DeclaredNames[i]);
                    Assert.IsNotNull(child, $"slot {i} must have produced a child named '{DeclaredNames[i]}'.");
                    var mr = child.GetComponent<MeshRenderer>();
                    Assert.AreEqual(Declared[i], mr.shadowCastingMode,
                        $"GameObject backend must carry the DECLARED cast mode at slot {i} " +
                        $"('{DeclaredNames[i]}'), not one it re-derived from the layer kind.");
                    Assert.IsTrue(mr.receiveShadows, $"slot {i} must receive shadows.");
                }
            }
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        [Test]
        public void EntitiesBackend_TransportsTheDeclaredShadowModes_PerLayer()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            EntitiesTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new EntitiesTileRenderer(MaterialsOf(set), DeclaredNames, Declared);
                double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
                    meshes.Add(mesh);
                    handles[i] = r.AddTileLayer(mesh, origin, i, Tile);
                }

                for (int i = 0; i < 3; i++)
                {
                    var (cast, receive) = r.GetShadowFilter(handles[i]);
                    Assert.AreEqual(Declared[i], cast,
                        $"Entities backend must carry the DECLARED cast mode at slot {i}, not one it " +
                        "re-derived from the layer kind.");
                    Assert.IsTrue(receive, $"slot {i} must receive shadows.");
                }

                Assert.AreEqual(1, r.RenderMeshArraysCreated,
                    "the two shadow-mode prototypes are built from ONE RenderMeshArray value — " +
                    "content-hashed equality collapses them to one shared component.");
            }
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        [Test]
        public void BrgBackend_TransportsTheDeclaredShadowModes_PerDrawRange()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            BrgTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new BrgTileRenderer(MaterialsOf(set), Declared);
                double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
                    meshes.Add(mesh);
                    r.AddTileLayer(mesh, origin, i, Tile);
                }
                r.Rebuild(SceneFrame.Mercator(double2.zero));

                var emit   = new List<int>();
                var ranges = new List<BatchDrawRange>();
                Assert.AreEqual(3, r.ComputeEmitOrder(emit, BatchCullingViewType.Camera),
                    "a camera view emits every live draw item.");
                Assert.AreEqual(3, r.ComputeDrawRanges(emit, ranges),
                    "[On, Off, On] over three ordered commands run-length groups into THREE ranges — " +
                    "one range would mean the declaration was flattened.");

                // Walk range → its commands → the slot each command draws, and check the range's declared
                // filter against that slot's declaration. Ranges must partition the command array exactly.
                int covered = 0;
                foreach (BatchDrawRange range in ranges)
                {
                    Assert.AreEqual((uint)covered, range.drawCommandsBegin,
                        "ranges must partition the command array contiguously and in emission order.");
                    Assert.IsTrue(range.filterSettings.receiveShadows, "every map draw range receives shadows.");
                    for (uint c = 0; c < range.drawCommandsCount; c++)
                    {
                        int slot = r.MaterialIndexAtSorted(emit[covered + (int)c]);
                        Assert.AreEqual(Declared[slot], range.filterSettings.shadowCastingMode,
                            $"BRG backend must carry the DECLARED cast mode for slot {slot}, not one it " +
                            "re-derived from the layer kind.");
                    }
                    covered += (int)range.drawCommandsCount;
                }
                Assert.AreEqual(emit.Count, covered, "the ranges must cover every emitted command exactly once.");
            }
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        [Test]
        public void BrgBackend_LightView_EmitsOnlyTheCasterSlots()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            BrgTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new BrgTileRenderer(MaterialsOf(set), Declared);
                double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
                    meshes.Add(mesh);
                    r.AddTileLayer(mesh, origin, i, Tile);
                }
                r.Rebuild(SceneFrame.Mercator(double2.zero));

                var emit = new List<int>();
                r.ComputeEmitOrder(emit, BatchCullingViewType.Light);
                var castSlots = new List<int>();
                foreach (int sortedIndex in emit) castSlots.Add(r.MaterialIndexAtSorted(sortedIndex));
                CollectionAssert.AreEqual(new[] { 0, 2 }, castSlots,
                    "a LIGHT view must emit ONLY the slots declared On — 'only fill-extrusion casts' has to " +
                    "hold in code we own, not by trusting the engine to filter our range settings.");

                r.ComputeEmitOrder(emit, BatchCullingViewType.Camera);
                Assert.AreEqual(3, emit.Count, "a camera view still emits every slot, casters or not.");
            }
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        // ── Tooth 2 — DERIVATION ──────────────────────────────────────────────────────────────────

        [Test]
        public void LayerShadowModes_DeclaresOn_AtExactlyTheFillExtrusionSlot()
        {
            var set = new RenderLayerSet();
            try
            {
                set.Build(StyleParser.Parse(AllKindsStyleJson), 0.0, MapMaterialSetTestUtil.Load());
                Assert.AreEqual(5, set.Count,
                    "fixture: background, fill, line, fill-extrusion and symbol must each take a slot.");

                List<ShadowCastingMode> modes = TileManager.LayerShadowModes(set);

                for (int i = 0; i < set.Count; i++)
                {
                    bool isExtrusion = set[i] is FillExtrusionRenderLayer;
                    Assert.AreEqual(
                        isExtrusion ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        modes[i],
                        $"slot {i} ('{set[i].StyleLayer?.Id}'): only fill-extrusion casts — ground-draped " +
                        "kinds are coplanar (acne, no benefit) and symbols are billboards.");
                }
            }
            finally { set.Dispose(); }
        }
    }
}
