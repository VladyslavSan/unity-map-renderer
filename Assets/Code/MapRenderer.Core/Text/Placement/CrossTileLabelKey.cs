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
    /// cell. <c>For</c> is a general primitive parameterised by <c>quantizeMeters</c>; its production callers now
    /// pass the FIXED <see cref="CanonicalGridMeters"/> (Stage 3: the store dedup; Stage 3b: the point fade id).
    /// A fixed grid is correct because parent/child tile OVERLAP does not happen today (design §1.2) — the only
    /// real dup is the SAME feature in adjacent/overlapping tiles, whose anchor is identical, so any small fixed
    /// grid collapses it; separating two genuinely distinct labels that are close on screen is the COLLISION
    /// pass's job, not this key's. (FUTURE, when parent/child overlap lands (design §6): the grid goes back to a
    /// zoom-scaled value — pick the coarser band's grid + finest-zoom-wins — a change localised to the caller's
    /// <c>quantizeMeters</c> input and <see cref="CanonicalGridMeters"/>'s use.)</para>
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

        /// <summary>The single canonical dedup+fade grid (metres). Stage 3: the cross-tile dedup key
        /// (<see cref="MapRenderer.Unity.Text.SymbolTileLabelStore"/>) quantizes to this grid — so a point
        /// label's dedup cell is a pure function of the tile set (Stage 3b will fold the point fade id onto it
        /// too, making dedup cell == fade cell). Fixed (zoom-independent) because parent/child tile overlap does
        /// not happen today (design §1.2): the only real dup is the SAME feature in adjacent/overlapping tiles,
        /// whose anchorRender is identical, so any fixed grid collapses it; distinct-feature visual overlap is
        /// the COLLISION pass's job, not the dedup's. 4 m (matching today's fade grid — one tuning knob, see
        /// design §8). FUTURE (parent/child overlap, design §6): go back to a zoom-scaled grid — pick the coarser
        /// band's grid + finest-zoom-wins; change the store's grid input + this const's use, nothing
        /// downstream.</summary>
        public const double CanonicalGridMeters = 4.0;

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
            QuantizeAnchor(anchorRender, quantizeMeters, out long gx, out long gz, out long gy);
            return new CrossTileLabelKey(gx, gz, gy, layerId, text, iconImage);
        }

        /// <summary>
        /// The shared anchor→grid quantization: snaps all three render-space axes to a
        /// <paramref name="quantizeMeters"/> grid (<c>≤ 0</c> ⇒ a defensive 1-metre grid). Extracted (Stage 2)
        /// so the string-keyed <see cref="For"/> and the integer-keyed dedup key produce BIT-IDENTICAL grids
        /// from one code path. Behaviour-preserving: the <c>q ≤ 0 → 1.0</c> fallback + <c>math.round</c> are
        /// exactly as <see cref="For"/> did them inline.
        /// </summary>
        public static void QuantizeAnchor(in double3 anchorRender, double quantizeMeters,
            out long gridX, out long gridZ, out long gridY)
        {
            double q = quantizeMeters > 0.0 ? quantizeMeters : 1.0;
            gridX = (long)math.round(anchorRender.x / q);
            gridZ = (long)math.round(anchorRender.z / q);
            gridY = (long)math.round(anchorRender.y / q);
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
