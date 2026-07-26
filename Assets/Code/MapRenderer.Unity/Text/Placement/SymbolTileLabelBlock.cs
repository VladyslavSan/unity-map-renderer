// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, never an inline
// `Unity.Mathematics.X`. Unity-side (needs Unity.Collections' NativeArray — Core stays engine-free).

using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 1 (design doc §4, §5 B): one tile's baked, NATIVE mirror of the
    /// per-tile slice of <see cref="SymbolLabelBatch"/>'s SoA — built ONCE at tile-build commit
    /// (<see cref="SymbolTileLabelBlockBaker.Bake"/>) instead of every frame, and held on the tile's
    /// <c>SymbolTileLabelStore</c> entry with the SAME lifecycle as its <c>Labels</c> (kept warm across a
    /// release→cache→restore round trip, dropped only when the labels themselves drop).
    ///
    /// <para><b>Stage 1 scope: purely additive.</b> NOTHING reads this block's arrays yet — the production
    /// per-frame path (the parity oracle's <c>Build</c> + a per-frame mirror copy)
    /// is unchanged. Stage 1 only proves the bake + native lifetime out; Stage 2 wires a per-frame gather that
    /// compacts winning blocks' slices straight into the placement job's NativeLists.</para>
    ///
    /// <para><b>Null-slot invariant.</b> Indexed by the RAW <c>List&lt;LabelInstance&gt;</c> position the
    /// baker walked, NOT a compacted index — a <c>null</c> label at raw index <c>i</c> (a per-label build
    /// failure — see <c>StyledSymbolTileBuilder</c>'s per-label isolation) bakes an INERT record at
    /// <see cref="Kinds"/>/<see cref="Detail"/>[i]: <see cref="LabelRecordKind.Point"/>, zero quad/world
    /// contribution, zero <see cref="MaxBoxes"/>/<see cref="MaxQuads"/>/<see cref="MaxCandidates"/> share. So
    /// <c>localIndex == i</c> always maps straight back to the source list position and a later real label
    /// never renumbers when an earlier slot is null — the invariant Stage 2's <c>(blockId, localIndex)</c>
    /// winner plan depends on.</para>
    ///
    /// <para><b>Lifetime.</b> Every array is <see cref="Allocator.Persistent"/>, freed exactly once
    /// (<see cref="DoDispose"/>, idempotent via <see cref="VerifiedDisposable"/> — the codebase's shared
    /// dispose-once + leak-finalizer base, reused here rather than hand-rolling a fresh guard). Single owner:
    /// the store's entry holds it as a bare <c>System.IDisposable</c> (engine-free store, see
    /// <c>SymbolTileLabelStore</c>'s header) and disposes it at each drop site (commit-overwrite / FIFO-evict /
    /// true-release / Clear) — never on a stale-survives (<c>BeginBuild</c> pull) or a cache-hit move
    /// (<c>Restore</c>). <see cref="SymbolTileLabelBlockBaker.Bake"/> allocates into a fresh instance of this
    /// type and disposes it on ANY exception mid-bake (its own field doc) — no partial-allocation leak.</para>
    /// </summary>
    internal sealed class SymbolTileLabelBlock : VerifiedDisposable
    {
        // Every array below is allocated at its EXACT final size: SymbolTileLabelBlockBaker counts each pool
        // in a first pass (CountSizes) with the same per-label arithmetic the fill pass then uses, so each
        // array's own Length IS its element count. There are deliberately no `XXXCount` companion fields —
        // eight of them existed, duplicated Length exactly, and had zero production readers (SymbolBlockView
        // omits pool counts by design: the gather indexes only by a winner's LocalIndex/Detail/*Start). A
        // count that can disagree with Length is a bug waiting to happen; Length cannot disagree with itself.

        // ── per-label records, RAW list order (nulls are inert placeholders — see the type doc) ──
        // Length == the raw labels list length (incl. inert null slots).
        internal NativeArray<byte>    Kinds;
        internal NativeArray<int>     Detail;      // index into Points[] or Curveds[] (by Kinds[i])
        internal NativeArray<int>     WorldStart;  // start of this label's world points in WorldPoints
        internal NativeArray<int>     WorldCount;  // 1 (point anchor) or path length (curved); 0 for an inert null slot
        internal NativeArray<double3> RepAnchor;   // the B-3 distance-cull point (RepresentativeAnchor)

        // ── point details ──
        internal NativeArray<PointStageInput> Points; // stable fields; dynamic (screen/depth/incumbency) patched per frame downstream
        internal NativeArray<int> PointQuadStart;      // into Quads
        internal NativeArray<int> PointQuadCount;

        // ── curved details ──
        internal NativeArray<CurvedStageInput> Curveds;
        internal NativeArray<int> CurvedGlyphStart;    // into Glyphs
        internal NativeArray<int> CurvedGlyphCount;
        internal NativeArray<int> CurvedAnchorStart;   // into Anchors
        internal NativeArray<int> CurvedAnchorCount;
        internal NativeArray<int> CurvedAnchorFadeStart; // into AnchorFadeIds ([count) anchors + 1 fallback)

        // ── flat pools ──
        internal NativeArray<SymbolQuad>  Quads;
        internal NativeArray<CurvedGlyph> Glyphs;
        internal NativeArray<LineAnchor>  Anchors;
        internal NativeArray<double3>     WorldPoints; // anchor (point) / path verts (curved) — for projection
        internal NativeArray<long>        AnchorFadeIds; // per curved anchor + a trailing fallback slot

        // ── staging output upper bounds (mirrors SymbolLabelBatch.Max* — independent of the camera) ──
        internal int MaxBoxes;
        internal int MaxQuads;
        internal int MaxCandidates;

        /// <summary>The physical tile every (non-inert) label in this block belongs to — one bake is always
        /// one (source, tile) build's output, so a single key serves the whole block.</summary>
        internal long TileKey;

        /// <summary>Live (constructed, not yet Disposed) block count — mirrors
        /// <c>MeshDataPayload.DebugLiveAllocCount</c>'s established leak-guard idiom (S48/S51) for the same
        /// assertion shape: a deliberately-undisposed block shows a non-zero delta (positive control), and a
        /// throwing bake (which disposes its own partial block before rethrowing — see
        /// <see cref="SymbolTileLabelBlockBaker.Bake"/>'s (G)) must return this to baseline. Debug/test
        /// instrumentation only — no production logic reads it.</summary>
        internal static long DebugLiveAllocCount;

        internal SymbolTileLabelBlock() => System.Threading.Interlocked.Increment(ref DebugLiveAllocCount);

        /// <summary>Frees every allocated array (guarded by <see cref="NativeArray{T}.IsCreated"/> so a
        /// partial bake — some fields never assigned before a mid-bake throw — disposes cleanly, and a
        /// second call, e.g. from <see cref="SymbolTileLabelBlockBaker.Bake"/>'s catch after
        /// <see cref="VerifiedDisposable"/>'s own idempotency already ran it, is a safe no-op).</summary>
        protected override void DoDispose()
        {
            System.Threading.Interlocked.Decrement(ref DebugLiveAllocCount);
            if (Kinds.IsCreated) Kinds.Dispose();
            if (Detail.IsCreated) Detail.Dispose();
            if (WorldStart.IsCreated) WorldStart.Dispose();
            if (WorldCount.IsCreated) WorldCount.Dispose();
            if (RepAnchor.IsCreated) RepAnchor.Dispose();

            if (Points.IsCreated) Points.Dispose();
            if (PointQuadStart.IsCreated) PointQuadStart.Dispose();
            if (PointQuadCount.IsCreated) PointQuadCount.Dispose();

            if (Curveds.IsCreated) Curveds.Dispose();
            if (CurvedGlyphStart.IsCreated) CurvedGlyphStart.Dispose();
            if (CurvedGlyphCount.IsCreated) CurvedGlyphCount.Dispose();
            if (CurvedAnchorStart.IsCreated) CurvedAnchorStart.Dispose();
            if (CurvedAnchorCount.IsCreated) CurvedAnchorCount.Dispose();
            if (CurvedAnchorFadeStart.IsCreated) CurvedAnchorFadeStart.Dispose();

            if (Quads.IsCreated) Quads.Dispose();
            if (Glyphs.IsCreated) Glyphs.Dispose();
            if (Anchors.IsCreated) Anchors.Dispose();
            if (WorldPoints.IsCreated) WorldPoints.Dispose();
            if (AnchorFadeIds.IsCreated) AnchorFadeIds.Dispose();
        }
    }
}
