// Unity EditMode only — needs a real Camera/Mesh/GameObject/Texture2D (LabelSlotPresenter creates one,
// SymbolRenderLayer clones materials). NOT registered in core-tests.csproj.
//
// I5b — the icon render-integration WIRING tooth (headless proves compile + byte-identical text + the
// draw-side bind; the actual on-screen sprite pixels are eyeball-owed, see the I5b plan). Three ticks:
//   1. An icon-bearing batch through LabelPlacementSystem.Tick with a fixture sprite Texture2D + a
//      SymbolRenderLayer whose WorldIconMaterial is a real "Map/Symbol/IconWorld" clone — the icon slot mesh
//      must build non-zero verts, LastQuadCount must include the icon quad, and the icon presenter's BOUND
//      material's _MainTex must be the sprite texture (GetTexture — no framebuffer readback, no GPU
//      snapshot needed for this tooth).
//   2. A TEXT-ONLY batch (no icon labels, no spriteTexture) must leave the icon presenter HIDDEN and the
//      text mesh/material path unaffected — the #1-rule parity check.
//   3. Shader.Find("Map/Symbol/IconWorld") must resolve (the shader actually compiled/imported).

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
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolIconWiringTests
    {
        private const string StyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""poi"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""layout"": { ""icon-image"": ""marker"" } }
            ]
        }";

        // A throwaway MapMaterialSet built directly from the real committed shaders (Shader.Find) — never
        // loads/mutates the production Assets/Settings/Map/MapMaterialSet.asset, mirrors how every other
        // Symbol test builds its material(s) (`new Material(Shader.Find("Map/Symbol/TextWorld"))`).
        private static MapMaterialSet BuildSettings()
        {
            var settings = ScriptableObject.CreateInstance<MapMaterialSet>();
            // Epic A / A1 (Codex #2): SymbolTextWorld is REQUIRED — points/icons draw through the world
            // path, so both world bases must be assigned for this wiring tooth to observe real content.
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
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static LabelInstance MakeIconLabel(double3 sceneOriginRender)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            var layout = new TextLayoutResult
            {
                Quads = quads, BoundsMin = new float2(-8f, -8f), BoundsMax = new float2(8f, 8f), LineCount = 1,
            };
            return new LabelInstance
            {
                AnchorRender = sceneOriginRender,
                Layout = layout,
                Kind = LabelKind.Icon, // I3: routes this label onto the icon draw path (AtlasKind, I5b)
                Paint = LabelPaint.Default,
                TextSizePx = TextQuadLayout.OneEm, // scale 1 — mirrors StyledSymbolTileBuilder's real icon path
                SortKey = 0f,
                FeatureIndex = 0,
                TileKey = 0L,
            };
        }

        private static LabelInstance MakeTextLabel(double3 sceneOriginRender)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            var layout = new TextLayoutResult { Quads = quads, BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1 };
            return new LabelInstance
            {
                AnchorRender = sceneOriginRender,
                Layout = layout,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                SortKey = 0f,
                FeatureIndex = 0,
                TileKey = 0L,
            };
        }

        private static (GameObject camGo, MapCamera mapCamera, SceneFrame frame) BuildScene()
        {
            var camGo = new GameObject("IconWiring_TestCamera");
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

        // Epic A / A1 (D5): the world-anchored icon shader pair must compile/import.
        [Test]
        public void ShaderMapSymbolIconWorld_IsFound()
        {
            Assert.IsNotNull(Shader.Find("Map/Symbol/IconWorld"), "the SymbolIconWorld.shader must compile/import under \"Map/Symbol/IconWorld\".");
        }

        [Test]
        public void IconBatch_BuildsIconMesh_AndBindsSpriteTexture_TextBatchLeavesIconPresenterHidden()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();
            var settings = BuildSettings();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            // One plan across both halves: rebuilding it advances WinnerSetVersion, which is what makes the
            // second (text-only) tick refill the mirror instead of hitting the gather memo.
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                // ── 1. Icon-bearing batch ──────────────────────────────────────────────────────────────
                // Epic A / A1: icons draw through the WORLD path now — the icon quad no longer lands on
                // system.IconMesh/renderLayer.IconPresenterVisible (the screen slot/presenter), so this
                // reads the world surface instead (TryGetWorldSlotMesh/IsWorldSlotVisible).
                var iconLabels = new List<LabelInstance> { MakeIconLabel(frame.SceneOriginRender) };
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(iconLabels), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(iconLabels), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                Assert.AreEqual(1, system.LastQuadCount, "LastQuadCount must include the icon quad (no text labels this Tick).");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Icon, out Mesh worldIconMesh),
                    "the world icon slot mesh must exist (created lazily on the icon's first Emit).");
                Assert.Greater(worldIconMesh.vertexCount, 0, "the world icon slot mesh must have built non-zero vertices.");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, LabelKind.Icon), "the world icon presenter must be showing after an icon Tick.");

                Assert.IsNotNull(renderLayer.WorldIconMaterial, "settings.SymbolIconWorld was assigned — WorldIconMaterial must be a clone, not null.");
                Texture boundTexture = renderLayer.WorldIconMaterial.GetTexture("_MainTex");
                Assert.AreSame(spriteTexture, boundTexture,
                    "the world icon presenter's bound material's _MainTex must be the SAME sprite texture instance passed to Tick.");

                // ── 2. Text-only batch (parity: the #1 rule) — world icon slot must go back to HIDDEN, text unaffected ──
                var textLabels = new List<LabelInstance> { MakeTextLabel(frame.SceneOriginRender) };
                system.Tick(in frame, plan.Build(textLabels), atlasTexture, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(textLabels), atlasTexture, deltaTime: float.PositiveInfinity, symbolLayers: layers);

                Assert.AreEqual(1, system.LastQuadCount, "the text-only Tick must place its one glyph quad (precondition).");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, LabelKind.Text), "the world text presenter must still show on a text-only Tick.");
                Assert.IsFalse(system.IsWorldSlotVisible(0L, 0, LabelKind.Icon),
                    "the world icon presenter must be HIDDEN on a text-only Tick (spriteTexture omitted, no icon quads) — " +
                    "the #1 rule: no sprite loaded ⇒ nothing icon-related builds/binds/presents.");
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
