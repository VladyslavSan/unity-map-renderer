using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Per-layer zoom → material-uniform applier: "build-once, restyle via uniforms". <see cref="ApplyZoom"/>
    /// re-evaluates only queued bindings (a settled Constant is set once at bind time) and writes the layer's
    /// own Material via <c>SetFloat</c>/<c>SetColor</c>, never a <c>MaterialPropertyBlock</c>, which disables
    /// the SRP Batcher. Four typed lists keep it free of boxing. A rebind after <see cref="SetTransition"/>
    /// re-targets the entry: both endpoints evaluate at the live zoom, and time moves only the mix.
    /// Limitation: <see cref="BindDevicePixelFloat"/>/<see cref="BindDevicePixelVector"/> are the ONE seam
    /// where px meets the device-pixel ratio, but nothing stops a new px property from using
    /// <see cref="BindFloat"/>; docs/device-pixel-ratio-design.md § "The table is the guard, not the compiler".
    /// </summary>
    public sealed class ZoomStyleApplier
    {
        // ── Binding entry — NOT a ValueTuple (an 8-element tuple boxes into TRest and allocates) ─────

        private struct Binding<T>
        {
            public int Id;
            public StyleProperty<T> Target;
            public StyleProperty<T> Origin;        // null => settled
            public double StartSeconds;             // armed-at + delay
            public double DurationSeconds;
            public bool Discrete;                   // switch at StartSeconds instead of interpolating
            public bool PushEveryFrame;              // settled behaviour: zoom-dependent / always pushed
            public T LastPushed;                     // the value pushed on the previous ApplyZoom
            public bool ScaledByFade;                // multiply by the layer fade at the SetFloat
        }

        // ── Typed binding lists — no boxing ──────────────────────────────────────────────────────

        private readonly List<Binding<float>>     _floatBindings = new List<Binding<float>>();
        private readonly List<Binding<CoreColor>> _colorBindings = new List<Binding<CoreColor>>();

        // ── Device-pixel bindings — logical px in, the consumer's space out ──────────────────────
        // Separate lists: they never take the bind-time constant shortcut (see BindDevicePixelFloat).

        private readonly List<Binding<float>>   _devicePixelFloatBindings   = new List<Binding<float>>();
        private readonly List<Binding<double2>> _devicePixelVectorBindings = new List<Binding<double2>>();

        private readonly Material _material;

        // ── Pending transition — set by SetTransition, consumed by the NEXT Bind* calls ─────────────

        private StyleTransition _pendingTransition;
        private double _pendingNowSeconds;
        private bool _transitionArmed;

        // ── Layer fade (the minzoom/maxzoom/visibility draw gate) ────────────────────────────────────
        // RenderLayerSet owns the ease and pushes the RESOLVED amount; 1 means no gate.

        private float _fade = 1f;
        private bool  _fadeMoved;   // set by SetFade, consumed by the next ApplyZoom

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

        // ── Restyle arming ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Arm the SUBSEQUENT <c>Bind*</c> calls as re-targets of an existing binding rather than a
        /// fresh build. Never called by <c>TryCreate</c>, so a first bind is never a re-target.
        /// </summary>
        internal void SetTransition(in StyleTransition transition, double nowSeconds)
        {
            _pendingTransition = transition;
            _pendingNowSeconds = nowSeconds;
            _transitionArmed = true;
        }

        /// <summary>
        /// Store the RESOLVED fade amount <see cref="RenderLayerSet"/> eased. An unchanged amount returns
        /// immediately, which is what lets the next <see cref="ApplyZoom"/> skip re-pushing a settled
        /// opacity binding.
        /// </summary>
        /// <param name="amount">0 (absent) to 1 (fully drawn).</param>
        internal void SetFade(float amount)
        {
            if (amount == _fade) return;
            _fade      = amount;
            _fadeMoved = true;
        }

        /// <summary>
        /// Seed the fade at build time, with no ease — the fade analogue of "a first bind is never a
        /// re-target". Without it a layer built out of range would fade down from 1 over the transition
        /// instead of starting hidden. Must run BEFORE the paint is bound (see <see cref="BindOpacity"/>).
        /// <see cref="RenderLayerSet.Build"/> seeds its own ease from the SAME
        /// <c>StyleLayer.IsVisibleAtZoom(initialZoom)</c> predicate, so the two always agree.
        /// </summary>
        /// <param name="value">0 (absent) to 1 (fully drawn).</param>
        internal void SeedFade(float value)
        {
            _fade      = value;
            _fadeMoved = false;
        }

        /// <summary>The smallest alpha an 8-bit framebuffer can show. ONE definition — a second copy of
        /// this threshold is how a threshold drifts.</summary>
        internal const float VisibleOpacityEpsilon = 1f / 255f;

        /// <summary>
        /// True when fade times the authored opacity falls below one 8-bit step. Each
        /// <see cref="IFadeableRenderLayer"/>'s <c>PaintsSomething</c> negates it for
        /// <see cref="Backend.ITileRenderBackend.SetLayerVisible"/>, so an out-of-range layer and a transparent
        /// one submit no draw alike. A layer mid-fade reads false while the product is still showable.
        /// </summary>
        internal bool EffectiveOpacityIsZero => _fade * AuthoredOpacity < VisibleOpacityEpsilon;

        /// <summary>
        /// The UNSCALED authored opacity last pushed, or 1 when this layer binds none.
        /// <see cref="ApplyZoom"/> rewrites it in the very call the gate is pushed after, so it is never a
        /// frame stale; a settled Constant keeps its bind-time value, which is its value at every zoom. A
        /// feature-dependent opacity binds a constant 1 (<c>Materials.MaterialFactory</c>) and so reads 1
        /// here — a value that varies per feature can never gate a whole layer out.
        /// </summary>
        private float AuthoredOpacity
        {
            get
            {
                for (int i = 0; i < _floatBindings.Count; i++)
                    if (_floatBindings[i].ScaledByFade) return _floatBindings[i].LastPushed;
                return 1f;
            }
        }

        /// <summary>Entries currently easing (across all four lists) — read by teeth only.</summary>
        internal int TransitioningCount
            => CountTransitioning(_floatBindings) + CountTransitioning(_colorBindings)
             + CountTransitioning(_devicePixelFloatBindings) + CountTransitioning(_devicePixelVectorBindings);

        private static int CountTransitioning<T>(List<Binding<T>> list)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Origin != null) n++;
            return n;
        }

        // ── Binding API ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bind a <see cref="StyleProperty{float}"/> to a float shader property.
        /// Constant-kind bindings are applied immediately (no allocation); Zoom-kind bindings are
        /// queued for <see cref="ApplyZoom"/>. Pass a cached id from <c>ShaderProperties.PropertyId</c>,
        /// <c>ShaderProperties.Line.PropertyId</c>, or <c>ShaderProperties.Fill.PropertyId</c>.
        /// </summary>
        public void BindFloat(StyleProperty<float> prop, int id)
            => BindOrRetarget(_floatBindings, prop, id, discrete: false, prop.IsZoomDependent,
                v => _material.SetFloat(id, v));

        /// <summary>
        /// As <see cref="BindFloat"/>, but the pushed value is scaled by the layer fade — the ONE
        /// binding the <c>minzoom</c>/<c>maxzoom</c>/<c>visibility</c> gate rides. Every opacity writer goes
        /// through here, so fade has a single writer and no direct <c>SetFloat</c> competes with it.
        /// The bind-time push is scaled too: a Constant binding is pushed once and then skipped forever, so
        /// an unscaled push here would make a seeded fade of 0 invisible to the material.
        /// </summary>
        internal void BindOpacity(StyleProperty<float> prop, int id)
            => BindOrRetarget(_floatBindings, prop, id, discrete: false, prop.IsZoomDependent,
                v => _material.SetFloat(id, v * _fade), scaledByFade: true);

        /// <summary>
        /// As <see cref="BindFloat"/>, but the binding switches at the transition's start instant
        /// instead of interpolating — an enum-like property such as <c>*-translate-anchor</c>.
        /// </summary>
        internal void BindDiscreteFloat(StyleProperty<float> prop, int id)
            => BindOrRetarget(_floatBindings, prop, id, discrete: true, prop.IsZoomDependent,
                v => _material.SetFloat(id, v));

        /// <summary>
        /// Bind a <see cref="StyleProperty{CoreColor}"/> to a color shader property.
        /// Constant-kind bindings are applied immediately; Zoom-kind bindings are queued.
        /// Pass a cached id from <c>ShaderProperties.PropertyId</c>, <c>ShaderProperties.Line.PropertyId</c>, or <c>ShaderProperties.Fill.PropertyId</c>.
        /// </summary>
        public void BindColor(StyleProperty<CoreColor> prop, int id)
            => BindOrRetarget(_colorBindings, prop, id, discrete: false, prop.IsZoomDependent,
                v => _material.SetColor(id, ToUnityColor(v)));

        /// <summary>
        /// Bind a px-valued <see cref="StyleProperty{float}"/> whose shader consumer measures against the
        /// PHYSICAL framebuffer (<see cref="PixelSpace.Device"/>): the style's logical px are multiplied by
        /// the device-pixel ratio in <see cref="ApplyZoom"/>, so a <c>line-width: 2</c> road is 2 LOGICAL px
        /// on every panel density. It ALWAYS queues, even for a Constant, because the ratio can change live
        /// (a window dragged between panels).
        /// </summary>
        public void BindDevicePixelFloat(StyleProperty<float> prop, int id)
            => BindOrRetarget(_devicePixelFloatBindings, prop, id, discrete: false, pushEveryFrame: true, pushNow: null);

        /// <summary>
        /// The <see cref="double2"/> counterpart of <see cref="BindDevicePixelFloat"/> — a px-valued
        /// two-component property (the <c>*-translate</c> pair) bound to a <c>float4</c> shader uniform as
        /// <c>(x, y, 0, 0)</c>. Always queued, for the same reason.
        /// </summary>
        public void BindDevicePixelVector(StyleProperty<double2> prop, int id)
            => BindOrRetarget(_devicePixelVectorBindings, prop, id, discrete: false, pushEveryFrame: true, pushNow: null);

        /// <summary>True only when both properties are provably Constant AND evaluate to the same value —
        /// the one case a retarget can cheaply prove is not a real change.</summary>
        private static bool ValuesEqualIfBothConstant<T>(StyleProperty<T> a, StyleProperty<T> b)
        {
            if (a.Kind != MapRenderer.Core.Expressions.ExpressionKind.Constant) return false;
            if (b.Kind != MapRenderer.Core.Expressions.ExpressionKind.Constant) return false;
            return EqualityComparer<T>.Default.Equals(a.Evaluate(0.0), b.Evaluate(0.0));
        }

        /// <summary>
        /// Add-or-retarget one binding. Not armed (or the id is new): a plain bind —
        /// queue Zoom-kind, push Constant-kind once. Armed and the id exists: swap in the new
        /// <paramref name="prop"/> as <c>Target</c>, keep (or snapshot) the old value as <c>Origin</c>,
        /// and arm the ease window — <see cref="ApplyZoom"/> does the rest.
        /// </summary>
        private void BindOrRetarget<T>(List<Binding<T>> list, StyleProperty<T> prop, int id, bool discrete,
            bool pushEveryFrame, System.Action<T> pushNow, bool scaledByFade = false)
        {
            int i = -1;
            for (int k = 0; k < list.Count; k++)
            {
                if (list[k].Id == id) { i = k; break; }
            }

            if (i < 0 || !_transitionArmed)
            {
                var added = new Binding<T>
                {
                    Id = id,
                    Target = prop,
                    Origin = null,
                    Discrete = discrete,
                    PushEveryFrame = pushEveryFrame,
                    ScaledByFade = scaledByFade,
                };
                if (!pushEveryFrame)
                {
                    added.LastPushed = prop.Evaluate(0.0);
                    pushNow(added.LastPushed);
                }
                list.Add(added);
                return;
            }

            var b = list[i];
            if (_pendingTransition.IsInstant)
            {
                // A plain bind that arms nothing: an instant restyle must leave TransitioningCount == 0.
                b.Target = prop;
                b.Origin = null;
                b.Discrete = discrete;
                b.PushEveryFrame = pushEveryFrame;
                b.ScaledByFade = scaledByFade;
                if (!pushEveryFrame)
                {
                    b.LastPushed = prop.Evaluate(0.0);
                    pushNow(b.LastPushed);
                }
                list[i] = b;
                return;
            }

            // A restyle rebinds every paint property, so an unchanged settled Constant must arm NOTHING.
            // Any other kind re-arms rather than risk missing a real change.
            if (b.Origin == null && ValuesEqualIfBothConstant(b.Target, prop))
            {
                b.Target = prop;
                b.Discrete = discrete;
                b.PushEveryFrame = pushEveryFrame;
                b.ScaledByFade = scaledByFade;
                list[i] = b;
                return;
            }

            // Settled -> ease from the settled property itself (may still be zoom-dependent).
            // Mid-ease -> interrupt: ease from the value pushed last frame, a Constant.
            b.Origin = b.Origin == null ? b.Target : new StyleProperty<T>(b.LastPushed);
            b.Target = prop;
            b.StartSeconds = _pendingNowSeconds + _pendingTransition.DelaySeconds;
            b.DurationSeconds = _pendingTransition.DurationSeconds;
            b.Discrete = discrete;
            b.PushEveryFrame = pushEveryFrame;
            b.ScaledByFade = scaledByFade;
            list[i] = b;
        }

        // ── Per-frame evaluation ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluate every binding at the live inputs and push the results into the layer material.
        /// This is the per-frame hot path — it must not allocate. Both the origin and the target of
        /// an easing binding are evaluated at the LIVE zoom every frame; time only moves the mix
        /// between them — a zoom change mid-ease moves both endpoints.
        /// </summary>
        /// <param name="inputs">The live zoom, device-pixel ratio, and wall clock for this frame.</param>
        public void ApplyZoom(in StyleFrameInputs inputs)
        {
            double zoom = inputs.Zoom, now = inputs.NowSeconds, dpr = inputs.DevicePixelRatio;
            bool fadeMoved = _fadeMoved;
            _fadeMoved = false;

            // Float bindings — plain for-loop, no allocation.
            for (int i = 0; i < _floatBindings.Count; i++)
            {
                var b = _floatBindings[i];
                if (b.Origin == null)
                {
                    // A fade move re-pushes ONLY the opacity binding. Without the ScaledByFade test
                    // every settled float on the layer would be re-evaluated for the whole fade.
                    if (!b.PushEveryFrame && !(b.ScaledByFade && fadeMoved)) continue;
                    b.LastPushed = b.Target.Evaluate(zoom);
                }
                else
                {
                    b.LastPushed = EvalFloat(ref b, zoom, now);
                }
                // LastPushed stays the UNSCALED authored value — it is the interrupt origin a re-target
                // eases from, and scaling it would make that ease start from a faded value.
                _material.SetFloat(b.Id, b.ScaledByFade ? b.LastPushed * _fade : b.LastPushed);
                _floatBindings[i] = b;
            }

            // Color bindings — plain for-loop, no allocation.
            for (int i = 0; i < _colorBindings.Count; i++)
            {
                var b = _colorBindings[i];
                if (b.Origin == null)
                {
                    if (!b.PushEveryFrame) continue;
                    b.LastPushed = b.Target.Evaluate(zoom);
                }
                else
                {
                    b.LastPushed = EvalColor(ref b, zoom, now);
                }
                _material.SetColor(b.Id, ToUnityColor(b.LastPushed));
                _colorBindings[i] = b;
            }

            // Device-pixel float bindings — logical px × dpr. Same plain-for shape; always queued, so
            // Origin == null here always means PushEveryFrame == true (never the settled-constant skip).
            for (int i = 0; i < _devicePixelFloatBindings.Count; i++)
            {
                var b = _devicePixelFloatBindings[i];
                b.LastPushed = b.Origin == null ? b.Target.Evaluate(zoom) : EvalFloat(ref b, zoom, now);
                _material.SetFloat(b.Id,
                    (float)DeviceScaling.LogicalToDevicePx(b.LastPushed, PixelSpace.Device, dpr));
                _devicePixelFloatBindings[i] = b;
            }

            // Device-pixel vector bindings — both components through the same conversion. Unity boundary
            // cast (double2 → Vector4) happens here, at the SetVector call site.
            for (int i = 0; i < _devicePixelVectorBindings.Count; i++)
            {
                var b = _devicePixelVectorBindings[i];
                double2 logical = b.Origin == null ? b.Target.Evaluate(zoom) : EvalVector(ref b, zoom, now);
                b.LastPushed = logical;
                _material.SetVector(b.Id, new Vector4(
                    (float)DeviceScaling.LogicalToDevicePx(logical.x, PixelSpace.Device, dpr),
                    (float)DeviceScaling.LogicalToDevicePx(logical.y, PixelSpace.Device, dpr),
                    0f, 0f));
                _devicePixelVectorBindings[i] = b;
            }
        }

        // ── The one ease, three times over (per T) — endpoints at live zoom, time moves only the mix ──
        // Non-obvious why: the delay hold tests elapsed < 0 on the wall clock, so a zero-duration binding
        // still honors its delay. A Discrete binding settles when the delay ends. The `t <= 0` arm stays,
        // because Mix(A, B, 0) is not bit-exactly A (StyleTransitionTests.Transition_Endpoints_AreExact).

        private static float EvalFloat(ref Binding<float> b, double zoom, double now)
        {
            double elapsed = now - b.StartSeconds;
            if (elapsed < 0.0) return b.Origin.Evaluate(zoom);
            if (b.Discrete) { b.Origin = null; return b.Target.Evaluate(zoom); }

            double t = b.DurationSeconds <= 0.0 ? 1.0 : math.saturate(elapsed / b.DurationSeconds);
            if (t <= 0.0) return b.Origin.Evaluate(zoom);
            float target = b.Target.Evaluate(zoom);
            if (t >= 1.0) { b.Origin = null; return target; }
            float origin = b.Origin.Evaluate(zoom);
            return math.lerp(origin, target, (float)math.smoothstep(0.0, 1.0, t));
        }

        private static CoreColor EvalColor(ref Binding<CoreColor> b, double zoom, double now)
        {
            double elapsed = now - b.StartSeconds;
            if (elapsed < 0.0) return b.Origin.Evaluate(zoom);
            if (b.Discrete) { b.Origin = null; return b.Target.Evaluate(zoom); }

            double t = b.DurationSeconds <= 0.0 ? 1.0 : math.saturate(elapsed / b.DurationSeconds);
            if (t <= 0.0) return b.Origin.Evaluate(zoom);
            CoreColor target = b.Target.Evaluate(zoom);
            if (t >= 1.0) { b.Origin = null; return target; }
            CoreColor origin = b.Origin.Evaluate(zoom);
            return CoreColor.MixPremultiplied(origin, target, math.smoothstep(0.0, 1.0, t));
        }

        private static double2 EvalVector(ref Binding<double2> b, double zoom, double now)
        {
            double elapsed = now - b.StartSeconds;
            if (elapsed < 0.0) return b.Origin.Evaluate(zoom);
            if (b.Discrete) { b.Origin = null; return b.Target.Evaluate(zoom); }

            double t = b.DurationSeconds <= 0.0 ? 1.0 : math.saturate(elapsed / b.DurationSeconds);
            if (t <= 0.0) return b.Origin.Evaluate(zoom);
            double2 target = b.Target.Evaluate(zoom);
            if (t >= 1.0) { b.Origin = null; return target; }
            double2 origin = b.Origin.Evaluate(zoom);
            return math.lerp(origin, target, math.smoothstep(0.0, 1.0, t));
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
