// World-space symbol motion and curved-path GPU/visual acceptance tests. Split by the CS0104 bare-`Object`
// collision: SymbolWorldRenderTests.cs holds the bare-Object users, so the two may not merge.
//
// Contents:
//   WorldSymbolAbRenderSnapshotTests  — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
//   WorldSymbolMotionTests            — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, GPU render + CPU readback.
//   CurvedTextOnPathRenderTests       — Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader.
//   SymbolTextColorRenderTests        — Unity EditMode only — render tests requiring a GPU context (VisualScene/SnapshotRenderer).
//   MapPitchedWorldArcLayoutTests     — Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real SymbolPlacementSystem.Tick), mesh readback through the live camera.
//   GeoJsonPointSymbolFixtureTests    — Unity EditMode only — the tilted point-symbol fixture.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.View;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using System.Globalization;
using TextAnchor = MapRenderer.Core.Text.TextAnchor;
using MapRenderer.Tests.Text.Placement;
using System.Text;

namespace MapRenderer.Tests.Text.Placement
{
    // Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
    // NOT registered in core-tests.csproj. The GPU A/B equivalence tooth: the SAME real glyph through a real
    // SymbolPlacementSystem.Tick and through a one-off WorldBillboardMeshBuilder mesh on Map/Symbol/TextWorld.
    //
    // Non-obvious why: the headless camera→RenderTexture readback is vertically mirrored for EVERY path, so
    // both readbacks go through the SAME `FlipRowsVertically` and must land upright and centred. A mirrored
    // result is fixed at the corner emit (see BuildOneGlyphWorldMesh), never in the shader.

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldSymbolAbRenderSnapshotTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldSymbolAbRenderSnapshotTests : BaseTestFixture
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        /// <summary>Same minimal <see cref="IGlyphMetricsProvider"/> shim as SymbolAtlasOrientationSnapshotTests
        /// (duplicated, not shared — a private nested type, not worth widening across files for one method).</summary>
        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        [Test]
        public void OldAndNewPath_RenderSameGlyph_UprightCenteredAndEquivalent()
        {
            // 1. Real SDF atlas, real fixture glyph 'A' — identical setup to the orientation test.
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0);
            using var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);
            Assert.AreEqual(1, quads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = quads[0];

            var camGo = Track(new GameObject("WorldSymbolAb_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // Same tiny-north-offset convention as the orientation test — keeps the anchor comfortably
            // inside the frame without depending on its exact vertical landing.
            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);
            const float textSizePx = 220f;

            // Point text draws through the world path, so a realistic containing tile keeps the AnchorLocal
            // bake float32-safe; TileKey=0 is ~2e7 m away.
            long tileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default, textSizePx: textSizePx, sortKey: 0f, featureIndex: 0, tileKey: tileKey);

            Color32[] oldPixels;
            Color32[] newPixels;

            // ── OLD arm: a real Tick, which gives a point symbol the WORLD path, so this is real Tick vs
            // scaffold. It needs its own world base material. ────────────────────────
            using (var system = new SymbolPlacementSystem(mapCamera,
                       worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld"))))
            using (var snapOld = new SnapshotRenderer(Size, Size))
            using (var plan = new TestSymbolPlan(mapCamera.Projection))
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the OLD path's label must not be culled.");

                snapOld.Render(uCam);
                oldPixels = (Color32[])snapOld.Pixels.Pixels.Clone();
                snapOld.WritePng("world-symbol-ab-old.png");
            }

            // ── NEW path: a one-off world-anchored mesh built straight from the SAME layout/anchor ─────
            {
                float4 textColor = SymbolPaint.Default.TextColor;
                Mesh worldMesh = Track(BuildOneGlyphWorldMesh(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z)));

                Material worldMaterial = Track(new Material(Shader.Find("Map/Symbol/TextWorld")));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                GameObject presenterGo = Track(new GameObject("WorldSymbolAb_Presenter"));
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                // Level-2 placement: the anchor is its own bake origin (AnchorLocal == 0), and the object's
                // position and rotation carry the rest, as for a real tile mesh.
                float3 objectPos = FloatingOrigin.TileToSceneRebased(anchorRender, frame.SceneOriginRender, frame.Rebase);
                presenterGo.transform.position = new Vector3(objectPos.x, objectPos.y, objectPos.z);
                presenterGo.transform.rotation = Quaternion.identity; // frame.Rebase is float3x3.identity on Mercator

                using (var snapNew = new SnapshotRenderer(Size, Size))
                {
                    snapNew.Render(uCam);
                    newPixels = (Color32[])snapNew.Pixels.Pixels.Clone();
                    snapNew.WritePng("world-symbol-ab-new.png");
                }
            }

            // ── Un-mirror BOTH raw readbacks the same way (see this file's header) ─────────────────────
            WorldSymbolInkAnalysis.FlipRowsVertically(oldPixels, Size, Size);
            WorldSymbolInkAnalysis.FlipRowsVertically(newPixels, Size, Size);

            WorldSymbolInkAnalysis.AnalyzeInk(oldPixels, Size, Size,
                out int oldMinRow, out int oldMaxRow, out int oldMinCol, out int oldMaxCol,
                out float oldCentroidRow, out float oldCentroidCol, out int oldInkCount);
            WorldSymbolInkAnalysis.AnalyzeInk(newPixels, Size, Size,
                out int newMinRow, out int newMaxRow, out int newMinCol, out int newMaxCol,
                out float newCentroidRow, out float newCentroidCol, out int newInkCount);

            Assert.Greater(oldInkCount, 50, "OLD path must render meaningful ink (not blank/GPU-context-failed).");
            Assert.Greater(newInkCount, 50, "NEW path must render meaningful ink (not blank/GPU-context-failed).");

            // (1) Both upright: bottom third (crossbar/legs) wider than top third (apex) — same guard as
            // the orientation test, applied to BOTH paths.
            WorldSymbolInkAnalysis.ThirdWidths(oldPixels, Size, Size, oldMinRow, oldMaxRow,
                out float oldTopThird, out float oldBottomThird);
            WorldSymbolInkAnalysis.ThirdWidths(newPixels, Size, Size, newMinRow, newMaxRow,
                out float newTopThird, out float newBottomThird);
            Assert.Greater(oldBottomThird, oldTopThird * 1.3f, "OLD path must render 'A' upright (regression sentinel, not the tooth under test).");
            Assert.Greater(newBottomThird, newTopThird * 1.3f,
                "NEW path must render 'A' upright after the SAME un-mirror as the OLD path — if this fails mirrored " +
                "(top third wider), the world-mesh corner-emit convention (BuildOneGlyphWorldMesh) needs its Offset.y/UV.y sign flipped.");

            // (2) Both horizontally centered (flip-invariant axis).
            Assert.That((oldMinCol + oldMaxCol) * 0.5f, Is.EqualTo(Size * 0.5f).Within(Size * 0.15f), "OLD path horizontal placement.");
            Assert.That((newMinCol + newMaxCol) * 0.5f, Is.EqualTo(Size * 0.5f).Within(Size * 0.15f), "NEW path horizontal placement.");

            // (3) Equivalence: centroids within 6 px, bbox corners within 8 px, and under 5% of pixels changed
            // (R channel, >24).
            Assert.That(newCentroidRow, Is.EqualTo(oldCentroidRow).Within(6f), "ink centroid row must match within 6px.");
            Assert.That(newCentroidCol, Is.EqualTo(oldCentroidCol).Within(6f), "ink centroid col must match within 6px.");
            Assert.That(newMinRow, Is.EqualTo(oldMinRow).Within(8), "ink bbox top edge must match within 8px.");
            Assert.That(newMaxRow, Is.EqualTo(oldMaxRow).Within(8), "ink bbox bottom edge must match within 8px.");
            Assert.That(newMinCol, Is.EqualTo(oldMinCol).Within(8), "ink bbox left edge must match within 8px.");
            Assert.That(newMaxCol, Is.EqualTo(oldMaxCol).Within(8), "ink bbox right edge must match within 8px.");

            float changedFraction = WorldSymbolInkAnalysis.ChangedPixelFraction(oldPixels, newPixels, Size, Size);
            Assert.Less(changedFraction, 0.05f, $"changed-pixel fraction ({changedFraction:P1}) must stay under 5% of the frame.");
        }

        /// <summary>
        /// Builds a one-glyph world-anchored <see cref="Mesh"/> from a real <see cref="SymbolQuad"/> — the
        /// test-scaffold analogue of <see cref="BillboardMath.BuildQuad"/>, so these teeth exercise
        /// <see cref="WorldBillboardMeshBuilder"/> on a genuine glyph. AnchorLocal is zero; only the corner
        /// <c>Offset</c> varies. Non-obvious why: the stock MVP has no on-screen <c>ndc.y</c> flip, so
        /// <c>Offset.y</c> is negated while UV stays on its corner; unflipped, it renders upside down. Never move
        /// this into the shader.
        /// </summary>
        private static Mesh BuildOneGlyphWorldMesh(in SymbolQuad quad, float textSizePx, float3 colorRgb)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;
            float2 tl = quad.TopLeft * scale;
            float2 br = quad.BottomRight * scale;
            float2 tr = new float2(br.x, tl.y);
            float2 bl = new float2(tl.x, br.y);

            // RESOLVED Y CONVENTION — negate the offset's Y only (see this method's doc comment).
            float2 tlOffset = new float2(tl.x, -tl.y);
            float2 trOffset = new float2(tr.x, -tr.y);
            float2 brOffset = new float2(br.x, -br.y);
            float2 blOffset = new float2(bl.x, -bl.y);

            float2 uvTl = quad.UvTopLeft;
            float2 uvBr = quad.UvBottomRight;
            float2 uvTr = new float2(uvBr.x, uvTl.y);
            float2 uvBl = new float2(uvTl.x, uvBr.y);

            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            vertices[0] = MakeVertex(tlOffset, uvTl, quad.Page, colorRgb);
            vertices[1] = MakeVertex(trOffset, uvTr, quad.Page, colorRgb);
            vertices[2] = MakeVertex(brOffset, uvBr, quad.Page, colorRgb);
            vertices[3] = MakeVertex(blOffset, uvBl, quad.Page, colorRgb);

            var opacity = new NativeArray<float>(4, Allocator.Temp);
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f; // constant, no fade in this scaffold

            var indices = new NativeArray<int>(6, Allocator.Temp);
            indices[0] = 0; indices[1] = 1; indices[2] = 2; // TL,TR,BR — matches SymbolBillboardJob's winding
            indices[3] = 0; indices[4] = 2; indices[5] = 3; // TL,BR,BL

            var mesh = new Mesh { name = "WorldSymbolAb_OneGlyph" };
            try
            {
                WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh);
            }
            finally
            {
                vertices.Dispose();
                opacity.Dispose();
                indices.Dispose();
            }
            return mesh;
        }

        private static WorldBillboardVertex MakeVertex(float2 offsetPx, float2 uv, int page, float3 colorRgb)
            => new WorldBillboardVertex
            {
                AnchorLocal = float3.zero,
                ColorRGB = colorRgb,
                Uv = uv,
                Page = page,
                Offset = offsetPx,
                AlignFlags = 0f,
            };
    }

    // Unity EditMode only — the world-anchor MOTION tooth. The world-anchored mesh is built ONCE and frozen,
    // the camera PANS, and only the object transform updates. The glyph must land near the ANALYTIC screen
    // position (SymbolScreenProjection.TryProjectPoint), not stay on its old pixel. Non-obvious why: the tooth is
    // not vacuous, because a screen-space mesh reads a (0, 0) delta against the expected (-182, 0).

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldSymbolMotionTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldSymbolMotionTests : BaseTestFixture
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        /// <summary>Same minimal shim as WorldSymbolAbRenderSnapshotTests/SymbolAtlasOrientationSnapshotTests
        /// (duplicated, not shared — a private nested type, not worth widening across files for one method).</summary>
        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        [Test]
        public void WorldPath_TracksWorldAnchor_AfterCameraPan()
        {
            // 1. Real SDF atlas, real fixture glyph 'A'.
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0);
            using var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var layoutQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, layoutQuads);
            Assert.AreEqual(1, layoutQuads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = layoutQuads[0];
            const float textSizePx = 220f;

            var camGo = Track(new GameObject("WorldSymbolMotion_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var lookAt0 = new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt0, zoom: 8.0, heading: 0.0, tilt: 0.0));

            {
                // The FROZEN world anchor, computed ONCE. AnchorLocal is zero, so the object's transform alone
                // carries the anchor's placement.
                SceneFrame frame0 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude }),
                    Rebase = float3x3.identity,
                };
                double altitude0 = uCam.transform.position.y;
                double3 anchorRender = frame0.SceneOriginRender + new double3(0.0, 0.0, altitude0 * 0.02);

                float4 textColor = SymbolPaint.Default.TextColor;
                Mesh worldMesh = Track(BuildOneGlyphWorldMesh(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z)));
                Material worldMaterial = Track(new Material(Shader.Find("Map/Symbol/TextWorld")));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                GameObject presenterGo = Track(new GameObject("WorldSymbolMotion_Presenter"));
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                // ── Pose 0: place + render the frozen mesh at the initial camera framing ────────────────
                PlacePresenter(presenterGo, anchorRender, frame0);
                Color32[] pixels0;
                using (var snap0 = new SnapshotRenderer(Size, Size))
                {
                    snap0.Render(uCam);
                    pixels0 = (Color32[])snap0.Pixels.Pixels.Clone();
                    snap0.WritePng("world-label-motion-pose0.png");
                }
                WorldSymbolInkAnalysis.FlipRowsVertically(pixels0, Size, Size);
                WorldSymbolInkAnalysis.AnalyzeInk(pixels0, Size, Size,
                    out _, out _, out _, out _, out float centroidRow0, out float centroidCol0, out int inkCount0);
                Assert.Greater(inkCount0, 50, "pose-0 render must show meaningful ink (not blank/GPU-context-failed).");

                float4x4 viewProj0 = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                        anchorRender, frame0.SceneOriginRender, viewProj0, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen0, out _),
                    "anchor must project in front of the camera at pose 0.");

                // ── Pan 0.5° in longitude; the frozen mesh is reused and only its object transform is
                // recomputed. 0.5° stays on the 512 px frame and still shifts > 50 px (asserted below). ─────
                var lookAt1 = new GeoCoordinate3D { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude + 0.5, Altitude = 0.0 };
                mapCamera.SetProperties(new CameraProperties(lookAt1, zoom: 8.0, heading: 0.0, tilt: 0.0));
                mapCamera.SyncToCamera();
                SceneFrame frame1 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt1.Latitude, Longitude = lookAt1.Longitude }),
                    Rebase = float3x3.identity,
                };

                PlacePresenter(presenterGo, anchorRender, frame1);
                Color32[] pixels1;
                using (var snap1 = new SnapshotRenderer(Size, Size))
                {
                    snap1.Render(uCam);
                    pixels1 = (Color32[])snap1.Pixels.Pixels.Clone();
                    snap1.WritePng("world-label-motion-pose1.png");
                }
                WorldSymbolInkAnalysis.FlipRowsVertically(pixels1, Size, Size);
                WorldSymbolInkAnalysis.AnalyzeInk(pixels1, Size, Size,
                    out _, out _, out _, out _, out float centroidRow1, out float centroidCol1, out int inkCount1);
                Assert.Greater(inkCount1, 50, "pose-1 render must show meaningful ink (not blank/GPU-context-failed) — a mistracked anchor could also land off-screen.");

                float4x4 viewProj1 = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                        anchorRender, frame1.SceneOriginRender, viewProj1, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen1, out _),
                    "anchor must project in front of the camera at pose 1.");

                // ── The analytic shift: Offset is a fixed additive clip-space term that cancels in a delta, so
                // the glyph's screen delta equals the ANCHOR's (see WorldBillboardRtcAlgebraTests). ──────────
                float2 expectedDeltaScreen = anchorScreen1 - anchorScreen0;
                Assert.Greater(math.abs(expectedDeltaScreen.x) + math.abs(expectedDeltaScreen.y), 50f,
                    "the pan must produce a nontrivial expected screen shift, or this tooth is vacuous.");

                // Col tracks screen X directly. Rows are top-origin after the flip and screen Y is bottom-origin,
                // so a screen-Y increase is a row DECREASE.
                float actualDeltaCol = centroidCol1 - centroidCol0;
                float actualDeltaRowAsScreenY = centroidRow0 - centroidRow1;

                Assert.That(actualDeltaCol, Is.EqualTo(expectedDeltaScreen.x).Within(12f),
                    $"X: the glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.x:F1}px, got {actualDeltaCol:F1}px) — if it stays near 0 instead, the world path is not tracking its anchor (the OLD screen-space bypass's failure mode).");
                Assert.That(actualDeltaRowAsScreenY, Is.EqualTo(expectedDeltaScreen.y).Within(12f),
                    $"Y: the glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.y:F1}px, got {actualDeltaRowAsScreenY:F1}px).");
            }
        }

        // ── The LATITUDE pan pins anchor-Y against the same analytic delta, which a longitude pan cannot:
        //    an anchor-Y mirror about the centre passes the longitude case and fails this one. ──
        [Test]
        public void WorldPath_TracksWorldAnchor_AfterLatitudeCameraPan()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0);
            using var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var layoutQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, layoutQuads);
            Assert.AreEqual(1, layoutQuads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = layoutQuads[0];
            // Smaller than the longitude case's glyph: the ~200 px vertical shift would clip a large glyph at
            // the frame edge and bias the centroid. The whole ink stays in frame, so the ~12 px bound holds.
            const float textSizePx = 60f;

            var camGo = Track(new GameObject("WorldSymbolMotionLat_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var lookAt0 = new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt0, zoom: 8.0, heading: 0.0, tilt: 0.0));

            {
                SceneFrame frame0 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude }),
                    Rebase = float3x3.identity,
                };
                double altitude0 = uCam.transform.position.y;
                double3 anchorRender = frame0.SceneOriginRender + new double3(0.0, 0.0, altitude0 * 0.02);

                float4 textColor = SymbolPaint.Default.TextColor;
                Mesh worldMesh = Track(BuildOneGlyphWorldMesh(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z)));
                Material worldMaterial = Track(new Material(Shader.Find("Map/Symbol/TextWorld")));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                GameObject presenterGo = Track(new GameObject("WorldSymbolMotionLat_Presenter"));
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                PlacePresenter(presenterGo, anchorRender, frame0);
                Color32[] pixels0;
                using (var snap0 = new SnapshotRenderer(Size, Size))
                {
                    snap0.Render(uCam);
                    pixels0 = (Color32[])snap0.Pixels.Pixels.Clone();
                    snap0.WritePng("world-label-motion-lat-pose0.png");
                }
                WorldSymbolInkAnalysis.FlipRowsVertically(pixels0, Size, Size);
                WorldSymbolInkAnalysis.AnalyzeInk(pixels0, Size, Size,
                    out _, out _, out _, out _, out float centroidRow0, out float centroidCol0, out int inkCount0);
                Assert.Greater(inkCount0, 50, "pose-0 render must show meaningful ink (not blank/GPU-context-failed).");

                float4x4 viewProj0 = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                        anchorRender, frame0.SceneOriginRender, viewProj0, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen0, out _),
                    "anchor must project in front of the camera at pose 0.");

                // ── Pan: a new look-at LATITUDE (fixed longitude) — pins anchor-Y as the longitude case above
                // pins anchor-X. 0.5deg mirrors the longitude case's magnitude (kept on-screen, nontrivial). ──
                var lookAt1 = new GeoCoordinate3D { Latitude = lookAt0.Latitude + 0.5, Longitude = lookAt0.Longitude, Altitude = 0.0 };
                mapCamera.SetProperties(new CameraProperties(lookAt1, zoom: 8.0, heading: 0.0, tilt: 0.0));
                mapCamera.SyncToCamera();
                SceneFrame frame1 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt1.Latitude, Longitude = lookAt1.Longitude }),
                    Rebase = float3x3.identity,
                };

                PlacePresenter(presenterGo, anchorRender, frame1);
                Color32[] pixels1;
                using (var snap1 = new SnapshotRenderer(Size, Size))
                {
                    snap1.Render(uCam);
                    pixels1 = (Color32[])snap1.Pixels.Pixels.Clone();
                    snap1.WritePng("world-label-motion-lat-pose1.png");
                }
                WorldSymbolInkAnalysis.FlipRowsVertically(pixels1, Size, Size);
                WorldSymbolInkAnalysis.AnalyzeInk(pixels1, Size, Size,
                    out _, out _, out _, out _, out float centroidRow1, out float centroidCol1, out int inkCount1);
                Assert.Greater(inkCount1, 50, "pose-1 render must show meaningful ink (not blank/GPU-context-failed) — a mistracked anchor could also land off-screen.");

                float4x4 viewProj1 = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                        anchorRender, frame1.SceneOriginRender, viewProj1, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen1, out _),
                    "anchor must project in front of the camera at pose 1.");

                float2 expectedDeltaScreen = anchorScreen1 - anchorScreen0;
                Assert.Greater(math.abs(expectedDeltaScreen.x) + math.abs(expectedDeltaScreen.y), 50f,
                    "the pan must produce a nontrivial expected screen shift, or this tooth is vacuous.");

                float actualDeltaCol = centroidCol1 - centroidCol0;
                float actualDeltaRowAsScreenY = centroidRow0 - centroidRow1;
                TestContext.Out.WriteLine($"latitude-pan measured delta: expectedY={expectedDeltaScreen.y:F1}px actualY={actualDeltaRowAsScreenY:F1}px (bound 12px)");

                Assert.That(actualDeltaCol, Is.EqualTo(expectedDeltaScreen.x).Within(12f),
                    $"X: a latitude-only pan should leave X roughly put (expected {expectedDeltaScreen.x:F1}px, got {actualDeltaCol:F1}px).");
                // The SAME tight 12px bound as the longitude case: the smaller 60px glyph (above) keeps the
                // full ink footprint in-frame at both poses, so nothing here measures a clipped run.
                Assert.That(actualDeltaRowAsScreenY, Is.EqualTo(expectedDeltaScreen.y).Within(12f),
                    $"Y: the glyph must shift by the anchor's projected screen-Y delta after a LATITUDE pan (expected {expectedDeltaScreen.y:F1}px, got {actualDeltaRowAsScreenY:F1}px) — " +
                    "the anchor-Y pin: a hypothetical mirror-about-center would fail here even though it passes the longitude (X) case.");
            }
        }

        /// <summary>Places the presenter GameObject at Level-2: AnchorLocal is float3.zero on every
        /// vertex (the mesh's object-space origin IS the world anchor), so the object's position alone —
        /// recomputed against <paramref name="frame"/> — carries the anchor to its per-frame place. Mirrors
        /// exactly how a real tile renderer re-places a FROZEN mesh every frame; the mesh itself is never
        /// touched here.</summary>
        private static void PlacePresenter(GameObject presenterGo, in double3 anchorRender, in SceneFrame frame)
        {
            float3 objectPos = FloatingOrigin.TileToSceneRebased(anchorRender, frame.SceneOriginRender, frame.Rebase);
            presenterGo.transform.position = new Vector3(objectPos.x, objectPos.y, objectPos.z);
            presenterGo.transform.rotation = Quaternion.identity; // frame.Rebase is float3x3.identity on Mercator
        }

        /// <summary>Builds a one-glyph world-anchored <see cref="Mesh"/> from a real <see cref="SymbolQuad"/>,
        /// a private copy of WorldSymbolAbRenderSnapshotTests.BuildOneGlyphWorldMesh. AnchorLocal is zero,
        /// and <c>Offset.y</c> is negated per corner with UV left on its corner, for the same reason as
        /// there.</summary>
        private static Mesh BuildOneGlyphWorldMesh(in SymbolQuad quad, float textSizePx, float3 colorRgb)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;
            float2 tl = quad.TopLeft * scale;
            float2 br = quad.BottomRight * scale;
            float2 tr = new float2(br.x, tl.y);
            float2 bl = new float2(tl.x, br.y);

            // RESOLVED Y CONVENTION — negate the offset's Y only (see this method's doc comment).
            float2 tlOffset = new float2(tl.x, -tl.y);
            float2 trOffset = new float2(tr.x, -tr.y);
            float2 brOffset = new float2(br.x, -br.y);
            float2 blOffset = new float2(bl.x, -bl.y);

            float2 uvTl = quad.UvTopLeft;
            float2 uvBr = quad.UvBottomRight;
            float2 uvTr = new float2(uvBr.x, uvTl.y);
            float2 uvBl = new float2(uvTl.x, uvBr.y);

            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            vertices[0] = MakeVertex(tlOffset, uvTl, quad.Page, colorRgb);
            vertices[1] = MakeVertex(trOffset, uvTr, quad.Page, colorRgb);
            vertices[2] = MakeVertex(brOffset, uvBr, quad.Page, colorRgb);
            vertices[3] = MakeVertex(blOffset, uvBl, quad.Page, colorRgb);

            var opacity = new NativeArray<float>(4, Allocator.Temp);
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f; // constant, no fade in this scaffold

            var indices = new NativeArray<int>(6, Allocator.Temp);
            indices[0] = 0; indices[1] = 1; indices[2] = 2; // TL,TR,BR — matches SymbolBillboardJob's winding
            indices[3] = 0; indices[4] = 2; indices[5] = 3; // TL,BR,BL

            var mesh = new Mesh { name = "WorldSymbolMotion_OneGlyph" };
            try
            {
                WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh);
            }
            finally
            {
                vertices.Dispose();
                opacity.Dispose();
                indices.Dispose();
            }
            return mesh;
        }

        private static WorldBillboardVertex MakeVertex(float2 offsetPx, float2 uv, int page, float3 colorRgb)
            => new WorldBillboardVertex
            {
                AnchorLocal = float3.zero,
                ColorRGB = colorRgb,
                Uv = uv,
                Page = page,
                Offset = offsetPx,
                AlignFlags = 0f,
            };
    }
}

