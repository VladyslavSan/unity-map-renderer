// TOP-LEVEL `using Unity.Mathematics;` + unqualified float3/double3: an inline `Unity.Mathematics.X` hits a
// namespace collision inside MapRenderer.Core.Text.Placement (see SymbolScreenProjection).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The draw-side payload of one collision candidate: the contiguous range of staged <see cref="PlacedQuad"/>s
    /// to emit if it survives, and the slot to emit them into; blittable, for a Burst job. A candidate owns the
    /// range <see cref="SymbolCandidate.EmitStart"/>/<see cref="SymbolCandidate.EmitCount"/>, not one entry per
    /// <see cref="SymbolCandidate.SymbolIndex"/>, because an icon+text pair is one candidate with two emits.
    /// </summary>
    public struct CandidateEmit
    {
        /// <summary>Index of this candidate's first quad in the flat staged-quad pool.</summary>
        public int QuadStart;

        /// <summary>Number of quads this candidate emits (1 point symbol → its glyph quads; curved → N glyphs).</summary>
        public int QuadCount;

        /// <summary>Per-symbol-layer material/mesh slot the surviving quads draw into.</summary>
        public int Slot;

        /// <summary>The texture the emitted quads sample from (glyph atlas vs. sprite atlas), carried
        /// from <see cref="PointStageInput.AtlasKind"/>. Default <see cref="SymbolKind.Text"/>.</summary>
        public SymbolKind AtlasKind;

        /// <summary>The world-anchored draw payload, read only by
        /// <see cref="MapRenderer.Unity.Text.Placement.WorldSymbolRenderer"/> when <see cref="IsWorld"/> is true.
        /// It mirrors <see cref="PointStageInput"/>'s same-named fields; <see cref="TileKey"/> keys the
        /// renderer's per-(tile,slot,kind) dictionary.</summary>
        public float3 AnchorLocal;

        /// <summary>See <see cref="PointStageInput.TileOriginRender"/> — the render-space origin
        /// <see cref="AnchorLocal"/> was baked against.</summary>
        public double3 TileOriginRender;

        /// <summary>The symbol's tile key (the slot-dictionary key) — always populated on a point candidate
        /// (mirrors <see cref="PointStageInput.TileKey"/>), default 0 on a curved candidate (unread, since
        /// <see cref="IsWorld"/> is false there).</summary>
        public long TileKey;

        /// <summary>The <c>text-translate</c> screen offset (<c>screenPx − s.ScreenPx</c>). The world path does not
        /// project the anchor, so <c>BillboardMath.BuildWorldQuad</c> adds this to every corner's <c>Offset</c>
        /// instead. Zero when untranslated.</summary>
        public float2 TranslateDeltaPx;

        /// <summary>True when the world-anchored draw sink should read this candidate — set by BOTH
        /// <see cref="SymbolStagingMath.StagePoint"/> and <see cref="SymbolStagingMath.StageCurved"/>;
        /// the emit loop branches on this WITHOUT a per-candidate record lookup. See <see cref="AlongLine"/>
        /// for which of the two it was.</summary>
        public bool IsWorld;

        /// <summary>True ONLY when <see cref="SymbolStagingMath.StageCurved"/> set
        /// this candidate's emit — <see cref="MapRenderer.Unity.Text.Placement.WorldSymbolRenderer"/> reads
        /// each quad's OWN <see cref="PlacedQuad.AnchorLocal"/>/<see cref="PlacedQuad.Tangent"/> instead of
        /// this record's per-CANDIDATE <see cref="AnchorLocal"/> (which a curved candidate leaves default —
        /// one anchor can't serve every glyph of an along-line symbol). False (default) on a point candidate.</summary>
        public bool AlongLine;

        /// <summary>
        /// A constant quad rotation (radians) added to the shader's tangent rotation, read only when
        /// <see cref="AlongLine"/> is true; its one source is <c>icon-rotate</c> on a line icon (curved text: 0).
        /// Non-local invariant: it is already in <c>BillboardMath</c>'s counter-clockwise sense, converted once by
        /// <see cref="SymbolBearing.IconRotationRadians"/>, so do not negate it again.
        /// </summary>
        public float ExtraRotationRadians;

        /// <summary>The unit surface normal at this candidate's anchor (point/icon arm) — pre-RTC
        /// render-space DIRECTION, from <c>IProjection.ProjectPoint(...).Up</c> via
        /// <see cref="PointStageInput.SurfaceUp"/>. The renderer writes it to the vertex stream, but no shader
        /// reads it: a point emit is never map-pitched. The curved arm takes its up from each
        /// <see cref="PlacedQuad.SurfaceUp"/> instead.</summary>
        public float3 SurfaceUp;

        /// <summary>
        /// The UNIT of this emit's corner offsets: 0 (point and viewport-pitched curved emits) = logical screen
        /// px; &gt; 0 = world metres, with this as the metres-per-logical-pixel factor (<c>emScale = TextSizePx · this</c>).
        /// Non-local invariant: <c>WorldSymbolRenderer.Emit</c> derives both the scale and the shader's bit2 from
        /// this one value, so they cannot disagree; only <see cref="SymbolStagingMath.StageCurved"/> writes it,
        /// with the factor that spaces the glyphs.
        /// </summary>
        public float CornerMetresPerLogicalPixel;

        /// <summary><c>text-halo-color</c>, pre-linearized, with the halo colour's OWN alpha in <c>.w</c>
        /// (text-opacity rides the quad's colour, and the emit multiplies the two). Carried per emit because
        /// a halo is drawn as a SECOND copy of this label's glyph run — see
        /// <c>WorldSymbolRenderer.Emit</c>.</summary>
        public float4 HaloColor;

        /// <summary><c>text-halo-width</c> in LOGICAL px — how far the halo run grows the glyph past its
        /// fill edge. Zero means no halo run at all, which is the spec default.</summary>
        public float HaloWidthPx;

        /// <summary><c>text-halo-blur</c> in LOGICAL px — how much the halo run widens the AA transition.
        /// Scaled to device px by the SAME factor as <see cref="HaloWidthPx"/> (both are added to a
        /// signed distance the SDF shader carries in device px, so scaling one without the other renders the
        /// halo inconsistently at dpr ≠ 1).</summary>
        public float HaloBlurPx;
    }
}
