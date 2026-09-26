// Fill-pattern GPU/visual tests. The file split follows two CS0104 collisions (`CameraProperties`, bare
// `Object`); this file holds the UnityEngine.Rendering (or neutral) importers that use bare Object.
//
// Contents:
//   FillPatternSnapshotTests            — the black-region fix, at the pixel level.
//   FillPatternPeriodDiagnostic         — Measures what a pattern fill actually puts on screen, for a single tile under a top-down camera.
//   FillPatternThroughSpriteSheetTests  — U4 — a fill-pattern resolved through the REAL SpriteSheet never samples outside its own content rect.
//   LitFillSnapshotTests                — acceptance tests — Lit material foundation for fills.
//   FillOutwardBandProbeTests           — Unity EditMode only — the OUTWARD-BAND mechanism probe.
//   GlobeFillBandRenderTests            — The fill boundary band, observed in rendered pixels on the curved (globe) arm.
//   WorldFillSnapshotTests              — Headless visual snapshot tests: render the world-fill map to an off-screen RenderTexture, write PNGs to Logs/snapshots/, and run a tolerant coverage assertion.
//   FillTranslateSnapshotTests          — fill-translate as a real SCREEN-PIXEL offset, and fill-translate-anchor as a real branch.

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;
using FillShaderProps = MapRenderer.Unity.Rendering.ShaderProperties.Fill;
using FillStyle       = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.Text;
using System.IO;
using UnityEngine.Rendering;
using UnityEditor;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Core.Geo;
using System.Collections.Generic;
using MapRenderer.Jobs.Fill;
using MapRenderer.Unity.Rendering.Materials;

namespace MapRenderer.Tests.Visual
{
    // Keep `using Unity.Mathematics;` + bare `int2`: inline, `Unity.Mathematics.int2` binds `Unity` to
    // `MapRenderer.Unity` (reachable from `MapRenderer.Tests.Visual`), not the global root — CS0234.

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPatternSnapshotTests — the black-region fix
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The black-region fix and the fill-color tint, at the pixel level. A <c>fill-pattern</c> layer usually
    /// declares no <c>fill-color</c>; its absent colour binds white, and an explicit one tints the sprite
    /// (docs/fill-parity-design.md). <see cref="UnresolvedPattern_PaintsNothing"/> asserts the BACKGROUND
    /// fraction, so a shader that ignores the pattern cannot pass it. <c>FillPatternTests</c> pins the resolve
    /// arithmetic engine-free.
    /// </summary>
    [TestFixture]
    public class FillPatternSnapshotTests : BaseTestFixture
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

        // An explicit opaque-black fill-color: on an unresolved pattern it must still paint nothing, and a
        // renderer that never consults the pattern would paint these regions solid black.
        private const string OpaqueBlack = "[\"rgba\",0,0,0,1]";

        /// <summary>Any zoom works — these teeth assert colour, not size — but it must be a real one so the
        /// derived period is sane.</summary>
        private const double DiagnosticZoom = 14.0;

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("FillPatternSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSz;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        /// <summary>A 2×2 sheet of one solid, saturated colour, so "did the sprite reach the screen" is
        /// answerable from the pixel colour alone — no fixture-art dependency, no filtering ambiguity.</summary>
        private static Texture2D BuildSolidSheet(Color color)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            tex.SetPixels(new[] { color, color, color, color });
            tex.Apply(updateMipmaps: false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode   = TextureWrapMode.Clamp;
            return tex;
        }

        private static SpriteAtlasView SheetView(int size) => new SpriteAtlasView
        {
            Index = SpriteIndex.Parse(
                $@"{{ ""solid"": {{ ""x"":0, ""y"":0, ""width"":{size}, ""height"":{size}, ""pixelRatio"":1 }} }}"),
            Size = new int2(size, size),
        };

        // ── THE tooth: a pattern layer that cannot resolve paints NOTHING (was: solid black) ─────

        [Test]
        public void UnresolvedPattern_PaintsNothing()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillColorExpression: OpaqueBlack);
            Track(mapGo);
            var (camGo, camera) = BuildCamera();
            Track(camGo);
            // Baseline: the SAME geometry with no pattern. It proves the geometry is on screen, so "no black
            // pixels" cannot pass on an empty render.
            mat.SetFloat(FillShaderProps.PropertyId.FillPattern, 0f);
            using var solid = new SnapshotRenderer(SnapW, SnapH);
            solid.Render(camera);
            solid.WritePng("fill-pattern-black-baseline.png");

            var solidVerdict = SnapshotCoverage.Analyse(solid.Pixels, Bg32);
            Assert.Greater(solidVerdict.FilledFraction, 0.02f,
                "precondition: the black fill must actually cover the frame, or the negative " +
                "assertion below proves nothing.");

            // Now declare it a pattern layer whose sprite did not resolve (zero-area rect — the state
            // every pattern layer starts in, since the sheet is fetched asynchronously).
            mat.SetFloat (FillShaderProps.PropertyId.FillPattern,  1f);
            mat.SetVector(FillShaderProps.PropertyId.PatternRect,  Vector4.zero);

            using var unresolved = new SnapshotRenderer(SnapW, SnapH);
            unresolved.Render(camera);
            unresolved.WritePng("fill-pattern-unresolved.png");

