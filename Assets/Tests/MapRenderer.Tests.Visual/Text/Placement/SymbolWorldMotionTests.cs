// World-space symbol motion and curved-path GPU/visual acceptance tests.
//
// Split by the bare-`Object` using collision (System.Object vs UnityEngine.Object,
// CS0104): SymbolWorldMotionTests.cs holds the System-importing (or neutral) members,
// SymbolWorldRenderTests.cs holds the bare-Object users — the two may not merge.
// SymbolLayerOrderSnapshotTests moved OUT of this topic entirely, to
// Rendering/LayerOcclusionTests.cs: all four of its tests pin draw order / render-
// layer occlusion (docs/commit-conventions.md), not text placement geometry.
// CurvedTextOnPathRenderTests aliases `TextAnchor` to MapRenderer.Core.Text.TextAnchor
// (UnityEngine also declares one); no other file in either destination references
// TextAnchor at all, so the alias is inert for them.
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
    // NOT registered in core-tests.csproj. Modeled on SymbolAtlasOrientationSnapshotTests (same fixture glyph,
    // same headless-readback caveats) but does NOT touch that file — this is the GPU A/B equivalence tooth,
    // short-named T2 by the tests that model against it, rendering the SAME real glyph through BOTH the OLD
    // SymbolPlacementSystem.Tick, exactly like the orientation test) and the NEW world-anchored path (a one-off
    // WorldBillboardMeshBuilder mesh presented through Map/Symbol/TextWorld).
    //
    // Y RECONCILIATION (resolved empirically, see WorldSymbolInkAnalysis):
    // the headless camera→RenderTexture readback is vertically mirrored vs on-screen for EVERY path (Unity's
    // well-known render-to-texture flip is a property of the RT-readback route, not of any one shader's
    // math — see SymbolAtlasOrientationSnapshotTests' header). The OLD path's shader additionally bakes an
    // on-screen calibration flip (`ndc.y = -ndc.y`) that only cancels correctly for the on-screen route; the
    // NEW path's stock `TransformObjectToHClip` carries no such calibration. Both raw readbacks are therefore
    // put through the SAME `FlipRowsVertically` un-mirror before comparison (mirrors the orientation test's own
    // treatment) and BOTH must land upright + centered; if the NEW path lands mirrored relative to the OLD
    // path after that un-mirror, the corner-emit convention below (Offset.y / UV.y sign) is the fix point —
    // see the RESOLVED CONVENTION note on <see cref="BuildOneGlyphWorldMesh"/>.

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

            // point text now draws through the world path — a realistic containing tile keeps
            // the AnchorLocal bake float32-safe (TileKey=0 is ~2e7m away, see
            // SymbolAtlasOrientationSnapshotTests' identical note).
            long tileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default, textSizePx: textSizePx, sortKey: 0f, featureIndex: 0, tileKey: tileKey);

            Color32[] oldPixels;
            Color32[] newPixels;

            // ── OLD path: a real SymbolPlacementSystem.Tick, which for a point symbol produces the WORLD
            // path's output — this arm is real-Tick-vs-scaffold, not a literal screen-space "old". Needs its
            // own world base material. ────────────────────────────────────────────────
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

                // Level-2 placement: AnchorLocal is baked relative to the anchor itself (tileOrigin
                // == anchorRender, so AnchorLocal == 0) — a one-symbol test mesh has no real tile to bake
                // against, so the anchor doubles as its own bake origin. The object's position/rotation
                // carry the rest, identically to how a real tile mesh is placed.
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

            // (3) Equivalence — explicit numeric bounds (no vague "AA tolerance"):
            //   - ink centroid within 6px (row and col) of each other;
            //   - ink bbox corners within 8px of each other;
            //   - changed-pixel fraction (R channel, >24 threshold) under 5% of the frame.
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
        /// Builds a one-glyph world-anchored <see cref="Mesh"/> straight from a real <see cref="SymbolQuad"/> —
        /// the test-scaffold analogue of <see cref="BillboardMath.BuildQuad"/>. Production owns the real
        /// per-tile emit; this helper exists only so these teeth can exercise
        /// <see cref="WorldBillboardMeshBuilder"/> against a genuine glyph. AnchorLocal is
        /// <see cref="float3.zero"/> for every corner (the mesh's
        /// object-space origin IS the anchor — see the call site's placement comment); only the corner
        /// <c>Offset</c> varies per vertex, mirroring <c>BillboardMath.BuildQuad</c>'s unrotated
        /// anchor-relative corners exactly (same TL/TR/BR/BL UV mapping, no flip). <b>RESOLVED Y CONVENTION
        /// (empirical, see this file's header):</b> a direct, unflipped carry-over of the OLD path's
        /// Offset sign rendered the glyph UPSIDE DOWN after the shared un-mirror (confirmed by a failing
        /// run of the A/B tooth below — the OLD path's
        /// SymbolPassVertex bakes an extra <c>ndc.y = -ndc.y</c> on-screen calibration flip that the NEW
        /// path's stock MVP has no equivalent of (SymbolTextWorld_ForwardPass.hlsl's header). The resolved
        /// fix point is the emit: <c>Offset.y</c> is negated here (UV stays attached to
        /// its original corner) — a vertical mirror of each corner's SCREEN position while the TEXTURE
        /// content it samples stays put, which is exactly the missing flip. Never move this into the shader
        /// (that would re-import the hack MVP exists to remove).
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

    // Unity EditMode only — real Camera/RenderTexture/Material/Mesh, GPU render + CPU readback. Fixture/
    // harness modeled on WorldSymbolAbRenderSnapshotTests (its T2) but does NOT touch that file. This is the world-anchor
    // MOTION regression tooth: build the NEW world-anchored mesh ONCE (frozen — AnchorLocal never rebaked), then
    // PAN the camera (a new look-at longitude) and re-render the SAME frozen mesh with only the per-frame
    // object transform updated (FloatingOrigin.TileToSceneRebased against the panned SceneFrame) — the glyph
    // must track its WORLD anchor, landing near the ANALYTIC expected screen position
    // (SymbolScreenProjection.TryProjectPoint at
    // the new pose), not stay pinned to its original screen pixel.
    //
    // NOT VACUOUS, measured. A screen-space mesh whose vertex stage never reads the camera transform renders
    // at the IDENTICAL screen pixel after a pan without a second Tick. Pointed at such a mesh, this tooth
    // reads a byte-identical ink count across the 0.5deg pan and a screen delta of EXACTLY (0.0, 0.0)
    // against an ANALYTIC expected delta of (-182.0, 0.0). The check is not committed as a failing test —
    // see AGENTS.md's regression-test convention.

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
                // The FROZEN world anchor — a real-world render-space point, computed ONCE. AnchorLocal is
                // baked to float3.zero (the mesh's object-space origin IS the anchor, mirrors WorldSymbolAbRenderSnapshotTests'
                // BuildOneGlyphWorldMesh) so the object's transform alone carries the anchor's placement.
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

                // ── Pan: a new look-at longitude, same zoom/heading/tilt — the SAME frozen mesh (and its
                // baked AnchorLocal) is reused; only the per-frame object transform is recomputed against
                // the new SceneFrame, mirroring exactly how a real tile renderer re-places a tile mesh
                // each frame (FloatingOrigin.TileToSceneRebased), never rebuilding the mesh itself. ─────
                // 0.5deg — a modest pan (a 10deg pan pushed the anchor entirely off the 512px frame, 0
                // ink; this magnitude keeps it on-screen while still producing a >50px expected shift,
                // verified below).
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

                // ── The analytic expected shift (Offset is a fixed additive clip-space term that cancels
                // exactly in a delta — see SymbolTextWorld_ForwardPass.hlsl's pinned math and
                // WorldBillboardRtcAlgebraTests/T1's identical reasoning — so the glyph's screen delta must
                // equal the ANCHOR's screen delta, independent of the constant glyph-corner offset). ──────
                float2 expectedDeltaScreen = anchorScreen1 - anchorScreen0;
                Assert.Greater(math.abs(expectedDeltaScreen.x) + math.abs(expectedDeltaScreen.y), 50f,
                    "the pan must produce a nontrivial expected screen shift, or this tooth is vacuous.");

                // Ink-space delta: col tracks screen X directly (flip-invariant axis, mirrors WorldSymbolAbRenderSnapshotTests'
                // horizontal-centering check); row is top-origin after FlipRowsVertically while screen Y is
                // bottom-origin (SymbolScreenProjection's y-up convention), so a screen-Y increase (glyph
                // moves UP) is a row DECREASE — the sign flips between the two deltas.
                float actualDeltaCol = centroidCol1 - centroidCol0;
                float actualDeltaRowAsScreenY = centroidRow0 - centroidRow1;

                Assert.That(actualDeltaCol, Is.EqualTo(expectedDeltaScreen.x).Within(12f),
                    $"X: the glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.x:F1}px, got {actualDeltaCol:F1}px) — if it stays near 0 instead, the world path is not tracking its anchor (the OLD screen-space bypass's failure mode).");
                Assert.That(actualDeltaRowAsScreenY, Is.EqualTo(expectedDeltaScreen.y).Within(12f),
                    $"Y: the glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.y:F1}px, got {actualDeltaRowAsScreenY:F1}px).");
            }
        }

        // ── The motion tooth above pans LONGITUDE only — no tooth
        //    isolated the anchor's VERTICAL GPU projection. Pans LATITUDE at fixed longitude instead, pinning
        //    anchor-Y against the SAME analytic SymbolScreenProjection delta the longitude case pins anchor-X
        //    against — a hypothetical anchor-Y mirror-about-center would pass the longitude case but fail
        //    this one. RED-VERIFIED like the longitude case: a frozen OLD screen-space mesh (which ignored
        //    the camera transform entirely, per the retired shader's header) stays pinned at its
        //    original screen pixel after the pan, so it fails this assertion exactly as it fails the
        //    longitude one (confirmed by the same manual swap-in-old-mesh check documented at this file's
        //    header, applied to a latitude pan). ──
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
            // SMALLER than the longitude case's 220px — a latitude pan
            // shifts the glyph vertically by ~200px (Mercator Y-distortion at lat 30 makes this larger than
            // the longitude case's ~182px horizontal shift), and a 220px glyph risked PARTIAL vertical
            // clipping against the 512px frame, biasing the ink-centroid measurement (not the underlying
            // projection — the original 30px-widened bound's root cause). A small glyph keeps the full ink
            // footprint safely in-frame at both poses, so the tight ~12px bound (matching the longitude case)
            // holds on the true measurement, not a clipping artifact.
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

        /// <summary>Builds a one-glyph world-anchored <see cref="Mesh"/> straight from a real
        /// <see cref="SymbolQuad"/> — the test-scaffold analogue of <see cref="BillboardMath.BuildQuad"/>
        /// (duplicated from WorldSymbolAbRenderSnapshotTests.BuildOneGlyphWorldMesh, not shared — a private
        /// per-file helper, same reasoning as this file's AtlasMetrics shim). AnchorLocal is
        /// <see cref="float3.zero"/> for every corner; only the corner <c>Offset</c> varies, mirroring
        /// <c>BillboardMath.BuildQuad</c>'s unrotated anchor-relative corners. <b>Same RESOLVED Y
        /// CONVENTION as WorldSymbolAbRenderSnapshotTests</b> (see that file's identical helper for the
        /// full empirical rationale): <c>Offset.y</c> is negated per corner; UV stays attached to its
        /// original corner.</summary>
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
    // WHY THIS FILE EXISTS. The maintainer's very first reported defect was that road symbols render "not on the
    // road geometry itself but with some offset, below or above the road" — and it reproduces at tilt 0, so it is
    // not a pitch defect at all. The cause is a producer one: CurvedTextLayout baked every cell BASELINE-relative
    // while the point path applied TextQuadLayout's optical-centre shift. The earlier gate was structurally blind
    // to it: every downstream curved fixture HAND-BUILDS its CurvedGlyph.Cell, and the two rendered curved
    // fixtures borrow the POINT layout as a quad factory. These teeth take their cell from the REAL curved
    // producer, which is the whole point of them.
    //
    // WHY TILT 0 IS THE RIGHT POSE, not a weaker one. This is where the maintainer sees the defect, and it is
    // also where the reading is cleanest: at tilt 0 with the road at screen angle 0° the road is one screen row,
    // so "off the road" is exactly "off in screen rows". No tilt-dependent convexity, no collision-box-vs-world
    // question (the collision-box teeth own that), and the arm is falsifiable exactly where the bug was reported.
    //
    // EXTREMES, NOT CENTROIDS. Both teeth read minRow/maxRow, never a centroid. A centroid is a faithful position
    // reading at tilt 0 only (MapPitchedGlyphSizeTiltZeroTests' header states why, and a 12.41 px
    // convexity gap appears the moment tilt is non-zero); bounding-box extremes project exactly and carry the
    // reading these teeth need — the ink band's MID-ROW — without borrowing that caveat at all.
    //
    // '5' IS THE GLYPH, AND THAT IS LOAD-BEARING. It is baseline-resting AND exactly one nominal cap height tall
    // on the committed fixture (CurvedTextCentringTests' T1 asserts both from the entry itself), so its ink
    // band is EXACTLY symmetric about the anchor after the fix. That is what lets Curved-T4 carry a derived bound rather
    // than a fitted one: the residual is rasterisation + SDF-threshold error only. A glyph with a descender would
    // need a per-glyph ink-bounds correction, and the tooth would then be measuring its own arithmetic.
    //
    // NO METRE LITERALS: every world length here is a multiple of `scene.MetresPerDevicePixel`, the same rule
    // TiltedGroundScene's consumers all enforce — a bare metre literal is sub-pixel at this pose.

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
        /// Row-agreement bound, in device pixels. DERIVED, not fitted: for '5' the cap band is exactly
        /// symmetric about the anchor in cell coordinates (<c>CurvedTextCentringTests</c> T1, with its
        /// fixture preconditions), so the only residual left in a rendered reading is rasterisation plus the
        /// SDF alpha threshold — sub-pixel on each edge, and the two edges enter the mid-row averaged. 4.0
        /// device px is 0.6 BAKED px at this text size. The defect this file exists for reads
        /// <c>17.5 · 160/24 = 116.67 px</c>, a 29× discrimination margin.
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
        /// <b>Curved-T4 — a curved symbol's ink is centred on the road it is drawn along, at tilt 0.</b> Proves the
        /// maintainer's reported defect is gone, in the pose they reported it in, measured from a REAL
        /// <see cref="CurvedTextLayout"/> cell rendered through the real placement system and the real shader.
        ///
        /// <para><b>The oracle shares no code with the arm under test.</b> It is the road anchor's own screen
        /// row, obtained by projecting the anchor with the fixture's camera
        /// (<c>UnityCamera.WorldToScreenPoint</c>) and converting to the top-down row convention the ink
        /// analysis reads in. It does not come from the cell, from
        /// <c>TextQuadLayout.OpticalCentreBelowReferencePx</c>, or from anything this tooth's own arm edits:
        /// a reference drawn from the arm under test cancels the very defect it is meant to expose.</para>
        ///
        /// <para><b>Why the mid-row of the ink bbox is the right measurement.</b> Cell y = 0 IS the point on
        /// the path: both consumers map the cell linearly and homogeneously about the anchor
        /// (<c>BillboardMath.BuildWorldQuad</c>, <c>SymbolBox.BuildRotatedGlyph</c>), so zero maps to zero
        /// under every branch. '5' being exactly cap-height and baseline-resting, its ink band is symmetric
        /// about that zero, and the rendered band's midpoint must therefore land on the anchor's row.</para>
        ///
        /// <para>RED recipe: remove the shift (the earlier code) ⇒ 116.7 px. Apply it twice ⇒ 116.7 px the
        /// other way. Negate it ⇒ 233 px. The measured residual is printed on every run as a drift
        /// baseline.</para>
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
        /// the same anchor, rendered.</b> The render-level statement of the requirement that the two producers
        /// have the same optical relationship to their anchor.
        ///
        /// <para><b>Worth its cost even beside Curved-T4, for two distinct reasons.</b> (1) The point arm shares
        /// NO code with the curved producer — <c>TextQuadLayout</c>'s centre shift was fixed by a different
        /// stage, is pinned by <c>TextVerticalCentringTests</c>, and is untouched here, so this is
        /// an independent reference rather than a self-derivation. (2) It is INVARIANT to any mistake in
        /// Curved-T4's row/flip convention: both arms are read through the identical scan, so a convention error
        /// cancels here and cannot make this tooth green for the wrong reason. Curved-T4 and Curved-T5 failing together
        /// means the shift is wrong; Curved-T4 alone failing means the row convention is.</para>
        ///
        /// <para>Both arms render at the same <see cref="TextSizePx"/>, from the same atlas, at the same
        /// world anchor, in the same scene and camera — the pair differs in the PRODUCER and nothing else.</para>
        ///
        /// <para>RED recipe: identical to Curved-T4's — every injection that moves the curved cell moves this by
        /// the same 116.7 px, because the point arm does not move at all.</para>
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
        /// projected screen row.
        ///
        /// <para>'5' is built here rather than reusing <c>WorldCurvedAbRenderSnapshotTests.BuildGlyphF</c>:
        /// that helper hands back a POINT-layout quad for 'F', and this file needs both the curved producer's
        /// own output and a glyph whose ink band is exactly symmetric about the anchor (the header explains
        /// why '5' and not 'F'). The shaped run is built directly from <see cref="PositionedGlyph"/> — both
        /// layouts step the pen by the atlas entry's own advance, so no shaper/metrics adapter is involved.</para>
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

                // The road, in the render-space XZ plane, at screen angle 0° (east = X; the flat local
                // approximation is exact enough at zero tilt and zero heading). At this pose a screen-
                // horizontal road makes "off the road" exactly "off in screen rows". NO metre literals —
                // every length is a multiple of the frame ruler.
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

                // The oracle. The anchor is the road's midpoint, which is the scene origin, which is Unity
                // world Vector3.zero after RTC.
                //
                // The row convention, derived link by link rather than assumed:
                // (1) the frame is BOTTOM-UP (row 0 is the bottom scanline, Unity's native ReadPixels
                // convention); (2) FlipRowsVertically turns it top-down, which is what AnalyzeInk scans;
                // (3) WorldToScreenPoint's y is bottom-up in device pixels. So a top-down row r is bottom-up
                // row (SizePx-1-r), whose centre is at screen y = SizePx - r - 0.5, giving r = SizePx - 0.5 - y.
                // The half-pixel is two orders below the bound and is written out rather than dropped so the
                // convention is legible. Curved-T5 is the control that does not depend on any of this.
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
    // The product-observing tooth for the symbol text-color carrier split: every other tooth in
    // SymbolTextColorCarrierTests shares a CPU model of the fragment (`vertex × uniform`) with the code it
    // checks — a model that would agree with a plausible wrong port just as readily as with the real one. This
    // reads the rendered pixel itself, through the full production path (VisualScene → real MapView →
    // SymbolRenderLayer's per-layer material → the real shader).
    //
    // Two arms differing ONLY in `text-color` — grey #808080 and white #ffffff — same glyph, same anchor. The
    // ratio grey/white must land at linear(0.5019) ≈ 0.2158: the authored multiplier, applied ONCE. Landing
    // on its square (≈0.0466) means both carriers hold the colour; landing on 1.0 means the vertex carries
    // white and the uniform never reached the fragment.
    //
    // This tooth is an INVARIANT, not a regression check: it must pass against the UNMODIFIED tree too (the
    // colour then rides the vertex stream alone, with no uniform in the picture at all) and pass again once
    // the two-carrier split lands. A RED baseline means the harness resolved the wrong material — see the
    // stage plan's escalation note before touching this file.

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

        // Half-size of the box sampled for the LINEAR colour average — small enough to sit well inside the
        // glyph's solid interior (mirrors PaintColorRenderTests' "sample box comfortably inside" pattern),
        // once centred on the measured ink centroid rather than an assumed screen position.
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
            using var whiteScene = BuildScene("#ffffff");
            VisualFrame whiteFrame = whiteScene.Render(SizePx);

            // Locate the glyph from the white arm's own render — no assumed screen position. The box then
            // reused for the grey arm too, since both arms share IDENTICAL geometry (colour is the only
            // difference), so the same rectangle samples the same glyph pixels in both.
            whiteFrame.InkStatsIn(0, 0, whiteFrame.Width, whiteFrame.Height, out double2 centroid, out int inkCount);
            TestContext.WriteLine($"[SymbolTextColorRender] white ink centroid={centroid} count={inkCount}");
            Assert.Greater(inkCount, 0, "the white arm must render some ink to locate the glyph from.");

            int cx = (int)math.round(centroid.x), cy = (int)math.round(centroid.y);
            int x0 = cx - SampleHalf, x1 = cx + SampleHalf + 1;
            int y0 = cy - SampleHalf, y1 = cy + SampleHalf + 1;

            double3 whiteSample = SampleLinearBox(whiteFrame, x0, y0, x1, y1);

            using var greyScene = BuildScene("#808080");
            VisualFrame greyFrame = greyScene.Render(SizePx);
            double3 greySample = SampleLinearBox(greyFrame, x0, y0, x1, y1);

            double3 ratio = greySample / whiteSample;
            const double expected = 0.2158; // linear(0x80/255)
            const double squared  = expected * expected;
            TestContext.WriteLine($"[SymbolTextColorRender] white={whiteSample} grey={greySample} ratio={ratio} " +
                                   $"expected~={expected} squared~={squared}");

            // The ratio construction cancels any factor common to BOTH arms — a shader edit that scales
            // every text pixel would leave the ratio (and this tooth) green over a real defect. Assert the
            // white arm's absolute intensity first, so a bad denominator (partial coverage, not colour)
            // reports as a bad denominator rather than a confusing ratio.
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
    // The FIXTURE arm (Fixture-T1…Fixture-T5): the assertions the apparatus was built for and deferred.
    // Read `OffLookAtSymbolScene`'s header first; `OffLookAtSymbolFixtureTests` (M1–M13) is the
    // apparatus' own acceptance suite and every tooth there still passes unchanged.
    //
    // THE MODEL, SETTLED, NOT RE-DERIVED HERE: `text-size` under `*-pitch-alignment: map` means X px TOP-DOWN.
    // A glyph advance is fixed ONCE as a world length and the perspective divide does the rest, so letters AND
    // letter spacing foreshorten together — the same principle as `line-width`.
    //
    // THE TRAP THESE TEETH EXIST TO AVOID. `CrossNear`/`CrossFar` are ISO-DEPTH inherently, and for an
    // iso-depth symbol a true per-glyph WORLD walk and a screen walk scaled by ONE per-symbol constant produce
    // IDENTICAL output — and that second thing is the screen-walk model these teeth exist to reject. A change
    // can be green on all 14 apparatus teeth while re-implementing the bug. Fixture-T2/T3/T4 live on the RECEDING
    // (depth-spanning) arm and carry the falsifiability; Fixture-T1 is the inherited regression tooth and Fixture-T5
    // is the DPR tooth.
    //
    // ORACLE HYGIENE. `AdvanceWorldMetres` is `AdvanceBakedPx` and `TextSizePx` (fixture constants), `OneEm` (a
    // unit definition), and `MapCamera.MetresPerDevicePixel × Config.DevicePixelRatio` (the frame ruler, whose
    // DPR factor the fixture applies from its OWN constant — never read back out of production, or the two sides
    // would drop it together and Fixture-T5 would be vacuous). Nothing measured feeds it. Where a tooth projects through
    // the live camera (Fixture-T3) the only measured input is a POSITION; the LENGTH projected is always the constant.

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
        /// <b>Fixture-T1 — THE REGRESSION TOOTH the whole epic was chasing.</b> Proves: the cross-azimuth pair's
        /// mean screen spacing halves when the view depth doubles — far/near reads 0.500, where the earlier
        /// screen-constant layout read 1.0000.
        ///
        /// <para>It is a RATIO of two readings from ONE frame and ONE symbol pair, so any uniform scale error
        /// (OneEm, TextSizePx, mpp, DPR, atlas scale, a wrong P11) multiplies both and CANCELS. Only the depth
        /// dependence survives, which is exactly the question.</para>
        ///
        /// <para><b>Does NOT prove that spacing foreshortens per-GLYPH rather than per-LABEL.</b> Both symbols
        /// are iso-depth, so a screen walk scaled by one per-symbol constant passes this tooth. That is what
        /// Fixture-T2 is for, and why Fixture-T1 alone would not be an acceptable set.</para>
        ///
        /// <para>RED-verify: injection I1 (force <c>worldArc = false</c>) — reads 1.0000.</para>
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
        /// the model stated directly: one glyph advance is one fixed world length, everywhere in the frame.
        ///
        /// <para><b>Why it is not self-referential:</b> the comparand is two fixture constants, a unit
        /// definition and the frame ruler. Nothing measured. The MEASURAND is the staged world anchors read
        /// back off the built meshes.</para>
        ///
        /// <para><b>Why a per-symbol constant cannot pass it — the iso-depth trap closed.</b> On the RECEDING
        /// arms a screen-uniform walk (or a world walk scaled by one per-symbol constant, which is the same
        /// thing) produces world gaps that GROW along the symbol as depth increases, reading well over the
        /// oracle at the far end. Against a 1 % bound that is enormous. The cross arms cannot see this; the
        /// receding arms are where the tooth has teeth.</para>
        ///
        /// <para>Both roads are straight two-vertex segments, so chord distance IS arc distance and the
        /// expectation is exact to float — the 1 % is headroom for the RTC bake's float narrowing, not for
        /// model slop.</para>
        ///
        /// <para>RED-verify: injection I1 (force <c>worldArc = false</c>).</para>
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
        /// screen gap must match it within 3 %.
        ///
        /// <para><b>Discipline:</b> the only measured input is a POSITION. The LENGTH projected is the fixture
        /// constant — the exact analogue of <c>OracleAtDepth</c> taking only <c>w</c>. A spacing is never fed
        /// back in.</para>
        ///
        /// <para><b>Why not a closed form.</b> <c>spacingWorld·|P11|·H/(2w)</c> is the PERPENDICULAR span; a
        /// receding displacement's perpendicular component is <c>L·cos θ</c>, so on this arm the closed form
        /// inherently reads far low. Projecting the two real endpoints through the live camera is exact
        /// at any depth AND any direction, needs no <c>cos θ</c>, and imports no second-order correction. The
        /// closed-form number is REPORTED alongside so the substitution is auditable.</para>
        ///
        /// <para>Proves: the screen reading a later stage (the render arm) will consume is the projection of a
        /// world-welded advance. RED-verify: injection I1.</para>
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
                    // A DIRECTION taken from the measurement, never a length: the road runs along ±ĝ and
                    // which sign is a fact about how the symbol was laid out, not about how far apart the
                    // glyphs are.
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
        /// more than 1.10× the farthest.
        ///
        /// <para>Model-discriminating on its own and with no oracle at all: a screen-constant walk gives
        /// UNIFORM gaps (ratio 1.000, no monotonicity), so this cannot pass on the earlier layout. It is the
        /// tooth that survives even if every closed form and every ruler in the fixture were wrong.</para>
        ///
        /// <para>Ordered by DEPTH rather than by glyph index: which end of the road glyph 0 sits at is a
        /// layout detail (the keep-upright walk direction), and a tooth that assumed one would be pinning the
        /// wrong thing.</para>
        ///
        /// <para>RED-verify: injection I1 — gaps go uniform and both clauses fail.</para>
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
        /// <c>AdvanceWorldMetres</c>, the SAME number of metres as at DPR 1.
        ///
        /// <para><b>The expectation is NOT "twice the DPR-1 value".</b> The altitude framing uses
        /// <c>ViewportLogicalPx</c>, so at DPR 2 the orbit radius halves and <c>MetresPerDevicePixel</c>
        /// halves with it — leaving <c>metresPerLogicalPixel</c>, and therefore the advance in metres,
        /// INVARIANT. What does change is that a fixed world length projects to twice as many device px.
        /// Writing "2×" here would encode the wrong law.</para>
        ///
        /// <para><b>What it catches.</b> <c>TextSizePx</c> is LOGICAL px while
        /// <c>MapCamera.MetresPerDevicePixel</c> is per DEVICE px by its own doc. A production ruler that
        /// dropped the <c>× DevicePixelRatio</c> would be half the correct value at DPR 2 — so the measured
        /// world gap reads 0.5× here and exactly 1.0× at DPR 1, where the omission is invisible and no
        /// self-referential oracle could see it either.</para>
        ///
        /// <para>RED-verify: injection I2 (drop <c>* _camera.DevicePixelRatio</c> in
        /// <c>SymbolPlacementSystem</c>) — DPR 1 stays green, this reads 0.5×.</para>
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
                "thing) — the model already reverted twice. A uniform 0.5× at DPR 2 with DPR 1 " +
                "green is the dropped DevicePixelRatio factor.");
        }
    }

    // Unity EditMode only — the tilted point-symbol fixture.
    // NOT registered in Tools/core-tests/core-tests.csproj (engine-bound: drives a real MapViewComponent
    // through the geojson→symbol pipeline and a live SymbolPlacementSystem).
    //
    // Two point features, IDENTICAL text ("A"), DISTINCT lon/lat, mid-tile, under a tilt=45° camera. Proves the
    // geojson→symbol pipeline lands ink where the camera actually projects the authored coordinate — the FIRST
    // proof of point-symbol positional correctness in this kit. Text point-symbols only: no icons, no
    // line/curved placement, no seam interaction, no perspective-foreshortening suite.
    //
    // THE ORACLE. `PredictedScreenPx` is NOT `IProjection.GroundToScreen` — that method is documented as exact
    // only at tilt=0 ("Exact inverse of ScreenToGround at zero tilt") and its Web-Mercator
    // implementation carries no tilt term at all (WebMercatorProjection.cs:98-132), so at this fixture's tilt=45°
    // it would inherently predict the WRONG screen position, not merely imprecisely. The oracle instead
    // re-derives the two-line formula `MapView.BuildSceneFrame` uses (`SceneOriginRender = proj.Project(lookAt)`)
    // from PUBLIC API only (`MapCamera.Projection` / `MapCamera.CurrentProperties`), then projects the resulting
    // Unity-world point through the LIVE camera's own matrix (`GroundRuler.ProjectPx` → `WorldToScreenPoint`) —
    // the same "project a KNOWN world point through the live camera" oracle discipline `OffLookAtSymbolScene`
    // established (`GroundRuler.cs`'s own doc: "a second implementation of the projection inside the test is the
    // thing most likely to be wrong"). This is shared reference infra (projection + camera-pose bookkeeping), not
    // the mechanism under test (SymbolFeatureExtractor / SymbolPlacementSystem).
    //
    // NO FLIP. VisualFrame.Pixels is bottom-left origin (SnapshotRenderer's own doc); Camera.WorldToScreenPoint
    // is also bottom-left, +y up. InkStatsIn's window coordinates and the oracle's predicted px therefore compare
    // directly, with no coordinate conversion anywhere in this file.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonPointSymbolFixtureTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    internal class GeoJsonPointSymbolFixtureTests
    {
        // ── The proof geometry: one tile, two mid-tile points, generous margins on every side ──────────
        private static readonly TileId ProofTile = new TileId { Z = 6, X = 40, Y = 25 };

        // Tile-local unit-square coordinates. Separated in LONGITUDE (U) at a SHARED latitude (V): at
        // heading=0 the camera's cross-azimuth (iso-view-depth) axis is east/west
        // (CameraPoseMath.ComputeRelativePose: pos = (0, alt·cosT, -alt·sinT) at heading 0, so the receding
        // direction is north/south) — so both anchors sit at very nearly the SAME view depth, keeping the
        // glyph's systematic ink-centroid bias the SAME vector for both symbols (what the residual-agreement
        // clause of T-Pos needs). 0.30/0.70 are each ≥25% of the tile's extent from every edge (asserted, not
        // assumed — AssertMidTileFence).
        private const double PointAU = 0.30, PointBU = 0.70, PointV = 0.50;
        private const double MidTileFenceMinFraction = 0.25;

        private const string FontName   = "Fixture Point Label Font";
        private const string Text  = "A";
        private const double TextSizePx = 32.0;
        private const double TiltDeg    = 45.0;
        private const int    SizePx     = 512;

        // Half-size of the predicted-anchor ink window, px — generous around a single 32px-text-size glyph
        // cell (measured ~19px wide, ~355 ink px) so a real but small placement error still lands the ink
        // inside the window. This is a PRESENCE FLOOR ONLY (T-Present's job) — 80px is far wider than the
        // real cell on purpose, since T-Pos's own tight tolerance N is what actually bounds the position; do
        // not read WindowHalfPx as a position claim.
        private const int WindowHalfPx = 80;

        // T-Pos: N, the ink-centroid-to-predicted-anchor tolerance, px. Tuned from the MEASURED residual
        // (printed by every T-Pos run via TestContext.WriteLine): a first green run measured |residualA|
        // =1.28px, |residualB|=1.35px — the systematic text-anchor/glyph-metric offset an ink reading
        // carries (this is not OffLookAt's 2px GEOMETRY bound; that reads staged vertex positions, not
        // rendered ink). N=5px is a ~3.7x margin over the observed max, tight enough to catch a real
        // placement defect (RED-verified at a >=40px injected offset) while tolerant of ordinary
        // cross-machine AA/rasterization jitter.
        private const double PositionToleranceN = 5.0;

        // The two symbols' residual VECTORS (identical text ⇒ identical systematic bias) must agree within a
        // few px — the sharper clause that isolates a real per-symbol placement error from the shared bias,
        // which the plain distance-to-anchor bound alone cannot (both could be biased by the same amount in
        // the same direction and still individually pass while genuinely wrong relative to each other — not
        // this fixture's failure mode, but the clause exists for exactly that discrimination). Measured
        // agreement on the same green run: 0.12px; 2.0px leaves a wide margin for jitter while staying far
        // tighter than PositionToleranceN.
        private const double ResidualAgreementToleranceN = 2.0;

        private const int OnScreenMarginPx = 20;

        // Anti-blob floors/ceilings for T-Distinct — tooth membership is not coverage — tuned
        // from a measured green run: inkBetween=0, whole-frame filled fraction=0.27%. Not exactly zero, to
        // tolerate ordinary AA fringe.
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

            // ── T-Present: the predicted window must actually carry ink. RED-verify: rendering
            // BuildEmptyScene() through this same window logic must yield inkA == inkB == 0 (T-Neg is this
            // tooth's own negative control). ──────────────────────────────
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

            // NOT frame.Coverage().IsBlank: SnapshotCoverage.IsBlank fires at >=97% BACKGROUND fraction
            // (SnapshotCoverage.cs), but two small glyph cells cover well under 1% of a 512x512 frame (the
            // POSITIVE fixture measured 0.27% filled) — IsBlank would read TRUE on the positive frame too,
            // making this control vacuous (one instrument's blind spot does not transfer to another: G-V0's
            // fill negative control legitimately used IsBlank because a fill covers tens of percent of the
            // frame; that does not transfer to an instrument reading two glyph cells). InkStatsIn over the
            // WHOLE frame reads the same background predicate at the granularity this fixture needs.
            frame.InkStatsIn(0, 0, frame.Width, frame.Height, out _, out int inkTotal);
            TestContext.WriteLine($"T-Neg: inkTotal={inkTotal}");
            Assert.AreEqual(0, inkTotal,
                $"a symbol layer present but bound to an EMPTY points source must render ZERO non-background " +
                $"ink frame-wide (got {inkTotal} ink px) — the PRIMARY negative control: without it, " +
                "T-Present/T-Pos's positive arm cannot distinguish 'rendered the authored points' from " +
                "'rendered anything at all'. Layer PRESENT + source EMPTY (rather than 'no layer') pins that " +
                "the authored POINTS produced the ink, not the layer's mere existence.");

            // Non-vacuous control: because ExpectSymbolQuads(0) makes SpinUntilSymbolsReady exit on its FIRST
            // iteration (0 >= 0), a zero-ink frame here is ALSO what "the pipeline never staged anything,
            // positive or negative" would look like — a broken glyph/extraction path would pass the inkTotal
            // check above too. LastInputSymbolCount == 0 is what proves the zero-ink frame reflects a
            // genuinely EMPTY points source rather than a pipeline that produced no symbols for any input.
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
