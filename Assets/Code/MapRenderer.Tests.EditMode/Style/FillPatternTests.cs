// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

// Namespace-collision guard: a top-level `using Unity.Mathematics;` + bare `int2` is REQUIRED. Written
// inline as `Unity.Mathematics.int2` inside namespace `MapRenderer.Tests`, the leading `Unity` segment binds
// to `MapRenderer.Unity` (which exists), not the global `Unity` root — CS0234.
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text.Sprites;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// P2 — <see cref="Fill.FillPattern"/>: resolving a <c>fill-pattern</c> sprite name against a sheet into
    /// the rect + repeat count the fill shader samples with.
    ///
    /// <para>The load-bearing case is the NEGATIVE one. A <c>fill-pattern</c> layer characteristically
    /// declares no <c>fill-color</c>, so it inherits the spec's opaque-black default; if an unresolvable
    /// pattern fell back to that colour instead of reporting "unresolved", the layer paints solid black.
    /// That is exactly the defect this stage fixes (Liberty's <c>road_area_pattern</c> plazas and
    /// <c>landcover_wetland</c>) — see docs/fill-parity-design.md §1.</para>
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class FillPatternTests
    {
        // A 64×64 sheet holding one 32×32 @1x sprite and one 32×32 @2x sprite (16 css px logical).
        private const string SheetJson = @"{
            ""plaza"":    { ""x"": 0,  ""y"": 0,  ""width"": 32, ""height"": 32, ""pixelRatio"": 1 },
            ""plaza2x"":  { ""x"": 32, ""y"": 0,  ""width"": 32, ""height"": 32, ""pixelRatio"": 2 },
            ""tall"":     { ""x"": 0,  ""y"": 32, ""width"": 16, ""height"": 32, ""pixelRatio"": 1 },
            ""degenerate"":{ ""x"": 0, ""y"": 0,  ""width"": 0,  ""height"": 0,  ""pixelRatio"": 1 }
        }";

        private static SpriteAtlasView Sheet() => new SpriteAtlasView
        {
            Index = SpriteIndex.Parse(SheetJson),
            Size  = new int2(64, 64),
        };

        // ── The fix: every "cannot resolve" path reports unresolved, never a colour fallback ──────

        [Test]
        public void Unresolved_WhenNoPatternDeclared()
        {
            Assert.IsFalse(Fill.FillPattern.TryResolve(null, Sheet(), out var r),
                "a layer with no fill-pattern must not resolve a pattern");
            Assert.IsFalse(r.IsResolved);
            Assert.AreEqual(0.0, r.Rect.z, "unresolved must be a ZERO-AREA rect — the shader's clip signal");
            Assert.AreEqual(0.0, r.Rect.w);
        }

        [Test]
        public void Unresolved_WhenSheetHasNotArrivedYet()
        {
            // The real steady state for the first frames of every style: the sheet is fetched async, so a
            // pattern layer's material is built before it exists.
            Assert.IsFalse(Fill.FillPattern.TryResolve("plaza", null, out var r),
                "a null atlas (sheet not fetched yet) must report unresolved, not fall back to fill-color");
            Assert.IsFalse(r.IsResolved);
            Assert.AreEqual(0.0, r.Rect.z);
        }

        [Test]
        public void Unresolved_WhenNameAbsentFromSheet()
        {
            Assert.IsFalse(Fill.FillPattern.TryResolve("no-such-sprite", Sheet(), out var r),
                "a name absent from the sheet must report unresolved (spec: the layer is not painted)");
            Assert.IsFalse(r.IsResolved);
            Assert.AreEqual(0.0, r.Rect.z);
        }

        [Test]
        public void Unresolved_WhenSpriteRectIsDegenerate()
        {
            Assert.IsFalse(Fill.FillPattern.TryResolve("degenerate", Sheet(), out var r),
                "a zero-area sprite is not drawable and must report unresolved");
            Assert.IsFalse(r.IsResolved);
        }

        // ── Resolution arithmetic ────────────────────────────────────────────────────────────────

        [Test]
        public void Resolved_RectIsTheSpriteSheetPixelRect()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza2x", Sheet(), out var r));
            Assert.IsTrue(r.IsResolved);
            Assert.AreEqual(32.0, r.Rect.x, "rect.xy is the sprite's top-left in SHEET pixels");
            Assert.AreEqual(0.0,  r.Rect.y);
            Assert.AreEqual(32.0, r.Rect.z, "rect.zw is the sprite's size in SHEET pixels (not logical px)");
            Assert.AreEqual(32.0, r.Rect.w);
        }

        [Test]
        public void Resolved_CarriesTheSpritesAuthoredPixelSize()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));
            Assert.AreEqual(32.0, r.LogicalSizePixels.x, 1e-9, "32 sheet px at pixelRatio 1 = 32 css px");
            Assert.AreEqual(32.0, r.LogicalSizePixels.y, 1e-9);
        }

        [Test]
        public void Resolved_PixelRatioHalvesTheAuthoredSize()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza2x", Sheet(), out var r));
            Assert.AreEqual(16.0, r.LogicalSizePixels.x, 1e-9,
                "32 sheet px at pixelRatio 2 is a 16 css px sprite — that divisor is what keeps an @2x sheet " +
                "from drawing its patterns at double size");
        }

        [Test]
        public void Resolved_AspectIsHeightOverWidth()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("tall", Sheet(), out var r)); // 16×32
            Assert.AreEqual(2.0, r.Aspect, 1e-9, "a 16×32 sprite is twice as tall as it is wide");
        }

        [Test]
        public void Resolved_MalformedZeroPixelRatioDoesNotProduceInfiniteRepeats()
        {
            var sheet = new SpriteAtlasView
            {
                Index = SpriteIndex.Parse(
                    @"{ ""bad"": { ""x"":0, ""y"":0, ""width"":32, ""height"":32, ""pixelRatio"": 0 } }"),
                Size = new int2(64, 64),
            };
            Assert.IsTrue(Fill.FillPattern.TryResolve("bad", sheet, out var r),
                "a malformed pixelRatio must degrade to 1, not reject an otherwise-valid sprite");
            Assert.AreEqual(32.0, r.LogicalSizePixels.x, 1e-9);
        }

        // ── Sizing: ScreenRelative (spec) vs WorldAbsolute (engine extension) ────────────────────
        //
        // These now measure repetitions per WORLD UNIT, not per tile. The tile frame was abandoned because a
        // tile's own zoom is not derivable from the display zoom: OpenFreeMap's source stops at z14 while the
        // camera zooms to 18+, so the same tiles are stretched across five display zooms, and a mixed-zoom
        // cover (ScreenSpaceLod, the default) mixes levels within one frame. Both made the old tile-relative
        // correction wrong by up to 16×.

        [Test]
        public void ScreenRelative_PeriodIsTheSpriteAtItsAuthoredPixelSize()
        {
            // The spec's meaning: the sprite occupies its own pixel size on screen. So one repetition spans
            // logicalPixels × metresPerPixel of world — no tile term anywhere.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));
            const double zoom = 14.0;

            double2 repeats = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, zoom, 0.0);

            double expectedPeriod = 32.0 * WebMercator.GroundResolution(zoom); // 32 css px sprite
            Assert.AreEqual(1.0 / expectedPeriod, repeats.x, 1e-12);
        }

        [Test]
        public void ScreenRelative_IsContinuousInZoom_NoSteppingAndNoPerLevelSnap()
        {
            // The artefact this replaced: repeats used to be rounded to whole numbers per tile, so a
            // continuous zoom produced ~16 discrete snaps per zoom level — invisible in a screenshot, obvious
            // while zooming. Period is now a smooth function of zoom, so consecutive samples must differ
            // smoothly and never repeat a value (which is what a stair-step looks like numerically).
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double previous = -1.0;
            for (int step = 0; step <= 80; step++)
            {
                double zoom = 14.0 + step / 40.0;   // spans two whole zoom levels
                double value = Fill.FillPattern.RepeatsPerWorldUnit(
                    r, Fill.FillPatternSizing.ScreenRelative, zoom, 0.0).x;

                Assert.Greater(value, previous,
                    $"repeats-per-world-unit must increase STRICTLY with zoom (at {zoom}); an equal " +
                    "consecutive value is a stair-step, which is exactly the snapping this replaced");
                if (previous > 0.0)
                    Assert.Less(value / previous, 1.1,
                        $"and it must step smoothly — a jump at {zoom} would be a visible pop while zooming");
                previous = value;
            }
        }

        [Test]
        public void ScreenRelative_CrossingAZoomLevelBoundaryIsSmooth()
        {
            // The old form reset at each integer zoom (the cover switching level). Nothing resets now, so
            // straddling z15 must be indistinguishable from any other neighbouring pair.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double below = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 14.999, 0.0).x;
            double above = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 15.001, 0.0).x;

            Assert.AreEqual(1.0, above / below, 0.01,
                "crossing an integer zoom must not jump — the tile-relative form snapped here, which is what " +
                "made zooming look broken");
        }

        [Test]
        public void ScreenRelative_IsIndependentOfTheTilesOwnZoom_SoOverzoomIsCorrect()
        {
            // THE overzoom tooth. Past the source's maxzoom the same tiles are drawn at every display zoom, so
            // any term derived from the tile would be frozen while the camera keeps going. Repeats depend on
            // the DISPLAY zoom only, so the apparent size stays correct: two display zooms one level apart
            // must differ by exactly 2× regardless of which tiles are underneath.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double atZ14 = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;
            double atZ18 = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 18.0, 0.0).x;

            Assert.AreEqual(16.0, atZ18 / atZ14, 1e-9,
                "four zoom levels in ⇒ 16× more repetitions per world unit, so the pattern holds its screen " +
                "size. The tile-relative form was off by exactly this factor past maxzoom — the reported " +
                "'pattern is too zoomed in' at z15-18 over a z14 source");
        }

        [Test]
        public void WorldAbsolute_IsIndependentOfZoomEntirely()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));
            const double period = 50.0;

            foreach (double zoom in new[] { 0.0, 7.5, 14.0, 18.25, 22.0 })
                Assert.AreEqual(1.0 / period,
                    Fill.FillPattern.RepeatsPerWorldUnit(
                        r, Fill.FillPatternSizing.WorldAbsolute, zoom, period).x, 1e-12,
                    $"a world-sized pattern must not vary with zoom at all (checked {zoom})");
        }

        [Test]
        public void WorldAbsolute_PeriodOfOne_AdvancesExactlyOneRepetitionPerWorldUnit()
        {
            // The defining case: pattern coordinate IS world distance. A fill covering one world unit covers
            // exactly one repetition — which is what makes "period" the honest name for the parameter.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            Assert.AreEqual(1.0,
                Fill.FillPattern.RepeatsPerWorldUnit(
                    r, Fill.FillPatternSizing.WorldAbsolute, 14.0, 1.0).x, 1e-12);
        }

        [Test]
        public void WorldAbsolute_PreservesSpriteAspect_SoANonSquareSpriteIsNotStretched()
        {
            // "tall" is 16×32, so at a 50-unit width period it must be 100 units tall — i.e. half as many
            // repetitions per world unit vertically.
            Assert.IsTrue(Fill.FillPattern.TryResolve("tall", Sheet(), out var r));
            double2 repeats = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.WorldAbsolute, 14.0, 50.0);

            Assert.AreEqual(repeats.x / 2.0, repeats.y, 1e-12,
                "the period applies along the sprite's WIDTH; the height follows the sprite's aspect, so a " +
                "16×32 sprite repeats half as often vertically instead of being squashed square");
        }

        [Test]
        public void PixelRatio_HalvesTheAuthoredSize_SoAn2xSpriteRepeatsTwiceAsOften()
        {
            // @2x sheets store a 16 css px sprite as 32 sheet px. Without the divisor an @2x sheet would draw
            // its patterns at double size.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza",   Sheet(), out var at1x));
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza2x", Sheet(), out var at2x));

            double r1 = Fill.FillPattern.RepeatsPerWorldUnit(
                at1x, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;
            double r2 = Fill.FillPattern.RepeatsPerWorldUnit(
                at2x, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;

            Assert.AreEqual(2.0, r2 / r1, 1e-9);
            Assert.AreEqual(at1x.Rect.z, at2x.Rect.z,
                "pixelRatio must NOT change the sheet-pixel rect — only the authored size");
        }

        [Test]
        public void WorldAbsolute_WithNoPeriod_FallsBackToScreenRelativeRatherThanDividingByZero()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double unset = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.WorldAbsolute, 14.0, 0.0).x;
            double screen = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;

            Assert.AreEqual(screen, unset, 1e-12,
                "WorldAbsolute with no period must degrade to the spec behaviour, not to infinity");
            Assert.IsFalse(double.IsInfinity(unset) || double.IsNaN(unset));
        }

        [Test]
        public void Unresolved_YieldsZeroRepeats_InEitherMode()
        {
            var unresolved = Fill.FillPattern.Resolution.Unresolved;
            foreach (var sizing in new[] { Fill.FillPatternSizing.ScreenRelative,
                                           Fill.FillPatternSizing.WorldAbsolute })
                Assert.AreEqual(0.0,
                    Fill.FillPattern.RepeatsPerWorldUnit(unresolved, sizing, 14.0, 50.0).x, 1e-12,
                    $"an unresolved pattern has no period to invert ({sizing})");
        }

        [Test]
        public void ScreenRelativeIsTheDefaultMode_BecauseThatIsWhatAMapLibreStyleMeans()
        {
            Assert.AreEqual(0, (int)Fill.FillPatternSizing.ScreenRelative,
                "ScreenRelative must be the zero/default enum value — a stock MapLibre style has no way to " +
                "ask for world-absolute sizing, so the default must be the spec behaviour.");
        }

        // ── The sizing mode is parsed from the style ─────────────────────────────────────────────

        [Test]
        public void Paint_NoSizeKey_IsScreenRelative_TheSpecBehaviour()
        {
            var paint = new Fill.PaintProperties(JsonParser.Parse(@"{""fill-pattern"":""plaza""}"));

            Assert.AreEqual(Fill.FillPatternSizing.ScreenRelative, paint.PatternSizing,
                "every stock MapLibre style must mean screen-relative — it has no way to ask for anything else");
            Assert.AreEqual(0.0, paint.PatternWorldPeriodMetres, 1e-9);
        }

        [Test]
        public void Paint_ExtensionSizeKey_SwitchesToWorldAbsolute()
        {
            var paint = new Fill.PaintProperties(JsonParser.Parse(
                @"{""fill-pattern"":""plaza"", ""x-fill-pattern-metres"": 25.5}"));

            Assert.AreEqual(Fill.FillPatternSizing.WorldAbsolute, paint.PatternSizing,
                "a ground size is only meaningful under world-absolute sizing, so supplying one selects it — " +
                "the two cannot disagree because they are one key");
            Assert.AreEqual(25.5, paint.PatternWorldPeriodMetres, 1e-9);
        }

        [Test]
        public void Paint_UnusableSizeValue_FallsBackToSpecBehaviour()
        {
            // Forward-compat posture, matching the rest of this parser: an unreadable EXTENSION must never
            // cost the layer its rendering. Zero and negative are unusable as a divisor; a string is garbage.
            foreach (string value in new[] { "0", "-4", "\"big\"" })
            {
                var paint = new Fill.PaintProperties(JsonParser.Parse(
                    $@"{{""fill-pattern"":""plaza"", ""x-fill-pattern-metres"": {value}}}"));

                Assert.AreEqual(Fill.FillPatternSizing.ScreenRelative, paint.PatternSizing,
                    $"x-fill-pattern-metres={value} is unusable and must degrade to screen-relative, not throw");
                Assert.AreEqual(0.0, paint.PatternWorldPeriodMetres, 1e-9);
            }
        }

        [Test]
        public void Paint_ExtensionKeyCountsAsPresent_SoTheLayerIsNotInert()
        {
            // IsInertFallback drives "this layer declared nothing" short-circuits; an extension-only paint
            // block HAS declared something, so treating it as inert would silently drop the layer.
            var paint = new Fill.PaintProperties(JsonParser.Parse(@"{""x-fill-pattern-metres"": 10}"));
            Assert.IsFalse(paint.IsInertFallback);
        }

        // ── The paint side of the same defect ────────────────────────────────────────────────────

        [Test]
        public void PatternLayerWithoutFillColor_StillCarriesTheOpaqueBlackDefault()
        {
            // Pins WHY the shader must clip rather than paint: this is Liberty's road_area_pattern verbatim,
            // and its Color evaluates to opaque black. Nothing here is wrong — the spec default IS black —
            // which is precisely why "unresolved" cannot be allowed to fall through to the colour path.
            var paint = new Fill.PaintProperties(JsonParser.Parse(@"{""fill-pattern"":""pedestrian_polygon""}"));

            Assert.AreEqual("pedestrian_polygon", paint.PatternName);
            var color = paint.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, color.R, 1e-9, "fill-color default is opaque black …");
            Assert.AreEqual(0.0, color.G, 1e-9);
            Assert.AreEqual(0.0, color.B, 1e-9);
            Assert.AreEqual(1.0, color.A, 1e-9, "… fully opaque — hence solid black regions, not faint ones");
        }
    }
}
