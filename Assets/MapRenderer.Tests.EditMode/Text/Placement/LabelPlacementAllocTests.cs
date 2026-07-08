// Unity EditMode only — needs UnityEngine.TestTools.Constraints (GC.Alloc profiler recorder) + a real
// Camera/Mesh/Material. NOT registered in core-tests.csproj.
//
// Per docs/lessons-learned.md: only UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() is
// trustworthy for this measurement (GC.GetTotalMemory / GetAllocatedBytesForCurrentThread both lie on
// this Unity Mono runtime). First-frame warmup (NativeList growth, first Mesh/Material creation) may
// allocate; the tooth is the STEADY (post-warmup) path.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 T4: over a STABLE synthetic label set, a steady-state <see cref="LabelPlacementSystem.Tick"/>
    /// (project → billboard-build → submit) allocates ZERO managed garbage.
    /// </summary>
    [TestFixture]
    public class LabelPlacementAllocTests
    {
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
                Bitmap = new byte[16 * 16], // CellSize = (10+2*3, 10+2*3) = (16,16)
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
        // label is silently culled (steady-state Tick would then measure the trivial "nothing to place"
        // no-op branch, not the real project->build->submit path).
        private static List<LabelInstance> BuildLabels(int count, double3 sceneOriginRender)
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

            var labels = new List<LabelInstance>(count);
            for (int i = 0; i < count; i++)
            {
                labels.Add(new LabelInstance
                {
                    AnchorRender = sceneOriginRender + new double3(i * 10.0, 0.0, i * 5.0),
                    Layout = layout,
                    Paint = LabelPaint.Default,
                    TextSizePx = 24f,
                    SortKey = 0f,
                    FeatureIndex = i,
                    TileKey = 0L,
                });
            }
            return labels;
        }

        [Test]
        public void Tick_SteadyState_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("LabelAlloc_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));

            // Build the frame from the projection, matching MapView.BuildSceneFrame's math (identity
            // rebase for planar Mercator).
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 }),
                float3x3.identity);

            var atlasTexture = BuildTinyAtlasTexture();
            var labels = BuildLabels(20, frame.SceneOriginRender);
            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/SymbolText")));

            try
            {
                // Warm-up ticks: first-frame NativeList growth + Mesh/Material creation is allowed to allocate.
                for (int i = 0; i < 3; i++)
                {
                    system.Tick(in frame, labels, atlasTexture);
                }

                Assert.That(() => system.Tick(in frame, labels, atlasTexture),
                    Is.Not.AllocatingGCMemory(),
                    "a steady-state Tick (same label count/shape as the warm-up) must allocate ZERO managed garbage");
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
