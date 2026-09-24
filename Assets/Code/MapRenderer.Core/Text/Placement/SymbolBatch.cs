// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double3 — this
// file lives in MapRenderer.Core.Text.Placement (see SymbolScreenProjection for the namespace-collision trap).

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The blittable Structure-of-Arrays for one collected symbol set, the per-frame placement source of truth.
    /// It is rebuilt only when <c>SymbolTileStore.Version</c> changes, and every per-frame pass reads it with no
    /// managed iteration. Non-local invariant: records keep the collected order, so collision ordinals and the
    /// built mesh are deterministic; <see cref="Kinds"/> and <see cref="Detail"/> index the per-kind arrays.
    /// Arrays grow and never shrink (<see cref="Reset"/> rewinds counts); per-frame values live with the consumer.
    /// </summary>
    public sealed class SymbolBatch
    {
        // ── per-symbol records, in collected order ──
        public SymbolPlacementKind[] Kinds = Array.Empty<SymbolPlacementKind>();
        public int[]     Detail     = Array.Empty<int>();   // index into Points[] or Curveds[]
        public int[]     WorldStart = Array.Empty<int>();   // start of this symbol's world points in WorldPoints
        public int[]     WorldCount = Array.Empty<int>();   // 1 (point anchor) or path length (curved)
        public double3[] RepAnchor  = Array.Empty<double3>(); // the distance-cull point (RepresentativeAnchor)
        public bool[]    SymbolDeparting = Array.Empty<bool>(); // per record → its tile is leaving cover (fade OUT, don't pop)
        // Separate from SymbolDeparting: a coverage-fading tile is still active (loaded, in cover); only its
        // on-screen coverage crossed below threshold (SymbolTileCoverageFilter).
        public bool[]    SymbolCoverageFading = Array.Empty<bool>(); // per record → its tile's coverage crossed below threshold (fade OUT, don't pop)
        public int       Count;                             // number of records (symbols)

        // ── point details ──
        public PointStageInput[] Points     = Array.Empty<PointStageInput>(); // stable fields; dynamic patched per frame
        public int[]             PointQuadStart = Array.Empty<int>();         // into Quads
        public int[]             PointQuadCount = Array.Empty<int>();
        public int               PointCount;

        // ── curved details ──
        public CurvedStageInput[] Curveds            = Array.Empty<CurvedStageInput>();
        public int[]              CurvedGlyphStart    = Array.Empty<int>();   // into Glyphs
        public int[]              CurvedGlyphCount    = Array.Empty<int>();
        public int[]              CurvedAnchorStart   = Array.Empty<int>();   // into Anchors
        public int[]              CurvedAnchorCount   = Array.Empty<int>();   // per-anchor count (= min(anchors, cap))
        public int[]              CurvedAnchorFadeStart = Array.Empty<int>(); // into AnchorFadeIds ([count) anchors + 1 fallback)
        public int               CurvedCount;

        // ── flat pools ──
        public SymbolQuad[]  Quads        = Array.Empty<SymbolQuad>();
        public int           QuadCount;
        public CurvedGlyph[] Glyphs       = Array.Empty<CurvedGlyph>();
        public int           GlyphCount;
        public LineAnchor[]  Anchors      = Array.Empty<LineAnchor>();
        public int           AnchorCount;
        public double3[]     WorldPoints  = Array.Empty<double3>();          // anchor (point) / path verts (curved) — for projection
        public int           WorldPointCount;
        // The unit surface normal at each WorldPoints entry, index-parallel (same WorldStart/WorldCount).
        // float3 is enough: a unit vector at float precision has ~1e-7 rad of angular error.
        public float3[]      WorldUps     = Array.Empty<float3>();
        public int           WorldUpCount;
        public long[]        AnchorFadeIds = Array.Empty<long>();            // per curved anchor + a trailing fallback slot
        public int           AnchorFadeCount;

        // ── staging output upper bounds (from this batch's shape — independent of the camera) ──
        // Pre-size once per rebuild. Point: 1 box, Q quads, 1 candidate; curved: (anchors + 1) × glyphCount.
        public int MaxBoxes;
        public int MaxQuads;
        public int MaxCandidates;

        /// <summary>Monotonic id bumped by <see cref="Reset"/> — a consumer mirrors the batch into native buffers
        /// only when this changes (i.e. the batch was rebuilt), never per frame.</summary>
        public long BuildId { get; private set; }

        /// <summary>Rewind all counts to reuse the buffers for a fresh rebuild (arrays keep their capacity).</summary>
        public void Reset()
        {
            Count = 0; PointCount = 0; CurvedCount = 0;
            QuadCount = 0; GlyphCount = 0; AnchorCount = 0; WorldPointCount = 0; WorldUpCount = 0; AnchorFadeCount = 0;
            MaxBoxes = 0; MaxQuads = 0; MaxCandidates = 0;
            BuildId++;
        }

        // ── append helpers (geometric growth, never shrink) — the builder appends; the counts are the live length ──
        public int AddSymbol(SymbolPlacementKind kind, int detail, int worldStart, int worldCount, in double3 repAnchor,
            bool departing, bool coverageFading)
        {
            Grow(ref Kinds, Count); Grow(ref Detail, Count); Grow(ref WorldStart, Count); Grow(ref WorldCount, Count);
            Grow(ref RepAnchor, Count); Grow(ref SymbolDeparting, Count); Grow(ref SymbolCoverageFading, Count);
            Kinds[Count] = kind; Detail[Count] = detail; WorldStart[Count] = worldStart; WorldCount[Count] = worldCount;
            RepAnchor[Count] = repAnchor; SymbolDeparting[Count] = departing; SymbolCoverageFading[Count] = coverageFading;
            return Count++;
        }

        public int AddPoint(in PointStageInput input, int quadStart, int quadCount)
        {
            Grow(ref Points, PointCount); Grow(ref PointQuadStart, PointCount); Grow(ref PointQuadCount, PointCount);
            Points[PointCount] = input; PointQuadStart[PointCount] = quadStart; PointQuadCount[PointCount] = quadCount;
            MaxBoxes += 1; MaxQuads += quadCount; MaxCandidates += 1; // one AABB box + its quads, one candidate
            return PointCount++;
        }

        public int AddCurved(in CurvedStageInput input, int glyphStart, int glyphCount,
            int anchorStart, int anchorCount, int anchorFadeStart)
        {
            Grow(ref Curveds, CurvedCount);
            Grow(ref CurvedGlyphStart, CurvedCount); Grow(ref CurvedGlyphCount, CurvedCount);
            Grow(ref CurvedAnchorStart, CurvedCount); Grow(ref CurvedAnchorCount, CurvedCount);
            Grow(ref CurvedAnchorFadeStart, CurvedCount);
            Curveds[CurvedCount] = input;
            CurvedGlyphStart[CurvedCount] = glyphStart; CurvedGlyphCount[CurvedCount] = glyphCount;
            CurvedAnchorStart[CurvedCount] = anchorStart; CurvedAnchorCount[CurvedCount] = anchorCount;
            CurvedAnchorFadeStart[CurvedCount] = anchorFadeStart;
            // Worst case: every anchor plus the centred fallback stages, each with glyphCount boxes/quads.
            int placements = anchorCount + 1;
            MaxBoxes += placements * glyphCount; MaxQuads += placements * glyphCount; MaxCandidates += placements;
            return CurvedCount++;
        }

        public int AddQuad(in SymbolQuad q)      { Grow(ref Quads, QuadCount);   Quads[QuadCount] = q;   return QuadCount++; }
        public int AddGlyph(in CurvedGlyph g)    { Grow(ref Glyphs, GlyphCount); Glyphs[GlyphCount] = g; return GlyphCount++; }
        public int AddAnchor(in LineAnchor a)    { Grow(ref Anchors, AnchorCount); Anchors[AnchorCount] = a; return AnchorCount++; }
        public int AddWorldPoint(in double3 p)   { Grow(ref WorldPoints, WorldPointCount); WorldPoints[WorldPointCount] = p; return WorldPointCount++; }
        public int AddWorldUp(in float3 up)      { Grow(ref WorldUps, WorldUpCount); WorldUps[WorldUpCount] = up; return WorldUpCount++; }
        public int AddAnchorFadeId(long id)      { Grow(ref AnchorFadeIds, AnchorFadeCount); AnchorFadeIds[AnchorFadeCount] = id; return AnchorFadeCount++; }

        private static void Grow<T>(ref T[] arr, int index)
        {
            if (index < arr.Length) return;
            int cap = arr.Length == 0 ? 16 : arr.Length * 2;
            Array.Resize(ref arr, cap);
        }
    }
}
