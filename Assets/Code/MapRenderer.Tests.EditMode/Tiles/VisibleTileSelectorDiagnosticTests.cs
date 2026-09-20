// Unity EditMode only — ACCEPTANCE test for the tilt-aware visible-tile selector's TWO shipped arms: it began
// as the diagnostic that pinned the tilt-selection bug, and now also pins the screen-space LOD cover
// MapViewConfig.cs:170 actually ships — checked by a different predicate (below).
//
// Reproduces the demo scenario from the bug screenshot (Berlin, zoom 13, swept over tilt × heading) and checks,
// per case:
//   (1) the production FrustumTileSelector's FLAT and LOD covers,
//   (2) the GROUND TRUTH — a top-down quadtree from tile 0/0/0, subdividing every tile whose ground quad
//       intersects the REAL Unity camera frustum, down to the target selection zoom, and
//   (3) the engine-free Core ViewFrustum driving the SAME traversal.
//
// Hard gates: (3) must equal (2) tile-for-tile (proves the engine-free frustum matches the real camera — the
// linchpin). The FLAT cover must have ZERO tiles MISSING vs (2) by EXACT set membership — its oracle at :128
// is single-zoom by construction, so exact membership is only a well-posed question for this arm; it is what
// pinned the original bug (the pre-fix corner-bbox selector missed ~40 of ~60 tiles at 60° tilt). The LOD
// cover instead needs every ground-truth tile covered by exactly one ANCESTOR-OR-SELF — a coarse tile
// legitimately stands in for its children under level-of-detail, so exact membership is the wrong question
// there. Grep the run for "[TILEDIAG]" to see both covers, truth, and their diffs per case.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;

namespace MapRenderer.Tests.Tiles
{
    public class VisibleTileSelectorDiagnosticTests
    {
        // Sweep tilt (heading 0) — the bug screenshot is ~60° from overhead (camera Y=3647,Z=-6317 ⇒
        // atan(6317/3647)=60°), where the top of the frustum grazes the horizon — then sweep heading at a
        // fixed steep tilt to show tilt+heading compounding. Watch MISSING grow.
        [TestCase(0.0,  0.0)]
        [TestCase(30.0, 0.0)]
        [TestCase(45.0, 0.0)]
        [TestCase(60.0, 0.0)]   // ← the screenshot
        [TestCase(70.0, 0.0)]
        [TestCase(60.0, 30.0)]  // tilt + heading
        [TestCase(60.0, 45.0)]
        [TestCase(60.0, 90.0)]
        public void Diagnose_TiltedView_FlatAndLodCovers_vs_FrustumTraversal(double tiltDeg, double headingDeg)
        {
            // ── Scenario (matches the bug screenshot) ────────────────────────────────────────────────
            double2 vp   = new double2(1600, 900); // logical framing viewport (DPR=1 for the test)
            double  fov  = 60.0;
            var     cam  = new CameraProperties(
                new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52, Altitude = 0 },
                zoom: 13, heading: headingDeg, tilt: tiltDeg, verticalFovDeg: fov);
            var     proj = new WebMercatorProjection();

            // Selection zoom the pipeline uses (offset 0 under the S93 512 convention), clamped like the selector.
            const int minZoom = 0, maxZoom = 14, onScreenTilePx = 512;
            int offset  = (int)math.round(math.log2(WebMercator.TilePixelSize / onScreenTilePx));
            int targetZ = math.clamp(cam.IntegerZoom + offset, minZoom, maxZoom);

            // ── (1) CURRENT selectors — flat cover and screen-space LOD cover, same ViewContext ────────
            var viewContext = new ViewContext { Camera = cam, ViewportPx = vp, Projection = proj };

            var selector = new FrustumTileSelector(minZoom: minZoom, maxZoom: maxZoom,
                                                          onScreenTilePx: onScreenTilePx);
            var current = new List<TileId>();
            selector.SelectVisibleTiles(viewContext, current);

            var lodSelector = new FrustumTileSelector(minZoom: minZoom, maxZoom: maxZoom,
                                                      onScreenTilePx: onScreenTilePx,
                                                      lod: new ScreenSpaceLodStrategy(),
                                                      farPolicy: new GeometryAwareFarPlane());
            var lodCover = new List<TileId>();
            lodSelector.SelectVisibleTiles(viewContext, lodCover);

