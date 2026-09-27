namespace MapRenderer.Unity.Interpolation
{
    /// <summary>An interpolation window over a value that is itself <see cref="IInterpolatable{T}"/>. A caller
    /// that holds the concrete implementing struct avoids boxing; reaching it through this interface boxes the
    /// struct on every call.</summary>
    internal interface IInterpolation<T> where T : struct, IInterpolatable<T>
    {
        /// <summary>The current interpolated value — the result, an override, or a plain <see cref="Set"/>.</summary>
        T Current { get; }

        /// <summary>True from <see cref="Start"/> until the weight reaches 1 or <see cref="Set"/> runs.</summary>
        bool IsActive { get; }

        /// <summary>Starts moving <see cref="Current"/> toward <paramref name="target"/>: hold for
        /// <paramref name="delaySeconds"/>, then interpolate over <paramref name="durationSeconds"/>, from
        /// <paramref name="nowSeconds"/>. Instant (both ≤ 0) sets <see cref="Current"/> to
        /// <paramref name="target"/> at once (see <see cref="Set"/>).</summary>
        void Start(in T target, double delaySeconds, double durationSeconds, double nowSeconds);

        /// <summary>Sets <see cref="Current"/> to <paramref name="value"/> at once and stops any running move.</summary>
        void Set(in T value);

        /// <summary>Steps the window to <paramref name="nowSeconds"/> and updates <see cref="Current"/>. Returns
        /// true when <see cref="Current"/> changed this call.</summary>
        bool Update(double nowSeconds);
    }
}
