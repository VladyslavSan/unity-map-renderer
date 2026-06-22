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
    ///     <c>sceneOrigin</c> is the camera's look-at point, re-snapped every frame (camera-relative
    ///     rendering — the look-at always sits at the render origin).</item>
    /// </list>
    ///
    /// <para><b>Why two levels compose correctly.</b> The GPU adds the two floats:
    /// <c>rendered = (float)(merc_vertex − tileOrigin) + (float)(tileOrigin − sceneOrigin)</c>. The
    /// <c>tileOrigin</c> term cancels analytically, so the rendered position is
    /// <c>≈ merc_vertex − sceneOrigin</c>. Precision is therefore governed entirely by how far the
    /// farthest visible vertex's true Mercator position lies from <c>sceneOrigin</c>:
    /// <c>≤ |camera − sceneOrigin| + coverRadius</c>. With <c>sceneOrigin ≡ look-at</c> each frame,
    /// <c>|camera − sceneOrigin|</c> is ~0 (the camera orbits the render origin); <see cref="TileCover"/>
    /// bounds <c>coverRadius</c>; and the per-tile in-tile span is bounded by the zoom. The worst-case
    /// rendered magnitude stays within the float32 ULP budget (≈ 8.4 km ⇒ sub-mm) by a wide margin.</para>
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
