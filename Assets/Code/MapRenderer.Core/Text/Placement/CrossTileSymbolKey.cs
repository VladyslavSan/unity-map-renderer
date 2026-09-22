// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double3 — this
// file lives in MapRenderer.Core.Text.Placement (see SymbolScreenProjection's header for the inline-qualification
// trap this avoids).

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A stable CROSS-TILE identity for a point symbol — <c>(quantized world anchor, layer, text, icon
    /// image)</c> — so the SAME symbol appearing in more than one loaded tile (a parent + its child during a
    /// zoom transition) is recognised as one symbol (deduped, and carried through the fade so a tile swap is
    /// a seamless no-op). <see cref="IconImage"/> extends the identity to icon symbols (whose
    /// <see cref="Text"/> is always null) so two distinct co-located icons (same anchor cell + layer,
    /// different sprite) stay distinct instead of colliding.
    ///
    /// <para><b>Why quantize, and to what.</b> The same geo feature is MVT-quantized to each tile's own extent
    /// grid, so a parent (coarser) and child (finer) tile place its anchor a few metres apart. Snapping the
    /// render-space (Mercator-metre) anchor to a grid of <c>quantizeMeters</c> collapses that difference to one
    /// cell. <c>For</c> is a general primitive parameterised by <c>quantizeMeters</c>; its production callers
    /// pass the FIXED <see cref="CanonicalGridMeters"/>. A fixed grid is correct because parent/child tile
    /// OVERLAP does not happen today — the only real dup is the SAME feature in adjacent/overlapping tiles,
    /// whose anchor is identical, so any small fixed grid collapses it; separating two genuinely distinct
    /// symbols that are close on screen is the COLLISION pass's job, not this key's. When parent/child
    /// overlap lands the grid goes back to a zoom-scaled value — pick the coarser band's grid +
    /// finest-zoom-wins — a change localised to the caller's <c>quantizeMeters</c> input and
    /// <see cref="CanonicalGridMeters"/>'s use.</para>
    ///
    /// <para><b>Boundary caveat.</b> Grid snapping misses when the two anchors straddle a cell edge; that yields
    /// a rare one-frame double-symbol, not a persistent error (neighbour-cell matching is a future refinement).
    /// Full <see cref="Text"/> equality (not a hash) is folded in so two different symbols sharing a cell never
    /// merge.</para>
    /// </summary>
    public readonly struct CrossTileSymbolKey : IEquatable<CrossTileSymbolKey>
    {
        public readonly long GridX;
        public readonly long GridZ;
        public readonly long GridY;
        public readonly int LayerId;
        public readonly string Text;

        /// <summary>The resolved sprite name — the icon's cross-tile identity. Null for a text symbol, whose
        /// hash/equality must not change: <see cref="GetHashCode"/> skips the fold rather than folding
        /// <c>?? 0</c>.</summary>
        public readonly string IconImage;

        public CrossTileSymbolKey(long gridX, long gridZ, long gridY, int layerId, string text, string iconImage)
        {
            GridX = gridX; GridZ = gridZ; GridY = gridY; LayerId = layerId; Text = text; IconImage = iconImage;
        }

        /// <summary>The single canonical dedup+fade grid (metres). The cross-tile dedup key
        /// (<see cref="MapRenderer.Unity.Text.SymbolTileStore"/>) quantizes to this grid, so a point symbol's
        /// dedup cell is a pure function of the tile set. Fixed (zoom-independent) because parent/child tile
        /// overlap does not happen today: the only real dup is the SAME feature in adjacent/overlapping tiles,
        /// whose anchorRender is identical, so any fixed grid collapses it; distinct-feature visual overlap is
        /// the COLLISION pass's job, not the dedup's. 4 m, matching the fade grid — one tuning knob. When
        /// parent/child overlap lands, go back to a zoom-scaled grid (pick the coarser band's grid +
        /// finest-zoom-wins): change the store's grid input and this const's use, nothing downstream.</summary>
        public const double CanonicalGridMeters = 4.0;

        /// <summary>
        /// The identity of a point symbol whose render-space (pre-RTC Mercator) anchor is
        /// <paramref name="anchorRender"/>, on layer <paramref name="layerId"/>, reading
        /// <paramref name="text"/> (icon: <see cref="MapRenderer.Core.Style.Symbol.SymbolFeature.IconImage"/>
        /// via <paramref name="iconImage"/>), snapped to a <paramref name="quantizeMeters"/> grid. All three
        /// grid axes are quantized: on the Mercator plane render.y ≡ 0 for every surface symbol, so
        /// <c>GridY</c> is inert there (the key partition is unchanged); on the globe two equator-mirrored
        /// anchors (e.g. 30°N vs 30°S at the same longitude) share render X/Z but differ in Y — without the Y
        /// axis they'd collide into one symbol. <paramref name="quantizeMeters"/> ≤ 0 falls back to a 1-metre
        /// grid (a defensive default; callers pass a real display-zoom pixel size).
        /// </summary>
        public static CrossTileSymbolKey For(
            in double3 anchorRender, int layerId, string text, string iconImage, double quantizeMeters)
        {
            QuantizeAnchor(anchorRender, quantizeMeters, out long gx, out long gz, out long gy);
            return new CrossTileSymbolKey(gx, gz, gy, layerId, text, iconImage);
        }

        /// <summary>
        /// The shared anchor→grid quantization: snaps all three render-space axes to a
        /// <paramref name="quantizeMeters"/> grid (<c>≤ 0</c> ⇒ a defensive 1-metre grid). One code path, so
        /// the string-keyed <see cref="For"/> and the integer-keyed dedup key produce BIT-IDENTICAL grids.
        /// </summary>
        public static void QuantizeAnchor(in double3 anchorRender, double quantizeMeters,
            out long gridX, out long gridZ, out long gridY)
        {
            double q = quantizeMeters > 0.0 ? quantizeMeters : 1.0;
            gridX = (long)math.round(anchorRender.x / q);
            gridZ = (long)math.round(anchorRender.z / q);
            gridY = (long)math.round(anchorRender.y / q);
        }

        public bool Equals(CrossTileSymbolKey other)
            => GridX == other.GridX && GridZ == other.GridZ && GridY == other.GridY
               && LayerId == other.LayerId && Text == other.Text && IconImage == other.IconImage;

        public override bool Equals(object obj) => obj is CrossTileSymbolKey o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + GridX.GetHashCode();
                h = h * 31 + GridZ.GetHashCode();
                h = h * 31 + GridY.GetHashCode();
                h = h * 31 + LayerId;
                h = h * 31 + (Text?.GetHashCode() ?? 0);
                // Guard-skip fold: only fold IconImage when non-null, so a text key (IconImage always null)
                // keeps its hash — an unconditional `?? 0` fold would change every text hash.
                if (IconImage != null) h = h * 31 + IconImage.GetHashCode();
                return h;
            }
        }
    }
}
