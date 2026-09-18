// Unity EditMode only — runs a real SymbolPlacementSystem Tick and reads back a built Mesh.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// The halo's teeth after it left the shader. There is no _HaloColor/_HaloWidthPx/_HaloBlurPx uniform to
// assert against any more: the text shader takes one colour and one pair of device-px widenings, and a halo
// is a SECOND copy of the label's glyph run carrying text-halo-* in exactly those channels
// (WorldSymbolRenderer.Emit). So every halo claim is now observable in ONE place — the emitted mesh — and
// this fixture reads it there: that the run exists, that it is submitted BEFORE the text run, that its
// width/blur reach the vertex in DEVICE px, that its colour is linearized exactly once, and that a haloless
// layer emits nothing extra.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolHaloEmitTests
    {
        private static readonly GeoCoordinate Anchor =
            new GeoCoordinate { Latitude = 48.2082, Longitude = 16.3738 };

        // Authored text-halo-*, in the units a style uses: sRGB colour, LOGICAL px.
        private const float HaloWidthLogicalPx = 2.5f;
        private const float HaloBlurLogicalPx  = 1.5f;
        private const float HaloAlpha          = 0.7f;
        private static readonly float3 HaloSrgb = new float3(0.5f, 0.25f, 0.75f); // no channel equal: a
                                                                                  // swapped channel is visible
        private static readonly float3 TextSrgb = new float3(0.1f, 0.9f, 0.2f);

        private static SymbolPaint Paint(float haloWidthPx) => new SymbolPaint
        {
            TextColor   = new float4(TextSrgb, 1f),
            Opacity     = 1f,
            HaloColor   = new float4(HaloSrgb, HaloAlpha),
            HaloWidthPx = haloWidthPx,
            HaloBlurPx  = HaloBlurLogicalPx,
        };

        private static SymbolTileBuffer MakeLabel(in SymbolPaint paint, int glyphCount)
        {
            // At the camera's own anchor: a label anywhere else risks the distance cull, which would read as
            // "the halo did not emit" rather than as the precondition failure it is.
            double3 anchorRender = new WebMercatorProjection().Project(Anchor);
            var quads = new List<SymbolQuad>();
            for (int g = 0; g < glyphCount; g++)
                quads.Add(new SymbolQuad
                {
                    TopLeft       = new float2(-6f + g * 10f, 18f),
                    BottomRight   = new float2(12f + g * 10f, 0f),
                    UvTopLeft     = new float2(0.1f, 0.1f),
                    UvBottomRight = new float2(0.4f, 0.4f),
                    LineIndex     = 0,
                });

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, float2.zero, new float2(18f, 18f),
                up: new double3(0.0, 1.0, 0.0), paint: paint, textSizePx: 24f, sortKey: 0f,
                featureIndex: 0, tileKey: 0L);
            return buffer;
        }

        /// <summary>Runs one real Tick at <paramref name="devicePixelRatio"/> and returns the built world
        /// mesh's stream-0 vertices, its stream-1 opacity and its index buffer.</summary>
        private static void RunTick(SymbolTileBuffer buffer, double devicePixelRatio,
            out WorldBillboardVertex[] vertices, out float[] opacity, out int[] indices)
        {
            var projection = new WebMercatorProjection();
            var camGo = new GameObject("SymbolHaloEmit_TestCamera");
            // Owned so the finally can release them. A camera left ENABLED with a live target keeps
            // rendering on every Editor update after this fixture returns, and the visual fixtures that run
            // later sample real pixels — leaking GPU state out of here shows up as THEIR failure, which is a
            // miserable thing to debug.
            RenderTexture target = null;
            Material textMaterial = null;
            try
            {
                var uCam = camGo.AddComponent<Camera>();
                uCam.enabled = false;
                target = new RenderTexture(320, 240, 0);
                uCam.targetTexture = target;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude, Altitude = 0.0 },
                    zoom: 6.0, heading: 0.0, tilt: 0.0), projection: projection);
                mapCamera.DevicePixelRatio = devicePixelRatio;

                var frame = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(Anchor),
                    Rebase            = float3x3.identity,
                };
                var atlasTexture = BuildTinyAtlasTexture();
                textMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                var system = new SymbolPlacementSystem(mapCamera, worldTextBase: textMaterial);
                try
                {
                    // Collision verdicts apply one Tick late — duplicate before reading placement.
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh),
                        "precondition: the label must place and build a world slot mesh.");
                    WorldMeshReadback.Read(mesh, out vertices, out opacity);
                    indices = mesh.GetIndices(0);
                }
                finally
                {
                    system.Dispose();
                    atlasTexture.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                if (target != null)
                {
                    if (RenderTexture.active == target) RenderTexture.active = null;
                    target.Release();
                    Object.DestroyImmediate(target);
                }
                if (textMaterial != null) Object.DestroyImmediate(textMaterial);
            }
        }

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph
            {
                Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16],
            };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static float3 Linear(in float3 srgb)
        {
            Color c = new Color(srgb.x, srgb.y, srgb.z, 1f).linear;
            return new float3(c.r, c.g, c.b);
        }

        /// <summary>
        /// A label with a halo emits its glyph run TWICE — the text corners then the halo corners — and the
        /// halo's triangles come FIRST in the index buffer. That ordering is the whole point of the change:
        /// with ZWrite Off and one queue, submission order IS the layering, so a halo that indexed second
        /// would paint over the glyphs it is meant to sit behind.
        /// </summary>
        [Test]
        public void HaloedLabel_EmitsBothRuns_WithEveryHaloTriangleBeforeEveryTextTriangle()
        {
            const int glyphs = 3;
            RunTick(MakeLabel(Paint(HaloWidthLogicalPx), glyphs), devicePixelRatio: 1.0,
                out WorldBillboardVertex[] v, out _, out int[] indices);

            Assert.AreEqual(glyphs * 8, v.Length,
                $"{glyphs} glyphs x (4 text corners + 4 halo corners) — a halo that did not emit reads as {glyphs * 4}.");
            Assert.AreEqual(glyphs * 12, indices.Length, "two triangles per run per glyph");

            // Vertices [0, 4g) are the text run and [4g, 8g) the halo run (Emit keeps the text quad at the
            // base of the block it has always been at). The FIRST half of the index buffer must reference
            // only the halo half, and the second half only the text half.
            int textVertEnd = glyphs * 4;
            for (int i = 0; i < glyphs * 6; i++)
                Assert.GreaterOrEqual(indices[i], textVertEnd,
                    $"index {i} is in the first (halo) block but points at a TEXT vertex ({indices[i]}) — the " +
                    "halo would rasterize into the text run's place.");
            for (int i = glyphs * 6; i < indices.Length; i++)
                Assert.Less(indices[i], textVertEnd,
                    $"index {i} is in the second (text) block but points at a HALO vertex ({indices[i]}) — the " +
                    "halo would draw LAST, i.e. over the glyphs it must sit behind.");
        }

        /// <summary>
        /// The halo run carries <c>text-halo-color</c> (linearized exactly once, its own alpha folded onto
        /// the opacity stream) and <c>text-halo-width</c>/<c>-blur</c>; the text run carries
        /// <c>text-color</c> and a ZERO widening, which is what makes the shader's one shading path render a
        /// plain glyph.
        /// </summary>
        [Test]
        public void HaloRun_CarriesHaloColourAndWidening_TextRunCarriesNeither()
        {
            RunTick(MakeLabel(Paint(HaloWidthLogicalPx), glyphCount: 1), devicePixelRatio: 1.0,
                out WorldBillboardVertex[] v, out float[] opacity, out _);
            Assert.AreEqual(8, v.Length, "precondition: one glyph, both runs");

            float3 expectedText = Linear(TextSrgb);
            float3 expectedHalo = Linear(HaloSrgb);

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(0f, v[i].SdfWidenPx.x, 1e-6f, $"text corner {i}: edge widening must be ZERO");
                Assert.AreEqual(0f, v[i].SdfWidenPx.y, 1e-6f, $"text corner {i}: AA widening must be ZERO");
                AssertColor(expectedText, v[i].ColorRGB, $"text corner {i}");
            }

            for (int i = 4; i < 8; i++)
            {
                Assert.AreEqual(HaloWidthLogicalPx, v[i].SdfWidenPx.x, 1e-4f,
                    $"halo corner {i}: text-halo-width must reach the vertex (dpr 1 ⇒ logical == device).");
                Assert.AreEqual(HaloBlurLogicalPx, v[i].SdfWidenPx.y, 1e-4f,
                    $"halo corner {i}: text-halo-blur must reach the vertex.");
                AssertColor(expectedHalo, v[i].ColorRGB,
                    $"halo corner {i} — a value near the authored sRGB means the sRGB→linear convert was " +
                    "dropped; far below linear means it was applied twice");
                Assert.AreEqual(opacity[i - 4] * HaloAlpha, opacity[i], 1e-4f,
                    $"halo corner {i}: text-halo-color's alpha multiplies the text opacity — the shader has " +
                    "no halo alpha of its own to apply it with.");
            }
        }

        private static void AssertColor(in float3 expected, in float3 actual, string what)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f, $"{what}: red");
            Assert.AreEqual(expected.y, actual.y, 1e-4f, $"{what}: green");
            Assert.AreEqual(expected.z, actual.z, 1e-4f, $"{what}: blue");
        }

        /// <summary>
        /// <c>text-halo-width</c>/<c>-blur</c> are authored in LOGICAL px and the SDF shader measures in
        /// DEVICE px, so both are scaled by the live device-pixel ratio — TOGETHER (S107). Scaling one
        /// without the other renders the halo inconsistently on a 2x panel; not scaling at all renders it
        /// half as thick as styled.
        /// </summary>
        [Test]
        public void HaloWidthAndBlur_ScaleTogetherWithDevicePixelRatio()
        {
            RunTick(MakeLabel(Paint(HaloWidthLogicalPx), glyphCount: 1), devicePixelRatio: 2.0,
                out WorldBillboardVertex[] v, out _, out _);
            Assert.AreEqual(8, v.Length, "precondition: one glyph, both runs");

            for (int i = 4; i < 8; i++)
            {
                Assert.AreEqual(HaloWidthLogicalPx * 2f, v[i].SdfWidenPx.x, 1e-4f,
                    $"halo corner {i}: text-halo-width must read {HaloWidthLogicalPx * 2f} device px at dpr 2.");
                Assert.AreEqual(HaloBlurLogicalPx * 2f, v[i].SdfWidenPx.y, 1e-4f,
                    $"halo corner {i}: text-halo-blur takes the SAME ratio as the width.");
            }
        }

        /// <summary>
        /// A zero <c>text-halo-width</c> — the spec default, and most layers — emits no halo run at all, so a
        /// haloless label costs exactly what it did before the halo left the shader.
        /// </summary>
        [Test]
        public void HalolessLabel_EmitsOneRunOnly()
        {
            RunTick(MakeLabel(Paint(haloWidthPx: 0f), glyphCount: 2), devicePixelRatio: 1.0,
                out WorldBillboardVertex[] v, out _, out int[] indices);

            Assert.AreEqual(2 * 4, v.Length, "a zero-width halo must emit NO second run");
            Assert.AreEqual(2 * 6, indices.Length, "…and no second set of triangles");
            for (int i = 0; i < v.Length; i++)
                Assert.AreEqual(float2.zero, v[i].SdfWidenPx, $"corner {i}: the text run never widens");
        }
    }
}
