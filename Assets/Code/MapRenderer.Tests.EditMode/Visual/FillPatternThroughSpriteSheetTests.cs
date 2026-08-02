// Namespace-collision guard: `using Unity.Mathematics;` + bare `int2` is REQUIRED. Inline as
// `Unity.Mathematics.int2` the leading `Unity` segment binds to `MapRenderer.Unity` (reachable from this
// file's `MapRenderer.Tests.Visual` namespace), not the global `Unity` root — CS0234.
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Unity.Text;
using FillShaderProps = MapRenderer.Unity.Rendering.ShaderProperties.Fill;
using FillStyle       = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// U4 — a <c>fill-pattern</c> resolved through the REAL <see cref="SpriteSheet"/> never samples outside
    /// its own content rect.
    ///
    /// <para>This is the gap <c>FillPatternSnapshotTests</c> leaves: that fixture builds its own two-texel
    /// <c>FilterMode.Point</c> texture and never touches <see cref="SpriteSheet"/>, so nothing there sees the
    /// padded repack at all. Patterns are the consumer with the most to lose from it — they wrap with
    /// <c>frac()</c> INSIDE the rect the index reports, so the moment <see cref="SpriteEntry"/>'s rect stops
    /// meaning "the content" and starts meaning "the padded cell", every tiling seam samples the transparent
    /// border and the pattern develops holes.</para>
    ///
    /// <para>Two failures are therefore watched at once: a texel of the NEIGHBOURING sprite's hue (the
    /// sampler reached across the cell) and a TRANSPARENT texel (the sampler reached into the border). Both
    /// are impossible while the rect is the content rect and the pattern is point-sampled inside it.</para>
    /// </summary>
    [TestFixture]
    public class FillPatternThroughSpriteSheetTests
    {
        private const int SnapW = 512;
        private const int SnapH = 512;
        private const float OrthoSz = 70f;
        private const float CamY = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;

        private const string OpaqueBlack = "[\"rgba\",0,0,0,1]";
        private const double DiagnosticZoom = 14.0;

        /// <summary>Repeats forced high enough that many tiling seams land inside the frame — one seam would
        /// be a weak probe, since a border tap only shows up AT a seam.</summary>
        private const double MinimumRepeats = 8.0;

        [Test]
        public void PatternThroughARepackedSheet_NeverSamplesTheBorderOrTheNeighbour()
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(fillColorExpression: OpaqueBlack);
            var (camGo, camera) = BuildCamera();
            SpriteSheet sheet = null;
            try
            {
                // Baseline first: the SAME geometry painted with the opaque-black fill colour, i.e. full
                // coverage. Without it "no transparent pixels" would also pass on an empty render.
                mat.SetFloat(FillShaderProps.PropertyId.FillPattern, 0f);
                using var opaque = new SnapshotRenderer(SnapW, SnapH);
                opaque.Render(camera);
                var baseline = SnapshotCoverage.Analyse(opaque.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                if (baseline.IsBlank)
                    Assert.Inconclusive("No geometry rendered — GPU/shader context unavailable in this run.");
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

                var verdict = SnapshotCoverage.Analyse(patterned.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                int bled = 0;
                for (int i = 0; i < SnapW * SnapH; i++)
                {
                    int b = i * 4;
                    int dr = patterned.RawPixels[b] - BgR8;
                    int dg = patterned.RawPixels[b + 1] - BgG8;
                    int db = patterned.RawPixels[b + 2] - BgB8;
                    if (Mathf.Abs(dr) + Mathf.Abs(dg) + Mathf.Abs(db) <= SnapshotCoverage.Tolerance)
                        continue; // background — not painted by the pattern at all
                    if (patterned.RawPixels[b + 2] > patterned.RawPixels[b] + 24)
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
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        /// <summary>The world span <c>FillSceneHelper</c> fits the fixture mesh into (its
        /// <c>DefaultViewSize</c>), used to derive a period that yields many seams on screen.</summary>
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
}
