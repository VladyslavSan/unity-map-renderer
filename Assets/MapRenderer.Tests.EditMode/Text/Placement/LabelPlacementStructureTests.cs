// Unity EditMode only — reads source files under Application.dataPath + needs a real Camera/Mesh. NOT
// registered in core-tests.csproj.

using System.Collections.Generic;
using System.IO;
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
    /// <summary>
    /// S20 T5: labels are the per-frame path, never the static tile path.
    /// (a) STRUCTURAL — a grep guard: nothing under the label-placement source tree calls
    ///     <c>ITileRenderBackend.AddTileLayer</c>.
    /// (b) BEHAVIORAL — the billboard buffer is rebuilt from scratch every <see cref="LabelPlacementSystem.Tick"/>,
    ///     not cached/accumulated across calls.
    /// </summary>
    [TestFixture]
    public class LabelPlacementStructureTests
    {
        // ── (a) Structural: grep guard ───────────────────────────────────────────────────────────

        [Test]
        public void SourceTree_NeverReferencesAddTileLayer()
        {
            string dir = Path.Combine(Application.dataPath, "MapRenderer.Unity", "Text", "Placement");
            Assert.IsTrue(Directory.Exists(dir), $"expected the label-placement source directory to exist at {dir}");

            // Match the CALL form "AddTileLayer(" (no space before the paren) so a doc comment discussing
            // the constraint in prose ("must never call AddTileLayer (the static ... path)") is not itself
            // flagged as an offender -- only an actual invocation is.
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (text.Contains("AddTileLayer("))
                {
                    offenders.Add(file);
                }
            }

            Assert.IsEmpty(offenders,
                "The per-frame label placement path must NEVER call AddTileLayer (the static per-(tile,layer) " +
                "mesh path) -- it is a structurally separate submission path (T5). Offending files:\n" +
                string.Join("\n", offenders));
        }

        // ── (b) Behavioral: rebuilt every Tick, never cached/accumulated ─────────────────────────

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph
            {
                Codepoint = 65,
                Width = 10,
                Height = 10,
                Left = 0,
                Top = 8,
                Advance = 12,
                Bitmap = new byte[16 * 16],
            };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // AnchorRender is render-space PRE-RTC (the same space projection.Project(geo) emits) -- NOT a
        // small local offset. It must be built relative to the frame's SceneOriginRender (a large absolute
        // Mercator coordinate), or TryProjectAnchor's rebase lands it far outside the viewport and every
        // label is silently culled (the bug this comment fixes: LastQuadCount was 0, not 2/1).
        private static LabelInstance MakeLabel(int featureIndex, double3 sceneOriginRender)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f),
                    BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f),
                    UvBottomRight = new float2(0.4f, 0.4f),
                    LineIndex = 0,
                },
            };
            var layout = new TextLayoutResult { Quads = quads, BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1 };
            return new LabelInstance
            {
                AnchorRender = sceneOriginRender + new double3(featureIndex * 5.0, 0.0, featureIndex * 2.0),
                Layout = layout,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                SortKey = 0f,
                FeatureIndex = featureIndex,
                TileKey = 0L,
                // AllowOverlap: this test asserts the buffer is REBUILT-not-accumulated every Tick (2/1/2
                // quads), which is orthogonal to Slice-2 collision. Without it, whether the two nearby
                // anchors' boxes overlap (and one gets culled) is projection-dependent and would make the
                // quad-count assertion flaky. Collision itself is covered by LabelCollisionTests (T1).
                AllowOverlap = true,
            };
        }

        [Test]
        public void Tick_AdvancesTickCount_AndRebuildsQuadCountFromScratchEveryCall()
        {
            var camGo = new GameObject("LabelStructure_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);

            var atlasTexture = BuildTinyAtlasTexture();
            var twoLabels = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender), MakeLabel(1, frame.SceneOriginRender) };
            var oneLabel = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender) };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/Text")));
            try
            {
                Assert.AreEqual(0, system.TickCount, "TickCount starts at 0 before any Tick.");

                system.Tick(in frame, twoLabels, atlasTexture);
                Assert.AreEqual(1, system.TickCount);
                Assert.AreEqual(2, system.LastQuadCount, "2 labels x 1 quad each = 2 placed quads.");

                // Rebuilt from scratch, not accumulated: ticking with FEWER labels must report FEWER quads,
                // not the sum of every Tick so far (which would prove a cache/append bug).
                system.Tick(in frame, oneLabel, atlasTexture);
                Assert.AreEqual(2, system.TickCount);
                Assert.AreEqual(1, system.LastQuadCount,
                    "a Tick with 1 label must report 1 placed quad -- NOT 3 (2 from the prior Tick + 1), " +
                    "which would mean the buffer is cached/appended instead of rebuilt every Tick.");

                system.Tick(in frame, twoLabels, atlasTexture);
                Assert.AreEqual(3, system.TickCount);
                Assert.AreEqual(2, system.LastQuadCount);
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
