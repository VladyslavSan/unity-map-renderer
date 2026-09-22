using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Tests.TestSupport
{
    /// <summary>
    /// Decodes an MVT feature's command/parameter integer stream into paths (rings for polygons,
    /// polylines for lines) in tile-local coordinates. Per the MVT spec: command = id &amp; 0x7,
    /// repeat-count = id &gt;&gt; 3; MoveTo=1, LineTo=2, ClosePath=7; parameters are zigzag-encoded
    /// deltas applied to a running cursor.
    ///
    /// <para><b>Zero production callers</b>, and retained deliberately. Fill,
    /// line and symbol obtain geometry from the shared <c>TileGeometryBuffers</c> that the Burst
    /// <c>MvtDecodeJob</c> fills. What keeps this type in Core is that it is the <b>measurement instrument</b>
    /// for that job: (a) the managed reference decoder <c>MvtDecodeJob</c> is checked against
    /// (<c>JobifiedPipelineTests</c>' decode parity — the only thing keeping the Burst decoder honest against
    /// this spec transcription), (b) the arm-A oracle of the line and symbol differential teeth
    /// (<c>StyledLineBufferParityTests</c>, <c>SymbolBufferParityTests</c>), and (c) <c>Tools/core-tests</c>'
    /// decode ground truth. It is also this repo's only in-source transcription of the MVT command encoding.
    /// Deleting it would delete the oracle rather than retire a decoder.</para>
    /// </summary>
    public static class MvtGeometry
    {
        private const uint MoveTo = 1, LineTo = 2, ClosePath = 7;

        public static List<List<double2>> Decode(uint[] g)
        {
            var paths = new List<List<double2>>();
            if (g == null || g.Length == 0) return paths;

            List<double2> current = null;
            long x = 0, y = 0;
            int i = 0;

            while (i < g.Length)
            {
                uint commandInteger = g[i++];
                uint command = commandInteger & 0x7;
                uint count = commandInteger >> 3;

                if (command == MoveTo)
                {
                    for (uint k = 0; k < count; k++)
                    {
                        x += ZigZag(g[i++]);
                        y += ZigZag(g[i++]);
                        current = new List<double2> { new double2(x, y) };
                        paths.Add(current);
                    }
                }
                else if (command == LineTo)
                {
                    for (uint k = 0; k < count; k++)
                    {
                        x += ZigZag(g[i++]);
                        y += ZigZag(g[i++]);
                        current?.Add(new double2(x, y));
                    }
                }
                else if (command == ClosePath)
                {
                    // Ring is implicitly closed (the first vertex is not repeated).
                }
            }
            return paths;
        }

        /// <summary>Protobuf zigzag decode: (n &gt;&gt; 1) ^ -(n &amp; 1).</summary>
        public static long ZigZag(uint u) => (long)(u >> 1) ^ -(long)(u & 1);
    }
}
