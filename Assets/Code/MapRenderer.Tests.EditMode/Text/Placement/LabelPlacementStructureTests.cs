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
            string dir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Text", "Placement");
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
        private static LabelInstance MakeLabel(int featureIndex, double3 sceneOriginRender, float2 translatePx = default,
            AlignmentMode rotationAlignment = AlignmentMode.Auto)
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
                TranslatePx = translatePx,
                RotationAlignment = rotationAlignment,
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

        // ── Slice C: text-translate shifts the placed billboard vertices in screen space (guards that
        //    LabelPlacementSystem.Tick actually applies LabelTranslate — the fast LabelTranslateTests only
        //    cover the pure delta math; this proves Tick calls it). ──
        [Test]
        public void Tick_TextTranslate_ShiftsEveryPlacedVertex_ByScreenDelta()
        {
            var camGo = new GameObject("LabelTranslate_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            // MapLibre text-translate [7,3] = right 7, DOWN 3 → screen (y-up) delta (+7, -3), depth unchanged.
            var translate = new float2(7f, 3f);
            var baseline = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender) };
            var moved    = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender, translate) };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/Text")));
            try
            {
                system.Tick(in frame, baseline, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "one label, one quad");
                Vector3[] v0 = system.Mesh.vertices;

                system.Tick(in frame, moved, atlasTexture);
                Vector3[] v1 = system.Mesh.vertices;

                Assert.AreEqual(v0.Length, v1.Length, "same vertex count (only the translate changed)");
                Assert.Greater(v0.Length, 0, "the label placed at least one quad");
                for (int i = 0; i < v0.Length; i++)
                {
                    Assert.AreEqual(translate.x, v1[i].x - v0[i].x, 1e-3f, $"vertex {i}: +tx in screen x");
                    Assert.AreEqual(-translate.y, v1[i].y - v0[i].y, 1e-3f, $"vertex {i}: -ty in screen y (y-down text-translate → y-up screen)");
                    Assert.AreEqual(0f, v1[i].z - v0[i].z, 1e-3f, $"vertex {i}: depth unchanged by a screen translate");
                }
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── #4: text-rotation-alignment:map rotates the billboard with the map bearing (guards that Tick
        //    threads label.RotationAlignment + the bearing into BillboardMath — the axis-aligned viewport
        //    billboard becomes a rotated quad under a non-zero heading). ──
        [Test]
        public void Tick_RotationAlignmentMap_RotatesBillboard_UnderBearing_ViewportStaysAxisAligned()
        {
            var camGo = new GameObject("LabelRotation_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            // A non-zero heading (45°) — at bearing 0 map and viewport coincide, so the rotation is only
            // observable with an active bearing.
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 45.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            var viewportLabels = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender, default, AlignmentMode.Viewport) };
            var mapLabels      = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender, default, AlignmentMode.Map) };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/Text")));
            try
            {
                system.Tick(in frame, viewportLabels, atlasTexture);
                Vector3[] vp = system.Mesh.vertices; // v[0]=TL, v[1]=TR, v[2]=BR, v[3]=BL
                Assert.AreEqual(4, vp.Length, "one quad → 4 verts");

                system.Tick(in frame, mapLabels, atlasTexture);
                Vector3[] mp = system.Mesh.vertices;

                // Viewport: top edge horizontal (axis-aligned billboard) — TL.y == TR.y.
                Assert.AreEqual(vp[0].y, vp[1].y, 1e-3f, "viewport billboard's top edge stays horizontal");
                // Map under a 45° bearing: the quad is rotated, so the top edge is NOT horizontal.
                Assert.That(math.abs(mp[0].y - mp[1].y), Is.GreaterThan(1f),
                    "rotation-alignment:map must rotate the billboard under a non-zero bearing (top edge no longer horizontal)");
                // And it genuinely differs from the viewport placement (rotation actually applied).
                Assert.That(math.abs(mp[1].x - vp[1].x) + math.abs(mp[1].y - vp[1].y), Is.GreaterThan(1f),
                    "map- and viewport-aligned billboards must differ under a non-zero bearing");
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
