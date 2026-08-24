// Unity EditMode only — needs a real Camera/Material + the production SymbolGatherPlan Tick path (via
// TickSymbols). NOT registered in core-tests.csproj.
//
// The pre-projection ZOOM gate: a symbol whose style layer is out of the live camera zoom's [minzoom, maxzoom)
// is hard-skipped in GatherSymbolPoints — never projected/staged/collided — instead of being projected, staged,
// collided and only THEN marked Suppressed (the pre-change cost these teeth pin removing). The gate is a peer of
// the tile/distance/departing/horizon fade triggers and shares their fade-out-in-place exemption: a record still
// fading stays staged (ApplySuppression owns its same-frame hide), only fade-DEAD out-of-zoom records are
// skipped. See SymbolPlacementSystem.GatherSymbolPoints / IsOutOfLiveZoom / ApplySuppression.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolZoomGateGatherTests
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

        // MaterialIndex 0 ⇒ slot 0 ⇒ symbolLayers[0]. The anchor sits AT the look-at so no distance/horizon cull
        // preempts the zoom trigger — the zoom gate is the only thing that can skip it.
        private static SymbolTileBuffer Point(double3 anchor, string text, int feature)
        {
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: new SymbolPaint { TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f },
                textSizePx: 24f, paddingPx: 2f, sortKey: 0f, text: text, featureIndex: feature, tileKey: 0L,
                materialIndex: 0);
            return buffer;
        }

        // A single symbol layer (slot 0). minzoom null ⇒ visible at any zoom (the in-zoom control); a minzoom
        // above the Harness's live zoom (5.0) ⇒ out of zoom ⇒ the gate fires.
        private static SymbolRenderLayer BuildRenderLayer(double? minZoom)
        {
            string minZoomJson = minZoom.HasValue ? $@", ""minzoom"": {minZoom.Value}" : "";
            string styleJson = $@"{{
                ""version"": 8,
                ""layers"": [
                    {{ ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l""{minZoomJson},
                      ""layout"": {{ ""text-field"": ""{{NAME}}"" }} }}
                ]
            }}";
            var settings = MapMaterialSetTestUtil.Load();
            return SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(styleJson).Layers[0], settings, initialZoom: 5.0, drawIndex: 0);
        }

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly IProjection Projection;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("ZoomGate_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                // Live camera zoom 5.0 — a layer's minzoom > 5 is out of zoom, minzoom null/≤5 is in zoom.
                var cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Projection = cam.Projection;
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                System = new SymbolPlacementSystem(cam, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        /// <summary>THE headline tooth (RED against the pre-gate code): a FRESH symbol whose layer is out of the
        /// live zoom is hard-skipped in gather — never a collision candidate — and attributed to the zoom bucket.
        /// Before the gate, it would have been staged as a Suppressed candidate (<c>LastCandidateCount == 1</c>,
        /// <c>LastZoomCulledCount == 0</c>): the work this change removes.</summary>
        [Test]
        public void FreshOutOfZoomSymbol_HardSkippedInGather_AbsentFromCollision()
        {
            using var h = new Harness();
            var outOfZoom = BuildRenderLayer(minZoom: 10.0); // 10 > live 5.0 ⇒ out of zoom
            try
            {
                var buffer = Point(h.Origin, "A", 0);
                var layers = new List<SymbolRenderLayer> { outOfZoom };

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: layers); // fresh — no live fade

                Assert.AreEqual(0, h.System.LastCandidateCount, "an out-of-zoom label never enters the collision pass");
                Assert.AreEqual(1, h.System.LastZoomCulledCount, "…it is attributed to the pre-projection zoom cull");
                Assert.AreEqual(0, h.System.LastDistanceCulledCount, "…and not mis-attributed to the B-3 distance cull");
                Assert.AreEqual(0, h.System.LastQuadCount, "…and draws nothing (output unchanged — it drew nothing before either)");
            }
            finally { outOfZoom.Dispose(); }
        }

        /// <summary>Control: the identical symbol under an IN-zoom layer is NOT zoom-culled — it stages as a
        /// candidate and the zoom bucket stays empty. Pins that the gate fires on the zoom predicate, not on
        /// merely having a layer list (which would silently cull everything).</summary>
        [Test]
        public void InZoomSymbol_NotZoomCulled_StagesAsCandidate()
        {
            using var h = new Harness();
            var inZoom = BuildRenderLayer(minZoom: null); // unbounded ⇒ visible at zoom 5.0
            try
            {
                var buffer = Point(h.Origin, "A", 0);
                var layers = new List<SymbolRenderLayer> { inZoom };

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: layers);

                Assert.AreEqual(1, h.System.LastCandidateCount, "an in-zoom label stages as a collision candidate");
                Assert.AreEqual(0, h.System.LastZoomCulledCount, "…and nothing is zoom-culled");
            }
            finally { inZoom.Dispose(); }
        }

        /// <summary>With NO layer list (the demo / single-material path) the zoom gate is inert — every symbol
        /// stages, nothing is zoom-culled. Pins <c>IsOutOfLiveZoom</c>'s <c>slotCount == 0</c> early return, the
        /// mirror of <c>ApplySuppression</c>'s own "no layer list → no zoom ranges" guard.</summary>
        [Test]
        public void NoLayerList_ZoomGateInert_EverythingStages()
        {
            using var h = new Harness();
            var buffer = Point(h.Origin, "A", 0);

            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection); // symbolLayers: null

            Assert.AreEqual(1, h.System.LastCandidateCount, "no layer list ⇒ no zoom gate ⇒ the label stages");
            Assert.AreEqual(0, h.System.LastZoomCulledCount, "…and nothing is zoom-culled");
        }

        /// <summary>The fade-out-in-place exemption (guards the fall-through): a symbol shown while IN zoom, whose
        /// layer then leaves the zoom range, must NOT be hard-skipped while its fade is still alive — it stays a
        /// staged candidate so <c>ApplySuppression</c> hides it the SAME frame (no pop), and it is NOT counted in
        /// the zoom bucket (only fade-DEAD records are). Goes RED if the gate ever hard-skips out-of-zoom records
        /// unconditionally instead of routing live-fade ones through <c>MarkFadeOutIfAlive</c>.</summary>
        [Test]
        public void PreviouslyShownSymbol_GoneOutOfZoom_StaysStagedFading_NotHardSkipped()
        {
            using var h = new Harness();
            var inZoom = BuildRenderLayer(minZoom: null);
            var outOfZoom = BuildRenderLayer(minZoom: 10.0);
            try
            {
                var buffer = Point(h.Origin, "A", 0);
                var inLayers = new List<SymbolRenderLayer> { inZoom };
                var outLayers = new List<SymbolRenderLayer> { outOfZoom };

                // Two in-zoom ticks: it stages, wins its harvested verdict, and snaps to full opacity (a LIVE fade).
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: inLayers);
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: inLayers);
                Assert.AreEqual(1, h.System.LastQuadCount, "precondition: the label is placed and drawing while in zoom");

                // Now its layer leaves the live zoom. The record has a live fade ⇒ it must stay staged (fall
                // through), be Suppressed, and hide THIS frame — not be hard-culled (which would pop it).
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: outLayers);

                Assert.AreEqual(1, h.System.LastCandidateCount, "a still-fading out-of-zoom label stays staged (not hard-skipped)");
                Assert.AreEqual(0, h.System.LastZoomCulledCount, "…so it is NOT counted in the zoom-cull bucket (only fade-dead records are)");
                Assert.AreEqual(0, h.System.LastQuadCount, "…and ApplySuppression hides it the same frame (no lingering draw)");
            }
            finally { inZoom.Dispose(); outOfZoom.Dispose(); }
        }
    }
}
