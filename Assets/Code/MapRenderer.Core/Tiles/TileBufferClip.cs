using Unity.Mathematics;

namespace MapRenderer.Core.Tiles
{
    /// <summary>
    /// How much of a tile's <b>buffer</b> (geometry beyond <c>[0, extent)</c>) the fill path keeps before
    /// triangulating. Non-obvious why: two neighbours that both paint the overlap strip composite a
    /// translucent fill to <c>1 − (1 − α)²</c> there, a bright seam band; clipping to
    /// <c>[−b, extent + b]</c> removes it. The margin is in tile units at <see cref="ReferenceExtent"/>,
    /// converted per layer only in <see cref="TryWindow"/>; 64 (the OpenMapTiles buffer) keeps everything
    /// and 0 cuts at the tile boundary. <c>default</c> is <see cref="Disabled"/>: "no clip".
    /// </summary>
    public readonly struct TileBufferClip
    {
        /// <summary>The extent the <see cref="KeepAtReferenceExtent"/> knob is authored against — the MVT
        /// authoring convention, in ONE place.</summary>
        public const double ReferenceExtent = 4096.0;

        /// <summary>No clipping: rings reach triangulation exactly as decoded.</summary>
        public static TileBufferClip Disabled => default;

        /// <summary>
        /// Decodes an Inspector-authored margin: negative means "do not run the clip stage", unlike 0,
        /// which cuts at the tile boundary. It lives here so every consumer, including the parity oracles'
        /// reference arm, decodes the field into the same window.
        /// </summary>
        public static TileBufferClip FromInspectorUnits(double unitsAtReferenceExtent)
            => unitsAtReferenceExtent < 0.0 ? Disabled : KeepTileUnits(unitsAtReferenceExtent);

        /// <summary>Keep <paramref name="unitsAtReferenceExtent"/> tile units of buffer on every side, measured
        /// at <see cref="ReferenceExtent"/>. Negative input clamps to 0 (cut at the tile boundary) rather than
        /// eroding into the tile.</summary>
        public static TileBufferClip KeepTileUnits(double unitsAtReferenceExtent)
            => new TileBufferClip(
                // NaN is rejected, not clamped: math.max(0, NaN) is NaN, and a NaN window clips the whole
                // map away, so NaN degrades to "clip at the tile boundary".
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
