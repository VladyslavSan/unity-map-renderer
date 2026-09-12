#if UNITY_EDITOR
// Unity-only: render test requiring a GPU context (SnapshotRenderer). Degrades to Inconclusive when the
// context is unavailable in batch mode, per the other snapshot fixtures.
// NOT included in Tools/core-tests/core-tests.csproj.
//
// UMR-135: text-halo-color reaches a Color-typed material property (_HaloColor, SymbolTextWorld.shader),
// which Unity gamma-converts sRGB->linear on upload in Linear colour space — the same convention
// PaintColorRenderTests.ConstantLineColor_RenderedPixel_MatchesAuthored pins for _BaseColor. If production
// ALSO pre-converts on the CPU (SymbolRenderLayer.BindHalo's `.linear`), the value is converted twice and
// renders far too dark.
//
// This isolates the halo term from the fill term (SymbolTextWorld_ForwardPass.hlsl's
// `lerp(_HaloColor.rgb, input.color.rgb, fillAlpha)`) by forcing fillAlpha to 0 via two SDF-shape overrides
// (_SdfEdge/_SdfPixelRange — never _HaloColor/_HaloWidthPx/_HaloBlurPx, which stay exactly as production
// bound them). That makes the centre pixel PURE halo colour, opaque, over a black clear — the shader is
// unlit, so unlike PaintColorRenderTests no white/black calibration arm is needed. No glyph/atlas fixture
// is needed either: the quad's UV is constant, so its actual sampled texel is irrelevant once _SdfEdge(3)
// exceeds the maximum possible SDF sample (1) by a comfortable margin — see RenderHalo.

