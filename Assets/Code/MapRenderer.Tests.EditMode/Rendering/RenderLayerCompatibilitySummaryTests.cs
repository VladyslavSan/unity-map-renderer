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
using MapRenderer.Unity.Rendering.Materials;
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
                set.ApplyZoom(new StyleFrameInputs(frame * 0.1, 1.0, 0.0));

            Assert.AreEqual(countAfterBuild, set.SkippedLayers.Count,
                "50 simulated frames must not change the compatibility summary — it is bounded to style load.");
        }

        // ── UMR-95 stage 1: the numbering fold's granularity (MapView.LayerNumbering) ──────────────────

        // Extrusion present; the OTHER layer (circle-b, an unsupported kind) is skipped — the lone survivor
        // is the extrusion layer, at dense index 0.
        private const string ExtrusionPresentOtherSkippedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""ext-layer"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-extrusion-height"": 20 } },
        { ""id"": ""circle-b"", ""type"": ""circle"", ""source"": ""s"", ""source-layer"": ""b"" }
    ]
}";

        // Extrusion skipped (via the material below); the OTHER layer (a real line) is present — the lone
        // survivor is the line layer, also at dense index 0. Equal SURVIVOR COUNT to the style above (1),
        // but a different (index, id) pair.
        private const string ExtrusionSkippedOtherPresentStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""ext-layer2"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-extrusion-height"": 20 } },
        { ""id"": ""line-other"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1], ""line-width"": 2 } }
    ]
}";

        /// <summary>A committed <see cref="MapMaterialSet"/> clone with <c>FillExtrusionMaterial</c> unset —
        /// the one lever this codebase has to skip an extrusion layer without touching style content
        /// (<c>FillMaterial</c>/<c>LineMaterial</c>/<c>SymbolTextWorld</c> are all <c>Validate()</c>-required,
        /// never absent).</summary>
        private static MapMaterialSet WithoutExtrusionMaterial()
        {
            var lit = MapMaterialSetTestUtil.Load();
            var set = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.FillMaterial    = lit.FillMaterial;
            set.LineMaterial    = lit.LineMaterial;
            set.SymbolTextWorld = lit.SymbolTextWorld;
            return set;
        }

        /// <summary>Pins that <see cref="MapView.LayerNumbering"/> folds the actual (dense index, id) PAIRS,
        /// not merely the survivor COUNT. The two styles here produce the SAME count (1) by different skip
        /// patterns — extrusion present with the other layer skipped, versus extrusion skipped with the
        /// other layer present — so a fold that degraded to <c>layers.Count</c> would see them as identical
        /// and let the second style serve the first's stale bake. RED recipe: replace
        /// <see cref="MapView.LayerNumbering"/>'s body with <c>layers.Count.ToString()</c> — both sides then
        /// return <c>"1"</c> and this assertion fails.</summary>
        [Test]
        public void LayerNumbering_EqualSurvivorCount_DifferentSkipPattern_ProducesDifferentNumbering()
        {
            using RenderLayerSet extrusionPresent = Build(ExtrusionPresentOtherSkippedStyleJson);
            Assert.AreEqual(1, extrusionPresent.Count, "drive precondition: only the extrusion layer survives.");

            var extrusionLessMaterials = WithoutExtrusionMaterial();
            try
            {
                using RenderLayerSet extrusionSkipped = new RenderLayerSet();
                extrusionSkipped.Build(StyleParser.Parse(ExtrusionSkippedOtherPresentStyleJson), 0.0, extrusionLessMaterials);
                Assert.AreEqual(1, extrusionSkipped.Count, "drive precondition: only the line layer survives.");

                Assert.AreNotEqual(
                    MapView.LayerNumbering(extrusionPresent), MapView.LayerNumbering(extrusionSkipped),
                    "equal survivor counts from different skip patterns must still fold to different tokens.");
            }
            finally
            {
                // The RenderLayerSet above disposes (its `using`) BEFORE this — each layer's Material is a
                // CLONE of extrusionLessMaterials' base materials, never the base itself, but disposing the
                // consumer before its source is the safer order regardless.
                Object.DestroyImmediate(extrusionLessMaterials);
            }
        }

        // Same id SET, same count (2), different declared ORDER — the numbering fold's other blind spot: a
        // fold that folded an unordered id set (or sorted the ids "for determinism") would pass the test
        // above (one survivor each side) yet still miss this — dense POSITION is part of the cache key
        // because cached geometry is baked against a dense INDEX, not just an id.
        private const string FillThenLineStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""line-b"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1], ""line-width"": 2 } }
    ]
}";

        private const string LineThenFillStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""line-b"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""line-color"": [""rgba"",0,255,0,1], ""line-width"": 2 } },
        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } }
    ]
}";

        /// <summary>Pins that the fold is sensitive to dense POSITION, not just the id SET. RED recipe: fold
        /// <c>layers[li].StyleLayer?.Id</c> into a set/sorted list instead of an ordered <c>(li, id)</c> walk
        /// — both sides carry the same two ids and this assertion fails.</summary>
        [Test]
        public void LayerNumbering_SameIdSet_DifferentOrder_ProducesDifferentNumbering()
        {
            using RenderLayerSet fillThenLine = Build(FillThenLineStyleJson);
            using RenderLayerSet lineThenFill = Build(LineThenFillStyleJson);
            Assert.AreEqual(2, fillThenLine.Count);
            Assert.AreEqual(2, lineThenFill.Count);

            Assert.AreNotEqual(
                MapView.LayerNumbering(fillThenLine), MapView.LayerNumbering(lineThenFill),
                "the same two ids in a different declared order must fold to different tokens.");
        }

        // ── Layers that can never draw are never constructed ──────────────────────────────────────

        // No shipped style uses either of these, so the cases are CONSTRUCTED here rather than sampled:
        // a `visibility: none` layer, and one authored at a constant fully-transparent opacity. Both sit
        // between two layers that do render, so the surviving slots are observable across the skip.
        private const string NeverDrawnInterleavedStyleJson = @"{
    ""version"": 8,
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill-a"",      ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""hidden-b"",    ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"",
          ""layout"": { ""visibility"": ""none"" }, ""paint"": { ""fill-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""clear-c"",     ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"",
          ""paint"": { ""fill-color"": [""rgba"",0,0,255,1], ""fill-opacity"": 0 } },
        { ""id"": ""line-d"",      ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""d"", ""paint"": { ""line-color"": [""rgba"",0,0,0,1] } }
    ]
}";

        /// <summary>
        /// A layer that can never draw takes no slot: <c>visibility: none</c> and a CONSTANT
        /// fully-transparent opacity are both refused at construction, and the layers around them stay
        /// contiguous — the same contract an unsupported kind already has.
        /// </summary>
        [Test]
        public void NeverDrawnLayers_AreNeverConstructed_SurvivorsStayContiguous()
        {
            using RenderLayerSet set = Build(NeverDrawnInterleavedStyleJson);

            Assert.AreEqual(2, set.Count,
                "only fill-a and line-d can ever draw. A count of 4 means a layer that paints nothing at " +
                "any zoom still owns a slot, a material and a backend registration.");
            Assert.AreEqual("fill-a", set[0].StyleLayer.Id);
            Assert.AreEqual(0, set[0].DrawIndex);
            Assert.AreEqual("line-d", set[1].StyleLayer.Id);
            Assert.AreEqual(1, set[1].DrawIndex,
                "line-d must land at slot 1 — neither skipped layer may reserve a slot.");

            var byId = new Dictionary<string, LayerSkipReason>();
            foreach (SkippedLayer sl in set.SkippedLayers) byId[sl.Id] = sl.Reason;
            Assert.AreEqual(LayerSkipReason.Hidden, byId["hidden-b"],
                "a `visibility: none` layer must be reported as Hidden, not silently dropped.");
            Assert.AreEqual(LayerSkipReason.FullyTransparent, byId["clear-c"],
                "a constant fully-transparent layer must be reported as FullyTransparent — a DIFFERENT " +
                "reason from Hidden, so the summary says which of the two the author wrote.");
        }

        /// <summary>
        /// Neither never-drawn layer is a compatibility gap, so <see cref="MapView.LogSkippedLayers"/> stays
        /// silent about both. Warning here would put a line in every log for a style doing what its author
        /// asked.
        /// </summary>
        [Test]
        public void LogSkippedLayers_SilentForALayerThatCanNeverDraw()
        {
            MapView.LogSkippedLayers(new List<SkippedLayer>
            {
                new SkippedLayer { Id = "hidden-b", RawType = "fill", Reason = LayerSkipReason.Hidden },
                new SkippedLayer { Id = "clear-c", RawType = "fill", Reason = LayerSkipReason.FullyTransparent },
            });
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// A source read only by layers that can never draw is never fetched, so its tiles are never
        /// decoded either. <c>TryGetFetchSource</c> is the one registry <c>MapView.BuildSourceSpecs</c>
        /// derives from, so excluding a layer here is what stops the whole per-tile pipeline for it.
        /// </summary>
        [Test]
        public void TryGetFetchSource_RefusesALayerThatCanNeverDraw()
        {
            StyleDocument doc = StyleParser.Parse(NeverDrawnInterleavedStyleJson);
            var fetched = new List<string>();
            foreach (StyleLayer sl in doc.Layers)
                if (RenderLayerFactory.TryGetFetchSource(sl, out string sid))
                    fetched.Add(sl.Id + "=>" + sid);

            CollectionAssert.AreEquivalent(new[] { "fill-a=>s", "line-d=>s" }, fetched,
                "only the two layers that can draw may register a fetch source. Including hidden-b or " +
                "clear-c downloads and MVT-decodes tiles for a layer whose pixels can never reach the " +
                "screen — the cost the draw gate cannot reach, because it acts after the mesh exists.");
        }
    }
}
