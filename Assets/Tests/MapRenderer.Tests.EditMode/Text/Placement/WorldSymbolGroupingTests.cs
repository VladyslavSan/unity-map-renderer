// Unity EditMode only: WorldSymbolRenderer's SceneTileTree creates real GameObjects. NOT in core-tests.csproj.
// World symbols join the root → tile container → symbol-layer node → text/icon children tree that the
// GameObjects tile backend uses, via the shared SceneTileTree. Non-local invariant: the live tile-container
// count equals the number of DISTINCT tiles, never of slots, and only a tile container carries the per-frame
// floating-origin transform (FloatingOrigin.TileToSceneRebased); a text/icon child stays at local identity.
// Count via WorldSymbolTileCount(): the root's childCount also includes the inactive pool nodes.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldSymbolGroupingTests : BaseTestFixture
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
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

        private static void AddText(SymbolTileBuffer buffer, double3 anchorRender, long tileKey, int featureIndex)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            // AllowOverlap: true — all three test symbols share the SAME anchor (frame.SceneOriginRender);
            // without it, collision would cull all but one (mirrors SymbolPlacementAllocTests' identical note).
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default, textSizePx: 24f, sortKey: 0f, featureIndex: featureIndex, tileKey: tileKey,
                allowOverlap: true,
                // PointFadeId hashes (AnchorRender, MaterialIndex, Text, IconImage), so same-anchor symbols need
                // distinct text, or they share the FadeId display key and both show when one wins.
                text: "T" + featureIndex);
        }

        private static void AddIcon(SymbolTileBuffer buffer, double3 anchorRender, long tileKey, int featureIndex)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon, paint: SymbolPaint.Default, textSizePx: TextQuadLayout.OneEm, sortKey: 0f,
                featureIndex: featureIndex, tileKey: tileKey, allowOverlap: true,
                // Same FadeId-uniqueness note as AddText — IconImage (not Text) is the icon-kind
                // identity fold; distinct per-featureIndex.
                iconImage: "icon" + featureIndex);
        }

        [Test]
        public void TextAndIcon_SameTileAndSlot_AreSiblingsUnderOneLayerNode_UnderOneTileContainer_AndASecondTileGetsItsOwnContainer()
        {
            var camGo = Track(new GameObject("WorldSymbolGrouping_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            // Two DISTINCT tiles (arbitrary — unrelated to the camera's actual view).
            var tileA = new TileId { Z = 12, X = 100, Y = 200 };
            var tileB = new TileId { Z = 12, X = 105, Y = 200 };
            long tileAKey = SymbolTileKey.Pack(tileA);
            long tileBKey = SymbolTileKey.Pack(tileB);

            using var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = Track(BuildSpriteTexture());

            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            {
                // tileA: text + icon on the SAME slot (0, the demo/no-symbolLayers path). tileB: text only.
                var buffer = new SymbolTileBuffer();
                AddText(buffer, frame.SceneOriginRender, tileAKey, featureIndex: 0);
                AddIcon(buffer, frame.SceneOriginRender, tileAKey, featureIndex: 1);
                AddText(buffer, frame.SceneOriginRender, tileBKey, featureIndex: 2);
                // Duplicate — the collision verdict is harvested one Tick late.
                system.TickSymbols(in frame, buffer, atlasTexture, mapCamera.Projection, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);
                system.TickSymbols(in frame, buffer, atlasTexture, mapCamera.Projection, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.AreEqual(buffer.Symbols.Count, system.LastQuadCount, "DIAGNOSTIC precondition: all three labels must place (no cull).");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Text), "tileA's text slot must be built+presented.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Icon), "tileA's icon slot must be built+presented.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileBKey, 0, SymbolKind.Text), "tileB's text slot must be built+presented.");

                Transform textA = system.WorldSlotTransform(tileAKey, 0, SymbolKind.Text);
                Transform iconA = system.WorldSlotTransform(tileAKey, 0, SymbolKind.Icon);
                Transform textB = system.WorldSlotTransform(tileBKey, 0, SymbolKind.Text);
                Assert.IsNotNull(textA); Assert.IsNotNull(iconA); Assert.IsNotNull(textB);

                // ── Grouping: text + icon on tileA are SIBLINGS under ONE layer node under ONE container ──
                Assert.AreSame(textA.parent, iconA.parent,
                    "tileA's text and icon children must be SIBLINGS under the SAME per-symbol-layer node — " +
                    "the defect made them separate root-parented GameObjects instead.");
                Transform layerNodeA = textA.parent;
                Assert.AreEqual(2, layerNodeA.childCount, "the shared layer node must have exactly the 2 registered children (text, icon).");

                Transform containerA = layerNodeA.parent;
                Transform containerB = textB.parent.parent;
                Assert.AreNotSame(containerA, containerB, "tileA and tileB must get SEPARATE tile containers.");

                // ── Root's DIRECT children must be counted per TILE, not per SLOT ──────────────────
                Assert.AreSame(system.WorldSymbolTreeRoot(), containerA.parent, "tileA's container must be a DIRECT child of the \"Map Symbols\" root.");
                Assert.AreSame(system.WorldSymbolTreeRoot(), containerB.parent, "tileB's container must be a DIRECT child of the \"Map Symbols\" root.");
                Assert.AreEqual(2, system.WorldSymbolTileCount(),
                    "the root must have exactly 2 direct children (one per DISTINCT tile) even though 3 slots " +
                    "(text+icon on tileA, text on tileB) are live — under the old flat dict this would be 3.");
                Assert.AreEqual("Map Symbols", system.WorldSymbolTreeRoot().name);
                Assert.IsTrue(containerA.name.StartsWith("Tile "), $"container name '{containerA.name}' must follow the tile backend's \"Tile z/x/y\" convention.");

                // ── One transform write per tile, not per slot: children sit at LOCAL identity; only the ──
                // ── container carries the per-frame floating-origin placement.                            ──
                Assert.AreEqual(Vector3.zero, textA.localPosition, "a text/icon child must NOT carry its own placement — the old path wrote one onto every slot's own GameObject.");
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
        }

        // ── Idle-reclaim lifecycle: child destroy → layer-node refcount → tile ReleaseChildFrom ──────────
        // Drives a REAL text+icon pair through reclaim; SymbolPlacementAllocTests' reclaim slot has no GameObject.
        [Test]
        public void IdleReclaim_ReleasesChild_ThenLayerNode_ThenTileContainer_AndRecyclesThemOnReEmit()
        {
            var camGo = Track(new GameObject("WorldSymbolReclaim_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            var tileA = new TileId { Z = 12, X = 110, Y = 200 }; // arbitrary — unrelated to the camera's view
            long tileAKey = SymbolTileKey.Pack(tileA);

            using var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = Track(BuildSpriteTexture());

            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            {
                var textAndIcon = new SymbolTileBuffer();
                AddText(textAndIcon, frame.SceneOriginRender, tileAKey, featureIndex: 0);
                AddIcon(textAndIcon, frame.SceneOriginRender, tileAKey, featureIndex: 1);
                for (int i = 0; i < 3; i++)
                    system.TickSymbols(in frame, textAndIcon, atlasTexture, mapCamera.Projection, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Text), "precondition: text built+presented.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Icon), "precondition: icon built+presented.");
                Transform textChild = system.WorldSlotTransform(tileAKey, 0, SymbolKind.Text);
                Transform iconChild = system.WorldSlotTransform(tileAKey, 0, SymbolKind.Icon);
                Assert.AreSame(textChild.parent, iconChild.parent, "precondition: text+icon are siblings under one layer node.");
                Transform layerNode = textChild.parent;
                Transform container = layerNode.parent;
                Assert.AreEqual(2, layerNode.childCount, "precondition: the layer node has 2 children.");
                Assert.AreEqual(1, system.WorldSymbolTileCount(), "precondition: 1 live tile container.");

                // ── Stop emitting the ICON symbol (keep emitting text) for >IdleReclaimFrames(60) — the icon ──
                // ── child must be destroyed while text + its layer node + the tile container SURVIVE.       ──
                var textOnly = new SymbolTileBuffer();
                AddText(textOnly, frame.SceneOriginRender, tileAKey, featureIndex: 0);
                for (int i = 0; i < 65; i++)
                    system.TickSymbols(in frame, textOnly, atlasTexture, mapCamera.Projection, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.IsFalse(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Icon), "the icon slot must go idle once no longer emitted.");
                Assert.IsNull(system.WorldSlotTransform(tileAKey, 0, SymbolKind.Icon),
                    "the icon child GameObject must be DESTROYED after >IdleReclaimFrames consecutive un-emitted ticks — not just hidden.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Text), "the text slot must still be presented — unaffected by the icon's reclaim.");
                Assert.IsNotNull(system.WorldSlotTransform(tileAKey, 0, SymbolKind.Text), "the text child must SURVIVE the icon's reclaim.");
                Assert.AreSame(layerNode, system.WorldSlotTransform(tileAKey, 0, SymbolKind.Text).parent,
                    "the text child's layer node must SURVIVE (only its icon sibling died) — the SAME node instance as before.");
                Assert.AreEqual(1, layerNode.childCount, "the layer node must have exactly 1 child left (text) after the icon's reclaim.");
                Assert.AreSame(container, layerNode.parent, "the tile container must SURVIVE — its OTHER child (the layer node) is still alive.");
                Assert.AreEqual(1, system.WorldSymbolTileCount(), "the tile container itself must SURVIVE the icon's reclaim.");

                // ── Now stop emitting TEXT too — the layer node AND the tile container must be torn down. ──
                var none = new SymbolTileBuffer();
                for (int i = 0; i < 65; i++)
                    system.TickSymbols(in frame, none, atlasTexture, mapCamera.Projection, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.IsFalse(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Text));
                Assert.IsNull(system.WorldSlotTransform(tileAKey, 0, SymbolKind.Text), "the text child must be released from its slot once idle.");
                Assert.AreEqual(0, system.WorldSymbolTileCount(), "ZERO live tile containers once every tile has gone idle.");

                // Parked nodes sit under an INACTIVE pool node below the root: `== null` and root reachability
                // cannot tell parked from live, but activeInHierarchy can (a leaked orphan stays active).
                Assert.IsFalse(layerNode.gameObject.activeInHierarchy,
                    "the layer node must leave the LIVE tree once its last child (text) is reclaimed (WorldSymbolRenderer.ReleaseChild).");
                Assert.IsFalse(container.gameObject.activeInHierarchy,
                    "the tile container must leave the LIVE tree once its last layer node is gone (SceneTileTree.ReleaseChildFrom).");

                // Checked before any re-emit or dereference: with pooling broken these are destroyed, and the
                // asserts below would fail as a MissingReferenceException from `.parent` without saying why.
                Assert.IsTrue(textChild  != null, "reclaim must PARK the text leaf for reuse, not destroy it.");
                Assert.IsTrue(iconChild  != null, "reclaim must PARK the icon leaf for reuse, not destroy it.");
                Assert.IsTrue(layerNode  != null, "reclaim must PARK the layer node for reuse, not destroy it.");
                Assert.IsTrue(container  != null, "reclaim must PARK the tile container for reuse, not destroy it.");

                // ── Re-emit the SAME tile+slot: every node must come back as the SAME instance ──
                // Nothing else here notices a pool that stops working (a Return that destroys, a Rent that creates).
                for (int i = 0; i < 3; i++)
                    system.TickSymbols(in frame, textAndIcon, atlasTexture, mapCamera.Projection, deltaTime: float.PositiveInfinity, spriteTexture: spriteTexture);

                Assert.AreSame(textChild, system.WorldSlotTransform(tileAKey, 0, SymbolKind.Text),
                    "the reclaimed TEXT leaf must be recycled, not rebuilt — its two AddComponent calls are what pooling exists to avoid.");
                Assert.AreSame(iconChild, system.WorldSlotTransform(tileAKey, 0, SymbolKind.Icon),
                    "the reclaimed ICON leaf must come back from its OWN pool — one shared pool would hand the text leaf to an icon slot.");
                Assert.AreSame(layerNode, textChild.parent, "the recycled leaf must re-parent under the recycled layer node.");
                Assert.AreSame(container, layerNode.parent, "…which itself re-parents under the recycled tile container.");
                Assert.AreEqual(1, system.WorldSymbolTileCount(), "exactly one live tile container again.");

                // A recycled leaf must carry NO state from its previous tenancy: the slot's Mesh is destroyed on
                // release, so a leaf that kept the binding would draw a destroyed Mesh (pink in the Editor).
                Assert.IsTrue(system.IsWorldSlotVisible(tileAKey, 0, SymbolKind.Text), "the recycled text leaf must draw again.");
                Assert.IsTrue(system.TryGetWorldSlotMesh(tileAKey, 0, SymbolKind.Text, out Mesh reusedMesh) && reusedMesh != null,
                    "the recycled leaf's slot must hold a live mesh.");
                Assert.AreSame(reusedMesh, textChild.GetComponent<MeshFilter>().sharedMesh,
                    "the MeshFilter must point at the slot's CURRENT mesh, not the destroyed one from its previous tenancy.");
            }
        }

    }
}