using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text.Placement;
using Color = UnityEngine.Color;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class SymbolHaloColorRenderTests
    {
        private const int SnapSize = 128;

        // 0.5 grey — the discriminating value: ~17.6% of correct if the sRGB->linear conversion is applied
        // twice (UMR-135's predicted magnitude).
        private const string AuthoredHaloHex = "#808080";

        private static Symbol.StyleLayer BuildSymbolLayer(string haloHex)
        {
            string styleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" },
                      ""paint"": { ""text-halo-color"": """ + haloHex + @""", ""text-halo-width"": 20, ""text-halo-blur"": 0 } }
                ]
            }";
            StyleDocument style = StyleParser.Parse(styleJson);
            return (Symbol.StyleLayer)style.Layers[0];
        }

        /// <summary>A degenerate screen-space billboard: every corner shares one <c>AnchorLocal</c> (world
        /// position is irrelevant — <see cref="WorldBillboardMeshBuilder"/>'s corner offset is added in
        /// clip space); only <see cref="WorldBillboardVertex.Offset"/> (logical px) gives it screen size.
        /// UV is constant so the fragment's sampled texel plays no part in the measurement (see this file's
        /// header).</summary>
        private static Mesh BuildQuad(float halfSizePx)
        {
            var corners = new[]
            {
                new float2(-halfSizePx,  halfSizePx),
                new float2( halfSizePx,  halfSizePx),
                new float2( halfSizePx, -halfSizePx),
                new float2(-halfSizePx, -halfSizePx),
            };

            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            for (int i = 0; i < 4; i++)
                vertices[i] = new WorldBillboardVertex
                {
                    AnchorLocal = float3.zero,
                    ColorRGB    = new float3(1f, 0f, 0f), // never sampled — fillAlpha is forced to 0
                    Uv          = float2.zero,
                    Page        = 0f,
                    Offset      = corners[i],
                    AlignFlags  = 0f,
                    Tangent     = float3.zero,
                    Up          = float3.zero,
                };
            var opacity = new NativeArray<float>(4, Allocator.Temp);
            for (int i = 0; i < 4; i++) opacity[i] = 1f;
            var indices = new NativeArray<int>(new[] { 0, 1, 2, 0, 2, 3 }, Allocator.Temp);

            var mesh = new Mesh { name = "SymbolHaloColorRender_Quad" };
            try { WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh); }
            finally { vertices.Dispose(); opacity.Dispose(); indices.Dispose(); }
            return mesh;
        }

        /// <summary>
        /// Renders one halo through the production bind (<see cref="SymbolRenderLayer.Create"/>). Returns
        /// the centre sample in linear RGB, or null with no GPU context.
        /// </summary>
        private static double3? RenderHalo(string haloHex)
        {
            Symbol.StyleLayer layer    = BuildSymbolLayer(haloHex);
            MapMaterialSet    settings = MapMaterialSetTestUtil.Load();
            SymbolRenderLayer renderLayer = SymbolRenderLayer.Create(layer, settings, initialZoom: 8.0, drawIndex: 0);
            Assert.IsNotNull(renderLayer.WorldTextMaterial, "MapMaterialSet.SymbolTextWorld must be assigned.");
            Material mat = renderLayer.WorldTextMaterial;

            // Force fillAlpha == 0 everywhere, regardless of the atlas texture's actual content: a tiny
            // _SdfPixelRange(0) zeroes SymbolTextWorld_ForwardPass.hlsl's `unitRange`, floor-ing
            // screenPxRange to its 1.0 minimum REGARDLESS of the atlas texel size or the (constant) UV's
            // screen-space derivative. _SdfEdge(3) exceeds the maximum possible SDF sample (1) by a
            // comfortable margin, so screenDist is comfortably negative everywhere.
            mat.SetFloat(Shader.PropertyToID("_SdfPixelRange"), 0f);
            mat.SetFloat(Shader.PropertyToID("_SdfEdge"), 3f);
            mat.SetVector(Shader.PropertyToID("_ScreenParamsLogical"), new Vector4(SnapSize, SnapSize, 0f, 0f));

            // _MainTex is a Texture2DArray sampler — every other production/test caller of this shader binds
            // a real atlas before rendering; an unbound array sampler renders nothing on this backend. Its
            // content is irrelevant here (the overrides above force fillAlpha to 0 regardless of what is
            // sampled), so a single-texel placeholder is enough.
            var blankAtlas = new Texture2DArray(1, 1, 1, TextureFormat.R8, false);
            blankAtlas.SetPixels(new[] { UnityEngine.Color.black }, 0);
            blankAtlas.Apply();
            mat.SetTexture(Shader.PropertyToID("_MainTex"), blankAtlas);

            Mesh mesh = BuildQuad(halfSizePx: 40f);

            var quadGo = new GameObject("SymbolHaloColorRender_Quad");
            quadGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            quadGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var camGo  = new GameObject("SymbolHaloColorRender_Camera");
            var camera = camGo.AddComponent<Camera>();
            camera.orthographic      = true;
            camera.orthographicSize  = 1f;
            camera.nearClipPlane     = 0.1f;
            camera.farClipPlane      = 100f;
            camera.clearFlags        = CameraClearFlags.SolidColor;
            camera.backgroundColor   = Color.black;
            camera.enabled           = false;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.transform.rotation = Quaternion.identity;

            using var snap = new SnapshotRenderer(SnapSize, SnapSize);
            try
            {
                snap.Render(camera);
                snap.WritePng($"symbol-halo-{haloHex.TrimStart('#')}.png");
                if (snap.IsAllBlack()) return null; // no GPU context
                return SampleCenterLinear(snap);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(quadGo);
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(blankAtlas);
            }
        }

        /// <summary>Mean linear RGB of the centre sample box. The render target is sRGB-encoded on
        /// readback, so the bytes are decoded through <see cref="Color.linear"/> before averaging — mirrors
        /// <c>PaintColorRenderTests.SampleLinear</c>.</summary>
        private static double3 SampleCenterLinear(SnapshotRenderer snap)
        {
            const int half = 6;
            double3 sum = double3.zero;
            int n = 0;
            for (int y = SnapSize / 2 - half; y <= SnapSize / 2 + half; y++)
            for (int x = SnapSize / 2 - half; x <= SnapSize / 2 + half; x++)
            {
                int b = (y * SnapSize + x) * 4;
                Color lin = new Color(snap.RawPixels[b] / 255f, snap.RawPixels[b + 1] / 255f,
                                      snap.RawPixels[b + 2] / 255f, 1f).linear;
                sum += new double3(lin.r, lin.g, lin.b);
                n++;
            }
            return sum / n;
        }

        /// <summary>
        /// The rendered albedo of a halo whose <c>text-halo-color</c> is a NON-WHITE constant (0.5 grey)
        /// must be the authored colour, converted sRGB->linear exactly ONCE by Unity's upload of the
        /// Color-typed <c>_HaloColor</c> property. Pre-fix, <see cref="SymbolRenderLayer"/> ALSO converted on
        /// the CPU, so the value reaches the screen converted twice.
        /// </summary>
        [Test]
        public void HaloColor_RenderedPixel_MatchesAuthored()
        {
            double3? measured = RenderHalo(AuthoredHaloHex);
            if (measured == null)
            {
                Assert.Inconclusive("No GPU context (the halo arm rendered blank).");
                return;
            }

            Assert.IsTrue(ColorUtility.TryParseHtmlString(AuthoredHaloHex, out Color authored));
            Color   expected = authored.linear;
            double3 expect3  = new double3(expected.r, expected.g, expected.b);

            Debug.Log($"[SymbolHaloColorRender] measured={measured.Value} authored(linear)={expect3}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured.Value[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: text-halo-color must reach the fragment converted sRGB->linear ONCE. " +
                    $"measured={measured.Value} authored(linear)={expect3}. Landing far below authored means " +
                    $"SymbolRenderLayer.BindHalo's CPU `.linear` conversion is double-applying Unity's own " +
                    $"upload conversion of the Color-typed _HaloColor property (UMR-135).");
        }
    }
}
#endif
