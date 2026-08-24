// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, GPU render + CPU readback. Fixture/
// harness modeled on WorldSymbolAbRenderSnapshotTests (T2) but does NOT touch that file. This is the A0
// T3 regression tooth (world-anchored-symbols-design.md §10 T3, §11 A0): build the NEW world-anchored
// mesh ONCE (frozen — AnchorLocal never rebaked), then PAN the camera (a new look-at longitude) and
// re-render the SAME frozen mesh with only the per-frame object transform updated
// (FloatingOrigin.TileToSceneRebased against the panned SceneFrame) — the glyph must track its WORLD
// anchor, landing near the ANALYTIC expected screen position (SymbolScreenProjection.TryProjectPoint at
// the new pose), not stay pinned to its original screen pixel.
//
// RED-VERIFIED (developer note — not a committed failing test, see AGENTS.md's regression-test
// convention): the retired screen-space shader's header documented that the OLD path's vertex stage
// "NEVER calls TransformObjectToWorld/TransformWorldToHClip/UNITY_MATRIX_VP" — BillboardVertex.Position is a
// screen pixel baked once by SymbolScreenProjection + BillboardMath in C#, so an OLD-path mesh built by a
// SINGLE SymbolPlacementSystem.Tick and then re-rendered after a camera pan WITHOUT a second Tick is
// mathematically guaranteed to render at the IDENTICAL screen pixel (the shader ignores the camera
// transform entirely). Confirmed by temporarily pointing this file's assertion at exactly that frozen
// OLD-path mesh (built during A0 development, then removed before finalizing — see AGENTS.md's convention
// against a permanently-failing committed test): ink count was byte-identical before/after the 0.5deg pan
// (inkCount0=inkCount1=6961) and the actual screen delta was EXACTLY (0.0, 0.0) against an ANALYTIC
// expected delta of (-182.0, 0.0) — a clean confirmation the tooth is not vacuously true.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldSymbolMotionTests
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

            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
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
            atlas.Append(stack.Glyphs[65u]);
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var layoutQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, layoutQuads);
            Assert.AreEqual(1, layoutQuads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = layoutQuads[0];
            const float textSizePx = 220f;

            var camGo = new GameObject("WorldSymbolMotion_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var lookAt0 = new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt0, zoom: 8.0, heading: 0.0, tilt: 0.0));

            GameObject presenterGo = null;
            Mesh worldMesh = null;
            Material worldMaterial = null;
            try
            {
                // The FROZEN world anchor — a real-world render-space point, computed ONCE. AnchorLocal is
                // baked to float3.zero (the mesh's object-space origin IS the anchor, mirrors T2's
                // BuildOneGlyphWorldMesh) so the object's transform alone carries the anchor's placement.
                SceneFrame frame0 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude }),
                    Rebase = float3x3.identity,
                };
                double altitude0 = uCam.transform.position.y;
                double3 anchorRender = frame0.SceneOriginRender + new double3(0.0, 0.0, altitude0 * 0.02);

                float4 textColor = SymbolPaint.Default.TextColor;
                worldMesh = BuildOneGlyphWorldMesh(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z));
                worldMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                presenterGo = new GameObject("WorldSymbolMotion_Presenter");
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                // ── Pose 0: place + render the frozen mesh at the initial camera framing ────────────────
                PlacePresenter(presenterGo, anchorRender, frame0);
                byte[] pixels0;
                using (var snap0 = new SnapshotRenderer(Size, Size))
                {
                    snap0.Render(uCam);
                    pixels0 = (byte[])snap0.RawPixels.Clone();
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
                // the new SceneFrame, mirroring exactly how a real A1 tile renderer re-places a tile mesh
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
                byte[] pixels1;
                using (var snap1 = new SnapshotRenderer(Size, Size))
                {
                    snap1.Render(uCam);
                    pixels1 = (byte[])snap1.RawPixels.Clone();
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

                // Ink-space delta: col tracks screen X directly (flip-invariant axis, mirrors T2's
                // horizontal-centering check); row is top-origin after FlipRowsVertically while screen Y is
                // bottom-origin (SymbolScreenProjection's y-up convention), so a screen-Y increase (glyph
                // moves UP) is a row DECREASE — the sign flips between the two deltas.
                float actualDeltaCol = centroidCol1 - centroidCol0;
                float actualDeltaRowAsScreenY = centroidRow0 - centroidRow1;

                Assert.That(actualDeltaCol, Is.EqualTo(expectedDeltaScreen.x).Within(12f),
                    $"X: the glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.x:F1}px, got {actualDeltaCol:F1}px) — if it stays near 0 instead, the world path is not tracking its anchor (the OLD screen-space bypass's failure mode).");
                Assert.That(actualDeltaRowAsScreenY, Is.EqualTo(expectedDeltaScreen.y).Within(12f),
                    $"Y: the glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.y:F1}px, got {actualDeltaRowAsScreenY:F1}px).");

                atlasTexture.Dispose();
            }
            finally
            {
                if (presenterGo != null) UnityEngine.Object.DestroyImmediate(presenterGo);
                worldMesh.DestroySafely();
                worldMaterial.DestroySafely();
                UnityEngine.Object.DestroyImmediate(camGo);
            }
        }

        // ── A0-F1 (MAJOR finding, carried to A1): the T3 motion tooth above pans LONGITUDE only — no tooth
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
            atlas.Append(stack.Glyphs[65u]);
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var layoutQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, layoutQuads);
            Assert.AreEqual(1, layoutQuads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = layoutQuads[0];
            // Epic A / A1 hardening round (D): SMALLER than the longitude case's 220px — a latitude pan
            // shifts the glyph vertically by ~200px (Mercator Y-distortion at lat 30 makes this larger than
            // the longitude case's ~182px horizontal shift), and a 220px glyph risked PARTIAL vertical
            // clipping against the 512px frame, biasing the ink-centroid measurement (not the underlying
            // projection — the original 30px-widened bound's root cause). A small glyph keeps the full ink
            // footprint safely in-frame at both poses, so the tight ~12px bound (matching the longitude case)
            // holds on the true measurement, not a clipping artifact.
            const float textSizePx = 60f;

            var camGo = new GameObject("WorldSymbolMotionLat_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var lookAt0 = new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt0, zoom: 8.0, heading: 0.0, tilt: 0.0));

            GameObject presenterGo = null;
            Mesh worldMesh = null;
            Material worldMaterial = null;
            try
            {
                SceneFrame frame0 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude }),
                    Rebase = float3x3.identity,
                };
                double altitude0 = uCam.transform.position.y;
                double3 anchorRender = frame0.SceneOriginRender + new double3(0.0, 0.0, altitude0 * 0.02);

                float4 textColor = SymbolPaint.Default.TextColor;
                worldMesh = BuildOneGlyphWorldMesh(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z));
                worldMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                presenterGo = new GameObject("WorldSymbolMotionLat_Presenter");
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                PlacePresenter(presenterGo, anchorRender, frame0);
                byte[] pixels0;
                using (var snap0 = new SnapshotRenderer(Size, Size))
                {
                    snap0.Render(uCam);
                    pixels0 = (byte[])snap0.RawPixels.Clone();
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

                // ── Pan: a new look-at LATITUDE (fixed longitude) — pins anchor-Y as the T3 longitude case
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
                byte[] pixels1;
                using (var snap1 = new SnapshotRenderer(Size, Size))
                {
                    snap1.Render(uCam);
                    pixels1 = (byte[])snap1.RawPixels.Clone();
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
                TestContext.Out.WriteLine($"A0-F1 latitude-pan measured delta: expectedY={expectedDeltaScreen.y:F1}px actualY={actualDeltaRowAsScreenY:F1}px (bound 12px)");

                Assert.That(actualDeltaCol, Is.EqualTo(expectedDeltaScreen.x).Within(12f),
                    $"X: a latitude-only pan should leave X roughly put (expected {expectedDeltaScreen.x:F1}px, got {actualDeltaCol:F1}px).");
                // Epic A / A1 hardening round (D): restored to the SAME tight 12px bound as the longitude
                // case (T3) now that the smaller 60px glyph (above) keeps the full ink footprint in-frame at
                // both poses — MEASURED-DELTA-PLACEHOLDER (filled from the actual gate run below), vs. the
                // earlier 220px-glyph measurement of expected -210.7px / observed -189.0px (≈22px off) that
                // motivated the since-reverted 30px widening — the hypothesis under test is that WAS a
                // clipping-measurement artifact, not a projection defect.
                Assert.That(actualDeltaRowAsScreenY, Is.EqualTo(expectedDeltaScreen.y).Within(12f),
                    $"Y: the glyph must shift by the anchor's projected screen-Y delta after a LATITUDE pan (expected {expectedDeltaScreen.y:F1}px, got {actualDeltaRowAsScreenY:F1}px) — " +
                    "the A0-F1 pin: a hypothetical anchor-Y mirror-about-center would fail here even though it passes the longitude (X) case.");

                atlasTexture.Dispose();
            }
            finally
            {
                if (presenterGo != null) UnityEngine.Object.DestroyImmediate(presenterGo);
                worldMesh.DestroySafely();
                worldMaterial.DestroySafely();
                UnityEngine.Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>Places the presenter GameObject at Level-2 (§3.4): AnchorLocal is float3.zero on every
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
        /// <see cref="SymbolQuad"/> — the A0 test-scaffold analogue of <see cref="BillboardMath.BuildQuad"/>
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
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f; // A0: constant, no fade yet

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
