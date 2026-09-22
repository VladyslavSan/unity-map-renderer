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
    /// The result of a <see cref="VisualScene.Render"/> call: the decoded frame plus the seams a
    /// fixture asserts over — the exact <see cref="StyleDocument"/> handed to <c>SetStyle</c> (the T-Parse
    /// seam), and the live <see cref="MapViewComponent"/> + <see cref="Camera"/> handles a LATER stage's
    /// <c>Ink(layerId)</c>/<c>GlyphAnchors(layerId)</c> accessors will read (plan §10 — not implemented now,
    /// the handles are kept so adding them touches no composer).
    /// </summary>
    internal sealed class VisualFrame
    {
        /// <summary>Row-major, BOTTOM-left origin (Unity's native <c>ReadPixels</c> convention — see
        /// <see cref="SnapshotRenderer.Pixels"/>). Whole-frame fractions/buckets are orientation-independent;
        /// a region assertion must stay origin-symmetric (a center box, or corners AS A SET) rather than name
        /// a screen direction.</summary>
        public Frame Pixels { get; }

        /// <summary>Render target width, px.</summary>
        public int Width { get; }

        /// <summary>Render target height, px.</summary>
        public int Height { get; }

        /// <summary>This scene's background colour — the same colour <see cref="Coverage"/> and
        /// <see cref="InkStatsIn"/> compare every pixel against.</summary>
        public Color32 Background { get; }

        /// <summary>The exact <see cref="StyleDocument"/> instance <see cref="VisualScene.Render"/> handed to
        /// <c>MapViewComponent.SetStyle</c> — the T-Parse seam: it can only carry the shape the REAL
        /// <c>StyleParser.Parse</c> produces (plan §6).</summary>
        public StyleDocument ParsedStyle { get; }

        /// <summary>The live view this frame was rendered from — kept for a later ink/anchor accessor and for
        /// wiring observability (<c>MapViewTestExtensions.WiredFeatureSourceCount</c>), NOT torn down by this
        /// carrier: <see cref="VisualScene.Dispose"/> owns its lifetime.</summary>
        public MapViewComponent MapView { get; }

        /// <summary>The Unity camera this frame was rendered from. Same lifetime note as <see cref="MapView"/>.</summary>
        public Camera Camera { get; }

        internal VisualFrame(
            Frame pixels, int width, int height, Color32 background,
            StyleDocument parsedStyle, MapViewComponent mapView, Camera camera)
        {
            Pixels       = pixels;
            Width        = width;
            Height       = height;
            Background   = background;
            ParsedStyle  = parsedStyle;
            MapView      = mapView;
            Camera       = camera;
        }

        /// <summary>Whole-frame coverage verdict against this scene's background colour. Delegates to
        /// <see cref="SnapshotCoverage.Analyse"/> — see <see cref="SnapshotVerdict.Passes"/> for the tolerant
        /// pass/fail band.</summary>
        public SnapshotVerdict Coverage() => SnapshotCoverage.Analyse(Pixels, Background);

        /// <summary>Mean (R,G,B) of the inclusive-exclusive rectangle [x0,x1)×[y0,y1), each channel in [0,1].
        /// Passthrough to <see cref="SnapshotCoverage.SampleRegionMeanColor"/>.</summary>
        public double[] RegionMeanColor(int x0, int y0, int x1, int y1)
            => SnapshotCoverage.SampleRegionMeanColor(Pixels, x0, y0, x1, y1);

        /// <summary>Total colour variance of the same rectangle. Passthrough to
        /// <see cref="SnapshotCoverage.RegionColorVariance"/>.</summary>
        public double RegionColorVariance(int x0, int y0, int x1, int y1)
            => SnapshotCoverage.RegionColorVariance(Pixels, x0, y0, x1, y1);

        /// <summary>Mean luminance of pixels that are NOT the background colour. Passthrough to
        /// <see cref="SnapshotCoverage.MeanLuminanceOfNonBackground"/>.</summary>
        public double MeanLuminanceOfNonBackground()
            => SnapshotCoverage.MeanLuminanceOfNonBackground(Pixels, Background);

        /// <summary>Pixel-weighted ink centroid + count inside the inclusive-exclusive window
        /// <c>[x0,x1)×[y0,y1)</c>. A pixel is "ink" when it differs from THIS frame's background beyond
        /// <see cref="SnapshotCoverage.Tolerance"/> — the SAME background predicate
        /// <see cref="SnapshotCoverage.Analyse"/> uses (not a forked threshold); unusable here is
        /// <c>WorldSymbolInkAnalysis</c>'s white-background "R &lt; 200" rule, since this kit's background is
        /// dark slate and every background pixel would read as ink under it.
        ///
        /// <para><b>No flip.</b> <see cref="Pixels"/> is BOTTOM-left origin (Unity's native
        /// <c>ReadPixels</c>), and <c>IProjection.GroundToScreen</c> / <c>Camera.WorldToScreenPoint</c> are
        /// also bottom-left, <c>+y</c> up — so <paramref name="x0"/>/<paramref name="y0"/> here compare
        /// directly against an oracle screen pixel with no coordinate conversion at all.</para>
        ///
        /// <para><b>Deferred: a per-layer <c>Ink(layerId)</c>.</b> A shared frame cannot attribute a
        /// pixel to a style layer, and this stage renders exactly one inking layer (the symbol text), so all
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
                for (int x = x0; x < x1; x++)
                {
                    Color32 px = Pixels[x, y];
                    int dist = math.abs(px.r - Background.r)
                             + math.abs(px.g - Background.g)
                             + math.abs(px.b - Background.b);
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
