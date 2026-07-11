using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// B-2: the parallel per-frame SYMBOL projection — projects a flat buffer of render-space world points
    /// (every visible symbol's screen geometry this frame: a point symbol's anchor, a line symbol's path
    /// vertices) to logical screen pixels + NDC depth, culling only behind-camera. It is deliberately GENERIC
    /// over what a symbol renders — text today, an icon later, both together — because a symbol is projected as
    /// its anchor/path world points regardless; the glyph/icon layout is a separate, serial staging step that
    /// reads this job's output. Uses the SAME <see cref="LabelScreenProjection.TryProjectPoint"/> the serial
    /// path uses (one copy of the projection math), so the job and inline results are bit-identical.
    ///
    /// <para><b>Pure projection, cull downstream.</b> The point-anchor viewport-margin cull
    /// (<see cref="LabelScreenProjection.IsWithinViewportMargin"/>) and the line near-plane blow-up guard are
    /// applied by the serial staging pass, NOT here — they are cheap screen-space tests, and keeping them out
    /// makes this job a single uniform matrix-mul over both symbol kinds.</para>
    ///
    /// <para>Note: EditMode tests run jobs via managed fallback (not Burst-compiled) — they validate the
    /// numerics + the index mapping but do NOT prove Burst compilation. Burst-compile correctness is confirmed
    /// by ./Tools/run-tests.sh (batch mode). Below a count threshold the caller fills the same output arrays
    /// with a serial loop instead — the Schedule+Complete overhead beats a parallel job only at high counts.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct SymbolProjectionJob : IJobParallelFor
    {
        // ── Input ─────────────────────────────────────────────────────────────────────────────
        /// <summary>The flat render-space world points to project (pre-RTC — the space <c>projection.Project</c> emits).</summary>
        [ReadOnly] public NativeArray<double3> Points;

        /// <summary>The per-frame floating-origin the camera orbits (<c>SceneFrame.SceneOriginRender</c>) — subtracted before the camera transform.</summary>
        [ReadOnly] public double3 SceneOriginRender;

        /// <summary>The combined view-projection matrix for the current frame (<c>projectionMatrix * worldToCameraMatrix</c>).</summary>
        [ReadOnly] public float4x4 ViewProj;

        /// <summary>The logical (DPR-normalized) viewport size in pixels.</summary>
        [ReadOnly] public double2 ViewportLogicalPx;

        // ── Output (one entry per input point, same index) ──────────────────────────────────────
        [WriteOnly] public NativeArray<float2> OutScreen; // logical screen pixel (y-up, bottom-left origin)
        [WriteOnly] public NativeArray<float>  OutDepth;  // NDC depth (clip.z / clip.w)
        [WriteOnly] public NativeArray<byte>   OutValid;  // 1 = projected in front of the camera; 0 = behind (cull)

        public void Execute(int index)
        {
            bool ok = LabelScreenProjection.TryProjectPoint(
                Points[index], SceneOriginRender, ViewProj, ViewportLogicalPx, out float2 screen, out float depth);
            OutScreen[index] = screen;
            OutDepth[index]  = depth;
            OutValid[index]  = (byte)(ok ? 1 : 0);
        }
    }
}
