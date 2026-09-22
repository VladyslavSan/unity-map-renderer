using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Per-layer zoom → material-uniform applier: "build-once, restyle via uniforms".
    ///
    /// Holds typed binding lists (<see cref="StyleProperty{float}"/> and
    /// <see cref="StyleProperty{CoreColor}"/>) keyed to shader property IDs. On each call to
    /// <see cref="ApplyZoom"/> it evaluates only the Zoom-kind bindings (Constant bindings are set
    /// once at bind-time and never re-evaluated). Drives the per-layer Material instance directly via
    /// <c>SetFloat</c>/<c>SetColor</c> — never <c>MaterialPropertyBlock</c>, which disables the SRP
    /// Batcher (ARCHITECTURE).
    ///
    /// This is also the ONE seam where a px-valued style property meets the device-pixel ratio.
    /// <see cref="BindDevicePixelFloat"/> / <see cref="BindDevicePixelVector"/> name the consumer's
    /// <see cref="PixelSpace"/> at the binding site, with no default argument to forget. That does not
    /// close the hole: a px property routed through these bindings must state its space, but nothing stops
    /// a new px property being bound via <see cref="BindFloat"/> and keeping the wrong basis. Unitless
    /// properties have no pixel space, so forcing a <see cref="PixelSpace"/> on every binding would be
    /// wrong; the guard against a missed property is the px-surface table in
    /// <c>docs/device-pixel-ratio-design.md</c> and its teeth, not the compiler. Those bindings always
    /// queue (the ratio is a per-frame input, not a bind-time constant), so a Constant <c>line-width</c>
    /// costs one <c>SetFloat</c> per layer per frame.
    ///
    /// <c>StyleProperty&lt;float&gt;</c> and <c>StyleProperty&lt;Color&gt;</c>
    /// are distinct closed generic types and cannot share one binding list without boxing. Four typed
    /// lists guarantee zero boxing in <see cref="ApplyZoom"/>, which is the alloc-free hot path.
    ///
    /// A rebind (<see cref="SetTransition"/> then a <c>Bind*</c> call
    /// with an id already in the list) re-targets the entry instead of replacing it — both the old and
    /// new endpoint are evaluated at the LIVE zoom every frame; time only moves the mix between them.
    /// A first bind is never a re-target: <see cref="SetTransition"/> is never called before the initial
    /// build.
    ///
    /// Property IDs are cached via <see cref="Shader.PropertyToID"/> at bind-time to eliminate
    /// per-call string lookup allocations. The binding loops are plain <c>for</c> over typed
    /// <see cref="List{T}"/>s, which use struct enumerators and have no closure overhead.
    ///
    /// Clean-room: design follows this repo's own design docs and the public MapLibre Style Spec.
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
            public bool PushEveryFrame;              // settled behaviour: today's zoom-dependent / always
            public T LastPushed;                     // the value pushed on the previous ApplyZoom
            public bool ScaledByFade;                // multiply by the layer fade at the SetFloat
        }

        // ── Typed binding lists — no boxing ──────────────────────────────────────────────────────

        private readonly List<Binding<float>>     _floatBindings = new List<Binding<float>>();
        private readonly List<Binding<CoreColor>> _colorBindings = new List<Binding<CoreColor>>();

        // ── Device-pixel bindings — logical px in, the consumer's space out ──────────────────────
        // Separate lists rather than a flag on the two above: these carry the SPACE as part of their
        // identity, and they can never take the bind-time constant shortcut (see BindDevicePixelFloat).

        private readonly List<Binding<float>>   _devicePixelFloatBindings   = new List<Binding<float>>();
        private readonly List<Binding<double2>> _devicePixelVectorBindings = new List<Binding<double2>>();

        private readonly Material _material;

        // ── Pending transition — set by SetTransition, consumed by the NEXT Bind* calls ─────────────

        private StyleTransition _pendingTransition;
        private double _pendingNowSeconds;
        private bool _transitionArmed;

        // ── Layer fade (the minzoom/maxzoom/visibility draw gate) ────────────────────────────────────
        // Not a style property, and not eased here: RenderLayerSet owns the target, the ease and the clock
        // and pushes the RESOLVED amount. This side keeps only the value and the multiply it feeds.
        // Starts at 1, so a layer with no gate behaves exactly as before.

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
        /// <c>StyleLayer.IsVisibleAtZoom(initialZoom)</c> predicate, so the two agree by construction.
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
        /// True when this layer paints nothing the framebuffer can show: fade times the authored
        /// opacity falls below one 8-bit step. Negated into each <see cref="IFadeableRenderLayer"/>
        /// implementer's own <c>PaintsSomething</c>, which <see cref="Backend.ITileRenderBackend.SetLayerVisible"/>
        /// reads, so a layer outside its zoom range and one whose opacity has fallen below the
        /// threshold are one case — neither submits a draw. A layer mid-fade reads false while the
        /// product is still showable, which leaves the fade something to blend.
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
        /// on every panel density.
        ///
        /// <para>Unlike <see cref="BindFloat"/> this ALWAYS queues, even for a Constant-kind property: the
        /// value depends on the ratio, which is not known at bind time and can change live (a window dragged
        /// between panels). The bind-time shortcut would freeze it at whatever the first frame's ratio was.</para>
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
        /// Add-or-retarget one binding. Not armed (or the id is new): behaves exactly as today —
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
                // Degenerates to today's code: set Target, leave Origin null, and — if not pushed
                // every frame — write the value immediately. Arms nothing (criteria 3/4 need
                // TransitioningCount == 0 after an instant restyle).
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

            // A restyle rebinds EVERY paint property wholesale (no diffing), so a property whose
            // VALUE did not change still reaches here as a "retarget". Without this check it would arm a
            // pointless transition (Origin != null for the full duration, settling on the same value it
            // started at) — a discriminant-only restyle must arm NOTHING, not just nothing visible.
            // Scoped to the settled case; an interrupted transition already has a genuine reason
            // to keep easing. Both sides must be provably Constant to compare cheaply — a Zoom/Feature/
            // Composite property conservatively re-arms rather than risk missing a real change.
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
        //
        // The delay-hold check runs on the WALL CLOCK directly (elapsed < 0), never through the
        // Duration-normalized `t` — a zero-duration binding must still honor a nonzero delay, which a
        // `Duration <= 0 => t = 1` shortcut would skip. A Discrete binding switches the instant the
        // delay ends and settles immediately — it has nothing left to interpolate, so there is no
        // reason to keep it "transitioning" for the rest of the duration window. A continuous binding
        // still needs the separate `t <= 0` arm below: even at elapsed == 0 exactly, Mix(A, B, 0) is
        // not bit-exactly A (tooth #3).

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
