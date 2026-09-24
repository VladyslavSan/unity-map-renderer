// Namespace-collision guard (see GlyphAtlasTexture.cs's header): TOP-LEVEL `using Unity.Mathematics;` and
// unqualified types, never an inline `Unity.Mathematics.X`.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Pool;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// The world-anchored symbol renderer: one mesh per <c>(TileKey, Slot, Kind)</c>, under its own
    /// <see cref="SceneTileTree"/> (root → tile → symbol layer → text/icon). It is not an
    /// <see cref="Backend.ITileRenderBackend"/>, which has no op for a per-frame Opacity rewrite. Per frame:
    /// <see cref="BeginFrame"/> clears, <see cref="Emit"/> appends, <see cref="EndFrame"/> builds, hides and
    /// reclaims. Main-thread only.
    /// </summary>
    internal sealed class WorldSymbolRenderer : VerifiedDisposable
    {
        // A slot idle for this many consecutive EndFrames (no content, nothing presented) is torn down —
        // roughly a second of frames at 60fps. A reappearing key just lazily re-creates its slot.
        private const int IdleReclaimFrames = 60;

        // WorldBillboardVertex.AlignFlags bit1: rotate Offset by the projected Tangent. Curved sets only this
        // bit, and the shader then ignores bit0 (map-bearing), so line placement ignores text-rotation-alignment.
        private const float AlongLineAlignFlag = 2f;

        // WorldBillboardVertex.AlignFlags bit2: Offset is world metres, applied in the anchor's ground plane
        // before projection (Shaders/Map/Symbol/SymbolWorldPitchAlign.hlsl).
        private const float MapPitchAlignFlag = 4f;

        private sealed class Slot
        {
            public Mesh Mesh;
            public MeshNode Node; // lazy — rented on the first visible EndFrame
            public NativeList<WorldBillboardVertex> Vertices;
            public NativeList<float> Opacity;
            public NativeList<int> Indices;
            public double3 TileOriginRender;
            public int IdleFrames;
        }

        // The per-symbol-layer node, keyed by (tile, DrawIndex), so a layer's text and icon are siblings. It
        // is the tree level below the tile container, which SceneTileTree does not own.
        private struct LayerNodeRec
        {
            public GameObject Go;
            public int        ChildCount; // text/icon children; node dies when this hits 0
        }

        private readonly struct LayerNodeKey : IEquatable<LayerNodeKey>
        {
            public TileId TileId { get; init; }
            public int    Slot   { get; init; }

            public bool Equals(LayerNodeKey other) => TileId.Equals(other.TileId) && Slot == other.Slot;

            public override int GetHashCode()
            {
                unchecked
                {
                    return TileId.GetHashCode() * 31 + Slot;
                }
            }
        }

        // Hierarchy names for the symbol tree. Non-obvious why: WorldSymbolGroupingTests asserts these as
        // literals, because a test that reads the same constant compares a value to itself.
        private const string TreeRootName  = "Map Symbols";
        private const string TextChildName = "text";

        private const string IconChildName = "icon";

        // Fallback layer-node name when a slot's style layer has no id — mirrors
        // GameObjects.TileRenderer.AddTileLayer's identical fallback.
        private const string LayerNodeFallbackName = "symbol-";

        // The symbol path's own root→tile tree, the same shape whichever backend draws tile fills. Internal so
        // tests read NodeCount, the live tile count; _tree.Root.childCount also holds the pool node.
        internal readonly SceneTileTree                          _tree       = new(TreeRootName);
        private readonly Dictionary<LayerNodeKey, LayerNodeRec> _layerNodes = new();

        private readonly Dictionary<WorldSymbolKey, Slot> _slots = new();

        // Recycled scene nodes: a pooled leaf keeps its MeshFilter and MeshRenderer, so a rent is a reparent.
        // Text and icon pools are separate so a rent never renames. Released nodes park under the inactive
        // _poolRoot, a child of the tree root. Non-obvious why: SetParent(null) makes an active scene-root object.
        private const string PoolRootName = "(node pool)";

        private readonly GameObject             _poolRoot;
        private readonly ObjectPool<MeshNode>   _textChildPool;
        private readonly ObjectPool<MeshNode>   _iconChildPool;
        private readonly ObjectPool<GameObject> _layerNodePool;

        internal WorldSymbolRenderer()
        {
            _poolRoot = new GameObject(PoolRootName) { hideFlags = HideFlags.DontSave };
            _poolRoot.transform.SetParent(_tree.Root, worldPositionStays: false);
            _poolRoot.SetActive(false);

            _textChildPool = NewLeafPool(TextChildName);
            _iconChildPool = NewLeafPool(IconChildName);
            _layerNodePool = NewNodePool(() =>
                new GameObject(LayerNodeFallbackName) { hideFlags = HideFlags.DontSave });
        }

        /// <summary>One detached leaf pool. <see cref="MeshNode.Release"/> drops the tenancy's mesh/material
        /// and disables the renderer before detaching — a detached object stays ACTIVE, so that disable is
        /// what stops a parked leaf drawing.</summary>
        private ObjectPool<MeshNode> NewLeafPool(string name)
            => new ObjectPool<MeshNode>(
                createFunc: () => NewLeaf(name),
                actionOnGet: null, // EnsureChild attaches — only it knows the layer node
                actionOnRelease: node =>
                {
                    node.Release();
                    node.Transform.SetParent(_poolRoot.transform, worldPositionStays: false);
                },
                actionOnDestroy: node => node.Dispose(),
                collectionCheck: true, // a double-release would hand one node to two slots
                defaultCapacity: 32,
                maxSize: 512);

        /// <summary>A leaf's settings, applied once at creation: DontSave, and no shadows. Non-obvious why: a
        /// symbol is a billboard above the ground, so a cast shadow is a floating dark quad and a received one
        /// darkens the glyphs. <c>Style.IRenderLayer.CastShadows</c> holds the tile-geometry half.</summary>
        private static MeshNode NewLeaf(string name)
        {
            var node = new MeshNode(name);
            node.GameObject.hideFlags        = HideFlags.DontSave;
            node.Renderer.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            node.Renderer.receiveShadows     = false;
            node.Renderer.enabled            = false; // EndFrame enables once a material resolves
            return node;
        }

        /// <summary>One detached layer-node pool (bare GameObjects — no mesh, no renderer).</summary>
        private ObjectPool<GameObject> NewNodePool(Func<GameObject> create)
            => new ObjectPool<GameObject>(
                createFunc: create,
                actionOnGet: null, // each rent site reparents — only it knows the parent
                actionOnRelease: go => go.transform.SetParent(_poolRoot.transform, worldPositionStays: false),
                actionOnDestroy: go => go.DestroySafely(),
                collectionCheck: true, // a double-release would hand one node to two slots
                defaultCapacity: 32,
                maxSize: 512);

        // Reused reclaim-sweep scratch (cleared each EndFrame, never reallocated in steady state).
        private readonly List<WorldSymbolKey> _reclaimKeys = new();

        /// <summary>Profiler marker name constants (SSOT) for this type's per-frame work — referenced by the
        /// <see cref="ProfilerMarker"/> field below and by <c>ProfilerMarkerTests</c>.</summary>
        internal static class ProfilerMarkerNames
        {
            internal const string EndFrame = "MapRenderer.Symbol.EndFrame";
        }

        // Brackets the whole EndFrame body (mesh rebuild + per-slot SetTexture/SetVector + _tree.Rebuild +
        // idle reclaim) so its cost is visible in the Profiler rather than folded into SymbolTick's self-time.
        private static readonly ProfilerMarker PmEndFrame =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.EndFrame);

        /// <summary>Clears every live slot's accumulators for a fresh emit pass. Dictionary + slots persist
        /// (no alloc in steady state).</summary>
        public void BeginFrame()
        {
            foreach (KeyValuePair<WorldSymbolKey, Slot> kv in _slots)
            {
                kv.Value.Vertices.Clear();
                kv.Value.Opacity.Clear();
                kv.Value.Indices.Clear();
            }
        }

        /// <summary>
        /// Appends <paramref name="emit"/>'s staged glyph quads into its <c>(TileKey, Slot, AtlasKind)</c> slot,
        /// created on first use, and returns the symbol's quad count; a halo run does not change that count.
        /// A visible <c>text-halo-*</c> writes the run twice, halo first, as contiguous blocks of one index buffer.
        /// Non-obvious why: the whole symbol's halo then draws behind all of its text, however its glyphs overlap.
        /// </summary>
        /// <param name="haloDevicePixelRatio">Scales the halo width and blur from logical px into the device px
        /// the SDF shader measures in; read per Tick, so a dpr change needs no re-bake.</param>
        public int Emit(in CandidateEmit emit, NativeArray<PlacedQuad> quads, float fadeOpacity,
            float haloDevicePixelRatio)
        {
            var key = new WorldSymbolKey(emit.TileKey, emit.Slot, emit.AtlasKind);
            if (!_slots.TryGetValue(key, out Slot slot))
            {
                slot = new Slot
                {
                    Mesh = new Mesh { name = $"WorldSymbolRenderer_Mesh_{emit.TileKey}_{emit.Slot}_{emit.AtlasKind}" },
                    Vertices = new NativeList<WorldBillboardVertex>(Allocator.Persistent),
                    Opacity = new NativeList<float>(Allocator.Persistent),
                    Indices = new NativeList<int>(Allocator.Persistent),
                };
                slot.Mesh.MarkDynamic();
                _slots[key] = slot;
            }

            slot.TileOriginRender = emit.TileOriginRender;

            int quadCount = emit.QuadCount;
            if (quadCount == 0) return 0;

            // One ResizeUninitialized per stream for the whole quad run, then writes through array views. Icons,
            // a zero-width halo and a transparent halo all emit the text run alone.
            bool drawHalo = emit.AtlasKind != SymbolKind.Icon
                         && emit.HaloWidthPx > 0f && emit.HaloColor.w > 0f;
            int  runs     = drawHalo ? 2 : 1;

            int vBaseAll = slot.Vertices.Length;
            int iBaseAll = slot.Indices.Length;
            slot.Vertices.ResizeUninitialized(vBaseAll + quadCount * 4 * runs);
            slot.Opacity.ResizeUninitialized(vBaseAll + quadCount * 4 * runs); // stream-1 opacity is 1:1 with vertices
            slot.Indices.ResizeUninitialized(iBaseAll + quadCount * 6 * runs);
            NativeArray<WorldBillboardVertex> vView = slot.Vertices.AsArray();
            NativeArray<float>                oView = slot.Opacity.AsArray();
            NativeArray<int>                  iView = slot.Indices.AsArray();

            // The halo run OWNS the first index block so it rasterizes first; its VERTICES sit after the text
            // run's, which keeps the text quad at the base of the block it has always been at.
            int haloVertBase  = vBaseAll + quadCount * 4;
            int textIndexBase = iBaseAll + (drawHalo ? quadCount * 6 : 0);
            float  haloOpacityScale = emit.HaloColor.w;
            float3 haloColorLinear  = emit.HaloColor.xyz;
            float2 haloWiden        = new float2(emit.HaloWidthPx, emit.HaloBlurPx) * haloDevicePixelRatio;

            for (int k = 0; k < quadCount; k++)
            {
                PlacedQuad q = quads[emit.QuadStart + k];
                // A curved candidate's anchor, tangent and up are per glyph, and the shader rotates it from the
                // projected Tangent on top of icon-rotate. Point/icon use the per-candidate anchor and baked rotation.
                float3 anchorLocal     = emit.AlongLine ? q.AnchorLocal : emit.AnchorLocal;
                float  rotationRadians = emit.AlongLine ? emit.ExtraRotationRadians : q.RotationRadians;
                float3 tangentLocal    = emit.AlongLine ? q.Tangent : float3.zero;
                float3 surfaceUp       = emit.AlongLine ? q.SurfaceUp : emit.SurfaceUp;

                // One local decides both the corner unit and the shader bit, so they cannot disagree. The AlongLine
                // conjunct is a scope fence: only StageCurvedAnchor writes CornerMetresPerLogicalPixel.
                bool  mapPitchCorners  = emit.AlongLine && emit.CornerMetresPerLogicalPixel > 0f;
                // 1f off map pitch, and `x * 1f` is bitwise identity, so the non-map vertex stream is unchanged.
                float cornerScale      = mapPitchCorners ? emit.CornerMetresPerLogicalPixel : 1f;
                float  alignFlags      = emit.AlongLine
                    ? (mapPitchCorners ? AlongLineAlignFlag + MapPitchAlignFlag : AlongLineAlignFlag)
                    : 0f;
                // The shader has one displacement path, so corners and translate share one unit. Limitation: under
                // map pitch `text-translate` is a world translate, though the spec ties that to `text-translate-anchor`.
                float2 translateDelta  = emit.TranslateDeltaPx * cornerScale;

                BillboardMath.BuildWorldQuad(in q.Quad, in anchorLocal, q.TextSizePx * cornerScale, q.Color.xyz,
                    rotationRadians, in translateDelta, in tangentLocal, in surfaceUp, alignFlags,
                    out WorldBillboardVertex tl, out WorldBillboardVertex tr,
                    out WorldBillboardVertex br, out WorldBillboardVertex bl);

                int v = vBaseAll + k * 4;
                vView[v + 0] = tl;
                vView[v + 1] = tr;
                vView[v + 2] = br;
                vView[v + 3] = bl;

                float opacity = q.Color.w * fadeOpacity; // stream 1 — the fade × the quad's own alpha
                oView[v + 0] = opacity;
                oView[v + 1] = opacity;
                oView[v + 2] = opacity;
                oView[v + 3] = opacity;

                WriteQuadIndices(iView, textIndexBase + k * 6, v);

                if (!drawHalo) continue;

                // The halo copy is the same geometry with its own colour and SDF widening. Limitation: a halo
                // wider than the glyph cell's SDF padding clips at the cell edge.
                tl.ColorRGB = haloColorLinear; tl.SdfWidenPx = haloWiden;
                tr.ColorRGB = haloColorLinear; tr.SdfWidenPx = haloWiden;
                br.ColorRGB = haloColorLinear; br.SdfWidenPx = haloWiden;
                bl.ColorRGB = haloColorLinear; bl.SdfWidenPx = haloWiden;

                int h = haloVertBase + k * 4;
                vView[h + 0] = tl;
                vView[h + 1] = tr;
                vView[h + 2] = br;
                vView[h + 3] = bl;

                // text-halo-color's alpha rides the opacity stream. The quad's alpha already carries
                // text-opacity, so LinearHaloColor does not fold it in again.
                float haloOpacity = opacity * haloOpacityScale;
                oView[h + 0] = haloOpacity;
                oView[h + 1] = haloOpacity;
                oView[h + 2] = haloOpacity;
                oView[h + 3] = haloOpacity;

                WriteQuadIndices(iView, iBaseAll + k * 6, h);
            }

            return quadCount;
        }

        /// <summary>Writes one quad's two triangles at <paramref name="at"/>, over the four corners starting
        /// at <paramref name="corner"/> — the winding <c>BillboardMath.BuildWorldQuad</c> emits.</summary>
        private static void WriteQuadIndices(NativeArray<int> indices, int at, int corner)
        {
            indices[at + 0] = corner + 0;
            indices[at + 1] = corner + 1;
            indices[at + 2] = corner + 2;
            indices[at + 3] = corner + 0;
            indices[at + 4] = corner + 2;
            indices[at + 5] = corner + 3;
        }

        private static readonly int AtlasPropId               = Shader.PropertyToID("_MainTex");
        private static readonly int ScreenParamsLogicalPropId = Shader.PropertyToID("_ScreenParamsLogical");

        /// <summary>
        /// Builds every emitted slot's mesh, attaches it under its tile's layer node, and hides every other slot.
        /// A slot whose material resolves to null stays hidden instead of throwing. It refreshes the tree's
        /// per-tile transforms once per call, and it destroys a slot idle for <see cref="IdleReclaimFrames"/> frames.
        /// </summary>
        /// <param name="frame">This frame's floating-origin scene frame; every tile container rebases on it.</param>
        /// <param name="symbolLayers">Render layers indexed by a slot's layer index, each with its own world
        /// materials. Null or empty resolves every slot to the fallback pair.</param>
        /// <param name="fallbackTextMaterial">The world text material for a slot whose layer supplies none.
        /// Null hides that slot.</param>
        /// <param name="fallbackIconMaterial">The world icon counterpart. Null is routine, because icons are
        /// optional; it hides icon slots.</param>
        /// <param name="atlasTexture">The glyph atlas (Texture2DArray) TEXT world materials bind to.</param>
        /// <param name="spriteTexture">The sprite sheet ICON world materials bind to (may be null — icons optional).</param>
        /// <param name="viewportLogicalPx">This frame's logical viewport size, written to the material's
        /// <c>_ScreenParamsLogical</c> (the vertex shader's px→clip offset scale).</param>
        public void EndFrame(in SceneFrame frame,                IReadOnlyList<SymbolRenderLayer> symbolLayers,
            Material                       fallbackTextMaterial, Material fallbackIconMaterial,
            Texture                        atlasTexture,         Texture spriteTexture, double2 viewportLogicalPx)
        {
            using (PmEndFrame.Auto())
            {
            foreach (KeyValuePair<WorldSymbolKey, Slot> kv in _slots)
            {
                WorldSymbolKey key  = kv.Key;
                Slot          slot = kv.Value;

                // Idle-reclaim tracks emission, not presentation: an emitted slot with no material is a steady
                // state that stays resident and hidden instead of churning every IdleReclaimFrames.
                bool emittedThisFrame = slot.Vertices.Length > 0;
                Material material = emittedThisFrame
                    ? ResolveMaterial(in key, symbolLayers, fallbackTextMaterial, fallbackIconMaterial)
                    : null;

                if (material != null)
                {
                    WorldBillboardMeshBuilder.Build(slot.Vertices.AsArray(), slot.Opacity.AsArray(),
                        slot.Indices.AsArray(), slot.Mesh);

                    // The same per-frame texture and screen-params bind that BuildSlotMesh does for the screen
                    // path. The texture depends only on Kind.
                    Texture texture = key.Kind == SymbolKind.Icon ? spriteTexture : atlasTexture;
                    material.SetTexture(AtlasPropId, texture);
                    material.SetVector(ScreenParamsLogicalPropId,
                        new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                    EnsureChild(in key, slot, symbolLayers); // lazy: tile container → layer node → text/icon child
                    if (slot.Node.Renderer.sharedMaterial != material) slot.Node.Renderer.sharedMaterial = material;
                    slot.Node.Renderer.enabled = true;
                    slot.IdleFrames       = 0;
                }
                else
                {
                    if (slot.Node != null)
                    {
                        slot.Node.Renderer.enabled = false;
                    }

                    slot.IdleFrames = emittedThisFrame ? 0 : slot.IdleFrames + 1;
                }

                if (slot.IdleFrames >= IdleReclaimFrames)
                {
                    _reclaimKeys.Add(key);
                }
            }

            // ONE transform write per tile container per frame — not per slot.
            _tree.Rebuild(in frame);

            for (int i = 0; i < _reclaimKeys.Count; i++)
            {
                WorldSymbolKey key  = _reclaimKeys[i];
                Slot          slot = _slots[key];
                ReleaseChild(in key, slot);
                slot.Mesh.DestroySafely();
                slot.Vertices.Dispose();
                slot.Opacity.Dispose();
                slot.Indices.Dispose();
                _slots.Remove(key);
            }

            _reclaimKeys.Clear();
            }
        }

        /// <summary>Lazily creates <paramref name="slot"/>'s text/icon child GameObject (MeshFilter bound to
        /// the slot's persistent, reused <see cref="Mesh"/> — no rebind afterward, only its buffers change)
        /// under <c>(TileId, key.Slot)</c>'s layer node, itself lazily created under the tile's container.
        /// No-op if the child already exists (steady-state: zero GameObject churn, zero alloc).</summary>
        private void EnsureChild(in WorldSymbolKey key, Slot slot, IReadOnlyList<SymbolRenderLayer> symbolLayers)
        {
            if (slot.Node != null) return;

            TileId    tileId        = SymbolTileKey.Unpack(key.TileKey);
            Transform tileContainer = _tree.GetOrCreateTileNode(tileId, slot.TileOriginRender);

            var layerKey = new LayerNodeKey { TileId = tileId, Slot = key.Slot };
            if (!_layerNodes.TryGetValue(layerKey, out LayerNodeRec layerRec) || layerRec.Go == null)
            {
                // Name after the style layer id ("poi-symbol", …) so the Hierarchy reads like the tile
                // backend's fill/line children — mirrors GameObjects.TileRenderer.AddTileLayer's fallback.
                string layerName = (symbolLayers != null && (uint)key.Slot < (uint)symbolLayers.Count)
                    ? symbolLayers[key.Slot]?.StyleLayer?.Id
                    : null;
                if (string.IsNullOrEmpty(layerName)) layerName = LayerNodeFallbackName + key.Slot;

                GameObject layerGo = _layerNodePool.Get();
                layerGo.transform.SetParent(tileContainer, worldPositionStays: false);
                layerGo.name                     = layerName;
                layerGo.transform.localPosition  = Vector3.zero; // recycled: local TRS is whatever the last
                layerGo.transform.localRotation  = Quaternion.identity; // tenant left (SetParent preserves it)
                layerRec = new LayerNodeRec { Go = layerGo, ChildCount = 0 };
                _tree.AddChild(tileId);
            }

            MeshNode node = (key.Kind == SymbolKind.Icon ? _iconChildPool : _textChildPool).Get();

            // AttachAt resets the previous tenant's local TRS. The child sits at local identity; the container
            // carries the one per-tile placement write.
            node.AttachAt(layerRec.Go.transform, key.Kind == SymbolKind.Icon ? IconChildName : TextChildName);

            slot.Node = node;
            // sharedMesh: rebound per tenancy — the SAME Mesh is then rewritten in place every rebuild.
            node.Filter.sharedMesh = slot.Mesh;

            layerRec.ChildCount++;
            _layerNodes[layerKey] = layerRec;
        }

        /// <summary>Releases <paramref name="slot"/>'s text/icon child (if ever created) from
        /// its layer node — which is destroyed once its last child is gone, releasing the tile container in
        /// turn (via <see cref="SceneTileTree.ReleaseChildFrom"/>) once its last layer node is gone.</summary>
        private void ReleaseChild(in WorldSymbolKey key, Slot slot)
        {
            if (slot.Node == null)
            {
                // never presented — nothing to tear down
                return;
            }

            (key.Kind == SymbolKind.Icon ? _iconChildPool : _textChildPool).Release(slot.Node);
            slot.Node = null;

            TileId tileId   = SymbolTileKey.Unpack(key.TileKey);
            var    layerKey = new LayerNodeKey { TileId = tileId, Slot = key.Slot };
            if (!_layerNodes.TryGetValue(layerKey, out LayerNodeRec layerRec)) return;

            layerRec.ChildCount--;
            if (layerRec.ChildCount <= 0)
            {
                // Null-guarded: ObjectPool.Release faults on a GameObject destroyed out from under us.
                if (layerRec.Go != null) _layerNodePool.Release(layerRec.Go);
                _layerNodes.Remove(layerKey);
                _tree.ReleaseChildFrom(tileId);
            }
            else
            {
                _layerNodes[layerKey] = layerRec;
            }
        }

        // The per-layer world material, else the fallback; an out-of-range slot resolves to the fallback. Both
        // renderQueues are set at build time (RenderLayerSet.Build, SymbolRenderLayer.Create), so this only selects.
        private static Material ResolveMaterial(in WorldSymbolKey key, IReadOnlyList<SymbolRenderLayer> symbolLayers,
            Material                                             fallbackTextMaterial, Material fallbackIconMaterial)
        {
            SymbolRenderLayer layer = (symbolLayers != null && (uint)key.Slot < (uint)symbolLayers.Count)
                ? symbolLayers[key.Slot]
                : null;

            return key.Kind == SymbolKind.Icon
                ? (layer?.WorldIconMaterial ?? fallbackIconMaterial)
                : (layer?.WorldTextMaterial ?? fallbackTextMaterial);
        }

        // ── Test surface (also used to read fade opacity off the world mesh — see
        //    WorldMeshReadback) ──────────────────────────────────────────────────────────────────────────

        /// <summary>The mesh bound to <c>(tileKey, slot, kind)</c>'s slot, or null if no such slot exists yet.
        /// Returns the mesh regardless of whether the slot is currently VISIBLE (a hidden slot still holds
        /// its last-built content) — see <see cref="IsSlotVisible"/> for the presenter's show/hide state.</summary>
        internal bool TryGetSlotMesh(long tileKey, int slot, SymbolKind kind, out Mesh mesh)
        {
            if (_slots.TryGetValue(new WorldSymbolKey(tileKey, slot, kind), out Slot s))
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
        internal bool IsSlotVisible(long tileKey, int slot, SymbolKind kind)
        {
            return _slots.TryGetValue(new WorldSymbolKey(tileKey, slot, kind), out Slot s)
                   && s.Node != null && s.Node.Renderer.enabled;
        }

        /// <summary>The symbol tree's root transform ("Map Symbols"). Test surface — the grouping tooth reads
        /// the live Hierarchy through it (root → per-tile container → per-symbol-layer node → text/icon
        /// sibling children).</summary>
        internal Transform TreeRoot => _tree.Root;

        /// <summary>The text/icon CHILD transform bound to <c>(tileKey, slot, kind)</c>'s slot, or null if
        /// never presented. Test surface — the grouping tooth walks <c>.parent</c> (the layer node) and
        /// <c>.parent.parent</c> (the tile container) from here, and asserts this transform itself sits at
        /// LOCAL identity (the container carries the ONE per-tile placement write, not this child).</summary>
        internal Transform GetSlotTransform(long tileKey, int slot, SymbolKind kind)
            => _slots.TryGetValue(new WorldSymbolKey(tileKey, slot, kind), out Slot s) && s.Node != null
                ? s.Node.Transform
                : null;

        /// <summary>Destroys every GameObject in the shared tree (containers, layer nodes, text/icon
        /// children — one <see cref="SceneTileTree.Dispose"/> call, root-down) BEFORE destroying each slot's
        /// mesh (a MeshRenderer whose sharedMesh was destroyed first logs/renders pink in edit mode), then
        /// disposes every accumulator. Idempotent (an empty dictionary after the first call is a no-op).</summary>
        protected override void DoDispose()
        {
            _tree.Dispose();

            // Clear destroys only each pool's parked objects; the tree dispose above destroyed the live ones.
            // Both must run, or the parked set outlives the renderer.
            _textChildPool.Clear();
            _iconChildPool.Clear();
            _layerNodePool.Clear(); // _poolRoot itself died with the tree root above

            foreach (KeyValuePair<WorldSymbolKey, Slot> kv in _slots)
            {
                // The tree already destroyed this node's GameObject; disposing the WRAPPER is still required
                // — it owns the node and an undisposed one is reported as a leak (MeshNode.DoDispose).
                kv.Value.Node?.Dispose();
                kv.Value.Mesh.DestroySafely();
                kv.Value.Vertices.Dispose();
                kv.Value.Opacity.Dispose();
                kv.Value.Indices.Dispose();
            }

            _slots.Clear();
            _layerNodes.Clear();
        }
    }

    /// <summary>The slot dictionary key: <c>(TileKey, Slot, Kind)</c>. <c>TileKey</c> is the tile dimension
    /// (stable across batch rebuilds); <c>Slot</c> is the <c>SymbolRenderLayer</c> material slot (== the
    /// layer's <c>DrawIndex</c>); <c>Kind</c> discriminates text vs icon (two separate meshes/materials per
    /// tile+slot). A struct (not a tuple) so <see cref="Dictionary{TKey,TValue}"/> hashing avoids boxing.</summary>
    internal readonly struct WorldSymbolKey : IEquatable<WorldSymbolKey>
    {
        public readonly long      TileKey;
        public readonly int       Slot;
        public readonly SymbolKind Kind;

        public WorldSymbolKey(long tileKey, int slot, SymbolKind kind)
        {
            TileKey = tileKey;
            Slot    = slot;
            Kind    = kind;
        }

        public bool Equals(WorldSymbolKey other) => TileKey == other.TileKey && Slot == other.Slot && Kind == other.Kind;
        public override bool Equals(object obj) => obj is WorldSymbolKey other && Equals(other);

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