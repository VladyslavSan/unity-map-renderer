using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Unity.Interpolation
{
    /// <summary>
    /// An <see cref="IInterpolation{T}"/> for a scene writer outside the render layers (sun, sky, haze): owns
    /// the from/to endpoints and <see cref="Current"/>, the value the writer last wrote. Uses the same clock
    /// and smoothstep curve as <c>ZoomStyleApplier</c>: hold during the delay, then interpolate over the
    /// duration. Holds no references, so an update never allocates.
    /// </summary>
    internal struct Smoothstep<T> : IInterpolation<T> where T : struct, IInterpolatable<T>
    {
        private T      _from;
        private T      _to;
        private double _startSeconds;
        private double _durationSeconds;

        /// <summary>The current interpolated value — the result, an override, or a plain <see cref="Set"/>. The
        /// writer may still scale it (e.g. by light intensity) before writing it out.</summary>
        public T Current { get; private set; }

        /// <summary>True from <see cref="Start"/> until the weight reaches 1 or <see cref="Set"/> runs.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Starts moving <see cref="Current"/> toward <paramref name="target"/>: hold for
        /// <paramref name="delaySeconds"/>, then interpolate over <paramref name="durationSeconds"/>, from
        /// <paramref name="nowSeconds"/>. Instant (both ≤ 0) sets <see cref="Current"/> to
        /// <paramref name="target"/> at once (see <see cref="Set"/>).</summary>
        public void Start(in T target, double delaySeconds, double durationSeconds, double nowSeconds)
        {
            if (delaySeconds <= 0.0 && durationSeconds <= 0.0) { Set(target); return; }
            _from            = Current;
            _to              = target;
            IsActive         = true;
            _startSeconds    = nowSeconds + delaySeconds;
            _durationSeconds = durationSeconds;
        }

        /// <summary>Sets <see cref="Current"/> to <paramref name="value"/> at once and stops any running move
        /// (a runtime override, or the first value before any style has applied).</summary>
        public void Set(in T value)
        {
            Current  = value;
            IsActive = false;
        }

        /// <summary>Steps the window to <paramref name="nowSeconds"/> and updates <see cref="Current"/>. A no-op
        /// during the delay or once <see cref="IsActive"/> is already false. At the window's end
        /// <see cref="Current"/> becomes the target exactly, not a near-1 mix. Returns true when
        /// <see cref="Current"/> changed this call, so the caller knows whether to re-push it.</summary>
        public bool Update(double nowSeconds)
        {
            if (!IsActive) return false;
            double elapsed = nowSeconds - _startSeconds;
            if (elapsed < 0.0) return false;
            double t = _durationSeconds <= 0.0 ? 1.0 : math.saturate(elapsed / _durationSeconds);
            if (t >= 1.0)
            {
                Current  = _to;
                IsActive = false;
                return true;
            }
            float weight = (float)math.smoothstep(0.0, 1.0, t);
            Current = _from.Interpolate(_to, weight);
            return true;
        }
    }

    /// <summary>Colour interpolation shared by every <see cref="IInterpolatable{T}"/> that carries a <see cref="Color"/>.</summary>
    internal static class ColorMix
    {
        /// <summary>Component-wise mix of two colours at <paramref name="weight"/>.</summary>
        public static Color Lerp(Color from, Color to, float weight)
            => (Vector4)math.lerp((float4)(Vector4)from, (float4)(Vector4)to, weight);
    }
}
