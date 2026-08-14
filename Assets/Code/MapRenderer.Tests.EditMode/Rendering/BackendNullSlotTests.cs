// Unity EditMode only — constructs the three real tile-render backends. NOT included in Tools/core-tests.
//
// E1 tooth (design §3.3 risk 3): once symbol/background layers take a draw slot with a NULL Material, the
// full-width per-layer material list every backend indexes by materialIndex carries null entries at those
// slots. A missed guard is a hard NRE on the first symbol/background-bearing style. This test constructs
// each backend with a [null, realMaterial] list and proves AddTileLayer at the REAL slot (index 1) still
// works and Rebuild/Dispose stay clean — the null slot itself is never AddTileLayer'd (the tile produce
// path filters to ITileMeshRenderLayer slots, §6 of the E1 plan), so this is a construction-time guard test.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests.Rendering
{
    /// <summary>
    /// E1 §3.3 risk 3: all three <see cref="ITileRenderBackend"/> implementations must tolerate a null
    /// entry in the material list they are constructed with (a symbol/background slot, §3.3).
    /// </summary>
    [TestFixture]
    public class BackendNullSlotTests
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
            ITileRenderBackend backend = null;
            var meshes = new List<Mesh>();
            try
            {
                backend = new BrgTileRenderer(mats);
                var mesh = new Mesh();
                meshes.Add(mesh);
                int handle = backend.AddTileLayer(mesh, double3.zero, 1, new TileId { Z = 0, X = 0, Y = 0 });
                Assert.GreaterOrEqual(handle, 0);
                backend.Rebuild(SceneFrame.Mercator(double2.zero));
            }
            finally
            {
                backend?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        [Test]
        public void Entities_NullMaterialSlot_ConstructsAndAddsAtRealSlot()
        {
            var mats = NullThenRealMaterial(out var set);
            ITileRenderBackend backend = null;
            var meshes = new List<Mesh>();
            try
            {
                backend = new EntitiesTileRenderer(mats);
                var mesh = new Mesh();
                meshes.Add(mesh);
                int handle = backend.AddTileLayer(mesh, double3.zero, 1, new TileId { Z = 0, X = 0, Y = 0 });
                Assert.GreaterOrEqual(handle, 0);
                backend.Rebuild(SceneFrame.Mercator(double2.zero));
            }
            finally
            {
                backend?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }

        [Test]
        public void GameObject_NullMaterialSlot_ConstructsAndAddsAtRealSlot()
        {
            var mats = NullThenRealMaterial(out var set);
            ITileRenderBackend backend = null;
            var meshes = new List<Mesh>();
            try
            {
                backend = new GameObjectTileRenderer(mats);
                var mesh = new Mesh();
                meshes.Add(mesh);
                int handle = backend.AddTileLayer(mesh, double3.zero, 1, new TileId { Z = 0, X = 0, Y = 0 });
                Assert.GreaterOrEqual(handle, 0);
                backend.Rebuild(SceneFrame.Mercator(double2.zero));
            }
            finally
            {
                backend?.Dispose();
                foreach (var m in meshes) if (m != null) Object.DestroyImmediate(m);
                set.Dispose();
            }
        }
    }
}
