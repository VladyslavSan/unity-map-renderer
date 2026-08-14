// Unity EditMode only — shared scaffold for the MapView.SetStyle commit-atomicity tests
// (MapViewBackgroundRestyleTests + MapViewMaterialValidationOrderingTests). A restyle must keep the
// PREVIOUS style live until the single synchronous post-await commit; both files drive that seam through a
// main-thread gated loader and assert the old style survives resolution/cancel/validation-failure.

using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    /// <summary>Scaffold shared by the SetStyle commit-atomicity fixtures. Identical helpers had been
    /// pasted into both files; hoisted here so a change to the gate/spin protocol lands once.</summary>
    internal static class SetStyleAtomicity
    {
        /// <summary>A background-only style (no fetching layer ⇒ its own SetStyle commits synchronously).</summary>
        public static string BackgroundOnlyStyle(string colorHex) => $@"{{
            ""version"": 8,
            ""layers"": [ {{ ""id"": ""bg"", ""type"": ""background"",
                             ""paint"": {{ ""background-color"": ""{colorHex}"" }} }} ]
        }}";

        /// <summary>Background + ONE url-source fill, so <c>MapView.BuildSourceSpecs</c>' await(loader)
        /// genuinely suspends until the test releases the gate.</summary>
        public const string BackgroundPlusUrlSourceStyle = @"{
            ""version"": 8,
            ""sources"": { ""s"": { ""type"": ""vector"", ""url"": ""https://example.com/tilejson.json"" } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#ff0000"" } },
                { ""id"": ""f"",  ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": ""#0000ff"" } }
            ]
        }";

        /// <summary>A gated document loader: <c>await</c>ing <see cref="Load"/> suspends (returns control to
        /// the caller) until <see cref="Release"/> is called — entirely on the MAIN thread throughout (a
        /// <see cref="UniTaskCompletionSource{T}"/>-backed task, never a ThreadPool hop), so <see cref="Release"/>
        /// runs the REST of MapView.SetStyle's synchronous commit (Unity APIs, main-thread only) inline, exactly
        /// like production's UnityWebRequest-backed loader resuming on the main thread.</summary>
        public sealed class GatedLoader
        {
            private readonly UniTaskCompletionSource<string> _tcs = new UniTaskCompletionSource<string>();

            /// <summary>The TileJSON handed back when the gate is released.</summary>
            public string TileJsonText = @"{ ""tilejson"":""3.0.0"", ""tiles"":[""https://example.com/{z}/{x}/{y}.pbf""], ""minzoom"":0, ""maxzoom"":0 }";

            /// <summary>Number of times <see cref="Load"/> was invoked.</summary>
            public int CallCount;

            /// <summary>The loader delegate to install as <c>view.View.DocumentLoaderOverride</c>.</summary>
            public UniTask<string> Load(string uri, CancellationToken ct)
            {
                Interlocked.Increment(ref CallCount);
                return _tcs.Task;
            }

            /// <summary>Completes the pending <see cref="Load"/>, resuming SetStyle's commit on the main thread.</summary>
            public void Release() => _tcs.TrySetResult(TileJsonText);
        }

        /// <summary>Spins to completion WITHOUT observing the result — used where the caller expects (and
        /// separately asserts on) a fault/cancel.</summary>
        public static void SpinToCompleted(UniTask task, int maxSpins = 20000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) Thread.Sleep(1);
        }

        /// <summary>Spins to completion and RE-THROWS on fault/cancel — used where the caller expects
        /// success, so a regression surfaces as the real exception instead of a silently-stale assertion.</summary>
        public static void SpinToSucceeded(UniTask task, int maxSpins = 20000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) Thread.Sleep(1);
            t.GetAwaiter().GetResult();
        }

        /// <summary>The universal "the OLD style is still live" invariant these fixtures assert (identity
        /// unchanged, and the old background layer/material still current). Callers add their own extra pins
        /// (layer count, RenderLayerSet identity) inline.</summary>
        /// <param name="view">The MapView under test.</param>
        /// <param name="oldBackgroundMaterial">The style-A background material captured before the restyle.</param>
        /// <param name="expectedStyleId">The style id that must still be reported (default "A").</param>
        /// <param name="because">Context appended to the assertion messages.</param>
        public static void AssertOldStyleIntact(
            MapViewComponent view, Material oldBackgroundMaterial, string expectedStyleId = "A", string because = "")
        {
            Assert.AreEqual(expectedStyleId, view.StyleId, $"the OLD identity must still be reported. {because}");
            Assert.AreSame(oldBackgroundMaterial, view.Layers[0].Material,
                $"the OLD background layer/material must still be live. {because}");
        }
    }
}
