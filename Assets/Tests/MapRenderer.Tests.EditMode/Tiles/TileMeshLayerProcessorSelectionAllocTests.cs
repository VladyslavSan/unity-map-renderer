// Unity EditMode only: Is.Not.AllocatingGCMemory() is the only trustworthy allocation meter on Unity Mono.
// Pins that ProcessOnWorker's feature selection appends into the build's pooled TileBuildBuffers.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Zero-alloc tooth for <see cref="TileMeshLayerProcessor.ProcessOnWorker"/>'s feature-selection step,
    /// isolated from the geometry-write path so a residual there cannot mask (or be mistaken for) this one.
    /// </summary>
    [TestFixture]
    public class TileMeshLayerProcessorSelectionAllocTests
    {
        private static readonly TileId Tile = new TileId { Z = 4, X = 2, Y = 3 };
        private const double Zoom = 4.0;

        /// <summary>
        /// Builds an <see cref="MvtLayer"/> of <paramref name="count"/> point features and NO adopted
        /// <see cref="MvtLayer.Geometry"/> (stays <c>default</c>/<c>IsCreated == false</c>), so
        /// <c>ProcessOnWorker</c>'s <c>geometry.IsCreated</c> gate skips <c>BuildGraphRequest</c> entirely and only the
        /// selection step (this tooth's fence) runs, not the fill/line geometry pipeline (measured
        /// elsewhere, and already zero-alloc).
        /// </summary>
        private static MvtLayer MakeSourceLayer(string name, int count)
        {
            var layer = new MvtLayer { Name = name, Extent = 4096, Version = 2 };
            for (int i = 0; i < count; i++)
                layer.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Point });
            return layer;
        }

        private static StyleLayer SelectAllLayer(string id, string sourceLayer) => new StyleLayer
        {
            Id          = id,
            Source      = "s",
            SourceLayer = sourceLayer,
            // Filter left null ⇒ CompiledFilter's shared match-all sentinel — every feature is selected,
            // exercising the selection loop over its full declared count rather than short-circuiting.
        };

        /// <summary>Never invoked in this file (every fixture layer leaves <c>Geometry.IsCreated</c> false),
        /// but the interface requires a full implementation; a call would be a test bug, so it fails loudly.</summary>
        private sealed class UnreachedRenderLayer : ITileMeshRenderLayer
        {
            public UnreachedRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public StyleLayer       StyleLayer      { get; }
            public RenderLayerBuild Build           => RenderLayerBuild.TileMesh;
            public DrawPersistence  Persistence     => DrawPersistence.Persistent;
            public int              DrawIndex       => 0;
            public LayerSubSlot     MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material         Material        => null;
            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public int TransitioningCount => 0;
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
            {
                Assert.Fail("BuildGraphRequest must not be reached — this fixture's layers carry no adopted " +
                            "geometry (IsCreated == false), by design, to isolate the selection step.");
                return null;
            }
        }

        /// <summary>
        /// Non-vacuity + correctness precondition for the new pooled-buffers overload, checked OUTSIDE the
        /// measured region: a null (match-all) filter over N features selects exactly N, in declared order.
        /// </summary>
        [Test]
        public void SelectFeatures_ScratchOverload_SelectsEveryFeature_InDeclaredOrder()
        {
            MvtLayer roads = MakeSourceLayer("roads", 30);
            StyleLayer styleLayer = SelectAllLayer("roads-all", "roads");
            var buffers = new TileBuildBuffers();

            SelectedTileFeature[] buffer = buffers.SelectionBuffer(roads.Features.Count);
            int selectedCount = FeatureSelector.SelectFeatures(styleLayer, roads, Zoom, buffer);

            Assert.AreEqual(30, selectedCount, "a null filter must select every feature.");
            for (int i = 0; i < selectedCount; i++)
                Assert.AreEqual(i, buffer[i].Ordinal, $"declared order must be preserved at slot {i}.");
        }

        /// <summary>
        /// A warmed <see cref="TileMeshLayerProcessor.ProcessOnWorker"/>, run across TWO style layers that
        /// resolve to different-sized source layers with a REAL <see cref="TileBuildBuffers"/>, allocates zero
        /// managed bytes. A per-call <c>new List&lt;SelectedTileFeature&gt;()</c> would red this test alone.
        /// </summary>
        [Test]
        public void ProcessOnWorker_OverTwoLayers_WithPooledBuffers_AllocatesNoGCMemory()
        {
            MvtLayer roads  = MakeSourceLayer("roads", 30);
            MvtLayer places = MakeSourceLayer("places", 45);
            var tile = new MvtTile();
            tile.Layers.Add(roads);
            tile.Layers.Add(places);

            var roadsLayer  = SelectAllLayer("roads-all", "roads");
            var placesLayer = SelectAllLayer("places-all", "places");

            TileMeshLayerProcessor roadsProcessor  = TileMeshLayerProcessor.AllocateForKick(new UnreachedRenderLayer(roadsLayer),  materialIndex: 0);
            TileMeshLayerProcessor placesProcessor = TileMeshLayerProcessor.AllocateForKick(new UnreachedRenderLayer(placesLayer), materialIndex: 1);

            // ONE buffers instance for the whole test, exactly as ONE TileBuildBuffers is rented for the
            // whole duration of a real worker pass and reused sequentially across every layer it processes.
            var buffers = new TileBuildBuffers();
            var context = new TileLayerProcessContext
            {
                Tile             = Tile,
                Zoom             = Zoom,
                TileOriginRender = double3.zero,
                Projection       = null, // never read — BuildGraphRequest is unreachable in this fixture
                Buffers          = buffers,
            };

            try
            {
                void RunBothLayers()
                {
                    roadsProcessor.ProcessOnWorker(tile, in context);
                    placesProcessor.ProcessOnWorker(tile, in context);
                }

                // Warm the same delegate the constraint measures: JIT and the buffers' one-time grow happen
                // here, outside the measured region, so they cannot read as false positives.
                for (int i = 0; i < 8; i++) RunBothLayers();

                Assert.That(RunBothLayers, Is.Not.AllocatingGCMemory(),
                    "ProcessOnWorker must not allocate managed memory for feature selection once it appends " +
                    "into the pooled TileBuildBuffers instead of `new List<SelectedTileFeature>()`.");
            }
            finally
            {
                roadsProcessor.Release();
                placesProcessor.Release();
                tile.Dispose();
            }
        }
    }
}
