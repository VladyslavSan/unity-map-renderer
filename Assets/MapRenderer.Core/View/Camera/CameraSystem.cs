using System;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// S45 D3/D5: Stateful Core camera holder. Owns the current camera state and an optional
    /// in-flight animation. Engine-free; takes explicit <c>dt</c> from the caller (MapView feeds
    /// <c>Time.deltaTime</c>; headless tests feed deterministic values).
    ///
    /// <para><b>D3 — two paths:</b>
    /// <list type="bullet">
    ///   <item><see cref="Apply"/> with <c>Duration==0</c>: instant/jumpTo fast path — sets current
    ///     props, clears in-flight animation, <b>zero heap allocation</b> (struct update only).</item>
    ///   <item><see cref="Apply"/> with <c>Duration&gt;0</c>: stores target props + starts timer.
    ///     The <see cref="AnimationState"/> class holds the animation state (one alloc per easeTo).</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>D7 — Interruption:</b> a new <see cref="Apply"/> replaces any in-flight animation,
    /// starting from the *current interpolated* value (stays smooth).</para>
    ///
    /// <para><b>Framing constants</b> (<see cref="ReferenceViewportHeightPx"/>,
    /// <see cref="VerticalFovDeg"/>) are provided at construction time and used for the zoom↔distance
    /// conversion and clip-plane computation.</para>
    /// </summary>
    public sealed class CameraSystem
    {
        // ── Framing constants (moved from MapController — deterministic, not Camera.pixelHeight) ──
        public readonly double ReferenceViewportHeightPx;
        public readonly double VerticalFovDeg;

        // ── Current state ─────────────────────────────────────────────────────────────────────────
        private CameraProperties _current;

        // ── In-flight animation (null = no animation in progress; D3/D7) ─────────────────────────
        private AnimationState _animation; // class so null means "no animation"

        // ── Completion callback seam (D7) ─────────────────────────────────────────────────────────
        /// <summary>
        /// Optional callback invoked when an animation finishes (elapsed >= duration).
        /// Called from <see cref="Advance"/> on the frame it completes.
        /// Seam for chaining; full chaining is a follow-up.
        /// </summary>
        public Action OnAnimationComplete;

        // ── Construction ──────────────────────────────────────────────────────────────────────────

        public CameraSystem(CameraProperties initial,
                            double referenceViewportHeightPx = 1080.0,
                            double verticalFovDeg             = 60.0)
        {
            _current                  = initial;
            ReferenceViewportHeightPx = referenceViewportHeightPx;
            VerticalFovDeg            = verticalFovDeg;
        }

        // ── Public state ──────────────────────────────────────────────────────────────────────────

        /// <summary>The current (possibly mid-animation) camera properties.</summary>
        public CameraProperties Current => _current;

        /// <summary>True while an animation is in flight (elapsed &lt; duration).</summary>
        public bool IsAnimating => _animation != null;

        // ── Apply ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies a <see cref="CameraPropertiesUpdate"/> patch with the given animation.
        ///
        /// <para><b>Instant path (Duration==0):</b> merges the patch over <see cref="Current"/>,
        /// clears any in-flight animation. Struct-only path — no heap allocation.</para>
        ///
        /// <para><b>Animated path (Duration&gt;0):</b> resolves the TARGET props from the current
        /// state + patch; starts an animation from current to target. Replaces any prior animation,
        /// starting from the current *interpolated* value (D7).</para>
        ///
        /// <para>An empty patch with <see cref="CameraAnimation.Instant"/> is a no-op (no work).</para>
        /// </summary>
        public void Apply(CameraPropertiesUpdate update, CameraAnimation animation)
        {
            if (update.IsEmpty && animation.IsInstant)
                return; // trivial no-op

            if (animation.IsInstant)
            {
                // ── Instant / jumpTo fast path ─────────────────────────────────────────────────
                // Merge patch over current; clear any in-flight animation. Zero heap allocation.
                _current   = update.ApplyTo(_current, ReferenceViewportHeightPx, VerticalFovDeg);
                _animation = null;
            }
            else
            {
                // ── Animated path ──────────────────────────────────────────────────────────────
                // Target = patch merged over current (D7: current is already the interpolated value).
                CameraProperties target = update.ApplyTo(_current, ReferenceViewportHeightPx, VerticalFovDeg);
                _animation = new AnimationState(_current, target, animation);
            }
        }

        // ── Advance ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Advances the camera by <paramref name="dt"/> seconds. Call this as the first step in
        /// MapView.UpdateFrame so the pose consumers see the post-update camera (D5).
        ///
        /// <para>If no animation is in flight, this is a no-op (steady-state free path).</para>
        ///
        /// <para>When an animation reaches its target (<c>elapsed &ge; duration</c>), <see cref="Current"/>
        /// snaps to the target and <see cref="IsAnimating"/> clears. The optional
        /// <see cref="OnAnimationComplete"/> callback is invoked once.</para>
        /// </summary>
        public void Advance(double dt)
        {
            if (_animation == null) return;

            _animation.Elapsed += dt;

            if (_animation.Elapsed >= _animation.Anim.Duration)
            {
                // Animation complete: snap to target, clear state.
                _current   = _animation.Target;
                _animation = null;
                OnAnimationComplete?.Invoke();
            }
            else
            {
                // Interpolate in eased-t space.
                double t = _animation.Elapsed / _animation.Anim.Duration;
                double easedT = _animation.Anim.Easing == CameraEasing.EaseTo
                    ? EasingFunctions.SmoothStep(t)
                    : t; // fallback: linear (future easings extend here)

                _current = CameraPoseMath.Interpolate(_animation.From, _animation.Target, easedT);
            }
        }

        // ── Clip plane helpers ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes near clip plane from altitude (S42 D3: near = altitude * 0.01, min 0.1).
        /// </summary>
        public static double NearClip(double altitude) => Math.Max(0.1, altitude * 0.01);

        /// <summary>
        /// Computes far clip plane from altitude (S42 D3: far = altitude * 4).
        /// </summary>
        public static double FarClip(double altitude) => altitude * 4.0;

        // ── Animation state (heap object, one-per-animation) ──────────────────────────────────────
        /// <summary>In-flight animation state. Heap-allocated once per animated Apply.</summary>
        private sealed class AnimationState
        {
            public readonly CameraProperties From;
            public readonly CameraProperties Target;
            public readonly CameraAnimation  Anim;
            public          double           Elapsed;

            public AnimationState(CameraProperties from, CameraProperties target, CameraAnimation anim)
            {
                From    = from;
                Target  = target;
                Anim    = anim;
                Elapsed = 0.0;
            }
        }
    }
}
