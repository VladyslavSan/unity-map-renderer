// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file lives in
// MapRenderer.Unity.Text.Placement and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;`
// + unqualified types, never an inline `Unity.Mathematics.X`.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Epic A / A1 (design §3.3, §11 A1 D1) + the label-draw-backend-rework corrective (design §5, §7): the
    /// dedicated world-anchored label renderer — NOT an <see cref="Backend.ITileRenderBackend"/> (no
    /// mesh-update op, no per-instance registration a label's per-frame Opacity rewrite would fit). Owns a
    /// persistent <c>Dictionary&lt;WorldLabelKey, Slot&gt;</c> keyed by <c>(TileKey, Slot, Kind)</c> — one
    /// mesh per (tile, material slot, text/icon) — plus its OWN <see cref="SceneTileTree"/> ("Map Labels"
    /// root) so labels are grouped root → per-tile container → per-symbol-layer node → text/icon sibling
    /// children, the SAME organization the <see cref="Backend.GameObjects.TileRenderer"/> tile backend uses
    /// for fill/line, regardless of which backend is drawing tile fills (Entities/BRG draw them
    /// GameObject-free, so labels cannot piggyback on a tile-backend container — they mirror the shape with
    /// their own tree instance instead).
    ///
    /// <para><b>Per-frame lifecycle</b> (mirrors <see cref="LabelPlacementSystem"/>'s per-slot present/hide
    /// discipline): <see cref="BeginFrame"/> clears every live slot's native accumulators (dictionary +
    /// slots persist — no alloc); <see cref="Emit"/> appends a surviving point/icon/curved candidate's glyph
    /// corners into its slot (created lazily on first use — warm-up-only alloc); <see cref="EndFrame"/>
    /// builds every non-empty slot's mesh, lazily attaches it to a text/icon child under its tile's
    /// per-symbol-layer node, hides the rest, refreshes the tree's per-tile transform ONCE (not per slot),
    /// and reclaims a slot idle for <see cref="IdleReclaimFrames"/> consecutive frames (self-contained —
    /// reads only what <see cref="Emit"/> handed it, never a batch tile array, so the coverage cull's
    /// <c>-1</c> degenerate can never reach here — D2's BLOCKER fix).</para>
    ///
    /// <para>Main-thread only (touches <see cref="Mesh"/>/<see cref="GameObject"/>, mirrors every other
    /// GPU-resource boundary in this codebase).</para>
    /// </summary>
    internal sealed class WorldLabelRenderer : IDisposable
    {
        // A slot idle for this many consecutive EndFrames (no content, nothing presented) is torn down —
        // roughly a second of frames at 60fps. A reappearing key just lazily re-creates its slot (D1).
        private const int IdleReclaimFrames = 60;

        // Stage AC (curved-world) D-I: WorldBillboardVertex.AlignFlags bit1 — "rotate OffsetPx by the
        // projected Tangent." Point/icon leave bit0 (map-bearing, A3) as their only bit; curved sets ONLY
        // this one (bit1 set ⇒ the shader ignores bit0 — MapLibre line placement ignores
        // text-rotation-alignment).
        private const float AlongLineAlignFlag = 2f;

        private sealed class Slot
        {
            public Mesh Mesh;
            public MeshFilter Filter;      // lazy — created on the first visible EndFrame (mirrors old Presenter laziness)
            public MeshRenderer Renderer;
            public NativeList<WorldBillboardVertex> Vertices;
            public NativeList<float> Opacity;
            public NativeList<int> Indices;
            public double3 TileOriginRender;
            public int IdleFrames;
        }

        // The per-symbol-layer node under a tile container that a slot's text/icon child hangs off. Keyed by
        // (tile, DrawIndex) so a tile's text and icon children for the SAME layer are siblings under ONE node
        // — the layer-node level of the root→tile→layer tree that SceneTileTree itself only owns one level
        // of (root→tile); this dictionary is the thin second level specific to the label path.
        private struct LayerNodeRec
        {
            public GameObject Go;
            public int ChildCount; // text/icon children; node dies when this hits 0
        }

        private readonly struct LayerNodeKey : IEquatable<LayerNodeKey>
        {
            public readonly TileId TileId;
            public readonly int Slot;
            public LayerNodeKey(TileId tileId, int slot) { TileId = tileId; Slot = slot; }
            public bool Equals(LayerNodeKey other) => TileId.Equals(other.TileId) && Slot == other.Slot;
            public override bool Equals(object obj) => obj is LayerNodeKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return TileId.GetHashCode() * 31 + Slot; }
            }
        }

        // "Map Labels" — the label path's own root→tile-container tree (mirrors the GameObjects tile
        // backend's "MapTiles (GameObject backend)" tree, §5 of the rework design: labels always maintain
        // their own tree so the organization is identical no matter which backend draws tile fills).
        private readonly SceneTileTree _tree = new SceneTileTree("Map Labels");
        private readonly Dictionary<LayerNodeKey, LayerNodeRec> _layerNodes = new Dictionary<LayerNodeKey, LayerNodeRec>();

        private readonly Dictionary<WorldLabelKey, Slot> _slots = new Dictionary<WorldLabelKey, Slot>();

        // Reused reclaim-sweep scratch (cleared each EndFrame, never reallocated in steady state — T4).
        private readonly List<WorldLabelKey> _reclaimScratch = new List<WorldLabelKey>();

        /// <summary>Clears every live slot's accumulators for a fresh emit pass. Dictionary + slots persist
        /// (no alloc in steady state).</summary>
        public void BeginFrame()
        {
            foreach (KeyValuePair<WorldLabelKey, Slot> kv in _slots)
            {
                kv.Value.Vertices.Clear();
                kv.Value.Opacity.Clear();
                kv.Value.Indices.Clear();
            }
        }

        /// <summary>
        /// Appends <paramref name="emit"/>'s already-staged glyph quads (<paramref name="quads"/>, the SAME
        /// pool <c>LabelPlacementSystem</c>'s screen path reads — D6, no new staged-quad stream) into the
        /// slot for <c>(emit.TileKey, emit.Slot, emit.AtlasKind)</c>, creating it lazily on first use.
        /// Returns the quad count emitted (for <c>LastQuadCount</c>). Reads the world payload ONLY from
        /// <paramref name="emit"/> — never a batch tile array (D2).
        /// </summary>
        public int Emit(in CandidateEmit emit, NativeArray<PlacedQuad> quads, float fadeOpacity)
        {
            var key = new WorldLabelKey(emit.TileKey, emit.Slot, emit.AtlasKind);
            if (!_slots.TryGetValue(key, out Slot slot))
            {
                slot = new Slot
                {
                    Mesh = new Mesh { name = $"WorldLabelRenderer_Mesh_{emit.TileKey}_{emit.Slot}_{emit.AtlasKind}" },
                    Vertices = new NativeList<WorldBillboardVertex>(Allocator.Persistent),
                    Opacity = new NativeList<float>(Allocator.Persistent),
                    Indices = new NativeList<int>(Allocator.Persistent),
                };
                slot.Mesh.MarkDynamic();
                _slots[key] = slot;
            }
            slot.TileOriginRender = emit.TileOriginRender;

            int quadCount = emit.QuadCount;
            for (int k = 0; k < quadCount; k++)
            {
                PlacedQuad q = quads[emit.QuadStart + k];
                // Stage AC D2/D6: a curved candidate's world anchor/tangent are PER-GLYPH (on the quad
                // itself), not per-candidate (emit.AnchorLocal is a point label's single shared anchor) —
                // and its corners are UNROTATED (rotationRadians: 0f), rotated instead by the shader from
                // the projected Tangent (D-E). Point/icon keep the existing per-candidate anchor + baked
                // screen rotation + tangentLocal=0/alignFlags=0 (byte-identical — the shader's tangent
                // branch is never taken for them).
                float3 anchorLocal = emit.AlongLine ? q.AnchorLocal : emit.AnchorLocal;
                float  rotationRadians = emit.AlongLine ? 0f : q.RotationRadians;
                float3 tangentLocal = emit.AlongLine ? q.Tangent : float3.zero;
                float  alignFlags = emit.AlongLine ? AlongLineAlignFlag : 0f;

                BillboardMath.BuildWorldQuad(in q.Quad, in anchorLocal, q.TextSizePx, q.Color.xyz,
                    rotationRadians, in emit.TranslateDeltaPx, in tangentLocal, alignFlags,
                    out WorldBillboardVertex tl, out WorldBillboardVertex tr,
                    out WorldBillboardVertex br, out WorldBillboardVertex bl);

                int vBase = slot.Vertices.Length;
                slot.Vertices.Add(tl); slot.Vertices.Add(tr); slot.Vertices.Add(br); slot.Vertices.Add(bl);

                float opacity = q.Color.w * fadeOpacity; // stream 1 — the A-4 fade × the quad's own alpha
                slot.Opacity.Add(opacity); slot.Opacity.Add(opacity); slot.Opacity.Add(opacity); slot.Opacity.Add(opacity);

                slot.Indices.Add(vBase + 0); slot.Indices.Add(vBase + 1); slot.Indices.Add(vBase + 2);
                slot.Indices.Add(vBase + 0); slot.Indices.Add(vBase + 2); slot.Indices.Add(vBase + 3);
            }
            return quadCount;
        }

        /// <summary>
        /// Builds every non-empty slot's mesh and lazily attaches it to a text/icon child under its tile's
        /// per-symbol-layer node in the shared <see cref="_tree"/> (root → tile container → layer node →
        /// text/icon sibling children — design §3/§5/§6), resolving its draw material from
        /// <paramref name="symbolLayers"/> (D5) or the <paramref name="fallbackTextMaterial"/>/
        /// <paramref name="fallbackIconMaterial"/> demo path; hides every other live slot (idle-frame
        /// reclamation destroys one idle <see cref="IdleReclaimFrames"/> consecutive frames). A
        /// resolved-null material (Codex #1b — an icon slot with no <c>WorldIconMaterial</c> configured)
        /// hides that slot instead of throwing. Refreshes the tree's per-tile transform exactly ONCE per
        /// call via <see cref="SceneTileTree.Rebuild"/> — not once per slot (the A1 defect this corrects).
        /// </summary>
        private static readonly int AtlasPropId               = Shader.PropertyToID("_MainTex");
        private static readonly int ScreenParamsLogicalPropId = Shader.PropertyToID("_ScreenParamsLogical");

        /// <param name="atlasTexture">The glyph atlas (Texture2DArray) TEXT world materials bind to.</param>
        /// <param name="spriteTexture">The sprite sheet ICON world materials bind to (may be null — icons optional).</param>
        /// <param name="viewportLogicalPx">This frame's logical viewport size — refreshes the resolved
        /// material's <c>_ScreenParamsLogical</c> (the vertex shader's px→clip offset scale), mirrors
        /// <c>LabelPlacementSystem.BuildSlotMesh</c>'s identical per-frame refresh.</param>
        public void EndFrame(in SceneFrame frame, IReadOnlyList<SymbolRenderLayer> symbolLayers,
            Material fallbackTextMaterial, Material fallbackIconMaterial,
            Texture atlasTexture, Texture spriteTexture, double2 viewportLogicalPx)
        {
            foreach (KeyValuePair<WorldLabelKey, Slot> kv in _slots)
            {
                WorldLabelKey key = kv.Key;
                Slot slot = kv.Value;

                // Idle-reclaim tracks whether this key was EMITTED to this frame (D1's "self-contained"
                // reclamation), NOT whether it ended up presented — an emitted-but-unrenderable slot (no
                // resolved material: e.g. an icon slot with no WorldIconMaterial configured, or the demo path
                // with a null worldTextBase) is a legitimate steady-state case that must stay resident
                // (hidden), not churn its Mesh/GameObject/NativeLists every IdleReclaimFrames.
                bool emittedThisFrame = slot.Vertices.Length > 0;
                Material material = emittedThisFrame
                    ? ResolveMaterial(in key, symbolLayers, fallbackTextMaterial, fallbackIconMaterial)
                    : null;

                if (material != null)
                {
                    WorldBillboardMeshBuilder.Build(slot.Vertices.AsArray(), slot.Opacity.AsArray(), slot.Indices.AsArray(), slot.Mesh);

                    // The texture/screen-params refresh BuildSlotMesh does for the screen path — the world
                    // material needs the SAME per-frame bind (atlas/sprite texture never changes per-slot,
                    // only per-Kind, so this is a straight compare-assign-free SetTexture/SetVector).
                    Texture texture = key.Kind == LabelKind.Icon ? spriteTexture : atlasTexture;
                    material.SetTexture(AtlasPropId, texture);
                    material.SetVector(ScreenParamsLogicalPropId,
                        new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                    EnsureChild(in key, slot, symbolLayers); // lazy: tile container → layer node → text/icon child
                    if (slot.Renderer.sharedMaterial != material) slot.Renderer.sharedMaterial = material;
                    slot.Renderer.enabled = true;
                    slot.IdleFrames = 0;
                }
                else
                {
                    if (slot.Renderer != null) slot.Renderer.enabled = false;
                    slot.IdleFrames = emittedThisFrame ? 0 : slot.IdleFrames + 1;
                }

                if (slot.IdleFrames >= IdleReclaimFrames) _reclaimScratch.Add(key);
            }

            // ONE transform write per tile container per frame — not per slot (the A1 defect this corrects).
            _tree.Rebuild(in frame);

            for (int i = 0; i < _reclaimScratch.Count; i++)
            {
                WorldLabelKey key = _reclaimScratch[i];
                Slot slot = _slots[key];
                ReleaseChild(in key, slot);
                slot.Mesh.DestroySafely();
                slot.Vertices.Dispose();
                slot.Opacity.Dispose();
                slot.Indices.Dispose();
                _slots.Remove(key);
            }
            _reclaimScratch.Clear();
        }

        /// <summary>Lazily creates <paramref name="slot"/>'s text/icon child GameObject (MeshFilter bound to
        /// the slot's persistent, reused <see cref="Mesh"/> — no rebind afterward, only its buffers change)
        /// under <c>(TileId, key.Slot)</c>'s layer node, itself lazily created under the tile's container.
        /// No-op if the child already exists (steady-state: zero GameObject churn, zero alloc).</summary>
        private void EnsureChild(in WorldLabelKey key, Slot slot, IReadOnlyList<SymbolRenderLayer> symbolLayers)
        {
            if (slot.Renderer != null) return;

            TileId tileId = SymbolFeatureExtractor.UnpackTileKey(key.TileKey);
            Transform tileContainer = _tree.GetOrCreateTileNode(tileId, slot.TileOriginRender);

            var layerKey = new LayerNodeKey(tileId, key.Slot);
            if (!_layerNodes.TryGetValue(layerKey, out LayerNodeRec layerRec) || layerRec.Go == null)
            {
                // Name after the style layer id ("poi-label", …) so the Hierarchy reads like the tile
                // backend's fill/line children — mirrors GameObjects.TileRenderer.AddTileLayer's fallback.
                string layerName = (symbolLayers != null && (uint)key.Slot < (uint)symbolLayers.Count)
                    ? symbolLayers[key.Slot]?.StyleLayer?.Id
                    : null;
                if (string.IsNullOrEmpty(layerName)) layerName = $"symbol-{key.Slot}";

                var layerGo = new GameObject(layerName);
                layerGo.transform.SetParent(tileContainer, worldPositionStays: false);
                layerGo.transform.localPosition = Vector3.zero;
                layerRec = new LayerNodeRec { Go = layerGo, ChildCount = 0 };
                _tree.AddChild(tileId);
            }

            var childGo = new GameObject(key.Kind == LabelKind.Icon ? "icon" : "text") { hideFlags = HideFlags.DontSave };
            childGo.transform.SetParent(layerRec.Go.transform, worldPositionStays: false);
            childGo.transform.localPosition = Vector3.zero;

            slot.Filter = childGo.AddComponent<MeshFilter>();
            slot.Filter.sharedMesh = slot.Mesh; // sharedMesh: assign once — the SAME Mesh object is rewritten in place every rebuild

            slot.Renderer = childGo.AddComponent<MeshRenderer>();
            slot.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            slot.Renderer.receiveShadows    = false;

            layerRec.ChildCount++;
            _layerNodes[layerKey] = layerRec;
        }

        /// <summary>Destroys <paramref name="slot"/>'s text/icon child (if ever created), releasing it from
        /// its layer node — which is destroyed once its last child is gone, releasing the tile container in
        /// turn (via <see cref="SceneTileTree.ReleaseChildFrom"/>) once its last layer node is gone.</summary>
        private void ReleaseChild(in WorldLabelKey key, Slot slot)
        {
            if (slot.Renderer == null) return; // never presented — nothing to tear down

            slot.Renderer.gameObject.DestroySafely();
            slot.Filter = null;
            slot.Renderer = null;

            TileId tileId = SymbolFeatureExtractor.UnpackTileKey(key.TileKey);
            var layerKey = new LayerNodeKey(tileId, key.Slot);
            if (!_layerNodes.TryGetValue(layerKey, out LayerNodeRec layerRec)) return;

            layerRec.ChildCount--;
            if (layerRec.ChildCount <= 0)
            {
                layerRec.Go.DestroySafely();
                _layerNodes.Remove(layerKey);
                _tree.ReleaseChildFrom(tileId);
            }
            else
            {
                _layerNodes[layerKey] = layerRec;
            }
        }

        // D5: the per-layer world material (WorldTextMaterial/WorldIconMaterial), else the demo fallback —
        // mirrors LabelPlacementSystem.ResolveSlotMaterial/ResolveIconMaterial. A slot index out of range
        // (or no symbolLayers — the demo path) resolves straight to the fallback, never throws. Both world
        // materials' renderQueue are now written at Build/Create time (RenderLayerSet.Build for text via
        // SymbolRenderLayer.Material, SymbolRenderLayer.Create for icon — §0.1), so this is pure selection —
        // no per-frame queue sync.
        private static Material ResolveMaterial(in WorldLabelKey key, IReadOnlyList<SymbolRenderLayer> symbolLayers,
            Material fallbackTextMaterial, Material fallbackIconMaterial)
        {
            SymbolRenderLayer layer = (symbolLayers != null && (uint)key.Slot < (uint)symbolLayers.Count)
                ? symbolLayers[key.Slot] : null;

            return key.Kind == LabelKind.Icon
                ? (layer?.WorldIconMaterial ?? fallbackIconMaterial)
                : (layer?.WorldTextMaterial ?? fallbackTextMaterial);
        }

        // ── Test surface (D9 §E-flip; also used to read fade opacity off the world mesh — see
        //    WorldMeshReadback) ──────────────────────────────────────────────────────────────────────────

        /// <summary>The mesh bound to <c>(tileKey, slot, kind)</c>'s slot, or null if no such slot exists yet.
        /// Returns the mesh regardless of whether the slot is currently VISIBLE (a hidden slot still holds
        /// its last-built content) — see <see cref="IsSlotVisible"/> for the presenter's show/hide state.</summary>
        internal bool TryGetSlotMesh(long tileKey, int slot, LabelKind kind, out Mesh mesh)
        {
            if (_slots.TryGetValue(new WorldLabelKey(tileKey, slot, kind), out Slot s))
            {
                mesh = s.Mesh;
                return true;
            }
            mesh = null;
            return false;
        }

        /// <summary>Whether <c>(tileKey, slot, kind)</c>'s text/icon child is currently drawing — queries
        /// the child's own <see cref="MeshRenderer.enabled"/> in the shared tree (a child that was never
        /// created, because the slot has never resolved a material, reads as not visible).</summary>
        internal bool IsSlotVisible(long tileKey, int slot, LabelKind kind)
            => _slots.TryGetValue(new WorldLabelKey(tileKey, slot, kind), out Slot s)
                && s.Renderer != null && s.Renderer.enabled;

        /// <summary>The label tree's root transform ("Map Labels"). Test surface — the grouping tooth reads
        /// the live Hierarchy through it (root → per-tile container → per-symbol-layer node → text/icon
        /// sibling children).</summary>
        internal Transform TreeRoot => _tree.Root;

        /// <summary>The text/icon CHILD transform bound to <c>(tileKey, slot, kind)</c>'s slot, or null if
        /// never presented. Test surface — the grouping tooth walks <c>.parent</c> (the layer node) and
        /// <c>.parent.parent</c> (the tile container) from here, and asserts this transform itself sits at
        /// LOCAL identity (the container carries the ONE per-tile placement write, not this child).</summary>
        internal Transform GetSlotTransform(long tileKey, int slot, LabelKind kind)
            => _slots.TryGetValue(new WorldLabelKey(tileKey, slot, kind), out Slot s) && s.Renderer != null
                ? s.Renderer.transform : null;

        /// <summary>Destroys every GameObject in the shared tree (containers, layer nodes, text/icon
        /// children — one <see cref="SceneTileTree.Dispose"/> call, root-down) BEFORE destroying each slot's
        /// mesh (a MeshRenderer whose sharedMesh was destroyed first logs/renders pink in edit mode), then
        /// disposes every accumulator. Idempotent (an empty dictionary after the first call is a no-op).</summary>
        public void Dispose()
        {
            _tree.Dispose();
            foreach (KeyValuePair<WorldLabelKey, Slot> kv in _slots)
            {
                kv.Value.Mesh.DestroySafely();
                kv.Value.Vertices.Dispose();
                kv.Value.Opacity.Dispose();
                kv.Value.Indices.Dispose();
            }
            _slots.Clear();
            _layerNodes.Clear();
        }
    }

    /// <summary>D1's slot dictionary key: <c>(TileKey, Slot, Kind)</c>. <c>TileKey</c> is the tile dimension
    /// (stable across batch rebuilds); <c>Slot</c> is the <c>SymbolRenderLayer</c> material slot (== the
    /// layer's <c>DrawIndex</c>); <c>Kind</c> discriminates text vs icon (two separate meshes/materials per
    /// tile+slot). A struct (not a tuple) so <see cref="Dictionary{TKey,TValue}"/> hashing avoids boxing.</summary>
    internal readonly struct WorldLabelKey : IEquatable<WorldLabelKey>
    {
        public readonly long TileKey;
        public readonly int Slot;
        public readonly LabelKind Kind;

        public WorldLabelKey(long tileKey, int slot, LabelKind kind)
        {
            TileKey = tileKey;
            Slot = slot;
            Kind = kind;
        }

        public bool Equals(WorldLabelKey other) => TileKey == other.TileKey && Slot == other.Slot && Kind == other.Kind;
        public override bool Equals(object obj) => obj is WorldLabelKey other && Equals(other);

        // Manual combine (mirrors TileManager.LoadedTileKey's identical pattern) rather than
        // System.HashCode.Combine — codebase convention for a small blittable key struct.
        public override int GetHashCode()
        {
            unchecked
            {
                int h = TileKey.GetHashCode();
                h = h * 31 + Slot;
                h = h * 31 + (int)Kind;
                return h;
            }
        }
    }
}
