// Unity EditMode only — uses RenderLayerSet, RenderLayerFactory, MaterialFactory (per-layer Materials).
// NOT included in Tools/core-tests.
//
// UMR-116: a style layer of an unsupported kind used to vanish with no explanation
// (RenderLayerFactory.Create returned a bare null; RenderLayerSet.Build's "if (layer == null) continue;"
// dropped it on the floor). These teeth pin the style-load compatibility summary that replaces the
// silence: every skipped layer's id, raw type and WHY, collected once per Build — never per tile, never
// per frame — with the supported layers around it still taking their slots undisturbed.

using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.Tests.Rendering
{
    [TestFixture]
    public class RenderLayerCompatibilitySummaryTests
    {
        // A circle layer — the ticket's own DONE WHEN example — interleaved with two layers that DO
        // render, so the surviving slots' declared-order/DrawIndex sequence is observable across the skip.
        private const string CircleInterleavedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"",   ""type"": ""fill"",   ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""circle-b"", ""type"": ""circle"", ""source"": ""s"", ""source-layer"": ""b"" },
        { ""id"": ""line-c"",   ""type"": ""line"",   ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1] } }
    ]
}";

        // A symbol layer with NO "source" key — the by-design (not unsupported) skip case.
        private const string SourcelessSymbolStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"",    ""type"": ""fill"",   ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""symbol-nosrc"", ""type"": ""symbol"", ""layout"": { ""text-field"": ""{NAME}"" } }
    ]
}";

        private static RenderLayerSet Build(string json)
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        /// <summary>The summary is REPLACED, not appended to, on a restyle — <see cref="RenderLayerSet.SkippedLayers"/>'s
        /// own doc promises an old style's skips "stop applying the moment a new style replaces it"; nothing
        /// else in this repo ever calls <see cref="RenderLayerSet.Build"/> twice on the same instance, so
        /// this is the only coverage of that promise. RED recipe: delete the <c>_skippedLayers.Clear()</c>
        /// line in <see cref="RenderLayerSet"/>'s private <c>ClearLayers</c> — the count becomes 2 and
        /// <c>[0]</c> is the stale <c>circle-b</c> from the first style.</summary>
        [Test]
        public void SkippedLayers_ReplacedNotAppended_OnRestyle()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);
            set.Build(StyleParser.Parse(SourcelessSymbolStyleJson), 0.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(1, set.SkippedLayers.Count, "a restyle replaces the summary — the old style's skips are gone.");
            Assert.AreEqual(LayerSkipReason.GenuinelyUnpainted, set.SkippedLayers[0].Reason);
        }

        /// <summary>Acceptance tooth 1 (UMR-116 DONE WHEN): the unsupported circle layer is named
        /// explicitly in the summary — id, raw type, reason — while the fill and line layers either side of
        /// it still render. RED recipe: revert <see cref="RenderLayerFactory"/>'s reason-reporting overload
        /// to the original bare-null <c>_ =&gt; null</c> arm (or otherwise stop threading
        /// <see cref="LayerSkipReason.UnsupportedKind"/> through) — this test's reason assertion fails.</summary>
        [Test]
        public void Build_CircleLayer_ReportedAsUnsupportedKind_SupportedLayersStillRender()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);

            Assert.AreEqual(2, set.Count, "fill-a and line-c must both still take a slot.");
            Assert.AreEqual(1, set.SkippedLayers.Count, "exactly circle-b is skipped.");

            SkippedLayer skip = set.SkippedLayers[0];
            Assert.AreEqual("circle-b", skip.Id, "the summary must name the skipped layer's OWN id.");
            Assert.AreEqual("circle", skip.RawType, "the summary must carry the raw type string, not just the enum.");
            Assert.AreEqual(LayerSkipReason.UnsupportedKind, skip.Reason,
                "an unsupported kind must report UnsupportedKind — not silence, and not lumped in with a " +
                "material-misconfiguration or by-design skip.");
        }

        /// <summary>Distinguishes the third reason from the other two (team-lead constraint): a source-less
        /// symbol layer has nothing to place BY DESIGN — not an unsupported kind, not a misconfiguration.
        /// RED recipe: delete the bare <c>Symbol.StyleLayer =&gt;</c> arm in <see cref="RenderLayerFactory.Create"/>
        /// — the layer falls through to the default arm and reports <see cref="LayerSkipReason.UnsupportedKind"/>
        /// instead, which this assertion catches.</summary>
        [Test]
        public void Build_SourcelessSymbolLayer_ReportedAsGenuinelyUnpainted()
        {
            using RenderLayerSet set = Build(SourcelessSymbolStyleJson);

            Assert.AreEqual(1, set.Count, "only fill-a renders.");
            Assert.AreEqual(1, set.SkippedLayers.Count);

            SkippedLayer skip = set.SkippedLayers[0];
            Assert.AreEqual("symbol-nosrc", skip.Id);
            Assert.AreEqual(LayerSkipReason.GenuinelyUnpainted, skip.Reason,
                "a source-less symbol layer is a by-design skip, not a compatibility gap.");
        }

        /// <summary>MapView.LogSkippedLayers must warn for an actual compatibility gap but stay silent for a
        /// by-design skip — the exact distinction UMR-116 asked the reason enum (not the log site) to carry.
        /// RED recipe: remove the <c>GenuinelyUnpainted</c> filter in <see cref="MapView.LogSkippedLayers"/>
        /// — the second call below then also warns, and <see cref="LogAssert.NoUnexpectedReceived"/> fails it.</summary>
        [Test]
        public void LogSkippedLayers_WarnsForCompatibilityGap_SilentForByDesignSkip()
        {
            var unsupported = new List<SkippedLayer>
            {
                new SkippedLayer { Id = "circle-b", RawType = "circle", Reason = LayerSkipReason.UnsupportedKind },
            };
            LogAssert.Expect(LogType.Warning, new Regex("circle-b.*UnsupportedKind"));
            MapView.LogSkippedLayers(unsupported);

            var byDesignOnly = new List<SkippedLayer>
            {
                new SkippedLayer { Id = "symbol-nosrc", RawType = "symbol", Reason = LayerSkipReason.GenuinelyUnpainted },
            };
            MapView.LogSkippedLayers(byDesignOnly);
            LogAssert.NoUnexpectedReceived(); // a by-design skip must not produce ANY warning
        }

        /// <summary>Acceptance tooth 2: a skipped layer consumes no draw slot and does not create a gap —
        /// the two rendering layers keep DrawIndex 0 and 1 (declared order, compacted), exactly as they
        /// would if circle-b were absent from the style entirely. RED recipe: increment <c>drawIndex</c> in
        /// <see cref="RenderLayerSet.Build"/>'s skipped-layer branch (as well as the real-layer branch) —
        /// line-c's DrawIndex becomes 2 instead of 1, desyncing its renderQueue slot from its Material index.</summary>
        [Test]
        public void Build_SkippedLayer_DoesNotConsumeADrawSlot_SurvivingLayersStayContiguous()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);

            Assert.AreEqual("fill-a", set[0].StyleLayer.Id);
            Assert.AreEqual(0, set[0].DrawIndex, "fill-a is the first declared layer — slot 0.");
            Assert.AreEqual("line-c", set[1].StyleLayer.Id);
            Assert.AreEqual(1, set[1].DrawIndex,
                "line-c must land at slot 1 — the skipped circle-b in between must not have reserved a slot.");
        }

        /// <summary>Acceptance tooth 3: the summary is a style-load snapshot, not a per-frame log — pumping
        /// the per-frame call (<see cref="RenderLayerSet.ApplyZoom"/>) many times over must never grow it.
        /// RED recipe: append to the skipped-layers list from ANY per-frame method instead of only from
        /// <see cref="RenderLayerSet.Build"/> — this test's repeated-count assertion catches the growth
        /// directly (no instrumentation needed).</summary>
        [Test]
        public void SkippedLayers_UnaffectedByRepeatedPerFrameCalls()
        {
            using RenderLayerSet set = Build(CircleInterleavedStyleJson);
            int countAfterBuild = set.SkippedLayers.Count;
            Assert.AreEqual(1, countAfterBuild, "sanity: circle-b was skipped by Build.");

            for (int frame = 0; frame < 50; frame++)
                set.ApplyZoom(frame * 0.1, 1.0);

            Assert.AreEqual(countAfterBuild, set.SkippedLayers.Count,
                "50 simulated frames must not change the compatibility summary — it is bounded to style load.");
        }
    }
}
