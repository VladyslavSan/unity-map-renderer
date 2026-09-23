using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.View;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Backend.BRG
{
    /// <summary>
    /// BRG render backend (internal, IDisposable).
    ///
    /// Draws tile-layer meshes via <see cref="BatchRendererGroup"/> instead of per-layer GameObjects.
    /// One <see cref="BatchDrawCommand"/> per (tile,layer) mesh, sharing one batch and one
    /// <see cref="GraphicsBuffer"/> for all per-instance data. Materials are registered once from the
    /// <see cref="RenderLayerSet"/>; draw commands are emitted in ascending renderQueue order so the
    /// painter's-algorithm layer order is honoured (NOT GameObject child order).
    ///
    /// Per-instance buffer layout: see <see cref="MapInstanceData"/> (the single source of truth for
    /// the SoA layout). <see cref="InstancePropPlan.BuildFromStruct{T}"/> reflects it ONCE at
    /// construction to build the cached packing plan; per-frame pack is reflection-free.
    ///
    /// Clean-room: design follows the MapLibre Style Spec and Unity BRG documentation.
    /// </summary>
    internal sealed class TileRenderer : VerifiedDisposable, ITileRenderBackend
    {
        // ── Reflected-once packing plan ───────────────────────────────────────────────────────
        // Built once at construction from MapInstanceData. Per-frame pack indexes MaterialEntries[]
        // by index only — no reflection, no boxing, no LINQ, no managed allocation.

        // internal, not private: the test assembly's observability extensions read it (see
        // BrgTileRendererTestExtensions) — the sanctioned footprint for test-only accessors.
        internal readonly InstancePropPlan _plan = InstancePropPlan.BuildFromStruct<MapInstanceData>();

        // ── Per-draw-item record ─────────────────────────────────────────────────────────────

        internal struct DrawItem
        {
            public BatchMeshID     MeshId;
            public BatchMaterialID MatId;
            public int             LayerRenderQueue;   // material.renderQueue — sort key
            public double3         TileOriginRender;   // SW-corner projected render origin (Mercator: (mercX,0,mercZ))
            public int             MaterialIndex;      // index into _layerMaterials for prop readback
        }

        // ── BRG state ─────────────────────────────────────────────────────────────────────────

        private BatchRendererGroup _brg;
        internal GraphicsBuffer    _instanceBuffer;
        private BatchID            _batchId;
        private bool               _batchRegistered;

        // The instance count that was used when the batch was last registered.
        // SoA byte offsets depend on N, so batch must be re-registered when N changes.
        private int _lastRegisteredCount = 0;

        // Test observability: counts how many times OnPerformCulling has been called.
        // Reset to 0 by the caller if needed. Public so tests can probe without subclassing.
        internal int CullingCallCount;

        // Handle → DrawItem  (stable for handle-based removal API).
        internal readonly Dictionary<int, DrawItem> _items = new Dictionary<int, DrawItem>(64);
        private int _nextHandle;

        // Per-layer materials in SLOT order (index == materialIndex == DrawIndex); draw order rides each
        // DrawItem's LayerRenderQueue. mat is referenced, not owned — RenderLayerSet disposes them.
        private readonly List<(BatchMaterialID id, Material mat)> _layerMaterials
            = new List<(BatchMaterialID, Material)>(16);

        // Mesh → BatchMeshID (de-dup; each tile-layer has a unique Mesh).
        private readonly Dictionary<Mesh, BatchMeshID> _meshIds
            = new Dictionary<Mesh, BatchMeshID>(64);

        // Sorted draw list (ascending renderQueue) rebuilt in Rebuild. Cleared + filled each call
        // from _items — no allocations in steady state.
        internal readonly List<(int renderQueue, int handle)> _sortedItems
            = new List<(int, int)>(64);

        // Reusable scratch holding the compacted emit order (indices into _sortedItems that are still live
        // in _items) for OnPerformCulling. Grown on demand, reused each cull → no per-frame managed alloc.
        private readonly List<int> _emitList = new List<int>(64);

        // Reusable scratch holding the run-length-grouped draw ranges for OnPerformCulling (one per run of
        // consecutive same-shadow-mode commands). Reused each cull → no per-frame managed alloc.
        private readonly List<BatchDrawRange> _drawRanges = new List<BatchDrawRange>(8);

        // Per-layer shadow-cast declaration, parallel to _layerMaterials — IRenderLayer.CastShadows, carried
        // verbatim from TileManager.LayerShadowModes. Absent or short ⇒ Off, identically in all three backends.
        private readonly List<ShadowCastingMode> _layerShadowModes = new List<ShadowCastingMode>();

        // Per-layer draw gate (ITileRenderBackend.SetLayerVisible), parallel to _layerMaterials. True ⇒ this
        // slot emits a draw command. Absent or short ⇒ visible, identically in all three backends.
        private readonly List<bool> _layerVisible = new List<bool>();

        // CPU-side instance data (SoA layout). Grown on demand, never shrunk — no per-frame alloc.
        internal float[] _cpuBuffer = Array.Empty<float>();

        // Generous bounds so minimal culling never culls tiles.
        private static readonly Bounds GenBounds = new Bounds(
            Vector3.zero, new Vector3(100_000_000f, 100_000_000f, 100_000_000f));

        // ── Construction ──────────────────────────────────────────────────────────────────────

        /// <summary>Constructs the BRG and registers each layer material — the one ordered, FULL-WIDTH
        /// per-layer list in SLOT order (<c>index == materialIndex == AddTileLayer index ==
        /// <see cref="Style.IRenderLayer.DrawIndex"/></c>). Materials are referenced, not owned. See
        /// `docs/tile-pipeline-design.md` for the null-placeholder note.</summary>
        /// <param name="layerMaterials">The full-width per-layer material list, indexed by slot.</param>
        /// <param name="layerShadowModes">Per-layer shadow declarations; see <see cref="ShadowModeFor"/>.</param>
        public TileRenderer(
            System.Collections.Generic.IReadOnlyList<Material> layerMaterials,
            System.Collections.Generic.IReadOnlyList<ShadowCastingMode> layerShadowModes = null)
        {
            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);

            if (layerShadowModes != null)
                for (int i = 0; i < layerShadowModes.Count; i++) _layerShadowModes.Add(layerShadowModes[i]);

            for (int i = 0; i < layerMaterials.Count; i++)
            {
                var mat = layerMaterials[i];
                if (mat == null) { _layerMaterials.Add((default, null)); continue; } // placeholder — keeps the list full-width aligned
                _layerMaterials.Add((_brg.RegisterMaterial(mat), mat));
            }
        }

        /// <summary>Restyle-time material/queue update — see `docs/tile-pipeline-design.md`.</summary>
        public void SetLayerMaterials(
            System.Collections.Generic.IReadOnlyList<Material> layerMaterials,
            System.Collections.Generic.IReadOnlyList<ShadowCastingMode> layerShadowModes)
        {
            ThrowIfDisposed();

            int oldCount = _layerMaterials.Count;
            for (int i = 0; i < oldCount; i++)
            {
                var (oldId, oldMat) = _layerMaterials[i];
                Material newMat = i < layerMaterials.Count ? layerMaterials[i] : null;
                // Reference-null, NOT `!=` (Unity's fake-null hides a DESTROYED material — see
                // docs/tile-pipeline-design.md's SetLayerMaterials note).
                if (!ReferenceEquals(oldMat, null) && !ReferenceEquals(oldMat, newMat))
                    _brg.UnregisterMaterial(oldId);
            }

            var next = new List<(BatchMaterialID id, Material mat)>(layerMaterials.Count);
            for (int i = 0; i < layerMaterials.Count; i++)
            {
                Material mat    = layerMaterials[i];
                Material oldMat = i < oldCount ? _layerMaterials[i].mat : null;
                if (ReferenceEquals(mat, oldMat)) { next.Add(_layerMaterials[i]); continue; }
                if (mat == null) { next.Add((default, null)); continue; }
                next.Add((_brg.RegisterMaterial(mat), mat));
            }
            _layerMaterials.Clear();
            _layerMaterials.AddRange(next);

            _layerShadowModes.Clear();
            if (layerShadowModes != null)
                for (int i = 0; i < layerShadowModes.Count; i++) _layerShadowModes.Add(layerShadowModes[i]);

            // A retired slot's stale DrawItems are removed here (dict-entry drop only) — see
            // docs/tile-pipeline-design.md for why and how this differs from the other two backends.
            foreach (int handle in new List<int>(_items.Keys))
            {
                DrawItem item = _items[handle];
                Material mat = (uint)item.MaterialIndex < (uint)_layerMaterials.Count
                    ? _layerMaterials[item.MaterialIndex].mat : null;
                if (mat == null) { _items.Remove(handle); continue; }
                item.LayerRenderQueue = mat.renderQueue;
                _items[handle] = item;
            }
        }

        // Read-only queries over this class's state that only tests ask for — DrawItemCount, HasBuffer,
        // FloatsPerInstance, MetadataEntryCount, GetInstanceTranslation, GetInstancePropValue,
        // GetPropSoaOffset, GetEmittedRenderQueues — live in the test assembly; see
        // BrgTileRendererTestExtensions. CullingCallCount stays a field above because this class WRITES
        // it, and ComputeEmitOrder stays because Rebuild calls it.

        /// <summary>
        /// Returns the XZ scene-space bounding box that covers all registered tile instances.
        /// Each tile origin is the translation from the packed O2W buffer; <paramref name="tileSizeWorld"/>
        /// is added to the max to account for the tile's mesh extent beyond its origin.
        /// Used by tests to frame a camera that sees all BRG-rendered tiles (BRG has no child
        /// GameObjects so the standard ComputeChildBounds approach does not apply).
        ///
        /// Returns <c>default(Bounds)</c> if no draw items are registered or the buffer is empty.
        /// </summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld)
        {
            int count = _sortedItems.Count;
            if (count == 0 || _cpuBuffer == null || _cpuBuffer.Length < count * _plan.FloatsPerInstance)
                return new Bounds(Vector3.zero, Vector3.zero);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int si = 0; si < count; si++)
            {
                // Read tile origin from packed O2W buffer: tx = b+9, tz = b+11.
                float px = _cpuBuffer[si * 12 + 9];
                float pz = _cpuBuffer[si * 12 + 11];
                if (px < minX) minX = px;
                if (px + tileSizeWorld > maxX) maxX = px + tileSizeWorld;
                if (pz < minZ) minZ = pz;
                if (pz + tileSizeWorld > maxZ) maxZ = pz + tileSizeWorld;
            }

            if (minX == float.MaxValue) return new Bounds(Vector3.zero, Vector3.zero);
            float cx = (minX + maxX) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;
            return new Bounds(new Vector3(cx, 0f, cz), new Vector3(maxX - minX, 1f, maxZ - minZ));
        }

        // ── Draw item registration ────────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a tile-layer mesh for BRG drawing. Returns a handle for later removal.
        /// <paramref name="materialIndex"/> is the layer's global SLOT (<see cref="Style.IRenderLayer.DrawIndex"/>),
        /// indexing the full-width material list built at construction; non-tile-mesh slots are null and
        /// never receive an AddTileLayer call. <paramref name="tileId"/> is part of the shared
        /// <see cref="ITileRenderBackend"/> contract for the Entities backend's per-tile hierarchy; BRG
        /// draws a flat instance buffer and does not use it.
        /// </summary>
        public int AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId)
        {
            ThrowIfDisposed();
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if ((uint)materialIndex >= (uint)_layerMaterials.Count)
                throw new ArgumentOutOfRangeException(nameof(materialIndex));

            if (!_meshIds.TryGetValue(mesh, out BatchMeshID meshId))
            {
                meshId = _brg.RegisterMesh(mesh);
                _meshIds[mesh] = meshId;
            }

            var (matId, mat) = _layerMaterials[materialIndex];

            int handle = _nextHandle++;
            _items[handle] = new DrawItem
            {
                MeshId           = meshId,
                MatId            = matId,
                LayerRenderQueue = mat.renderQueue,
                TileOriginRender = tileOriginRender,
                MaterialIndex    = materialIndex,
            };
            return handle;
        }

        /// <summary>
        /// Removes a previously registered draw item. The Mesh is NOT unregistered here — the caller
        /// (TileManager) destroys the Mesh asset separately. Idempotent for unknown handles.
        /// </summary>
        public void RemoveItem(int handle)
        {
            if (IsDisposed) return;
            _items.Remove(handle);
        }

        /// <summary>BRG removal is a plain dict remove, so the batch is just the loop — no structural-change
        /// cost to amortise (unlike the Entities backend). See <see cref="ITileRenderBackend.RemoveItems"/>.</summary>
        public void RemoveItems(ReadOnlySpan<int> handles)
        {
            if (IsDisposed) return;
            for (int i = 0; i < handles.Length; i++) _items.Remove(handles[i]);
        }

        // ── Per-frame rebuild ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the instance data buffer from all registered draw items using SoA layout.
        /// Must be called once per frame on the BRG path.
        ///
        /// Recomputes per-instance objectToWorld from <paramref name="frame"/> (the camera-relative
        /// <see cref="SceneFrame"/> — scene origin + rebase rotation) and reads material props back from each
        /// layer material (no per-frame managed allocation in steady state — buffer is grown on demand but
        /// never shrunk).
        ///
        /// SoA (Struct-of-Arrays): all instance O2W first, then all W2O, then all _BaseColor, etc.
        /// Byte offsets in ReRegisterBatch are computed from the current instance count N.
        /// The batch is re-registered whenever N changes (offsets change with N).
        /// </summary>
        public void Rebuild(in SceneFrame frame)
        {
            if (IsDisposed || _items.Count == 0)
            {
                _sortedItems.Clear();
                return;
            }

            int count = _items.Count;

            // Rebuild the sorted draw list (ascending renderQueue = painter's algorithm order).
            _sortedItems.Clear();
            foreach (var kv in _items)
                _sortedItems.Add((kv.Value.LayerRenderQueue, kv.Key));
            _sortedItems.Sort((a, b) => a.renderQueue.CompareTo(b.renderQueue));

            // Grow CPU buffer if needed (no shrink → steady-state no-alloc).
            int floatsNeeded = count * _plan.FloatsPerInstance;
            if (_cpuBuffer.Length < floatsNeeded)
                _cpuBuffer = new float[floatsNeeded];

            // Pack per-instance data in SoA layout.
            // Each property block P occupies floats [Pfx_P*count .. Pfx_P*count + count*stride_P - 1].
            // Instance si's value for property P is at: Pfx_P*count + si*stride_P.

            // ── O2W block (12 floats per instance) ──────────────────────────────────────────
            int o2wBase = _plan.O2WFloatOffset * count; // = 0 * count = 0
            // ── W2O block (12 floats per instance) ──────────────────────────────────────────
            int w2oBase = _plan.W2OFloatOffset * count; // = 12 * count

            // Place each tile in the look-at's local ENU frame. The rebase is a proper (orthonormal)
            // rotation, so O2W = [R | pos] and its RIGID inverse W2O = [Rᵀ | -Rᵀ·pos]. Building both directly
            // from the float3x3 (no quaternion round-trip, no math.inverse) keeps the Mercator
            // identity-rebase case a bit-for-bit translation-only packing (R = I ⇒ pos.y = 0).
            float3x3 rebase  = frame.Rebase;
            float3x3 rebaseT = math.transpose(rebase);

            for (int si = 0; si < count; si++)
            {
                int handle = _sortedItems[si].handle;
                var item   = _items[handle];

                float3   pos = FloatingOrigin.TileToSceneRebased(item.TileOriginRender, frame.SceneOriginRender, rebase);
                float4x4 o2w = new float4x4(rebase,  pos);
                float4x4 w2o = new float4x4(rebaseT, math.mul(rebaseT, -pos));

                PackPackedFloat3x4(_cpuBuffer, o2wBase + si * 12, o2w);
                PackPackedFloat3x4(_cpuBuffer, w2oBase + si * 12, w2o);
            }

            // ── Material property blocks ─────────────────────────────────────────────────────
            for (int si = 0; si < count; si++)
            {
                int handle = _sortedItems[si].handle;
                var item   = _items[handle];
                Material mat = _layerMaterials[item.MaterialIndex].mat;
                PackMaterialProps(si, count, mat);
            }

            // Ensure GPU buffer is large enough and re-register if count changed, then upload.
            EnsureBuffer(count);
            _instanceBuffer.SetData(_cpuBuffer, 0, 0, floatsNeeded);
        }

        /// <summary>
        /// Packs all DOTS-instanced material properties from <paramref name="mat"/> into the CPU SoA
        /// buffer for instance <paramref name="index"/> out of <paramref name="count"/> total instances.
        ///
        /// Driven by the reflected <see cref="InstancePropPlan"/> — no hand-maintained property list.
        /// Allocation-free: array indexed by <c>for</c>, int-id Material accessors (no string lookups),
        /// value-type defaults. Safe to call 100× per frame without triggering GC gen-0.
        /// </summary>
        private void PackMaterialProps(int index, int count, Material mat)
        {
            for (int e = 0; e < _plan.MaterialEntries.Length; e++)
            {
                InstancePropEntry entry   = _plan.MaterialEntries[e];
                int               soaBase = entry.SoaFloatOffset * count;

                switch (entry.Kind)
                {
                    case PropKind.Color:
                    {
                        Color c = mat.HasProperty(entry.PropId)
                            ? mat.GetColor(entry.PropId)
                            : new Color(entry.Default.x, entry.Default.y, entry.Default.z, entry.Default.w);
                        int b4 = soaBase + index * 4;
                        _cpuBuffer[b4 + 0] = c.r;
                        _cpuBuffer[b4 + 1] = c.g;
                        _cpuBuffer[b4 + 2] = c.b;
                        _cpuBuffer[b4 + 3] = c.a;
                        break;
                    }
                    case PropKind.Vector:
                    {
                        Vector4 v = mat.HasProperty(entry.PropId)
                            ? mat.GetVector(entry.PropId)
                            : new Vector4(entry.Default.x, entry.Default.y, entry.Default.z, entry.Default.w);
                        int b4 = soaBase + index * 4;
                        _cpuBuffer[b4 + 0] = v.x;
                        _cpuBuffer[b4 + 1] = v.y;
                        _cpuBuffer[b4 + 2] = v.z;
                        _cpuBuffer[b4 + 3] = v.w;
                        break;
                    }
                    default: // PropKind.Float
                    {
                        float f = mat.HasProperty(entry.PropId)
                            ? mat.GetFloat(entry.PropId)
                            : entry.Default.x;
                        _cpuBuffer[soaBase + index] = f;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Packs a <see cref="float4x4"/> into Unity BRG's packed float3x4 slot (12 floats), matching the
        /// layout UnityDOTSInstancing.hlsl <c>LoadDOTSInstancedData_float3x4</c> reconstructs:
        ///   p1 = [m00,m10,m20,m01], p2 = [m11,m21,m02,m12], p3 = [m22,m03,m13,m23]
        /// where <c>mᵣc = m.c{c}[r]</c> (float4x4 is column-major), i.e. the 12 floats are the first three
        /// columns' xyz then the translation column's xyz, interleaved as below.
        /// </summary>
        private static void PackPackedFloat3x4(float[] buf, int b, in float4x4 m)
        {
            buf[b +  0] = m.c0.x; buf[b +  1] = m.c0.y; buf[b +  2] = m.c0.z; buf[b +  3] = m.c1.x;
            buf[b +  4] = m.c1.y; buf[b +  5] = m.c1.z; buf[b +  6] = m.c2.x; buf[b +  7] = m.c2.y;
            buf[b +  8] = m.c2.z; buf[b +  9] = m.c3.x; buf[b + 10] = m.c3.y; buf[b + 11] = m.c3.z;
        }

        // ── GraphicsBuffer / batch management ─────────────────────────────────────────────────

        private void EnsureBuffer(int instanceCount)
        {
            int floatsNeeded = instanceCount * _plan.FloatsPerInstance;
            bool bufferGrew = false;

            if (_instanceBuffer == null || _instanceBuffer.count < floatsNeeded)
            {
                // Release and reallocate (grow-only: count doubles if needed to amortize).
                int prevCount = _instanceBuffer?.count ?? 0;
                _instanceBuffer?.Release();
                _instanceBuffer = null;
                int newCount = math.max(floatsNeeded, prevCount * 2);
                newCount = math.max(newCount, _plan.FloatsPerInstance); // at least one instance
                _instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newCount, sizeof(float));
                bufferGrew = true;
            }

            // Re-register whenever the instance count changes OR the buffer grew.
            // SoA byte offsets are functions of N → a batch registered for N_old is wrong for N_new.
            if (bufferGrew || instanceCount != _lastRegisteredCount)
            {
                ReRegisterBatch(instanceCount);
                _lastRegisteredCount = instanceCount;
            }
        }

        /// <summary>
        /// Registers (or re-registers) the BRG batch with SoA byte offsets computed for
        /// <paramref name="instanceCount"/> instances.
        ///
        /// MetadataValue.Value = (uint)(array_byte_offset | 0x80000000u):
        ///   array_byte_offset = property_SoaFloatOffset × instanceCount × 4
        /// This is the offset of the FIRST element of the property's SoA array in the buffer.
        /// Unity computes instance i's value at: array_byte_offset + i × sizeof(property).
        ///
        /// Driven by the reflected plan — no hand-maintained M(...) wall. Each material entry is
        /// emitted in one loop; the 2 transform entries are emitted first.
        /// </summary>
        private void ReRegisterBatch(int instanceCount)
        {
            if (IsDisposed || _brg == null) return;

            if (_batchRegistered)
            {
                _brg.RemoveBatch(_batchId);
                _batchRegistered = false;
            }

            if (_instanceBuffer == null) return;

            int N = instanceCount;

            var meta = new NativeArray<MetadataValue>(_plan.MetaCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try
            {
                static MetadataValue M(int nameId, int byteOffset) => new MetadataValue
                {
                    NameID = nameId,
                    Value  = (uint)byteOffset | 0x80000000u,
                };

                int idx = 0;
                // Transform entries first (O2W, W2O):
                meta[idx++] = M(_plan.O2WPropId, _plan.O2WFloatOffset * N * 4);
                meta[idx++] = M(_plan.W2OPropId, _plan.W2OFloatOffset * N * 4);
                // Material property entries (31 entries, driven by the reflected plan):
                for (int e = 0; e < _plan.MaterialEntries.Length; e++)
                {
                    InstancePropEntry entry = _plan.MaterialEntries[e];
                    meta[idx++] = M(entry.PropId, entry.SoaFloatOffset * N * 4);
                }

                _batchId         = _brg.AddBatch(meta, _instanceBuffer.bufferHandle);
                _batchRegistered = true;
            }
            finally
            {
                meta.Dispose();
            }
        }

        // ── OnPerformCulling ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// BRG culling callback. Emits one <see cref="BatchDrawCommand"/> per draw item in ascending
        /// renderQueue order (painter's algorithm), grouped into run-length
        /// <see cref="BatchDrawRange"/>s by declared shadow-cast mode
        /// (<see cref="ComputeDrawRanges"/>). Minimal culling for a camera view: all draws are emitted. A
        /// LIGHT view emits only the caster slots (<see cref="ComputeEmitOrder"/>), so "only fill-extrusion
        /// casts" holds in code we own rather than depending on the engine honouring range filter settings.
        ///
        /// <c>unsafe</c> is required to fill <see cref="BatchCullingOutputDrawCommands"/> via raw
        /// pointers and <see cref="UnsafeUtility.Malloc"/>.
        /// </summary>
        private unsafe JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            // Track invocations so tests can confirm this path is driven by the render loop.
            Interlocked.Increment(ref CullingCallCount);

            // Compacted emit order: the sorted-item slots whose handle is still live in _items. Handles
            // removed by RemoveItem since the last Rebuild (tile eviction during zoom) are filtered out
            // here, so they get NO draw command. This is load-bearing: UnsafeUtility.Malloc does NOT zero
            // memory, so emitting one command per _sortedItems slot and skipping stale ones in place would
            // leave uninitialized garbage BatchDrawCommands (invalid batch/mesh/material id) — the source
            // of the "MeshID <null>" BRG error seen while zooming.
            int emitted = ComputeEmitOrder(_emitList, cullingContext.viewType);
            if (emitted == 0 || !_batchRegistered) return default;

            // One range per run of consecutive same-shadow-mode commands: BatchDrawCommand carries no shadow
            // flag, so the RANGE's filterSettings is the only place the declaration can live.
            int rangeCount = ComputeDrawRanges(_emitList, _drawRanges);

            // cullingOutput.drawCommands is a NativeArray<BatchCullingOutputDrawCommands> (length 1).
            var drawCommandsPtr = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

            // Allocate EXACTLY the compacted count — never _sortedItems.Count.
            drawCommandsPtr->drawRangeCount = rangeCount;
            drawCommandsPtr->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                (long)sizeof(BatchDrawRange) * rangeCount,
                UnsafeUtility.AlignOf<BatchDrawRange>(),
                Allocator.TempJob);

            drawCommandsPtr->drawCommandCount = emitted;
            drawCommandsPtr->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                (long)sizeof(BatchDrawCommand) * emitted,
                UnsafeUtility.AlignOf<BatchDrawCommand>(),
                Allocator.TempJob);

            drawCommandsPtr->visibleInstanceCount = emitted;
            drawCommandsPtr->visibleInstances = (int*)UnsafeUtility.Malloc(
                (long)sizeof(int) * emitted,
                UnsafeUtility.AlignOf<int>(),
                Allocator.TempJob);

            // ComputeDrawRanges wrote EVERY field of every range — Malloc does not zero (see the MeshID
            // <null> note above), and a half-written BatchDrawRange fails as random shadow/layer-mask
            // behaviour rather than cleanly.
            for (int r = 0; r < rangeCount; r++) drawCommandsPtr->drawRanges[r] = _drawRanges[r];

            // Fill exactly `emitted` contiguous commands — no holes. _emitList[e] is
            // the packed instance-buffer slot (Rebuild packed instance i at sorted index i).
            for (int e = 0; e < emitted; e++)
            {
                int i = _emitList[e];
                var item = _items[_sortedItems[i].handle];

                drawCommandsPtr->visibleInstances[e] = i;

                drawCommandsPtr->drawCommands[e] = new BatchDrawCommand
                {
                    visibleOffset       = (uint)e,
                    visibleCount        = 1,
                    batchID             = _batchId,
                    materialID          = item.MatId,
                    meshID              = item.MeshId,
                    submeshIndex        = 0,
                    splitVisibilityMask = 0xff,
                    flags               = BatchDrawCommandFlags.None,
                    sortingPosition     = 0,
                };
            }

            return default;
        }

        /// <summary>
        /// Builds the compacted draw-command emit order into <paramref name="dst"/>: for each entry in
        /// <see cref="_sortedItems"/> (ascending renderQueue) whose handle is still live in
        /// <see cref="_items"/>, appends that entry's index (the packed instance-buffer slot). Handles
        /// removed by <see cref="RemoveItem"/> since the last <see cref="Rebuild"/> — e.g. tiles evicted
        /// while zooming, before <c>_sortedItems</c> is rebuilt — are filtered out, so no draw command is
        /// emitted for a stale slot (which would otherwise be uninitialized garbage: the BRG
        /// "MeshID &lt;null&gt;" error). Returns the number of live items.
        ///
        /// Internal for white-box testing of the eviction/compaction invariant. Allocation-free in steady
        /// state (reuses <paramref name="dst"/>).
        /// </summary>
        /// <param name="dst">Reused destination list; cleared first.</param>
        /// <param name="viewType">The view being culled. <see cref="BatchCullingViewType.Light"/> is the
        /// shadow pass, and drops every slot declared <see cref="ShadowCastingMode.Off"/> — see
        /// <see cref="OnPerformCulling"/> for why the range's filter settings alone are not relied on.</param>
        /// <returns>The number of items written.</returns>
        internal int ComputeEmitOrder(List<int> dst, BatchCullingViewType viewType)
        {
            bool shadowView = viewType == BatchCullingViewType.Light;
            dst.Clear();
            for (int i = 0; i < _sortedItems.Count; i++)
            {
                if (!_items.TryGetValue(_sortedItems[i].handle, out DrawItem item)) continue;
                if (!Visible(item.MaterialIndex)) continue;
                if (shadowView && ShadowModeFor(item.MaterialIndex) == ShadowCastingMode.Off) continue;
                dst.Add(i);
            }
            return dst.Count;
        }

        /// <summary>This backend's copy of the shared shadow-mode lookup: the declared mode for
        /// <paramref name="materialIndex"/>, or <see cref="ShadowCastingMode.Off"/> when no list was supplied
        /// or it is short. The fallback must read identically in all three backends
        /// (<see cref="ITileRenderBackend"/>).</summary>
        /// <param name="materialIndex">The layer's global SLOT.</param>
        private ShadowCastingMode ShadowModeFor(int materialIndex)
            => (uint)materialIndex < (uint)_layerShadowModes.Count
                ? _layerShadowModes[materialIndex]
                : ShadowCastingMode.Off;

        /// <summary>True when <paramref name="materialIndex"/>'s slot is visible, so it emits a draw
        /// command in every view it is not otherwise excluded from. One list read per item per cull — see
        /// <see cref="ComputeEmitOrder"/>.</summary>
        /// <param name="materialIndex">The layer's global SLOT.</param>
        private bool Visible(int materialIndex)
            => (uint)materialIndex >= (uint)_layerVisible.Count || _layerVisible[materialIndex];

        /// <inheritdoc cref="ITileRenderBackend.SetLayerVisible"/>
        public void SetLayerVisible(int slot, bool visible)
        {
            if (IsDisposed || slot < 0) return;
            while (_layerVisible.Count <= slot) _layerVisible.Add(true);
            if (_layerVisible[slot] == visible) return; // unchanged ⇒ nothing to update
            _layerVisible[slot] = visible;
        }

        /// <summary>
        /// Run-length groups <paramref name="emitOrder"/> (already in emission order) by declared shadow-cast
        /// mode into <paramref name="dst"/>, one <see cref="BatchDrawRange"/> per run. A range is the ONLY
        /// place a shadow declaration can live — <see cref="BatchDrawCommand"/> carries no such field — so
        /// per-layer variation costs one extra range per mode change, never a reorder: the ranges partition
        /// the command array contiguously and in order, leaving the painter's ordering untouched.
        /// <see cref="BatchFilterSettings"/> is written in FULL (see <see cref="OnPerformCulling"/>).
        ///
        /// Internal for the same reason <see cref="ComputeEmitOrder"/> is — it is the testable seam into
        /// <see cref="OnPerformCulling"/>, which needs a live GPU otherwise. Allocation-free in steady state.
        /// </summary>
        /// <param name="emitOrder">Compacted emit order from <see cref="ComputeEmitOrder"/>.</param>
        /// <param name="dst">Reused destination list; cleared first.</param>
        /// <returns>The number of ranges written.</returns>
        internal int ComputeDrawRanges(List<int> emitOrder, List<BatchDrawRange> dst)
        {
            dst.Clear();
            int e = 0;
            while (e < emitOrder.Count)
            {
                ShadowCastingMode cast = EmittedShadowMode(emitOrder[e]);
                int begin = e;
                do { e++; }
                while (e < emitOrder.Count && EmittedShadowMode(emitOrder[e]) == cast);

                dst.Add(new BatchDrawRange
                {
                    drawCommandsBegin = (uint)begin,
                    drawCommandsCount = (uint)(e - begin),
                    filterSettings    = new BatchFilterSettings
                    {
                        renderingLayerMask = 0xFFFFFFFF,
                        layer              = 0,
                        motionMode         = MotionVectorGenerationMode.Camera,
                        shadowCastingMode  = cast,
                        receiveShadows     = true,
                        staticShadowCaster = false,
                        allDepthSorted     = false,
                    },
                });
            }
            return dst.Count;
        }

        /// <summary>The declared shadow-cast mode of the draw item at sorted slot
        /// <paramref name="sortedIndex"/>.</summary>
        /// <param name="sortedIndex">An index into <see cref="_sortedItems"/>, as emitted by
        /// <see cref="ComputeEmitOrder"/>.</param>
        private ShadowCastingMode EmittedShadowMode(int sortedIndex)
            => ShadowModeFor(_items[_sortedItems[sortedIndex].handle].MaterialIndex);

        // ── Dispose ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Releases the BRG, all registered meshes/materials, and the GraphicsBuffer.
        /// Idempotent: safe to call multiple times.
        ///
        /// BRG + every GraphicsBuffer is released on teardown. Failing to dispose a process-global BRG
        /// pollutes other cameras and tests, so this is load-bearing.
        /// </summary>
        protected override void DoDispose()
        {
            if (_batchRegistered && _brg != null)
            {
                _brg.RemoveBatch(_batchId);
                _batchRegistered = false;
            }

            // Unregister all materials (do before BRG dispose).
            foreach (var (id, _) in _layerMaterials)
                _brg?.UnregisterMaterial(id);
            _layerMaterials.Clear();

            // Unregister all meshes.
            foreach (var kv in _meshIds)
                _brg?.UnregisterMesh(kv.Value);
            _meshIds.Clear();

            _items.Clear();
            _sortedItems.Clear();

            _instanceBuffer?.Release();
            _instanceBuffer = null;

            _brg?.Dispose();
            _brg = null;
        }
    }
}
