#if UNITY_EDITOR
// Unity-only: render tests requiring a GPU context (SnapshotRenderer). Degrade to Inconclusive when the
// context is unavailable in batch mode, per the other snapshot fixtures.
// NOT included in Tools/core-tests/core-tests.csproj.
//
// The cross-instrument half of PaintColorSingleApplyTests. That fixture re-implements the fragment's
// `_BaseColor.rgb × vColor.rgb` on the CPU from the two real sources and could drift from the HLSL; this one
// reads the quantity ITSELF off a rendered pixel. Neither substitutes for the other — but the CPU one is the
// headline, because this one can only report Inconclusive without a GPU.
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

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using Unity.Mathematics;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using Color = UnityEngine.Color;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class PaintColorRenderTests
    {
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
            => Line.PaintProperties.Parse(JsonParser.Parse($"{{\"line-color\":{colorToken},\"line-width\":{LineWidthPx}}}"));

        private static Line.LayoutProperties Layout()
            => Line.LayoutProperties.Parse(JsonParser.Parse("{}"));

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
                int b = (y * SnapW + x) * 4;
                Color lin = new Color(snap.RawPixels[b] / 255f, snap.RawPixels[b + 1] / 255f,
                                      snap.RawPixels[b + 2] / 255f, 1f).linear;
                sum += new double3(lin.r, lin.g, lin.b);
                n++;
            }
            return sum / n;
        }

        /// <summary>
        /// Renders one line layer end-to-end: the mesh through the production builder, the material through
        /// the production binder, framed on the mesh's own bounds. Returns the centre sample in linear RGB,
        /// or null when there is no GPU context.
        /// </summary>
        private static double3? RenderLayer(string colorToken, string tag)
        {
            Line.PaintProperties paint = Paint(colorToken);
            Mesh mesh = TestTileMeshBuilder.BuildLine(
                new[] { LineFeature() }, paint, Layout(), Zoom, Extent, Tile, double2.zero);
            Assert.IsNotNull(mesh, "the fixture feature must produce line geometry.");

            Material mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/Line base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindLinePaintToApplier(paint, applier, mat);
            applier.ApplyZoom(Zoom, 1.0);

            Bounds b = mesh.bounds;
            float  orthoSize = math.max(b.extents.x, b.extents.z) * 1.2f;

            var lineGo = new GameObject("PaintColorRender_Line");
            lineGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            lineGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var camGo  = new GameObject("PaintColorRender_Camera");
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
            try
            {
                snap.Render(camera);
                snap.WritePng($"paint-color-{tag}.png");
                if (snap.IsAllBlack()) return null; // caller decides: no GPU, or a genuinely black arm
                return SampleLinear(snap);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lineGo);
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// The rendered albedo of a layer whose <c>line-color</c> is a CONSTANT #6699CC must be the authored
        /// colour, not its square. Pre-fix the layer carried the colour in BOTH the uniform and the COLOR
        /// stream and the fragment multiplied them.
        /// </summary>
        [Test]
        public void ConstantLineColor_RenderedPixel_MatchesAuthored()
        {
            int   prevQuality      = QualitySettings.GetQualityLevel();
            var   prevAmbientMode  = RenderSettings.ambientMode;
            var   prevAmbientLight = RenderSettings.ambientLight;
            bool  prevFog          = RenderSettings.fog;
            QualitySettings.SetQualityLevel(0, false);
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f, 1f);
            RenderSettings.fog          = false;
            try
            {
                double3? white = RenderLayer("\"#ffffff\"", "white");
                if (white == null)
                {
                    Assert.Inconclusive("No GPU context (the white reference arm rendered blank).");
                    return;
                }
                // The black arm is legitimately near-black, so IsAllBlack cannot distinguish it from a
                // missing context — but the white arm above already proved the context exists.
                double3 black = RenderLayer("\"#000000\"", "black") ?? double3.zero;
                double3? layer = RenderLayer($"\"{AuthoredHex}\"", "authored");
                Assert.IsNotNull(layer, "the #6699CC arm rendered blank while the white arm did not.");

                double3 span = white.Value - black;
                Assert.Greater(math.cmin(span), 0.05,
                    $"the white and black reference arms must be separable to calibrate against " +
                    $"(white={white.Value}, black={black}).");

                double3 measured = (layer.Value - black) / span;

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
            finally
            {
                RenderSettings.fog          = prevFog;
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                QualitySettings.SetQualityLevel(prevQuality, false);
            }
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

            int   prevQuality      = QualitySettings.GetQualityLevel();
            var   prevAmbientMode  = RenderSettings.ambientMode;
            var   prevAmbientLight = RenderSettings.ambientLight;
            bool  prevFog          = RenderSettings.fog;
            QualitySettings.SetQualityLevel(0, false);
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f, 1f);
            RenderSettings.fog          = false;
            try
            {
                double3? opaque = RenderLayer("\"#ffffff\"", "alpha-opaque");
                if (opaque == null)
                {
                    Assert.Inconclusive("No GPU context (the opaque reference arm rendered blank).");
                    return;
                }
                double3? translucent = RenderLayer("[\"rgba\",255,255,255,0.5]", "alpha-half");
                Assert.IsNotNull(translucent, "the translucent arm rendered blank while the opaque arm did not.");

                Assert.Greater(math.cmin(opaque.Value), 0.05,
                    $"the opaque reference must be well clear of the black clear to divide by (got {opaque.Value}).");

                double3 ratio = translucent.Value / opaque.Value;
                Debug.Log($"[PaintColorRender] alpha ratio={ratio} opaque={opaque.Value} translucent={translucent.Value}");

                for (int c = 0; c < 3; c++)
                    Assert.That(ratio[c], Is.EqualTo(AuthoredAlpha).Within(0.03),
                        $"channel {c}: a CONSTANT line-color's authored ALPHA must reach the composite. " +
                        $"ratio={ratio} (translucent={translucent.Value}, opaque={opaque.Value}). " +
                        $"1.0 means the fragment ignores _BaseColor.a: the constant colour moved to the " +
                        $"uniform but the two forward passes still compute alpha from vColor.a alone, so the " +
                        $"authored alpha is gone. 0.25 would mean it is applied twice.");
            }
            finally
            {
                RenderSettings.fog          = prevFog;
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                QualitySettings.SetQualityLevel(prevQuality, false);
            }
        }
    }
}
#endif
