// Single source of truth for the BRG per-instance SoA layout: to add a property, add a field here.
// InstancePropPlan.BuildFromStruct<MapInstanceData>() reflects it once into the plan every Rebuild uses.

using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace MapRenderer.Unity.Rendering.Backend.BRG
{
    // ── Attribute ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Semantic kind of a DOTS-instanced material property (selects the Material.Get* overload).</summary>
    internal enum PropKind
    {
        /// <summary>Scalar — <c>Material.GetFloat(int)</c>.</summary>
        Float,
        /// <summary>RGBA colour — <c>Material.GetColor(int)</c>; writes r,g,b,a to four consecutive floats.</summary>
        Color,
        /// <summary>Four-component vector — <c>Material.GetVector(int)</c>; writes x,y,z,w.</summary>
        Vector,
    }

    /// <summary>
    /// Declares the per-instance read kind and default value for a <see cref="MapInstanceData"/> field.
    /// <see cref="InstancePropPlan.BuildFromStruct{T}"/> reads this attribute via reflection once at
    /// construction; it is never accessed per-frame.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class InstancedPropAttribute : Attribute
    {
        internal PropKind Kind { get; }
        internal float    X    { get; }
        internal float    Y    { get; }
        internal float    Z    { get; }
        internal float    W    { get; }

        /// <param name="kind">Read kind (Float/Color/Vector).</param>
        /// <param name="x">Default x / red / scalar component.</param>
        /// <param name="y">Default y / green (Color/Vector only).</param>
        /// <param name="z">Default z / blue  (Color/Vector only).</param>
        /// <param name="w">Default w / alpha (Color/Vector only).</param>
        internal InstancedPropAttribute(PropKind kind, float x = 0f, float y = 0f, float z = 0f, float w = 0f)
        {
            Kind = kind;
            X = x; Y = y; Z = z; W = w;
        }
    }

    // ── Staging struct ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AoS descriptor of the BRG per-instance layout; field order is the physical SoA order.
    /// <see cref="TileRenderer"/> transposes it to SoA on write, so never memcpy it to the GPU buffer.
    /// Field names equal the shader property names, which <c>InstanceStructShaderParityTests</c> compares.
    /// The <c>unity_</c> transform fields are not DOTS-instanced props and are excluded from the material
    /// plan and from that parity check.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MapInstanceData
    {
        // ── Transforms (unity_ prefix → excluded from material prop plan) ─────────────────────

        public float3x4 unity_ObjectToWorld;
        public float3x4 unity_WorldToObject;

        // ── Common URP Lit props (in both Fill and Line DOTS blocks) ──────────────────────────

        [InstancedProp(PropKind.Color, 1f, 1f, 1f, 1f)] public float4 _BaseColor;
        [InstancedProp(PropKind.Color, 1f, 1f, 1f, 1f)] public float4 _SpecColor;
        [InstancedProp(PropKind.Color, 0f, 0f, 0f, 0f)] public float4 _EmissionColor;
        [InstancedProp(PropKind.Float, 0.5f)]            public float  _Cutoff;
        [InstancedProp(PropKind.Float, 0f)]              public float  _Smoothness;
        [InstancedProp(PropKind.Float, 0f)]              public float  _Metallic;
        [InstancedProp(PropKind.Float, 1f)]              public float  _BumpScale;
        [InstancedProp(PropKind.Float, 0f)]              public float  _Parallax;
        [InstancedProp(PropKind.Float, 1f)]              public float  _OcclusionStrength;
        [InstancedProp(PropKind.Float, 0f)]              public float  _ClearCoatMask;
        [InstancedProp(PropKind.Float, 1f)]              public float  _ClearCoatSmoothness;
        [InstancedProp(PropKind.Float, 1f)]              public float  _DetailAlbedoMapScale;
        [InstancedProp(PropKind.Float, 1f)]              public float  _DetailNormalMapScale;
        [InstancedProp(PropKind.Float, 1f)]              public float  _Opacity;

        // ── Fill-specific (in Fill DOTS block; not in Line) ───────────────────────────────────

        [InstancedProp(PropKind.Color,  0f, 0f, 0f, 0f)] public float4 _FillOutlineColor;
        [InstancedProp(PropKind.Vector)]                  public float4 _FillTranslate;
        [InstancedProp(PropKind.Float,  1f)]              public float  _FillAntialias;
        [InstancedProp(PropKind.Float,  0f)]              public float  _FillTranslateAnchor;
        [InstancedProp(PropKind.Float,  0f)]              public float  _FillPattern;
        // Fill-pattern sampling. Both are DOTS-instanced, so an unlisted one reads unwritten instance metadata:
        // a zero-area _PatternRect clips every BRG pattern fill, and MeshRenderer snapshot tests do not see it.
        [InstancedProp(PropKind.Vector)]                  public float4 _PatternRect;
        [InstancedProp(PropKind.Vector, 1f, 1f, 0f, 0f)]  public float4 _PatternScale;

        // ── Line-specific (in Line DOTS block; not in Fill) ───────────────────────────────────

        [InstancedProp(PropKind.Float,  0f)]             public float  _Width;
        [InstancedProp(PropKind.Float,  0f)]             public float  _Blur;
        [InstancedProp(PropKind.Float,  0f)]             public float  _GapWidth;
        [InstancedProp(PropKind.Vector)]                 public float4 _LineTranslate;
        [InstancedProp(PropKind.Float,  0f)]             public float  _LineTranslateAnchor;
        [InstancedProp(PropKind.Float,  0f)]             public float  _LinePattern;
        [InstancedProp(PropKind.Vector)]                 public float4 _DashArray;
        [InstancedProp(PropKind.Float,  0f)]             public float  _DashCount;
        [InstancedProp(PropKind.Float,  0f)]             public float  _LineOffset;
        [InstancedProp(PropKind.Float,  0f)]             public float  _WidthIsPixels;
    }
}
