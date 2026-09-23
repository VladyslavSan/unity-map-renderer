// TOP-LEVEL `using Unity.Mathematics;` + unqualified float3/double3: an inline `Unity.Mathematics.X` hits a
// namespace collision inside MapRenderer.Core.Text.Placement (see SymbolScreenProjection).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The draw-side payload of one collision candidate: the contiguous range of staged <see cref="PlacedQuad"/>s
    /// to emit if it survives, and the material/mesh slot to emit them into. Blittable, so the staging math can
    /// fill it in a Burst job.
    /// <para>NOT keyed by <see cref="SymbolCandidate.SymbolIndex"/>: a centred icon+text pair is ONE candidate
    /// with TWO emits (the halves use different atlases), so a candidate owns the range
    /// <see cref="SymbolCandidate.EmitStart"/>/<see cref="SymbolCandidate.EmitCount"/>. Indexing by
    /// <c>SymbolIndex</c> reads the wrong emit after the first pair. Sorting candidates leaves the ranges valid.</para>
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

        /// <summary>The world-anchored draw payload, read ONLY by
        /// <see cref="MapRenderer.Unity.Text.Placement.WorldSymbolRenderer"/> when <see cref="IsWorld"/> is
        /// true — never a batch tile array indexed by <c>tileIndex</c>.
        /// <see cref="AnchorLocal"/>/<see cref="TileOriginRender"/> mirror <see cref="PointStageInput"/>'s
        /// identically-named fields (the SAME bake); <see cref="TileKey"/> keys the renderer's per-
        /// (tile,slot,kind) slot dictionary.</summary>
        public float3 AnchorLocal;

        /// <summary>See <see cref="PointStageInput.TileOriginRender"/> — the render-space origin
        /// <see cref="AnchorLocal"/> was baked against.</summary>
        public double3 TileOriginRender;

        /// <summary>The symbol's tile key (the slot-dictionary key) — always populated on a point candidate
        /// (mirrors <see cref="PointStageInput.TileKey"/>), default 0 on a curved candidate (unread, since
        /// <see cref="IsWorld"/> is false there).</summary>
        public long TileKey;

        /// <summary>The additive screen-space corner offset from
        /// <c>text-translate</c>, computed by <see cref="SymbolStagingMath.StagePoint"/> as
        /// <c>screenPx − s.ScreenPx</c> (both already resolved there). The world path does not project the
        /// anchor, so it cannot fold the translate into the anchor's screen position; instead
        /// <c>BillboardMath.BuildWorldQuad</c> adds this to every corner's <c>Offset</c> (same Y negation
        /// as the corner). Default <c>float2.zero</c> — no-op for the common (untranslated) case.</summary>
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
        /// A constant CPU-side quad rotation (radians) added to the shader's live tangent rotation. Read ONLY when
        /// <see cref="AlongLine"/> is true, where the renderer forces <see cref="PlacedQuad.RotationRadians"/> to 0.
        /// The only source is <c>icon-rotate</c> on a map-aligned line icon; curved text leaves it 0. It is not
        /// folded into <see cref="PlacedQuad.RotationRadians"/>, which curved text fills with a tangent angle, so
        /// composing both would double-rotate.
        /// <para>Sign, a non-local invariant: the value is ALREADY in <c>BillboardMath</c>'s sense (positive =
        /// counter-clockwise on screen). <c>StageCurvedAnchor</c> writes it through
        /// <see cref="SymbolBearing.IconRotationRadians"/>, the ONE place <c>icon-rotate</c>'s clockwise-positive
        /// convention is converted. Do not negate it again.</para>
        /// </summary>
        public float ExtraRotationRadians;

        /// <summary>The unit surface normal at this candidate's anchor (point/icon arm) — pre-RTC
        /// render-space DIRECTION, from <c>IProjection.ProjectPoint(...).Up</c> via
        /// <see cref="PointStageInput.SurfaceUp"/>. The renderer writes it to the vertex stream, but no shader
        /// reads it: a point emit is never map-pitched. The curved arm takes its up from each
        /// <see cref="PlacedQuad.SurfaceUp"/> instead.</summary>
        public float3 SurfaceUp;

        /// <summary>
        /// The UNIT of this emit's corner offsets. <c>0</c> (the struct's zero value, which every point emit and
        /// every non-map-pitched curved emit keeps) ⇒
        /// <c>WorldBillboardVertex.Offset</c> is in LOGICAL SCREEN PIXELS. <c>&gt; 0</c> ⇒ it is in WORLD METRES,
        /// and this is the metres-per-logical-pixel factor (<c>BuildWorldQuad</c>'s <c>emScale = TextSizePx · this</c>).
        /// <para>One value carries both the conversion and the shader's bit2, a non-local invariant: a separate
        /// flag could disagree with the scale and make the shader read pixels as metres.
        /// <c>WorldSymbolRenderer.Emit</c> derives both from it. Only <see cref="SymbolStagingMath.StageCurved"/>'s
        /// <c>worldArc</c> predicate writes it, with the value <c>arcScale</c> spaces the glyphs by, so a
        /// map-pitched symbol's spacing and size foreshorten together.</para>
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
