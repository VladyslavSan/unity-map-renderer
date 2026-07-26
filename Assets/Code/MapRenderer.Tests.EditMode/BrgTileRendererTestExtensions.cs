// Unity EditMode only — real BatchRendererGroup state. NOT registered in core-tests.csproj.
//
// Namespace is MapRenderer.Tests (not .Visual), matching the GameObjects and Entities files: C# resolves
// extension methods only through the call site's ENCLOSING namespaces, and callers live in both
// MapRenderer.Tests (BrgTileRendererEvictionTests) and MapRenderer.Tests.Visual (the snapshot/readback
// fixtures). The parent namespace is the one both can see.
//
// Observability for the BRG backend, living in the TEST assembly rather than on the production class.
// These eight sat on Backend.BRG.TileRenderer under two "Test observability" banners with ZERO production
// callers between them. They read the backend's _plan/_items/_cpuBuffer/_sortedItems/_instanceBuffer,
// broadened private -> internal, which IS the sanctioned footprint.
//
// Unlike the GameObjects and Entities moves, nothing here dropped a post-dispose guard: BRG never had
// observability leniency. Its four IsDisposed sites are on RemoveItem/RemoveItems/Rebuild/ReRegisterBatch —
// drive methods, deliberately left alone.
//
// Two of these are DECODERS of the production SoA packing rather than trivial read-backs
// (GetInstanceTranslation, GetInstancePropValue). Having them in the test assembly is the point: a readback
// test that decodes the buffer with its own copy of the layout rules fails when the writer's layout drifts,
// which is precisely the regression those teeth exist to catch. Keep them in sync with Rebuild deliberately,
// not by sharing code with it.

using System.Collections.Generic;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;

namespace MapRenderer.Tests
{
    internal static class BrgTileRendererTestExtensions
    {
        /// <summary>Number of currently registered draw items.</summary>
        internal static int DrawItemCount(this BrgTileRenderer renderer) => renderer._items.Count;

        /// <summary>True if the instance GraphicsBuffer is allocated.</summary>
        internal static bool HasBuffer(this BrgTileRenderer renderer) => renderer._instanceBuffer != null;

        /// <summary>Total floats per instance in the SoA buffer, as derived from <c>MapInstanceData</c>.</summary>
        internal static int FloatsPerInstance(this BrgTileRenderer renderer) => renderer._plan.FloatsPerInstance;

        /// <summary>Total BRG metadata entry count (2 transforms + the material props), as derived from
        /// <c>MapInstanceData</c>.</summary>
        internal static int MetadataEntryCount(this BrgTileRenderer renderer) => renderer._plan.MetaCount;

        /// <summary>
        /// The packed translation (X, Z) of the instance <paramref name="handle"/> from the last
        /// <c>Rebuild</c> call. GPU-independent — decodes the CPU buffer (SoA layout), not the GPU buffer.
        /// Returns (NaN, NaN) if the handle is not registered or no Rebuild has run.
        /// </summary>
        internal static (float x, float z) GetInstanceTranslation(this BrgTileRenderer renderer, int handle)
        {
            int count = renderer._sortedItems.Count;
            if (renderer._cpuBuffer == null || count == 0
                || renderer._cpuBuffer.Length < count * renderer._plan.FloatsPerInstance)
                return (float.NaN, float.NaN);

            for (int si = 0; si < count; si++)
            {
                if (renderer._sortedItems[si].handle == handle)
                {
                    // SoA layout: O2W array starts at float 0.
                    // Instance si's O2W occupies floats [si*12 .. si*12+11].
                    // Unity BRG packed float3x4 format (see UnityDOTSInstancing.hlsl):
                    //   p1=[m00,m10,m20,m01], p2=[m11,m21,m02,m12], p3=[m22,m03,m13,m23]
                    // tx = m03 = float index si*12+9
                    // ty = m13 = float index si*12+10
                    // tz = m23 = float index si*12+11
                    return (renderer._cpuBuffer[si * 12 + 9], renderer._cpuBuffer[si * 12 + 11]);
                }
            }
            return (float.NaN, float.NaN);
        }

        /// <summary>
        /// The packed value of the material property <paramref name="propId"/> for the instance
        /// <paramref name="handle"/> from the last <c>Rebuild</c> call. GPU-independent — decodes the CPU
        /// SoA buffer directly.
        ///
        /// <para><paramref name="component"/> selects the float within a multi-float property
        /// (0=x/r, 1=y/g, 2=z/b, 3=w/a). For scalar properties component must be 0.</para>
        ///
        /// Returns <c>float.NaN</c> if the handle is not registered, the property is not in the plan, or no
        /// <c>Rebuild</c> has run. NaN (not 0) makes the buggy-build case (no plan entry) fail explicitly
        /// rather than silently reading 0.
        /// </summary>
        internal static float GetInstancePropValue(
            this BrgTileRenderer renderer, int handle, int propId, int component = 0)
        {
            int count = renderer._sortedItems.Count;
            if (renderer._cpuBuffer == null || count == 0
                || renderer._cpuBuffer.Length < count * renderer._plan.FloatsPerInstance)
                return float.NaN;

            int slot = -1;
            for (int si = 0; si < count; si++)
            {
                if (renderer._sortedItems[si].handle == handle) { slot = si; break; }
            }
            if (slot < 0) return float.NaN;

            var entries = renderer._plan.MaterialEntries;
            for (int e = 0; e < entries.Length; e++)
            {
                var entry = entries[e];
                if (entry.PropId != propId) continue;
                int idx = entry.SoaFloatOffset * count + slot * entry.FloatCount + component;
                if ((uint)idx >= (uint)renderer._cpuBuffer.Length) return float.NaN;
                return renderer._cpuBuffer[idx];
            }
            return float.NaN;
        }

        /// <summary>
        /// The SoA float offset (the Pfx_ equivalent) for the material property <paramref name="propId"/>,
        /// or -1 if not found in the plan. Used for the byte-identical-wire spot check.
        /// </summary>
        internal static int GetPropSoaOffset(this BrgTileRenderer renderer, int propId)
            => renderer._plan.GetSoaFloatOffset(propId);

        /// <summary>
        /// The renderQueue of each draw command in emission order, as computed by the last <c>Rebuild</c>
        /// call. GPU-independent — reads the sorted items list.
        /// </summary>
        internal static int[] GetEmittedRenderQueues(this BrgTileRenderer renderer)
        {
            List<(int renderQueue, int handle)> sorted = renderer._sortedItems;
            var result = new int[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                int h = sorted[i].handle;
                result[i] = renderer._items.TryGetValue(h, out var item) ? item.LayerRenderQueue : -1;
            }
            return result;
        }
    }
}
