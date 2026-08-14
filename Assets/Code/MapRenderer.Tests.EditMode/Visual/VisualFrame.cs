// Unity EditMode only — Stage G-V0, the declarative visual-test authoring kit.
// NOT registered in Tools/core-tests/core-tests.csproj.

#if UNITY_EDITOR
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The result of a <see cref="VisualScene.Render"/> call: the decoded RGBA32 frame plus the seams a
    /// fixture asserts over — the exact <see cref="StyleDocument"/> handed to <c>SetStyle</c> (the T-Parse
    /// seam), and the live <see cref="MapViewComponent"/> + <see cref="Camera"/> handles a LATER stage's
    /// <c>Ink(layerId)</c>/<c>GlyphAnchors(layerId)</c> accessors will read (plan §10 — not implemented now,
    /// the handles are kept so adding them touches no composer).
    /// </summary>
    internal sealed class VisualFrame
    {
        /// <summary>RGBA32, row-major, BOTTOM-left origin (Unity's native <c>ReadPixels</c> convention — see
        /// <see cref="SnapshotRenderer.RawPixels"/>). Whole-frame fractions/buckets are orientation-independent;
        /// a region assertion must stay origin-symmetric (a center box, or corners AS A SET) rather than name
        /// a screen direction.</summary>
        public byte[] RawPixels { get; }

        /// <summary>Render target width, px.</summary>
        public int Width { get; }

        /// <summary>Render target height, px.</summary>
        public int Height { get; }

        private readonly byte _bgR, _bgG, _bgB;

        /// <summary>The exact <see cref="StyleDocument"/> instance <see cref="VisualScene.Render"/> handed to
        /// <c>MapViewComponent.SetStyle</c> — the T-Parse seam: it can only carry the shape the REAL
        /// <c>StyleParser.Parse</c> produces (plan §6).</summary>
        public StyleDocument ParsedStyle { get; }

        /// <summary>True when this frame AND a same-background blank-camera control both render (near-)all-black
        /// — batch EditMode has no GPU context, not a genuine "nothing rendered". A fixture must check this
        /// BEFORE any coverage assertion, positive or negative (a negative-control assertion cannot tell a real
        /// skip from a GPU-less black frame on its own).</summary>
        public bool NoGpuContext { get; }

        /// <summary>The live view this frame was rendered from — kept for a later ink/anchor accessor and for
        /// wiring observability (<c>MapViewTestExtensions.WiredFeatureSourceCount</c>), NOT torn down by this
        /// carrier: <see cref="VisualScene.Dispose"/> owns its lifetime.</summary>
        public MapViewComponent MapView { get; }

        /// <summary>The Unity camera this frame was rendered from. Same lifetime note as <see cref="MapView"/>.</summary>
        public Camera Camera { get; }

        internal VisualFrame(
            byte[] rawPixels, int width, int height, byte bgR, byte bgG, byte bgB,
            StyleDocument parsedStyle, bool noGpuContext, MapViewComponent mapView, Camera camera)
        {
            RawPixels    = rawPixels;
            Width        = width;
            Height       = height;
            _bgR         = bgR;
            _bgG         = bgG;
            _bgB         = bgB;
            ParsedStyle  = parsedStyle;
            NoGpuContext = noGpuContext;
            MapView      = mapView;
            Camera       = camera;
        }

        /// <summary>Whole-frame coverage verdict against this scene's background colour. Delegates to
        /// <see cref="SnapshotCoverage.Analyse"/> — see <see cref="SnapshotVerdict.Passes"/> for the tolerant
        /// pass/fail band.</summary>
        public SnapshotVerdict Coverage() => SnapshotCoverage.Analyse(RawPixels, Width, Height, _bgR, _bgG, _bgB);

        /// <summary>Mean (R,G,B) of the inclusive-exclusive rectangle [x0,x1)×[y0,y1), each channel in [0,1].
        /// Passthrough to <see cref="SnapshotCoverage.SampleRegionMeanColor"/>.</summary>
        public double[] RegionMeanColor(int x0, int y0, int x1, int y1)
            => SnapshotCoverage.SampleRegionMeanColor(RawPixels, Width, Height, x0, y0, x1, y1);

        /// <summary>Total colour variance of the same rectangle. Passthrough to
        /// <see cref="SnapshotCoverage.RegionColorVariance"/>.</summary>
        public double RegionColorVariance(int x0, int y0, int x1, int y1)
            => SnapshotCoverage.RegionColorVariance(RawPixels, Width, Height, x0, y0, x1, y1);

        /// <summary>Mean luminance of pixels that are NOT the background colour. Passthrough to
        /// <see cref="SnapshotCoverage.MeanLuminanceOfNonBackground"/>.</summary>
        public double MeanLuminanceOfNonBackground()
            => SnapshotCoverage.MeanLuminanceOfNonBackground(RawPixels, Width, Height, _bgR, _bgG, _bgB);

        /// <summary>Pixel-weighted ink centroid + count inside the inclusive-exclusive window
        /// <c>[x0,x1)×[y0,y1)</c>. A pixel is "ink" when it differs from THIS frame's background beyond
        /// <see cref="SnapshotCoverage.Tolerance"/> — the SAME background predicate
        /// <see cref="SnapshotCoverage.Analyse"/> uses (not a forked threshold); unusable here is
        /// <c>WorldSymbolInkAnalysis</c>'s white-background "R &lt; 200" rule, since this kit's background is
        /// dark slate and every background pixel would read as ink under it.
        ///
        /// <para><b>No flip.</b> <see cref="RawPixels"/> is BOTTOM-left origin (Unity's native
        /// <c>ReadPixels</c>), and <c>IProjection.GroundToScreen</c> / <c>Camera.WorldToScreenPoint</c> are
        /// also bottom-left, <c>+y</c> up — so <paramref name="x0"/>/<paramref name="y0"/> here compare
        /// directly against an oracle screen pixel with no coordinate conversion at all.</para>
        ///
        /// <para><b>Deferred: a per-layer <c>Ink(layerId)</c>.</b> A shared RGBA frame cannot attribute a
        /// pixel to a style layer, and this stage renders exactly one inking layer (the label text), so all
        /// non-background ink already IS that layer's ink — a <c>layerId</c> parameter would be vacuous. A
        /// real per-layer accessor waits for a stage that renders two inking layers together and needs to
        /// tell them apart.</para></summary>
        /// <param name="x0">Window left edge, px (inclusive).</param>
        /// <param name="y0">Window bottom edge, px (inclusive) — bottom-left origin, see above.</param>
        /// <param name="x1">Window right edge, px (exclusive).</param>
        /// <param name="y1">Window top edge, px (exclusive).</param>
        /// <param name="centroidPx">The window's pixel-weighted ink centroid, in this frame's bottom-left
        /// px space; <c>(NaN, NaN)</c> when <paramref name="inkCount"/> is zero.</param>
        /// <param name="inkCount">Count of non-background pixels inside the window.</param>
        public void InkStatsIn(int x0, int y0, int x1, int y1, out double2 centroidPx, out int inkCount)
        {
            x0 = math.clamp(x0, 0, Width);
            x1 = math.clamp(x1, 0, Width);
            y0 = math.clamp(y0, 0, Height);
            y1 = math.clamp(y1, 0, Height);

            double sumX = 0.0, sumY = 0.0;
            int count = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * Width;
                for (int x = x0; x < x1; x++)
                {
                    int b = (row + x) * 4;
                    int dist = math.abs(RawPixels[b] - _bgR)
                             + math.abs(RawPixels[b + 1] - _bgG)
                             + math.abs(RawPixels[b + 2] - _bgB);
                    if (dist <= SnapshotCoverage.Tolerance) continue; // background — same predicate as Analyse

                    sumX += x + 0.5; // pixel CENTER, not corner
                    sumY += y + 0.5;
                    count++;
                }
            }

            inkCount   = count;
            centroidPx = count > 0 ? new double2(sumX / count, sumY / count) : new double2(double.NaN, double.NaN);
        }
    }
}
#endif // UNITY_EDITOR
