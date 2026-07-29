using NUnit.Framework;
using UnityEngine;
using FillShaderProps = MapRenderer.Unity.Rendering.ShaderProperties.Fill;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// P5 acceptance — <c>fill-translate</c> as a real SCREEN-PIXEL offset, and <c>fill-translate-anchor</c>
    /// as a real branch.
    ///
    /// <para><b>What was wrong.</b> <c>Fill_VertexModify</c> applied <c>_FillTranslate.xy</c> as world units
    /// while its own comment claimed "px→world applied CPU-side" — and nothing applied it. So
    /// <c>fill-translate: [16, -8]</c> displaced geometry by 16 world METRES, at every zoom. The sign was
    /// also inverted against the spec ("negatives indicate left and up" ⇒ +y is DOWN, i.e. south on a
    /// north-up map), and <c>fill-translate-anchor</c> had no effect at all.</para>
    ///
    /// <para><b>The discriminating tooth is zoom-invariance</b>
    /// (<see cref="Translate_DisplacesTheSameScreenDistance_AtDifferentZooms"/>): a screen-pixel offset moves
    /// the geometry by the same number of PIXELS regardless of camera scale, whereas a world-unit offset
    /// moves it by half as many pixels when the view covers twice the world. No parse test can catch that,
    /// and the pre-P5 shader cannot pass it.</para>
    ///
    /// Camera + background match the sibling fill snapshot fixtures (top-down ortho 512², dark slate).
    ///
    /// <para><b>Raster orientation.</b> <see cref="SnapshotRenderer.RawPixels"/> is BOTTOM-left origin (row 0
    /// is the bottom scanline; row index grows UPWARD on screen) — Unity's native ReadPixels convention. The
    /// camera looks down −Y with up = +Z, so NORTH is the TOP of the image = HIGH row index, and SOUTH is
    /// LOW row index. Getting this backwards is what made the first version of the sign test look like a
    /// shader bug when the shader was correct.</para>
    /// </summary>
    [TestFixture]
    public class FillTranslateSnapshotTests
    {
        private const int   SnapW = 512;
        private const int   SnapH = 512;
        private const float CamY  = 200f;

        // FillSceneHelper fits the fixture into 100 world units; these both frame it with margin and differ
        // by exactly 2× in world-units-per-pixel — the ratio the zoom-invariance tooth turns on.
        private const float NearOrtho = 70f;
        private const float FarOrtho  = 140f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;

        private static (GameObject go, Camera camera) BuildCamera(float orthoSize, float yawDegrees = 0f)
        {
            var go     = new GameObject("FillTranslateSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            // Pitch 90° looks straight down; yaw rotates the view about the world up axis (map bearing).
            camera.transform.rotation = Quaternion.Euler(90f, yawDegrees, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = orthoSize;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        /// <summary>Centroid (column, row) of the non-background pixels. Column grows RIGHT; row grows UP
        /// (bottom-left origin — see the fixture summary). Returns false when nothing rendered.
        ///
        /// <para><paramref name="touchesBorder"/> is the load-bearing output. A centroid only tracks a rigid
        /// translation while the whole shape is INSIDE the frame — if the fill is clipped at an edge, moving
        /// it just trades pixels across that edge and the centroid barely shifts. That silently turned the
        /// first version of these tests into a no-op (a 40 px translate measured as 4.8 px), so every caller
        /// asserts on this rather than trusting the camera framing.</para></summary>
        private static bool TryCentroid(byte[] pixels, out Vector2 centroid, out bool touchesBorder)
        {
            double sumX = 0, sumY = 0;
            int count = 0;
            touchesBorder = false;
            for (int row = 0; row < SnapH; row++)
            for (int col = 0; col < SnapW; col++)
            {
                int b = (row * SnapW + col) * 4;
                int dr = pixels[b] - BgR8, dg = pixels[b + 1] - BgG8, db = pixels[b + 2] - BgB8;
                if (Mathf.Abs(dr) + Mathf.Abs(dg) + Mathf.Abs(db) <= SnapshotCoverage.Tolerance) continue;
                sumX += col; sumY += row; count++;
                if (row == 0 || col == 0 || row == SnapH - 1 || col == SnapW - 1) touchesBorder = true;
            }
            centroid = count > 0 ? new Vector2((float)(sumX / count), (float)(sumY / count)) : Vector2.zero;
            return count > 0;
        }

        /// <summary>Renders the fixture fill at <paramref name="orthoSize"/>/<paramref name="yaw"/> with the
        /// given translate, and returns the rendered centroid. Inconclusive when nothing rendered (no GPU).</summary>
        private static Vector2 CentroidWithTranslate(float orthoSize, Vector2 translatePx, float anchor,
                                                     float yaw, string pngName)
        {
            var (mapGo, mat)    = FillSceneHelper.BuildFillGo();
            var (camGo, camera) = BuildCamera(orthoSize, yaw);
            try
            {
                mat.SetVector(FillShaderProps.PropertyId.FillTranslate,
                    new Vector4(translatePx.x, translatePx.y, 0f, 0f));
                mat.SetFloat(FillShaderProps.PropertyId.FillTranslateAnchor, anchor);

                using var snap = new SnapshotRenderer(SnapW, SnapH);
                snap.Render(camera);
                snap.WritePng(pngName);

                if (!TryCentroid(snap.RawPixels, out Vector2 centroid, out bool touchesBorder))
                    Assert.Inconclusive("No geometry rendered — GPU/shader context unavailable in this run.");
                Assert.IsFalse(touchesBorder,
                    $"{pngName}: the fill must be fully INSIDE the frame for its centroid to track a rigid " +
                    "translation. Touching a border means it is clipped, and the measured displacement " +
                    "understates the real one — widen orthographicSize.");
                return centroid;
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        // ── THE tooth: a screen-pixel offset is invariant to camera scale ────────────────────────

        [Test]
        public void Translate_DisplacesTheSameScreenDistance_AtDifferentZooms()
        {
            const float translatePx = 40f;

            // FillSceneHelper fits the mesh into 100 world units, so orthographicSize must exceed ~50 for the
            // whole fill to stay in frame (see TryCentroid's border guard). 70 and 140 both contain it with
            // margin and differ by exactly 2× in world-units-per-pixel.
            Vector2 nearBase  = CentroidWithTranslate(NearOrtho, Vector2.zero,                0f, 0f, "fill-translate-near-base.png");
            Vector2 nearMoved = CentroidWithTranslate(NearOrtho, new Vector2(translatePx, 0), 0f, 0f, "fill-translate-near-moved.png");
            Vector2 farBase   = CentroidWithTranslate(FarOrtho,  Vector2.zero,                0f, 0f, "fill-translate-far-base.png");
            Vector2 farMoved  = CentroidWithTranslate(FarOrtho,  new Vector2(translatePx, 0), 0f, 0f, "fill-translate-far-moved.png");

            float nearShift = nearMoved.x - nearBase.x;
            float farShift  = farMoved.x  - farBase.x;
            Debug.Log($"[FillTranslateSnapshotTests] shift near={nearShift:F1}px far={farShift:F1}px");

            Assert.Greater(Mathf.Abs(nearShift), 5f,
                "precondition: a 40 px translate must visibly move the fill, or the comparison is vacuous.");

            // Screen-pixel semantics ⇒ equal pixel shift at both scales. World-unit semantics (the pre-P5
            // behaviour) would make farShift half of nearShift, since the far camera covers 2× the world.
            Assert.AreEqual(nearShift, farShift, 4f,
                $"a screen-pixel fill-translate must displace by the SAME pixel count at any camera scale " +
                $"(near {nearShift:F1}px vs far {farShift:F1}px). A ~2× ratio means the offset is still being " +
                "applied in world units.");

            // And it must actually be the requested magnitude, not merely scale-invariant.
            Assert.AreEqual(translatePx, Mathf.Abs(nearShift), 8f,
                $"a {translatePx} px translate must displace by ~{translatePx} px; got {Mathf.Abs(nearShift):F1}.");
        }

        // ── Sign: the spec says +y is DOWN ───────────────────────────────────────────────────────

        [Test]
        public void PositiveY_MovesGeometryDownTheScreen_PerSpecNegativesAreUp()
        {
            Vector2 baseline = CentroidWithTranslate(NearOrtho, Vector2.zero,        0f, 0f, "fill-translate-sign-base.png");
            Vector2 movedY   = CentroidWithTranslate(NearOrtho, new Vector2(0, 40f), 0f, 0f, "fill-translate-sign-down.png");

            float rowShift = movedY.y - baseline.y; // rows grow UPWARD (bottom-left origin)
            Debug.Log($"[FillTranslateSnapshotTests] +y row shift = {rowShift:F1} (negative = down-screen)");

            Assert.Less(rowShift, -5f,
                "MapLibre's fill-translate spec states \"negatives indicate left and up\", so a POSITIVE y " +
                "must move the geometry DOWN the screen (south on a north-up map). In this bottom-left-origin " +
                "raster that means the centroid row must DECREASE. A positive shift here means the " +
                "north/south sign is inverted.");
        }

        // ── Anchor: map rides the bearing, viewport does not ─────────────────────────────────────

        [Test]
        public void Anchor_MapRotatesWithBearing_ViewportDoesNot()
        {
            const float yaw = 90f;
            var offset = new Vector2(40f, 0f);

            // Under a 90° yaw the map's east axis is no longer screen-right, so a MAP-anchored offset must
            // land on a different screen axis than a VIEWPORT-anchored one.
            Vector2 mapBase  = CentroidWithTranslate(NearOrtho, Vector2.zero, 0f, yaw, "fill-translate-anchor-map-base.png");
            Vector2 mapMoved = CentroidWithTranslate(NearOrtho, offset,       0f, yaw, "fill-translate-anchor-map.png");
            Vector2 viewBase = CentroidWithTranslate(NearOrtho, Vector2.zero, 1f, yaw, "fill-translate-anchor-view-base.png");
            Vector2 viewMoved= CentroidWithTranslate(NearOrtho, offset,       1f, yaw, "fill-translate-anchor-view.png");

            Vector2 mapShift  = mapMoved  - mapBase;
            Vector2 viewShift = viewMoved - viewBase;
            Debug.Log($"[FillTranslateSnapshotTests] yaw={yaw}° map shift={mapShift} viewport shift={viewShift}");

            // Viewport is screen-locked: +x is screen-right whatever the bearing.
            Assert.Greater(viewShift.x, 5f,
                "a VIEWPORT-anchored +x offset must move the geometry screen-RIGHT regardless of bearing.");
            Assert.Less(Mathf.Abs(viewShift.y), Mathf.Abs(viewShift.x) * 0.5f,
                "a viewport-anchored +x offset must be predominantly horizontal on screen.");

            // Map is bearing-locked: at 90° yaw the map's east axis projects onto the screen's vertical.
            Assert.Greater(mapShift.magnitude, 5f, "a MAP-anchored offset must still move the geometry.");
            Assert.Less(Mathf.Abs(mapShift.x), Mathf.Abs(viewShift.x) * 0.5f,
                "under a 90° bearing a MAP-anchored +x offset must NOT stay screen-horizontal — if it moves " +
                "the same way the viewport-anchored one does, fill-translate-anchor is being ignored.");
        }
    }
}
