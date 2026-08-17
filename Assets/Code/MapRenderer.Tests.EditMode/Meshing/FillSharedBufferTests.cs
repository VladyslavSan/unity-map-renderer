// Unity EditMode only — Mesh/MeshData + the Burst fill pipeline. NOT registered in Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Tests.Jobs;
using MapRenderer.Unity.Rendering.Meshing;
using Color = UnityEngine.Color;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// IR B7a — fill reads a buffer it <b>shares</b> with other layers, and joins its per-feature side arrays
    /// by the source-layer <b>ordinal</b> rather than by its own selected-list position.
    ///
    /// <para><c>FillSortKeyAndOpacityTests</c> is the draw-order instrument (and the only one in the repo:
    /// <c>fill-sort-key</c> appears in no committed fixture style, so the whole snapshot corpus is blind to
    /// fill draw order). Its harness has <c>selected == the whole buffer</c> and no non-polygon features,
    /// which makes it structurally unable to see three of B7's four new failure modes. This file is the
    /// arm that can: it drives the same three features through a buffer that <b>also</b> holds features this
    /// layer does not select and one it selects but cannot draw.</para>
    /// </summary>
    [TestFixture]
    public class FillSharedBufferTests
    {
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        private static readonly string ColourByName =
            @"{""fill-color"": [""match"", [""get"", ""name""],
               ""bottom"", ""#ff0000"", ""middle"", ""#00ff00"", ""top"", ""#0000ff"", ""#ffffff""]}";
        private const string SortKeyBySk = @"{""fill-sort-key"": [""get"", ""sk""]}";

        // ── T1b — the shared-buffer form of the draw-order tooth ──────────────────────────────────

        /// <summary>
        /// The SAME three sort-keyed squares, but sharing their buffer with (a) two polygon features this
        /// layer's filter rejects, and (b) one selected <b>LineString</b> whose sort key interleaves with
        /// theirs. The index buffer's colour runs — and the vertex count — must be exactly what the
        /// three-features-alone build produces.
        ///
        /// <para>Three failure modes live here and nowhere else in the repo:</para>
        /// <list type="bullet">
        /// <item>a visit order that forgets the <b>selection</b> predicate (the <c>rank == -1</c> skip) —
        /// unselected features get triangulated, so vertex count and colour runs both grow;</item>
        /// <item>a visit order that forgets the <b>kind</b> predicate — the LineString's ring reaches
        /// assembly;</item>
        /// <item><c>featureColors</c> indexed by <b>selected slot</b> instead of ordinal — the geometry is
        /// right and the colours come out permuted, which the vertex count alone cannot see.</item>
        /// </list>
        /// <para>None is reachable from the plain harness, whose selection IS the whole buffer, so slot and
        /// ordinal coincide there.</para>
        /// </summary>
        [Test]
        public void SharedBuffer_UnselectedAndNonPolygonFeatures_DoNotChangeTheDrawOrderOrTheGeometry()
            => AssertSharedBufferMatchesTheUnsharedControl(TileBufferClip.Disabled);

        /// <summary>
        /// The same comparison run down the <b>clip</b> branch — which is the branch production takes.
        ///
        /// <para><b>Why this exists (B7a review R1/B1).</b> <c>MapViewConfig.FillTileBufferClip = 0.0</c> →
        /// <c>TileBufferClip.FromInspectorUnits(0.0)</c> → <c>KeepTileUnits(0.0)</c> → <c>IsEnabled == true</c>,
        /// so <c>FillMeshPipeline.DeriveVisitedRings</c> runs <c>RingClipJob</c>, not <c>RingSelectJob</c>, for
        /// every fill layer of a real style. The test above — the only fixture in the repo with a genuinely
        /// permuted, subsetted visit order — ran with <c>clip: default</c>, i.e. DISABLED, so
        /// <c>RingClipJob</c>'s <c>int ri = RingVisitOrder[k];</c> indirection was observed by nothing: every
        /// other clip fixture supplies an identity order, under which <c>ri == k</c> holds by construction.
        /// Collapsing it to <c>int ri = k;</c> passed the entire 2279-test gate.</para>
        ///
        /// <para>Traced discriminator: this fixture's visit order is <c>[5, 2, 1]</c>. Under <c>ri = k</c> the
        /// clip reads rings <c>0, 1, 2</c> — <c>unselected-a</c>, <c>top</c>, <c>middle</c> — so the geometry
        /// moves (a square at (2000,2000) instead of the one at the origin) AND the colours move
        /// (<c>featureColors[0]</c> is the default <c>(0,0,0,0)</c>, because ordinal 0 was never ranked).</para>
        ///
        /// <para>The clip itself is behaviourally inert here — every fixture ring lies inside the b = 0 window
        /// — which is the point: the arm exists to route the visit order through the clip branch, not to test
        /// clipping. That is what makes "control == shared" the same claim as the test above.</para>
        /// </summary>
        [Test]
        public void SharedBuffer_OnTheClipBranch_UnselectedAndNonPolygonFeatures_StillChangeNothing()
            => AssertSharedBufferMatchesTheUnsharedControl(TileBufferClip.KeepTileUnits(0.0));

        private static void AssertSharedBufferMatchesTheUnsharedControl(TileBufferClip clip)
        {
            // The control arm: only the three features this layer draws, in declared order.
            var alone = new List<IFeature>
            {
                Square(0, 0, "top",    sortKey: 10.0),
                Square(0, 0, "middle", sortKey:  5.0),
                Square(0, 0, "bottom", sortKey:  1.0),
            };

            // The shared arm: the same three at ordinals 1, 2 and 5, with unselected polygons at 0 and 3 and
            // a SELECTED LineString at 4 whose sort key (7) sits between "middle" and "top" — so a rank that
            // failed to skip it would shift every later rank, and a slot-indexed colour lookup would land one
            // entry off.
            var shared = new List<IFeature>
            {
                Square(2000, 2000, "unselected-a", sortKey: 0.5),
                Square(0, 0, "top",    sortKey: 10.0),
                Square(0, 0, "middle", sortKey:  5.0),
                Square(3000,  100, "unselected-b", sortKey: 99.0),
                Line(1000, 1000, "road", sortKey: 7.0),
                Square(0, 0, "bottom", sortKey:  1.0),
            };
            int[] selectedOrdinals = { 1, 2, 4, 5 };

            var paint  = new Fill.PaintProperties(JsonParser.Parse(ColourByName));
            var layout = new Fill.LayoutProperties(JsonParser.Parse(SortKeyBySk));

            Mesh control = null, mixed = null;
            try
            {
                control = TestTileMeshBuilder.BuildFill(alone, paint, 0.0, Extent, Tile, null, layout, clip);
                mixed   = BuildFillFromSharedBuffer(shared, selectedOrdinals, paint, layout, clip);

                // Non-vacuity: both arms produced geometry, and the fixtures really do differ in what the
                // BUFFER holds — otherwise this is one build compared with itself.
                Assert.IsNotNull(control, "the control arm must produce geometry");
                Assert.IsNotNull(mixed, "the shared-buffer arm must produce geometry");
                Assert.AreEqual(6, shared.Count, "precondition: the shared buffer holds six features");
                Assert.AreEqual(4, selectedOrdinals.Length, "precondition: this layer selects four of them");
                Assert.AreEqual(TileGeometryType.LineString, shared[4].GeometryType,
                    "precondition: one SELECTED feature is a LineString the fill layer cannot draw");

                Assert.AreEqual(control.vertexCount, mixed.vertexCount,
                    "the extra features must contribute NO geometry — a larger count means the selection or " +
                    "the kind predicate was dropped from the ring visit order, and a smaller one means a " +
                    "wanted feature was skipped");
                Assert.AreEqual(control.triangles.Length, mixed.triangles.Length, "…and no extra triangles");

                List<Color> controlRuns = PaintOrderColorRuns(control);
                List<Color> mixedRuns   = PaintOrderColorRuns(mixed);

                Assert.AreEqual(3, controlRuns.Count,
                    "precondition: the control really draws three distinguishable colour runs");
                Assert.Greater(controlRuns[0].r, 0.5f,
                    "precondition: ascending sort key puts red 'bottom' first in the control. Read a failure " +
                    "HERE as the finding rather than as a broken fixture: it means the ring visit order moved " +
                    "on the control's own build, before the shared-buffer comparison below could even run. " +
                    "(That is exactly how the clip arm reports the `int ri = k;` defect.)");

                Assert.AreEqual(controlRuns.Count, mixedRuns.Count, "same number of colour runs");
                for (int i = 0; i < controlRuns.Count; i++)
                    Assert.AreEqual(controlRuns[i], mixedRuns[i],
                        $"colour run {i} differs. Same run count with different colours is the ORDINAL-JOIN " +
                        "failure: featureColors indexed by the selected slot instead of the source-layer " +
                        "ordinal, so every feature paints with a neighbour's colour.");
            }
            finally
            {
                if (control != null) Object.DestroyImmediate(control);
                if (mixed != null)   Object.DestroyImmediate(mixed);
            }
        }

        // ── T1d — the GLOBE colour join (a second, independent read of featureColors) ─────────────

        /// <summary>
        /// The globe fill path colours its vertices through a <b>second</b>, independent read of
        /// <c>featureColors</c> — <c>GlobeFillVertex.Feature</c> after subdivision, not
        /// <c>TileMeshBuffers.VertexFeatureIdx</c>. Three sort-keyed coincident squares must paint in the same
        /// order on the globe as on the flat sheet.
        ///
        /// <para><b>Why it exists — measured.</b> B7a's RED sweep injected an off-by-one into that read alone
        /// and the whole gate stayed green at 2278/2278. A sweep of the test tree found the reason: <b>no
        /// globe fill test reads vertex colours at all</b>. The one globe fixture that does
        /// (<c>TileBackgroundQuadProjectionTests</c>) has exactly one feature, so any mis-join clamps back to
        /// the same entry. That is the "silent globe-only miscolour" the B7 plan named as the thing a
        /// flat-only tooth cannot see — this closes it rather than recording it a second time.</para>
        /// </summary>
        [Test]
        public void GlobeFill_ColoursByOrdinalToo_SoTheDrawOrderMatchesTheFlatPath()
        {
            var features = new List<IFeature>
            {
                Square(0, 0, "top",    sortKey: 10.0),
                Square(0, 0, "middle", sortKey:  5.0),
                Square(0, 0, "bottom", sortKey:  1.0),
            };
            var paint  = new Fill.PaintProperties(JsonParser.Parse(ColourByName));
            var layout = new Fill.LayoutProperties(JsonParser.Parse(SortKeyBySk));

            Mesh flat = null, globe = null;
            try
            {
                flat  = TestTileMeshBuilder.BuildFill(features, paint, 0.0, Extent, Tile, null, layout);
                globe = TestTileMeshBuilder.BuildFill(
                    features, paint, 0.0, Extent, Tile, new SphericalProjection(), layout);

                Assert.IsNotNull(flat, "precondition: the flat arm must produce geometry");
                Assert.IsNotNull(globe, "precondition: the globe arm must produce geometry");

                // Non-vacuity: the two arms really are different code paths, not the same one twice — the
                // globe path subdivides, so it has strictly more vertices.
                Assert.Greater(globe.vertexCount, flat.vertexCount,
                    "precondition: the globe arm must actually have gone through the subdivided write");

                List<Color> flatRuns  = PaintOrderColorRuns(flat);
                List<Color> globeRuns = PaintOrderColorRuns(globe);

                Assert.AreEqual(3, flatRuns.Count, "precondition: three distinguishable colour runs on the flat path");
                Assert.AreEqual(flatRuns.Count, globeRuns.Count,
                    "the globe path must paint the same number of colour runs as the flat one");
                for (int i = 0; i < flatRuns.Count; i++)
                    Assert.AreEqual(flatRuns[i], globeRuns[i],
                        $"globe colour run {i} differs from the flat path's. The globe write reads " +
                        "featureColors through GlobeFillVertex.Feature — a SECOND join, which no other test " +
                        "in the repo observes, and which a mis-index turns into a globe-only miscolour that " +
                        "renders perfectly and is simply the wrong colour.");
            }
            finally
            {
                if (flat != null)  Object.DestroyImmediate(flat);
                if (globe != null) Object.DestroyImmediate(globe);
            }
        }

        // ── T1c — WITHIN-feature ring order (the blind spot R2 measured) ──────────────────────────

        /// <summary>
        /// A single polygon feature whose rings are <b>outer then hole</b> must still be triangulated with the
        /// hole cut out. This pins the visit order's <b>within-rank</b> ordering, which the sort-key tests
        /// cannot see because every feature they use has exactly one ring.
        ///
        /// <para><b>Why it exists — measured, not anticipated.</b> B7a's RED sweep injected the real defect
        /// (bucket the counting sort in descending ring index, so a feature's rings come out reversed) and the
        /// <b>entire gate stayed green at 2277/2277</b> — while a diagnostic proved the defect was firing hard:
        /// 923 fill builds in the suite reach a rank <b>450 rings wide</b>, and the produced fill dropped from
        /// 30638 to 29982 vertices on the flat path and from 164535 to 137379 on the globe path. Every
        /// corpus-scale fill oracle in the repo is either a differential between two arms that would BOTH be
        /// injected (cache vs fresh, sync vs async, view vs direct) or drives <c>FillMeshPipeline.Schedule</c>
        /// with an identity visit order, bypassing this code entirely. So this was a genuine blind surface,
        /// not a redundant one.</para>
        ///
        /// <para>The oracle is <b>covered area</b>, derived from the fixture's own geometry rather than
        /// transcribed from the pipeline: a 3000-unit square with a 1000-unit square hole covers exactly
        /// 8/9 of the solid square. Reversing the two rings makes the hole establish the exterior sign, the
        /// real outer fails containment, and the covered area changes to the hole's 1/9.</para>
        /// </summary>
        [Test]
        public void WithinAFeature_RingsKeepDecodeOrder_SoAPolygonsHoleIsStillCutOut()
        {
            // Authored through the repo's existing MVT command-stream helper, which carries the running
            // cursor ACROSS rings exactly as the spec requires — the detail a hand-rolled second ring gets
            // wrong. Outer is CCW (positive shoelace), hole is the opposite winding, and the hole's centroid
            // (2000, 2000) is inside the outer, so RingAssemblyJob's containment check accepts it.
            var outerRing = MvtCommandStream.Ring(500, 500, 3500, 500, 3500, 3500, 500, 3500);
            var holeRing  = MvtCommandStream.Ring(1500, 1500, 1500, 2500, 2500, 2500, 2500, 1500);

            var withHole = new List<IFeature>
            {
                new DictionaryFeature(
                    Props("ring", 0.0), TileGeometryType.Polygon,
                    geometry: MvtCommandStream.Feature(outerRing, holeRing)),
            };
            var solid = new List<IFeature>
            {
                new DictionaryFeature(
                    Props("ring", 0.0), TileGeometryType.Polygon,
                    geometry: MvtCommandStream.Feature(outerRing)),
            };

            var paint = new Fill.PaintProperties(JsonParser.Parse(@"{""fill-color"":""#ffffff""}"));

            Mesh holed = null, full = null, holedOnTheClipBranch = null;
            try
            {
                holed = TestTileMeshBuilder.BuildFill(withHole, paint, 0.0, Extent, Tile);
                full  = TestTileMeshBuilder.BuildFill(solid,    paint, 0.0, Extent, Tile);
                // The same claim on the branch production actually takes (B7a review R1): with the clip
                // enabled the rings reach assembly through RingClipJob rather than RingSelectJob. Every ring
                // here lies inside the b = 0 window, so the clip is inert and the area must not move.
                holedOnTheClipBranch = TestTileMeshBuilder.BuildFill(
                    withHole, paint, 0.0, Extent, Tile, null, null, TileBufferClip.KeepTileUnits(0.0));

                Assert.IsNotNull(full, "precondition: the solid square must produce geometry");
                Assert.IsNotNull(holed, "precondition: the holed square must produce geometry");

                double solidArea = TriangleArea(full);
                double holedArea = TriangleArea(holed);
                Assert.Greater(solidArea, 0.0, "precondition: the solid square covers a positive area");

                // Non-vacuity: the two fixtures really do differ, and by the amount the geometry says.
                // hole is 1000² of a 3000² outer ⇒ 1/9 removed ⇒ 8/9 remains.
                double expected  = solidArea * 8.0 / 9.0;
                double tolerance = solidArea * 0.01;
                Assert.Greater(math.abs(solidArea - holedArea), tolerance,
                    "precondition: a hole this size must change the covered area, or the oracle is inert");

                Assert.AreEqual(expected, holedArea, tolerance,
                    "the feature's SECOND ring must be read as the hole of its FIRST. A visit order that " +
                    "reversed a feature's rings makes the hole establish the exterior sign, the real outer " +
                    "fail its containment check, and the covered area collapse to the hole's 1/9 — a defect " +
                    "measured to change 923 fill builds in this suite while every other test stayed green.");

                Assert.IsNotNull(holedOnTheClipBranch, "the clip-branch arm must produce geometry");
                Assert.AreEqual(expected, TriangleArea(holedOnTheClipBranch), tolerance,
                    "the same feature built with the clip ENABLED — the production configuration — must cut " +
                    "the same hole. RingClipJob walks the ring visit order through its own indirection, so " +
                    "within-feature order is a separate claim on that branch.");
            }
            finally
            {
                if (holed != null) Object.DestroyImmediate(holed);
                if (full != null)  Object.DestroyImmediate(full);
                if (holedOnTheClipBranch != null) Object.DestroyImmediate(holedOnTheClipBranch);
            }
        }

        /// <summary>Total covered area of a mesh's triangles, in its own (world) units — an oracle derived
        /// from the vertices the GPU will draw, not from anything the pipeline reports about itself.</summary>
        private static double TriangleArea(Mesh mesh)
        {
            Vector3[] v = mesh.vertices;
            int[]     t = mesh.triangles;
            double    a = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                // Mercator at z0 is a flat XZ sheet, so the planar cross product in XZ is the area.
                Vector3 p0 = v[t[i]], p1 = v[t[i + 1]], p2 = v[t[i + 2]];
                a += math.abs((p1.x - p0.x) * (p2.z - p0.z) - (p2.x - p0.x) * (p1.z - p0.z)) * 0.5;
            }
            return a;
        }

        // ── T7 — the tile extent is ROUTED, not assumed ───────────────────────────────────────────

        /// <summary>
        /// The fill mesh write reads the tile extent off the <b>buffer</b>. Built at extent 2048 and at 4096
        /// from the same geometric fractions of the tile, the pattern-coordinate stream (stream 1, world
        /// units from the tile origin) must be identical.
        ///
        /// <para><b>What this adds.</b> <c>TileGeometryMaterializerSeamTests</c> already pins extent routing
        /// through the pipeline's clip window and its tile→geodetic stage. Neither can see
        /// <c>WriteGeometry</c>'s own <c>extentInv</c>, which is the parameter B7 <i>removed</i> from the
        /// signature (§"extent stops travelling beside the buffer"): world positions come out of the Burst
        /// chain and would stay correct while the UVs silently halved. A literal <c>4096.0</c> substituted for
        /// <c>geometry.Extent</c> here doubles the 2048 arm's pattern coordinates and nothing else in the repo
        /// notices.</para>
        ///
        /// <para>Driven through <c>PathGeometryMaterializer</c>, which takes tile-local coordinates directly,
        /// so a non-4096 fixture costs nothing.</para>
        /// </summary>
        [Test]
        public void PatternCoords_ComeFromTheBuffersOwnExtent_NotA4096Literal()
        {
            Mesh low = null, high = null;
            try
            {
                low  = BuildQuadAtExtent(2048.0);
                high = BuildQuadAtExtent(4096.0);

                Assert.IsNotNull(low, "the 2048 arm must produce geometry");
                Assert.IsNotNull(high, "the 4096 arm must produce geometry");
                Assert.AreEqual(high.vertexCount, low.vertexCount,
                    "precondition: the two arms describe the same quad, so they triangulate identically");

                Vector2[] lowUv  = low.uv;
                Vector2[] highUv = high.uv;
                Assert.Greater(lowUv.Length, 0, "precondition: stream 1 (pattern coords) really was written");

                // Non-vacuity: the UVs are not all zero, so "identical" is a claim about real numbers.
                bool anyNonZero = false;
                foreach (Vector2 uv in lowUv) if (uv.sqrMagnitude > 0f) { anyNonZero = true; break; }
                Assert.IsTrue(anyNonZero, "precondition: at least one pattern coordinate is non-zero");

                for (int i = 0; i < lowUv.Length; i++)
                    Assert.AreEqual(highUv[i], lowUv[i],
                        $"pattern coordinate {i} differs between extent 2048 and extent 4096 for the SAME " +
                        "fraction of the tile. The extent must come off the buffer; a 4096 literal makes the " +
                        "2048 arm exactly 2x.");
            }
            finally
            {
                if (low != null)  Object.DestroyImmediate(low);
                if (high != null) Object.DestroyImmediate(high);
            }
        }

        // ── T8 — the tile ADDRESS is routed too (B7a review N1) ───────────────────────────────────

        /// <summary>
        /// The pattern stream's world span comes from <c>geometry.Tile</c>, the buffer's own address. The same
        /// tile-local quad materialized for z0 and for z1 must produce pattern coordinates in exactly the 2:1
        /// ratio of their world spans (a tile halves in width per zoom level).
        ///
        /// <para><b>What this closes.</b> Until this stage <c>WriteGeometry</c> took a <c>TileId</c> parameter
        /// <i>beside</i> the buffer: the pipeline projected using <c>geometry.Tile</c> while the flat path
        /// computed the pattern scale — and the globe path its subdivision — from the separate <c>id</c>.
        /// Pairing a z0 buffer with a z1 id gave base vertices from one tile and pattern coordinates at half
        /// scale, silently. The parameter is gone; this tooth is what keeps it gone, because a re-introduced
        /// second copy (or a hardcoded zoom) collapses the ratio to 1:1.</para>
        ///
        /// <para>Both arms use the SAME extent and the same tile fractions, so the only quantity that differs
        /// is the address — which is the point of the ruler.</para>
        /// </summary>
        [Test]
        public void PatternCoords_ComeFromTheBuffersOwnTile_NotASecondTileId()
        {
            var z0 = new TileId { Z = 0, X = 0, Y = 0 };
            var z1 = new TileId { Z = 1, X = 0, Y = 0 };

            Mesh coarse = null, fine = null;
            try
            {
                coarse = BuildQuad(z0, Extent);
                fine   = BuildQuad(z1, Extent);

                Assert.IsNotNull(coarse, "the z0 arm must produce geometry");
                Assert.IsNotNull(fine, "the z1 arm must produce geometry");
                Assert.AreEqual(coarse.vertexCount, fine.vertexCount,
                    "precondition: the two arms describe the same tile-local quad, so they triangulate identically");

                Vector2[] coarseUv = coarse.uv;
                Vector2[] fineUv   = fine.uv;
                Assert.Greater(coarseUv.Length, 0, "precondition: stream 1 (pattern coords) really was written");

                // Non-vacuity: the two arms must actually DIFFER, or "ratio 2" is a statement about zeros.
                bool anyDifferent = false;
                for (int i = 0; i < coarseUv.Length; i++)
                    if (coarseUv[i].x != fineUv[i].x || coarseUv[i].y != fineUv[i].y) { anyDifferent = true; break; }
                Assert.IsTrue(anyDifferent,
                    "precondition: the two zooms must produce different pattern coordinates at all — if they " +
                    "are identical the 2:1 assertion below is a statement about zeros.");

                // Exact, not approximate: the span is C / 2^z, so halving it is exact in binary floating point
                // and so is the resulting product. A tolerance here would let a nearly-right scale through.
                for (int i = 0; i < coarseUv.Length; i++)
                {
                    Assert.AreEqual(coarseUv[i].x * 0.5f, fineUv[i].x, 0.0,
                        $"pattern coordinate {i}.x: a z1 tile spans exactly HALF the world units of a z0 tile, " +
                        "so its pattern coordinates must halve. Equal values mean the span was computed from " +
                        "something other than the buffer's own Tile.");
                    Assert.AreEqual(coarseUv[i].y * 0.5f, fineUv[i].y, 0.0, $"pattern coordinate {i}.y");
                }
            }
            finally
            {
                if (coarse != null) Object.DestroyImmediate(coarse);
                if (fine != null)   Object.DestroyImmediate(fine);
            }
        }

        // ── Fixture ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Builds a fill mesh from a buffer holding <paramref name="all"/>, where this layer selects
        /// only <paramref name="selectedOrdinals"/> — the production shape a plain feature-list harness cannot
        /// express.</summary>
        private static Mesh BuildFillFromSharedBuffer(
            IReadOnlyList<IFeature> all, IReadOnlyList<int> selectedOrdinals,
            Fill.PaintProperties paint, Fill.LayoutProperties layout, TileBufferClip clip = default)
        {
            var selected = new List<SelectedTileFeature>(selectedOrdinals.Count);
            foreach (int ordinal in selectedOrdinals)
                selected.Add(new SelectedTileFeature { Feature = all[ordinal], Ordinal = ordinal });

            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(all, Tile, Extent);
            var mda = Mesh.AllocateWritableMeshData(1);
            try
            {
                StyledFillTileBuilder.WriteMeshData(
                    mda[0], selected, geometry, paint, 0.0,
                    TileRenderOrigin.Project(Tile, null),
                    out int vertexCount, out Bounds bounds, null, layout, clip);
                return Finish(mda, vertexCount, bounds);
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>The tile's middle half, as a single-feature path geometry at the given extent — the same
        /// fraction of the tile at both extents, so every derived quantity that is a fraction (pattern
        /// coordinates, world positions) must agree.</summary>
        private static Mesh BuildQuadAtExtent(double extent) => BuildQuad(Tile, extent);

        /// <summary>The middle half of <paramref name="tile"/> at <paramref name="extent"/>, written through
        /// <c>WriteGeometry</c>. Both the address and the extent reach the write ONLY through the buffer —
        /// there is no second copy to pass, which is the property the two teeth above assert.</summary>
        private static Mesh BuildQuad(TileId tile, double extent)
        {
            double lo = extent * 0.25, hi = extent * 0.75;
            var paths = new[]
            {
                new[]
                {
                    new[]
                    {
                        new double2(lo, lo), new double2(hi, lo),
                        new double2(hi, hi), new double2(lo, hi),
                    },
                },
            };
            var kinds = new[] { TileGeometryType.Polygon };

            TileGeometryBuffers geometry =
                new PathGeometryMaterializer(tile, extent, kinds, paths).Materialize();
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var mda = Mesh.AllocateWritableMeshData(1);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent);
            featureColors[0]  = new Vector4(1f, 1f, 1f, 1f);
            try
            {
                StyledFillTileBuilder.WriteGeometry(
                    mda[0], geometry, visitOrder, featureColors,
                    TileRenderOrigin.Project(tile, null), null, default,
                    out int vertexCount, out Bounds bounds);
                return Finish(mda, vertexCount, bounds);
            }
            finally
            {
                featureColors.Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        private static Mesh Finish(Mesh.MeshDataArray mda, int vertexCount, Bounds bounds)
        {
            if (vertexCount == 0) { mda.Dispose(); return null; }
            var mesh = new Mesh { name = "B7aFill", indexFormat = IndexFormat.UInt32 };
            Mesh.ApplyAndDisposeWritableMeshData(
                mda, mesh, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = bounds;
            return mesh;
        }

        /// <summary>Each triangle's colour in RASTERIZATION order (the index buffer), deduped to one entry per
        /// contiguous run — the same oracle <c>FillSortKeyAndOpacityTests</c> uses, and for the same reason: a
        /// vertex-order oracle would keep passing if the pipeline ever regrouped its index emission.</summary>
        private static List<Color> PaintOrderColorRuns(Mesh mesh)
        {
            Color[] colors    = mesh.colors;
            int[]   triangles = mesh.triangles;
            var     runs      = new List<Color>();
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Color c = colors[triangles[t]];
                if (runs.Count == 0 || runs[runs.Count - 1] != c) runs.Add(c);
            }
            return runs;
        }

        private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

        /// <summary>One axis-aligned 100-unit square in MVT command form — the real decode path.</summary>
        private static DictionaryFeature Square(int x, int y, string name, double sortKey)
            => new DictionaryFeature(
                Props(name, sortKey), TileGeometryType.Polygon,
                geometry: new[]
                {
                    (1u << 3) | 1u, ZigZag(x),    ZigZag(y),
                    (3u << 3) | 2u, ZigZag(100),  ZigZag(0),
                                    ZigZag(0),    ZigZag(100),
                                    ZigZag(-100), ZigZag(0),
                    (1u << 3) | 7u,
                });

        /// <summary>A LineString whose ring has 4 points and a large signed area — so it is exactly the
        /// shape the area-based assembler would misread if a kind gate went missing.</summary>
        private static DictionaryFeature Line(int x, int y, string name, double sortKey)
            => new DictionaryFeature(
                Props(name, sortKey), TileGeometryType.LineString,
                geometry: new[]
                {
                    (1u << 3) | 1u, ZigZag(x),    ZigZag(y),
                    (3u << 3) | 2u, ZigZag(400),  ZigZag(0),
                                    ZigZag(0),    ZigZag(400),
                                    ZigZag(-400), ZigZag(0),
                });

        private static Dictionary<string, Value> Props(string name, double sortKey)
            => new Dictionary<string, Value>
            {
                ["name"] = Value.String(name),
                ["sk"]   = Value.Number(sortKey),
            };
    }
}
