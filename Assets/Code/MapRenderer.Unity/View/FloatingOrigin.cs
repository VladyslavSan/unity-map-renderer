using System;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Two-level Relative-To-Center (RTC) / floating-origin math (pure, engine-free): keeps world-scale
    /// Web-Mercator coordinates (±20,037,508 m) inside float32's usable precision so a panned/zoomed/tilted
    /// scene does not jitter (ARCHITECTURE "floating origin", <c>docs/coordinates-and-projections.md</c>).
    ///
    /// <para>Level 1: mesh vertices are tile-origin-relative — <c>ProjectPointsJob</c> emits each vertex as
    /// <c>(merc_vertex − tileOrigin)</c> in double, cast to float32 at mesh-write, where <c>tileOrigin</c> =
    /// <see cref="TileLocalOriginMercator"/>. The in-tile offset spans at most one tile
    /// (≈ <c>4.0075e7 / 2^z</c> m), so its float32 ULP shrinks with zoom. Level 2: the tile GameObject's
    /// local position is scene-origin-relative — its transform carries <c>(tileOrigin − sceneOrigin)</c>
    /// cast to float32 (<see cref="TileLocalToScene"/>), where <c>sceneOrigin</c> is the camera's look-at,
    /// re-snapped every frame, so the look-at always sits at the render origin.</para>
    ///
    /// <para>Non-local invariant, why the two levels compose: the GPU adds the two floats,
    /// <c>rendered = (float)(merc_vertex − tileOrigin) + (float)(tileOrigin − sceneOrigin)</c>. The
    /// <c>tileOrigin</c> term cancels analytically, so <c>rendered ≈ merc_vertex − sceneOrigin</c>.
    /// Precision is governed by how far the farthest visible vertex lies from <c>sceneOrigin</c>: at most
    /// <c>|camera − sceneOrigin| + coverRadius</c>. With <c>sceneOrigin ≡ look-at</c> each frame,
    /// <c>|camera − sceneOrigin| ≈ 0</c>; <see cref="IVisibleTileSelector"/> bounds <c>coverRadius</c>; and
    /// the zoom bounds each vertex's distance from its tile origin to one tile span. The worst-case
    /// rendered magnitude stays well inside the float32 budget: below ≈ 8.4 km, a float32 ULP is
    /// sub-millimetre.</para>
    ///
    /// <para>All inputs/outputs are Web-Mercator meters except <see cref="TileLocalToScene"/>, which
    /// returns the small float32 render-space offset (east=+X, height=+Y, north=+Z).</para>
    /// </summary>
    public static class FloatingOrigin
    {
        /// <summary>
        /// The Mercator min-corner of a tile — the origin that <c>ProjectPointsJob</c> bakes mesh
        /// vertices relative to.
        /// </summary>
        public static double2 TileLocalOriginMercator(TileId tile)
        {
            var (min, _) = tile.MercatorBounds();
            return min;
        }

        /// <summary>
        /// The tile GameObject's local position in render space: <c>(tileOrigin − sceneOrigin)</c> cast to
        /// float32. Small and near the camera once the scene origin is rebased. Mapping:
        /// Mercator east → +X, north → +Z, height → +Y (fills are flat on XZ).
        /// </summary>
        public static float3 TileLocalToScene(double2 tileOriginMerc, double2 sceneOriginMerc)
        {
            double dx = tileOriginMerc.x - sceneOriginMerc.x;
            double dz = tileOriginMerc.y - sceneOriginMerc.y;
            return new float3((float)dx, 0f, (float)dz);
        }

        /// <summary>
        /// Projection-agnostic generalization of <see cref="TileLocalToScene"/>: the tile GameObject's
        /// local <b>position</b> in render space when the scene is rebased into the look-at's local ENU
        /// frame — the placement that makes ONE camera-orbit pose (<c>CameraPoseMath.ComputeRelativePose</c>,
        /// look-at at the render origin, up=+Y) work for both the plane and the globe.
        ///
        /// <para>Mesh vertices are baked tile-origin-relative (Level 1) about
        /// <paramref name="tileOriginRender"/> = the tile's SW corner projected through the chosen
        /// projection (Mercator: <c>(mercX, 0, mercZ)</c>; globe: the corner's ECEF). The Level-2 transform
        /// places the tile with <b>position</b> = <c>rebase · (tileOriginRender − sceneOriginRender)</c>
        /// (this method) and <b>rotation</b> = <paramref name="rebase"/> (set once per frame, same for
        /// every tile), where <paramref name="rebase"/> = <c>transpose(projection.TangentBasisAt(lookAt))</c>
        /// and <paramref name="sceneOriginRender"/> = <c>projection.Project(lookAt)</c>. The two compose so
        /// a mesh vertex renders at <c>rebase · (project(v) − sceneOriginRender)</c> — the tileOrigin term
        /// cancels exactly as in the flat two-level scheme, so meshes never rebake on camera motion.</para>
        ///
        /// <para>Mercator reduces to the flat case: its tangent basis is the identity, so <c>rebase = I</c>
        /// and this returns <c>(float3)(tileOriginRender − sceneOriginRender)</c>, identical to
        /// <see cref="TileLocalToScene"/>. Non-local invariant: <paramref name="rebase"/> is a proper
        /// rotation (det +1) for both projections — the globe's ECEF→render axis-swap reflection composes
        /// with the (East, Up, North) column order to restore right-handedness — so it converts to a unit
        /// quaternion cleanly (no handedness flip).</para>
        /// </summary>
        public static float3 TileToSceneRebased(double3 tileOriginRender, double3 sceneOriginRender, float3x3 rebase)
        {
            // Delta in render space, cast to float32 (small once the scene origin is the near look-at).
            float3 delta = new float3(
                (float)(tileOriginRender.x - sceneOriginRender.x),
                (float)(tileOriginRender.y - sceneOriginRender.y),
                (float)(tileOriginRender.z - sceneOriginRender.z));

            // Rotate the render-space delta into the look-at's local ENU frame (Mercator: rebase = I ⇒ delta).
            return math.mul(rebase, delta);
        }

        /// <summary>
        /// Reproduces the GPU's two-float composition for a single vertex and returns the rendered
        /// render-space position. Used to measure floating-origin precision honestly (the "no jitter"
        /// gate): the engine bakes <c>(float)(merc − tileOrigin)</c> into the mesh and adds the tile
        /// transform <c>(float)(tileOrigin − sceneOrigin)</c> at draw time. This method performs both float
        /// casts and the float add exactly as the pipeline does, so a test can compare it against the exact
        /// double truth <c>(merc − sceneOrigin)</c>.
        /// </summary>
        public static float3 RenderVertex(double2 mercVertex, double2 tileOriginMerc, double2 sceneOriginMerc)
        {
            // Level 1: mesh vertex baked tile-origin-relative (what ProjectPointsJob writes).
            float vLocalX = (float)(mercVertex.x - tileOriginMerc.x);
            float vLocalZ = (float)(mercVertex.y - tileOriginMerc.y);

            // Level 2: tile transform scene-origin-relative (what TileLocalToScene writes).
            float tPosX = (float)(tileOriginMerc.x - sceneOriginMerc.x);
            float tPosZ = (float)(tileOriginMerc.y - sceneOriginMerc.y);

            // GPU adds the two floats (object→world translation of the baked vertex).
            return new float3(vLocalX + tPosX, 0f, vLocalZ + tPosZ);
        }
    }
}
