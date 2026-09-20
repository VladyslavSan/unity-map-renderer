// Unity-only (UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() — the ONLY trustworthy
// allocation meter on Unity Mono). Excluded from core-tests.csproj.
//
// Rank 3 (GC-allocation fix, rapid-zoom stutter) F2 closer: after the three per-layer attribution
// columns (featColors/featWidths/featSelected) moved from managed T[] to NativeArray<T>, a CONSTANT
// style (paint.Width/Opacity.DependsOnFeature both false) makes StyledLineTileBuilder.WriteMeshData
// allocate zero managed bytes — this is the runtime tooth pinning that.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests.Jobs;
using MapRenderer.Unity.Rendering.Meshing;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class LineBuildAllocTests
    {
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        // CONSTANT paint (no data-driven expression): both Width and Opacity are literal values, so
        // paint.Width/Opacity.DependsOnFeature are false and their per-feature TryEvaluate branches
        // never run — isolates the tooth to the three array allocations (the Rank-3 fence), not
        // expression evaluation.
        private const string PaintJson = @"{""line-color"": ""#ff0000"", ""line-width"": 4}";

        /// <summary>
        /// A multi-ring, multi-feature line selection built with a CONSTANT paint. Measures a warmed
        /// <see cref="MapRenderer.Unity.Rendering.Meshing.StyledLineTileBuilder.WriteMeshData"/> call
        /// against a FRESH <c>Mesh.MeshDataArray</c> (never touched before), so a residual cannot be
        /// misattributed to <c>SetVertexBufferParams</c>/<c>SetIndexBufferParams</c> being called a
        /// second time on an already-declared <c>MeshData</c>.
        /// </summary>
        [Test]
        public void WriteMeshData_OverAConstantStyle_AllocatesNoGCMemory()
        {
            List<IFeature> features = SyntheticLineLayer();
            IReadOnlyList<SelectedTileFeature> selection = TestTileMeshBuilder.Selection(features);

            var styleLayer = new Line.StyleLayer
            {
                Id          = "alloc-line",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "alloc",
                Paint       = TestStyle.LinePaint(PaintJson),
                Layout      = TestStyle.LineLayout(),
            };
            var paint  = styleLayer.Paint;
            var layout = styleLayer.Layout;

            // Non-vacuity: a constant style really does bypass both per-feature bake branches — otherwise
            // this tooth would be measuring the expression-eval path (out of the Rank-3 fence) instead of
            // the three attribution arrays.
            Assert.IsFalse(paint.Width.DependsOnFeature,
                "precondition: line-width must be a CONSTANT — a data-driven width would exercise the " +
                "TryEvaluate bake branch, which is out of this stage's fence");
            Assert.IsFalse(paint.Opacity.DependsOnFeature,
                "precondition: line-opacity must be a CONSTANT for the same reason");

            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, Tile, Extent);
            try
            {
                Assert.Greater(geometry.RingCount, 1, "precondition: a multi-ring fixture — a single ring " +
                    "would not exercise the per-feature attribution columns meaningfully");

                double3 origin = double3.zero;

                // Warm-up (outside the measured region): JIT + one-time native growth. Its own MeshData —
                // never reused for the measured call.
                Mesh.MeshDataArray warmMda = Mesh.AllocateWritableMeshData(1);
                SyncMeshWrite.Line(warmMda[0], selection, geometry, paint, layout, Zoom,
                    origin, out int warmVertexCount, out Bounds _);
                warmMda.Dispose();
                Assert.Greater(warmVertexCount, 0,
                    "non-vacuity: the warm-up call must actually produce line geometry");

                // Measured call — a FRESH MeshDataArray (never SetVertexBufferParams'd before), so a
                // residual cannot be the reused-MeshData artifact the plan's discriminator names.
                Mesh.MeshDataArray measuredMda = Mesh.AllocateWritableMeshData(1);
                try
                {
                    Assert.That(() =>
                    {
                        SyncMeshWrite.Line(measuredMda[0], selection, geometry, paint, layout,
                            Zoom, origin, out int _, out Bounds _);
                    },
                    Is.Not.AllocatingGCMemory(),
                    "WriteMeshData must not allocate managed memory for a constant-style, multi-ring build " +
                    "once the three per-layer attribution columns are NativeArray<T> instead of T[]");
                }
                finally
                {
                    measuredMda.Dispose();
                }
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>Three LineString features, two of them multi-ring — real geometry through the shared
        /// <see cref="MvtCommandStream"/> encoder, not a hand-rolled buffer.</summary>
        private static List<IFeature> SyntheticLineLayer() => new List<IFeature>
        {
            new DictionaryFeature(
                properties:   new Dictionary<string, Value> { ["cls"] = Value.String("a") },
                geometryType: TileGeometryType.LineString,
                geometry:     MvtCommandStream.Feature(
                    MvtCommandStream.Ring(400, 500, 1200, 500, 2000, 500))),
            new DictionaryFeature(
                properties:   new Dictionary<string, Value> { ["cls"] = Value.String("b") },
                geometryType: TileGeometryType.LineString,
                geometry:     MvtCommandStream.Feature(
                    MvtCommandStream.Ring(300, 1500, 1100, 1500, 2100, 1500),
                    MvtCommandStream.Ring(300, 1800, 1100, 1800, 2100, 1800))),
            new DictionaryFeature(
                properties:   new Dictionary<string, Value> { ["cls"] = Value.String("c") },
                geometryType: TileGeometryType.LineString,
                geometry:     MvtCommandStream.Feature(
                    MvtCommandStream.Ring(400, 2000, 1600, 2000),
                    MvtCommandStream.Ring(700, 2500, 2400, 2500))),
        };
    }
}
