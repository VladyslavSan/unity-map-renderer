// Namespace-collision guard (see SymbolPlacementSystem.cs's header): keep TOP-LEVEL `using Unity.Mathematics;`
// and unqualified float2/double2/math.*, never an inline `Unity.Mathematics.X`.

// Opt-in, one-shot diagnostic: it buckets this frame's placement buffers by style layer and by vertical screen
// band, then logs a table. Cold path: it allocates once per click; an un-armed frame pays one branch.

using System.Collections.Generic;
using System.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Style;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Unity.Text.Placement
{
    internal sealed partial class SymbolPlacementSystem
    {
        /// <summary>Number of vertical screen bands the on-screen histogram splits the viewport into
        /// (band 0 = the top 10% of the screen, band 9 = the bottom 10%).</summary>
        private const int BreakdownBandCount = 10;

        /// <summary>Set by <see cref="RequestSymbolBreakdown"/>, consumed (and cleared) by the next placement
        /// Tick's <see cref="CaptureSymbolBreakdown"/>.</summary>
        private bool _breakdownRequested;

        /// <summary>Arm a one-shot symbol-breakdown capture: the next placement Tick logs a per-layer +
        /// per-screen-band tally of this frame's input records to the Console. Idempotent — re-arming before
        /// the capture runs is a no-op. Called from the dev overlay button (<c>SymbolBreakdownOverlay</c>).</summary>
        internal void RequestSymbolBreakdown() => _breakdownRequested = true;

        /// <summary>Resolve a record's material slot to a readable style-layer id, falling back to the raw slot
        /// index when no layer list is supplied (the demo/single-material path) or the slot is out of range.</summary>
        /// <param name="symbolLayers">The per-slot render layers this Tick was handed (may be null).</param>
        /// <param name="slot">The record's pre-clamped material/mesh slot.</param>
        private static string ResolveLayerName(IReadOnlyList<SymbolRenderLayer> symbolLayers, int slot)
        {
            if (symbolLayers != null && slot >= 0 && slot < symbolLayers.Count
                && symbolLayers[slot]?.StyleLayer?.Id is { } id && id.Length > 0)
                return id;
            return $"slot#{slot}";
        }

        /// <summary>Per-layer tally accumulated during a capture: how many input records the layer contributed,
        /// how many were actually projected (survived the pre-projection gather cull), and how many landed
        /// on screen. The gap total→projected is the pre-projection cull; projected→onScreen is behind-camera /
        /// off-viewport.</summary>
        private struct LayerTally
        {
            /// <summary>Input records (non-dropped) belonging to this layer.</summary>
            public int Total;
            /// <summary>Of <see cref="Total"/>, how many reached projection (gather kept them).</summary>
            public int Projected;
            /// <summary>Of <see cref="Projected"/>, how many projected in front of the camera and on screen.</summary>
            public int OnScreen;
        }

        /// <summary>One-shot: bucket this frame's input records by style layer and by vertical screen band and
        /// log the result. Runs only on an armed frame, AFTER projection (reads <see cref="_symbolScreen"/> /
        /// <see cref="_symbolValid"/> / <see cref="_stagePointOffset"/> and the native mirror), so the numbers
        /// are this Tick's. Clears the arm flag first so a mid-capture re-arm is honored next frame, not this one.</summary>
        /// <param name="symbolLayers">The per-slot render layers, for slot→layer-id resolution (may be null).</param>
        /// <param name="viewportLogicalPx">This frame's logical viewport size — the band denominator.</param>
        private void CaptureSymbolBreakdown(IReadOnlyList<SymbolRenderLayer> symbolLayers, double2 viewportLogicalPx)
        {
            _breakdownRequested = false;

            double viewportH = viewportLogicalPx.y > 0.0 ? viewportLogicalPx.y : 1.0;
            var perLayer = new Dictionary<string, LayerTally>();
            var bandOnScreen = new int[BreakdownBandCount];
            int input = 0, projected = 0, onScreen = 0, behind = 0, culled = 0;

            for (int r = 0; r < _mirrorCount; r++)
            {
                if (_mirrorSymbolDropped[r] != 0) continue; // Dropped: masked out of placement, not an "input" symbol
                input++;

                int detail = _mirrorDetail[r];
                int slot = _mirrorKinds[r] == SymbolPlacementKind.Point
                    ? _mirrorPoints[detail].Slot
                    : _mirrorCurveds[detail].Slot;
                string layer = ResolveLayerName(symbolLayers, slot);

                int offset = _stagePointOffset[r];
                bool recProjected = offset >= 0;   // -1 = hard-skipped by the gather cull (never projected)
                bool recOnScreen = false;
                if (recProjected)
                {
                    projected++;
                    if (_symbolValid[offset] != 0)
                    {
                        float2 screen = _symbolScreen[offset];
                        // y-up (bottom-left origin): top of screen is high y ⇒ band 0 = top.
                        double fracFromTop = 1.0 - math.clamp(screen.y / viewportH, 0.0, 1.0);
                        int band = (int)(fracFromTop * BreakdownBandCount);
                        if (band < 0) band = 0; else if (band >= BreakdownBandCount) band = BreakdownBandCount - 1;
                        bandOnScreen[band]++;
                        onScreen++;
                        recOnScreen = true;
                    }
                    else behind++;
                }
                else culled++;

                perLayer.TryGetValue(layer, out LayerTally t);
                t.Total++;
                if (recProjected) t.Projected++;
                if (recOnScreen) t.OnScreen++;
                perLayer[layer] = t;
            }

            Debug.Log(BuildBreakdownReport(perLayer, bandOnScreen, input, projected, onScreen, behind, culled, viewportLogicalPx));
        }

        /// <summary>Format the captured tallies into the multi-line Console report — a header (camera + totals),
        /// a per-layer table sorted by record count descending, and the top→bottom screen-band histogram.</summary>
        /// <param name="perLayer">Per-layer tallies from the capture.</param>
        /// <param name="bandOnScreen">On-screen record count per vertical band (index 0 = top).</param>
        /// <param name="input">Total non-dropped input records.</param>
        /// <param name="projected">Records that reached projection (gather kept them).</param>
        /// <param name="onScreen">Records that projected on screen, in front of the camera.</param>
        /// <param name="behind">Records projected behind the camera / off viewport.</param>
        /// <param name="culled">Records hard-skipped by the pre-projection gather cull.</param>
        /// <param name="viewportLogicalPx">This frame's logical viewport, for the header.</param>
        private string BuildBreakdownReport(Dictionary<string, LayerTally> perLayer, int[] bandOnScreen,
            int input, int projected, int onScreen, int behind, int culled, double2 viewportLogicalPx)
        {
            var cam = _camera.CurrentProperties; // CameraProperties — `var` avoids importing the type here
            var sb = new StringBuilder(4096);
            sb.AppendLine("========== LABEL BREAKDOWN ==========");
            sb.AppendLine($"camera: zoom {cam.Zoom:F2}  tilt {math.degrees(cam.Tilt.Radians):F1}°  " +
                          $"heading {math.degrees(cam.Heading.Radians):F1}°   viewport {viewportLogicalPx.x:F0}x{viewportLogicalPx.y:F0} logical");
            sb.AppendLine($"input records (non-dropped): {input}");
            sb.AppendLine($"  ├─ projected (gather kept):  {projected}   ({input - projected} culled pre-projection)");
            sb.AppendLine($"  │    ├─ on screen:           {onScreen}");
            sb.AppendLine($"  │    └─ behind / off-view:   {behind}");
            sb.AppendLine($"  └─ hard-skipped by cull:     {culled}");
            // Per-trigger split of the hard-skips. Each counts only fade-dead records that fired that trigger,
            // so the five sum to `culled`.
            sb.AppendLine($"       ├─ zoom-gated:      {LastZoomCulledCount}");
            sb.AppendLine($"       ├─ distance (far):  {LastDistanceCulledCount}");
            sb.AppendLine($"       ├─ horizon:         {LastHorizonCulledCount}");
            sb.AppendLine($"       ├─ coverage-fading: {LastCoverageFadingCulledCount}");
            sb.AppendLine($"       └─ departing:       {LastDepartingCulledCount}");
            sb.AppendLine();

            // ── Per-layer table (sorted by total desc) ──
            var rows = new List<KeyValuePair<string, LayerTally>>(perLayer);
            rows.Sort((a, b) => b.Value.Total.CompareTo(a.Value.Total));
            int maxLayerTotal = 1;
            foreach (var kv in rows) if (kv.Value.Total > maxLayerTotal) maxLayerTotal = kv.Value.Total;

            sb.AppendLine($"--- by layer ({rows.Count} layers, record count desc) ---");
            sb.AppendLine($"{"layer",-28} {"total",8} {"projctd",8} {"onScrn",8}   share");
            foreach (var kv in rows)
            {
                LayerTally t = kv.Value;
                sb.AppendLine($"{Trunc(kv.Key, 28),-28} {t.Total,8} {t.Projected,8} {t.OnScreen,8}   {Bar(t.Total, maxLayerTotal, 24)}");
            }
            sb.AppendLine();

            // ── Screen-band histogram (on-screen records, top→bottom) ──
            int maxBand = 1;
            for (int i = 0; i < bandOnScreen.Length; i++) if (bandOnScreen[i] > maxBand) maxBand = bandOnScreen[i];
            sb.AppendLine("--- by screen band (on-screen records, top→bottom) ---");
            for (int i = 0; i < bandOnScreen.Length; i++)
            {
                int hiPct = 100 - i * 10, loPct = 90 - i * 10;
                string tag = i == 0 ? " (top)" : i == bandOnScreen.Length - 1 ? " (bottom)" : "";
                sb.AppendLine($"band {i}  {loPct,3}-{hiPct,3}%{tag,-9} {bandOnScreen[i],8}   {Bar(bandOnScreen[i], maxBand, 32)}");
            }
            sb.AppendLine("=====================================");
            return sb.ToString();
        }

        /// <summary>An ASCII bar proportional to <paramref name="value"/>/<paramref name="max"/>, up to
        /// <paramref name="width"/> blocks — the histogram/table visual weight.</summary>
        /// <param name="value">The magnitude to draw.</param>
        /// <param name="max">The magnitude that fills the whole bar.</param>
        /// <param name="width">Bar width in characters at full scale.</param>
        private static string Bar(int value, int max, int width)
        {
            if (max <= 0 || value <= 0) return "";
            int n = (int)math.round((double)value / max * width);
            if (n <= 0) n = 1;
            return new string('#', n);
        }

        /// <summary>Truncate a layer id to <paramref name="width"/> chars (ellipsis) so the table columns line
        /// up regardless of style-layer name length.</summary>
        /// <param name="s">The layer id.</param>
        /// <param name="width">Max width.</param>
        private static string Trunc(string s, int width) =>
            s.Length <= width ? s : s.Substring(0, width - 1) + "…";
    }
}
