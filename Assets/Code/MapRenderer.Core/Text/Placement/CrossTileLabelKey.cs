// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double3 — this
// file lives in MapRenderer.Core.Text.Placement (see PolylineArcWalker's header for the inline-qualification
// trap this avoids).

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A-3: a stable CROSS-TILE identity for a point label — <c>(quantized world anchor, layer, text, icon
    /// image)</c> — so the SAME symbol appearing in more than one loaded tile (a parent + its child during a
    /// zoom transition) is recognised as one label (deduped now; carried through the A-4 fade so a tile swap
    /// is a seamless no-op, not a fade-out/fade-in). I6: <see cref="IconImage"/> extends the identity to icon
    /// labels (whose <see cref="Text"/> is always null, I3) so two distinct co-located icons (same anchor
    /// cell + layer, different sprite) stay distinct instead of colliding.
    ///
    /// <para><b>Why quantize, and to what.</b> The same geo feature is MVT-quantized to each tile's own extent
    /// grid, so a parent (coarser) and child (finer) tile place its anchor a few metres apart. Snapping the
    /// render-space (Mercator-metre) anchor to a grid of <c>quantizeMeters</c> collapses that difference to one
    /// cell. The caller passes <c>quantizeMeters = CameraPoseMath.MetersPerPixel(displayZoom)</c> — one logical
    /// PIXEL at the current zoom: coarse enough that adjacent-zoom reprojection diffs land in the same cell
    /// (they are sub-pixel-to-a-few-pixels apart), fine enough that two genuinely distinct labels &gt; a pixel
    /// apart stay separate. A fixed grid cannot serve all zooms (a metre at z5 vs a metre at z14 differ by
    /// 2^9×), so the grid MUST be display-zoom-relative — hence it is supplied per frame, not baked.</para>
    ///
    /// <para><b>Boundary caveat.</b> Grid snapping misses when the two anchors straddle a cell edge; that yields
    /// a rare one-frame double-label, not a persistent error (neighbour-cell matching is a future refinement).
    /// Full <see cref="Text"/> equality (not a hash) is folded in so two different labels sharing a cell never
    /// merge.</para>
    /// </summary>
    public readonly struct CrossTileLabelKey : IEquatable<CrossTileLabelKey>
    {
        public readonly long GridX;
        public readonly long GridZ;
        public readonly long GridY;
        public readonly int LayerId;
        public readonly string Text;

        /// <summary>I6: the resolved sprite name — the icon's cross-tile identity. Null for a text label
        /// (every pre-I6 caller), so a text key's hash/equality is UNCHANGED (a guard-skip fold, not
        /// <c>?? 0</c> — see <see cref="GetHashCode"/>).</summary>
        public readonly string IconImage;

        public CrossTileLabelKey(long gridX, long gridZ, long gridY, int layerId, string text, string iconImage)
        {
            GridX = gridX; GridZ = gridZ; GridY = gridY; LayerId = layerId; Text = text; IconImage = iconImage;
        }

        /// <summary>
        /// The identity of a point label whose render-space (pre-RTC Mercator) anchor is
        /// <paramref name="anchorRender"/>, on layer <paramref name="layerId"/>, reading
        /// <paramref name="text"/> (icon: <see cref="MapRenderer.Core.Style.Symbol.SymbolLabel.IconImage"/>
        /// via <paramref name="iconImage"/>), snapped to a <paramref name="quantizeMeters"/> grid. All three
        /// grid axes are quantized: on the Mercator plane render.y ≡ 0 for every surface label, so
        /// <c>GridY</c> is inert there (the key partition is unchanged); on the globe two equator-mirrored
        /// anchors (e.g. 30°N vs 30°S at the same longitude) share render X/Z but differ in Y — without the Y
        /// axis they'd collide into one label. <paramref name="quantizeMeters"/> ≤ 0 falls back to a 1-metre
        /// grid (a defensive default; callers pass a real display-zoom pixel size).
        /// </summary>
        public static CrossTileLabelKey For(
            in double3 anchorRender, int layerId, string text, string iconImage, double quantizeMeters)
        {
            double q = quantizeMeters > 0.0 ? quantizeMeters : 1.0;
            long gx = (long)math.round(anchorRender.x / q);
            long gz = (long)math.round(anchorRender.z / q);
            long gy = (long)math.round(anchorRender.y / q);
            return new CrossTileLabelKey(gx, gz, gy, layerId, text, iconImage);
        }

        public bool Equals(CrossTileLabelKey other)
            => GridX == other.GridX && GridZ == other.GridZ && GridY == other.GridY
               && LayerId == other.LayerId && Text == other.Text && IconImage == other.IconImage;

        public override bool Equals(object obj) => obj is CrossTileLabelKey o && Equals(o);

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
                // I6 guard-skip fold: only fold IconImage when non-null, so a text key (IconImage always
                // null) hashes IDENTICALLY to before this field existed — an unconditional `?? 0` fold would
                // still change every text hash (folding an extra constant term), breaking the #1 invariant.
                if (IconImage != null) h = h * 31 + IconImage.GetHashCode();
                return h;
            }
        }
    }
}
