using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Per-layer zoom → material-uniform applier for S11/S60: "build-once, restyle via uniforms".
    ///
    /// Holds typed binding lists (<see cref="StyleProperty{float}"/> and
    /// <see cref="StyleProperty{CoreColor}"/>) keyed to shader property IDs. On each call to
    /// <see cref="ApplyZoom"/> it evaluates only the Zoom-kind bindings (Constant bindings are set
    /// once at bind-time and never re-evaluated). Drives the per-layer Material instance directly via
    /// <c>SetFloat</c>/<c>SetColor</c> — never <c>MaterialPropertyBlock</c>, which disables the SRP
    /// Batcher (ARCHITECTURE §2).
    ///
    /// S107: this is also the ONE seam where a px-valued style property meets the device-pixel ratio.
    /// <see cref="BindDevicePixelFloat"/> / <see cref="BindDevicePixelVector"/> name the consumer's
    /// <see cref="PixelSpace"/> at the binding site, with no default argument to forget. Note what that
    /// does and does not buy: a px property routed through these bindings must state its space, but
    /// nothing stops a new px property being bound via <see cref="BindFloat"/> and silently keeping the
    /// wrong basis — which is exactly how the line family drifted. Unitless properties have no pixel
    /// space, so forcing a <see cref="PixelSpace"/> on every binding would be wrong; the guard against a
    /// missed property is the px-surface table in <c>docs/device-pixel-ratio-design.md</c> §2.4 and its
    /// teeth, not the compiler. Those bindings always
    /// queue (the ratio is a per-frame input, not a bind-time constant), which is why a Constant
    /// <c>line-width</c> now costs one <c>SetFloat</c> per layer per frame instead of one at build.
    ///
    /// S60 design (locked): <c>StyleProperty&lt;float&gt;</c> and <c>StyleProperty&lt;Color&gt;</c>
    /// are distinct closed generic types and cannot share one binding list without boxing. Two typed
    /// lists guarantee zero boxing in <see cref="ApplyZoom"/>, which is the alloc-free hot path.
    ///
    /// Property IDs are cached via <see cref="Shader.PropertyToID"/> at bind-time to eliminate
    /// per-call string lookup allocations. The binding loops are plain <c>for</c> over typed
    /// <see cref="List{T}"/>s, which use struct enumerators and have no closure overhead.
    ///
    /// Clean-room: design follows the S11 plan and the public MapLibre Style Spec.
    /// </summary>
    public sealed class ZoomStyleApplier
    {
        // ── Typed binding tuples — no boxing ─────────────────────────────────────────────────────

        private readonly List<(StyleProperty<float>     prop, int id)> _floatBindings =
            new List<(StyleProperty<float>, int)>();

        private readonly List<(StyleProperty<CoreColor> prop, int id)> _colorBindings =
            new List<(StyleProperty<CoreColor>, int)>();

        // ── Device-pixel bindings (S107) — logical px in, the consumer's space out ────────────────
        // Separate lists rather than a flag on the two above: these carry the SPACE as part of their
        // identity, and they can never take the bind-time constant shortcut (see BindDevicePixelFloat).

        private readonly List<(StyleProperty<float> prop, int id)> _devicePixelFloatBindings =
            new List<(StyleProperty<float>, int)>();

        private readonly List<(StyleProperty<double2> prop, int id)> _devicePixelVectorBindings =
            new List<(StyleProperty<double2>, int)>();

        private readonly Material _material;

        /// <summary>
        /// Create an applier for the given material instance. The material must not be null and must
        /// be a per-layer instance (not a shared material), so <c>SetFloat</c>/<c>SetColor</c> do not
        /// pollute other renderers.
        /// </summary>
        public ZoomStyleApplier(Material material)
        {
            if (material == null)
                throw new System.ArgumentNullException(nameof(material));
            _material = material;
        }

        // ── Binding API ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bind a <see cref="StyleProperty{float}"/> to a float shader property.
        /// Constant-kind bindings are applied immediately (no allocation); Zoom-kind bindings are
        /// queued for <see cref="ApplyZoom"/>. Pass a cached id from <c>ShaderProperties.PropertyId</c>,
        /// <c>ShaderProperties.Line.PropertyId</c>, or <c>ShaderProperties.Fill.PropertyId</c>.
        /// </summary>
        public void BindFloat(StyleProperty<float> prop, int id)
        {
            if (prop.IsZoomDependent)
                _floatBindings.Add((prop, id));
            else
                _material.SetFloat(id, prop.Evaluate(0.0));
        }

        /// <summary>
        /// Bind a <see cref="StyleProperty{CoreColor}"/> to a color shader property.
        /// Constant-kind bindings are applied immediately; Zoom-kind bindings are queued.
        /// Pass a cached id from <c>ShaderProperties.PropertyId</c>, <c>ShaderProperties.Line.PropertyId</c>, or <c>ShaderProperties.Fill.PropertyId</c>.
        /// </summary>
        public void BindColor(StyleProperty<CoreColor> prop, int id)
        {
            if (prop.IsZoomDependent)
                _colorBindings.Add((prop, id));
            else
                _material.SetColor(id, ToUnityColor(prop.Evaluate(0.0)));
        }

        /// <summary>
        /// Bind a px-valued <see cref="StyleProperty{float}"/> whose shader consumer measures against the
        /// PHYSICAL framebuffer (<see cref="PixelSpace.Device"/>): the style's logical px are multiplied by
        /// the device-pixel ratio in <see cref="ApplyZoom"/>, so a <c>line-width: 2</c> road is 2 LOGICAL px
        /// on every panel density (S107 Stage 1).
        ///
        /// <para>Unlike <see cref="BindFloat"/> this ALWAYS queues, even for a Constant-kind property: the
        /// value depends on the ratio, which is not known at bind time and can change live (a window dragged
        /// between panels). The bind-time shortcut would freeze it at whatever the first frame's ratio was.</para>
        /// </summary>
        public void BindDevicePixelFloat(StyleProperty<float> prop, int id)
            => _devicePixelFloatBindings.Add((prop, id));

        /// <summary>
        /// The <see cref="double2"/> counterpart of <see cref="BindDevicePixelFloat"/> — a px-valued
        /// two-component property (the <c>*-translate</c> pair) bound to a <c>float4</c> shader uniform as
        /// <c>(x, y, 0, 0)</c>. Always queued, for the same reason.
        /// </summary>
        public void BindDevicePixelVector(StyleProperty<double2> prop, int id)
            => _devicePixelVectorBindings.Add((prop, id));

        // ── Per-frame evaluation ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluate all Zoom-kind bindings at <paramref name="zoom"/> and push the results into the
        /// layer material. This is the per-frame hot path — it must not allocate.
        ///
        /// Two typed for-loops — no boxing, no union struct, no BindingKind enum branch.
        /// Guaranteed alloc-free when all stop outputs are Constant-folded (the parser does this at
        /// parse time for all constant stop outputs in <see cref="MapRenderer.Core.Expressions.ExpressionParser"/>).
        /// </summary>
        /// <param name="zoom">The current map zoom level.</param>
        /// <param name="devicePixelRatio">Physical ÷ logical px for the panel this frame — the factor the
        /// device-pixel bindings (<see cref="BindDevicePixelFloat"/>) apply. The zoom/color bindings ignore
        /// it entirely; only px-valued properties whose consumer measures the physical framebuffer scale.</param>
        public void ApplyZoom(double zoom, double devicePixelRatio)
        {
            // Float bindings — plain for-loop, no allocation.
            for (int i = 0; i < _floatBindings.Count; i++)
            {
                var (prop, id) = _floatBindings[i];
                _material.SetFloat(id, prop.Evaluate(zoom));
            }

            // Color bindings — plain for-loop, no allocation.
            for (int i = 0; i < _colorBindings.Count; i++)
            {
                var (prop, id) = _colorBindings[i];
                _material.SetColor(id, ToUnityColor(prop.Evaluate(zoom)));
            }

            // Device-pixel float bindings — logical px × dpr. Same plain-for shape; the conversion is a
            // static over doubles, so this stays boxing- and closure-free.
            for (int i = 0; i < _devicePixelFloatBindings.Count; i++)
            {
                var (prop, id) = _devicePixelFloatBindings[i];
                _material.SetFloat(id,
                    (float)DeviceScaling.LogicalToDevicePx(prop.Evaluate(zoom), PixelSpace.Device, devicePixelRatio));
            }

            // Device-pixel vector bindings — both components through the same conversion. Unity boundary
            // cast (double2 → Vector4) happens here, at the SetVector call site.
            for (int i = 0; i < _devicePixelVectorBindings.Count; i++)
            {
                var (prop, id) = _devicePixelVectorBindings[i];
                double2 logical = prop.Evaluate(zoom);
                _material.SetVector(id, new Vector4(
                    (float)DeviceScaling.LogicalToDevicePx(logical.x, PixelSpace.Device, devicePixelRatio),
                    (float)DeviceScaling.LogicalToDevicePx(logical.y, PixelSpace.Device, devicePixelRatio),
                    0f, 0f));
            }
        }

        // ── Color conversion ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Convert a Core expression <see cref="CoreColor"/> (sRGB [0,1] doubles) to a
        /// <see cref="UnityEngine.Color"/> (sRGB [0,1] floats): direct component cast, no gamma conversion.
        /// </summary>
        public static UnityEngine.Color ToUnityColor(CoreColor c)
            => new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
    }
}
