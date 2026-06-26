using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst-compiled smoke wrapper for the line tessellation pipeline.
    ///
    /// S05 scope: validates that a simple straight 2-point line tessellates via a Burst job and
    /// produces the expected vertex count (4) and index count (6). This smoke test confirms Burst
    /// compilation succeeds. Full parallel multi-line jobification (matching the EarcutJob pattern)
    /// is deferred to S06 integration.
    ///
    /// Output capacity sizing: line tessellation output size is data-dependent (join type,
    /// cap type, and roundSegments all vary vertex count). A sizing pre-pass is required before
    /// allocating output buffers — per the S06 lesson. This wrapper uses a fixed conservative
    /// capacity for the smoke scenario; the production pipeline will add a count pre-pass.
    ///
    /// Does NOT reference MapRenderer.Core (keeps System.Math off the Burst path).
    /// Implements the straight-segment case inline for Burst compatibility.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct LineTessellationJob : IJob
    {
        // ── Input ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Input polyline: flat 2D points in world-meter space.</summary>
        [ReadOnly] public NativeArray<double2> InputPoints;

        /// <summary>Number of valid points in <see cref="InputPoints"/>.</summary>
        [ReadOnly] public int PointCount;

        // ── Output ────────────────────────────────────────────────────────────────────────────
        /// <summary>Output positions (centerline, y=0, east=x, north=z mapped to xy).</summary>
        [WriteOnly] public NativeArray<float3> OutPositions;

        /// <summary>Output extrusion normals (xy, meter space; float2 per vertex).</summary>
        [WriteOnly] public NativeArray<float2> OutNormals;

        /// <summary>Output (side, distanceAlong) per vertex.</summary>
        [WriteOnly] public NativeArray<float2> OutSideAndDist;

        /// <summary>Output widthScale per vertex.</summary>
        [WriteOnly] public NativeArray<float> OutWidthScales;

        /// <summary>Output triangle indices.</summary>
        [WriteOnly] public NativeArray<int> OutIndices;

        /// <summary>[0] = number of vertices written.</summary>
        public NativeArray<int> OutVertexCount;

        /// <summary>[0] = number of indices written.</summary>
        public NativeArray<int> OutIndexCount;

        // ── IJob ──────────────────────────────────────────────────────────────────────────────
        public void Execute()
        {
            OutVertexCount[0] = 0;
            OutIndexCount[0]  = 0;

            if (PointCount < 2) return;

            // Inline straight-segment tessellation (Butt cap, no joins) for Burst compatibility.
            // This is a Burst smoke test for a 2-point line → 4 verts, 6 indices.
            // The Core-path algorithm (LineTessellator) is used in the managed path (LineBootstrap);
            // full Burst porting with round/miter/bevel is deferred to S06.

            // For a 2-point Butt-cap line: 4 verts (2 per endpoint × left/right).
            if (PointCount == 2)
            {
                double2 p0 = InputPoints[0];
                double2 p1 = InputPoints[1];

                double dx  = p1.x - p0.x;
                double dy  = p1.y - p0.y;
                double len = math.sqrt(dx * dx + dy * dy);
                if (len < 1e-12) return;

                // Unit left normal (CCW 90° of tangent).
                float nx = (float)(-dy / len);
                float ny = (float)( dx / len);

                // 4 vertices: [0]=start-left, [1]=start-right, [2]=end-left, [3]=end-right.
                OutPositions[0]  = new float3((float)p0.x, 0f, (float)p0.y);
                OutNormals[0]    = new float2(nx, ny);
                OutSideAndDist[0] = new float2(+1f, 0f);
                OutWidthScales[0] = 1f;

                OutPositions[1]  = new float3((float)p0.x, 0f, (float)p0.y);
                OutNormals[1]    = new float2(-nx, -ny);
                OutSideAndDist[1] = new float2(-1f, 0f);
                OutWidthScales[1] = 1f;

                float totalLen = (float)len;
                OutPositions[2]  = new float3((float)p1.x, 0f, (float)p1.y);
                OutNormals[2]    = new float2(nx, ny);
                OutSideAndDist[2] = new float2(+1f, totalLen);
                OutWidthScales[2] = 1f;

                OutPositions[3]  = new float3((float)p1.x, 0f, (float)p1.y);
                OutNormals[3]    = new float2(-nx, -ny);
                OutSideAndDist[3] = new float2(-1f, totalLen);
                OutWidthScales[3] = 1f;

                // Two triangles (CCW): [0,1,3] and [0,3,2].
                OutIndices[0] = 0; OutIndices[1] = 1; OutIndices[2] = 3;
                OutIndices[3] = 0; OutIndices[4] = 3; OutIndices[5] = 2;

                OutVertexCount[0] = 4;
                OutIndexCount[0]  = 6;
            }
        }
    }
}