            // ── Real Unity camera, posed EXACTLY as MapCamera.SyncToCamera (DPR=1) ──────────────────────
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, vp.y, fov);
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value,
                out double3 pos, out double3 fwd, out double3 up);

            var go   = new GameObject("TileDiagCam");
            var ucam = go.AddComponent<Camera>();
            var rt   = new RenderTexture((int)vp.x, (int)vp.y, 24);
            try
            {
                ucam.targetTexture     = rt;
                ucam.aspect            = (float)(vp.x / vp.y);
                ucam.fieldOfView       = (float)fov;
                ucam.nearClipPlane     = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
                ucam.farClipPlane      = (float)CameraPoseMath.FarClip(altitude);
                ucam.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
                ucam.transform.rotation = Quaternion.LookRotation(
                    new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                    new Vector3((float)up.x,  (float)up.y,  (float)up.z));
                ucam.enabled = false;

                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(ucam);

                // Render-space scene frame (look-at at the origin), matching MapView.BuildSceneFrame.
                var lookAt = new GeoCoordinate
                {
                    Latitude  = proj.ClampValidLatitude(cam.LookAt.Latitude),
                    Longitude = cam.LookAt.Longitude,
                };
                double3  origin = proj.Project(lookAt);
                float3x3 basis  = proj.TangentBasisAt(lookAt); // rebase = transpose(basis) ⇒ dot with columns

                // Shared render-space AABB of a tile's flat ground quad (thin vertical slab).
                void TileAabb(TileId t, out double3 min, out double3 max)
                {
                    min = new double3(double.MaxValue, double.MaxValue, double.MaxValue);
                    max = new double3(double.MinValue, double.MinValue, double.MinValue);
                    ReadOnlySpanCorners(out var corners);
                    for (int i = 0; i < corners.Length; i++)
                    {
                        double2 ll    = t.ToLonLat(corners[i].x, corners[i].y, 1.0);
                        double3 world = proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
                        double3 rel   = world - origin;
                        double rx = basis.c0.x * rel.x + basis.c0.y * rel.y + basis.c0.z * rel.z;
                        double ry = basis.c1.x * rel.x + basis.c1.y * rel.y + basis.c1.z * rel.z;
                        double rz = basis.c2.x * rel.x + basis.c2.y * rel.y + basis.c2.z * rel.z;
                        min = new double3(math.min(min.x, rx), math.min(min.y, ry), math.min(min.z, rz));
                        max = new double3(math.max(max.x, rx), math.max(max.y, ry), math.max(max.z, rz));
                    }
                    min = new double3(min.x, min.y - 2.0, min.z); // thin slab so the flat quad isn't degenerate
                    max = new double3(max.x, max.y + 2.0, max.z);
                }

                // The quadtree traversal, parameterised by the visibility oracle.
                List<TileId> Traverse(System.Func<TileId, bool> visible, out int testedCount)
                {
                    var outp = new List<TileId>();
                    var st = new Stack<TileId>();
                    st.Push(new TileId { Z = 0, X = 0, Y = 0 });
                    int n = 0;
                    while (st.Count > 0)
                    {
                        TileId t = st.Pop();
                        n++;
                        if (!visible(t)) continue;
                        if (t.Z >= targetZ) { outp.Add(t); continue; }
                        for (int dx = 0; dx < 2; dx++)
                            for (int dy = 0; dy < 2; dy++)
                                st.Push(new TileId { Z = t.Z + 1, X = t.X * 2 + dx, Y = t.Y * 2 + dy });
                    }
                    testedCount = n;
                    return outp;
                }

                // The tile's flat ground quad in render space (4 corners), and an EXACT test: the quad clipped
                // against the six REAL Unity frustum planes is non-empty. This is the true "is it visible" oracle
                // — unlike TestPlanesAABB, which tests the tile's AABB and so keeps false positives (a big box
                // diagonally past a corner). The AABB test is a conservative superset of this.
                Vector3[] TileQuad(TileId t)
                {
                    var q = new Vector3[4];
                    var uv = new (double x, double y)[] { (0, 0), (1, 0), (1, 1), (0, 1) };
                    for (int i = 0; i < 4; i++)
                    {
                        double2 ll = t.ToLonLat(uv[i].x, uv[i].y, 1.0);
                        double3 w  = proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
                        double3 rl = w - origin;
                        q[i] = new Vector3((float)(basis.c0.x*rl.x+basis.c0.y*rl.y+basis.c0.z*rl.z),
                                           (float)(basis.c1.x*rl.x+basis.c1.y*rl.y+basis.c1.z*rl.z),
                                           (float)(basis.c2.x*rl.x+basis.c2.y*rl.y+basis.c2.z*rl.z));
                    }
                    return q;
                }
                bool QuadMeetsFrustum(TileId t)
                {
                    var poly = new List<Vector3>(TileQuad(t));
                    foreach (Plane pl in planes) // Sutherland-Hodgman against each inward plane
                    {
                        var clip = new List<Vector3>();
                        for (int i = 0; i < poly.Count; i++)
                        {
                            Vector3 a = poly[i], b = poly[(i + 1) % poly.Count];
                            float da = pl.GetDistanceToPoint(a), db = pl.GetDistanceToPoint(b);
                            if (da >= 0) clip.Add(a);
                            if ((da >= 0) != (db >= 0)) clip.Add(Vector3.Lerp(a, b, da / (da - db)));
                        }
                        poly = clip;
                        if (poly.Count == 0) return false;
                    }
                    return true;
                }

                // ── (2) GROUND TRUTHS ──────────────────────────────────────────────────────────────────────
                // exactTruth = genuinely visible (quad clip). truth = Unity's 6-plane AABB test (a superset —
                // has false positives). The production ViewFrustum sits BETWEEN them: it covers every visible
                // tile (conservative) but tightens the AABB test with a reverse frustum-AABB pre-cull, so it
                // drops the worst false positives Unity keeps.
                var exactTruth = Traverse(QuadMeetsFrustum, out int tested);
                var truth = Traverse(t =>
                {
                    TileAabb(t, out double3 min, out double3 max);
                    var b = new Bounds();
                    b.SetMinMax(new Vector3((float)min.x, (float)min.y, (float)min.z),
                                new Vector3((float)max.x, (float)max.y, (float)max.z));
                    return GeometryUtility.TestPlanesAABB(planes, b);
                }, out _);

                // ── LINCHPIN — the ENGINE-FREE ViewFrustum brackets between exact-visible and Unity's 6-plane
                //    test: it covers every genuinely-visible tile (no false negatives) yet never exceeds the
                //    plane test (its reverse pre-cull only removes exact false positives). ─────────────────────
                var frustum = ViewFrustum.FromPose(pos, fwd, up, fov, (double)ucam.aspect,
                                                   ucam.nearClipPlane, ucam.farClipPlane);
                var truthEF = Traverse(t =>
                {
                    TileAabb(t, out double3 min, out double3 max);
                    return frustum.IntersectsAabb(min, max);
                }, out _);

                CollectionAssert.IsSubsetOf(exactTruth, truthEF,
                    $"engine-free ViewFrustum dropped a genuinely-visible tile (false negative) " +
                    $"(tilt={tiltDeg}° heading={headingDeg}°) — exactVisible={exactTruth.Count}, ViewFrustum={truthEF.Count}");
                CollectionAssert.IsSubsetOf(truthEF, truth,
                    $"engine-free ViewFrustum selected a tile beyond Unity's 6-plane frustum " +
                    $"(tilt={tiltDeg}° heading={headingDeg}°) — ViewFrustum={truthEF.Count}, Unity={truth.Count}");

                // ── Diff + logs ─────────────────────────────────────────────────────────────────────────
                var cs      = new HashSet<TileId>(current);
                var es      = new HashSet<TileId>(exactTruth);
                var missing = exactTruth.Where(t => !cs.Contains(t)).ToList(); // visible but NOT selected → gaps
                var extra   = current.Where(t => !es.Contains(t)).ToList();    // selected but NOT visible → wasted

                // ── LOD partition check — every ground-truth tile needs exactly one ANCESTOR-OR-SELF in the
                // LOD cover. z == g.Z (self) counts; the walk goes all the way to z == 0 (the world tile is an
                // ancestor of everything); a descendant branch is impossible (targetZ caps the cover and
                // g.Z == targetZ, so no cover tile is below g).
                var lodSet     = new HashSet<TileId>(lodCover);
                var holes      = new List<TileId>();
                var overlapped = new List<TileId>();
                foreach (TileId g in exactTruth)
                {
                    int n = 0;
                    for (int z = g.Z; z >= 0; z--)
                    {
                        int s = g.Z - z;
                        if (lodSet.Contains(new TileId { Z = z, X = g.X >> s, Y = g.Y >> s })) n++;
                    }
                    if (n == 0) holes.Add(g);
                    else if (n >= 2) overlapped.Add(g);
                }

                Debug.Log($"[TILEDIAG] scenario: Berlin z={cam.Zoom} tilt={cam.Tilt.Degrees}° heading={cam.Heading.Degrees}° " +
                          $"fov={fov} vp={vp.x}×{vp.y} → targetZ={targetZ}, altitude={altitude:F0}m, quadtree tested={tested} tiles");
                Debug.Log($"[TILEDIAG] CURRENT selector : {current.Count,4} tiles  {Fmt(current)}");
                Debug.Log($"[TILEDIAG] EXACT visible    : {exactTruth.Count,4} tiles (Unity 6-plane={truth.Count})  {Fmt(exactTruth)}");
                Debug.Log($"[TILEDIAG] MISSING (visible, NOT selected → white/gaps): {missing.Count,4}  {Fmt(missing)}");
                Debug.Log($"[TILEDIAG] EXTRA   (selected, NOT visible → wasted)    : {extra.Count,4}  {Fmt(extra)}");
                Debug.Log($"[TILEDIAG] extent — current X:[{Range(current, ti => ti.X)}] Y:[{Range(current, ti => ti.Y)}]  " +
                          $"exact X:[{Range(exactTruth, ti => ti.X)}] Y:[{Range(exactTruth, ti => ti.Y)}]");
                Debug.Log($"[TILEDIAG] LOD selector     : {lodCover.Count,4} tiles (z {Range(lodCover, ti => ti.Z)})  " +
                          $"holes={holes.Count} overlap={overlapped.Count}  {Fmt(lodCover)}");

                // ACCEPTANCE (the hard gate): the selector must request EVERY genuinely-visible tile — zero gaps
                // — at any tilt/heading. EXTRA (residual AABB false positives) is only logged, not asserted.
                Assert.AreEqual(0, missing.Count,
                    $"selector MUST cover every visible tile (tilt={tiltDeg}° heading={headingDeg}°); " +
                    $"{missing.Count} MISSING: {Fmt(missing)}");

                // LEVEL-OF-DETAIL ACCEPTANCE: exact set membership is the wrong question here — a coarse
                // ancestor legitimately stands in for its children. Every ground-truth tile must instead be
                // covered by exactly one ancestor-or-self. Holes and overlaps are different defects (a white
                // gap vs. a double-covered patch, different costs, different fixes) — assert them separately.
                Assert.AreEqual(0, holes.Count,
                    $"screen-space LOD left {holes.Count} visible tile(s) uncovered (white gaps) at " +
                    $"tilt={tiltDeg}° heading={headingDeg}°: {Fmt(holes)}");
                // Tripwire, not a live check: the traversal emits a tile or descends into its children,
                // never both (FrustumTileSelector's emit-then-continue), so the cover is prefix-free and
                // n >= 2 is unreachable today. It becomes reachable under the planned retain-until-replaced
                // change in TileLodStrategy, which is why the clause is live code rather than a comment.
                Assert.AreEqual(0, overlapped.Count,
                    $"screen-space LOD covers {overlapped.Count} visible tile(s) more than once (a cover " +
                    $"tile and its own ancestor are both emitted) at tilt={tiltDeg}° heading={headingDeg}°: " +
                    $"{Fmt(overlapped)}");

                // ANTI-VACUITY 1 — runs at EVERY pose. Holes and overlaps cannot see a cover that is
                // uniformly too COARSE: every truth tile still has exactly one ancestor, so both stay zero.
                // The near field must reach full detail, which is what the strategy promises.
                Assert.AreEqual(targetZ, lodCover.Max(t => t.Z),
                    $"screen-space LOD never reaches full detail at tilt={tiltDeg}° " +
                    $"heading={headingDeg}° — the near field must hit the target zoom z={targetZ}");

                // ANTI-VACUITY 2 — the LOD arm must not degenerate into a second flat arm (the wrong
                // strategy wired in). Bounded to tilt >= 45 because below that the shipped cover is
                // honestly single-zoom; asserting mixed-zoom there would red a correct tree.
                if (tiltDeg >= 45.0)
                    Assert.Less(lodCover.Min(t => t.Z), lodCover.Max(t => t.Z),
                        $"screen-space LOD cover is single-zoom at tilt={tiltDeg}° heading={headingDeg}° — " +
                        $"the LOD arm is not exercising level-of-detail (did the wrong strategy get wired in?)");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
            }
        }

        private static void ReadOnlySpanCorners(out (double x, double y)[] corners)
            => corners = new (double, double)[] { (0, 0), (1, 0), (0, 1), (1, 1) };

        // Compact "z/x/y z/x/y …" (sorted, capped) for the log.
        private static string Fmt(List<TileId> tiles)
        {
            const int cap = 120;
            var sb = new StringBuilder();
            var sorted = tiles.OrderBy(t => t.Z).ThenBy(t => t.X).ThenBy(t => t.Y).ToList();
            for (int i = 0; i < sorted.Count && i < cap; i++)
                sb.Append(sorted[i].Z).Append('/').Append(sorted[i].X).Append('/').Append(sorted[i].Y).Append(' ');
            if (sorted.Count > cap) sb.Append("… (+").Append(sorted.Count - cap).Append(" more)");
            return sb.ToString();
        }

        private static string Range(List<TileId> tiles, System.Func<TileId, int> sel)
        {
            if (tiles.Count == 0) return "empty";
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var t in tiles) { int v = sel(t); if (v < lo) lo = v; if (v > hi) hi = v; }
            return $"{lo}..{hi}";
        }
    }
}
