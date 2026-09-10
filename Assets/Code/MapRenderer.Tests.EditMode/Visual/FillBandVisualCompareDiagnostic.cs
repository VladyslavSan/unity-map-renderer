// Image-production harness for the fill boundary band: renders band-OFF and band-ON frames over real MVT
// fixtures and writes them to disk as PNGs for a human to judge by eye. NOT a tooth — every test here is
// [Explicit], so an unfiltered gate run never touches it.
//
// WHY BAND-OFF IS THE "BEFORE". The band-off arm is `FillMeshPipeline.LayerInput.SuppressBoundaryBand`,
// which removes ALL band geometry — the exact fill rendering `main` produces. Toggling it isolates THIS
// change: same build, same shader, same camera, same mesh pipeline, one flag apart. Checking out `main`
// would also drag in every unrelated commit on the branch.
//
// Scene, camera and mesh-arm construction are modelled on FillBandFrameCostDiagnostic (which measures the
// band's COST over the same corpus); that file is left untouched.

#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using Fill = MapRenderer.Core.Style.Fill;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using Debug = UnityEngine.Debug;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Produces before/after image pairs of the fill boundary band over real MVT fixtures, plus the
    /// graded-pixel counts that go with each pair.
    /// </summary>
    [TestFixture]
    [Explicit("Image-production harness, not a tooth — run by name.")]
    public class FillBandVisualCompareDiagnostic
    {
        /// <summary>Side of the square off-screen target every frame is rendered into. The band is one
        /// DEVICE pixel wide, so the target's pixel size is what sets its apparent width.</summary>
        private const int RtPx = 1024;

        /// <summary>Side of the magnified crop window, in source pixels.</summary>
        private const int CropPx = 64;

        /// <summary>Integer pixel-replication factor for the crop. Nearest-neighbour by construction — any
        /// filtered resize would manufacture a soft edge in the band-OFF arm and destroy the comparison.
        /// </summary>
        private const int CropMag = 8;

        /// <summary>World-unit size the tile mesh is fitted to, and the ortho camera's full height — so the
        /// tile exactly fills the frame.</summary>
        private const float ViewSize = 100f;

        /// <summary>Where the PNGs land. Session scratch, not the repo.</summary>
        private const string OutDir =
            "/private/tmp/claude-502/-Users-vladyslav-odobesku-intellias-com-programming-unity-map-renderer/" +
            "c4786429-a6fe-4710-9aa4-e4e9383a60e6/scratchpad/fill-aa-compare";

        /// <summary>Prefix on every reported line, so the numbers pull out of Logs/test-run.log with one
        /// grep.</summary>
        private const string Tag = "BANDIMG|";

        /// <summary>One rendered case: a fixture, the layer to draw from it, the layer opacity, and the
        /// slug that names its files.</summary>
        private readonly struct Case
        {
            public readonly string File, Layer, Slug;
            public readonly int Z, X, Y;
            public readonly float Opacity;

            public Case(string file, int z, int x, int y, string layer, string slug, float opacity)
            {
                File = file; Z = z; X = x; Y = y; Layer = layer; Slug = slug; Opacity = opacity;
            }
        }

        /// <summary>The rendered set. Three opaque cases spanning the density range, plus the archipelago
        /// again at <c>landcover_wood</c>'s 0.4 — a translucent layer is where the band's compositing at an
        /// abutting edge was contested, so the maintainer should see one.</summary>
        private static readonly Case[] Cases =
        {
            new Case("sample-tile.bytes", 0, 0, 0, "countries", "countries-z0", 1f),
            new Case("water-real-norway-fjords-8-132-72.pbf.bytes", 8, 132, 72, "water",
                     "norway-fjords-z8", 1f),
            new Case("water-real-stockholm-archipelago-9-282-150.pbf.bytes", 9, 282, 150, "water",
                     "stockholm-archipelago-z9", 1f),
            new Case("water-real-stockholm-archipelago-9-282-150.pbf.bytes", 9, 282, 150, "water",
                     "stockholm-archipelago-z9-opacity40", 0.4f),
        };

        /// <summary>A pixel classified against the scene's two extremes. Same three classes, and the same
        /// 3-LSB tolerance, that <c>FillBoundaryBandRenderTests</c> asserts on.</summary>
        private enum Ink { Background, Full, Graded }

        // ── The one test ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders every case in both arms, writes four PNGs per case (full frame ×2, 8× crop ×2), and
        /// reports the graded-pixel count for BOTH arms.
        ///
        /// <para>The band-OFF graded count is the control, not decoration: MSAA is off in this project's URP
        /// asset and every case draws one uniform fill colour, so band-OFF must come back at or near zero. A
        /// substantial count there would mean the classifier is reading shading or per-feature colour as a
        /// ramp, and no band-ON number from the same run could be believed.</para>
        ///
        /// <para>Both arms are classified against references taken from the band-OFF frame (background =
        /// its corner pixel, full = its brightest pixel), so the two counts are read off one ruler.</para>
        /// </summary>
        [Test]
        public void Produce_BeforeAfterImages()
        {
            Directory.CreateDirectory(OutDir);
            using var scene = Scene.Create();

            foreach (Case c in Cases)
            {
                using var off = MeshArm.Build(c, suppressBand: true);
                using var on  = MeshArm.Build(c, suppressBand: false);

                using var offPlaced = scene.Place(off);
                using var onPlaced  = scene.Place(on);

                byte[] pxOff = scene.Capture(offPlaced);
                byte[] pxOn  = scene.Capture(onPlaced);

                if (!References(pxOff, out double3 background, out double3 full))
                {
                    Report($"{c.Slug}: SKIPPED — band-off frame carries no ink (no GPU context in batch " +
                           "EditMode). Re-run as PlayMode.");
                    continue;
                }

                Ink[] clsOff = Classify(pxOff, background, full);
                Ink[] clsOn  = Classify(pxOn,  background, full);

                int gradedOff = Count(clsOff, Ink.Graded);
                int gradedOn  = Count(clsOn,  Ink.Graded);
                int boundaryOff = BoundaryTransitions(clsOff, out int tx, out int ty);
                int differing = DifferingPixels(pxOff, pxOn);
                int overOff = Overcomposited(pxOff, background, full);
                int overOn = Overcomposited(pxOn, background, full);

                Report($"{c.Slug} fixture={c.File} z={c.Z} layer={c.Layer} opacity={c.Opacity:F2} " +
                       $"verts_off={off.VertexCount} verts_on={on.VertexCount} " +
                       $"ratio={(double)on.VertexCount / math.max(1, off.VertexCount):F3}");
                Report($"{c.Slug} graded_off={gradedOff} graded_on={gradedOn} " +
                       $"boundary_px_off={boundaryOff} (h={tx} v={ty}) " +
                       $"graded_on_per_boundary_px={(double)gradedOn / math.max(1, boundaryOff):F2} " +
                       $"differing_px={differing} of {RtPx * RtPx}");
                Report($"{c.Slug} overcomposited_off={overOff} overcomposited_on={overOn} " +
                       "(pixels reading MORE covered than the full-coverage reference — a translucent " +
                       "band lying over an already-painted neighbour; must be 0 on an opaque arm)");
                Report($"{c.Slug} refs background=({background.x:F3},{background.y:F3},{background.z:F3}) " +
                       $"full=({full.x:F3},{full.y:F3},{full.z:F3}) " +
                       $"interior_mean={InteriorMean(pxOff, clsOff):F3}");

                WritePng(pxOff, RtPx, RtPx, $"fill-aa-{c.Slug}-off.png");
                WritePng(pxOn,  RtPx, RtPx, $"fill-aa-{c.Slug}-on.png");

                // The crop rect is chosen from the band-OFF frame ALONE — the arm without the effect — so
                // the window cannot have been selected for where the band happens to look best.
                int2 crop = PickCrop(clsOff, out int cropScore);
                Report($"{c.Slug} crop origin=({crop.x},{crop.y}) size={CropPx} diagonality_score={cropScore} " +
                       $"(frame coords, y grows UPWARD from the bottom row) mag={CropMag}x nearest-neighbour");

                WritePng(Magnify(pxOff, crop), CropPx * CropMag, CropPx * CropMag,
                         $"fill-aa-{c.Slug}-off-crop8x.png");
                WritePng(Magnify(pxOn, crop), CropPx * CropMag, CropPx * CropMag,
                         $"fill-aa-{c.Slug}-on-crop8x.png");
            }

            Report($"wrote images to {OutDir}");
        }

        // ── Classification ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The two reference colours a frame is classified against: the corner pixel (background)
        /// and the frame's brightest pixel (full coverage), taken from the SAME frame so shading, opacity
        /// and colour management never have to be modelled.</summary>
        /// <param name="px">Raw RGBA32 pixels, row-major from the bottom-left.</param>
        /// <param name="background">The background reference.</param>
        /// <param name="full">The full-coverage reference.</param>
        /// <returns>False when the frame carries no ink at all — no GPU context.</returns>
        private static bool References(byte[] px, out double3 background, out double3 full)
        {
            background = new double3(px[0] / 255.0, px[1] / 255.0, px[2] / 255.0);
            full = background;
            double best = 0.0;
            for (int i = 0; i < px.Length; i += 4)
            {
                var c = new double3(px[i] / 255.0, px[i + 1] / 255.0, px[i + 2] / 255.0);
                double d = math.length(c - background);
                if (d > best) { best = d; full = c; }
            }
            return best >= 0.05;
        }

        /// <summary>Classifies every pixel against the given references.</summary>
        /// <param name="px">Raw RGBA32 pixels.</param>
        /// <param name="background">The background reference.</param>
        /// <param name="full">The full-coverage reference.</param>
        /// <returns>Per-pixel classes, row-major from the bottom-left.</returns>
        private static Ink[] Classify(byte[] px, double3 background, double3 full)
        {
            // One LSB of an 8-bit channel is 1/255; three of them is a floor that cannot absorb a real
            // partially-covered pixel. Same tolerance FillBoundaryBandRenderTests asserts on.
            double tolerance = 3.0 / 255.0 * math.sqrt(3.0);
            var classes = new Ink[px.Length / 4];
            for (int i = 0; i < classes.Length; i++)
            {
                var c = new double3(px[i * 4] / 255.0, px[i * 4 + 1] / 255.0, px[i * 4 + 2] / 255.0);
                if (math.length(c - background) <= tolerance) classes[i] = Ink.Background;
                else if (math.length(c - full) <= tolerance) classes[i] = Ink.Full;
                else classes[i] = Ink.Graded;
            }
            return classes;
        }

        /// <summary>Counts pixels of one class.</summary>
        /// <param name="classes">A classification.</param>
        /// <param name="want">The class to count.</param>
        /// <returns>The count.</returns>
        private static int Count(Ink[] classes, Ink want)
        {
            int n = 0;
            foreach (Ink c in classes) if (c == want) n++;
            return n;
        }

        /// <summary>Pixels that read as MORE covered than the frame's own full-coverage reference — the
        /// signature of two translucent surfaces compositing over each other. On a translucent layer the
        /// outward band of one polygon lies on top of its neighbour's already-painted interior, so this
        /// separates that rim from the ramp pixels <c>graded</c> also counts. Zero on an opaque arm, where
        /// alpha saturates.</summary>
        /// <param name="px">Raw RGBA32 pixels.</param>
        /// <param name="background">The background reference.</param>
        /// <param name="full">The full-coverage reference.</param>
        /// <returns>The count.</returns>
        private static int Overcomposited(byte[] px, double3 background, double3 full)
        {
            double tolerance = 3.0 / 255.0 * math.sqrt(3.0);
            double fullDistance = math.length(full - background);
            int n = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                var c = new double3(px[i] / 255.0, px[i + 1] / 255.0, px[i + 2] / 255.0);
                if (math.length(c - background) > fullDistance + tolerance) n++;
            }
            return n;
        }

        /// <summary>Background↔full adjacencies in the hard-edged arm — a pixel-count proxy for the
        /// on-screen boundary length, which is what makes the band-ON graded count interpretable (graded
        /// pixels per boundary pixel is the ramp's apparent width).</summary>
        /// <param name="classes">The band-OFF classification.</param>
        /// <param name="horizontal">Transitions across a horizontal step.</param>
        /// <param name="vertical">Transitions across a vertical step.</param>
        /// <returns>Their sum.</returns>
        private static int BoundaryTransitions(Ink[] classes, out int horizontal, out int vertical)
        {
            horizontal = 0;
            vertical = 0;
            for (int y = 0; y < RtPx; y++)
                for (int x = 0; x < RtPx; x++)
                {
                    Ink here = classes[y * RtPx + x];
                    if (x + 1 < RtPx && Crosses(here, classes[y * RtPx + x + 1])) horizontal++;
                    if (y + 1 < RtPx && Crosses(here, classes[(y + 1) * RtPx + x])) vertical++;
                }
            return horizontal + vertical;
        }

        /// <summary>Whether two adjacent classes span the silhouette.</summary>
        /// <param name="a">One class.</param>
        /// <param name="b">The other.</param>
        /// <returns>True when one is background and the other full.</returns>
        private static bool Crosses(Ink a, Ink b) =>
            (a == Ink.Background && b == Ink.Full) || (a == Ink.Full && b == Ink.Background);

        /// <summary>Mean luminance of the fully-covered pixels — the check that a translucent case really
        /// is blending: it must sit strictly between the background and opaque white.</summary>
        /// <param name="px">Raw RGBA32 pixels.</param>
        /// <param name="classes">Their classification.</param>
        /// <returns>Mean luminance in [0,1], or 0 when nothing is fully covered.</returns>
        private static double InteriorMean(byte[] px, Ink[] classes)
        {
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i] != Ink.Full) continue;
                sum += (px[i * 4] + px[i * 4 + 1] + px[i * 4 + 2]) / (3.0 * 255.0);
                n++;
            }
            return n == 0 ? 0.0 : sum / n;
        }

        /// <summary>Pixels whose RGB differs at all between the two arms.</summary>
        /// <param name="a">One arm's pixels.</param>
        /// <param name="b">The other's.</param>
        /// <returns>The count.</returns>
        private static int DifferingPixels(byte[] a, byte[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) n++;
            return n;
        }

        // ── Crop selection and magnification ───────────────────────────────────────────────────────────

        /// <summary>
        /// Picks the crop window from the band-OFF classification: the window scoring highest on
        /// <c>2 · min(horizontal, vertical)</c> silhouette transitions.
        ///
        /// <para>The min of the two axes rather than their sum is what makes it a DIAGONAL boundary: an
        /// axis-aligned edge produces transitions across one step direction only and scores zero, while a
        /// 45° edge produces both in equal number. The band's whole claim is about a diagonal silhouette
        /// (an axis-aligned one is bit-identical under either gradient), so that is what the crop must
        /// show.</para>
        /// </summary>
        /// <param name="classes">The band-OFF classification.</param>
        /// <param name="score">The winning window's score — reported, because a LOW one means this
        /// fixture's densest window is nearly axis-aligned and the crop is not showing the diagonal case
        /// the band's gradient claim is about.</param>
        /// <returns>The window's bottom-left corner in frame pixels.</returns>
        private static int2 PickCrop(Ink[] classes, out int score)
        {
            const int Stride = 16;
            int best = -1;
            var origin = new int2((RtPx - CropPx) / 2, (RtPx - CropPx) / 2);

            for (int oy = 0; oy + CropPx <= RtPx; oy += Stride)
                for (int ox = 0; ox + CropPx <= RtPx; ox += Stride)
                {
                    int h = 0, v = 0;
                    for (int y = oy; y < oy + CropPx; y++)
                        for (int x = ox; x < ox + CropPx; x++)
                        {
                            Ink here = classes[y * RtPx + x];
                            if (x + 1 < RtPx && Crosses(here, classes[y * RtPx + x + 1])) h++;
                            if (y + 1 < RtPx && Crosses(here, classes[(y + 1) * RtPx + x])) v++;
                        }
                    int windowScore = 2 * math.min(h, v);
                    if (windowScore > best) { best = windowScore; origin = new int2(ox, oy); }
                }
            score = best;
            return origin;
        }

        /// <summary>Crops and pixel-replicates by <see cref="CropMag"/>. Nearest-neighbour by
        /// construction — every output pixel is a byte copy of its source pixel, so neither arm gains a
        /// softness the renderer did not put there.</summary>
        /// <param name="px">Raw RGBA32 pixels of the full frame.</param>
        /// <param name="origin">The window's bottom-left corner.</param>
        /// <returns>The magnified crop's raw RGBA32 pixels.</returns>
        private static byte[] Magnify(byte[] px, int2 origin)
        {
            int side = CropPx * CropMag;
            var outPx = new byte[side * side * 4];
            for (int y = 0; y < side; y++)
            {
                int sy = origin.y + y / CropMag;
                for (int x = 0; x < side; x++)
                {
                    int sx = origin.x + x / CropMag;
                    int s = (sy * RtPx + sx) * 4;
                    int d = (y * side + x) * 4;
                    outPx[d] = px[s];
                    outPx[d + 1] = px[s + 1];
                    outPx[d + 2] = px[s + 2];
                    outPx[d + 3] = 255;
                }
            }
            return outPx;
        }

        /// <summary>Encodes raw RGBA32 pixels to a PNG under <see cref="OutDir"/>.</summary>
        /// <param name="px">Raw RGBA32 pixels, row-major from the bottom-left.</param>
        /// <param name="w">Width in pixels.</param>
        /// <param name="h">Height in pixels.</param>
        /// <param name="name">File name.</param>
        private static void WritePng(byte[] px, int w, int h, string name)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.LoadRawTextureData(px);
                tex.Apply(false);
                File.WriteAllBytes(Path.Combine(OutDir, name), tex.EncodeToPNG());
                Report($"  wrote {name} ({w}x{h})");
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        // ── Scene ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The render scene: an off-screen target, a top-down orthographic camera framing the fitted tile,
        /// and the lit-ambient recipe <c>TiltedGroundScene.Create</c> established — restored on dispose,
        /// because it is process-global state.
        ///
        /// <para>Orthographic and untilted on purpose: under an orthographic projection the band is exactly
        /// one device pixel wide everywhere in frame, so a crop from any part of the frame shows the same
        /// ramp width the shipped renderer produces looking straight down.</para>
        /// </summary>
        private sealed class Scene : IDisposable
        {
            public Camera Cam;
            private GameObject _camGo, _lightGo;
            private RenderTexture _rt;
            private Texture2D _full;
            private (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) _savedAmbient;

            public static Scene Create()
            {
                var s = new Scene();
                s._savedAmbient = (QualitySettings.GetQualityLevel(),
                                   RenderSettings.ambientMode, RenderSettings.ambientLight);
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

                s._lightGo = new GameObject("BandImg_DirLight");
                s._lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                var light = s._lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;

                s._rt = new RenderTexture(RtPx, RtPx, 24, RenderTextureFormat.ARGB32);
                s._rt.Create();
                s._full = new Texture2D(RtPx, RtPx, TextureFormat.RGBA32, false);

                s._camGo = new GameObject("BandImg_Camera");
                s.Cam = s._camGo.AddComponent<Camera>();
                s.Cam.enabled = false;      // manual Render() only
                s.Cam.targetTexture = s._rt;
                s.Cam.clearFlags = CameraClearFlags.SolidColor;
                s.Cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
                s.Cam.orthographic = true;
                s.Cam.orthographicSize = ViewSize * 0.5f;
                s.Cam.nearClipPlane = 0.1f;
                s.Cam.farClipPlane = 2000f;
                s._camGo.transform.position = new Vector3(0f, 500f, 0f);
                s._camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                return s;
            }

            /// <summary>Instantiates one renderer for the arm's mesh, fitted to <see cref="ViewSize"/> and
            /// centred, so both arms land on identical screen pixels.</summary>
            /// <param name="arm">The arm to place.</param>
            /// <returns>The placed, inactive renderer.</returns>
            public Placed Place(MeshArm arm)
            {
                var root = new GameObject("BandImg_Root");
                Bounds b = arm.Mesh.bounds;
                float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z), 1e-6f);
                float scale = ViewSize / maxDim;

                var go = new GameObject("mesh");
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = arm.Mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = arm.Material;
                go.transform.localScale = Vector3.one * scale;
                go.transform.localPosition = -b.center * scale;

                root.SetActive(false);
                return new Placed(root);
            }

            /// <summary>Renders one arm and reads the frame back.</summary>
            /// <param name="placed">The arm's renderer.</param>
            /// <returns>Raw RGBA32 pixels, row-major from the bottom-left.</returns>
            public byte[] Capture(Placed placed)
            {
                placed.Root.SetActive(true);
                try
                {
                    Cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = _rt;
                    _full.ReadPixels(new Rect(0, 0, RtPx, RtPx), 0, 0, false);
                    _full.Apply(false);
                    RenderTexture.active = prev;
                    return (byte[])_full.GetRawTextureData().Clone();
                }
                finally { placed.Root.SetActive(false); }
            }

            public void Dispose()
            {
                if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
                if (_lightGo != null) UnityEngine.Object.DestroyImmediate(_lightGo);
                if (_rt != null) { _rt.Release(); UnityEngine.Object.DestroyImmediate(_rt); }
                if (_full != null) UnityEngine.Object.DestroyImmediate(_full);
                QualitySettings.SetQualityLevel(_savedAmbient.quality, false);
                RenderSettings.ambientMode = _savedAmbient.mode;
                RenderSettings.ambientLight = _savedAmbient.light;
            }
        }

        /// <summary>A placed renderer, inactive except while it is being rendered.</summary>
        private sealed class Placed : IDisposable
        {
            public readonly GameObject Root;
            public Placed(GameObject root) => Root = root;
            public void Dispose() { if (Root != null) UnityEngine.Object.DestroyImmediate(Root); }
        }

        // ── One arm's mesh + material ──────────────────────────────────────────────────────────────────

        /// <summary>One arm: the fill mesh a fixture produces with the band emitted or suppressed, plus the
        /// material it draws with. One uniform fill colour per arm, deliberately — a per-feature palette
        /// would make every non-brightest feature classify as Graded and destroy the control.</summary>
        private sealed class MeshArm : IDisposable
        {
            public Mesh Mesh;
            public Material Material;
            public int VertexCount;

            public static MeshArm Build(Case c, bool suppressBand)
            {
                var id = new TileId { Z = c.Z, X = c.X, Y = c.Y };
                byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", c.File));
                using MvtTile tile = MvtDecoder.Decode(id, bytes);

                StyleDocument style = StyleParser.Parse(StyleJson(c.Layer));
                StyleLayer styleLayer = style.Layers[0];
                ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, tile);
                Assert.IsNotNull(mvtLayer, $"{c.File}: source-layer '{c.Layer}' must resolve");

                var selected = TestTileMeshBuilder.Select(styleLayer, mvtLayer, c.Z);
                Mesh mesh = TestTileMeshBuilder.BuildFillFromLayer(
                    mvtLayer, selected, new Fill.PaintProperties(styleLayer), c.Z, id,
                    suppressBoundaryBand: suppressBand);
                Assert.IsNotNull(mesh, $"{c.File}: '{c.Layer}' produced no fill geometry");

                var shader = MapMaterialSetTestUtil.Load().FillMaterial.shader;
                var mat = new Material(shader) { name = "BandImg_Fill" };
                FillMaterialTweaker.ApplyPainterContract(mat);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Opacity", c.Opacity);

                return new MeshArm { Mesh = mesh, Material = mat, VertexCount = mesh.vertexCount };
            }

            public void Dispose()
            {
                if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
                if (Material != null) UnityEngine.Object.DestroyImmediate(Material);
            }
        }

        /// <summary>Minimal one-fill-layer style so <c>SourceLayerResolver</c> binds the fixture's source
        /// layer.</summary>
        /// <param name="layerName">The source layer to draw.</param>
        /// <returns>The style JSON.</returns>
        private static string StyleJson(string layerName) => @"{
  ""version"": 8,
  ""name"": ""BandImg"",
  ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
  ""layers"": [ {
      ""id"": """ + layerName + @""",
      ""type"": ""fill"",
      ""source"": ""maplibre"",
      ""source-layer"": """ + layerName + @""",
      ""paint"": { ""fill-color"": [""rgba"",255,255,255,1] }
  } ]
}";

        /// <summary>Logs a line under <see cref="Tag"/>, to both the Editor log and the results XML.</summary>
        /// <param name="line">The line.</param>
        private static void Report(string line)
        {
            Debug.Log(Tag + line);
            TestContext.Out.WriteLine(Tag + line);
        }
    }
}
#endif
