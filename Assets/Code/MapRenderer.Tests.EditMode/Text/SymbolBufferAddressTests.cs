// Unity EditMode only. It drives SymbolFeatureExtractor.Extract, which materializes/reads a Waist-1
// TileGeometryBuffers and therefore depends on Unity.Collections — must not be added to core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// IR C1 fix stage, B2/B3: <b>the symbol consumer reads its tile address, its extent and its feature
    /// count off the BUFFER</b>, not off a caller-supplied argument and not off the layer's feature list.
    ///
    /// <para>P2/P3 removed the second address copy from the mesh consumers —
    /// <c>ITileMeshRenderLayer.WriteInto</c> lost its <c>TileId</c> parameter and fill/line read
    /// <c>geometry.Tile</c> — but symbol kept taking one, so <c>MvtDecoder</c>'s claim that the address
    /// "enters the pipeline exactly ONCE, at the fetch" was false for exactly one consumer. These are the
    /// teeth for closing it; each one moves ONE of the three quantities and pins that the output follows the
    /// buffer.</para>
    /// </summary>
    [TestFixture]
    public class SymbolBufferAddressTests
    {
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

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

        private static readonly TileId DecodedAt = new TileId { Z = 0, X = 0, Y = 0 };
        private static readonly TileId WrongTile = new TileId { Z = 1, X = 1, Y = 0 };

        private static SymbolStyle.StyleLayer CentroidsLayer() => new SymbolStyle.StyleLayer
        {
            Id          = "labels",
            LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
            SourceLayer = "centroids",
            Paint       = SymbolStyle.PaintProperties.Parse(null),
            Layout      = SymbolStyle.LayoutProperties.Parse(JsonParser.Parse("{\"text-field\":\"{NAME}\"}")),
        };

        private static List<SymbolStyle.SymbolFeature> Extract(
            SymbolStyle.StyleLayer layer, IDecodedTile tile, TileId callerSuppliedId)
        {
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, callerSuppliedId, 0.0, new WebMercatorProjection(), symbols);
            return symbols;
        }

        // ── B2 · the ADDRESS, in the production configuration ─────────────────────────────────────────

        /// <summary>
        /// A real <c>MvtTile</c>, decoded at one address and then extracted with a DIFFERENT address handed
        /// to <c>Extract</c>. Nothing synthetic: this is precisely the mispairing the caller-supplied
        /// parameter makes expressible, and the whole reason the mesh seam stopped taking one.
        /// </summary>
        [Test]
        public void TheProjectionUsesTheBuffersOwnAddress_NotTheCallerSuppliedOne()
        {
            byte[] bytes = LoadFixture();
            MvtTile decodedHere  = TestDecodedTiles.Track(MvtDecoder.Decode(DecodedAt, bytes));
            MvtTile decodedThere = TestDecodedTiles.Track(MvtDecoder.Decode(WrongTile, bytes));

            List<SymbolStyle.SymbolFeature> fromHere  = Extract(CentroidsLayer(), decodedHere,  DecodedAt);
            List<SymbolStyle.SymbolFeature> fromThere = Extract(CentroidsLayer(), decodedThere, WrongTile);

            // Anti-vacuity, and it is the whole tooth: if the address did not move the output, corrupting it
            // below would prove nothing. Assert BOTH observable consequences separately — the projected
            // anchor and the packed TileKey — so a change that stops one from depending on the address
            // cannot hide behind the other.
            Assert.Greater(fromHere.Count, 0, "precondition: the fixture must yield symbols at all");
            Assert.AreEqual(fromHere.Count, fromThere.Count, "precondition: the same features are selected either way");
            Assert.AreNotEqual(fromHere[0].AnchorRender.x, fromThere[0].AnchorRender.x,
                "precondition: the tile address must genuinely move the projected anchor");
            Assert.AreNotEqual(fromHere[0].TileKey, fromThere[0].TileKey,
                "precondition: the tile address must genuinely move the packed TileKey");

            // The tooth: same decoded tile (buffer stamped DecodedAt), a CORRUPTED caller-supplied address.
            List<SymbolStyle.SymbolFeature> corruptedCaller = Extract(CentroidsLayer(), decodedHere, WrongTile);

            Assert.AreEqual(fromHere.Count, corruptedCaller.Count);
            for (int i = 0; i < fromHere.Count; i++)
            {
                Assert.AreEqual(fromHere[i].AnchorRender.x, corruptedCaller[i].AnchorRender.x,
                    $"label {i}: the anchor must come from the BUFFER's address, not the caller's");
                Assert.AreEqual(fromHere[i].AnchorRender.y, corruptedCaller[i].AnchorRender.y, $"label {i} (y)");
                Assert.AreEqual(fromHere[i].AnchorRender.z, corruptedCaller[i].AnchorRender.z, $"label {i} (z)");
                Assert.AreEqual(fromHere[i].TileKey, corruptedCaller[i].TileKey,
                    $"label {i}: the TileKey must be packed from the BUFFER's address");
            }
        }

        // ── B2 · the EXTENT ───────────────────────────────────────────────────────────────────────────

        /// <summary>An <see cref="ITileLayer"/> that forwards everything except <see cref="Extent"/>, which
        /// it misreports. <b>Synthetic by necessity</b>: for MVT the layer's extent and its buffer's extent
        /// are the same value by construction (<c>MvtDecoder</c> stamps one into the other), so no
        /// production configuration can tell the two reads apart today. The shape becomes constructible the
        /// moment a second <see cref="ITileLayer"/> producer exists, which is what this pins against.</summary>
        private sealed class ExtentMisreportingLayer : ITileLayer
        {
            private readonly ITileLayer _inner;
            public ExtentMisreportingLayer(ITileLayer inner, uint misreportedExtent)
            {
                _inner = inner;
                Extent = misreportedExtent;
            }

            public string                  Name     => _inner.Name;
            public uint                    Extent   { get; }
            public IReadOnlyList<IFeature> Features => _inner.Features;
            public TileGeometryBuffers     Geometry => _inner.Geometry;
        }

        /// <summary>A one-layer <see cref="IDecodedTile"/> over a supplied layer. Owns nothing — the
        /// wrapped layer's buffer belongs to the tile <see cref="TestDecodedTiles"/> already tracks.</summary>
        private sealed class SingleLayerTile : IDecodedTile
        {
            private readonly ITileLayer _layer;
            public SingleLayerTile(ITileLayer layer) => _layer = layer;
            public ITileLayer GetLayer(string name) => name == _layer.Name ? _layer : null;
            public void Dispose() { }
        }

        /// <summary>Protobuf zigzag ENcode, for hand-building a one-point MVT command stream.</summary>
        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        [Test]
        public void TheProjectionUsesTheBuffersOwnExtent_NotTheLayersDeclaredOne()
        {
            const uint bufferExtent      = 4096;
            const uint misreportedExtent = 2048;

            // One point at (2048, 2048) — dead centre of a 4096 tile, and exactly ON the upper bound of a
            // 2048 one. EmitAtAnchor's single-world clip is `[0, extent)`, so reading the misreported extent
            // does not merely shift the anchor, it DROPS the symbol: a discriminator with no tolerance in it.
            var point = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Point,
                Geometry     = new uint[] { (1u) | (1u << 3), ZigZagEncode(2048), ZigZagEncode(2048) },
            };

            InMemoryDecodedTile real = TestDecodedTiles.Of("pts", DecodedAt, new List<IFeature> { point }, bufferExtent);
            ITileLayer          honest = real.GetLayer("pts");

            Assert.AreEqual(bufferExtent, (uint)honest.Geometry.Extent,
                "precondition: the buffer must carry the real extent");

            var layer = new SymbolStyle.StyleLayer
            {
                Id          = "labels",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "pts",
                Paint       = SymbolStyle.PaintProperties.Parse(null),
                Layout      = SymbolStyle.LayoutProperties.Parse(JsonParser.Parse("{\"text-field\":\"X\"}")),
            };

            List<SymbolStyle.SymbolFeature> honestSymbols = Extract(layer, real, DecodedAt);
            Assert.AreEqual(1, honestSymbols.Count, "precondition: the honest layer yields exactly one label");

            var lying = new SingleLayerTile(new ExtentMisreportingLayer(honest, misreportedExtent));
            List<SymbolStyle.SymbolFeature> symbols = Extract(layer, lying, DecodedAt);

            Assert.AreEqual(1, symbols.Count,
                "the extent must come from the BUFFER (4096), where the point is mid-tile. Read from the " +
                "layer's declared 2048 instead and the point sits ON the exclusive upper bound of the " +
                "single-world clip, so the label is silently dropped.");
            Assert.AreEqual(honestSymbols[0].AnchorRender.x, symbols[0].AnchorRender.x, "anchor x must not move");
            Assert.AreEqual(honestSymbols[0].AnchorRender.y, symbols[0].AnchorRender.y, "anchor y must not move");
            Assert.AreEqual(honestSymbols[0].AnchorRender.z, symbols[0].AnchorRender.z, "anchor z must not move");
        }

        // ── B3 · the FEATURE COUNT the ring buckets are sized from ────────────────────────────────────

        /// <summary>An <see cref="ITileLayer"/> exposing a PREFIX of the wrapped layer's features while
        /// forwarding the whole buffer — the mispairing shape <c>MvtLayer</c>'s lockstep rules out for MVT
        /// and nothing structural rules out for a second producer. Synthetic, and deliberately so: the
        /// property under test is "the bucket array is sized to the domain of the index that addresses it",
        /// and no MVT input can separate the two counts.</summary>
        private sealed class TruncatedFeatureListLayer : ITileLayer
        {
            private readonly ITileLayer _inner;
            public TruncatedFeatureListLayer(ITileLayer inner, int exposedCount)
            {
                _inner = inner;
                var kept = new List<IFeature>(exposedCount);
                for (int i = 0; i < exposedCount; i++) kept.Add(inner.Features[i]);
                Features = kept;
            }

            public string                  Name     => _inner.Name;
            public uint                    Extent   => _inner.Extent;
            public IReadOnlyList<IFeature> Features { get; }
            public TileGeometryBuffers     Geometry => _inner.Geometry;
        }

        [Test]
        public void TheRingBucketsAreSizedFromTheBuffersFeatureColumn_NotTheFeatureList()
        {
            var features = new List<IFeature>();
            for (int i = 0; i < 3; i++)
                features.Add(new InMemoryTileFeature
                {
                    GeometryType = TileGeometryType.Point,
                    Geometry     = new uint[] { (1u) | (1u << 3), ZigZagEncode(100 + i * 100), ZigZagEncode(100) },
                });

            InMemoryDecodedTile real  = TestDecodedTiles.Of("pts", DecodedAt, features);
            ITileLayer          whole = real.GetLayer("pts");

            Assert.AreEqual(3, whole.Geometry.FeatureCount, "precondition: the buffer's feature column holds 3");
            Assert.AreEqual(3, whole.Geometry.RingCount,    "precondition: one ring per point feature");

            // The highest ring→feature index the buffer carries must EXCEED the truncated list, or the two
            // sizings cannot be told apart.
            int highestRingFeatureIdx = 0;
            for (int r = 0; r < whole.Geometry.RingCount; r++)
                highestRingFeatureIdx = math.max(highestRingFeatureIdx, whole.Geometry.RingFeatureIdx[r]);
            Assert.AreEqual(2, highestRingFeatureIdx,
                "precondition: a ring must be attributed to a feature index beyond the truncated list");

            var truncated = new SingleLayerTile(new TruncatedFeatureListLayer(whole, exposedCount: 2));

            var layer = new SymbolStyle.StyleLayer
            {
                Id          = "labels",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "pts",
                Paint       = SymbolStyle.PaintProperties.Parse(null),
                Layout      = SymbolStyle.LayoutProperties.Parse(JsonParser.Parse("{\"text-field\":\"X\"}")),
            };

            // Asserted, not merely observed by the runner catching a throw: the defect's manifestation IS an
            // out-of-range index, so wrapping it makes the failing ASSERTION the one that names the property,
            // rather than a bare stack trace that could be read as an unrelated crash.
            List<SymbolStyle.SymbolFeature> symbols = null;
            Assert.DoesNotThrow(() => symbols = Extract(layer, truncated, DecodedAt),
                "the counting sort must be sized from geometry.FeatureCount — RingFeatureIdx's values index " +
                "the buffer's OWN feature column, so sizing from Features.Count indexes ringStart out of " +
                "range the moment the two disagree (and mis-buckets silently before that).");

            Assert.AreEqual(2, symbols.Count, "one label per exposed feature, bucketed correctly");
        }
    }
}
