// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — Unity.Mathematics + IProjection only.

using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Per-frame <b>view context</b> — everything that describes "what the camera sees right now":
    /// the camera pose, the framing viewport, and the active pixel↔ground projection. Bundled into one
    /// carrier so the per-frame seams that consume it (<see cref="IVisibleTileSelector"/> for tile
    /// selection, and the camera-interaction ops) share a single type rather than three loose parameters.
    ///
    /// <para><b>Not algorithm config.</b> This is view <i>state</i> (what the camera sees), never an
    /// algorithm knob (how tiles are chosen). Tuning constants — pad, zoom clamps — live on the concrete
    /// selector's constructor, not here. In particular <see cref="Projection"/> is the swappable S63
    /// pixel↔ground <i>service</i> (Web-Mercator today, globe later); it belongs with the per-frame view
    /// inputs because it changes at runtime and every selector impl needs it.</para>
    ///
    /// <para><b>Passed by <c>in</c>.</b> A <c>readonly struct</c> with <c>init</c>-only members (the
    /// data-carrier convention): <c>in ViewContext</c> takes a reference with no defensive copy. It carries
    /// a managed <see cref="IProjection"/> reference, so it is a small managed carrier — fine for this
    /// once-per-frame seam, NOT a Burst/blittable struct.</para>
    /// </summary>
    public readonly struct ViewContext
    {
        /// <summary>Camera pose: look-at, zoom, heading, tilt.</summary>
        public CameraProperties Camera { get; init; }

        /// <summary>
        /// The viewport size in pixels. Usage depends on the consumer:
        /// <list type="bullet">
        ///   <item><b>Camera-interaction seam (S73)</b> — the <b>live</b> interaction viewport
        ///     (<c>Camera.pixelWidth, Camera.pixelHeight</c>); must be in the same pixel scale as
        ///     the cursor positions fed to the gesture mapping so the pin invariants hold.</item>
        ///   <item><b>Tile-selection seam (S71)</b> — the <b>framing</b> viewport
        ///     <c>(ReferenceViewportHeightPx · liveAspect, ReferenceViewportHeightPx)</c>, NOT the
        ///     raw live window size (see <see cref="IVisibleTileSelector"/> D6/D7).</item>
        /// </list>
        /// The type is usage-neutral; each consumer fills this field with the appropriate scale.
        /// </summary>
        public double2 ViewportPx { get; init; }

        /// <summary>The active pixel↔ground projection service (S63). Web-Mercator today; globe later.</summary>
        public IProjection Projection { get; init; }
    }
}
