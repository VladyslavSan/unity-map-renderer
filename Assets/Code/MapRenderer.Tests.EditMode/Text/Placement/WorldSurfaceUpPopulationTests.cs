// Unity EditMode only — real Camera/Mesh readback via WorldMeshReadback. NOT registered in core-tests.csproj.
//
// P2 T-3: the byte-identical invariant (§1 of the P2 plan) means the RENDER cannot tell you whether `Up` is
// populated correctly — a zeroed, a hardcoded +Y, and a correct per-projection `Up` would all render
// IDENTICALLY (the vertex attribute is written and unread by every shader). So this file inspects DATA: it
// drives a real SymbolPlacementSystem.Tick to a real world mesh (WorldMeshReadback.Read) and asserts the
// vertex `Up` against a CLOSED FORM written out independently in each test — never by calling the production
// sampler (IProjection.ProjectPoint / PolylineArcMath.SampleUp) to produce the expectation.

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using System.Collections.Generic;
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
    [TestFixture]
    public class WorldSurfaceUpPopulationTests
    {
        // A deliberately non-equator, non-prime-meridian anchor (sign-constants-render-at-a-discriminating-value):
        // all three closed-form components (cosφcosλ, sinφ, cosφsinλ) are distinct and nonzero here.
        private static readonly GeoCoordinate Anchor = new GeoCoordinate { Latitude = 47.0, Longitude = 8.0 };

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph
            {
                Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16],
            };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static SymbolTileBuffer MakePointSymbol(double3 anchorRender, double3 upRender)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, float2.zero, new float2(18f, 18f),
                up: upRender, paint: SymbolPaint.Default, textSizePx: 24f, sortKey: 0f, featureIndex: 0, tileKey: 0L);
            return buffer;
        }

        // Runs one real Tick of `buffer` under `projection` and returns the world mesh's stream-0 vertices.
        private static WorldBillboardVertex[] RunPointTick(IProjection projection, SymbolTileBuffer buffer)
        {
            var camGo = new GameObject("SurfaceUpPopulation_TestCamera");
            try
            {
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude, Altitude = 0.0 },
                    zoom: 6.0, heading: 0.0, tilt: 0.0), projection: projection);
                var frame = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(Anchor),
                    Rebase = float3x3.identity,
                };
                var atlasTexture = BuildTinyAtlasTexture();
                var system = new SymbolPlacementSystem(mapCamera,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                try
                {
                    // R3: collision verdicts apply one Tick late — duplicate before reading placement.
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    Assert.AreEqual(1, system.LastQuadCount, "precondition: the label must place (not cull).");
                    Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh), "the world slot mesh must exist.");
                    WorldMeshReadback.Read(mesh, out WorldBillboardVertex[] v, out _);
                    return v;
                }
                finally
                {
                    system.Dispose();
                    atlasTexture.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        // ── (a) Spherical, point symbol ──────────────────────────────────────────────────────────────────

        [Test]
        public void SphericalPointSymbol_Up_MatchesClosedFormGeodeticNormal_OnEveryCorner()
        {
            var projection = new SphericalProjection();
            double lambda = Anchor.Longitude * math.PI_DBL / 180.0;
            double phi    = Anchor.Latitude  * math.PI_DBL / 180.0;
            double sinPhi = math.sin(phi), cosPhi = math.cos(phi);
            double sinLam = math.sin(lambda), cosLam = math.cos(lambda);
            // Closed form written out here, not derived by calling ProjectPoint: render axes are (X,Z,Y) —
            // SphericalProjection.cs's own documented axis-swap (docs §7) — so Up.y carries sinφ.
            var expectedUp = new float3((float)(cosPhi * cosLam), (float)sinPhi, (float)(cosPhi * sinLam));

            double3 anchorRender = projection.Project(Anchor);
            double3 upRender = projection.ProjectPoint(Anchor).Up; // the value the extractor threads through P2's chain

            WorldBillboardVertex[] v = RunPointTick(projection, MakePointSymbol(anchorRender, upRender));
            Assert.AreEqual(4, v.Length, "one point quad, 4 corners");

            foreach (WorldBillboardVertex vv in v)
            {
                Assert.AreEqual(expectedUp.x, vv.Up.x, 1e-5f, "Up.x (cosφ·cosλ)");
                Assert.AreEqual(expectedUp.y, vv.Up.y, 1e-5f, "Up.y (sinφ) — the axis a swapped narrow would miss");
                Assert.AreEqual(expectedUp.z, vv.Up.z, 1e-5f, "Up.z (cosφ·sinλ)");
                Assert.AreEqual(1.0, math.length(vv.Up), 1e-4, "Up is unit-length");
                Assert.AreNotEqual(new float3(0f, 1f, 0f), vv.Up, "must not be the Mercator-hardcoded +Y");
            }
            // All four corners of the same quad share the anchor's up (kill: a per-corner constant/garbage).
            for (int i = 1; i < v.Length; i++)
                Assert.AreEqual(v[0].Up, v[i].Up, "every corner of a point quad carries the SAME Up");
        }

        // ── (b) Web Mercator, same fixture ──────────────────────────────────────────────────────────────

        [Test]
        public void WebMercatorPointSymbol_Up_IsExactlyPlusY()
        {
            var projection = new WebMercatorProjection();
            double3 anchorRender = projection.Project(Anchor);
            double3 upRender = projection.ProjectPoint(Anchor).Up;

            WorldBillboardVertex[] v = RunPointTick(projection, MakePointSymbol(anchorRender, upRender));
            Assert.AreEqual(4, v.Length);

            foreach (WorldBillboardVertex vv in v)
                Assert.AreEqual(new float3(0f, 1f, 0f), vv.Up, "Web Mercator's Up is the CONSTANT +Y — no per-vertex math to narrow wrong");
        }

        // ── (c) Spherical, curved/along-line symbol ──────────────────────────────────────────────────────

        [Test]
        public void SphericalCurvedSymbol_Up_MatchesClosedFormAtEachGlyphsOwnSampledPosition()
        {
            var projection = new SphericalProjection();
            // A short (2 deg) span centred on the camera look-at, on the same non-equator/non-meridian
            // latitude as the point fixture — short enough that geodesic vs. linear lat/lon interpolation
            // at the (recovered) sample fraction agree well inside the tooth's own 1e-4 tolerance.
            var geoA = new GeoCoordinate { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude - 1.0 };
            var geoB = new GeoCoordinate { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude + 1.0 };
            double3 a = projection.Project(geoA), b = projection.Project(geoB);
            double3 upA = projection.ProjectPoint(geoA).Up, upB = projection.ProjectPoint(geoB).Up;
            var path = new[] { a, b };
            var pathUps = new[] { upA, upB };

            var glyphs = new List<CurvedGlyph>
            {
                MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f),
            };
            LineAnchor centerAnchor = AnchorAtMidpoint(path);

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, new[] { centerAnchor }, path, pathUps,
                placement: SymbolPlacement.LineCenter, paint: SymbolPaint.Default, textSizePx: 24f,
                maxAngleDeg: 45f, keepUpright: true, featureIndex: 0, tileKey: 0L);

            var camGo = new GameObject("SurfaceUpPopulationCurved_TestCamera");
            WorldBillboardVertex[] v;
            try
            {
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude, Altitude = 0.0 },
                    zoom: 6.0, heading: 0.0, tilt: 0.0), projection: projection);
                var frame = new SceneFrame { SceneOriginRender = mapCamera.Projection.Project(Anchor), Rebase = float3x3.identity };
                var atlasTexture = BuildTinyAtlasTexture();
                var system = new SymbolPlacementSystem(mapCamera,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                try
                {
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    Assert.AreEqual(3, system.LastQuadCount, "precondition: 3 glyphs, all placed");
                    Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh), "the world text slot must exist.");
                    WorldMeshReadback.Read(mesh, out v, out _);
                }
                finally
                {
                    system.Dispose();
                    atlasTexture.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }

            Assert.AreEqual(12, v.Length, "3 glyphs x 4 verts");

            // TileKey 0 unpacks to TileId{0,0,0} (SymbolTileKey.Pack's default) — recover
            // the SAME render-space tile origin TestSymbolPlan baked AnchorLocal against, so worldPt below
            // is the glyph's REAL sampled world position, not an approximation.
            double3 tileOriginRender = TileRenderOrigin.Project(new TileId { Z = 0, X = 0, Y = 0 }, projection);

            double3 abDelta = b - a;
            double abLenSq = math.dot(abDelta, abDelta);
            var sampledT = new double[3];
            for (int g = 0; g < 3; g++)
            {
                WorldBillboardVertex vv = v[g * 4]; // one AnchorLocal/Up per glyph, shared by its 4 corners
                double3 worldPt = new double3(vv.AnchorLocal.x, vv.AnchorLocal.y, vv.AnchorLocal.z) + tileOriginRender;
                double t = math.dot(worldPt - a, abDelta) / abLenSq;
                sampledT[g] = t;

                var geoAtT = new GeoCoordinate { Latitude = Anchor.Latitude, Longitude = math.lerp(geoA.Longitude, geoB.Longitude, t) };
                double lambda = geoAtT.Longitude * math.PI_DBL / 180.0;
                double phi    = geoAtT.Latitude  * math.PI_DBL / 180.0;
                var expectedUp = new float3(
                    (float)(math.cos(phi) * math.cos(lambda)), (float)math.sin(phi), (float)(math.cos(phi) * math.sin(lambda)));

                Assert.AreEqual(expectedUp.x, vv.Up.x, 1e-4f, $"glyph {g}: Up.x at its own sampled position");
                Assert.AreEqual(expectedUp.y, vv.Up.y, 1e-4f, $"glyph {g}: Up.y at its own sampled position");
                Assert.AreEqual(expectedUp.z, vv.Up.z, 1e-4f, $"glyph {g}: Up.z at its own sampled position");

                // The residual is bounded by the subdivision policy (~2°) for a genuine curved surface —
                // a swapped axis (Up<->Tangent) would give O(1), not a small residual.
                float dotUpTangent = math.dot(vv.Up, vv.Tangent);
                Assert.Less(math.abs(dotUpTangent), 0.02f, $"glyph {g}: |dot(Up, Tangent)| must stay small");

                for (int c = 1; c < 4; c++)
                    Assert.AreEqual(vv.Up, v[g * 4 + c].Up, $"glyph {g}: every corner shares the glyph's own Up");
            }

            // Not a vacuous per-symbol constant: the three glyphs sample genuinely different, MONOTONIC
            // fractions — never out of order, never a duplicate. NOT hardcoded ascending: SymbolStagingMath.
            // StageCurvedAnchor's text-keep-upright reversal ("a centre tangent pointing leftward reads
            // right-to-left; walk the arc reversed...") flips the WHOLE symbol's walk direction when the
            // anchor's on-screen tangent points leftward — a real, pre-existing (P1, untouched by P2) branch
            // this fixture's camera/anchor geometry happens to trigger, producing DESCENDING t here. Either
            // direction is a correct render; only a MIXED order (a glyph out of step with its neighbours) or
            // a duplicate (all three landing on one point) would be a real defect.
            bool ascending = sampledT[0] < sampledT[1];
            if (ascending)
            {
                Assert.Less(sampledT[0], sampledT[1] - 1e-3, "glyph 0 must sample strictly before glyph 1 (ascending walk)");
                Assert.Less(sampledT[1], sampledT[2] - 1e-3, "glyph 1 must sample strictly before glyph 2 (ascending walk)");
            }
            else
            {
                Assert.Greater(sampledT[0], sampledT[1] + 1e-3, "glyph 0 must sample strictly after glyph 1 (KeepUpright-reversed walk)");
                Assert.Greater(sampledT[1], sampledT[2] + 1e-3, "glyph 1 must sample strictly after glyph 2 (KeepUpright-reversed walk)");
            }
        }

        private static CurvedGlyph MakeCurvedGlyph(float arcCenter) => new CurvedGlyph
        {
            ArcCenter = arcCenter,
            Cell = new SymbolQuad
            {
                TopLeft = new float2(-5f, 8f), BottomRight = new float2(5f, -2f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        // The single-segment (2-point path) case of SymbolPlacementStructureTests' AnchorAt(path, 0.5) —
        // duplicated rather than shared (that helper is `private` on a sibling test class; the "broaden to
        // internal" footprint is for production seams, not a 6-line test-local formula).
        private static LineAnchor AnchorAtMidpoint(double3[] path)
        {
            double total = math.length(path[1] - path[0]);
            return total > 0.0 ? new LineAnchor(0, 0.5f) : new LineAnchor(0, 0f);
        }
    }
}
