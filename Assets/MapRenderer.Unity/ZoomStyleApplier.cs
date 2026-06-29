using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Style;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Per-layer zoom → material-uniform applier for S11/S60: "build-once, restyle via uniforms".
    ///
    /// Holds two typed binding lists (<see cref="StyleProperty{float}"/> and
    /// <see cref="StyleProperty{CoreColor}"/>) keyed to shader property IDs. On each call to
    /// <see cref="ApplyZoom"/> it evaluates only the Zoom-kind bindings (Constant bindings are set
    /// once at bind-time and never re-evaluated). Drives the per-layer Material instance directly via
    /// <c>SetFloat</c>/<c>SetColor</c> — never <c>MaterialPropertyBlock</c>, which disables the SRP
    /// Batcher (ARCHITECTURE §2).
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
        public void ApplyZoom(double zoom)
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
