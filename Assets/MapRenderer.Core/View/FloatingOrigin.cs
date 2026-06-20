using System;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Two-level Relative-To-Center (RTC) / floating-origin math (pure, engine-free). This is the
    /// machinery that keeps world-scale Web-Mercator coordinates (±20,037,508 m) inside float32's usable
    /// precision so a panned/zoomed/tilted scene does not jitter (ARCHITECTURE §"floating origin", docs
    /// coordinates §5).
    ///
    /// <para><b>The two levels — and where each is applied:</b></para>
    /// <list type="number">
    ///   <item><b>Mesh vertices are tile-origin-relative.</b> The projection job
    ///     (<c>ProjectTileVerticesJob</c>) bakes each vertex as <c>(merc_vertex − tileOrigin)</c> cast to
    ///     float32, where <c>tileOrigin</c> = <see cref="TileLocalOriginMercator"/>. The in-tile offset
    ///     spans at most one tile (≈ <c>4.0075e7 / 2^z</c> m), so its float32 ULP shrinks with zoom.</item>
    ///   <item><b>The tile GameObject local position is scene-origin-relative.</b> Its transform carries
    ///     <c>(tileOrigin − sceneOrigin)</c> cast to float32 (<see cref="TileLocalToScene"/>), where
    ///     <c>sceneOrigin</c> is snapped near the camera and rebased as the camera moves.</item>
    /// </list>
    ///
    /// <para><b>Why two levels compose correctly.</b> The GPU adds the two floats:
    /// <c>rendered = (float)(merc_vertex − tileOrigin) + (float)(tileOrigin − sceneOrigin)</c>. The
    /// <c>tileOrigin</c> term cancels analytically, so the rendered position is
    /// <c>≈ merc_vertex − sceneOrigin</c>. Precision is therefore governed entirely by how far the
    /// farthest visible vertex's true Mercator position lies from <c>sceneOrigin</c>:
    /// <c>≤ |camera − sceneOrigin| + coverRadius</c>. Rebasing keeps <c>|camera − sceneOrigin|</c> below
    /// <see cref="ShouldRebase"/>'s threshold; <see cref="TileCover"/> bounds <c>coverRadius</c>; and the
    /// per-tile in-tile span is bounded by the zoom. Pick the threshold + zoom so the worst-case
    /// rendered magnitude stays within the desired float32 ULP budget (≈ 8.4 km ⇒ sub-mm).</para>
    ///
    /// <para>All inputs/outputs are Web-Mercator meters except <see cref="TileLocalToScene"/>, which
    /// returns the small float32 render-space offset (east=+X, height=+Y, north=+Z per docs §7).</para>
    /// </summary>
    public static class FloatingOrigin
    {
        /// <summary>
        /// The Mercator min-corner of a tile — the origin that <c>ProjectTileVerticesJob</c> bakes mesh
        /// vertices relative to. (Matches <c>MapFillBootstrap</c>/<c>JobifiedPipelineTests</c> which pass
        /// <c>TileId.MercatorBounds().min</c> as the projection origin.)
        /// </summary>
        public static double2 TileLocalOriginMercator(TileId tile)
        {
            var (min, _) = tile.MercatorBounds();
            return min;
        }

        /// <summary>
        /// True when the camera has drifted farther than <paramref name="thresholdMeters"/> from the
        /// current scene origin — i.e. it is time to re-snap <c>sceneOrigin</c> to the camera. Uses the
        /// squared distance to avoid a sqrt.
        /// </summary>
        public static bool ShouldRebase(double2 sceneOriginMerc, double2 cameraMerc, double thresholdMeters)
        {
            double dx = cameraMerc.x - sceneOriginMerc.x;
            double dy = cameraMerc.y - sceneOriginMerc.y;
            return (dx * dx + dy * dy) > (thresholdMeters * thresholdMeters);
        }

        /// <summary>
        /// The shift (in Mercator meters) that every tile transform must be offset by when the scene
        /// origin moves from <paramref name="oldOrigin"/> to <paramref name="newOrigin"/>. Equal to
        /// <c>oldOrigin − newOrigin</c>: a tile previously at scene position <c>(tileOrigin − oldOrigin)</c>
        /// moves to <c>(tileOrigin − newOrigin) = previous + (oldOrigin − newOrigin)</c>, preserving its
        /// absolute world position.
        /// </summary>
        public static double2 RebaseDelta(double2 oldOrigin, double2 newOrigin)
            => new double2(oldOrigin.x - newOrigin.x, oldOrigin.y - newOrigin.y);

        /// <summary>
        /// The tile GameObject's local position in render space: <c>(tileOrigin − sceneOrigin)</c> cast to
        /// float32. Small and near the camera once the scene origin is rebased. Mapping per docs §7:
        /// Mercator east → +X, north → +Z, height → +Y (fills are flat on XZ).
        /// </summary>
        public static float3 TileLocalToScene(double2 tileOriginMerc, double2 sceneOriginMerc)
        {
            double dx = tileOriginMerc.x - sceneOriginMerc.x;
            double dz = tileOriginMerc.y - sceneOriginMerc.y;
            return new float3((float)dx, 0f, (float)dz);
        }

        /// <summary>
        /// Reproduces the GPU's two-float composition for a single vertex and returns the rendered
        /// render-space position. Used to <i>measure</i> floating-origin precision honestly (the headline
        /// "no jitter" gate): the engine bakes <c>(float)(merc − tileOrigin)</c> into the mesh and adds the
        /// tile transform <c>(float)(tileOrigin − sceneOrigin)</c> at draw time. This method performs both
        /// float casts and the float add exactly as the pipeline does, so a test can compare it against the
        /// exact double truth <c>(merc − sceneOrigin)</c> and assert the error is within budget.
        /// </summary>
        public static float3 RenderVertex(double2 mercVertex, double2 tileOriginMerc, double2 sceneOriginMerc)
        {
            // Level 1: mesh vertex baked tile-origin-relative (what ProjectTileVerticesJob writes).
            float vLocalX = (float)(mercVertex.x - tileOriginMerc.x);
            float vLocalZ = (float)(mercVertex.y - tileOriginMerc.y);

            // Level 2: tile transform scene-origin-relative (what TileLocalToScene writes).
            float tPosX = (float)(tileOriginMerc.x - sceneOriginMerc.x);
            float tPosZ = (float)(tileOriginMerc.y - sceneOriginMerc.y);

            // GPU adds the two floats (object→world translation of the baked vertex).
            return new float3(vLocalX + tPosX, 0f, vLocalZ + tPosZ);
        }

        /// <summary>
        /// The exact (double-precision) render-space truth for a vertex: <c>merc − sceneOrigin</c>. The
        /// difference between this and <see cref="RenderVertex"/> is the floating-origin precision error.
        /// </summary>
        public static double3 RenderVertexTruth(double2 mercVertex, double2 sceneOriginMerc)
            => new double3(mercVertex.x - sceneOriginMerc.x, 0.0, mercVertex.y - sceneOriginMerc.y);
    }
}
