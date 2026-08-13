// S82 acceptance — PreparedTileCache cache-unit teeth (no MapView, no TileManager wiring). These are
// falsifiable without S83 (real per-style ids): distinct StyleToken values are constructed directly.
//
// Teeth covered here:
//   - Multi-style coexistence: two StyleTokens' entries for the SAME TileId/layerId coexist; a lookup under
//     one style never returns the other's mesh. A naive drop-previous-on-insert cache fails this.
//   - Bounded / eviction: inserting beyond the byte budget evicts the LRU entry and DESTROYS its Mesh.
//   - Byte accounting: BytesHeld tracks the sum of each held entry's estimated bytes (vertexCount * stride +
//     indexCount * 4), matching PreparedTileCache.EstimateBytes's documented formula.
//   - Empty-layer marker: a null Mesh is a valid, zero-byte completeness entry (Contains/TryTake both honour it).
//   - Destroy-once: Dispose destroys every held mesh exactly once and is idempotent.
//
// Unity-only (UnityEngine.Mesh) — not included in the fast dotnet core-tests project.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class PreparedTileCacheTests
    {
        private static readonly TileId Tile0 = new TileId { Z = 3, X = 1, Y = 1 };

        // ── Fixtures (coexistence tooth only — real fixture styles, not synthetic meshes) ─────────

        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static StyleDocument LoadStyle(string fileName)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", fileName);
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return StyleParser.Parse(File.ReadAllText(path));
        }

        /// <summary>Real fill-layer mesh build (mirrors S82PreparedCacheTests.GroundTruthColorAtZoom, minus
        /// the Core-only leak-tracked allocator — this is a cache-unit test, not a NativeArray-invariant
        /// one) — used to prove coexistence with genuinely different baked colors per style, not just two
        /// synthetic meshes over identical geometry.</summary>
        private static Mesh BuildFillMesh(byte[] mvtBytes, StyleDocument style, TileId id, double zoom)
        {
            using var mvtTile = MvtDecoder.Decode(id, mvtBytes);
            var fillLayer = style.Layers[0];
            var paint     = new Fill.PaintProperties(fillLayer);
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "Fixture must contain a resolvable MVT layer.");
            var selected  = TestTileMeshBuilder.Select(fillLayer, mvtLayer, zoom);
            Assert.Greater(selected.Count, 0, "Fixture must produce >=1 feature (non-vacuous).");
            Mesh mesh = TestTileMeshBuilder.BuildFillFromLayer(mvtLayer, selected, paint, zoom, id);
            Assert.IsNotNull(mesh, "Fill build must produce geometry.");
            return mesh;
        }

        /// <summary>Mirrors S82PreparedCacheTests's FirstVertexColor/ColorsClose — a fill-color expression
        /// with no per-feature "get" is a pure function of zoom, so every vertex shares one color.</summary>
        private static Color FirstVertexColor(Mesh mesh)
        {
            var colors = new System.Collections.Generic.List<Color>();
            mesh.GetColors(colors);
            Assert.Greater(colors.Count, 0, "Mesh must have vertex colors.");
            return colors[0];
        }

        private static bool ColorsClose(Color a, Color b, float eps)
            => Mathf.Abs(a.r - b.r) < eps && Mathf.Abs(a.g - b.g) < eps && Mathf.Abs(a.b - b.b) < eps;

        /// <summary>A minimal real mesh (classic API) — vertexCount vertices, one triangle if >= 3 — enough
        /// for GetVertexBufferStride(0)/GetIndexCount(0) to report real, non-zero values. Content is
        /// irrelevant; these tests only exercise cache bookkeeping and Mesh lifecycle, never rendering.</summary>
        private static Mesh MakeMesh(int vertexCount)
        {
            var mesh = new Mesh { name = $"PreparedTileCacheTests-{vertexCount}v" };
            var verts = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++) verts[i] = new Vector3(i, 0f, 0f);
            mesh.vertices = verts;
            if (vertexCount >= 3) mesh.triangles = new[] { 0, 1, 2 };
            return mesh;
        }

        /// <summary>Independently computed reference matching PreparedTileCache.EstimateBytes's documented
        /// formula — via Mesh's own public API, never the cache's private method.</summary>
        private static long ExpectedBytes(Mesh m)
        {
            if (m == null) return 0;
            return (long)m.vertexCount * m.GetVertexBufferStride(0) + (long)m.GetIndexCount(0) * 4;
        }

        // ── Multi-style coexistence (falsifiable without S83) ──────────────────────────────────

        [Test]
        public void TwoStyles_Coexist_NoCrossContamination()
        {
            // Genuinely DIFFERENT-colored styles per styleId (stronger than two tokens over identical
            // synthetic geometry): interp-fill-style.json (zoom-interpolated, red<->blue) vs
            // coexist-fill-style.json (constant green), both over the same fixture tile/layer.
            byte[] bytes  = FixtureBytes();
            var styleDocA = LoadStyle("interp-fill-style.json");
            var styleDocB = LoadStyle("coexist-fill-style.json");
            Mesh meshA = BuildFillMesh(bytes, styleDocA, Tile0, zoom: 3.0);
            Mesh meshB = BuildFillMesh(bytes, styleDocB, Tile0, zoom: 3.0);

            Color colorA = FirstVertexColor(meshA);
            Color colorB = FirstVertexColor(meshB);
            Assert.IsFalse(ColorsClose(colorA, colorB, 1e-3f),
                "Positive control: styleA/styleB fixtures must bake genuinely different colors " +
                "(non-vacuous coexistence — a same-color pair could pass by coincidence).");

            var cache  = new PreparedTileCache(byteBudget: 0, countCap: 0); // unbounded
            var styleA = new StyleToken("A");
            var styleB = new StyleToken("B");
            try
            {
                // Same TileId/layerId, different style — a naive "drop previous on insert" cache would let
                // meshB's Put destroy/replace meshA's entry; PreparedTileCache keys them independently.
                cache.Put(new PreparedKey(styleA, Tile0, 0), meshA);
                cache.Put(new PreparedKey(styleB, Tile0, 0), meshB);

                Assert.AreEqual(2, cache.Count, "Both styles' entries must coexist under the same TileId/layerId.");

                Assert.IsTrue(cache.TryTake(new PreparedKey(styleA, Tile0, 0), out Mesh takenA));
                Assert.AreSame(meshA, takenA, "styleA lookup must hit the styleA mesh, not styleB's.");
                Assert.IsTrue(ColorsClose(colorA, FirstVertexColor(takenA), 1e-4f),
                    "styleA lookup must hit content baked from the interp-fill style, not the coexist style.");

                Assert.IsTrue(cache.TryTake(new PreparedKey(styleB, Tile0, 0), out Mesh takenB));
                Assert.AreSame(meshB, takenB, "styleB lookup must hit the styleB mesh, not styleA's — " +
                    "neither toggle re-prepared (TryTake serves the SAME Mesh object both put in).");
                Assert.IsTrue(ColorsClose(colorB, FirstVertexColor(takenB), 1e-4f),
                    "styleB lookup must hit content baked from the coexist style, not the interp-fill style.");
            }
            finally
            {
                cache.Dispose(); // both already taken out — no-op; destroy the test meshes explicitly.
                if (meshA != null) Object.DestroyImmediate(meshA);
                if (meshB != null) Object.DestroyImmediate(meshB);
            }
        }

        // ── Bounded / eviction ──────────────────────────────────────────────────────────────────

        [Test]
        public void Bounded_EvictsLru_FreesMesh()
        {
            Mesh mesh1 = MakeMesh(100);
            Mesh mesh2 = MakeMesh(100);
            long bytes1 = ExpectedBytes(mesh1);
            Assert.Greater(bytes1, 0, "Positive control: the test mesh must have non-zero estimated bytes.");

            // Budget fits exactly one mesh, not two.
            long budget = bytes1 + bytes1 / 2;
            var cache = new PreparedTileCache(budget, countCap: 0);
            var style = StyleToken.Default;
            var keyA  = new PreparedKey(style, Tile0, 0);
            var keyB  = new PreparedKey(style, Tile0, 1);

            cache.Put(keyA, mesh1);
            Assert.AreEqual(1, cache.Count);
            Assert.IsTrue(mesh1 != null, "mesh1 must still be alive while within budget.");

            // Exceeds the budget → evicts the LRU entry (keyA/mesh1), destroying its Mesh.
            cache.Put(keyB, mesh2);

            Assert.AreEqual(1, cache.Count, "Only the surviving entry remains after LRU eviction.");
            Assert.IsFalse(cache.Contains(keyA), "The LRU entry (keyA) must have been evicted.");
            Assert.IsTrue(cache.Contains(keyB), "The most-recently-Put entry (keyB) must survive.");
            Assert.IsTrue(mesh1 == null,
                "Evicted mesh must be DESTROYED, not merely dropped from bookkeeping (Unity fake-null after " +
                "DestroyImmediate). Falsifier: an unbounded/non-destroying cache would leave mesh1 alive.");
            Assert.AreEqual(1, cache.Evictions,
                "S82: exactly one LRU eviction (keyA/mesh1) must have been counted. A TryTake-based removal " +
                "must NOT bump this counter (only the forced-eviction path in Put does).");

            cache.Dispose(); // destroys mesh2
        }

        // ── S82: ByteBudget/MaxCount expose the LIVE applied (clamped) values ──────────────────

        [Test]
        public void ByteBudgetAndMaxCount_ExposeConfiguredOrClampedUnboundedValues()
        {
            var bounded = new PreparedTileCache(byteBudget: 1000, countCap: 5);
            try
            {
                Assert.AreEqual(1000, bounded.ByteBudget, "A positive configured byte budget is exposed unchanged.");
                Assert.AreEqual(5, bounded.MaxCount, "A positive configured count cap is exposed unchanged.");
            }
            finally
            {
                bounded.Dispose();
            }

            var unbounded = new PreparedTileCache(byteBudget: 0, countCap: 0);
            try
            {
                Assert.AreEqual(long.MaxValue, unbounded.ByteBudget,
                    "A <=0 byteBudget clamps internally to long.MaxValue ('unbounded') — the accessor must " +
                    "expose the LIVE applied value, not the raw 0 the constructor received.");
                Assert.AreEqual(int.MaxValue, unbounded.MaxCount,
                    "A <=0 countCap clamps internally to int.MaxValue ('unbounded') — same live-value contract.");
            }
            finally
            {
                unbounded.Dispose();
            }
        }

        // ── Byte accounting ─────────────────────────────────────────────────────────────────────

        [Test]
        public void BytesHeld_TracksEstimateBytes_Sum()
        {
            var cache = new PreparedTileCache(byteBudget: 0, countCap: 0);
            Mesh meshA = MakeMesh(10);
            Mesh meshB = MakeMesh(20);
            try
            {
                cache.Put(new PreparedKey(StyleToken.Default, Tile0, 0), meshA);
                cache.Put(new PreparedKey(StyleToken.Default, Tile0, 1), meshB);

                long expected = ExpectedBytes(meshA) + ExpectedBytes(meshB);
                Assert.Greater(expected, 0, "Positive control: non-zero expected bytes (non-vacuous).");
                Assert.AreEqual(expected, cache.BytesHeld,
                    "BytesHeld must equal the sum of each held entry's estimated bytes.");
            }
            finally
            {
                cache.Dispose();
            }
        }

        // ── Empty-layer completeness marker ─────────────────────────────────────────────────────

        [Test]
        public void NullMeshMarker_IsAValidZeroByteEntry()
        {
            var cache = new PreparedTileCache(byteBudget: 0, countCap: 0);
            var key   = new PreparedKey(StyleToken.Default, Tile0, 2);

            cache.Put(key, null);

            Assert.IsTrue(cache.Contains(key), "A null-mesh (empty-layer) marker must be a valid entry.");
            Assert.AreEqual(0, cache.BytesHeld, "A null-mesh marker costs 0 bytes.");
            Assert.IsTrue(cache.TryTake(key, out Mesh mesh));
            Assert.IsNull(mesh, "TryTake on a marker entry must hand back null (not throw / fabricate a mesh).");

            cache.Dispose();
        }

        // ── Destroy-once (double-free guard at the cache-unit level) ───────────────────────────

        [Test]
        public void Dispose_DestroysHeldMeshes_Once()
        {
            var cache = new PreparedTileCache(byteBudget: 0, countCap: 0);
            Mesh mesh = MakeMesh(5);
            cache.Put(new PreparedKey(StyleToken.Default, Tile0, 0), mesh);

            cache.Dispose();
            Assert.IsTrue(mesh == null, "Dispose must destroy every held mesh.");

            // Idempotent — a second Dispose (mirroring TileManager.Dispose's idempotence contract) must not throw.
            Assert.DoesNotThrow(() => cache.Dispose());
        }
    }
}
