// Unity EditMode only — off-screen GPU renders of the REAL icon draw path. NOT registered in
// core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D/SpriteSheet).
//
// These are the teeth the icon path was owed for RESAMPLING — how the sprite sheet's texels are mapped onto
// device pixels. Every pre-existing icon test renders ONE static frame, so none of them can see a defect
// whose whole signature is "the render changes when it should not". That is why the bug below shipped.
//
// The reported symptom was "pixels inside the icon warp while zooming/panning". The geometry cannot produce
// that: BillboardMath.BuildWorldQuad gives all four corners the SAME bitwise anchorLocal plus static
// per-corner OffsetPx, so a quad is RIGID in screen space — an anchor precision error TRANSLATES an icon and
// can never deform its interior. Interior deformation therefore has to be resampling, and it was:
// SpriteSheet bound the sheet with FilterMode.Point.
//
// Nearest-neighbour is exact only at INTEGER magnification. An icon's magnification is
// `iconSize * dpr / pixelRatio` — the sheet is always fetched @1x and dpr is Screen.dpi/160, so it is
// essentially never an integer. At a non-integer magnification each source texel covers either N or N+1
// device pixels, and WHICH depends on the quad's sub-pixel phase, so panning re-quantises the icon's
// interior every frame. Note the trap in that: at exactly 1x, Point sampling is a pixel-perfect blit, so the
// defect is INVISIBLE at the one setting anyone would eyeball first.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolIconResamplingTests
    {
        private const int Size = 256;
        private const int SheetSize = 64;

        /// <summary>
        /// Magnification (device px per source texel) the phase sweep renders at. Deliberately NON-INTEGER —
        /// at an integer magnification nearest-neighbour is exact and the defect under test does not exist.
        /// 2.5 is chosen over, say, 1.4 because it separates the two filters furthest: see
        /// <see cref="MaxCentroidDeviationPx"/>.
        /// </summary>
        private const float Magnification = 2.5f;

        /// <summary>Phase step of the sweep, device px. The sweep spans one FULL device pixel — a partial
        /// sweep could sit entirely inside one tread of the nearest-neighbour staircase and read smooth.</summary>
        private const float PhaseStepPx = 0.125f;

        /// <summary>
        /// The PRIMARY tooth, in device px: the least the ink is allowed to advance for one
        /// <see cref="PhaseStepPx"/> of quad shift. Set to half the ideal step, so it states "the icon moves
        /// when the map moves" with a wide tolerance on HOW MUCH.
        ///
        /// <para>Chosen over a deviation-from-ramp bound (kept below as a secondary check) because it
        /// separates the two filters by an order of magnitude rather than a factor of 1.4. Nearest-neighbour
        /// does not move the ink AT ALL until the phase crosses a texel boundary, so its worst step is
        /// exactly <c>0.000</c> px — measured, against the un-fixed tree: the centroid sat on 129.000 px for
        /// three consecutive phases. Bilinear advances ~0.125 px every step.</para>
        /// </summary>
        private const float MinCentroidStepPx = 0.5f * PhaseStepPx;

        /// <summary>
        /// Secondary bound, device px: worst deviation of the measured centroid from the ideal ramp. Weaker
        /// than <see cref="MinCentroidStepPx"/> — measured at 0.2125 px against the un-fixed tree, so it
        /// clears this bound by only 1.4x. It is retained because it catches a defect the step test cannot:
        /// ink that advances smoothly but at the WRONG RATE.
        /// </summary>
        private const float MaxCentroidDeviationPx = 0.15f;

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth 1 — sub-pixel phase stability.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void IconInterior_TracksSubPixelPhaseSmoothly_DoesNotSnapToTheTexelGrid()
        {
            // A ONE-TEXEL-wide opaque column is the sharpest probe available: at a non-integer magnification
            // nearest-neighbour renders it as either floor(M) or floor(M)+1 device px wide depending on
            // phase, so its centroid can only sit on the destination pixel grid.
            var index = SpriteIndex.Parse(
                $"{{\"stripe\":{{\"x\":0,\"y\":0,\"width\":{SheetSize},\"height\":{SheetSize},\"pixelRatio\":1}}}}");
            var sheet = new SpriteSheet(BuildStripeSheetPng(), index);
            GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            try
            {
                AssertSheetDecodedAsAuthored(sheet);
                Assert.IsTrue(index.TryGetSprite("stripe", out SpriteEntry entry));

                var phasesPx = new float[8];
                for (int i = 0; i < phasesPx.Length; i++) phasesPx[i] = i * PhaseStepPx;
                var centroids = new float[phasesPx.Length];

                for (int i = 0; i < phasesPx.Length; i++)
                {
                    // IconQuadLayout multiplies icon-offset by icon-size, so pre-divide to land on an exact
                    // device-px shift. The fixture's dpr is 1 (asserted below), so logical px ARE device px.
                    float2 offset = new float2(phasesPx[i] / Magnification, 0f);
                    SymbolQuad quad = IconQuadLayout.Layout(
                        entry, sheet.View.Size, Magnification, MapRenderer.Core.Text.TextAnchor.Center, offset);
                    centroids[i] = RenderAndMeasureInkCentroidX(sheet, glyphAtlas, quad, "stripe");
                }

                // Ideal: the ink's centroid advances by exactly the phase shift. Fit is not needed — the
                // slope is known to be 1 — so compare against the ramp anchored at the sweep's own mean,
                // which removes the arbitrary absolute position of the anchor's projection.
                float meanCentroid = 0f, meanPhase = 0f;
                for (int i = 0; i < phasesPx.Length; i++) { meanCentroid += centroids[i]; meanPhase += phasesPx[i]; }
                meanCentroid /= phasesPx.Length;
                meanPhase /= phasesPx.Length;

                float worst = 0f;
                int worstAt = 0;
                float smallestStep = float.MaxValue;
                int smallestStepAt = 0;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float expected = meanCentroid + (phasesPx[i] - meanPhase);
                    float deviation = math.abs(centroids[i] - expected);
                    float step = i == 0 ? float.NaN : centroids[i] - centroids[i - 1];
                    report.Append($"\n  phase {phasesPx[i]:F3}px → centroid {centroids[i]:F4}px " +
                                  $"(expected {expected:F4}, off by {deviation:F4}" +
                                  (i == 0 ? ")" : $", step {step:+0.0000;-0.0000})"));
                    if (deviation > worst) { worst = deviation; worstAt = i; }
                    if (i > 0 && step < smallestStep) { smallestStep = step; smallestStepAt = i; }
                }

                // Record the sweep unconditionally, not only on failure: the PASSING margins are what tell
                // the next person whether these bounds are comfortable or a flake waiting for a different
                // GPU, and they cost nothing to keep in the results XML.
                TestContext.Out.WriteLine($"phase sweep (magnification {Magnification}):{report}");
                TestContext.Out.WriteLine(
                    $"  smallest step {smallestStep:F4}px (bound {MinCentroidStepPx}, ideal {PhaseStepPx}) | " +
                    $"worst ramp deviation {worst:F4}px (bound {MaxCentroidDeviationPx})");

                // PRIMARY: the ink must MOVE for every sub-pixel step. A zero step is the nearest-neighbour
                // staircase's tread — the icon frozen against a map that is still panning.
                Assert.Greater(smallestStep, MinCentroidStepPx,
                    $"the icon's interior must ADVANCE for every sub-pixel shift of the quad. Smallest " +
                    $"advance was {smallestStep:F4}px at phase {phasesPx[smallestStepAt]:F3}px (bound " +
                    $"{MinCentroidStepPx}px, ideal {PhaseStepPx}px). A step at or near ZERO means the ink " +
                    $"snapped to the sheet's texel grid and held still, then jumped — which on screen is the " +
                    $"icon's interior warping as the map pans.{report}");

                // SECONDARY: smooth but at the wrong rate — a defect the step bound above cannot see.
                Assert.Less(worst, MaxCentroidDeviationPx,
                    $"the icon's ink must advance at the SAME rate as the quad, not merely advance. Worst " +
                    $"deviation from the ideal ramp {worst:F4}px at phase {phasesPx[worstAt]:F3}px " +
                    $"(bound {MaxCentroidDeviationPx}px).{report}");
            }
            finally
            {
                glyphAtlas.Dispose();
                sheet.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth 2 — the atlas-bleed guard.
        //
        // This one is GREEN under nearest-neighbour and exists to fence the FIX: a bare switch to bilinear
        // turns it RED. A sprite's UV rect runs exactly texel-EDGE to texel-EDGE, so a bilinear tap at the
        // rect's boundary blends the NEIGHBOURING sprite in the packed sheet. The half-texel inset in
        // IconQuadLayout is what keeps it green — that is the whole reason the inset exists, so it must be
        // pinned by something that fails without it.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void IconSampling_NeverBleedsTheNeighbouringSprite_AcrossThePackedSheetBoundary()
        {
            // Two sprites sharing an internal edge, in maximally-separated hues so any blend is unambiguous.
            var index = SpriteIndex.Parse(
                "{\"left\":{\"x\":0,\"y\":0,\"width\":32,\"height\":64,\"pixelRatio\":1}," +
                "\"right\":{\"x\":32,\"y\":0,\"width\":32,\"height\":64,\"pixelRatio\":1}}");
            var sheet = new SpriteSheet(BuildTwoSpriteSheetPng(), index);
            GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            try
            {
                Assert.IsTrue(index.TryGetSprite("left", out SpriteEntry left));

                SymbolQuad quad = IconQuadLayout.Layout(
                    left, sheet.View.Size, Magnification, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);

                byte[] px = RenderIcon(sheet, glyphAtlas, quad, "left", LabelPaintWhite(), Color.black, out _);

                // The left sprite is pure RED. Any pixel where blue leads red carries ink from the RIGHT
                // sprite — impossible unless the sampler reached across the rect boundary.
                int bledPixels = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    if (px[i + 2] > px[i] + 24) bledPixels++;
                }

                Assert.Zero(bledPixels,
                    $"{bledPixels} pixels carry the NEIGHBOURING sprite's blue. A sprite's UV rect spans " +
                    $"texel-edge to texel-edge, so a bilinear tap at the rect boundary reaches into the " +
                    $"sprite packed beside it. IconQuadLayout's half-texel inset is what prevents this — " +
                    $"if this went red, the inset was dropped or the rect was widened back to the edges.");
            }
            finally
            {
                glyphAtlas.Dispose();
                sheet.Dispose();
            }
        }

        // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A minimal one-glyph atlas. Required even though nothing here renders TEXT:
        /// <c>LabelPlacementSystem.TickCore</c> gates its whole placement pass on
        /// <c>atlas?.Texture != null</c>, so a null glyph atlas silently places ZERO icons. An empty
        /// <see cref="GlyphAtlas"/> will not do either — <see cref="GlyphAtlasTexture.Upload"/> no-ops at
        /// <c>Size.y == 0</c> and leaves the texture null, which trips the same gate.
        /// </summary>
        private static GlyphAtlasTexture BuildMinimalGlyphAtlas()
        {
            byte[] pbf = File.ReadAllBytes(Path.Combine(
                Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes"));
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(pbf).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u]); // 'A' — never drawn; it exists only to make the atlas non-empty
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            Assert.IsNotNull(texture.Texture, "precondition: the glyph atlas texture must exist or no icon places.");
            return texture;
        }

        /// <summary>
        /// A 64x64 sheet: RGB is white EVERYWHERE and only alpha carries the shape, so bilinear taps across
        /// the stripe's edge interpolate between white and white — no dark fringe to bias the centroid, and
        /// nothing that depends on how the PNG encoder treats RGB under a zero alpha. The stripe is one
        /// texel wide, and VERTICAL, which makes it invariant under SpriteSheet's row flip.
        /// </summary>
        private static byte[] BuildStripeSheetPng()
        {
            var tex = new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[SheetSize * SheetSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 0);

            const int stripeX = SheetSize / 2;
            for (int y = 2; y < SheetSize - 2; y++) // transparent margin top and bottom
                pixels[y * SheetSize + stripeX] = new Color32(255, 255, 255, 255);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>Left half opaque RED, right half opaque BLUE — two sprites sharing an internal edge.</summary>
        private static byte[] BuildTwoSpriteSheetPng()
        {
            var tex = new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[SheetSize * SheetSize];
            for (int y = 0; y < SheetSize; y++)
                for (int x = 0; x < SheetSize; x++)
                    pixels[y * SheetSize + x] = x < SheetSize / 2
                        ? new Color32(255, 0, 0, 255)
                        : new Color32(0, 0, 255, 255);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>
        /// Guards the synthetic fixture itself: proves the PNG round-trip and SpriteSheet's row flip left the
        /// one-texel stripe exactly where this test believes it is. Without this, a mangled fixture would
        /// present as a resampling verdict.
        /// </summary>
        private static void AssertSheetDecodedAsAuthored(SpriteSheet sheet)
        {
            const int stripeX = SheetSize / 2;
            Assert.AreEqual(SheetSize, sheet.Texture.width, "fixture sheet width");
            Assert.AreEqual(1f, sheet.Texture.GetPixel(stripeX, SheetSize / 2).a, 1e-3f,
                "the stripe column must be opaque after the PNG round-trip and the row flip.");
            Assert.AreEqual(0f, sheet.Texture.GetPixel(stripeX - 1, SheetSize / 2).a, 1e-3f,
                "the stripe must be exactly ONE texel wide — its left neighbour must be transparent.");
            Assert.AreEqual(0f, sheet.Texture.GetPixel(stripeX + 1, SheetSize / 2).a, 1e-3f,
                "the stripe must be exactly ONE texel wide — its right neighbour must be transparent.");
        }

        // ── render + measure ──────────────────────────────────────────────────────────────────────────

        private static LabelPaint LabelPaintWhite() => new LabelPaint
        {
            TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
            HaloColor = default, HaloWidthPx = 0f, HaloBlurPx = 0f,
        };

        /// <summary>
        /// Renders one icon and returns the X centroid of its ink, in device px, weighted by per-pixel
        /// darkness against the white background. A centroid is deliberately preferred over an ink MASS or a
        /// measured WIDTH: it is normalised, so a monotonic transfer curve on the readback (sRGB vs linear)
        /// cannot move it the way it moves a raw sum, and it states the property under test directly — where
        /// the icon's content actually sits.
        /// </summary>
        private static float RenderAndMeasureInkCentroidX(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName)
        {
            byte[] px = RenderIcon(sheet, glyphAtlas, quad, iconName, LabelPaint.Default, Color.white, out int width);

            double weighted = 0.0, total = 0.0;
            for (int i = 0, p = 0; i + 3 < px.Length; i += 4, p++)
            {
                double ink = (255.0 - px[i]) / 255.0; // black ink on white
                if (ink <= 0.004) continue;           // ignore readback noise
                weighted += ink * (p % width);
                total += ink;
            }

            Assert.Greater(total, 1.0, "the icon must actually have drawn — no ink found in the frame.");
            return (float)(weighted / total);
        }

        /// <summary>
        /// Drives ONE icon through the real LabelPlacementSystem → Map/Symbol/IconWorld path and returns the
        /// raw RGBA32 readback. The readback is the vertical mirror of on-screen (Unity's render-to-texture
        /// Y-flip), which is irrelevant to every measurement here — all of them are horizontal.
        /// </summary>
        private static byte[] RenderIcon(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName,
            LabelPaint paint, Color background, out int width)
        {
            var camGo = new GameObject("IconResampling_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = background;

            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));

            // The sweep converts a logical-px icon-offset into an exact DEVICE-px phase, which only holds at
            // dpr 1. Assert it rather than assume it: a fixture default of 2 would halve every phase step and
            // quietly weaken the tooth into a sweep of half a pixel.
            Assert.AreEqual(1.0, mapCamera.DevicePixelRatio, 1e-9,
                "this fixture converts logical px to device px 1:1 — it requires dpr 1.");

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(
                    new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // A realistic containing tile keeps the world-anchored bake float32-safe (TileKey=0 is ~2e7 m
            // away) — the same note SymbolIconRenderSnapshotTests carries.
            long tileKey = TestTileKeys.PackedContaining(
                new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            var labels = new List<LabelInstance>
            {
                new LabelInstance
                {
                    AnchorRender = frame.SceneOriginRender,
                    Layout = IconQuadLayout.ToLayoutResult(quad),
                    Kind = LabelKind.Icon,
                    Paint = paint,
                    TextSizePx = TextQuadLayout.OneEm, // scale 1 — the real StyledSymbolTileBuilder icon path
                    IconImage = iconName,
                    AllowOverlap = true,
                    SortKey = 0f,
                    FeatureIndex = 0,
                    TileKey = tileKey,
                },
            };

            var system = new LabelPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // The collision verdict is harvested one Tick late (§2.6) — hence the duplicate tick.
                system.Tick(in frame, plan.Build(labels), glyphAtlas, deltaTime: float.PositiveInfinity,
                    spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(labels), glyphAtlas, deltaTime: float.PositiveInfinity,
                    spriteTexture: sheet.Texture);
                Assert.AreEqual(1, system.LastQuadCount, "the single icon quad must place (not culled).");

                snap.Render(uCam);
                if (snap.IsAllBlack())
                    Assert.Inconclusive("render is all-black — no GPU context in this batch session " +
                                        "(see SnapshotRenderer.IsAllBlack).");

                width = snap.Width;
                return (byte[])snap.RawPixels.Clone();
            }
            finally
            {
                snap.Dispose();
                system.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
