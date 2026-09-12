// Unity EditMode only — drives MapView.SetStyle, the TileManager pipeline diff, and the decode lease.

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Json;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using CoreMapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.GeoJsons
{
    /// <summary>
    /// GeoJSON S2 — the source, end to end: <b>T1</b> (an inline geojson source actually renders through
    /// <c>MapView.SetStyle</c>), <b>T3</b> (<c>SourceKey</c> carries the inline-data identity, both
    /// directions) and <b>T5</b> (the slice happens inside <c>GetTile</c>, OFF the main thread, and its
    /// native buffers are freed by the lease's last release).
    /// </summary>
    [TestFixture]
    public class GeoJsonSourceTests
    {
        private static readonly TileId WorldTile = new TileId { Z = 0, X = 0, Y = 0 };

        // Off-main, matching production's desktop policy — these teeth exercise GetTile's/the source's own
        // contract, not the scheduler choice.
        private static readonly IWorkScheduler Scheduler = new ThreadPoolWorkScheduler();

        private const int ProfilerSampleCapacity = 64;

        // Tile-local corners of the authored rectangle, at the extent MapView slices with
        // (GeoJsonSliceOptions.Default). Chosen well inside [0, extent] so no clip, buffered or not, can touch
        // them — the tooth is about the source rendering at all, not about seams.
        private const double RectMin = 1024.0, RectMax = 3072.0;

        // A second rectangle, disjoint from the first, for the restyle arms. Its whole point is that a mesh
        // built from it CANNOT be mistaken for one built from the first.
        private const double OtherMin = 3200.0, OtherMax = 3900.0;

        // ── Fixture builders ──────────────────────────────────────────────────────────────────────────

        private static string RectangleAt(double min, double max)
        {
            double2 nw = WorldTile.ToLonLat(min, min, GeoJsonSliceOptions.DefaultExtent);
            double2 se = WorldTile.ToLonLat(max, max, GeoJsonSliceOptions.DefaultExtent);
            // Tile-local Y grows SOUTHWARD, so the small-Y corner carries the NORTH latitude.
            return GeoJsonTestFixtures.Collection(GeoJsonTestFixtures.Feature(
                "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(nw.x, se.y, se.x, nw.y)}]"));
        }

        /// <summary>A one-source, one-fill-layer style over inline geojson. The style layer deliberately
        /// declares NO <c>source-layer</c> — that is what the Style Spec says for a geojson source, and it is
        /// the exact shape that used to select zero features.</summary>
        private static string StyleWithInlineData(string dataJson) => $@"{{
            ""version"": 8,
            ""sources"": {{ ""geo"": {{ ""type"": ""geojson"", ""data"": {dataJson} }} }},
            ""layers"": [
                {{ ""id"": ""geo-fill"", ""type"": ""fill"", ""source"": ""geo"",
                   ""paint"": {{ ""fill-color"": ""#ff0000"" }} }}
            ]
        }}";

        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("GeoJsonSource");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            // Seed the camera BEFORE any SetStyle: SetStyle builds the render layers at the CURRENT zoom, so
            // a test that seeded afterwards would be styling at whatever the default happened to be.
            view.View.Camera.SetProperties(Cam(0.0));
            view.View.Camera.SyncToCamera();
            return view;
        }

        private static CameraProperties Cam(double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, zoom, 0, 0);

        private static void SpinToCompleted(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }

        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
            }
        }

        /// <summary>The world-space (x, z) the authored tile-local corner projects to, RELATIVE to the tile's
        /// render origin — which is what a tile mesh's vertices are expressed in. Computed through the same
        /// public chain the pipeline uses (tile-local → geodetic → Mercator), never copied from a run.</summary>
        /// <summary>Groups a flat index buffer into triangles.</summary>
        /// <param name="indices">The flat index buffer.</param>
        /// <returns>One three-element array per triangle.</returns>
        private static IEnumerable<int[]> Triples(int[] indices)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
                yield return new[] { indices[i], indices[i + 1], indices[i + 2] };
        }

        private static double2 ExpectedWorldXZ(double tileLocalX, double tileLocalY)
        {
            double2 lonLat = WorldTile.ToLonLat(tileLocalX, tileLocalY, GeoJsonSliceOptions.DefaultExtent);
            double2 merc   = WebMercator.FromLonLat(
                new GeoCoordinate3D { Longitude = lonLat.x, Latitude = lonLat.y, Altitude = 0 });
            var (boundsMin, _) = WorldTile.MercatorBounds();
            return new double2(merc.x - boundsMin.x, merc.y - boundsMin.y);
        }

        /// <summary>The total unsigned area of a mesh's INDEXED triangles in the ground plane (x, z). Read
        /// through the index buffer, not the vertex list: a collapsed or degenerately-wound fan has vertices
        /// in plausible places and no area, and only the indices reveal it.</summary>
        private static double TriangleArea(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            int[]     indices  = mesh.triangles;

            double total = 0.0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
                total += 0.5 * math.abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z));
            }
            return total;
        }

        // ── T1 · a GeoJSON source renders through the real loop ───────────────────────────────────────

        /// <summary>
        /// <b>T1 (the headline)</b> — a style whose only source is inline geojson renders a fill mesh, driven
        /// through the production <c>MapView.SetStyle</c> → <c>BuildSourceSpecs</c> path (not a hand-minted
        /// <c>SourceSpec</c>, or the wiring would be unobserved).
        ///
        /// <para>The positional assertion is the part that makes it more than a smoke test: the mesh's four
        /// vertices must sit on the AUTHORED rectangle's corners, computed independently through
        /// <c>ToLonLat</c> → <c>WebMercator.FromLonLat</c>. A pipeline that rendered <i>something</i> — the
        /// whole tile, a mis-scaled quad, the wrong feature — fails it.</para>
        ///
        /// <para><b>Coverage, not membership.</b> The corners are consumed ONE-TO-ONE and the triangulated
        /// area is checked against the rectangle's. "Each vertex is near SOME corner" is a strictly weaker
        /// claim that a mesh with all four vertices collapsed onto one valid corner satisfies while
        /// rendering a degenerate polygon — measured, not reasoned: a fill writer injected to emit
        /// <c>WorldPositions[0]</c> for every vertex passed the membership form of this test.</para>
        ///
        /// <para><b>Partition, not summed area.</b> The indexed triangles are also required to TILE the quad:
        /// three distinct corners each, all four corners referenced, and the shared edge an authored
        /// diagonal. Summing unsigned triangle areas cannot make that claim — emit both index triples as the
        /// same three-corner half and the two half-areas add to exactly the rectangle's, with every vertex
        /// still present and still consumed one-to-one, while half the polygon renders twice.</para>
        ///
        /// <para>At <c>cbe1d7d9</c> this fails at the first step: <c>BuildSourceSpecs</c> has no geojson
        /// branch, so the source "resolved to no tiles" and no pipeline existed at all.</para>
        /// </summary>
        [Test]
        public void T1_AnInlineGeoJsonSource_RendersItsAuthoredPolygon()
        {
            var view = NewView(out var go);
            int docFetches = 0, factoryCalls = 0;
            view.View.DocumentLoaderOverride    = (uri, ct) => { Interlocked.Increment(ref docFetches); return UniTask.FromResult(""); };
            view.View.TileSourceFactoryOverride = template => { Interlocked.Increment(ref factoryCalls); return TestDataSource.Absent(); };

            try
            {
                SpinToCompleted(view.SetStyle(
                    StyleParser.Parse(StyleWithInlineData(RectangleAt(RectMin, RectMax))), "geojson"));
                PumpUntilSettled(view);

                Assert.AreEqual(0, docFetches,  "a geojson source must never attempt a TileJSON round trip");
                Assert.AreEqual(0, factoryCalls, "…nor construct a byte fetcher: it has no bytes");

                Assert.IsTrue(view.AllTilesSettled(), "the tile must settle");
                Mesh[] meshes = view.GetTileMeshes(WorldTile);
                Assert.IsNotNull(meshes, "the inline geojson source must have produced a tile mesh");
                Assert.AreEqual(1, meshes.Length, "one fill layer ⇒ one layer mesh");
                Assert.IsNotNull(meshes[0]);
                // 4 interior + 8 band: the rectangle triangulates to a 4-vertex quad (Mercator, no
                // subdivision), and the outward boundary band appends two vertices per ring vertex AFTER it.
                // Every claim below is about the INTERIOR quad, so each reads the prefix — a band vertex sits
                // at a ring corner too and would exhaust the one-to-one corner pool.
                const int interiorVertexCount = 4;
                Assert.AreEqual(interiorVertexCount + 2 * interiorVertexCount, meshes[0].vertexCount,
                    "the authored rectangle triangulates to a 4-vertex quad (Mercator, no subdivision), " +
                    "plus the outward boundary band's two vertices per ring vertex");

                // One tile unit at this extent, in world metres — the quantization bound, derived rather
                // than guessed, and multiplied by 2 to cover the round trip through geodetic.
                double tileUnitWorld = 2.0 * WebMercator.WorldExtent / GeoJsonSliceOptions.DefaultExtent;
                double tolerance     = 2.0 * tileUnitWorld;

                var expected = new List<double2>
                {
                    ExpectedWorldXZ(RectMin, RectMin), ExpectedWorldXZ(RectMax, RectMin),
                    ExpectedWorldXZ(RectMax, RectMax), ExpectedWorldXZ(RectMin, RectMax),
                };
                // A whole tile edge must be far outside the tolerance, or "on the authored corners" would be
                // satisfiable by "anywhere in the tile".
                Assert.Less(tolerance * 100.0, 2.0 * WebMercator.WorldExtent,
                    "the tolerance must be orders of magnitude below a tile edge, or it could pass by looseness");

                // Consumed one-to-one: a corner already claimed by an earlier vertex is no longer available,
                // so four vertices can only pass by covering four DISTINCT corners. WHICH corner each mesh
                // slot claimed is recorded, because the topology arm below has to read the index buffer in
                // terms of AUTHORED corners rather than of mesh slots.
                Vector3[] meshVertices = meshes[0].vertices;
                var authoredCornerOf   = new int[interiorVertexCount];
                var unclaimed          = new List<double2>(expected);
                var unclaimedCorner    = new List<int>();
                for (int c = 0; c < expected.Count; c++) unclaimedCorner.Add(c);

                for (int v = 0; v < interiorVertexCount; v++)
                {
                    Vector3 vertex  = meshVertices[v];
                    int     claimed = -1;
                    for (int c = 0; c < unclaimed.Count; c++)
                        if (math.abs(vertex.x - unclaimed[c].x) <= tolerance &&
                            math.abs(vertex.z - unclaimed[c].y) <= tolerance) { claimed = c; break; }

                    Assert.GreaterOrEqual(claimed, 0,
                        $"mesh vertex ({vertex.x}, {vertex.z}) is not on any AS-YET-UNCLAIMED authored corner " +
                        $"of the polygon (expected one of {string.Join("; ", expected)}, still unclaimed: " +
                        $"{string.Join("; ", unclaimed)}). The source rendering SOMETHING is not the claim — " +
                        "it must render the authored geometry, at the authored place, and a corner may only " +
                        "be matched once, or four vertices collapsed onto one corner would pass.");
                    authoredCornerOf[v] = unclaimedCorner[claimed];
                    unclaimed.RemoveAt(claimed);
                    unclaimedCorner.RemoveAt(claimed);
                }
                Assert.IsEmpty(unclaimed,
                    $"every authored corner must be covered: {string.Join("; ", unclaimed)} was not. With " +
                    "the four-vertex count pinned above and one-to-one consumption this is already implied " +
                    "— it is asserted anyway because it names the missing corner, which neither the count " +
                    "nor the per-vertex arm does. It does NOT survive a fixture emitting more vertices: " +
                    "AreEqual(4, vertexCount) fires first, and a fifth vertex would trip the per-vertex arm " +
                    "against an emptied pool.");

                // ── The triangles must PARTITION the quad, which the summed area below cannot see. ──
                // Both triples emitted as the SAME three-corner half leaves every vertex present, every
                // corner consumed one-to-one, and two half-area triangles summing to exactly the whole —
                // while rendering half the polygon, twice. Only the index TOPOLOGY distinguishes it.
                // The INTERIOR triangles only — a band triangle is one with a vertex past the interior
                // prefix, and it has no authored corner to name.
                var interiorIndices = new List<int>();
                foreach (int[] triangle in Triples(meshes[0].triangles))
                    if (triangle[0] < interiorVertexCount && triangle[1] < interiorVertexCount && triangle[2] < interiorVertexCount)
                        interiorIndices.AddRange(triangle);
                int[] meshIndices = interiorIndices.ToArray();
                Assert.AreEqual(6, meshIndices.Length,
                    "a quad is two triangles, so six indices — asserted before the topology arms read them, " +
                    "or a fan of some other length would make those arms mean something else");
                Assert.AreEqual(6 + 6 * interiorVertexCount, meshes[0].triangles.Length,
                    "…and the band contributes six indices per ring edge on top, which is what makes the " +
                    "filter above a filter rather than a no-op");

                var referencedCorners = new HashSet<int>();
                var triangleCorners   = new List<HashSet<int>>();
                for (int t = 0; t + 2 < meshIndices.Length; t += 3)
                {
                    var corners = new HashSet<int>
                    {
                        authoredCornerOf[meshIndices[t]],
                        authoredCornerOf[meshIndices[t + 1]],
                        authoredCornerOf[meshIndices[t + 2]],
                    };
                    Assert.AreEqual(3, corners.Count,
                        $"triangle {t / 3} names only {corners.Count} distinct authored corners — it is " +
                        "degenerate, an edge or a point pretending to be a face");
                    triangleCorners.Add(corners);
                    referencedCorners.UnionWith(corners);
                }

                Assert.AreEqual(4, referencedCorners.Count,
                    "every one of the four authored corners must be REFERENCED by the index buffer. This is " +
                    "the arm the area sum cannot make: two triples naming the same three corners leave the " +
                    "fourth vertex present-but-unindexed, and half the rectangle's area counted twice adds " +
                    "up to exactly the whole. The polygon on screen would be a triangle.");

                var sharedCorners = new HashSet<int>(triangleCorners[0]);
                sharedCorners.IntersectWith(triangleCorners[1]);
                Assert.AreEqual(2, sharedCorners.Count,
                    "the two triangles must meet on exactly one EDGE — two shared corners. Three means they " +
                    "are the same face; fewer means they do not tile a quad at all.");

                var sharedPair = new List<int>(sharedCorners);
                sharedPair.Sort();
                Assert.AreEqual(2, sharedPair[1] - sharedPair[0],
                    $"the shared edge must be a DIAGONAL of the authored rectangle — corners two apart in " +
                    $"ring order — not a side. Corners {sharedPair[0]} and {sharedPair[1]} are adjacent, " +
                    "which means the two triangles fold over one side of the quad and cover it twice while " +
                    "leaving the other half bare.");

                // …and the quad encloses the authored AREA. Position alone cannot see a fan wound so that the
                // triangles cancel or overlap; a degenerate mesh is zero here, not merely mis-placed.
                double2 spanMin = ExpectedWorldXZ(RectMin, RectMin);
                double2 spanMax = ExpectedWorldXZ(RectMax, RectMax);
                double expectedArea = math.abs((spanMax.x - spanMin.x) * (spanMax.y - spanMin.y));
                Assert.Greater(expectedArea, 0.0, "precondition: the authored rectangle has area at all");

                Assert.AreEqual(expectedArea, TriangleArea(meshes[0]), expectedArea * 0.01,
                    "the indexed triangles must enclose the AUTHORED area, within 1%. Zero here is the " +
                    "collapsed-mesh failure the positional arms above are deliberately not asked to catch; " +
                    "twice it would be a doubled or self-overlapping fan.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// <b>T1 anti-vacuity.</b> The same style with an EMPTY FeatureCollection must render nothing. Without
        /// this arm the tooth above cannot tell "rendered the dataset" from "rendered anything at all".
        ///
        /// <para><b>Measured limitation, stated so it is not over-read.</b> This arm is satisfied UPSTREAM of
        /// the decoder: an empty dataset has an inverted bounding box, so <c>GetTile</c>'s probe rejects every
        /// tile and no decode is even attempted. Verified by injection — a decoder made to emit a full-extent
        /// quad whenever its slice is empty leaves this arm GREEN. So the "renders anything" hole is closed by
        /// two teeth together, not by this one: this arm observes the PROBE (its partner is
        /// <see cref="GetTile_AnEmptyDataset_RejectsEveryTile"/>), and
        /// <c>GeoJsonTileDecoderTests.T4_AnEmptySlice_YieldsATileWithNoLayerAtAll</c> observes the DECODER —
        /// that one is what the injected quad reds.</para>
        /// </summary>
        [Test]
        public void T1_AnEmptyInlineDataset_RendersNothing()
        {
            var view = NewView(out var go);
            try
            {
                SpinToCompleted(view.SetStyle(StyleParser.Parse(StyleWithInlineData(
                    @"{""type"":""FeatureCollection"",""features"":[]}")), "geojson-empty"));
                PumpUntilSettled(view);

                Mesh[] meshes = view.GetTileMeshes(WorldTile);
                int vertices = 0;
                if (meshes != null)
                    foreach (Mesh mesh in meshes)
                        if (mesh != null) vertices += mesh.vertexCount;

                Assert.AreEqual(0, vertices,
                    "an empty FeatureCollection must produce ZERO vertices. If it produces any, the sibling " +
                    "tooth is not observing the dataset — it is observing that the pipeline draws something.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary>A geojson source whose <c>data</c> is a URL string (not supported in v1) or malformed must
        /// be SKIPPED with a warning, never fault <c>SetStyle</c> — the same failure isolation the TileJSON
        /// path already has.
        ///
        /// <para><b>"Skipped" is observed, not inferred from the absence of a throw.</b> Not throwing is
        /// satisfied by an implementation that silently substituted a different source — a byte fetcher over
        /// the URL, say, or an empty dataset standing in. So the arms below also require that NO source
        /// factory and NO document loader was reached, that nothing rendered, and — the arm the other three
        /// cannot make — that <b>no pipeline owns a feature source at all</b>.</para>
        ///
        /// <para><b>Why that last arm is not redundant.</b> A local <c>GeoJsonTileFeatureSource</c> is
        /// constructed inside <c>BuildSourceSpecs</c> through neither override, and an EMPTY
        /// <c>FeatureCollection</c> substituted for the bad <c>data</c> fetches nothing, builds no byte
        /// source and renders nothing — its inverted bbox makes the probe reject every tile. Absence of
        /// output is therefore not absence of wiring, and only a count of wired sources separates
        /// them.</para></summary>
        [Test]
        public void UnsupportedOrMalformedData_IsSkipped_NotThrown()
        {
            var view = NewView(out var go);
            int docFetches = 0, factoryCalls = 0;
            view.View.DocumentLoaderOverride    = (uri, ct) => { Interlocked.Increment(ref docFetches); return UniTask.FromResult(""); };
            view.View.TileSourceFactoryOverride = template => { Interlocked.Increment(ref factoryCalls); return TestDataSource.Absent(); };

            try
            {
                Assert.DoesNotThrow(() => SpinToCompleted(view.SetStyle(
                    StyleParser.Parse(StyleWithInlineData(@"""https://example.com/data.geojson""")), "url-data")),
                    "URL-valued `data` is not supported yet and must be skipped, not thrown");
                AssertNothingWasWired(view, () => docFetches, () => factoryCalls, "a URL-valued `data`");

                Assert.DoesNotThrow(() => SpinToCompleted(view.SetStyle(
                    StyleParser.Parse(StyleWithInlineData(@"{""type"":""Nonsense""}")), "bad-data")),
                    "a malformed inline dataset must be skipped, not thrown — a fixture typo must not take " +
                    "the whole style down");
                AssertNothingWasWired(view, () => docFetches, () => factoryCalls, "a malformed inline dataset");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary>The skip contract, positively: no source is wired, nothing was fetched, nothing was
        /// constructed, nothing rendered. Pumped first — a substituted source needs the chance to produce
        /// something before the absence of output means anything. The frame budget is small on purpose: a
        /// skipped source has no pipeline, so there is nothing to WAIT for, only something to give a wrong
        /// implementation room to do.
        ///
        /// <para>The counters arrive as <c>Func&lt;int&gt;</c>, not as <c>int</c>: passed by value they are
        /// evaluated at the CALL SITE, so the pump this helper performs could not affect them and a
        /// substitution that fetched lazily at tile-load time would be invisible to both arms.</para>
        /// </summary>
        private static void AssertNothingWasWired(
            MapView view, System.Func<int> docFetches, System.Func<int> factoryCalls, string what)
        {
            PumpUntilSettled(view, maxFrames: 200);

            Assert.AreEqual(0, docFetches(), $"{what} must not trigger a TileJSON round trip — skipping it " +
                                             "means it was never wired, not that it was wired to something else");
            Assert.AreEqual(0, factoryCalls(), $"…nor construct a byte fetcher for {what}");

            Mesh[] meshes = view.GetTileMeshes(WorldTile);
            int vertices = 0;
            if (meshes != null)
                foreach (Mesh mesh in meshes)
                    if (mesh != null) vertices += mesh.vertexCount;

            Assert.AreEqual(0, vertices,
                $"{what} must render NOTHING. Any geometry here means a source was substituted for the one " +
                "that was skipped — which passes a does-not-throw test while putting data on screen that " +
                "the style does not declare.");

            // LAST on purpose: the three arms above are the ones an empty-dataset substitution satisfies, so
            // leaving them ahead of this makes a RED here evidence that they really did stay green — that
            // the hole this arm closes is a hole and not a duplicate.
            Assert.AreEqual(0, view.WiredFeatureSourceCount(),
                $"{what} must leave NO source wired. This is the arm the three above cannot make: a source " +
                "substituted with an EMPTY FeatureCollection fetches nothing, builds no byte source and " +
                "renders nothing, because its inverted bbox rejects every tile — so silence is not proof of " +
                "a skip, only a count of wired sources is.");
        }

        // ── T3 · SourceKey carries the inline-data identity ───────────────────────────────────────────

        private static SourceDefinition GeoJsonDef(string dataJson)
            => StyleParser.Parse(StyleWithInlineData(dataJson)).GetSource("geo");

        /// <summary><b>T3 arm A (unit)</b> — two definitions identical except for the inline <c>data</c>
        /// produce DIFFERENT keys. Without the field they are value-equal on all six other fields (an inline
        /// source has no url, no tiles[], and takes the spec defaults for zoom/scheme/bounds), so the restyle
        /// diff keeps the first dataset's pipeline.</summary>
        [Test]
        public void T3A_TwoInlineSources_DifferingOnlyInData_HaveDifferentKeys()
        {
            SourceDefinition first  = GeoJsonDef(RectangleAt(RectMin, RectMax));
            SourceDefinition second = GeoJsonDef(RectangleAt(OtherMin, OtherMax));

            // Precondition: everything else really is equal, so `data` is the only discriminator available.
            Assert.AreEqual(first.Url, second.Url);
            Assert.AreEqual(first.MinZoom, second.MinZoom);
            Assert.AreEqual(first.MaxZoom, second.MaxZoom);
            Assert.AreEqual(first.Scheme, second.Scheme);
            Assert.IsNull(first.Tiles, "precondition: an inline source has no tiles[]");

            Assert.AreNotEqual(TileManager.SourceKey.From(first), TileManager.SourceKey.From(second),
                "two different inline datasets must not be the same source. They were, before S2 — so a " +
                "restyle between them kept the first one's pipeline and rendered the wrong geometry.");
        }

        /// <summary><b>T3 arm B (unit)</b> — two definitions with the SAME data produce EQUAL keys, even
        /// though the restyle re-parsed the document into fresh <see cref="JsonValue"/> objects. Arm A alone
        /// over-claims: a "fix" keyed on reference identity passes it and silently rebuilds every GeoJSON
        /// pipeline on every restyle.</summary>
        [Test]
        public void T3B_TwoInlineSources_WithTheSameData_HaveEqualKeys_AcrossAReparse()
        {
            string data = RectangleAt(RectMin, RectMax);
            SourceDefinition first  = GeoJsonDef(data);
            SourceDefinition second = GeoJsonDef(data);

            Assert.AreNotSame(first.Data, second.Data,
                "precondition: the two parses really are distinct objects — if they were the same instance, " +
                "an identity-keyed implementation would pass this arm too and it would prove nothing");

            Assert.AreEqual(TileManager.SourceKey.From(first), TileManager.SourceKey.From(second),
                "the same inline dataset must be the same source across a re-parse, or every restyle " +
                "rebuilds every geojson pipeline and the keep-the-pipeline path is dead for this source type");
            Assert.AreEqual(
                TileManager.SourceKey.From(first).GetHashCode(),
                TileManager.SourceKey.From(second).GetHashCode(),
                "…and the hash must agree, or the dictionary lookups the diff performs never reach Equals");
        }

        /// <summary>The same claim where member ORDER differs — the realistic re-parse difference, and the
        /// one a naive concatenation of the raw text would get wrong.</summary>
        [Test]
        public void T3B_MemberOrderInTheInlineData_DoesNotChangeTheKey()
        {
            SourceDefinition a = GeoJsonDef(@"{""type"":""FeatureCollection"",""features"":[]}");
            SourceDefinition b = GeoJsonDef(@"{""features"":[],""type"":""FeatureCollection""}");

            Assert.AreEqual(TileManager.SourceKey.From(a), TileManager.SourceKey.From(b),
                "JSON object member order is not semantic; two authorings of one dataset are one source");
        }

        // ── T3C · the key's OTHER identity field: the source type ─────────────────────────────────────

        /// <summary>Two definitions equal on every key field except <c>type</c>. Constructed directly rather
        /// than parsed, because the point is to hold the other six fields EQUAL by construction — a style
        /// document reaching this shape has to carry each type's keys on the other type's source, which is
        /// possible but obscures what the arm is about.</summary>
        private static SourceDefinition DefTyped(SourceType type) => new SourceDefinition
        {
            Type    = type,
            RawType = type == SourceType.Vector ? "vector" : "geojson",
            Url     = null,
            Tiles   = new[] { "https://example.com/{z}/{x}/{y}.pbf" },
            MinZoom = 0,
            MaxZoom = 22,
            Scheme  = "xyz",
            Bounds  = null,
            Data    = JsonParser.Parse(RectangleAt(RectMin, RectMax)),
        };

        /// <summary>
        /// <b>T3C (unit)</b> — <c>type</c> is part of the key. It is not a tolerated extra field: it is the
        /// field <c>MapView.BuildSourceSpecs</c> branches on to decide whether the source fetches bytes or
        /// slices a local dataset, so two definitions differing only in it are not the same source under any
        /// reading of the question the key asks.
        ///
        /// <para><b>The value is structural, not the repro.</b> Reaching this collision through a style
        /// document is narrow — a data-less geojson source and a tiles-less vector source are both skipped
        /// before a spec is minted, so each side must also carry the other type's keys — but the field
        /// belongs in the key regardless, and a future reader must not delete it for being hard to trigger.
        /// </para>
        /// </summary>
        [Test]
        public void T3C_TwoSourcesDifferingOnlyInType_HaveDifferentKeys()
        {
            TileManager.SourceKey vector  = TileManager.SourceKey.From(DefTyped(SourceType.Vector));
            TileManager.SourceKey geoJson = TileManager.SourceKey.From(DefTyped(SourceType.GeoJson));

            // Preconditions: EVERY other key field really is equal, so `type` is the only discriminator left.
            // Asserted field by field rather than claimed, because a key that gained a seventh field would
            // otherwise make this test pass for a reason that has nothing to do with the type.
            Assert.AreEqual(vector.Url,     geoJson.Url,     "precondition: equal url");
            Assert.AreEqual(vector.Tiles,   geoJson.Tiles,   "precondition: equal tiles[]");
            Assert.AreEqual(vector.MinZoom, geoJson.MinZoom, "precondition: equal minzoom");
            Assert.AreEqual(vector.MaxZoom, geoJson.MaxZoom, "precondition: equal maxzoom");
            Assert.AreEqual(vector.Scheme,  geoJson.Scheme,  "precondition: equal scheme");
            Assert.AreEqual(vector.Bounds,  geoJson.Bounds,  "precondition: equal bounds");
            Assert.AreEqual(vector.Data,    geoJson.Data,    "precondition: equal canonical `data` text");

            Assert.AreNotEqual(vector, geoJson,
                "a vector source and a geojson source are not the same source, however alike their other " +
                "fields. Without `type` in the key they compare equal, the restyle diff keeps the first " +
                "pipeline, and the map goes on fetching MVT for a style that now declares inline GeoJSON.");
            Assert.AreNotEqual(vector.GetHashCode(), geoJson.GetHashCode(),
                "…and `type` must participate in the HASH as well. Today's diff never hashes a key — " +
                "SourceRegistry.Find is a linear scan calling Equals directly, and SourceKey is a dictionary " +
                "key nowhere — so this pins structural participation, not correctness: a key whose hash ignores " +
                "a field it compares is only correct while nothing hashes it, and the day something does, " +
                "two keys that differ ONLY in that field land in the same bucket by construction.");
        }

        /// <summary>
        /// <b>T3C (behavioural)</b> — the same claim at the production diff site: flipping only the source
        /// type across a restyle REBUILDS the pipeline, and the factory that runs is the NEW spec's.
        ///
        /// <para>Both thunks build the same kind of feature source on purpose — the tooth is about WHICH
        /// spec's thunk the diff invokes, so the recorded tag is the discriminator, not the object.</para>
        /// </summary>
        [Test]
        public void T3C_TheRestyleDiff_RebuildsWhenOnlyTheSourceTypeChanged()
        {
            var view = NewView(out var go);
            try
            {
                var style = StyleParser.Parse(StyleWithInlineData(RectangleAt(RectMin, RectMax)));
                view.View.Layers.Build(style, 0.0, view.Config.MaterialSet);

                var built = new List<SourceType>();

                void Apply(SourceDefinition def)
                {
                    var specs = new List<TileManager.SourceSpec>
                    {
                        new TileManager.SourceSpec("s", TileManager.SourceKey.From(def), 0, 22, () =>
                        {
                            built.Add(def.Type);
                            return new GeoJsonTileFeatureSource(
                                GeoJsonParser.Parse(RectangleAt(RectMin, RectMax)), GeoJsonSliceOptions.Default,
                                Scheduler);
                        }),
                    };
                    view.View.TileManager.SetSources(specs, view.Config.Backend);
                }

                Apply(DefTyped(SourceType.Vector));
                CollectionAssert.AreEqual(new[] { SourceType.Vector }, built,
                    "precondition: the first application builds the pipeline");

                Apply(DefTyped(SourceType.Vector));
                CollectionAssert.AreEqual(new[] { SourceType.Vector }, built,
                    "precondition: re-applying the SAME definition keeps the pipeline — without this the " +
                    "arm below could pass against a diff that rebuilds unconditionally");

                Apply(DefTyped(SourceType.GeoJson));
                CollectionAssert.AreEqual(new[] { SourceType.Vector, SourceType.GeoJson }, built,
                    "flipping only the source type must REBUILD the pipeline and run the NEW spec's factory. " +
                    "Keeping the old one is silent wrong-source rendering: the style declares one payload " +
                    "and the map keeps serving the other.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// <b>T3 arms A and B, behaviourally</b>, at the production diff site: <c>TileManager.SetSources</c>
        /// KEEPS a pipeline whose <c>(SourceId, Key)</c> matches and REBUILDS one whose key changed. Observed
        /// by counting <c>CreateSource</c> invocations — the thunk the diff calls only for a new or changed
        /// spec — with keys computed by the production <c>SourceKey.From</c>.
        /// </summary>
        [Test]
        public void T3_TheRestyleDiff_KeepsThePipelineForEqualData_AndRebuildsItForDifferent()
        {
            var view = NewView(out var go);
            try
            {
                var style = StyleParser.Parse(StyleWithInlineData(RectangleAt(RectMin, RectMax)));
                view.View.Layers.Build(style, 0.0, view.Config.MaterialSet);

                int created = 0;
                var sources = new List<ITileFeatureSource>();

                void Apply(SourceDefinition def)
                {
                    var specs = new List<TileManager.SourceSpec>
                    {
                        new TileManager.SourceSpec("geo", TileManager.SourceKey.From(def), 0, 22, () =>
                        {
                            created++;
                            var s = new GeoJsonTileFeatureSource(
                                GeoJsonParser.Parse(def.Data), GeoJsonSliceOptions.Default, Scheduler);
                            sources.Add(s);
                            return s;
                        }),
                    };
                    view.View.TileManager.SetSources(specs, view.Config.Backend);
                }

                Apply(GeoJsonDef(RectangleAt(RectMin, RectMax)));
                Assert.AreEqual(1, created, "precondition: the first application builds the pipeline");

                Apply(GeoJsonDef(RectangleAt(RectMin, RectMax)));
                Assert.AreEqual(1, created,
                    "a restyle to the SAME inline data must KEEP the pipeline (and its source instance). A " +
                    "key built on reference identity or on JsonValue.ToString would rebuild here.");

                Apply(GeoJsonDef(RectangleAt(OtherMin, OtherMax)));
                Assert.AreEqual(2, created,
                    "a restyle to DIFFERENT inline data must REBUILD the pipeline. Keeping it is the recorded " +
                    "bug: the second style's tiles would be served from the first dataset.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// <b>T3 arm A, end to end</b>: a <c>SetStyle</c> → <c>SetStyle</c> between two datasets renders the
        /// SECOND one's geometry. The unit arms pin the key; this pins that the key is what the render path
        /// actually follows.
        /// </summary>
        [Test]
        public void T3A_ARestyleBetweenTwoDatasets_RendersTheSecondOne()
        {
            var view = NewView(out var go);
            try
            {
                SpinToCompleted(view.SetStyle(
                    StyleParser.Parse(StyleWithInlineData(RectangleAt(RectMin, RectMax))), "first"));
                PumpUntilSettled(view);
                Assert.IsNotNull(view.GetTileMeshes(WorldTile), "precondition: the first dataset rendered");

                SpinToCompleted(view.SetStyle(
                    StyleParser.Parse(StyleWithInlineData(RectangleAt(OtherMin, OtherMax))), "second"));
                PumpUntilSettled(view);

                Mesh[] meshes = view.GetTileMeshes(WorldTile);
                Assert.IsNotNull(meshes, "the restyled source must render too");
                // 4 interior + 8 band (the outward boundary band appends two per ring vertex).
                Assert.AreEqual(12, meshes[0].vertexCount);

                double tolerance = 4.0 * WebMercator.WorldExtent / GeoJsonSliceOptions.DefaultExtent;
                double2 expectedNw = ExpectedWorldXZ(OtherMin, OtherMin);
                double2 firstNw    = ExpectedWorldXZ(RectMin,  RectMin);
                Assert.Greater(math.abs(expectedNw.x - firstNw.x), tolerance * 10.0,
                    "precondition: the two rectangles must be far enough apart for the tolerance to " +
                    "discriminate them");

                double minX = double.PositiveInfinity;
                foreach (Vector3 v in meshes[0].vertices) minX = math.min(minX, v.x);

                Assert.AreEqual(expectedNw.x, minX, tolerance,
                    "the restyled tile must carry the SECOND dataset's geometry. Rendering the first one's " +
                    "is the recorded SourceKey bug — the pipeline was kept because the two inline sources " +
                    "compared equal on every field the key had.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── T5 · the lease: the handle is LAZY and the buffers are FREED ──────────────────────────────

        private static GeoJsonTileFeatureSource SourceOver(string dataJson, GeoJsonSliceOptions options)
            => new GeoJsonTileFeatureSource(GeoJsonParser.Parse(dataJson), options, Scheduler);

        /// <summary>
        /// <b>T5 (the decisive arm), INVERTED — <c>GetTile</c> SLICES, and slices OFF THE MAIN THREAD.</b>
        ///
        /// <para>This tooth used to assert the opposite: that <c>GetTile</c> completed without slicing and
        /// the fault surfaced only at the first <c>GetOrDecode()</c>. Laziness was load-bearing under the
        /// scoped lease because <c>TileManager.Tick</c> calls <c>GetTile</c> for every cover tile while only
        /// a subset ever opened a scope, so a pre-built tile on the others would have been freed by nothing.
        /// The reference count removed that premise — every drop path is an owner now — so the source slices
        /// eagerly like the MVT one.</para>
        ///
        /// <para><b>What laziness also bought for free, and now has to be arranged deliberately:</b> the
        /// slice ran inside the kick's pool lambda, so it was never main-thread work. Eagerly slicing inside
        /// <c>Tick</c> would put a full slice on the frame thread for EVERY cover tile — the exact defect
        /// this asserts against, and the reason the source routes through
        /// <c>TileDecodeDispatch.DecodeAsync</c> rather than calling its decoder inline. A main-thread-only
        /// recorder on the decode marker must read ZERO while an all-thread recorder reads at least one; the
        /// synchronous form flips the first to non-zero.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator T5_GetTile_SlicesOffTheMainThread()
        {
            using var source = SourceOver(RectangleAt(RectMin, RectMax), GeoJsonSliceOptions.Default);

            using var mainOnly  = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Tile.Decode",
                ProfilerSampleCapacity, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            using var anyThread = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Tile.Decode",
                ProfilerSampleCapacity, ProfilerRecorderOptions.SumAllSamplesInFrame);

            UniTask<SharedDisposable<IDecodedTile>> task = source.GetTile(WorldTile);
            SharedDisposable<IDecodedTile> handle = null;
            for (int f = 0; f < 200 && !task.Status.IsCompleted(); f++) yield return null;
            Assert.IsTrue(task.Status.IsCompleted(), "the slice must complete within the pumped window");
            handle = task.GetAwaiter().GetResult();

            Assert.IsNotNull(handle, "precondition: the world tile is inside the dataset's bbox, so it slices");
            ITileLayer layer = handle.Value.GetLayer(null);
            Assert.IsNotNull(layer, "precondition: GetTile really produced a decoded tile, not an empty shell");
            Assert.IsTrue(layer.Geometry.IsCreated,
                "precondition: the slice really allocated native buffers — otherwise 'it sliced' is vacuous");
            handle.Release();

            yield return null; // let the recorder's frame close

            long mainHits = 0, anyHits = 0;
            for (int i = 0; i < math.min(mainOnly.Count,  ProfilerSampleCapacity); i++) mainHits += mainOnly.GetSample(i).Count;
            for (int i = 0; i < math.min(anyThread.Count, ProfilerSampleCapacity); i++) anyHits  += anyThread.GetSample(i).Count;

            Assert.Greater(anyHits, 0,
                "sanity: the decode marker fired at all — GetTile really performed the slice, rather than " +
                "handing back something that had never been built");
            Assert.AreEqual(0, mainHits,
                "DECISIVE: MapRenderer.Tile.Decode must NOT fire on the main thread. GetTile is called once " +
                "per cover tile from inside Tick, so a source that sliced inline would put a full slice on " +
                "the frame thread for every one of them — the cost laziness used to avoid by accident and " +
                "TileDecodeDispatch.DecodeAsync now avoids on purpose.");
        }

        /// <summary>
        /// <b>T5, POLICY ARM</b> — proves the source actually REACHES the <see cref="IWorkScheduler"/> it was
        /// constructed with, rather than merely landing off the main thread by coincidence. <see cref="T5_GetTile_SlicesOffTheMainThread"/>
        /// only proves "not on main", which a <c>DecodeAsync</c> that ignored its scheduler parameter and
        /// hardcoded <see cref="ThreadPoolWorkScheduler"/> internally would satisfy too — that substitution is
        /// exactly what the WebGL fix depends on being impossible.
        ///
        /// <para>Constructed with <see cref="InlineWorkScheduler"/> instead of <see cref="Scheduler"/>.
        /// <see cref="GeoJsonTileFeatureSource.GetTile"/> has no <c>await</c> before <c>DecodeAsync</c>, so
        /// under Inline the whole slice runs synchronously on THIS test's own thread — the inverse of the
        /// sibling tooth's assertion, using the same two recorders.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator T5_GetTile_HonoursAnInjectedInlineScheduler_AndSlicesOnTheCallingThread()
        {
            using var source = new GeoJsonTileFeatureSource(
                GeoJsonParser.Parse(RectangleAt(RectMin, RectMax)), GeoJsonSliceOptions.Default,
                new InlineWorkScheduler());

            using var mainOnly  = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Tile.Decode",
                ProfilerSampleCapacity, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            using var anyThread = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Tile.Decode",
                ProfilerSampleCapacity, ProfilerRecorderOptions.SumAllSamplesInFrame);

            UniTask<SharedDisposable<IDecodedTile>> task = source.GetTile(WorldTile);
            SharedDisposable<IDecodedTile> handle = null;
            for (int f = 0; f < 200 && !task.Status.IsCompleted(); f++) yield return null;
            Assert.IsTrue(task.Status.IsCompleted(), "the slice must complete within the pumped window");
            handle = task.GetAwaiter().GetResult();

            Assert.IsNotNull(handle, "precondition: the world tile is inside the dataset's bbox, so it slices");
            ITileLayer layer = handle.Value.GetLayer(null);
            Assert.IsNotNull(layer, "precondition: GetTile really produced a decoded tile, not an empty shell");
            Assert.IsTrue(layer.Geometry.IsCreated,
                "precondition: the slice really allocated native buffers — otherwise 'it sliced' is vacuous");
            handle.Release();

            yield return null; // let the recorder's frame close

            long mainHits = 0, anyHits = 0;
            for (int i = 0; i < math.min(mainOnly.Count,  ProfilerSampleCapacity); i++) mainHits += mainOnly.GetSample(i).Count;
            for (int i = 0; i < math.min(anyThread.Count, ProfilerSampleCapacity); i++) anyHits  += anyThread.GetSample(i).Count;

            Assert.Greater(anyHits, 0, "sanity: the slice ran");
            Assert.Greater(mainHits, 0,
                "DECISIVE: the injected Inline scheduler must be REACHED by the slice. A DecodeAsync that " +
                "ignored its scheduler parameter and minted a ThreadPoolWorkScheduler internally would satisfy " +
                "the sibling tooth's 'not on main' arm but never put a sample here.");
        }

        /// <summary>
        /// <b>T5 (the lifetime arm)</b> — the LAST release frees the tile's native buffers, and a second
        /// <c>GetTile</c> for the same tile slices again (there is no cross-call cache).
        ///
        /// <para>Reduced from "one slice per open SCOPE": a lease never slices, so sharing within one is
        /// trivially true now and is pinned where it belongs (<c>SharedDisposableSharingTests</c>). What
        /// survives here is the half no probe can see — <c>Geometry.IsCreated</c> going false is the only
        /// place in the suite that observes the NATIVE free rather than a counted <c>Dispose</c> call.</para>
        /// </summary>
        [Test]
        public async Task T5_TheLastReleaseFreesTheBuffers_AndASecondGetTileSlicesAgain()
        {
            using var source = SourceOver(RectangleAt(RectMin, RectMax), GeoJsonSliceOptions.Default);
            SharedDisposable<IDecodedTile> handle = await source.GetTile(WorldTile);
            Assert.IsNotNull(handle, "precondition: the world tile is inside the dataset's bbox");

            IDecodedTile firstTile = handle.Value;
            Assert.AreSame(firstTile, handle.Value,
                "a second read must return the same tile — a lease holds one slice, it does not make them");

            ITileLayer firstLayer = firstTile.GetLayer(null);
            Assert.IsNotNull(firstLayer, "precondition: the tile sliced to a layer");
            Assert.IsTrue(firstLayer.Geometry.IsCreated,
                "precondition: the layer really allocated native buffers — if it did not, the freed " +
                "assertion below would be vacuously true");

            handle.Release();

            Assert.IsFalse(firstLayer.Geometry.IsCreated,
                "the LAST release must free the tile's buffers. Under the eager decode this is the whole " +
                "lifetime protocol: memory that exists from fetch-completion onwards is memory every drop " +
                "path has to release, and this is the only assertion in the suite that watches the native " +
                "free itself rather than a counted Dispose call.");

            // …and a second GetTile for the same tile slices AGAIN — there is no cross-call cache, so the
            // coordinator can never be handed a handle whose tile another owner already freed.
            SharedDisposable<IDecodedTile> second = await source.GetTile(WorldTile);
            Assert.AreNotSame(firstTile, second.Value, "a second GetTile re-slices rather than resurrecting the tile");

            ITileLayer secondLayer = second.Value.GetLayer(null);
            Assert.IsNotNull(secondLayer);
            Assert.IsTrue(secondLayer.Geometry.IsCreated, "…and the re-slice really allocated again");
            Assert.AreEqual(firstLayer.Features.Count, secondLayer.Features.Count,
                "slicing is a pure function of (dataset, tile, options), so the re-slice is identical");
            second.Release();
        }

        /// <summary>
        /// The O(1) emptiness probe: a tile the dataset provably cannot reach gets a NULL handle (so no
        /// decode is even possible for it), while a tile it does reach gets one. Both directions, because
        /// "always null" and "never null" each satisfy one of them.
        /// </summary>
        [Test]
        public async Task GetTile_ReturnsNull_OnlyForTilesTheDatasetCannotReach()
        {
            using var source = SourceOver(RectangleAt(RectMin, RectMax), GeoJsonSliceOptions.Default);

            // async Task, and every non-null handle released: GetTile is async now, so
            // `.GetAwaiter().GetResult()` on a UniTask that has not completed does not block, and a handle
            // taken and dropped is a decoded tile nothing frees.
            async UniTask<SharedDisposable<IDecodedTile>> Take(TileId id)
            {
                SharedDisposable<IDecodedTile> h = await source.GetTile(id);
                h?.Release();
                return h;
            }

            // The rectangle is authored at tile-local 1024..3072 of extent 4096 at z0, i.e. unit-square
            // [0.25, 0.75]². At z3 each tile is 0.125 wide and the buffer adds 64/4096/8 ≈ 0.00195, so the
            // corner tiles' windows ([−0.002, 0.127] and [0.873, 1.002]) miss it and the middle ones cover it
            // outright. The CORNER tiles, not the z2 quadrant ones: at z2 the buffered window reaches 0.2539,
            // which touches the rectangle's edge — the probe is right to keep those, and a test asserting
            // otherwise would be asserting the buffer away.
            Assert.IsNotNull(await Take(new TileId { Z = 3, X = 3, Y = 3 }),
                "a tile the dataset overlaps must get a handle");
            Assert.IsNull(await Take(new TileId { Z = 3, X = 0, Y = 0 }),
                "a tile disjoint from the dataset's bounding box must get a null handle — the coordinator's " +
                "absent-tile contract, and the reason a fixture-sized dataset costs O(1) per cover tile");
            Assert.IsNull(await Take(new TileId { Z = 3, X = 7, Y = 7 }),
                "…and the same on the far side, so the probe is not simply rejecting low indices");

            // The probe is CONSERVATIVE, and that has to be visible: a tile the buffered window merely grazes
            // is kept, even though it may slice to nothing. Rejecting it would be the failure mode that
            // matters (geometry silently lost); keeping it costs an empty tile the coordinator already
            // handles.
            Assert.IsNotNull(await Take(new TileId { Z = 2, X = 0, Y = 0 }),
                "a tile whose BUFFERED window reaches the dataset's box must be kept — the probe never " +
                "rejects a tile that could carry geometry");
        }

        /// <summary>A dataset with no located feature has an INVERTED bounding box, so every tile is
        /// disjoint from it and nothing is ever sliced. This is the arm that makes T1's anti-vacuity arm
        /// mean "the probe rejected it", not "the fill happened to be empty".</summary>
        [Test]
        public async Task GetTile_AnEmptyDataset_RejectsEveryTile()
        {
            using var source = SourceOver(
                @"{""type"":""FeatureCollection"",""features"":[]}", GeoJsonSliceOptions.Default);

            Assert.IsNull(await source.GetTile(WorldTile),
                "an empty dataset's bbox is inverted, so even the world tile is disjoint from it");
        }

        /// <summary>
        /// Options this source cannot honour are rejected where they are ACCEPTED, not once per tile inside
        /// a scope — <b>the whole option set, not the extent alone</b>.
        ///
        /// <para><c>default(GeoJsonSliceOptions)</c> has <c>Extent == 0</c>, which makes the probe's window
        /// NaN. Every NaN comparison is false, so <c>disjoint</c> is false, so the probe answers "keep" for
        /// every tile in the cover and each one then faults its own <c>GetTile</c> task — loud, but at the
        /// wrong place and N times, with the wiring site that chose the options nowhere in the report.</para>
        ///
        /// <para><b>The tolerance arm is newer than the extent one, and its lateness had a reason.</b> A
        /// non-zero <c>SimplifyTolerance</c> is unimplemented, so it is exactly as unusable as a zero extent,
        /// yet it used to be admitted here on purpose: the fault it produced at the first slice was the
        /// instrument <c>T5_GetTile_DoesNotSlice_…</c> used to observe that the source had not sliced. That
        /// tooth is retired — the source decodes eagerly and <c>T5_GetTile_SlicesOffTheMainThread</c>
        /// replaced it — so the carve-out serves nothing and this arm closes it.</para>
        /// </summary>
        [Test]
        public void TheSource_RejectsUnusableOptions_AtConstruction()
        {
            GeoJsonDataset dataset = GeoJsonParser.Parse(RectangleAt(RectMin, RectMax));

            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new GeoJsonTileFeatureSource(dataset, default, Scheduler),
                "a default(GeoJsonSliceOptions) has a zero extent and cannot slice anything. Accepting it " +
                "here defers the fault to every tile's own decode, once per cover tile.");

            var simplifying = new GeoJsonSliceOptions
            {
                Extent                  = GeoJsonSliceOptions.DefaultExtent,
                BufferAtReferenceExtent = GeoJsonSliceOptions.DefaultBufferAtReferenceExtent,
                SimplifyTolerance       = 1.0
            };
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new GeoJsonTileFeatureSource(dataset, simplifying, Scheduler),
                "…and so must an unimplemented non-zero SimplifyTolerance, whose extent is perfectly " +
                "usable: every tile this source is asked for would fault at its own slice instead.");

            Assert.DoesNotThrow(
                () => new GeoJsonTileFeatureSource(dataset, GeoJsonSliceOptions.Default, Scheduler).Dispose(),
                "…while usable options must still construct, or the guard is refusing everything");
        }

        /// <summary>
        /// <c>GetTile</c> observes its <c>CancellationToken</c>. Nothing here is long enough to cancel
        /// mid-call, and no production caller threads a token today — <c>TileManager.Tick</c> calls
        /// <c>GetTile(id)</c>, the sole call site, for both implementations. This pins SEAM-CONTRACT
        /// CONFORMANCE, not an observed cancellation path: the seam declares the parameter, so an
        /// implementation that ignored it would go on minting handles the moment the coordinator threads
        /// one through a teardown, and the defect would surface as a leak rather than as a fault.
        /// </summary>
        [Test]
        public async Task GetTile_ObservesCancellation()
        {
            using var source = SourceOver(RectangleAt(RectMin, RectMax), GeoJsonSliceOptions.Default);
            using var cts = new CancellationTokenSource();

            SharedDisposable<IDecodedTile> live = await source.GetTile(WorldTile, cts.Token);
            Assert.IsNotNull(live,
                "precondition: this tile gets a handle while the token is live, so the arm below cannot " +
                "pass because the probe rejected it anyway");
            live.Release();

            cts.Cancel();
            System.Exception thrown = null;
            try { await source.GetTile(WorldTile, cts.Token); }
            catch (System.Exception ex) { thrown = ex; }
            Assert.IsInstanceOf<System.OperationCanceledException>(thrown,
                "a cancelled cover pass must not keep minting handles. GetTile is async now, so the refusal " +
                "arrives as a FAULTED task rather than a synchronous throw — the seam says so explicitly, " +
                "and both implementations now agree on it.");
        }

        /// <summary>Reported honestly, and pinned so it cannot drift into a lie: this source has nothing in
        /// flight, ever. <c>TileManager</c> sums this across pipelines to decide whether the map is idle, so
        /// a non-zero constant here would make the view never settle.</summary>
        [Test]
        public async Task TheSource_ReportsNoInFlightWork()
        {
            using var source = SourceOver(RectangleAt(RectMin, RectMax), GeoJsonSliceOptions.Default);
            SharedDisposable<IDecodedTile> handle = await source.GetTile(WorldTile);
            Assert.AreEqual(0, source.InFlightCount);
            handle?.Release();
        }
    }
}
