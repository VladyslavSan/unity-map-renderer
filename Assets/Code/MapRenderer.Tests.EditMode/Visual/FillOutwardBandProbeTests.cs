// Unity EditMode only — the OUTWARD-BAND mechanism probe. Answers, on rendered pixels, the questions the
// costing left open before any plan is written against them.
// NOT registered in Tools/core-tests/core-tests.csproj (engine-bound: it renders).
//
// THE MECHANISM. Leave the filled region where it is; grow a ~1 device-px band OUTWARD from the boundary
// in the vertex shader and ramp coverage 1 → 0 across it. Interior vertices carry `side = 0`, band outer
// vertices carry `side = 1`, and the fragment reuses the line path's outer-edge formula:
//
//     sideGrad = max(length(float2(ddx(side), ddy(side))), 1e-6)
//     coverage = saturate((1 - |side|) / sideGrad)
//
// P1 — THE QUESTION. Across an interior triangle every vertex carries the SAME `side`, so the interpolated
// varying is constant and its screen derivative is exactly 0. Interior coverage therefore rests entirely
// on the `1e-6` clamp: `saturate(1 / 1e-6)` must come back as 1.0, on the GPU, after whatever the shader
// compiler does to that expression. That is a fragment-stage fact about a real device, not something the
// HLSL can be read for, so this fixture renders it. Three arms, because a constant is only interesting
// where it stops being one: the interior alone, the interior across its junction with the band (where a
// 2x2 derivative quad can straddle two primitives carrying different gradients), and the same under a
// grazing tilt where the band's own derivative is largest.
//
// P3 — the depth question, isolated. Every depth-writing fill pass hardcodes `ZWrite On`, so a band
// fragment at coverage 0 writes depth (and casts a shadow) unless the pass clips it. A quad whose every
// vertex carries `side = 1` renders coverage 0 everywhere, which is exactly that fragment; putting opaque
// geometry behind it and asking whether it survives settles the question without the band existing yet.

