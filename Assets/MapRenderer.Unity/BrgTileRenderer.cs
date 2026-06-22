using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.View;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S49 BRG render backend (internal, IDisposable).
    ///
    /// Draws tile-layer meshes via <see cref="BatchRendererGroup"/> instead of per-layer GameObjects.
    /// One <see cref="BatchDrawCommand"/> per (tile,layer) mesh, sharing one batch and one
    /// <see cref="GraphicsBuffer"/> for all per-instance data. Materials are registered once from the
    /// <see cref="StyledLayerSet"/>; draw commands are emitted in ascending renderQueue order so the
    /// painter's-algorithm layer order is honoured (NOT GameObject child order).
    ///
    /// Per-instance buffer layout — STRUCT-OF-ARRAYS (SoA):
    ///   Unity BRG's MetadataValue.Value is the byte offset of the ARRAY start for a property, not the
    ///   start of a per-instance record. Instance i's property P lives at:
    ///     byteOffset_of_P_array + i * sizeof(P)
    ///   So all N instances' O2W matrices come first, then all N W2O matrices, then all N _BaseColor
    ///   values, etc. (NOT interleaved per-instance records).
    ///
    /// SoA array byte offsets (N = instance count, all ×4 for float→byte):
    ///   O2W          : 0         (N×12 floats = N×48 bytes)
    ///   W2O          : N×48      (N×12 floats = N×48 bytes)
    ///   _BaseColor   : N×96      (N×4 floats  = N×16 bytes)
    ///   _SpecColor   : N×112     (N×4 floats)
    ///   _EmissionColor: N×128    (N×4 floats)
    ///   _Cutoff      : N×144     (N×1 float)
    ///   _Smoothness  : N×148     (N×1 float)
    ///   _Metallic    : N×152     (N×1 float)
    ///   _BumpScale   : N×156     (N×1 float)
    ///   _Parallax    : N×160     (N×1 float)
    ///   _OcclusionStrength: N×164 (N×1 float)
    ///   _ClearCoatMask: N×168    (N×1 float)
    ///   _ClearCoatSmoothness: N×172 (N×1 float)
    ///   _DetailAlbedoMapScale: N×176 (N×1 float)
    ///   _DetailNormalMapScale: N×180 (N×1 float)
    ///   _MapColor    : N×184     (N×4 floats)
    ///   _Opacity     : N×200     (N×1 float)
    ///   _FillOutlineColor: N×204 (N×4 floats)
    ///   _FillTranslate: N×220    (N×4 floats)
    ///   _FillAntialias: N×236    (N×1 float)
    ///   _FillTranslateAnchor: N×240 (N×1 float)
    ///   _FillPattern : N×244     (N×1 float)
    ///   TOTAL        : N×248 bytes (N×62 floats) — same total as AoS, different arrangement.
    ///
    /// IMPORTANT: byte offsets depend on N → batch must be re-registered whenever instance count changes.
    ///
    /// Values are read back from the live Material object (after ZoomStyleApplier writes them) each
    /// <see cref="Rebuild"/> call — no per-frame managed alloc.
    ///
    /// Graphics buffer is Raw (not ConstantBuffer). Metal-specific constant-buffer windowing is deferred
    /// to S52 (acceptable for the toggle-off default path in S49).
    ///
    /// Clean-room: design follows the MapLibre Style Spec and Unity BRG documentation.
    /// </summary>
    internal sealed class BrgTileRenderer : IDisposable
    {
        // ── Per-instance property counts (number of floats per property per instance) ─────────
        // These are stride multipliers used when packing the SoA CPU buffer.
        //
        // SoA array layout (floats per property, per instance):
        //   O2W:           12 (Unity BRG packed float3x4: p1=[m00,m10,m20,m01], p2=[m11,m21,m02,m12], p3=[m22,m03,m13,m23])
        //   W2O:           12
        //   _BaseColor:     4
        //   _SpecColor:     4
        //   _EmissionColor: 4
        //   _Cutoff:        1
        //   _Smoothness:    1
        //   _Metallic:      1
        //   _BumpScale:     1
        //   _Parallax:      1
        //   _OccStr:        1
        //   _CCMask:        1
        //   _CCSmoothness:  1
        //   _DetailAlb:     1
        //   _DetailNorm:    1
        //   _MapColor:      4
        //   _Opacity:       1
        //   _FillOutlineColor: 4
        //   _FillTranslate: 4
        //   _FillAA:        1
        //   _FillTrAnch:    1
        //   _FillPattern:   1
        //   TOTAL: 62 floats per instance (same as AoS — SoA just reorders them)

        private const int FloatsPerInstance = 62;

        // Float-count prefix sums for each SoA array (= starting float index for property P's array,
        // in units of "per-instance floats", i.e. the actual start = prefix[P] * N).
        // Named Pfx_ (prefix) to distinguish from the old AoS offsets.
        private const int Pfx_O2W          =  0; // cumulative start: 0 floats from the start of the instance group
        private const int Pfx_W2O          = 12; // 0 + 12
        private const int Pfx_BaseColor    = 24; // 12 + 12
        private const int Pfx_SpecColor    = 28; // 24 + 4
        private const int Pfx_Emission     = 32; // 28 + 4
        private const int Pfx_Cutoff       = 36; // 32 + 4
        private const int Pfx_Smoothness   = 37; // 36 + 1
        private const int Pfx_Metallic     = 38; // 37 + 1
        private const int Pfx_BumpScale    = 39; // 38 + 1
        private const int Pfx_Parallax     = 40; // 39 + 1
        private const int Pfx_OccStr       = 41; // 40 + 1
        private const int Pfx_CCMask       = 42; // 41 + 1
        private const int Pfx_CCSmoothness = 43; // 42 + 1
        private const int Pfx_DetailAlb    = 44; // 43 + 1
        private const int Pfx_DetailNorm   = 45; // 44 + 1
        private const int Pfx_MapColor     = 46; // 45 + 1
        private const int Pfx_Opacity      = 50; // 46 + 4
        private const int Pfx_FillOutline  = 51; // 50 + 1
        private const int Pfx_FillTrans    = 55; // 51 + 4
        private const int Pfx_FillAA       = 59; // 55 + 4
        private const int Pfx_FillTrAnch   = 60; // 59 + 1
        private const int Pfx_FillPattern  = 61; // 60 + 1
        // 61 + 1 = 62 = FloatsPerInstance ✓

        // ── Per-draw-item record ─────────────────────────────────────────────────────────────

        private struct DrawItem
        {
            public BatchMeshID     MeshId;
            public BatchMaterialID MatId;
            public int             LayerRenderQueue;   // material.renderQueue — sort key
            public double2         TileOriginMerc;
            public int             MaterialIndex;      // index into _layerMaterials for prop readback
        }

        // ── BRG state ─────────────────────────────────────────────────────────────────────────

        private BatchRendererGroup _brg;
        private GraphicsBuffer     _instanceBuffer;
        private BatchID            _batchId;
        private bool               _batchRegistered;
        private bool               _disposed;

        // The instance count that was used when the batch was last registered.
        // SoA byte offsets depend on N, so batch must be re-registered when N changes.
        private int _lastRegisteredCount = 0;

        // Test observability: counts how many times OnPerformCulling has been called.
        // Reset to 0 by the caller if needed. Public so tests can probe without subclassing.
        internal int CullingCallCount;

        // Handle → DrawItem  (stable for handle-based removal API).
        private readonly Dictionary<int, DrawItem> _items = new Dictionary<int, DrawItem>(64);
        private int _nextHandle;

        // Per-layer materials registered with BRG (same order: fills first, then lines).
        // mat is referenced (not owned) — StyledLayerSet disposes materials on teardown.
        private readonly List<(BatchMaterialID id, Material mat)> _layerMaterials
            = new List<(BatchMaterialID, Material)>(16);

        // Mesh → BatchMeshID (de-dup; in S49 each tile-layer has a unique Mesh).
        private readonly Dictionary<Mesh, BatchMeshID> _meshIds
            = new Dictionary<Mesh, BatchMeshID>(64);

        // Sorted draw list (ascending renderQueue) rebuilt in Rebuild. Cleared + filled each call
        // from _items — no allocations in steady state.
        private readonly List<(int renderQueue, int handle)> _sortedItems
            = new List<(int, int)>(64);

        // CPU-side instance data (SoA layout). Grown on demand, never shrunk — no per-frame alloc.
        private float[] _cpuBuffer = Array.Empty<float>();

        // Generous bounds so minimal culling never culls tiles.
        private static readonly Bounds GenBounds = new Bounds(
            Vector3.zero, new Vector3(100_000_000f, 100_000_000f, 100_000_000f));

        // ── Construction ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Constructs the BRG and registers each layer material from <paramref name="layers"/>.
        /// Materials are referenced (not owned) — <see cref="StyledLayerSet"/> disposes them.
        /// </summary>
        public BrgTileRenderer(StyledLayerSet layers)
        {
            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);

            for (int i = 0; i < layers.FillCount; i++)
            {
                var mat   = layers.Fills[i].Material;
                var brgId = _brg.RegisterMaterial(mat);
                _layerMaterials.Add((brgId, mat));
            }
            for (int i = 0; i < layers.LineCount; i++)
            {
                var mat   = layers.Lines[i].Material;
                var brgId = _brg.RegisterMaterial(mat);
                _layerMaterials.Add((brgId, mat));
            }
        }

        // ── Test observability ────────────────────────────────────────────────────────────────

        /// <summary>Number of currently registered draw items.</summary>
        public int DrawItemCount => _items.Count;

        /// <summary>True if <see cref="Dispose"/> has been called.</summary>
        public bool IsDisposed => _disposed;

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
            if (_cpuBuffer == null || _cpuBuffer.Length < count * FloatsPerInstance || count == 0)
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
            if (count == 0 || _cpuBuffer == null || _cpuBuffer.Length < count * FloatsPerInstance)
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
        /// (fills in declared order, then lines in declared order).
        /// </summary>
        public int AddTileLayer(Mesh mesh, double2 tileOriginMerc, int materialIndex)
        {
            if (_disposed)   throw new ObjectDisposedException(nameof(BrgTileRenderer));
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
                TileOriginMerc   = tileOriginMerc,
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
            if (_disposed) return;
            _items.Remove(handle);
        }

        // ── Per-frame rebuild ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the instance data buffer from all registered draw items using SoA layout.
        /// Must be called once per frame on the BRG path.
        ///
        /// Recomputes per-instance objectToWorld from <paramref name="sceneOrigin"/> and reads
        /// material props back from each layer material (no per-frame managed allocation in steady
        /// state — buffer is grown on demand but never shrunk).
        ///
        /// SoA (Struct-of-Arrays): all instance O2W first, then all W2O, then all _BaseColor, etc.
        /// Byte offsets in ReRegisterBatch are computed from the current instance count N.
        /// The batch is re-registered whenever N changes (offsets change with N).
        /// </summary>
        public void Rebuild(double2 sceneOrigin)
        {
            if (_disposed || _items.Count == 0)
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
            int floatsNeeded = count * FloatsPerInstance;
            if (_cpuBuffer.Length < floatsNeeded)
                _cpuBuffer = new float[floatsNeeded];

            // Pack per-instance data in SoA layout.
            // Each property block P occupies floats [Pfx_P*count .. Pfx_P*count + count*stride_P - 1].
            // Instance si's value for property P is at: Pfx_P*count + si*stride_P.

            // ── O2W block (12 floats per instance) ──────────────────────────────────────────
            int o2wBase = Pfx_O2W * count; // = 0
            // ── W2O block (12 floats per instance) ──────────────────────────────────────────
            int w2oBase = Pfx_W2O * count; // = 12*count

            for (int si = 0; si < count; si++)
            {
                int handle = _sortedItems[si].handle;
                var item   = _items[handle];

                float3 pos = FloatingOrigin.TileLocalToScene(item.TileOriginMerc, sceneOrigin);

                // O2W: Unity BRG packed float3x4 (see UnityDOTSInstancing.hlsl LoadDOTSInstancedData_float3x4).
                // The HLSL reads p1/p2/p3 (three float4 packed words) and reconstructs the 4×4 matrix:
                //   Row 0: [p1.x, p1.w, p2.z, p3.y] = [m00, m01, m02, m03]
                //   Row 1: [p1.y, p2.x, p2.w, p3.z] = [m10, m11, m12, m13]
                //   Row 2: [p1.z, p2.y, p3.x, p3.w] = [m20, m21, m22, m23]
                // For identity scale + translate (tx,ty,tz):
                //   p1 = [m00, m10, m20, m01] = [1,  0,  0,  0]
                //   p2 = [m11, m21, m02, m12] = [1,  0,  0,  0]
                //   p3 = [m22, m03, m13, m23] = [1, tx, ty, tz]
                int b = o2wBase + si * 12;
                _cpuBuffer[b +  0] = 1f;     _cpuBuffer[b +  1] = 0f;     _cpuBuffer[b +  2] = 0f;     _cpuBuffer[b +  3] = 0f;
                _cpuBuffer[b +  4] = 1f;     _cpuBuffer[b +  5] = 0f;     _cpuBuffer[b +  6] = 0f;     _cpuBuffer[b +  7] = 0f;
                _cpuBuffer[b +  8] = 1f;     _cpuBuffer[b +  9] = pos.x;  _cpuBuffer[b + 10] = pos.y;  _cpuBuffer[b + 11] = pos.z;

                // W2O: inverse translate by (-tx, -ty, -tz) — same packed format.
                int bw = w2oBase + si * 12;
                _cpuBuffer[bw +  0] = 1f;    _cpuBuffer[bw +  1] = 0f;    _cpuBuffer[bw +  2] = 0f;    _cpuBuffer[bw +  3] = 0f;
                _cpuBuffer[bw +  4] = 1f;    _cpuBuffer[bw +  5] = 0f;    _cpuBuffer[bw +  6] = 0f;    _cpuBuffer[bw +  7] = 0f;
                _cpuBuffer[bw +  8] = 1f;    _cpuBuffer[bw +  9] = -pos.x; _cpuBuffer[bw + 10] = -pos.y; _cpuBuffer[bw + 11] = -pos.z;
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
        /// Reads all DOTS-instanced material properties from <paramref name="mat"/> and packs them
        /// into <see cref="_cpuBuffer"/> in SoA layout for instance <paramref name="index"/> out of
        /// <paramref name="count"/> total instances.
        ///
        /// SoA: property P for instance si is at float index Pfx_P*count + si*stride_P.
        /// GetColor/GetFloat are allocation-free; HasProperty is allocation-free.
        /// </summary>
        private void PackMaterialProps(int index, int count, Material mat)
        {
            Color   baseColor  = mat.HasProperty("_BaseColor")           ? mat.GetColor("_BaseColor")           : Color.white;
            Color   specColor  = mat.HasProperty("_SpecColor")           ? mat.GetColor("_SpecColor")           : Color.white;
            Color   emission   = mat.HasProperty("_EmissionColor")       ? mat.GetColor("_EmissionColor")       : Color.black;
            Color   mapColor   = mat.HasProperty("_MapColor")            ? mat.GetColor("_MapColor")            : Color.white;
            Color   outline    = mat.HasProperty("_FillOutlineColor")    ? mat.GetColor("_FillOutlineColor")    : Color.clear;
            Vector4 fillTrans  = mat.HasProperty("_FillTranslate")       ? mat.GetVector("_FillTranslate")      : Vector4.zero;

            float cutoff     = mat.HasProperty("_Cutoff")              ? mat.GetFloat("_Cutoff")              : 0.5f;
            float smoothness = mat.HasProperty("_Smoothness")          ? mat.GetFloat("_Smoothness")          : 0f;
            float metallic   = mat.HasProperty("_Metallic")            ? mat.GetFloat("_Metallic")            : 0f;
            float bumpScale  = mat.HasProperty("_BumpScale")           ? mat.GetFloat("_BumpScale")           : 1f;
            float parallax   = mat.HasProperty("_Parallax")            ? mat.GetFloat("_Parallax")            : 0f;
            float occStr     = mat.HasProperty("_OcclusionStrength")   ? mat.GetFloat("_OcclusionStrength")   : 1f;
            float ccMask     = mat.HasProperty("_ClearCoatMask")       ? mat.GetFloat("_ClearCoatMask")       : 0f;
            float ccSmooth   = mat.HasProperty("_ClearCoatSmoothness") ? mat.GetFloat("_ClearCoatSmoothness") : 1f;
            float detailAlb  = mat.HasProperty("_DetailAlbedoMapScale")? mat.GetFloat("_DetailAlbedoMapScale"): 1f;
            float detailNorm = mat.HasProperty("_DetailNormalMapScale") ? mat.GetFloat("_DetailNormalMapScale"): 1f;
            float opacity    = mat.HasProperty("_Opacity")             ? mat.GetFloat("_Opacity")             : 1f;
            float fillAA     = mat.HasProperty("_FillAntialias")       ? mat.GetFloat("_FillAntialias")       : 1f;
            float fillTrAnch = mat.HasProperty("_FillTranslateAnchor") ? mat.GetFloat("_FillTranslateAnchor") : 0f;
            float fillPat    = mat.HasProperty("_FillPattern")         ? mat.GetFloat("_FillPattern")         : 0f;

            // SoA packing: property P for instance 'index' lives at Pfx_P*count + index*stride_P.
            // stride_P = 4 for Color, 1 for float.

            int bc4 = Pfx_BaseColor * count + index * 4;
            _cpuBuffer[bc4 + 0] = baseColor.r;
            _cpuBuffer[bc4 + 1] = baseColor.g;
            _cpuBuffer[bc4 + 2] = baseColor.b;
            _cpuBuffer[bc4 + 3] = baseColor.a;

            int sc4 = Pfx_SpecColor * count + index * 4;
            _cpuBuffer[sc4 + 0] = specColor.r;
            _cpuBuffer[sc4 + 1] = specColor.g;
            _cpuBuffer[sc4 + 2] = specColor.b;
            _cpuBuffer[sc4 + 3] = specColor.a;

            int em4 = Pfx_Emission * count + index * 4;
            _cpuBuffer[em4 + 0] = emission.r;
            _cpuBuffer[em4 + 1] = emission.g;
            _cpuBuffer[em4 + 2] = emission.b;
            _cpuBuffer[em4 + 3] = emission.a;

            _cpuBuffer[Pfx_Cutoff       * count + index] = cutoff;
            _cpuBuffer[Pfx_Smoothness   * count + index] = smoothness;
            _cpuBuffer[Pfx_Metallic     * count + index] = metallic;
            _cpuBuffer[Pfx_BumpScale    * count + index] = bumpScale;
            _cpuBuffer[Pfx_Parallax     * count + index] = parallax;
            _cpuBuffer[Pfx_OccStr       * count + index] = occStr;
            _cpuBuffer[Pfx_CCMask       * count + index] = ccMask;
            _cpuBuffer[Pfx_CCSmoothness * count + index] = ccSmooth;
            _cpuBuffer[Pfx_DetailAlb    * count + index] = detailAlb;
            _cpuBuffer[Pfx_DetailNorm   * count + index] = detailNorm;

            int mc4 = Pfx_MapColor * count + index * 4;
            _cpuBuffer[mc4 + 0] = mapColor.r;
            _cpuBuffer[mc4 + 1] = mapColor.g;
            _cpuBuffer[mc4 + 2] = mapColor.b;
            _cpuBuffer[mc4 + 3] = mapColor.a;

            _cpuBuffer[Pfx_Opacity * count + index] = opacity;

            int fo4 = Pfx_FillOutline * count + index * 4;
            _cpuBuffer[fo4 + 0] = outline.r;
            _cpuBuffer[fo4 + 1] = outline.g;
            _cpuBuffer[fo4 + 2] = outline.b;
            _cpuBuffer[fo4 + 3] = outline.a;

            int ft4 = Pfx_FillTrans * count + index * 4;
            _cpuBuffer[ft4 + 0] = fillTrans.x;
            _cpuBuffer[ft4 + 1] = fillTrans.y;
            _cpuBuffer[ft4 + 2] = fillTrans.z;
            _cpuBuffer[ft4 + 3] = fillTrans.w;

            _cpuBuffer[Pfx_FillAA      * count + index] = fillAA;
            _cpuBuffer[Pfx_FillTrAnch  * count + index] = fillTrAnch;
            _cpuBuffer[Pfx_FillPattern * count + index] = fillPat;
        }

        // ── GraphicsBuffer / batch management ─────────────────────────────────────────────────

        private void EnsureBuffer(int instanceCount)
        {
            int floatsNeeded = instanceCount * FloatsPerInstance;
            bool bufferGrew = false;

            if (_instanceBuffer == null || _instanceBuffer.count < floatsNeeded)
            {
                // Release and reallocate (grow-only: count doubles if needed to amortize).
                int prevCount = _instanceBuffer?.count ?? 0;
                _instanceBuffer?.Release();
                _instanceBuffer = null;
                int newCount = Math.Max(floatsNeeded, prevCount * 2);
                newCount = Math.Max(newCount, FloatsPerInstance); // at least one instance
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
        ///   array_byte_offset = property_prefix_float_count × instanceCount × 4
        /// This is the offset of the FIRST element of the property's SoA array in the buffer.
        /// Unity computes instance i's value at: array_byte_offset + i × sizeof(property).
        /// </summary>
        private void ReRegisterBatch(int instanceCount)
        {
            if (_disposed || _brg == null) return;

            if (_batchRegistered)
            {
                _brg.RemoveBatch(_batchId);
                _batchRegistered = false;
            }

            if (_instanceBuffer == null) return;

            // Compute SoA byte offsets for the given instance count.
            // byte_offset(P) = Pfx_P * instanceCount * 4
            int N = instanceCount;

            const int MetaCount = 22; // O2W, W2O + 20 material props
            var meta = new NativeArray<MetadataValue>(MetaCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try
            {
                static MetadataValue M(int nameId, int byteOffset) => new MetadataValue
                {
                    NameID = nameId,
                    Value  = (uint)byteOffset | 0x80000000u,
                };

                int idx = 0;
                // Transforms (12 floats per instance each):
                meta[idx++] = M(Shader.PropertyToID("unity_ObjectToWorld"),    Pfx_O2W          * N * 4);
                meta[idx++] = M(Shader.PropertyToID("unity_WorldToObject"),    Pfx_W2O          * N * 4);
                // Material properties:
                meta[idx++] = M(Shader.PropertyToID("_BaseColor"),             Pfx_BaseColor    * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_SpecColor"),             Pfx_SpecColor    * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_EmissionColor"),         Pfx_Emission     * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_Cutoff"),                Pfx_Cutoff       * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_Smoothness"),            Pfx_Smoothness   * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_Metallic"),              Pfx_Metallic     * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_BumpScale"),             Pfx_BumpScale    * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_Parallax"),              Pfx_Parallax     * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_OcclusionStrength"),     Pfx_OccStr       * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_ClearCoatMask"),         Pfx_CCMask       * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_ClearCoatSmoothness"),   Pfx_CCSmoothness * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_DetailAlbedoMapScale"),  Pfx_DetailAlb    * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_DetailNormalMapScale"),  Pfx_DetailNorm   * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_MapColor"),              Pfx_MapColor     * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_Opacity"),               Pfx_Opacity      * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_FillOutlineColor"),      Pfx_FillOutline  * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_FillTranslate"),         Pfx_FillTrans    * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_FillAntialias"),         Pfx_FillAA       * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_FillTranslateAnchor"),   Pfx_FillTrAnch   * N * 4);
                meta[idx++] = M(Shader.PropertyToID("_FillPattern"),           Pfx_FillPattern  * N * 4);

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

            int count = _sortedItems.Count;
            if (count == 0 || !_batchRegistered) return default;

            // cullingOutput.drawCommands is a NativeArray<BatchCullingOutputDrawCommands> (length 1).
            var drawCommandsPtr = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

            // One draw range covering all commands.
            drawCommandsPtr->drawRangeCount = 1;
            drawCommandsPtr->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                sizeof(BatchDrawRange) * 1,
                UnsafeUtility.AlignOf<BatchDrawRange>(),
                Allocator.TempJob);

            drawCommandsPtr->drawCommandCount = count;
            drawCommandsPtr->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                (long)sizeof(BatchDrawCommand) * count,
                UnsafeUtility.AlignOf<BatchDrawCommand>(),
                Allocator.TempJob);

            drawCommandsPtr->visibleInstanceCount = count;
            drawCommandsPtr->visibleInstances = (int*)UnsafeUtility.Malloc(
                (long)sizeof(int) * count,
                UnsafeUtility.AlignOf<int>(),
                Allocator.TempJob);

            drawCommandsPtr->drawRanges[0] = new BatchDrawRange
            {
                drawCommandsBegin = 0,
                drawCommandsCount = (uint)count,
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

            // Emit draw commands in ascending renderQueue (painter's algorithm order).
            for (int i = 0; i < count; i++)
            {
                int handle = _sortedItems[i].handle;
                if (!_items.TryGetValue(handle, out var item)) continue;

                drawCommandsPtr->visibleInstances[i] = i;

                drawCommandsPtr->drawCommands[i] = new BatchDrawCommand
                {
                    visibleOffset       = (uint)i,
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

        // ── Dispose ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Releases the BRG, all registered meshes/materials, and the GraphicsBuffer.
        /// Idempotent: safe to call multiple times.
        ///
        /// Tooth 5 (S49): BRG + every GraphicsBuffer released on teardown. Process-global BRG
        /// failure to dispose would pollute other cameras/tests; this is load-bearing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
