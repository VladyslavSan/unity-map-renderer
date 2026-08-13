// Unity EditMode only — drives SymbolFeatureExtractor and the Burst Waist-1 materializer over native
// containers. NOT registered in Tools/core-tests.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// IR stage B4: the teeth on moving <c>SymbolFeatureExtractor</c> off its own managed
    /// <c>MvtGeometry.Decode</c> and onto the shared <see cref="TileGeometryBuffers"/>.
    ///
    /// <para><b>Why a differential oracle, and why NOT <c>SymbolProcessorParityTests</c>.</b> That suite's two
    /// arms BOTH run through <c>StyledSymbolTileBuilder.ExtractLayers</c> → <c>SymbolFeatureExtractor.Extract</c>
    /// — the exact code B4 changes — so B4's rewire lands identically in both and the tooth cannot disagree
    /// about it, whatever it breaks. (Its own doc states the premise: "only ExtractLayers/ShapeAsync, <i>which
    /// A3 does not modify</i>, are shared." B4 modifies them; that premise expires with this stage.) It stays a
    /// valuable REGRESSION tooth and stays green unedited, but it is not B4's acceptance tooth. T1 below is:
    /// arm A is <c>MvtGeometry.Decode</c> — the OLD implementation, running live, in code B4 does not touch —
    /// and arm B is the materializer plus a span read. The two arms share no helper, so the oracle can
    /// genuinely disagree: at feature f, path p, point i.</para>
    ///
    /// <para><b>Landmine #2 — symbol is the consumer that finally OBSERVES the unfiltered buffer.</b> B1
    /// measured that a short-ring filter in the shared decode stage reds NOTHING for fill (ring assembly
    /// re-filters); B3 measured the same for line (<c>LineRibbonJob</c> returns early below 2 points). Symbol's
    /// point branch has <b>no length filter at all</b>, so a 1-point path is a real, rendered label — T2 is the
    /// instrument those two stages could not build.</para>
    /// </summary>
    [TestFixture]
    public class SymbolBufferParityTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles()
        {
            TestDecodedTiles.DisposeAll();
            _decodedFixture = null;
        }
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double FixtureExtent = 4096.0;
        private const uint   SyntheticExtent = 4096;
        private static readonly TileId SyntheticTileId = new TileId { Z = 1, X = 0, Y = 0 };

        // ── T1: the differential oracle ─────────────────────────────────────────────────────────────

        /// <summary>
        /// T1 — the per-feature path list the extractor iterates, read out of the shared buffer, is
        /// element-wise identical to the one the OLD managed decoder produces for the same selected features,
        /// running live in the same process. Everything downstream of that path list is literally unchanged
        /// code (<c>LineCurvatureSubdivision.Subdivide</c>, <c>LineAnchorPlacement.Compute</c>,
        /// <c>KeepAnchorsInsideTile</c>, <c>ProjectPath</c>, every emit and every style evaluation), so
        /// path-level equality is label-level equality; the ~75 symbol extraction suites running unedited are
        /// the end-to-end confirmation of that implication.
        /// <para>Run over BOTH fixture layers: <c>centroids</c> is the only one carrying 1-point paths (the
        /// case line's oracle could not have) and <c>geolines</c> the only one carrying a feature with more
        /// than one path.</para>
        /// <para>Exact comparison, no tolerance: both decoders accumulate <c>long</c> deltas and emit
        /// <c>(double)</c> of integer magnitudes far inside <c>double</c>'s exact range, so the vertices are
        /// bit-identical. A tolerance here would be a weakened tooth.</para>
        /// <para><b>What this tooth does NOT observe, measured not assumed.</b> Arm B <i>transcribes</i> the
        /// production bucketing and span read rather than calling them, so a defect injected into
        /// <c>Extract</c>'s own <c>ringStart</c>/<c>ringOrder</c>/<c>CopyRing</c> leaves this test green — the
        /// RED sweep measured exactly that (reversed within-feature order, an off-by-one path count, and a
        /// zero-length ring copy all reddened dozens of symbol suites and none of them reddened this test).
        /// The claim here is therefore about the DECODERS — that the Burst job and the managed decoder agree
        /// bit-for-bit, in order, unfiltered — which is the substance of the swap, and for which this tooth IS
        /// armed (a short-ring filter in the shared stage reds it). Production's own path ordering is observed
        /// by <see cref="SymbolLabelOrder_WithinAFeature_FollowsDecodeOrder"/> below.</para>
        /// </summary>
        [Test]
        public void SymbolPaths_FromTheSharedBuffer_MatchTheManagedDecodeOracle()
        {
            var kindsSeen = new HashSet<TileGeometryType>();
            int sawLengthOne = 0, sawMultiPathFeature = 0, totalPoints = 0, totalEntries = 0;
            bool anyNonZeroFeatureIdx = false;

            foreach (string sourceLayer in new[] { "centroids", "geolines" })
            {
                MvtFixtureStreams.Layer fixture = MvtFixtureStreams.ReadLayer(LoadFixture(), sourceLayer);
                Assert.IsNotNull(fixture, $"the fixture must carry a '{sourceLayer}' layer");
                ITileLayer decodedLayer = DecodedFixture().GetLayer(sourceLayer);
                Assert.IsNotNull(decodedLayer, $"the decoded fixture must carry a '{sourceLayer}' layer");
                IReadOnlyList<IFeature> features = SelectedFeatures(sourceLayer);
                Assert.AreEqual(fixture.Kinds.Count, features.Count,
                    "this fixture's symbol layer has no filter, so the selection IS the whole source-layer — " +
                    "arm A (the independent fixture reader) and arm B (production) must therefore index the " +
                    "same feature ordinals");
                Assert.Greater(features.Count, 0,
                    $"precondition: the '{sourceLayer}' layer must select > 0 features");

                // ── Arm A — the OLD implementation, executing now. UNFILTERED: symbol's point branch has no
                //    length filter, so a >= 2 filter here would hide exactly the case this stage adds.
                var oracle = new List<(int featureIdx, double2[] points)>();
                for (int fi = 0; fi < features.Count; fi++)
                {
                    List<List<double2>> paths = MvtGeometry.Decode(fixture.Commands[fi]);
                    if (paths == null) continue;
                    foreach (List<double2> path in paths)
                        oracle.Add((fi, path.ToArray()));
                }

                // ── Arm B — the production path: the DECODED LAYER's own buffer (IR C1 P3 — the object a
                //    real consumer borrows), the production bucketing, and a span read. Borrowed, so nothing
                //    is disposed here.
                var actual = new List<(int featureIdx, double2[] points)>();
                TileGeometryBuffers geometry = decodedLayer.Geometry;
                {
                    Assert.IsTrue(geometry.IsCreated, "precondition: the decoded layer carries a buffer");

                    int featureCount = features.Count;
                    var ringStart = new int[featureCount + 1];
                    for (int r = 0; r < geometry.RingCount; r++) ringStart[geometry.RingFeatureIdx[r] + 1]++;
                    for (int i = 0; i < featureCount; i++) ringStart[i + 1] += ringStart[i];
                    var ringOrder = new int[geometry.RingCount];
                    var cursor    = (int[])ringStart.Clone();
                    for (int r = 0; r < geometry.RingCount; r++) ringOrder[cursor[geometry.RingFeatureIdx[r]]++] = r;

                    for (int f = 0; f < featureCount; f++)
                    {
                        int pathCount = ringStart[f + 1] - ringStart[f];
                        for (int p = 0; p < pathCount; p++)
                        {
                            int r     = ringOrder[ringStart[f] + p];
                            int start = geometry.RingOffsets[r];
                            int n     = geometry.RingOffsets[r + 1] - start;
                            var points = new double2[n];
                            for (int k = 0; k < n; k++) points[k] = geometry.Vertices[start + k];
                            actual.Add((f, points));
                        }
                    }
                }

                // ── Accumulate the non-vacuity evidence across both layers (asserted once, below).
                var pathsPerFeature = new Dictionary<int, int>();
                foreach ((int featureIdx, double2[] points) entry in oracle)
                {
                    totalEntries++;
                    totalPoints += entry.points.Length;
                    if (entry.points.Length == 1) sawLengthOne++;
                    if (entry.featureIdx != 0) anyNonZeroFeatureIdx = true;
                    pathsPerFeature.TryGetValue(entry.featureIdx, out int c);
                    pathsPerFeature[entry.featureIdx] = c + 1;
                }
                foreach (KeyValuePair<int, int> kv in pathsPerFeature)
                    if (kv.Value > 1) sawMultiPathFeature++;
                foreach (IFeature feature in features) kindsSeen.Add(feature.GeometryType);

                // ── The comparison.
                Assert.AreEqual(oracle.Count, actual.Count,
                    $"'{sourceLayer}': the shared buffer must yield exactly the same number of paths as the " +
                    "managed decoder — unfiltered, including 1-point paths");

                for (int i = 0; i < oracle.Count; i++)
                {
                    Assert.AreEqual(oracle[i].featureIdx, actual[i].featureIdx,
                        $"'{sourceLayer}': path {i} must belong to the same feature in both arms — " +
                        "feature-then-path order is the contract RingFeatureIdx joins through, and the " +
                        "extractor's per-tile `ordinal` makes it observable output");
                    Assert.AreEqual(oracle[i].points.Length, actual[i].points.Length,
                        $"'{sourceLayer}': path {i} must have the same point count in both arms");

                    double2[] expected = oracle[i].points;
                    double2[] got      = actual[i].points;
                    for (int k = 0; k < expected.Length; k++)
                    {
                        Assert.AreEqual(expected[k].x, got[k].x,
                            $"'{sourceLayer}': path {i} point {k} x must be BIT-identical between the " +
                            "managed and Burst decoders");
                        Assert.AreEqual(expected[k].y, got[k].y,
                            $"'{sourceLayer}': path {i} point {k} y must be BIT-identical between the " +
                            "managed and Burst decoders");
                    }
                }
            }

            // ── Non-vacuity, all measured against the fixture before being asserted: a corpus that could not
            //    tell the arms apart would make every equality above true for the wrong reason.
            Assert.Greater(totalEntries, 200,
                "precondition: the two layers must contribute real path corpora, not a handful");
            Assert.Greater(totalPoints, 50,
                "precondition: the fixture must carry real geometry, not a handful of points");
            Assert.Greater(sawLengthOne, 0,
                "precondition: at least one path of length EXACTLY 1 must be compared — the Point case line's " +
                "oracle could not have, and the case landmine #2 is about");
            Assert.Greater(sawMultiPathFeature, 0,
                "precondition: at least one feature must contribute MORE THAN ONE path, or path ordering " +
                "WITHIN a feature is not pinned — only ordering across features would be");
            Assert.IsTrue(anyNonZeroFeatureIdx,
                "precondition: the path→feature join must be exercised by a non-zero index");
            Assert.IsTrue(kindsSeen.Contains(TileGeometryType.Point),
                "precondition: Point features must be among those compared");
            Assert.IsTrue(kindsSeen.Contains(TileGeometryType.LineString),
                "precondition: LineString features must be among those compared");
        }

        // ── T8: production's OWN path ordering, through Extract ─────────────────────────────────────

        /// <summary>
        /// T8 — the labels <c>Extract</c> emits for one feature follow that feature's paths in DECODE ORDER,
        /// and there is one per path. This observes <c>Extract</c>'s own <c>ringStart</c>/<c>ringOrder</c>
        /// bucketing and <c>CopyRing</c>, which T1 cannot: T1 transcribes both into the test, so an injection
        /// into production leaves it green. Added after the RED sweep measured that blind spot rather than
        /// predicted it.
        /// <para>Path order is observable OUTPUT, not an implementation detail: the extractor's per-tile
        /// <c>ordinal</c> becomes <c>SymbolLabel.FeatureIndex</c>, the stable S20 tiebreak, so a reordering
        /// silently changes which label wins a collision.</para>
        /// <para>Also pins <c>RingCapacity == RingCount</c> for an MVT-materialized buffer. That identity is
        /// why substituting one for the other in the bucketing is currently an arithmetic no-op
        /// (<c>MvtGeometryMaterializer</c> sizes exactly from <c>PrecountRingsAndVertices</c>) — and it is the
        /// assertion that goes RED on the day a producer starts over-allocating, which is the day the
        /// capacity-vs-count trap becomes real. Without it, that trap has no observer at all.</para>
        /// </summary>
        [Test]
        public void SymbolLabelOrder_WithinAFeature_FollowsDecodeOrder()
        {
            // Three DISTINCT points in one MultiPoint feature ⇒ three 1-point paths in one feature, so the
            // ordering under test is WITHIN a feature, not across features.
            var authored = new[]
            {
                new double2(400, 500), new double2(1600, 1700), new double2(2800, 2900),
            };
            IFeature feature = PointFeature(MultiPointGeometry(authored));

            TileGeometryBuffers geometry = Materialize(new[] { feature }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.AreEqual(3, geometry.RingCount,
                    "precondition: one feature contributing THREE paths — with one path this test could not " +
                    "distinguish an ordering defect from a counting one");
                Assert.AreEqual(geometry.RingCount, geometry.RingCapacity,
                    "MvtGeometryMaterializer sizes EXACTLY (PrecountRingsAndVertices), so capacity equals " +
                    "count for every buffer it mints. This is why iterating RingCapacity instead of RingCount " +
                    "is currently a no-op — and this assertion is what goes RED the day a producer starts " +
                    "over-allocating, i.e. the day that substitution becomes a real out-of-range bug.");
            }
            finally
            {
                geometry.Dispose();
            }

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(
                PointLabelLayer(), TileOf(feature), SyntheticTileId, 0.0, new WebMercatorProjection(), labels);

            Assert.AreEqual(authored.Length, labels.Count,
                "one label per path — a dropped or duplicated path is a counting defect in the bucketing");

            var projection = new WebMercatorProjection();
            for (int i = 0; i < authored.Length; i++)
            {
                double2 lonLat = SyntheticTileId.ToLonLat(authored[i].x, authored[i].y, SyntheticExtent);
                double3 expected = projection.Project(
                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });

                Assert.AreEqual(expected.x, labels[i].AnchorRender.x, 1e-6,
                    $"label {i} must be the {i}th AUTHORED path, in decode order — the bucketing is a stable " +
                    "counting sort precisely so a feature's paths keep the order the decoder produced them in, " +
                    "and FeatureIndex (the S20 collision tiebreak) is stamped from that order");
                Assert.AreEqual(expected.y, labels[i].AnchorRender.y, 1e-6, $"label {i} anchor.y");
                // NOT a second pin on path order: `ordinal++` is stamped at each emit site and labels are
                // appended in emission order, so this holds for ANY path ordering. It pins emission order
                // only; the anchor comparison above is the clause that discriminates.
                Assert.AreEqual(i, labels[i].FeatureIndex,
                    $"label {i}'s per-tile ordinal must follow EMISSION order (not path order)");
            }
        }

        // ── T2: landmine #2, the observer line could not build ──────────────────────────────────────

        /// <summary>
        /// T2 — a Point feature whose command stream is a single <c>MoveTo</c> of ONE point still emits
        /// exactly one label, at that point's projection. This is the instrument B1 and B3 both recorded as
        /// missing: fill re-filters short rings downstream and line returns early below 2 points, so a
        /// <c>&lt; 2</c>-point filter fused into the shared decode stage was invisible in both. Here it deletes
        /// a rendered label.
        /// <para>The control — a <c>MoveTo</c> of THREE points expecting THREE labels — is what makes "one
        /// label" a statement about the boundary value rather than an artefact of the emitter collapsing
        /// anything.</para>
        /// </summary>
        [Test]
        public void SymbolPointFeature_OnePointPath_StillEmitsOneLabel()
        {
            var onePoint    = new double2(1000, 1500);
            var threePoints = new[] { new double2(600, 700), new double2(1200, 1400), new double2(2400, 2800) };

            IFeature single = PointFeature(MultiPointGeometry(onePoint));
            IFeature triple = PointFeature(MultiPointGeometry(threePoints));

            // Non-vacuity: the buffer really does carry ONE ring whose span is EXACTLY 1, so the tooth is
            // provably at the boundary value rather than on a longer path that a filter would spare.
            TileGeometryBuffers geometry = Materialize(new[] { single }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the single-point selection materialized");
                Assert.AreEqual(1, geometry.RingCount, "precondition: exactly one ring");
                Assert.AreEqual(1, geometry.RingOffsets[1] - geometry.RingOffsets[0],
                    "precondition: that ring's span must be EXACTLY 1 point — the boundary value a " +
                    "< 2 filter in the shared decode stage would delete");
            }
            finally
            {
                geometry.Dispose();
            }

            var oneLabel = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(
                PointLabelLayer(), TileOf(single), SyntheticTileId, 0.0, new WebMercatorProjection(), oneLabel);
            Assert.AreEqual(1, oneLabel.Count,
                "a 1-point path is a REAL label: symbol's point branch has no length filter at all, so any " +
                "short-ring filter in MvtGeometryMaterializer, MvtDecodeJob or any shared stage deletes it");

            // …and it is at that point's real projection, not a stub.
            double2 lonLat = SyntheticTileId.ToLonLat(onePoint.x, onePoint.y, SyntheticExtent);
            double3 expected = new WebMercatorProjection().Project(
                new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            Assert.AreEqual(expected.x, oneLabel[0].AnchorRender.x, 1e-6);
            Assert.AreEqual(expected.y, oneLabel[0].AnchorRender.y, 1e-6);
            Assert.AreEqual(expected.z, oneLabel[0].AnchorRender.z, 1e-6);

            // Control: a MultiPoint MoveTo of 3 emits 3 — "one label" above is the boundary value, not a
            // collapse.
            var threeLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(
                PointLabelLayer(), TileOf(triple), SyntheticTileId, 0.0, new WebMercatorProjection(), threeLabels);
            Assert.AreEqual(3, threeLabels.Count,
                "control: a MoveTo of three points is three 1-point paths and therefore three labels");
        }

        // ── T3: symbol's OWN length threshold, < 2 and not < 3 ──────────────────────────────────────

        /// <summary>
        /// T3 — under LINE placement, a 2-point polyline places anchors (symbol's line filter is
        /// <c>&lt; 2</c>, not fill's <c>&lt; 3</c>), while a 1-point path under the SAME layer places none.
        /// The pair pins WHICH side of the boundary each length falls on, not merely that geometry appeared.
        /// Three consumers, three thresholds (<c>&lt; 3</c> / <c>&lt; 2</c> / none), one unfiltered buffer.
        /// </summary>
        [Test]
        public void SymbolLineFeature_TwoPointPath_PlacesAnchors()
        {
            IFeature twoPoint = LineFeature(new double2(500, 500), new double2(3500, 3500));
            IFeature onePoint = LineFeature(new double2(500, 500));

            // Non-vacuity: spans of EXACTLY 2 and EXACTLY 1 reach the consumer unfiltered.
            AssertSingleRingSpan(twoPoint, 2);
            AssertSingleRingSpan(onePoint, 1);

            var twoPointLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(
                LineLabelLayer(), TileOf(twoPoint), SyntheticTileId, 0.0, new WebMercatorProjection(),
                twoPointLabels);
            Assert.Greater(twoPointLabels.Count, 0,
                "a 2-point polyline has exactly one segment to place along — symbol's line filter is < 2, " +
                "not fill's < 3, and moving it to < 3 would silently delete these labels");

            var onePointLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(
                LineLabelLayer(), TileOf(onePoint), SyntheticTileId, 0.0, new WebMercatorProjection(),
                onePointLabels);
            Assert.AreEqual(0, onePointLabels.Count,
                "control: a 1-point path has no segment, so the LINE branch's < 2 filter drops it — this is " +
                "what makes the assertion above about the boundary rather than about geometry existing");
        }

        // ── T5: ownership, on the exit paths ────────────────────────────────────────────────────────

        /// <summary>
        /// T5 — the three selections that exercise <c>Extract</c>'s non-obvious buffer states: none throws and
        /// none emits, and each asserts WHY it emitted nothing, so "zero labels" can never be mistaken for
        /// "the buffer was empty".
        /// <list type="number">
        /// <item>zero selected features ⇒ <c>Materialize</c> returns <c>default</c>, so <c>Dispose()</c> is a
        /// no-op, not a throw;</item>
        /// <item>one Polygon feature with a genuine multi-point ring ⇒ the buffer IS created with
        /// <c>RingCount &gt; 0</c>, so the zero labels provably came from <b>symbol's kind gate</b>;</item>
        /// <item>one feature whose <c>Geometry</c> is <c>null</c> ⇒ the buffer IS created (feature count ≥ 1)
        /// with <c>RingCount == 0</c>. B3's dev report recorded its plan claiming <c>IsCreated == false</c>
        /// here and being wrong; this asserts what actually holds.</item>
        /// </list>
        /// </summary>
        [Test]
        public void SymbolExtract_EmptyPolygonAndNullGeometrySelections_EmitNothingAndDoNotThrow()
        {
            // (1) zero selected features — the layer resolves, the filter matches nothing.
            IFeature anyPoint = PointFeature(MultiPointGeometry(new double2(100, 100)));
            TileGeometryBuffers empty = Materialize(Array.Empty<IFeature>(), SyntheticTileId, SyntheticExtent);
            Assert.IsFalse(empty.IsCreated,
                "a zero-feature selection must return default(TileGeometryBuffers) — Dispose on it is a no-op");
            Assert.DoesNotThrow(() => empty.Dispose(), "Dispose on a default buffer early-returns");

            var noneSelected = new List<SymbolStyle.SymbolLabel>();
            Assert.DoesNotThrow(() => SymbolFeatureExtractor.Extract(
                PointLabelLayer("[\"==\",\"nope\",\"nope\"]"), TileOf(anyPoint), SyntheticTileId, 0.0,
                new WebMercatorProjection(), noneSelected));
            Assert.AreEqual(0, noneSelected.Count, "a selection matching nothing emits nothing");

            // (2) an all-Polygon selection — the buffer is real, symbol's kind gate is what rejects it.
            IFeature polygon = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("poly") },
                geometryType: TileGeometryType.Polygon,
                geometry: MultiPointRing(
                    new double2(1000, 1000), new double2(2000, 1000),
                    new double2(2000, 2000), new double2(1000, 2000)));

            TileGeometryBuffers polyBuffer = Materialize(new[] { polygon }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(polyBuffer.IsCreated, "the Polygon selection DOES materialize a buffer");
                Assert.Greater(polyBuffer.RingCount, 0,
                    "…with real rings — so the zero labels below come from symbol's kind gate, not from an " +
                    "empty buffer. Without this the case would be vacuous.");
            }
            finally
            {
                polyBuffer.Dispose();
            }

            var polygonLabels = new List<SymbolStyle.SymbolLabel>();
            Assert.DoesNotThrow(() => SymbolFeatureExtractor.Extract(
                PointLabelLayer(), TileOf(polygon), SyntheticTileId, 0.0, new WebMercatorProjection(),
                polygonLabels));
            Assert.AreEqual(0, polygonLabels.Count, "Polygon is never accepted at any placement (the D2 fence)");

            // (3) a feature whose Geometry is null.
            IFeature nullGeometry = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("ghost") },
                geometryType: TileGeometryType.Point,
                geometry: null);

            TileGeometryBuffers nullBuffer = Materialize(new[] { nullGeometry }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(nullBuffer.IsCreated,
                    "a null Geometry still counts as a feature, so the buffer IS created (feature count >= 1)");
                Assert.AreEqual(0, nullBuffer.RingCount, "…carrying zero rings — the materializer treats null " +
                    "as zero commands");
            }
            finally
            {
                nullBuffer.Dispose();
            }

            var nullLabels = new List<SymbolStyle.SymbolLabel>();
            Assert.DoesNotThrow(() => SymbolFeatureExtractor.Extract(
                PointLabelLayer(), TileOf(nullGeometry), SyntheticTileId, 0.0, new WebMercatorProjection(),
                nullLabels));
            Assert.AreEqual(0, nullLabels.Count, "no geometry, no anchors, no labels");
        }

        // ── Fixture + synthetic plumbing ────────────────────────────────────────────────────────────

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-tile.bytes not found walking up from cwd/AppContext.");
        }

        /// <summary>The features a symbol layer over <paramref name="sourceLayer"/> selects — the SAME list,
        /// in the same order, the extractor materializes and indexes <c>RingFeatureIdx</c> against.</summary>
        private static MvtTile _decodedFixture;

        /// <summary>The decoded fixture, ONE instance per test (released by the fixture's TearDown) — arm B
        /// must read the very buffer a production consumer would borrow, not a re-materialization.</summary>
        private static IDecodedTile DecodedFixture()
            => _decodedFixture ??= TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));

        private static IReadOnlyList<IFeature> SelectedFeatures(string sourceLayer)
            => FeatureSelector.SelectFeatures(
                PointLabelLayer(sourceLayer: sourceLayer), DecodedFixture(), 0.0);

        /// <summary>Arm B's buffer, minted through the REAL producer from synthetic features' command
        /// streams — the same thing the decoder does for a real layer (IR C1 P3).</summary>
        private static TileGeometryBuffers Materialize(
            IReadOnlyList<IFeature> features, TileId tile, double extent)
            => TestTileMeshBuilder.Materialize(features, tile, extent);

        private static void AssertSingleRingSpan(IFeature feature, int expectedSpan)
        {
            TileGeometryBuffers geometry = Materialize(new[] { feature }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the selection materialized");
                Assert.AreEqual(1, geometry.RingCount, "precondition: exactly one ring");
                Assert.AreEqual(expectedSpan, geometry.RingOffsets[1] - geometry.RingOffsets[0],
                    $"precondition: that ring's span must be EXACTLY {expectedSpan} — the buffer is " +
                    "unfiltered, so the length reaching the consumer is the length authored");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        private static SymbolStyle.StyleLayer PointLabelLayer(
            string filterJson = null, string sourceLayer = "probe")
            => new SymbolStyle.StyleLayer
            {
                Id          = "b4-point",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = sourceLayer,
                LayoutJson  = JsonParser.Parse("{\"text-field\":\"{NAME}\"}"),
                Filter      = filterJson != null ? JsonParser.Parse(filterJson) : null,
            };

        private static SymbolStyle.StyleLayer LineLabelLayer()
            => new SymbolStyle.StyleLayer
            {
                Id          = "b4-line",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "probe",
                LayoutJson  = JsonParser.Parse(
                    "{\"text-field\":\"{NAME}\",\"symbol-placement\":\"line\",\"symbol-spacing\":1}"),
            };

        private static IFeature PointFeature(uint[] geometry)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("probe") },
                geometryType: TileGeometryType.Point,
                geometry: geometry);

        private static IFeature LineFeature(params double2[] points)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("probe") },
                geometryType: TileGeometryType.LineString,
                geometry: MultiPointRing(points));

        private static IDecodedTile TileOf(IFeature feature)
            => TestDecodedTiles.Of("probe", SyntheticTileId, new List<IFeature> { feature }, SyntheticExtent);

        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        /// <summary>A single <c>MoveTo</c> of N points — the MultiPoint encoding, which decodes to N separate
        /// ONE-POINT paths (both decoders start a new path per MoveTo point).</summary>
        private static uint[] MultiPointGeometry(params double2[] tilePoints)
        {
            var stream = new List<uint> { 1u | ((uint)tilePoints.Length << 3) }; // MoveTo, count=N
            long cursorX = 0, cursorY = 0;
            foreach (double2 p in tilePoints)
            {
                long x = (long)p.x, y = (long)p.y;
                stream.Add(ZigZagEncode(x - cursorX));
                stream.Add(ZigZagEncode(y - cursorY));
                cursorX = x;
                cursorY = y;
            }
            return stream.ToArray();
        }

        /// <summary>One path of N points: <c>MoveTo</c>×1 + <c>LineTo</c>×(N−1).</summary>
        private static uint[] MultiPointRing(params double2[] tilePoints)
        {
            var stream = new List<uint> { 1u | (1u << 3) }; // MoveTo, count=1
            long cursorX = 0, cursorY = 0;
            void Append(double2 p)
            {
                long x = (long)p.x, y = (long)p.y;
                stream.Add(ZigZagEncode(x - cursorX));
                stream.Add(ZigZagEncode(y - cursorY));
                cursorX = x;
                cursorY = y;
            }
            Append(tilePoints[0]);
            if (tilePoints.Length > 1)
            {
                stream.Add(2u | ((uint)(tilePoints.Length - 1) << 3)); // LineTo, count=N-1
                for (int i = 1; i < tilePoints.Length; i++) Append(tilePoints[i]);
            }
            return stream.ToArray();
        }
    }
}
