// Shader paint-color conversion GPU/visual acceptance test.
//
// Standalone, not merged with ShaderLightingTests.cs: the two collide on bare `Object`
// (System.Object vs UnityEngine.Object, CS0104) — this file uses the bare
// UnityEngine.Object.DestroyImmediate, ShaderLightingTests.cs imports System.
//
// Contents:
//   PaintColorRenderTests  — Unity-only: render tests requiring a GPU context (SnapshotRenderer).

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using Unity.Mathematics;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using Color = UnityEngine.Color;
using Line = MapRenderer.Core.Style.Line;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Visual
{
    // Unity-only: render tests requiring a GPU context (SnapshotRenderer).
    // NOT included in Tools/core-tests/core-tests.csproj.
    //
    // The cross-instrument half of PaintColorSingleApplyTests. That fixture re-implements the fragment's
    // `_BaseColor.rgb × vColor.rgb` on the CPU from the two real sources and could drift from the HLSL; this one
    // reads the quantity ITSELF off a rendered pixel. Neither substitutes for the other — but the CPU one is the
    // headline, because this one needs a GPU to render anything at all.
    //
    // Lighting is cancelled rather than controlled. Three renders differing ONLY in the style's line-color give
    //
    //     albedo = (linear(pixel) - linear(pixelBlack)) / (linear(pixelWhite) - linear(pixelBlack))
    //
    // which is exact under any affine response `pixel = k1·albedo + k2` — so no assumption about ambient, the
    // URP Lit BRDF, or exposure enters the assertion. The white and black arms run the identical production
    // binder + builder path, so they carry no hand-set uniforms to drift from production.
    //
    // This is also the measurement that settles how a Color-typed material property reaches the shader: in Linear
    // colour space Unity converts it sRGB→linear on upload, so a correctly-single-applied #6699CC must measure
    // LINEAR (0.1329, 0.3185, 0.6038). Measuring the sRGB triple (0.4, 0.6, 0.8) instead would mean no upload
    // conversion happens and PaintColorSingleApplyTests' composition is the half that is wrong.

    // ───────────────────────────────────────────────────────────────────────────────────
    // PaintColorRenderTests — Unity-only: render tests requiring a GPU context (SnapshotRenderer).
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class PaintColorRenderTests : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            QualityLevel = 0,
            AmbientMode  = AmbientMode.Flat,
            AmbientLight = new Color(0.6f, 0.6f, 0.6f, 1f),
            Fog          = false,
        };

        private const int    SnapW = 256;
        private const int    SnapH = 256;
        private const double Extent = 4096.0;
        private const double Zoom   = 14.0;

        // z14 x8192 y8192 is the tile whose SW corner IS the Mercator origin, so an unshifted build lands
        // within ~2.4 km of world zero — float-clean for a 256 px frame.
        private static readonly TileId Tile = new TileId { Z = 14, X = 8192, Y = 8192 };

        private const string AuthoredHex = "#6699CC";

        /// <summary>A fat ribbon, so the centre sample sits well inside the solid core and never on the
        /// coverage-feathered edge (where the alpha blend against the clear would bias the reading).</summary>
        private const int LineWidthPx = 60;

        // Sample box at the image centre, comfortably inside a 60 px ribbon.
        private const int SampleHalf = 6;

        /// <param name="colorToken">The style value for <c>line-color</c>, verbatim JSON — a quoted hex
        /// string or an <c>["rgba", …]</c> literal, so an authored ALPHA can be driven too.</param>
        private static Line.PaintProperties Paint(string colorToken)
            => TestStyle.LinePaint($"{{\"line-color\":{colorToken},\"line-width\":{LineWidthPx}}}");

        private static IFeature LineFeature()
        {
            uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));
            return new DictionaryFeature(geometryType: TileGeometryType.LineString, geometry: new uint[]
            {
                (1u << 3) | 1u, ZigZag(200),  ZigZag(2048), // MoveTo(1)
                (1u << 3) | 2u, ZigZag(3600), ZigZag(0),    // LineTo(1) — straight across the tile
            });
        }

        /// <summary>Mean linear RGB of the centre sample box. The render target is sRGB-encoded on readback,
        /// so the bytes are decoded through <see cref="Color.linear"/> before averaging.</summary>
        private static double3 SampleLinear(SnapshotRenderer snap)
        {
            double3 sum = double3.zero;
            int n = 0;
            for (int y = SnapH / 2 - SampleHalf; y <= SnapH / 2 + SampleHalf; y++)
            for (int x = SnapW / 2 - SampleHalf; x <= SnapW / 2 + SampleHalf; x++)
            {
                Color32 px = snap.Pixels[x, y];
                Color lin = new Color(px.r / 255f, px.g / 255f, px.b / 255f, 1f).linear;
                sum += new double3(lin.r, lin.g, lin.b);
                n++;
            }
            return sum / n;
        }

        /// <summary>
        /// Renders one line layer end-to-end: the mesh through the production builder, the material through
        /// the production binder, framed on the mesh's own bounds. Returns the centre sample in linear RGB.
        /// </summary>
        private static double3 RenderLayer(string colorToken, string tag)
        {
            using var bag = new ObjectDisposalBag();
            Line.PaintProperties paint = Paint(colorToken);
            Mesh mesh = bag.Track(TestTileMeshBuilder.BuildLine(
                new[] { LineFeature() }, paint, TestStyle.LineLayout("{}"), Zoom, Extent, Tile, double2.zero));
            Assert.IsNotNull(mesh, "the fixture feature must produce line geometry.");

            Material mat = bag.Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Line base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindLinePaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Bounds b = mesh.bounds;
            float  orthoSize = math.max(b.extents.x, b.extents.z) * 1.2f;

            var lineGo = bag.Track(new GameObject("PaintColorRender_Line"));
            lineGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            lineGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var camGo  = bag.Track(new GameObject("PaintColorRender_Camera"));
            var camera = camGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(b.center.x, 500f, b.center.z);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = orthoSize;
            camera.farClipPlane       = 5000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = Color.black;
            camera.enabled            = false;

            // The frame constant the line shader converts a PIXEL width with. Omitting it renders every
            // styled width as a plausible-looking 1 px hairline, which the centre sample would still hit —
            // and then the reading would come off a feathered edge instead of the solid core.
            Shader.SetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel,
                                  2f * orthoSize / SnapH);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng($"paint-color-{tag}.png");
            return SampleLinear(snap);
        }

        /// <summary>
        /// The rendered albedo of a layer whose <c>line-color</c> is a CONSTANT #6699CC must be the authored
        /// colour, not its square. Pre-fix the layer carried the colour in BOTH the uniform and the COLOR
        /// stream and the fragment multiplied them.
        /// </summary>
        [Test]
        public void ConstantLineColor_RenderedPixel_MatchesAuthored()
        {
            double3 white = RenderLayer("\"#ffffff\"", "white");
            double3 black = RenderLayer("\"#000000\"", "black");
            double3 layer = RenderLayer($"\"{AuthoredHex}\"", "authored");

            double3 span = white - black;
            Assert.Greater(math.cmin(span), 0.05,
                $"the white and black reference arms must be separable to calibrate against " +
                $"(white={white}, black={black}).");

            double3 measured = (layer - black) / span;

            Assert.IsTrue(ColorUtility.TryParseHtmlString(AuthoredHex, out Color authored));
            Color   expected = authored.linear;
            double3 expect3  = new double3(expected.r, expected.g, expected.b);
            double3 squared  = expect3 * expect3;

            Debug.Log($"[PaintColorRender] measured={measured} authored(linear)={expect3} squared={squared}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: a CONSTANT line-color must reach the fragment ONCE. " +
                    $"measured={measured} authored(linear)={expect3} authored²={squared}. " +
                    $"Landing on authored² means the colour is in both the _BaseColor uniform and the " +
                    $"COLOR stream. Landing on the sRGB triple ({authored.r:F3},{authored.g:F3}," +
                    $"{authored.b:F3}) instead would mean Unity does NOT linearize a Color material " +
                    $"property on upload, and PaintColorSingleApplyTests' CPU composition is wrong.");
        }

        /// <summary>
        /// The line fragment REPLACES surface alpha rather than multiplying into it, so <c>_BaseColor.a</c>
        /// only reaches the output because the two forward passes multiply it in explicitly. Nothing on the
        /// CPU can observe that: a composed check spells out the INTENDED formula and passes whether the
        /// shader carries the term or not. This reads the composite instead.
        ///
        /// <para>Two arms of identical WHITE albedo over a black clear, differing only in the authored alpha.
        /// Straight-alpha blend against a zero destination gives <c>pixel = alpha × albedo</c>, so the ratio
        /// is the authored alpha and every lighting term cancels. It reads <b>1.0</b> — the authored alpha
        /// silently dropped — if the builder gate ships without the fragment delta.</para>
        /// </summary>
        [Test]
        public void ConstantLineColorAlpha_RenderedPixel_CarriesTheAuthoredAlpha()
        {
            const double AuthoredAlpha = 0.5;

            double3 opaque      = RenderLayer("\"#ffffff\"", "alpha-opaque");
            double3 translucent = RenderLayer("[\"rgba\",255,255,255,0.5]", "alpha-half");

            Assert.Greater(math.cmin(opaque), 0.05,
                $"the opaque reference must be well clear of the black clear to divide by (got {opaque}).");

            double3 ratio = translucent / opaque;
            Debug.Log($"[PaintColorRender] alpha ratio={ratio} opaque={opaque} translucent={translucent}");

            for (int c = 0; c < 3; c++)
                Assert.That(ratio[c], Is.EqualTo(AuthoredAlpha).Within(0.03),
                    $"channel {c}: a CONSTANT line-color's authored ALPHA must reach the composite. " +
                    $"ratio={ratio} (translucent={translucent}, opaque={opaque}). " +
                    $"1.0 means the fragment ignores _BaseColor.a: the constant colour moved to the " +
                    $"uniform but the two forward passes still compute alpha from vColor.a alone, so the " +
                    $"authored alpha is gone. 0.25 would mean it is applied twice.");
        }

        // ── fill: the site-2 gamma-convention pin ────────────────────────────────────────────────

        /// <summary>A square polygon well inside the tile, margined for the fill boundary band's feathered
        /// edge — mirrors <see cref="LineFeature"/>'s "fat ribbon, sample the solid core" shape.</summary>
        private static IFeature FillSquareFeature()
        {
            uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));
            return new DictionaryFeature(geometryType: TileGeometryType.Polygon, geometry: new uint[]
            {
                (1u << 3) | 1u, ZigZag(548),  ZigZag(548),   // MoveTo → (548, 548)
                (3u << 3) | 2u,
                ZigZag(3000),  ZigZag(0),                    // +x
                ZigZag(0),     ZigZag(3000),                 // +y
                ZigZag(-3000), ZigZag(0),                    // -x
            });
        }

        /// <summary>Renders one fill layer end-to-end, the fill counterpart of <see cref="RenderLayer"/>.</summary>
        private static double3 RenderFillLayer(string colorToken, string tag)
        {
            using var bag = new ObjectDisposalBag();
            Fill.PaintProperties paint = TestStyle.FillPaint($"{{\"fill-color\":{colorToken}}}");
            Mesh mesh = bag.Track(TestTileMeshBuilder.BuildFill(new[] { FillSquareFeature() }, paint, Zoom, Extent, Tile));
            Assert.IsNotNull(mesh, "the fixture feature must produce fill geometry.");

            Material mat = bag.Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Bounds b = mesh.bounds;
            float  orthoSize = math.max(b.extents.x, b.extents.z) * 1.2f;

            var fillGo = bag.Track(new GameObject("PaintColorRender_Fill"));
            fillGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            fillGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var camGo  = bag.Track(new GameObject("PaintColorRender_Camera"));
            var camera = camGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(b.center.x, 500f, b.center.z);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = orthoSize;
            camera.farClipPlane       = 5000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = Color.black;
            camera.enabled            = false;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng($"paint-color-fill-{tag}.png");
            return SampleLinear(snap);
        }

        /// <summary>
        /// The rendered albedo of a CONSTANT fill-color must be the authored colour, not its square (both
        /// carriers holding it) and not its raw sRGB triple (a <c>.linear</c> pre-conversion at the
        /// <c>_BaseColor</c> bind site — Unity already converts a Color-typed material property on upload).
        /// This is the only observer of the site-2 gamma convention; 2a/2b compose CPU-side read-backs and
        /// cannot see an upload-time convention error.
        /// </summary>
        [Test]
        public void ConstantFillColor_RenderedPixel_MatchesAuthored()
        {
            double3 white = RenderFillLayer("\"#ffffff\"", "fill-white");
            double3 black = RenderFillLayer("\"#000000\"", "fill-black");
            double3 layer = RenderFillLayer($"\"{AuthoredHex}\"", "fill-authored");

            double3 span = white - black;
            Assert.Greater(math.cmin(span), 0.05,
                $"the white and black reference arms must be separable to calibrate against " +
                $"(white={white}, black={black}).");

            double3 measured = (layer - black) / span;

            Assert.IsTrue(ColorUtility.TryParseHtmlString(AuthoredHex, out Color authored));
            Color   expectedLinear = authored.linear;
            double3 expect3  = new double3(expectedLinear.r, expectedLinear.g, expectedLinear.b);
            double3 squared  = expect3 * expect3;
            double3 srgb     = new double3(authored.r, authored.g, authored.b);

            Debug.Log($"[PaintColorRender] fill measured={measured} authored(linear)={expect3} " +
                      $"squared={squared} authored(sRGB)={srgb}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: a CONSTANT fill-color must reach the fragment ONCE, via the linear " +
                    $"value Unity converts _BaseColor to on upload. measured={measured} " +
                    $"authored(linear)={expect3} authored²={squared} authored(sRGB)={srgb}. " +
                    $"Landing on authored² means both carriers hold it (site 2 shipped without the " +
                    $"site-1 gate). Landing on the sRGB triple means a .linear conversion was added at " +
                    $"the _BaseColor bind site — the Color-typed bind defect.");
        }

        // ── the eased colour renders in sRGB ─────────────────────────────────────────────────────

        /// <summary>As <see cref="RenderFillLayer"/>, but settles at <paramref name="oldColorToken"/> first,
        /// then retargets to <paramref name="newColorToken"/> and samples mid-ease at <paramref name="atSeconds"/>
        /// (duration 1s). Pins WHERE the mix happens: in sRGB (this file's authored space), with Unity
        /// converting on upload — the same convention <see cref="ConstantFillColor_RenderedPixel_MatchesAuthored"/>
        /// pins for the unanimated path.</summary>
        private static double3 RenderFillLayerEased(string oldColorToken, string newColorToken, double atSeconds, string tag)
        {
            Fill.PaintProperties oldPaint = TestStyle.FillPaint($"{{\"fill-color\":{oldColorToken}}}");
            Fill.PaintProperties newPaint = TestStyle.FillPaint($"{{\"fill-color\":{newColorToken}}}");
            using var bag = new ObjectDisposalBag();
            Mesh mesh = bag.Track(TestTileMeshBuilder.BuildFill(new[] { FillSquareFeature() }, oldPaint, Zoom, Extent, Tile));
            Assert.IsNotNull(mesh, "the fixture feature must produce fill geometry.");

            Material mat = bag.Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(oldPaint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0)); // settle at the origin colour

            applier.SetTransition(new StyleTransition { DurationSeconds = 1.0 }, nowSeconds: 0.0);
            MaterialFactory.BindFillPaintToApplier(newPaint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, atSeconds)); // sample mid-ease

            Bounds b = mesh.bounds;
            float  orthoSize = math.max(b.extents.x, b.extents.z) * 1.2f;

            var fillGo = bag.Track(new GameObject("PaintColorRender_FillEased"));
            fillGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            fillGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var camGo  = bag.Track(new GameObject("PaintColorRender_CameraEased"));
            var camera = camGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(b.center.x, 500f, b.center.z);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = orthoSize;
            camera.farClipPlane       = 5000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = Color.black;
            camera.enabled            = false;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng($"paint-color-fill-eased-{tag}.png");
            return SampleLinear(snap);
        }

        /// <summary>
        /// At t=0.25 of a #6699CC → #CC6633 transition (D=1), the measured albedo must equal
        /// linear(MixPremultiplied(A,B,0.15625)) — the mix happens in sRGB (the authored space) and
        /// Unity converts on upload, exactly like the unanimated Constant path. Both fixture colours are
        /// non-white on every channel and their channels are permuted, so a channel swap cannot pass.
        /// </summary>
        [Test]
        public void EasedColor_RendersTheMixedPixel_AtTheQuarterPoint()
        {
            const string HexA = "#6699CC";
            const string HexB = "#CC6633";
            var colorA = new MapRenderer.Core.Expressions.Color(0.4, 0.6, 0.8, 1.0);
            var colorB = new MapRenderer.Core.Expressions.Color(0.8, 0.4, 0.2, 1.0);

            double3 white = RenderFillLayer("\"#ffffff\"", "fill-eased-white");
            double3 black = RenderFillLayer("\"#000000\"", "fill-eased-black");
            double3 layer = RenderFillLayerEased($"\"{HexA}\"", $"\"{HexB}\"", 0.25, "quarter");

            double3 span = white - black;
            Assert.Greater(math.cmin(span), 0.05,
                $"the white and black reference arms must be separable to calibrate against " +
                $"(white={white}, black={black}).");

            double3 measured = (layer - black) / span;

            var mixedSrgb = MapRenderer.Core.Expressions.Color.MixPremultiplied(colorA, colorB, 0.15625);
            Color expectedLinear = new Color((float)mixedSrgb.R, (float)mixedSrgb.G, (float)mixedSrgb.B, 1f).linear;
            double3 expect3 = new double3(expectedLinear.r, expectedLinear.g, expectedLinear.b);
            double3 srgb    = new double3(mixedSrgb.R, mixedSrgb.G, mixedSrgb.B);

            Debug.Log($"[PaintColorRender] eased measured={measured} authored(linear)={expect3} authored(sRGB)={srgb}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: the eased colour must reach the fragment as the LINEAR conversion of " +
                    $"the sRGB mix. measured={measured} authored(linear)={expect3} authored(sRGB)={srgb}. " +
                    $"Landing on the sRGB triple would mean the mix was pre-converted to linear before " +
                    $"SetColor — the Color-typed bind defect shape, applied to the transition path.");
        }
    }
}
