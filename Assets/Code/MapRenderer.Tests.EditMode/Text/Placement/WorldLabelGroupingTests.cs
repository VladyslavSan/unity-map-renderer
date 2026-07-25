// Unity EditMode only — real Camera/Mesh/GameObject/Texture2D (WorldLabelRenderer's SceneTileTree creates
// real GameObjects). NOT registered in core-tests.csproj.
//
// The label-draw-backend-rework corrective (docs/.utmp/label-draw-backend-rework.md §3/§4/§6/§7): A1
// (876e7103) grouped world labels into a bespoke FLAT Dictionary<(TileKey,Slot,Kind)> of root-parented
// GameObjects, each re-writing the tile's floating-origin transform every frame. This pins the FIX — labels
// now join the SAME root → per-tile-container → per-symbol-layer-node → text/icon-sibling-children tree the
// GameObjects tile backend uses (Rendering/Backend/GameObjects/TileRenderer.cs), via the shared
// SceneTileTree:
//   • Grouping: two labels (text + icon) on the SAME tile+slot must be SIBLINGS under ONE layer node under
//     ONE tile container; a THIRD label on a DIFFERENT tile must get its OWN separate container — but the
//     "Map Labels" root's DIRECT child count must equal the number of DISTINCT TILES (2), never the number
//     of slots (3), which is exactly what an A1-shaped regression (one root-parented GO per slot) would fail.
//   • One transform write per tile, not per slot: a text/icon child's own LOCAL transform must stay at
//     identity — only its tile container carries the per-frame floating-origin placement, matching the
//     GameObjects backend's oracle formula (FloatingOrigin.TileToSceneRebased).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldLabelGroupingTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
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

        private static LabelInstance MakeTextLabel(double3 anchorRender, long tileKey, int featureIndex)
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
                // AllowOverlap: true — all three test labels share the SAME anchor (frame.SceneOriginRender);
                // without it, collision would cull all but one (mirrors LabelPlacementAllocTests' identical note).
                AnchorRender = anchorRender, Layout = layout, Paint = LabelPaint.Default,
                TextSizePx = 24f, SortKey = 0f, FeatureIndex = featureIndex, TileKey = tileKey, AllowOverlap = true,
                // R3: PointFadeId hashes (AnchorRender, MaterialIndex, Text, IconImage) — NOT FeatureIndex/TileKey
                // — so two labels sharing the SAME anchor (as every call site here does) with Text left at its
                // default would collide on FadeId. Under R3, FadeId is the display key (LabelCandidate.FadeId's
                // uniqueness contract), so co-live candidates sharing an id both show when one wins. Distinct
                // per-featureIndex text keeps every call site's identity unique.
                Text = "T" + featureIndex,
            };
        }

        private static LabelInstance MakeIconLabel(double3 anchorRender, long tileKey, int featureIndex)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            var layout = new TextLayoutResult { Quads = quads, BoundsMin = new float2(-8f, -8f), BoundsMax = new float2(8f, 8f), LineCount = 1 };
            return new LabelInstance
            {
                AnchorRender = anchorRender, Layout = layout, Kind = LabelKind.Icon, Paint = LabelPaint.Default,
                TextSizePx = TextQuadLayout.OneEm, SortKey = 0f, FeatureIndex = featureIndex, TileKey = tileKey, AllowOverlap = true,
                // R3: same FadeId-uniqueness note as MakeTextLabel — IconImage (not Text) is the icon-kind
                // identity fold; distinct per-featureIndex.
                IconImage = "icon" + featureIndex,
            };
        }

        [Test]
        public void TextAndIcon_SameTileAndSlot_AreSiblingsUnderOneLayerNode_UnderOneTileContainer_AndASecondTileGetsItsOwnContainer()
        {
            var camGo = new GameObject("WorldLabelGrouping_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(mapCamera.Projection.Project(lookAt), float3x3.identity);

            // Two DISTINCT tiles (arbitrary — unrelated to the camera's actual view).
            var tileA = new TileId { Z = 12, X = 100, Y = 200 };
            var tileB = new TileId { Z = 12, X = 105, Y = 200 };
            long tileAKey = SymbolFeatureExtractor.PackTileKey(tileA);
            long tileBKey = SymbolFeatureExtractor.PackTileKey(tileB);

            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();

            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            try
            {
                // tileA: text + icon on the SAME slot (0, the demo/no-symbolLayers path). tileB: text only.
                var labels = new List<LabelInstance>
                {
                    MakeTextLabel(frame.SceneOriginRender, tileAKey, featureIndex: 0),
                    MakeIconLabel(frame.SceneOriginRender, tileAKey, featureIndex: 1),
                    MakeTextLabel(frame.SceneOriginRender, tileBKey, featureIndex: 2),
                };
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, labels, atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);
                system.Tick(in frame, labels, atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.AreEqual(labels.Count, system.LastQuadCount, "DIAGNOSTIC precondition: all three labels must place (no cull).");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, LabelKind.Text), "tileA's text slot must be built+presented.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, LabelKind.Icon), "tileA's icon slot must be built+presented.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileBKey, 0, LabelKind.Text), "tileB's text slot must be built+presented.");

                Transform textA = system.WorldSlotTransform(tileAKey, 0, LabelKind.Text);
                Transform iconA = system.WorldSlotTransform(tileAKey, 0, LabelKind.Icon);
                Transform textB = system.WorldSlotTransform(tileBKey, 0, LabelKind.Text);
                Assert.IsNotNull(textA); Assert.IsNotNull(iconA); Assert.IsNotNull(textB);

                // ── Grouping: text + icon on tileA are SIBLINGS under ONE layer node under ONE container ──
                Assert.AreSame(textA.parent, iconA.parent,
                    "tileA's text and icon children must be SIBLINGS under the SAME per-symbol-layer node — " +
                    "the A1 defect made them separate root-parented GameObjects instead.");
                Transform layerNodeA = textA.parent;
                Assert.AreEqual(2, layerNodeA.childCount, "the shared layer node must have exactly the 2 registered children (text, icon).");

                Transform containerA = layerNodeA.parent;
                Transform containerB = textB.parent.parent;
                Assert.AreNotSame(containerA, containerB, "tileA and tileB must get SEPARATE tile containers.");

                // ── Root's DIRECT children must be counted per TILE, not per SLOT (the A1 defect) ──
                Assert.AreSame(system.WorldLabelTreeRoot, containerA.parent, "tileA's container must be a DIRECT child of the \"Map Labels\" root.");
                Assert.AreSame(system.WorldLabelTreeRoot, containerB.parent, "tileB's container must be a DIRECT child of the \"Map Labels\" root.");
                Assert.AreEqual(2, system.WorldLabelTreeRoot.childCount,
                    "the root must have exactly 2 direct children (one per DISTINCT tile) even though 3 slots " +
                    "(text+icon on tileA, text on tileB) are live — under A1's flat dict this would be 3.");
                Assert.AreEqual("Map Labels", system.WorldLabelTreeRoot.name);
                Assert.IsTrue(containerA.name.StartsWith("Tile "), $"container name '{containerA.name}' must follow the tile backend's \"Tile z/x/y\" convention.");

                // ── One transform write per tile, not per slot: children sit at LOCAL identity; only the ──
                // ── container carries the per-frame floating-origin placement.                            ──
                Assert.AreEqual(Vector3.zero, textA.localPosition, "a text/icon child must NOT carry its own placement — A1 wrote one onto every slot's own GameObject.");
                Assert.AreEqual(Vector3.zero, iconA.localPosition, "a text/icon child must NOT carry its own placement.");
                Assert.AreEqual(Quaternion.identity, textA.localRotation);
                Assert.AreEqual(Vector3.zero, textB.localPosition);

                double3 originA = TileRenderOrigin.Project(tileA, mapCamera.Projection);
                double3 originB = TileRenderOrigin.Project(tileB, mapCamera.Projection);
                float3 expectedA = FloatingOrigin.TileToSceneRebased(originA, frame.SceneOriginRender, frame.Rebase);
                float3 expectedB = FloatingOrigin.TileToSceneRebased(originB, frame.SceneOriginRender, frame.Rebase);
                Assert.That(containerA.position.x, Is.EqualTo(expectedA.x).Within(0.01f), "tileA's container must sit at FloatingOrigin.TileToSceneRebased(tileA's origin) — the SAME formula the GameObjects tile backend's Rebuild uses.");
                Assert.That(containerA.position.z, Is.EqualTo(expectedA.z).Within(0.01f));
                Assert.That(containerB.position.x, Is.EqualTo(expectedB.x).Within(0.01f), "tileB's container must sit at its OWN tile origin, independently of tileA's.");
                Assert.That(containerB.position.z, Is.EqualTo(expectedB.z).Within(0.01f));
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Idle-reclaim lifecycle: child destroy → layer-node refcount → tile ReleaseChildFrom ──────────
        // The only pre-existing K=60-crossing tooth (LabelPlacementAllocTests) uses a slot that never builds
        // a GameObject (no material resolved), so the NEW refcount-decrement/destroy code this rework added
        // (WorldLabelRenderer.ReleaseChild + SceneTileTree.ReleaseChildFrom) was reached only by reasoning,
        // not exercised. This drives a REAL text+icon pair through reclaim end-to-end.
        [Test]
        public void IdleReclaim_DestroysChild_ThenLayerNode_ThenTileContainer_AsSlotsGoIdle()
        {
            var camGo = new GameObject("WorldLabelReclaim_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(mapCamera.Projection.Project(lookAt), float3x3.identity);

            var tileA = new TileId { Z = 12, X = 110, Y = 200 }; // arbitrary — unrelated to the camera's view
            long tileAKey = SymbolFeatureExtractor.PackTileKey(tileA);

            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();

            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            try
            {
                var textAndIcon = new List<LabelInstance>
                {
                    MakeTextLabel(frame.SceneOriginRender, tileAKey, featureIndex: 0),
                    MakeIconLabel(frame.SceneOriginRender, tileAKey, featureIndex: 1),
                };
                for (int i = 0; i < 3; i++)
                    system.Tick(in frame, textAndIcon, atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, LabelKind.Text), "precondition: text built+presented.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, LabelKind.Icon), "precondition: icon built+presented.");
                Transform textChild = system.WorldSlotTransform(tileAKey, 0, LabelKind.Text);
                Transform iconChild = system.WorldSlotTransform(tileAKey, 0, LabelKind.Icon);
                Assert.AreSame(textChild.parent, iconChild.parent, "precondition: text+icon are siblings under one layer node.");
                Transform layerNode = textChild.parent;
                Transform container = layerNode.parent;
                Assert.AreEqual(2, layerNode.childCount, "precondition: the layer node has 2 children.");
                Assert.AreEqual(1, system.WorldLabelTreeRoot.childCount, "precondition: 1 live tile container.");

                // ── Stop emitting the ICON label (keep emitting text) for >IdleReclaimFrames(60) — the icon ──
                // ── child must be destroyed while text + its layer node + the tile container SURVIVE.       ──
                var textOnly = new List<LabelInstance> { MakeTextLabel(frame.SceneOriginRender, tileAKey, featureIndex: 0) };
                for (int i = 0; i < 65; i++)
                    system.Tick(in frame, textOnly, atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.IsFalse(system.IsWorldSlotVisible(tileAKey, 0, LabelKind.Icon), "the icon slot must go idle once no longer emitted.");
                Assert.IsNull(system.WorldSlotTransform(tileAKey, 0, LabelKind.Icon),
                    "the icon child GameObject must be DESTROYED after >IdleReclaimFrames consecutive un-emitted ticks — not just hidden.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, LabelKind.Text), "the text slot must still be presented — unaffected by the icon's reclaim.");
                Assert.IsNotNull(system.WorldSlotTransform(tileAKey, 0, LabelKind.Text), "the text child must SURVIVE the icon's reclaim.");
                Assert.AreSame(layerNode, system.WorldSlotTransform(tileAKey, 0, LabelKind.Text).parent,
                    "the text child's layer node must SURVIVE (only its icon sibling died) — the SAME node instance as before.");
                Assert.AreEqual(1, layerNode.childCount, "the layer node must have exactly 1 child left (text) after the icon's reclaim.");
                Assert.AreSame(container, layerNode.parent, "the tile container must SURVIVE — its OTHER child (the layer node) is still alive.");
                Assert.AreEqual(1, system.WorldLabelTreeRoot.childCount, "the tile container itself must SURVIVE the icon's reclaim.");

                // ── Now stop emitting TEXT too — the layer node AND the tile container must be torn down. ──
                var none = new List<LabelInstance>();
                for (int i = 0; i < 65; i++)
                    system.Tick(in frame, none, atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.IsFalse(system.IsWorldSlotVisible(tileAKey, 0, LabelKind.Text));
                Assert.IsNull(system.WorldSlotTransform(tileAKey, 0, LabelKind.Text), "the text child must also be destroyed once idle.");
                Assert.IsTrue(layerNode == null, "the layer node must be destroyed once its last child (text) is reclaimed — no orphan node (WorldLabelRenderer.ReleaseChild).");
                Assert.IsTrue(container == null, "the tile container must be destroyed once its last layer node is gone — no orphan container (SceneTileTree.ReleaseChildFrom).");
                Assert.AreEqual(0, system.WorldLabelTreeRoot.childCount, "the root must have ZERO children once every tile has gone idle.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
