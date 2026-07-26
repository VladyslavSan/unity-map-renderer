// Unity EditMode only — exercises BrgTileRenderer (BatchRendererGroup) directly.
// NOT included in Tools/core-tests (engine-dependent: Mesh, BatchRendererGroup, materials).
//
// Regression for a latent BRG bug present since the BRG backend was introduced: a BatchDrawCommand
// submitted with a null Mesh ("MeshID <null>") while zooming. Root cause: OnPerformCulling sized its
// (Malloc'd, non-zeroed) draw-command output to _sortedItems.Count and skipped handles removed since the
// last Rebuild *in place* — leaving uninitialized garbage commands. During zoom, tiles evict (RemoveItem)
// between Rebuilds, so stale _sortedItems slots produced garbage draws. The fix compacts: ComputeEmitOrder
// filters out removed handles, and OnPerformCulling emits/allocates EXACTLY the surviving count.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;
namespace MapRenderer.Tests
{
    /// <summary>
    /// White-box test of the BRG eviction/compaction invariant (the "MeshID &lt;null&gt;" bug):
    /// a draw item removed via <see cref="BrgTileRenderer.RemoveItem"/> WITHOUT a subsequent
    /// <c>Rebuild</c> must NOT produce a draw command — <see cref="BrgTileRenderer.ComputeEmitOrder"/>
    /// (the selection feeding <c>OnPerformCulling</c>) must drop it.
    /// </summary>
    [TestFixture]
    public class BrgTileRendererEvictionTests
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
            var set   = new RenderLayerSet();
            set.Build(style, 0.0, MapMaterialSetTestUtil.Load());

            var brg    = new BrgTileRenderer(new[] { set[0].Material });
            var meshes = new List<Mesh>();
            var scratch = new List<int>();

            try
            {
                int h0 = brg.AddTileLayer(Track(meshes), double3.zero, 0, new TileId { Z = 0, X = 0, Y = 0 });
                int h1 = brg.AddTileLayer(Track(meshes), double3.zero, 0, new TileId { Z = 1, X = 0, Y = 0 });
                int h2 = brg.AddTileLayer(Track(meshes), double3.zero, 0, new TileId { Z = 2, X = 0, Y = 0 });

                brg.Rebuild(SceneFrame.Mercator(double2.zero));
                Assert.AreEqual(3, brg.ComputeEmitOrder(scratch),
                    "All three live items must be emitted after Rebuild.");

                // ── The zoom eviction race ──────────────────────────────────────────────────────
                // Evict the middle tile but do NOT Rebuild: _sortedItems still holds h1's slot. The old
                // code emitted a command for that stale slot (uninitialized Malloc memory → BRG
                // "MeshID <null>"). The compaction fix must drop it.
                brg.RemoveItem(h1);

                Assert.AreEqual(2, brg.ComputeEmitOrder(scratch),
                    "RemoveItem WITHOUT a Rebuild must NOT leave a phantom draw command for the evicted " +
                    "tile — exactly the two surviving items must be emitted (this is the null-mesh bug).");
                Assert.AreEqual(2, brg.DrawItemCount(), "Two items remain registered.");

                // After a Rebuild the sorted list resyncs and the count is still 2.
                brg.Rebuild(SceneFrame.Mercator(double2.zero));
                Assert.AreEqual(2, brg.ComputeEmitOrder(scratch), "After Rebuild, two items remain.");

                // Evict the rest without a Rebuild → nothing emitted (no zero-command range / no garbage).
                brg.RemoveItem(h0);
                brg.RemoveItem(h2);
                Assert.AreEqual(0, brg.ComputeEmitOrder(scratch),
                    "All items evicted → ComputeEmitOrder returns 0 (OnPerformCulling emits nothing).");
            }
            finally
            {
                brg.Dispose();
                for (int i = 0; i < meshes.Count; i++)
                    if (meshes[i] != null) Object.DestroyImmediate(meshes[i]);
                set.Dispose();
            }
        }

        private static Mesh Track(List<Mesh> meshes)
        {
            var m = new Mesh();
            meshes.Add(m);
            return m;
        }
    }
}
