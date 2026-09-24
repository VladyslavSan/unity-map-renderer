// Namespace-collision guard (see GlyphAtlasTexture.cs's header): TOP-LEVEL `using Unity.Mathematics;` and
// unqualified types, never an inline `Unity.Mathematics.X`.

using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// One tile's native mirror of its slice of <see cref="SymbolBatch"/>, baked once at tile-build commit
    /// (<see cref="SymbolTileBlockBaker.Bake"/>). One dense slot per <see cref="SymbolTileBuffer.Symbols"/>
    /// entry, so the <c>(blockId, localIndex)</c> winner plan indexes these columns directly.
    /// Non-local invariant: the <c>SymbolTileStore</c> entry is the single owner and disposes it at each
    /// drop site, never on a stale-survives pull (<c>BeginBuild</c>) or a cache-hit move (<c>Restore</c>).
    /// </summary>
    internal sealed class SymbolTileBlock : VerifiedDisposable
    {
        // Every array is allocated at its final size (SymbolTileBlockBaker.CountSizes), so its Length is its
        // element count. No companion count field exists, because a count can disagree with Length.

        // ── per-symbol columns, RAW list order (dense — one slot per source symbol) ──
        // Length == the symbols list length.
        internal NativeArray<SymbolPlacementKind> Kinds;
        internal NativeArray<int>     Detail;      // index into Points[] or Curveds[] (by Kinds[i])
        internal NativeArray<int>     WorldStart;  // start of this symbol's world points in WorldPoints
        internal NativeArray<int>     WorldCount;  // 1 (point anchor) or path length (curved)
        internal NativeArray<double3> RepAnchor;   // the distance-cull point (RepresentativeAnchor)
        // Per-symbol column in RAW order, mirroring the symbol's MaterialIndex — baked so the off-main
        // reconciler can read it from the block instead of dereferencing the managed source symbol.
        internal NativeArray<int>     MaterialIndexes;
        // The resolved pair role per raw slot (None/Owner/Rider), baked from SymbolPairing over the tile list.
        // A curved symbol is never paired, so its role is None.
        internal NativeArray<SymbolPairRole> PairRoles;
        // Interned ids in raw order (SymbolStringTable): TextIds[i] == Intern(symbol.Text), 0 for null Text,
        // and likewise for IconImageIds. The off-main dedup keys on these int columns.
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
        // The unit surface normal at each WorldPoints entry — index-parallel, same WorldStart/WorldCount.
        internal NativeArray<float3>      WorldUps;
        internal NativeArray<long>        AnchorFadeIds; // per curved anchor + a trailing fallback slot

        // ── staging output upper bounds (mirrors SymbolBatch.Max* — independent of the camera) ──
        internal int MaxBoxes;
        internal int MaxQuads;
        internal int MaxCandidates;

        /// <summary>The physical tile every symbol in this block belongs to — one bake is always
        /// one (source, tile) build's output, so a single key serves the whole block.</summary>
        internal long TileKey;

        /// <summary>Live (constructed, not yet disposed) block count, the same leak-guard idiom as
        /// <c>MeshDataPayload.DebugLiveAllocCount</c>. A throwing <see cref="SymbolTileBlockBaker.Bake"/>
        /// disposes its partial block, so this returns to baseline. Debug/test instrumentation only.</summary>
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
