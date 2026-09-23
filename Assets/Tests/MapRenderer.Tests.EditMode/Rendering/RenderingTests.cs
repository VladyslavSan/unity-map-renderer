// Rendering/RenderingTests.cs — the three tile-render backends, the render-layer model, and the
// style-load compatibility summary. Unity EditMode only (constructs real backends/materials); not in
// Tools/core-tests.
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

using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Map;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Rendering
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // BackendDrawGateTests — a gated-out layer slot submits no draw item, in every backend
    // ───────────────────────────────────────────────────────────────────────────────────

    // The per-slot DRAW gate (ITileRenderBackend.SetLayerVisible). A layer that paints nothing the framebuffer
    // can show must submit no draw item at all, so it costs no vertex stage and no draw call — not merely no
    // fragment.
    //
    // Each backend is asserted on its OWN mechanism (BRG drops the slot from the emit order, GameObjects
    // disables the renderer, Entities adds DisableRendering), because a shared read would go green on a gate
    // that reached the list and nothing else. Three fill layers, and slot 1 is the gated one — a middle slot,
    // so a gate that dropped the first or last item by an off-by-one reads differently from a gate that
    // selected slot 1.
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
            set.Build(StyleParser.Parse(ThreeFillsStyleJson), 0.0, MapMaterialSetTestUtil.Load());
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

        /// <summary>
        /// A gated slot is absent from the emit order in BOTH views. The light view is asserted separately
        /// because that is where a gated building would otherwise keep casting a shadow with nothing above
        /// it — the one visible artefact a camera-only gate would leave behind.
        /// </summary>
        [Test]
        public void BrgBackend_GatedSlot_EmitsNoDrawCommand_InEitherView()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
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
        /// BRG reads the gate while it computes the emit order, so an item registered into an already-gated
        /// slot is covered by the same read — pinned here so the three backends are asserted on the same
        /// property and not only on the two whose mechanism is per-item.
        /// </summary>
        [Test]
        public void BrgBackend_ItemAddedIntoAGatedSlot_EmitsNoDrawCommand()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
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

        // ── GameObjects ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void GameObjectBackend_GatedSlot_DisablesOnlyThatSlotsRenderer()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
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

        /// <summary>
        /// The GameObject backend pools its layer children and enables the renderer per rent, so a fresh
        /// item lands drawn unless <c>AddTileLayer</c> consults the gate. Registering into an already-gated
        /// slot is the ordinary case: tiles keep finishing while a layer is out of its zoom range.
        /// </summary>
        [Test]
        public void GameObjectBackend_ItemAddedIntoAGatedSlot_IsNotDrawn()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
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

        // ── Entities ──────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EntitiesBackend_GatedSlot_DisablesRenderingOnOnlyThatSlot()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
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

        /// <summary>
        /// Both layer prototypes are built without <see cref="DisableRendering"/>, so a fresh instance
        /// arrives drawn unless <c>AddTileLayer</c> consults the gate.
        /// </summary>
        [Test]
        public void EntitiesBackend_ItemAddedIntoAGatedSlot_IsNotDrawn()
        {
            using RenderLayerSet set = ThreeFillLayerSet();
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
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // BackendNullSlotTests — every backend tolerates a null material-list entry
    // ───────────────────────────────────────────────────────────────────────────────────

    // E1 tooth: once symbol/background layers take a draw slot with a NULL Material, the
    // full-width per-layer material list every backend indexes by materialIndex carries null entries at those
    // slots. A missed guard is a hard NRE on the first symbol/background-bearing style. This test constructs
    // each backend with a [null, realMaterial] list and proves AddTileLayer at the REAL slot (index 1) still
    // works and Rebuild/Dispose stay clean — the null slot itself is never AddTileLayer'd (the tile produce
    // path filters to ITileMeshRenderLayer slots), so this is a construction-time guard test.
    [TestFixture]
    public class BackendNullSlotTests : BaseTestFixture
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
            var style = StyleParser.Parse(OneFillStyleJson);
            ownerSet  = new RenderLayerSet();
            ownerSet.Build(style, 0.0, MapMaterialSetTestUtil.Load());
            return new List<Material> { null, ownerSet[0].Material };
        }

        [Test]
        public void Brg_NullMaterialSlot_ConstructsAndAddsAtRealSlot()
        {
            var mats = NullThenRealMaterial(out var set);
            var meshes = new List<Mesh>();
            try
            {
                using ITileRenderBackend backend = new BrgTileRenderer(mats);
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

        [Test]
        public void Entities_NullMaterialSlot_ConstructsAndAddsAtRealSlot()
        {
            var mats = NullThenRealMaterial(out var set);
            var meshes = new List<Mesh>();
            try
            {
                using ITileRenderBackend backend = new EntitiesTileRenderer(mats);
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

        [Test]
        public void GameObject_NullMaterialSlot_ConstructsAndAddsAtRealSlot()
        {
            var mats = NullThenRealMaterial(out var set);
            var meshes = new List<Mesh>();
            try
            {
                using ITileRenderBackend backend = new GameObjectTileRenderer(mats);
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

    // Two INDEPENDENT teeth for "buildings cast shadows, ground receives them", not collapsed into
    // one parity test:
    //
    //   TRANSPORT  — each backend is handed a shadow list ([On, Off, On] over three FILL layers) that no backend
    //                could reconstruct from the layer kinds, and must report exactly it back. Three backends
    //                agreeing while all reading one list would be near-vacuous: a wrong list reds nothing. A list
    //                that contradicts the kinds kills "ignores the list", "re-derives from the material" and
    //                "hard-codes a constant" in one assertion.
    //   DERIVATION — TileManager.LayerShadowModes yields On at exactly the fill-extrusion slot. Pure; touches no
    //                backend, so a backend defect cannot red it and a wrong declaration cannot hide behind one.
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // BrgTileRendererEvictionTests — an evicted item leaves no phantom draw command
    // ───────────────────────────────────────────────────────────────────────────────────

    // Regression for a latent BRG bug present since the BRG backend was introduced: a BatchDrawCommand
    // submitted with a null Mesh ("MeshID <null>") while zooming. Root cause: OnPerformCulling sized its
    // (Malloc'd, non-zeroed) draw-command output to _sortedItems.Count and skipped handles removed since the
    // last Rebuild *in place* — leaving uninitialized garbage commands. During zoom, tiles evict (RemoveItem)
    // between Rebuilds, so stale _sortedItems slots produced garbage draws. The fix compacts: ComputeEmitOrder
    // filters out removed handles, and OnPerformCulling emits/allocates EXACTLY the surviving count.
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
            var style = StyleParser.Parse(OneFillStyleJson);
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
            // Evict the middle tile but do NOT Rebuild: _sortedItems still holds h1's slot. The old
            // code emitted a command for that stale slot (uninitialized Malloc memory → BRG
            // "MeshID <null>"). The compaction fix must drop it.
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

    // T4: a style with a fill-extrusion layer (a) TryGetFetchSource returns true with its source, and
    // (b) RenderLayerFactory.Create returns a non-null IRenderLayer taking a slot. RED-verified
    // against the previous code, where both returned false/null.
    //
    // The render layer clones its OWN Map/FillExtrusion base material
    // (MapMaterialSet.FillExtrusionMaterial), not the flat FILL base fill reuses. This suite builds its own
    // synthetic settings via Shader.Find (mirroring MaterialTweakerTests' NewFill()/NewLine() precedent) so it
    // stays independent of the committed asset. The committed production (Lit) MapMaterialSet DOES carry a
    // FillExtrusionMaterial (the depth-writing render-state wiring it exercises is guarded by
    // MaterialTweakerTests.CreateFillExtrusionMaterial_AppliesElevatedContract_NotFlatPainter).
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
            IRenderLayer created = RenderLayerFactory.Create(layer, settings, 0.0, drawIndex, out _);
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerCompatibilitySummaryTests — an unpainted/unsupported layer is named, not silent
    // ───────────────────────────────────────────────────────────────────────────────────

    // Without the compatibility summary, a style layer of an unsupported kind would vanish with no
    // explanation (RenderLayerFactory.Create returns a bare null; RenderLayerSet.Build's
    // "if (layer == null) continue;" drops it on the floor). These teeth pin the style-load compatibility
    // summary that replaces that silence: every skipped layer's id, raw type and WHY, collected once per
    // Build — never per tile, never per frame — with the supported layers around it still taking their
    // slots undisturbed.
    [TestFixture]
    public class RenderLayerCompatibilitySummaryTests : BaseTestFixture
    {
        // A circle layer — the ticket's own DONE WHEN example — interleaved with two layers that DO
        // render, so the surviving slots' declared-order/DrawIndex sequence is observable across the skip.
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

        private static RenderLayerSet Build(string json)
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        /// <summary>The summary is REPLACED, not appended to, on a restyle — <see cref="RenderLayerSet.SkippedLayers"/>'s
        /// own doc promises an old style's skips "stop applying the moment a new style replaces it"; nothing
        /// else in this repo ever calls <see cref="RenderLayerSet.Build"/> twice on the same instance, so
        /// this is the only coverage of that promise. RED recipe: delete the <c>_skippedLayers.Clear()</c>
        /// line in <see cref="RenderLayerSet"/>'s private <c>ClearLayers</c> — the count becomes 2 and
        /// <c>[0]</c> is the stale <c>circle-b</c> from the first style.</summary>
        [Test]
        public void SkippedLayers_ReplacedNotAppended_OnRestyle()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);
            set.Build(StyleParser.Parse(SourcelessSymbolStyleJson), 0.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(1, set.SkippedLayers.Count, "a restyle replaces the summary — the old style's skips are gone.");
            Assert.AreEqual(LayerSkipReason.GenuinelyUnpainted, set.SkippedLayers[0].Reason);
        }

        /// <summary>The unsupported circle layer is named
        /// explicitly in the summary — id, raw type, reason — while the fill and line layers either side of
        /// it still render. RED recipe: revert <see cref="RenderLayerFactory"/>'s reason-reporting overload
        /// to the original bare-null <c>_ =&gt; null</c> arm (or otherwise stop threading
        /// <see cref="LayerSkipReason.UnsupportedKind"/> through) — this test's reason assertion fails.</summary>
        [Test]
        public void Build_CircleLayer_ReportedAsUnsupportedKind_SupportedLayersStillRender()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);

            Assert.AreEqual(2, set.Count, "fill-a and line-c must both still take a slot.");
            Assert.AreEqual(1, set.SkippedLayers.Count, "exactly circle-b is skipped.");

            SkippedLayer skip = set.SkippedLayers[0];
            Assert.AreEqual("circle-b", skip.Id, "the summary must name the skipped layer's OWN id.");
            Assert.AreEqual("circle", skip.RawType, "the summary must carry the raw type string, not just the enum.");
            Assert.AreEqual(LayerSkipReason.UnsupportedKind, skip.Reason,
                "an unsupported kind must report UnsupportedKind — not silence, and not lumped in with a " +
                "material-misconfiguration or by-design skip.");
        }

        /// <summary>Distinguishes the third reason from the other two (team-lead constraint): a source-less
        /// symbol layer has nothing to place BY DESIGN — not an unsupported kind, not a misconfiguration.
        /// RED recipe: delete the bare <c>Symbol.StyleLayer =&gt;</c> arm in <see cref="RenderLayerFactory.Create"/>
        /// — the layer falls through to the default arm and reports <see cref="LayerSkipReason.UnsupportedKind"/>
        /// instead, which this assertion catches.</summary>
        [Test]
        public void Build_SourcelessSymbolLayer_ReportedAsGenuinelyUnpainted()
        {
            using RenderLayerSet set = Build(SourcelessSymbolStyleJson);

            Assert.AreEqual(1, set.Count, "only fill-a renders.");
            Assert.AreEqual(1, set.SkippedLayers.Count);

            SkippedLayer skip = set.SkippedLayers[0];
            Assert.AreEqual("symbol-nosrc", skip.Id);
            Assert.AreEqual(LayerSkipReason.GenuinelyUnpainted, skip.Reason,
                "a source-less symbol layer is a by-design skip, not a compatibility gap.");
        }

        /// <summary>MapView.LogSkippedLayers must warn for an actual compatibility gap but stay silent for a
        /// by-design skip — the distinction the reason enum (not the log site) carries.
        /// RED recipe: remove the <c>GenuinelyUnpainted</c> filter in <see cref="MapView.LogSkippedLayers"/>
        /// — the second call below then also warns, and <see cref="LogAssert.NoUnexpectedReceived"/> fails it.</summary>
        [Test]
        public void LogSkippedLayers_WarnsForCompatibilityGap_SilentForByDesignSkip()
        {
            var unsupported = new List<SkippedLayer>
            {
                new SkippedLayer { Id = "circle-b", RawType = "circle", Reason = LayerSkipReason.UnsupportedKind },
            };
            LogAssert.Expect(LogType.Warning, new Regex("circle-b.*UnsupportedKind"));
            MapView.LogSkippedLayers(unsupported);

            var byDesignOnly = new List<SkippedLayer>
            {
                new SkippedLayer { Id = "symbol-nosrc", RawType = "symbol", Reason = LayerSkipReason.GenuinelyUnpainted },
            };
            MapView.LogSkippedLayers(byDesignOnly);
            LogAssert.NoUnexpectedReceived(); // a by-design skip must not produce ANY warning
        }

        /// <summary>A skipped layer consumes no draw slot and does not create a gap —
        /// the two rendering layers keep DrawIndex 0 and 1 (declared order, compacted), exactly as they
        /// would if circle-b were absent from the style entirely. RED recipe: increment <c>drawIndex</c> in
        /// <see cref="RenderLayerSet.Build"/>'s skipped-layer branch (as well as the real-layer branch) —
        /// line-c's DrawIndex becomes 2 instead of 1, desyncing its renderQueue slot from its Material index.</summary>
        [Test]
        public void Build_SkippedLayer_DoesNotConsumeADrawSlot_SurvivingLayersStayContiguous()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);

            Assert.AreEqual("fill-a", set[0].StyleLayer.Id);
            Assert.AreEqual(0, set[0].DrawIndex, "fill-a is the first declared layer — slot 0.");
            Assert.AreEqual("line-c", set[1].StyleLayer.Id);
            Assert.AreEqual(1, set[1].DrawIndex,
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

        // Extrusion skipped (via the material below); the OTHER layer (a real line) is present — the lone
        // survivor is the line layer, also at dense index 0. Equal SURVIVOR COUNT to the style above (1),
        // but a different (index, id) pair.
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

        /// <summary>Pins that <see cref="MapView.LayerNumbering"/> folds the actual (dense index, id) PAIRS,
        /// not merely the survivor COUNT. The two styles here produce the SAME count (1) by different skip
        /// patterns — extrusion present with the other layer skipped, versus extrusion skipped with the
        /// other layer present — so a fold that degraded to <c>layers.Count</c> would see them as identical
        /// and let the second style serve the first's stale bake. RED recipe: replace
        /// <see cref="MapView.LayerNumbering"/>'s body with <c>layers.Count.ToString()</c> — both sides then
        /// return <c>"1"</c> and this assertion fails.</summary>
        [Test]
        public void LayerNumbering_EqualSurvivorCount_DifferentSkipPattern_ProducesDifferentNumbering()
        {
            using RenderLayerSet extrusionPresent = Build(ExtrusionPresentOtherSkippedStyleJson);
            Assert.AreEqual(1, extrusionPresent.Count, "drive precondition: only the extrusion layer survives.");

            // Bag-tracked and disposed (LIFO) AFTER extrusionSkipped below — each layer's Material is a CLONE
            // of extrusionLessMaterials' base materials, never the base itself, but disposing the consumer
            // before its source is the safer order regardless.
            var extrusionLessMaterials = Track(WithoutExtrusionMaterial());
            using RenderLayerSet extrusionSkipped = new RenderLayerSet();
            extrusionSkipped.Build(StyleParser.Parse(ExtrusionSkippedOtherPresentStyleJson), 0.0, extrusionLessMaterials);
            Assert.AreEqual(1, extrusionSkipped.Count, "drive precondition: only the line layer survives.");

            Assert.AreNotEqual(
                MapView.LayerNumbering(extrusionPresent), MapView.LayerNumbering(extrusionSkipped),
                "equal survivor counts from different skip patterns must still fold to different tokens.");
        }

        // Same id SET, same count (2), different declared ORDER — the numbering fold's other blind spot: a
        // fold that folded an unordered id set (or sorted the ids "for determinism") would pass the test
        // above (one survivor each side) yet still miss this — dense POSITION is part of the cache key
        // because cached geometry is baked against a dense INDEX, not just an id.
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

        /// <summary>Pins that the fold is sensitive to dense POSITION, not just the id SET. RED recipe: fold
        /// <c>layers[li].StyleLayer?.Id</c> into a set/sorted list instead of an ordered <c>(li, id)</c> walk
        /// — both sides carry the same two ids and this assertion fails.</summary>
        [Test]
        public void LayerNumbering_SameIdSet_DifferentOrder_ProducesDifferentNumbering()
        {
            using RenderLayerSet fillThenLine = Build(FillThenLineStyleJson);
            using RenderLayerSet lineThenFill = Build(LineThenFillStyleJson);
            Assert.AreEqual(2, fillThenLine.Count);
            Assert.AreEqual(2, lineThenFill.Count);

            Assert.AreNotEqual(
                MapView.LayerNumbering(fillThenLine), MapView.LayerNumbering(lineThenFill),
                "the same two ids in a different declared order must fold to different tokens.");
        }

        // ── Layers that can never draw are never constructed ──────────────────────────────────────

        // No shipped style uses either of these, so the cases are CONSTRUCTED here rather than sampled:
        // a `visibility: none` layer, and one authored at a constant fully-transparent opacity. Both sit
        // between two layers that do render, so the surviving slots are observable across the skip.
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
            Assert.AreEqual(0, set[0].DrawIndex);
            Assert.AreEqual("line-d", set[1].StyleLayer.Id);
            Assert.AreEqual(1, set[1].DrawIndex,
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
        /// Neither never-drawn layer is a compatibility gap, so <see cref="MapView.LogSkippedLayers"/> stays
        /// silent about both. Warning here would put a line in every log for a style doing what its author
        /// asked.
        /// </summary>
        [Test]
        public void LogSkippedLayers_SilentForALayerThatCanNeverDraw()
        {
            MapView.LogSkippedLayers(new List<SkippedLayer>
            {
                new SkippedLayer { Id = "hidden-b", RawType = "fill", Reason = LayerSkipReason.Hidden },
                new SkippedLayer { Id = "clear-c", RawType = "fill", Reason = LayerSkipReason.FullyTransparent },
            });
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// A source read only by layers that can never draw is never fetched, so its tiles are never
        /// decoded either. <c>TryGetFetchSource</c> is the one registry <c>MapView.BuildSourceSpecs</c>
        /// derives from, so excluding a layer here is what stops the whole per-tile pipeline for it.
        /// </summary>
        [Test]
        public void TryGetFetchSource_RefusesALayerThatCanNeverDraw()
        {
            StyleDocument doc = StyleParser.Parse(NeverDrawnInterleavedStyleJson);
            var fetched = new List<string>();
            foreach (StyleLayer sl in doc.Layers)
                if (RenderLayerFactory.TryGetFetchSource(sl, out string sid))
                    fetched.Add(sl.Id + "=>" + sid);

            CollectionAssert.AreEquivalent(new[] { "fill-a=>s", "line-d=>s" }, fetched,
                "only the two layers that can draw may register a fetch source. Including hidden-b or " +
                "clear-c downloads and MVT-decodes tiles for a layer whose pixels can never reach the " +
                "screen — the cost the draw gate cannot reach, because it acts after the mesh exists.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerEmptyGateTests — BuildGraphRequest returns null for an empty selection
    // ───────────────────────────────────────────────────────────────────────────────────

    // T6 (the per-layer build-object stage): the relocated emptiness gate — each
    // ITileMeshRenderLayer.BuildGraphRequest now decides "nothing to build" itself (job-
    // scheduling-design.md's HasWork, moved out of TileMeshLayerProcessor and up into the
    // three render layers) — must match HasWork's old semantics exactly: null for an empty
    // selection, a rented build for a real one. Calls BuildGraphRequest DIRECTLY on each of the three
    // production layers (NIT 7) — not through the pipeline, which already gates emptiness upstream
    // (TileMeshLayerProcessor.cs's own selected.Count > 0 / geometry.IsCreated
    // checks) and would make every arm but this one's direct call vacuous.
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
            var style = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""f"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""sl"",
                                ""paint"": { ""fill-color"": ""#ff0000"" } } ]
            }");
            var created = RenderLayerFactory.Create(style.Layers[0], Settings(), 0.0, drawIndex: 0, reason: out _);
            try { AssertEmptyThenRealGate((ITileMeshRenderLayer)created, SquareFeature()); }
            finally { created?.Dispose(); }
        }

        [Test]
        public void FillExtrusionRenderLayer_EmptySelection_ReturnsNull_RealSelection_ReturnsABuild()
        {
            var style = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""fe"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""sl"",
                                ""paint"": { ""fill-extrusion-height"": 30 } } ]
            }");
            var created = RenderLayerFactory.Create(style.Layers[0], Settings(), 0.0, drawIndex: 0, reason: out _);
            try { AssertEmptyThenRealGate((ITileMeshRenderLayer)created, SquareFeature()); }
            finally { created?.Dispose(); }
        }

        /// <summary>The line arm — NIT 7's own callout: <c>TileMeshLayerProcessor.cs</c>'s
        /// <c>selected.Count &gt; 0</c>/<c>geometry.IsCreated</c> gates already make the end-to-end line path
        /// vacuous for this property, so this direct call is the ONLY place it is actually observed.</summary>
        [Test]
        public void LineRenderLayer_EmptySelection_ReturnsNull_RealSelection_ReturnsABuild()
        {
            var style = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""l"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""sl"",
                                ""paint"": { ""line-color"": ""#000000"" } } ]
            }");
            var created = RenderLayerFactory.Create(style.Layers[0], Settings(), 0.0, drawIndex: 0, reason: out _);
            try { AssertEmptyThenRealGate((ITileMeshRenderLayer)created, LineFeature()); }
            finally { created?.Dispose(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerSetTests — one ordered set over every painted kind (global numbering)
    // ───────────────────────────────────────────────────────────────────────────────────

    // E1 (the render-layer model): global numbering supersedes the older
    // "non-renderable takes no slot" contract — background/symbol now take slots too. These teeth fail if the
    // old type-bucketing (fills then lines) or a second ordering creeps back, if a genuinely unpainted type
    // (raster/unknown) takes a slot, or if a symbol/background slot's queue math desyncs the tile-mesh layers
    // shifted above it.
    //
    // E3 flip: background is now material-bearing too (a real quad + fill-base clone) — the LAST
    // null-material slot from E1/E2 is gone. Build_Materials_* and Build_QueueShift assert slot 0 like every
    // other slot; Factory_* now Dispose() the whole layer (background owns a GO + Mesh besides its material).
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

            // EVERY painted layer takes a slot — background and symbol included. Five declared
            // layers → five slots, in declared order (not bucketed by type).
            Assert.AreEqual(5, set.Count, "background/symbol/fill/line all take slots under global numbering.");

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

            // Background is a source-less per-covered-tile TileMesh layer — ViewGeometry
            // is REMOVED (design: "The RenderLayerBuild.ViewGeometry enum decision").
            Assert.AreEqual(RenderLayerBuild.TileMesh, set[0].Build, "background is TileMesh.");
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

            // renderQueue = LayerDrawOrder.QueueFor(DrawIndex, MaterialSubSlot) — DrawIndex
            // == the slot's own declared-order index, uniformly for EVERY slot (RenderLayerSet.Build
            // increments drawIndex once per slot regardless; the QUEUE WRITE is skipped only when that
            // slot's own base material is unconfigured); MaterialSubSlot is Base for background/fill/line and
            // Above for symbol-b's Material (its WorldTextMaterial), since a symbol layer's text must draw
            // over its own icon. E3 made background material-bearing too, so slot 0 (bg) now carries its own
            // queue same as every other slot — the last E1/E2 null-material slot is gone.
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

        // The world icon material's renderQueue is set at Build time, with no per-frame sync — this
        // style, with two symbol layers at distinct draw indices, pins that BOTH world materials carry
        // the correct Build-time queue with NO Tick at all.
        private const string TwoSymbolLayersStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""symbol-a"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""la"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""symbol-b"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""lb"", ""layout"": { ""text-field"": ""{NAME}"" } }
    ]
}";

        // A contract correction, NOT a re-bake. The version this replaces asserted BOTH materials
        // equal LayerDrawOrder.QueueFor(i) — i.e. it PINNED the bug (icon and text sharing one queue,
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
                    $"slot {i}'s WorldIconMaterial queue must be set directly by SymbolRenderLayer.Create, " +
                    "with NO Tick, at its own layer's Base sub-slot.");
                Assert.AreEqual(LayerDrawOrder.QueueFor(i, LayerSubSlot.Above), textQueue,
                    $"slot {i}'s WorldTextMaterial queue must be set by RenderLayerSet.Build (via Material), " +
                    "with NO Tick, at its own layer's Above sub-slot.");

                // The tooth a re-bake cannot fake: icon strictly below its own layer's text.
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

            // Background does not own a scene GameObject (its geometry is per-tile, produced
            // by BackgroundQuad and owned by the backend) — only symbol presenters still take
            // the shared-root parenting path (lazily, on first Present), so there is nothing eager left to
            // assert here for background; this test now only pins the shared root's own visibility contract.
        }

        [Test]
        public void Factory_IsTheSoleDispatchPoint_UnpaintedTypesYieldNoRenderLayer()
        {
            // Extensibility contract: RenderLayerFactory maps a StyleLayer subtype → IRenderLayer. Genuinely
            // unpainted/unsupported-for-now types (raster) map to null (no slot); background/symbol now DO
            // produce a render layer — the axis-bearing placeholders.
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
