using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;
using FillShaderProps = MapRenderer.Unity.Rendering.ShaderProperties.Fill;
using FillStyle       = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Measures what a pattern fill actually puts on screen, for a single tile under a top-down camera.
    /// Written as a diagnostic while chasing a maintainer-reported pattern artefact — every theory at the
    /// time was speculation, and this replaced it with a count.
    ///
    /// <para>Answers the question the eye conflates with everything else: does the pattern actually TILE, or
    /// is one sprite stretched across the fill because <c>_PatternScale</c> never reached the shader? That
    /// distinction is invisible in a description and decides which bug you are looking at.</para>
    ///
    /// <para>Uses a sprite whose two halves differ sharply, so counting transitions along a scanline counts
    /// repetitions directly — no autocorrelation guesswork.</para>
    /// </summary>
    [TestFixture]
    public class FillPatternPeriodDiagnostic
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;

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
            var camGo = new GameObject("PatternDiagCam");
            var camera = camGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 200f, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = 70f;   // frames the 100-unit fixture fill
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;

            Texture2D sheet = BuildTwoToneSheet();
            try
            {
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
                        if (!IsBackground(snap.RawPixels, row, col)) cover++;
                    if (cover > bestCover) { bestCover = cover; bestRow = row; }
                }

                if (bestRow < 0 || bestCover < 32)
                {
                    Debug.Log("[PatternDiag] nothing rendered — GPU/shader context unavailable");
                    Assert.Inconclusive("no geometry rendered");
                }

                int redRuns = 0, blueRuns = 0, transitions = 0;
                int prev = 0; // 0 = bg, 1 = red-ish, 2 = blue-ish
                for (int col = 0; col < SnapW; col++)
                {
                    int cls = Classify(snap.RawPixels, bestRow, col);
                    if (cls != 0 && cls != prev && prev != 0) transitions++;
                    if (cls == 1 && prev != 1) redRuns++;
                    if (cls == 2 && prev != 2) blueRuns++;
                    prev = cls;
                }

                Debug.Log($"[PatternDiag] row={bestRow} covered={bestCover}px  " +
                          $"redRuns={redRuns} blueRuns={blueRuns} transitions={transitions}  " +
                          $"repeatsAcrossTile={repeatsAcrossTile}");

                // Exact counts are not assertable: the fixture's polygon is not convex, so a scanline crosses
                // it in several disjoint spans and each break splits a run. What IS assertable is that the
                // pattern TILES — several alternations of both colours. One run of each would mean the sprite
                // is stretched across the whole fill (i.e. _PatternScale never reached the shader), and zero
                // would mean a flat colour (sampling a single texel).
                Assert.GreaterOrEqual(redRuns, 2,
                    $"the pattern must repeat, not stretch — got {redRuns} red run(s). 1 means _PatternScale " +
                    "is not reaching the shader; 0 means the sample is a flat colour.");
                Assert.GreaterOrEqual(blueRuns, 2,
                    $"both halves of the sprite must recur — got {blueRuns} blue run(s).");
            }
            finally
            {
                Object.DestroyImmediate(sheet);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        private static bool IsBackground(byte[] px, int row, int col)
        {
            int b = (row * SnapW + col) * 4;
            return Mathf.Abs(px[b] - BgR8) + Mathf.Abs(px[b + 1] - BgG8) + Mathf.Abs(px[b + 2] - BgB8)
                   <= SnapshotCoverage.Tolerance;
        }

        private static int Classify(byte[] px, int row, int col)
        {
            if (IsBackground(px, row, col)) return 0;
            int b = (row * SnapW + col) * 4;
            return px[b] >= px[b + 2] ? 1 : 2; // more red than blue ⇒ red half, else blue half
        }
    }
}
