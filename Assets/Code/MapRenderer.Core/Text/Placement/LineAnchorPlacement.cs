// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double2 — this
// file lives in MapRenderer.Core.Text.Placement (see PolylineArcWalker's header for the namespace-collision
// trap that forbids inline `Unity.Mathematics.X` here).

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A-2: computes a <c>symbol-placement: line</c> / <c>line-center</c> symbol's along-line anchors ONCE at
    /// build time, in TILE space (projection-agnostic, zoom-invariant) — the MapLibre <c>getAnchors</c>
    /// analogue. Anchors are returned as stable <see cref="LineAnchor"/> topology (segment + t), so the number
    /// of anchors and their world positions are FIXED for the tile's lifetime: the per-frame placement pass
    /// projects the line and lays glyphs out around each anchor's projected position, and the anchors no longer
    /// slide or pop as the camera zooms (the old fixed screen-px-from-start walk did both).
    /// </summary>
    public static class LineAnchorPlacement
    {
        /// <summary>
        /// Defence-in-depth ceiling on the anchor count. A real line has a handful of anchors; this bound only
        /// stops a pathological input (a very long line with a tiny spacing) from producing an unbounded array —
        /// the build-time twin of the placement pass's per-frame <c>MaxAnchorsPerLine</c> guard (the
        /// always-bound-loops principle: never derive an iteration count from data without a finite ceiling).
        /// </summary>
        public const int MaxAnchors = 256;

        // per-thread free-lists of whole scratch arrays — the EvalArgBuffers Rent/Return shape
        // (MapRenderer.Core.Expressions.EvalArgBuffers), reused here because Compute runs on whatever worker
        // thread builds a tile (SymbolFeatureExtractor is worker-safe), so a single shared buffer would race,
        // and a per-call `new` was the prior cost: a fresh `cumulative` array plus a growing `List<LineAnchor>`
        // and its final `ToArray()`, once per curved along-line symbol. A Stack (not a single field) survives
        // reentrancy if Compute is ever called from within another Compute on the same thread. Every Rent is
        // paired with a Return in a `finally`.
        [ThreadStatic] private static Stack<double[]> _cumulativeFree;
        [ThreadStatic] private static Stack<LineAnchor[]> _anchorFree;

        private static double[] RentCumulative(int length)
        {
            Stack<double[]> free = _cumulativeFree ??= new Stack<double[]>();
            while (free.Count > 0)
            {
                double[] buffer = free.Pop();
                if (buffer.Length >= length) return buffer;
            }
            return new double[length];
        }

        private static void ReturnCumulative(double[] buffer) => (_cumulativeFree ??= new Stack<double[]>()).Push(buffer);

        // Always exactly MaxAnchors — the loop below never fills past it — so no size check is needed.
        private static LineAnchor[] RentAnchors()
        {
            Stack<LineAnchor[]> free = _anchorFree ??= new Stack<LineAnchor[]>();
            return free.Count > 0 ? free.Pop() : new LineAnchor[MaxAnchors];
        }

        private static void ReturnAnchors(LineAnchor[] buffer) => (_anchorFree ??= new Stack<LineAnchor[]>()).Push(buffer);

        /// <summary>
        /// Anchors along <paramref name="tilePath"/> (tile-local units), spaced by
        /// <paramref name="spacingTileUnits"/> (<c>symbol-spacing px · extent / TilePixelSize</c> at the tile's
        /// integer zoom). <see cref="SymbolPlacement.LineCenter"/> → a single anchor at the line's mid arc
        /// length. <see cref="SymbolPlacement.Line"/> → anchors at <c>spacing·(k+0.5)</c> from the start (capped
        /// at <see cref="MaxAnchors"/>); a line shorter than one spacing still gets a single centred anchor so a
        /// geometrically-valid line always yields ≥1 anchor (whether the LABEL actually fits at the current zoom
        /// is a per-frame decision the placement pass still makes). Returns empty for &lt;2 points or a
        /// zero-length line.
        /// </summary>
        public static LineAnchor[] Compute(IReadOnlyList<double2> tilePath, double spacingTileUnits,
            SymbolPlacement placement)
        {
            if (tilePath == null || tilePath.Count < 2) return Array.Empty<LineAnchor>();

            int n = tilePath.Count;
            // Cumulative tile-space arc length at each vertex (index 0 == 0). Rented — may be OVERSIZED, so
            // every read below is bound by `n`, never `cumulative.Length`.
            double[] cumulative = RentCumulative(n);
            try
            {
                cumulative[0] = 0.0;
                for (int i = 1; i < n; i++)
                    cumulative[i] = cumulative[i - 1] + math.length(tilePath[i] - tilePath[i - 1]);
                double total = cumulative[n - 1];
                if (!(total > 0.0)) return Array.Empty<LineAnchor>(); // degenerate (coincident points)

                if (placement == SymbolPlacement.LineCenter)
                    return new[] { AnchorAtArc(cumulative, n, total * 0.5) };

                double spacing = spacingTileUnits > 0.0 ? spacingTileUnits : 1.0;
                LineAnchor[] scratch = RentAnchors();
                try
                {
                    int count = 0;
                    for (int k = 0; k < MaxAnchors; k++)
                    {
                        double arc = spacing * (k + 0.5);
                        if (arc > total) break; // past the line end — stop (bounded by MaxAnchors regardless)
                        scratch[count++] = AnchorAtArc(cumulative, n, arc);
                    }
                    if (count == 0) scratch[count++] = AnchorAtArc(cumulative, n, total * 0.5); // short line → one centred
                    // The caller keeps this beyond Compute's return, so it must be an independent copy — the
                    // one allocation this method cannot avoid.
                    var result = new LineAnchor[count];
                    Array.Copy(scratch, result, count);
                    return result;
                }
                finally { ReturnAnchors(scratch); }
            }
            finally { ReturnCumulative(cumulative); }
        }

        // The (segment, t) topology of the point at tile-space arc distance `arc` (clamped to [0, total]).
        // `n` is the path's vertex count — passed explicitly, not read off `cumulative.Length`, since a rented
        // `cumulative` may be oversized.
        private static LineAnchor AnchorAtArc(double[] cumulative, int n, double arc)
        {
            double total = cumulative[n - 1];
            if (arc <= 0.0) return new LineAnchor(0, 0f);
            if (arc >= total) return new LineAnchor(n - 2, 1f);

            // First vertex whose cumulative length reaches `arc` ends the containing segment.
            int seg = 0;
            for (int i = 1; i < n; i++)
                if (cumulative[i] >= arc) { seg = i - 1; break; }

            double segStart = cumulative[seg];
            double segLen = cumulative[seg + 1] - segStart;
            float t = segLen > 0.0 ? (float)((arc - segStart) / segLen) : 0f;
            return new LineAnchor(seg, t);
        }
    }
}
