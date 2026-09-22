using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;

using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// Input descriptor for one line layer's graph build — the line twin of
    /// <see cref="FillMeshPipeline.LayerInput"/>, with the same BORROWED/owned split.
    /// </summary>
    public struct LayerInput
    {
        /// <summary>The shared tile geometry — <b>BORROWED</b>. <see cref="LineMeshGraph.Schedule"/>
        /// never disposes it, never writes into it, and does not retain it past <c>Handle.Complete()</c>.
        /// Also the sole authority for the tile address and extent.</summary>
        public TileGeometryBuffers Geometry;

        /// <summary>This layer's per-feature membership column, sized to <see cref="Geometry"/>'s
        /// <c>FeatureCount</c> and owned by the request — an unselected ordinal reads <c>false</c>. The
        /// colour and width columns are not here: those are write-step inputs, living in a
        /// <c>MapRenderer.Unity</c> type this assembly does not depend on.</summary>
        public NativeArray<bool> FeatureSelected;

        /// <summary>The tile's SW-corner render origin — every projected point is stored relative to this
        /// (RTC).</summary>
        public double3 OriginRender;

        /// <summary>The projection this layer bakes with. Never null by the time this reaches
        /// <see cref="LineMeshGraph.Schedule"/>: the caller resolves the null-means-Mercator default
        /// before building a request.</summary>
        public IProjection Projection;

        /// <summary>Corner geometry style for the ribbon.</summary>
        public JoinType Join;

        /// <summary>Endpoint geometry style for the ribbon.</summary>
        public CapType Cap;

        /// <summary>Maximum miter ratio — <see cref="RibbonJob.MiterLimit"/>'s own contract.</summary>
        public double MiterLimit;

        /// <summary>Minimum miter ratio a round join needs before it fans — <see cref="RibbonJob.RoundLimit"/>'s
        /// own contract.</summary>
        public double RoundLimit;

        /// <summary>Arc divisions per round join/cap half.</summary>
        public int RoundSegments;

        /// <summary>The always-bound-loops ceiling on this layer's total ribbon vertex count;
        /// <see cref="LineMeshGraph.DefaultMaxOutputVertices"/> is the production value. A FIELD, not a
        /// constant read inside a job, so a test can drive its own ceiling with a synthetic ring without the
        /// production constant changing what it observes.</summary>
        public int MaxOutputVertices;
    }
}
