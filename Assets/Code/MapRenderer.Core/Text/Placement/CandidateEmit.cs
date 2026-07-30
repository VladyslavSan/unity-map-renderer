// Engine-free: no UnityEngine dependency. Pure blittable data carrier. TOP-LEVEL `using Unity.Mathematics;`
// + unqualified float3/double3 — this file lives in MapRenderer.Core.Text.Placement (see PolylineArcWalker
// for the namespace-collision trap an inline `Unity.Mathematics.X` would hit).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The draw-side payload of one collision candidate: the contiguous range of staged
    /// <see cref="PlacedQuad"/>s to emit if it survives, and the material/mesh slot to emit them into.
    /// <para><b>NOT keyed by <see cref="LabelCandidate.LabelIndex"/>.</b> Since §10 (D8) a candidate owns a
    /// RANGE of emits — <see cref="LabelCandidate.EmitStart"/>/<see cref="LabelCandidate.EmitCount"/>, mirroring
    /// <see cref="LabelCandidate.BoxStart"/>/<see cref="LabelCandidate.BoxCount"/> — because a centred icon+text
    /// pair is ONE candidate emitting TWO of these (the halves live in different atlases, so they cannot share
    /// one emit). Indexing this pool by <c>LabelIndex</c> reads the wrong emit for every candidate after the
    /// first pair; always go through the owning candidate's range. Collision may still sort the candidates
    /// freely — the ranges point INTO this pool and are unaffected.</para>
    /// Blittable (all ints) so the staging math can fill it in a Burst job (Lever C).
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

        /// <summary>
        /// P-B: a constant CPU-side quad rotation (radians) applied ON TOP of the shader's live tangent
        /// rotation — read ONLY when <see cref="AlongLine"/> is true, where
        /// <see cref="PlacedQuad.RotationRadians"/> is deliberately unusable (the renderer forces it to 0 so
        /// the rotation comes from the projected world <see cref="PlacedQuad.Tangent"/> instead, Stage AC).
        /// Today's only source is <c>icon-rotate</c> on a map-aligned line icon; curved text leaves it 0, so
        /// the renderer passes the same <c>0f</c> it used to hardcode.
        /// <para>A per-CANDIDATE field rather than a per-quad one because an along-line candidate has exactly
        /// one emit. Deliberately NOT folded into <see cref="PlacedQuad.RotationRadians"/>: curved text writes
        /// a live tangent angle into that field for the dead screen path, so a future reader composing both
        /// would double-rotate.</para>
        /// <para><b>Sign.</b> This value is ALREADY in <c>BillboardMath</c>'s rotation sense
        /// (positive = counter-clockwise on screen), the same frame the point path's composed
        /// <see cref="PlacedQuad.RotationRadians"/> is in: <c>StageCurvedAnchor</c> writes it through
        /// <see cref="LabelBearing.IconRotationRadians"/>, which is the ONE place <c>icon-rotate</c>'s
        /// clockwise-positive convention is converted. Do not negate here or at the write site — that flip
        /// point has moved into <see cref="LabelBearing"/>, and doing it twice would restore the bug it
        /// fixed. The shader's tangent rotation acts on the already-converted offsets, so the two compose
        /// additively in one frame.</para>
        /// </summary>
        public float ExtraRotationRadians;
    }
}
