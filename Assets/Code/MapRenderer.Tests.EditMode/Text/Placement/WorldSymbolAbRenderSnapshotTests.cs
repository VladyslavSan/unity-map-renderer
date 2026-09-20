// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
// NOT registered in core-tests.csproj. Modeled on SymbolAtlasOrientationSnapshotTests (same fixture glyph,
// same headless-readback caveats) but does NOT touch that file — this is the A0 GPU A/B equivalence tooth,
// short-named T2 by the tests that model against it, rendering the SAME real glyph through BOTH the OLD
// SymbolPlacementSystem.Tick, exactly like the orientation test) and the NEW world-anchored path (a one-off
// WorldBillboardMeshBuilder mesh presented through Map/Symbol/TextWorld).
//
// Y RECONCILIATION (advisor #1 / A0's make-or-break — resolved EMPIRICALLY, see WorldSymbolInkAnalysis):
// the headless camera→RenderTexture readback is vertically mirrored vs on-screen for EVERY path (Unity's
// well-known render-to-texture flip is a property of the RT-readback route, not of any one shader's
// math — see SymbolAtlasOrientationSnapshotTests' header). The OLD path's shader additionally bakes an
// on-screen calibration flip (`ndc.y = -ndc.y`) that only cancels correctly for the on-screen route; the
// NEW path's stock `TransformObjectToHClip` carries no such calibration. Both raw readbacks are therefore
// put through the SAME `FlipRowsVertically` un-mirror before comparison (mirrors the orientation test's own
// treatment) and BOTH must land upright + centered; if the NEW path lands mirrored relative to the OLD
// path after that un-mirror, the corner-emit convention below (Offset.y / UV.y sign) is the fix point —
// see the RESOLVED CONVENTION note on <see cref="BuildOneGlyphWorldMesh"/>.

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

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldSymbolAbRenderSnapshotTests
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
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);
            Assert.AreEqual(1, quads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = quads[0];

            var camGo = new GameObject("WorldSymbolAb_TestCamera");
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

            // Epic A / A1: point text now draws through the world path — a realistic containing tile keeps
            // the AnchorLocal bake float32-safe (Risk R1; TileKey=0 is ~2e7m away, see
            // SymbolAtlasOrientationSnapshotTests' identical note).
            long tileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default, textSizePx: textSizePx, sortKey: 0f, featureIndex: 0, tileKey: tileKey);

            byte[] oldPixels;
            byte[] newPixels;

            // ── OLD path: a real SymbolPlacementSystem.Tick — since A1, this Tick produces the WORLD path's
            // output for a point symbol (the design's "Open items" note: this arm is repointed to
            // real-Tick-vs-scaffold, no longer a literal screen-space "old"). Needs its own world base
            // material (D7). ──────────────────────────────────────────────────────────────────────────
            using (var system = new SymbolPlacementSystem(mapCamera,
                       worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld"))))
            using (var snapOld = new SnapshotRenderer(Size, Size))
            using (var plan = new TestSymbolPlan(mapCamera.Projection))
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the OLD path's label must not be culled.");

                snapOld.Render(uCam);
                oldPixels = (byte[])snapOld.RawPixels.Clone();
                snapOld.WritePng("world-symbol-ab-old.png");
            }

            // ── NEW path: a one-off world-anchored mesh built straight from the SAME layout/anchor ─────
            GameObject presenterGo = null;
            Mesh worldMesh = null;
            Material worldMaterial = null;
            try
            {
                float4 textColor = SymbolPaint.Default.TextColor;
                worldMesh = BuildOneGlyphWorldMesh(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z));

                worldMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                presenterGo = new GameObject("WorldSymbolAb_Presenter");
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                // Level-2 placement (§3.4): AnchorLocal is baked relative to the anchor itself (tileOrigin
                // == anchorRender, so AnchorLocal == 0) — a one-symbol test mesh has no real tile to bake
                // against, so the anchor doubles as its own bake origin. The object's position/rotation
                // carry the rest, identically to how a real tile mesh is placed.
                float3 objectPos = FloatingOrigin.TileToSceneRebased(anchorRender, frame.SceneOriginRender, frame.Rebase);
                presenterGo.transform.position = new Vector3(objectPos.x, objectPos.y, objectPos.z);
                presenterGo.transform.rotation = Quaternion.identity; // frame.Rebase is float3x3.identity on Mercator

                using (var snapNew = new SnapshotRenderer(Size, Size))
                {
                    snapNew.Render(uCam);
                    newPixels = (byte[])snapNew.RawPixels.Clone();
                    snapNew.WritePng("world-symbol-ab-new.png");
                }
            }
            finally
            {
                if (presenterGo != null) UnityEngine.Object.DestroyImmediate(presenterGo);
                worldMesh.DestroySafely();
                worldMaterial.DestroySafely();
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

            atlasTexture.Dispose();
            UnityEngine.Object.DestroyImmediate(camGo);
        }

        /// <summary>
        /// Builds a one-glyph world-anchored <see cref="Mesh"/> straight from a real <see cref="SymbolQuad"/> —
        /// the A0 test-scaffold analogue of <see cref="BillboardMath.BuildQuad"/> (A1 owns the real per-tile
        /// emit; this helper exists only so A0's teeth can exercise <see cref="WorldBillboardMeshBuilder"/>
        /// against a genuine glyph). AnchorLocal is <see cref="float3.zero"/> for every corner (the mesh's
        /// object-space origin IS the anchor — see the call site's placement comment); only the corner
        /// <c>Offset</c> varies per vertex, mirroring <c>BillboardMath.BuildQuad</c>'s unrotated
        /// anchor-relative corners exactly (same TL/TR/BR/BL UV mapping, no flip). <b>RESOLVED Y CONVENTION
        /// (empirical, see this file's header):</b> a direct, unflipped carry-over of the OLD path's
        /// Offset sign rendered the glyph UPSIDE DOWN after the shared un-mirror (confirmed by a failing
        /// run of the A/B tooth below: top-third-wider instead of bottom-third-wider) — the OLD path's
        /// SymbolPassVertex bakes an extra <c>ndc.y = -ndc.y</c> on-screen calibration flip that the NEW
        /// path's stock MVP has no equivalent of (SymbolTextWorld_ForwardPass.hlsl's header). The resolved
        /// fix point, per the A0 plan, is the emit: <c>Offset.y</c> is negated here (UV stays attached to
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
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f; // A0: constant, no fade yet

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
}
