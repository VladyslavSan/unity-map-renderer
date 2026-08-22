using UnityEngine;

namespace MapRenderer.App.Menu
{
    /// <summary>Diagnostics page: on-demand analyses of the live map. Arms the label breakdown
    /// (<c>LabelPlacementSystem.RequestLabelBreakdown</c>), which logs a per-style-layer + per-vertical-screen-band
    /// tally of the next frame's input labels to the Console (the "what/where are all these labels" reproducer).
    /// Supersedes the standalone <c>LabelBreakdownOverlay</c>.</summary>
    internal sealed class DiagnosticsPage : IMenuPage
    {
        // Last action feedback. Always drawn (even when empty) so the control count is constant across the
        // frame's Layout/Repaint passes — see IMenuPage's IMGUI contract.
        private string _status = " ";

        /// <inheritdoc/>
        public string Title => "Diagnostics";

        /// <inheritdoc/>
        public void Draw(MenuOverlay menu)
        {
            var labels = menu.MapComponent != null ? menu.MapComponent.View?.Labels : null;

            GUILayout.Label("Label placement analysis (logs to the Console).");

            if (GUILayout.Button("Analyze labels (per-layer + screen band)"))
            {
                if (labels == null)
                    _status = "No live MapView.";
                else
                {
                    labels.RequestLabelBreakdown();
                    _status = "Capture armed — see Console next frame.";
                }
            }

            GUILayout.Label(_status);
        }
    }
}
