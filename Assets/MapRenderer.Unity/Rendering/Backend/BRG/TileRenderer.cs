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
using MapRenderer.Core.View;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Backend.BRG
{
    /// <summary>
    /// S49 BRG render backend (internal, IDisposable).
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

        private readonly InstancePropPlan _plan = InstancePropPlan.BuildFromStruct<MapInstanceData>();

        // ── Per-draw-item record ─────────────────────────────────────────────────────────────

        private struct DrawItem
        {
            public BatchMeshID     MeshId;
            public BatchMaterialID MatId;
            public int             LayerRenderQueue;   // material.renderQueue — sort key
            public double3         TileOriginRender;   // SW-corner projected render origin (Mercator: (mercX,0,mercZ))
            public int             MaterialIndex;      // index into _layerMaterials for prop readback
        }

        // ── BRG state ─────────────────────────────────────────────────────────────────────────

        private BatchRendererGroup _brg;
        private GraphicsBuffer     _instanceBuffer;
        private BatchID            _batchId;
        private bool               _batchRegistered;

        // The instance count that was used when the batch was last registered.
        // SoA byte offsets depend on N, so batch must be re-registered when N changes.
        private int _lastRegisteredCount = 0;

        // Test observability: counts how many times OnPerformCulling has been called.
        // Reset to 0 by the caller if needed. Public so tests can probe without subclassing.
        internal int CullingCallCount;

        // Handle → DrawItem  (stable for handle-based removal API).
        private readonly Dictionary<int, DrawItem> _items = new Dictionary<int, DrawItem>(64);
        private int _nextHandle;

        // Per-layer materials registered with BRG in declared order (index == materialIndex == draw order).
        // mat is referenced (not owned) — RenderLayerSet disposes materials on teardown.
        private readonly List<(BatchMaterialID id, Material mat)> _layerMaterials
            = new List<(BatchMaterialID, Material)>(16);

        // Mesh → BatchMeshID (de-dup; in S49 each tile-layer has a unique Mesh).
        private readonly Dictionary<Mesh, BatchMeshID> _meshIds
            = new Dictionary<Mesh, BatchMeshID>(64);

        // Sorted draw list (ascending renderQueue) rebuilt in Rebuild. Cleared + filled each call
        // from _items — no allocations in steady state.
        private readonly List<(int renderQueue, int handle)> _sortedItems
            = new List<(int, int)>(64);

        // Reusable scratch holding the compacted emit order (indices into _sortedItems that are still live
        // in _items) for OnPerformCulling. Grown on demand, reused each cull → no per-frame managed alloc.
        private readonly List<int> _emitScratch = new List<int>(64);

        // CPU-side instance data (SoA layout). Grown on demand, never shrunk — no per-frame alloc.
        private float[] _cpuBuffer = Array.Empty<float>();

        // Generous bounds so minimal culling never culls tiles.
        private static readonly Bounds GenBounds = new Bounds(
            Vector3.zero, new Vector3(100_000_000f, 100_000_000f, 100_000_000f));

        // ── Test observability ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Total floats per instance in the SoA buffer, as derived from <see cref="MapInstanceData"/>.
        /// Asserted == 76 by the S76 readback tooth.
        /// </summary>
        internal int FloatsPerInstance => _plan.FloatsPerInstance;

        /// <summary>
        /// Total BRG metadata entry count (2 transforms + 31 material props), as derived from
        /// <see cref="MapInstanceData"/>. Asserted == 33 by the S76 readback tooth.
        /// </summary>
        internal int MetadataEntryCount => _plan.MetaCount;

        // ── Construction ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Constructs the BRG and registers each layer material from <paramref name="layerMaterials"/> — the
        /// one ordered, per-layer material list in declared order (<c>index == materialIndex ==
        /// AddTileLayer index</c>). Materials are referenced (not owned) — the <see cref="Style.RenderLayerSet"/>
        /// disposes them. Draw order is decided per-item by <c>material.renderQueue</c> in <see cref="Rebuild"/>,
        /// independent of this registration order.
        /// </summary>
        public TileRenderer(System.Collections.Generic.IReadOnlyList<Material> layerMaterials)
        {
            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);

            for (int i = 0; i < layerMaterials.Count; i++)
            {
                var mat   = layerMaterials[i];
                var brgId = _brg.RegisterMaterial(mat);
                _layerMaterials.Add((brgId, mat));
            }
        }

        // ── Test observability ────────────────────────────────────────────────────────────────

        /// <summary>Number of currently registered draw items.</summary>
        public int DrawItemCount => _items.Count;

        // IsDisposed is inherited from VerifiedDisposable (public there too — no shadow needed).

        /// <summary>True if the instance GraphicsBuffer is allocated.</summary>
        public bool HasBuffer => _instanceBuffer != null;

        /// <summary>
        /// Returns the packed translation (X, Z) of the instance <paramref name="handle"/> from the last
        /// <see cref="Rebuild"/> call. GPU-independent — reads the CPU buffer (SoA layout), not the GPU buffer.
        /// Returns (NaN, NaN) if the handle is not registered or no Rebuild has run.
        /// </summary>
        public (float x, float z) GetInstanceTranslation(int handle)
        {
            int count = _sortedItems.Count;
            if (_cpuBuffer == null || _cpuBuffer.Length < count * _plan.FloatsPerInstance || count == 0)
                return (float.NaN, float.NaN);

            for (int si = 0; si < count; si++)
            {
                if (_sortedItems[si].handle == handle)
                {
                    // SoA layout: O2W array starts at float 0.
                    // Instance si's O2W occupies floats [si*12 .. si*12+11].
                    // Unity BRG packed float3x4 format (see UnityDOTSInstancing.hlsl):
                    //   p1=[m00,m10,m20,m01], p2=[m11,m21,m02,m12], p3=[m22,m03,m13,m23]
                    // tx = m03 = float index si*12+9
                    // ty = m13 = float index si*12+10
                    // tz = m23 = float index si*12+11
                    return (_cpuBuffer[si * 12 + 9], _cpuBuffer[si * 12 + 11]);
                }
            }
            return (float.NaN, float.NaN);
        }

        /// <summary>
        /// Returns the packed value of the material property <paramref name="propId"/> for the instance
        /// <paramref name="handle"/> from the last <see cref="Rebuild"/> call.
        /// GPU-independent — reads the CPU SoA buffer directly.
        ///
        /// <para><paramref name="component"/> selects the float within a multi-float property
        /// (0=x/r, 1=y/g, 2=z/b, 3=w/a). For scalar properties component must be 0.</para>
        ///
        /// Returns <c>float.NaN</c> if the handle is not registered, the property is not in the plan,
        /// or no <see cref="Rebuild"/> has run. NaN (not 0) makes the buggy-build case (no plan entry)
        /// fail explicitly rather than silently reading 0.
        /// </summary>
        internal float GetInstancePropValue(int handle, int propId, int component = 0)
        {
            int count = _sortedItems.Count;
            if (_cpuBuffer == null || _cpuBuffer.Length < count * _plan.FloatsPerInstance || count == 0)
                return float.NaN;

            int slot = -1;
            for (int si = 0; si < count; si++)
            {
                if (_sortedItems[si].handle == handle) { slot = si; break; }
            }
            if (slot < 0) return float.NaN;

            for (int e = 0; e < _plan.MaterialEntries.Length; e++)
            {
                InstancePropEntry entry = _plan.MaterialEntries[e];
                if (entry.PropId == propId)
                {
                    int idx = entry.SoaFloatOffset * count + slot * entry.FloatCount + component;
                    if ((uint)idx >= (uint)_cpuBuffer.Length) return float.NaN;
                    return _cpuBuffer[idx];
                }
            }
            return float.NaN;
        }

        /// <summary>
        /// Returns the SoA float offset (the Pfx_ equivalent) for the material property
        /// <paramref name="propId"/>, or -1 if not found in the plan.
        /// Used by tests for the byte-identical-wire spot check (e.g. <c>_Opacity</c> must be 46).
        /// </summary>
        internal int GetPropSoaOffset(int propId) => _plan.GetSoaFloatOffset(propId);

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

        ///<summary>
        /// Returns the renderQueue of each draw command in emission order, as computed by the last
        /// <see cref="Rebuild"/> call. GPU-independent — reads the sorted items list.
        /// </summary>
        public int[] GetEmittedRenderQueues()
        {
            var result = new int[_sortedItems.Count];
            for (int i = 0; i < _sortedItems.Count; i++)
            {
                int h = _sortedItems[i].handle;
                result[i] = _items.TryGetValue(h, out var item) ? item.LayerRenderQueue : -1;
            }
            return result;
        }

        // ── Draw item registration ────────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a tile-layer mesh for BRG drawing. Returns a handle for later removal.
        /// <paramref name="materialIndex"/> indexes into the material list built at construction
        /// (fills in declared order, then lines in declared order). <paramref name="tileId"/> is part of
        /// the shared <see cref="ITileRenderBackend"/> contract for the Entities backend's per-tile
        /// hierarchy; BRG draws a flat instance buffer and does not use it.
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

            // S91-C: place each tile in the look-at's local ENU frame. The rebase is a proper (orthonormal)
            // rotation, so O2W = [R | pos] and its RIGID inverse W2O = [Rᵀ | -Rᵀ·pos]. Building both directly
            // from the float3x3 (no quaternion round-trip, no math.inverse) keeps the Mercator identity-rebase
            // case bit-for-bit the pre-S91 translation-only packing (R = I ⇒ pos.y = 0 ⇒ old p1/p2/p3).
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
        /// renderQueue order (painter's algorithm). Minimal culling: all draws are always emitted.
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
            int emitted = ComputeEmitOrder(_emitScratch);
            if (emitted == 0 || !_batchRegistered) return default;

            // cullingOutput.drawCommands is a NativeArray<BatchCullingOutputDrawCommands> (length 1).
            var drawCommandsPtr = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

            // Allocate EXACTLY the compacted count — never _sortedItems.Count.
            drawCommandsPtr->drawRangeCount = 1;
            drawCommandsPtr->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                sizeof(BatchDrawRange) * 1,
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

            drawCommandsPtr->drawRanges[0] = new BatchDrawRange
            {
                drawCommandsBegin = 0,
                drawCommandsCount = (uint)emitted,
                filterSettings    = new BatchFilterSettings
                {
                    renderingLayerMask = 0xFFFFFFFF,
                    layer              = 0,
                    motionMode         = MotionVectorGenerationMode.Camera,
                    shadowCastingMode  = ShadowCastingMode.Off,
                    receiveShadows     = false,
                    staticShadowCaster = false,
                    allDepthSorted     = false,
                },
            };

            // Fill exactly `emitted` contiguous commands — no holes, by construction. _emitScratch[e] is
            // the packed instance-buffer slot (Rebuild packed instance i at sorted index i).
            for (int e = 0; e < emitted; e++)
            {
                int i = _emitScratch[e];
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
        internal int ComputeEmitOrder(List<int> dst)
        {
            dst.Clear();
            for (int i = 0; i < _sortedItems.Count; i++)
            {
                if (_items.ContainsKey(_sortedItems[i].handle))
                    dst.Add(i);
            }
            return dst.Count;
        }

        // ── Dispose ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Releases the BRG, all registered meshes/materials, and the GraphicsBuffer.
        /// Idempotent: safe to call multiple times.
        ///
        /// Tooth 5 (S49): BRG + every GraphicsBuffer released on teardown. Process-global BRG
        /// failure to dispose would pollute other cameras/tests; this is load-bearing.
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
