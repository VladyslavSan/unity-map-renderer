// Unity EditMode only — needs a real Camera/Material + the production Tick path (via TickSymbols). NOT registered
// in core-tests.csproj (the Core distance math is pinned engine-free in SymbolFarPlaneCullTests).
//
// The pre-projection far-distance cull, WIRED through the gather loop: a symbol farther from the camera than
// SymbolMaxDistanceFraction × Camera.farClipPlane is hard-skipped in GatherSymbolPoints (never projected/staged/
// collided) and attributed to the distance bucket (LastDistanceCulledCount). These teeth pin the CONFIGURABLE
// knob — the SAME symbol crosses the cull boundary when the fraction changes, with the far plane held fixed.
//
// The harness injects a FIXED far-plane policy (the cull reads MapCamera.CurrentFarMetres, derived from that
// policy — NOT the raw Camera.farClipPlane, which is Unity's default until a SyncToCamera runs), so the far
// distance each assertion asserts against is one the test itself established. CameraRelativePosition is the
// default (0,0,0), so the camera→anchor distance is just the anchor's offset from the scene origin.

using System.Collections.Generic;
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
    [TestFixture]
    public class SymbolFarDistanceCullGatherTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, string text, int feature)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: new SymbolPaint { TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f },
                textSizePx: 24f, paddingPx: 2f, sortKey: 0f, text: text, featureIndex: feature, tileKey: 0L,
                materialIndex: 0);

        // A far-plane policy that returns a FIXED distance regardless of altitude/tilt/FOV — so a test can pin the
        // exact far the cull compares against (MapCamera.CurrentFarMetres reads this) instead of deriving it from
        // the geometry.
        private sealed class FixedFarPlane : IFarPlanePolicy
        {
            private readonly double _far;
            public FixedFarPlane(double far) { _far = far; }
            public double FarMetres(double altitude, Angle tilt, double fovDegVertical, double aspect) => _far;
            public string Name => "fixed";
        }

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly MapCamera Cam;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly IProjection Projection;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("FarDistanceCull_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                Cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = Cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Projection = Cam.Projection;
                // CameraRelativePosition left at (0,0,0): the camera sits at the look-at, so a symbol's distance from
                // the camera equals its render-space offset from the scene origin — the quantity the test controls.
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                System = new SymbolPlacementSystem(Cam, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        /// <summary>THE headline tooth: with the far plane fixed at 10 000 m, a symbol 6 000 m from the camera is
        /// hard-skipped when the cull fraction is 0.5 (cull distance 5 000 m &lt; 6 000 m) — never a collision
        /// candidate, attributed to the distance bucket — but stages normally when the fraction is 0.8 (cull
        /// distance 8 000 m &gt; 6 000 m). Same symbol, same far plane, only the knob moves: pins that
        /// <c>SymbolMaxDistanceFraction</c> scales the cull distance as a fraction of the far plane.</summary>
        [Test]
        public void FarSymbol_CulledOrStaged_TracksTheDistanceFraction()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, "N", 0);                                    // at the look-at ⇒ distance 0
            AddPoint(buffer, h.Origin + new double3(6000.0, 0.0, 0.0), "F", 1);    // 6 000 m from the camera
            h.Cam.FarPlanePolicy = new FixedFarPlane(10000.0); // CurrentFarMetres ≡ 10 000 m

            // Tight fraction: cull distance = 0.5 × 10 000 = 5 000 m ⇒ the 6 000 m symbol is culled.
            h.System.SymbolMaxDistanceFraction = 0.5;
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);

            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "the 6 000 m label is past 0.5 × far (5 000 m) ⇒ culled");
            Assert.AreEqual(1, h.System.LastCandidateCount, "…only the near label stages as a collision candidate");

            // Loose fraction: cull distance = 0.8 × 10 000 = 8 000 m ⇒ the SAME symbol now survives.
            h.System.SymbolMaxDistanceFraction = 0.8;
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);

            Assert.AreEqual(0, h.System.LastDistanceCulledCount, "the 6 000 m label is within 0.8 × far (8 000 m) ⇒ kept");
            Assert.AreEqual(2, h.System.LastCandidateCount, "…both labels stage as collision candidates");
        }

        /// <summary>The far plane is the second lever: with the fraction held at 1.0, the same 6 000 m symbol is
        /// culled when the far plane is 5 000 m and kept when it is 10 000 m. Guards against a regression that read
        /// a constant distance instead of the live <c>Camera.farClipPlane</c>.</summary>
        [Test]
        public void FarSymbol_CulledOrStaged_TracksTheFarPlane()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, "N", 0);
            AddPoint(buffer, h.Origin + new double3(6000.0, 0.0, 0.0), "F", 1);
            h.System.SymbolMaxDistanceFraction = 1.0;

            h.Cam.FarPlanePolicy = new FixedFarPlane(5000.0);
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);
            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "6 000 m > far 5 000 m ⇒ culled");

            h.Cam.FarPlanePolicy = new FixedFarPlane(10000.0);
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);
            Assert.AreEqual(0, h.System.LastDistanceCulledCount, "6 000 m < far 10 000 m ⇒ kept");
        }
    }
}
