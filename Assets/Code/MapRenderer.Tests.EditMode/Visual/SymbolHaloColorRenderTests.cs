#if UNITY_EDITOR
// Unity-only: render test requiring a GPU context (SnapshotRenderer). Degrades to Inconclusive when the
// context is unavailable in batch mode, per the other snapshot fixtures.
// NOT included in Tools/core-tests/core-tests.csproj.
//
// UMR-135, now answered on BOTH sides — read this before "fixing" either arm. The question is unchanged
// (does text-halo-color reach the screen converted sRGB->linear exactly ONCE?) but there are two carriers to
// ask it of, and the conversion lives in a different place on each:
//   * CONSTANT text-halo-color rides the Color-TYPED `_HaloColor` uniform, which Unity converts on upload —
//     so SymbolRenderLayer.BindColorTint must NOT pre-convert (that was UMR-135's original finding), and the
//     vertex stream carries WHITE.
//   * every other kind bakes into the vertex COLOR stream on a second copy of the label's glyphs, which
//     Unity does NOT convert — so SymbolPlacementSystem.LinearHaloColor MUST pre-convert, the exact sibling
//     of LinearColor, and the uniform stays WHITE.
// The fragment multiplies the two, so exactly one of them is ever non-white. One arm below per carrier; a
// conversion done twice or not at all fails whichever arm owns it.
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

        /// <summary>What <c>SymbolFeatureExtractor.StreamRgba</c> bakes for a CONSTANT text-halo-color: the
        /// colour rides <c>_HaloColor</c>, so the vertex stream is white at this fixture's opaque alpha.</summary>
        private static readonly float4 StreamWhite = new float4(1f, 1f, 1f, 1f);

        // A ZOOM-kind text-halo-color that evaluates to AuthoredHaloHex at every zoom — the value is held
        // constant so the arms differ in CARRIER alone, never in the colour being measured.
        private const string ZoomHaloColorJson =
            @"[""interpolate"",[""linear""],[""zoom""],0,""" + AuthoredHaloHex + @""",22,""" + AuthoredHaloHex + @"""]";

        /// <param name="haloColorJson">The raw <c>text-halo-color</c> JSON value — a quoted hex for the
        /// CONSTANT arm, an expression for the vertex-stream arm.</param>
        private static Symbol.StyleLayer BuildSymbolLayer(string haloColorJson)
        {
            string styleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" },
                      ""paint"": { ""text-halo-color"": " + haloColorJson + @", ""text-halo-width"": 20, ""text-halo-blur"": 0 } }
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
        /// Renders one halo run: the production material (<see cref="SymbolRenderLayer.Create"/>, which
        /// binds <c>_HaloColor</c>) over a quad whose vertex COLOR is <paramref name="streamSrgb"/> taken
        /// through <c>SymbolPlacementSystem.LinearHaloColor</c> — the caller spells out that payload so the
        /// carrier under test is explicit, never derived from the production predicate. Optionally followed
        /// by a <see cref="SymbolRenderLayer.Restyle"/> to <paramref name="restyleToHex"/> — T7 (UMR-147).
        /// Returns the centre sample in linear RGB, or null with no GPU context.
        /// </summary>
        private static double3? RenderHalo(string haloColorJson, in float4 streamSrgb,
            string restyleToHex = null, double duration = 0.0)
        {
            Symbol.StyleLayer layer    = BuildSymbolLayer(haloColorJson);
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

            // T7: the restyle seam. AFTER the SDF overrides (Restyle/ApplyZoom touch only _TextColor and
            // _HaloColor, so the overrides above survive unaffected) and BEFORE the render, so the sample
            // reflects the eased/settled colour, not the initial bind.
            if (restyleToHex != null)
            {
                Symbol.StyleLayer newLayer = BuildSymbolLayer("\"" + restyleToHex + "\"");
                renderLayer.Restyle(newLayer, StyleTransition.Default, nowSeconds: 0.0);
                renderLayer.ApplyZoom(new StyleFrameInputs(8.0, 1.0, duration));
            }

            // _MainTex is a Texture2DArray sampler — every other production/test caller of this shader binds
            // a real atlas before rendering; an unbound array sampler renders nothing on this backend. Its
            // content is irrelevant here (the overrides above fix coverage regardless of what is sampled), so
            // a single-texel placeholder is enough.
            var blankAtlas = new Texture2DArray(1, 1, 1, TextureFormat.R8, false);
            blankAtlas.SetPixels(new[] { UnityEngine.Color.black }, 0);
            blankAtlas.Apply();
            mat.SetTexture(Shader.PropertyToID("_MainTex"), blankAtlas);

            // The production chain a halo vertex's colour actually comes from: what
            // SymbolFeatureExtractor.StreamRgba bakes for this carrier, through
            // SymbolPlacementSystem.LinearHaloColor (where the stream's one sRGB→linear convert lives).
            // dpr is 1 here, so text-halo-width's logical px are already device px.
            var paint = new SymbolPaint
            {
                HaloColor = streamSrgb,
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
                snap.WritePng($"symbol-halo-{(restyleToHex ?? haloColorJson).Trim('"', '#')}.png");
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
            double3? measured = RenderHalo($"\"{AuthoredHaloHex}\"", StreamWhite);
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
                    $"BindColorTint pre-converted on top of Unity's own upload conversion of the " +
                    $"Color-typed _HaloColor (UMR-135); far ABOVE means the uniform never reached the " +
                    $"fragment and the white vertex stream is all that is left.");
        }

        /// <summary>
        /// The SECOND carrier. A non-Constant <c>text-halo-color</c> bakes into the vertex COLOR stream, and
        /// the uniform stays white — so the one sRGB->linear conversion is
        /// <c>SymbolPlacementSystem.LinearHaloColor</c>'s. Without this arm the fixture above would leave
        /// that conversion unobserved: its stream payload is white, on which any conversion is a no-op.
        /// </summary>
        [Test]
        public void StreamCarriedHaloColor_RenderedPixel_MatchesAuthored()
        {
            Symbol.StyleLayer layer = BuildSymbolLayer(ZoomHaloColorJson);
            Assert.That(SymbolTextColorCarrier.RidesUniform(layer.Paint.HaloColor), Is.False,
                "precondition: this arm's text-halo-color must ride the vertex stream, not the uniform — " +
                "otherwise it silently duplicates the constant arm.");

            double3? measured = RenderHalo(ZoomHaloColorJson,
                HaloSrgbFloat4(layer.Paint.HaloColor.Evaluate(Zoom)));
            if (measured == null)
            {
                Assert.Inconclusive("No GPU context (the halo arm rendered blank).");
                return;
            }

            Assert.IsTrue(ColorUtility.TryParseHtmlString(AuthoredHaloHex, out Color authored));
            Color   expected = authored.linear;
            double3 expect3  = new double3(expected.r, expected.g, expected.b);

            Debug.Log($"[SymbolHaloColorRender] stream measured={measured.Value} authored(linear)={expect3}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured.Value[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: a stream-carried text-halo-color must reach the fragment converted " +
                    $"sRGB->linear ONCE. measured={measured.Value} authored(linear)={expect3}. Far ABOVE " +
                    $"authored means LinearHaloColor's `.linear` was dropped, leaving sRGB bytes in a " +
                    $"stream Unity never converts; far BELOW means it was applied twice.");
        }

        // #808080 -> #4099C0: no shared channel, none at 0/1 (plan §4 colour choice for the halo pair).
        private const string RestyledHaloHex = "#4099C0";

        /// <summary>
        /// <b>T7 (UMR-147).</b> A RESTYLED <c>text-halo-color</c> must reach the fragment as the NEW
        /// authored colour, converted sRGB->linear exactly once — <see cref="HaloColor_RenderedPixel_MatchesAuthored"/>
        /// only guards the initial <c>Create</c> write; this is the missing assertion over a restyle
        /// re-bind. RED-verify: <c>Restyle</c> omits the halo re-bind — the pixel stays <c>#808080</c> and
        /// the R assertion fires first (linear 0.2159 vs expected 0.0513).
        /// </summary>
        [Test]
        public void RestyledHaloColor_RenderedPixel_MatchesTheNewAuthored()
        {
            double3? measured = RenderHalo($"\"{AuthoredHaloHex}\"", StreamWhite,
                RestyledHaloHex, StyleTransition.Default.DurationSeconds);
            if (measured == null)
            {
                Assert.Inconclusive("No GPU context (the halo arm rendered blank).");
                return;
            }

            Assert.IsTrue(ColorUtility.TryParseHtmlString(RestyledHaloHex, out Color authored));
            Color   expected = authored.linear;
            double3 expect3  = new double3(expected.r, expected.g, expected.b);

            Debug.Log($"[SymbolHaloColorRender] restyled measured={measured.Value} authored(linear)={expect3}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured.Value[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: a RESTYLED text-halo-color must reach the fragment converted sRGB->linear " +
                    $"ONCE, at the NEW authored value. measured={measured.Value} authored(linear)={expect3}. " +
                    "Landing at the OLD authored value means Restyle never re-bound the halo.");
        }
    }
}
#endif