namespace MapRenderer.Tests.Visual
{
    // Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL
    // SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader. NOT registered in
    // Tools/core-tests/core-tests.csproj (it renders).
    //
    // THE HEADLINE ARM: a curved road symbol's ink sits ON the road, at tilt 0.
    //
    // Non-obvious why: a cell baked BASELINE-relative, while the point path applies TextQuadLayout's
    // optical-centre shift, renders off the road even at tilt 0. Fixtures that hand-build CurvedGlyph.Cell or
    // borrow the POINT layout cannot see that, so these teeth take their cell from the REAL curved producer.
    // At tilt 0 and screen angle 0° the road is one screen row, so "off the road" is "off in rows". Both teeth
    // read the ink band's MID-ROW from minRow/maxRow, never a centroid. '5' is baseline-resting and one cap
    // height tall (CurvedTextCentringTests asserts both), so its band is symmetric about the anchor and
    // Curved-T4's bound is derived, not fitted.
    // NO METRE LITERALS: every world length is a multiple of `scene.MetresPerDevicePixel`.

    // ───────────────────────────────────────────────────────────────────────────────────
    // CurvedTextOnPathRenderTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class CurvedTextOnPathRenderTests
    {
        private const int   SizePx     = 512;
        private const float TextSizePx = 160f;

        /// <summary>Half-length of the road, as a multiple of the frame ruler. Only has to exceed the chord
        /// probe's half-width (the symbol is ONE glyph, so its arc span is exactly 0 and the spill gate is
        /// trivially satisfied); 200 leaves a wide margin at both ends.</summary>
        private const double RoadHalfLengthRulerUnits = 200.0;

        /// <summary>
        /// Row-agreement bound, in device pixels. DERIVED, not fitted: '5' is symmetric about the anchor
        /// (<c>CurvedTextCentringTests</c>), so only rasterisation and the SDF threshold remain, sub-pixel per
        /// edge. 4.0 device px is 0.6 BAKED px here; a baseline-relative cell reads 116.67 px, a 29× margin.
        /// </summary>
        private const double RowTolerancePx = 4.0;

        /// <summary>An ink reading below this is not a rendered glyph — a blank frame, a GPU-context failure
        /// or a quad collapsed to a point would otherwise pass a row test by agreeing about nothing.
        /// Asserted on EVERY arm.</summary>
        private const int InkFloor = 500;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Curved-T4 — the ink sits on the road. The oracle is the PROJECTED ROAD ANCHOR, not the cell.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Curved-T4 — a curved symbol's ink is centred on the road it is drawn along, at tilt 0,</b> from a
        /// REAL <see cref="CurvedTextLayout"/> cell through the real placement system and shader.
        /// Non-obvious why: the oracle is the anchor's row from <c>UnityCamera.WorldToScreenPoint</c>, sharing
        /// no code with the cell. Cell y = 0 maps to the path point in every consumer, and '5' is symmetric
        /// about it, so the ink band's mid-row lands on the anchor row. No shift reads 116.7 px off.
        /// </summary>
        [Test]
        public void CurvedTextInk_SitsOnTheRoad_AtTiltZero()
        {
            MeasureBothArms(out RowReading curved, out RowReading point, out double anchorRow);

            double delta = curved.MidRow - anchorRow;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Curved-T4  curved ink rows=[{0}, {1}]  mid={2:F3}  anchor row={3:F3}  delta={4:F3} px  ink={5}",
                curved.MinRow, curved.MaxRow, curved.MidRow, anchorRow, delta, curved.Ink));

