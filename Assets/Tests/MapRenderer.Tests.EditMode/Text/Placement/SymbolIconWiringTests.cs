// Unity EditMode only — needs a real Camera/Mesh/GameObject/Texture2D (SymbolSlotPresenter creates one,
// SymbolRenderLayer clones materials). NOT registered in core-tests.csproj.
//
// The icon render-integration WIRING tooth (headless proves compile + byte-identical text + the
// draw-side bind; the on-screen sprite pixels stay eyeball-owed). Three ticks:
//   1. An icon-bearing batch through SymbolPlacementSystem.Tick with a fixture sprite Texture2D + a
//      SymbolRenderLayer whose WorldIconMaterial is a real "Map/Symbol/IconWorld" clone — the icon slot mesh
//      must build non-zero verts, LastQuadCount must include the icon quad, and the icon presenter's BOUND
//      material's _MainTex must be the sprite texture (GetTexture — no framebuffer readback, no GPU
//      snapshot needed for this tooth).
//   2. A TEXT-ONLY batch (no icon symbols, no spriteTexture) must leave the icon presenter HIDDEN and the
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
    public class SymbolIconWiringTests : BaseTestFixture
    {
        private const string StyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""poi"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""layout"": { ""icon-image"": ""marker"" } }
            ]
        }";

        // A throwaway MapMaterialSet built directly from the real committed shaders (Shader.Find) — never
        // loads/mutates the committed production MapMaterialSet, mirrors how every other
        // Symbol test builds its material(s) (`new Material(Shader.Find("Map/Symbol/TextWorld"))`).
        private static MapMaterialSet BuildSettings()
        {
            var settings = ScriptableObject.CreateInstance<MapMaterialSet>();
            // SymbolTextWorld is REQUIRED — points/icons draw through the world
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
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static SymbolTileBuffer MakeIcon(double3 sceneOriginRender)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, quads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon, // routes this symbol onto the icon draw path (AtlasKind)
                paint: SymbolPaint.Default,
                textSizePx: TextQuadLayout.OneEm, // scale 1 — mirrors StyledSymbolTileBuilder's real icon path
                sortKey: 0f,
                featureIndex: 0,
                tileKey: 0L);
            return buffer;
        }

        private static SymbolTileBuffer MakeText(double3 sceneOriginRender)
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
            TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, quads, float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: 0L);
            return buffer;
        }

        // ── P-B: a MAP-aligned LINE icon — the one-glyph curved instance StyledSymbolTileBuilder now emits. ──
        private static readonly SymbolQuad AlongLineIconCell = new SymbolQuad
        {
            TopLeft = new float2(-16f, 4f), BottomRight = new float2(16f, -4f),
            UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
        };

        private static void AddAlongLineIcon(SymbolTileBuffer buffer,
            double3 sceneOriginRender, int materialIndex, float iconRotateRadians, params LineAnchor[] anchors)
        {
            // A short east-west road straddling the look-at, in render space (east = +X).
            var path = new[]
            {
                sceneOriginRender + new double3(-400.0, 0.0, 0.0),
                sceneOriginRender + new double3(400.0, 0.0, 0.0),
            };
            var glyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = AlongLineIconCell } };
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, anchors, path,
                placement: SymbolPlacement.Line,
                iconImage: "arrow",
                kind: SymbolKind.Icon,
                materialIndex: materialIndex,
                textSizePx: TextQuadLayout.OneEm, // scale 1 — mirrors the real builder's along-line icon path
                maxAngleDeg: 180f,
                keepUpright: false,
                featureIndex: 0,
                tileKey: 0L,
                allowOverlap: true, // wiring tooth — never let collision decide whether there is ink to read
                iconRotateRadians: iconRotateRadians,
                paint: SymbolPaint.Default);
        }

        // ── End-to-end: a map-aligned line icon lands in the (tile, slot) ICON world mesh and
        //    NOTHING in the text mesh, with one candidate staged per along-line anchor. ──
        [Test]
        public void AlongLineIconBatch_BuildsIntoTheIconWorldMesh_NotTheTextMesh_OneCandidatePerAnchor()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            Track(camGo);
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = Track(BuildSpriteTexture());
            var settings = Track(BuildSettings());
            Track(settings.SymbolTextWorld);
            Track(settings.SymbolIconWorld);
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                var anchors = new[] { new LineAnchor(0, 0.35f), new LineAnchor(0, 0.65f) };
                var buffer = new SymbolTileBuffer();
                AddAlongLineIcon(buffer, frame.SceneOriginRender, materialIndex: 0, iconRotateRadians: 0f, anchors);

                // Duplicate — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                Assert.AreEqual(anchors.Length, system.LastCandidateCount,
                    "one candidate per along-line anchor (the curved path's per-anchor staging).");
                Assert.AreEqual(anchors.Length, system.LastQuadCount,
                    "one quad per anchor — a one-glyph curved label emits exactly its single cell each time.");

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Icon, out Mesh iconMesh),
                    "the along-line icon must create the ICON world slot (AtlasKind must reach the emit).");
                Assert.AreEqual(anchors.Length * 4, iconMesh.vertexCount, "4 corners per placed anchor");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, SymbolKind.Icon), "the icon slot must be showing");

                // The decisive half: an AtlasKind-forgetting impl routes these quads to the GLYPH texture.
                bool textSlotExists = system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh textMesh);
                Assert.IsTrue(!textSlotExists || textMesh.vertexCount == 0,
                    "no along-line icon quad may land in the TEXT world mesh.");
                Assert.IsFalse(system.IsWorldSlotVisible(0L, 0, SymbolKind.Text),
                    "the text slot must stay hidden — this scene has no text label at all.");

                // The tangent branch is what the shader reads: every emitted corner must carry a non-zero
                // baked world Tangent and the along-line align flag (bit1). Without them the icon would draw
                // unrotated on the GPU, which no CPU readback of position alone could detect.
                WorldMeshReadback.Read(iconMesh, out WorldBillboardVertex[] verts, out _);
                Assert.AreEqual(anchors.Length * 4, verts.Length);
                for (int v = 0; v < verts.Length; v++)
                {
                    Assert.GreaterOrEqual(verts[v].AlignFlags, 1.5f,
                        $"vertex {v}: AlignFlags bit1 (along-line) must be set — the icon shader branches on it.");
                    Assert.Greater(math.lengthsq(verts[v].Tangent), 1e-6f,
                        $"vertex {v}: a non-zero world Tangent must be baked for the shader to rotate by.");
                }
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
            }
        }

        // ── Icon-rotate on the ALONG-LINE path, observed headlessly by mesh readback. Two
        //    identical layers over the SAME road, one with icon-rotate: 180 — the rotated layer's corner
        //    offsets must be the exact negation of the unrotated one's, with identical UVs. ──
        [Test]
        public void AlongLineIconRotate180_NegatesEveryCornerOffset_LeavingUvsUntouched()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            Track(camGo);
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = Track(BuildSpriteTexture());
            var settings = Track(BuildSettings());
            Track(settings.SymbolTextWorld);
            Track(settings.SymbolIconWorld);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                var anchor = new[] { new LineAnchor(0, 0.5f) };
                var buffer = new SymbolTileBuffer();
                AddAlongLineIcon(buffer, frame.SceneOriginRender, materialIndex: 0, iconRotateRadians: 0f, anchor);
                AddAlongLineIcon(buffer, frame.SceneOriginRender, materialIndex: 1, iconRotateRadians: math.PI, anchor);

                // Duplicate — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer, slotCount: 2), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(buffer, slotCount: 2), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Icon, out Mesh plainMesh),
                    "precondition: the UNROTATED layer must have emitted an icon mesh.");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 1, SymbolKind.Icon, out Mesh rotatedMesh),
                    "precondition: the icon-rotate: 180 layer must have emitted its own icon mesh.");

                WorldMeshReadback.Read(plainMesh, out WorldBillboardVertex[] plain, out _);
                WorldMeshReadback.Read(rotatedMesh, out WorldBillboardVertex[] rotated, out _);
                Assert.AreEqual(4, plain.Length, "precondition: the unrotated layer emitted exactly one quad.");
                Assert.AreEqual(plain.Length, rotated.Length, "icon-rotate changes no topology");

                const float Tol = 1e-2f;
                for (int v = 0; v < plain.Length; v++)
                {
                    // The whole tooth: a 180 deg rotation is -I, so every drawn corner offset flips sign.
                    // Without the rotation the two meshes are IDENTICAL, so this is what discriminates.
                    Assert.AreEqual(-plain[v].Offset.x, rotated[v].Offset.x, Tol, $"vertex {v}: Offset.x negated");
                    Assert.AreEqual(-plain[v].Offset.y, rotated[v].Offset.y, Tol, $"vertex {v}: Offset.y negated");
                    // …and the corners move, the TEXTURE does not: an impl that rotated the UVs with them
                    // would draw the sprite mirrored rather than turned.
                    Assert.AreEqual(plain[v].Uv.x, rotated[v].Uv.x, 1e-6f, $"vertex {v}: Uv.x unchanged");
                    Assert.AreEqual(plain[v].Uv.y, rotated[v].Uv.y, 1e-6f, $"vertex {v}: Uv.y unchanged");
                    // The tangent rotation still comes from the shader, identically for both.
                    Assert.AreEqual(plain[v].AlignFlags, rotated[v].AlignFlags, 1e-6f, $"vertex {v}: same align flags");
                }

                Assert.Greater(math.lengthsq(plain[0].Offset), 1f,
                    "precondition: the unrotated offsets must be non-degenerate, or the negation is vacuous.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
            }
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

        // The world-anchored icon shader pair must compile/import.
        [Test]
        public void ShaderMapSymbolIconWorld_IsFound()
        {
            Assert.IsNotNull(Shader.Find("Map/Symbol/IconWorld"), "the SymbolIconWorld.shader must compile/import under \"Map/Symbol/IconWorld\".");
        }

        [Test]
        public void IconBatch_BuildsIconMesh_AndBindsSpriteTexture_TextBatchLeavesIconPresenterHidden()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            Track(camGo);
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = Track(BuildSpriteTexture());
            var settings = Track(BuildSettings());
            Track(settings.SymbolTextWorld);
            Track(settings.SymbolIconWorld);
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            // One plan across both halves: rebuilding it advances WinnerSetVersion, which is what makes the
            // second (text-only) tick refill the mirror instead of hitting the gather memo.
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                // ── 1. Icon-bearing batch ──────────────────────────────────────────────────────────────
                // Icons draw through the WORLD path — the icon quad no longer lands on
                // system.IconMesh/renderLayer.IconPresenterVisible (the screen slot/presenter), so this
                // reads the world surface instead (TryGetWorldSlotMesh/IsWorldSlotVisible).
                var iconBuffer = MakeIcon(frame.SceneOriginRender);
                // Duplicate — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(iconBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(iconBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                Assert.AreEqual(1, system.LastQuadCount, "LastQuadCount must include the icon quad (no text labels this Tick).");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Icon, out Mesh worldIconMesh),
                    "the world icon slot mesh must exist (created lazily on the icon's first Emit).");
                Assert.Greater(worldIconMesh.vertexCount, 0, "the world icon slot mesh must have built non-zero vertices.");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, SymbolKind.Icon), "the world icon presenter must be showing after an icon Tick.");

                Assert.IsNotNull(renderLayer.WorldIconMaterial, "settings.SymbolIconWorld was assigned — WorldIconMaterial must be a clone, not null.");
                Texture boundTexture = renderLayer.WorldIconMaterial.GetTexture("_MainTex");
                Assert.AreSame(spriteTexture, boundTexture,
                    "the world icon presenter's bound material's _MainTex must be the SAME sprite texture instance passed to Tick.");

                // ── 2. Text-only batch (parity: the #1 rule) — world icon slot must go back to HIDDEN, text unaffected ──
                var textBuffer = MakeText(frame.SceneOriginRender);
                system.Tick(in frame, plan.Build(textBuffer), atlasTexture, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(textBuffer), atlasTexture, deltaTime: float.PositiveInfinity, symbolLayers: layers);

                Assert.AreEqual(1, system.LastQuadCount, "the text-only Tick must place its one glyph quad (precondition).");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, SymbolKind.Text), "the world text presenter must still show on a text-only Tick.");
                Assert.IsFalse(system.IsWorldSlotVisible(0L, 0, SymbolKind.Icon),
                    "the world icon presenter must be HIDDEN on a text-only Tick (spriteTexture omitted, no icon quads) — " +
                    "the #1 rule: no sprite loaded ⇒ nothing icon-related builds/binds/presents.");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
            }
        }
    }
}
