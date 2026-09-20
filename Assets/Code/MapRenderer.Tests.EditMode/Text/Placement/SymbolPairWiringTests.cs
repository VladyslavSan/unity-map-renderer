// Unity EditMode only — needs a real Camera/Mesh/GameObject/Texture2D (SymbolSlotPresenter creates one,
// SymbolRenderLayer clones materials). NOT registered in core-tests.csproj.
//
// Road-shields §10 D8/D9 (docs/road-shields-design.md) P10 — the Tick()-level wiring tooth for a centred
// icon+text pair: a Owner(icon)+Rider(text) pair must stage as ONE SymbolCandidate (LastCandidateCount) yet
// still place BOTH halves' quads (LastQuadCount) and build BOTH the world text AND world icon meshes at the
// same (tileKey, slot) — the direct fix for "only the number, no badge". A second test drives the pair
// through a real collision loss and asserts BOTH halves drop together (no bare number).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolPairWiringTests
    {
        private const string StyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""shield"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""layout"": { ""icon-image"": ""shield"" } }
            ]
        }";

        private static MapMaterialSet BuildSettings()
        {
            var settings = ScriptableObject.CreateInstance<MapMaterialSet>();
            settings.SymbolTextWorld = new Material(Shader.Find("Map/Symbol/TextWorld"));
            settings.SymbolIconWorld = new Material(Shader.Find("Map/Symbol/IconWorld"));
            return settings;
        }

        private static Texture2D BuildSpriteTexture()
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(200, 50, 50, 255);
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            return tex;
        }

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // A centred icon+text pair: Owner (icon) immediately followed by its Rider (text) — the adjacency
        // contract §10 D10 relies on (SymbolTileBlockBaker/TestSymbolPlan preserve list order per tile).
        // Stage C: <paramref name="textOptional"/> stamps text-optional on the RIDER, and
        // <paramref name="textTranslatePx"/> pushes the text's box clear of the icon's so a blocker can
        // address one half alone (at a shared anchor the two boxes overlap by construction).
        private static void AddPairSymbols(SymbolTileBuffer buffer, double3 sceneOriginRender,
            bool textOptional = false, float textTranslatePx = 0f)
        {
            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            // Owner (icon) then Rider (text), in that order — the adjacency contract §10 D10 relies on.
            TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, iconQuads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon,
                iconImage: "shield", // a REAL identity — a null one would dedup-collide with any other
                                      // null-identity symbol sharing this anchor (e.g. a hand-built blocker).
                paint: SymbolPaint.Default,
                textSizePx: TextQuadLayout.OneEm,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: 0L,
                pairRole: SymbolPairRole.Owner,
                pairId: 0);

            var textQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 6f), BottomRight = new float2(6f, -6f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, textQuads, new float2(-6f, -6f), new float2(6f, 6f),
                text: "42", // a REAL identity — see the icon's IconImage comment above
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                sortKey: 0f,
                featureIndex: 1,
                tileKey: 0L,
                pairRole: SymbolPairRole.Rider,
                pairId: 0,
                pairOptional: textOptional,
                translatePx: new float2(textTranslatePx, 0f),
                translateAnchor: TextTranslateAnchor.Viewport);
        }

        private static (GameObject camGo, MapCamera mapCamera, SceneFrame frame) BuildScene()
        {
            var camGo = new GameObject("PairWiring_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            return (camGo, mapCamera, frame);
        }

        // ── P10 (§10) ──────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void CentredPair_Tick_DrawsBothTextAndIconMeshes_OneCandidateBothHalves()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();
            var settings = BuildSettings();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                var pairBuffer = new SymbolTileBuffer();
                AddPairSymbols(pairBuffer, frame.SceneOriginRender);

                // R3: duplicate — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(pairBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(pairBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                // §10 D8: TWO symbols (icon+text), ONE candidate — the whole point of the pairing fix (fewer
                // sort/grid/fade operations than the pre-fix two-independent-candidates shape).
                Assert.AreEqual(1, system.LastCandidateCount, "a centred pair must stage as exactly ONE candidate");
                Assert.AreEqual(2, system.LastQuadCount, "both halves' quads must still be placed (icon quad + text quad)");

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Icon, out Mesh worldIconMesh),
                    "the world icon slot mesh must exist");
                Assert.Greater(worldIconMesh.vertexCount, 0, "the icon mesh must have built non-zero vertices");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, SymbolKind.Icon), "the icon presenter must be showing");

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh worldTextMesh),
                    "the world text slot mesh must exist");
                Assert.Greater(worldTextMesh.vertexCount, 0,
                    "the text mesh must have built non-zero vertices — the bare-number bug is a MISSING icon, not a missing text");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, SymbolKind.Text),
                    "the text presenter must be showing — BOTH halves survive together");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(settings.SymbolTextWorld);
                Object.DestroyImmediate(settings.SymbolIconWorld);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── C8 (stage C) at the Tick level: text-optional lets the ICON survive its text's collision loss ────
        //    The two runs differ ONLY by the property, so anything else that could explain a missing text mesh
        //    (the blocker, the translate, the tile split) is held constant.
        [Test]
        public void TextOptional_Tick_TextLosesCollision_IconMeshStillBuilds_TextMeshDoesNot(
            [Values(false, true)] bool textOptional)
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();
            var settings = BuildSettings();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                const float TextOffsetPx = 200f;
                var mixedBuffer = new SymbolTileBuffer();
                AddPairSymbols(mixedBuffer, frame.SceneOriginRender, textOptional, TextOffsetPx);

                // A higher-priority blocker sitting on the TEXT half's translated box and nowhere near the
                // icon's (±40 px around +200, vs. the icon's ±8 around 0). It lives on its OWN tile key so its
                // (Kind=Text) quads land in a different world slot than the pair's text half — otherwise a
                // non-empty text mesh could not be attributed.
                long blockerTileKey = SymbolTileKey.Pack(new TileId { Z = 1, X = 1, Y = 0 });
                var blockerQuads = new List<SymbolQuad>
                {
                    new SymbolQuad
                    {
                        TopLeft = new float2(-40f, 40f), BottomRight = new float2(40f, -40f),
                        UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                    },
                };
                TestSymbolTileBuffer.AddPoint(mixedBuffer, frame.SceneOriginRender, blockerQuads, new float2(-40f, -40f), new float2(40f, 40f),
                    text: "blocker",
                    paint: SymbolPaint.Default,
                    textSizePx: 24f,
                    sortKey: -1f,
                    featureIndex: 99,
                    tileKey: blockerTileKey,
                    translatePx: new float2(TextOffsetPx, 0f),
                    translateAnchor: TextTranslateAnchor.Viewport);

                // Two Ticks: the first schedules the collision, the second harvests its verdict (R3) — and,
                // for the optional case, seeds the per-half drop mask the emit loop reads.
                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                Assert.AreEqual(2, system.LastCandidateCount, "precondition: the blocker + the pair (one candidate each)");

                bool iconShows = system.IsWorldSlotVisible(0L, 0, SymbolKind.Icon);
                bool textShows = system.IsWorldSlotVisible(0L, 0, SymbolKind.Text);
                if (textOptional)
                {
                    Assert.AreEqual(2, system.LastQuadCount,
                        "text-optional: the blocker's quad AND the pair's ICON quad place — the text half alone drops");
                    Assert.IsTrue(iconShows, "the icon must survive its text half losing collision");
                    Assert.IsFalse(textShows, "the pair's text half lost collision, so its mesh must stay empty");
                }
                else
                {
                    Assert.AreEqual(1, system.LastQuadCount,
                        "without text-optional only the blocker places — the pair drops all-or-nothing (§10 P3)");
                    Assert.IsFalse(iconShows, "un-optional: the icon drops WITH its text");
                    Assert.IsFalse(textShows, "un-optional: the text drops too");
                }
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(settings.SymbolTextWorld);
                Object.DestroyImmediate(settings.SymbolIconWorld);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── P3/P4 at the Tick level: a real collision loss drops BOTH halves, not just the icon ──────────────
        [Test]
        public void CentredPair_Tick_BlockedByHigherPrioritySymbol_BothHalvesDropTogether_NoBareNumber()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();
            var settings = BuildSettings();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                // A higher-priority (lower SortKey) blocker at the SAME anchor, big enough to overlap the icon's box.
                var blockerQuads = new List<SymbolQuad>
                {
                    new SymbolQuad
                    {
                        TopLeft = new float2(-40f, 40f), BottomRight = new float2(40f, -40f),
                        UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                    },
                };
                var mixedBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddPoint(mixedBuffer, frame.SceneOriginRender, blockerQuads, new float2(-40f, -40f), new float2(40f, 40f),
                    paint: SymbolPaint.Default,
                    textSizePx: 24f,
                    sortKey: -1f,
                    featureIndex: 99,
                    tileKey: 0L);
                AddPairSymbols(mixedBuffer, frame.SceneOriginRender);

                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                Assert.AreEqual(2, system.LastCandidateCount, "the blocker + the pair (one candidate each)");
                // The blocker is itself Kind=Text (default), sharing the SAME (tileKey, slot, Text) mesh as the
                // pair's text half, so IsWorldSlotVisible can't distinguish "blocker shows" from "pair's text
                // shows" — LastQuadCount is the falsifiable count: only the blocker's ONE quad may place.
                Assert.AreEqual(1, system.LastQuadCount,
                    "only the blocker's quad places — the pair (icon+text) drops TOGETHER, not just the icon (no bare number)");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(settings.SymbolTextWorld);
                Object.DestroyImmediate(settings.SymbolIconWorld);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