            Assert.That(math.abs(delta), Is.LessThanOrEqualTo(RowTolerancePx),
                $"Curved-T4: a curved label's ink must straddle the road it is drawn along. Ink rows " +
                $"[{curved.MinRow}, {curved.MaxRow}], mid-row {curved.MidRow:F3}, road anchor row " +
                $"{anchorRow:F3} — off by {delta:F3} device px (bound {RowTolerancePx}). At this text size " +
                $"a missing optical-centre shift reads 116.67 px and a doubled one reads the same the other " +
                $"way; a residual of a few px instead means the shift is right and something else drifted.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Curved-T5 — the rendered half of the point cross-check, and Curved-T4's row-convention control
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Curved-T5 — a curved symbol and a centre-anchored POINT symbol of the same glyph sit the same way on
        /// the same anchor, rendered.</b> The pair differs only in the PRODUCER. Non-obvious why: beside
        /// Curved-T4, the point arm shares no code with the curved producer (<c>TextVerticalCentringTests</c>
        /// pins it), and one scan reads both arms, so a row-convention error cancels. Both failing means the
        /// shift is wrong; Curved-T4 alone failing means the row convention is.
        /// </summary>
        [Test]
        public void CurvedTextInk_MatchesThePointPathTwin_AtTiltZero()
        {
            MeasureBothArms(out RowReading curved, out RowReading point, out double anchorRow);

            double delta = curved.MidRow - point.MidRow;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Curved-T5  curved mid={0:F3} (rows [{1}, {2}])  point mid={3:F3} (rows [{4}, {5}])  " +
                "delta={6:F3} px  (anchor row {7:F3})",
                curved.MidRow, curved.MinRow, curved.MaxRow,
                point.MidRow, point.MinRow, point.MaxRow, delta, anchorRow));

