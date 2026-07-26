// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, GPU render + CPU readback. Fixture/harness
// modeled on WorldLabelMotionTests (T3) but does NOT touch that file — this is Stage AC's T-MOT regression
// tooth (curved-world-plan.md §4 T-MOT): build the NEW world-anchored CURVED mesh ONCE (frozen — AnchorLocal/
// Tangent never rebaked, exactly like WorldLabelMotionTests' point case), then PAN AND ROTATE (heading) the
// camera and re-render the SAME frozen mesh with only the per-frame object transform updated — the glyph
// must (a) track its WORLD anchor (position) and (b) RE-ORIENT to the live screen tangent (the whole point
// of Stage AC's shader-side projection — a shallow impl that baked a screen rotation would pass (a) and fail
// (b)). Uses a NONZERO per-glyph AnchorLocal (a tile-corner bake, mirrors WorldPointEmitRenderTests' NEW-F1
// pattern) and a NON-axis-aligned (diagonal) world Tangent, per the plan's explicit T-MOT requirements.
//
// RED-VERIFIED (2026-07-20) — the two failure modes were actually INJECTED and observed to fail, not argued
// by analogy:
//   (b) RE-ORIENTATION (the novel curved-only assertion): injecting a static-angle defect into the shader's
//       along-line branch (SymbolTextWorld_ForwardPass.hlsl — force `ang = 0.0`, skipping the D-E Jacobian)
//       makes this test FAIL after the pan+rotate (Failed(Child) on a filtered run). So the re-orient check
//       genuinely discriminates — a shallow impl that baked a static screen rotation does NOT slip past.
//   (a) POSITION tracking: rides the SHARED anchor line `clip = TransformObjectToHClip(input.anchorOS)`
//       (line ~61) that point/icon use too, which the point motion precedent WorldLabelMotionTests already
//       RED-verifies against a frozen screen-baked mesh; this test additionally exercises it with a nonzero
//       tile-corner AnchorLocal, so the shared reprojection is covered by that precedent (same code path).

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
    public class WorldCurvedMotionTests
    {
        private const int Size = 512;

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
        public void WorldCurvedPath_TracksWorldAnchor_AndReorients_AfterCameraPanAndRotate()
        {
            // 1. Real SDF atlas, real fixture glyph 'F' — asymmetric (see WorldCurvedAbRenderSnapshotTests'
            //    header for why 'A' would be an unsafe choice for an orientation-sensitive tooth).
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[70u]);
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "F", Metrics = new AtlasMetrics(atlas) });
            TextLayoutResult layout = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default);
            Assert.AreEqual(1, layout.Quads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = layout.Quads[0];
            const float textSizePx = 140f;

            var camGo = new GameObject("WorldCurvedMotion_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var lookAt0 = new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt0, zoom: 12.0, heading: 0.0, tilt: 0.0));

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

                // The FROZEN world anchor — the glyph's own world point, a diagonal world Tangent (non-
                // axis-aligned), and a NONZERO AnchorLocal (baked against a NEIGHBOR tile's origin, not the
                // anchor's own containing tile — mirrors WorldPointEmitRenderTests' NEW-F1 pattern).
                double3 anchorRender = frame0.SceneOriginRender + new double3(0.0, 0.0, altitude0 * 0.02);
                var projection = new WebMercatorProjection();
                TileId containingTile = TestTileKeys.Containing(new GeoCoordinate { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude }, zoom: 14);
                TileId neighborTile = new TileId { X = containingTile.X + 1, Y = containingTile.Y, Z = containingTile.Z };
                double3 tileOriginRender = TileRenderOrigin.Project(neighborTile, projection);
                float3 anchorLocal = new float3(
                    (float)(anchorRender.x - tileOriginRender.x),
                    (float)(anchorRender.y - tileOriginRender.y),
                    (float)(anchorRender.z - tileOriginRender.z));

                float diagRad = math.radians(45f);
                var tangentLocal = math.normalize(new float3(math.cos(diagRad), 0f, math.sin(diagRad)));

                float4 textColor = LabelPaint.Default.TextColor;
                worldMesh = BuildOneGlyphWorldMeshCurved(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z),
                    anchorLocal, tangentLocal);
                worldMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                presenterGo = new GameObject("WorldCurvedMotion_Presenter");
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                // ── Pose 0 ──────────────────────────────────────────────────────────────────────────────
                PlacePresenter(presenterGo, tileOriginRender, frame0);
                byte[] pixels0 = RenderAndReadback(uCam, "world-curved-motion-pose0.png");
                WorldSymbolInkAnalysis.AnalyzeInk(pixels0, Size, Size,
                    out int minRow0, out int maxRow0, out int minCol0, out int maxCol0,
                    out float centroidRow0, out float centroidCol0, out int inkCount0);
                Assert.Greater(inkCount0, 30, "pose-0 render must show meaningful ink (not blank/GPU-context-failed).");
                float aspect0 = (float)(maxCol0 - minCol0 + 1) / (maxRow0 - minRow0 + 1);

                float4x4 viewProj0 = math.mul(
                    LabelPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    LabelPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(LabelScreenProjection.TryProjectPoint(
                        anchorRender, frame0.SceneOriginRender, viewProj0, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen0, out _),
                    "anchor must project in front of the camera at pose 0.");

                // ── Pan (new look-at longitude) AND rotate (a new heading) — the SAME frozen mesh (its baked
                //    AnchorLocal/Tangent never rebaked) is reused; only the per-frame object transform is
                //    recomputed against the new SceneFrame, mirroring exactly how a real tile/label renderer
                //    re-places a frozen mesh every frame. ────────────────────────────────────────────────
                // WorldLabelMotionTests' point precedent uses 0.5deg at zoom 8 (~182px shift) — this test runs
                // at zoom 12 (16x finer: each zoom level doubles screen-px-per-degree), so the SAME 0.5deg pan
                // would blow ~2900px past the 512px frame (0 ink). Scaled down by 2^(12-8) to land the SAME
                // ballpark on-screen shift.
                var lookAt1 = new GeoCoordinate3D { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude + 0.5 / 16.0, Altitude = 0.0 };
                mapCamera.SetProperties(new CameraProperties(lookAt1, zoom: 12.0, heading: 90.0, tilt: 0.0));
                mapCamera.SyncToCamera();
                SceneFrame frame1 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt1.Latitude, Longitude = lookAt1.Longitude }),
                    Rebase = float3x3.identity,
                };

                PlacePresenter(presenterGo, tileOriginRender, frame1);
                byte[] pixels1 = RenderAndReadback(uCam, "world-curved-motion-pose1.png");
                WorldSymbolInkAnalysis.AnalyzeInk(pixels1, Size, Size,
                    out int minRow1, out int maxRow1, out int minCol1, out int maxCol1,
                    out float centroidRow1, out float centroidCol1, out int inkCount1);
                Assert.Greater(inkCount1, 30, "pose-1 render must show meaningful ink (not blank/GPU-context-failed) — a mistracked anchor could also land off-screen.");
                float aspect1 = (float)(maxCol1 - minCol1 + 1) / (maxRow1 - minRow1 + 1);

                float4x4 viewProj1 = math.mul(
                    LabelPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    LabelPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(LabelScreenProjection.TryProjectPoint(
                        anchorRender, frame1.SceneOriginRender, viewProj1, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen1, out _),
                    "anchor must project in front of the camera at pose 1.");

                // (a) POSITION: the glyph must shift by the anchor's own projected screen delta — the SAME
                //     analytic reasoning as WorldLabelMotionTests (OffsetPx is a fixed additive clip-space
                //     term that cancels exactly in a delta).
                float2 expectedDeltaScreen = anchorScreen1 - anchorScreen0;
                Assert.Greater(math.abs(expectedDeltaScreen.x) + math.abs(expectedDeltaScreen.y), 50f,
                    "the pan must produce a nontrivial expected screen shift, or this tooth is vacuous.");

                float actualDeltaCol = centroidCol1 - centroidCol0;
                float actualDeltaRowAsScreenY = centroidRow0 - centroidRow1; // row is top-origin, screen Y is bottom-origin
                Assert.That(actualDeltaCol, Is.EqualTo(expectedDeltaScreen.x).Within(20f),
                    $"X: the curved glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.x:F1}px, got {actualDeltaCol:F1}px) — if it stays near 0 instead, the world path is not tracking its anchor.");
                Assert.That(actualDeltaRowAsScreenY, Is.EqualTo(expectedDeltaScreen.y).Within(20f),
                    $"Y: the curved glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.y:F1}px, got {actualDeltaRowAsScreenY:F1}px).");

                // (b) ORIENTATION: a 90° heading change against a diagonal world tangent must swap the
                //     rendered glyph's on-screen aspect ratio measurably — the shallow-impl failure mode
                //     (a baked screen rotation, or no rotation at all) leaves this ratio UNCHANGED, since a
                //     baked/absent rotation is oblivious to the live camera bearing.
                float aspectRatioChange = math.abs(aspect1 - aspect0) / math.max(aspect0, 1e-3f);
                Assert.Greater(aspectRatioChange, 0.25f,
                    $"the curved glyph must visibly RE-ORIENT after the heading rotation (aspect ratio {aspect0:F2} -> {aspect1:F2}, " +
                    $"change {aspectRatioChange:P0}) — a shallow implementation that bakes the screen rotation at decision time " +
                    "(or applies none) would leave this ratio unchanged regardless of the live camera bearing.");

                atlasTexture.Dispose();
            }
            finally
            {
                if (presenterGo != null) Object.DestroyImmediate(presenterGo);
                worldMesh.DestroySafely();
                worldMaterial.DestroySafely();
                Object.DestroyImmediate(camGo);
            }
        }

        private static byte[] RenderAndReadback(Camera uCam, string pngName)
        {
            using var snap = new SnapshotRenderer(Size, Size);
            snap.Render(uCam);
            byte[] pixels = (byte[])snap.RawPixels.Clone();
            snap.WritePng(pngName);
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, Size, Size);
            return pixels;
        }

        /// <summary>Places the presenter GameObject at Level-2 (§3.4): the mesh's baked AnchorLocal is
        /// relative to <paramref name="tileOriginRender"/> — recomputed against <paramref name="frame"/> —
        /// so the object's position alone carries the tile placement to its per-frame place. Mirrors exactly
        /// how a real world label renderer re-places a FROZEN mesh every frame; the mesh itself is never
        /// touched here.</summary>
        private static void PlacePresenter(GameObject presenterGo, in double3 tileOriginRender, in SceneFrame frame)
        {
            float3 objectPos = FloatingOrigin.TileToSceneRebased(tileOriginRender, frame.SceneOriginRender, frame.Rebase);
            presenterGo.transform.position = new Vector3(objectPos.x, objectPos.y, objectPos.z);
            presenterGo.transform.rotation = Quaternion.identity; // frame.Rebase is float3x3.identity on Mercator
        }

        /// <summary>Builds a one-glyph world-anchored CURVED <see cref="Mesh"/> via the REAL production
        /// <see cref="BillboardMath.BuildWorldQuad"/> (unlike WorldSymbolAbRenderSnapshotTests'/
        /// WorldLabelMotionTests' point scaffolds, which hand-roll the corner math independently — curved's
        /// rotation-by-tangent has no simpler independent form worth re-deriving here; BuildWorldQuad's own
        /// corner/rotation math is separately pinned by BillboardMathTests). Unrotated corners
        /// (rotationRadians: 0f) + AlignFlags bit1 set — the shader rotates <c>OffsetPx</c> live from
        /// <paramref name="tangentLocal"/>'s projected screen angle (D-E).</summary>
        private static Mesh BuildOneGlyphWorldMeshCurved(in SymbolQuad quad, float textSizePx, float3 colorRgb,
            in float3 anchorLocal, in float3 tangentLocal)
        {
            BillboardMath.BuildWorldQuad(in quad, in anchorLocal, textSizePx, in colorRgb, 0f, in float2.zero,
                in tangentLocal, alignFlags: 2f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr,
                out WorldBillboardVertex br, out WorldBillboardVertex bl);

            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            vertices[0] = tl; vertices[1] = tr; vertices[2] = br; vertices[3] = bl;

            var opacity = new NativeArray<float>(4, Allocator.Temp);
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f;

            var indices = new NativeArray<int>(6, Allocator.Temp);
            indices[0] = 0; indices[1] = 1; indices[2] = 2;
            indices[3] = 0; indices[4] = 2; indices[5] = 3;

            var mesh = new Mesh { name = "WorldCurvedMotion_OneGlyph" };
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
    }
}
