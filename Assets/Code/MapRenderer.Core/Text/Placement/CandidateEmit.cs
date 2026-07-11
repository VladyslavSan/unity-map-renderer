// Engine-free: no UnityEngine dependency. Pure blittable data carrier.

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The draw-side payload of one collision candidate: the contiguous range of staged
    /// <see cref="PlacedQuad"/>s to emit if it survives, and the material/mesh slot to emit them into.
    /// Kept parallel to the <see cref="LabelCandidate"/> array (keyed by its <see cref="LabelCandidate.LabelIndex"/>)
    /// so collision can sort the candidates without disturbing the quad ranges. Blittable (all ints) so the
    /// staging math can fill it in a Burst job (Lever C).
    /// </summary>
    public struct CandidateEmit
    {
        /// <summary>Index of this candidate's first quad in the flat staged-quad pool.</summary>
        public int QuadStart;

        /// <summary>Number of quads this candidate emits (1 point label → its glyph quads; curved → N glyphs).</summary>
        public int QuadCount;

        /// <summary>Per-symbol-layer material/mesh slot the surviving quads draw into.</summary>
        public int Slot;
    }
}
