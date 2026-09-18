#if UNITY_EDITOR
// Unity-only: render test requiring a GPU context (SnapshotRenderer). Degrades to Inconclusive when the
// context is unavailable in batch mode, per the other snapshot fixtures.
// NOT included in Tools/core-tests/core-tests.csproj.
//
// UMR-135, INVERTED — read this before "fixing" the assertion back. The question is unchanged (does
// text-halo-color reach the screen converted sRGB->linear exactly ONCE?) but the answer moved to the other
// side. UMR-135's finding was that `_HaloColor` is a Color-TYPED material property, which Unity converts on
// upload, so SymbolRenderLayer had to NOT pre-convert. That uniform no longer exists: the text shader has no
// halo term at all, and a halo is a second copy of the label's glyphs carrying text-halo-color in the vertex
// COLOR stream (SymbolTextWorld_ForwardPass.hlsl's header). Unity does not convert a vertex stream, so
// SymbolPlacementSystem.LinearHaloColor now MUST pre-convert — the exact sibling of LinearColor, which has
// always done it for text-color. Same rendered colour, conversion moved one step upstream; this fixture
// still fails if it happens twice or not at all.
//
// The quad below carries exactly what WorldSymbolRenderer.Emit's halo run writes, taken through the
// production linearization — never from the authored hex, which is what makes the conversion observable. Two
// SDF-shape overrides (_SdfEdge/_SdfRangeTexels, never the halo values) drive coverage to a flat 1 so the
// centre pixel is the pure halo colour, opaque, over a black clear — the shader is unlit, so unlike
// PaintColorRenderTests no white/black calibration arm is needed. No glyph/atlas fixture is needed either:
// the quad's UV is constant, so its sampled texel plays no part — see RenderHalo.

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
        // twice, ~2.2x if it is skipped entirely.
        private const string AuthoredHaloHex = "#808080";

        // The zoom the layer's constant halo is evaluated at. Any value: the fixture's halo is a constant.
        private const double Zoom = 8.0;

        private static float4 HaloSrgbFloat4(MapRenderer.Core.Expressions.Color c)
            => new float4((float)c.R, (float)c.G, (float)c.B, (float)c.A);

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

        /// <summary>A degenerate screen-space billboard carrying one halo run's vertex payload: every corner
        /// shares one <c>AnchorLocal</c> (world position is irrelevant — the corner offset is added in clip
        /// space); only <see cref="WorldBillboardVertex.Offset"/> (logical px) gives it screen size. UV is
        /// constant so the fragment's sampled texel plays no part (see this file's header).</summary>
        private static Mesh BuildHaloQuad(float halfSizePx, in float4 haloColorLinear, float haloWidthPx)
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
                    ColorRGB    = haloColorLinear.xyz,    // the value under test
                    Uv          = float2.zero,
                    Page        = 0f,
                    Offset      = corners[i],
                    AlignFlags  = 0f,
                    Tangent     = float3.zero,
                    Up          = float3.zero,
                    SdfWidenPx  = new float2(haloWidthPx, 0f),
                };
            var opacity = new NativeArray<float>(4, Allocator.Temp);
            for (int i = 0; i < 4; i++) opacity[i] = haloColorLinear.w;
            var indices = new NativeArray<int>(new[] { 0, 1, 2, 0, 2, 3 }, Allocator.Temp);

            var mesh = new Mesh { name = "SymbolHaloColorRender_Quad" };
            try { WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh); }
            finally { vertices.Dispose(); opacity.Dispose(); indices.Dispose(); }
            return mesh;
        }

        /// <summary>
        /// Renders one halo run: the production material (<see cref="SymbolRenderLayer.Create"/>) over a quad
        /// whose colour came through <c>SymbolPlacementSystem.LinearHaloColor</c>. Returns the centre sample
        /// in linear RGB, or null with no GPU context.
        /// </summary>
        private static double3? RenderHalo(string haloHex)
        {
            Symbol.StyleLayer layer    = BuildSymbolLayer(haloHex);
            MapMaterialSet    settings = MapMaterialSetTestUtil.Load();
            SymbolRenderLayer renderLayer = SymbolRenderLayer.Create(layer, settings, initialZoom: Zoom, drawIndex: 0);
            Assert.IsNotNull(renderLayer.WorldTextMaterial, "MapMaterialSet.SymbolTextWorld must be assigned.");
            Material mat = renderLayer.WorldTextMaterial;

            // Drive coverage to a flat 1 regardless of the atlas texture's content: a tiny _SdfRangeTexels(0)
            // zeroes the fragment's `unitRange`, floor-ing screenPxRange to its 1.0 minimum REGARDLESS of the
            // atlas texel size or the (constant) UV's screen-space derivative, so screenDist is exactly
            // -_SdfEdge. The styled 20px halo widening then swamps _SdfEdge(3) and saturates.
            mat.SetFloat(Shader.PropertyToID("_SdfRangeTexels"), 0f);
            mat.SetFloat(Shader.PropertyToID("_SdfEdge"), 3f);
            mat.SetVector(Shader.PropertyToID("_ScreenParamsLogical"), new Vector4(SnapSize, SnapSize, 0f, 0f));

            // _MainTex is a Texture2DArray sampler — every other production/test caller of this shader binds
            // a real atlas before rendering; an unbound array sampler renders nothing on this backend. Its
            // content is irrelevant here (the overrides above fix coverage regardless of what is sampled), so
            // a single-texel placeholder is enough.
            var blankAtlas = new Texture2DArray(1, 1, 1, TextureFormat.R8, false);
            blankAtlas.SetPixels(new[] { UnityEngine.Color.black }, 0);
            blankAtlas.Apply();
            mat.SetTexture(Shader.PropertyToID("_MainTex"), blankAtlas);

            // The production chain a halo vertex's colour actually comes from: the parsed style paint,
            // through SymbolPlacementSystem.LinearHaloColor (which is where the one sRGB→linear convert now
            // lives). dpr is 1 here, so text-halo-width's logical px are already device px.
            var paint = new SymbolPaint
            {
                HaloColor = HaloSrgbFloat4(layer.Paint.HaloColor.Evaluate(Zoom)),
                Opacity   = 1f,
            };
            Mesh mesh = BuildHaloQuad(halfSizePx: 40f,
                SymbolPlacementSystem.LinearHaloColor(paint),
                (float)layer.Paint.HaloWidth.Evaluate(Zoom));

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
        /// The rendered albedo of a halo whose <c>text-halo-color</c> is a NON-WHITE constant (0.5 grey) must
        /// be the authored colour, converted sRGB->linear exactly ONCE — now by
        /// <c>SymbolPlacementSystem.LinearHaloColor</c>, because the vertex COLOR stream it rides is one Unity
        /// does not convert. See this file's header for why that is the opposite of what UMR-135 concluded.
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
                    $"measured={measured.Value} authored(linear)={expect3}. Far BELOW authored means " +
                    $"LinearHaloColor's `.linear` is landing on top of a second conversion; far ABOVE " +
                    $"means it was dropped, leaving sRGB bytes in a stream Unity never converts.");
        }
    }
}
#endif
