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
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using CoreMapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.GeoJsons
{
    /// <summary>
    /// The GeoJSON source, end to end: an inline geojson source actually renders through
    /// <c>MapView.SetStyle</c>, <c>SourceKey</c> carries the inline-data identity in both directions,
    /// and the slice happens inside <c>GetTile</c>, OFF the main thread, with its native buffers freed
    /// by the lease's last release.
    /// </summary>
    [TestFixture]
    public class GeoJsonSourceTests : BaseTestFixture
    {
        private static readonly TileId WorldTile = new TileId { Z = 0, X = 0, Y = 0 };

        // Off-main, matching production's desktop policy — these teeth exercise GetTile's/the source's own
        // contract, not the scheduler choice.
        private static readonly IWorkScheduler Scheduler = new ThreadPoolWorkScheduler();

        private const int ProfilerSampleCapacity = 64;

        // Tile-local corners of the authored rectangle at GeoJsonSliceOptions.Default's extent, well inside
        // [0, extent] so no clip can touch them.
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

        /// <summary>A one-source, one-fill-layer style over inline geojson. The style layer declares NO
        /// <c>source-layer</c> — that is what the Style Spec says for a geojson source. A wrong
        /// source-layer resolution selects zero features here.</summary>
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

        // ── a GeoJSON source renders through the real loop ────────────────────────────────────────────

        /// <summary>
        /// A style whose only source is inline geojson renders a fill mesh through the production
        /// <c>MapView.SetStyle</c> → <c>BuildSourceSpecs</c> path. The four interior vertices must sit on the
        /// AUTHORED corners, each claimed ONE-TO-ONE, so four vertices collapsed on one corner fail. The
        /// triangles must also TILE the quad: two copies of one half-triangle pass a summed-area check but
        /// render half the polygon twice.
        /// </summary>
        [Test]
        public void AnInlineGeoJsonSource_RendersItsAuthoredPolygon()
        {
            var view = NewView(out var go);
            Track(go);
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
                // 4 interior + 8 band vertices, the band AFTER the quad. Claims below read only the interior
                // prefix, because band vertices also sit on corners and would exhaust the corner pool.
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

                // Each vertex claims a DISTINCT corner. The claimed corner per slot is recorded, because the
                // topology arm below reads the index buffer in authored corners.
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
                // Interior triangles only: a band triangle has a vertex past the prefix and no authored corner.
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
                    "collapsed-mesh failure the positional arms above are not asked to catch; " +
                    "twice it would be a doubled or self-overlapping fan.");
            }
            finally { view.Teardown(); }
        }

        /// <summary>
        /// The same style with an EMPTY FeatureCollection renders nothing, so the test above cannot pass by
        /// rendering anything at all. Limitation: the probe rejects every tile before any decode (see
        /// <see cref="GetTile_AnEmptyDataset_RejectsEveryTile"/>), so a decoder that emits a quad for an empty
        /// slice passes here; <c>GeoJsonTileDecoderTests.AnEmptySlice_YieldsATileWithNoLayerAtAll</c> catches it.
        /// </summary>
        [Test]
        public void AnEmptyInlineDataset_RendersNothing()
        {
            var view = NewView(out var go);
            Track(go);
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
            finally { view.Teardown(); }
        }

        /// <summary>A geojson source whose <c>data</c> is a URL string (unsupported) or malformed is SKIPPED
        /// with a warning and never faults <c>SetStyle</c>. Skipping is observed, not inferred from no throw: no
        /// source factory or document loader runs, nothing renders, and NO source is wired. The last arm
        /// catches an EMPTY <c>FeatureCollection</c> substitute, which fetches and renders nothing.</summary>
        [Test]
        public void UnsupportedOrMalformedData_IsSkipped_NotThrown()
        {
            var view = NewView(out var go);
            Track(go);
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
            finally { view.Teardown(); }
        }

        /// <summary>The skip contract: no source is wired, nothing was fetched, constructed or rendered. It pumps
        /// a few frames first, so a substituted source has the chance to produce something. The counters are
        /// <c>Func&lt;int&gt;</c>, so they are read AFTER the pump and see a lazy fetch at tile-load time.
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

            // LAST, so a RED here shows the three arms above stayed green for an empty-dataset substitute.
            Assert.AreEqual(0, view.WiredFeatureSourceCount(),
                $"{what} must leave NO source wired. This is the arm the three above cannot make: a source " +
                "substituted with an EMPTY FeatureCollection fetches nothing, builds no byte source and " +
                "renders nothing, because its inverted bbox rejects every tile — so silence is not proof of " +
                "a skip, only a count of wired sources is.");
        }

        // ── SourceKey carries the inline-data identity ────────────────────────────────────────────────

        private static SourceDefinition GeoJsonDef(string dataJson)
            => StyleParser.Parse(StyleWithInlineData(dataJson)).GetSource("geo");

        /// <summary><b>Different data (unit)</b> — two definitions identical except for the inline <c>data</c>
        /// produce DIFFERENT keys. Without the field they are value-equal on all six other fields (an inline
        /// source has no url, no tiles[], and takes the spec defaults for zoom/scheme/bounds), so the restyle
        /// diff keeps the first dataset's pipeline.</summary>
        [Test]
        public void TwoInlineSources_DifferingOnlyInData_HaveDifferentKeys()
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
                "two different inline datasets must not be the same source. If they were, a restyle " +
                "between them would keep the first one's pipeline and render the wrong geometry.");
        }

        /// <summary><b>Same data (unit)</b> — two definitions with the SAME data produce EQUAL keys, even
        /// though the restyle re-parsed the document into fresh <see cref="JsonValue"/> objects. The
        /// different-data test alone over-claims: a "fix" keyed on reference identity passes it and silently
        /// rebuilds every GeoJSON pipeline on every restyle.</summary>
        [Test]
        public void TwoInlineSources_WithTheSameData_HaveEqualKeys_AcrossAReparse()
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
        public void MemberOrderInTheInlineData_DoesNotChangeTheKey()
        {
            SourceDefinition a = GeoJsonDef(@"{""type"":""FeatureCollection"",""features"":[]}");
            SourceDefinition b = GeoJsonDef(@"{""features"":[],""type"":""FeatureCollection""}");

            Assert.AreEqual(TileManager.SourceKey.From(a), TileManager.SourceKey.From(b),
                "JSON object member order is not semantic; two authorings of one dataset are one source");
        }

        // ── the key's OTHER identity field: the source type ──────────────────────────────────────────

        /// <summary>Two definitions equal on every key field except <c>type</c>. Constructed directly rather
        /// than parsed, because the point is to hold the other six fields EQUAL — a style
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
        /// <c>type</c> is part of the source key: <c>MapView.BuildSourceSpecs</c> branches on it to fetch bytes
        /// or slice a local dataset. Non-obvious why: the field stays in the key although a style
        /// rarely reaches this collision, because two definitions differing only in it are different sources.
        /// </summary>
        [Test]
        public void TwoSourcesDifferingOnlyInType_HaveDifferentKeys()
        {
            TileManager.SourceKey vector  = TileManager.SourceKey.From(DefTyped(SourceType.Vector));
            TileManager.SourceKey geoJson = TileManager.SourceKey.From(DefTyped(SourceType.GeoJson));

            // Preconditions, field by field: EVERY other key field is equal, so a new key field cannot make this
            // pass for a reason other than `type`.
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
                "two keys that differ ONLY in that field always land in the same bucket.");
        }

        /// <summary>
        /// <b>Source type (behavioural)</b> — the same claim at the production diff site: flipping only the source
        /// type across a restyle REBUILDS the pipeline, and the factory that runs is the NEW spec's.
        ///
        /// <para>Both thunks build the same kind of feature source on purpose — the tooth is about WHICH
        /// spec's thunk the diff invokes, so the recorded tag is the discriminator, not the object.</para>
        /// </summary>
        [Test]
        public void TheRestyleDiff_RebuildsWhenOnlyTheSourceTypeChanged()
        {
            var view = NewView(out var go);
            Track(go);
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
            finally { view.Teardown(); }
        }

        /// <summary>
        /// <b>Same data and different data, behaviourally</b>, at the production diff site:
        /// <c>TileManager.SetSources</c> KEEPS a pipeline whose <c>(SourceId, Key)</c> matches and REBUILDS
        /// one whose key changed. Observed by counting <c>CreateSource</c> invocations — the thunk the diff
        /// calls only for a new or changed spec — with keys computed by the production <c>SourceKey.From</c>.
        /// </summary>
        [Test]
        public void TheRestyleDiff_KeepsThePipelineForEqualData_AndRebuildsItForDifferent()
        {
            var view = NewView(out var go);
            Track(go);
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
            finally { view.Teardown(); }
        }

        /// <summary>
        /// <b>Different data, end to end</b>: a <c>SetStyle</c> → <c>SetStyle</c> between two datasets renders
        /// the SECOND one's geometry. The unit tests pin the key; this pins that the key is what the render
        /// path actually follows.
        /// </summary>
        [Test]
        public void ARestyleBetweenTwoDatasets_RendersTheSecondOne()
        {
            var view = NewView(out var go);
            Track(go);
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
            finally { view.Teardown(); }
        }

        // ── the lease: the handle is LAZY and the buffers are FREED ───────────────────────────────────

        private static GeoJsonTileFeatureSource SourceOver(string dataJson, GeoJsonSliceOptions options)
            => new GeoJsonTileFeatureSource(GeoJsonParser.Parse(dataJson), options, Scheduler);

        /// <summary>
        /// <c>GetTile</c> slices eagerly, like the MVT source, and OFF THE MAIN THREAD through
        /// <c>TileDecodeDispatch.DecodeAsync</c>; an inline slice would put a full slice on the frame thread
        /// for EVERY cover tile. A main-thread-only recorder on the decode marker reads ZERO while an
        /// all-thread recorder reads at least one.
        /// </summary>
        [UnityTest]
        public IEnumerator GetTile_SlicesOffTheMainThread()
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
                "the frame thread for every one of them — the cost TileDecodeDispatch.DecodeAsync exists " +
                "to avoid.");
        }

        /// <summary>
        /// The source REACHES the <see cref="IWorkScheduler"/> it was constructed with. A <c>DecodeAsync</c>
        /// that hardcoded <see cref="ThreadPoolWorkScheduler"/> passes <see cref="GetTile_SlicesOffTheMainThread"/>
        /// but breaks WebGL. With <see cref="InlineWorkScheduler"/>, and no <c>await</c> before
        /// <c>DecodeAsync</c>, the whole slice runs on THIS thread: the inverse of the sibling test.
        /// </summary>
        [UnityTest]
        public IEnumerator GetTile_HonoursAnInjectedInlineScheduler_AndSlicesOnTheCallingThread()
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
        /// The LAST release frees the tile's native buffers, and a second <c>GetTile</c> for the same tile
        /// slices again (no cross-call cache). <c>Geometry.IsCreated</c> going false is the only place in the
        /// suite that observes the NATIVE free; <c>SharedDisposableSharingTests</c> pins sharing.
        /// </summary>
        [Test]
        public async Task TheLastReleaseFreesTheBuffers_AndASecondGetTileSlicesAgain()
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

            // Awaited, because GetResult() on an incomplete UniTask does not block. Every non-null handle is
            // released, or its decoded tile leaks.
            async UniTask<SharedDisposable<IDecodedTile>> Take(TileId id)
            {
                SharedDisposable<IDecodedTile> h = await source.GetTile(id);
                h?.Release();
                return h;
            }

            // The rectangle spans unit-square [0.25, 0.75]². The z3 corner windows ([−0.002, 0.127] with the
            // buffer) miss it; z2 corner windows reach 0.2539 and touch it, so the probe keeps those.
            Assert.IsNotNull(await Take(new TileId { Z = 3, X = 3, Y = 3 }),
                "a tile the dataset overlaps must get a handle");
            Assert.IsNull(await Take(new TileId { Z = 3, X = 0, Y = 0 }),
                "a tile disjoint from the dataset's bounding box must get a null handle — the coordinator's " +
                "absent-tile contract, and the reason a fixture-sized dataset costs O(1) per cover tile");
            Assert.IsNull(await Take(new TileId { Z = 3, X = 7, Y = 7 }),
                "…and the same on the far side, so the probe is not simply rejecting low indices");

            // The probe is CONSERVATIVE: it keeps a tile the buffered window grazes. Rejecting it could lose
            // geometry; keeping it costs an empty tile.
            Assert.IsNotNull(await Take(new TileId { Z = 2, X = 0, Y = 0 }),
                "a tile whose BUFFERED window reaches the dataset's box must be kept — the probe never " +
                "rejects a tile that could carry geometry");
        }

        /// <summary>A dataset with no located feature has an INVERTED bounding box, so every tile is
        /// disjoint from it and nothing is ever sliced. This is the arm that makes
        /// <see cref="AnEmptyInlineDataset_RendersNothing"/> mean "the probe rejected it", not "the fill
        /// happened to be empty".</summary>
        [Test]
        public async Task GetTile_AnEmptyDataset_RejectsEveryTile()
        {
            using var source = SourceOver(
                @"{""type"":""FeatureCollection"",""features"":[]}", GeoJsonSliceOptions.Default);

            Assert.IsNull(await source.GetTile(WorldTile),
                "an empty dataset's bbox is inverted, so even the world tile is disjoint from it");
        }

        /// <summary>
        /// Unusable options — a zero <c>Extent</c> or a non-zero <c>SimplifyTolerance</c> — are rejected at
        /// construction, not once per tile. A zero extent makes the probe's window NaN, so every cover tile is
        /// kept and faults its own <c>GetTile</c> task, N times and away from the wiring site.
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
        /// <c>GetTile</c> observes its <c>CancellationToken</c>. This pins seam-contract conformance: the
        /// sole caller, <c>TileManager</c>, passes no token. An implementation that ignored it would mint
        /// handles once a caller cancels in teardown, and the defect would show as a leak, not a fault.
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
