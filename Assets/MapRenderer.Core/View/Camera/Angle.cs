using Unity.Mathematics;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// A strongly-typed angle value, stored internally in <b>degrees</b> (the canonical unit
    /// for all camera-orientation params in this project).
    ///
    /// <para>This is the project's <b>sole home of the degrees↔radians conversion</b>
    /// (<c>* math.PI_DBL / 180.0</c>). Every trig site in camera code reads
    /// <see cref="Sin"/>/<see cref="Cos"/> or <see cref="Radians"/> instead of repeating
    /// the multiply. Degree storage guarantees <b>bitwise-exact integer-degree wraps</b>
    /// (e.g. <c>370 % 360 == 10.0</c> exactly, no floating-point residual).</para>
    ///
    /// <para><b>Construction is always explicit:</b> use the static factories
    /// <see cref="FromDegrees"/> / <see cref="FromRadians"/> — never a bare numeric literal
    /// assigned to an <c>Angle</c> field. There is no implicit <c>double</c> conversion.</para>
    ///
    /// <para>Engine-free: only <c>Unity.Mathematics</c> <c>math.*</c> is used.</para>
    /// </summary>
    public readonly struct Angle
    {
        // ── Storage ──────────────────────────────────────────────────────────────────────────────

        // Stored in degrees.  Radians are computed on demand; the project's sole conversion site.
        private readonly double _degrees;

        // ── Construction (explicit factories — unit must be stated at every construction site) ──

        private Angle(double degrees) => _degrees = degrees;

        /// <summary>Creates an <see cref="Angle"/> from a value in degrees.</summary>
        public static Angle FromDegrees(double degrees) => new Angle(degrees);

        /// <summary>Creates an <see cref="Angle"/> from a value in radians.</summary>
        public static Angle FromRadians(double radians) => new Angle(radians * 180.0 / math.PI_DBL);

        // ── Properties ───────────────────────────────────────────────────────────────────────────

        /// <summary>The angle in degrees (the stored unit).</summary>
        public double Degrees => _degrees;

        /// <summary>
        /// The angle in radians. This is the project's <b>sole</b>
        /// <c>* math.PI_DBL / 180.0</c> conversion site.
        /// </summary>
        public double Radians => _degrees * math.PI_DBL / 180.0;

        /// <summary>sine of this angle (computed via radians internally).</summary>
        public double Sin => math.sin(_degrees * math.PI_DBL / 180.0);

        /// <summary>cosine of this angle (computed via radians internally).</summary>
        public double Cos => math.cos(_degrees * math.PI_DBL / 180.0);

        // ── Normalization ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a new <see cref="Angle"/> with degrees wrapped into <c>[0, 360)</c>.
        /// Uses integer-modulus arithmetic so the result is <b>bitwise-exact</b> for integer-degree
        /// inputs (e.g. <c>370 % 360 == 10.0</c> exactly).
        /// </summary>
        public Angle NormalizedDegrees()
        {
            double h = _degrees % 360.0;
            if (h < 0) h += 360.0;
            return new Angle(h);
        }

        // ── Interpolation ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Shortest-path angular lerp between two angles. Always traverses the shorter arc:
        /// <c>LerpShortest(350°, 10°, t)</c> traverses <c>+20°</c> (not <c>−340°</c>).
        /// The result is wrapped into <c>[0, 360)</c>.
        /// </summary>
        public static Angle LerpShortest(Angle from, Angle to, double t)
        {
            double diff = ((to._degrees - from._degrees + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
            return FromDegrees(from._degrees + diff * t).NormalizedDegrees();
        }

        // ── Comparison ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when the absolute difference in degrees is within
        /// <paramref name="toleranceDegrees"/>. Opt-in tolerance check; exact equality
        /// is the default (<c>Equals</c> / <c>==</c>) and compares the stored degree values.
        /// </summary>
        public bool ApproximatelyEquals(Angle other, double toleranceDegrees)
            => math.abs(_degrees - other._degrees) <= toleranceDegrees;

        // ── Object overrides ──────────────────────────────────────────────────────────────────────

        public override string ToString() => $"{_degrees:F4}°";
    }
}