            Assert.That(math.abs(delta), Is.LessThanOrEqualTo(RowTolerancePx),
                $"Curved-T5: the curved producer and the centre-anchored point producer must put the same glyph " +
                $"in the same place on the same anchor — curved mid-row {curved.MidRow:F3}, point mid-row " +
                $"{point.MidRow:F3}, off by {delta:F3} device px (bound {RowTolerancePx}). This tooth is " +
                $"blind to Curved-T4's row convention (both arms carry it), so a failure here is the curved cell's " +
                $"vertical placement and nothing else.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Harness
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>One arm's rendered vertical ink signature. EXTREMES only — see this file's header.</summary>
        private readonly struct RowReading
        {
            public readonly int MinRow;
            public readonly int MaxRow;
            public readonly int Ink;

            public RowReading(int minRow, int maxRow, int ink)
            {
                MinRow = minRow;
                MaxRow = maxRow;
                Ink    = ink;
            }

            public double MidRow => 0.5 * (MinRow + MaxRow);
        }

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        /// <summary>
        /// Renders the same glyph twice through ONE scene and ONE camera — once as a CURVED along-line symbol
        /// whose cell comes from the real <see cref="CurvedTextLayout"/>, once as a centre-anchored POINT
        /// symbol whose quads come from <see cref="TextQuadLayout"/> — and also returns the road anchor's own
        /// projected screen row. The run is built directly from <see cref="PositionedGlyph"/>, because both
        /// layouts step the pen by the atlas entry's own advance.
        /// </summary>
        private static void MeasureBothArms(out RowReading curved, out RowReading point, out double anchorRow)
        {
            var sceneConfig = new TiltedGroundSceneConfig
            {
                TiltDegrees      = 0.0,   // THE pose — see this file's header.
                SizePx           = SizePx,
                DevicePixelRatio = 1.0,
                BackgroundColor  = Color.white, // WorldSymbolInkAnalysis.InkThreshold reads dark ink on white.
                LitAmbient       = false,       // the symbol arm needs no lit recipe.
            };

            TiltedGroundScene    scene    = null;
            GlyphAtlasTexture    texture  = null;
            SymbolPlacementSystem system   = null;
            TestSymbolPlan       plan     = null;
            SnapshotRenderer     snapshot = null;
            try
            {
                scene = TiltedGroundScene.Create(sceneConfig);

                FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
                var atlas = new GlyphAtlas();
                atlas.Append(stack.Glyphs[(uint)'5'], 0);
                texture = new GlyphAtlasTexture();
                texture.Upload(atlas);

                var run = new ShapedRun
                {
                    Glyphs = new List<PositionedGlyph>
                    {
                        new PositionedGlyph { AtlasCodepoint = (uint)'5', XAdvance = 0f, Cluster = 0 },
                    },
                    Direction = TextDirection.LeftToRight,
                };

                var curvedCells = new List<CurvedGlyph>();
                CurvedTextLayout.Layout(run, atlas, curvedCells);
                Assert.AreEqual(1, curvedCells.Count, "precondition: '5' lays out to exactly one curved cell.");

                var pointQuads = new List<SymbolQuad>();
                TextLayoutOptions pointOptions = new TextLayoutOptions
                {
                    Anchor          = TextAnchor.Center,
                    Offset          = float2.zero,
                    RadialOffset    = 0f,
                    Justify         = TextJustify.Center,
                    MaxWidthEm      = 10f,
                    LineHeightEm    = 1.2f,
                    LetterSpacingEm = 0f,
                };
                TextLayoutBounds pointBounds = TextQuadLayout.Layout(run, atlas, in pointOptions, pointQuads);
                Assert.AreEqual(1, pointQuads.Count, "precondition: '5' lays out to exactly one point quad.");
                Assert.AreEqual(1, pointBounds.LineCount, "precondition: the point twin must be single-line.");

                SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
                double3 origin = frame.SceneOriginRender;
                double mpp = scene.MetresPerDevicePixel;

                // The road along east = X at screen angle 0°, so "off the road" is "off in screen rows". NO metre
                // literals: every length is a multiple of the frame ruler.
                double halfLen = RoadHalfLengthRulerUnits * mpp;
                var dir = new double3(1.0, 0.0, 0.0);
                double3 pathA = origin - dir * halfLen;
                double3 pathB = origin + dir * halfLen;
                var up = new double3(0.0, 1.0, 0.0); // Web-Mercator scene: up IS +Y.

                long tileKey = TestTileKeys.PackedContaining(sceneConfig.LookAt.Surface, zoom: 14);

                system = new SymbolPlacementSystem(scene.MapCam,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                plan = new TestSymbolPlan(scene.MapCam.Projection);
                snapshot = new SnapshotRenderer(SizePx, SizePx);

                var curvedBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddCurved(curvedBuffer, curvedCells, new[] { new LineAnchor(0, 0.5f) },
                    new[] { pathA, pathB }, new[] { up, up },
                    placement: SymbolPlacement.LineCenter,
                    up: up,
                    paint: SymbolPaint.Default,
                    text: "curved",
                    textSizePx: TextSizePx,
                    maxAngleDeg: 180f,
                    keepUpright: false,
                    // At coarse zoom the dedup/collision machinery decides who emits
                    // and a fixture silently loses its symbol.
                    allowOverlap: true,
                    featureIndex: 0,
                    tileKey: tileKey);

                var pointBuffer = new SymbolTileBuffer();
                // Placement left at its default (Point) — this is the point emit path.
                TestSymbolTileBuffer.AddPoint(pointBuffer, origin, pointQuads, pointBounds.Min, pointBounds.Max,
                    up: up,
                    paint: SymbolPaint.Default,
                    // Distinct from the curved arm's: PointFadeId hashes (AnchorRender, MaterialIndex, Text,
                    // IconImage), and the two arms share an anchor.
                    text: "point",
                    textSizePx: TextSizePx,
                    allowOverlap: true,
                    featureIndex: 1,
                    tileKey: tileKey);

                curved = RenderArm(scene, system, plan, snapshot, texture, in frame, curvedBuffer, "curved");
                point  = RenderArm(scene, system, plan, snapshot, texture, in frame, pointBuffer,  "point");

                // The oracle: the road's midpoint is the scene origin, Unity world Vector3.zero after RTC.
                // Non-obvious why: r = SizePx - 0.5 - y, because the frame is bottom-up, FlipRowsVertically makes
                // it top-down for AnalyzeInk, and WorldToScreenPoint's y is bottom-up with pixel centres at +0.5.
                // Curved-T5 is the control that does not depend on this convention.
                Vector3 anchorScreen = scene.UnityCamera.WorldToScreenPoint(Vector3.zero);
                anchorRow = SizePx - 0.5 - anchorScreen.y;
            }
            finally
            {
                snapshot?.Dispose();
                plan?.Dispose();
                system?.Dispose();
                texture?.Dispose();
                scene?.Dispose();
            }
        }

        private static RowReading RenderArm(
            TiltedGroundScene scene, SymbolPlacementSystem system, TestSymbolPlan plan,
            SnapshotRenderer snapshot, GlyphAtlasTexture atlas, in SceneFrame frame,
            SymbolTileBuffer buffer, string armName)
        {
            // Duplicate Tick — the collision verdict is harvested one Tick late.
            system.Tick(in frame, plan.Build(buffer), atlas);
            system.Tick(in frame, plan.Build(buffer), atlas);
            Assert.That(system.LastQuadCount, Is.EqualTo(1),
                $"Curved-T4/T5 precondition ({armName}): the label must stage exactly one quad, got " +
                $"{system.LastQuadCount}. A zero means it spilled its road (StageCurved's centerArc ± halfSpan " +
                "gate) or was culled — nothing measured downstream would mean anything.");

            scene.Render(snapshot);
            var pixels = (Color32[])snapshot.Pixels.Pixels.Clone();
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, SizePx, SizePx);
            WorldSymbolInkAnalysis.AnalyzeInk(pixels, SizePx, SizePx,
                out int minRow, out int maxRow, out _, out _,
                out _, out _, out int ink);

            Assert.That(ink, Is.GreaterThan(InkFloor),
                $"Curved-T4/T5 precondition ({armName}): the arm rendered {ink} ink px (floor {InkFloor}) — a " +
                "blank frame, a GPU-context failure or a collapsed quad. An arm agreeing about nothing is " +
                "not a measurement.");

            return new RowReading(minRow, maxRow, ink);
        }
    }

