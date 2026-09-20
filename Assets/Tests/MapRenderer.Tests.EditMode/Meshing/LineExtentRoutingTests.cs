// Unity EditMode only — StyledLineTileBuilder + Burst jobs. NOT registered in Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Closes IR C1 P2's <b>recorded finding 0</b>: line's own <c>TileToGeoJob</c> extent routing had
    /// <b>no observing tooth</b>. A hardcoded <c>Extent = 4096.0</c> in
    /// <c>StyledLineTileBuilder.WriteMeshData</c>'s two subdivided-projection calls passed the ENTIRE
    /// 2286-test gate — discovered by accident when a review agent's injection was left live in the tree.
    ///
    /// <para><b>Why it survived, and why this fixture is built the way it is.</b> Every committed
    /// <c>.pbf</c> fixture is extent 4096, so substituting the literal is <b>inert across the whole
    /// corpus</b>. Fill has a tooth (<c>FillSharedBufferTests.PatternCoords_…NotA4096Literal</c>) and the
    /// producer seam has one (<c>TileGeometryMaterializerSeamTests</c>), but neither one's fixture reaches
    /// line's call site. So the discriminator here is the fixture's extent — <b>not</b> 4096 — and if it
    /// were ever changed back to 4096 this tooth would be vacuous by construction. That is the whole
    /// point.</para>
    ///
    /// <para><b>Why P3 is where it lands.</b> P3 makes the buffer the sole authority for both <c>Tile</c>
    /// and <c>Extent</c> (the decoder stamps them; <c>ITileDecoder.Decode</c> takes the id), and tooth B
    /// pins the <c>Tile</c> half. Pinning one half structurally and leaving the identically-routed other
    /// half to convention is the asymmetry this epic exists to remove. And P3 makes the fixture cheap: a
    /// layer owns its buffer, so its extent is a constructor argument.</para>
    ///
    /// <para><b>How the observation works.</b> Tile-local coordinates are geo-referenced by
    /// <c>x / extent</c>, so decoding an identical ring under two different extents places it at two
    /// different geodetic longitudes and therefore two different world positions. A builder that ignored
    /// <c>geometry.Extent</c> in favour of a 4096 literal would produce the SAME mesh for both — which is
    /// exactly what the assertion below forbids.</para>
    /// </summary>
    [TestFixture]
    public class LineExtentRoutingTests
    {
        private static readonly TileId Tile = new TileId { Z = 3, X = 4, Y = 3 };

        /// <summary>Deliberately NOT 4096 — see the fixture note on the type. Half of 4096, so the same
        /// integer ring lands at twice the tile fraction and the difference is large, not marginal.</summary>
        private const uint NonDefaultExtent = 2048;
        private const uint DefaultExtent    = 4096;

        private static Line.PaintProperties Paint() => StyleLayer().Paint;
        private static Line.LayoutProperties Layout() => StyleLayer().Layout;

        private static Line.StyleLayer StyleLayer() => new Line.StyleLayer
        {
            Id = "extent-probe", LayerType = StyleLayerType.Line, SourceLayer = "probe",
            Paint = TestStyle.LinePaint("{\"line-color\":\"#ffffff\",\"line-width\":2}"),
            Layout = TestStyle.LineLayout(),
        };

        /// <summary>One three-point polyline, well inside the tile at BOTH extents (max coord 900 &lt; 2048).</summary>
        private static IReadOnlyList<MapRenderer.Core.Expressions.IFeature> ProbeFeatures() =>
            new MapRenderer.Core.Expressions.IFeature[]
            {
                new InMemoryTileFeature
                {
                    GeometryType = TileGeometryType.LineString,
                    Geometry     = MvtCommandStreamForExtentProbe(),
                },
            };

        private static uint[] MvtCommandStreamForExtentProbe()
            => MapRenderer.Tests.Jobs.MvtCommandStream.Feature(
                MapRenderer.Tests.Jobs.MvtCommandStream.Ring(100, 100, 500, 400, 900, 300));

        private static float3[] BuildAt(uint extent)
        {
            using var tile = new InMemoryDecodedTile(
                new InMemoryTileLayer("probe", Tile, ProbeFeatures(), extent));
            var layer = tile.GetLayer("probe");
            Assert.IsTrue(layer.Geometry.IsCreated, "precondition: the probe layer materialized");
            Assert.AreEqual((double)extent, layer.Geometry.Extent,
                "precondition: the buffer must carry the extent it was minted at — if this is 4096 for the " +
                "non-default arm, the fixture is vacuous by construction (the exact reason the defect lived)");

            Mesh mesh = TestTileMeshBuilder.BuildLineFromLayer(
                layer, TestTileMeshBuilder.Select(StyleLayer(), layer, 0.0),
                Paint(), Layout(), zoom: Tile.Z, id: Tile, origin: double2.zero);
            Assert.IsNotNull(mesh, $"the probe must produce line geometry at extent {extent}");
            try
            {
                Vector3[] verts = mesh.vertices;
                var copy = new float3[verts.Length];
                for (int i = 0; i < verts.Length; i++) copy[i] = verts[i];
                return copy;
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void LineSubdividedProjection_ReadsTheBuffersOwnExtent_NotA4096Literal()
        {
            float3[] atDefault    = BuildAt(DefaultExtent);
            float3[] atNonDefault = BuildAt(NonDefaultExtent);

            // Non-vacuity: both arms really produced comparable geometry.
            Assert.Greater(atDefault.Length, 0, "precondition: the 4096 arm produced vertices");
            Assert.AreEqual(atDefault.Length, atNonDefault.Length,
                "precondition: the two arms must emit the same vertex COUNT — the rings are identical, only " +
                "the quantization range differs, so a count difference would mean the arms diverge for some " +
                "reason other than the extent and the position comparison below would be meaningless");

            int differing = 0;
            for (int i = 0; i < atDefault.Length; i++)
                if (math.distance(atDefault[i], atNonDefault[i]) > 1e-4f) differing++;

            Assert.AreEqual(atDefault.Length, differing,
                "EVERY vertex must move when the source-layer's extent changes. Tile-local coordinates are " +
                "geo-referenced by x/extent, so the same integer ring at extent 2048 sits at twice the tile " +
                "fraction it does at 4096. If the two builds agree, StyledLineTileBuilder's TileToGeoJob " +
                "calls are NOT reading `geometry.Extent` — they are using a literal, which is precisely the " +
                "injection that survived a full green 2286-test gate in IR C1 P2 (recorded finding 0).");
        }
    }
}
