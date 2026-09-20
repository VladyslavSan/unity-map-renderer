// Unity EditMode only — constructs the three real tile-render backends. NOT included in Tools/core-tests.
//
// The per-slot DRAW gate (ITileRenderBackend.SetLayerVisible). A layer that paints nothing the framebuffer
// can show must submit no draw item at all, so it costs no vertex stage and no draw call — not merely no
// fragment.
//
// Each backend is asserted on its OWN mechanism (BRG drops the slot from the emit order, GameObjects
// disables the renderer, Entities adds DisableRendering), because a shared read would go green on a gate
// that reached the list and nothing else. Three fill layers, and slot 1 is the gated one — a middle slot,
// so a gate that dropped the first or last item by an off-by-one reads differently from a gate that
// selected slot 1.

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
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests.Rendering
{
    /// <summary>
    /// A gated-out layer SLOT submits no draw item, in all three
    /// <see cref="ITileRenderBackend"/> implementations — and an item added while the slot is already gated
    /// is gated too.
    /// </summary>
    [TestFixture]
    public class BackendDrawGateTests
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
            RenderLayerSet set = ThreeFillLayerSet();
            BrgTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new BrgTileRenderer(MaterialsOf(set), AllCast);
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
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
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
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
            RenderLayerSet set = ThreeFillLayerSet();
            BrgTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new BrgTileRenderer(MaterialsOf(set), AllCast);
                r.SetLayerVisible(GatedSlot, false); // gate FIRST, then register
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
                    meshes.Add(mesh);
                    r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }
                r.Rebuild(SceneFrame.Mercator(double2.zero));

                CollectionAssert.AreEqual(new[] { 0, 2 }, EmittedSlots(r, BatchCullingViewType.Camera),
                    "a tile that finishes building while its layer is gated out must not draw — that is " +
                    "the ordinary case, since tiles keep loading across a zoom bound.");
            }
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        // ── GameObjects ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void GameObjectBackend_GatedSlot_DisablesOnlyThatSlotsRenderer()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            GameObjectTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new GameObjectTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
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
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        /// <summary>
        /// The GameObject backend pools its layer children and enables the renderer per rent, so a fresh
        /// item lands drawn unless <c>AddTileLayer</c> consults the gate. Registering into an already-gated
        /// slot is the ordinary case: tiles keep finishing while a layer is out of its zoom range.
        /// </summary>
        [Test]
        public void GameObjectBackend_ItemAddedIntoAGatedSlot_IsNotDrawn()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            GameObjectTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new GameObjectTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                r.SetLayerVisible(GatedSlot, false); // gate FIRST, then register
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
                    meshes.Add(mesh);
                    handles[i] = r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }

                for (int i = 0; i < 3; i++)
                    Assert.AreEqual(i != GatedSlot, r.IsItemDrawn(handles[i]),
                        $"slot {i}: an item registered into a gated slot must arrive NOT drawn.");
            }
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        // ── Entities ──────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EntitiesBackend_GatedSlot_DisablesRenderingOnOnlyThatSlot()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            EntitiesTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new EntitiesTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
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
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        /// <summary>
        /// Both layer prototypes are built without <see cref="DisableRendering"/>, so a fresh instance
        /// arrives drawn unless <c>AddTileLayer</c> consults the gate.
        /// </summary>
        [Test]
        public void EntitiesBackend_ItemAddedIntoAGatedSlot_IsNotDrawn()
        {
            RenderLayerSet set = ThreeFillLayerSet();
            EntitiesTileRenderer r = null;
            var meshes = new List<Mesh>();
            try
            {
                r = new EntitiesTileRenderer(MaterialsOf(set), LayerNames, AllCast);
                r.SetLayerVisible(GatedSlot, false); // gate FIRST, then register
                var handles = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    var mesh = new Mesh();
                    meshes.Add(mesh);
                    handles[i] = r.AddTileLayer(mesh, TileOrigin(), i, Tile);
                }

                for (int i = 0; i < 3; i++)
                    Assert.AreEqual(i != GatedSlot, r.IsItemDrawn(handles[i]),
                        $"slot {i}: an item registered into a gated slot must arrive NOT drawn.");
            }
            finally
            {
                r?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }
    }
}
