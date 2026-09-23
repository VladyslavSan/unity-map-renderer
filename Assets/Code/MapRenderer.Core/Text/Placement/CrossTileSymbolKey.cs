// TOP-LEVEL `using Unity.Mathematics;` + unqualified double3: an inline qualification hits a namespace
// collision inside MapRenderer.Core.Text.Placement (see SymbolScreenProjection's header).

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A stable CROSS-TILE identity for a point symbol — <c>(quantized world anchor, layer, text, icon image)</c>
    /// — so the SAME symbol in more than one loaded tile is deduped as one and carried through the fade.
    /// <see cref="IconImage"/> keeps two co-located icons with different sprites distinct (an icon's
    /// <see cref="Text"/> is null). Full <see cref="Text"/> equality, not a hash, keeps two symbols that share a
    /// cell apart; separating distinct symbols that are close on screen is the collision pass's job.
    /// <para>Production dedups with <c>SymbolReconciler</c>'s all-integer <c>DedupKey</c>, which shares
    /// <see cref="QuantizeAnchor"/> and has the same equality classes; <see cref="For"/> is the tests'
    /// parity oracle for it.</para>
    /// <para>Limitation: snapping misses when two anchors straddle a cell edge, which gives a rare one-frame
    /// double symbol, not a persistent error.</para>
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

        /// <summary>The single canonical dedup+fade grid, in metres. The production dedup key
        /// (<c>SymbolReconciler</c>'s <c>DedupKey</c>) and the fade id quantize to it, so a symbol's dedup cell
        /// is a pure function of the tile set. It is fixed, not zoom-scaled: the cover is a quadtree cut, so a parent
        /// never overlaps its child, and the duplicates are edge/buffer copies between neighbouring tiles and
        /// cross-source copies. 4 m, the same as the fade grid: one tuning knob. Seam: a cover that overlaps a
        /// parent and its child needs a zoom-scaled grid (finest zoom wins); only this constant's use and the
        /// store's grid input change.</summary>
        public const double CanonicalGridMeters = 4.0;

        /// <summary>
        /// The identity of a point symbol with render-space (pre-RTC) anchor <paramref name="anchorRender"/>, layer
        /// <paramref name="layerId"/>, <paramref name="text"/> and <paramref name="iconImage"/>, snapped to a
        /// <paramref name="quantizeMeters"/> grid (≤ 0 falls back to 1 m). All three axes are quantized:
        /// <c>GridY</c> is inert on the Mercator plane (render.y ≡ 0), but on the globe two equator-mirrored
        /// anchors share render X/Z and differ only in Y.
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
