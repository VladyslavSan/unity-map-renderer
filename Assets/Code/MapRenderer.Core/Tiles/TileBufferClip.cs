using Unity.Mathematics;

namespace MapRenderer.Core.Tiles
{
    /// <summary>
    /// How much of a tile's <b>buffer</b> — the geometry an MVT tile carries beyond <c>[0, extent)</c> so
    /// neighbours can join seamlessly — the fill path keeps before triangulating.
    ///
    /// <para>Every tile paints its whole buffer today, so two neighbours double-paint the overlap strip.
    /// Under the fill shader's <c>SrcAlpha</c>/<c>OneMinusSrcAlpha</c>, <c>ZWrite</c>-off contract a
    /// translucent fill composites to <c>1 − (1 − α)²</c> there instead of <c>α</c> — a uniform brighter
    /// band one buffer-width wide along every seam. Clipping fill rings to
    /// <c>[−b, extent + b]</c> in tile space removes it (and the ~6 % overdraw with it).</para>
    ///
    /// <para><b>Units.</b> Authored in tile units <i>at the 4096-extent convention</i>
    /// (<see cref="ReferenceExtent"/>) and converted to the layer's own extent inside
    /// <see cref="TryWindow"/> — the single conversion site. Raw tile units would mean 8× the intended
    /// margin on a 512-extent layer (extent is a per-layer property, not a constant); a bare
    /// fraction-of-extent is correct but unreadable to author (<c>0.015625</c> for the standard buffer).
    /// <c>64</c> is the OpenMapTiles standard buffer and reproduces the pre-clip geometry exactly;
    /// <c>0</c> cuts at the tile boundary.</para>
    ///
    /// <para><c>default</c> is <see cref="Disabled"/>, so an unset knob means "no clip" — the
    /// behaviour-preserving state.</para>
    /// </summary>
    public readonly struct TileBufferClip
    {
        /// <summary>The extent the <see cref="KeepAtReferenceExtent"/> knob is authored against — the MVT
        /// authoring convention, in ONE place.</summary>
        public const double ReferenceExtent = 4096.0;

        /// <summary>No clipping: rings reach triangulation exactly as decoded.</summary>
        public static TileBufferClip Disabled => default;

        /// <summary>Keep <paramref name="unitsAtReferenceExtent"/> tile units of buffer on every side, measured
        /// at <see cref="ReferenceExtent"/>. Negative input clamps to 0 (cut at the tile boundary) rather than
        /// eroding into the tile.</summary>
        public static TileBufferClip KeepTileUnits(double unitsAtReferenceExtent)
            => new TileBufferClip(
                // NaN is rejected explicitly, not clamped: math.max(0, NaN) IS NaN (the comparison is
                // false, so the second operand wins), and a NaN margin makes a NaN clip window, against
                // which every vertex tests outside — the whole map clips away to nothing. A silent blank
                // screen is the worst failure this type can produce, so it degrades to "clip at the tile
                // boundary" instead.
                double.IsNaN(unitsAtReferenceExtent) ? 0.0 : math.max(0.0, unitsAtReferenceExtent));

        private TileBufferClip(double keepAtReferenceExtent)
        {
            IsEnabled             = true;
            KeepAtReferenceExtent = keepAtReferenceExtent;
        }

        /// <summary>False on <c>default</c>/<see cref="Disabled"/> — the pipeline then skips the clip stage
        /// entirely (no allocation, arrays pass through untouched).</summary>
        public bool IsEnabled { get; }

        /// <summary>The authored margin in tile units at <see cref="ReferenceExtent"/>; 0 when disabled.</summary>
        public double KeepAtReferenceExtent { get; }

        /// <summary>
        /// The clip window in <paramref name="extent"/>'s own tile units:
        /// <c>[−b, extent + b]²</c> with <c>b = KeepAtReferenceExtent × extent / ReferenceExtent</c>.
        /// Returns false (and leaves the outputs at <c>default</c>) when disabled or when
        /// <paramref name="extent"/> is not positive — both mean "do not clip".
        /// </summary>
        public bool TryWindow(double extent, out double2 min, out double2 max)
        {
            min = default;
            max = default;
            if (!IsEnabled || extent <= 0.0) return false;

            double keep = KeepAtReferenceExtent * extent / ReferenceExtent;
            min = new double2(-keep, -keep);
            max = new double2(extent + keep, extent + keep);
            return true;
        }
    }
}
