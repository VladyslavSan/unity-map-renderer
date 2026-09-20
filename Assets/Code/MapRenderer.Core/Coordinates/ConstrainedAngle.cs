namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// The strategy used to enforce an <see cref="ConstrainedAngle"/>'s <c>[lo, hi]</c> range.
    /// </summary>
    public enum AngleConstraint
    {
        /// <summary>Clamp the value to <c>[lo, hi]</c>.</summary>
        Clamp,

        /// <summary>Wrap the value into <c>[0, 360)</c> using modular arithmetic.</summary>
        Wrap,
    }

    /// <summary>
    /// A camera-orientation angle that enforces a <c>[lo, hi]</c> range at construction time.
    /// Carries an <see cref="Angle"/> value together with the bounds and the enforcement strategy
    /// (<see cref="AngleConstraint.Clamp"/> or <see cref="AngleConstraint.Wrap"/>); the stored
    /// <see cref="Value"/> is <b>always already in-range</b> — there is no way to hold an
    /// out-of-range value via the public factories.
    ///
    /// <para><b>Presets:</b>
    /// <list type="bullet">
    ///   <item><see cref="Heading"/> — <c>[0, 360)</c> Wrap.</item>
    ///   <item><see cref="Tilt"/> — <c>[0, 90]</c> Clamp.</item>
    ///   <item><see cref="Clamped"/> — arbitrary <c>[lo, hi]</c> Clamp (e.g. a runtime
    ///     <c>maxPitch</c> limit in <c>ViewInput.ApplyTiltDelta</c>).</item>
    /// </list>
    /// </para>
    ///
    /// <para><b><c>default(ConstrainedAngle)</c>:</b> carries <c>0°</c> with a degenerate
    /// <c>[0, 0]</c> range. Reads return <c>0°</c>, matching <c>default(double)</c>. Never rely
    /// on a defaulted value re-clamping anything — always construct through a preset factory.</para>
    ///
    /// <para>Managed only (not Burst-safe due to the enum field). Engine-free; no
    /// <c>UnityEngine</c> dependency.</para>
    /// </summary>
    public readonly struct ConstrainedAngle
    {
        // ── Fields ────────────────────────────────────────────────────────────────────────────────

        private readonly double          _lo;
        private readonly double          _hi;
        private readonly AngleConstraint _strategy;

        // ── Value (the constrained angle) ─────────────────────────────────────────────────────────

        /// <summary>The constrained angle value.</summary>
        public Angle Value { get; }

        // ── Ergonomic forwarders ──────────────────────────────────────────────────────────────────

        /// <summary>The constrained angle in degrees (delegates to <see cref="Value"/>).</summary>
        public double Degrees => Value.Degrees;

        /// <summary>The constrained angle in radians (delegates to <see cref="Value"/>).</summary>
        public double Radians => Value.Radians;

        // ── Private constructor (strategy applied here — the invariant gate) ───────────────────────

        private ConstrainedAngle(double degrees, double lo, double hi, AngleConstraint strategy)
        {
            _lo       = lo;
            _hi       = hi;
            _strategy = strategy;

            Value = strategy == AngleConstraint.Wrap
                ? Angle.FromDegrees(degrees).NormalizedDegrees()
                : Angle.FromDegrees(degrees < lo ? lo : (degrees > hi ? hi : degrees));
        }

        // ── Static factories ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Camera bearing — wraps into <c>[0, 360)</c>. Constraint: <see cref="AngleConstraint.Wrap"/>.
        /// </summary>
        public static ConstrainedAngle Heading(double degrees)
            => new ConstrainedAngle(degrees, 0.0, 360.0, AngleConstraint.Wrap);

        /// <summary>
        /// Camera tilt — clamps to <c>[0, 90]</c> per the §7 locked definition:
        /// <c>tilt=0</c> is top-down (camera forward = inverse of earth normal at LookAt),
        /// <c>tilt=90</c> is parallel to the surface (horizon).
        /// </summary>
        public static ConstrainedAngle Tilt(double degrees)
            => new ConstrainedAngle(degrees, 0.0, 90.0, AngleConstraint.Clamp);

        /// <summary>
        /// General clamped angle with a runtime <c>[loDeg, hiDeg]</c> range. Used where the limit
        /// is a runtime value (e.g. <c>maxPitch</c> in <c>ViewInput.ApplyTiltDelta</c>) — a distinct,
        /// potentially narrower limit than the <c>[0, 90]</c> type invariant of <see cref="Tilt"/>.
        /// </summary>
        public static ConstrainedAngle Clamped(double degrees, double loDeg, double hiDeg)
            => new ConstrainedAngle(degrees, loDeg, hiDeg, AngleConstraint.Clamp);

        // ── Mutation (re-applies same constraint) ─────────────────────────────────────────────────

        /// <summary>
        /// Returns a new <see cref="ConstrainedAngle"/> with <paramref name="degrees"/> as the
        /// input, re-applying this instance's range and strategy.
        /// </summary>
        public ConstrainedAngle WithDegrees(double degrees)
            => new ConstrainedAngle(degrees, _lo, _hi, _strategy);

        // ── Accumulation operator ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a raw angle delta to the constrained accumulator and re-applies the constraint.
        /// Asymmetric: <paramref name="lhs"/> is the constrained accumulator (carries the
        /// range/strategy); <paramref name="rhs"/> is a raw delta with no constraint of its own.
        /// The result is always within <paramref name="lhs"/>'s range.
        /// </summary>
        public static ConstrainedAngle operator +(ConstrainedAngle lhs, Angle rhs)
            => lhs.WithDegrees(lhs.Value.Degrees + rhs.Degrees);

        // ── Object overrides ──────────────────────────────────────────────────────────────────────

        public override string ToString() => $"{Degrees:F4}° [{_lo},{_hi}] {_strategy}";
    }
}