    // Unity EditMode only — render tests requiring a GPU context (VisualScene/SnapshotRenderer).
    // NOT included in Tools/core-tests/core-tests.csproj.
    //
    // Non-obvious why: SymbolTextColorCarrierTests shares a CPU model of the fragment with the code it checks,
    // so this reads the rendered pixel through the full production path. Two arms differ ONLY in `text-color`
    // (#808080, #ffffff). grey/white must be linear(0.5019) ≈ 0.2158, applied ONCE: its square (≈0.0466)
    // means both carriers hold the colour, and 1.0 means the uniform never reached the fragment.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTextColorRenderTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    internal class SymbolTextColorRenderTests
    {
        private static readonly TileId Tile = new TileId { Z = 6, X = 40, Y = 25 };
        private const string FontName    = "Fixture Text Color Font";
        private const string GlyphText   = "I"; // a simple, near-solid vertical stroke in a sans font
        private const double TextSizePx  = 220.0;
        private const int    SizePx      = 256;

        // Half-size of the LINEAR colour sample box, small enough to sit inside the glyph's solid interior
        // once centred on the measured ink centroid.
        private const int SampleHalf = 3;

        private static byte[] _glyphBytes;

        private static (double lon, double lat) TileCenter()
        {
            double2 c = Tile.ToLonLat(0.5, 0.5, 1.0);
            return (c.x, c.y);
        }

        private static VisualScene BuildScene(string hexColor)
        {
            (double lon, double lat) = TileCenter();
            return VisualScene.New()
                .Source("points", GeoJson.Points((lon, lat, GlyphText)))
                .Layer(VisualLayer.SymbolText("labels").Source("points").TextField("name")
                    .TextSize(TextSizePx).TextFont(FontName).TextColor(hexColor))
                .Glyphs(FontName, LoadGlyphBytes())
                .Camera(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0.0 }, zoom: Tile.Z)
                .ExpectSymbolQuads(1);
        }

