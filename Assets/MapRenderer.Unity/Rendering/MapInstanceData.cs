// Single source of truth for the BRG per-instance SoA layout.
// Add a property here = add a field; nothing is hand-copied.
// InstancePropPlan.BuildFromStruct<MapInstanceData>() reflects this struct ONCE at construction
// to build the cached packing plan used by BrgTileRenderer every Rebuild.

using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace MapRenderer.Unity.Rendering
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
    /// Single source of truth for the BRG per-instance SoA layout.
    ///
    /// <para>Field order = physical SoA order: transforms first, then the 31 material props (19 pre-existing
    /// fill/common-Lit props in their original order for byte-identical fill wire, then the 12 new line props
    /// appended). This is an AoS descriptor only — <see cref="BrgTileRenderer"/> transposes to SoA on write.
    /// Do NOT memcpy to the GPU buffer.</para>
    ///
    /// <para>Field names must literally match the shader property names (leading underscore) so the
    /// bidirectional parity guard in <c>InstanceStructShaderParityTests</c> compares by name without
    /// indirection.</para>
    ///
    /// <para>Fields prefixed <c>unity_</c> are transform metadata (not UNITY_DOTS_INSTANCED_PROPs);
    /// they are excluded from the material-property plan and from the parity comparison.</para>
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
        [InstancedProp(PropKind.Float,  1f)]             public float  _MetersPerPixel;
        [InstancedProp(PropKind.Float,  1f)]             public float  _AaEdgeWidth;  // default 1 — never 0 (AA tooth)
    }
}
