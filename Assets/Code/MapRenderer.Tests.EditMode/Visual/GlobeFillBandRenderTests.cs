// GlobeFillBandRenderTests.cs — the boundary band, observed in rendered pixels on the CURVED arm.
//
// The shipped Liberty scene is a globe scene, so the flat arm's rendered teeth say nothing about the
// configuration the feature was built for. This renders the same globe twice — band on, band off — and
// measures the difference between the two frames rather than modelling absolute colours, which a lit
// sphere makes unmodellable.
//
// CULL BACK IS LOAD-BEARING HERE, not scene dressing. Earcut normalises every outer ring's winding before
// triangulating, so a band that inherited its ring's own sign is counter-wound against the interior and
// back-face culled: the fill renders EXACTLY as before, with correct geometry in the mesh and every
// job-level tooth green. That defect is invisible under the _Cull Off default and visible here.

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Fill;
using MapRenderer.Unity.Rendering.Materials;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class GlobeFillBandRenderTests
    {
        private const int SnapPx = 512;

        /// <summary>Ocean-ish clear colour, so land polygons read against a known background.</summary>
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        /// <summary>Three 8-bit LSBs, in RGB distance — comfortably above quantisation and far below a
        /// partially-covered pixel's step.</summary>
        private const double Tolerance = 3.0 / 255.0 * 1.7320508075688772;

        private const string NoGpuMessage =
            "Globe render is all-background: no GPU context in batch EditMode. " +
            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode";

        /// <summary>The three-quarter view the Americas face: no fill boundary lies along the silhouette,
        /// so grazing incidence is approached but never entered.</summary>
        private static readonly (Vector3 Position, Vector3 Up) ObliquePose =
            (new Vector3(1.7f, 1.2f, -3.0f), Vector3.up);

        /// <summary>Straight down the polar axis (+Y in render space — <c>SphericalProjection</c> maps
        /// latitude to Y), which puts the EQUATOR on the silhouette. Equatorial coastline then runs along the
        /// limb with its outward band direction pointing along the view ray, which is the one geometry that
        /// drives <c>MapPixelsToWorld</c>'s probe to zero. <c>Vector3.up</c> cannot be the camera's up here —
        /// it is the view direction.</summary>
        private static readonly (Vector3 Position, Vector3 Up) PolarPose =
            (new Vector3(0f, 3.5f, 0f), Vector3.forward);

        /// <summary>Renders the z0 countries fixture on a sphere, with or without the boundary band.</summary>
        /// <param name="pose">Camera position and up vector; the camera always looks at the origin.</param>
        /// <param name="suppressBand">True to build the same mesh with no band geometry at all.</param>
        /// <param name="superSample">Render at this multiple and box-downsample, to see sub-pixel geometry.</param>
        /// <returns>The frame's RGBA32 pixels, row-major from the bottom-left.</returns>
        private static byte[] RenderGlobe(
            (Vector3 Position, Vector3 Up) pose, bool suppressBand, int superSample = 1)
        {
            var (mapGo, material) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"rgba\",95,165,95,1]",
                viewSize: 2f,
                projection: new SphericalProjection(),
                suppressBoundaryBand: suppressBand);

            // Stock Cull Back — see this file's header. Without it a counter-wound band still renders and
            // the winding defect this fixture is here to catch passes.
            if (material != null) material.SetCull(CullMode.Back);

            var lightGo = new GameObject("GlobeBandLight");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);
            lightGo.transform.SetParent(mapGo.transform, worldPositionStays: true);

            var cameraGo = new GameObject("GlobeBandCamera");
            Camera camera = cameraGo.AddComponent<Camera>();
            camera.transform.position = pose.Position;
            camera.transform.rotation = Quaternion.LookRotation(-pose.Position, pose.Up);
            camera.orthographic = false;
            camera.fieldOfView = 35f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = OceanBg;
            camera.enabled = false;

            var snap = new SnapshotRenderer(SnapPx * superSample, SnapPx * superSample);
            try
            {
                snap.Render(camera);
                byte[] raw = snap.RawPixels;
                if (superSample == 1) return (byte[])raw.Clone();

                // Box-downsample: a pixel that any sub-sample covered carries ink. This is what a hard
                // rasterizer WOULD have drawn if it could see sub-pixel geometry.
                //
                // MEASURED, so nobody re-opens it: reducing by MAX deviation from the clear colour instead —
                // strictly more faithful to "was any sub-sample covered", since a 1/64-covered pixel averages
                // down to a single LSB against a half-LSB threshold — produces the IDENTICAL offender count
                // on both poses. The residual this tooth reports is therefore not an artefact of averaging.
                int wide = SnapPx * superSample;
                var small = new byte[SnapPx * SnapPx * 4];
                for (int y = 0; y < SnapPx; y++)
                    for (int x = 0; x < SnapPx; x++)
                    {
                        int acc0 = 0, acc1 = 0, acc2 = 0;
                        for (int sy = 0; sy < superSample; sy++)
                            for (int sx = 0; sx < superSample; sx++)
                            {
                                int si = ((y * superSample + sy) * wide + x * superSample + sx) * 4;
                                acc0 += raw[si]; acc1 += raw[si + 1]; acc2 += raw[si + 2];
                            }
                        int n2 = superSample * superSample, di = (y * SnapPx + x) * 4;
                        small[di] = (byte)(acc0 / n2); small[di + 1] = (byte)(acc1 / n2);
                        small[di + 2] = (byte)(acc2 / n2); small[di + 3] = 255;
                    }
                return small;
            }
            finally
            {
                snap.Dispose();
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo); // the light is parented to mapGo
            }
        }

        /// <summary>One pixel's RGB.</summary>
        /// <param name="px">The frame.</param>
        /// <param name="i">Pixel index.</param>
        /// <returns>The colour, channels in [0,1].</returns>
        private static double3 At(byte[] px, int i)
            => new double3(px[i * 4] / 255.0, px[i * 4 + 1] / 255.0, px[i * 4 + 2] / 255.0);

        // ── The band reaches the globe, and grows outward by one pixel ─────────────────────────────────

        /// <summary>
        /// Rendered band-on against band-off on the same globe:
        /// <list type="bullet">
        /// <item>background pixels GAIN ink, and there are some — the band renders at all, un-culled;</item>
        /// <item>no pixel LOSES ink to background — nothing was displaced inward or removed;</item>
        /// <item>every gained pixel sits within <see cref="FillBandJob.MiterLimit"/> + 1 px of geometry the
        /// 8× reference shows — a rim, not a geometry shift.</item>
        /// </list>
        ///
        /// <para><b>The reach bound is DERIVED, and so is the oracle.</b> Both were wrong in an earlier form
        /// of this tooth, which asserted 2 px against the band-free frame and failed on 481 correct pixels.
        /// <list type="number">
        /// <item><b>Why <see cref="FillBandJob.MiterLimit"/> + 1, not 1.</b> The band is one device pixel
        /// measured PERPENDICULAR to an edge, and the join factor rides in the band vector's magnitude
        /// precisely to hold that through a corner — so at a sharp coastline spike the outer vertex sits up
        /// to <c>MiterLimit</c> px along the bisector, by the same design
        /// <c>FillBandJob</c>'s <c>AMiterKeepsThePerpendicularWidthThroughARightAngle</c> asserts. A bound
        /// below that contradicts a tooth of this same feature. The <c>+ 1</c> is the rasterised pixel the
        /// outermost vertex lands in; nothing else is added, and the number is never widened to clear a
        /// count.</item>
        /// <item><b>Why the 8× reference and not the band-free frame.</b> An island or peninsula narrower
        /// than a pixel renders as NOTHING at 1×, so the band-free frame is missing exactly the geometry the
        /// band is correctly antialiasing: 66 such pixels had no 1× ink within 16 px in any direction while
        /// sitting right on top of real coastline. <b>It does not close the gap entirely</b> — 45 pixels here
        /// and 78 pole-on remain, whose banded ink is a HAIRLINE (median 2-4 ink px in a 5×5, most with no
        /// offender neighbour), i.e. features too thin for even 8× to sample. Reducing by max coverage
        /// instead of mean leaves the same counts, so it is not an averaging artefact. The exact oracle is
        /// the mesh, not pixels — project the band quads' ring segments and test distance to the SEGMENT.
        /// <b>Attempted and withdrawn, so the next attempt starts informed:</b> collecting ring edges as
        /// "every edge of a band triangle whose two endpoints both carry side 0" measured far WORSE (2231
        /// here, 3585 pole-on). Its mask traced front-facing coastline near the disk centre and back-side
        /// geometry showing through, but was empty along front-facing coastline AT THE LIMB — where the
        /// offenders are — so the collection, not the idea, is what is incomplete. Two traps already paid
        /// for: <c>Camera.WorldToScreenPoint</c> projects to the camera's pixel rect, which
        /// <c>SnapshotRenderer</c> has not yet set to <see cref="SnapPx"/> at that point (use the
        /// clip-space route), and no flip of the mask improves the fit, so alignment is not the fault.</item>
        /// </list>
        /// Together they make this a bound on DISPLACEMENT rather than on antialiasing quality: band ink can
        /// only ever appear within a miter of the ring segment it is anchored to, so a displacement that is
        /// not the size it claims lands outside it.</para>
        ///
        /// <para><b>The bound is a COUNT, not zero, and the count is the oracle's blind spot — not the
        /// band's error.</b> Three numbers, three roles: <b>measured</b> 45 here and 78 pole-on on this
        /// machine; <b>asserted</b> 60 and 100, the measurement plus slack, because these are rendered
        /// counts and a different GPU or driver may rasterise a hairline differently — an exact pin on a
        /// GPU-dependent quantity is a flake, not a tighter tooth; <b>RED</b> at 290 and 351, where a x2
        /// displacement lands. The slack spends under a tenth of the distance to the RED, which still fails
        /// by 4.8x oblique and 3.5x pole-on. The surviving pixels are hairlines:
        /// median 2-4 ink px in a 5×5 neighbourhood, most with no offender neighbour at all. They exist
        /// because a band quad is emitted per ring edge regardless of how thin the feature is, so a coastline
        /// sliver too thin for even 8× supersampling to sample renders as nothing in the reference while its
        /// band is drawn at full width. Nothing is taken away — <c>lost = 0</c> throughout.
        /// What the bound cannot discriminate is sub-pixel geometry, which is a property of the oracle and
        /// is written down here rather than left as an unexplained residual.</para>
        ///
        /// <para><b>Two behaviours this deliberately passes, because they are the contract.</b> A band vertex
        /// up to <see cref="FillBandJob.MiterLimit"/> px out at a sharp corner, with the perpendicular width
        /// still one pixel — that is what the miter is FOR. And band ink over a feature the hard rasterizer
        /// dropped entirely: an island narrower than a pixel renders as nothing band-free and is painted
        /// band-on, which is antialiasing doing its job, not a leak (<c>lost = 0</c> proves nothing was taken
        /// away). Neither is a defect to suppress; suppressing either would change what the feature
        /// promises.</para>
        ///
        /// <para><b>No interior-purity assertion here, deliberately.</b> The fixture is the countries layer,
        /// whose polygons share internal edges, and a band along an intra-layer shared edge composites over
        /// its neighbour's interior BY DESIGN — the residual rim the mechanism accepts. An interior-purity
        /// check over this fixture would therefore red on correct behaviour. What covers that claim instead:
        /// <c>FillBoundaryBandRenderTests.ASquareFillHasGradedBoundaryPixels_AndAnUngradedInterior</c> on a
        /// single polygon, and — on this arm specifically —
        /// <c>GlobeFillBandTests.ABandQuadSplitsTheSharedEdgeExactlyWhereTheInteriorDoes</c>, which asserts
        /// every interior vertex still carries <c>side = 0</c> and a bit-identical position after
        /// subdivision.</para>
        ///
        /// <para>RED-verify: ① suppress the band on both arms and the gain count falls to 0; ② counter-wind
        /// the band quads (drop <c>FillBandJob</c>'s reversal for a positively-wound ring) and Cull Back
        /// removes them, so the gain count falls to 0 again; ③ amplify the shader displacement <b>×2</b> and
        /// the REACH assertion reds — 45 offenders become 290 here and 78 become 351 pole-on.
        /// <b>×2, not ×20, and the difference is not cosmetic.</b> At ×20 the band adds 132 984 px against
        /// 56 091 px of fill, so the ink-volume guard above fires FIRST and the reach assertion is never
        /// evaluated: ×20 reds this test while proving nothing about the bound it is here to verify. A ×2
        /// band stays under that guard (11 889 &lt; 14 022) and is caught by reach alone, which also shows
        /// the bound discriminates a mere DOUBLING, not only a gross displacement.</para>
        /// </summary>
        [Test]
        public void TheGlobeFillsSilhouetteGainsInkOutward_WithinTheMiterLimit()
            => AssertTheBandGrowsOutwardWithinAMiter(ObliquePose, "oblique", oracleBlindPixels: 60);

        /// <summary>
        /// The same contract straight down the polar axis, which is the pose that actually exercises the
        /// grazing-incidence hazard of UMR-105, if anything does. Pole-on, the equator IS the silhouette,
        /// so equatorial coastline runs ALONG the limb and its outward band direction points down the view
        /// ray — the geometry that drives <c>MapPixelsToWorld</c>'s probe span to zero, where its
        /// <c>max(refPx, 0.1)</c> clamp stops the scale exploding but cannot stop a finite step along a
        /// tangent that is edge-on. <see cref="ObliquePose"/> approaches that regime and never enters it:
        /// with the guard disabled entirely it renders pixel-for-pixel the same frame.
        ///
        /// <para><b>This pose is the evidence that the regime is not reached.</b> A grazing-incidence
        /// suppression guard lived in <c>Fill_VertexModify.hlsl</c> and was deleted after measurement:
        /// disabling it entirely left BOTH poses pixel-for-pixel identical on every field, here included, so
        /// nothing it could have suppressed was ever drawn. The arithmetic behind the hazard is real and is
        /// recorded in UMR-105; its manifestation was not reachable from any camera tried, and this is the
        /// pose that tried hardest. A guard nobody can trigger is superstition, and the difference is a pose
        /// where removing it changes the picture — there is none.</para>
        ///
        /// <para>RED-verify: as the oblique case, and additionally this pose renders 64% more ink
        /// (92 086 px against 56 091), so a displacement defect has more boundary to show up on, not
        /// less.</para>
        /// </summary>
        [Test]
        public void PoleOn_WhereTheLimbCarriesFillBoundary_TheBandIsStillBoundedByAMiter()
            => AssertTheBandGrowsOutwardWithinAMiter(PolarPose, "polar", oracleBlindPixels: 100);

        /// <summary>Renders one pose band-on against band-off and asserts the whole outward-growth contract.
        /// </summary>
        /// <param name="pose">Camera position and up vector.</param>
        /// <param name="poseName">Short label for the reported measurements.</param>
        /// <param name="oracleBlindPixels">Offenders the 8× oracle cannot explain — a limitation of the
        /// ORACLE, not of the band. Measurement plus slack, because the count is GPU-dependent; see this
        /// file's <c>TheGlobeFillsSilhouetteGainsInkOutward</c> doc for all three numbers.</param>
        private static void AssertTheBandGrowsOutwardWithinAMiter(
            (Vector3 Position, Vector3 Up) pose, string poseName, int oracleBlindPixels)
        {
            byte[] hard = RenderGlobe(pose, suppressBand: true);
            byte[] banded = RenderGlobe(pose, suppressBand: false);

            int n = SnapPx * SnapPx;
            double3 background = At(hard, 0);

            var isBackground = new bool[n];
            int ink = 0;
            for (int i = 0; i < n; i++)
            {
                isBackground[i] = math.length(At(hard, i) - background) <= Tolerance;
                if (!isBackground[i]) ink++;
            }
            if (ink == 0) Assert.Ignore(NoGpuMessage);

            int gained = 0, lost = 0;
            var lostDetail = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                bool bandedIsBackground = math.length(At(banded, i) - background) <= Tolerance;
                if (isBackground[i] && !bandedIsBackground) gained++;
                if (!isBackground[i] && bandedIsBackground)
                {
                    lost++;
                    if (lost <= 8)
                        lostDetail.Append($" ({i % SnapPx},{i / SnapPx}) hard={At(hard, i)} banded={At(banded, i)}");
                }
            }

            Assert.Greater(gained, 0,
                $"the band contributed no pixel to a globe frame (ink={ink}). Either it is not emitted on " +
                "the curved arm at all, or it is counter-wound and Cull Back is discarding it — the failure " +
                "mode that leaves every job-level tooth and every digest green.");
            Assert.AreEqual(0, lost,
                "a pixel that carried ink without the band lost it with the band. The band only ever adds " +
                $"coverage OUTSIDE the boundary; anything that removes ink means geometry moved. bg={background}" +
                $" first lost:{lostDetail}");
            Assert.Less(gained, ink / 4,
                $"the band added {gained} px against {ink} px of fill — far too many for a one-pixel rim " +
                "around the silhouettes. That is a geometry shift, not antialiasing.");

            // Reach: every gained pixel must sit within a miter of geometry that is really there. The bound
            // is DERIVED from the production miter ceiling, not chosen — see this test's doc. The failure
            // carries each offender's separation AND its radius from the frame centre, because the globe's
            // limb sits at a known radius and "is this the limb?" is the first question a red here raises.
            // The oracle for "is there geometry here" is the band-free build SUPERSAMPLED 8× and
            // box-downsampled — see this test's doc for why the 1× frame cannot serve. ANY deviation from
            // the background counts here, not the 3-LSB colour tolerance, because a sub-pixel sliver is
            // exactly what this reference exists to find.
            byte[] superHard = RenderGlobe(pose, suppressBand: true, superSample: 8);
            const double faintest = 0.5 / 255.0;
            var hasGeometry = new bool[n];
            for (int i = 0; i < n; i++)
                hasGeometry[i] = math.length(At(superHard, i) - background) > faintest;

            int reach = (int)FillBandJob.MiterLimit + 1;
            const int probe = 16;
            int worst = 0, offenders = 0;
            var offenderIndices = new List<int>();
            var detail = new System.Text.StringBuilder();
            for (int y = probe; y < SnapPx - probe; y++)
                for (int x = probe; x < SnapPx - probe; x++)
                {
                    int i = y * SnapPx + x;
                    if (!isBackground[i]) continue;
                    if (math.length(At(banded, i) - background) <= Tolerance) continue;

                    int nearest = int.MaxValue;
                    for (int dy = -probe; dy <= probe; dy++)
                        for (int dx = -probe; dx <= probe; dx++)
                            if (hasGeometry[(y + dy) * SnapPx + (x + dx)])
                                nearest = math.min(nearest, math.max(math.abs(dx), math.abs(dy)));
                    if (nearest <= reach) continue;

                    offenders++;
                    offenderIndices.Add(i);
                    worst = math.max(worst, nearest == int.MaxValue ? probe : nearest);
                    if (offenders <= 10)
                        detail.Append($" ({x},{y}) sep={nearest} r={math.length(new double2(x - 256.0, y - 256.0)):F1}");
                }

            // How much band ink lands at the limb, where UMR-105 says the px->world differential is least
            // trustworthy. Reported, not asserted — it is a quantity to know, not a contract.
            int nearLimb = 0;
            for (int i = 0; i < n; i++)
                if (isBackground[i] && math.length(At(banded, i) - background) > Tolerance
                    && math.length(new double2(i % SnapPx - 256.0, i / SnapPx - 256.0)) > 220.0)
                    nearLimb++;

            TestContext.WriteLine($"[globe band {poseName}] ink={ink} gained={gained} lost={lost} reach={reach} " +
                                  $"offenders={offenders} worst={worst} gained-near-limb={nearLimb}");

            if (offenders > 0)
            {
                var mask = (byte[])banded.Clone();
                foreach (int i in offenderIndices)
                { mask[i * 4] = 255; mask[i * 4 + 1] = 0; mask[i * 4 + 2] = 255; }
                TestContext.WriteLine($"[globe band {poseName}] " +
                    SnapshotRenderer.WritePngFromRgba32(hard, SnapPx, SnapPx, $"band-limb-{poseName}-hard.png") + " " +
                    SnapshotRenderer.WritePngFromRgba32(banded, SnapPx, SnapPx, $"band-limb-{poseName}-banded.png") + " " +
                    SnapshotRenderer.WritePngFromRgba32(mask, SnapPx, SnapPx, $"band-limb-{poseName}-offenders.png"));
            }

            Assert.LessOrEqual(offenders, oracleBlindPixels,
                $"{offenders} pixel(s) gained ink from the band more than {reach} px from any geometry in " +
                $"the 8x supersampled band-free reference, above the {oracleBlindPixels} allowed for this " +
                $"oracle's blind spot; worst separation {worst} px. The band reaches at " +
                $"most {FillBandJob.MiterLimit} px along a join bisector, so ink beyond that is a " +
                "displacement that is not the size it claims. The globe's " +
                "silhouette in this fixture has radius 231.2 px " +
                "about (256,256) — an offender at r near that is at the LIMB, where the surface tangent is " +
                $"edge-on and MapPixelsToWorld hits its clamp.{detail}");
        }
    }
}
#endif