            var verdict = SnapshotCoverage.Analyse(unresolved.Pixels, Bg32);
            Debug.Log($"[FillPatternSnapshotTests] solid filled={solidVerdict.FilledFraction:P2}, " +
                      $"unresolved filled={verdict.FilledFraction:P2}");
            Assert.Greater(verdict.BackgroundFraction, 0.99f,
                "An unresolved fill-pattern layer must paint NOTHING — the background must be intact. " +
                $"Got {verdict.BackgroundFraction:P2} background / {verdict.FilledFraction:P2} filled. " +
                "A failure here means the layer is painting fill-color's opaque-black default again " +
                "(the original black-regions defect).");
        }

        // ── The positive half: a resolved pattern with no fill-color paints the untinted SHEET ───

        /// <summary>A pattern layer that sets no fill-color paints the sprite untinted, never black: its absent
        /// fill-color binds white, and the sprite is multiplied by it.</summary>
        [Test]
        public void ResolvedPattern_WithoutFillColor_PaintsTheUntintedSprite()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillPattern: "solid", omitFillColor: true);
            Track(mapGo);
            var (camGo, camera) = BuildCamera();
            Track(camGo);
            Texture2D sheet = Track(BuildSolidSheet(Color.green));
            Assert.IsTrue(FillStyle.FillPattern.TryResolve(
                "solid", SheetView(2), out var pattern), "fixture sheet must resolve");

            mat.SetFloat  (FillShaderProps.PropertyId.FillPattern,  1f);
            mat.SetTexture(FillShaderProps.TexturePropertyId.PatternMap,   sheet);
            mat.SetVector (FillShaderProps.PropertyId.PatternRect,
                new Vector4((float)pattern.Rect.x, (float)pattern.Rect.y,
                            (float)pattern.Rect.z, (float)pattern.Rect.w));
            // Repeats per WORLD UNIT now — the mesh's stream 1 carries world metres from the tile
            // origin, not a 0..1 tile fraction, so no tile term enters this.
            double2 repeats = FillStyle.FillPattern.RepeatsPerWorldUnit(
                pattern, FillStyle.FillPatternSizing.ScreenRelative, DiagnosticZoom, 0.0);
            mat.SetVector (FillShaderProps.PropertyId.PatternScale,
                new Vector4((float)repeats.x, (float)repeats.y, 0f, 0f));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng("fill-pattern-resolved.png");

            var verdict = SnapshotCoverage.Analyse(snap.Pixels, Bg32);
            Assert.Greater(verdict.FilledFraction, 0.02f,
                "a RESOLVED pattern must paint — this is the other side of the clip.");

            // The sprite is green and the layer sets no fill-color. Green dominance proves the sprite reached the
            // screen untinted: a black default would leave every channel near zero. (Lit shading scales
            // magnitude, not hue order.)
            double meanG = 0, meanR = 0, meanB = 0;
            int n = 0;
            for (int i = 0; i < SnapW * SnapH; i++)
            {
                Color32 c = snap.Pixels.Pixels[i];
                int dr = c.r - Bg32.r, dg = c.g - Bg32.g, db = c.b - Bg32.b;
                if (Mathf.Abs(dr) + Mathf.Abs(dg) + Mathf.Abs(db) <= SnapshotCoverage.Tolerance) continue;
                meanR += c.r; meanG += c.g; meanB += c.b;
                n++;
            }
            Assert.Greater(n, 0, "expected non-background pixels to average");
            meanR /= n; meanG /= n; meanB /= n;
            Debug.Log($"[FillPatternSnapshotTests] resolved mean RGB = ({meanR:F1}, {meanG:F1}, {meanB:F1})");

            Assert.Greater(meanG, meanR + 10.0,
                $"the green sprite must show untinted (mean G={meanG:F1} vs R={meanR:F1}) — if they match, the " +
                "sprite is tinted black by a spec-default fill-color, or the pattern is ignored.");
            Assert.Greater(meanG, meanB + 10.0,
                $"mean G={meanG:F1} must exceed mean B={meanB:F1} for a green sprite.");
        }

        /// <summary>A pattern layer that sets fill-color multiplies the sprite by it: a white sprite under a red
        /// fill-color paints red.</summary>
        [Test]
        public void ResolvedPattern_WithFillColor_TintsTheSprite()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillColorExpression: "[\"rgba\",255,0,0,1]",
                                                           fillPattern: "solid");
            Track(mapGo);
            var (camGo, camera) = BuildCamera();
            Track(camGo);
            ResolveSolidSheet(mat, Track(BuildSolidSheet(Color.white)));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng("fill-pattern-tinted.png");

            double3 mean = MeanFilledRgb(snap.Pixels);
            Assert.Greater(mean.x, mean.y + 10.0,
                $"a white sprite under a red fill-color must paint red (mean RGB {mean}); equal channels mean the " +
                "fill-color is ignored on the pattern layer.");
            Assert.Greater(mean.x, mean.z + 10.0, $"mean R must exceed mean B (mean RGB {mean}).");
        }

        /// <summary>A data-driven fill-color tints a pattern too: it bakes into the vertex colour, which the
        /// pattern branch multiplies in. The expression reads a feature, and yields red for every feature.</summary>
        [Test]
        public void ResolvedPattern_WithDataDrivenFillColor_TintsTheSprite()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"case\",[\"has\",\"no-such-property\"],\"#0000ff\",\"#ff0000\"]",
                fillPattern: "solid");
            Track(mapGo);
            var (camGo, camera) = BuildCamera();
            Track(camGo);
            ResolveSolidSheet(mat, Track(BuildSolidSheet(Color.white)));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng("fill-pattern-tinted-data-driven.png");

            double3 mean = MeanFilledRgb(snap.Pixels);
            Assert.Greater(mean.x, mean.y + 10.0,
                $"a white sprite under a data-driven red fill-color must paint red (mean RGB {mean}); equal " +
                "channels mean the baked vertex colour does not reach the pattern branch.");
            Assert.Greater(mean.x, mean.z + 10.0, $"mean R must exceed mean B (mean RGB {mean}).");
        }

        /// <summary>The tint rule reaches only pattern layers: a solid fill that sets no fill-color keeps the
        /// spec default, opaque black, not the pattern layer's white.</summary>
        [Test]
        public void SolidFill_WithoutFillColor_StaysSpecBlack()
        {
            var (mapGo, _) = FillSceneHelper.BuildFillGo(omitFillColor: true);
            Track(mapGo);
            var (camGo, camera) = BuildCamera();
            Track(camGo);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng("fill-solid-default-black.png");

            Assert.Greater(SnapshotCoverage.Analyse(snap.Pixels, Bg32).FilledFraction, 0.02f,
                "precondition: the fill must cover part of the frame.");
            double3 mean = MeanFilledRgb(snap.Pixels);
            // Lit black still picks up a little ambient and specular (a blue channel near 25); lit white is
            // near 150.
            Assert.Less(math.cmax(mean), 60.0,
                $"a solid fill with no fill-color must paint the spec's black (mean RGB {mean}).");
        }

        /// <summary>Resolves the 2×2 "solid" sprite of <paramref name="sheet"/> onto the material.</summary>
        private static void ResolveSolidSheet(Material mat, Texture2D sheet)
        {
            Assert.IsTrue(FillStyle.FillPattern.TryResolve("solid", SheetView(2), out var pattern),
                "fixture sheet must resolve");
            mat.SetTexture(FillShaderProps.TexturePropertyId.PatternMap, sheet);
            mat.SetVector (FillShaderProps.PropertyId.PatternRect,
                new Vector4((float)pattern.Rect.x, (float)pattern.Rect.y,
                            (float)pattern.Rect.z, (float)pattern.Rect.w));
            double2 repeats = FillStyle.FillPattern.RepeatsPerWorldUnit(
                pattern, FillStyle.FillPatternSizing.ScreenRelative, DiagnosticZoom, 0.0);
            mat.SetVector (FillShaderProps.PropertyId.PatternScale,
                new Vector4((float)repeats.x, (float)repeats.y, 0f, 0f));
        }

        /// <summary>Mean RGB (0-255) of the pixels that differ from the background.</summary>
        private static double3 MeanFilledRgb(Frame frame)
        {
            double3 sum = 0.0;
            int n = 0;
            for (int i = 0; i < frame.Pixels.Length; i++)
            {
                Color32 c = frame.Pixels[i];
                if (math.abs(c.r - Bg32.r) + math.abs(c.g - Bg32.g) + math.abs(c.b - Bg32.b) <= SnapshotCoverage.Tolerance)
                    continue;
                sum += new double3(c.r, c.g, c.b);
                n++;
            }
            Assert.Greater(n, 0, "expected non-background pixels to average");
            return sum / n;
        }

        // ── The sprite's ALPHA must reach the framebuffer, not just its RGB ──────────────────────

        [Test]
        public void PatternWithTransparentTexels_ShowsBackgroundThrough()
        {
            // A pattern sprite is usually an alpha-masked overlay, so discarding its alpha renders a solid block.
            // An opaque surface fails this: URP's OutputAlpha() forces alpha to 1 without _SURFACE_TYPE_TRANSPARENT.
            var (mapGo, mat)    = FillSceneHelper.BuildFillGo(fillColorExpression: OpaqueBlack);
            Track(mapGo);
            var (camGo, camera) = BuildCamera();
            Track(camGo);
            Texture2D sheet = Track(BuildSolidSheet(new Color(0f, 1f, 0f, 0f))); // green, FULLY transparent
            Assert.IsTrue(FillStyle.FillPattern.TryResolve("solid", SheetView(2), out var pattern));

            mat.SetFloat  (FillShaderProps.PropertyId.FillPattern,  1f);
            mat.SetTexture(FillShaderProps.TexturePropertyId.PatternMap, sheet);
            mat.SetVector (FillShaderProps.PropertyId.PatternRect,
                new Vector4((float)pattern.Rect.x, (float)pattern.Rect.y,
                            (float)pattern.Rect.z, (float)pattern.Rect.w));
            // Repeats per WORLD UNIT now — the mesh's stream 1 carries world metres from the tile
            // origin, not a 0..1 tile fraction, so no tile term enters this.
            double2 repeats = FillStyle.FillPattern.RepeatsPerWorldUnit(
                pattern, FillStyle.FillPatternSizing.ScreenRelative, DiagnosticZoom, 0.0);
            mat.SetVector (FillShaderProps.PropertyId.PatternScale,
                new Vector4((float)repeats.x, (float)repeats.y, 0f, 0f));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng("fill-pattern-alpha-masked.png");

            var verdict = SnapshotCoverage.Analyse(snap.Pixels, Bg32);
            Debug.Log($"[FillPatternSnapshotTests] fully-transparent sprite → filled " +
                      $"{verdict.FilledFraction:P2}");

            Assert.Greater(verdict.BackgroundFraction, 0.99f,
                "a fully TRANSPARENT pattern sprite must leave the background intact — the sprite's " +
                $"alpha must reach the blend unit. Got {verdict.FilledFraction:P2} filled, which means " +
                "alpha is being discarded and every pattern renders as a solid block.");
        }

        // ── The claim, made falsifiable: resolving late does NOT re-mesh ─────────────────────────

        [Test]
        public void ResolvingTheSheetLate_DoesNotRebuildTheMesh()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillColorExpression: OpaqueBlack);
            Track(mapGo);
            Texture2D sheet = Track(BuildSolidSheet(Color.green));
            var filter = mapGo.GetComponent<MeshFilter>();
            Mesh before      = filter.sharedMesh;
            int  vertsBefore = before.vertexCount;

            mat.SetFloat(FillShaderProps.PropertyId.FillPattern, 1f);
            mat.SetVector(FillShaderProps.PropertyId.PatternRect, Vector4.zero); // unresolved …

            Assert.IsTrue(FillStyle.FillPattern.TryResolve(
                "solid", SheetView(2), out var pattern));
            mat.SetTexture(FillShaderProps.TexturePropertyId.PatternMap, sheet);        // … then the sheet lands
            mat.SetVector (FillShaderProps.PropertyId.PatternRect,
                new Vector4((float)pattern.Rect.x, (float)pattern.Rect.y,
                            (float)pattern.Rect.z, (float)pattern.Rect.w));

            // Late resolve is cheap because the mesh carries world-unit pattern coordinates in stream 1. If this
            // fails, docs/fill-parity-design.md's "pure material-uniform change" is void and resolve needs a rebuild.
            Assert.AreSame(before, filter.sharedMesh,
                "resolving a pattern must not replace the Mesh instance (no re-mesh).");
            Assert.AreEqual(vertsBefore, filter.sharedMesh.vertexCount,
                "resolving a pattern must not change vertex count (no re-mesh).");
            Assert.Greater(before.uv.Length, 0,
                "the fill mesh must carry TEXCOORD0 — the pattern space the shader tiles in.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPatternPeriodDiagnostic — Measures what a pattern fill actually puts on screen
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Measures what a pattern fill puts on screen, for a single tile under a top-down camera: does the pattern
    /// TILE, or is one sprite stretched across the fill because <c>_PatternScale</c> never reached the shader?
    /// The sprite's two halves differ sharply, so transitions along a scanline count repetitions directly.
    /// </summary>
    [TestFixture]
    public class FillPatternPeriodDiagnostic : BaseTestFixture
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

        /// <summary>A 2×1 sprite: left texel red, right texel blue. One repetition therefore produces exactly
        /// one red→blue transition, so transitions along a row == repeats across that row.</summary>
        private static Texture2D BuildTwoToneSheet()
        {
            var tex = new Texture2D(2, 1, TextureFormat.RGBA32, mipChain: false);
            tex.SetPixels(new[] { Color.red, Color.blue });
            tex.Apply(updateMipmaps: false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode   = TextureWrapMode.Clamp;
            return tex;
        }

        private static SpriteAtlasView TwoToneView() => new SpriteAtlasView
        {
            Index = SpriteIndex.Parse(
                @"{ ""two"": { ""x"":0, ""y"":0, ""width"":2, ""height"":1, ""pixelRatio"":1 } }"),
            Size = new int2(2, 1),
        };

        [Test]
        public void Report_WhatThePatternActuallyRasterizes()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo();
            Track(mapGo);
            var camGo = Track(new GameObject("PatternDiagCam"));
            var camera = camGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 200f, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = 70f;   // frames the 100-unit fixture fill
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;

            Texture2D sheet = Track(BuildTwoToneSheet());
            Assert.IsTrue(FillStyle.FillPattern.TryResolve("two", TwoToneView(), out var pattern));

            // Ask for a known, modest number of repeats ACROSS THE FIXTURE TILE. _PatternScale is
            // repeats per WORLD UNIT, so convert: the fixture builds tile z0, which spans the world.
            const float repeatsAcrossTile = 6f;
            double tileSpanWorldUnits = MapRenderer.Core.Geo.EarthConstants.EquatorialCircumferenceMetres;
            float requestedRepeats = (float)(repeatsAcrossTile / tileSpanWorldUnits);
            mat.SetFloat  (FillShaderProps.PropertyId.FillPattern, 1f);
            mat.SetTexture(FillShaderProps.TexturePropertyId.PatternMap, sheet);
            mat.SetVector (FillShaderProps.PropertyId.PatternRect,
                new Vector4((float)pattern.Rect.x, (float)pattern.Rect.y,
                            (float)pattern.Rect.z, (float)pattern.Rect.w));
            mat.SetVector (FillShaderProps.PropertyId.PatternScale,
                new Vector4(requestedRepeats, requestedRepeats, 0f, 0f));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng("fill-pattern-period-diagnostic.png");

            // Walk the widest fully-covered scanline and classify each pixel red / blue / background.
            int bestRow = -1, bestCover = 0;
            for (int row = 0; row < SnapH; row++)
            {
                int cover = 0;
                for (int col = 0; col < SnapW; col++)
                    if (!IsBackground(snap.Pixels, row, col)) cover++;
                if (cover > bestCover) { bestCover = cover; bestRow = row; }
            }

            if (bestRow < 0 || bestCover < 32)
            {
                Debug.Log("[PatternDiag] nothing rendered — GPU/shader context unavailable");
                Assert.Fail("no geometry rendered");
            }

            int redRuns = 0, blueRuns = 0, transitions = 0;
            int prev = 0; // 0 = bg, 1 = red-ish, 2 = blue-ish
            for (int col = 0; col < SnapW; col++)
            {
                int cls = Classify(snap.Pixels, bestRow, col);
                if (cls != 0 && cls != prev && prev != 0) transitions++;
                if (cls == 1 && prev != 1) redRuns++;
                if (cls == 2 && prev != 2) blueRuns++;
                prev = cls;
            }

            Debug.Log($"[PatternDiag] row={bestRow} covered={bestCover}px  " +
                      $"redRuns={redRuns} blueRuns={blueRuns} transitions={transitions}  " +
                      $"repeatsAcrossTile={repeatsAcrossTile}");

            // The polygon is not convex, so a scanline crosses it in several spans and exact counts are not
            // assertable. Several runs of both colours mean it TILES; one means stretched, zero a flat colour.
            Assert.GreaterOrEqual(redRuns, 2,
                $"the pattern must repeat, not stretch — got {redRuns} red run(s). 1 means _PatternScale " +
                "is not reaching the shader; 0 means the sample is a flat colour.");
            Assert.GreaterOrEqual(blueRuns, 2,
                $"both halves of the sprite must recur — got {blueRuns} blue run(s).");
        }

        private static bool IsBackground(Frame frame, int row, int col)
        {
            Color32 px = frame[col, row];
            return Mathf.Abs(px.r - Bg32.r) + Mathf.Abs(px.g - Bg32.g) + Mathf.Abs(px.b - Bg32.b)
                   <= SnapshotCoverage.Tolerance;
        }

        private static int Classify(Frame frame, int row, int col)
        {
            if (IsBackground(frame, row, col)) return 0;
            Color32 px = frame[col, row];
            return px.r >= px.b ? 1 : 2; // more red than blue ⇒ red half, else blue half
        }
    }

    // Keep `using Unity.Mathematics;` + bare `int2`: inline, `Unity.Mathematics.int2` binds `Unity` to
    // `MapRenderer.Unity` (reachable from `MapRenderer.Tests.Visual`), not the global root — CS0234.

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPatternThroughSpriteSheetTests — U4 — a fill-pattern resolved through the REAL SpriteSheet never samples outside its own…
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>fill-pattern</c> resolved through the REAL <see cref="SpriteSheet"/> never samples outside its own
    /// content rect; <c>FillPatternSnapshotTests</c> never touches the padded repack. Patterns wrap with
    /// <c>frac()</c> INSIDE the <see cref="SpriteEntry"/> rect, so a rect meaning "the padded cell" makes every
    /// tiling seam sample the transparent border. The test watches for a NEIGHBOURING sprite's hue and for a
    /// TRANSPARENT texel; neither can occur while the rect is the content rect.
    /// </summary>
    [TestFixture]
    public class FillPatternThroughSpriteSheetTests : BaseTestFixture
    {
        private const int SnapW = 512;
        private const int SnapH = 512;
        private const float OrthoSz = 70f;
        private const float CamY = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

        private const double DiagnosticZoom = 14.0;

        private static readonly int BaseColorId = MapRenderer.Unity.Rendering.ShaderProperties.PropertyId.BaseColor;

        /// <summary>Repeats forced high enough that many tiling seams land inside the frame — one seam would
        /// be a weak probe, since a border tap only shows up AT a seam.</summary>
        private const double MinimumRepeats = 8.0;

        [Test]
        public void PatternThroughARepackedSheet_NeverSamplesTheBorderOrTheNeighbour()
        {
            // A real pattern layer with no fill-color: the parser's white default keeps the sprite untinted, so
            // its two hues stay measurable.
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillPattern: "A", omitFillColor: true);
            Track(mapGo);
            var (camGo, camera) = BuildCamera();
            Track(camGo);
            SpriteSheet sheet = null; // not a UnityEngine.Object — disposed below, outside the bag
            try
            {
                // Baseline first: the SAME geometry painted solid, i.e. full coverage. Without it "no transparent
                // pixels" would also pass on an empty render. It paints black, as the coverage reference always
                // has: a lighter edge counts more antialiased pixels as filled. The layer colour is then restored.
                Color layerColor = mat.GetColor(BaseColorId);
                mat.SetColor(BaseColorId, Color.black);
                mat.SetFloat(FillShaderProps.PropertyId.FillPattern, 0f);
                using var opaque = new SnapshotRenderer(SnapW, SnapH);
                opaque.Render(camera);
                var baseline = SnapshotCoverage.Analyse(opaque.Pixels, Bg32);
                Assert.Greater(baseline.FilledFraction, 0.02f,
                    "precondition: the fill must actually cover part of the frame.");

                // Two abutting sprites in maximally-separated hues, run through the REAL sheet path.
                var index = SpriteIndex.Parse(
                    "{\"A\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                    "\"B\":{\"x\":16,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}");
                sheet = new SpriteSheet(BuildTwoHueSheetPng(), index);

                Assert.IsTrue(FillStyle.FillPattern.TryResolve("A", sheet.View, out var pattern),
                    "the repacked index must still resolve the pattern name");

                double2 repeats = FillStyle.FillPattern.RepeatsPerWorldUnit(
                    pattern, FillStyle.FillPatternSizing.WorldAbsolute, DiagnosticZoom,
                    worldPeriodMetres: FillSceneHelperWorldSpan / MinimumRepeats);

                mat.SetColor(BaseColorId, layerColor);
                mat.SetFloat(FillShaderProps.PropertyId.FillPattern, 1f);
                mat.SetTexture(FillShaderProps.TexturePropertyId.PatternMap, sheet.Texture);
                mat.SetVector(FillShaderProps.PropertyId.PatternRect,
                    new Vector4((float)pattern.Rect.x, (float)pattern.Rect.y,
                                (float)pattern.Rect.z, (float)pattern.Rect.w));
                mat.SetVector(FillShaderProps.PropertyId.PatternScale,
                    new Vector4((float)repeats.x, (float)repeats.y, 0f, 0f));

                using var patterned = new SnapshotRenderer(SnapW, SnapH);
                patterned.Render(camera);
                patterned.WritePng("fill-pattern-through-sprite-sheet.png");

                var verdict = SnapshotCoverage.Analyse(patterned.Pixels, Bg32);

                int bled = 0;
                for (int i = 0; i < SnapW * SnapH; i++)
                {
                    Color32 c = patterned.Pixels.Pixels[i];
                    int dr = c.r - Bg32.r;
                    int dg = c.g - Bg32.g;
                    int db = c.b - Bg32.b;
                    if (Mathf.Abs(dr) + Mathf.Abs(dg) + Mathf.Abs(db) <= SnapshotCoverage.Tolerance)
                        continue; // background — not painted by the pattern at all
                    if (c.b > c.r + 24)
                        bled++;
                }

                Debug.Log($"[FillPatternThroughSpriteSheet] baseline filled={baseline.FilledFraction:P2}, " +
                          $"patterned filled={verdict.FilledFraction:P2}, blue-dominant pixels={bled}");

                Assert.Zero(bled,
                    $"{bled} painted pixels carry the NEIGHBOURING sprite's blue. The pattern wraps inside " +
                    $"the rect SpriteEntry reports, so this can only happen if that rect stopped being the " +
                    $"sprite's own content.");

                // A border tap would be alpha 0, and a fill layer's alpha reaches the blend unit, so it would
                // punch background-coloured holes at every tiling seam — a measurable coverage LOSS.
                Assert.GreaterOrEqual(verdict.FilledFraction, baseline.FilledFraction - 0.002f,
                    $"the patterned render covers {verdict.FilledFraction:P2} against the opaque baseline's " +
                    $"{baseline.FilledFraction:P2}. Lost coverage means the sampler reached the sprite's " +
                    $"TRANSPARENT border at the tiling seams.");
            }
            finally
            {
                sheet?.Dispose();
            }
        }

        /// <summary>The world span <c>FillSceneHelper</c> fits the fixture mesh into (its
        /// <c>DefaultViewSize</c>), the basis for a period that yields many seams on screen.</summary>
        private const double FillSceneHelperWorldSpan = 100.0;

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go = new GameObject("FillPatternThroughSpriteSheetCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic = true;
            camera.orthographicSize = OrthoSz;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BgColor;
            camera.enabled = false;
            return (go, camera);
        }

        /// <summary>A 32×16 sheet: left half opaque RED ("A"), right half opaque BLUE ("B") — abutting with
        /// a zero-pixel gap, exactly as a published sheet packs them.</summary>
        private static byte[] BuildTwoHueSheetPng()
        {
            const int width = 32, height = 16;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = x < 16
                        ? new Color32(255, 0, 0, 255)
                        : new Color32(0, 0, 255, 255);
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LitFillSnapshotTests — acceptance tests
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Lit material foundation for fills: PBR lighting is active, and a restyle needs no mesh rebuild.
    /// The shader teeth — Map/Fill compiles, and a normal map, a base map and metallic/smoothness each change
    /// the render — fail on a shallow or stripped shader.
    /// Ambient is Flat near-black so the directional term dominates. Camera: top-down ortho 512×512.
    /// </summary>
    [TestFixture]
    public class LitFillSnapshotTests : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            QualityLevel = 0,
            AmbientMode  = AmbientMode.Flat,
            AmbientLight = new Color(0.02f, 0.02f, 0.02f, 1f),
        };

        private const int   SnapW    = 512;
        private const int   SnapH    = 512;
        private const float OrthoSz  = 70f;

        // Background: distinctive dark slate (matches WorldFillSnapshotTests — not black).
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

        // Luminance delta threshold: absolute difference > 0.05 (5%) proves lighting is active.
        private const double LuminanceDeltaTol = 0.05;

        // ─── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>Top-down orthographic snapshot camera — see <see cref="VisualTestFixture.BuildCamera"/>.</summary>
        private (GameObject go, Camera camera) BuildCamera() => BuildCamera(new CameraSettings
        {
            ViewSize   = new float2(OrthoSz * 2f, OrthoSz * 2f),
            Background = BgColor,
        });

        /// <summary>
        /// Build the fill GO via FillSceneHelper (StyledFillTileBuilder-backed).
        /// Returns (mapGO, the live lit material on the MeshRenderer).
        /// </summary>
        private static (GameObject mapGo, Material liveMaterial) BuildFillGo()
            => FillSceneHelper.BuildFillGo();

        /// <summary>
        /// Add a directional light as a child of the given parent. Returns the Light component.
        /// </summary>
        private static Light AddDirectionalLight(GameObject parent, float intensity, Quaternion rotation)
        {
            var lightGo = new GameObject("DirLight");
            lightGo.transform.SetParent(parent.transform);
            lightGo.transform.rotation = rotation;
            var light = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = intensity;
            return light;
        }

        // ─── Test 1: PBR lighting is active ────────────────────────────────────────

        [Test]
        public void LitFill_LuminanceChangesWith_LightIntensity()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo();
            Track(mapGo);
            if (mat != null) mat.SetColor("_BaseColor", new Color(0.5f, 0.9f, 0.3f, 1f));

            Light light = AddDirectionalLight(mapGo, 2f, Quaternion.Euler(45f, 0f, 0f));

            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            VisualFrame frame1 = Capture(camera);
            SnapshotRenderer.WritePngFromRgba32(frame1.Pixels, "lit-fill-light-on.png");

            double lum1 = SnapshotCoverage.MeanLuminanceOfNonBackground(frame1.Pixels, Bg32);

            light.intensity = 0f;
            snap2.Render(camera);
            snap2.WritePng("lit-fill-light-off.png");

            double lum2 = SnapshotCoverage.MeanLuminanceOfNonBackground(snap2.Pixels, Bg32);

            Debug.Log($"[LitFillSnapshotTests] Lum(light on)={lum1:F4}, Lum(light off)={lum2:F4}, " +
                      $"delta={System.Math.Abs(lum1 - lum2):F4}");

            Assert.That(System.Math.Abs(lum1 - lum2), Is.GreaterThan(LuminanceDeltaTol),
                $"Luminance must change when directional light intensity changes " +
                $"(light-on={lum1:F4}, light-off={lum2:F4}, delta={System.Math.Abs(lum1 - lum2):F4}). " +
                $"Expected |delta| > {LuminanceDeltaTol}.");
        }

        // ─── Test 2: Restyle (SetColor) with no mesh rebuild ───────────────────────

        [Test]
        public void LitFill_RestyleColor_NoMeshRebuild()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo();
            Track(mapGo);

            AddDirectionalLight(mapGo, 1.5f, Quaternion.Euler(50f, 20f, 0f));

            var meshFilter    = mapGo.GetComponent<MeshFilter>();
            var meshBefore    = meshFilter.sharedMesh;

            if (mat != null) mat.SetColor("_BaseColor", new Color(0.2f, 0.8f, 0.2f, 1f));
            VisualFrame frame1 = Capture(camera);
            SnapshotRenderer.WritePngFromRgba32(frame1.Pixels, "lit-fill-color-green.png");

            // ── No-rebuild proof (CPU — always runs) ──
            if (mat != null) mat.SetColor("_BaseColor", new Color(0.9f, 0.1f, 0.1f, 1f));

            var meshAfter = meshFilter.sharedMesh;
            Assert.AreSame(meshBefore, meshAfter,
                "sharedMesh reference must be the SAME object before and after SetColor. " +
                "If they differ, a mesh rebuild occurred — violates no-rebuild contract.");

            VisualFrame frame2 = Capture(camera);
            SnapshotRenderer.WritePngFromRgba32(frame2.Pixels, "lit-fill-color-red.png");

            double greenR = MeanChannel(frame1.Pixels, 0);
            double greenG = MeanChannel(frame1.Pixels, 1);
            double redR   = MeanChannel(frame2.Pixels, 0);
            double redG   = MeanChannel(frame2.Pixels, 1);

            Debug.Log($"[LitFillSnapshotTests] Green render: meanR={greenR:F3}, meanG={greenG:F3}. " +
                      $"Red render: meanR={redR:F3}, meanG={redG:F3}.");

            Assert.That(greenR + greenG, Is.GreaterThan(0.01),
                "Green render has near-zero channel means — fills may not have rendered.");

            Assert.That(redR, Is.GreaterThan(greenR - 0.05),
                $"After SetColor to red, mean R channel ({redR:F3}) should be >= green render R ({greenR:F3}).");
        }

        // ─── Test 3: Shader validity — the shader must compile ────────────────────

        [Test]
        public void LitFill_FillShader_CompilesWithoutErrors()
        {
            var shader = Shader.Find("Map/Fill");
            Assert.That(shader, Is.Not.Null,
                "Map/Fill shader not found. Check that Assets/Code/MapRenderer.Unity/Shaders/Map/Fill/Fill.shader " +
                "exists and Unity has imported it.");

            bool hasErrors = ShaderUtil.ShaderHasError(shader);
            if (hasErrors)
            {
                var msgs = ShaderUtil.GetShaderMessages(shader);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Map/Fill shader has {msgs.Length} compile error(s):");
                foreach (var m in msgs)
                    sb.AppendLine($"  [{m.severity}] {m.message} (file:{m.file} line:{m.line})");
                Assert.Fail(sb.ToString());
            }
        }

        // ─── Test 4: Normal map changes shading ───────────────────────────────────
        // A hand-assembled constant-normal surface cannot fake this.

        [Test]
        public void LitFill_NormalMap_ChangesShading()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo();
            Track(mapGo);

            if (mat == null)
                Assert.Fail("Fill material is null — shader may not be compiled yet.");

            // Use a bright lit material so the normal effect is visible.
            mat.SetColor("_BaseColor", new Color(0.8f, 0.8f, 0.8f, 1f));
            mat.SetFloat("_Metallic",   0f);
            mat.SetFloat("_Smoothness", 0.3f);

            // A 45° light makes the normal map vary the shading; intensity 1.0 keeps the flat render from
            // saturating to lum=1.0, which would hide the difference.
            AddDirectionalLight(mapGo, 1.0f, Quaternion.Euler(45f, 45f, 0f));

            using var snapNormal = new SnapshotRenderer(SnapW, SnapH);
            // Render WITHOUT normal map (flat +Y normal only).
            mat.DisableKeyword("_NORMALMAP");
            mat.SetTexture("_BumpMap", null);
            VisualFrame frameFlat = Capture(camera);
            SnapshotRenderer.WritePngFromRgba32(frameFlat.Pixels, "lit-fill-normal-off.png");

            // Create a procedural normal map: alternating bumps to produce measurable shading delta.
            // A 16×16 texture with alternating left/right normals (in tangent space).
            var normalTex = Track(CreateProceduralNormalMap(16));

            // Render WITH normal map keyword enabled and the procedural texture bound.
            mat.SetTexture("_BumpMap", normalTex);
            mat.EnableKeyword("_NORMALMAP");
            snapNormal.Render(camera);
            snapNormal.WritePng("lit-fill-normal-on.png");

            // Mean absolute per-pixel difference: a constant-+Y-normal shader gives ≈ 0, a working normal map
            // gives > 0. Background pixels cancel, as both renders share the slate.
            double lumFlat = SnapshotCoverage.MeanLuminanceOfNonBackground(frameFlat.Pixels, Bg32);
            double absPixelDiff = MeanAbsDiff(frameFlat.Pixels, snapNormal.Pixels);

            Debug.Log($"[LitFillSnapshotTests] NormalMap: lum(flat)={lumFlat:F4}, " +
                      $"meanAbsDiff(flat vs normalmap)={absPixelDiff:F4}");

            if (lumFlat > 0.98)
            {
                // Both renders are saturated — that is a broken test setup, so fail loudly rather
                // than let CI read it as a skip.
                Assert.Fail(
                    $"Flat render is near-max (lum={lumFlat:F4}) — both renders may be saturated. " +
                    $"absPixelDiff={absPixelDiff:F4}. If absPixelDiff is 0, the normal map has no effect; " +
                    "re-run with lower light intensity. this tooth requires a non-saturated render.");
            }

            Assert.That(absPixelDiff, Is.GreaterThan(0.005),
                $"Binding a normal map with alternating ±X deflections must produce a per-pixel " +
                $"luminance difference vs the flat render (mean abs diff = {absPixelDiff:F4}). " +
                "A hand-assembled constant-normal surface (shallow shader) gives diff ≈ 0. " +
                "Check that _NORMALMAP keyword is enabled and InitializeStandardLitSurfaceData is called.");
        }

        // ─── Test 5: Base map samples ─────────────────────────────────────────────
        // Binding an albedo texture must produce spatial color variance (pattern visible).

        [Test]
        public void LitFill_BaseMap_ProducesSpatialVariance()
        {
            // Ambient up so the texture is visible — overrides the fixture's near-black default for
            // THIS test's render only; VisualTestFixture restores the pre-fixture value regardless.
            RenderSettings.ambientLight = new Color(0.3f, 0.3f, 0.3f, 1f);

            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo();
            Track(mapGo);

            if (mat == null)
                Assert.Fail("Fill material is null.");

            mat.SetColor("_BaseColor",      Color.white); // neutral — let base map color dominate
            mat.SetColor("_BaseColor",  Color.white);
            mat.SetFloat("_Metallic",   0f);
            mat.SetFloat("_Smoothness", 0.1f);

            AddDirectionalLight(mapGo, 1.5f, Quaternion.Euler(50f, 0f, 0f));

            // Checkerboard base map: alternating red/blue 8×8 squares.
            var checker = Track(CreateCheckerTexture(128, Color.red, Color.blue));

            using var snapTex   = new SnapshotRenderer(SnapW, SnapH);
            // Render without base map (flat white).
            mat.SetTexture("_BaseMap", null);
            VisualFrame frameNoTex = Capture(camera);
            SnapshotRenderer.WritePngFromRgba32(frameNoTex.Pixels, "lit-fill-basemap-off.png");

            // Render with checkerboard base map.
            mat.SetTexture("_BaseMap", checker);
            snapTex.Render(camera);
            snapTex.WritePng("lit-fill-basemap-on.png");

            // Measure spatial variance in the checkerboard render — non-uniform pixels expected.
            double varianceR = PixelVariance(snapTex.Pixels, 0);
            double varianceB = PixelVariance(snapTex.Pixels, 2);

            Debug.Log($"[LitFillSnapshotTests] BaseMap: varianceR={varianceR:F4}, varianceB={varianceB:F4}");

            if (varianceR < 0.001 && varianceB < 0.001)
            {
                Assert.Fail(
                    "Base map checkerboard shows near-zero spatial variance (the checker tooth). " +
                    "The base map pattern should be visible — check that InitializeStandardLitSurfaceData " +
                    "is called (not a hand-assembled constant albedo).");
            }

            // At least one of R or B should show the checker pattern.
            Assert.That(varianceR + varianceB, Is.GreaterThan(0.001),
                "Base map (checker texture) must produce spatial color variance across the fill (the checker tooth).");
        }

        // ─── Test 6: Metallic/smoothness produce a specular delta ─────────────────

        [Test]
        public void LitFill_MetallicSmoothness_ProduceSpecularDelta()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo();
            Track(mapGo);

            if (mat == null)
                Assert.Fail("Fill material is null.");

            // White base color so specular shows clearly.
            mat.SetColor("_BaseColor",     Color.white);
            mat.SetColor("_BaseColor", Color.white);

            // Bright directional light aimed at a glancing angle so specular is strong.
            AddDirectionalLight(mapGo, 3f, Quaternion.Euler(30f, 0f, 0f));

            using var snapSpecular = new SnapshotRenderer(SnapW, SnapH);
            // Render: matte (metallic=0, low smoothness).
            mat.SetFloat("_Metallic",   0f);
            mat.SetFloat("_Smoothness", 0.05f);
            VisualFrame frameMatte = Capture(camera);
            SnapshotRenderer.WritePngFromRgba32(frameMatte.Pixels, "lit-fill-specular-off.png");

            // Render: specular (metallic=1, high smoothness).
            mat.SetFloat("_Metallic",   1f);
            mat.SetFloat("_Smoothness", 0.95f);
            snapSpecular.Render(camera);
            snapSpecular.WritePng("lit-fill-specular-on.png");

            double lumMatte   = SnapshotCoverage.MeanLuminanceOfNonBackground(frameMatte.Pixels, Bg32);
            double lumSpecular = SnapshotCoverage.MeanLuminanceOfNonBackground(snapSpecular.Pixels, Bg32);

            Debug.Log($"[LitFillSnapshotTests] Specular: lum(matte)={lumMatte:F4}, lum(specular)={lumSpecular:F4}, " +
                      $"delta={System.Math.Abs(lumMatte - lumSpecular):F4}");

            // Metallic+high-smoothness must produce a measurably different (usually brighter)
            // luminance than matte. The delta proves PBR BRDF is live.
            Assert.That(System.Math.Abs(lumMatte - lumSpecular), Is.GreaterThan(0.02),
                $"Metallic=1/Smoothness=0.95 must produce a specular highlight vs matte (the specular tooth). " +
                $"lum(matte)={lumMatte:F4}, lum(metallic)={lumSpecular:F4}. " +
                "Check that _Metallic / _Smoothness feed into InitializeStandardLitSurfaceData " +
                "and that UniversalFragmentPBR is called in the forward pass.");
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Compute the mean value [0,1] of a single RGBA channel across ALL pixels.
        /// Channel index: 0=R, 1=G, 2=B, 3=A.
        /// </summary>
        private static byte Channel(Color32 px, int channel) => channel switch
        {
            0 => px.r, 1 => px.g, 2 => px.b, _ => px.a,
        };

        private static double MeanChannel(Frame frame, int channel)
        {
            Color32[] pixels = frame.Pixels;
            if (pixels == null || pixels.Length == 0) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < pixels.Length; i++)
                sum += Channel(pixels[i], channel);
            return (sum / pixels.Length) / 255.0;
        }

        /// <summary>
        /// Compute the variance of a single RGBA channel across ALL pixels (normalised to [0,1]).
        /// High variance = spatial non-uniformity (e.g. checker pattern).
        /// </summary>
        private static double PixelVariance(Frame frame, int channel)
        {
            Color32[] pixels = frame.Pixels;
            if (pixels == null || pixels.Length == 0) return 0.0;
            double mean = MeanChannel(frame, channel);
            double sumSq = 0.0;
            for (int i = 0; i < pixels.Length; i++)
            {
                double v = Channel(pixels[i], channel) / 255.0 - mean;
                sumSq += v * v;
            }
            return sumSq / pixels.Length;
        }

        /// <summary>
        /// Create a procedural normal map texture (Unity NormalMap format) with alternating
        /// left/right normals arranged in a grid, to force measurable shading variation.
        /// </summary>
        private static Texture2D CreateProceduralNormalMap(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Alternate between a left-leaning and right-leaning normal every 2 pixels.
                // Normal map encoding: R=x, G=y, tangent space. Tilted 45° left/right.
                bool leftTile = ((x + y) / 2 % 2) == 0;
                // In tangent space: tilted normal = (±0.7, 0, 0.7) normalized, encoded to [0,1].
                // DXT5nm: x in A, y in G; or Unity std: x in R, y in G.
                float nx = leftTile ? -0.7f : 0.7f;
                float ny = 0.0f;
                // Encode: R = nx*0.5+0.5, G = ny*0.5+0.5, B = 1 (z=1 approx).
                byte r = (byte)Mathf.Clamp(Mathf.RoundToInt((nx * 0.5f + 0.5f) * 255), 0, 255);
                byte g = (byte)Mathf.Clamp(Mathf.RoundToInt((ny * 0.5f + 0.5f) * 255), 0, 255);
                pixels[y * size + x] = new Color32(r, g, 255, 255);
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Compute the mean absolute per-pixel difference between two RGBA byte arrays,
        /// normalised to [0,1] (0 = identical, 1 = maximum possible difference).
        /// Returns the mean over all pixels (not channels) so one saturated channel
        /// doesn't dominate.
        /// </summary>
        private static double MeanAbsDiff(Frame frameA, Frame frameB)
        {
            Color32[] pixA = frameA.Pixels, pixB = frameB.Pixels;
            if (pixA == null || pixB == null || pixA.Length != pixB.Length) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < pixA.Length; i++)
                sum += System.Math.Abs(pixA[i].r - pixB[i].r) + System.Math.Abs(pixA[i].g - pixB[i].g)
                     + System.Math.Abs(pixA[i].b - pixB[i].b) + System.Math.Abs(pixA[i].a - pixB[i].a);
            // Normalise: divide by (pixels * 255) so result is [0,1].
            return sum / (pixA.Length * 255.0);
        }

        /// <summary>
        /// Create a checkerboard Texture2D alternating two colors in 8×8 tiles.
        /// </summary>
        private static Texture2D CreateCheckerTexture(int size, Color colorA, Color colorB)
        {
            const int tileSize = 8;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            tex.filterMode = FilterMode.Point;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool isA = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                Color c = isA ? colorA : colorB;
                pixels[y * size + x] = c;
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }

    // Unity EditMode only (it renders, so it is not in Tools/core-tests) — the OUTWARD-BAND mechanism probe.
    // Interior vertices carry `side = 0`, band outer vertices `side = 1`, and the fragment reuses the line
    // path's outer-edge formula:
    //
    //     sideGrad = max(length(float2(ddx(side), ddy(side))), 1e-6)
    //     coverage = saturate((1 - |side|) / sideGrad)
    //
    // Non-obvious why: an interior's `side` is constant, so its derivative is 0 and coverage rests on
    // `saturate(1 / 1e-6)` returning 1.0 after the GPU's shader compiler — a device fact only a render answers.
    // P1 checks it face-on, across the band junction (a 2x2 derivative quad can straddle two primitives) and
    // under a grazing tilt. P3: a coverage-0 fragment in a `ZWrite On` pass writes depth unless the pass clips
    // it; a quad of `side = 1` isolates that fragment.

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillOutwardBandProbeTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    internal class FillOutwardBandProbeTests
    {
        private const int SnapPx = 512;

        /// <summary>Half-extent of the constant-<c>side</c> interior square, world units.</summary>
        private const float InteriorHalf = 8f;

        /// <summary>Orthographic half-height. With <see cref="SnapPx"/> = 512 this makes one device pixel
        /// exactly <c>2 * OrthoSize / 512</c> world units, which is how the band is sized in real px.</summary>
        private const float OrthoSize = 10f;

        /// <summary>One device pixel in world units under the orthographic arm.</summary>
        private const float WorldPerPx = 2f * OrthoSize / SnapPx;

        /// <summary>Non-black background, so a real "coverage 0 everywhere" reading is distinguishable
        /// from an all-black render — this fixture's whole subject is whether coverage is 1 or 0.</summary>
        private static readonly Color BackgroundColor = new Color(0.10f, 0.11f, 0.15f, 1f);

        // ── The probe scene ────────────────────────────────────────────────────────────────────────────

        /// <summary>A rendered probe frame plus the handles the fixture tears down.</summary>
        private sealed class ProbeFrame : System.IDisposable
        {
            /// <summary>Bottom-left origin (<see cref="SnapshotRenderer.Pixels"/>'s convention).</summary>
            public Frame Pixels;

            private readonly GameObject[] _objects;
            private readonly Material[] _materials;
            private readonly Mesh[] _meshes;

            /// <param name="objects">Scene objects to destroy.</param>
            /// <param name="materials">Materials to destroy.</param>
            /// <param name="meshes">Meshes to destroy.</param>
            public ProbeFrame(GameObject[] objects, Material[] materials, Mesh[] meshes)
            {
                _objects   = objects;
                _materials = materials;
                _meshes    = meshes;
            }

            /// <summary>Linear-space coverage at one pixel: the composite's position on the
            /// background→white axis, which for this probe's white ink IS the fragment's alpha.</summary>
            /// <param name="column">Pixel column.</param>
            /// <param name="row">Pixel row, bottom-left origin.</param>
            public double CoverageAt(int column, int row)
                => PixelCoverage.CoverageAt(Pixels, column, row,
                                            BackgroundLinear(), new float3(1f, 1f, 1f));

            /// <summary>The background colour in linear space — the composite floor every reading is
            /// measured against.</summary>
            public static float3 BackgroundLinear() => new float3(
                PixelCoverage.ToLinear((byte)math.round(BackgroundColor.r * 255f)),
                PixelCoverage.ToLinear((byte)math.round(BackgroundColor.g * 255f)),
                PixelCoverage.ToLinear((byte)math.round(BackgroundColor.b * 255f)));

            /// <summary>Mean linear RGB over the inclusive-exclusive box <c>[x0,x1)x[y0,y1)</c>.</summary>
            /// <param name="x0">Left column, inclusive.</param>
            /// <param name="y0">Bottom row, inclusive.</param>
            /// <param name="x1">Right column, exclusive.</param>
            /// <param name="y1">Top row, exclusive.</param>
            public float3 MeanLinear(int x0, int y0, int x1, int y1)
            {
                float3 sum = float3.zero;
                int n = 0;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { sum += PixelCoverage.SampleLinear(Pixels, x, y); n++; }
                return sum / math.max(n, 1);
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                foreach (GameObject go in _objects) if (go != null) Object.DestroyImmediate(go);
                foreach (Material m in _materials)  if (m  != null) Object.DestroyImmediate(m);
                foreach (Mesh mesh in _meshes)      if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// Builds the probe mesh: a constant-<c>side</c>-0 interior square ringed by a band whose outer
        /// vertices carry <c>side = 1</c>. The interior is TWO triangles all of whose vertices share one
        /// <c>side</c> — the zero-derivative case — and the ring is four quads across which <c>side</c>
        /// varies, so one mesh carries both régimes and the junction between them.
        /// </summary>
        /// <param name="bandWorldWidth">Band thickness in world units; at <see cref="WorldPerPx"/> it is
        /// one device pixel under the orthographic arm.</param>
        private static Mesh BuildBandedQuad(float bandWorldWidth)
        {
            float inner = InteriorHalf;
            float outer = InteriorHalf + bandWorldWidth;

            var vertices = new Vector3[8];
            var uvs = new Vector2[8];
            // 0..3 interior (side 0), 4..7 band outer (side 1), both wound counter-clockwise from -X-Y.
            var innerCorners = new[] { new Vector2(-inner, -inner), new Vector2(inner, -inner),
                                       new Vector2(inner,  inner), new Vector2(-inner, inner) };
            var outerCorners = new[] { new Vector2(-outer, -outer), new Vector2(outer, -outer),
                                       new Vector2(outer,  outer), new Vector2(-outer, outer) };
            for (int i = 0; i < 4; i++)
            {
                vertices[i]     = new Vector3(innerCorners[i].x, innerCorners[i].y, 0f);
                uvs[i]          = new Vector2(0f, 0f);
                vertices[i + 4] = new Vector3(outerCorners[i].x, outerCorners[i].y, 0f);
                uvs[i + 4]      = new Vector2(1f, 0f);
            }

            var triangles = new System.Collections.Generic.List<int> { 0, 1, 2, 0, 2, 3 };
            for (int i = 0; i < 4; i++)
            {
                int a = i, b = (i + 1) % 4;
                triangles.AddRange(new[] { a, a + 4, b + 4, a, b + 4, b });
            }

            var mesh = new Mesh { name = "FillBandCoverageProbeQuad" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A flat quad whose every vertex carries <paramref name="side"/> — constant, so it renders
        /// one uniform coverage. Used for the depth arm, where <c>side = 1</c> gives coverage 0 everywhere.</summary>
        /// <param name="half">Half-extent, world units.</param>
        /// <param name="side">The constant <c>side</c> every vertex carries.</param>
        /// <param name="z">Plane depth, world units.</param>
        private static Mesh BuildConstantSideQuad(float half, float side, float z)
        {
            var mesh = new Mesh { name = $"ConstantSideQuad_{side}" };
            mesh.SetVertices(new[]
            {
                new Vector3(-half, -half, z), new Vector3(half, -half, z),
                new Vector3( half,  half, z), new Vector3(-half, half, z),
            });
            mesh.SetUVs(0, new[] { new Vector2(side, 0f), new Vector2(side, 0f),
                                   new Vector2(side, 0f), new Vector2(side, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Renders <paramref name="meshes"/> (each with its own material) through a camera looking down
        /// <c>+Z</c> at the <c>z = 0</c> plane.
        /// </summary>
        /// <param name="meshes">Geometry to draw, in the order given.</param>
        /// <param name="materials">One material per mesh; disposed with the frame.</param>
        /// <param name="tiltDegrees">Camera rotation about X; 0 is face-on. Non-zero is PERSPECTIVE, as orthographic
        /// foreshortens uniformly and would not vary the per-pixel derivative.</param>
        private static ProbeFrame Render(Mesh[] meshes, Material[] materials, float tiltDegrees)
        {
            var objects = new GameObject[meshes.Length + 1];
            for (int i = 0; i < meshes.Length; i++)
            {
                var go = new GameObject($"BandProbe_{i}");
                go.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = materials[i];
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                objects[i] = go;
            }

            var camGo = new GameObject("BandProbe_Camera");
            objects[meshes.Length] = camGo;
            var camera = camGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor;
            camera.enabled = false;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 500f;

            if (tiltDegrees == 0f)
            {
                camera.orthographic = true;
                camera.orthographicSize = OrthoSize;
                camGo.transform.position = new Vector3(0f, 0f, -50f);
                camGo.transform.rotation = Quaternion.identity;
            }
            else
            {
                // Orbit by `tiltDegrees` and aim back at the origin: the projected band narrows toward zero px,
                // so |grad side| grows without bound.
                camera.orthographic = false;
                camera.fieldOfView = 40f;
                float radians = math.radians(tiltDegrees);
                const float distance = 60f;
                camGo.transform.position =
                    new Vector3(0f, -distance * math.sin(radians), -distance * math.cos(radians));
                camGo.transform.LookAt(Vector3.zero, Vector3.up);
            }

            var frame = new ProbeFrame(objects, materials, meshes);
            using (var snapshot = new SnapshotRenderer(SnapPx, SnapPx))
            {
                snapshot.Render(camera);
                frame.Pixels = snapshot.Pixels;
            }
            return frame;
        }

        /// <summary>The probe material, white ink so the composited grey level IS the fragment's alpha.</summary>
        /// <param name="zWrite">Value for <c>_ProbeZWrite</c>; 1 reproduces the fill's depth-writing passes.</param>
        /// <param name="renderQueue">Explicit queue, so the depth arm can order two draws.</param>
        private static Material ProbeMaterial(float zWrite, int renderQueue)
        {
            Shader shader = Shader.Find("Hidden/MapRenderer/Tests/FillBandCoverageProbe");
            Assert.IsNotNull(shader, "the test-only probe shader must be importable by name.");
            var material = new Material(shader) { renderQueue = renderQueue };
            material.SetColor("_Color", Color.white);
            material.SetFloat("_ProbeZWrite", zWrite);
            return material;
        }

        // ── P1 guard: the copied formula must still be the shipped one ─────────────────────────────────

        /// <summary>
        /// The probe's fragment body is a copy of the shipped outer-edge expression, so it can only answer
        /// the real question while it stays a copy. Both lines are read from disk and compared; this fails
        /// the moment <c>Line_VertexExtrude.hlsl</c>'s formula changes, which is the direction that matters
        /// — a drifted probe would keep passing while measuring something the product no longer does.
        /// </summary>
        [Test]
        public void ProbeFormula_IsVerbatimTheShippedLineCoverageExpression()
        {
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string shipped = File.ReadAllText(Path.Combine(root,
                "Assets/Code/MapRenderer.Unity/Shaders/Map/Line/Line_VertexExtrude.hlsl"));
            string probe = File.ReadAllText(Path.Combine(root,
                "Assets/Tests/MapRenderer.Tests.Visual/FillBandCoverageProbe.shader"));

            const string gradient = "max(length(float2(ddx(side), ddy(side))), 1e-6)";
            const string coverage = "saturate((1.0 - absSide) / sideGrad)";

            StringAssert.Contains(gradient, shipped,
                "Line_VertexExtrude.hlsl no longer computes the Euclidean side gradient this way; the probe " +
                "is measuring a formula the product has stopped using.");
            StringAssert.Contains(coverage, shipped,
                "Line_VertexExtrude.hlsl no longer computes the outer-edge coverage this way.");
            StringAssert.Contains(gradient.Replace("side)", "input.side)"), probe,
                "the probe shader must carry the shipped gradient expression verbatim.");
            StringAssert.Contains(coverage, probe,
                "the probe shader must carry the shipped coverage expression verbatim.");
        }

        // ── P1: is a constant side = 0 interior stable? ─────────────────────────────────────────────────

        /// <summary>
        /// Face-on, band 20 device px wide so the interior and the ramp separate. The interior's <c>side</c> is
        /// constant, so its derivative is 0 and coverage comes from the <c>1e-6</c> clamp alone. The outer-edge
        /// assertion stops a shader that returns a constant 1 from passing the interior half.
        /// </summary>
        [Test]
        public void ConstantSideInterior_ReadsFullCoverage_FaceOn()
        {
            Mesh mesh = BuildBandedQuad(20f * WorldPerPx);
            using ProbeFrame frame = Render(new[] { mesh }, new[] { ProbeMaterial(0f, 3000) }, tiltDegrees: 0f);

            // Interior square spans +/-8 world units = +/-204.8 px about the frame centre; sample well inside.
            double worst = 1.0;
            int worstX = -1, worstY = -1;
            for (int y = 156; y < 356; y++)
            for (int x = 156; x < 356; x++)
            {
                double c = frame.CoverageAt(x, y);
                if (c < worst) { worst = c; worstX = x; worstY = y; }
            }
            TestContext.WriteLine(
                $"P1 face-on: interior minimum coverage = {worst:F4} at ({worstX},{worstY}) over 200x200 px.");

            Assert.That(worst, Is.GreaterThan(0.99),
                $"a constant side = 0 interior read coverage {worst:F4} at ({worstX},{worstY}). The " +
                "derivative there is exactly 0, so this is the 1e-6 clamp failing to saturate — the " +
                "outward-band mechanism needs a different interior encoding.");

            // The ramp must exist, or the interior verdict is vacuous. "Coverage 0 beyond the geometry" passes a
            // shader that returns 1 everywhere; only a working ramp gives a pixel strictly between 0 and 1.
            double graded = 0.0;
            int gradedRow = -1;
            var ramp = new System.Text.StringBuilder("P1 face-on: rows across the band's outer edge → ");
            for (int row = SnapPx / 2 + 200; row <= SnapPx / 2 + 232; row++)
            {
                double c = frame.CoverageAt(SnapPx / 2, row);
                ramp.Append($"{row - SnapPx / 2}={c:F3} ");
                double gradedness = math.min(c, 1.0 - c);
                if (gradedness > graded) { graded = gradedness; gradedRow = row; }
            }
            TestContext.WriteLine(ramp.ToString());
            Assert.That(graded, Is.GreaterThan(0.05),
                $"no partially-covered pixel anywhere across the band's outer edge (best gradedness " +
                $"{graded:F4} at row {gradedRow}) — the probe is returning a constant and its interior " +
                "reading means nothing.");
        }

        /// <summary>
        /// The junction case: a 2x2 derivative quad straddling the interior/band boundary holds fragments of two
        /// primitives with <c>side</c> gradients 0 and <c>1/bandPx</c>. At the shipped one-pixel band, coverage
        /// must stay saturated up to the styled edge. A dip there renders as a dark hairline inset from every
        /// polygon boundary.
        /// </summary>
        [Test]
        public void ConstantSideInterior_HoldsAcrossTheBandJunction()
        {
            Mesh mesh = BuildBandedQuad(WorldPerPx);
            using ProbeFrame frame = Render(new[] { mesh }, new[] { ProbeMaterial(0f, 3000) }, tiltDegrees: 0f);

            // The interior's top edge sits at 8 world units above centre = 204.8 px; the band adds 1 px.
            const int centre = SnapPx / 2;
            var profile = new System.Text.StringBuilder("P1 junction: rows about the interior/band seam → ");
            double worstInside = 1.0;
            int worstRow = -1;
            for (int row = centre + 195; row <= centre + 212; row++)
            {
                double c = frame.CoverageAt(centre, row);
                profile.Append($"{row - centre}={c:F3} ");
                if (row <= centre + 203 && c < worstInside) { worstInside = c; worstRow = row; }
            }
            TestContext.WriteLine(profile.ToString());

            Assert.That(worstInside, Is.GreaterThan(0.99),
                $"coverage dipped to {worstInside:F4} at row {worstRow}, inside the interior and within a " +
                "pixel of its junction with the band. That is a derivative quad straddling two primitives " +
                "poisoning the constant-side interior — the mechanism would draw a dark hairline just " +
                "inside every boundary.");
        }

        /// <summary>
        /// Grazing tilt. The band foreshortens toward zero projected width, so <c>|grad side|</c> grows
        /// without bound and the band's own coverage collapses — that part is expected and is the mechanism
        /// degrading to today's hard edge, not a defect. What must NOT move is the interior: its derivative
        /// is 0 at any view angle, so its coverage must still be 1.
        /// </summary>
        [Test]
        public void ConstantSideInterior_HoldsUnderGrazingTilt()
        {
            Mesh mesh = BuildBandedQuad(WorldPerPx);
            using ProbeFrame frame = Render(new[] { mesh }, new[] { ProbeMaterial(0f, 3000) }, tiltDegrees: 80f);

            // Under an 80-degree tilt the quad compresses toward the frame's middle band; sample a box that
            // is interior at that pose, then report what was actually found rather than trusting the pose.
            double worst = 1.0;
            int worstX = -1, worstY = -1, sampled = 0;
            for (int y = 249; y < 263; y++)
            for (int x = 236; x < 276; x++)
            {
                double c = frame.CoverageAt(x, y);
                sampled++;
                if (c < worst) { worst = c; worstX = x; worstY = y; }
            }
            TestContext.WriteLine(
                $"P1 tilt 80 deg: interior minimum coverage = {worst:F4} at ({worstX},{worstY}) over {sampled} px.");

            Assert.That(worst, Is.GreaterThan(0.99),
                $"under an 80 degree tilt the constant-side interior read {worst:F4} at " +
                $"({worstX},{worstY}). The interior's derivative is 0 at every view angle, so a shortfall " +
                "here means the clamp is view-dependent.");
        }

        // ── P3: does a coverage-0 fragment in a ZWrite-On pass occlude what is behind it? ───────────────

        /// <summary>
        /// The fill's ShadowCaster / GBuffer / DepthOnly / DepthNormals passes hardcode <c>ZWrite On</c>, so a
        /// coverage-0 band fragment writes depth unless the pass clips. A constant <c>side = 1</c> quad (coverage
        /// 0 everywhere) sits in front of an opaque red quad. <c>ZWrite On</c> models the fill's depth passes;
        /// <c>ZWrite Off</c> is the control that proves the red quad is visible at all.
        /// </summary>
        [Test]
        public void ZeroCoverageFragment_WritesDepth_AndHidesGeometryBehindIt()
        {
            const int Box = 40;
            int lo = SnapPx / 2 - Box, hi = SnapPx / 2 + Box;

            double[] redness = new double[2];
            foreach (bool zWrite in new[] { false, true })
            {
                Mesh front = BuildConstantSideQuad(6f, side: 1f, z: 0f);
                Mesh back  = BuildConstantSideQuad(6f, side: 0f, z: 5f);

                var frontMaterial = ProbeMaterial(zWrite ? 1f : 0f, 3000);
                var backMaterial  = ProbeMaterial(0f, 3100);
                backMaterial.SetColor("_Color", Color.red);

                using ProbeFrame frame = Render(new[] { front, back },
                                                new[] { frontMaterial, backMaterial }, tiltDegrees: 0f);

                float3 mean = frame.MeanLinear(lo, lo, hi, hi);
                float3 background = ProbeFrame.BackgroundLinear();
                // How far the box has travelled from background toward pure red, in linear space.
                redness[zWrite ? 1 : 0] = (mean.x - background.x) / math.max(1f - background.x, 1e-6f);
                TestContext.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "P3 ZWrite {0}: mean linear RGB behind the coverage-0 quad = ({1:F4},{2:F4},{3:F4}), " +
                    "redness = {4:F4}", zWrite ? "On " : "Off", mean.x, mean.y, mean.z, redness[zWrite ? 1 : 0]));
            }

            Assert.That(redness[0], Is.GreaterThan(0.8),
                $"control: with ZWrite Off the red quad behind must be visible through a fully transparent " +
                $"fragment; redness {redness[0]:F4}. If this fails the arms below compare nothing.");
            Assert.That(redness[1], Is.LessThan(0.2),
                $"with ZWrite On a coverage-0 fragment did NOT occlude the geometry behind it (redness " +
                $"{redness[1]:F4}). That would mean the fill's depth passes need no coverage clip, which " +
                "contradicts the whole reason this arm exists — check the fixture before the conclusion.");
        }

        // ── P2: two fragments of ONE fill layer at one pixel — do they composite twice? ─────────────────

        /// <summary>
        /// The outward band puts a polygon's ramp OVER its neighbour's interior wherever two polygons of one
        /// layer abut, which at the shipped <c>FillTileBufferClip: 0</c> is every tile seam. Do two fragments of
        /// the SAME layer and draw blend twice at one pixel, or does depth state reject the second? Two
        /// overlapping polygons answer it with no probe shader, measuring the composited alpha a rim is made of.
        /// </summary>
        [Test]
        public void OverlappingPolygonsInOneLayer_ReportsWhetherTheyCompositeTwice()
        {
            const double Opacity = 0.5;
            var tile = new TileId { Z = 6, X = 40, Y = 25 };

            double2 Corner(double u, double v) => tile.ToLonLat(u, v, 1.0);
            string Rect(double westU, double eastU)
            {
                double2 nw = Corner(westU, 0.2), se = Corner(eastU, 0.8);
                // Tile-local v grows SOUTHWARD, so the small-v corner carries the NORTH latitude.
                return GeoJsonTestFixtures.Feature(
                    "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(nw.x, se.y, se.x, nw.y)}]");
            }

            double2 centre = Corner(0.5, 0.5);
            using var scene = VisualScene.New()
                .RenderMode(MapRenderer.Unity.Rendering.Materials.RenderMode.Unlit)
                .Source("poly", GeoJson.FeatureCollection(
                    GeoJsonTestFixtures.Collection(Rect(0.15, 0.55), Rect(0.45, 0.85))))
                .Layer(VisualLayer.Fill("probe-fill").Source("poly").Color("#808080").Opacity(Opacity))
                .Camera(new GeoCoordinate3D { Longitude = centre.x, Latitude = centre.y, Altitude = 0.0 },
                        zoom: tile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            Frame px = frame.Pixels;
            float3 background = PixelCoverage.BackgroundLinear(px);
            float3 Mean(int x0, int y0, int x1, int y1)
            {
                float3 sum = float3.zero;
                int n = 0;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { sum += PixelCoverage.SampleLinear(px, x, y); n++; }
                return sum / math.max(n, 1);
            }

            // Overlap spans u 0.45..0.55 = columns 230..282; single cover sits well left of it.
            float3 single  = Mean(120, 200, 180, 260);
            float3 overlap = Mean(244, 200, 268, 260);

            // Ink laid down, relative to the background, as a fraction of the axis the single-covered
            // region defines: 1.0 = one coat, 1.5 = two coats at alpha 0.5 (0.75 / 0.50).
            double singleInk  = math.length(single  - background);
            double overlapInk = math.length(overlap - background);
            double ratio = overlapInk / math.max(singleInk, 1e-6);

            TestContext.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "P2 overlap at fill-opacity {0:F2}: single-cover ink {1:F4}, overlap ink {2:F4}, " +
                "ratio {3:F4} (1.000 = the second fragment was rejected, 1.500 = it composited over the first)",
                Opacity, singleInk, overlapInk, ratio));

            Assert.That(ratio, Is.GreaterThan(1.05),
                $"two overlapping polygons of ONE layer composited to a ratio of {ratio:F4}, i.e. the " +
                "second fragment did not blend over the first. If that is real it changes the outward " +
                "band's abutting-pair story completely — verify the fixture before believing it.");
        }
    }

    // GlobeFillBandRenderTests — the boundary band, observed in rendered pixels on the CURVED arm, which the
    // shipped scene uses. It compares band-on and band-off frames, as a lit sphere defies absolute colours.
    // Non-obvious why: Cull Back is load-bearing. Earcut normalises outer-ring winding, so a band that kept
    // its ring's sign is counter-wound and culled, which only this fixture sees: the _Cull default is Off.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeFillBandRenderTests — The fill boundary band
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class GlobeFillBandRenderTests
    {
        private const int SnapPx = 512;

        /// <summary>Ocean-ish clear colour, so land polygons read against a known background.</summary>
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        /// <summary>Three 8-bit LSBs, in RGB distance — comfortably above quantisation and far below a
        /// partially-covered pixel's step.</summary>
        private const double Tolerance = 3.0 / 255.0 * 1.7320508075688772;

        private const string NoGpuMessage =
            "Globe render is all-background: no GPU context in batch EditMode. " +
            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode";

        /// <summary>The three-quarter view the Americas face: no fill boundary lies along the silhouette,
        /// so grazing incidence is approached but never entered.</summary>
        private static readonly (Vector3 Position, Vector3 Up) ObliquePose =
            (new Vector3(1.7f, 1.2f, -3.0f), Vector3.up);

        /// <summary>Straight down the polar axis (+Y in render space — <c>SphericalProjection</c> maps
        /// latitude to Y), which puts the EQUATOR on the silhouette. Equatorial coastline then runs along the
        /// limb with its outward band direction pointing along the view ray, which is the one geometry that
        /// drives <c>MapPixelsToWorld</c>'s probe to zero. <c>Vector3.up</c> cannot be the camera's up here —
        /// it is the view direction.</summary>
        private static readonly (Vector3 Position, Vector3 Up) PolarPose =
            (new Vector3(0f, 3.5f, 0f), Vector3.forward);

        /// <summary>Renders the z0 countries fixture on a sphere, with or without the boundary band.</summary>
        /// <param name="pose">Camera position and up vector; the camera always looks at the origin.</param>
        /// <param name="suppressBand">True to build the same mesh with no band geometry at all.</param>
        /// <param name="superSample">Render at this multiple and box-downsample, to see sub-pixel geometry.</param>
        /// <returns>The frame's RGBA32 pixels, row-major from the bottom-left.</returns>
        private static Frame RenderGlobe(
            (Vector3 Position, Vector3 Up) pose, bool suppressBand, int superSample = 1)
        {
            using var bag = new ObjectDisposalBag();
            var (mapGo, material) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"rgba\",95,165,95,1]",
                viewSize: 2f,
                projection: new SphericalProjection(),
                suppressBoundaryBand: suppressBand);
            bag.Track(mapGo);

            // Stock Cull Back — see this file's header. Without it a counter-wound band still renders and
            // the winding defect this fixture is here to catch passes.
            if (material != null) material.SetCull(CullMode.Back);

            // Not bag-tracked: parented to mapGo below, so destroying mapGo destroys it too.
            var lightGo = new GameObject("GlobeBandLight");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);
            lightGo.transform.SetParent(mapGo.transform, worldPositionStays: true);

            var cameraGo = bag.Track(new GameObject("GlobeBandCamera"));
            Camera camera = cameraGo.AddComponent<Camera>();
            camera.transform.position = pose.Position;
            camera.transform.rotation = Quaternion.LookRotation(-pose.Position, pose.Up);
            camera.orthographic = false;
            camera.fieldOfView = 35f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = OceanBg;
            camera.enabled = false;

            using var snap = new SnapshotRenderer(SnapPx * superSample, SnapPx * superSample);
            snap.Render(camera);
            Color32[] raw = snap.Pixels.Pixels;
            if (superSample == 1) return new Frame((Color32[])raw.Clone(), SnapPx, SnapPx);

            // Box-downsample: a pixel any sub-sample covered carries ink. Non-obvious why: a MAX-deviation
            // reduction gives the identical offender count on both poses, so the residual is not an averaging artefact.
            int wide = SnapPx * superSample;
            var small = new Color32[SnapPx * SnapPx];
            for (int y = 0; y < SnapPx; y++)
                for (int x = 0; x < SnapPx; x++)
                {
                    int acc0 = 0, acc1 = 0, acc2 = 0;
                    for (int sy = 0; sy < superSample; sy++)
                        for (int sx = 0; sx < superSample; sx++)
                        {
                            Color32 s2 = raw[(y * superSample + sy) * wide + x * superSample + sx];
                            acc0 += s2.r; acc1 += s2.g; acc2 += s2.b;
                        }
                    int n2 = superSample * superSample;
                    small[y * SnapPx + x] = new Color32(
                        (byte)(acc0 / n2), (byte)(acc1 / n2), (byte)(acc2 / n2), 255);
                }
            return new Frame(small, SnapPx, SnapPx);
        }

        /// <summary>One pixel's RGB.</summary>
        /// <param name="px">The frame.</param>
        /// <param name="i">Pixel index.</param>
        /// <returns>The colour, channels in [0,1].</returns>
        private static double3 At(Frame px, int i)
        {
            Color32 c = px.Pixels[i];
            return new double3(c.r / 255.0, c.g / 255.0, c.b / 255.0);
        }

        // ── The band reaches the globe, and grows outward by one pixel ─────────────────────────────────

        /// <summary>
        /// Rendered band-on against band-off on the same globe: background pixels GAIN ink (the band renders,
        /// un-culled), no pixel LOSES ink (nothing moved inward), and every gained pixel lies within
        /// <see cref="FillBandJob.MiterLimit"/> + 1 px of geometry in the 8× supersampled band-free frame.
        ///
        /// <para>Non-obvious why: the reach is <c>MiterLimit</c> + 1, not 1, because the band stays one pixel
        /// wide PERPENDICULAR to the edge, so at a sharp spike its outer vertex sits up to <c>MiterLimit</c> px
        /// along the bisector (<c>FillBandJobTests.AMiterKeepsThePerpendicularWidthThroughARightAngle</c>); the
        /// + 1 is the pixel that vertex lands in. The oracle is the 8× frame because an island narrower than a pixel
        /// renders as nothing at 1× while its band is correctly drawn.</para>
        ///
        /// <para>Limitation: features too thin even for 8× leave hairline offenders, so the bound is a COUNT,
        /// not zero — measured 45 oblique and 78 pole-on, asserted 60 and 100 as GPU slack, against 290 and 351
        /// for a ×2 displacement. An exact oracle would test distance to the band's ring SEGMENTS; a mask of
        /// band-triangle edges with both ends at side 0 missed the limb. Project through clip space, not
        /// <c>Camera.WorldToScreenPoint</c>: <c>SnapshotRenderer</c> sets the pixel rect only later.</para>
        ///
        /// <para>No interior-purity check: countries share internal edges, where the band overlaps the
        /// neighbour's interior by design. Covered instead by
        /// <c>FillBoundaryBandRenderTests.ASquareFillHasGradedBoundaryPixels_AndAnUngradedInterior</c> and
        /// <c>GlobeFillBandTests.ABandQuadSplitsTheSharedEdgeExactlyWhereTheInteriorDoes</c>. The reach RED
        /// case is ×2: ×20 trips the ink-volume guard (<c>gained &lt; ink / 4</c>) before reach runs.</para>
        /// </summary>
        [Test]
        public void TheGlobeFillsSilhouetteGainsInkOutward_WithinTheMiterLimit()
            => AssertTheBandGrowsOutwardWithinAMiter(ObliquePose, "oblique", oracleBlindPixels: 60);

        /// <summary>
        /// The same contract straight down the polar axis. Pole-on the equator IS the silhouette, so coastline
        /// runs along the limb with its band direction down the view ray, driving <c>MapPixelsToWorld</c>'s
        /// probe span to zero; its <c>max(refPx, 0.1)</c> clamp bounds the scale but not a finite step along an
        /// edge-on tangent. It also renders more ink than the oblique pose, so a displacement has more boundary.
        /// Limitation: no camera tried reaches that hazard. A grazing-incidence guard in
        /// <c>Fill_VertexModify.hlsl</c> would change neither frame, so the shader has none.
        /// </summary>
        [Test]
        public void PoleOn_WhereTheLimbCarriesFillBoundary_TheBandIsStillBoundedByAMiter()
            => AssertTheBandGrowsOutwardWithinAMiter(PolarPose, "polar", oracleBlindPixels: 100);

        /// <summary>Renders one pose band-on against band-off and asserts the whole outward-growth contract.
        /// </summary>
        /// <param name="pose">Camera position and up vector.</param>
        /// <param name="poseName">Short label for the reported measurements.</param>
        /// <param name="oracleBlindPixels">Offenders the 8× oracle cannot explain: measurement plus GPU slack, see
        /// <c>TheGlobeFillsSilhouetteGainsInkOutward_WithinTheMiterLimit</c>.</param>
        private static void AssertTheBandGrowsOutwardWithinAMiter(
            (Vector3 Position, Vector3 Up) pose, string poseName, int oracleBlindPixels)
        {
            Frame hard = RenderGlobe(pose, suppressBand: true);
            Frame banded = RenderGlobe(pose, suppressBand: false);

            int n = SnapPx * SnapPx;
            double3 background = At(hard, 0);

            var isBackground = new bool[n];
            int ink = 0;
            for (int i = 0; i < n; i++)
            {
                isBackground[i] = math.length(At(hard, i) - background) <= Tolerance;
                if (!isBackground[i]) ink++;
            }
            if (ink == 0) Assert.Ignore(NoGpuMessage);

            int gained = 0, lost = 0;
            var lostDetail = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                bool bandedIsBackground = math.length(At(banded, i) - background) <= Tolerance;
                if (isBackground[i] && !bandedIsBackground) gained++;
                if (!isBackground[i] && bandedIsBackground)
                {
                    lost++;
                    if (lost <= 8)
                        lostDetail.Append($" ({i % SnapPx},{i / SnapPx}) hard={At(hard, i)} banded={At(banded, i)}");
                }
            }

            Assert.Greater(gained, 0,
                $"the band contributed no pixel to a globe frame (ink={ink}). Either it is not emitted on " +
                "the curved arm at all, or it is counter-wound and Cull Back is discarding it — the failure " +
                "mode that leaves every job-level tooth and every digest green.");
            Assert.AreEqual(0, lost,
                "a pixel that carried ink without the band lost it with the band. The band only ever adds " +
                $"coverage OUTSIDE the boundary; anything that removes ink means geometry moved. bg={background}" +
                $" first lost:{lostDetail}");
            Assert.Less(gained, ink / 4,
                $"the band added {gained} px against {ink} px of fill — far too many for a one-pixel rim " +
                "around the silhouettes. That is a geometry shift, not antialiasing.");

            // Reach: every gained pixel sits within a miter of geometry in the band-free build SUPERSAMPLED 8×.
            // Non-obvious why: ANY deviation from background counts here, not the 3-LSB tolerance, because a
            // sub-pixel sliver is what this reference exists to find. A failure reports each offender's
            // separation and its radius, since the limb sits at a known radius.
            Frame superHard = RenderGlobe(pose, suppressBand: true, superSample: 8);
            const double faintest = 0.5 / 255.0;
            var hasGeometry = new bool[n];
            for (int i = 0; i < n; i++)
                hasGeometry[i] = math.length(At(superHard, i) - background) > faintest;

            int reach = (int)FillBandJob.MiterLimit + 1;
            const int probe = 16;
            int worst = 0, offenders = 0;
            var offenderIndices = new List<int>();
            var detail = new System.Text.StringBuilder();
            for (int y = probe; y < SnapPx - probe; y++)
                for (int x = probe; x < SnapPx - probe; x++)
                {
                    int i = y * SnapPx + x;
                    if (!isBackground[i]) continue;
                    if (math.length(At(banded, i) - background) <= Tolerance) continue;

                    int nearest = int.MaxValue;
                    for (int dy = -probe; dy <= probe; dy++)
                        for (int dx = -probe; dx <= probe; dx++)
                            if (hasGeometry[(y + dy) * SnapPx + (x + dx)])
                                nearest = math.min(nearest, math.max(math.abs(dx), math.abs(dy)));
                    if (nearest <= reach) continue;

                    offenders++;
                    offenderIndices.Add(i);
                    worst = math.max(worst, nearest == int.MaxValue ? probe : nearest);
                    if (offenders <= 10)
                        detail.Append($" ({x},{y}) sep={nearest} r={math.length(new double2(x - 256.0, y - 256.0)):F1}");
                }

            // How much band ink lands at the limb, where the px->world differential is least
            // trustworthy. Reported, not asserted — it is a quantity to know, not a contract.
            int nearLimb = 0;
            for (int i = 0; i < n; i++)
                if (isBackground[i] && math.length(At(banded, i) - background) > Tolerance
                    && math.length(new double2(i % SnapPx - 256.0, i / SnapPx - 256.0)) > 220.0)
                    nearLimb++;

            TestContext.WriteLine($"[globe band {poseName}] ink={ink} gained={gained} lost={lost} reach={reach} " +
                                  $"offenders={offenders} worst={worst} gained-near-limb={nearLimb}");

            if (offenders > 0)
            {
                var mask = (Color32[])banded.Pixels.Clone();
                foreach (int i in offenderIndices)
                    mask[i] = new Color32(255, 0, 255, mask[i].a);
                TestContext.WriteLine($"[globe band {poseName}] " +
                    SnapshotRenderer.WritePngFromRgba32(hard, $"band-limb-{poseName}-hard.png") + " " +
                    SnapshotRenderer.WritePngFromRgba32(banded, $"band-limb-{poseName}-banded.png") + " " +
                    SnapshotRenderer.WritePngFromRgba32(new Frame(mask, SnapPx, SnapPx), $"band-limb-{poseName}-offenders.png"));
            }

            Assert.LessOrEqual(offenders, oracleBlindPixels,
                $"{offenders} pixel(s) gained ink from the band more than {reach} px from any geometry in " +
                $"the 8x supersampled band-free reference, above the {oracleBlindPixels} allowed for this " +
                $"oracle's blind spot; worst separation {worst} px. The band reaches at " +
                $"most {FillBandJob.MiterLimit} px along a join bisector, so ink beyond that is a " +
                "displacement that is not the size it claims. The globe's " +
                "silhouette in this fixture has radius 231.2 px " +
                "about (256,256) — an offender at r near that is at the LIMB, where the surface tangent is " +
                $"edge-on and MapPixelsToWorld hits its clamp.{detail}");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldFillSnapshotTests — Headless visual snapshot tests
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Headless snapshot tests: render the world-fill map to an off-screen <c>RenderTexture</c> (so batchmode
    /// needs no display), write PNGs to <c>Logs/snapshots/</c> as readable artefacts with no golden baselines,
    /// and run a tolerant coverage assertion. The blank-render control must FAIL that assertion, so the gate
    /// has teeth. The background is dark slate, not black, so an all-black frame reads as "no GPU context".
    /// </summary>
    [TestFixture]
    public class WorldFillSnapshotTests : BaseTestFixture
    {
        // Snapshot resolution — 512×512 is a good balance of detail vs render time.
        private const int SnapW = 512;
        private const int SnapH = 512;

        // Background: distinctive dark slate (NOT black) so all-black = "no GPU context".
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32  = new Color32(26, 28, 38, 255); // BgColor, byte-quantised

        // Coverage thresholds — wide to be GPU/driver/Unity-tolerant.
        private const float MinFill    = 0.10f; // at least 10% fill pixels
        private const float MaxFill    = 0.85f; // at most 85% fill pixels
        private const int   MinBuckets = 8;     // at least 8 of 64 grid cells hit

        // -----------------------------------------------------------------------------------------
        // Test 1: Render world fill, write PNG, assert coverage passes.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RendersWorldFill_WritesPng_AndPassesCoverage()
        {
            // Build the scene objects.
            var (mapGo, camera, cameraGo) = BuildScene();
            Track(mapGo);
            Track(cameraGo);
            using var snap = new SnapshotRenderer(SnapW, SnapH);

            snap.Render(camera);

            // Write the world-fill PNG — this is the human/agent-readable artefact.
            string pngPath = snap.WritePng("world-fill.png");
            FileAssert.Exists(pngPath);
            var fi = new FileInfo(pngPath);
            Assert.That(fi.Length, Is.GreaterThan(500L),
                "PNG file must be non-trivial (> 500 bytes); a 0-byte or header-only file " +
                "indicates an encode failure.");
            Debug.Log($"[SnapshotTest] World-fill PNG written: {pngPath} ({fi.Length} bytes)");

            // Run the tolerant coverage assertion.
            Frame pixels = snap.Pixels;
            SnapshotVerdict verdict = SnapshotCoverage.Analyse(pixels, Bg32);

            Debug.Log(
                $"[SnapshotTest] World-fill coverage: filled={verdict.FilledFraction:P1}, " +
                $"bg={verdict.BackgroundFraction:P1}, buckets={verdict.DistinctRegionBucketsHit}/64, " +
                $"isBlank={verdict.IsBlank}, isUniform={verdict.IsUniform}");

            Assert.IsFalse(verdict.IsBlank,
                "World-fill render must not be detected as blank. " +
                "A blank result means the mesh was not built or the camera does not frame it.");
            Assert.IsFalse(verdict.IsUniform,
                "World-fill render must not be uniform. " +
                "A uniform result means the fill covers the entire frame or something is wrong.");
            Assert.That(verdict.FilledFraction,
                Is.InRange(MinFill, MaxFill),
                $"Fill fraction {verdict.FilledFraction:P1} must be in [{MinFill:P0}, {MaxFill:P0}]. " +
                "If the fill is outside this band the camera may not frame the map correctly, " +
                "or the mesh was not built.");
            Assert.That(verdict.DistinctRegionBucketsHit,
                Is.GreaterThanOrEqualTo(MinBuckets),
                $"Expected >= {MinBuckets} grid buckets hit, got {verdict.DistinctRegionBucketsHit}. " +
                "The fill must be spatially spread across the frame, not concentrated in one corner.");
            Assert.IsTrue(verdict.Passes(MinFill, MaxFill, MinBuckets),
                "World-fill render must pass the overall coverage gate.");
        }

        // -----------------------------------------------------------------------------------------
        // Test 2: Blank render (no map object) must FAIL coverage — gate has teeth.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void BlankRender_FailsCoverage()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            using var snap = new SnapshotRenderer(SnapW, SnapH);

            snap.Render(camera);

            // Write the blank-control PNG for inspection.
            string pngPath = snap.WritePng("blank-control.png");
            FileAssert.Exists(pngPath);
            Debug.Log($"[SnapshotTest] Blank-control PNG written: {pngPath}");

            Frame pixels = snap.Pixels;
            SnapshotVerdict verdict = SnapshotCoverage.Analyse(pixels, Bg32);

            Debug.Log(
                $"[SnapshotTest] Blank-control coverage: filled={verdict.FilledFraction:P1}, " +
                $"bg={verdict.BackgroundFraction:P1}, isBlank={verdict.IsBlank}");

            // This is the negative control: a camera with no map should render only the
            // background colour → IsBlank == true, Passes() == false.
            Assert.IsTrue(verdict.IsBlank,
                $"Blank render (no map) must be detected as blank " +
                $"(bg fraction={verdict.BackgroundFraction:P1}, filled={verdict.FilledFraction:P1}). " +
                "If this fails, there is an unexpected object in the scene or the background colour " +
                "is being misread.");
            Assert.IsFalse(verdict.Passes(MinFill, MaxFill, MinBuckets),
                "Blank render must fail the coverage gate — this validates that the gate has teeth " +
                "and cannot be trivially fooled by an empty render.");
        }

        // -----------------------------------------------------------------------------------------
        // Scene helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a camera GameObject: top-down orthographic, solid-colour clear with the dark-slate
        /// background.
        /// </summary>
        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("SnapshotCamera");
            var camera = go.AddComponent<Camera>();

            // Top-down orthographic.
            camera.transform.position = new Vector3(0f, 200f, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = 70f;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;

            // Disable in-scene cameras so they don't interfere.
            camera.enabled = false;

            return (go, camera);
        }

        /// <summary>
        /// Builds the fill scene: FillSceneHelper + camera + directional light, ready to render.
        /// Returns (mapGameObject, camera, cameraGameObject). Caller must destroy both GOs.
        ///
        /// Uses FillSceneHelper (StyledFillTileBuilder-backed).
        /// Directional light: required because Map/Fill (URP Lit) renders near-black
        /// at ambient-only.
        /// </summary>
        private static (GameObject mapGo, Camera camera, GameObject cameraGo) BuildScene()
        {
            var (cameraGo, camera) = BuildCamera();

            // Add a directional light so the Lit material renders brightly enough for coverage.
            var lightGo = new GameObject("SceneLight");
            var light   = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            // Build the map fill via FillSceneHelper (StyledFillTileBuilder + fixture tile).
            var (mapGo, _) = FillSceneHelper.BuildFillGo(viewSize: 100f);

            // Attach the light to mapGo for unified cleanup (caller destroys mapGo + cameraGo).
            lightGo.transform.SetParent(mapGo.transform);

            return (mapGo, camera, cameraGo);
        }

    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillTranslateSnapshotTests — fill-translate as a real SCREEN-PIXEL offset
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>fill-translate</c> as a real SCREEN-PIXEL offset, with the spec's sign ("negatives indicate left and
    /// up", so +y is south on a north-up map), and <c>fill-translate-anchor</c> as a real branch.
    /// <see cref="Translate_DisplacesTheSameScreenDistance_AtDifferentZooms"/> discriminates: a world-unit
    /// offset moves half as many pixels when the view covers twice the world. Non-obvious why:
    /// <see cref="SnapshotRenderer.Pixels"/> has a BOTTOM-left origin, so NORTH (the image top) is a HIGH row.
    /// </summary>
    [TestFixture]
    public class FillTranslateSnapshotTests
    {
        private const int   SnapW = 512;
        private const int   SnapH = 512;
        private const float CamY  = 200f;

        // FillSceneHelper fits the fixture into 100 world units; these both frame it with margin and differ
        // by exactly 2× in world-units-per-pixel — the ratio the zoom-invariance tooth turns on.
        private const float NearOrtho = 70f;
        private const float FarOrtho  = 140f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

        private static (GameObject go, Camera camera) BuildCamera(float orthoSize, float yawDegrees = 0f)
        {
            var go     = new GameObject("FillTranslateSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            // Pitch 90° looks straight down; yaw rotates the view about the world up axis (map bearing).
            camera.transform.rotation = Quaternion.Euler(90f, yawDegrees, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = orthoSize;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        /// <summary>Centroid (column, row) of the non-background pixels; column grows RIGHT, row grows UP.
        /// Returns false when nothing rendered. <paramref name="touchesBorder"/> is load-bearing: a centroid
        /// tracks a rigid translation only while the shape is INSIDE the frame, as a clipped fill trades pixels
        /// across the edge and barely moves. Every caller asserts it rather than trusting the framing.</summary>
        private static bool TryCentroid(Frame frame, out Vector2 centroid, out bool touchesBorder)
        {
            double sumX = 0, sumY = 0;
            int count = 0;
            touchesBorder = false;
            for (int row = 0; row < SnapH; row++)
            for (int col = 0; col < SnapW; col++)
            {
                Color32 px = frame[col, row];
                int dr = px.r - Bg32.r, dg = px.g - Bg32.g, db = px.b - Bg32.b;
                if (Mathf.Abs(dr) + Mathf.Abs(dg) + Mathf.Abs(db) <= SnapshotCoverage.Tolerance) continue;
                sumX += col; sumY += row; count++;
                if (row == 0 || col == 0 || row == SnapH - 1 || col == SnapW - 1) touchesBorder = true;
            }
            centroid = count > 0 ? new Vector2((float)(sumX / count), (float)(sumY / count)) : Vector2.zero;
            return count > 0;
        }

        /// <summary>Renders the fixture fill at <paramref name="orthoSize"/>/<paramref name="yaw"/> with the
        /// given translate, and returns the rendered centroid. Inconclusive when nothing rendered (no GPU).</summary>
        private static Vector2 CentroidWithTranslate(float orthoSize, Vector2 translatePx, float anchor,
                                                     float yaw, string pngName)
        {
            using var bag = new ObjectDisposalBag();
            var (mapGo, mat)    = FillSceneHelper.BuildFillGo();
            bag.Track(mapGo);
            var (camGo, camera) = BuildCamera(orthoSize, yaw);
            bag.Track(camGo);
            mat.SetVector(FillShaderProps.PropertyId.FillTranslate,
                new Vector4(translatePx.x, translatePx.y, 0f, 0f));
            mat.SetFloat(FillShaderProps.PropertyId.FillTranslateAnchor, anchor);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng(pngName);

            TryCentroid(snap.Pixels, out Vector2 centroid, out bool touchesBorder);
            Assert.IsFalse(touchesBorder,
                $"{pngName}: the fill must be fully INSIDE the frame for its centroid to track a rigid " +
                "translation. Touching a border means it is clipped, and the measured displacement " +
                "understates the real one — widen orthographicSize.");
            return centroid;
        }

        // ── THE tooth: a screen-pixel offset is invariant to camera scale ────────────────────────

        [Test]
        public void Translate_DisplacesTheSameScreenDistance_AtDifferentZooms()
        {
            const float translatePx = 40f;

            // The mesh spans 100 world units, so orthographicSize must exceed ~50 to keep the fill in frame
            // (TryCentroid's border guard); NearOrtho and FarOrtho both do.
            Vector2 nearBase  = CentroidWithTranslate(NearOrtho, Vector2.zero,                0f, 0f, "fill-translate-near-base.png");
            Vector2 nearMoved = CentroidWithTranslate(NearOrtho, new Vector2(translatePx, 0), 0f, 0f, "fill-translate-near-moved.png");
            Vector2 farBase   = CentroidWithTranslate(FarOrtho,  Vector2.zero,                0f, 0f, "fill-translate-far-base.png");
            Vector2 farMoved  = CentroidWithTranslate(FarOrtho,  new Vector2(translatePx, 0), 0f, 0f, "fill-translate-far-moved.png");

            float nearShift = nearMoved.x - nearBase.x;
            float farShift  = farMoved.x  - farBase.x;
            Debug.Log($"[FillTranslateSnapshotTests] shift near={nearShift:F1}px far={farShift:F1}px");

            Assert.Greater(Mathf.Abs(nearShift), 5f,
                "precondition: a 40 px translate must visibly move the fill, or the comparison is vacuous.");

            // Screen-pixel semantics ⇒ equal pixel shift at both scales. World-unit semantics (the
            // behaviour) would make farShift half of nearShift, since the far camera covers 2× the world.
            Assert.AreEqual(nearShift, farShift, 4f,
                $"a screen-pixel fill-translate must displace by the SAME pixel count at any camera scale " +
                $"(near {nearShift:F1}px vs far {farShift:F1}px). A ~2× ratio means the offset is still being " +
                "applied in world units.");

            // And it must actually be the requested magnitude, not merely scale-invariant.
            Assert.AreEqual(translatePx, Mathf.Abs(nearShift), 8f,
                $"a {translatePx} px translate must displace by ~{translatePx} px; got {Mathf.Abs(nearShift):F1}.");
        }

        // ── Sign: the spec says +y is DOWN ───────────────────────────────────────────────────────

        [Test]
        public void PositiveY_MovesGeometryDownTheScreen_PerSpecNegativesAreUp()
        {
            Vector2 baseline = CentroidWithTranslate(NearOrtho, Vector2.zero,        0f, 0f, "fill-translate-sign-base.png");
            Vector2 movedY   = CentroidWithTranslate(NearOrtho, new Vector2(0, 40f), 0f, 0f, "fill-translate-sign-down.png");

            float rowShift = movedY.y - baseline.y; // rows grow UPWARD (bottom-left origin)
            Debug.Log($"[FillTranslateSnapshotTests] +y row shift = {rowShift:F1} (negative = down-screen)");

            Assert.Less(rowShift, -5f,
                "MapLibre's fill-translate spec states \"negatives indicate left and up\", so a POSITIVE y " +
                "must move the geometry DOWN the screen (south on a north-up map). In this bottom-left-origin " +
                "raster that means the centroid row must DECREASE. A positive shift here means the " +
                "north/south sign is inverted.");
        }

        // ── Anchor: map rides the bearing, viewport does not ─────────────────────────────────────

        [Test]
        public void Anchor_MapRotatesWithBearing_ViewportDoesNot()
        {
            const float yaw = 90f;
            var offset = new Vector2(40f, 0f);

            // Under a 90° yaw the map's east axis is no longer screen-right, so a MAP-anchored offset must
            // land on a different screen axis than a VIEWPORT-anchored one.
            Vector2 mapBase  = CentroidWithTranslate(NearOrtho, Vector2.zero, 0f, yaw, "fill-translate-anchor-map-base.png");
            Vector2 mapMoved = CentroidWithTranslate(NearOrtho, offset,       0f, yaw, "fill-translate-anchor-map.png");
            Vector2 viewBase = CentroidWithTranslate(NearOrtho, Vector2.zero, 1f, yaw, "fill-translate-anchor-view-base.png");
            Vector2 viewMoved= CentroidWithTranslate(NearOrtho, offset,       1f, yaw, "fill-translate-anchor-view.png");

            Vector2 mapShift  = mapMoved  - mapBase;
            Vector2 viewShift = viewMoved - viewBase;
            Debug.Log($"[FillTranslateSnapshotTests] yaw={yaw}° map shift={mapShift} viewport shift={viewShift}");

            // Viewport is screen-locked: +x is screen-right whatever the bearing.
            Assert.Greater(viewShift.x, 5f,
                "a VIEWPORT-anchored +x offset must move the geometry screen-RIGHT regardless of bearing.");
            Assert.Less(Mathf.Abs(viewShift.y), Mathf.Abs(viewShift.x) * 0.5f,
                "a viewport-anchored +x offset must be predominantly horizontal on screen.");

            // Map is bearing-locked: at 90° yaw the map's east axis projects onto the screen's vertical.
            Assert.Greater(mapShift.magnitude, 5f, "a MAP-anchored offset must still move the geometry.");
            Assert.Less(Mathf.Abs(mapShift.x), Mathf.Abs(viewShift.x) * 0.5f,
                "under a 90° bearing a MAP-anchored +x offset must NOT stay screen-horizontal — if it moves " +
                "the same way the viewport-anchored one does, fill-translate-anchor is being ignored.");
        }
    }
}
