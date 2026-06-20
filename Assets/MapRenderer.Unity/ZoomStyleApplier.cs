using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Per-layer zoom → material-uniform applier for S11: "build-once, restyle via uniforms".
    ///
    /// Holds a set of <see cref="PaintPropertyEvaluator"/> bindings keyed to shader property IDs.
    /// On each call to <see cref="ApplyZoom"/> it evaluates only the Zoom-kind bindings (Constant
    /// bindings are set once at bind-time and never re-evaluated).  Drives the per-layer Material
    /// instance directly via <c>SetFloat</c>/<c>SetColor</c> — never <c>MaterialPropertyBlock</c>,
    /// which disables the SRP Batcher (ARCHITECTURE §2).
    ///
    /// Property IDs are cached via <see cref="Shader.PropertyToID"/> at bind-time to eliminate
    /// per-call string lookup allocations.  The binding loop is a plain <c>for</c> over a
    /// <see cref="List{T}"/>, which uses a struct enumerator and has no closure overhead.
    ///
    /// Color conversion from <see cref="CoreColor"/> to <see cref="UnityEngine.Color"/> is a direct
    /// component copy at float precision, matching <see cref="LayerStack"/>'s existing convention.
    ///
    /// Clean-room: design follows the S11 plan and the public MapLibre Style Spec.
    /// </summary>
    public sealed class ZoomStyleApplier
    {
        private enum BindingKind { Float, Color }

        private readonly struct Binding
        {
            public readonly PaintPropertyEvaluator Evaluator;
            public readonly int PropertyId;
            public readonly BindingKind Kind;

            public Binding(PaintPropertyEvaluator evaluator, int propertyId, BindingKind kind)
            {
                Evaluator = evaluator;
                PropertyId = propertyId;
                Kind = kind;
            }
        }

        private readonly Material _material;

        // Zoom-dependent bindings (re-evaluated every ApplyZoom call).
        private readonly List<Binding> _zoomBindings = new List<Binding>();

        // Statically maps spec paint-property names to shader property IDs (cached once).
        // Key = property-id int, value = paint evaluator.

        /// <summary>
        /// Create an applier for the given material instance.  The material must not be null and must
        /// be a per-layer instance (not a shared material), so <c>SetFloat</c>/<c>SetColor</c> do not
        /// pollute other renderers.
        /// </summary>
        public ZoomStyleApplier(Material material)
        {
            if (material == null)
                throw new System.ArgumentNullException(nameof(material));
            _material = material;
        }

        // ---- binding API -------------------------------------------------------------------------

        /// <summary>
        /// Bind a <see cref="PaintPropertyEvaluator"/> to a float shader property.
        /// Constant-kind bindings are applied immediately; Zoom-kind bindings are queued for
        /// <see cref="ApplyZoom"/>.
        /// </summary>
        public void BindFloat(PaintPropertyEvaluator evaluator, string shaderPropertyName)
        {
            int id = Shader.PropertyToID(shaderPropertyName);
            if (evaluator.IsZoomDependent)
                _zoomBindings.Add(new Binding(evaluator, id, BindingKind.Float));
            else
                _material.SetFloat(id, (float)evaluator.EvaluateNumber(0.0));
        }

        /// <summary>
        /// Bind a <see cref="PaintPropertyEvaluator"/> to a color shader property.
        /// Constant-kind bindings are applied immediately; Zoom-kind bindings are queued for
        /// <see cref="ApplyZoom"/>.
        /// </summary>
        public void BindColor(PaintPropertyEvaluator evaluator, string shaderPropertyName)
        {
            int id = Shader.PropertyToID(shaderPropertyName);
            if (evaluator.IsZoomDependent)
                _zoomBindings.Add(new Binding(evaluator, id, BindingKind.Color));
            else
                _material.SetColor(id, ToUnityColor(evaluator.EvaluateColor(0.0)));
        }

        // ---- per-frame evaluation ----------------------------------------------------------------

        /// <summary>
        /// Evaluate all Zoom-kind bindings at <paramref name="zoom"/> and push the results into the
        /// layer material.  This is the per-frame hot path — it must not allocate.
        ///
        /// Guaranteed alloc-free when all stop outputs of the bound <c>interpolate</c>/<c>step</c>
        /// expressions are Constant-folded (the parser does this at parse time for all constant stop
        /// outputs, see <see cref="ExpressionParser"/>).  With Constant-folded stops, the evaluation
        /// path is: <c>ZoomExpression</c> (no alloc) → <c>InterpolateExpression</c>/<c>StepExpression</c>
        /// (struct-based) → <c>LiteralExpression</c> (no alloc).
        /// </summary>
        /// <param name="zoom">The current map zoom level.</param>
        public void ApplyZoom(double zoom)
        {
            // Plain for-loop over List<Binding> (struct-backed enumerator — no allocation).
            for (int i = 0; i < _zoomBindings.Count; i++)
            {
                Binding b = _zoomBindings[i];
                if (b.Kind == BindingKind.Float)
                {
                    double v = b.Evaluator.EvaluateNumber(zoom);
                    _material.SetFloat(b.PropertyId, (float)v);
                }
                else
                {
                    CoreColor c = b.Evaluator.EvaluateColor(zoom);
                    _material.SetColor(b.PropertyId, ToUnityColor(c));
                }
            }
        }

        // ---- color conversion --------------------------------------------------------------------

        /// <summary>
        /// Convert a Core expression <see cref="CoreColor"/> (sRGB [0,1] doubles) to a
        /// <see cref="UnityEngine.Color"/> (sRGB [0,1] floats).  Matching the existing
        /// <see cref="LayerStack"/> convention: direct component cast, no gamma conversion.
        /// </summary>
        public static UnityEngine.Color ToUnityColor(CoreColor c)
            => new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
    }
}
