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
    /// One tile's baked, NATIVE mirror of the
    /// per-tile slice of <see cref="SymbolBatch"/>'s SoA — built ONCE at tile-build commit
    /// (<see cref="SymbolTileBlockBaker.Bake"/>) instead of every frame, and held on the tile's
    /// <c>SymbolTileStore</c> entry with the SAME lifecycle as its <c>SymbolPlacementSystem</c> (kept warm across a
    /// release→cache→restore round trip, dropped only when the symbols themselves drop).
    ///
    /// <para><b>Dense per-symbol mirror.</b> Indexed one slot per <see cref="SymbolTileBuffer.Symbols"/> entry
    /// in the order the baker walked. The source list is itself dense — <c>StyledSymbolTileBuilder</c> skips a
    /// per-symbol build failure outright rather than recording a gap — so <c>localIndex == i</c> is the source
    /// symbol position and Stage 2's <c>(blockId, localIndex)</c> winner plan indexes straight into these
    /// columns.</para>
    ///
    /// <para><b>Lifetime.</b> Every array is <see cref="Allocator.Persistent"/>, freed exactly once
    /// (<see cref="DoDispose"/>, idempotent via <see cref="VerifiedDisposable"/> — the codebase's shared
    /// dispose-once + leak-finalizer base, reused here rather than hand-rolling a fresh guard). Single owner:
    /// the store's entry holds it as a bare <c>System.IDisposable</c> (engine-free store, see
    /// <c>SymbolTileStore</c>'s header) and disposes it at each drop site (commit-overwrite / FIFO-evict /
    /// true-release / Clear) — never on a stale-survives (<c>BeginBuild</c> pull) or a cache-hit move
    /// (<c>Restore</c>). <see cref="SymbolTileBlockBaker.Bake"/> allocates into a fresh instance of this
    /// type and disposes it on ANY exception mid-bake (its own field doc) — no partial-allocation leak.</para>
    /// </summary>
    internal sealed class SymbolTileBlock : VerifiedDisposable
    {
        // Every array below is allocated at its EXACT final size: SymbolTileBlockBaker counts each pool
        // in a first pass (CountSizes) with the same per-symbol arithmetic the fill pass then uses, so each
        // array's own Length IS its element count. There are deliberately no `XXXCount` companion fields —
        // eight of them existed, duplicated Length exactly, and had zero production readers (SymbolBlockView
        // omits pool counts by design: the gather indexes only by a winner's LocalIndex/Detail/*Start). A
        // count that can disagree with Length is a bug waiting to happen; Length cannot disagree with itself.

        // ── per-symbol symbols, RAW list order (dense — one slot per source symbol) ──
        // Length == the symbols list length.
        internal NativeArray<SymbolPlacementKind> Kinds;
        internal NativeArray<int>     Detail;      // index into Points[] or Curveds[] (by Kinds[i])
        internal NativeArray<int>     WorldStart;  // start of this symbol's world points in WorldPoints
        internal NativeArray<int>     WorldCount;  // 1 (point anchor) or path length (curved)
        internal NativeArray<double3> RepAnchor;   // the B-3 distance-cull point (RepresentativeAnchor)
        // Native-representation migration: per-symbol column in RAW order, mirroring the symbol's MaterialIndex —
        // baked so the off-main reconciler can read it from the block instead of dereferencing the managed source
        // symbol.
        internal NativeArray<int>     MaterialIndexes;
        // Native-representation migration: the RESOLVED pair role per raw slot (None/Owner/Rider), baked from the
        // SAME SymbolPairing resolution the reconciler used to run over the tile list — so the reconciler reads the
        // baked role instead of re-resolving (byte-identical because two computations over the same immutable list
        // agree). None for every curved symbol (the §10 fence — a curved symbol is never paired).
        internal NativeArray<SymbolPairRole> PairRoles;
        // Native-representation migration (additive): the interned text/icon ids in RAW order — same intern table
        // (SymbolStringTable) the store's CompleteBuild feeds, so TextIds[i] == Intern(symbol.Text) (0 for null
        // Text; likewise IconImageIds[i] == Intern(symbol.IconImage)). Baked so the off-main dedup can
        // key on the block's int columns instead of the entry's parallel int[] arrays. Additive stage: WRITTEN by
        // the bake (identical ids to the store's own arrays — idempotent interning), not consumed by any reader yet.
        internal NativeArray<int>     TextIds;
        internal NativeArray<int>     IconImageIds;

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
        // P2: the unit surface normal at each WorldPoints entry — index-parallel, same WorldStart/WorldCount.
        // Written by the bake; not yet consumed by any downstream reader (P3 reads it).
        internal NativeArray<float3>      WorldUps;
        internal NativeArray<long>        AnchorFadeIds; // per curved anchor + a trailing fallback slot

        // ── staging output upper bounds (mirrors SymbolBatch.Max* — independent of the camera) ──
        internal int MaxBoxes;
        internal int MaxQuads;
        internal int MaxCandidates;

        /// <summary>The physical tile every symbol in this block belongs to — one bake is always
        /// one (source, tile) build's output, so a single key serves the whole block.</summary>
        internal long TileKey;

        /// <summary>Live (constructed, not yet Disposed) block count — mirrors
        /// <c>MeshDataPayload.DebugLiveAllocCount</c>'s established leak-guard idiom (S48/S51) for the same
        /// assertion shape: a deliberately-undisposed block shows a non-zero delta (positive control), and a
        /// throwing bake (which disposes its own partial block before rethrowing — see
        /// <see cref="SymbolTileBlockBaker.Bake"/>'s (G)) must return this to baseline. Debug/test
        /// instrumentation only — no production logic reads it.</summary>
        internal static long DebugLiveAllocCount;

        internal SymbolTileBlock() => System.Threading.Interlocked.Increment(ref DebugLiveAllocCount);

        /// <summary>Frees every allocated array. A partial bake (some fields never assigned before a
        /// mid-bake throw) and a second call after <see cref="VerifiedDisposable"/>'s own idempotency both
        /// dispose cleanly on their own: <see cref="NativeArray{T}"/>'s <c>Dispose</c> early-returns on an
        /// uncreated/default value, so no per-field guard is needed.</summary>
        protected override void DoDispose()
        {
            System.Threading.Interlocked.Decrement(ref DebugLiveAllocCount);
            Kinds.Dispose();
            Detail.Dispose();
            WorldStart.Dispose();
            WorldCount.Dispose();
            RepAnchor.Dispose();
            MaterialIndexes.Dispose();
            PairRoles.Dispose();
            TextIds.Dispose();
            IconImageIds.Dispose();

            Points.Dispose();
            PointQuadStart.Dispose();
            PointQuadCount.Dispose();

            Curveds.Dispose();
            CurvedGlyphStart.Dispose();
            CurvedGlyphCount.Dispose();
            CurvedAnchorStart.Dispose();
            CurvedAnchorCount.Dispose();
            CurvedAnchorFadeStart.Dispose();

            Quads.Dispose();
            Glyphs.Dispose();
            Anchors.Dispose();
            WorldPoints.Dispose();
            WorldUps.Dispose();
            AnchorFadeIds.Dispose();
        }
    }
}