        /// <summary>Mean LINEAR RGB of the inclusive-exclusive box, decoded per-pixel from the sRGB-encoded
        /// readback (mirrors <c>PaintColorRenderTests.SampleLinear</c> — averaging encoded bytes first
        /// would be a different, wrong quantity).</summary>
        private static double3 SampleLinearBox(VisualFrame frame, int x0, int y0, int x1, int y1)
        {
            double3 sum = double3.zero;
            int n = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                Color32 px = frame.Pixels[x, y];
                Color lin = new Color(px.r / 255f, px.g / 255f, px.b / 255f, 1f).linear;
                sum += new double3(lin.r, lin.g, lin.b);
                n++;
            }
            return sum / n;
        }

        [Test]
        public void ConstantTextColor_RenderedPixel_RidesTheUniformOnceNotSquared()
        {
            // Dispose the white scene first: a live VisualScene draws into another scene's frame.
            (int x0, int x1, int y0, int y1) box;
            double3 whiteSample;
            using (var whiteScene = BuildScene("#ffffff"))
            {
                VisualFrame whiteFrame = whiteScene.Render(SizePx);

                // Locate the glyph from the white arm's render. Both arms share identical geometry, so the same
                // box samples the same pixels in the grey arm.
                whiteFrame.InkStatsIn(0, 0, whiteFrame.Width, whiteFrame.Height, out double2 centroid, out int inkCount);
                TestContext.WriteLine($"[SymbolTextColorRender] white ink centroid={centroid} count={inkCount}");
                Assert.Greater(inkCount, 0, "the white arm must render some ink to locate the glyph from.");

                int cx = (int)math.round(centroid.x), cy = (int)math.round(centroid.y);
                box = (cx - SampleHalf, cx + SampleHalf + 1, cy - SampleHalf, cy + SampleHalf + 1);

                whiteSample = SampleLinearBox(whiteFrame, box.x0, box.y0, box.x1, box.y1);
            }

            using var greyScene = BuildScene("#808080");
            VisualFrame greyFrame = greyScene.Render(SizePx);
            double3 greySample = SampleLinearBox(greyFrame, box.x0, box.y0, box.x1, box.y1);

            double3 ratio = greySample / whiteSample;
            const double expected = 0.2158; // linear(0x80/255)
            const double squared  = expected * expected;
            TestContext.WriteLine($"[SymbolTextColorRender] white={whiteSample} grey={greySample} ratio={ratio} " +
                                   $"expected~={expected} squared~={squared}");

            // The ratio cancels any factor common to both arms, so assert the white arm's absolute intensity
            // first; a bad denominator then reports as itself, not as a confusing ratio.
            for (int c = 0; c < 3; c++)
                Assert.That(whiteSample[c], Is.EqualTo(1.0).Within(0.02),
                    $"the white reference arm must render at full linear intensity. whiteSample={whiteSample}. " +
                    "A value below 1 means either the sample box missed the glyph's solid interior (the ratio's " +
                    "denominator is then partial coverage, not colour) or something scales ALL text — which the " +
                    "grey/white ratio cancels and cannot see.");

            for (int c = 0; c < 3; c++)
                Assert.That(ratio[c], Is.EqualTo(expected).Within(0.03),
                    $"channel {c}: a CONSTANT text-color must reach the fragment ONCE. ratio={ratio} " +
                    $"expected~={expected} squared~={squared}. Landing on the square means both the " +
                    "_TextColor uniform and the vertex COLOR stream carry the colour. Landing on 1.0 means " +
                    "the uniform never reached the fragment (vertex white, uniform unread or absent).");
        }

        // ── Glyph fixture loading (mirrors GeoJsonPointSymbolFixtureTests.LoadGlyphBytes) ────────────────

        private static byte[] LoadGlyphBytes()
            => _glyphBytes ??= LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");

        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { System.IO.Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new System.IO.DirectoryInfo(start);
                while (dir != null)
                {
                    string p = System.IO.Path.Combine(dir.FullName, System.IO.Path.Combine(relative));
                    if (System.IO.File.Exists(p)) return System.IO.File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new System.IO.FileNotFoundException(
                $"could not locate fixture file under any ancestor of the working directory: {System.IO.Path.Combine(relative)}");
        }
    }

    // Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real
    // SymbolPlacementSystem.Tick), mesh readback through the live camera.
    // NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // The FIXTURE arm (Fixture-T1…Fixture-T5) over OffLookAtSymbolScene (read its header first). `text-size`
    // under `*-pitch-alignment: map` means X px TOP-DOWN: a glyph advance is one fixed world length, so letters
    // and spacing foreshorten together, as `line-width` does.
    //
    // Non-obvious why: the cross arm is iso-depth, where a per-glyph WORLD walk equals a screen walk scaled by
    // one constant, so Fixture-T2/T3/T4 on the RECEDING arm carry the falsifiability. The oracle
    // `AdvanceWorldMetres` uses only fixture constants and the camera ruler, never a measured length.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapPitchedWorldArcLayoutTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapPitchedWorldArcLayoutTests
    {
        private static readonly OffLookAtSymbolId[] CurvedIds =
        {
            OffLookAtSymbolId.CrossNear, OffLookAtSymbolId.CrossFar,
            OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar,
        };

        private static readonly OffLookAtSymbolId[] RecedingIds =
        {
            OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar,
        };

        private static OffLookAtSymbolScene CreateFixture(double devicePixelRatio = 1.0)
            => OffLookAtSymbolScene.Create(new OffLookAtSymbolSceneConfig
            {
                DevicePixelRatio = devicePixelRatio,
            });

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Fixture-T1 — the inherited regression tooth.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Fixture-T1 — the regression tooth.</b> The cross-azimuth pair's mean screen spacing halves when
        /// the view depth doubles: far/near reads 0.500, and a screen-constant layout (<c>worldArc = false</c>)
        /// reads 1.0000. A uniform scale error cancels in the ratio. Limitation: both symbols are iso-depth, so
        /// a per-symbol constant passes; Fixture-T2 closes that.
        /// </summary>
        [Test]
        public void CrossAzimuthPair_ScreenSpacing_HalvesWithDepth()
        {
            using var f = CreateFixture();
            double nearMean = OffLookAtSymbolScene.Mean(f.Measure(OffLookAtSymbolId.CrossNear).ScreenSpacingPx);
            double farMean  = OffLookAtSymbolScene.Mean(f.Measure(OffLookAtSymbolId.CrossFar).ScreenSpacingPx);
            double ratio = farMean / nearMean;
            // Printed as well as asserted: the depth-derived expectation is what the 0.500 constant stands
            // for, and seeing both makes a pose change legible instead of mysterious.
            double depthDerived = f.NearAnchorViewDepthMetres / f.FarAnchorViewDepthMetres;

            Assert.That(ratio, Is.EqualTo(0.500).Within(3).Percent,
                $"Fixture-T1: a map-pitched glyph advance is a WORLD length, so at twice the view depth it must " +
                $"project to half the screen spacing — far/near reads {ratio:F4} (near {nearMean:F3} px, far " +
                $"{farMean:F3} px; the pose's own w_near/w_far is {depthDerived:F4}). A reading near 1.0000 " +
                "is the earlier screen-constant layout: the advance stayed a screen length and did not " +
                "foreshorten at all.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Fixture-T2 — THE HEADLINE. Per gap, all four curved symbols, both directions, both depths.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Fixture-T2 — THE HEADLINE TOOTH.</b> Proves: EVERY gap of ALL FOUR curved symbols — 16 gaps across two
        /// directions and two depths — measures <c>AdvanceWorldMetres</c> in the world, within 1 %. That is
        /// the model stated directly. Non-obvious why: on the RECEDING arms a per-symbol constant makes world
        /// gaps GROW with depth, so it fails. The roads are straight two-vertex segments, so chord equals
        /// arc, and the 1 % is headroom for the RTC bake's float narrowing only.
        /// </summary>
        [Test]
        public void EveryCurvedGap_MeasuresOneWorldAdvance_AtBothDepths_BothDirections()
        {
            using var f = CreateFixture();
            AssertEveryGapMatchesTheWorldAdvance(f, "Fixture-T2", boundPercent: 1.0);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Fixture-T3 — the same claim, through the LIVE projection, on the depth-spanning arm.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Fixture-T3 — the world claim carried into SCREEN space through the live camera.</b> For each gap of
        /// each receding symbol, the expectation is
        /// <c>|ProjectPx(A_i + d̂·AdvanceWorldMetres) − ProjectPx(A_i)|</c>, where <c>A_i</c> is glyph
        /// <c>i</c>'s MEASURED world position and <c>d̂</c> is the fixture's own road direction. The measured
        /// screen gap must match it within 3 %. The only measured input is a POSITION; the projected LENGTH is
        /// the constant. Non-obvious why: the closed form <c>spacingWorld·|P11|·H/(2w)</c> is the PERPENDICULAR
        /// span and reads far low on a receding road, so both endpoints are projected (the closed form is
        /// reported alongside).
        /// </summary>
        [Test]
        public void RecedingGaps_ProjectAsAWorldWeldedAdvance_ThroughTheLiveCamera()
        {
            using var f = CreateFixture();
            double worstErrorPercent = 0.0;
            var table = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;
            table.AppendLine();
            table.AppendLine("   label          gap  measuredPx   projectedPx   err%    closedFormPx (reported)");

            foreach (OffLookAtSymbolId id in RecedingIds)
            {
                SymbolMeasurement m = f.Measure(id);
                for (int g = 0; g + 1 < m.Glyphs.Length; g++)
                {
                    double3 a = m.Glyphs[g].WorldUnity;
                    // Only a DIRECTION comes from the measurement, never a length: the sign of ±ĝ is a layout
                    // fact, not a spacing.
                    double sign = math.sign(math.dot(m.Glyphs[g + 1].WorldUnity - a, f.RecedingDir));
                    double3 b = a + f.RecedingDir * (sign * f.AdvanceWorldMetres);
                    double projectedPx = math.length(
                        GroundRuler.ProjectPx(f.UnityCamera, b) - GroundRuler.ProjectPx(f.UnityCamera, a));
                    double measuredPx = m.ScreenSpacingPx[g];
                    double errorPercent = 100.0 * math.abs(measuredPx / projectedPx - 1.0);
                    worstErrorPercent = math.max(worstErrorPercent, errorPercent);

                    double gapDepth = 0.5 * (m.Glyphs[g].ViewDepthMetres + m.Glyphs[g + 1].ViewDepthMetres);
                    table.AppendLine(string.Format(c, "   {0,-13} {1,3} {2,11:F3} {3,13:F3} {4,7:F3} {5,15:F3}",
                        id, g, measuredPx, projectedPx, errorPercent, f.OracleAtDepth(gapDepth)));
                }
            }

            Assert.That(worstErrorPercent, Is.LessThan(3.0),
                $"Fixture-T3: each receding gap's screen size must be the live projection of ONE world advance " +
                $"({f.AdvanceWorldMetres:F1} m) laid along the road from that glyph's own measured position " +
                $"— worst error {worstErrorPercent:F3} % (bound 3 %).{table}" +
                "   (the closedFormPx column is the PERPENDICULAR closed form, reported only: it reads low " +
                "on a receding arm by construction — see this tooth's doc.)");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Fixture-T4 — monotone decrease, the cheap model-discriminating tooth.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Fixture-T4 — the cheapest discriminating reading here.</b> Proves: along each receding
        /// symbol, screen gaps decrease STRICTLY with view depth, and on <c>RecedingNear</c> the nearest gap is
        /// more than 1.10× the farthest. It needs no oracle: a screen-constant walk gives UNIFORM gaps. Gaps
        /// are ordered by DEPTH, not glyph index, because the keep-upright walk decides which end glyph 0 is.
        /// </summary>
        [Test]
        public void RecedingGaps_ShrinkStrictlyWithDepth()
        {
            using var f = CreateFixture();
            foreach (OffLookAtSymbolId id in RecedingIds)
            {
                SymbolMeasurement m = f.Measure(id);
                int gaps = m.ScreenSpacingPx.Length;
                Assert.That(gaps, Is.GreaterThan(1),
                    $"Fixture-T4 precondition ({id}): need at least two gaps to speak of monotonicity, got {gaps}.");

                // Gap g's own depth, so "along the receding direction" is read off the geometry rather than
                // assumed from the index order.
                var gapDepth = new double[gaps];
                for (int g = 0; g < gaps; g++)
                    gapDepth[g] = 0.5 * (m.Glyphs[g].ViewDepthMetres + m.Glyphs[g + 1].ViewDepthMetres);
                bool depthRisesWithIndex = gapDepth[gaps - 1] > gapDepth[0];

                double worstStep = double.MaxValue;
                var report = new StringBuilder();
                for (int g = 0; g + 1 < gaps; g++)
                {
                    // (shallower gap) − (deeper gap): must be strictly positive at every step.
                    double step = depthRisesWithIndex
                        ? m.ScreenSpacingPx[g] - m.ScreenSpacingPx[g + 1]
                        : m.ScreenSpacingPx[g + 1] - m.ScreenSpacingPx[g];
                    worstStep = math.min(worstStep, step);
                }
                for (int g = 0; g < gaps; g++)
                    report.Append(string.Format(CultureInfo.InvariantCulture,
                        " [{0}] {1:F3} px @ {2:F0} m;", g, m.ScreenSpacingPx[g], gapDepth[g]));

                double nearest = depthRisesWithIndex ? m.ScreenSpacingPx[0] : m.ScreenSpacingPx[gaps - 1];
                double farthest = depthRisesWithIndex ? m.ScreenSpacingPx[gaps - 1] : m.ScreenSpacingPx[0];

                Assert.That(worstStep, Is.GreaterThan(0.0),
                    $"Fixture-T4 ({id}): screen gaps must shrink STRICTLY as view depth grows — smallest step " +
                    $"{worstStep:F6} px. Gaps:{report} A flat sequence is the earlier screen-constant walk.");

                if (id == OffLookAtSymbolId.RecedingNear)
                    Assert.That(nearest / farthest, Is.GreaterThan(1.10),
                        $"Fixture-T4 ({id}): the nearest gap must exceed the farthest by more than 10 % — reads " +
                        $"{nearest / farthest:F4} ({nearest:F3} px vs {farthest:F3} px). RecedingFar is " +
                        "deliberately excluded from this clause: at ~2× the depth the same world span is a " +
                        "smaller relative spread, and a bound with no margin is not a tooth.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Fixture-T5 — the device-pixel ratio.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Fixture-T5 — the DPR tooth.</b> Fixture-T2 re-run on a fixture built at
        /// <c>DevicePixelRatio = 2</c>: every gap of every curved symbol must still measure
        /// <c>AdvanceWorldMetres</c>, the SAME metres as at DPR 1 (not twice: <c>MetresPerDevicePixel</c>
        /// halves at DPR 2). Non-obvious why: <c>TextSizePx</c> is LOGICAL px, so a ruler that dropped
        /// <c>× DevicePixelRatio</c> in <c>SymbolPlacementSystem</c> reads 0.5× here and 1.0× at DPR 1.
        /// </summary>
        [Test]
        public void EveryCurvedGap_MeasuresTheSameWorldAdvance_AtDevicePixelRatioTwo()
        {
            using var f = CreateFixture(devicePixelRatio: 2.0);
            Assert.That(f.Config.DevicePixelRatio, Is.EqualTo(2.0),
                "Fixture-T5 precondition: this tooth means nothing unless the scene really was built at DPR 2.");
            AssertEveryGapMatchesTheWorldAdvance(f, "Fixture-T5 (DPR 2)", boundPercent: 1.0);
        }

        // ── shared ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Computes the world residual for every gap of all four curved symbols, asserts the WORST,
        /// and puts the full per-symbol/per-gap table in the failure message (NUnit throws on the first
        /// failure, so asserting per gap would let the first one shadow the rest).</summary>
        private static void AssertEveryGapMatchesTheWorldAdvance(
            OffLookAtSymbolScene f, string what, double boundPercent)
        {
            double expectedM = f.AdvanceWorldMetres;
            Assert.That(expectedM, Is.GreaterThan(0.0),
                $"{what} precondition: AdvanceWorldMetres reads {expectedM} — the expectation is degenerate " +
                "and every residual below would be meaningless.");

            double worstErrorPercent = 0.0;
            int gapsChecked = 0;
            var table = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;
            table.AppendLine();
            table.AppendLine(string.Format(c,
                "   expected advance = {0:F2} m  (AdvanceBakedPx {1:F1} / OneEm × TextSizePx {2:F1} × " +
                "metresPerLogicalPixel {3:F4}, DPR {4:F2})",
                expectedM, f.Config.AdvanceBakedPx, f.Config.TextSizePx, f.MetresPerLogicalPixel,
                f.Config.DevicePixelRatio));
            table.AppendLine("   label          gap      worldM        err%     viewDepthM");

            foreach (OffLookAtSymbolId id in CurvedIds)
            {
                SymbolMeasurement m = f.Measure(id);
                Assert.That(m.WorldSpacingM.Length, Is.EqualTo(f.Config.GlyphCount - 1),
                    $"{what} precondition ({id}): expected {f.Config.GlyphCount - 1} gaps, got " +
                    $"{m.WorldSpacingM.Length} — the label did not stage every glyph.");
                for (int g = 0; g < m.WorldSpacingM.Length; g++)
                {
                    double errorPercent = 100.0 * math.abs(m.WorldSpacingM[g] / expectedM - 1.0);
                    worstErrorPercent = math.max(worstErrorPercent, errorPercent);
                    gapsChecked++;
                    table.AppendLine(string.Format(c, "   {0,-13} {1,3} {2,11:F1} {3,11:F4} {4,14:F1}",
                        id, g, m.WorldSpacingM[g], errorPercent,
                        0.5 * (m.Glyphs[g].ViewDepthMetres + m.Glyphs[g + 1].ViewDepthMetres)));
                }
            }

            Assert.That(gapsChecked, Is.EqualTo(CurvedIds.Length * (f.Config.GlyphCount - 1)),
                $"{what} precondition: {gapsChecked} gaps were checked, not " +
                $"{CurvedIds.Length * (f.Config.GlyphCount - 1)} — a label is missing and the worst-case " +
                "assertion below would be taken over the wrong set.");
            Assert.That(worstErrorPercent, Is.LessThan(boundPercent),
                $"{what}: every glyph advance must be ONE fixed world length, everywhere in the frame — " +
                $"worst residual {worstErrorPercent:F4} % over {gapsChecked} gaps (bound " +
                $"{boundPercent:F2} %).{table}" +
                "   A receding label reading well ABOVE the expected advance, growing with depth, is a " +
                "SCREEN-uniform walk (or a world walk scaled by one per-label constant, which is the same " +
                "thing), which is wrong under perspective. A uniform 0.5× at DPR 2 with DPR 1 " +
                "green is the dropped DevicePixelRatio factor.");
        }
    }

    // Unity EditMode only — the tilted point-symbol fixture. Two point features with IDENTICAL text ("A") at
    // distinct mid-tile coordinates, under a tilt=45° camera: the geojson→symbol pipeline must land ink where
    // the camera projects the authored coordinate. Text point-symbols only.
    //
    // Non-obvious why: the oracle is not `IProjection.GroundToScreen`, which is exact only at tilt 0. The oracle
    // rebuilds `SceneOriginRender = proj.Project(lookAt)` from public API and projects through the LIVE camera
    // (`GroundRuler.ProjectPx`). NO FLIP: VisualFrame.Pixels and WorldToScreenPoint are both bottom-left.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonPointSymbolFixtureTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    internal class GeoJsonPointSymbolFixtureTests
    {
        // ── The proof geometry: one tile, two mid-tile points, generous margins on every side ──────────
        private static readonly TileId ProofTile = new TileId { Z = 6, X = 40, Y = 25 };

        // Tile-local, separated in LONGITUDE: at heading 0 east/west is iso-depth, so both glyphs share one
        // centroid bias (T-Pos). AssertMidTileFence asserts ≥25% clearance from every edge.
        private const double PointAU = 0.30, PointBU = 0.70, PointV = 0.50;
        private const double MidTileFenceMinFraction = 0.25;

        private const string FontName   = "Fixture Point Label Font";
        private const string Text  = "A";
        private const double TextSizePx = 32.0;
        private const double TiltDeg    = 45.0;
        private const int    SizePx     = 512;

        // Half-size of the predicted-anchor ink window, px: a PRESENCE floor only (T-Present), far wider than
        // the ~19 px cell. T-Pos's tolerance bounds the position, not this.
        private const int WindowHalfPx = 80;

        // T-Pos: the ink-centroid-to-anchor tolerance, px. Residuals of ~1.3 px (printed each run) are the
        // glyph-metric bias of an ink reading; 5 px is ~3.7× that and still catches a real misplacement.
        private const double PositionToleranceN = 5.0;

        // The two residual VECTORS share one bias (identical text), so they must agree within 2 px. That
        // separates a per-symbol placement error from the shared bias, which the distance bound cannot.
        private const double ResidualAgreementToleranceN = 2.0;

        private const int OnScreenMarginPx = 20;

        // Anti-blob bounds for T-Distinct: a green frame reads no ink between the symbols and 0.27% filled.
        // They are not zero, to tolerate AA fringe.
        private const int    BetweenBandInkFloor    = 2;
        private const double WholeFrameFillCeiling  = 0.01;

        private static byte[] _glyphBytes;

        // ── Fixture geometry helpers ──────────────────────────────────────────────────────────────────

        private static GeoCoordinate3D ProofLookAt()
        {
            double2 c = ProofTile.ToLonLat(0.5, 0.5, 1.0);
            return new GeoCoordinate3D { Longitude = c.x, Latitude = c.y, Altitude = 0.0 };
        }

        private static (double lon, double lat) TileLocal(double u, double v)
        {
            double2 ll = ProofTile.ToLonLat(u, v, 1.0);
            return (ll.x, ll.y);
        }

        private static void AssertMidTileFence()
        {
            AssertFraction(PointAU, "point A's U");
            AssertFraction(PointBU, "point B's U");
            AssertFraction(PointV, "both points' V");

            static void AssertFraction(double f, string what)
            {
                Assert.That(f, Is.GreaterThanOrEqualTo(MidTileFenceMinFraction)
                                .And.LessThanOrEqualTo(1.0 - MidTileFenceMinFraction),
                    $"fixture precondition (mid-tile fence): {what} ({f:F2}) must sit >= " +
                    $"{MidTileFenceMinFraction:P0} of the tile's extent from every edge — no cross-tile " +
                    "seam interaction is in scope here.");
            }
        }

        // ── Scene builders ────────────────────────────────────────────────────────────────────────────

        private static VisualScene BuildScene(
            (double lon, double lat) a, (double lon, double lat) b, int expectedQuads)
        {
            return VisualScene.New()
                .Source("points", GeoJson.Points((a.lon, a.lat, Text), (b.lon, b.lat, Text)))
                .Layer(VisualLayer.SymbolText("labels").Source("points").TextField("name")
                    .TextSize(TextSizePx).TextFont(FontName).TextColor("#ffffff"))
                .Glyphs(FontName, LoadGlyphBytes())
                .Camera(ProofLookAt(), zoom: ProofTile.Z, tilt: TiltDeg)
                .ExpectSymbolQuads(expectedQuads);
        }

        private static VisualScene BuildEmptyScene()
        {
            return VisualScene.New()
                .Source("points", GeoJson.FeatureCollection(
                    "{\"type\":\"FeatureCollection\",\"features\":[]}"))
                .Layer(VisualLayer.SymbolText("labels").Source("points").TextField("name")
                    .TextSize(TextSizePx).TextFont(FontName).TextColor("#ffffff"))
                .Glyphs(FontName, LoadGlyphBytes())
                .Camera(ProofLookAt(), zoom: ProofTile.Z, tilt: TiltDeg)
                .ExpectSymbolQuads(0);
        }

        // ── The oracle (see the file header) ───────────────────────────────────────────────────────────

        private static double2 PredictedScreenPx(VisualFrame frame, double lon, double lat)
        {
            var camera = frame.MapView.Camera;
            IProjection proj = camera.Projection;
            CameraProperties cp = camera.CurrentProperties;
            var lookAt = new GeoCoordinate
            {
                Latitude  = proj.ClampValidLatitude(cp.LookAt.Latitude),
                Longitude = cp.LookAt.Longitude,
            };
            double3 sceneOriginRender = proj.Project(lookAt);
            double3 pointRender       = proj.Project(new GeoCoordinate { Latitude = lat, Longitude = lon });
            double3 worldUnity        = pointRender - sceneOriginRender;
            return GroundRuler.ProjectPx(frame.Camera, worldUnity);
        }

        private static void WindowAround(double2 center, out int x0, out int y0, out int x1, out int y1)
        {
            int cx = (int)math.round(center.x);
            int cy = (int)math.round(center.y);
            x0 = cx - WindowHalfPx; x1 = cx + WindowHalfPx;
            y0 = cy - WindowHalfPx; y1 = cy + WindowHalfPx;
        }

        private static void AssertOnScreenWithMargin(VisualFrame frame, double2 predicted, string label)
        {
            Assert.That(predicted.x, Is.GreaterThanOrEqualTo(OnScreenMarginPx)
                                        .And.LessThanOrEqualTo(frame.Width - OnScreenMarginPx),
                $"label {label}'s predicted screen X ({predicted.x:F1}) must land on-screen with margin " +
                $"(frame width {frame.Width})");
            Assert.That(predicted.y, Is.GreaterThanOrEqualTo(OnScreenMarginPx)
                                        .And.LessThanOrEqualTo(frame.Height - OnScreenMarginPx),
                $"label {label}'s predicted screen Y ({predicted.y:F1}) must land on-screen with margin " +
                $"(frame height {frame.Height})");
        }

        // ── T-Present + T-Pos (payoff) ────────────────────────────────────────────────────────────────

        [Test]
        public void Present_And_Pos_InkLandsAtEachAuthoredAnchor_AndResidualsAgree()
        {
            AssertMidTileFence();

            (double lon, double lat) a = TileLocal(PointAU, PointV);
            (double lon, double lat) b = TileLocal(PointBU, PointV);
            using var scene = BuildScene(a, b, expectedQuads: 2);
            VisualFrame frame = scene.Render(SizePx);

            // A size mismatch here would silently bias every centroid rather than failing loud.
            Assert.That(frame.Camera.pixelWidth, Is.EqualTo(frame.Width),
                "render target and Unity camera viewport width must agree");
            Assert.That(frame.Camera.pixelHeight, Is.EqualTo(frame.Height),
                "render target and Unity camera viewport height must agree");

            double2 predA = PredictedScreenPx(frame, a.lon, a.lat);
            double2 predB = PredictedScreenPx(frame, b.lon, b.lat);
            AssertOnScreenWithMargin(frame, predA, "A");
            AssertOnScreenWithMargin(frame, predB, "B");

            WindowAround(predA, out int ax0, out int ay0, out int ax1, out int ay1);
            WindowAround(predB, out int bx0, out int by0, out int bx1, out int by1);
            frame.InkStatsIn(ax0, ay0, ax1, ay1, out double2 centroidA, out int inkA);
            frame.InkStatsIn(bx0, by0, bx1, by1, out double2 centroidB, out int inkB);

            TestContext.WriteLine($"T-Present/T-Pos: predA={predA} inkA={inkA} centroidA={centroidA}");
            TestContext.WriteLine($"T-Present/T-Pos: predB={predB} inkB={inkB} centroidB={centroidB}");

            // ── T-Present: the predicted window must carry ink; T-Neg is its negative control. ─────────────
            Assert.Greater(inkA, 0, "label A's predicted window carries no ink — the label did not reach the screen");
            Assert.Greater(inkB, 0, "label B's predicted window carries no ink — the label did not reach the screen");

            // ── T-Pos: the ink centroid must sit within N px of the predicted anchor, and the two symbols'
            // residual vectors (identical text ⇒ identical systematic bias) must agree. ─────────────────────
            double2 residualA = centroidA - predA;
            double2 residualB = centroidB - predB;
            double distA = math.length(residualA);
            double distB = math.length(residualB);
            double agreement = math.length(residualA - residualB);

            TestContext.WriteLine($"T-Pos: residualA={residualA} |.|={distA:F2}px");
            TestContext.WriteLine($"T-Pos: residualB={residualB} |.|={distB:F2}px");
            TestContext.WriteLine($"T-Pos: residual agreement |A-B|={agreement:F2}px (N={PositionToleranceN}px, agreement bound={ResidualAgreementToleranceN}px)");

            Assert.Less(distA, PositionToleranceN,
                $"label A's ink centroid strayed {distA:F2}px from the predicted anchor (N={PositionToleranceN}px)");
            Assert.Less(distB, PositionToleranceN,
                $"label B's ink centroid strayed {distB:F2}px from the predicted anchor (N={PositionToleranceN}px)");
            Assert.Less(agreement, ResidualAgreementToleranceN,
                $"the two identical-text labels' residual vectors disagree by {agreement:F2}px " +
                $"(bound={ResidualAgreementToleranceN}px) — a shared systematic bias should cancel here; " +
                "disagreement means a real per-label placement error, not just the known bias");
        }

        // ── T-Distinct: two separated ink regions, not one blob, and exactly two survivors ───────────────

        [Test]
        public void Distinct_TwoSeparateInkRegions_AndExactlyTwoSurvivors()
        {
            AssertMidTileFence();

            (double lon, double lat) a = TileLocal(PointAU, PointV);
            (double lon, double lat) b = TileLocal(PointBU, PointV);
            using var scene = BuildScene(a, b, expectedQuads: 2);
            VisualFrame frame = scene.Render(SizePx);

            double2 predA = PredictedScreenPx(frame, a.lon, a.lat);
            double2 predB = PredictedScreenPx(frame, b.lon, b.lat);
            WindowAround(predA, out int ax0, out int ay0, out int ax1, out int ay1);
            WindowAround(predB, out int bx0, out int by0, out int bx1, out int by1);

            frame.InkStatsIn(ax0, ay0, ax1, ay1, out _, out int inkA);
            frame.InkStatsIn(bx0, by0, bx1, by1, out _, out int inkB);
            Assert.Greater(inkA, 0, "(a) label A's window must carry ink");
            Assert.Greater(inkB, 0, "(a) label B's window must carry ink");

            // (b) the BETWEEN band (the strip strictly between the two windows) must be ≈ empty — the
            // anti-blob guard: membership in a window is not coverage (lessons tooth-membership-is-not-coverage).
            int leftX1  = math.min(ax1, bx1);
            int rightX0 = math.max(ax0, bx0);
            // The two windows must not overlap or there is no between-band to read — a fixture precondition,
            // not an assumption.
            Assert.Less(leftX1, rightX0,
                $"fixture precondition: the two anchor windows overlap (A=[{ax0},{ax1}), B=[{bx0},{bx1})) — " +
                "no between-band exists to assert against; widen the anchor separation or shrink WindowHalfPx.");
            int betweenY0 = math.min(ay0, by0);
            int betweenY1 = math.max(ay1, by1);
            frame.InkStatsIn(leftX1, betweenY0, rightX0, betweenY1, out _, out int inkBetween);
            TestContext.WriteLine($"T-Distinct: inkA={inkA} inkB={inkB} inkBetween={inkBetween}");
            Assert.LessOrEqual(inkBetween, BetweenBandInkFloor,
                $"(b) the band between the two labels carries {inkBetween} ink pixels — the two windows read " +
                "as one blob rather than two separated labels");

            // (c) ink OUTSIDE both windows is bounded (whole-frame fill stays tiny — two small glyph cells).
            SnapshotVerdict verdict = frame.Coverage();
            TestContext.WriteLine($"T-Distinct: whole-frame filled fraction={verdict.FilledFraction:P2}");
            Assert.LessOrEqual(verdict.FilledFraction, WholeFrameFillCeiling,
                $"(c) whole-frame ink fraction ({verdict.FilledFraction:P2}) exceeds the ceiling " +
                $"({WholeFrameFillCeiling:P0}) for two small glyph cells — ink is spreading outside the two " +
                "windows");

            // A dedup/collision drop shows up HERE, as a survivor-count miss, not as missing ink.
            int survivors = frame.MapView.View.SymbolPlacementSystem.LastSurvivorCount;
            Assert.AreEqual(2, survivors,
                $"exactly 2 labels must survive collision/dedup (got {survivors}) — plan lessons " +
                "flaky-tilesymbolkick-settle: a dedup drop shows here, not as missing ink");
        }

        // ── T-Neg (negative control) ──────────────────────────────────────────────────────────────────

        [Test]
        public void Neg_SymbolLayerPresent_EmptyPointsSource_RendersBackgroundOnly()
        {
            using var scene = BuildEmptyScene();
            VisualFrame frame = scene.Render(SizePx);

            // Not Coverage().IsBlank: it fires at >=97% background, which the positive frame (0.27% filled)
            // also meets. Whole-frame InkStatsIn reads the same predicate at the needed granularity.
            frame.InkStatsIn(0, 0, frame.Width, frame.Height, out _, out int inkTotal);
            TestContext.WriteLine($"T-Neg: inkTotal={inkTotal}");
            Assert.AreEqual(0, inkTotal,
                $"a symbol layer present but bound to an EMPTY points source must render ZERO non-background " +
                $"ink frame-wide (got {inkTotal} ink px) — the PRIMARY negative control: without it, " +
                "T-Present/T-Pos's positive arm cannot distinguish 'rendered the authored points' from " +
                "'rendered anything at all'. Layer PRESENT + source EMPTY (rather than 'no layer') pins that " +
                "the authored POINTS produced the ink, not the layer's mere existence.");

            // ExpectSymbolQuads(0) exits the spin at once, so a broken pipeline also gives zero ink.
            // LastInputSymbolCount == 0 proves the points source was genuinely EMPTY.
            int inputSymbolCount = frame.MapView.View.SymbolPlacementSystem.LastInputSymbolCount;
            Assert.AreEqual(0, inputSymbolCount,
                $"the empty points source must feed exactly 0 labels into placement (got {inputSymbolCount})");
        }

        // ── G-VR: golden reference-image regression (change detector, layered ALONGSIDE the analytic
        // teeth above — those stay the correctness oracle; this only catches "different from last bake") ──

        [Test]
        public void Golden_Gv1Symbol_MatchesBakedReference()
        {
            AssertMidTileFence();

            (double lon, double lat) a = TileLocal(PointAU, PointV);
            (double lon, double lat) b = TileLocal(PointBU, PointV);
            using var scene = BuildScene(a, b, expectedQuads: 2);
            VisualFrame frame = scene.Render(SizePx);
            GoldenImage.Assert(frame, "gv1-label");
        }

        // ── Glyph fixture loading (mirrors SymbolProcessorParityTests.LoadUp) ────────────────────────────

        private static byte[] LoadGlyphBytes()
            => _glyphBytes ??= LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");

        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, Path.Combine(relative));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException(
                $"could not locate fixture file under any ancestor of the working directory: {Path.Combine(relative)}");
        }
    }
}
