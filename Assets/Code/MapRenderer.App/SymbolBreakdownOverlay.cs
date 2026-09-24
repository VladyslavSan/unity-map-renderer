using UnityEngine;
using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App
{
    /// <summary>
    /// Dev diagnostic (opt-in, not part of the render path): an on-screen button that arms a one-shot
    /// symbol-breakdown capture on the live <see cref="MapView"/>'s <c>SymbolPlacementSystem</c>. The next
    /// placement Tick logs a per-style-layer and per-vertical-screen-band tally of that frame's input records to
    /// the Console. Delete this file and <c>SymbolPlacementSystem.Diagnostics.cs</c> to strip the feature.
    /// </summary>
    public sealed class SymbolBreakdownOverlay : MonoBehaviour
    {
        /// <summary>The MapView whose symbol placement this analyzes (set in the Inspector).</summary>
        [Tooltip("The MapView whose label placement this analyzes (set in the Inspector).")]
        public MapViewComponent Map;

        /// <summary>Top-left screen position of the button, in pixels.</summary>
        [Tooltip("Top-left screen position of the button, in pixels.")]
        public Vector2 ButtonPosition = new Vector2(12f, 120f);

        // Last action's result, shown under the button so a click gives visible feedback.
        private string _status;

        /// <summary>Draw the button and route a click to <c>SymbolPlacementSystem.RequestSymbolBreakdown</c>.</summary>
        private void OnGUI()
        {
            var buttonRect = new Rect(ButtonPosition.x, ButtonPosition.y, 180f, 30f);
            if (GUI.Button(buttonRect, "Analyze symbols"))
            {
                var symbols = Map != null ? Map.View?.SymbolPlacementSystem : null;
                if (symbols == null)
                    _status = "no live MapView (enter Play mode)";
                else
                {
                    symbols.RequestSymbolBreakdown();
                    _status = "capture armed — see Console";
                }
            }

            if (!string.IsNullOrEmpty(_status))
                GUI.Label(new Rect(buttonRect.x, buttonRect.yMax + 2f, 360f, 24f), _status);
        }
    }
}
