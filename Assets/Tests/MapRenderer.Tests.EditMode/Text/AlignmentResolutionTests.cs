// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Tests.Style; // SymbolTestFixtures lives in the Style test folder
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// P1 (pitch-alignment epic) — pins <see cref="AlignmentResolution.ResolvePitch"/>: the spec's
    /// pitch-alignment <c>auto</c> resolves against the RESOLVED rotation alignment (never the raw one), an
    /// explicit pitch value always wins, and the two enum arguments are not interchangeable. Engine-free;
    /// runs in both runners.
    /// </summary>
    [TestFixture]
    public class AlignmentResolutionTests
    {
        private static readonly AlignmentMode[] AllModes =
            { AlignmentMode.Auto, AlignmentMode.Map, AlignmentMode.Viewport };

        private static readonly SymbolPlacement[] AllPlacements =
            { SymbolPlacement.Point, SymbolPlacement.Line, SymbolPlacement.LineCenter };

        // T1 — the exhaustive 27-row resolution table (the core tooth).
        [Test]
        public void ResolvePitch_ExhaustiveTable_MatchesSpecChain()
        {
            foreach (AlignmentMode pitch in AllModes)
            foreach (AlignmentMode rotation in AllModes)
            foreach (SymbolPlacement placement in AllPlacements)
            {
                AlignmentMode expected = ExpectedFor(pitch, rotation, placement);
                AlignmentMode actual = AlignmentResolution.ResolvePitch(pitch, rotation, placement);
                Assert.AreEqual(expected, actual,
                    $"pitch={pitch}, rotation={rotation}, placement={placement}");
            }
        }

        // Hand-derivation of the table in the plan/spec — kept separate from the production switch so the
        // test doesn't just restate the implementation.
        private static AlignmentMode ExpectedFor(AlignmentMode pitch, AlignmentMode rotation, SymbolPlacement placement)
        {
            if (pitch == AlignmentMode.Map) return AlignmentMode.Map;
            if (pitch == AlignmentMode.Viewport) return AlignmentMode.Viewport;
            // pitch == Auto
            if (rotation == AlignmentMode.Map) return AlignmentMode.Map;
            if (rotation == AlignmentMode.Viewport) return AlignmentMode.Viewport;
            // rotation == Auto too: resolves by placement, exactly like Resolve().
            return placement == SymbolPlacement.Point ? AlignmentMode.Viewport : AlignmentMode.Map;
        }

        // T2 — never Auto, over the same 27 rows.
        [Test]
        public void ResolvePitch_NeverReturnsAuto()
        {
            foreach (AlignmentMode pitch in AllModes)
            foreach (AlignmentMode rotation in AllModes)
            foreach (SymbolPlacement placement in AllPlacements)
            {
                AlignmentMode actual = AlignmentResolution.ResolvePitch(pitch, rotation, placement);
                Assert.AreNotEqual(AlignmentMode.Auto, actual,
                    $"ResolvePitch's contract is a RESOLVED value — pitch={pitch}, rotation={rotation}, placement={placement}");
            }
        }

        // T3 — the argument order is not symmetric; kills a swapped-parameter implementation.
        [Test]
        public void ResolvePitch_ArgumentOrder_IsNotSymmetric()
        {
            Assert.AreEqual(AlignmentMode.Map,
                AlignmentResolution.ResolvePitch(AlignmentMode.Map, AlignmentMode.Viewport, SymbolPlacement.Point),
                "an explicit Map pitch wins over a Viewport rotation");
            Assert.AreEqual(AlignmentMode.Viewport,
                AlignmentResolution.ResolvePitch(AlignmentMode.Viewport, AlignmentMode.Map, SymbolPlacement.Point),
                "an explicit Viewport pitch wins over a Map rotation");
        }

        // T5 — grounded on shipped liberty layers, in both directions.
        [Test]
        public void ResolvePitch_Liberty_TextLineLayers_ResolveMap()
        {
            // highway-name-path/-minor/-major: text-rotation-alignment is EXPLICIT 'map' (not auto), and
            // text-pitch-alignment is absent (auto). This grounds the explicit-rotation pass-through
            // (Resolve(Map, *) = Map regardless of placement) on real shipped data — it is NOT the
            // auto-auto-placement chain (that genuine witness is road_one_way_arrow* below, where BOTH
            // keys are absent).
            foreach (string id in new[] { "highway-name-path", "highway-name-minor", "highway-name-major" })
            {
                SymbolStyle.StyleLayer layer = SymbolTestFixtures.FindSymbolLayer(id);
                Assert.IsNotNull(layer, $"liberty must ship a symbol layer '{id}'");

                LayoutProperties layout = layer.Layout;
                SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(14.0, null, out SymbolPlacement evaluated)
                    ? evaluated : SymbolPlacement.Point;

                AlignmentMode resolved = AlignmentResolution.ResolvePitch(
                    layout.TextPitchAlignment, layout.TextRotationAlignment, placement);
                Assert.AreEqual(AlignmentMode.Map, resolved,
                    $"{id}: text-pitch-alignment absent (auto), text-rotation-alignment EXPLICIT map " +
                    "-> pitch resolves map via the explicit-rotation pass-through");
            }
        }

        [Test]
        public void ResolvePitch_Liberty_RoadOneWayArrow_IconResolvesMap()
        {
            // road_one_way_arrow / road_one_way_arrow_opposite: symbol-placement:line, BOTH icon-rotation-alignment
            // and icon-pitch-alignment absent (auto/auto) -- the full pitch-auto -> rotation-auto -> placement
            // chain, the single highest-value row in the stage.
            foreach (string id in new[] { "road_one_way_arrow", "road_one_way_arrow_opposite" })
            {
                SymbolStyle.StyleLayer layer = SymbolTestFixtures.FindSymbolLayer(id);
                Assert.IsNotNull(layer, $"liberty must ship a symbol layer '{id}'");

                LayoutProperties layout = layer.Layout;
                SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(14.0, null, out SymbolPlacement evaluated)
                    ? evaluated : SymbolPlacement.Point;

                AlignmentMode resolved = AlignmentResolution.ResolvePitch(
                    layout.IconPitchAlignment, layout.IconRotationAlignment, placement);
                Assert.AreEqual(AlignmentMode.Map, resolved,
                    $"{id}: icon-pitch-alignment and icon-rotation-alignment both absent (auto), placement line " +
                    "-> pitch resolves map via the full auto-auto chain");
            }
        }

        [Test]
        public void ResolvePitch_Liberty_HighwayShieldNonUs_IconResolvesViewport()
        {
            // Contrast row: highway-shield-non-us sets icon-rotation-alignment:viewport EXPLICITLY, so icon
            // pitch resolves viewport regardless of placement -- without this row, T5 could pass on an
            // implementation that returns Map unconditionally. symbol-placement is a step expression
            // (point below z11, line at/above); zoom 12 (>= 11) picked so placement is 'line', proving the
            // explicit rotation value still wins over the line-placement auto-auto default.
            SymbolStyle.StyleLayer layer = SymbolTestFixtures.FindSymbolLayer("highway-shield-non-us");
            Assert.IsNotNull(layer, "liberty must ship a symbol layer 'highway-shield-non-us'");

            LayoutProperties layout = layer.Layout;
            SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(12.0, null, out SymbolPlacement evaluated)
                ? evaluated : SymbolPlacement.Point;
            Assert.AreEqual(SymbolPlacement.Line, placement, "z12 is at/above the step's z11 boundary -> line");

            AlignmentMode resolved = AlignmentResolution.ResolvePitch(
                layout.IconPitchAlignment, layout.IconRotationAlignment, placement);
            Assert.AreEqual(AlignmentMode.Viewport, resolved,
                "icon-rotation-alignment:viewport is explicit -> icon pitch resolves viewport even under line placement");
        }
    }
}
