// fill-antialias is the ONE antialiasing switch that stays implementable per layer: MSAA and camera
// post-AA are render-target settings, so they cannot be turned off for a single fill. This layer's answer
// is geometric — a layer that opts out emits no boundary band at all — and the seam that decides it is
// StyledFillTileBuilder.BuildLayerInput, the only place that holds both the parsed paint and the
// LayerInput. These tests read that seam through the graph, so they observe the emitted geometry rather
// than the assignment.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class FillAntialiasBandTests
    {
        /// <summary>One triangular polygon ring, well inside the tile window so no edge is a clip edge —
        /// the band's own suppression predicate must not be what makes a count zero here.</summary>
        /// <param name="tile">The tile the buffers are addressed to.</param>
        private static TileGeometryBuffers TrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[0] = 0;
            g.RingOffsets[1] = 3;
            g.Vertices[0] = new double2(100, 100);
            g.Vertices[1] = new double2(900, 100);
            g.Vertices[2] = new double2(900, 900);
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        /// <summary>The one selected polygon feature <see cref="TrianglePolygon"/>'s ring belongs to.</summary>
        private static IReadOnlyList<SelectedTileFeature> OneSelectedPolygon() =>
            new List<SelectedTileFeature>
            {
                new SelectedTileFeature { Feature = new InMemoryTileFeature { GeometryType = TileGeometryType.Polygon }, Ordinal = 0 },
            };

        /// <summary>What the fill graph produced for a layer whose paint block is
        /// <paramref name="paintJson"/> — the band vertex count and the interior vertices, copied out
        /// before the native buffers are freed so two builds can be compared after both have run.</summary>
        /// <param name="paintJson">The layer's paint block, parsed exactly as a style would.</param>
        /// <param name="bandVertexCount">Vertices in the band suffix — 0 iff no band was emitted.</param>
        /// <param name="interior">Every tile-space vertex ahead of that suffix.</param>
        /// <param name="antialiasWhereUnspecified">MapViewConfig.FillAntialiasing — the parse-time DEFAULT
        /// for layers whose style does not specify fill-antialias. A layer that specifies wins.</param>
        private static void Build(string paintJson, out int bandVertexCount, out double2[] interior,
                                  bool antialiasWhereUnspecified = true)
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            TileGeometryBuffers geometry = TrianglePolygon(tile);
            // The project default enters HERE, at parse — exactly where production puts it
            // (StyleParser.Parse -> Fill.StyleLayer.AntialiasDefault -> this ctor).
            var paint = new Fill.PaintProperties(JsonParser.Parse(paintJson), antialiasWhereUnspecified);

            FillMeshPipeline.LayerInput input = StyledFillTileBuilder.BuildLayerInput(
                OneSelectedPolygon(), geometry, paint, zoom: 0.0, tileOriginRender: double3.zero,
                out NativeArray<Vector4> featureColors, new WebMercatorProjection(),
                layout: null, clip: TileBufferClip.KeepTileUnits(0.0));

            FillGraphOutput output = default;
            try
            {
                Assert.IsTrue(input.RingVisitOrder.IsCreated, $"{paintJson}: the fixture must build a layer.");
                output = FillMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(FillGraphCounts.Ok, output.Error.Value, $"{paintJson}: the graph must succeed.");

                bandVertexCount = output.Counts[0].BandVertexCount;
                interior = new double2[output.TileVertices.Length - bandVertexCount];
                for (int i = 0; i < interior.Length; i++) interior[i] = output.TileVertices[i];
            }
            finally
            {
                output.Dispose();
                featureColors.Dispose();
                input.RingVisitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── fill-antialias: false emits no band, and changes nothing else ──────────────────────────────
        //
        // Both halves matter. "No band" alone is satisfied by a build that produced nothing at all; the
        // interior comparison is what says the layer still renders, identically, minus the band.
        //
        // RED: delete the `SuppressBoundaryBand = …` line from StyledFillTileBuilder.BuildLayerInput (or
        // pin it to false). The parse is guarded separately and does not need re-verifying here —
        // FillPaintTests.FillPaint_Antialias_ParsesAsABoolean owns that half.
        [Test]
        public void FillAntialiasFalse_EmitsNoBand_AndLeavesTheInteriorBitIdentical()
        {
            Build("{\"fill-color\":\"#ffffff\"}", out int defaultBand, out double2[] defaultInterior);
            Build("{\"fill-color\":\"#ffffff\",\"fill-antialias\":false}", out int offBand, out double2[] offInterior);

            // Two band vertices per ring vertex, over one 3-vertex ring — the count the sizing tooth in
            // TileBuildGraphTests reads too. Stated as a precondition, because a tree that emits no band at
            // all satisfies the assertion below without the property doing anything.
            Assert.AreEqual(6, defaultBand,
                "precondition: with fill-antialias absent (⇒ true, the spec default) this ring must carry a " +
                "full band, or the zero below is measuring a band-free tree rather than the opt-out.");

            Assert.AreEqual(0, offBand,
                "fill-antialias: false must emit NO band geometry — this is the property's only consumer, " +
                "and the one that keeps it implementable per layer.");

            Assert.AreEqual(defaultInterior, offInterior,
                "opting out of antialiasing must drop the band and nothing else — the interior triangulation " +
                "is bit-identical either way.");
        }

        // fill-antialias: true is the spec default, so it must be indistinguishable from absent — a
        // threshold read the wrong way round would show up here and nowhere else.
        [Test]
        // NOTE the name is about OUTPUT, and is narrower than it reads: since the global default was
        // added, Fill.PaintProperties.AntialiasSpecified DOES tell an explicit true from an absent
        // property. That distinction is exactly what lets the global default apply only to the absent
        // case. What stays indistinguishable is the geometry, whenever the global default is ON.
        public void FillAntialiasTrue_IsIndistinguishableFromTheProperty_BeingAbsent()
        {
            Build("{\"fill-color\":\"#ffffff\"}", out int absentBand, out double2[] absentInterior);
            Build("{\"fill-color\":\"#ffffff\",\"fill-antialias\":true}", out int trueBand, out double2[] trueInterior);

            Assert.AreEqual(absentBand, trueBand, "an explicit true must band exactly as the default does.");
            Assert.AreEqual(absentInterior, trueInterior, "and produce the same interior.");
        }

        // ── The global override (MapViewConfig.FillAntialiasing) ───────────────────────────────────────

        /// <summary>The global override forces the band off for a layer whose style ASKS for antialiasing,
        /// and perturbs nothing else: the interior is vertex-for-vertex what the banded build produced.
        /// <para>Without the interior comparison this would pass for a build that emitted no geometry at
        /// all, which is the failure mode a bare "band count is 0" assertion cannot see.</para></summary>
        [Test]
        public void GlobalDefaultOff_SuppressesTheBand_WhereTheStyleIsSilent()
        {
            Build("{}", out int bandedCount, out double2[] bandedInterior);
            Assert.Greater(bandedCount, 0, "precondition: the default style must produce a band to suppress.");

            Build("{}", out int overriddenCount, out double2[] overriddenInterior, antialiasWhereUnspecified: false);

            Assert.AreEqual(0, overriddenCount,
                "MapViewConfig.FillAntialiasing = false must force the band off even though the style's " +
                "fill-antialias defaults to true.");
            CollectionAssert.AreEqual(bandedInterior, overriddenInterior,
                "the override must remove the band and change nothing else — the interior must be identical.");
        }

        /// <summary>The override is one-way. It can force the band OFF; it cannot turn it back ON for a layer
        /// whose style set <c>fill-antialias: false</c> — the two are OR'd, not overridden, so a style stays
        /// readable rather than being silently countermanded by a global switch.</summary>
        [Test]
        public void GlobalDefaultOff_LeavesALayerThatExplicitlyAsksForAntialiasingAlone()
        {
            // The one case that separates "global is a default" from "global overrides everything", and the
            // case the shipped Liberty style actually contains: landcover_wetland sets fill-antialias: true
            // explicitly while 12 other fill layers say nothing.
            Build("{\"fill-antialias\": true}", out int count, out _, antialiasWhereUnspecified: false);
            Assert.Greater(count, 0,
                "a layer that explicitly asks for antialiasing must keep its band when the GLOBAL default " +
                "is off — the global setting decides only for layers whose style said nothing.");
        }

        /// <summary>The global default cannot turn a band back ON for a layer that opted out.</summary>
        [Test]
        public void GlobalDefaultOn_DoesNotResurrectABandTheStyleTurnedOff()
        {
            Build("{\"fill-antialias\": false}", out int count, out _, antialiasWhereUnspecified: true);
            Assert.AreEqual(0, count,
                "fill-antialias: false must stay band-free with the global override left ON — the global " +
                "knob must not be able to override a layer's explicit opt-out.");
        }

    }
}
