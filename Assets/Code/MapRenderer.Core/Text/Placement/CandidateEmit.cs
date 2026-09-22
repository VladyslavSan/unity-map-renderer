// Engine-free: no UnityEngine dependency. Pure blittable data carrier. TOP-LEVEL `using Unity.Mathematics;`
// + unqualified float3/double3 — this file lives in MapRenderer.Core.Text.Placement (see SymbolScreenProjection
// for the namespace-collision trap an inline `Unity.Mathematics.X` would hit).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The draw-side payload of one collision candidate: the contiguous range of staged
    /// <see cref="PlacedQuad"/>s to emit if it survives, and the material/mesh slot to emit them into.
    /// <para><b>NOT keyed by <see cref="SymbolCandidate.SymbolIndex"/>.</b> A candidate owns a
    /// RANGE of emits — <see cref="SymbolCandidate.EmitStart"/>/<see cref="SymbolCandidate.EmitCount"/>, mirroring
    /// <see cref="SymbolCandidate.BoxStart"/>/<see cref="SymbolCandidate.BoxCount"/> — because a centred icon+text
    /// pair is ONE candidate emitting TWO of these (the halves live in different atlases, so they cannot share
    /// one emit). Indexing this pool by <c>SymbolIndex</c> reads the wrong emit for every candidate after the
    /// first pair; always go through the owning candidate's range. Collision may still sort the candidates
    /// freely — the ranges point INTO this pool and are unaffected.</para>
    /// Blittable (all ints) so the staging math can fill it in a Burst job.
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
        /// <c>screenPx − s.ScreenPx</c> (both already resolved there) — the world path does not project the
        /// anchor, so it cannot fold the translate into it the way the OLD path does; instead
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
        /// A constant CPU-side quad rotation (radians) applied ON TOP of the shader's live tangent
        /// rotation — read ONLY when <see cref="AlongLine"/> is true, where
        /// <see cref="PlacedQuad.RotationRadians"/> is unusable (the renderer forces it to 0 so the rotation
        /// comes from the projected world <see cref="PlacedQuad.Tangent"/> instead).
        /// Today's only source is <c>icon-rotate</c> on a map-aligned line icon; curved text leaves it 0, so
        /// the renderer passes the same <c>0f</c> it used to hardcode.
        /// <para>A per-CANDIDATE field rather than a per-quad one because an along-line candidate has exactly
        /// one emit. Deliberately NOT folded into <see cref="PlacedQuad.RotationRadians"/>: curved text writes
        /// a live tangent angle into that field for the dead screen path, so a future reader composing both
        /// would double-rotate.</para>
        /// <para><b>Sign.</b> This value is ALREADY in <c>BillboardMath</c>'s rotation sense
        /// (positive = counter-clockwise on screen), the same frame the point path's composed
        /// <see cref="PlacedQuad.RotationRadians"/> is in: <c>StageCurvedAnchor</c> writes it through
        /// <see cref="SymbolBearing.IconRotationRadians"/>, which is the ONE place <c>icon-rotate</c>'s
        /// clockwise-positive convention is converted. Do not negate here or at the write site — that flip
        /// point has moved into <see cref="SymbolBearing"/>, and doing it twice would restore the bug it
        /// fixed. The shader's tangent rotation acts on the already-converted offsets, so the two compose
        /// additively in one frame.</para>
        /// </summary>
        public float ExtraRotationRadians;

        /// <summary>The unit surface normal at this candidate's anchor (point/icon arm) — pre-RTC
        /// render-space DIRECTION, from <c>IProjection.ProjectPoint(...).Up</c> via
        /// <see cref="PointStageInput.SurfaceUp"/>. Written, but not yet read by any shader.</summary>
        public float3 SurfaceUp;

        /// <summary>
        /// The UNIT of this emit's corner offsets, and the factor that produces it.
        /// <list type="bullet">
        /// <item><b><c>0</c></b> ⇒ <c>WorldBillboardVertex.Offset</c> is in LOGICAL SCREEN PIXELS (the
        /// pre-W2 unit). This is the struct's zero value, so every point emit, every non-map-pitched curved
        /// emit and every hand-built fixture emit keeps the old behaviour without being edited.</item>
        /// <item><b>&gt; <c>0</c></b> ⇒ those offsets are in WORLD METRES, and this is the metres-per-logical-
        /// pixel factor the renderer multiplied them by
        /// (<c>BuildWorldQuad</c>'s <c>emScale = TextSizePx · this</c>).</item>
        /// </list>
        /// <para><b>One value carries BOTH the unit conversion and the shader's bit2.</b> There is no
        /// separate <c>bool MapPitched</c>: a design where a flag and a scale can disagree is a design where
        /// the shader can reinterpret pixels as metres. <c>WorldSymbolRenderer.Emit</c> derives the
        /// multiplier and the flag from this one field.</para>
        /// <para><b>Written in exactly one place</b> — <see cref="SymbolStagingMath.StageCurved"/>'s
        /// <c>worldArc</c> predicate, carried into <c>StageCurvedAnchor</c>'s emit. It is the SAME value
        /// <c>arcScale</c> already spaces the glyph anchors with, so a map-pitched symbol's spacing and its
        /// drawn size come from one constant and foreshorten together: a map-pitched <c>text-size</c> is
        /// X px TOP-DOWN, like <c>line-width</c>.</para>
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
