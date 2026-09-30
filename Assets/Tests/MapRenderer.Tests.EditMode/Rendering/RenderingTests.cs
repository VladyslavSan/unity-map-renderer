// Rendering/RenderingTests.cs — tile-render backends, the render-layer model and the compatibility summary.
// Unity EditMode only (constructs real backends/materials); not in Tools/core-tests.
//
// Contents:
//   BackendDrawGateTests                 — a gated-out layer SLOT submits no draw item in any backend.
//   BackendNullSlotTests                 — every backend tolerates a null entry in its material list
//                                           (a symbol/background slot).
//   BackendShadowModeTests               — the per-layer CastShadows declaration reaches the GPU through
//                                           all three backends; only fill-extrusion casts.
//   BrgTileRendererEvictionTests         — regression: an item removed without a Rebuild must not leave a
//                                           phantom BRG draw command.
//   FillExtrusionRenderLayerTests        — RenderLayerFactory dispatches a fill-extrusion layer to
//                                           FillExtrusionRenderLayer, cloning its own base material.
//   RenderLayerCompatibilitySummaryTests — an unsupported/by-design-skipped layer is named (id, raw type,
//                                           reason) instead of vanishing silently; survivors stay contiguous.
//   RenderLayerEmptyGateTests            — each ITileMeshRenderLayer's BuildGraphRequest returns null for
//                                           an empty selection and a real build otherwise.
//   RenderLayerSetTests                  — the unified render-layer model: one ordered set over every
//                                           painted kind, declared-order slots, queue/sub-slot math.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Jobs.Geometry;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Layers;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Map;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using FillExtrusion = MapRenderer.Unity.Style.FillExtrusion;
using Fill = MapRenderer.Unity.Style.Fill;
using Line = MapRenderer.Unity.Style.Line;
using Symbol = MapRenderer.Unity.Style.Symbol;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Rendering
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // BackendDrawGateTests — a gated-out layer slot submits no draw item, in every backend
    // ───────────────────────────────────────────────────────────────────────────────────

    // The per-slot DRAW gate (ITileRenderBackend.SetLayerVisible): a gated layer submits no draw item. Each
    // backend is read on its OWN mechanism, and the gated slot is the middle one, so an off-by-one reads red.
    [TestFixture]
    public class BackendDrawGateTests : BaseTestFixture
    {
        private const string ThreeFillsStyleJson = @"{
    ""version"": 8,
    ""name"": ""BackendDrawGateTest"",
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

        private static readonly string[] LayerNames = { "fill0", "fill1", "fill2" };

        // Every slot casts, so the LIGHT-view tooth below measures the DRAW gate and not the shadow filter
        // that already drops non-casters (BackendShadowModeTests owns that one).
        private static readonly ShadowCastingMode[] AllCast =
        {
            ShadowCastingMode.On, ShadowCastingMode.On, ShadowCastingMode.On,
        };

        private const int GatedSlot = 1;

        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        private static RenderLayerSet ThreeFillLayerSet()
        {
            var set = new RenderLayerSet();
            set.Build(TestStyle.Document(ThreeFillsStyleJson), 0.0, MapMaterialSetTestUtil.Load());
            Assert.AreEqual(3, set.Count, "fixture: the style must produce exactly three slots.");
            return set;
        }

        private static List<Material> MaterialsOf(RenderLayerSet set)
        {
            var mats = new List<Material>(set.Count);
            for (int i = 0; i < set.Count; i++) mats.Add(set[i].Material);
            return mats;
        }

        private static double3 TileOrigin() => FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();

        // ── BRG ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The layer SLOT behind each emitted draw command, in emission order.</summary>
        /// <param name="viewType">Camera or light view.</param>
        private static List<int> EmittedSlots(BrgTileRenderer r, BatchCullingViewType viewType)
        {
            var emit = new List<int>();
            r.ComputeEmitOrder(emit, viewType);
            var slots = new List<int>(emit.Count);
            foreach (int sortedIndex in emit) slots.Add(r.MaterialIndexAtSorted(sortedIndex));
            slots.Sort();
            return slots;
        }

        /// <summary>
        /// A gated slot is absent from the emit order in BOTH views, whether an item is registered before or
        /// after the slot is gated (BRG reads the gate while it computes the emit order). The light view is
        /// asserted separately: a gated building would otherwise keep casting a shadow with nothing above it.
        /// A slot that leaves and returns through <c>SetLayerMaterials</c> comes back drawn.
        /// </summary>
        [Test]
        public void BrgBackend_DrawGate_SuppressesSlot_RegardlessOfRegistrationOrder()
        {
            // ── Gate AFTER registration ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var meshes = new List<Mesh>();
                using BrgTileRenderer r = new BrgTileRenderer(MaterialsOf(set), AllCast);
                for (int i = 0; i < 3; i++)
                {
                    var mesh = Track(new Mesh());
                    meshes.Add(mesh);
                    r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }
                r.Rebuild(SceneFrame.Mercator(double2.zero));

                var emit = new List<int>();
                Assert.AreEqual(3, r.ComputeEmitOrder(emit, BatchCullingViewType.Camera),
                    "anti-vacuity: before the gate every slot must emit, or its absence proves nothing.");

                r.SetLayerVisible(GatedSlot, false);

                CollectionAssert.AreEqual(new[] { 0, 2 }, EmittedSlots(r, BatchCullingViewType.Camera),
                    "a gated slot must emit NO draw command to the camera view. Emitting it and relying on " +
                    "a fragment discard is the mechanism this replaced — it still pays the vertex stage.");
                CollectionAssert.AreEqual(new[] { 0, 2 }, EmittedSlots(r, BatchCullingViewType.Light),
                    "a gated slot must emit NO draw command to the LIGHT view either — every slot here " +
                    "declares CastShadows.On, so the shadow filter cannot be what removed it.");

                r.SetLayerVisible(GatedSlot, true);
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, EmittedSlots(r, BatchCullingViewType.Camera),
                    "lifting the gate must restore the slot. A one-way gate would strand a layer that " +
                    "zooms back into its range.");
            }

            // ── Gate BEFORE registration ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var meshes = new List<Mesh>();
                using BrgTileRenderer r = new BrgTileRenderer(MaterialsOf(set), AllCast);
                r.SetLayerVisible(GatedSlot, false); // gate FIRST, then register
                for (int i = 0; i < 3; i++)
                {
                    var mesh = Track(new Mesh());
                    meshes.Add(mesh);
                    r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }
                r.Rebuild(SceneFrame.Mercator(double2.zero));

                CollectionAssert.AreEqual(new[] { 0, 2 }, EmittedSlots(r, BatchCullingViewType.Camera),
                    "a tile that finishes building while its layer is gated out must not draw — that is " +
                    "the ordinary case, since tiles keep loading across a zoom bound.");
            }

            // ── A slot that leaves and returns through SetLayerMaterials comes back drawn ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var mats = MaterialsOf(set);
                using BrgTileRenderer r = new BrgTileRenderer(mats, AllCast);
                r.SetLayerVisible(GatedSlot, false);
                r.SetLayerMaterials(mats.GetRange(0, 1), AllCast); // the gated slot leaves
                r.SetLayerMaterials(mats, AllCast);                // ...and returns
                for (int i = 0; i < 3; i++) r.AddTileLayer(Track(new Mesh()), TileOrigin(), i, Tile);
                r.Rebuild(SceneFrame.Mercator(double2.zero));
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, EmittedSlots(r, BatchCullingViewType.Camera),
                    "the material list and the draw gate must stay the same width: a stale gate flag must " +
                    "not hide a slot that returns.");
            }
        }

        // ── GameObjects ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Only the gated slot's renderer disables, and that holds REGARDLESS of whether an item is
        /// registered before or after the slot is gated — the ordinary case, since tiles keep finishing
        /// while a layer is out of its zoom range. The backend pools its layer children and enables the
        /// renderer per rent, so a fresh item lands drawn unless <c>AddTileLayer</c> consults the gate.
        /// A slot that leaves and returns through <c>SetLayerMaterials</c> comes back drawn.
        /// </summary>
        [Test]
        public void GameObjectBackend_DrawGate_SuppressesSlot_RegardlessOfRegistrationOrder()
        {
            // ── Gate AFTER registration ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var meshes = new List<Mesh>();
                using GameObjectTileRenderer r = new GameObjectTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = Track(new Mesh());
                    meshes.Add(mesh);
                    handles[i] = r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }
                for (int i = 0; i < 3; i++)
                    Assert.IsTrue(r.IsItemDrawn(handles[i]),
                        $"anti-vacuity: slot {i} must draw before the gate is applied.");

                r.SetLayerVisible(GatedSlot, false);
                for (int i = 0; i < 3; i++)
                    Assert.AreEqual(i != GatedSlot, r.IsItemDrawn(handles[i]),
                        $"slot {i}: only the gated slot may stop drawing. Disabling every renderer would " +
                        "pass a one-slot check and blank the map.");

                r.SetLayerVisible(GatedSlot, true);
                Assert.IsTrue(r.IsItemDrawn(handles[GatedSlot]), "lifting the gate must restore the slot.");
            }

            // ── Gate BEFORE registration ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var meshes = new List<Mesh>();
                using GameObjectTileRenderer r = new GameObjectTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                r.SetLayerVisible(GatedSlot, false); // gate FIRST, then register
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = Track(new Mesh());
                    meshes.Add(mesh);
                    handles[i] = r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }

                for (int i = 0; i < 3; i++)
                    Assert.AreEqual(i != GatedSlot, r.IsItemDrawn(handles[i]),
                        $"slot {i}: an item registered into a gated slot must arrive NOT drawn.");
            }

            // ── A slot that leaves and returns through SetLayerMaterials comes back drawn ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var mats = MaterialsOf(set);
                using GameObjectTileRenderer r = new GameObjectTileRenderer(mats, LayerNames, AllCast);
                r.SetLayerVisible(GatedSlot, false);
                r.SetLayerMaterials(mats.GetRange(0, 1), AllCast); // the gated slot leaves
                r.SetLayerMaterials(mats, AllCast);                // ...and returns
                int handle = r.AddTileLayer(Track(new Mesh()), TileOrigin(), GatedSlot, Tile);
                Assert.IsTrue(r.IsItemDrawn(handle),
                    "the material list and the draw gate must stay the same width: a stale gate flag must " +
                    "not hide a slot that returns.");
            }
        }

        // ── Entities ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Only the gated slot carries <c>DisableRendering</c>, and that holds REGARDLESS of whether an
        /// item is registered before or after the slot is gated. Both layer prototypes are built without
        /// <c>DisableRendering</c>, so a fresh instance arrives drawn unless <c>AddTileLayer</c> consults
        /// the gate.
        /// A slot that leaves and returns through <c>SetLayerMaterials</c> comes back drawn.
        /// </summary>
        [Test]
        public void EntitiesBackend_DrawGate_SuppressesSlot_RegardlessOfRegistrationOrder()
        {
            // ── Gate AFTER registration ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var meshes = new List<Mesh>();
                using EntitiesTileRenderer r = new EntitiesTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = Track(new Mesh());
                    meshes.Add(mesh);
                    handles[i] = r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }
                for (int i = 0; i < 3; i++)
                    Assert.IsTrue(r.IsItemDrawn(handles[i]),
                        $"anti-vacuity: slot {i} must draw before the gate is applied.");

                r.SetLayerVisible(GatedSlot, false);
                for (int i = 0; i < 3; i++)
                    Assert.AreEqual(i != GatedSlot, r.IsItemDrawn(handles[i]),
                        $"slot {i}: only the gated slot may carry DisableRendering.");

                r.SetLayerVisible(GatedSlot, true);
                Assert.IsTrue(r.IsItemDrawn(handles[GatedSlot]),
                    "lifting the gate must REMOVE DisableRendering — an add-only gate strands a layer that " +
                    "zooms back into its range.");
            }

            // ── Gate BEFORE registration ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var meshes = new List<Mesh>();
                using EntitiesTileRenderer r = new EntitiesTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                r.SetLayerVisible(GatedSlot, false); // gate FIRST, then register
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = Track(new Mesh());
                    meshes.Add(mesh);
                    handles[i] = r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }

                for (int i = 0; i < 3; i++)
                    Assert.AreEqual(i != GatedSlot, r.IsItemDrawn(handles[i]),
                        $"slot {i}: an item registered into a gated slot must arrive NOT drawn.");
            }

            // ── A slot that leaves and returns through SetLayerMaterials comes back drawn ──
            using (RenderLayerSet set = ThreeFillLayerSet())
            {
                var mats = MaterialsOf(set);
                using EntitiesTileRenderer r = new EntitiesTileRenderer(mats, LayerNames, AllCast);
                r.SetLayerVisible(GatedSlot, false);
                r.SetLayerMaterials(mats.GetRange(0, 1), AllCast); // the gated slot leaves
                r.SetLayerMaterials(mats, AllCast);                // ...and returns
                int handle = r.AddTileLayer(Track(new Mesh()), TileOrigin(), GatedSlot, Tile);
                Assert.IsTrue(r.IsItemDrawn(handle),
                    "the material list and the draw gate must stay the same width: a stale gate flag must " +
                    "not hide a slot that returns.");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // BackendNullSlotTests — every backend tolerates a null material-list entry
    // ───────────────────────────────────────────────────────────────────────────────────

    // A backend's per-layer material list can hold a NULL entry (an unassigned base material). Each backend,
    // built with [null, realMaterial], must AddTileLayer at slot 1 and Rebuild/Dispose without an NRE.
    [TestFixture]
    internal class BackendNullSlotTests : BaseTestFixture
    {
        private const string OneFillStyleJson = @"{
    ""version"": 8,
    ""name"": ""BackendNullSlotTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": { ""fill-color"": [""rgba"",255,255,255,1] } }
    ]
}";

        private static List<Material> NullThenRealMaterial(out RenderLayerSet ownerSet)
        {
            var style = TestStyle.Document(OneFillStyleJson);
            ownerSet  = new RenderLayerSet();
            ownerSet.Build(style, 0.0, MapMaterialSetTestUtil.Load());
            return new List<Material> { null, ownerSet[0].Material };
        }

        private static IEnumerable<TestCaseData> BackendFactories()
        {
            yield return new TestCaseData((Func<List<Material>, ITileRenderBackend>)(m => new BrgTileRenderer(m)))
                .SetName("NullMaterialSlot_ConstructsAndAddsAtRealSlot(Brg)");
            yield return new TestCaseData((Func<List<Material>, ITileRenderBackend>)(m => new EntitiesTileRenderer(m)))
                .SetName("NullMaterialSlot_ConstructsAndAddsAtRealSlot(Entities)");
            yield return new TestCaseData((Func<List<Material>, ITileRenderBackend>)(m => new GameObjectTileRenderer(m)))
                .SetName("NullMaterialSlot_ConstructsAndAddsAtRealSlot(GameObject)");
        }

        [Test]
        [TestCaseSource(nameof(BackendFactories))]
        public void NullMaterialSlot_ConstructsAndAddsAtRealSlot(Func<List<Material>, ITileRenderBackend> makeBackend)
        {
            var mats = NullThenRealMaterial(out var set);
            var meshes = new List<Mesh>();
            try
            {
                using ITileRenderBackend backend = makeBackend(mats);
                var mesh = Track(new Mesh());
                meshes.Add(mesh);
                int handle = backend.AddTileLayer(mesh, double3.zero, 1, new TileId { Z = 0, X = 0, Y = 0 });
                Assert.GreaterOrEqual(handle, 0);
                backend.Rebuild(SceneFrame.Mercator(double2.zero));
            }
            finally
            {
                set.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // BackendShadowModeTests — the per-layer CastShadows declaration reaches the GPU
    // ───────────────────────────────────────────────────────────────────────────────────

    // Two INDEPENDENT teeth. TRANSPORT: each backend reports back [On, Off, On] over three FILL layers, a list
    // no backend could derive from the kinds. DERIVATION: TileManager.LayerShadowModes is On only at fill-extrusion.
    [TestFixture]
    public class BackendShadowModeTests : BaseTestFixture
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
            set.Build(TestStyle.Document(ThreeFillsStyleJson), 0.0, MapMaterialSetTestUtil.Load());
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
            using RenderLayerSet set = ThreeFillLayerSet();
            var meshes = new List<Mesh>();
            using GameObjectTileRenderer r = new GameObjectTileRenderer(MaterialsOf(set), DeclaredNames, Declared);
            double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
            for (int i = 0; i < 3; i++)
            {
                var mesh = Track(new Mesh());
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

        [Test]
        public void EntitiesBackend_TransportsTheDeclaredShadowModes_PerLayer()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
            var meshes = new List<Mesh>();
            using EntitiesTileRenderer r = new EntitiesTileRenderer(MaterialsOf(set), DeclaredNames, Declared);
            double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
            var handles = new int[3];
            for (int i = 0; i < 3; i++)
            {
                var mesh = Track(new Mesh());
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

        [Test]
        public void BrgBackend_TransportsTheDeclaredShadowModes_PerDrawRange()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
            var meshes = new List<Mesh>();
            using BrgTileRenderer r = new BrgTileRenderer(MaterialsOf(set), Declared);
            double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
            for (int i = 0; i < 3; i++)
            {
                var mesh = Track(new Mesh());
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

        [Test]
        public void BrgBackend_LightView_EmitsOnlyTheCasterSlots()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
            var meshes = new List<Mesh>();
            using BrgTileRenderer r = new BrgTileRenderer(MaterialsOf(set), Declared);
            double3 origin = FloatingOrigin.TileLocalOriginMercator(Tile).ToRenderOrigin();
            for (int i = 0; i < 3; i++)
            {
                var mesh = Track(new Mesh());
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

        // ── Tooth 2 — DERIVATION ──────────────────────────────────────────────────────────────────

        [Test]
        public void LayerShadowModes_DeclaresOn_AtExactlyTheFillExtrusionSlot()
        {
            var set = new RenderLayerSet();
            try
            {
                set.Build(TestStyle.Document(AllKindsStyleJson), 0.0, MapMaterialSetTestUtil.Load());
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // BrgTileRendererEvictionTests — an evicted item leaves no phantom draw command
    // ───────────────────────────────────────────────────────────────────────────────────

    // An item removed between Rebuilds must not leave an uninitialised BRG draw command ("MeshID <null>"):
    // ComputeEmitOrder filters removed handles, and OnPerformCulling allocates only the surviving count.
    [TestFixture]
    public class BrgTileRendererEvictionTests : BaseTestFixture
    {
        // One fill layer → BrgTileRenderer registers one layer material (materialIndex 0 valid).
        private const string OneFillStyleJson = @"{
    ""version"": 8,
    ""name"": ""BrgEvictionTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": { ""fill-color"": [""rgba"",255,255,255,1] } }
    ]
}";

        [Test]
        public void ComputeEmitOrder_RemoveItemWithoutRebuild_DropsEvictedItem()
        {
            var style = TestStyle.Document(OneFillStyleJson);
            using var set = new RenderLayerSet();
            set.Build(style, 0.0, MapMaterialSetTestUtil.Load());

            using var brg = new BrgTileRenderer(new[] { set[0].Material });
            var meshes = new List<Mesh>();
            var scratch = new List<int>();

            int h0 = brg.AddTileLayer(TrackedMesh(meshes), double3.zero, 0, new TileId { Z = 0, X = 0, Y = 0 });
            int h1 = brg.AddTileLayer(TrackedMesh(meshes), double3.zero, 0, new TileId { Z = 1, X = 0, Y = 0 });
            int h2 = brg.AddTileLayer(TrackedMesh(meshes), double3.zero, 0, new TileId { Z = 2, X = 0, Y = 0 });

            brg.Rebuild(SceneFrame.Mercator(double2.zero));
            Assert.AreEqual(3, brg.ComputeEmitOrder(scratch, BatchCullingViewType.Camera),
                "All three live items must be emitted after Rebuild.");

            // ── The zoom eviction race ──────────────────────────────────────────────────────
            // Evict the middle tile without a Rebuild: the emit must drop h1's stale slot.
            brg.RemoveItem(h1);

            Assert.AreEqual(2, brg.ComputeEmitOrder(scratch, BatchCullingViewType.Camera),
                "RemoveItem WITHOUT a Rebuild must NOT leave a phantom draw command for the evicted " +
                "tile — exactly the two surviving items must be emitted (this is the null-mesh bug).");
            Assert.AreEqual(2, brg.DrawItemCount(), "Two items remain registered.");

            // After a Rebuild the sorted list resyncs and the count is still 2.
            brg.Rebuild(SceneFrame.Mercator(double2.zero));
            Assert.AreEqual(2, brg.ComputeEmitOrder(scratch, BatchCullingViewType.Camera), "After Rebuild, two items remain.");

            // Evict the rest without a Rebuild → nothing emitted (no zero-command range / no garbage).
            brg.RemoveItem(h0);
            brg.RemoveItem(h2);
            Assert.AreEqual(0, brg.ComputeEmitOrder(scratch, BatchCullingViewType.Camera),
                "All items evicted → ComputeEmitOrder returns 0 (OnPerformCulling emits nothing).");
        }

        private Mesh TrackedMesh(List<Mesh> meshes)
        {
            var m = Track(new Mesh());
            meshes.Add(m);
            return m;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillExtrusionRenderLayerTests — factory dispatch + fetch source for fill-extrusion
    // ───────────────────────────────────────────────────────────────────────────────────

    // A fill-extrusion layer has a fetch source and a render layer that clones its OWN
    // MapMaterialSet.FillExtrusionMaterial. Settings come from Shader.Find, independent of the committed asset.
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
            StyleDocument style = TestStyle.Document(FillExtrusionStyleJson);
            Assert.AreEqual(1, style.Layers.Count);
            StyleLayer layer = style.Layers[0];
            Assert.AreEqual(StyleLayerType.FillExtrusion, layer.LayerType);

            bool result = RenderLayerFactory.TryGetFetchSource(layer, out string sourceId);

            Assert.IsTrue(result, "a fill-extrusion layer with a declared source must be an MVT-fetching kind.");
            Assert.AreEqual("s", sourceId, "the fetched source id must be the layer's declared source.");
        }

        /// <summary><see cref="FillExtrusionStyleJson"/>'s shape with an explicit constant
        /// <c>fill-extrusion-opacity</c> — the absent-opacity case is the const itself.</summary>
        private static string FillExtrusionStyleJsonWithOpacity(float opacity) => $@"{{
    ""version"": 8,
    ""name"": ""FillExtrusion"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [
        {{ ""id"": ""buildings-3d"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""buildings"",
          ""paint"": {{ ""fill-extrusion-color"": [""rgba"",120,120,120,1], ""fill-extrusion-height"": 30,
          ""fill-extrusion-opacity"": {opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)} }} }}
    ]
}}";

        /// <summary>The ONE fill-extrusion contract — depth ON, always alpha blend, keyword OFF (the
        /// fragment writes its own alpha directly). Opacity 1 is a plain overwrite through this SAME blend,
        /// so no separate opaque state exists to flip to.</summary>
        private static void AssertAlwaysBlendState(Material m, string when)
        {
            Assert.AreEqual((int)DepthWrite.On, (int)m.GetFloat(ShaderProperties.PropertyNames.ZWrite), $"{when}: fill-extrusion always writes depth.");
            Assert.AreEqual((int)CompareFunction.LessEqual, (int)m.GetFloat(ShaderProperties.PropertyNames.ZTest), $"{when}: fill-extrusion always tests LEqual.");
            Assert.IsFalse(m.IsKeywordEnabled(FillTweaker.SurfaceTypeTransparentKeyword), $"{when}: the transparent surface keyword stays OFF.");
            Assert.AreEqual((int)BlendMode.SrcAlpha, (int)m.GetFloat(ShaderProperties.PropertyNames.SrcBlend), $"{when}: fill-extrusion always blends SrcAlpha src.");
            Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, (int)m.GetFloat(ShaderProperties.PropertyNames.DstBlend), $"{when}: fill-extrusion always blends OneMinusSrcAlpha dst.");
        }

        /// <summary>Dispatch and slot at CREATE; the render state is the SAME always-blend contract before
        /// and after a restyle through several opacity values — Restyle never touches render state or
        /// <c>_BaseColor</c>, only <c>_Opacity</c> and the other paint uniforms, which genuinely EASES over
        /// <see cref="StyleTransition.Default"/> (no state to pop between, so it blends the whole way).</summary>
        [Test]
        public void Create_FillExtrusionLayer_TakingItsSlot_RestyleEasesOpacityWithNoContractChange()
        {
            StyleDocument style = TestStyle.Document(FillExtrusionStyleJson);
            var settings = SettingsWithFillExtrusionMaterial();
            StyleLayer layer = style.Layers[0];

            IRenderLayer created = RenderLayerFactory.Create(layer, settings, 0.0, out _, out _);
            try
            {
                Assert.IsNotNull(created, "a fill-extrusion layer with a configured material set must produce a render layer.");
                Assert.IsInstanceOf<FillExtrusionRenderLayer>(created, "'buildings-3d' must dispatch to FillExtrusionRenderLayer.");
                Assert.AreSame(layer, created.StyleLayer, "the render layer must reference its source StyleLayer.");
                Assert.IsInstanceOf<FillExtrusion.StyleLayer>(created.StyleLayer);
                Assert.IsInstanceOf<ITileMeshRenderLayer>(created, "fill-extrusion is a tile-mesh layer.");
                AssertAlwaysBlendState(created.Material, "at CREATE (absent opacity)");
                // MaterialFactory writes the white identity, then binds the fixture's CONSTANT
                // fill-extrusion-color over it — the bound colour must be what survives.
                Color boundColor = created.Material.GetColor(ShaderProperties.PropertyNames.BaseColor);
                Assert.AreNotEqual(Color.white, boundColor, "at CREATE: _BaseColor must be the BOUND paint colour, not the identity white.");

                var extrusionLayer = (FillExtrusionRenderLayer)created;

                // Default, not Instant: an Instant retarget re-pushes every constant binding regardless of
                // value, which would mask a stray white write here that a real transition would not.
                StyleDocument halfOpacityStyle = TestStyle.Document(FillExtrusionStyleJsonWithOpacity(0.5f));
                extrusionLayer.Restyle(halfOpacityStyle.Layers[0], StyleTransition.Default, nowSeconds: 0.0);
                extrusionLayer.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 1.0)); // past the 0.3s duration — settled
                AssertAlwaysBlendState(created.Material, "after the 0.5 transition settles");
                Assert.AreEqual(0.5f, created.Material.GetFloat(ShaderProperties.PropertyNames.Opacity), 1e-6f,
                    "after the 0.5 transition settles: _Opacity must carry the new value.");
                Assert.AreEqual(boundColor, created.Material.GetColor(ShaderProperties.PropertyNames.BaseColor),
                    "after Restyle to opacity 0.5: _BaseColor must still be the bound paint colour — Restyle " +
                    "must never write the identity white itself.");

                // A SECOND restyle, 0.5 -> 1, starting where the first left off: no separate opaque contract
                // to switch to means nothing can pop mid-ease — the blend state is unchanged, and only the
                // uniform moves. Mid-transition (0.15s into the 0.3s window) must read STRICTLY between.
                StyleDocument constantOneStyle = TestStyle.Document(FillExtrusionStyleJsonWithOpacity(1f));
                extrusionLayer.Restyle(constantOneStyle.Layers[0], StyleTransition.Default, nowSeconds: 1.0);
                extrusionLayer.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 1.15));
                AssertAlwaysBlendState(created.Material, "mid-transition from 0.5 to 1");
                float mid = created.Material.GetFloat(ShaderProperties.PropertyNames.Opacity);
                Assert.Greater(mid, 0.5f, "mid-transition: _Opacity must have moved past 0.5.");
                Assert.Less(mid, 1f, "mid-transition: _Opacity must not yet be at the target 1 — a real ease, not a pop.");
                Assert.AreEqual(boundColor, created.Material.GetColor(ShaderProperties.PropertyNames.BaseColor),
                    "mid-transition: _BaseColor must still be the bound paint colour.");

                extrusionLayer.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 2.0)); // past the 1.0-1.3s window — settled
                AssertAlwaysBlendState(created.Material, "after the 1.0 transition settles");
                Assert.AreEqual(1f, created.Material.GetFloat(ShaderProperties.PropertyNames.Opacity), 1e-6f,
                    "after the 1.0 transition settles: _Opacity must carry the target value.");
                Assert.AreEqual(boundColor, created.Material.GetColor(ShaderProperties.PropertyNames.BaseColor),
                    "after the 1.0 transition settles: _BaseColor must still be the bound paint colour.");
            }
            finally
            {
                created?.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerCompatibilitySummaryTests — an unpainted/unsupported layer is named, not silent
    // ───────────────────────────────────────────────────────────────────────────────────

    // Every skipped layer's id, raw type and reason is collected once per Build, not per tile or frame,
    // and the supported layers around it keep their slots.
    [TestFixture]
    public class RenderLayerCompatibilitySummaryTests : BaseTestFixture
    {
        // A circle layer — the ticket's own DONE WHEN example — interleaved with two layers that DO
        // render, so the surviving slots' queue bands are observable across the skip.
        private const string CircleInterleavedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"",   ""type"": ""fill"",   ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""circle-b"", ""type"": ""circle"", ""source"": ""s"", ""source-layer"": ""b"" },
        { ""id"": ""line-c"",   ""type"": ""line"",   ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1] } }
    ]
}";

        // A symbol layer with NO "source" key — the by-design (not unsupported) skip case.
        private const string SourcelessSymbolStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"",    ""type"": ""fill"",   ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""symbol-nosrc"", ""type"": ""symbol"", ""layout"": { ""text-field"": ""{NAME}"" } }
    ]
}";

        // Same three layers/ids as UnsupportedFilterInterleavedStyleJson below, but fill-b's filter still
        // compiles — the "before" half of a restyle that turns it unsupported.
        private const string ValidFilterInterleavedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""fill-b"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"",
          ""filter"": [""=="", ""class"", ""x""],
          ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } },
        { ""id"": ""line-c"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1] } }
    ]
}";

        // "within" is a real MapLibre expression operator (support-matrix: not supported). A fill layer
        // between two valid ones, so the survivors' contiguity is observable across the skip — same shape
        // as CircleInterleavedStyleJson.
        private const string UnsupportedFilterInterleavedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""fill-b"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"",
          ""filter"": [""within"", {""type"": ""Polygon"", ""coordinates"": [[[0,0],[1,0],[1,1],[0,0]]]}],
          ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } },
        { ""id"": ""line-c"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1] } }
    ]
}";

        // Same unsupported filter, on a symbol layer between two symbol layers that DO label.
        private const string UnsupportedFilterInterleavedSymbolStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""symbol-a"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""a"", ""layout"": { ""text-field"": ""{name}"" } },
        { ""id"": ""symbol-b"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""b"",
          ""filter"": [""within"", {""type"": ""Polygon"", ""coordinates"": [[[0,0],[1,0],[1,1],[0,0]]]}],
          ""layout"": { ""text-field"": ""{name}"" } },
        { ""id"": ""symbol-c"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""c"", ""layout"": { ""text-field"": ""{name}"" } }
    ]
}";

        private static RenderLayerSet Build(string json)
        {
            var set = new RenderLayerSet();
            set.Build(TestStyle.Document(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        /// <summary>The summary is REPLACED, not appended to, on a restyle, as
        /// <see cref="RenderLayerSet.SkippedLayers"/> promises. Nothing else in the repo calls
        /// <see cref="RenderLayerSet.Build"/> twice on one instance.</summary>
        [Test]
        public void SkippedLayers_ReplacedNotAppended_OnRestyle()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);
            set.Build(TestStyle.Document(SourcelessSymbolStyleJson), 0.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(1, set.SkippedLayers.Count, "a restyle replaces the summary — the old style's skips are gone.");
            Assert.AreEqual(LayerSkipReason.GenuinelyUnpainted, set.SkippedLayers[0].Reason);
        }

        private static SkippedLayer AssertSkippedLayerReportsExpectedReason(
            string styleJson, string[] expectedSurvivorIds, string skippedId, string expectedRawType,
            LayerSkipReason expectedReason)
        {
            using RenderLayerSet set = Build(styleJson);

            Assert.AreEqual(expectedSurvivorIds.Length, set.Count,
                "exactly the expected survivors must take a slot.");
            for (int i = 0; i < expectedSurvivorIds.Length; i++)
                Assert.AreEqual(expectedSurvivorIds[i], set[i].StyleLayer.Id,
                    $"slot {i} must hold '{expectedSurvivorIds[i]}' — surviving layers stay contiguous " +
                    "across the skip, exactly as if the skipped layer were absent from the style entirely.");

            Assert.AreEqual(1, set.SkippedLayers.Count, "exactly one layer is skipped.");
            SkippedLayer skip = set.SkippedLayers[0];
            Assert.AreEqual(skippedId, skip.Id, "the summary must name the skipped layer's OWN id.");
            Assert.AreEqual(expectedRawType, skip.RawType, "the summary must carry the raw type string, not just the enum.");
            Assert.AreEqual(expectedReason, skip.Reason,
                "the reason must match the layer's actual incompatibility — not silence, and not lumped in " +
                "with a different skip reason.");
            return skip;
        }

        /// <summary>An unsupported kind (circle) reports its reason with NO detail —
        /// <see cref="RenderLayerFactory.Create"/> only sets <c>detail</c> for an unsupported filter
        /// (RenderLayerFactory.cs:30). RED recipe: revert the bare default arm in
        /// <see cref="RenderLayerFactory.Create"/> — the reason assertion fails.</summary>
        [Test]
        public void Build_SkippedLayer_UnsupportedKind_ReportsNoDetail()
        {
            SkippedLayer skip = AssertSkippedLayerReportsExpectedReason(
                CircleInterleavedStyleJson, new[] { "fill-a", "line-c" }, "circle-b", "circle",
                LayerSkipReason.UnsupportedKind);
            Assert.IsNull(skip.Detail, "an unsupported-kind skip must carry no detail string.");
        }

        /// <summary>A by-design skip (source-less symbol) reports its reason with NO detail. RED recipe:
        /// delete the bare <c>Symbol.StyleLayer =&gt;</c> arm in <see cref="RenderLayerFactory.Create"/> —
        /// the layer falls through to the unsupported-kind arm and the reason assertion fails.</summary>
        [Test]
        public void Build_SkippedLayer_GenuinelyUnpainted_ReportsNoDetail()
        {
            SkippedLayer skip = AssertSkippedLayerReportsExpectedReason(
                SourcelessSymbolStyleJson, new[] { "fill-a" }, "symbol-nosrc", "symbol",
                LayerSkipReason.GenuinelyUnpainted);
            Assert.IsNull(skip.Detail, "a by-design skip must carry no detail string.");
        }

        /// <summary>An unsupported filter operator, on both a fill and a symbol layer, names WHICH operator
        /// failed in its detail — the same detail the old per-tile log carried. RED recipe: revert the
        /// filter-compile check in <see cref="RenderLayerFactory.Create"/> — that row's detail assertion
        /// fails.</summary>
        [Test]
        public void Build_SkippedLayer_UnsupportedFilter_Fill_ReportsDetail()
        {
            SkippedLayer skip = AssertSkippedLayerReportsExpectedReason(
                UnsupportedFilterInterleavedStyleJson, new[] { "fill-a", "line-c" }, "fill-b", "fill",
                LayerSkipReason.UnsupportedFilter);
            StringAssert.Contains("within", skip.Detail, "an unsupported-filter skip must name WHICH operator failed.");
        }

        [Test]
        public void Build_SkippedLayer_UnsupportedFilter_Symbol_ReportsDetail()
        {
            SkippedLayer skip = AssertSkippedLayerReportsExpectedReason(
                UnsupportedFilterInterleavedSymbolStyleJson, new[] { "symbol-a", "symbol-c" }, "symbol-b", "symbol",
                LayerSkipReason.UnsupportedFilter);
            StringAssert.Contains("within", skip.Detail, "an unsupported-filter skip must name WHICH operator failed.");
        }

        /// <summary>UMR-223, through the real <see cref="MapView.SetStyle(StyleDocument,string,System.Threading.CancellationToken)"/>
        /// path: a filter change is never an in-place restyle (<see cref="SurvivingLayerGate"/> refuses it —
        /// <c>filter</c> sits inside the compared signature), so this always takes the full-rebuild arm. When
        /// fill-b's filter turns unsupported, the rebuild must skip ONLY fill-b; fill-a and line-c keep their
        /// slots. RED recipe: same as
        /// <see cref="Build_SkippedLayer_UnsupportedFilter_Fill_ReportsDetail"/>.</summary>
        [Test]
        public void SetStyle_FullRebuild_FilterTurnsUnsupported_OnlyThatLayerIsSkipped()
        {
            MapViewComponent view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out GameObject go);
            Track(go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(TestStyle.Document(ValidFilterInterleavedStyleJson), "v1"));
                Assert.AreEqual(3, view.View.Layers.Count, "all three layers must render while every filter compiles.");

                RestyleHarness.SpinToCompleted(view.SetStyle(TestStyle.Document(UnsupportedFilterInterleavedStyleJson), "v2"));

                Assert.AreEqual(2, view.View.Layers.Count, "fill-a and line-c must both still take a slot after the rebuild.");
                Assert.AreEqual("fill-a", view.View.Layers[0].StyleLayer.Id);
                Assert.AreEqual("line-c", view.View.Layers[1].StyleLayer.Id);

                Assert.AreEqual(1, view.View.Layers.SkippedLayers.Count, "exactly fill-b is skipped.");
                SkippedLayer skip = view.View.Layers.SkippedLayers[0];
                Assert.AreEqual("fill-b", skip.Id);
                Assert.AreEqual(LayerSkipReason.UnsupportedFilter, skip.Reason);
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>An unsupported filter is a real compatibility gap, end-to-end through the real factory —
        /// so the DETAIL a caller sees in the log is the one <c>FilterCompiles</c> actually produced, not a
        /// hand-built stand-in.</summary>
        [Test]
        public void LogSkippedLayers_WarnsForUnsupportedFilter()
        {
            using RenderLayerSet set = Build(UnsupportedFilterInterleavedStyleJson);

            // Names which operator failed, not just that one did — the detail the old per-tile log carried.
            LogAssert.Expect(LogType.Warning, new Regex("fill-b.*UnsupportedFilter.*within"));
            MapView.LogSkippedLayers(set.SkippedLayers);
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// <see cref="MapView.LogSkippedLayers"/> warns for a real compatibility gap (an unsupported kind)
        /// but stays silent for a by-design skip (a source-less symbol, <c>visibility: none</c>, or a
        /// constant fully-transparent layer) — the distinction the reason ENUM carries, not the log site.
        /// RED recipe: remove a reason from the warn set in <see cref="MapView.LogSkippedLayers"/> — that
        /// row's expected warning never appears, or a silent row's <see cref="LogAssert.NoUnexpectedReceived"/>
        /// then fails it.
        /// </summary>
        private static IEnumerable<TestCaseData> LogSkippedLayersCases()
        {
            yield return new TestCaseData(
                    (object)new List<SkippedLayer> { new SkippedLayer { Id = "circle-b", RawType = "circle", Reason = LayerSkipReason.UnsupportedKind } },
                    new[] { "circle-b.*UnsupportedKind" })
                .SetName("LogSkippedLayers_WarnsOnlyForCompatibilityGaps(UnsupportedKind_Warns)");
            yield return new TestCaseData(
                    (object)new List<SkippedLayer> { new SkippedLayer { Id = "symbol-nosrc", RawType = "symbol", Reason = LayerSkipReason.GenuinelyUnpainted } },
                    Array.Empty<string>())
                .SetName("LogSkippedLayers_WarnsOnlyForCompatibilityGaps(GenuinelyUnpainted_Silent)");
            yield return new TestCaseData(
                    (object)new List<SkippedLayer>
                    {
                        new SkippedLayer { Id = "hidden-b", RawType = "fill", Reason = LayerSkipReason.Hidden },
                        new SkippedLayer { Id = "clear-c", RawType = "fill", Reason = LayerSkipReason.FullyTransparent },
                    },
                    Array.Empty<string>())
                .SetName("LogSkippedLayers_WarnsOnlyForCompatibilityGaps(HiddenAndFullyTransparent_Silent)");
        }

        // skipped is boxed as `object` (not the internal List<SkippedLayer>) because a public
        // [TestCaseSource] test method cannot name an internal type in its own signature.
        [Test]
        [TestCaseSource(nameof(LogSkippedLayersCases))]
        public void LogSkippedLayers_WarnsOnlyForCompatibilityGaps(object skipped, string[] expectedWarningRegexes)
        {
            foreach (string regex in expectedWarningRegexes) LogAssert.Expect(LogType.Warning, new Regex(regex));
            MapView.LogSkippedLayers((List<SkippedLayer>)skipped);
            LogAssert.NoUnexpectedReceived(); // a by-design skip must not produce ANY warning
        }

        /// <summary>A skipped layer consumes no draw slot and does not create a gap —
        /// the two rendering layers keep slots 0 and 1 (declared order, compacted), exactly as they
        /// would if circle-b were absent from the style entirely. RED recipe: increment <c>drawIndex</c> in
        /// <see cref="RenderLayerSet.Build"/>'s skipped-layer branch (as well as the real-layer branch) —
        /// line-c's queue becomes the slot-2 band instead of the slot-1 band.</summary>
        [Test]
        public void Build_SkippedLayer_DoesNotConsumeADrawSlot_SurvivingLayersStayContiguous()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);

            Assert.AreEqual("fill-a", set[0].StyleLayer.Id);
            Assert.AreEqual(LayerDrawOrder.QueueFor(0, set[0].MaterialSubSlot), set[0].Material.renderQueue,
                "fill-a is the first declared layer — slot 0.");
            Assert.AreEqual("line-c", set[1].StyleLayer.Id);
            Assert.AreEqual(LayerDrawOrder.QueueFor(1, set[1].MaterialSubSlot), set[1].Material.renderQueue,
                "line-c must land at slot 1 — the skipped circle-b in between must not have reserved a slot.");
        }

        /// <summary>The summary is a style-load snapshot, not a per-frame log — pumping
        /// the per-frame call (<see cref="RenderLayerSet.ApplyZoom"/>) many times over must never grow it.
        /// RED recipe: append to the skipped-layers list from ANY per-frame method instead of only from
        /// <see cref="RenderLayerSet.Build"/> — this test's repeated-count assertion catches the growth
        /// directly (no instrumentation needed).</summary>
        [Test]
        public void SkippedLayers_UnaffectedByRepeatedPerFrameCalls()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);
            int countAfterBuild = set.SkippedLayers.Count;
            Assert.AreEqual(1, countAfterBuild, "sanity: circle-b was skipped by Build.");

            for (int frame = 0; frame < 50; frame++)
                set.ApplyZoom(new StyleFrameInputs(frame * 0.1, 1.0, 0.0));

            Assert.AreEqual(countAfterBuild, set.SkippedLayers.Count,
                "50 simulated frames must not change the compatibility summary — it is bounded to style load.");
        }

        // ── The numbering fold's granularity (MapView.LayerNumbering) ──────────────────────────────────

        // Extrusion present; the OTHER layer (circle-b, an unsupported kind) is skipped — the lone survivor
        // is the extrusion layer, at dense index 0.
        private const string ExtrusionPresentOtherSkippedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""ext-layer"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-extrusion-height"": 20 } },
        { ""id"": ""circle-b"", ""type"": ""circle"", ""source"": ""s"", ""source-layer"": ""b"" }
    ]
}";

        // Extrusion skipped (via the material below): the lone survivor is the line at dense index 0 —
        // the same count as the style above, but a different (index, id) pair.
        private const string ExtrusionSkippedOtherPresentStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""ext-layer2"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-extrusion-height"": 20 } },
        { ""id"": ""line-other"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1], ""line-width"": 2 } }
    ]
}";

        /// <summary>A committed <see cref="MapMaterialSet"/> clone with <c>FillExtrusionMaterial</c> unset —
        /// the one lever this codebase has to skip an extrusion layer without touching style content
        /// (<c>FillMaterial</c>/<c>LineMaterial</c>/<c>SymbolTextWorld</c> are all <c>Validate()</c>-required,
        /// never absent).</summary>
        private static MapMaterialSet WithoutExtrusionMaterial()
        {
            var lit = MapMaterialSetTestUtil.Load();
            var set = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.FillMaterial    = lit.FillMaterial;
            set.LineMaterial    = lit.LineMaterial;
            set.SymbolTextWorld = lit.SymbolTextWorld;
            return set;
        }

        // Same id SET and count, different declared ORDER: cached geometry is baked against a dense INDEX,
        // so a fold over an unordered (or sorted) id set would miss this.
        private const string FillThenLineStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""line-b"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1], ""line-width"": 2 } }
    ]
}";

        private const string LineThenFillStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""line-b"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1], ""line-width"": 2 } },
        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } }
    ]
}";

        /// <summary>
        /// Two ways for two style builds to LOOK alike while <see cref="MapView.LayerNumbering"/> must still
        /// tell them apart: the same survivor COUNT reached by different skip patterns, and the same id SET
        /// in a different declared ORDER (cached geometry is baked against a dense index, so a fold over an
        /// unordered/sorted id set would miss this). RED recipe: fold <c>layers[li].StyleLayer?.Id</c> into a
        /// set/sorted list, or fold survivor count instead of the (dense index, id) pairs — either block's
        /// assertion then fails.
        /// </summary>
        [Test]
        public void LayerNumbering_SuperficiallySimilarSets_ProduceDifferentTokens()
        {
            // ── Equal survivor count, different skip pattern ──
            using (RenderLayerSet extrusionPresent = Build(ExtrusionPresentOtherSkippedStyleJson))
            {
                Assert.AreEqual(1, extrusionPresent.Count, "drive precondition: only the extrusion layer survives.");

                // Disposed (LIFO) AFTER extrusionSkipped below: layers clone these base materials, and the
                // consumer goes before its source.
                var extrusionLessMaterials = Track(WithoutExtrusionMaterial());
                using RenderLayerSet extrusionSkipped = new RenderLayerSet();
                extrusionSkipped.Build(TestStyle.Document(ExtrusionSkippedOtherPresentStyleJson), 0.0, extrusionLessMaterials);
                Assert.AreEqual(1, extrusionSkipped.Count, "drive precondition: only the line layer survives.");

                Assert.AreNotEqual(
                    MapView.LayerNumbering(extrusionPresent), MapView.LayerNumbering(extrusionSkipped),
                    "equal survivor counts from different skip patterns must still fold to different tokens.");
            }

            // ── Same id set, different declared order ──
            using (RenderLayerSet fillThenLine = Build(FillThenLineStyleJson))
            using (RenderLayerSet lineThenFill = Build(LineThenFillStyleJson))
            {
                Assert.AreEqual(2, fillThenLine.Count);
                Assert.AreEqual(2, lineThenFill.Count);

                Assert.AreNotEqual(
                    MapView.LayerNumbering(fillThenLine), MapView.LayerNumbering(lineThenFill),
                    "the same two ids in a different declared order must fold to different tokens.");
            }
        }

        // ── Layers that can never draw are never constructed ──────────────────────────────────────

        // CONSTRUCTED cases: a `visibility: none` layer and a constant fully-transparent one, between two
        // rendering layers so the surviving slots are observable across the skip.
        private const string NeverDrawnInterleavedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"",      ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""hidden-b"",    ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"",
          ""layout"": { ""visibility"": ""none"" }, ""paint"": { ""fill-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""clear-c"",     ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"",
          ""paint"": { ""fill-color"": [""rgba"",0,0,255,1], ""fill-opacity"": 0 } },
        { ""id"": ""line-d"",      ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""d"", ""paint"": { ""line-color"": [""rgba"",0,0,0,1] } }
    ]
}";

        /// <summary>
        /// A layer that can never draw takes no slot: <c>visibility: none</c> and a CONSTANT
        /// fully-transparent opacity are both refused at construction, and the layers around them stay
        /// contiguous — the same contract an unsupported kind already has.
        /// </summary>
        [Test]
        public void NeverDrawnLayers_AreNeverConstructed_SurvivorsStayContiguous()
        {
            using RenderLayerSet set = Build(NeverDrawnInterleavedStyleJson);

            Assert.AreEqual(2, set.Count,
                "only fill-a and line-d can ever draw. A count of 4 means a layer that paints nothing at " +
                "any zoom still owns a slot, a material and a backend registration.");
            Assert.AreEqual("fill-a", set[0].StyleLayer.Id);
            Assert.AreEqual(LayerDrawOrder.QueueFor(0, set[0].MaterialSubSlot), set[0].Material.renderQueue);
            Assert.AreEqual("line-d", set[1].StyleLayer.Id);
            Assert.AreEqual(LayerDrawOrder.QueueFor(1, set[1].MaterialSubSlot), set[1].Material.renderQueue,
                "line-d must land at slot 1 — neither skipped layer may reserve a slot.");

            var byId = new Dictionary<string, LayerSkipReason>();
            foreach (SkippedLayer sl in set.SkippedLayers) byId[sl.Id] = sl.Reason;
            Assert.AreEqual(LayerSkipReason.Hidden, byId["hidden-b"],
                "a `visibility: none` layer must be reported as Hidden, not silently dropped.");
            Assert.AreEqual(LayerSkipReason.FullyTransparent, byId["clear-c"],
                "a constant fully-transparent layer must be reported as FullyTransparent — a DIFFERENT " +
                "reason from Hidden, so the summary says which of the two the author wrote.");
        }

        /// <summary>
        /// A source read only by layers that can never draw — because their filter cannot compile, or
        /// because they can never draw at all (<c>visibility: none</c>/constant fully-transparent) — is never
        /// fetched, so its tiles are never decoded either. <c>TryGetFetchSource</c> is the one registry
        /// <c>MapView.BuildSourceSpecs</c> derives from, so excluding a layer here is what stops the whole
        /// per-tile pipeline for it.
        /// </summary>
        private static IEnumerable<TestCaseData> RefusesFetchSourceCases()
        {
            yield return new TestCaseData(UnsupportedFilterInterleavedStyleJson, new[] { "fill-a=>s", "line-c=>s" },
                    "fill-b's filter cannot compile, so it must not register 's' as a fetch source either — " +
                    "the other two layers on 's' already do, so the source is still fetched for them.")
                .SetName("TryGetFetchSource_RefusesALayerThatCanNeverDraw_ForAnyReason(UnsupportedFilter)");
            yield return new TestCaseData(NeverDrawnInterleavedStyleJson, new[] { "fill-a=>s", "line-d=>s" },
                    "only the two layers that can draw may register a fetch source. Including hidden-b or " +
                    "clear-c downloads and MVT-decodes tiles for a layer whose pixels can never reach the " +
                    "screen — the cost the draw gate cannot reach, because it acts after the mesh exists.")
                .SetName("TryGetFetchSource_RefusesALayerThatCanNeverDraw_ForAnyReason(HiddenAndFullyTransparent)");
        }

        [Test]
        [TestCaseSource(nameof(RefusesFetchSourceCases))]
        public void TryGetFetchSource_RefusesALayerThatCanNeverDraw_ForAnyReason(
            string styleJson, string[] expectedFetched, string message)
        {
            StyleDocument doc = TestStyle.Document(styleJson);
            var fetched = new List<string>();
            foreach (StyleLayer sl in doc.Layers)
                if (RenderLayerFactory.TryGetFetchSource(sl, out string sid))
                    fetched.Add(sl.Id + "=>" + sid);

            CollectionAssert.AreEquivalent(expectedFetched, fetched, message);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerEmptyGateTests — BuildGraphRequest returns null for an empty selection
    // ───────────────────────────────────────────────────────────────────────────────────

    // Each ITileMeshRenderLayer.BuildGraphRequest returns null for an empty selection and a rented build
    // otherwise. Called DIRECTLY: the pipeline gates emptiness upstream, which would make the arms vacuous.
    [TestFixture]
    public class RenderLayerEmptyGateTests
    {
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double Extent = 4096.0;

        private static TileLayerProcessContext Context() => new TileLayerProcessContext
        {
            Tile = Tile, Zoom = 0.0, TileOriginRender = double3.zero,
            Projection = new WebMercatorProjection(), BufferClip = TileBufferClip.KeepTileUnits(0.0),
        };

        private static MapMaterialSet Settings()
        {
            var settings = ScriptableObject.CreateInstance<MapMaterialSet>();
            settings.FillMaterial          = new Material(Shader.Find("Map/Fill"));
            settings.LineMaterial          = new Material(Shader.Find("Map/Line"));
            settings.FillExtrusionMaterial = new Material(Shader.Find("Map/FillExtrusion"));
            return settings;
        }

        /// <summary>A real, non-degenerate polygon feature — a 300-unit square well inside the tile — for
        /// the fill/fill-extrusion arms.</summary>
        private static DictionaryFeature SquareFeature() => new DictionaryFeature(
            properties: null,
            geometryType: TileGeometryType.Polygon,
            hasId: false,
            geometry: new uint[]
            {
                (1u << 3) | 1u, 200u, 200u,       // MoveTo (100, 100)
                (3u << 3) | 2u, 600u, 0u,         // LineTo +300, 0
                                0u,   600u,       // LineTo 0, +300
                                599u, 0u,         // LineTo -300, 0
                (1u << 3) | 7u,                   // ClosePath
            });

        /// <summary>A real two-point LineString for the line arm.</summary>
        private static DictionaryFeature LineFeature() => new DictionaryFeature(
            properties: null,
            geometryType: TileGeometryType.LineString,
            hasId: false,
            geometry: new uint[]
            {
                (1u << 3) | 1u, ZigZag(100), ZigZag(100), // MoveTo (100, 100)
                (1u << 3) | 2u, ZigZag(2000), ZigZag(0),  // LineTo (2100, 100) — ONE LineTo pair (count=1)
            });

        private static uint ZigZag(long n) => (uint)((n << 1) ^ (n >> 63));

        private static void AssertEmptyThenRealGate(ITileMeshRenderLayer layer, DictionaryFeature feature)
        {
            var features = new List<MapRenderer.Core.Expressions.IFeature> { feature };
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, Tile, Extent);
            var context = Context();
            try
            {
                ILayerMeshBuild emptyBuild = layer.BuildGraphRequest(
                    new List<SelectedTileFeature>(), geometry, in context, materialIndex: 0, payloadName: "probe");
                Assert.IsNull(emptyBuild,
                    $"{layer.GetType().Name}.BuildGraphRequest must return null for an empty selection.");

                List<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
                ILayerMeshBuild realBuild = layer.BuildGraphRequest(
                    selected, geometry, in context, materialIndex: 0, payloadName: "probe");
                Assert.IsNotNull(realBuild,
                    $"{layer.GetType().Name}.BuildGraphRequest must return a real build for a non-empty selection.");
                realBuild.Dispose();
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void FillRenderLayer_EmptySelection_ReturnsNull_RealSelection_ReturnsABuild()
        {
            var style = TestStyle.Document(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""f"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""sl"",
                                ""paint"": { ""fill-color"": ""#ff0000"" } } ]
            }");
            var created = RenderLayerFactory.Create(style.Layers[0], Settings(), 0.0, reason: out _, detail: out _);
            try { AssertEmptyThenRealGate((ITileMeshRenderLayer)created, SquareFeature()); }
            finally { created?.Dispose(); }
        }

        [Test]
        public void FillExtrusionRenderLayer_EmptySelection_ReturnsNull_RealSelection_ReturnsABuild()
        {
            var style = TestStyle.Document(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""fe"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""sl"",
                                ""paint"": { ""fill-extrusion-height"": 30 } } ]
            }");
            var created = RenderLayerFactory.Create(style.Layers[0], Settings(), 0.0, reason: out _, detail: out _);
            try { AssertEmptyThenRealGate((ITileMeshRenderLayer)created, SquareFeature()); }
            finally { created?.Dispose(); }
        }

        /// <summary>The line arm — NIT 7's own callout: <c>TileMeshLayerProcessor.cs</c>'s
        /// <c>selected.Count &gt; 0</c>/<c>geometry.IsCreated</c> gates already make the end-to-end line path
        /// vacuous for this property, so this direct call is the ONLY place it is actually observed.</summary>
        [Test]
        public void LineRenderLayer_EmptySelection_ReturnsNull_RealSelection_ReturnsABuild()
        {
            var style = TestStyle.Document(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""l"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""sl"",
                                ""paint"": { ""line-color"": ""#000000"" } } ]
            }");
            var created = RenderLayerFactory.Create(style.Layers[0], Settings(), 0.0, reason: out _, detail: out _);
            try { AssertEmptyThenRealGate((ITileMeshRenderLayer)created, LineFeature()); }
            finally { created?.Dispose(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerSetTests — one ordered set over every painted kind (global numbering)
    // ───────────────────────────────────────────────────────────────────────────────────

    // One declared-order numbering over every painted kind; background (slot 0) carries a material too.
    // These fail on type-bucketing, a slot for raster/unknown, or queue math that desyncs later slots.
    [TestFixture]
    public class RenderLayerSetTests
    {
        // background + fill + symbol + line + fill: all four painted kinds, in an order that separates
        // declared order from type-bucketed order, for both the slots and the queue shift.
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
            set.Build(TestStyle.Document(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        [Test]
        public void Build_AllFourKinds_TakeSlotsInDeclaredOrder()
        {
            using var set = Build(InterleavedStyleJson);

            // EVERY painted layer takes a slot — background and symbol included. Five declared
            // layers → five slots, in declared order (not bucketed by type).
            Assert.AreEqual(5, set.Count, "background/symbol/fill/line all take slots under global numbering.");

            Assert.IsInstanceOf<BackgroundRenderLayer>(set[0], "index 0 = declared background");
            Assert.IsInstanceOf<Fill.StyleLayer>(set[1].StyleLayer, "index 1 = declared fill-a");
            Assert.IsInstanceOf<SymbolRenderLayer>(set[2], "index 2 = declared symbol-b (BETWEEN fill and line)");
            Assert.IsInstanceOf<Line.StyleLayer>(set[3].StyleLayer, "index 3 = declared line-c");
            Assert.IsInstanceOf<Fill.StyleLayer>(set[4].StyleLayer, "index 4 = declared fill-d");
        }

        [Test]
        public void Build_TileMeshCapability_MatchesEachLayerKind()
        {
            using var set = Build(InterleavedStyleJson);

            // Only the feature-driven kinds (fill, line, fill-extrusion) implement the mesh-build
            // capability — the tile produce loop's filter (TileManager.ComputeDenseLayerIds) relies on
            // exactly this. Background is built per tile too, but source-less: TileManager kicks it by
            // its own concrete type, so it must NOT implement this interface.
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

            // renderQueue = QueueFor(declared-order index, MaterialSubSlot) for EVERY slot; the sub-slot is
            // Above for symbol-b's WorldTextMaterial, so text draws over its own icon, and Base otherwise.
            Assert.AreEqual(LayerDrawOrder.QueueFor(0, set[0].MaterialSubSlot), set[0].Material.renderQueue,
                "bg — slot 0, Base, material-bearing so it gets a queue like every other slot.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(1, set[1].MaterialSubSlot), set[1].Material.renderQueue, "fill-a — slot 1, Base.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(2, set[2].MaterialSubSlot), set[2].Material.renderQueue,
                "symbol-b — slot 2, Above (its Material IS WorldTextMaterial), material-bearing so it gets a queue like every other slot.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(3, set[3].MaterialSubSlot), set[3].Material.renderQueue, "line-c — slot 3, Base.");
            Assert.AreEqual(LayerDrawOrder.QueueFor(4, set[4].MaterialSubSlot), set[4].Material.renderQueue, "fill-d — slot 4, Base.");

            // Monotonic over EVERY material-bearing slot (background, fill, symbol, line, fill), in declared order.
            Assert.Less(set[0].Material.renderQueue, set[1].Material.renderQueue);
            Assert.Less(set[1].Material.renderQueue, set[2].Material.renderQueue);
            // Symbol-b's TEXT queue (slot 2, Above — its own layer's higher sub-slot) must still be
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

            // Background is material-bearing too (a fill-base clone, MaterialFactory
            // .CreateBackgroundMaterial) — no slot is null when the material set is configured.
            Assert.IsNotNull(set[0].Material, "bg now owns a material — BackgroundRenderLayer.Create clones MapMaterialSet.FillMaterial.");
            Assert.IsNotNull(set[1].Material);
            Assert.IsNotNull(set[2].Material, "symbol-b now owns a material — SymbolRenderLayer.Create clones MapMaterialSet.SymbolTextWorld.");
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

        // Two symbol layers at distinct draw indices: BOTH world materials carry their queue from Build
        // time, with NO Update.
        private const string TwoSymbolLayersStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""symbol-a"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""la"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""symbol-b"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""lb"", ""layout"": { ""text-field"": ""{NAME}"" } }
    ]
}";

        // Non-obvious why: icon and text are coplanar with ZWrite off, so one shared queue lets the badge paint
        // over its number. Icon sits at Base, text at Above, and icon < text is asserted explicitly.
        [Test]
        public void Build_SymbolLayers_WorldTextAndIconQueues_SetAtBuildTime_NoTickNeeded()
        {
            var settings = MapMaterialSetTestUtil.Load();
            Assert.IsNotNull(settings.SymbolIconWorld,
                "this tooth needs SymbolIconWorld assigned on the production set to observe the icon queue write.");

            using var set = new RenderLayerSet();
            set.Build(TestStyle.Document(TwoSymbolLayersStyleJson), 0.0, settings);

            Assert.AreEqual(2, set.Count, "one render layer per declared symbol layer.");
            for (int i = 0; i < set.Count; i++)
            {
                var symbolLayer = (SymbolRenderLayer)set[i];
                int iconQueue = symbolLayer.WorldIconMaterial.renderQueue;
                int textQueue = symbolLayer.WorldTextMaterial.renderQueue;
                int bandBase  = LayerDrawOrder.QueueFor(i, LayerSubSlot.Base);
                int bandTop   = bandBase + LayerDrawOrder.SubSlotsPerLayer - 1;

                Assert.AreEqual(bandBase, iconQueue,
                    $"slot {i}'s WorldIconMaterial queue must be set by RenderLayerSet.Build via SetDrawOrder, " +
                    "with NO Update, at its own layer's Base sub-slot.");
                Assert.AreEqual(LayerDrawOrder.QueueFor(i, LayerSubSlot.Above), textQueue,
                    $"slot {i}'s WorldTextMaterial queue must be set by RenderLayerSet.Build (via Material), " +
                    "with NO Update, at its own layer's Above sub-slot.");

                // The tooth a re-bake cannot fake: icon strictly below its own layer's text.
                Assert.Less(iconQueue, textQueue,
                    $"slot {i}: the icon must draw strictly BEFORE (below) its own text — otherwise the badge " +
                    "can paint over the number it frames, the exact bug this stage fixes.");
                Assert.That(iconQueue, Is.InRange(bandBase, bandTop), $"slot {i}'s icon queue must lie inside its own layer's band.");
                Assert.That(textQueue, Is.InRange(bandBase, bandTop), $"slot {i}'s text queue must lie inside its own layer's band.");
            }
        }

        // Cross-layer: every sub-slot of layer 0's band sits strictly below layer 1's band. A "text = queue + 1"
        // scheme fails here: layer 0's text and layer 1's icon collide.
        [Test]
        public void Build_SymbolLayers_Layer0Band_IsStrictlyBelow_Layer1Band()
        {
            var settings = MapMaterialSetTestUtil.Load();
            using var set = new RenderLayerSet();
            set.Build(TestStyle.Document(TwoSymbolLayersStyleJson), 0.0, settings);

            var layer0 = (SymbolRenderLayer)set[0];
            var layer1 = (SymbolRenderLayer)set[1];

            Assert.Less(layer0.WorldTextMaterial.renderQueue, layer1.WorldIconMaterial.renderQueue,
                "layer 0's text (its own band's Above sub-slot — the highest queue it owns) must be strictly " +
                "below layer 1's icon (its own band's Base sub-slot — the lowest queue it owns): a symbol " +
                "layer's text must never escape into the next layer's band.");
        }

        [Test]
        public void Factory_IsTheSoleDispatchPoint_UnpaintedTypesYieldNoRenderLayer()
        {
            // RenderLayerFactory maps a StyleLayer subtype → IRenderLayer: raster maps to null (no slot), and
            // background/symbol produce a render layer.
            StyleDocument style = TestStyle.Document(RasterAndFillStyleJson);
            var settings = MapMaterialSetTestUtil.Load();

            foreach (var sl in style.Layers)
            {
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, 0.0, out _, out _);
                if (sl.LayerType == StyleLayerType.Raster)
                {
                    Assert.IsNull(layer, $"'{sl.Id}' (raster) is genuinely unpainted — no slot.");
                    continue;
                }

                Assert.IsNotNull(layer, $"'{sl.Id}' is a renderable type and must produce an IRenderLayer.");
                Assert.AreSame(sl, layer.StyleLayer, "the render layer must reference its source StyleLayer.");
                layer.Dispose(); // no set owns it here — free it (correct for every kind; background also owns a GO+Mesh)
            }
        }

        [Test]
        public void Factory_Create_ReturnsTheAxisBearingPlaceholders_ForSymbolAndBackground()
        {
            StyleDocument style = TestStyle.Document(InterleavedStyleJson);
            var settings = MapMaterialSetTestUtil.Load();

            foreach (var sl in style.Layers)
            {
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, 0.0, out _, out _);
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
                layer?.Dispose(); // no set owns it here — free it (background also owns a GO+Mesh)
            }
        }
    }
}
