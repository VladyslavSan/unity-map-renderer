// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file lives in
// MapRenderer.Unity.Text.Placement and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;`
// + unqualified types, never an inline `Unity.Mathematics.X`.

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
    /// The dedicated world-anchored symbol renderer — NOT an <see cref="Backend.ITileRenderBackend"/> (no
    /// mesh-update op, no per-instance registration a symbol's per-frame Opacity rewrite would fit). Owns a
    /// persistent <c>Dictionary&lt;WorldSymbolKey, Slot&gt;</c> keyed by <c>(TileKey, Slot, Kind)</c> — one
    /// mesh per (tile, material slot, text/icon) — plus its OWN <see cref="SceneTileTree"/> ("Map Symbols"
    /// root) so symbols are grouped root → per-tile container → per-symbol-layer node → text/icon sibling
    /// children, the SAME organization the <see cref="Backend.GameObjects.TileRenderer"/> tile backend uses
    /// for fill/line.
    ///
    /// <para><b>Per-frame lifecycle</b> (mirrors <see cref="SymbolPlacementSystem"/>'s per-slot present/hide
    /// discipline): <see cref="BeginFrame"/> clears every live slot's native accumulators (dictionary +
    /// slots persist — no alloc); <see cref="Emit"/> appends a surviving point/icon/curved candidate's glyph
    /// corners into its slot (created lazily on first use — warm-up-only alloc); <see cref="EndFrame"/>
    /// builds every non-empty slot's mesh, lazily attaches it to a text/icon child under its tile's
    /// per-symbol-layer node, hides the rest, refreshes the tree's per-tile transform ONCE (not per slot),
    /// and reclaims a slot idle for <see cref="IdleReclaimFrames"/> consecutive frames (self-contained —
    /// reads only what <see cref="Emit"/> handed it, never a batch tile array, so the coverage cull's
    /// <c>-1</c> degenerate can never reach here).</para>
    ///
    /// <para>Main-thread only (touches <see cref="Mesh"/>/<see cref="GameObject"/>, mirrors every other
    /// GPU-resource boundary in this codebase).</para>
    /// </summary>
    internal sealed class WorldSymbolRenderer : VerifiedDisposable
    {
        // A slot idle for this many consecutive EndFrames (no content, nothing presented) is torn down —
        // roughly a second of frames at 60fps. A reappearing key just lazily re-creates its slot.
        private const int IdleReclaimFrames = 60;

        // WorldBillboardVertex.AlignFlags bit1 — "rotate Offset by the projected Tangent."
        // Point/icon leave bit0 (map-bearing) as their only bit; curved sets ONLY
        // this one (bit1 set ⇒ the shader ignores bit0 — MapLibre line placement ignores
        // text-rotation-alignment).
        private const float AlongLineAlignFlag = 2f;

        // WorldBillboardVertex.AlignFlags bit2 — "Offset is WORLD METRES; displace the anchor in its
        // own ground plane BEFORE projection" (Shaders/Map/Symbol/SymbolWorldPitchAlign.hlsl). bit0 is
        // map-bearing and bit1 is along-line, so bit2 is the next free one.
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

        // The per-symbol-layer node under a tile container that a slot's text/icon child hangs off. Keyed by
        // (tile, DrawIndex) so a tile's text and icon children for the SAME layer are siblings under ONE node
        // — the layer-node level of the root→tile→layer tree that SceneTileTree itself only owns one level
        // of (root→tile); this dictionary is the thin second level specific to the symbol path.
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

        // Hierarchy names for the symbol tree's three levels. Const so the two leaf names are picked from a
        // named pair rather than a literal ternary at the construction site. NOT shared with
        // WorldSymbolGroupingTests, which asserts these strings literally — pointing the test at the same
        // constant would make it compare a value to itself and stop pinning the name at all.
        private const string TreeRootName  = "Map Symbols";
        private const string TextChildName = "text";

        private const string IconChildName = "icon";

        // Fallback layer-node name when a slot's style layer has no id — mirrors
        // GameObjects.TileRenderer.AddTileLayer's identical fallback.
        private const string LayerNodeFallbackName = "symbol-";

        // The symbol path's own root→tile-container tree (mirrors the GameObjects tile backend's
        // "MapTiles (GameObject backend)" tree: symbols always maintain their own
        // tree so the organization is identical no matter which backend draws tile fills).
        // internal (not private): SymbolPlacementSystemTestExtensions reads NodeCount off it — the LIVE
        // tile-container count, which _tree.Root.childCount is not (it also holds the pool node).
        internal readonly SceneTileTree                          _tree       = new(TreeRootName);
        private readonly Dictionary<LayerNodeKey, LayerNodeRec> _layerNodes = new();

        private readonly Dictionary<WorldSymbolKey, Slot> _slots = new();

        // Recycled scene nodes. EnsureChild early-returns for a live slot, so a still camera creates nothing —
        // but a ZOOM STEP replaces the whole cover at once and a pan churns tiles continuously, and each
        // entering tile pays one layer node per symbol layer plus one leaf per (layer, kind). The leaves carry
        // two AddComponent calls each (MeshFilter + MeshRenderer), which is the real cost here and the reason
        // renting beats reallocating: a pooled leaf keeps its components, so a rent is a reparent.
        //
        // Text and icon get SEPARATE pools purely so a rent never has to rename ("text"/"icon" is the only way
        // the two differ). Layer nodes are named per style layer, so that pool does rename on rent.
        //
        // A released node parks under _poolRoot, which is INACTIVE. ObjectPool is scene-unaware — it only
        // files the reference away — so without a reparent a released leaf stays under its layer node, both
        // drawing and keeping the node looking occupied. SetParent(null) is NOT the alternative: it promotes
        // the leaf to a SCENE-ROOT object, live in the Hierarchy and still active. _poolRoot is a CHILD of
        // the symbol tree root, not a second scene root, so everything symbol-related stays under one
        // top-level object — which means the tree root's child count is live tiles PLUS this node.
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

        /// <summary>A leaf's per-node settings, applied once at CREATION (not per rent): symbols neither cast
        /// nor receive shadows, and DontSave keeps these out of the saved scene. MeshNode decides none of
        /// this — see its header.
        ///
        /// <para>The shadow flags are a DECISION, not an oversight, and unlike tile geometry they are
        /// per-node rather than per-rent because every leaf answers the same way: a symbol is a camera-facing
        /// billboard held a fixed offset above the ground, so a cast shadow would be a floating dark quad and
        /// a received one would darken the glyphs it is there to make legible. See
        /// <c>Style.IRenderLayer.CastShadows</c> for the tile-geometry half of the same decision.</para></summary>
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
        /// Appends <paramref name="emit"/>'s already-staged glyph quads (<paramref name="quads"/>, the SAME
        /// pool <c>SymbolPlacementSystem</c>'s screen path reads — no new staged-quad stream) into the
        /// slot for <c>(emit.TileKey, emit.Slot, emit.AtlasKind)</c>, creating it lazily on first use.
        /// Returns the quad count emitted (for <c>LastQuadCount</c>) — the LABEL's quad count, which a halo
        /// run does not change. Reads the world payload ONLY from <paramref name="emit"/> — never a batch
        /// tile array.
        ///
        /// <para><b>A visible <c>text-halo-*</c> emits this label's glyph run twice</b> — the halo copy
        /// first, then the text copy. The two runs are contiguous blocks of ONE index buffer, so the whole
        /// label's halo is behind the whole label's text however its glyphs overlap each other. Grouping is
        /// per CALL, i.e. per label (one <see cref="CandidateEmit"/> carries every glyph of every line a
        /// label shapes to): two labels that overlap are still ordered only by which emitted first, which
        /// collision already keeps from mattering.</para>
        /// </summary>
        /// <param name="haloDevicePixelRatio">Scales <see cref="CandidateEmit.HaloWidthPx"/>/
        /// <see cref="CandidateEmit.HaloBlurPx"/> from the style's logical px into the device px the SDF
        /// shader measures in. Read per Tick against the live ratio, so a dpr change needs no re-bake.</param>
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

            // Batch the stream growth: ONE ResizeUninitialized per stream for this candidate's whole quad run,
            // then write through array views — instead of 14 bounds/safety-checked NativeList.Add per quad. The
            // written values and index bases are byte-identical to the per-Add form (only the store mechanism
            // changes); the billboard math below is untouched. Trims Editor collections-check overhead; the
            // release delta is negligible (the real per-quad cost is BillboardMath.BuildWorldQuad, unchanged).
            // Icons have no `icon-halo-*` path, and a zero-width or fully transparent halo is the spec
            // default — all three emit the text run alone, so a haloless layer pays nothing.
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
                // A curved candidate's world anchor/tangent are PER-GLYPH (on the quad itself), not
                // per-candidate (emit.AnchorLocal is a point symbol's single shared anchor) — and its
                // corners carry no per-quad screen rotation, being rotated instead by the shader from the
                // projected Tangent. Point/icon keep the per-candidate anchor + baked screen rotation +
                // tangentLocal=0/alignFlags=0; the shader's tangent branch is never taken for them.
                // The along-line arm carries icon-rotate, the one constant the shader's tangent rotation
                // must compose ON TOP of. Curved text leaves ExtraRotationRadians at 0.
                float3 anchorLocal     = emit.AlongLine ? q.AnchorLocal : emit.AnchorLocal;
                float  rotationRadians = emit.AlongLine ? emit.ExtraRotationRadians : q.RotationRadians;
                float3 tangentLocal    = emit.AlongLine ? q.Tangent : float3.zero;
                // Same per-glyph-vs-per-candidate selector as tangentLocal above — a curved candidate's
                // up rides the quad (per-glyph, sampled along the path); point/icon carry the candidate's
                // single anchor up.
                float3 surfaceUp       = emit.AlongLine ? q.SurfaceUp : emit.SurfaceUp;

                // ONE local decides BOTH the corner unit and the shader bit, so a state where the offsets
                // are metres while the shader believes they are pixels — or the reverse — is not expressible.
                // The emit.AlongLine conjunct is a SCOPE FENCE, not a defensive check: SymbolStagingMath writes
                // CornerMetresPerLogicalPixel only in StageCurvedAnchor, so it is already 0 on every point
                // emit. It marks the one place a later change would cross into point/icon map-pitch.
                bool  mapPitchCorners  = emit.AlongLine && emit.CornerMetresPerLogicalPixel > 0f;
                // On every non-map path this is the literal 1f, and `x * 1f` is bitwise identity for every
                // finite float and for ±0/±Inf/NaN payloads alike, so the non-map vertex stream is
                // unchanged.
                float cornerScale      = mapPitchCorners ? emit.CornerMetresPerLogicalPixel : 1f;
                float  alignFlags      = emit.AlongLine
                    ? (mapPitchCorners ? AlongLineAlignFlag + MapPitchAlignFlag : AlongLineAlignFlag)
                    : 0f;
                // ONE unit for the whole `off`: the shader has a single displacement path, so the corners and
                // the translate delta must share whichever unit is in force. The consequence: under map pitch
                // `text-translate` becomes a WORLD translate rather than a screen one. The spec's proper
                // control for that is
                // `text-translate-anchor`, not `text-pitch-alignment`, so this is a recorded limitation and
                // not a design position; it is inert on all 7 shipped line-symbol layers, none of which set
                // a translate (the delta is exactly float2.zero there, and 0 · k == 0).
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

                // The halo copy is the SAME geometry — only the colour and the SDF widening differ, which is
                // all "halo" means to the shader. So a halo wider than the glyph cell's SDF padding clips at
                // the cell edge, exactly as it did when one fragment computed both.
                tl.ColorRGB = haloColorLinear; tl.SdfWidenPx = haloWiden;
                tr.ColorRGB = haloColorLinear; tr.SdfWidenPx = haloWiden;
                br.ColorRGB = haloColorLinear; br.SdfWidenPx = haloWiden;
                bl.ColorRGB = haloColorLinear; bl.SdfWidenPx = haloWiden;

                int h = haloVertBase + k * 4;
                vView[h + 0] = tl;
                vView[h + 1] = tr;
                vView[h + 2] = br;
                vView[h + 3] = bl;

                // text-halo-color's own alpha rides the opacity stream, the one channel the shader
                // multiplies coverage by. The quad's alpha already carries text-opacity, which is why
                // LinearHaloColor does not fold it in again.
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
        /// Builds every non-empty slot's mesh and lazily attaches it to a text/icon child under its tile's
        /// per-symbol-layer node in the shared <see cref="_tree"/> (root → tile container → layer node →
        /// text/icon sibling children), resolving its draw material from
        /// <paramref name="symbolLayers"/> or the <paramref name="fallbackTextMaterial"/>/
        /// <paramref name="fallbackIconMaterial"/> pair; hides every other live slot (idle-frame
        /// reclamation destroys one idle <see cref="IdleReclaimFrames"/> consecutive frames). A
        /// resolved-null material (an icon slot with no <c>WorldIconMaterial</c> configured)
        /// hides that slot instead of throwing. Refreshes the tree's per-tile transform exactly ONCE per
        /// call via <see cref="SceneTileTree.Rebuild"/> — not once per slot.
        /// </summary>
        /// <param name="frame">This frame's floating-origin scene frame — the ONE
        /// <see cref="SceneTileTree.Rebuild"/> below rebases every tile container against it.</param>
        /// <param name="symbolLayers">Per-symbol-layer render layers, indexed by a slot's layer index; each
        /// may own its own world text/icon material. Null / empty → every slot resolves to the
        /// fallback pair below.</param>
        /// <param name="fallbackTextMaterial">The world TEXT material used for any slot whose layer supplies
        /// none (and for every slot when <paramref name="symbolLayers"/> is null/empty). Null ⇒ that slot
        /// stays hidden rather than throwing.</param>
        /// <param name="fallbackIconMaterial">The world ICON counterpart. Null is the routine case — icons
        /// are optional, so an unconfigured icon material hides icon slots instead of faulting.</param>
        /// <param name="atlasTexture">The glyph atlas (Texture2DArray) TEXT world materials bind to.</param>
        /// <param name="spriteTexture">The sprite sheet ICON world materials bind to (may be null — icons optional).</param>
        /// <param name="viewportLogicalPx">This frame's logical viewport size — refreshes the resolved
        /// material's <c>_ScreenParamsLogical</c> (the vertex shader's px→clip offset scale), mirrors
        /// <c>SymbolPlacementSystem.BuildSlotMesh</c>'s identical per-frame refresh.</param>
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

                // Idle-reclaim tracks whether this key was EMITTED to this frame (self-contained
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
                    WorldBillboardMeshBuilder.Build(slot.Vertices.AsArray(), slot.Opacity.AsArray(),
                        slot.Indices.AsArray(), slot.Mesh);

                    // The texture/screen-params refresh BuildSlotMesh does for the screen path — the world
                    // material needs the SAME per-frame bind (atlas/sprite texture never changes per-slot,
                    // only per-Kind, so this is a straight compare-assign-free SetTexture/SetVector).
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

            // AttachAt resets local TRS: a recycled node carries the previous tenant's, and the grouping
            // tooth asserts this child sits at LOCAL identity — the container carries the one per-tile
            // placement write, never this child.
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

        // The per-layer world material (WorldTextMaterial/WorldIconMaterial), else the demo fallback —
        // mirrors SymbolPlacementSystem.ResolveSlotMaterial/ResolveIconMaterial. A slot index out of range
        // (or no symbolLayers — the demo path) resolves straight to the fallback, never throws. Both world
        // materials' renderQueue are written at Build/Create time (RenderLayerSet.Build for text via
        // SymbolRenderLayer.Material, SymbolRenderLayer.Create for icon), so this is pure selection —
        // no per-frame queue sync.
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

            // After the tree: Clear only destroys each pool's PARKED objects, and a live node is in the tree,
            // not the pool — the line above already destroyed those. Both halves have to run or the parked set
            // outlives the renderer.
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