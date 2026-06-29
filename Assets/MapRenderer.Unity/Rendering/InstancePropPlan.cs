// Reflected-once packing plan for BrgTileRenderer.
// Built at construction from MapInstanceData via System.Reflection + Marshal.OffsetOf;
// never touched per-frame — per-frame pack indexes MaterialEntries[] by index only (no boxing, no LINQ).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// One entry in the <see cref="InstancePropPlan"/> for a single DOTS-instanced material property.
    /// Read-only value type — stored in <see cref="InstancePropPlan.MaterialEntries"/> and indexed by
    /// the per-frame packer (no boxing, no LINQ, no reflection per call).
    /// </summary>
    internal readonly struct InstancePropEntry
    {
        /// <summary>Cached <c>Shader.PropertyToID(fieldName)</c>.</summary>
        internal readonly int PropId;

        /// <summary>
        /// Per-instance SoA float offset for this property (the <c>Pfx_*</c> equivalent).
        /// Buffer address for instance <c>i</c> of <c>count</c> instances:
        /// <c>SoaFloatOffset * count + i * FloatCount</c>.
        /// </summary>
        internal readonly int SoaFloatOffset;

        /// <summary>Number of consecutive floats this property occupies (1 for scalar, 4 for colour/vector).</summary>
        internal readonly int FloatCount;

        /// <summary>How to read the value from the Material (selects GetFloat / GetColor / GetVector).</summary>
        internal readonly PropKind Kind;

        /// <summary>
        /// Default value to use when <c>mat.HasProperty(PropId)</c> is false.
        /// For <c>PropKind.Float</c> only <c>.x</c> is used; for Color/Vector all four components are used.
        /// </summary>
        internal readonly float4 Default;

        internal InstancePropEntry(int propId, int soaFloatOffset, int floatCount, PropKind kind, float4 def)
        {
            PropId        = propId;
            SoaFloatOffset = soaFloatOffset;
            FloatCount    = floatCount;
            Kind          = kind;
            Default       = def;
        }
    }

    /// <summary>
    /// Reflected-once packing plan built from a <see cref="MapInstanceData"/>-shaped struct.
    ///
    /// <para>Call <see cref="BuildFromStruct{T}"/> once at construction. The resulting plan is immutable
    /// and used per-frame by <see cref="BrgTileRenderer"/> without further reflection or allocation.</para>
    /// </summary>
    internal sealed class InstancePropPlan
    {
        /// <summary>Total floats per instance in the SoA buffer (== sum of all field float-counts).</summary>
        internal int FloatsPerInstance { get; }

        /// <summary>Total BRG metadata entry count (2 transforms + <see cref="MaterialEntries"/>.Length).</summary>
        internal int MetaCount { get; }

        /// <summary>
        /// Cached packing entries for the 31 material properties, in physical SoA order.
        /// Index these by <c>for</c> loop only — no LINQ, no boxing, no per-frame allocation.
        /// </summary>
        internal InstancePropEntry[] MaterialEntries { get; }

        /// <summary>SoA float offset of the <c>unity_ObjectToWorld</c> array (always 0 for the first field).</summary>
        internal int O2WFloatOffset { get; }

        /// <summary>SoA float offset of the <c>unity_WorldToObject</c> array (always 12 for the second field).</summary>
        internal int W2OFloatOffset { get; }

        /// <summary>Cached <c>Shader.PropertyToID("unity_ObjectToWorld")</c>.</summary>
        internal int O2WPropId { get; }

        /// <summary>Cached <c>Shader.PropertyToID("unity_WorldToObject")</c>.</summary>
        internal int W2OPropId { get; }

        private InstancePropPlan(
            int floatsPerInstance,
            int metaCount,
            InstancePropEntry[] materialEntries,
            int o2wFloatOffset,
            int w2oFloatOffset,
            int o2wPropId,
            int w2oPropId)
        {
            FloatsPerInstance = floatsPerInstance;
            MetaCount         = metaCount;
            MaterialEntries   = materialEntries;
            O2WFloatOffset    = o2wFloatOffset;
            W2OFloatOffset    = w2oFloatOffset;
            O2WPropId         = o2wPropId;
            W2OPropId         = w2oPropId;
        }

        /// <summary>
        /// Returns the SoA float offset for the material entry with <paramref name="propId"/>, or -1 if
        /// not found. Used by tests for the byte-identical-wire spot check.
        /// </summary>
        internal int GetSoaFloatOffset(int propId)
        {
            for (int i = 0; i < MaterialEntries.Length; i++)
            {
                if (MaterialEntries[i].PropId == propId)
                    return MaterialEntries[i].SoaFloatOffset;
            }
            return -1;
        }

        /// <summary>
        /// Builds a plan by reflecting <typeparamref name="T"/> once.
        ///
        /// <para>Requirements for <typeparamref name="T"/>:
        /// <list type="bullet">
        ///   <item><c>[StructLayout(LayoutKind.Sequential)]</c> — layout must be blittable and float-aligned.</item>
        ///   <item>Exactly two fields named <c>unity_ObjectToWorld</c> and <c>unity_WorldToObject</c> (both
        ///         <c>float3x4</c>). They are excluded from material entries.</item>
        ///   <item>All remaining fields are material props; each carries an <see cref="InstancedPropAttribute"/>
        ///         declaring kind + default. Field names literally equal their shader property names.</item>
        ///   <item>All field types are <c>float</c>, <c>float4</c>, or <c>float3x4</c> — byte-aligned at 4.</item>
        /// </list>
        /// </para>
        ///
        /// <para>Fields are sorted by <c>Marshal.OffsetOf</c> to bind to the physical
        /// <c>[StructLayout(Sequential)]</c> order (reflection field order is not CLR-guaranteed).</para>
        /// </summary>
        internal static InstancePropPlan BuildFromStruct<T>() where T : struct
        {
            Type type = typeof(T);
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);

            // Sort by Marshal.OffsetOf to get the physical [StructLayout(Sequential)] declaration order.
            // GetFields() order is NOT CLR-guaranteed — sorting pins us to the physical layout.
            Array.Sort(fields, (a, b) =>
            {
                int oa = (int)Marshal.OffsetOf(type, a.Name);
                int ob = (int)Marshal.OffsetOf(type, b.Name);
                return oa.CompareTo(ob);
            });

            // FloatsPerInstance = Marshal.SizeOf / sizeof(float):
            // All fields are float-aligned (float, float4, float3x4) so the byte size is divisible by 4.
            int totalBytes       = Marshal.SizeOf(type);
            int floatsPerInstance = totalBytes / 4;

            int o2wFloatOffset = -1, w2oFloatOffset = -1;
            var entries = new List<InstancePropEntry>(fields.Length);

            foreach (FieldInfo field in fields)
            {
                // SoA float offset = byte offset / sizeof(float).
                // Valid because all field types are float-aligned (byte offset always divisible by 4).
                int byteOffset     = (int)Marshal.OffsetOf(type, field.Name);
                int soaFloatOffset = byteOffset / 4;
                int floatCount     = GetFloatCount(field.FieldType);

                bool isTransform = field.Name.StartsWith("unity_", StringComparison.Ordinal);
                if (isTransform)
                {
                    if (field.Name == "unity_ObjectToWorld") o2wFloatOffset = soaFloatOffset;
                    else if (field.Name == "unity_WorldToObject") w2oFloatOffset = soaFloatOffset;
                    // (other unity_* fields would be skipped here too)
                    continue;
                }

                var attr    = field.GetCustomAttribute<InstancedPropAttribute>();
                PropKind kind = attr?.Kind ?? PropKind.Float;
                float4   def  = attr != null
                    ? new float4(attr.X, attr.Y, attr.Z, attr.W)
                    : float4.zero;

                int propId = Shader.PropertyToID(field.Name);
                entries.Add(new InstancePropEntry(propId, soaFloatOffset, floatCount, kind, def));
            }

            if (o2wFloatOffset < 0 || w2oFloatOffset < 0)
                throw new InvalidOperationException(
                    $"{type.Name} must have fields named 'unity_ObjectToWorld' and 'unity_WorldToObject'.");

            int o2wPropId = Shader.PropertyToID("unity_ObjectToWorld");
            int w2oPropId = Shader.PropertyToID("unity_WorldToObject");

            var arr = entries.ToArray();
            return new InstancePropPlan(
                floatsPerInstance,
                2 + arr.Length,
                arr,
                o2wFloatOffset,
                w2oFloatOffset,
                o2wPropId,
                w2oPropId);
        }

        private static int GetFloatCount(Type fieldType)
        {
            if (fieldType == typeof(float))   return 1;
            if (fieldType == typeof(float4))  return 4;
            if (fieldType == typeof(float3x4)) return 12;
            throw new ArgumentException(
                $"Unsupported field type '{fieldType.Name}' in BRG instance struct. " +
                "Only float, float4, and float3x4 are allowed (all float-aligned).");
        }
    }
}