#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    internal class FillOutwardBandProbeTests
    {
        private const int SnapPx = 512;

        /// <summary>Half-extent of the constant-<c>side</c> interior square, world units.</summary>
        private const float InteriorHalf = 8f;

        /// <summary>Orthographic half-height. With <see cref="SnapPx"/> = 512 this makes one device pixel
        /// exactly <c>2 * OrthoSize / 512</c> world units, which is how the band is sized in real px.</summary>
        private const float OrthoSize = 10f;

        /// <summary>One device pixel in world units under the orthographic arm.</summary>
        private const float WorldPerPx = 2f * OrthoSize / SnapPx;

        /// <summary>Non-black background: a black frame can then only mean "no GPU context", never
        /// "coverage 0 everywhere" — the two are indistinguishable against black, and this fixture's whole
        /// subject is whether coverage is 1 or 0.</summary>
        private static readonly Color BackgroundColor = new Color(0.10f, 0.11f, 0.15f, 1f);

        private const string NoGpuMessage =
            "Probe render and a blank-background control are both all-black: no GPU context in batch " +
            "EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode";

        // ── The probe scene ────────────────────────────────────────────────────────────────────────────

        /// <summary>A rendered probe frame plus the handles the fixture tears down.</summary>
        private sealed class ProbeFrame : System.IDisposable
        {
            /// <summary>RGBA32, bottom-left origin (<see cref="SnapshotRenderer.RawPixels"/>'s convention).</summary>
            public byte[] Pixels;

            /// <summary>True when this frame and a blank control are both black — no GPU context.</summary>
            public bool NoGpuContext;

            private readonly GameObject[] _objects;
            private readonly Material[] _materials;
            private readonly Mesh[] _meshes;

            /// <param name="objects">Scene objects to destroy.</param>
            /// <param name="materials">Materials to destroy.</param>
            /// <param name="meshes">Meshes to destroy.</param>
            public ProbeFrame(GameObject[] objects, Material[] materials, Mesh[] meshes)
            {
                _objects   = objects;
                _materials = materials;
                _meshes    = meshes;
            }

            /// <summary>Linear-space coverage at one pixel: the composite's position on the
            /// background→white axis, which for this probe's white ink IS the fragment's alpha.</summary>
            /// <param name="column">Pixel column.</param>
            /// <param name="row">Pixel row, bottom-left origin.</param>
            public double CoverageAt(int column, int row)
                => PixelCoverage.CoverageAt(Pixels, SnapPx, SnapPx, column, row,
                                            BackgroundLinear(), new float3(1f, 1f, 1f));

            /// <summary>The background colour in linear space — the composite floor every reading is
            /// measured against.</summary>
            public static float3 BackgroundLinear() => new float3(
                PixelCoverage.ToLinear((byte)math.round(BackgroundColor.r * 255f)),
                PixelCoverage.ToLinear((byte)math.round(BackgroundColor.g * 255f)),
                PixelCoverage.ToLinear((byte)math.round(BackgroundColor.b * 255f)));

            /// <summary>Mean linear RGB over the inclusive-exclusive box <c>[x0,x1)x[y0,y1)</c>.</summary>
            /// <param name="x0">Left column, inclusive.</param>
            /// <param name="y0">Bottom row, inclusive.</param>
            /// <param name="x1">Right column, exclusive.</param>
            /// <param name="y1">Top row, exclusive.</param>
            public float3 MeanLinear(int x0, int y0, int x1, int y1)
            {
                float3 sum = float3.zero;
                int n = 0;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { sum += PixelCoverage.SampleLinear(Pixels, SnapPx, SnapPx, x, y); n++; }
                return sum / math.max(n, 1);
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                foreach (GameObject go in _objects) if (go != null) Object.DestroyImmediate(go);
                foreach (Material m in _materials)  if (m  != null) Object.DestroyImmediate(m);
                foreach (Mesh mesh in _meshes)      if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// Builds the probe mesh: a constant-<c>side</c>-0 interior square ringed by a band whose outer
        /// vertices carry <c>side = 1</c>. The interior is TWO triangles all of whose vertices share one
        /// <c>side</c> — the zero-derivative case — and the ring is four quads across which <c>side</c>
        /// varies, so one mesh carries both régimes and the junction between them.
        /// </summary>
        /// <param name="bandWorldWidth">Band thickness in world units; at <see cref="WorldPerPx"/> it is
        /// one device pixel under the orthographic arm.</param>
        private static Mesh BuildBandedQuad(float bandWorldWidth)
        {
            float inner = InteriorHalf;
            float outer = InteriorHalf + bandWorldWidth;

            var vertices = new Vector3[8];
            var uvs = new Vector2[8];
            // 0..3 interior (side 0), 4..7 band outer (side 1), both wound counter-clockwise from -X-Y.
            var innerCorners = new[] { new Vector2(-inner, -inner), new Vector2(inner, -inner),
                                       new Vector2(inner,  inner), new Vector2(-inner, inner) };
            var outerCorners = new[] { new Vector2(-outer, -outer), new Vector2(outer, -outer),
                                       new Vector2(outer,  outer), new Vector2(-outer, outer) };
            for (int i = 0; i < 4; i++)
            {
                vertices[i]     = new Vector3(innerCorners[i].x, innerCorners[i].y, 0f);
                uvs[i]          = new Vector2(0f, 0f);
                vertices[i + 4] = new Vector3(outerCorners[i].x, outerCorners[i].y, 0f);
                uvs[i + 4]      = new Vector2(1f, 0f);
            }

            var triangles = new System.Collections.Generic.List<int> { 0, 1, 2, 0, 2, 3 };
            for (int i = 0; i < 4; i++)
            {
                int a = i, b = (i + 1) % 4;
                triangles.AddRange(new[] { a, a + 4, b + 4, a, b + 4, b });
            }

            var mesh = new Mesh { name = "FillBandCoverageProbeQuad" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A flat quad whose every vertex carries <paramref name="side"/> — constant, so it renders
        /// one uniform coverage. Used for the depth arm, where <c>side = 1</c> gives coverage 0 everywhere.</summary>
        /// <param name="half">Half-extent, world units.</param>
        /// <param name="side">The constant <c>side</c> every vertex carries.</param>
        /// <param name="z">Plane depth, world units.</param>
        private static Mesh BuildConstantSideQuad(float half, float side, float z)
        {
            var mesh = new Mesh { name = $"ConstantSideQuad_{side}" };
            mesh.SetVertices(new[]
            {
                new Vector3(-half, -half, z), new Vector3(half, -half, z),
                new Vector3( half,  half, z), new Vector3(-half, half, z),
            });
            mesh.SetUVs(0, new[] { new Vector2(side, 0f), new Vector2(side, 0f),
                                   new Vector2(side, 0f), new Vector2(side, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Renders <paramref name="meshes"/> (each with its own material) through a camera looking down
        /// <c>+Z</c> at the <c>z = 0</c> plane.
        /// </summary>
        /// <param name="meshes">Geometry to draw, in the order given.</param>
        /// <param name="materials">One material per mesh; disposed with the frame.</param>
        /// <param name="tiltDegrees">Rotation about X applied to the camera; 0 is face-on. Non-zero
        /// switches the camera to PERSPECTIVE, because a grazing view under orthographic foreshortens
        /// uniformly and would not exercise the varying per-pixel derivative the tilt arm is about.</param>
        private static ProbeFrame Render(Mesh[] meshes, Material[] materials, float tiltDegrees)
        {
            var objects = new GameObject[meshes.Length + 1];
            for (int i = 0; i < meshes.Length; i++)
            {
                var go = new GameObject($"BandProbe_{i}");
                go.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = materials[i];
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                objects[i] = go;
            }

            var camGo = new GameObject("BandProbe_Camera");
            objects[meshes.Length] = camGo;
            var camera = camGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor;
            camera.enabled = false;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 500f;

            if (tiltDegrees == 0f)
            {
                camera.orthographic = true;
                camera.orthographicSize = OrthoSize;
                camGo.transform.position = new Vector3(0f, 0f, -50f);
                camGo.transform.rotation = Quaternion.identity;
            }
            else
            {
                // Orbit the camera up out of the plane's normal by `tiltDegrees` and aim it back at the
                // origin, so the quad is seen edge-on-ish: the projected band narrows toward zero px and
                // |grad side| grows without bound, which is the régime the tilt arm exists to reach.
                camera.orthographic = false;
                camera.fieldOfView = 40f;
                float radians = math.radians(tiltDegrees);
                const float distance = 60f;
                camGo.transform.position =
                    new Vector3(0f, -distance * math.sin(radians), -distance * math.cos(radians));
                camGo.transform.LookAt(Vector3.zero, Vector3.up);
            }

            var frame = new ProbeFrame(objects, materials, meshes);
            using (var snapshot = new SnapshotRenderer(SnapPx, SnapPx))
            {
                snapshot.Render(camera);
                frame.Pixels = snapshot.RawPixels;
                frame.NoGpuContext = snapshot.IsAllBlack() && BlankControlIsBlack();
            }
            return frame;
        }

        /// <summary>Renders a camera with nothing in front of it; all-black there means no GPU context
        /// rather than a genuinely empty scene (mirrors <c>VisualScene.DetectNoGpuContext</c>).</summary>
        private static bool BlankControlIsBlack()
        {
            var go = new GameObject("BandProbe_BlankCamera");
            try
            {
                var camera = go.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = BackgroundColor;
                using var snapshot = new SnapshotRenderer(SnapPx, SnapPx);
                snapshot.Render(camera);
                return snapshot.IsAllBlack();
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>The probe material, white ink so the composited grey level IS the fragment's alpha.</summary>
        /// <param name="zWrite">Value for <c>_ProbeZWrite</c>; 1 reproduces the fill's depth-writing passes.</param>
        /// <param name="renderQueue">Explicit queue, so the depth arm can order two draws.</param>
        private static Material ProbeMaterial(float zWrite, int renderQueue)
        {
            Shader shader = Shader.Find("Hidden/MapRenderer/Tests/FillBandCoverageProbe");
            Assert.IsNotNull(shader, "the test-only probe shader must be importable by name.");
            var material = new Material(shader) { renderQueue = renderQueue };
            material.SetColor("_Color", Color.white);
            material.SetFloat("_ProbeZWrite", zWrite);
            return material;
        }

        // ── P1 guard: the copied formula must still be the shipped one ─────────────────────────────────

        /// <summary>
        /// The probe's fragment body is a copy of the shipped outer-edge expression, so it can only answer
        /// the real question while it stays a copy. Both lines are read from disk and compared; this fails
        /// the moment <c>Line_VertexExtrude.hlsl</c>'s formula changes, which is the direction that matters
        /// — a drifted probe would keep passing while measuring something the product no longer does.
        /// </summary>
        [Test]
        public void ProbeFormula_IsVerbatimTheShippedLineCoverageExpression()
        {
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string shipped = File.ReadAllText(Path.Combine(root,
                "Assets/Code/MapRenderer.Unity/Shaders/Map/Line/Line_VertexExtrude.hlsl"));
            string probe = File.ReadAllText(Path.Combine(root,
                "Assets/Code/MapRenderer.Tests.EditMode/Visual/FillBandCoverageProbe.shader"));

            const string gradient = "max(length(float2(ddx(side), ddy(side))), 1e-6)";
            const string coverage = "saturate((1.0 - absSide) / sideGrad)";

            StringAssert.Contains(gradient, shipped,
                "Line_VertexExtrude.hlsl no longer computes the Euclidean side gradient this way; the probe " +
                "is measuring a formula the product has stopped using.");
            StringAssert.Contains(coverage, shipped,
                "Line_VertexExtrude.hlsl no longer computes the outer-edge coverage this way.");
            StringAssert.Contains(gradient.Replace("side)", "input.side)"), probe,
                "the probe shader must carry the shipped gradient expression verbatim.");
            StringAssert.Contains(coverage, probe,
                "the probe shader must carry the shipped coverage expression verbatim.");
        }

        // ── P1: is a constant side = 0 interior stable? ─────────────────────────────────────────────────

        /// <summary>
        /// Face-on, band 20 device px wide so the interior and the ramp are unambiguously separable. The
        /// interior's <c>side</c> is constant, so its screen derivative is exactly 0 and coverage falls out
        /// of the <c>1e-6</c> clamp alone. The outer-edge assertion is not decoration: without it a shader
        /// that returned a constant 1 everywhere would pass the interior half, and the probe would be
        /// vacuous.
        /// </summary>
        [Test]
        public void ConstantSideInterior_ReadsFullCoverage_FaceOn()
        {
            Mesh mesh = BuildBandedQuad(20f * WorldPerPx);
            using ProbeFrame frame = Render(new[] { mesh }, new[] { ProbeMaterial(0f, 3000) }, tiltDegrees: 0f);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            // Interior square spans +/-8 world units = +/-204.8 px about the frame centre; sample well inside.
            double worst = 1.0;
            int worstX = -1, worstY = -1;
            for (int y = 156; y < 356; y++)
            for (int x = 156; x < 356; x++)
            {
                double c = frame.CoverageAt(x, y);
                if (c < worst) { worst = c; worstX = x; worstY = y; }
            }
            TestContext.WriteLine(
                $"P1 face-on: interior minimum coverage = {worst:F4} at ({worstX},{worstY}) over 200x200 px.");

            Assert.That(worst, Is.GreaterThan(0.99),
                $"a constant side = 0 interior read coverage {worst:F4} at ({worstX},{worstY}). The " +
                "derivative there is exactly 0, so this is the 1e-6 clamp failing to saturate — the " +
                "outward-band mechanism needs a different interior encoding.");

            // …and the ramp must actually exist, or the interior verdict above is vacuous. Deliberately
            // NOT "coverage is 0 beyond the geometry": that holds trivially when nothing is drawn there and
            // would pass a shader returning 1 for every fragment it does run. A pixel strictly between 0
            // and 1 can only come from a working ramp.
            double graded = 0.0;
            int gradedRow = -1;
            var ramp = new System.Text.StringBuilder("P1 face-on: rows across the band's outer edge → ");
            for (int row = SnapPx / 2 + 200; row <= SnapPx / 2 + 232; row++)
            {
                double c = frame.CoverageAt(SnapPx / 2, row);
                ramp.Append($"{row - SnapPx / 2}={c:F3} ");
                double gradedness = math.min(c, 1.0 - c);
                if (gradedness > graded) { graded = gradedness; gradedRow = row; }
            }
            TestContext.WriteLine(ramp.ToString());
            Assert.That(graded, Is.GreaterThan(0.05),
                $"no partially-covered pixel anywhere across the band's outer edge (best gradedness " +
                $"{graded:F4} at row {gradedRow}) — the probe is returning a constant and its interior " +
                "reading means nothing.");
        }

        /// <summary>
        /// The junction case. A 2x2 derivative quad straddling the interior/band boundary contains fragments
        /// of two primitives with different <c>side</c> gradients — zero on one side, <c>1/bandPx</c> on the
        /// other. Scans outward across it at a one-device-pixel band, the shipped sizing, and requires
        /// coverage to stay saturated right up to the styled edge and then fall inside about a pixel. A dip
        /// at the junction is the failure mode this arm exists to catch; it would render as a dark hairline
        /// inset from every polygon boundary.
        /// </summary>
        [Test]
        public void ConstantSideInterior_HoldsAcrossTheBandJunction()
        {
            Mesh mesh = BuildBandedQuad(WorldPerPx);
            using ProbeFrame frame = Render(new[] { mesh }, new[] { ProbeMaterial(0f, 3000) }, tiltDegrees: 0f);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            // The interior's top edge sits at 8 world units above centre = 204.8 px; the band adds 1 px.
            const int centre = SnapPx / 2;
            var profile = new System.Text.StringBuilder("P1 junction: rows about the interior/band seam → ");
            double worstInside = 1.0;
            int worstRow = -1;
            for (int row = centre + 195; row <= centre + 212; row++)
            {
                double c = frame.CoverageAt(centre, row);
                profile.Append($"{row - centre}={c:F3} ");
                if (row <= centre + 203 && c < worstInside) { worstInside = c; worstRow = row; }
            }
            TestContext.WriteLine(profile.ToString());

            Assert.That(worstInside, Is.GreaterThan(0.99),
                $"coverage dipped to {worstInside:F4} at row {worstRow}, inside the interior and within a " +
                "pixel of its junction with the band. That is a derivative quad straddling two primitives " +
                "poisoning the constant-side interior — the mechanism would draw a dark hairline just " +
                "inside every boundary.");
        }

        /// <summary>
        /// Grazing tilt. The band foreshortens toward zero projected width, so <c>|grad side|</c> grows
        /// without bound and the band's own coverage collapses — that part is expected and is the mechanism
        /// degrading to today's hard edge, not a defect. What must NOT move is the interior: its derivative
        /// is 0 at any view angle, so its coverage must still be 1.
        /// </summary>
        [Test]
        public void ConstantSideInterior_HoldsUnderGrazingTilt()
        {
            Mesh mesh = BuildBandedQuad(WorldPerPx);
            using ProbeFrame frame = Render(new[] { mesh }, new[] { ProbeMaterial(0f, 3000) }, tiltDegrees: 80f);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            // Under an 80-degree tilt the quad compresses toward the frame's middle band; sample a box that
            // is interior at that pose, then report what was actually found rather than trusting the pose.
            double worst = 1.0;
            int worstX = -1, worstY = -1, sampled = 0;
            for (int y = 249; y < 263; y++)
            for (int x = 236; x < 276; x++)
            {
                double c = frame.CoverageAt(x, y);
                sampled++;
                if (c < worst) { worst = c; worstX = x; worstY = y; }
            }
            TestContext.WriteLine(
                $"P1 tilt 80 deg: interior minimum coverage = {worst:F4} at ({worstX},{worstY}) over {sampled} px.");

            Assert.That(worst, Is.GreaterThan(0.99),
                $"under an 80 degree tilt the constant-side interior read {worst:F4} at " +
                $"({worstX},{worstY}). The interior's derivative is 0 at every view angle, so a shortfall " +
                "here means the clamp is view-dependent.");
        }

        // ── P3: does a coverage-0 fragment in a ZWrite-On pass occlude what is behind it? ───────────────

        /// <summary>
        /// The fill's ShadowCaster / GBuffer / DepthOnly / DepthNormals passes hardcode <c>ZWrite On</c> and
        /// take no coverage input. A band fragment at coverage 0 is fully transparent but is still a
        /// fragment, so it writes depth unless the pass clips. Isolated here with a constant
        /// <c>side = 1</c> quad — coverage 0 across its whole face — drawn in front of an opaque red quad
        /// queued behind it. Both arms are rendered: <c>ZWrite On</c> is the fill's depth passes, and
        /// <c>ZWrite Off</c> is the control that proves the arms differ for the reason claimed and not
        /// because the red quad was never visible.
        /// </summary>
        [Test]
        public void ZeroCoverageFragment_WritesDepth_AndHidesGeometryBehindIt()
        {
            const int Box = 40;
            int lo = SnapPx / 2 - Box, hi = SnapPx / 2 + Box;

            double[] redness = new double[2];
            foreach (bool zWrite in new[] { false, true })
            {
                Mesh front = BuildConstantSideQuad(6f, side: 1f, z: 0f);
                Mesh back  = BuildConstantSideQuad(6f, side: 0f, z: 5f);

                var frontMaterial = ProbeMaterial(zWrite ? 1f : 0f, 3000);
                var backMaterial  = ProbeMaterial(0f, 3100);
                backMaterial.SetColor("_Color", Color.red);

                using ProbeFrame frame = Render(new[] { front, back },
                                                new[] { frontMaterial, backMaterial }, tiltDegrees: 0f);
                if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

                float3 mean = frame.MeanLinear(lo, lo, hi, hi);
                float3 background = ProbeFrame.BackgroundLinear();
                // How far the box has travelled from background toward pure red, in linear space.
                redness[zWrite ? 1 : 0] = (mean.x - background.x) / math.max(1f - background.x, 1e-6f);
                TestContext.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "P3 ZWrite {0}: mean linear RGB behind the coverage-0 quad = ({1:F4},{2:F4},{3:F4}), " +
                    "redness = {4:F4}", zWrite ? "On " : "Off", mean.x, mean.y, mean.z, redness[zWrite ? 1 : 0]));
            }

            Assert.That(redness[0], Is.GreaterThan(0.8),
                $"control: with ZWrite Off the red quad behind must be visible through a fully transparent " +
                $"fragment; redness {redness[0]:F4}. If this fails the arms below compare nothing.");
            Assert.That(redness[1], Is.LessThan(0.2),
                $"with ZWrite On a coverage-0 fragment did NOT occlude the geometry behind it (redness " +
                $"{redness[1]:F4}). That would mean the fill's depth passes need no coverage clip, which " +
                "contradicts the whole reason this arm exists — check the fixture before the conclusion.");
        }

        // ── P2: two fragments of ONE fill layer at one pixel — do they composite twice? ─────────────────

        /// <summary>
        /// The outward band puts a polygon's ramp OVER its neighbour's interior wherever two polygons of one
        /// layer abut — which, at the shipped <c>FillTileBufferClip: 0</c>, is every tile seam. Whether that
        /// costs anything at <c>fill-opacity &lt; 1</c> depends on a fact about the pipeline, not about the
        /// band: do two fragments of the SAME layer, same mesh, same draw, blend twice at one pixel, or does
        /// depth state reject the second?
        ///
        /// <para>Two overlapping polygons in one layer answer it directly and need no shader. The costing's
        /// §1 lemma is about the BACKGROUND's surviving weight and is correct; this measures the composited
        /// alpha, which is a different quantity and the one a rim is made of.</para>
        /// </summary>
        [Test]
        public void OverlappingPolygonsInOneLayer_ReportsWhetherTheyCompositeTwice()
        {
            const double Opacity = 0.5;
            var tile = new TileId { Z = 6, X = 40, Y = 25 };

            double2 Corner(double u, double v) => tile.ToLonLat(u, v, 1.0);
            string Rect(double westU, double eastU)
            {
                double2 nw = Corner(westU, 0.2), se = Corner(eastU, 0.8);
                // Tile-local v grows SOUTHWARD, so the small-v corner carries the NORTH latitude.
                return GeoJsonTestFixtures.Feature(
                    "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(nw.x, se.y, se.x, nw.y)}]");
            }

            double2 centre = Corner(0.5, 0.5);
            using var scene = VisualScene.New()
                .RenderMode(MapRenderer.Unity.Rendering.Materials.RenderMode.Unlit)
                .Source("poly", GeoJson.FeatureCollection(
                    GeoJsonTestFixtures.Collection(Rect(0.15, 0.55), Rect(0.45, 0.85))))
                .Layer(VisualLayer.Fill("probe-fill").Source("poly").Color("#808080").Opacity(Opacity))
                .Camera(new GeoCoordinate3D { Longitude = centre.x, Latitude = centre.y, Altitude = 0.0 },
                        zoom: tile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            if (frame.NoGpuContext) { Assert.Inconclusive(NoGpuMessage); return; }

            byte[] px = frame.RawPixels;
            float3 background = PixelCoverage.BackgroundLinear(px, frame.Width, frame.Height);
            float3 Mean(int x0, int y0, int x1, int y1)
            {
                float3 sum = float3.zero;
                int n = 0;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { sum += PixelCoverage.SampleLinear(px, frame.Width, frame.Height, x, y); n++; }
                return sum / math.max(n, 1);
            }

            // Overlap spans u 0.45..0.55 = columns 230..282; single cover sits well left of it.
            float3 single  = Mean(120, 200, 180, 260);
            float3 overlap = Mean(244, 200, 268, 260);

            // Ink laid down, relative to the background, as a fraction of the axis the single-covered
            // region defines: 1.0 = one coat, 1.5 = two coats at alpha 0.5 (0.75 / 0.50).
            double singleInk  = math.length(single  - background);
            double overlapInk = math.length(overlap - background);
            double ratio = overlapInk / math.max(singleInk, 1e-6);

            TestContext.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "P2 overlap at fill-opacity {0:F2}: single-cover ink {1:F4}, overlap ink {2:F4}, " +
                "ratio {3:F4} (1.000 = the second fragment was rejected, 1.500 = it composited over the first)",
                Opacity, singleInk, overlapInk, ratio));

            Assert.That(ratio, Is.GreaterThan(1.05),
                $"two overlapping polygons of ONE layer composited to a ratio of {ratio:F4}, i.e. the " +
                "second fragment did not blend over the first. If that is real it changes the outward " +
                "band's abutting-pair story completely — verify the fixture before believing it.");
        }
    }
}
#endif // UNITY_EDITOR
