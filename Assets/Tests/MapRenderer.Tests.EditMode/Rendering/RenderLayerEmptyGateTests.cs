// Unity EditMode only — Materials/MapMaterialSet, real render layers. NOT included in Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Rendering
{
    /// <summary>
    /// T6 (the per-layer build-object stage): the relocated emptiness gate — each
    /// <see cref="ITileMeshRenderLayer.BuildGraphRequest"/> now decides "nothing to build" itself (job-
    /// scheduling-design.md §3.2's <c>HasWork</c>, moved out of <c>TileMeshLayerProcessor</c> and up into the
    /// three render layers) — must match <c>HasWork</c>'s old semantics exactly: <c>null</c> for an empty
    /// selection, a rented build for a real one. Calls <c>BuildGraphRequest</c> DIRECTLY on each of the three
    /// production layers (NIT 7) — not through the pipeline, which already gates emptiness upstream
    /// (<c>TileMeshLayerProcessor.cs</c>'s own <c>selected.Count &gt; 0</c> / <c>geometry.IsCreated</c>
    /// checks) and would make every arm but this one's direct call vacuous.
    /// </summary>
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
        private static InMemoryTileFeature SquareFeature() => new InMemoryTileFeature
        {
            GeometryType = TileGeometryType.Polygon,
            Geometry = new uint[]
            {
                (1u << 3) | 1u, 200u, 200u,       // MoveTo (100, 100)
                (3u << 3) | 2u, 600u, 0u,         // LineTo +300, 0
                                0u,   600u,       // LineTo 0, +300
                                599u, 0u,         // LineTo -300, 0
                (1u << 3) | 7u,                   // ClosePath
            },
        };

        /// <summary>A real two-point LineString for the line arm.</summary>
        private static InMemoryTileFeature LineFeature() => new InMemoryTileFeature
        {
            GeometryType = TileGeometryType.LineString,
            Geometry = new uint[]
            {
                (1u << 3) | 1u, ZigZag(100), ZigZag(100), // MoveTo (100, 100)
                (1u << 3) | 2u, ZigZag(2000), ZigZag(0),  // LineTo (2100, 100) — ONE LineTo pair (count=1)
            },
        };

        private static uint ZigZag(long n) => (uint)((n << 1) ^ (n >> 63));

        private static void AssertEmptyThenRealGate(ITileMeshRenderLayer layer, InMemoryTileFeature feature)
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
}
