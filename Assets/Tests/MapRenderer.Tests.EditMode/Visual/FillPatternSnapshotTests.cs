// Namespace-collision guard: `using Unity.Mathematics;` + bare `int2` is REQUIRED. Inline as
// `Unity.Mathematics.int2` the leading `Unity` segment binds to `MapRenderer.Unity` (reachable from this
// file's `MapRenderer.Tests.Visual` namespace), not the global `Unity` root — CS0234.
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;
// Explicit aliases, not a bare `using` of the parent namespace: `Fill` names a NAMESPACE on both the
// shader-property side and the style side, and the codebase idiom is to alias rather than rely on
// nested-namespace lookup (see MaterialFactory's `using Fill = MapRenderer.Core.Style.Fill;`).
using FillShaderProps = MapRenderer.Unity.Rendering.ShaderProperties.Fill;
using FillStyle       = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// P2 acceptance — the black-region fix, at the pixel level.
    ///
    /// <para><b>The defect.</b> A <c>fill-pattern</c> layer characteristically declares no
    /// <c>fill-color</c>, so it inherits the spec default <c>rgba(0,0,0,1)</c>, which
    /// <c>StyledFillTileBuilder</c> bakes per-feature into the COLOR stream. Before this stage nothing
    /// consulted the pattern at all, so those layers painted solid opaque black — Liberty's
    /// <c>road_area_pattern</c> plazas and <c>landcover_wetland</c>. See docs/fill-parity-design.md §1.</para>
    ///
    /// <para><b>RED-verify.</b> <see cref="UnresolvedPattern_PaintsNothing"/> fails on the pre-P2 tree:
    /// <c>_FillPattern</c> was a declared-but-unread uniform, so setting it changed nothing and the black
    /// vertex colour reached the framebuffer. The assertion is on the BACKGROUND fraction, so a shader that
    /// ignores the pattern cannot pass it by accident.</para>
    ///
    /// <para>These are the engine-side teeth; the resolve arithmetic they depend on is pinned engine-free by
    /// <c>FillPatternTests</c>.</para>
    ///
    /// Camera + background match the sibling fill snapshot fixtures (top-down ortho 512², dark slate).
    /// </summary>
    [TestFixture]
    public class FillPatternSnapshotTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;

        // Opaque black — what a fill-pattern layer's fill-color defaults to, and the colour that used to
        // paint these regions. Baking it here is what makes the tooth faithful rather than modelled.
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
            var (camGo, camera) = BuildCamera();
            try
            {
                // Baseline: the SAME geometry with no pattern declared. This is the pre-fix rendering, and
                // it is what proves the geometry is actually on screen — without it, "no black pixels" would
                // also pass on an empty render.
                mat.SetFloat(FillShaderProps.PropertyId.FillPattern, 0f);
                using var solid = new SnapshotRenderer(SnapW, SnapH);
                solid.Render(camera);
                solid.WritePng("fill-pattern-black-baseline.png");

                var solidVerdict = SnapshotCoverage.Analyse(solid.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                if (solidVerdict.IsBlank)
                    Assert.Inconclusive("No geometry rendered — GPU/shader context unavailable in this run.");
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

                var verdict = SnapshotCoverage.Analyse(unresolved.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                Debug.Log($"[FillPatternSnapshotTests] solid filled={solidVerdict.FilledFraction:P2}, " +
                          $"unresolved filled={verdict.FilledFraction:P2}");

                Assert.Greater(verdict.BackgroundFraction, 0.99f,
                    "An unresolved fill-pattern layer must paint NOTHING — the background must be intact. " +
                    $"Got {verdict.BackgroundFraction:P2} background / {verdict.FilledFraction:P2} filled. " +
                    "A failure here means the layer is painting fill-color's opaque-black default again " +
                    "(the original black-regions defect).");
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        // ── The positive half: a resolved pattern samples the SHEET, not fill-color ──────────────

        [Test]
        public void ResolvedPattern_SamplesTheSheetInsteadOfFillColor()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillColorExpression: OpaqueBlack);
            var (camGo, camera) = BuildCamera();
            Texture2D sheet = BuildSolidSheet(Color.green);
            try
            {
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

                var verdict = SnapshotCoverage.Analyse(snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                if (verdict.IsBlank)
                    Assert.Inconclusive("No geometry rendered — GPU/shader context unavailable in this run.");

                Assert.Greater(verdict.FilledFraction, 0.02f,
                    "a RESOLVED pattern must paint — this is the other side of the clip.");

                // The sprite is green; fill-color is black. Green dominance proves the sprite REPLACED the
                // colour rather than tinting or being ignored. (Lit shading scales magnitude, not hue order.)
                double meanG = 0, meanR = 0, meanB = 0;
                int n = 0;
                for (int i = 0; i < SnapW * SnapH; i++)
                {
                    int b = i * 4;
                    int dr = snap.RawPixels[b] - BgR8, dg = snap.RawPixels[b + 1] - BgG8, db = snap.RawPixels[b + 2] - BgB8;
                    if (Mathf.Abs(dr) + Mathf.Abs(dg) + Mathf.Abs(db) <= SnapshotCoverage.Tolerance) continue;
                    meanR += snap.RawPixels[b]; meanG += snap.RawPixels[b + 1]; meanB += snap.RawPixels[b + 2];
                    n++;
                }
                Assert.Greater(n, 0, "expected non-background pixels to average");
                meanR /= n; meanG /= n; meanB /= n;
                Debug.Log($"[FillPatternSnapshotTests] resolved mean RGB = ({meanR:F1}, {meanG:F1}, {meanB:F1})");

                Assert.Greater(meanG, meanR + 10.0,
                    $"the green sprite must dominate the black fill-color (mean G={meanG:F1} vs R={meanR:F1}) — " +
                    "if they match, the pattern is being ignored and fill-color is still painting.");
                Assert.Greater(meanG, meanB + 10.0,
                    $"mean G={meanG:F1} must exceed mean B={meanB:F1} for a green sprite.");
            }
            finally
            {
                Object.DestroyImmediate(sheet);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        // ── The sprite's ALPHA must reach the framebuffer, not just its RGB ──────────────────────

        [Test]
        public void PatternWithTransparentTexels_ShowsBackgroundThrough()
        {
            // A fill-pattern sprite is typically an alpha-masked overlay (Liberty's wetland hatch is mostly
            // transparent), so discarding its alpha renders a solid block instead of a pattern. This failed
            // before fills declared _SURFACE_TYPE_TRANSPARENT: URP's OutputAlpha() forces alpha to 1 on an
            // opaque surface, so the mask never reached the blend unit however correct the sampling was.
            var (mapGo, mat)    = FillSceneHelper.BuildFillGo(fillColorExpression: OpaqueBlack);
            var (camGo, camera) = BuildCamera();
            Texture2D sheet = BuildSolidSheet(new Color(0f, 1f, 0f, 0f)); // green, FULLY transparent
            try
            {
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

                var verdict = SnapshotCoverage.Analyse(snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                Debug.Log($"[FillPatternSnapshotTests] fully-transparent sprite → filled " +
                          $"{verdict.FilledFraction:P2}");

                Assert.Greater(verdict.BackgroundFraction, 0.99f,
                    "a fully TRANSPARENT pattern sprite must leave the background intact — the sprite's " +
                    $"alpha must reach the blend unit. Got {verdict.FilledFraction:P2} filled, which means " +
                    "alpha is being discarded and every pattern renders as a solid block.");
            }
            finally
            {
                Object.DestroyImmediate(sheet);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        // ── §2.2's claim, made falsifiable: resolving late does NOT re-mesh ──────────────────────

        [Test]
        public void ResolvingTheSheetLate_DoesNotRebuildTheMesh()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillColorExpression: OpaqueBlack);
            Texture2D sheet = BuildSolidSheet(Color.green);
            try
            {
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

                // The whole reason late resolve is cheap: the mesh already carries tile-normalized UVs in
                // stream 1, so nothing about the geometry depends on the sprite. If this ever fails, the
                // "pure material-uniform change" claim in docs/fill-parity-design.md §2.2 is void and the
                // resolve path needs a tile rebuild.
                Assert.AreSame(before, filter.sharedMesh,
                    "resolving a pattern must not replace the Mesh instance (no re-mesh).");
                Assert.AreEqual(vertsBefore, filter.sharedMesh.vertexCount,
                    "resolving a pattern must not change vertex count (no re-mesh).");
                Assert.Greater(before.uv.Length, 0,
                    "the fill mesh must carry TEXCOORD0 — the pattern space the shader tiles in.");
            }
            finally
            {
                Object.DestroyImmediate(sheet);
                Object.DestroyImmediate(mapGo);
            }
        }
    }
}
