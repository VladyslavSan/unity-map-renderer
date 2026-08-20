// Unity EditMode only.

using System.IO;
using System.Threading;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// The load-bearing guard for the Dense property-storage win: <see cref="MapViewConfig"/> is the ONE
    /// production knob that <c>MapView.BuildSourceSpecs</c> resolves into the MVT decode path's
    /// <c>MvtPropertyStorage</c> argument, and the sole production <c>MvtTileFeatureSource</c> construction
    /// passes that resolved value explicitly. Nothing else in production reaches Dense — every other decode
    /// call site (the ~55 no-arg test call sites, and the API defaults themselves) stays Dictionary by
    /// design, as the maintainer-requested A/B oracle baseline (see <c>MvtPropertyStorage</c>). This tooth is
    /// the ONLY thing that reds if the production default silently reverts to Dictionary: the decode-side
    /// alloc tooth <c>Decode_SampleTile_DenseStorage_AllocatesFarUnderDictionary</c>
    /// (<c>DecodeGeometryFlattenAllocTests</c>) calls <c>Decode(..., Dense)</c> directly and stays green
    /// regardless of what production wires up.
    /// </summary>
    [TestFixture]
    public class ProductionPropertyStorageDefaultTests
    {
        [Test]
        public void ProductionConfig_DefaultsToDensePropertyStorage()
        {
            var config = new MapViewConfig();

            Assert.That(config.PropertyStorage, Is.EqualTo(PropertyStorageMode.Dense),
                "production decode must default to Dense — reverting this silently un-does the ~300 KB/tile " +
                "decode-allocation win the flatten stage measured, since Dense is reached ONLY through this " +
                "default (MapView.BuildSourceSpecs resolves it, and it is the sole production caller).");
        }

        /// <summary>
        /// The seam <see cref="ProductionConfig_DefaultsToDensePropertyStorage"/> does NOT cover: production
        /// reaches Dense through a SECOND step, the ternary at <c>MapView.BuildSourceSpecs</c> that resolves
        /// <see cref="MapViewConfig.PropertyStorage"/> into the decode-side <see cref="MvtPropertyStorage"/>
        /// argument. A tooth pinning only the config field stays green if that ternary is hardcoded to
        /// <c>Dictionary</c> — every tile would then decode through Dictionary and silently lose the
        /// ~300 KB/tile win, undetected. This tooth drives the real production method (widened
        /// <c>private</c> → <c>internal</c> for this one test seam, no behaviour change) with a real
        /// <c>tiles[]</c> vector source + fill layer, takes the <see cref="MvtTileFeatureSource"/> it
        /// actually constructs, decodes the sample fixture through it, and pins the RESOLVED store type —
        /// not just the config value the ternary reads. RED-verify: hardcode the ternary's result to
        /// <c>MvtPropertyStorage.Dictionary</c> and this reds; the field-only tooth above does not.
        /// </summary>
        [Test]
        public void ProductionConfig_ResolvesToDenseThroughBuildSourceSpecs()
        {
            byte[] fixtureBytes = File.ReadAllBytes(
                Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes"));

            const string styleJson = @"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
                ""layers"": [
                    { ""id"": ""s-fill"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""countries"",
                      ""paint"": { ""fill-color"": ""#ff0000"" } }
                ]
            }";
            StyleDocument style = StyleParser.Parse(styleJson);

            var go = new GameObject("ProductionPropertyStorageDefaultTests_BuildSourceSpecs");
            try
            {
                var view = go.AddComponent<MapViewComponent>().WithTestMaterials();
                view.WithTestCamera();
                // Never actually dials the network: the tiles[]-only source needs no TileJSON fetch, so
                // BuildSourceSpecs never awaits (SetSources still constructs the source lazily via
                // SourceSpec.CreateSource — this override supplies its bytes when that runs, below).
                view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(fixtureBytes);

                List<TileManager.SourceSpec> specs = Await(view.View.BuildSourceSpecs(style, CancellationToken.None));
                Assert.AreEqual(1, specs.Count, "precondition: exactly one source spec resolved from the style");

                using var source = (MvtTileFeatureSource)specs[0].CreateSource();
                // GetTile's decode runs OFF the main thread by design (Epic A / A7) — a plain `await` here
                // (an async Task test method) risks resuming past this point on a thread-pool thread, and
                // the DestroyImmediate below is main-thread-only. Await() blocks the CALLING (main) thread
                // instead, mirroring GeoJsonSourceTests.SpinToCompleted, so every line after it still runs
                // on the main thread.
                SharedDisposable<IDecodedTile> handle = Await(source.GetTile(new TileId { Z = 0, X = 0, Y = 0 }));
                Assert.IsNotNull(handle, "precondition: the fixture bytes must decode to a handle");

                var layer = (MvtLayer)handle.Value.GetLayer("countries");
                Assert.Greater(layer.Features.Count, 0, "precondition: the fixture's 'countries' layer has features");
                var feature = (MvtFeature)layer.Features[0];

                Assert.IsInstanceOf<DensePropertyStore>(feature.Store,
                    "the RESOLVED property storage — what MapView.BuildSourceSpecs' ternary actually passes " +
                    "to the MvtTileFeatureSource it constructs — must be Dense. A ternary hardcoded (or " +
                    "flipped) to Dictionary decodes into a DictionaryPropertyStore here even though the " +
                    "config field-only tooth above stays green.");

                handle.Release();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask{T}"/> — parks the calling
        /// thread on a kernel event (<c>UniTaskParkExtensions.WaitOffPlayerLoop</c>) rather than yielding
        /// the continuation to whatever thread completes the task, so the caller never leaves the main
        /// thread mid-test.</summary>
        private static T Await<T>(UniTask<T> task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            return t.GetAwaiter().GetResult();
        }
    }
}
