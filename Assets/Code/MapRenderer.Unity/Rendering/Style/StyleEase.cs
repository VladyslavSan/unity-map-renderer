using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// One restyle ease window for a scene writer outside the render layers (sun, sky, haze). It uses the
    /// same <see cref="StyleTransition"/>, clock and smoothstep curve as <c>ZoomStyleApplier</c>:
    /// hold during the delay, then ease over the duration. Holds no references, so a tick never allocates.
    /// </summary>
    internal struct StyleEase
    {
        private double _startSeconds;
        private double _durationSeconds;

        /// <summary>True from <see cref="Arm"/> until the weight reaches 1 or <see cref="Stop"/> runs.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Starts a window at <paramref name="nowSeconds"/>, or stops any window when
        /// <paramref name="transition"/> is instant.</summary>
        public void Arm(in StyleTransition transition, double nowSeconds)
        {
            IsActive         = !transition.IsInstant;
            _startSeconds    = nowSeconds + transition.DelaySeconds;
            _durationSeconds = transition.DurationSeconds;
        }

        /// <summary>Ends the window without reaching its end, so the caller's last write stands.</summary>
        public void Stop() => IsActive = false;

        /// <summary>Steps the window to <paramref name="nowSeconds"/> and returns the eased mix, in [0, 1]. At its
        /// end the step clears <see cref="IsActive"/>, and the caller must then write its target exactly.</summary>
        public float Step(double nowSeconds)
        {
            double elapsed = nowSeconds - _startSeconds;
            if (elapsed < 0.0) return 0f;
            double t = _durationSeconds <= 0.0 ? 1.0 : math.saturate(elapsed / _durationSeconds);
            if (t >= 1.0) IsActive = false;
            return (float)math.smoothstep(0.0, 1.0, t);
        }

        /// <summary>Component-wise mix of two colours at <paramref name="weight"/>.</summary>
        public static Color Mix(Color from, Color to, float weight)
            => (Vector4)math.lerp((float4)(Vector4)from, (float4)(Vector4)to, weight);
    }
}
