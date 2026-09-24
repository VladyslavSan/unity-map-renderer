// Unity EditMode only: test-assembly observability over the BRG backend's internal fields.
//
// Non-obvious why: C# finds extension methods only through the call site's enclosing namespaces, and callers
// live in MapRenderer.Tests and MapRenderer.Tests.Visual, so this file uses the parent namespace.
//
// Non-obvious why: GetInstanceTranslation and GetInstancePropValue decode the SoA packing with a second copy
// of the layout rules, so a readback test fails when Rebuild's layout drifts. Never share code with Rebuild.

using System.Collections.Generic;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;

namespace MapRenderer.Tests
{
    internal static class BrgTileRendererTestExtensions
    {
        /// <summary>Number of currently registered draw items.</summary>
        internal static int DrawItemCount(this BrgTileRenderer renderer) => renderer._items.Count;

        /// <summary>The layer draw slot (<c>materialIndex</c>) of the draw item at sorted slot
        /// <paramref name="sortedIndex"/> — the value a <c>ComputeEmitOrder</c> entry names. Lets a caller
        /// map an emitted draw command back to the layer that produced it.</summary>
        internal static int MaterialIndexAtSorted(this BrgTileRenderer renderer, int sortedIndex)
            => renderer._items[renderer._sortedItems[sortedIndex].handle].MaterialIndex;

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
                    // The O2W array starts at float 0; instance si's packed float3x4 (UnityDOTSInstancing.hlsl) is
                    // [m00,m10,m20,m01, m11,m21,m02,m12, m22,m03,m13,m23], so tx/ty/tz sit at si*12+9/+10/+11.
                    return (renderer._cpuBuffer[si * 12 + 9], renderer._cpuBuffer[si * 12 + 11]);
                }
            }
            return (float.NaN, float.NaN);
        }

        /// <summary>
        /// The packed value of the material property <paramref name="propId"/> for the instance
        /// <paramref name="handle"/> from the last <c>Rebuild</c> call, decoded from the CPU SoA buffer.
        /// <paramref name="component"/> selects the float within a multi-float property (0=x/r … 3=w/a; 0 for
        /// a scalar). Returns <c>float.NaN</c> for an unknown handle, a property missing from the plan, or no
        /// <c>Rebuild</c> yet, so a missing plan entry fails instead of reading 0.
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
