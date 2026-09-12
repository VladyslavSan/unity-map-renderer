// Unity EditMode — a real UnityEngine.Camera supplies the view-projection matrix (mirrors
// SymbolScreenProjectionUnityTests' pattern), but this test does NOT render anything (no GPU readback) —
// it is a pure algebra check, fast within the batch run. NOT registered in core-tests.csproj (needs
// UnityEngine.Camera for projectionMatrix/worldToCameraMatrix).
//
// A0 T1: pins the "two-term-RTC-vs-single-narrow" seam — the world-anchored path composes a screen
// position via TWO float32-narrowed terms (mesh-baked AnchorLocal = anchorRender-tileOriginRender, plus the
// per-frame tile transform tileOriginRender-sceneOriginRender), while the OLD path
// (SymbolScreenProjection.TryProjectPoint) narrows
// renderPos-sceneOriginRender in ONE step. FloatingOrigin's tileOrigin term cancels analytically (its own
// doc comment), so the two compositions should agree on the projected screen position within a tight px
// bound — this test proves that empirically across zooms, all 4 glyph corners, and several tile origins
// (including one that does NOT contain the anchor — the RTC cancellation must not depend on tile
// containment), for north-up Mercator (identity rebase; §11 A0 explicitly scopes tilt/globe out).

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldBillboardRtcAlgebraTests
    {
        private const int ViewportSize = 1024;

        // Glyph-corner offsets (logical px, y-up — mirrors BillboardMath's anchor-relative corner
        // convention): TL/TR/BR/BL of a nominal 40x20px glyph box around the anchor.
        private static readonly float2[] GlyphCornerOffsets =
        {
            new float2(-20f,  10f), // top-left
            new float2( 20f,  10f), // top-right
            new float2( 20f, -10f), // bottom-right
            new float2(-20f, -10f), // bottom-left
        };

        // A few look-at locations, including one far from Mercator's absolute (0,0) origin (near the
        // antimeridian) — the RTC cancellation must hold regardless of the ABSOLUTE Mercator magnitude,
        // per FloatingOrigin's doc (precision is governed by camera-to-sceneOrigin distance, not by how
        // far sceneOrigin sits from Mercator's own origin).
        private static readonly GeoCoordinate[] LookAts =
        {
            new GeoCoordinate { Latitude = 30.0,  Longitude = 30.0 },
            new GeoCoordinate { Latitude = 0.0,   Longitude = 0.0 },
            new GeoCoordinate { Latitude = -45.0, Longitude = -60.0 },
            new GeoCoordinate { Latitude = 60.0,  Longitude = 170.0 },
        };

        private static readonly int[] TileZooms = { 2, 8, 14, 20 };

        // Epic A / A1 (Codex minor — z0/z1 note, design §11 A1 D2): a z0/z1 tile spans a quarter-to-whole
        // Earth, so its AnchorLocal magnitude approaches the "wrong tile" regime — a RELAXED bound (not the
        // 0.5px used for z2-20 above) pins that the on-screen error stays BOUNDED (sub-pixel) rather than
        // diverging, the accepted low-zoom known-limit.
        private static readonly int[] LowZoomTileZooms = { 0, 1 };
        private const float LowZoomBoundPx = 2.0f;

        [Test]
        public void WorldPathCornerScreenPosition_MatchesOldPathAcrossZoomsCornersAndTileOrigins()
        {
            var camGo = new GameObject("WorldBillboardRtcAlgebra_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            try
            {
                uCam.targetTexture = new RenderTexture(ViewportSize, ViewportSize, 0);
                var projection = new WebMercatorProjection();

                foreach (GeoCoordinate lookAt in LookAts)
                {
                    var mapCamera = new MapCamera(uCam, new CameraProperties(
                        new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                        zoom: 10.0, heading: 0.0, tilt: 0.0), projection: projection);

                    double3 sceneOriginRender = mapCamera.Projection.Project(lookAt);
                    var frame = new SceneFrame { SceneOriginRender = sceneOriginRender, Rebase = float3x3.identity };

                    double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                    float4x4 viewProj = math.mul(
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));

                    // A small, deterministic offset from the look-at (mirrors
                    // SymbolAtlasOrientationSnapshotTests' pattern) so the anchor is not exactly screen-center.
                    double altitude = uCam.transform.position.y;
                    double3 anchorRender = sceneOriginRender + new double3(altitude * 0.01, 0.0, altitude * 0.02);

                    foreach (int tileZoom in TileZooms)
                    {
                        // Two tile origins per zoom: the tile actually containing the anchor (the realistic
                        // case) and a neighbor tile that does NOT contain it (proves the cancellation doesn't
                        // depend on containment).
                        TileId containing = TileContaining(lookAt, tileZoom);
                        TileId neighbor = new TileId { X = containing.X + 1, Y = containing.Y, Z = tileZoom };

                        foreach (TileId tile in new[] { containing, neighbor })
                        {
                            double3 tileOriginRender = TileRenderOrigin.Project(tile, projection);

                            foreach (float2 offsetPx in GlyphCornerOffsets)
                            {
                                float2 newScreenPx = ProjectWorldPathCorner(
                                    anchorRender, tileOriginRender, sceneOriginRender, viewProj, viewportLogicalPx, offsetPx);

                                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                                        anchorRender, sceneOriginRender, viewProj, viewportLogicalPx, float3x3.identity,
                                        out float2 oldAnchorScreenPx, out _),
                                    $"anchor must project in front of the camera (lookAt {lookAt.Latitude},{lookAt.Longitude}, tileZoom {tileZoom})");
                                float2 oldScreenPx = oldAnchorScreenPx + offsetPx; // OLD path: BillboardMath adds the corner offset directly

                                Assert.That(newScreenPx.x, Is.EqualTo(oldScreenPx.x).Within(0.5f),
                                    $"X mismatch: lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={tile} offset={offsetPx}");
                                Assert.That(newScreenPx.y, Is.EqualTo(oldScreenPx.y).Within(0.5f),
                                    $"Y mismatch: lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={tile} offset={offsetPx}");
                            }
                        }
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void WorldPathCornerScreenPosition_MatchesOldPathAtZ0Z1_WithinRelaxedBound()
        {
            var camGo = new GameObject("WorldBillboardRtcAlgebraLowZoom_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            try
            {
                uCam.targetTexture = new RenderTexture(ViewportSize, ViewportSize, 0);
                var projection = new WebMercatorProjection();

                foreach (GeoCoordinate lookAt in LookAts)
                {
                    var mapCamera = new MapCamera(uCam, new CameraProperties(
                        new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                        zoom: 10.0, heading: 0.0, tilt: 0.0), projection: projection);

                    double3 sceneOriginRender = mapCamera.Projection.Project(lookAt);
                    var frame = new SceneFrame { SceneOriginRender = sceneOriginRender, Rebase = float3x3.identity };

                    double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                    float4x4 viewProj = math.mul(
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));

                    double altitude = uCam.transform.position.y;
                    double3 anchorRender = sceneOriginRender + new double3(altitude * 0.01, 0.0, altitude * 0.02);

                    foreach (int tileZoom in LowZoomTileZooms)
                    {
                        TileId containing = TileContaining(lookAt, tileZoom);
                        double3 tileOriginRender = TileRenderOrigin.Project(containing, projection);

                        foreach (float2 offsetPx in GlyphCornerOffsets)
                        {
                            float2 newScreenPx = ProjectWorldPathCorner(
                                anchorRender, tileOriginRender, sceneOriginRender, viewProj, viewportLogicalPx, offsetPx);

                            Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                                    anchorRender, sceneOriginRender, viewProj, viewportLogicalPx, float3x3.identity,
                                    out float2 oldAnchorScreenPx, out _),
                                $"anchor must project in front of the camera (lookAt {lookAt.Latitude},{lookAt.Longitude}, tileZoom {tileZoom})");
                            float2 oldScreenPx = oldAnchorScreenPx + offsetPx;

                            Assert.That(newScreenPx.x, Is.EqualTo(oldScreenPx.x).Within(LowZoomBoundPx),
                                $"X mismatch (relaxed low-zoom bound): lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={containing} offset={offsetPx}");
                            Assert.That(newScreenPx.y, Is.EqualTo(oldScreenPx.y).Within(LowZoomBoundPx),
                                $"Y mismatch (relaxed low-zoom bound): lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={containing} offset={offsetPx}");
                        }
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>
        /// Reproduces the GPU's world-anchored composition in managed code: Level-1 bake
        /// (<c>AnchorLocal = anchorRender − tileOriginRender</c>, narrowed to float32), Level-2 per-frame
        /// transform (<see cref="FloatingOrigin.TileToSceneRebased"/>), the stock MVP multiply, and the
        /// SAME clip-space offset the vertex shader applies
        /// (<c>clip.xy += offsetPx / _ScreenParamsLogical.xy * 2 * clip.w</c> — <c>SymbolTextWorld_ForwardPass.hlsl</c>).
        /// </summary>
        private static float2 ProjectWorldPathCorner(
            in double3 anchorRender, in double3 tileOriginRender, in double3 sceneOriginRender,
            in float4x4 viewProj, in double2 viewportLogicalPx, in float2 offsetPx)
        {
            // Level 1: mesh-baked, tile-origin-relative (manual per-component narrow — mirrors
            // FloatingOrigin.TileToSceneRebased's own narrowing convention).
            float3 anchorLocal = new float3(
                (float)(anchorRender.x - tileOriginRender.x),
                (float)(anchorRender.y - tileOriginRender.y),
                (float)(anchorRender.z - tileOriginRender.z));

            // Level 2: per-frame tile transform (identity rebase ⇒ Mercator).
            float3 tileScenePos = FloatingOrigin.TileToSceneRebased(tileOriginRender, sceneOriginRender, float3x3.identity);

            // unity_ObjectToWorld * float4(AnchorLocal, 1) with an identity rotation ⇒ AnchorLocal + T.
            float3 worldPos = anchorLocal + tileScenePos;

            float4 clip = math.mul(viewProj, new float4(worldPos, 1f));

            float viewportX = (float)viewportLogicalPx.x;
            float viewportY = (float)viewportLogicalPx.y;

            // The pinned shader line: clip.xy += off / _ScreenParamsLogical.xy * 2.0 * clip.w (_ScreenParamsLogical
            // is set to viewportLogicalPx — see WorldBillboardMeshBuilder/SymbolPlacementSystem's identical convention).
            clip.x += offsetPx.x / viewportX * 2.0f * clip.w;
            clip.y += offsetPx.y / viewportY * 2.0f * clip.w;

            float ndcX = clip.x / clip.w;
            float ndcY = clip.y / clip.w;
            return new float2((ndcX * 0.5f + 0.5f) * viewportX, (ndcY * 0.5f + 0.5f) * viewportY);
        }

        /// <summary>Standard slippy-map lon/lat → tile x/y at <paramref name="zoom"/> (Web Mercator).</summary>
        private static TileId TileContaining(in GeoCoordinate geo, int zoom)
        {
            double n = math.pow(2.0, zoom);
            double latRad = geo.Latitude * math.PI_DBL / 180.0;
            double x = (geo.Longitude + 180.0) / 360.0 * n;
            double y = (1.0 - math.log(math.tan(latRad) + 1.0 / math.cos(latRad)) / math.PI_DBL) / 2.0 * n;
            return new TileId { X = (int)math.floor(x), Y = (int)math.floor(y), Z = zoom };
        }
    }
}
