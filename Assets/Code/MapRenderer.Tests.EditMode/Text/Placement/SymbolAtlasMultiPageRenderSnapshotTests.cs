// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
// NOT registered in core-tests.csproj.
//
// M-T3 (Stage M multi-atlas plan): a 2-page glyph atlas must render glyphs from BOTH pages correctly —
// the Texture2DArray sample must actually read the layer BillboardVertex.Page selects, not silently fall
// back to layer 0 (which would render page-0's leftover/garbage content, or nothing, at a page-1 UV).
//
// Approach (mirrors SymbolAtlasOrientationSnapshotTests' real-pipeline, off-screen-render, ink-pixel-
// count style): render the SAME real fixture glyph ('A') twice, at the SAME anchor/text-size —
//   (1) from a normal single-page atlas, where it packs onto Page 0 (the existing, already-proven path);
//   (2) from a FIXED atlas sized to exactly one glyph cell, pre-filled by a dummy filler glyph so the
//       real 'A' overflows onto Page 1.
// If the Texture2DArray upload/vertex Page/shader array-sample chain is wired correctly, both renders
// must produce near-identical ink coverage (same bitmap, different array layer). If the shader/vertex
// path silently samples layer 0 regardless of Page, the Page-1 render would show page 0's filler content
// (blank, since the filler has no bitmap) instead of 'A' — inkPage1 would collapse to ~0, failing the
// "meaningful ink" assertion below.

using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolAtlasMultiPageRenderSnapshotTests
    {
        private const int Size = 512;
        private const byte InkThreshold = 200; // background is white (255); black fill is well below this

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        [Test]
        public void RealAtlasGlyph_ForcedOntoPage1_RendersWithComparableInkCoverageToPage0()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            SdfGlyph a = stack.Glyphs[65u]; // 'A'

            // (1) Baseline: normal single-page (grow-mode) atlas — 'A' packs at Page 0.
            var page0Atlas = new GlyphAtlas();
            GlyphAtlasEntry page0Entry = page0Atlas.Append(a);
            Assert.AreEqual(0, page0Entry.Page, "fixture precondition: a fresh grow-mode atlas never pages");
            Assert.AreEqual(1, page0Atlas.PageCount);

            // (2) Forced multi-page: a FIXED atlas sized to exactly one 'A' cell. A filler glyph (no
            // bitmap — its content is irrelevant, only its cell size matters) fills page 0 completely, so
            // the real 'A' — appended second — overflows onto page 1.
            int2 cellA = a.CellSize;
            var page1Atlas = new GlyphAtlas(width: cellA.x, fixedHeight: cellA.y);
            var filler = new SdfGlyph { Codepoint = 0xFFFEu, Width = a.Width, Height = a.Height, Left = 0, Top = 0, Advance = 0, Bitmap = null };
            page1Atlas.Append(filler);
            GlyphAtlasEntry page1Entry = page1Atlas.Append(a);
            Assert.AreEqual(1, page1Entry.Page, "fixture precondition: 'A' must have overflowed onto page 1");
            Assert.AreEqual(2, page1Atlas.PageCount);

            int inkPage0 = RenderGlyphAndCountInk(page0Atlas, a);
            int inkPage1 = RenderGlyphAndCountInk(page1Atlas, a);

            Assert.Greater(inkPage0, 50, "DIAGNOSTIC baseline: the page-0 glyph must render a meaningful number of ink pixels");
            Assert.Greater(inkPage1, 50,
                "the page-1 glyph must render a meaningful number of ink pixels -- if this is ~0, the array sample is " +
                "reading an empty/wrong Texture2DArray layer instead of layer 1 (BillboardVertex.Page not reaching the fragment).");

            // Numeric ink-coverage bound: same glyph, same anchor/size, different array layer -> near-identical
            // coverage. A generous tolerance (not a byte-exact snapshot) absorbs anti-aliasing / rasterization
            // noise between the two independent draws while still catching "wrong layer" (which would either
            // blank out or draw a completely different, unrelated bitmap).
            Assert.That((float)inkPage1, Is.EqualTo((float)inkPage0).Within(inkPage0 * 0.2f),
                $"page-1 ink coverage ({inkPage1}px) must be comparable to page-0 ({inkPage0}px) -- same bitmap, " +
                $"different Texture2DArray layer.");
        }

        /// <summary>Lays out, uploads, ticks, and off-screen-renders a single glyph from <paramref name="atlas"/>
        /// (which must already have <paramref name="glyph"/> appended), returning the rendered ink-pixel count.</summary>
        private static int RenderGlyphAndCountInk(GlyphAtlas atlas, SdfGlyph glyph)
        {
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            TextLayoutResult layout = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default);

            var camGo = new GameObject("SymbolMultiPage_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));

            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                float3x3.identity);

            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);

            var label = new LabelInstance
            {
                AnchorRender = anchorRender,
                Layout = layout,
                Paint = LabelPaint.Default,
                TextSizePx = 220f,
                SortKey = 0f,
                FeatureIndex = 0,
                // Epic A / A1 Risk R1: a realistic containing tile keeps the world-anchored bake float32-safe
                // (TileKey=0 is ~2e7m away — see SymbolAtlasOrientationSnapshotTests' identical note).
                TileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14),
            };

            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new LabelPlacementSystem(mapCamera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var snap = new SnapshotRenderer(Size, Size);
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, new[] { label }, atlasTexture);
                system.Tick(in frame, new[] { label }, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the label's anchor must not be culled");

                snap.Render(uCam);
                byte[] px = snap.RawPixels; // RGBA32, row-major, top-left origin

                int inkPixelCount = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (px[i] < InkThreshold) inkPixelCount++;
                }
                return inkPixelCount;
            }
            finally
            {
                snap.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
