using Unity.Mathematics;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// S45 D3: Describes the animation that accompanies a <see cref="CameraPropertiesUpdate"/>.
    ///
    /// <para><b>Duration == 0</b> is the instant/jumpTo fast path. No animation object is allocated
    /// (the caller passes <see cref="Instant"/> as a readonly static).</para>
    ///
    /// <para><b>Duration &gt; 0</b> eases the set fields toward target. Easing is controlled by
    /// <see cref="Easing"/>. Ship <c>easeTo</c> (monotonic) now; <c>flyTo</c> is a later strategy
    /// on the same framework.</para>
    /// </summary>
    public readonly struct CameraAnimation
    {
        /// <summary>Animation duration in seconds. 0 = instant (no allocation, no object).</summary>
        public readonly double Duration;

        /// <summary>Easing function to use for this animation.</summary>
        public readonly CameraEasing Easing;

        /// <summary>Instant/jumpTo path — zero duration, no allocation on the input path.</summary>
        public static readonly CameraAnimation Instant = new CameraAnimation(0.0);

        public CameraAnimation(double duration, CameraEasing easing = CameraEasing.EaseTo)
        {
            Duration = math.max(0.0, duration);
            Easing   = easing;
        }

        /// <summary>True when this animation is instant (Duration == 0).</summary>
        public bool IsInstant => Duration <= 0.0;
    }

    /// <summary>
    /// S45 D4: Supported easing functions.
    /// Ship <c>EaseTo</c> (monotonic smooth-step) now; <c>FlyTo</c> (parabolic zoom-out/in) is
    /// reserved for a future stage (explicitly OUT of scope for S45).
    /// </summary>
    public enum CameraEasing
    {
        /// <summary>Monotonic ease-in/out (smooth-step). Default for <c>easeTo</c>.</summary>
        EaseTo = 0,
    }

    /// <summary>
    /// Easing math helpers (engine-free, static).
    ///
    /// <para>Smooth-step: t_eased = t² × (3 − 2t). Satisfies: f(0)=0, f(1)=1, f'(0)=f'(1)=0
    /// (zero velocity at both ends → smooth appearance).</para>
    /// </summary>
    public static class EasingFunctions
    {
        /// <summary>
        /// Maps a linear <paramref name="t"/> ∈ [0,1] to a smooth-step value ∈ [0,1].
        /// Clamped to [0,1] so callers needn't guard the boundary.
        /// </summary>
        public static double SmoothStep(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
