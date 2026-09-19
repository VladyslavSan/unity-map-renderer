using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// Burst job: ear-clipping polygon triangulator over NativeArrays. Faithful port of
    /// <c>Earcut.Triangulate</c> from MapRenderer.Core — same algorithm, same tie-breaking,
    /// same bridge/hole logic, same cure → split → clean-drop failure cascade (mesh-triangulation-
    /// robustness Stage 3 — mirrors the Stage 2 managed fix). Produces bit-identical output to the
    /// managed reference (integer tile-space coords are exact; pinned by
    /// <c>JobifiedPipelineTests.JobifiedPipeline_VertexAndIndexContentHash_MatchManagedPath</c>).
    ///
    /// Input: vertices for ONE polygon (outer + bridged holes packed contiguously), ring metadata
    /// from <see cref="RingAssemblyJob"/> output.
    ///
    /// Output: flat index array written to <see cref="OutIndices"/> starting at
    /// <see cref="OutIndexOffset"/> (pre-allocated by the pipeline coordinator).
    ///
    /// Does NOT reference MapRenderer.Core — keeps System.Math off the Burst path.
    ///
    /// Parity tie-break: hole sort (leftmost-x, then min-y, then ring index) must match the
    /// managed <c>validHoles.Sort</c> tie-break. The managed sort uses only leftmost-x (and
    /// List.Sort is unstable — the managed code re-sorts using the same comparator used here,
    /// extended with y + index for total order, to guarantee determinism on both sides).
    ///
    /// <b>No real recursion.</b> Burst does not reliably support recursion, so the managed
    /// <c>EarClipRing → SplitAndRetry → EarClipRing</c> recursion becomes an EXPLICIT stack of
    /// pending (start, remaining) ring-jobs (the <see cref="GlobeFillSubdivideJob{TProj}"/> pattern):
    /// <see cref="TrySplit"/>, instead of calling the ear-clip loop recursively on the two split
    /// halves, pushes both onto the stack. To reproduce the managed call order
    /// (<c>EarClipRing(a, remA); EarClipRing(c, remC);</c> — <c>a</c> fully processed, including any
    /// of its OWN nested splits, before <c>c</c> starts) via a LIFO stack, the halves are pushed
    /// <c>c</c> then <c>a</c>, so <c>a</c> pops next.
    ///
    /// <b>Working-buffer capacity (mesh-triangulation-robustness Stage 3).</b> <see cref="Verts"/>/
    /// <see cref="Prev"/>/<see cref="Next"/>/<see cref="Removed"/>/<see cref="IsBridgeCopy"/>/<see cref="IsEar"/>
    /// are fixed-size <c>NativeArray</c>s pre-sized by <c>FillMeshPipeline</c> to the deterministic
    /// merged-ring size PLUS a bounded split headroom (<see cref="MaxSplits"/> mirrors managed's cap
    /// exactly, but the Burst pre-sizing is a smaller, tile-appropriate bound — see
    /// <c>FillMeshPipeline</c>'s workCap sizing doc). <see cref="TrySplit"/> checks remaining
    /// capacity before writing a split's two new vertices; if the headroom is exhausted it refuses the
    /// split (never writes past the array) and the caller falls through to the clean-drop path — same
    /// as managed running out of <see cref="MaxSplits"/>, just a tighter bound in the rare case it binds.
    /// <see cref="OutMergedVertexCount"/> reports the ACTUAL final vertex count used (base merged count
    /// plus any split-added vertices) — this can be less than the pre-sized capacity when headroom goes
    /// unused (the common case), so the coordinator must read it (not assume capacity) when aggregating.
    ///
    /// IMPORTANT: this job operates on a single polygon's data (outer + holes). The pipeline
    /// coordinator schedules one job per polygon. For simple tiles the number of polygons is
    /// small (~tens to ~hundreds); Burst parallelism is across tiles.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct EarcutJob : IJob
    {
        /// <summary>Finite ceiling on split attempts per polygon — mirrors managed Earcut.MaxSplits
        /// EXACTLY (both the constant value and the decision-to-stop it gates) so the two paths agree
        /// on the split cap wherever the Burst pre-sized headroom (a Burst-only, tighter bound) doesn't
        /// bind first. A failure-path-only bound (lessons: never an unbounded data-derived loop).</summary>
        public const int MaxSplits = 512;

        // ── Per-polygon input ─────────────────────────────────────────────────────────────────
        /// <summary>Flat vertex array for this polygon's outer ring + holes (Earcut internal order).</summary>
        [ReadOnly] public NativeArray<double2> PolyVertices;

        /// <summary>Number of vertices in the outer ring.</summary>
        [ReadOnly] public int OuterCount;

        /// <summary>
        /// Hole vertex counts, sorted by (leftmost-x, min-y, ring-index) — same order as the
        /// managed Earcut.validHoles after the total-order sort. Length = HoleCount.
        /// </summary>
        [ReadOnly] public NativeArray<int> SortedHoleCounts;

        [ReadOnly] public int HoleCount;

        // ── Output ───────────────────────────────────────────────────────────────────────────
        /// <summary>Triangle index output. Pre-allocated by the coordinator.</summary>
        [WriteOnly] public NativeArray<int> OutIndices;

        /// <summary>Offset into <see cref="OutIndices"/> at which to start writing.</summary>
        [ReadOnly] public int OutIndexOffset;

        /// <summary>[0] = number of indices written.</summary>
        public NativeArray<int> OutIndexCount;

        /// <summary>[0] = clean-drop count (was "force-clip" — the old force-clip-fold stall guard is
        /// replaced by the cure → split → clean-drop cascade; this now counts loci the cascade could
        /// not resolve and dropped cleanly, never a folded/overlapping triangle). Same rename-in-place
        /// as managed <c>Earcut.Result.ForceClips</c>.</summary>
        public NativeArray<int> OutForceClipCount;

        /// <summary>[0] = the ACTUAL final merged-ring vertex count this polygon used (base bridged
        /// count plus any split-added vertices) — may be less than the scratch arrays' capacity when
        /// the split headroom goes unused. The coordinator MUST read this (not assume capacity) to know
        /// how many of <see cref="Verts"/> are meaningful; the tail beyond this count is
        /// unwritten scratch, not part of the triangulation.</summary>
        public NativeArray<int> OutMergedVertexCount;

        // ── Working buffers (pre-allocated by the coordinator, size = capacity + split headroom) ────
        // Using NativeArrays for all internal buffers so the job has no GC allocations.
        public NativeArray<double2> Verts;
        public NativeArray<int>    Prev;
        public NativeArray<int>    Next;
        public NativeArray<bool>   IsBridgeCopy;
        public NativeArray<bool>   Removed;
        public NativeArray<bool>   IsEar;

        /// <summary>One pending (start, remaining) ear-clip ring job — the explicit-stack substitute
        /// for the managed recursive <c>EarClipRing</c> call.</summary>
        private struct RingJob
        {
            public int Start;
            public int Remaining;
        }

        public void Execute()
        {
            int outerCount = OuterCount;
            if (outerCount < 3)
            {
                OutIndexCount[0]        = 0;
                OutForceClipCount[0]    = 0;
                OutMergedVertexCount[0] = 0;
                return;
            }

            // ── Sort holes by (leftmost-x, min-y, original-index) for deterministic bridging.
            // ── This MUST match the managed Earcut.validHoles.Sort tie-break.
            // We sort the SortedHoleCounts array indices. Since caller already provides them sorted,
            // we just use them in order. The coordinator sorts before scheduling this job.

            // The deterministic merged-ring size on clean input is outer + Σ(hole + 2 bridge verts) — the
            // coordinator pre-sizes the scratch NativeArrays to THAT plus a split headroom, and `total`
            // only ever grows past the base via SplitPolygon, bounded by the array's real Length (the
            // Verts.Length check in TrySplit — the single source of the capacity bound here).
            int total = 0;

            // ── Insert outer ring, normalised to CCW-on-screen (area2 < 0 in Y-down). ────────
            double outerArea2 = ComputeArea2(PolyVertices, 0, outerCount);
            bool reverseOuter = outerArea2 > 0.0; // positive = CW on screen → reverse to CCW
            for (int i = 0; i < outerCount; i++)
            {
                int src = reverseOuter ? (outerCount - 1 - i) : i;
                Verts[total] = PolyVertices[src];
                Prev[total] = total - 1;
                Next[total] = total + 1;
                IsBridgeCopy[total] = false;
                Removed[total]      = false;
                total++;
            }
            Prev[0]              = outerCount - 1;
            Next[outerCount - 1] = 0;

            int mergedRingStart = 0;
            int mergedRingCount = outerCount;

            // ── Bridge each hole into the outer (merged) ring. ────────────────────────────────
            int holeVertexStart = outerCount; // where in PolyVertices the holes start
            for (int h = 0; h < HoleCount; h++)
            {
                int holeStart  = total;
                int holeCount  = SortedHoleCounts[h];

                double holeArea2 = ComputeArea2(PolyVertices, holeVertexStart, holeCount);
                bool reverseHole = holeArea2 < 0.0; // negative = CCW on screen → reverse to CW
                for (int i = 0; i < holeCount; i++)
                {
                    int src = reverseHole ? (holeCount - 1 - i) : i;
                    Verts[total] = PolyVertices[holeVertexStart + src];
                    Prev[total] = total - 1;
                    Next[total] = total + 1;
                    IsBridgeCopy[total] = false;
                    Removed[total]      = false;
                    total++;
                }
                Prev[holeStart] = total - 1;
                Next[total - 1] = holeStart;

                holeVertexStart += holeCount;

                int holeLM      = HoleLeftmostIndex(holeStart, holeCount);
                int outerBridge = FindBridgeVertex(holeLM, mergedRingStart, mergedRingCount);

                // Provably-non-crossing guarantee (mesh-triangulation-robustness Stage 2/3, mirrors
                // managed Earcut.Triangulate's hole loop 1:1). The heuristic above picks a good bridge on
                // the common case but is NOT guaranteed non-crossing for a concave outer. VALIDATE the
                // chosen bridge against BOTH the merged ring AND this hole's own ring; if it crosses
                // either (or isn't locally inside), REPLACE it with the nearest vertex whose bridge is
                // provably clear. Kept whenever the heuristic result is already valid ⇒ clean cases stay
                // byte-identical to the pre-fix output; only genuinely-crossing bridges change.
                if (!BridgeValid(holeLM, outerBridge, mergedRingStart, mergedRingCount, holeStart, holeCount))
                {
                    int bestCand = -1;
                    double bestDist = double.MaxValue;
                    int scan = mergedRingStart;
                    for (int i = 0; i < mergedRingCount; i++)
                    {
                        if (BridgeValid(holeLM, scan, mergedRingStart, mergedRingCount, holeStart, holeCount))
                        {
                            double bdx = Verts[scan].x - Verts[holeLM].x, bdy = Verts[scan].y - Verts[holeLM].y;
                            double bd  = bdx * bdx + bdy * bdy;
                            if (bd < bestDist) { bestDist = bd; bestCand = scan; }
                        }
                        scan = Next[scan];
                    }
                    if (bestCand >= 0) outerBridge = bestCand; // else: dirty input; cure/split/drop backstop
                }

                int copyHoleLM = total;
                int copyOuter  = total + 1;
                total += 2;

                Verts[copyHoleLM] = Verts[holeLM];
                Verts[copyOuter]  = Verts[outerBridge];
                IsBridgeCopy[copyHoleLM] = true;
                IsBridgeCopy[copyOuter]  = true;
                Removed[copyHoleLM]      = false;
                Removed[copyOuter]       = false;

                int outerNext  = Next[outerBridge];
                int holePrevLM = Prev[holeLM];

                Next[outerBridge] = holeLM;      Prev[holeLM]      = outerBridge;
                Next[holePrevLM]  = copyHoleLM;  Prev[copyHoleLM]  = holePrevLM;
                Next[copyHoleLM]  = copyOuter;   Prev[copyOuter]   = copyHoleLM;
                Next[copyOuter]   = outerNext;   Prev[outerNext]   = copyOuter;

                mergedRingCount = total;
            }

            // ── Ear-clipping loop, with a cure → split → clean-drop failure cascade on stall. ──
            // Mirrors managed Earcut.Triangulate's EarClipRing exactly, except the SplitAndRetry
            // recursion is replaced by an explicit worklist stack (see class doc).
            for (int i = 0; i < total; i++)
                IsEar[i] = ComputeIsEar(total, i);

            int forceClipCount = 0; // clean-drop count (rename-in-place, matches managed)
            int splitsUsed     = 0;
            int outCount       = 0;
            int outBase        = OutIndexOffset;

            var stack = new NativeList<RingJob>(8, Allocator.Temp);
            stack.Add(new RingJob { Start = mergedRingStart, Remaining = total });

            while (stack.Length > 0)
            {
                RingJob job = stack[stack.Length - 1];
                stack.RemoveAtSwapBack(stack.Length - 1); // LIFO pop — see class doc for the DFS-order proof

                ProcessRing(job.Start, job.Remaining, ref total, ref splitsUsed, ref outCount, outBase,
                            ref forceClipCount, stack);
            }
            stack.Dispose();

            OutIndexCount[0]        = outCount;
            OutForceClipCount[0]    = forceClipCount;
            OutMergedVertexCount[0] = total;
        }

        // ── Ear-clip one ring to completion (mirrors managed EarClipRing) ───────────────────────

        /// <summary>
        /// Ear-clip exactly one ring (walked via Next/Prev from <paramref name="start"/>) to completion:
        /// normal ear removal, the first-stall full ear-status refresh, then — on a second stall — the
        /// cure → split → drop cascade. A split hands the two halves off via <paramref name="stack"/>
        /// instead of recursing (Burst has no real recursion — see class doc); this ring's own
        /// <paramref name="remaining"/> becomes 0 in that case and no final triangle is emitted for it
        /// here (matches managed: the split halves each emit their own final triangle when processed).
        /// </summary>
        private void ProcessRing(
            int start, int remaining,
            ref int total, ref int splitsUsed, ref int outCount, int outBase,
            ref int forceClipCount, NativeList<RingJob> stack)
        {
            if (remaining < 3) return;

            // Refresh ear status for this ring's own live vertices on entry.
            {
                int p = start, guard = 0, bound = remaining + 8;
                do
                {
                    if (!Removed[p]) IsEar[p] = ComputeIsEar(total, p);
                    p = Next[p];
                    guard++;
                } while (p != start && guard < bound);
            }

            while (remaining > 3)
            {
                if (Removed[start])
                {
                    int s = start, guard = 0;
                    while (Removed[s] && guard < total + 8) { s = Next[s]; guard++; }
                    start = s;
                }

                int stallLimit      = remaining;
                int stallIter       = 0;
                bool clippedAny     = false;
                bool didFullRefresh = false;

                int v = start;
                for (int iter = 0; iter < remaining * 4 && remaining > 3; iter++)
                {
                    if (Removed[v]) { v = Next[v]; continue; }

                    if (IsEar[v])
                    {
                        int p = Prev[v], n = Next[v];
                        OutIndices[outBase + outCount]     = p;
                        OutIndices[outBase + outCount + 1] = v;
                        OutIndices[outBase + outCount + 2] = n;
                        outCount += 3;

                        Next[p] = n; Prev[n] = p;
                        Removed[v] = true;
                        remaining--;
                        clippedAny     = true;
                        stallIter      = 0;
                        didFullRefresh = false;

                        IsEar[p] = ComputeIsEar(total, p);
                        IsEar[n] = ComputeIsEar(total, n);

                        if (start == v) start = n;
                        v = n;
                    }
                    else
                    {
                        v = Next[v];
                        stallIter++;
                        if (stallIter > stallLimit)
                        {
                            if (!didFullRefresh)
                            {
                                // First stall: full refresh of this ring's remaining ear statuses.
                                int p2 = v, guard2 = 0, bound2 = remaining + 8;
                                do
                                {
                                    if (!Removed[p2]) IsEar[p2] = ComputeIsEar(total, p2);
                                    p2 = Next[p2];
                                    guard2++;
                                } while (p2 != v && guard2 < bound2);
                                stallIter = 0; didFullRefresh = true; stallLimit = remaining;
                            }
                            else
                            {
                                // Second stall: cascade. Never fold.
                                int cureStart = v;
                                if (CureLocalIntersections(ref cureStart, ref remaining, total, ref outCount, outBase))
                                {
                                    start = cureStart;
                                    v = cureStart;
                                    stallIter = 0;
                                    didFullRefresh = false;
                                    clippedAny = true;
                                    if (remaining <= 3) break;
                                    continue;
                                }

                                if (TrySplit(v, remaining, ref total, ref splitsUsed, stack))
                                {
                                    remaining = 0; // handed off to the two pushed ring-jobs
                                    clippedAny = true;
                                    break;
                                }

                                // Genuinely stuck: neither cure nor split could make progress (or the
                                // Burst split headroom is exhausted). Drop this locus cleanly — no
                                // triangle folds. ForceClips counts exactly these clean drops.
                                forceClipCount++;
                                remaining = 0;
                                clippedAny = true;
                                break;
                            }
                        }
                    }
                }

                if (!clippedAny) break;
            }

            // Final triangle for this ring: the 3 remaining live vertices, in ascending vertex-index
            // order (matches managed's tie-break), with the Area2>0 swap enforcing CCW-on-screen output.
            if (remaining == 3)
            {
                int t0 = -1, t1 = -1, t2 = -1;
                int p3 = start, guard3 = 0, bound3 = remaining + 8;
                do
                {
                    if (!Removed[p3])
                    {
                        if (t0 < 0) t0 = p3;
                        else if (t1 < 0) t1 = p3;
                        else if (t2 < 0) t2 = p3;
                    }
                    p3 = Next[p3];
                    guard3++;
                } while (p3 != start && guard3 < bound3);

                if (t0 >= 0 && t1 >= 0 && t2 >= 0)
                {
                    if (t0 > t1) { int tmp = t0; t0 = t1; t1 = tmp; }
                    if (t1 > t2) { int tmp = t1; t1 = t2; t2 = tmp; }
                    if (t0 > t1) { int tmp = t0; t0 = t1; t1 = tmp; }

                    if (Area2(Verts[t0], Verts[t1], Verts[t2]) > 0.0)
                    {
                        int tmp = t1; t1 = t2; t2 = tmp;
                    }

                    OutIndices[outBase + outCount]     = t0;
                    OutIndices[outBase + outCount + 1] = t1;
                    OutIndices[outBase + outCount + 2] = t2;
                    outCount += 3;
                }
            }
        }

        /// <summary>
        /// mapbox cureLocalIntersections: at vertex p, edges (a=prev[p]→p) and (next[p]→b=next[next[p]])
        /// may cross (the seam-touching pattern bridging creates). If so — and the shortcut a→b is
        /// locally valid on both ends — cut triangle (a, p, b), splice p and next[p] out of the ring,
        /// and continue. Bounded by the ring's own live-vertex count; never emits a crossing triangle.
        /// </summary>
        private bool CureLocalIntersections(
            ref int start, ref int remaining, int total, ref int outCount, int outBase)
        {
            bool curedAny = false;
            int p = start;
            int guard = 0;
            int maxIter = remaining + 8;
            do
            {
                if (Removed[p]) { p = Next[p]; guard++; continue; }
                int a  = Prev[p];
                int pn = Next[p];
                int b  = Next[pn];

                if (a != b && a != p && pn != p && !Removed[a] && !Removed[pn] && !Removed[b] &&
                    !(Verts[a].x == Verts[b].x && Verts[a].y == Verts[b].y) &&
                    Intersects(a, p, pn, b) &&
                    LocallyInside(a, b) &&
                    LocallyInside(b, a))
                {
                    OutIndices[outBase + outCount]     = a;
                    OutIndices[outBase + outCount + 1] = p;
                    OutIndices[outBase + outCount + 2] = b;
                    outCount += 3;

                    Removed[p]  = true;
                    Removed[pn] = true;
                    Next[a] = b;
                    Prev[b] = a;
                    remaining -= 2;
                    curedAny = true;

                    IsEar[a] = ComputeIsEar(total, a);
                    IsEar[b] = ComputeIsEar(total, b);

                    start = b;
                    p = b;
                    if (remaining <= 3) break;
                }
                p = Next[p];
                guard++;
            } while (p != start && guard < maxIter);

            return curedAny;
        }

        /// <summary>
        /// mapbox splitEarcut: scan candidate pairs (a, b) for the first valid diagonal, split on it, and
        /// hand the two halves off via <paramref name="stack"/> (managed recurses the ear loop on both
        /// halves here — Burst has no real recursion, see class doc). O(n²) candidate pairs on the STUCK
        /// ring only (a failure-path escape, never the success path). Refuses the split — WITHOUT writing
        /// anything — if the pre-sized scratch headroom (<see cref="Verts"/>'s Length) is exhausted; the
        /// caller then falls through to the clean-drop path (Stage 3's Burst-only tighter bound under
        /// managed's <see cref="MaxSplits"/> cap — see class doc).
        /// </summary>
        private bool TrySplit(
            int start, int remaining, ref int total, ref int splitsUsed, NativeList<RingJob> stack)
        {
            if (splitsUsed >= MaxSplits) return false;
            int ringGuardBound = remaining + 8;
            int scanBudget = math.min(remaining, 400); // bounded candidate-pair search width

            int a = start;
            for (int ai = 0; ai < scanBudget; ai++)
            {
                if (!Removed[a])
                {
                    int b = Next[Next[a]];
                    for (int bi = 0; bi < scanBudget && b != Prev[a]; bi++)
                    {
                        if (!Removed[b] && a != b && IsValidDiagonal(a, b, ringGuardBound))
                        {
                            // Burst-only capacity guard: SplitPolygon needs 2 fresh scratch slots. Managed
                            // grows its arrays unboundedly (up to MaxSplits); Burst's NativeArrays are
                            // fixed-size, so refuse — never overflow — if the pre-sized headroom is spent.
                            if (total + 2 > Verts.Length) return false;

                            splitsUsed++;
                            int c = SplitPolygon(a, b, ref total);

                            IsEar[a] = ComputeIsEar(total, a);
                            IsEar[b] = ComputeIsEar(total, b);
                            IsEar[c] = ComputeIsEar(total, c);
                            int a2 = Prev[c];
                            IsEar[a2] = ComputeIsEar(total, a2);

                            int remA = CountRing(a, remaining + 8);
                            int remC = CountRing(c, remaining + 8);

                            // DFS order parity with managed's EarClipRing(a,remA); EarClipRing(c,remC):
                            // push c then a, so a (and any of ITS OWN nested splits) pops and fully
                            // completes before c starts — see class doc.
                            stack.Add(new RingJob { Start = c, Remaining = remC });
                            stack.Add(new RingJob { Start = a, Remaining = remA });
                            return true;
                        }
                        b = Next[b];
                    }
                }
                a = Next[a];
            }
            return false;
        }

        /// <summary>
        /// mapbox splitPolygon: duplicate a and b into two fresh vertices (a2, b2) and rewire so the ring
        /// splits into two independent cycles — [a → b → … → a] and [a2 → … → b2 → a2]. Split-added
        /// vertices are real ring vertices (IsBridgeCopy = false), unlike the zero-width bridge-seam
        /// copies, so they participate fully in point-in-triangle tests. Caller MUST have already verified
        /// <c>total + 2 &lt;= Verts.Length</c> (see <see cref="TrySplit"/>) — this never bounds-checks itself.
        /// </summary>
        private int SplitPolygon(int a, int b, ref int total)
        {
            int a2 = total++;
            int b2 = total++;

            Verts[a2] = Verts[a];
            Verts[b2] = Verts[b];
            IsBridgeCopy[a2] = false;
            IsBridgeCopy[b2] = false;
            Removed[a2] = false;
            Removed[b2] = false;

            int an = Next[a];
            int bp = Prev[b];

            Next[a]  = b;  Prev[b]  = a;
            Next[a2] = an; Prev[an] = a2;
            Next[b2] = a2; Prev[a2] = b2;
            Next[bp] = b2; Prev[b2] = bp;

            return b2;
        }

        /// <summary>
        /// mapbox isValidDiagonal: a candidate split diagonal (a, b) must not be an existing edge, must
        /// not cross any other edge of the ring, must be locally inside at BOTH endpoints, and its
        /// midpoint must fall inside the ring.
        /// </summary>
        private bool IsValidDiagonal(int a, int b, int ringGuardBound)
        {
            if (a == b || Next[a] == b || Prev[a] == b || Removed[a] || Removed[b]) return false;
            if (IntersectsRing(a, b, ringGuardBound)) return false;
            if (!LocallyInside(a, b)) return false;
            if (!LocallyInside(b, a)) return false;
            if (!MiddleInside(a, b, ringGuardBound)) return false;
            return true;
        }

        private bool IntersectsRing(int a, int b, int ringGuardBound)
        {
            int p = a;
            int guard = 0;
            do
            {
                if (!Removed[p])
                {
                    int q = Next[p];
                    if (!Removed[q] && p != a && p != b && q != a && q != b && Intersects(p, q, a, b))
                        return true;
                }
                p = Next[p];
                guard++;
            } while (p != a && guard < ringGuardBound);
            return false;
        }

        private bool MiddleInside(int a, int b, int ringGuardBound)
        {
            double mx = (Verts[a].x + Verts[b].x) * 0.5;
            double my = (Verts[a].y + Verts[b].y) * 0.5;
            bool inside = false;
            int p = a;
            int guard = 0;
            do
            {
                if (!Removed[p])
                {
                    int q = Next[p];
                    double px = Verts[p].x, py = Verts[p].y, qx = Verts[q].x, qy = Verts[q].y;
                    if ((py > my) != (qy > my))
                    {
                        double ix = px + (my - py) / (qy - py) * (qx - px);
                        if (ix > mx) inside = !inside;
                    }
                }
                p = Next[p];
                guard++;
            } while (p != a && guard < ringGuardBound);
            return inside;
        }

        private int CountRing(int start, int guardBound)
        {
            int count = 0;
            int p = start;
            int guard = 0;
            do
            {
                if (!Removed[p]) count++;
                p = Next[p];
                guard++;
            } while (p != start && guard < guardBound);
            return count;
        }

        // ── Bridge selection helpers ─────────────────────────────────────────────────────────────

        private double ComputeArea2(NativeArray<double2> verts, int start, int len)
        {
            double area = 0.0;
            for (int i = 0; i < len; i++)
            {
                double2 a = verts[start + i];
                double2 b = verts[start + (i + 1) % len];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        private int HoleLeftmostIndex(int holeStart, int holeCount)
        {
            int    idx  = holeStart;
            double minX = Verts[holeStart].x;
            for (int i = 1; i < holeCount; i++)
            {
                int j = holeStart + i;
                if (Verts[j].x < minX || (Verts[j].x == minX && Verts[j].y < Verts[idx].y))
                { minX = Verts[j].x; idx = j; }
            }
            return idx;
        }

        private int FindBridgeVertex(int holeLM, int mergedRingStart, int mergedRingCount)
        {
            double hx = Verts[holeLM].x;
            double hy = Verts[holeLM].y;

            int    bestVert       = -1;
            double bestIntersectX = double.NegativeInfinity;

            int cur = mergedRingStart;
            for (int iter = 0; iter < mergedRingCount * 2; iter++)
            {
                int nc = Next[cur];
                double ax = Verts[cur].x, ay = Verts[cur].y;
                double bx = Verts[nc].x,  by = Verts[nc].y;

                bool straddles = (ay > hy) != (by > hy);
                if (straddles)
                {
                    double t  = (hy - ay) / (by - ay);
                    double ix = ax + t * (bx - ax);
                    if (ix <= hx && ix > bestIntersectX)
                    {
                        bestIntersectX = ix;
                        bestVert = (ax >= bx) ? cur : nc;
                    }
                }
                cur = Next[cur];
                if (cur == mergedRingStart && iter > 0) break;
            }

            // Reflex-vertex refinement.
            if (bestVert >= 0)
            {
                double mx    = bestIntersectX;
                double candX = Verts[bestVert].x;
                double candY = Verts[bestVert].y;
                double sHMP  = (mx - hx) * (candY - hy);

                if (math.abs(sHMP) < 1e-10)
                {
                    cur = mergedRingStart;
                    for (int iter = 0; iter < mergedRingCount * 2; iter++)
                    {
                        double qx = Verts[cur].x, qy = Verts[cur].y;
                        if (cur != holeLM && math.abs(qy - hy) < 1e-10 && qx > candX && qx < hx)
                        { bestVert = cur; candX = qx; }
                        cur = Next[cur];
                        if (cur == mergedRingStart && iter > 0) break;
                    }
                }
                else
                {
                    double bestTan = (hx - candX) > 1e-12
                        ? math.abs(candY - hy) / (hx - candX) : double.MaxValue;

                    cur = mergedRingStart;
                    for (int iter = 0; iter < mergedRingCount * 2; iter++)
                    {
                        double qx = Verts[cur].x, qy = Verts[cur].y;
                        if (qx > mx && qx < hx && cur != holeLM)
                        {
                            double sHMQ = (mx - hx) * (qy - hy);
                            double sMPQ = (candX - mx) * (qy - hy) - (candY - hy) * (qx - mx);
                            double sMPH = -(candY - hy) * (hx - mx);
                            double sPHQ = (hx - candX) * (qy - candY) - (hy - candY) * (qx - candX);
                            double sPHM = (hy - candY) * (hx - mx);

                            bool inside =
                                sHMP * sHMQ >= 0.0 &&
                                sMPH * sMPQ >= 0.0 &&
                                sPHM * sPHQ >= 0.0;

                            // Non-crossing guard: the candidate bridge cur→holeLM must be locally inside
                            // at cur — stops the bridge from crossing an already-merged seam at scale.
                            if (inside && LocallyInside(cur, holeLM))
                            {
                                double dx  = hx - qx;
                                double tan = dx > 1e-12 ? math.abs(qy - hy) / dx : double.MaxValue;
                                bool tanTie = math.abs(tan - bestTan) < 1e-14;
                                bool accept =
                                    tan < bestTan ? true :
                                    tanTie && qx > candX ? true :
                                    tanTie && qx == candX ? SectorContainsSector(bestVert, cur) :
                                    false;
                                if (accept)
                                { bestTan = tan; bestVert = cur; candX = qx; candY = qy; }
                            }
                        }
                        cur = Next[cur];
                        if (cur == mergedRingStart && iter > 0) break;
                    }
                }
            }

            // Fallback: nearest by distance.
            if (bestVert < 0)
            {
                double bestDist = double.MaxValue;
                cur = mergedRingStart;
                for (int iter = 0; iter < mergedRingCount; iter++)
                {
                    double dx = Verts[cur].x - hx, dy = Verts[cur].y - hy;
                    double d  = dx * dx + dy * dy;
                    if (d < bestDist) { bestDist = d; bestVert = cur; }
                    cur = Next[cur];
                    if (cur == mergedRingStart && iter > 0) break;
                }
            }

            return bestVert < 0 ? mergedRingStart : bestVert;
        }

        /// <summary>
        /// Validate-and-fallback bridge check (mesh-triangulation-robustness Stage 2/3): the candidate
        /// bridge holeLM→cand must be locally inside at cand AND must not properly cross any edge of
        /// EITHER the already-merged ring OR the current hole's own ring.
        /// </summary>
        private bool BridgeValid(
            int holeLM, int cand, int mergedRingStart, int mergedRingCount, int holeStart, int holeCount)
            => LocallyInside(cand, holeLM)
               && !BridgeCrossesRing(holeLM, cand, mergedRingStart, mergedRingCount)
               && !BridgeCrossesRing(holeLM, cand, holeStart, holeCount);

        private bool ComputeIsEar(int total, int v)
        {
            if (Removed[v]) return false;
            int p = Prev[v], n = Next[v];
            if (Removed[p] || Removed[n]) return false;

            double2 a = Verts[p], b = Verts[v], c = Verts[n];

            double triArea2 = Area2(a, b, c);
            if (triArea2 > 1e-10) return false; // reflex vertex

            for (int i = 0; i < total; i++)
            {
                if (Removed[i] || i == p || i == v || i == n) continue;
                if (IsBridgeCopy[i]) continue;
                double vxi = Verts[i].x, vyi = Verts[i].y;
                if ((vxi == a.x && vyi == a.y) ||
                    (vxi == b.x && vyi == b.y) ||
                    (vxi == c.x && vyi == c.y))
                    continue;
                if (PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, vxi, vyi))
                    return false;
            }
            return true;
        }

        // ── Geometry primitives ──────────────────────────────────────────────────────────────────

        /// <summary>Twice the signed area of triangle (p, q, r). See managed Earcut.Area2 doc for the
        /// sign-convention note (this is also mapbox earcut's `area(p, q, r)`).</summary>
        private static double Area2(double2 p, double2 q, double2 r)
            => (q.x - p.x) * (r.y - p.y) - (r.x - p.x) * (q.y - p.y);

        /// <summary>mapbox earcut's `locallyInside(a, b)` — see managed Earcut.LocallyInside doc for the
        /// derivation and the y-down sign-convention note.</summary>
        private bool LocallyInside(int a, int b)
        {
            int ap = Prev[a], an = Next[a];

            bool aConvex = Area2(Verts[ap], Verts[a], Verts[an]) <= 0.0;
            bool insidePrevEdge = Area2(Verts[ap], Verts[a], Verts[b]) <= 0.0;
            bool insideNextEdge = Area2(Verts[a], Verts[an], Verts[b]) <= 0.0;
            return aConvex ? (insidePrevEdge && insideNextEdge)
                           : (insidePrevEdge || insideNextEdge);
        }

        /// <summary>mapbox earcut's `sectorContainsSector(m, p)` — innermost tiebreak in
        /// <see cref="FindBridgeVertex"/> only.</summary>
        private bool SectorContainsSector(int m, int p)
        {
            int mp = Prev[m], mn = Next[m];
            int pp = Prev[p], pn = Next[p];
            return Area2(Verts[mp], Verts[m], Verts[pp]) < 0.0 &&
                   Area2(Verts[pn], Verts[m], Verts[mn]) < 0.0;
        }

        /// <summary>Mirror of <see cref="MapRenderer.Core.Geometry.Earcut.PointInTriangle"/> — see its doc
        /// for the degenerate-candidate branch this predicate needs and why.</summary>
        private bool PointInTriangle(
            double ax, double ay, double bx, double by, double cx, double cy,
            double px, double py)
        {
            double d1 = Cross(ax, ay, bx, by, px, py);
            double d2 = Cross(bx, by, cx, cy, px, py);
            double d3 = Cross(cx, cy, ax, ay, px, py);
            if (d1 == 0.0 && d2 == 0.0 && d3 == 0.0)
            {
                // Degenerate candidate: the corners are collinear, so the triangle's point set is the
                // segment hull of its corners and containment is the bounding-box test.
                double minx = ax < bx ? (ax < cx ? ax : cx) : (bx < cx ? bx : cx);
                double maxx = ax > bx ? (ax > cx ? ax : cx) : (bx > cx ? bx : cx);
                double miny = ay < by ? (ay < cy ? ay : cy) : (by < cy ? by : cy);
                double maxy = ay > by ? (ay > cy ? ay : cy) : (by > cy ? by : cy);
                return px >= minx && px <= maxx && py >= miny && py <= maxy;
            }
            bool hasNeg = (d1 < 0.0) || (d2 < 0.0) || (d3 < 0.0);
            bool hasPos = (d1 > 0.0) || (d2 > 0.0) || (d3 > 0.0);
            return !(hasNeg && hasPos);
        }

        private static double Cross(double ax, double ay, double bx, double by, double px, double py)
            => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

        /// <summary>Proper segment-intersection test (mapbox earcut's `intersects`) over ring vertex
        /// indices. Used by <see cref="CureLocalIntersections"/> and <see cref="IsValidDiagonal"/>.</summary>
        private bool Intersects(int p1, int q1, int p2, int q2)
        {
            double o1 = Area2(Verts[p1], Verts[q1], Verts[p2]);
            double o2 = Area2(Verts[p1], Verts[q1], Verts[q2]);
            double o3 = Area2(Verts[p2], Verts[q2], Verts[p1]);
            double o4 = Area2(Verts[p2], Verts[q2], Verts[q1]);

            bool s1 = o1 > 0.0, s1n = o1 < 0.0;
            bool s2 = o2 > 0.0, s2n = o2 < 0.0;
            bool s3 = o3 > 0.0, s3n = o3 < 0.0;
            bool s4 = o4 > 0.0, s4n = o4 < 0.0;

            if ((s1 != s2 || s1n != s2n) && (s3 != s4 || s3n != s4n)) return true; // general case

            if (o1 == 0.0 && OnSegment(p1, p2, q1)) return true; // p2 lies on p1q1
            if (o2 == 0.0 && OnSegment(p1, q2, q1)) return true; // q2 lies on p1q1
            if (o3 == 0.0 && OnSegment(p2, p1, q2)) return true; // p1 lies on p2q2
            if (o4 == 0.0 && OnSegment(p2, q1, q2)) return true; // q1 lies on p2q2

            return false;
        }

        /// <summary>Given p, q, r already collinear, is q within the bounding box of segment p-r?</summary>
        private bool OnSegment(int p, int q, int r)
        {
            double px = Verts[p].x, py = Verts[p].y, qx = Verts[q].x, qy = Verts[q].y, rx = Verts[r].x, ry = Verts[r].y;
            return qx <= math.max(px, rx) && qx >= math.min(px, rx) &&
                   qy <= math.max(py, ry) && qy >= math.min(py, ry);
        }

        /// <summary>STRICT proper segment crossing (interiors intersect at a single point) — endpoint
        /// touches and collinear overlaps return false. Distinct from <see cref="Intersects"/>; the
        /// bridge-clearance test wants only genuine crossings so a bridge that merely shares/touches a
        /// ring vertex is not rejected.</summary>
        private static bool ProperlyCross(
            double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy)
        {
            double d1 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            double d2 = (bx - ax) * (dy - ay) - (by - ay) * (dx - ax);
            double d3 = (dx - cx) * (ay - cy) - (dy - cy) * (ax - cx);
            double d4 = (dx - cx) * (by - cy) - (dy - cy) * (bx - cx);
            return ((d1 > 0.0 && d2 < 0.0) || (d1 < 0.0 && d2 > 0.0)) &&
                   ((d3 > 0.0 && d4 < 0.0) || (d3 < 0.0 && d4 > 0.0));
        }

        /// <summary>Does the candidate bridge segment (holeLM → cand) properly cross any edge of the ring
        /// that starts at <paramref name="ringStart"/> and spans <paramref name="ringCount"/> vertices
        /// via Next[]? Edges incident to holeLM or cand are skipped (shared endpoint, not a crossing).
        /// Called for BOTH the already-merged ring AND the current hole's own ring (see
        /// <see cref="BridgeValid"/>) so the accepted bridge is provably non-crossing against every
        /// existing edge.</summary>
        private bool BridgeCrossesRing(int holeLM, int cand, int ringStart, int ringCount)
        {
            double ax = Verts[holeLM].x, ay = Verts[holeLM].y, bx = Verts[cand].x, by = Verts[cand].y;
            int c = ringStart;
            for (int i = 0; i < ringCount; i++)
            {
                int nc = Next[c];
                if (c != cand && nc != cand && c != holeLM && nc != holeLM)
                {
                    if (ProperlyCross(ax, ay, bx, by, Verts[c].x, Verts[c].y, Verts[nc].x, Verts[nc].y))
                        return true;
                }
                c = nc;
            }
            return false;
        }
    }
}
