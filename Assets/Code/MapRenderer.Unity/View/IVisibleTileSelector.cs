// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references.

using System.Collections.Generic;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// The algorithm-agnostic <b>seam</b> every consumer talks to for "which tiles does the camera see".
    /// One call per frame; the concrete algorithm lives behind it (<see cref="FrustumTileSelector"/> is
    /// the default; a distance-based-LOD impl is a drop-in replacement).
    ///
    /// <para><b>What keeps it generic (do not change these properties):</b></para>
    /// <list type="bullet">
    ///   <item>The method takes only a per-frame <see cref="ViewContext"/> and the reuse buffer — and
    ///     <b>no algorithm knob</b> (no pad, no min/max zoom, no <c>out selectionZoom</c>). Tuning constants
    ///     belong on the concrete impl's constructor, never on this seam or on <see cref="ViewContext"/>.</item>
    ///   <item>The returned set <b>MAY span multiple zoom levels</b> (each <see cref="TileId"/> carries its
    ///     own <c>Z</c>). The seam <b>never exposes a single selection-zoom for the whole set</b> — that
    ///     property is what makes a mixed-zoom (distance-LOD) impl a drop-in replacement. The default impl
    ///     selects one integer zoom internally; that is not surfaced here.</item>
    ///   <item>No fallback-provenance on the interface. The seam returns <i>just the set</i>; the consumer
    ///     owns how to transition between sets, including which parent/child a downgraded tile came
    ///     from.</item>
    /// </list>
    /// </summary>
    public interface IVisibleTileSelector
    {
        /// <summary>
        /// Called once per frame. Clears <paramref name="reuseBuffer"/> and refills it with the tiles the
        /// camera currently sees. Allocation-free in steady state (no LINQ, no per-call heap allocation).
        /// </summary>
        /// <param name="view">Per-frame view context (camera pose + framing viewport + projection).</param>
        /// <param name="reuseBuffer">Caller-owned result buffer. Cleared then refilled.</param>
        void SelectVisibleTiles(in ViewContext view, List<TileId> reuseBuffer);
    }
}
