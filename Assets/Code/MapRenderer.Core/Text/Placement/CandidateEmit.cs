// Engine-free: no UnityEngine dependency. Pure blittable data carrier. TOP-LEVEL `using Unity.Mathematics;`
// + unqualified float3/double3 — this file lives in MapRenderer.Core.Text.Placement (see PolylineArcWalker
// for the namespace-collision trap an inline `Unity.Mathematics.X` would hit).

using Unity.Mathematics;

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

        /// <summary>I5a — the texture the emitted quads sample from (glyph atlas vs. sprite atlas), carried
        /// from <see cref="PointStageInput.AtlasKind"/>. Default <see cref="LabelKind.Text"/>. NOT yet
        /// consumed by the draw side (I5b partitions the draw by it).</summary>
        public LabelKind AtlasKind;

        /// <summary>Epic A / A1 (design §11 A1 D2/D6): the world-anchored draw payload, read ONLY by
        /// <see cref="MapRenderer.Unity.Text.Placement.WorldLabelRenderer"/> when <see cref="IsWorld"/> is
        /// true — never a batch tile array indexed by <c>tileIndex</c> (the BLOCKER this design fixes).
        /// <see cref="AnchorLocal"/>/<see cref="TileOriginRender"/> mirror <see cref="PointStageInput"/>'s
        /// identically-named fields (the SAME bake); <see cref="TileKey"/> keys the renderer's per-
        /// (tile,slot,kind) slot dictionary (D1).</summary>
        public float3 AnchorLocal;

        /// <summary>See <see cref="PointStageInput.TileOriginRender"/> — the render-space origin
        /// <see cref="AnchorLocal"/> was baked against.</summary>
        public double3 TileOriginRender;

        /// <summary>The label's tile key (D1's slot-dictionary key) — always populated on a point candidate
        /// (mirrors <see cref="PointStageInput.TileKey"/>), default 0 on a curved candidate (unread, since
        /// <see cref="IsWorld"/> is false there).</summary>
        public long TileKey;

        /// <summary>Epic A / A1 (design §11 A1 D4): the additive screen-space corner offset from
        /// <c>text-translate</c>, computed by <see cref="LabelStagingMath.StagePoint"/> as
        /// <c>screenPx − s.ScreenPx</c> (both already resolved there) — the world path does not project the
        /// anchor, so it cannot fold the translate into it the way the OLD path does; instead
        /// <c>BillboardMath.BuildWorldQuad</c> adds this to every corner's <c>OffsetPx</c> (same Y negation
        /// as the corner). Default <c>float2.zero</c> — no-op for the common (untranslated) case.</summary>
        public float2 TranslateDeltaPx;

        /// <summary>True when the world-anchored draw sink should read this candidate — set by BOTH
        /// <see cref="LabelStagingMath.StagePoint"/> (D2/D6) and, since Stage AC, <see cref="LabelStagingMath.StageCurved"/>;
        /// the emit loop branches on this WITHOUT a per-candidate record lookup. See <see cref="AlongLine"/>
        /// for which of the two it was.</summary>
        public bool IsWorld;

        /// <summary>Stage AC (curved-world): true ONLY when <see cref="LabelStagingMath.StageCurved"/> set
        /// this candidate's emit — <see cref="MapRenderer.Unity.Text.Placement.WorldLabelRenderer"/> reads
        /// each quad's OWN <see cref="PlacedQuad.AnchorLocal"/>/<see cref="PlacedQuad.Tangent"/> instead of
        /// this record's per-CANDIDATE <see cref="AnchorLocal"/> (which a curved candidate leaves default —
        /// one anchor can't serve every glyph of an along-line label). False (default) on a point candidate.</summary>
        public bool AlongLine;
    }
}
